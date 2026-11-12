using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// An opposing commander that buys reinforcements out of <c>factionFunds</c> — the same pot the
/// player's depot buys out of, under the same rules. The Basegame only ever deploys the vehicle
/// reserve a mission was authored with and never buys anything, so a faction that converts income
/// into reinforcements is the difference between a sandbox and a game.
/// </summary>
/// <remarks>
/// The design goal is a fair duel: in MATCHED mode both commanders start on the same balance and
/// earn at the same rate, and neither gets a stipend, so the only thing that separates them is
/// what they spend it on. That choice is the <see cref="EnemyPlan"/> — the enemy reads what the
/// player is fielding and commits to the plan that counters it, which the player then has to
/// counter back.
///
/// This buys and shapes the composition, then hands the units to the Basegame depot
/// deployment and its objective-seeking ground AI rather than issuing routes of its own.
/// Upgrade to real orders only once buying alone stops being enough of a fight.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService : ICommanderTickPersistent, ICommanderResetSession
{
    // Effective modes. A saved 0 is the retired AUTO and plays as MATCHED, see EffectiveMode.
    internal const int ModeOff = 0;
    internal const int ModeMatched = 1;
    internal const int ModeMission = 2;

    internal const int AiCommandersListed = 0;
    internal const int AiCommandersEnemy = 1;
    internal const int AiCommandersBoth = 2;

    private const float ReviewIntervalSeconds = 30f;

    /// <summary>
    /// Share of the pot spent per review. Scale-free on purpose: faction funds are denominated in
    /// millions and every mission authors its own balance, so an absolute reserve floor cannot be
    /// calibrated. A quarter every 30 s is the tempo knob, not an advantage — it is what stops the
    /// commander dumping its whole balance the moment the mission starts.
    /// </summary>
    private const float SpendFraction = 0.25f;
    private const int PurchasesPerReview = 3;

    /// <summary>Drafted airframes a review puts up. The draft's whole wing is in stock at the start,
    /// and one a review would leave most of it on the ground for the first minutes.</summary>
    private const int DraftLaunchesPerReview = 3;

    /// <summary>What a free drafted launch reports as spent. Not money: see TryLaunchAirframe.</summary>
    private const float DraftLaunchMarker = 0.01f;

    /// <summary>Extra launches a review queues while airframes wait in stock. Each launch is its
    /// own frame (~11 ms), so this is a frame-time guard as much as a pace.</summary>
    private const int MaxStockLaunchesPerReview = 4;

    /// <summary>
    /// Reviews a new plan has to stay indicated before the commander switches to it. Without this
    /// the enemy re-counters inside one review and the player never gets to cash in a counter.
    /// </summary>
    private const int PlanCommitReviews = 2;

    /// <summary>
    /// The mod's own duel map is the one mission where the enemy commander is not an option the
    /// player has to find in a menu — it is the opponent, so it runs whether or not the setting is
    /// on and it plays harder. These are the knobs for "harder": a bigger opening balance, a
    /// faster spend, more purchases per review, and an air force that is allowed to be bigger than
    /// the mission authored. Everything else is still the same economy the player is on.
    /// </summary>
    private const float DuelHeadStart = 1.5f;

    /// <summary>Champions' head start in place of <see cref="DuelHeadStart"/>: one commander holding
    /// every base but one has seven fronts to fund from the same authored balance.</summary>
    private const float ChampionsHeadStart = 3f;
    private const float DuelSpendFraction = 0.45f;

    /// <summary>Fighting vehicles (field and reserve) a commander keeps before it saves for a
    /// building. Below it the build reserve is ignored, see <see cref="Review"/>.</summary>
    private const int ArmyBeforeSaving = 20;

    /// <summary>Share of its income buildings' pay a commander puts into the gold mine fund; the
    /// rest keeps buying its army and defence while it saves.</summary>
    private const float MineFundIncomeShare = 0.5f;

    /// <summary>Share of its income buildings' pay a commander with its home income up always spends
    /// on its army, air force and fleet, whatever the build reserve wants. See <see cref="TakeWarFund"/>.</summary>
    private const float WarFundIncomeShare = 0.35f;

    /// <summary>Air-defence vehicles out scouting at once, and the wait before the next is sent.</summary>
    private const int MaxAaScouts = 2;

    /// <summary>Fighting vehicles that are not air defence kept while no attack force is bought: they take the neutral bases.</summary>
    private const int CaptureSquadWhileHolding = 6;

    private const float AaScoutSeconds = 180f;

    /// <summary>The balance the mod's own missions author, and the most <see cref="WealthSteps"/>
    /// ever measures from: the roster's prices are the same whatever a mission starts on.</summary>
    private const float WealthReferenceFunds = 1000f;

    /// <summary>Ground vehicles bought per review.</summary>
    private const int DuelPurchasesPerReview = 3;

    /// <summary>
    /// Aircraft this commander may have in the world at once. A framerate guard, not a difficulty
    /// knob, so it does not scale with the balance: a richer commander buys aircraft sooner and
    /// dearer, not more of them. Champions has its own because the commander holds nearly every base.
    /// </summary>
    private const int DuelAirborneLimit = 30;
    private const int ChampionsAirborneLimit = 100;

    /// <summary>Share of one review's budget set aside for airframes.</summary>
    private const float DuelAirframeBudgetShare = 0.6f;

    /// <summary>Builders the commander keeps; more while its blueprints queue up with nobody on them.</summary>
    private const int MinEnemyBuilders = 2;
    private const int MaxEnemyBuilders = 6;
    private const int GodMinEnemyBuilders = 5;
    private const int GodMaxEnemyBuilders = 10;

    /// <summary>Hard ceiling on the guns one AI faction fields at once, whatever its plan or balance:
    /// past it every buy path skips artillery, and the guns' upkeep stops growing with them.</summary>
    private const int MaxEnemyArtillery = 50;

    /// <summary>Ballistic launchers one AI faction fields at once. They all park round one strike
    /// site, and more than five crowd it and eat the budget.</summary>
    private const int MaxEnemyBallisticLaunchers = 5;

    /// <summary>
    /// Share of the commander's combat vehicles that should be air defence, so the umbrella grows
    /// with the army instead of stopping at the couple the opening reviews bought. Below it, one
    /// purchase a review is forced onto a launcher whatever the plan says.
    /// </summary>
    private const float AirDefenceShareOfForce = 0.3f;

    /// <summary>Combat vehicles per munitions or fuel truck the commander keeps on the road.</summary>
    private const int VehiclesPerSupplyTruck = 16;

    /// <summary>Share of a review's budget saved toward the next SAM site launcher.</summary>
    private const float SamSiteBudgetShare = 0.5f;

    /// <summary>Share of income saved toward the next radar SAM, on top of the budget share.</summary>
    private const float SamFundIncomeShare = 0.4f;

    /// <summary>Fighting vehicles owned before the SAM fund starts saving. A duel head start clears
    /// a launcher's price at once, so without this the StratoLance was the first thing bought.</summary>
    private const int SamSiteAfterCombatVehicles = 6;

    /// <summary>GOD DIFFICULTY's ground and air buys per review, as a multiple of the wealth curve's.
    /// The curve reads a 30 B start as one tenfold (six vehicles a review), which on seven bases is
    /// a trickle; GOD is meant to be all out from the first review.</summary>
    private const int GodPurchaseMultiplier = 4;

    /// <summary>Most air-defence vehicles one review buys out of the fund.</summary>
    private const int AirDefenceBuysPerReview = 8;

    /// <summary>Ground vehicles added to a review's buy per tenfold over the authored balance.</summary>
    private const int GroundPurchasesPerDecade = 3;

    /// <summary>
    /// Airframes added to a review's buy per tenfold. One a review was the real ceiling on this air
    /// force, not <see cref="DuelAirborneLimit"/>: at one a review a wing takes ten minutes to reach
    /// twenty aircraft, so a raised ceiling on its own would never be reached at all. The rate and
    /// the ceiling have to climb together or neither means anything.
    /// </summary>
    private const int AirPurchasesPerDecade = 1;

    /// <summary>
    /// Tenfolds over the authored balance at which the convoy buy leans fully on value rather than
    /// spreading evenly over the roster. Unlike the counts this one genuinely saturates: past
    /// "always reach for the dearest thing on the roster" there is nothing further for money to buy.
    /// </summary>
    private const float DearRosterDecades = 3f;

    /// <summary>The least price lean a pick has under a unit cap: weight in proportion to price.</summary>
    private const float CappedDearLean = 1f;

    private readonly Dictionary<FactionHQ, CommanderState> states = new();

    /// <summary>HQs whose airframe list has already been written to the log. See LogAirRosterOnce.</summary>
    private readonly HashSet<FactionHQ> loggedAirRoster = new();

    /// <summary>Airframes the enemy has in the air, and when each entered it. See ReportLostAircraft.</summary>
    private readonly Dictionary<Aircraft, (float Since, string Label)> airborneSince = new();

    /// <summary>When each airframe type was last lost soon after launch. See IsBenched.</summary>
    private readonly Dictionary<AircraftDefinition, List<float>> quickLosses = new();
    private readonly List<Aircraft> lostAircraft = new();

    /// <summary>Airframes a buy could take, with the field that would launch each, before the price
    /// band and what the wing already flies narrow it down.</summary>
    private readonly List<(AircraftDefinition Definition, Airbase Field)> airCandidates = new();

    private readonly Dictionary<AircraftDefinition, int> airframesOwned = new();
    private readonly List<FactionHQ> staleHqs = new();
    private readonly List<VehicleDefinition> catalog = new();
    private readonly List<VehicleDefinition> candidates = new();

    /// <summary>
    /// Vehicles that no faction's convoy groups include: SAM site parts, artillery, and gun/laser
    /// air defence. Stock factions only ship tank, mechanized, mobile AA and logistics groups, so
    /// these would otherwise be unbuyable. Built once per mission by walking every prefab.
    /// </summary>
    private readonly List<VehicleDefinition> rosterParts = new();

    /// <summary>The commander's own ground vehicles by type, read once per review. What it already
    /// owns is what damps the next pick, which is the whole of the convoy variety.</summary>
    private readonly int[] ownedByType = new int[System.Enum.GetValues(typeof(VehicleType)).Length];

    /// <summary>AI factions still waiting for this round's buy review / home-guard review. One is
    /// worked per frame, so an AI-vs-AI match does not do both commanders' work in the same frame.</summary>
    private readonly Queue<FactionHQ> reviewQueue = new();
    private readonly Queue<FactionHQ> defenceQueue = new();

    /// <summary>The player's opening airbase. See GetStrikeTarget.</summary>
    private Airbase? playerHomeBase;

    /// <summary>How hard this review's buy leans on value, 0 (spread evenly over the roster) to 1
    /// (weight proportional to price). Read by <see cref="Choose"/>. See <see cref="DearRosterDecades"/>.</summary>
    /// <summary>Set by <c>TryBuyRole</c> when it held the fund for this airframe instead of buying,
    /// so <c>BuyAirframe</c> does not fall through to the any-airframe pass and spend it anyway.</summary>
    private AircraftDefinition? savingFor;
    private float dearLean;
    private int ownedCombat;

    /// <summary>The reviewing faction's <c>ArtilleryDistance</c>, read with the owned counts.</summary>
    private float reviewArtilleryDistance;
    private int ownedSupply;
    private float nextReviewAt;
    private float nextDefenceAt;
    private ForceRead queuedPlayerForce;

    /// <summary>Whether <see cref="rosterParts"/> has been built for this mission. The flag, not the
    /// list's count: a roster with none of those vehicles would otherwise be rescanned every review.</summary>
    private bool rosterPartsScanned;

    internal CommanderEnemyCommanderService()
    {
        Instance = this;
        nextReviewAt = 0f;
    }

    internal enum EnemyPlan
    {
        ReconScreen,
        Spearhead,
        FireSupport,
        AirDefence,
    }

    /// <summary>
    /// OFF, for this match only. Deliberately not saved: a saved OFF would silently leave every
    /// later duel without an opponent, and the duel is meant to have one out of the box. Cleared on
    /// scene change, so the next match starts on the saved setting (MATCHED unless changed).
    /// </summary>
    internal static bool OffThisMatch { get; private set; }

    internal static CommanderEnemyCommanderService? Instance { get; private set; }

    /// <summary>The host's AI COMMANDERS choice, as a guest last heard it.</summary>
    internal static int HostAiCommanders { get; set; }

    /// <summary>
    /// Which missions have AI commanders, the host's choice on the GC RTS Settings page: the listed
    /// missions only, the other side on every mission, or every side. Read on guests too, because
    /// the advanced tier (<c>CommanderFeatureGate</c>) and the duel rules follow it there as well.
    /// </summary>
    internal static int AiCommanders => CommanderNetwork.IsGuest ? HostAiCommanders : CommanderSettings.AiCommanders;

    /// <summary>Every mission is played as a Ground Control mission with an AI commander, not just
    /// the ones named in <see cref="IsListedDuelMission"/>.</summary>
    internal static bool CommandersEverywhere => AiCommanders != AiCommandersListed;

    /// <summary>The host's own team gets an AI commander as well: the
    /// "local team" the AI plays against rotates between factions (<c>CommanderNetwork.RunAsHostTeam</c>).</summary>
    internal static bool BothSides => AiCommanders == AiCommandersBoth;

    /// <summary>"The local team" is each AI in turn (<see cref="BothSides"/>), so no
    /// side is the player's to drop or skip.</summary>
    internal static bool LocalSideRotates => BothSides;

    /// <summary>The missions named for AI commanders whatever the setting says: the mod's own,
    /// Carrier Duel and any map the editor tagged GC RTS. Their duel rules (the home base that loses the match) stay theirs alone.</summary>
    internal static bool IsListedDuelMission =>
        CommanderFeatureGate.MissionName.IndexOf("Ground Control Duel", System.StringComparison.OrdinalIgnoreCase) >= 0
        || CommanderFeatureGate.MissionName.IndexOf("Ground Control Team Duel", System.StringComparison.OrdinalIgnoreCase) >= 0
        || CommanderFeatureGate.MissionName.IndexOf("Carrier Duel", System.StringComparison.OrdinalIgnoreCase) >= 0
        || IsChampionsMission
        || CommanderDraftService.IsDraftMission
        || CommanderTutorialService.IsTutorialMission
        || CommanderFeatureGate.IsTaggedMission;

    /// <summary>True on the missions the mod ships (the duel, Team Duel, Champions and the Draft),
    /// which play themselves, and on the base game's Carrier Duel. Team Duel is named on its own:
    /// "Ground Control Team Duel" does not contain "Ground Control Duel", and the commander stayed
    /// off there in singleplayer. Carrier Duel too: off this list, the base game's free AI aircraft
    /// flew both sides and NORMAL and CHALLENGING played exactly the same match.</summary>
    /// With AI COMMANDERS set past the listed missions, every mission is one, custom ones included.
    internal static bool IsDuelMission => IsListedDuelMission || (CommandersEverywhere && CommanderFeatureGate.MissionName.Length > 0);

    /// <summary>Team Duel and Team Duel PvE.</summary>
    internal static bool IsTeamDuelMission =>
        CommanderFeatureGate.MissionName.IndexOf("Ground Control Team Duel", System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Team Duel against the AI: the side the AI plays is closed to players at random when
    /// the mission loads (<c>CommanderTeamDuelPvePatches</c>), and that side is the only AI side.</summary>
    internal static bool IsTeamDuelPveMission =>
        CommanderFeatureGate.MissionName.IndexOf(CommanderTeamDuelPvePatches.MissionName, System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>One player base against an enemy holding the rest. See CommanderEconomyServiceChampions.</summary>
    internal static bool IsChampionsMission => IsShippedChampionsMission;

    /// <summary>The mod's own Champions maps, whose start bases are a hand-picked list.</summary>
    internal static bool IsShippedChampionsMission =>
        CommanderFeatureGate.MissionName.IndexOf("Ground Control Champions", System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// A match hosted for other players (the game's own <c>GameState.Multiplayer</c>), as opposed to
    /// singleplayer. Read on the host, where the AI runs.
    /// </summary>
    internal static bool IsMultiplayerMatch => GameManager.gameState == GameState.Multiplayer;

    /// <summary>
    /// The mode actually in force. On wherever AI Commanders puts one, singleplayer or hosted: the
    /// mod's own missions, or every mission past the listed ones; off only where it is set to Off.
    /// Champions is the exception: its enemy side is <c>preventJoin</c> and is always locked to
    /// MISSION FUNDS, singleplayer or hosted, since the authored balance is the difficulty.
    /// <see cref="OffThisMatch"/> overrides it until the next scene change.
    /// </summary>
    internal static int EffectiveMode
    {
        get
        {
            if (IsChampionsMission)
            {
                return ModeMission;
            }

            // The tutorial's enemy is scenery: the player is learning, not fighting. A mission whose
            // AI Commanders is set to Off has none either.
            if (CommanderTutorialService.IsTutorialMission || CommanderMapSettings.AiCommandersOff)
            {
                return ModeOff;
            }

            // AI Commanders decides whether there is one, singleplayer and multiplayer alike: anything
            // but Off is on. Teams with players stay theirs (IsAiFaction). The mode setting only picks
            // its funding (the retired AUTO, 0, plays as MATCHED).
            bool present = !OffThisMatch && IsDuelMission;
            return !present ? ModeOff : CommanderSettings.EnemyCommanderMode == ModeMission ? ModeMission : ModeMatched;
        }
    }

    /// <summary>Units this session's enemy commanders have bought, for the settings readout.</summary>
    internal int TotalPurchases { get; private set; }

    /// <summary>Plan and balance of the best-funded enemy commander, for the HUD readout.</summary>
    internal string StatusLine { get; private set; } = string.Empty;

    public void TickPersistent()
    {
        FlushPendingPins();

        // A dedicated server has no team of its own; the team with players stands in for it.
        CommanderNetwork.RunAsHostTeam(TickPersistentAsHost);
    }

    public void ResetSession()
    {
        OffThisMatch = false;
        PendingPins.Clear();
        IslandDepots.Clear();
        IslandBases.Clear();
        states.Clear();
        builderBusyAt.Clear();
        jammers.Clear();
        nextJammerPickAt = 0f;
        loggedAirRoster.Clear();
        loggedCounterRoster.Clear();
        batteryFixes.Clear();
        CommanderTargetPriorityPatches.Reset();
        opposition.Clear();
        oppositionWeight = 0f;
        counterBuying = false;
        airborneSince.Clear();
        lostAircraft.Clear();
        quickLosses.Clear();
        lastLaunchTimes.Clear();
        playerHomeBase = null;
        staleHqs.Clear();
        catalog.Clear();
        candidates.Clear();
        rosterParts.Clear();
        rosterPartsScanned = false;
        airCandidates.Clear();
        airframesOwned.Clear();
        System.Array.Clear(ownedByType, 0, ownedByType.Length);
        dearLean = 0f;
        ownedCombat = 0;
        ownedSupply = 0;
        reconUnits.Clear();
        shipCatalog.Clear();
        defenceCandidates.Clear();
        staleDefenders.Clear();
        TotalPurchases = 0;
        StatusLine = string.Empty;
        nextReviewAt = 0f;
        nextDefenceAt = 0f;
        reviewQueue.Clear();
        defenceQueue.Clear();
    }

    /// <summary>The settings button: MATCHED -> MISSION FUNDS -> OFF (this match) -> MATCHED.</summary>
    internal static void CycleSetting()
    {
        if (IsChampionsMission)
        {
            return;
        }

        if (OffThisMatch)
        {
            OffThisMatch = false;
            return;
        }

        int setting = CommanderSettings.EnemyCommanderMode;
        if (setting == ModeMission)
        {
            // What gets saved is MATCHED, so the next match is back to normal.
            CommanderSettings.EnemyCommanderMode = ModeMatched;
            OffThisMatch = true;
            return;
        }

        CommanderSettings.EnemyCommanderMode = ModeMission;
    }

    /// <summary>
    /// A faction the enemy commander plays when it is on: every faction the host holds that is not
    /// the host's own, except one a human player has joined. A team with a player on it is that
    /// player's to command, unless the ENEMY AI ON TEAMS WITH PLAYERS setting hands it back to the
    /// AI for players who only want to fly. Every enemy review asks this one question.
    /// </summary>
    internal static bool IsAiFaction(FactionHQ? hq, FactionHQ? localHq)
    {
        return EffectiveMode != ModeOff
            && hq != null
            && hq.faction != null
            && hq.IsServer
            && !ReferenceEquals(hq, localHq)
            && (CommanderSettings.EnemyCommanderHumanTeams || BothSides || !HasHumanPlayers(hq))

            // On PvE the players' side is never the AI's, even before anyone has joined it. With
            // both sides on AI it plays both, or the match is one AI against an empty side.
            && (LocalSideRotates || !IsTeamDuelPveMission || hq.preventJoin);
    }

    internal static bool HasHumanPlayers(FactionHQ hq)
    {
        return hq.GetPlayers(sortByScore: false).Count > 0;
    }

    /// <summary>Opposing factions left to their players by <see cref="IsAiFaction"/>, for the settings readout.</summary>
    internal static int CountTeamsLeftToPlayers()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        int count = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null
                && hq.faction != null
                && hq.IsServer
                && !ReferenceEquals(hq, localHq)
                && !IsAiFaction(hq, localHq))
            {
                count++;
            }
        }

        return count;
    }

    internal static string GetModeLabel(int mode)
    {
        return mode switch
        {
            ModeMatched => "MATCHED",
            ModeMission => "MISSION FUNDS",
            _ => "OFF",
        };
    }

    internal static string GetPlanLabel(EnemyPlan plan)
    {
        return plan switch
        {
            EnemyPlan.AirDefence => "AIR DEFENCE",
            EnemyPlan.FireSupport => "FIRE SUPPORT",
            EnemyPlan.Spearhead => "SPEARHEAD",
            _ => "RECON SCREEN",
        };
    }

    /// <summary>
    /// One runnable check for the counter triangle, run once at plugin load next to
    /// <see cref="CommanderServiceRegistryCheck"/>, because a Unity plugin has nowhere else to
    /// run a test. If a threshold in <see cref="ChoosePlan"/> is retuned and a whole plan stops
    /// being reachable, this is what says so in the BepInEx console.
    /// </summary>
    internal static void SelfCheck()
    {
        CheckPlan("air wing", new ForceRead { Aircraft = 4, Ground = 2, Armour = 2 }, EnemyPlan.AirDefence);
        CheckPlan("armoured push", new ForceRead { Armour = 6, Ground = 8 }, EnemyPlan.FireSupport);
        CheckPlan("static line", new ForceRead { Guns = 2, AirDefence = 3, Ground = 6 }, EnemyPlan.Spearhead);
        CheckPlan("nothing dominant", new ForceRead { Ground = 2, Armour = 1, Guns = 1 }, EnemyPlan.ReconScreen);
        CheckPlan("empty field", default, EnemyPlan.ReconScreen);
        CheckAirRoles();
        CheckDefencePosture();
        CheckPlanWeights();
        CheckPurchaseScaling();
        CheckCounterBatteryPick();
        CheckCounterWeights();
        CommanderTargetPriorityPatches.SelfCheck();
    }

    /// <summary>Rating first, price only as a tie-break. Split out so <see cref="SelfCheck"/> can run
    /// it with no scene: inverted to "cheapest first" this quietly buys a gun mount over a laser one
    /// and the commander still loses the base it was defending.</summary>
    private static bool IsBetterCounterBattery(float antiMissile, float value, float bestAntiMissile, float bestValue)
    {
        return antiMissile > bestAntiMissile || (antiMissile == bestAntiMissile && value < bestValue);
    }

    /// <summary>
    /// The counter-battery pick. Reach for the best munition-killer on the roster whatever it costs,
    /// and only then for the cheaper of two equals — the reverse of the fire-control truck's rule,
    /// which is next door and is a plausible thing to copy by mistake.
    /// </summary>
    private static void CheckCounterBatteryPick()
    {
        if (!IsBetterCounterBattery(0.8f, 40f, 0.3f, 5f))
        {
            CommanderPlugin.Log.LogError(
                "Enemy counter-battery self-check FAILED: the commander prefers a cheap mount to a better one, "
                    + "so massed rocket artillery has no answer.");
            return;
        }

        if (!IsBetterCounterBattery(0.5f, 8f, 0.5f, 12f) || IsBetterCounterBattery(0.5f, 12f, 0.5f, 8f))
        {
            CommanderPlugin.Log.LogError(
                "Enemy counter-battery self-check FAILED: two equally rated mounts no longer tie-break on price.");
        }
    }

    /// <summary>
    /// The convoy mix. Two things break it if the weight table is retuned: a plan that refuses a
    /// whole class of vehicle (which is the monoculture the weights replaced), and a plan whose own
    /// speciality is not the thing it reaches for most.
    /// </summary>
    private static void CheckPlanWeights()
    {
        VehicleType[] fighting =
        {
            VehicleType.LCV, VehicleType.AFV, VehicleType.MBT, VehicleType.ART,
            VehicleType.AAA, VehicleType.IR_SAM, VehicleType.R_SAM,
        };

        foreach (EnemyPlan plan in System.Enum.GetValues(typeof(EnemyPlan)))
        {
            float best = 0f;
            VehicleType bestType = VehicleType.LCV;
            for (int i = 0; i < fighting.Length; i++)
            {
                float weight = GetPlanWeight(plan, fighting[i]);
                if (weight <= 0f)
                {
                    CommanderPlugin.Log.LogError(
                        $"Enemy plan self-check FAILED: {GetPlanLabel(plan)} will never buy a {fighting[i]}, "
                            + "which is the monoculture the weights exist to prevent.");
                    return;
                }

                if (weight > best)
                {
                    best = weight;
                    bestType = fighting[i];
                }
            }

            VehicleType wanted = plan switch
            {
                EnemyPlan.AirDefence => VehicleType.AAA,
                EnemyPlan.FireSupport => VehicleType.ART,
                EnemyPlan.Spearhead => VehicleType.MBT,
                _ => VehicleType.LCV,
            };

            if (GetPlanWeight(plan, wanted) < best)
            {
                CommanderPlugin.Log.LogError(
                    $"Enemy plan self-check FAILED: {GetPlanLabel(plan)} reaches for {bestType} before {wanted}.");
                return;
            }
        }
    }

    private static void CheckPlan(string name, in ForceRead force, EnemyPlan expected)
    {
        EnemyPlan actual = ChoosePlan(force);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy plan self-check FAILED ({name}): expected {GetPlanLabel(expected)}, got {GetPlanLabel(actual)}.");
        }
    }

    /// <summary>
    /// The starting-cash knob. A commander handed more money than the mission authored has to field
    /// more of an army for it, up to what its depots can deploy — and a commander on the authored
    /// balance has to buy exactly what it always bought, or every stock mission quietly changes
    /// difficulty. Both ends are checked here because both are how the knob stops working.
    /// </summary>
    private static void CheckPurchaseScaling()
    {
        // The measure itself: one step per tenfold, whatever the mission's own numbers are.
        CheckSteps("authored balance", WealthSteps(1000f, 1000f), 0f);
        CheckSteps("the duel head start", WealthSteps(1500f, 1000f), 0.176f);
        CheckSteps("a billion against a thousand", WealthSteps(1e9f, 1000f), 6f);
        CheckSteps("ten billion against a thousand", WealthSteps(1e10f, 1000f), 7f);
        CheckSteps("bombed broke", WealthSteps(10f, 1000f), 0f);
        CheckSteps("mission authors nothing", WealthSteps(1e9f, 0f), 0f);
        CheckSteps("30 000 on a mission authored at 10 000", WealthSteps(30000f, 10000f), 1.477f);

        // No regression at the balance every stock mission authors, and the duel head start must not
        // tip it over a step either, or switching the commander on changes difficulty by itself.
        CheckPurchases("ground, authored balance", ScalePurchases(3, GroundPurchasesPerDecade, 0f), 3);
        CheckPurchases("ground, duel head start", ScalePurchases(3, GroundPurchasesPerDecade, 0.176f), 3);
        CheckPurchases("ground, ten times over", ScalePurchases(3, GroundPurchasesPerDecade, 1f), 6);

        // Different balances must never produce the same commander.
        CheckPurchases("ground, one billion", ScalePurchases(3, GroundPurchasesPerDecade, 6f), 21);
        CheckPurchases("ground, ten billion", ScalePurchases(3, GroundPurchasesPerDecade, 7f), 24);
        CheckPurchases("air, one billion", ScalePurchases(1, AirPurchasesPerDecade, 6f), 7);
        CheckPurchases("air, ten billion", ScalePurchases(1, AirPurchasesPerDecade, 7f), 8);

        // A fund that saves 45 against a 20 Cricket waits for two, not five.
        CheckPurchases("wave, poor fund", ReachableWaveSize(5, 45f, 20f, 30), 2);
        CheckPurchases("wave, rich fund", ReachableWaveSize(5, 600f, 20f, 30), 5);
        CheckPurchases("wave, no fund known", ReachableWaveSize(3, 0f, 20f, 30), 3);
        CheckPurchases("wave, ceiling full", ReachableWaveSize(5, 600f, 20f, 0), 2);
    }

    private static void CheckPurchases(string name, int actual, int expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy purchase self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void CheckSteps(string name, float actual, float expected)
    {
        if (Mathf.Abs(actual - expected) > 0.01f)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy wealth self-check FAILED ({name}): expected {expected:0.###}, got {actual:0.###}.");
        }
    }

    /// <summary>
    /// The duel's head start, applied once per enemy HQ: a slightly bigger opening balance, and
    /// nothing else. Deliberately small — the enemy still earns at the player's rates from here on,
    /// this only stops the first ten minutes being a walkover.
    /// <para>
    /// <c>AIAircraftLimit</c> is left as the lobby set it: <see cref="CommanderFreeAiPilots"/> flies
    /// that many free AI pilots per side, outside stock and outside the commanders' wings.
    /// </para>
    /// </summary>
    private static void PrepareDuel(FactionHQ hq, FactionHQ localHq)
    {
        // Applied in every mode, MISSION FUNDS included: the authored balance is the base the head
        // start multiplies, single player or multiplayer alike.
        // A GC RTS map may set its own multiple in the editor.
        // NORMAL takes no head start; CHALLENGING keeps it.
        float headStart = CommanderMapSettings.AiHeadStart
            ?? (IsChampionsMission ? ChampionsHeadStart : IsChallenging(hq) ? DuelHeadStart : 1f);
        hq.SetFunds(hq.factionFunds * headStart);

        hq.reserveAirframes = 0;

        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) takes the {(IsChampionsMission ? "Champions" : "duel")} head start (x{headStart:0.##}): {hq.factionFunds:0} funds. "
                + $"Free AI pilots: {hq.AIAircraftLimit} enemy, {localHq.AIAircraftLimit} friendly (lobby setting).");
        OpenEveryBase();
    }

    /// <summary>
    /// Every land airbase on the map is in play in the mod's missions. Bases the mission file lists
    /// keep their authored Disabled / Capturable flags; this only opens the ones it does not (a map update adding a field, or a
    /// base whose prefab default is closed). Ship decks stay as they are: the base game never lets
    /// an airbase attached to a unit be captured. Same two writes as the game's ModifyAirbaseOutcome.
    /// </summary>
    private static void OpenEveryBase()
    {
        foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
        {
            if (airbase == null
                || airbase.SavedAirbase == null
                || airbase.TryGetAttachedUnit(out _)
                || airbase.SavedAirbaseOverride // the mission authored this base: respect Disabled / Capturable
                || (airbase.SavedAirbase.Capturable && !airbase.disabled))
            {
                continue;
            }

            airbase.SavedAirbase.Disabled = false;
            airbase.SetDisabled(false);
            airbase.SavedAirbase.Capturable = true;
            airbase.capture.SetCapturable(true);
            CommanderPlugin.Log.LogInfo($"Opened {airbase.SavedAirbase.DisplayName} for capture.");
        }
    }

    /// <summary>
    /// Hands the enemy standing intel on every building the player owns. Buildings do not move, so
    /// one registration each is enough and nothing has to be refreshed. This is what turns the
    /// enemy from a faction that owns units into one that attacks: the Basegame ground AI drives at
    /// the nearest tracked enemy when it has no closer objective, and <c>CombatAI</c> only ever
    /// picks targets out of <c>trackingDatabase</c>, so an empty database is an enemy that flies
    /// over your base without seeing it. The player's mobile units are left unrevealed on purpose —
    /// the enemy knows where your base is, not where your army is.
    /// </summary>
    private static void RevealPlayerBase(FactionHQ hq, FactionHQ localHq)
    {
        if (localHq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in localHq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && hq.GetTrackingData(id) == null)
            {
                hq.RpcUpdateTrackingInfo(id);
            }
        }
    }

    /// <summary>
    /// Puts the enemy on the player's kill/tax rates, once, the first time this commander is
    /// reviewed, and resets it to its own authored opening balance (the player's only when the
    /// mission authors none), so turning the commander on mid-mission still starts from the
    /// opening position.
    /// </summary>
    private static void LevelEconomy(FactionHQ hq, FactionHQ localHq)
    {
        // Use the authored balance, not the live threshold, which the treasury hold raises and which
        // survives a restart. Prefer the enemy's own authored balance; only fall back to the player's
        // when the mission does not author this faction at all (an authored 0 is kept).
        float baseline;
        if (CommanderEconomyService.TryGetAuthoredBalance(hq, out float authored))
        {
            baseline = authored;
        }
        else if (CommanderEconomyService.TryGetAuthoredBalance(localHq, out authored))
        {
            baseline = authored;
        }
        else
        {
            authored = localHq.excessFundsThreshold;
            baseline = authored > 0f ? authored : localHq.factionFunds;
        }

        hq.SetFunds(baseline);
        hq.excessFundsThreshold = authored;
        hq.killReward = localHq.killReward;
        hq.playerTaxRate = localHq.playerTaxRate;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) matched to the player economy at {baseline:0}.");
    }

    /// <summary>
    /// The counter triangle. Each plan beats the force that provokes it and is soft against the
    /// one that provokes the next, so shifting your own composition flips theirs — that swap is
    /// the game.
    /// </summary>
    private static EnemyPlan ChoosePlan(in ForceRead force)
    {
        // Leaning on air power: build the umbrella.
        if (force.Aircraft >= 2 && force.Aircraft * 2 >= force.Ground)
        {
            return EnemyPlan.AirDefence;
        }

        // Massed armour: guns break a column faster than trading tank for tank.
        if (force.Armour >= 3 && force.Armour * 2 >= force.Ground)
        {
            return EnemyPlan.FireSupport;
        }

        // A static line of guns and launchers: run armour through it before it can range.
        int staticLine = force.Guns + force.AirDefence;
        if (staticLine >= 3 && staticLine * 2 >= force.Ground)
        {
            return EnemyPlan.Spearhead;
        }

        // Nothing dominant to counter: cheap mass, take ground.
        return EnemyPlan.ReconScreen;
    }

    /// <summary>
    /// How often a plan reaches for each kind of vehicle. Weights, not a filter: every plan can buy
    /// anything the depot sells, and <see cref="Choose"/> divides the weight by what is already owned,
    /// so the army converges on this mix instead of whatever type the roster has most of.
    /// </summary>
    private static float GetPlanWeight(EnemyPlan plan, VehicleType type)
    {
        return plan switch
        {
            EnemyPlan.AirDefence => type switch
            {
                VehicleType.R_SAM or VehicleType.IR_SAM or VehicleType.AAA => 4f,
                VehicleType.MBT or VehicleType.AFV => 1.5f,
                VehicleType.ART or VehicleType.LCV => 1f,
                _ => 0f,
            },
            EnemyPlan.FireSupport => type switch
            {
                VehicleType.ART => 4f,
                VehicleType.MBT or VehicleType.AFV => 2f,
                VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM => 1.5f,
                VehicleType.LCV => 1f,
                _ => 0f,
            },
            EnemyPlan.Spearhead => type switch
            {
                VehicleType.MBT => 4f,
                VehicleType.AFV => 2.5f,
                VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM => 1.5f,
                VehicleType.ART or VehicleType.LCV => 1f,
                _ => 0f,
            },
            _ => type switch
            {
                VehicleType.LCV => 3f,
                VehicleType.AFV => 2.5f,
                VehicleType.MBT => 2f,
                VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM => 1.5f,
                VehicleType.ART => 1f,
                _ => 0f,
            },
        };
    }

    private static string FundsLabel(float funds)
    {
        return UnitConverter.ValueReading(funds) ?? funds.ToString("F1", CultureInfo.CurrentCulture);
    }

    private static bool IsAirDefence(VehicleDefinition definition)
    {
        return definition.vehicleType is VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM;
    }

    /// <summary>A rearm truck: its prefab carries the game's ammunition stockpile component.</summary>
    private static bool IsSupplyVehicle(VehicleDefinition definition)
    {
        // Cached per type: called for every owned vehicle and the prefab walk is expensive.
        if (!SupplyTypes.TryGetValue(definition, out bool supply))
        {
            supply = definition.unitPrefab != null
                && definition.unitPrefab.GetComponentInChildren<Rearmer>(true) != null;
            SupplyTypes[definition] = supply;
        }

        return supply;
    }

    /// <summary>
    /// GOD: money is no object, builders are, so it keeps a bigger crew. Under a unit cap both bounds
    /// stop at the cap's builder share, so the crew never eats the army's slots.
    /// </summary>
    private static int GetBuilderBound(FactionHQ hq, bool min)
    {
        bool god = IsChampionsMission && CommanderGodService.IsGod(hq);
        int bound = min ? (god ? GodMinEnemyBuilders : MinEnemyBuilders) : (god ? GodMaxEnemyBuilders : MaxEnemyBuilders);
        return CommanderUnitCap.Enabled
            ? Mathf.Min(bound, Mathf.Max(MinEnemyBuilders, CommanderUnitCap.BuilderCap))
            : bound;
    }

    /// <summary>A repair vehicle, which the builder count owns rather than the convoy's supply count.</summary>
    private static bool IsBuilderVehicle(VehicleDefinition definition)
    {
        return definition.unitPrefab != null
            && definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null;
    }

    /// <summary>
    /// A fire-control truck: its prefab carries the game's own <c>FireControl</c>. A radar-guided
    /// launcher will not fire without one within 300 m of it, so a commander that buys a StratoLance
    /// and no truck has bought an expensive parked vehicle.
    /// </summary>
    private static bool IsFireControlVehicle(VehicleDefinition definition)
    {
        // The ballistic missile launchers carry a FireControl of their own, which filed them as SAM
        // site parts and put them in the convoy roll: one match bought nine of them as convoy
        // vehicles. A site's fire control is a truck with no launcher on it.
        if (!FireControlTypes.TryGetValue(definition, out bool fireControl))
        {
            fireControl = definition.unitPrefab != null
                && definition.unitPrefab.GetComponentInChildren<FireControl>(true) != null
                && definition.unitPrefab.GetComponentInChildren<MissileLauncher>(true) == null;
            FireControlTypes[definition] = fireControl;
        }

        return fireControl;
    }

    private static bool IsFireControlVehicle(Unit unit)
    {
        return unit.definition is VehicleDefinition definition && IsFireControlVehicle(definition);
    }

    private static bool IsCombatVehicle(VehicleDefinition definition)
    {
        // Type before the prefab walk: IsSupplyVehicle reaches into the prefab, and this runs over
        // every vehicle the faction owns on every review now.
        return definition.value > 0f
            && definition.vehicleType is VehicleType.AAA
                or VehicleType.IR_SAM
                or VehicleType.R_SAM
                or VehicleType.MBT
                or VehicleType.AFV
                or VehicleType.ART
                or VehicleType.LCV
            && !IsSupplyVehicle(definition);
    }

    /// <summary>What the player is actually fielding, which is the only input to the plan choice.</summary>
    private static ForceRead ReadForce(FactionHQ hq)
    {
        ForceRead read = default;
        read.HomeAfloat = CommanderVictoryService.TryGetHomeBase(hq, out Airbase home) && !CommanderGameAccess.IsLandBase(home);
        if (hq.factionUnits == null)
        {
            return read;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled)
            {
                continue;
            }

            if (unit is Aircraft)
            {
                read.Aircraft++;
                continue;
            }

            // The fleet is read by its dearest hull, the carrier on Carrier Duel: that is the ship
            // whose loss ends the match, so it is the armour an anti-ship sortie has to get through.
            if (unit is Ship)
            {
                read.Ships++;
                if (unit.definition.value > read.ShipValue)
                {
                    read.ShipValue = unit.definition.value;
                    read.ShipArmour = unit.definition.armorTier;
                }

                continue;
            }

            if (unit.definition is not VehicleDefinition definition)
            {
                continue;
            }

            switch (definition.vehicleType)
            {
                case VehicleType.MBT:
                case VehicleType.AFV:
                    read.Armour++;
                    read.Ground++;
                    break;
                case VehicleType.ART:
                    read.Guns++;
                    read.Ground++;
                    break;
                case VehicleType.AAA:
                case VehicleType.IR_SAM:
                case VehicleType.R_SAM:
                    read.AirDefence++;
                    read.Ground++;
                    break;
                case VehicleType.LCV:
                    read.Ground++;
                    break;
            }
        }

        return read;
    }

    private static void UpdatePlan(FactionHQ hq, CommanderState state, in ForceRead playerForce)
    {
        EnemyPlan wanted = ChoosePlan(playerForce);
        if (wanted == state.Plan)
        {
            state.Pending = wanted;
            state.PendingReviews = 0;
            return;
        }

        if (wanted == state.Pending)
        {
            state.PendingReviews++;
        }
        else
        {
            state.Pending = wanted;
            state.PendingReviews = 1;
        }

        if (state.PendingReviews < PlanCommitReviews)
        {
            return;
        }

        state.Plan = wanted;
        state.PendingReviews = 0;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) switches plan to {GetPlanLabel(wanted)}.");
    }

    /// <summary>
    /// How many tenfolds past the mission's authored opening balance this commander is holding.
    /// A ratio, since every mission uses its own numbers.
    /// <para>
    /// Logarithmic on purpose: a linear curve explodes and a capped one makes a billion and ten
    /// billion the same commander. Never below 0, so a commander bombed broke buys what it always did.
    /// </para>
    /// <para>
    /// The yardstick is capped at <see cref="WealthReferenceFunds"/>, because unit prices do not scale
    /// with the mission's balance.
    /// </para>
    /// </summary>
    private static float WealthSteps(float funds, float authored)
    {
        float reference = Mathf.Min(authored, WealthReferenceFunds);
        return reference > 0f ? Mathf.Log10(Mathf.Max(1f, funds / reference)) : 0f;
    }

    private static float GetWealthSteps(FactionHQ hq, FactionHQ localHq)
    {
        return WealthSteps(hq.factionFunds, CommanderEconomyService.GetAuthoredThreshold(localHq));
    }

    /// <summary>
    /// Things one review buys, ground or air, scaled by wealth so a rich commander actually fields
    /// more.
    /// <para>
    /// Ground throughput is still capped by the game: <c>FactionHQ.DeployVehicles</c> accepts one
    /// vehicle per depot per <c>VehicleDepot.spawnCooldown</c>, so extra buys just keep depots fed.
    /// Airframes come from hangars and have no such limit.
    /// </para>
    /// </summary>
    private static int ScalePurchases(int baseline, int perDecade, float steps)
    {
        return baseline + (perDecade * Mathf.FloorToInt(Mathf.Max(0f, steps)));
    }

    /// <summary>
    /// What <see cref="Review"/> holds back for the economy. Every rung holds its whole price except a
    /// gold mine: for that the commander puts <see cref="MineFundIncomeShare"/> of its income into
    /// <c>MineFund</c> and holds back only that, so the army keeps growing. The fund is a mark on the
    /// balance, not money moved; it is clamped to the balance and cleared once the mine is no longer
    /// the next rung.
    /// </summary>
    private static float GetSavedBuildReserve(FactionHQ hq, CommanderState state, bool incomeOnly)
    {
        float reserve = CommanderEconomyService.GetEnemyBuildReserve(hq, out bool goldMine, out bool income);
        float now = Time.time;
        if (!goldMine)
        {
            state.MineFund = 0f;
            state.MineFundAt = -1f;
            return incomeOnly && !income ? 0f : reserve;
        }

        if (state.MineFundAt >= 0f)
        {
            float minutes = (now - state.MineFundAt) / 60f;
            state.MineFund += CommanderEconomyService.GetEnemyIncomePerMinute(hq) * MineFundIncomeShare * minutes;
        }

        state.MineFundAt = now;
        state.MineFund = Mathf.Min(state.MineFund, reserve, Mathf.Max(0f, hq.factionFunds));
        return state.MineFund;
    }

    /// <summary>
    /// What this review must spend on the war out of the income since the last one, or the ladder could
    /// hold the whole balance. A share of income rather than of the balance, so buildings get the rest.
    /// </summary>
    private static float TakeWarFund(FactionHQ hq, CommanderState state)
    {
        float now = Time.time;
        float war = state.WarFundAt >= 0f
            ? CommanderEconomyService.GetEnemyIncomePerMinute(hq) * WarFundIncomeShare * (now - state.WarFundAt) / 60f
            : 0f;
        state.WarFundAt = now;

        // Holding its army back it still needs air defence and a capture squad before the income is up.
        return CommanderEconomyService.HasEnemyHomeIncome(hq) || !CommanderCaptureService.HasForwardBase(hq)
            ? Mathf.Min(war, Mathf.Max(0f, hq.factionFunds))
            : 0f;
    }

    private void Review(FactionHQ hq, FactionHQ localHq, int mode, in ForceRead playerForce)
    {
        if (!states.TryGetValue(hq, out CommanderState state))
        {
            state = new CommanderState();
            states[hq] = state;
        }

        bool duel = IsDuelMission;
        if (!state.Prepared)
        {
            if (mode == ModeMatched)
            {
                LevelEconomy(hq, localHq);
            }

            if (duel)
            {
                PrepareDuel(hq, localHq);
            }

            state.Prepared = true;
        }

        if (duel)
        {
            RevealPlayerBase(hq, localHq);
        }

        UpdatePlan(hq, state, playerForce);

        // Posture comes before spending on purpose: a commander with an empty balance still has to
        // fly the aircraft and drive the radars it already owns.
        ReviewPosture(hq, localHq, state);

        // A draft buys nothing: the drafted ground reserve rolls out through the base game's depot
        // deployment, and the air wing is launched out of stock here.
        if (CommanderDraftService.IsDraftMission)
        {
            if (CommanderDraftService.IsLive)
            {
                LaunchDraftedAirframes(hq, state, playerForce);
            }

            return;
        }

        // Money has nothing to do with it, so before the broke return below.
        ScrapIdleBuilders(hq);

        // Hold back whatever the economy service is saving for; both spend from factionFunds, and without
        // this the balance never reaches a factory or dock. Except while the army is short and the home
        // income stands, otherwise a small income stalls the army entirely. Never before the income, or a
        // commander with none never builds any.
        // No attack force until a base stands near enough to use one: the money saves for buildings
        // and flies. Only what takes neutral bases and the air defence are still bought.
        // GOD DIFFICULTY's opening holds the convoys too, and the plants it still has to build: aircraft
        // and air defence fly meanwhile, the attack column comes once the plants stand.
        bool godOpening = CommanderEconomyService.IsGodOpening(hq);
        bool holdArmy = (duel && !CommanderCaptureService.HasForwardBase(hq)) || godOpening;

        // A short army still saves for income (a mine out of its fund, an upgrade at its price): with
        // nothing held, 45% of the balance went every review, the balance sat near one minute's
        // income, and a 400-600 upgrade or mine was out of reach for most of the match.
        bool armyShort = !holdArmy && CountArmy(hq) < ArmyBeforeSaving && CommanderEconomyService.HasEnemyHomeIncome(hq);
        float reserve = GetSavedBuildReserve(hq, state, incomeOnly: armyShort);

        // The factory savings are held back whatever the army needs: they are a share of income,
        // not the whole price, so the convoys still get the rest.
        float godPlants = godOpening ? CommanderEconomyService.GetGodOpeningReserve(hq) : 0f;
        reserve = Mathf.Min(hq.factionFunds, reserve + CommanderEconomyService.GetEnemyFactoryFund(hq) + godPlants);
        float pot = hq.factionFunds - reserve;
        float warFund = TakeWarFund(hq, state);
        float spendable = Mathf.Max(pot * (duel ? DuelSpendFraction : SpendFraction), warFund);
        if (spendable <= 0f)
        {
            // Logged once per dry spell, so a broke commander is distinguishable from a broken one.
            if (!state.Broke)
            {
                state.Broke = true;
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) is buying nothing: balance {hq.factionFunds:0}, "
                        + $"{reserve:0} held back for its next building.");
            }

            ReviewRadarSamWhileBroke(hq, state);

            // A factory's output costs nothing to send out, and the launches below this return were the only
            // way out of stock. Ground reserve rolls out through the base game either way.
            if (duel)
            {
                state.PendingAirBuys = Mathf.Min(CountAirStock(hq), MaxStockLaunchesPerReview);
                state.AirBuyForce = playerForce;
                state.AirBuyWealth = GetWealthSteps(hq, localHq);
            }

            ReviewNaval(hq, state, 0f);
            return;
        }

        if (state.Broke)
        {
            state.Broke = false;
            CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) has money to spend again ({hq.factionFunds:0}).");
        }

        // Depots first, and deliberately before anything else is bought. Both commanders now spend
        // fuel and ammunition to put an aircraft back in the air, so a commander that keeps buying
        // airframes while its shelves are empty ends up with a row of grounded aeroplanes.
        spendable -= CommanderSupplyStoreService.Instance?.TopUpForAi(hq, spendable * SupplyBudgetShare) ?? 0f;

        spendable -= TryUpgradeAirDefence(hq, spendable);

        // At the cap a bought unit only sits in the reserve, so the money is kept for later.
        if (CommanderUnitCap.IsFull(hq))
        {
            return;
        }

        // A commander holding only ship decks (Carrier Duel's opening) has nowhere to field a
        // vehicle: the reserve climbed past twenty with none on the field while its air wing went
        // short. Ground buying waits for a land base; the air and naval buys below do not.
        bool landless = IsLandless(hq);

        // Ground's share of the unit cap is full: spend like a landless commander, on air and sea.
        landless |= CommanderUnitCap.IsGroundFull(hq);

        // Its buildings are put up by its own repair vehicles now, so a commander without builders
        // builds nothing: they come before the convoys, and never go to war (CollectCaptureSquad).
        if (!landless)
        {
            spendable -= TryBuyBuilder(hq, spendable);
        }

        CollectCatalog(hq);
        if (catalog.Count == 0)
        {
            return;
        }

        ReadOwnedVehicles(hq);

        // Piledrivers incoming and no StratoLance cover: Boltstrikes out of everything above the
        // build reserve, and the army and the air force wait until every base has its share.
        if (!landless && TryBuyPiledriverDefence(hq, state, hq.factionFunds - reserve, ref spendable))
        {
            state.PendingAirBuys = 0;
            return;
        }

        // Radar SAMs come first. The home guard says when its ring holds a launcher with no fire control,
        // and this buys the truck.
        spendable -= landless ? 0f : TryBuyFireControl(hq, state, spendable);
        spendable = Mathf.Max(0f, spendable - (landless ? 0f : TryBuySiteRadar(hq, state)));

        // The launcher itself, out of a fund, because it is the one piece of the commander's air
        // defence a weighted convoy roll will never reach for.
        spendable -= landless ? 0f : TryBuySamLauncher(hq, state, spendable);

        // The fund is only a claim on the balance, and spendable is a share of that balance: unguarded,
        // each review's convoys spent what the last one saved and a 700 launcher never added up.
        if (!landless)
        {
            spendable = Mathf.Min(spendable, Mathf.Max(warFund, hq.factionFunds - reserve - state.SamFund));
        }

        // A column with no munitions or fuel behind it stops where it stands, so the truck comes out
        // of the pot before the fighting vehicles it is there to keep fed.
        spendable -= landless || holdArmy ? 0f : TryBuyConvoySupport(hq, spendable);

        // The pushes' own air defence, which the home guard never takes.
        spendable -= landless || holdArmy ? 0f : TryBuyConvoyAirDefence(hq, spendable);
        spendable -= landless || holdArmy ? 0f : TryBuyConvoySpaag(hq, spendable);

        // Counter massed rocket artillery, one mount per player gun. Ahead of the plan, since SPEARHEAD
        // does nothing for the base being shelled meanwhile.
        spendable -= landless ? 0f : TryBuyCounterBattery(hq, spendable, playerForce.Guns);

        // Whatever the plan says, a commander with no air defence at all while the player is
        // flying is not playing the same game. One launcher first, then back to the plan.
        bool blindToAir = playerForce.Aircraft > 0 && CountOwnedAirDefence() == 0;

        // And the umbrella has to keep pace with the army, not stop at the two launchers the opening
        // reviews bought: below its share of the force, one purchase a review is a launcher.
        bool thinAirDefence = ownedCombat > 0
            && CountOwnedAirDefence() < Mathf.CeilToInt(ownedCombat * AirDefenceShareOfForce);

        // CHALLENGING: same pot, but it is all spent, and on what the opposition is weak to.
        counterBuying = IsChallenging(hq);
        state.Challenging = counterBuying;
        state.Tactics = counterBuying;
        int reserveDepth = 0;
        if (counterBuying)
        {
            ReadOpposition(localHq);
            reserveDepth = NoteReserve(hq);

            // Its umbrella is a ring at each base plus an escort for the push, not a share of the army.
            thinAirDefence = CountOwnedAirDefence()
                < ChallengingAirDefenceTarget(CommanderVictoryService.CountBases(hq)) + (ownedCombat / AssaultVehiclesPerEscort);

            // Guns before carriers: see TryBuyArtillery.
            spendable -= landless || holdArmy ? 0f : TryBuyArtillery(hq, state, spendable);
        }

        // Wealth in tenfolds over the authored opening. Drives vehicles and airframes per review, the
        // airborne ceiling, and how hard the pick leans on price.
        float wealthSteps = GetWealthSteps(hq, localHq);
        dearLean = Mathf.Clamp01(wealthSteps / DearRosterDecades);

        // Under a unit cap, slots run out before money, so lean on price as if rich.
        if (CommanderUnitCap.Enabled)
        {
            dearLean = Mathf.Max(dearLean, CappedDearLean);
        }

        int godMultiplier = IsChampionsMission && CommanderGodService.IsGod(hq) ? GodPurchaseMultiplier : 1;
        int purchases = godMultiplier * ScalePurchases(
            duel ? DuelPurchasesPerReview : PurchasesPerReview, GroundPurchasesPerDecade, wealthSteps);

        if (counterBuying)
        {
            // Deeper with more depots: the cap was six whatever was built, and a Champions commander
            // with a dozen depots and an empty reserve banked 1800 while buying six a review.
            int depots = CommanderCaptureSpoils.DepotsRef(hq)?.Count ?? 0;
            purchases = Mathf.Clamp(
                Mathf.Max(ChallengingReserveDepth, depots) - reserveDepth,
                0,
                Mathf.Max(ChallengingPurchasesPerReview, depots));
            LogCounterRosterOnce(hq);
            LogCounterRead(hq);
        }

        if (duel)
        {
            // The air share is set aside rather than spent-or-lost. An airframe costs several ground
            // vehicles, so a flat slice of a pot the commander keeps draining on convoys never once
            // added up to an aircraft after the opening minutes — which is exactly how an enemy that
            // flew at the start ended up with no air force at all.
            float airFund = state.AirFund;
            float airShare = spendable * (landless ? 1f : counterBuying ? ChallengingAirframeBudgetShare : DuelAirframeBudgetShare);
            spendable -= AccrueFund(
                ref airFund,
                airShare,
                counterBuying ? ChallengingAirFundFloor : GetSpawnableAirframeValue(hq, dearest: true));
            state.AirFund = airFund;
            state.AirSaveCap = airShare * FundSaveReviews;

            // Several a review once the commander is rich, because one a review was the actual
            // ceiling on its air force whatever the airborne limit said. The fund is what keeps this
            // honest on a poor one: it only ever holds a share of the pot, so a commander that cannot
            // afford a second airframe buys one and the loop breaks on the first refusal.
            // Launched one a frame by TickAirBuys, not all here: spawning an airframe costs the host
            // about 11 ms, and a rich commander's eight in one frame was a 90 ms stall.
            // A commander that can field no vehicle flies its ground share instead: NORMAL banked a
            // thousand on Carrier Duel while launching one airframe a review. Up to
            // MaxStockLaunchesPerReview more while airframes wait in stock: a base full of aircraft
            // plants turns out more than one a review, and the airborne ceiling still holds.
            state.PendingAirBuys = godMultiplier * ScalePurchases(
                (counterBuying ? ChallengingAirframesPerReview : 1) + (landless ? 1 : 0)
                    + Mathf.Min(CountAirStock(hq), MaxStockLaunchesPerReview),
                AirPurchasesPerDecade,
                wealthSteps);
            state.AirBuyForce = playerForce;
            state.AirBuyWealth = wealthSteps;
        }

        spendable -= ReviewNaval(hq, state, spendable * NavalBudgetShare);

        // An expansion with nothing that can take ground is an expansion that never happens, so a
        // capture unit outranks the plan exactly the way the first air-defence launcher does.
        // Holding back is not sitting still: the neutral bases are how it gets near enough to attack.
        bool needsCaptureUnit = CommanderCaptureService.Instance?.WantsCaptureUnit(hq) == true
            || (holdArmy && ownedCombat - CountOwnedAirDefence() < CaptureSquadWhileHolding);
        for (int purchase = 0; purchase < purchases && spendable > 0f && !landless; purchase++)
        {
            // Two overrides on the plan, both about a base rather than a front: no air defence at
            // all while the player flies, and a home-guard ring the commander cannot fill out of
            // what it already owns.
            bool forceAirDefence = (purchase == 0 && blindToAir)
                || (purchase == 1 && (thinAirDefence || (state.AirGuardShort && !counterBuying)))
                || (purchase == 2 && thinAirDefence && counterBuying);
            forceAirDefence |= holdArmy && thinAirDefence;
            if (holdArmy && !forceAirDefence && !(purchase == 0 && needsCaptureUnit))
            {
                continue;
            }

            EnemyPlan buyPlan = forceAirDefence ? EnemyPlan.AirDefence : state.Plan;

            // A forced launcher is priced against everything above the build reserve, not the review's
            // slice: the ground slice is about an eighth of the balance, so a LADS (200) or Boltstrike
            // (300) needed 1600-2500 banked, and once the factory hold stopped the balance piling up
            // every launcher a Champions commander bought was an AFV6 AA.
            float pickBudget = forceAirDefence ? Mathf.Max(spendable, hq.factionFunds - reserve - state.SamFund) : spendable;
            VehicleDefinition? choice = purchase == 0 && needsCaptureUnit
                ? ChooseCaptureUnit(spendable) ?? Pick(pickBudget, buyPlan, forceAirDefence)
                : state.WantsReconUnit && purchase == purchases - 1
                    ? ChooseReconUnit(spendable) ?? Pick(pickBudget, buyPlan, forceAirDefence)
                    : Pick(pickBudget, buyPlan, forceAirDefence);
            if (choice == null)
            {
                return;
            }

            float cost = Mathf.Max(0f, choice.value);
            if (!CommanderGameAccess.TrySpend(hq, cost))
            {
                return;
            }

            hq.ModifyUnitSupply(choice, 1);
            NoteOwned(choice);
            spendable -= cost;
            TotalPurchases++;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}, {(counterBuying && !forceAirDefence ? "COUNTER" : GetPlanLabel(buyPlan))}) bought "
                    + $"{CommanderGameAccess.GetVehicleLabel(choice)} for {cost:0}.");
        }
    }

    /// <summary>
    /// The draft's air loop: the ordinary buy path with an unlimited fund, which is safe because
    /// <see cref="IsInDraftStock"/> narrows every pick to the drafted stock and a launch out of stock
    /// costs nothing (it reports <see cref="DraftLaunchMarker"/> so the loop can tell it happened).
    /// </summary>
    private void LaunchDraftedAirframes(FactionHQ hq, CommanderState state, in ForceRead playerForce)
    {
        for (int launch = 0; launch < DraftLaunchesPerReview; launch++)
        {
            if (BuyAirframe(hq, state, float.MaxValue, playerForce, 0f) <= 0f)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Buys a repair vehicle when the commander has fewer than <see cref="MinEnemyBuilders"/>, or one
    /// more (up to <see cref="MaxEnemyBuilders"/>) while more blueprints wait unattended than there are
    /// builders on their way out of a depot. Counts the reserve, or every review would buy another.
    /// </summary>
    private float TryBuyBuilder(FactionHQ hq, float spendable)
    {
        VehicleDefinition? builder = CommanderGameAccess.FindRepairVehicleDefinition(hq);
        if (builder == null || hq.factionUnits == null)
        {
            return 0f;
        }

        int reserve = hq.GetUnitSupply(builder);
        int owned = reserve;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle && !unit.disabled && CommanderRepairService.IsRepairUnit(unit))
            {
                owned++;
            }
        }

        int minBuilders = GetBuilderBound(hq, min: true);
        int maxBuilders = GetBuilderBound(hq, min: false);
        bool waiting = (CommanderEconomyService.Instance?.CountBlueprintsWithoutBuilders(hq) ?? 0) > reserve;
        int wanted = waiting ? Mathf.Min(maxBuilders, Mathf.Max(minBuilders, owned + 1)) : minBuilders;
        float cost = Mathf.Max(0f, builder.value);
        if (owned >= wanted || cost > spendable || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        hq.ModifyUnitSupply(builder, 1);
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(builder)} builder for {cost:0} "
                + $"({owned} before, {(waiting ? "blueprints are queued" : "keeping a minimum")}).");
        return cost;
    }

    /// <summary>
    /// What the commander does with what it already owns, as opposed to what it buys: park the radar
    /// screen on the approaches, and give every idle airframe a mission. Both run every review and
    /// on every mission the commander is switched on for, because both are about units that are
    /// already paid for.
    /// </summary>
    private void ReviewPosture(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        ReviewRecon(hq, localHq, state);
        if (IsDuelMission)
        {
            // Only on the mod's own map. Every other mission launches its own AI aircraft off
            // AIAircraftLimit and may script what they do; overriding that is not a bug fix.
            TaskAirWing(hq, localHq);
            if (CommanderAirCommandService.Instance is { } airCommand)
            {
                TrySendNeutralCaptureLanding(airCommand, hq, state);
            }

            ReviewAirlift(hq, state);
        }
    }

    /// <summary>
    /// One vehicle for the convoy: a weighted roll over everything the depot sells, the plan setting
    /// the odds and the vehicles the commander already owns dividing them down. That division is what
    /// makes the force spread over the whole arsenal and stay unpredictable - a plain roll over a
    /// filtered list bought the same two carriers all match, because the roster has four of them.
    /// </summary>
    private VehicleDefinition? Choose(float budget, EnemyPlan plan, bool airDefenceOnly = false)
    {
        VehicleDefinition? pick = null;
        float total = 0f;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];

            // A forced launcher is priced against the whole pot, so the AIR DEFENCE plan's tanks and
            // guns cannot ride along: NORMAL Primeva bought artillery and a ballistic launcher on it
            // before its bases had air defence.
            if (definition.value > budget || !IsCombatVehicle(definition) || IsRadarGuidedAirDefence(definition)
                || IsArtilleryCapped(definition) || IsOutclassedAirDefence(definition, budget)
                || IsAirDefenceFull(definition) || (airDefenceOnly && !IsAirDefence(definition)))
            {
                continue;
            }

            float weight = GetPlanWeight(plan, definition.vehicleType) / (1 + CountOwnedType(definition.vehicleType));

            // A commander sitting several tenfolds past what the mission authored should be fielding
            // the dear end of the roster, not a random spread with scouts in it. The lean comes on
            // gradually with the balance and the per-type damping above still runs, so the convoy
            // stays mixed - it just stops being priced like a poor commander's. Affordability alone
            // never did this: every vehicle on the roster clears a big budget.
            if (dearLean > 0f)
            {
                weight *= Mathf.Pow(Mathf.Max(1f, definition.value), dearLean);
            }

            if (weight <= 0f)
            {
                continue;
            }

            total += weight;
            if (Random.value * total <= weight)
            {
                pick = definition;
            }
        }

        if (pick != null)
        {
            return pick;
        }

        // Nothing the plan wants is affordable, or this roster has none of it. The wrong vehicle
        // beats no vehicle, the same second pass the air buy makes.
        candidates.Clear();
        for (int i = 0; i < catalog.Count; i++)
        {
            if (catalog[i].value <= budget && IsCombatVehicle(catalog[i]) && !IsRadarGuidedAirDefence(catalog[i])
                && !IsOutclassedAirDefence(catalog[i], budget) && !IsAirDefenceFull(catalog[i])
                && (!airDefenceOnly || IsAirDefence(catalog[i])))
            {
                candidates.Add(catalog[i]);
            }
        }

        return candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)];
    }

    /// <summary>
    /// The cheapest munitions or fuel truck the budget allows: a convoy escort, not a fight. A
    /// builder is skipped even though it is not a combat vehicle either - <c>TryBuyBuilder</c> keeps
    /// its own count of those, and a builder bought here would never reach a blueprint.
    /// </summary>
    private VehicleDefinition? ChooseSupplyTruck(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value <= budget
                && IsSupplyVehicle(definition)
                && !IsBuilderVehicle(definition)
                && (best == null || definition.value < best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>
    /// The cheapest vehicle in the catalogue that can move an airbase's capture bar. Cheapest because
    /// its job is to sit in the ring. Must be a fighting vehicle (not a supply truck) and never air
    /// defence, which the capture squad refuses.
    /// </summary>
    private VehicleDefinition? ChooseCaptureUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.captureStrength > 0f
                && IsCombatVehicle(definition)
                && !IsAirDefence(definition)
                && definition.value <= budget
                && (best == null || definition.value < best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>At the artillery cap, or a type that could not reach the strike target even from the
    /// faction's nearest depot.</summary>
    private bool IsArtilleryCapped(VehicleDefinition definition)
    {
        return definition.vehicleType == VehicleType.ART
            && (CountOwnedType(VehicleType.ART) >= MaxEnemyArtillery
                || (CommanderUnitCap.ArtilleryCap > 0 && CountOwnedType(VehicleType.ART) >= CommanderUnitCap.ArtilleryCap)
                || (IsBallisticLauncherType(definition)
                    && (ballisticWaitsForSam
                        || ownedBallistic >= MaxEnemyBallisticLaunchers

                        // Basic air defence at every base first.
                        || CountOwnedAirDefence() < ownedBases * MinAirDefencePerBase))
                || (ArtilleryReach.TryGetValue(definition, out float reach) && reviewArtilleryDistance > reach));
    }

    private int CountOwnedType(VehicleType type)
    {
        int index = (int)type;
        return index >= 0 && index < ownedByType.Length ? ownedByType[index] : 0;
    }

    /// <summary>
    /// What the commander already has on the road, by type, plus its combat and supply totals. Read
    /// once per review and topped up as the review buys, so three purchases in one pass do not all
    /// see the same empty shelf and buy the same vehicle.
    /// </summary>
    private void ReadOwnedVehicles(FactionHQ hq)
    {
        reviewArtilleryDistance = states.TryGetValue(hq, out CommanderState artilleryState) ? artilleryState.ArtilleryDistance : 0f;
        System.Array.Clear(ownedByType, 0, ownedByType.Length);
        ownedCombat = 0;
        ownedSupply = 0;
        ownedEscorts = 0;
        ownedBallistic = 0;
        ownedBases = CommanderVictoryService.CountBases(hq);
        ringGunsFirst = ChooseRingGun(hq) != null;

        // Off the bases held, not the guard's site plan: that is only laid once the ring has posts.
        ballisticWaitsForSam = !IsChallenging(hq)
            && ChooseSamLauncherUnit(float.MaxValue) != null
            && CountSamLaunchers(hq) < Mathf.Clamp(CommanderVictoryService.CountBases(hq) * SamSitesPerBase, 1, MaxSamSites);
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit.definition is not VehicleDefinition definition)
            {
                continue;
            }

            NoteOwned(definition);

            // One standing in a ring is base air defence, not an escort.
            if (IsConvoyAirDefence(definition) && IsDefendingUnit(unit))
            {
                ownedEscorts--;
            }
        }

        // A launcher takes two minutes to build, so the ones still waiting in the reserve count too,
        // or every review in between buys another.
        for (int i = 0; i < catalog.Count; i++)
        {
            if (IsBallisticLauncherType(catalog[i]))
            {
                ownedBallistic += hq.GetUnitSupply(catalog[i]);
            }
        }
    }

    private void NoteOwned(VehicleDefinition definition)
    {
        int index = (int)definition.vehicleType;
        if (index >= 0 && index < ownedByType.Length)
        {
            ownedByType[index]++;
        }

        if (IsCombatVehicle(definition))
        {
            ownedCombat++;
        }

        if (IsBallisticLauncherType(definition))
        {
            ownedBallistic++;
        }

        if (IsConvoyAirDefence(definition))
        {
            ownedEscorts++;
        }
        else if (IsSupplyVehicle(definition) && !IsBuilderVehicle(definition))
        {
            ownedSupply++;
        }
    }

    /// <summary>Air defence the commander owns for its bases, off the per-type counts read this
    /// review. The convoy escorts are left out: they are not at a base.</summary>
    private int CountOwnedAirDefence()
    {
        return CountOwnedType(VehicleType.AAA) + CountOwnedType(VehicleType.IR_SAM) + CountOwnedType(VehicleType.R_SAM)
            - ownedEscorts;
    }

    /// <summary>
    /// One munitions or fuel truck per <see cref="VehiclesPerSupplyTruck"/> combat vehicles. Supply
    /// vehicles are not combat vehicles, so the main buy loop never picks them.
    /// </summary>
    private float TryBuyConvoySupport(FactionHQ hq, float spendable)
    {
        if (ownedCombat < VehiclesPerSupplyTruck || ownedSupply * VehiclesPerSupplyTruck >= ownedCombat)
        {
            return 0f;
        }

        VehicleDefinition? truck = ChooseSupplyTruck(spendable);
        if (truck == null)
        {
            return 0f;
        }

        float cost = Mathf.Max(0f, truck.value);
        if (!CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        hq.ModifyUnitSupply(truck, 1);
        NoteOwned(truck);
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) put a {CommanderGameAccess.GetVehicleLabel(truck)} on the convoy "
                + $"for {cost:0} ({ownedSupply} supply to {ownedCombat} combat vehicles).");
        return cost;
    }

    /// <summary>
    /// One fire-control truck for the home guard's launcher. Once only: the guard parks the truck,
    /// the launcher and a radar on one post, and a second truck adds nothing a third vehicle on that
    /// post could use.
    /// </summary>
    private float TryBuyFireControl(FactionHQ hq, CommanderState state, float spendable)
    {
        if (!state.WantsFireControl)
        {
            return 0f;
        }

        // One at a time: it can wait in the reserve for the depot at its site's base.
        VehicleDefinition? truck = ChooseFireControlUnit(spendable);
        if (truck == null || hq.GetUnitSupply(truck) > 0)
        {
            return 0f;
        }

        float cost = Mathf.Max(0f, truck.value);
        if (!CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        hq.ModifyUnitSupply(truck, 1);
        NoteOwned(truck);
        state.WantsFireControl = false;
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(truck)} "
                + $"for {cost:0} to aim its radar-guided launcher.");
        return cost;
    }

    /// <summary>
    /// A radar truck for a site whose launcher has none, paid from the whole balance: 100 here is what
    /// makes a 700 launcher fire. Nothing else bought one for a site, only the two for the overwatch
    /// screen, so a commander with more manned sites than that left the rest silent.
    /// </summary>
    private float TryBuySiteRadar(FactionHQ hq, CommanderState state)
    {
        if (!state.WantsSiteRadar || CountRadarReserve(hq) > 0)
        {
            return 0f;
        }

        VehicleDefinition? radar = ChooseReconUnit(Mathf.Max(0f, hq.factionFunds));
        if (radar == null)
        {
            return 0f;
        }

        float cost = Mathf.Max(0f, radar.value);
        if (!CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        hq.ModifyUnitSupply(radar, 1);
        NoteOwned(radar);
        state.WantsSiteRadar = false;
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(radar)} "
                + $"for {cost:0} for a SAM site with no radar.");
        return cost;
    }

    /// <summary>
    /// The commander's radar SAMs, StratoLances for the sites and Boltstrikes for the bases, bought out
    /// of a fund rather than a review's slice, on every difficulty.
    /// </summary>
    /// <remarks>
    /// A launcher costs several convoy vehicles, so a per-review share never reaches the price.
    /// Saving means a poor commander gets one too, just later.
    /// </remarks>
    private float TryBuySamLauncher(FactionHQ hq, CommanderState state, float spendable)
    {
        // The army comes before the umbrella over it.
        if (ownedCombat < SamSiteAfterCombatVehicles && CommanderCaptureService.HasForwardBase(hq))
        {
            return 0f;
        }

        // Out of income as well as the pot, because the build reserve keeps the pot near nothing. Clock on
        // Time.time, so a review skipped by a dry spell still counts.
        if (state.SamFundAt >= 0f)
        {
            state.SamFund += CommanderEconomyService.GetEnemyIncomePerMinute(hq) * SamFundIncomeShare * (Time.time - state.SamFundAt) / 60f;
        }

        state.SamFundAt = Time.time;

        // Buildings spend the same balance, so the fund can claim more than is left.
        state.SamFund = Mathf.Min(state.SamFund, Mathf.Max(0f, hq.factionFunds));

        // No timer: money is the only limit. The pass bound only keeps one review finite.
        float share = spendable * SamSiteBudgetShare;
        float taken = 0f;
        for (int buy = 0; buy < AirDefenceBuysPerReview; buy++)
        {
            if (IsAirDefenceAtMax())
            {
                state.SamFund = 0f;
                return taken;
            }

            float available = state.SamFund + share - taken;

            // The fund is for the whole ring: while it is short, every radar SAM is followed by a launcher
            // from the plan's air-defence roll.
            VehicleDefinition? launcher = ChooseRingGun(hq);
            if (launcher == null && state.SamFundRingTurn && state.AirGuardShort)
            {
                launcher = Choose(available, EnemyPlan.AirDefence, airDefenceOnly: true);
            }

            launcher ??= ChooseRadarSam(hq, state);
            if (launcher == null && state.AirGuardShort)
            {
                launcher = Choose(available, EnemyPlan.AirDefence, airDefenceOnly: true);
            }

            if (launcher == null)
            {
                // Nothing to save for, so the convoys get the money back rather than it sitting idle.
                state.SamFund = 0f;
                return taken;
            }

            // Never past the price of the next launcher: Review holds the fund back from everything else,
            // so a fund with no ceiling stops all buying.
            float cost = Mathf.Max(0f, launcher.value);
            float topUp = Mathf.Clamp(cost - state.SamFund, 0f, Mathf.Max(0f, share - taken));
            state.SamFund += topUp;
            taken += topUp;
            if (state.SamFund < cost || !CommanderGameAccess.TrySpend(hq, cost))
            {
                break;
            }

            hq.ModifyUnitSupply(launcher, 1);
            NoteOwned(launcher);
            state.SamFund -= cost;
            bool radarSam = IsSamSiteLauncher(launcher) || launcher.unitName == BoltstrikeName;
            state.SamFundRingTurn = radarSam;
            TotalPurchases++;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(launcher)} for {cost:0} "
                    + (IsSamSiteLauncher(launcher) ? "to stand up a SAM site." : radarSam ? "for its radar SAM cover." : "for its air-defence ring."));
        }

        return taken;
    }

    /// <summary>A review with nothing above the build reserve still saves for and buys a radar SAM,
    /// which is most reviews of a commander saving for its next building.</summary>
    private void ReviewRadarSamWhileBroke(FactionHQ hq, CommanderState state)
    {
        if (CommanderUnitCap.IsFull(hq) || CommanderUnitCap.IsGroundFull(hq) || IsLandless(hq))
        {
            return;
        }

        CollectCatalog(hq);
        ReadOwnedVehicles(hq);

        // A launcher fires at nothing without its truck, and the truck is a tenth of a Boltstrike.
        TryBuyFireControl(hq, state, Mathf.Max(0f, hq.factionFunds));
        TryBuySiteRadar(hq, state);
        TryBuySamLauncher(hq, state, 0f);
    }

    /// <summary>A LADS or CRAM while a base is short of one of each, whichever is fewer. These come
    /// before the Boltstrike and the StratoLance.</summary>
    private VehicleDefinition? ChooseRingGun(FactionHQ hq)
    {
        VehicleDefinition? lads = FindInCatalog(LadsName);
        VehicleDefinition? cram = FindInCatalog(CramName);
        int bases = CommanderVictoryService.CountBases(hq);
        int ownedLads = lads != null ? CountOwned(hq, lads) : bases;
        int ownedCram = cram != null ? CountOwned(hq, cram) : bases;
        VehicleDefinition? gun = ownedLads >= bases && ownedCram >= bases ? null
            : ownedCram < ownedLads ? cram
            : lads ?? cram;
        return gun != null && CommanderUnitCap.Enabled && IsAirDefenceFull(gun) ? null : gun;
    }

    /// <summary>The next radar SAM to save for: whichever of the two the commander has fewer of,
    /// a Boltstrike on a tie, since it is cheaper and comes a depot level earlier.</summary>
    private VehicleDefinition? ChooseRadarSam(FactionHQ hq, CommanderState state)
    {
        VehicleDefinition? stratolance = state.WantsSamSite ? ChooseSamLauncherUnit(float.MaxValue) : null;
        VehicleDefinition? boltstrike = FindInCatalog(BoltstrikeName);
        int boltstrikes = boltstrike != null ? CountOwned(hq, boltstrike) : 0;
        if (boltstrike != null
            && (boltstrikes >= WantedBoltstrikes(hq) || (CommanderUnitCap.Enabled && IsAirDefenceFull(boltstrike))))
        {
            boltstrike = null;
        }

        return stratolance == null ? boltstrike
            : boltstrike == null ? stratolance
            : boltstrikes <= CountSamLaunchers(hq) ? boltstrike : stratolance;
    }

    /// <summary>The longest-reaching radar-guided launcher the fund allows. Price stands in for reach
    /// the same way it does in the airframe buy: the roster prices these by what they can hold off.</summary>
    private VehicleDefinition? ChooseSamLauncherUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value <= budget
                && IsSamSiteLauncher(definition)
                && (best == null || definition.value > best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>
    /// One counter-battery mount per artillery piece the player is fielding, bought ahead of the plan
    /// the way the first air-defence launcher is.
    /// </summary>
    /// <remarks>
    /// A loop rather than one per review, since artillery arrives in a mass. Still bounded by what this
    /// review may spend, so a poor commander closes the gap over several reviews.
    /// </remarks>
    private float TryBuyCounterBattery(FactionHQ hq, float spendable, int playerGuns)
    {
        if (playerGuns <= 0 || spendable <= 0f)
        {
            return 0f;
        }

        int owned = CountCounterBatteryVehicles(hq);
        float spent = 0f;
        while (owned < playerGuns && spent < spendable && !IsAirDefenceAtMax())
        {
            VehicleDefinition? mount = ChooseCounterBatteryUnit(spendable - spent);
            if (mount == null)
            {
                break;
            }

            float cost = Mathf.Max(0f, mount.value);
            if (!CommanderGameAccess.TrySpend(hq, cost))
            {
                break;
            }

            hq.ModifyUnitSupply(mount, 1);
            NoteOwned(mount);
            TotalPurchases++;
            owned++;
            spent += cost;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(mount)} "
                    + $"for {cost:0} against the player's artillery ({owned} mounts to {playerGuns} guns).");
        }

        return spent;
    }

    /// <summary>The best munition-killer the budget allows: highest <c>antiMissile</c> first, price
    /// only breaking a tie. There is no threshold to tune and no name to go stale — whatever this
    /// game's roster rates highest against incoming rounds is what gets bought.</summary>
    private VehicleDefinition? ChooseCounterBatteryUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value > budget || !IsCounterBatteryDefence(definition))
            {
                continue;
            }

            if (best == null
                || IsBetterCounterBattery(
                    definition.roleIdentity.antiMissile,
                    definition.value,
                    best.roleIdentity.antiMissile,
                    best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>The cheapest fire-control truck the budget allows. It fights nothing and its only
    /// job is to sit beside the launcher, so there is nothing to spend up for.</summary>
    private VehicleDefinition? ChooseFireControlUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value <= budget
                && IsFireControlVehicle(definition)
                && (best == null || definition.value < best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    private void CollectCatalog(FactionHQ hq)
    {
        catalog.Clear();

        // Each vehicle waits for its depot tier, the same rule the player's queue has.
        int depotLevel = CommanderEconomyService.GetBestDepotLevel(hq);
        List<Faction.ConvoyGroup> convoyGroups = hq.faction.GetConvoyGroups();
        for (int groupIndex = 0; groupIndex < convoyGroups.Count; groupIndex++)
        {
            List<Faction.ConvoyUnit> constituents = convoyGroups[groupIndex].Constituents;
            for (int unitIndex = 0; unitIndex < constituents.Count; unitIndex++)
            {
                if (constituents[unitIndex].Type is VehicleDefinition definition
                    && CommanderGameAccess.IsSpawnableVehicleDefinition(definition)
                    && CommanderEconomyService.GetRequiredDepotLevel(definition) <= depotLevel
                    && !CommanderUnitBanService.IsBanned(definition)
                    && !catalog.Contains(definition))
                {
                    catalog.Add(definition);
                }
            }
        }

        // Convoy groups only cover tank, mechanized infantry, mobile air defence and logistics, so SAM
        // parts and artillery are pulled from the full vehicle list instead. The convoy roll itself
        // keeps the mix the groups author.
        CollectRosterParts();
        for (int i = 0; i < rosterParts.Count; i++)
        {
            VehicleDefinition part = rosterParts[i];
            if (CommanderEconomyService.GetRequiredDepotLevel(part) <= depotLevel
                && !CommanderUnitBanService.IsBanned(part)
                && !catalog.Contains(part))
            {
                catalog.Add(part);
            }
        }
    }

    /// <summary>
    /// The specialist vehicles, out of every ground vehicle in the game. Built once per mission: it
    /// walks every prefab in the scene, which is far too much to do on a 30 s review.
    /// </summary>
    private void CollectRosterParts()
    {
        // The scanned flag, not the count: a roster with none of these would otherwise walk every
        // prefab in the scene again on every review of every commander, forever.
        if (rosterPartsScanned || !CommanderGameAccess.TryGetLocalVehicleDefinitions(candidates))
        {
            return;
        }

        rosterPartsScanned = true;
        for (int i = 0; i < candidates.Count; i++)
        {
            VehicleDefinition definition = candidates[i];

            // Never a towed one. The roster carries trailer versions of the laser and gun mounts and
            // of the launchers, and nothing drives an enemy commander's trailer anywhere: the
            // tractor machinery is the player's (CommanderMobileEmplacementService, local HQ only),
            // so a bought trailer stands at the depot it spawned beside for the rest of the match.
            // Never a nuclear launcher either: its uranium gate refuses it at the depot, and under GOD
            // DIFFICULTY, where that gate is lifted, ReviewGodNukes alone decides how many there are.
            if (CommanderGameAccess.IsTrailerVehicleDefinition(definition)
                || CommanderFactionVehicleService.GetWarheadCost(definition) > 0)
            {
                continue;
            }

            if (definition.value > 0f
                && (IsSamSiteLauncher(definition)
                    || IsFireControlVehicle(definition)
                    || IsCounterBatteryDefence(definition)
                    || definition.vehicleType == VehicleType.ART
                    || definition.vehicleType == VehicleType.RDR))
            {
                rosterParts.Add(definition);
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Specialist roster: {DescribeRosterParts()} — the vehicles no convoy group authors, so this is "
                + "every one a commander can reach for outside its four platoons.");
    }

    private string DescribeRosterParts()
    {
        if (rosterParts.Count == 0)
        {
            return "nothing in this game's vehicle roster";
        }

        System.Text.StringBuilder text = new();
        for (int i = 0; i < rosterParts.Count; i++)
        {
            VehicleDefinition part = rosterParts[i];
            string role = CommanderRadarService.IsRadarGuidedLauncherDefinition(part)
                ? "launcher"
                : IsFireControlVehicle(part) ? "fire control"
                : part.vehicleType == VehicleType.ART ? "artillery"
                : IsCounterBatteryDefence(part) ? "counter-battery" : "radar";
            if (i > 0)
            {
                text.Append(", ");
            }

            // The two role numbers are printed for the air-defence entries because which vehicle is
            // a laser/gun counter to artillery is serialized asset data a decompile cannot read, and
            // this predicate is the only thing standing between the player's rocket spam and a
            // commander that answers it.
            string identity = IsAirDefence(part)
                ? $", anti-air {part.roleIdentity.antiAir:0.##}, anti-munition {part.roleIdentity.antiMissile:0.##}"
                : string.Empty;
            text.Append($"{CommanderGameAccess.GetVehicleLabel(part)} [{role}, {part.value:0}{identity}]");
        }

        return text.ToString();
    }

    /// <summary>Drops state for HQs that went away with a scene the reset did not catch.</summary>
    private void PruneStates(FactionHQ localHq)
    {
        staleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            // A both-sides match swaps which side is "local" every half minute; that
            // side is resting, not gone.
            if (entry.Key == null || (!LocalSideRotates && ReferenceEquals(entry.Key, localHq)))
            {
                staleHqs.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleHqs.Count; i++)
        {
            states.Remove(staleHqs[i]);
        }
    }

    private void TickPersistentAsHost()
    {
        int mode = EffectiveMode;

        // The HQ check comes before the schedule on purpose. This ticks from the menu onwards, and
        // consuming a review while no mission is loaded is what put the first purchase up to a
        // review behind the start of the match — the enemy has to be spending from minute zero.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (mode == ModeOff || localHq == null)
        {
            return;
        }

        DriveJammers(localHq);

        // The home guard runs on its own, faster clock. An attack the commander can see coming has
        // to move units before it lands, and the buy review is half a minute wide.
        if (defenceQueue.Count == 0 && CommanderScheduler.IsDue(ref nextDefenceAt, DefenceReviewIntervalSeconds))
        {
            QueueAiFactions(defenceQueue, localHq);
        }

        if (reviewQueue.Count == 0 && CommanderScheduler.IsDue(ref nextReviewAt, ReviewIntervalSeconds))
        {
            queuedPlayerForce = ReadForce(localHq);
            QueueAiFactions(reviewQueue, localHq);
        }

        // Queued work waits out a pause like the clocks that queued it, and the unit ban vote: what
        // it bought now could turn out banned, and nothing spawns until the vote locks anyway.
        if (Time.timeScale <= 0f || CommanderUnitBanService.IsVoting)
        {
            return;
        }

        // One commander's worth of work a frame, and never on a frame another AI review already used.
        // The guard first: it is the one reacting to an attack.
        if (defenceQueue.Count > 0 && CommanderScheduler.TryClaimHeavyFrame())
        {
            FactionHQ hq = defenceQueue.Dequeue();
            if (hq != null && IsAiFaction(hq, localHq))
            {
                ReviewDefences(hq, localHq);
            }
        }
        else if (reviewQueue.Count > 0 && CommanderScheduler.TryClaimHeavyFrame())
        {
            FactionHQ hq = reviewQueue.Dequeue();
            if (hq != null && IsAiFaction(hq, localHq))
            {
                Review(hq, localHq, mode, queuedPlayerForce);
            }

            if (reviewQueue.Count == 0)
            {
                FinishReviewRound(localHq);
            }
        }
        else
        {
            TickAirBuys(localHq);
        }

        static void QueueAiFactions(Queue<FactionHQ> queue, FactionHQ localHq)
        {
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (IsAiFaction(hq, localHq))
                {
                    queue.Enqueue(hq);
                }
            }
        }
    }

    private void FinishReviewRound(FactionHQ localHq)
    {
        PruneStates(localHq);
        FactionHQ? primary = null;
        foreach (FactionHQ hq in states.Keys)
        {
            if (hq != null && IsAiFaction(hq, localHq) && (primary == null || hq.factionFunds > primary.factionFunds))
            {
                primary = hq;
            }
        }

        StatusLine = primary != null && states.TryGetValue(primary, out CommanderState primaryState)
            ? $"{GetPlanLabel(primaryState.Plan)}   {FundsLabel(primary.factionFunds)}"
                + (primaryState.Defending ? "   DEFENDING" : string.Empty)
            : string.Empty;
    }

    /// <summary>
    /// The airframes the last review decided to buy, launched one a frame. Stops on the first refusal;
    /// the fund keeps a poor commander to one.
    /// </summary>
    private void TickAirBuys(FactionHQ localHq)
    {
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            CommanderState state = entry.Value;
            if (state.PendingAirBuys <= 0)
            {
                continue;
            }

            // Stock launches with an empty fund: it only pays the stores, and a factory's output
            // sitting in the hangar while the fund saved up for a new airframe wasted the plant.
            if (entry.Key == null || !IsAiFaction(entry.Key, localHq) || (state.AirFund <= 0f && !HasAirStock(entry.Key)))
            {
                state.PendingAirBuys = 0;
                continue;
            }

            if (!CommanderScheduler.TryClaimHeavyFrame())
            {
                return;
            }

            float spent = BuyAirframe(entry.Key, state, Mathf.Max(0f, state.AirFund), state.AirBuyForce, state.AirBuyWealth);
            state.PendingAirBuys = spent > 0f ? state.PendingAirBuys - 1 : 0;
            state.AirFund -= Mathf.Max(0f, spent);
            return;
        }
    }

    /// <summary>One vehicle's parking spot: the post, its place in that post's cluster, and where
    /// that is on the ground.</summary>
    private readonly struct DefenceSlot
    {
        internal DefenceSlot(int post, int index, GlobalPosition position)
        {
            Post = post;
            Index = index;
            Position = position;
        }

        internal int Post { get; }

        internal int Index { get; }

        internal GlobalPosition Position { get; }
    }

    private struct ForceRead
    {
        internal int Aircraft;
        internal int Armour;
        internal int Guns;
        internal int AirDefence;
        internal int Ground;
        internal int Ships;
        internal float ShipValue;
        internal float ShipArmour;

        /// <summary>The opponent loses the duel with a ship (Carrier Duel): sinking it is the win.</summary>
        internal bool HomeAfloat;
    }

    private sealed class CommanderState
    {
        /// <summary>The home guard: each pinned vehicle and the ring post it holds.</summary>
        internal Dictionary<Unit, int> Defenders { get; } = new();

        /// <summary>The guard's support vehicles, radar, fire control and a munitions truck, and the
        /// post each one holds. Kept apart from <see cref="Defenders"/> because none of them is a
        /// combat vehicle: counted there they would eat the ring's targets and its half-the-army
        /// cap, and the guard would be launchers on paper and trucks on the ground.</summary>
        internal Dictionary<Unit, int> Support { get; } = new();

        /// <summary>Where in its post's cluster each guard and support vehicle parks, picked once so
        /// it does not shuffle every review.</summary>
        internal Dictionary<Unit, DefenceSlot> Slots { get; } = new();

        /// <summary>Where each guard vehicle short of its spot last made progress, and since when.</summary>
        internal Dictionary<Unit, (GlobalPosition Position, float Since)> GuardProgress { get; } = new();

        /// <summary>Ring stations around every base this commander holds, and how many bases that
        /// was when they were picked.</summary>
        internal List<GlobalPosition> DefencePosts { get; } = new();

        /// <summary>The centre of the base each entry of <see cref="DefencePosts"/> belongs to.</summary>
        internal List<GlobalPosition> DefencePostBases { get; } = new();

        /// <summary>Standing overwatch posts, picked once and then kept.</summary>
        internal List<GlobalPosition> ReconPosts { get; } = new();

        /// <summary>Each artillery vehicle's firing position, picked once and then kept.</summary>
        internal Dictionary<Unit, GlobalPosition> Batteries { get; } = new();

        /// <summary>The depot nearest the strike target, the only one new artillery rolls out of, and
        /// how far it is from that target. Re-read by <c>ReviewArtillery</c>.</summary>
        internal VehicleDepot? ArtilleryDepot { get; set; }

        /// <summary>The depot the next StratoLance rolls out of, picked each guard review.</summary>
        internal VehicleDepot? SamDepot { get; set; }

        internal VehicleDepot? SiteRadarDepot { get; set; }

        internal VehicleDepot? SiteFireControlDepot { get; set; }

        /// <summary>The fewest sites that give every base one, set when the posts are laid.</summary>
        internal int SamSitesToCoverBases { get; set; }

        internal float ArtilleryDistance { get; set; }

        /// <summary>Where every ballistic launcher parks, round one ammo dump. Picked once.</summary>
        internal GlobalPosition? StrikeSite { get; set; }

        /// <summary>Strike-site slots handed out so far, so each launcher gets its own.</summary>
        internal int StrikeSlots { get; set; }

        /// <summary>The base each firing ballistic launcher is shelling.</summary>
        internal Dictionary<Unit, Airbase> StrikeAims { get; } = new();

        /// <summary>The munitions truck that keeps the strike site's dump stocked from a bunker.</summary>
        internal Unit? StrikeTruck { get; set; }

        internal bool StrikeTruckLoading { get; set; }

        internal float NextStrikeTruckBuyAt { get; set; }

        internal EnemyPlan Plan { get; set; }

        internal EnemyPlan Pending { get; set; }

        internal int PendingReviews { get; set; }

        internal bool Prepared { get; set; }

        /// <summary>Funds set aside for the next airframe and the next hull. Both are worth several
        /// ground vehicles, so a per-review slice has to accumulate or it never buys anything.</summary>
        internal float AirFund { get; set; }

        /// <summary>The dearest airframe the air fund may save for: its share over <c>FundSaveReviews</c>,
        /// which is also the fund's ceiling, so a goal is always reachable.</summary>
        internal float AirSaveCap { get; set; }

        /// <summary>Whether the next strike airframe is bought as a bomber or for close support;
        /// flips after each strike buy.</summary>
        internal bool NextStrikeBombs { get; set; }

        internal float NavalFund { get; set; }

        /// <summary>Funds set aside for the next gold mine out of the income, and when it last grew.
        /// See <c>GetSavedBuildReserve</c>.</summary>
        internal float MineFund { get; set; }

        internal float MineFundAt { get; set; } = -1f;

        internal float WarFundAt { get; set; } = -1f;

        /// <summary>Hulls bought and waiting at the rally point for their wave, and since when.</summary>
        internal HashSet<Ship> NavalRallying { get; } = new();

        internal float NavalWaitingSince { get; set; }

        /// <summary>Airframes the last review wants launched, and what it knew when it decided.
        /// See <c>TickAirBuys</c>.</summary>
        internal int PendingAirBuys { get; set; }

        internal ForceRead AirBuyForce { get; set; }

        internal float AirBuyWealth { get; set; }

        /// <summary>The same, for the next SAM site launcher, and for the same reason.</summary>
        internal float SamFund { get; set; }

        /// <summary><c>Time.time</c> income was last paid into <see cref="SamFund"/>, or negative for never.</summary>
        internal float SamFundAt { get; set; } = -1f;

        /// <summary><c>Time.time</c> income was last paid into the naval fund, or negative for never.</summary>
        internal float NavalFundAt { get; set; } = -1f;

        /// <summary>The last fund purchase was a radar SAM, so the ring's other launchers are next.</summary>
        internal bool SamFundRingTurn { get; set; }

        /// <summary>Short of a radar-guided launcher for one of its base SAM sites.</summary>
        internal bool WantsSamSite { get; set; }

        /// <summary>Short of a radar vehicle for the overwatch screen.</summary>
        internal bool WantsReconUnit { get; set; }

        /// <summary>Short of vehicles for the home guard, so the buy loop should get one.</summary>
        internal bool WantsDefenceUnit { get; set; }

        /// <summary>The ring's ground half is short, launchers aside. See <c>IsWantedByGuard</c>.</summary>
        internal bool GuardShort { get; set; }

        /// <summary>This commander plays CHALLENGING, as of its last review. Cached for the
        /// damage hook, which runs on every hit.</summary>
        internal bool Challenging { get; set; }

        /// <summary>CHALLENGING with the income-first tactics (<c>HasChallengingTactics</c>), cached
        /// for the same reason.</summary>
        internal bool Tactics { get; set; }

        /// <summary>One of its income buildings was hit: until when the fighters guard it, and where.
        /// See <c>NotifyUnitDamaged</c>.</summary>
        internal float EcoAlarmUntil { get; set; }

        internal GlobalPosition EcoAlarmAt { get; set; }

        /// <summary>An opponent fired a Piledriver: until when Boltstrikes come first, and whether the
        /// army and air buys are paused for them right now. See <c>TryBuyPiledriverDefence</c>.</summary>
        internal float PiledriverAlarmUntil { get; set; }

        internal bool PiledriverPause { get; set; }

        /// <summary>When the pause last bought a Boltstrike or started.</summary>
        internal float PiledriverPauseSince { get; set; }

        /// <summary>The pause timed out; it stays off until a Boltstrike is bought or the alarm ends.</summary>
        internal bool PiledriverGaveUp { get; set; }

        /// <summary>The alarm just ended, so the fighters it sent are recalled on the next review.</summary>
        internal bool EcoRecall { get; set; }

        /// <summary>The strike aircraft that went out with the current wave. A striker launched after
        /// it left waits at the staging base for the next one instead of flying in alone.</summary>
        internal HashSet<Aircraft> WaveMembers { get; } = new();

        /// <summary>The ring is short of air defence.</summary>
        internal bool AirGuardShort { get; set; }

        /// <summary>Funds a CHALLENGING commander has saved toward its next gun.</summary>
        internal float ArtilleryFund { get; set; }

        /// <summary>Its ring holds a radar-guided launcher with no fire-control truck to aim it.</summary>
        internal bool WantsFireControl { get; set; }

        /// <summary>A site with a launcher on it has no radar truck.</summary>
        internal bool WantsSiteRadar { get; set; }

        internal int DefenceBaseCount { get; set; } = -1;

        /// <summary>The bases <see cref="DefencePosts"/> were laid for.</summary>
        internal List<Airbase> DefenceBases { get; } = new();

        /// <summary>Defence posture: it holds until this time, and whether it is on right now.</summary>
        internal float ThreatUntil { get; set; }

        internal bool Defending { get; set; }

        /// <summary>Last reason this commander bought no aircraft, so it is only logged once.</summary>
        internal string LastAirDenial { get; set; } = string.Empty;

        /// <summary>Last review had nothing to spend, so "buying nothing" has been logged.</summary>
        internal bool Broke { get; set; }

        /// <summary>The strike wing is out attacking the player's base as one wave rather than
        /// holding over its staging base. Kept so the change is logged once.</summary>
        internal bool WaveOut { get; set; }

        /// <summary>When the first striker began waiting at staging for a wave (0: none waiting).</summary>
        internal float WaveWaitSince { get; set; }

        /// <summary>Strikers plus escorts the wave out left with.</summary>
        internal int WaveLeftWith { get; set; }

        /// <summary>The size and the longest wait the next wave is rolled with (0: not rolled yet).
        /// Rolled per wave so the player cannot learn "three, every two minutes".</summary>
        internal int WaveGoal { get; set; }

        internal float WaveWaitLimit { get; set; }

        /// <summary>When the next neutral capture landing may go (see the expansion partial).</summary>
        internal float NextNeutralAirCaptureAt { get; set; }

        /// <summary>The helicopter flying the current builder airlift, if one is.</summary>
        internal Aircraft? AirliftHelo { get; set; }

        /// <summary>Bases with a builder airlift on its way, and the helicopter carrying it.</summary>
        internal Dictionary<Airbase, Aircraft> BaseAirlifts { get; } = new();

        /// <summary>Bases with a builder landing run on its way, until when it is given to arrive.</summary>
        internal Dictionary<Airbase, float> BuilderRunUntil { get; } = new();

        internal float NextBuilderRunAt { get; set; }

        /// <summary>Lifts started on each stranded builder, and until when it is left alone once over the limit.</summary>
        internal Dictionary<Unit, (int Count, float Until)> StrandedTries { get; } = new();

        /// <summary>When another airlift helicopter may be launched.</summary>
        internal float NextAirliftLaunchAt { get; set; }

        /// <summary>Earliest time for the next builder spawned on an airlift hook.</summary>
        internal float NextSpawnLiftAt { get; set; }

        /// <summary>The last per-base guard line logged, and when the next may be.</summary>
        internal string LastGuardReport { get; set; } = string.Empty;

        internal float NextGuardReportAt { get; set; }

        /// <summary>GOD DIFFICULTY's divine wrath: until when, and the base it is avenging.</summary>
        internal float WrathUntil { get; set; }

        internal GlobalPosition WrathTarget { get; set; }

        /// <summary>Wrath just ended, so the fighters it sent are recalled on the next review.</summary>
        internal bool WrathRecall { get; set; }

        /// <summary>GOD DIFFICULTY's nuclear threat: armed, when the next launcher is ordered, and its launchers.</summary>
        internal bool NuclearArmed { get; set; }

        internal float NextLauncherBuyAt { get; set; }

        internal HashSet<Unit> NuclearLaunchers { get; } = new();

        internal HashSet<Unit> AaScouts { get; } = new();

        internal float NextAaScoutAt { get; set; }
    }
}
