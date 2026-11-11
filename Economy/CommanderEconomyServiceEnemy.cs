using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's half of the economy: it builds and upgrades the same mines and factories
/// the player does, at the same prices, out of its own faction funds. One purchase per review, and
/// never below a reserve multiple, so it does not starve the unit spender it shares the pot with.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    /// <summary>Sites tried per enemy build before it gives up and waits for the next review.</summary>
    private const int EnemySiteAttempts = 12;

    /// <summary>
    /// The dock search: bearings marched out from each base, the march step (each step is one
    /// terrain probe, so this is the cost knob), bisections to pin the water's edge, and how many
    /// spots stepping back inland from it are offered to the site rule. The search only runs until a
    /// dock is placed or the commander gives up for its current set of bases.
    /// </summary>
    private const int ShoreBearings = 12;
    private const float ShoreMarchStepMeters = 250f;

    /// <summary>How far water has to carry on past the shore for it to be the sea and not a pond.</summary>
    private const float OpenWaterMeters = 1000f;

    /// <summary>How far the water has to stay connected from the dock, by any route, to count as the
    /// sea rather than a lake. Checked on a <see cref="SeaFloodStepMeters"/> grid.</summary>
    private const float SeaReachMeters = 5000f;
    private const float SeaFloodStepMeters = 250f;
    private const int ShoreEdgeBisections = 5;
    private const int ShoreSiteTries = 4;
    private const float ShoreSiteBackoffMeters = 20f;

    /// <summary>Champions: flat income an AI commander gets every minute, on top of its buildings.</summary>
    private const float ChampionsEnemyIncomePerMinute = 100f;

    /// <summary>Champions: extra income per minute an AI commander gets for each human player on another team.</summary>
    private const float ChampionsEnemyIncomePerOpponentPerMinute = 75f;

    /// <summary>Team Duel PvE: extra income per minute the AI commander gets for each human player on the other team.</summary>
    private const float TeamPveEnemyIncomePerOpponentPerMinute = 10f;

    /// <summary>Champions: gold mines per base before the next one moves up from the back line.</summary>
    private const int ChampionsMinesPerRearBase = 2;

    /// <summary>Champions, GOD included: gold mines an AI commander gets for nothing, on NORMAL and
    /// on CHALLENGING.</summary>
    private const int ChampionsFreeMines = 1;
    private const int ChampionsChallengingFreeMines = 2;

    /// <summary>What the ladder saves for a free mine. Not 0: a reserve of 0 means the economy is
    /// finished, and <see cref="ReviewEnemy"/> stops there.</summary>
    private const float FreeMineReserve = 1f;

    /// <summary>Air-defence structures a commander keeps at each base it holds: the first one before
    /// any income, the second once its mines and factories are up. It was three for the whole
    /// faction, sited by a dart anywhere, and they were machine-gun posts.</summary>
    private const int EnemyDefenceBuildingsPerBase = 2;

    /// <summary>The dock level the commander saves for straight after its mines; the rest of the
    /// ladder comes before the level above it.</summary>
    private const int EnemyEarlyDockLevel = 2;

    /// <summary>A defence structure this close to a base counts as that base's.</summary>
    private const float DefenceCoverageMeters = 3000f;

    /// <summary>Munitions bunkers a commander builds: one, and only when it has none at all. Every base
    /// on the mod's maps already has one.</summary>
    private const int EnemySupplyBuildingTarget = 1;

    /// <summary>A base counts as having radar cover with a radar building this close to it.</summary>
    private const float RadarCoverageMeters = 3000f;

    /// <summary>How near a base a vehicle depot has to be to count as that base's depot.</summary>
    private const float DepotCoverageMeters = 3000f;

    /// <summary>Reviews a commander may fail to site a depot before it stops saving up for one.
    /// A base whose ground refuses a depot must not hold a review's funds hostage all match - the
    /// same escape the naval dock has when the map gave the commander no coast.</summary>
    private const int EnemyDepotSiteGiveUp = 5;

    /// <summary>Darts thrown for a depot site. More than an ordinary building gets: a depot needs a
    /// road and an exit that is not a hillside, so most sites near a base are refused.</summary>
    private const int EnemyDepotSiteAttempts = 40;

    /// <summary>A base counts as supplied with a munitions bunker this close to it. Matched to the
    /// range a resupply actually draws over, <c>CommanderSettings.SupplyStoreRangeKm</c>.</summary>
    private const float SupplyCoverageMeters = 5000f;

    /// <summary>Ring a base-adjacent structure is dropped into: near enough to be part of the base,
    /// far enough not to be on the apron.</summary>
    private const float BaseSiteMinMeters = 200f;
    private const float BaseSiteMaxMeters = 900f;

    /// <summary>The mine ring, and the gap kept between mines, as shares of the build radius. Spread so
    /// one pass cannot kill them all, but kept inside the base's own air defence. Wide enough for the
    /// duel's largest roll at this spacing, which `Economy.SelfCheck` asserts.</summary>
    private const float MineRingMinShare = 0.25f;
    private const float MineRingMaxShare = 0.55f;
    private const float MineSpacingShare = 0.28f;

    /// <summary>The Fuel Exchange and Gas Terminal go on a ring inside the gold mines', at half their
    /// spacing, so they stay close to the base.</summary>
    private const float StarterRingMinShare = 0.08f;

    /// <summary>Starters go this far from the base's nearest depot first, clear of its exit lane, and
    /// only then out on the ring.</summary>
    private const float StarterDepotMinMeters = 100f;
    private const float StarterDepotMaxMeters = 350f;

    /// <summary>Gap between income buildings beside a depot: the ring's spacing does not fit there.</summary>
    private const float StarterDepotSpacingMeters = 100f;

    /// <summary>Darts thrown for a spread mine site, more than an ordinary building gets: the ring
    /// is wide and most of it is road, water or ground something else already holds.</summary>
    private const int EnemyMineSiteAttempts = 24;

    /// <summary>An ammo dump this close to a strike site serves it.</summary>
    private const float StrikeDumpCoverMeters = 300f;

    /// <summary>Furthest from the middle of a strike site its dump may be put.</summary>
    private const float StrikeDumpSiteMeters = 60f;

    private static readonly List<Unit> WreckScratch = new();

    /// <summary>The duel's mine counts to roll from, mostly three.</summary>
    private static readonly int[] DuelEnemyMineRolls = { 2, 3, 3, 4 };

    /// <summary>Each commander's rolled duel mine count, see <see cref="RollEnemyMines"/>.</summary>
    private static readonly Dictionary<FactionHQ, int> EnemyMineRolls = new();

    /// <summary>The most gold mines each commander has had at once, so a bombed one is rebuilt first.</summary>
    private static readonly Dictionary<FactionHQ, int> EnemyMinePeak = new();

    /// <summary>Free gold mines each commander has finished. Counted at completion, so a free
    /// blueprint that is refunded (lost base, no builder) leaves the free mine still to come.</summary>
    private static readonly Dictionary<FactionHQ, int> EnemyFreeMinesBuilt = new();

    /// <summary>Its own dice: UnityEngine.Random is reseeded to 42 at every level load.</summary>
    private static readonly System.Random MineDice = new();

    private readonly List<Unit> dumpScratch = new();
    private readonly Dictionary<FactionHQ, int> depotSiteFailures = new();

    /// <summary>HQs already told, once, that this map gives them nowhere to put a dock.</summary>
    /// <summary>Commanders whose dock search failed, with how many bases they held then. A new base
    /// may have a coast, so the search runs again once that count changes.</summary>
    private readonly Dictionary<FactionHQ, int> shoreSearchFailed = new();

    /// <summary>The structure each building category resolves to, decided once per mission.</summary>
    private readonly Dictionary<BuildingType, BuildingDefinition?> categoryDefinitions = new();

    /// <summary>
    /// What this commander is saving up for, or 0 when its economy is finished. Two spenders share
    /// one <c>factionFunds</c> pool — this service and <see cref="CommanderEnemyCommanderService"/>
    /// — and the unit spender takes a fixed fraction of the balance every review, so the balance
    /// never climbed to a factory's price: the enemy built its opening mines out of the duel head
    /// start and then bought convoys with everything for the rest of the match. The unit spender
    /// now subtracts this from the pot it may touch, which is the same "save for it" fix
    /// <c>AccrueFund</c> is for airframes and hulls, and the build gates below ask for the plain
    /// price instead of a multiple of it, because a reserved price is already protected.
    /// </summary>
    /// <summary>The home income is up: the rest of the ladder is depots, factories and docks.</summary>
    internal static bool HasEnemyHomeIncome(FactionHQ hq)
    {
        return Instance != null && Instance.CountHomeIncome(hq) >= GetUpgradeMines(hq);
    }

    /// <summary>
    /// GOD DIFFICULTY's opening: until its <see cref="GodEnemyFactoryTarget"/> aircraft plants are built, not
    /// just placed (or
    /// <see cref="GodOpeningMaxSeconds"/> pass, so a map with no room for them cannot stall it), the money
    /// goes to plants, gold mines, the air push and air defence, and the attack convoys wait. Plants
    /// out-earn bought airframes over a match; bought outright, the bank went on aircraft and convoys
    /// and the first plant stood at minute seven.
    /// </summary>
    internal static bool IsGodOpening(FactionHQ hq)
    {
        // Built plants: the six blueprints go down in the first seconds, and counting those ended the
        // opening before a single plant stood.
        return IsGodEconomy(hq)
            && Time.timeSinceLevelLoad < GodOpeningMaxSeconds
            && CountBuiltFactories(hq) < GodEnemyFactoryTarget;
    }

    /// <summary>What the opening plants still to come cost, held back from the unit spender.</summary>
    internal static float GetGodOpeningReserve(FactionHQ hq)
    {
        // Placed plants are paid for, so only the ones not yet placed are held.
        // ponytail: priced as the next plant times the plants left; the next is the dearest new line,
        // so this over-holds a little, never under.
        return Instance != null && IsGodOpening(hq)
            ? Mathf.Max(0, GodEnemyFactoryTarget - CountFactories(hq)) * Instance.GetEnemyFactoryReserve(hq)
            : 0f;
    }

    // goldMine: the reserve is a gold mine's price. The unit spender saves for that one out of a
    // share of the income instead of holding the whole price back.
    // income: the rung pays money back (a mine, an income upgrade or a mine's AA guard), so the unit
    // spender holds it even while its army is short.
    internal static float GetEnemyBuildReserve(FactionHQ hq, out bool goldMine, out bool income)
    {
        goldMine = false;
        income = false;
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null || hq.faction == null)
        {
            return 0f;
        }

        // The depot upgrade needs no builders, so it does not queue behind a blueprint, and nothing
        // but the income comes before it (not the radar, the shelf or the AA post): the StratoLance
        // and every MBT are level-2 purchases, and behind the whole ladder a twelve-minute match
        // never saw one of either.
        if (service.HasDepotUpgradeIncome(hq) && GetBestDepotLevel(hq) < 2 && service.FindEnemyDepotToUpgrade(hq) != null)
        {
            return DepotUpgradeCost;
        }

        if (CountBlueprints(hq) >= GetEnemyBlueprintLimit(hq))
        {
            // Those are already paid for; saving for the next while the builders work on them
            // would only starve the convoys.
            return 0f;
        }

        if (service.IsRebuildingIncome(hq))
        {
            CommanderBuildKind rebuild = service.ChooseEnemyIncomeKind(hq);
            goldMine = rebuild == CommanderBuildKind.Mine;
            income = true;
            return service.GetEnemyIncomeReserve(hq, rebuild);
        }

        // An income building comes before the radar and the dock: the Duel pays an AI nothing else.
        if (service.CountMines(hq) == 0)
        {
            income = true;
            return GetEnemyMineReserve(service, hq, out goldMine);
        }

        // One radar, one shelf and one AA post, then income, then the same again at every other base.
        // Every base first was fine on the duel's single base; in Champions the enemy holds seven,
        // spent the whole match on masts and AA posts, never reached a mine and went broke.
        // A base that cannot see the attack coming outranks income: the defence posture reads the
        // tracking database that radar fills.
        BuildingDefinition? radar = service.ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar != null
            && CountBuildings(hq, BuildingType.RDR) == 0
            && TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out _))
        {
            return GetStructureCost(radar);
        }

        // A commander that cannot fuel and arm what it buys outranks income: with an empty shelf
        // every convoy and every sortie is refused at the door, and the balance just piles up.
        BuildingDefinition? supply = service.ResolveCategoryDefinition(BuildingType.AMMO, preferDearest: true);
        if (supply != null && CountBuildings(hq, BuildingType.AMMO) < EnemySupplyBuildingTarget)
        {
            return GetStructureCost(supply);
        }

        BuildingDefinition? defence = service.ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence != null && CountBuildings(hq, BuildingType.DEF) == 0 && TryGetUndefendedBase(hq, 1, out _))
        {
            return GetStructureCost(defence);
        }

        // Then upgrade existing income, best payback first, ahead of the dock and any new building.
        if (service.FindEnemyIncomeUpgrade(hq, out float upgrade) != null)
        {
            income = true;
            return upgrade;
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;

        // Home economy before the frontier: the dock, the opening mines, then the dock's first upgrade,
        // ahead of per-base rungs (which grow with every base captured). GetUpgradeMines does not grow
        // with bases held. The dock goes first because its builder may need a long drive or a lift.
        // GOD: the fleet waits for the opening plants.
        float dock = IsGodOpening(hq) ? 0f : service.GetEnemyDockCost(hq, 1);
        if (dock > 0f)
        {
            return dock;
        }

        // Any income building counts, not only gold mines, or a poor commander never reaches factories.
        if (service.CountHomeIncome(hq) < GetUpgradeMines(hq))
        {
            income = true;
            return GetEnemyMineReserve(service, hq, out goldMine);
        }

        // The first gold mine goes ahead of the plants. It pays itself back in about 4 minutes.
        if (service.CountMines(hq, CommanderBuildKind.Mine) == 0 && service.IsEnemyMineOpen(hq))
        {
            income = true;
            return GetEnemyMineReserve(service, hq, out goldMine);
        }

        // The opening plants straight after the home income, ahead of every per-base rung: the air
        // force comes out of them. A plant's price follows its product and the Unit Prices profile.
        float factory = CountFactories(hq) < GetEnemyFactoryTarget(hq, duel)
            ? service.GetEnemyFactoryReserve(hq)
            : 0f;
        if (factory > 0f)
        {
            return factory;
        }

        dock = service.GetEnemyDockCost(hq, EnemyEarlyDockLevel);
        if (dock > 0f)
        {
            return dock;
        }

        // No per-base bunker rung: every base on the mod's maps comes with one.

        // Uranium for the GPO-N, once the income is there. Behind the per-base rungs it was never
        // reached: every base captured reopens a depot, a radar and two AA posts ahead of it.
        float enrichment = service.GetEnemyEnrichmentCost(hq, out _, out _);
        if (enrichment > 0f)
        {
            return enrichment;
        }

        // Then a depot at every base. FactionHQ.DeployVehicles rolls the reserve out of the depots
        // the faction owns, nearest the front first, so a commander holding seven bases and one
        // depot started every convoy of the match from the same corner of the map and the player
        // only ever saw an attack from one direction.
        BuildingDefinition? depot = service.ResolveCategoryDefinition(BuildingType.DEP, preferDearest: false);
        if (depot != null
            && service.GetDepotSiteFailures(hq) < EnemyDepotSiteGiveUp
            && TryGetUncoveredBase(hq, BuildingType.DEP, DepotCoverageMeters, out _))
        {
            return GetStructureCost(depot);
        }

        // Income after every base can field and arm what it buys: seven mines ahead of these left
        // six Champions bases with nothing and the whole army rolling out of the capital.
        if (service.IsBelowEnemyIncomeTarget(hq))
        {
            income = true;
            return GetEnemyMineReserve(service, hq, out goldMine);
        }

        // CHALLENGING: more doors. Its depots are what caps its army, not its money.
        if (depot != null
            && CommanderEnemyCommanderService.IsChallenging(hq)
            && service.GetDepotSiteFailures(hq) < EnemyDepotSiteGiveUp
            && TryGetUncoveredBase(hq, BuildingType.DEP, DepotCoverageMeters, out _, CommanderEnemyCommanderService.ChallengingDepotsPerBase))
        {
            return GetStructureCost(depot);
        }

        if (radar != null && TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out _))
        {
            return GetStructureCost(radar);
        }

        if (defence != null && TryGetUndefendedBase(hq, 1, out _))
        {
            return GetStructureCost(defence);
        }

        if (defence != null && TryGetUndefendedBase(hq, EnemyDefenceBuildingsPerBase, out _))
        {
            return GetStructureCost(defence);
        }

        // Then income for the rest of the match, with no ceiling: a player has none, and capping the AI
        // at twice its mine target is what let the player out-earn it. The mine rung saves out of a
        // share of income (MineFund), so the army keeps the rest. Factories and the top dock level are
        // bought out of turn (GetEnemyFactoryFund, ReviewEnemy), so nothing is starved behind this.
        // ponytail: a commander with no mine site left just parks a mine fund (at most one price).
        income = true;
        return GetEnemyMineReserve(service, hq, out goldMine);
    }

    /// <summary>
    /// An ammo dump at the enemy commander's ballistic strike site, so its launchers reload where
    /// they stand and its munitions truck has somewhere to unload. One per site: true while a dump
    /// or its blueprint already stands within <see cref="StrikeDumpCoverMeters"/>, or one was queued.
    /// </summary>
    internal bool EnsureEnemyStrikeDump(FactionHQ hq, GlobalPosition site)
    {
        BuildingDefinition? dump = FindDumpDefinition();
        if (dump == null)
        {
            return false;
        }

        CommanderSupplyStoreService.CollectStores(hq, dumpScratch);
        for (int i = 0; i < dumpScratch.Count; i++)
        {
            if (CommanderSupplyStoreService.IsDump(dumpScratch[i])
                && FastMath.InRange(dumpScratch[i].GlobalPosition(), site, StrikeDumpCoverMeters))
            {
                dumpScratch.Clear();
                return true;
            }
        }

        dumpScratch.Clear();
        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite blueprint = constructionSites[i];
            if (ReferenceEquals(blueprint.Hq, hq)
                && blueprint.Definition == dump
                && FastMath.InRange(blueprint.Site, site, StrikeDumpCoverMeters))
            {
                return true;
            }
        }

        // Waits its turn behind the ladder's blueprints, like any other build: one per builder.
        if (CountBlueprints(hq) >= GetEnemyBlueprintLimit(hq))
        {
            return false;
        }

        // The middle of the site first, so every launcher parked round it is inside the dump's reach.
        float cost = GetStructureCost(dump);
        for (int attempt = 0; attempt < EnemySiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = attempt == 0 ? 0f : Random.Range(10f, StrikeDumpSiteMeters);
            GlobalPosition candidate = new(
                site.x + (Mathf.Cos(angle) * distance),
                site.y,
                site.z + (Mathf.Sin(angle) * distance));
            if (preview.IsSiteAllowed(dump, candidate, hq, out _)
                && QueueEnemyBlueprint(hq, CommanderBuildKind.Structure, dump, null, candidate, cost, GetStructureLabel(dump)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How far an ammo dump reaches to rearm a vehicle beside it, off the prefab
    /// (<c>Rearmer.Range</c> is asset data), or 0 when the game has no dump.</summary>
    internal float GetDumpReach()
    {
        BuildingDefinition? dump = FindDumpDefinition();
        Rearmer? rearmer = dump?.unitPrefab != null ? dump.unitPrefab.GetComponentInChildren<Rearmer>(true) : null;
        return rearmer != null ? rearmer.Range : 0f;
    }

    /// <summary>
    /// Mines the enemy aims for. In Champions it is one per base it holds (at least the duel's
    /// four): an enemy holding the whole map should out-earn the player, and every base the player
    /// takes away lowers the target it rebuilds toward.
    /// </summary>
    /// <summary>The price of the next income building, or of the AA post a new mine waits for.</summary>
    private static float GetEnemyMineReserve(CommanderEconomyService service, FactionHQ hq, out bool goldMine)
    {
        CommanderBuildKind kind = service.ChooseEnemyIncomeKind(hq);
        if (kind == CommanderBuildKind.Mine && service.NeedsMineDefence(hq, out BuildingDefinition? guard, out _))
        {
            goldMine = false;
            return GetStructureCost(guard!);
        }

        goldMine = kind == CommanderBuildKind.Mine;
        return service.GetEnemyIncomeReserve(hq, kind);
    }

    /// <summary>How many mines this commander wants before it upgrades its depot: the duel's rolled
    /// count, never more than its mine target (Champions' target is one per base, far too late).</summary>
    private static int GetUpgradeMines(FactionHQ hq)
    {
        return CommanderEnemyCommanderService.IsDuelMission
            ? RollEnemyMines(hq)
            : Mathf.Min(EnemyMineTarget, GetEnemyMineTarget(hq));
    }

    /// <summary>
    /// Blueprints a commander may have waiting at once: one per builder within the build radius of a
    /// base it holds, so every crew has its own site. At least one, or a commander that lost every
    /// builder would never queue the blueprint that makes it buy the next.
    /// </summary>
    private static int GetEnemyBlueprintLimit(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 1;
        }

        float radius = CommanderSettings.BuildRadiusKm * 1000f;
        int builders = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle
                || unit.disabled
                || !CommanderRepairService.IsRepairUnit(unit))
            {
                continue;
            }

            GlobalPosition at = unit.GlobalPosition();
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (airbase != null && airbase.center != null && FastMath.InRange(at, airbase.center.GlobalPosition(), radius))
                {
                    builders++;
                    break;
                }
            }
        }

        return Mathf.Max(1, builders);
    }

    /// <summary>GOD DIFFICULTY: ten billion in the bank, so it skips the fuel and gas starters for
    /// gold mines, builds them in parallel and opens on <see cref="GodEnemyFactoryTarget"/> aircraft plants.</summary>
    private static bool IsGodEconomy(FactionHQ hq)
    {
        return CommanderEnemyCommanderService.IsChampionsMission && CommanderGodService.IsGod(hq);
    }

    private static int GetEnemyFactoryTarget(FactionHQ hq, bool duel)
    {
        return IsGodEconomy(hq) ? GodEnemyFactoryTarget : duel ? DuelEnemyFactoryTarget : EnemyFactoryTarget;
    }

    /// <summary>The free mines are for the side that was dealt the map, not one on a single base.</summary>
    private static bool HoldsMostBases(FactionHQ hq)
    {
        int bases = CountLandBases(hq);
        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            if (other != null && other != hq && CountLandBases(other) >= bases)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountLandBases(FactionHQ hq)
    {
        int bases = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (CommanderGameAccess.IsLandBase(airbase))
            {
                bases++;
            }
        }

        return bases;
    }

    /// <summary>The duel mine count, rolled once per commander per mission, mostly three.</summary>
    private static int RollEnemyMines(FactionHQ hq)
    {
        if (!EnemyMineRolls.TryGetValue(hq, out int mines))
        {
            mines = DuelEnemyMineRolls[MineDice.Next(DuelEnemyMineRolls.Length)];
            EnemyMineRolls[hq] = mines;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) will build {mines} income buildings before its factories and depot upgrade.");
        }

        return mines;
    }

    private static int GetEnemyMineTarget(FactionHQ hq)
    {
        if (!CommanderEnemyCommanderService.IsDuelMission)
        {
            return EnemyMineTarget;
        }

        int rolled = RollEnemyMines(hq);
        int bases = CountLandBases(hq);

        // On the duel every base it captures is worth one more gold mine, which the sparsest-base
        // rule then puts on that base. The rolled count was the whole target, so a captured base got
        // none and every mine sat round the home strip.
        return CommanderEnemyCommanderService.IsChampionsMission
            ? Mathf.Max(rolled, bases)
            : rolled + Mathf.Max(0, bases - 1);
    }

    /// <summary>The first base this faction holds with no building of that category near it.</summary>
    private static bool TryGetUncoveredBase(
        FactionHQ hq,
        BuildingType type,
        float coverage,
        out GlobalPosition center,
        int wanted = 1)
    {
        center = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!CommanderGameAccess.IsLandBase(airbase) || CommanderCaptureService.IsBeingCaptured(airbase))
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            if (CountBuildingsNear(hq, type, candidate, coverage) < wanted)
            {
                center = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool HasBuildingNear(FactionHQ hq, BuildingType type, GlobalPosition position, float radius)
    {
        return CountBuildingsNear(hq, type, position, radius) > 0;
    }

    private static int CountBuildingsNear(FactionHQ hq, BuildingType type, GlobalPosition position, float radius)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && IsBuildingOfTypeNear(unit, type, position, radius))
            {
                count++;
            }
        }

        // A wrecked building still counts: the repair crews bring it back, and counting it out is
        // what had the commander put a second depot down beside a bombed one. factionUnits cannot
        // see it (UnitDisabled calls RemoveFactionUnit), so the wrecks come off the game's own
        // repair list.
        CommanderRepairService.CollectRepairable(hq, WreckScratch);
        for (int i = 0; i < WreckScratch.Count; i++)
        {
            if (WreckScratch[i].disabled && IsBuildingOfTypeNear(WreckScratch[i], type, position, radius))
            {
                count++;
            }
        }

        return count + CountBlueprintsNear(hq, type, position, radius);
    }

    private static bool IsBuildingOfTypeNear(Unit? unit, BuildingType type, GlobalPosition position, float radius)
    {
        return unit != null
            && unit is Building
            && unit.definition is BuildingDefinition definition
            && definition.buildingType == type
            && FastMath.InRange(unit.transform.GlobalPosition(), position, radius);
    }

    /// <summary>The first base this faction holds with fewer than <paramref name="perBase"/> defence structures.</summary>
    private static bool TryGetUndefendedBase(FactionHQ hq, int perBase, out GlobalPosition center)
    {
        center = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!CommanderGameAccess.IsLandBase(airbase) || CommanderCaptureService.IsBeingCaptured(airbase))
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            if (CountBuildingsNear(hq, BuildingType.DEF, candidate, DefenceCoverageMeters) < perBase)
            {
                center = candidate;
                return true;
            }
        }

        return false;
    }

    private static int CountBuildings(FactionHQ hq, BuildingType type)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && unit.definition is BuildingDefinition definition
                && definition.buildingType == type)
            {
                count++;
            }
        }

        return count + CountBlueprintsNear(hq, type, default, float.MaxValue);
    }

    /// <summary>Terrain at that spot is below the sea plane. A probe that hits no terrain at all
    /// keeps y = -1, i.e. counts as open sea past the edge of the map's land.</summary>
    private static bool IsWater(GlobalPosition anchor, Vector3 direction, float distance)
    {
        GlobalPosition probe = At(anchor, direction, distance);
        return CommanderGameAccess.IsBelowSeaLevel(
            CommanderGameAccess.SnapToTerrain(new GlobalPosition(probe.x, -1f, probe.z)));
    }

    /// <summary>Water on every probe for <see cref="OpenWaterMeters"/> further out along the bearing.</summary>
    private static bool IsOpenWater(GlobalPosition anchor, Vector3 direction, float distance)
    {
        for (float ahead = ShoreMarchStepMeters; ahead <= OpenWaterMeters; ahead += ShoreMarchStepMeters)
        {
            if (!IsWater(anchor, direction, distance + ahead))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Floods out over the water from <paramref name="start"/> (a wet probe) on a coarse grid and
    /// says whether it gets <see cref="SeaReachMeters"/> away: a pond or lake runs out of water
    /// first, the sea does not. The water may bend round a headland, which a straight probe misses.
    /// Only runs when a dock site is searched for, which is rare, so the allocation is fine.
    /// </summary>
    private static bool ReachesSea(GlobalPosition start)
    {
        int radius = Mathf.CeilToInt(SeaReachMeters / SeaFloodStepMeters);
        HashSet<(int X, int Z)> seen = new() { (0, 0) };
        Queue<(int X, int Z)> open = new();
        open.Enqueue((0, 0));
        while (open.Count > 0)
        {
            (int x, int z) = open.Dequeue();
            if ((x * x) + (z * z) >= radius * radius)
            {
                return true;
            }

            for (int n = 0; n < 4; n++)
            {
                (int X, int Z) next = n switch
                {
                    0 => (x + 1, z),
                    1 => (x - 1, z),
                    2 => (x, z + 1),
                    _ => (x, z - 1),
                };
                if (seen.Add(next)
                    && IsWater(new GlobalPosition(start.x + (next.X * SeaFloodStepMeters), start.y, start.z + (next.Z * SeaFloodStepMeters)), Vector3.zero, 0f))
                {
                    open.Enqueue(next);
                }
            }
        }

        return false;
    }

    private static GlobalPosition At(GlobalPosition anchor, Vector3 direction, float distance)
    {
        return new GlobalPosition(anchor.x + (direction.x * distance), anchor.y, anchor.z + (direction.z * distance));
    }

    private static bool TryPickEnemyBuildSite(FactionHQ hq, ref GlobalPosition site)
    {
        if (hq.factionUnits == null)
        {
            return false;
        }

        Unit? anchor = null;
        int seen = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled || unit is not Building)
            {
                continue;
            }

            // Reservoir sample so builds spread across the faction's buildings instead of always
            // stacking on whichever one happens to be first in the list.
            seen++;
            if (anchor == null || Random.Range(0, seen) == 0)
            {
                anchor = unit;
            }
        }

        if (anchor == null)
        {
            return false;
        }

        float angle = Random.Range(0f, Mathf.PI * 2f);
        float distance = Random.Range(150f, 350f);
        GlobalPosition anchorPosition = anchor.transform.GlobalPosition();
        site = new GlobalPosition(
            anchorPosition.x + (Mathf.Cos(angle) * distance),
            anchorPosition.y,
            anchorPosition.z + (Mathf.Sin(angle) * distance));
        return true;
    }

    /// <summary>
    /// A gold mine this commander had is gone (bombed, or its base lost) and it is still under its
    /// target: rebuilding it goes ahead of every other rung. Blueprints count, so once the
    /// replacement is queued the ladder moves on.
    /// </summary>
    private bool IsRebuildingIncome(FactionHQ hq)
    {
        int mines = CountMines(hq, CommanderBuildKind.Mine);
        int peak = EnemyMinePeak.TryGetValue(hq, out int seen) ? Mathf.Max(seen, mines) : mines;
        EnemyMinePeak[hq] = peak;
        return mines < peak && mines < GetEnemyMineTarget(hq);
    }

    private float GetEnemyIncomeReserve(FactionHQ hq, CommanderBuildKind kind)
    {
        return kind == CommanderBuildKind.Mine && HasFreeMine(hq) ? FreeMineReserve : GetBuildCost(kind);
    }

    /// <summary>This commander's next gold mine costs nothing. Blueprints count, or a commander with
    /// two builders would queue every mine free before the first one stood.</summary>
    private bool HasFreeMine(FactionHQ hq)
    {
        return CommanderEnemyCommanderService.IsChampionsMission
            && HoldsMostBases(hq)
            && CountFreeMines(hq) < (CommanderEnemyCommanderService.IsChallenging(hq) ? ChampionsChallengingFreeMines : ChampionsFreeMines);
    }

    /// <summary>Income buildings it paid for. A free gold mine is a gift on top.</summary>
    private int CountHomeIncome(FactionHQ hq)
    {
        return Mathf.Max(0, CountMines(hq) - CountFreeMines(hq));
    }

    private int CountFreeMines(FactionHQ hq)
    {
        int taken = EnemyFreeMinesBuilt.TryGetValue(hq, out int built) ? built : 0;
        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite blueprint = constructionSites[i];
            if (blueprint.Kind == CommanderBuildKind.Mine && blueprint.Cost <= 0f && ReferenceEquals(blueprint.Hq, hq))
            {
                taken++;
            }
        }

        return taken;
    }

    private void ReviewEnemies()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (!CommanderEnemyCommanderService.IsAiFaction(hq, localHq))
            {
                continue;
            }

            ReviewEnemy(hq);
        }
    }

    /// <summary>One economy purchase per review: a new mine while short of the target, else an upgrade.</summary>
    private void ReviewEnemy(FactionHQ hq)
    {
        // Keeping what it owns comes before buying more of it: a bombed refinery that stays bombed
        // costs the enemy commander its income, which is the same trade the player is making.
        if (hq.factionFunds >= CommanderRepairService.CrewCost * EnemyReserveMultiple
            && CommanderRepairService.Instance?.TrySendEnemyRepairCrew(hq) == true)
        {
            return;
        }

        // Out of turn once the money is plainly there, like the income upgrades: it needs no builder, and
        // as the ladder's last rung it was never reached.
        float dockUpgrade = GetEnemyDockCost(hq, MaxLevel);
        if (GetNavalDockLevel(hq) > 0
            && dockUpgrade > 0f
            && hq.factionFunds >= dockUpgrade * EnemyReserveMultiple
            && TryUpgradeEnemyNavalDock(hq))
        {
            return;
        }

        // The price, not a multiple of it. GetEnemyBuildReserve holds this much back from the unit
        // spender, so the balance is allowed to climb to it — see that method for why the multiple
        // meant the commander only ever built mines.
        // As many as it has builders for, not one a review: the limit is a builder each.
        bool built = TryBuildFundedFactory(hq);
        for (int i = GetEnemyBlueprintLimit(hq); i > 0; i--)
        {
            float wanted = GetEnemyBuildReserve(hq, out _, out _);
            if (wanted <= 0f || hq.factionFunds < wanted || !TryBuildEnemyEconomy(hq))
            {
                break;
            }

            built = true;
        }

        if (built)
        {
            return;
        }

        // Blueprints waiting on builders block the ladder, not an upgrade, which needs none.
        if (TryUpgradeEnemyIncome(hq, EnemyReserveMultiple))
        {
            return;
        }

        TryUpgradeEnemyFactory(hq);
    }

    private void TryUpgradeEnemyFactory(FactionHQ hq)
    {
        Factory[] factories = Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            Unit? attached = factory == null ? null : factory.attachedUnit;
            if (factory == null
                || attached == null
                || attached.disabled
                || attached.NetworkHQ != hq
                || factory.ProductionUnit == null)
            {
                continue;
            }

            int level = GetFactoryLevel(factory);
            float cost = GetFactoryUpgradeCost(factory, level);
            if (level >= GetFactoryMaxLevel(factory)
                || hq.factionFunds < cost * EnemyReserveMultiple
                || !CommanderGameAccess.TrySpend(hq, cost))
            {
                continue;
            }

            factoryLevels[attached] = level + 1;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) upgraded {CommanderGameAccess.GetUnitLabel(attached)} "
                    + $"to level {level + 1}.");
            return;
        }
    }

    /// <summary>Builds whatever <see cref="GetEnemyBuildReserve"/> said this commander wants next.</summary>
    private bool TryBuildEnemyEconomy(FactionHQ hq)
    {
        // Ahead of the blueprint gate, as in GetEnemyBuildReserve: an upgrade needs no builders.
        if (HasDepotUpgradeIncome(hq) && TryUpgradeEnemyDepot(hq))
        {
            return true;
        }

        // One blueprint per builder at home. The ladder below counts blueprints as built, so it moves on to
        // the next rung (or the next base) instead of queueing the same one again.
        if (CountBlueprints(hq) >= GetEnemyBlueprintLimit(hq))
        {
            return false;
        }

        if (IsRebuildingIncome(hq) && TryBuildEnemyMine(hq))
        {
            return true;
        }

        // The first income building before everything else, as in GetEnemyBuildReserve.
        if (CountMines(hq) == 0 && TryBuildEnemyMine(hq))
        {
            return true;
        }

        // Same order as GetEnemyBuildReserve, because that method is what saved up for this one.
        if (CountBuildings(hq, BuildingType.RDR) == 0 && TryBuildEnemyRadar(hq))
        {
            return true;
        }

        if (TryBuildEnemySupply(hq, EnemySupplyBuildingTarget))
        {
            return true;
        }

        if (CountBuildings(hq, BuildingType.DEF) == 0 && TryBuildEnemyDefence(hq, 1))
        {
            return true;
        }

        if (TryUpgradeEnemyIncome(hq, 1f))
        {
            return true;
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;

        // Home before the frontier, as in GetEnemyBuildReserve: the dock, the mines, its upgrade.
        if (!IsGodOpening(hq) && TryBuildEnemyDock(hq, 1))
        {
            return true;
        }

        if (CountHomeIncome(hq) < GetUpgradeMines(hq))
        {
            return TryBuildEnemyMine(hq);
        }

        if (CountMines(hq, CommanderBuildKind.Mine) == 0 && IsEnemyMineOpen(hq))
        {
            return TryBuildEnemyMine(hq);
        }

        if (CountFactories(hq) < GetEnemyFactoryTarget(hq, duel) && GetEnemyFactoryReserve(hq) > 0f)
        {
            return TryBuildEnemyFactory(hq);
        }

        if (TryBuildEnemyDock(hq, EnemyEarlyDockLevel))
        {
            return true;
        }

        if (TryBuildEnemyEnrichment(hq))
        {
            return true;
        }

        if (TryBuildEnemyDepot(hq))
        {
            return true;
        }

        if (IsBelowEnemyIncomeTarget(hq))
        {
            return TryBuildEnemyMine(hq);
        }

        if (CommanderEnemyCommanderService.IsChallenging(hq)
            && GetDepotSiteFailures(hq) < EnemyDepotSiteGiveUp
            && TryBuildEnemyDepot(hq, CommanderEnemyCommanderService.ChallengingDepotsPerBase))
        {
            return true;
        }

        if (TryBuildEnemyRadar(hq))
        {
            return true;
        }

        if (TryBuildEnemyDefence(hq, 1))
        {
            return true;
        }

        if (TryBuildEnemyDefence(hq, EnemyDefenceBuildingsPerBase))
        {
            return true;
        }

        if (TryBuildEnemyMine(hq))
        {
            return true;
        }

        if (GetEnemyFactoryReserve(hq) > 0f)
        {
            return TryBuildEnemyFactory(hq);
        }

        return TryBuildEnemyDock(hq, MaxLevel);
    }

    /// <summary>
    /// One radar building per base, rebuilt when the last one is bombed. Sited beside the base it
    /// covers rather than wherever a dart lands: the thing being answered is "this base has no
    /// radar", and a second mast next to the first one answers nothing.
    /// </summary>
    private bool TryBuildEnemyRadar(FactionHQ hq)
    {
        BuildingDefinition? radar = ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar == null
            || !TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out GlobalPosition anchor)
            || !TryFindSiteNear(hq, radar, anchor, out GlobalPosition site)
            || !QueueEnemyBlueprint(
                hq, CommanderBuildKind.Structure, radar, null, site, GetStructureCost(radar), GetStructureLabel(radar)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// One vehicle depot per base, so every base the commander holds can deploy what it buys.
    /// Sited beside a road with its door facing it, the same rule the player's Champions kit uses,
    /// because a depot is the one building whose exit has to lead somewhere; a dart near the base
    /// is the fallback for a base with no road.
    /// </summary>
    private bool TryBuildEnemyDepot(FactionHQ hq, int perBase = 1)
    {
        BuildingDefinition? depot = ResolveCategoryDefinition(BuildingType.DEP, preferDearest: false);
        if (depot == null
            || !TryGetUncoveredBase(hq, BuildingType.DEP, DepotCoverageMeters, out GlobalPosition anchor, perBase))
        {
            return false;
        }

        _ = Quaternion.identity;
        if ((!preview.TryFindRoadsideSite(
            depot,
            hq,
            anchor,
            BaseSiteMaxMeters,
            SiteDice,
            EnemyDepotSiteAttempts,
            out GlobalPosition site,
            out Quaternion facing)
                && !TryFindSiteNear(hq, depot, anchor, out site))
            || !QueueEnemyBlueprint(
                hq, CommanderBuildKind.Structure, depot, null, site, GetStructureCost(depot), GetStructureLabel(depot), facing))
        {
            NoteDepotSiteFailure(hq);
            return false;
        }

        depotSiteFailures.Remove(hq);
        return true;
    }

    private int GetDepotSiteFailures(FactionHQ hq)
    {
        return depotSiteFailures.TryGetValue(hq, out int failures) ? failures : 0;
    }

    private void NoteDepotSiteFailure(FactionHQ hq)
    {
        int failures = GetDepotSiteFailures(hq) + 1;
        depotSiteFailures[hq] = failures;
        if (failures == EnemyDepotSiteGiveUp)
        {
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) found nowhere to put a depot at its next base "
                    + "and has stopped saving for one.");
        }
    }

    /// <summary>
    /// Puts a munitions bunker beside a base that has none. Same shape as the radar rule and for the
    /// same reason: the question is "can this base arm what it launches", and a second bunker next
    /// to the first does not answer it. <paramref name="target"/> is <see cref="EnemySupplyBuildingTarget"/>,
    /// so it only ever runs for a commander that owns no bunker at all.
    /// </summary>
    private bool TryBuildEnemySupply(FactionHQ hq, int target)
    {
        BuildingDefinition? supply = ResolveCategoryDefinition(BuildingType.AMMO, preferDearest: true);
        if (supply == null || CountBuildings(hq, BuildingType.AMMO) >= target)
        {
            return false;
        }

        // Finished through PlaceStructure, which empties a new store like the player's.
        return TryGetUncoveredBase(hq, BuildingType.AMMO, SupplyCoverageMeters, out GlobalPosition anchor)
            && TryFindSiteNear(hq, supply, anchor, out GlobalPosition site)
            && QueueEnemyBlueprint(
                hq, CommanderBuildKind.Structure, supply, null, site, GetStructureCost(supply), GetStructureLabel(supply));
    }

    private BuildingDefinition? FindDumpDefinition()
    {
        IReadOnlyList<BuildingDefinition> entries = Catalog;
        for (int i = 0; i < entries.Count; i++)
        {
            if (CommanderBuildPreview.IsDumpDefinition(entries[i]))
            {
                return entries[i];
            }
        }

        return null;
    }

    /// <summary>Puts an air-defence structure beside a base that has fewer than <paramref name="perBase"/>.</summary>
    private bool TryBuildEnemyDefence(FactionHQ hq, int perBase)
    {
        BuildingDefinition? defence = ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence == null
            || !TryGetUndefendedBase(hq, perBase, out GlobalPosition anchor)
            || !TryFindSiteNear(hq, defence, anchor, out GlobalPosition site)
            || !QueueEnemyBlueprint(
                hq, CommanderBuildKind.Structure, defence, null, site, GetStructureCost(defence), GetStructureLabel(defence)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The structure a commander uses for a whole building category, resolved off the live
    /// encyclopedia rather than a name table: <c>BuildingType</c> is authored in the game's own
    /// assets, so a patch that adds a radar mast files itself and a patch that removes one degrades
    /// to "no radar" instead of to a null prefab. Logged once, because a choice a game patch can
    /// invalidate should not be a silent one.
    /// </summary>
    private BuildingDefinition? ResolveCategoryDefinition(BuildingType type, bool preferDearest)
    {
        if (categoryDefinitions.TryGetValue(type, out BuildingDefinition? cached))
        {
            return cached;
        }

        IReadOnlyList<BuildingDefinition> entries = Catalog;
        if (entries.Count == 0)
        {
            // The encyclopedia is not up yet. Ask again next review rather than caching a null.
            return null;
        }

        BuildingDefinition? best = null;
        for (int i = 0; i < entries.Count; i++)
        {
            BuildingDefinition candidate = entries[i];
            if (candidate.buildingType != type)
            {
                continue;
            }

            // DEFENCE is picked for anti-air (the game's own roleIdentity), cheapest on a tie. It was
            // the cheapest entry, a 12.7 mm machine-gun post, which does nothing to a jet.
            bool better = best == null
                || (type == BuildingType.DEF
                    ? candidate.roleIdentity.antiAir > best.roleIdentity.antiAir
                        || (candidate.roleIdentity.antiAir == best.roleIdentity.antiAir && candidate.value < best.value)
                    : preferDearest ? candidate.value > best.value : candidate.value < best.value);
            if (better)
            {
                best = candidate;
            }
        }

        categoryDefinitions[type] = best;
        CommanderPlugin.Log.LogInfo(best == null
            ? $"No {GetCategoryLabel(type)} structure in the encyclopedia, so no commander will build one."
            : $"Commanders will build {GetStructureLabel(best)} for {GetCategoryLabel(type)} "
                + $"({GetStructureCost(best):0}).");
        return best;
    }

    /// <summary>A legal site on a ring around one point, through the same shared rule as every
    /// other build in the mod.</summary>
    private bool TryFindSiteNear(
        FactionHQ hq,
        BuildingDefinition definition,
        GlobalPosition anchor,
        out GlobalPosition site,
        int attempts = EnemySiteAttempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(BaseSiteMinMeters, BaseSiteMaxMeters);
            GlobalPosition candidate = new(
                anchor.x + (Mathf.Cos(angle) * distance),
                anchor.y,
                anchor.z + (Mathf.Sin(angle) * distance));
            if (preview.IsSiteAllowed(definition, candidate, hq, out _))
            {
                site = candidate;
                return true;
            }
        }

        site = default;
        return false;
    }

    /// <summary>
    /// Drops an enemy mine on a ring round one of its bases, never within a gap of another one.
    /// The generic dart lands a build 150-350 m off a building the faction already owns, which on
    /// the duel is the pair of starting depots, so every mine ended up in the same few hundred
    /// metres beside the home base: a player who knew that flew one air-to-ground sortie there and
    /// ended the match. Income is the one thing worth a sortie, so each mine now costs its own trip
    /// to find and kill. The old dart stays as the fallback, because a commander that silently
    /// stops building its income is worse than a predictable one.
    /// </summary>
    private bool TryBuildEnemyMine(FactionHQ hq)
    {
        // One at a time: an upgrade pays back in 4-5 minutes and a new building in over 6, so holding here
        // until the blueprint stands builds one, maxes it, then builds the next. A free mine is rushed.
        // The god has the money for every builder to put one up at once, and so does a commander
        // holding several gold mines' worth.
        if (!HasFreeMine(hq)
            && !IsGodEconomy(hq)
            && hq.factionFunds < GetIncomeCost(CommanderBuildKind.Mine, 0) * EnemyReserveMultiple
            && CountBlueprints(hq, CommanderBuildKind.FuelExchange)
                + CountBlueprints(hq, CommanderBuildKind.GasTerminal)
                + CountBlueprints(hq, CommanderBuildKind.Mine) > 0)
        {
            return false;
        }

        CommanderBuildKind kind = ChooseEnemyIncomeKind(hq);

        // A gold mine is a huge target and most of the income, so it only goes up at a defended
        // base: the AA post comes first. If no post can be sited there, the mine goes up anyway,
        // because a commander stuck on an unsitable post would never build income again.
        if (kind == CommanderBuildKind.Mine
            && NeedsMineDefence(hq, out BuildingDefinition? guard, out GlobalPosition guardBase)
            && TryFindSiteNear(hq, guard!, guardBase, out GlobalPosition guardSite)
            && QueueEnemyBlueprint(
                hq, CommanderBuildKind.Structure, guard!, null, guardSite, GetStructureCost(guard!), GetStructureLabel(guard!)))
        {
            return true;
        }

        BuildingDefinition? mine = ResolveDefinition(kind);
        if (!TryFindSpreadMineSite(hq, kind, mine, out GlobalPosition site) && !TryFindEnemyBuildSite(hq, mine, ref site))
        {
            return false;
        }

        float cost = kind == CommanderBuildKind.Mine && HasFreeMine(hq) ? 0f : GetBuildCost(kind);
        return QueueEnemyBlueprint(hq, kind, mine, null, site, cost, GetIncomeName(kind));
    }

    /// <summary>The base the next gold mine goes to has no air defence structure yet.</summary>
    private bool NeedsMineDefence(FactionHQ hq, out BuildingDefinition? defence, out GlobalPosition anchor)
    {
        anchor = default;
        defence = ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        return defence != null
            && TryGetSparsestMineBase(hq, CommanderBuildKind.Mine, out anchor)
            && CountBuildingsNear(hq, BuildingType.DEF, anchor, DefenceCoverageMeters) == 0;
    }

    /// <summary>
    /// A legal income site on the ring round the chosen base, kept clear of the income buildings
    /// already there. Gold mines take the outer ring, starters the inner one. Sizes are shares of
    /// <c>CommanderSettings.BuildRadiusKm</c>, which is what the site rule itself measures against,
    /// so lowering that setting shrinks the ring instead of refusing every site in it.
    /// </summary>
    private bool TryFindSpreadMineSite(FactionHQ hq, CommanderBuildKind kind, BuildingDefinition? definition, out GlobalPosition site)
    {
        site = default;
        if (definition == null || !TryGetSparsestMineBase(hq, kind, out GlobalPosition anchor))
        {
            return false;
        }

        bool goldMine = kind == CommanderBuildKind.Mine;
        float radius = Mathf.Max(CommanderSettings.BuildRadiusKm, 0.1f) * 1000f;

        // Half the darts beside the depot, where the builders stand. Gold mines stay on their ring, so
        // one sortie cannot kill the economy.
        if (!goldMine && hq.TryGetNearestDepot(anchor.ToLocalPosition(), radius, out VehicleDepot depot))
        {
            GlobalPosition door = depot.transform.GlobalPosition();
            for (int attempt = 0; attempt < EnemyMineSiteAttempts / 2; attempt++)
            {
                float bearing = Random.Range(0f, Mathf.PI * 2f);
                float reach = Random.Range(StarterDepotMinMeters, StarterDepotMaxMeters);
                GlobalPosition candidate = new(door.x + (Mathf.Cos(bearing) * reach), door.y, door.z + (Mathf.Sin(bearing) * reach));
                if (CountMinesNear(hq, candidate, StarterDepotSpacingMeters) == 0
                    && preview.IsSiteAllowed(definition, candidate, hq, out _))
                {
                    site = candidate;
                    return true;
                }
            }
        }

        float spacing = radius * MineSpacingShare * (goldMine ? 1f : 0.5f);
        float inner = radius * (goldMine ? MineRingMinShare : StarterRingMinShare);
        float outer = radius * (goldMine ? MineRingMaxShare : MineRingMinShare);
        for (int attempt = 0; attempt < EnemyMineSiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(inner, outer);
            GlobalPosition candidate = new(
                anchor.x + (Mathf.Cos(angle) * distance),
                anchor.y,
                anchor.z + (Mathf.Sin(angle) * distance));
            if (CountMinesNear(hq, candidate, spacing) == 0
                && preview.IsSiteAllowed(definition, candidate, hq, out _))
            {
                site = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The base this faction holds with the fewest of these round it, so a commander that
    /// takes ground puts its next mine on the new base rather than a fifth one at home. Gold mines
    /// count only gold mines, and a tie goes to the base with more air defence built.</summary>
    private bool TryGetSparsestMineBase(FactionHQ hq, CommanderBuildKind kind, out GlobalPosition center)
    {
        center = default;
        bool goldMine = kind == CommanderBuildKind.Mine;
        float radius = Mathf.Max(CommanderSettings.BuildRadiusKm, 0.1f) * 1000f;
        int fewest = int.MaxValue;
        int bestDefence = -1;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        bool rear = goldMine && CommanderEnemyCommanderService.IsChampionsMission && localHq != null && localHq != hq;
        GlobalPosition playerCenter = rear ? CommanderCaptureService.GetTerritoryCenter(localHq!) : default;
        GlobalPosition rearCenter = default;
        float deepest = -1f;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!CommanderGameAccess.IsLandBase(airbase) || CommanderCaptureService.IsBeingCaptured(airbase))
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            int mines = CountMinesNear(hq, candidate, radius, goldMine ? CommanderBuildKind.Mine : null);

            // Champions: gold mines go to the back line (the base furthest from the player with room).
            if (rear && mines < ChampionsMinesPerRearBase)
            {
                float depth = FastMath.Distance(candidate, playerCenter);
                if (depth > deepest)
                {
                    deepest = depth;
                    rearCenter = candidate;
                }
            }

            if (mines > fewest)
            {
                continue;
            }

            int defence = goldMine ? CountBuildingsNear(hq, BuildingType.DEF, candidate, DefenceCoverageMeters) : 0;
            if (mines < fewest || defence > bestDefence)
            {
                fewest = mines;
                bestDefence = defence;
                center = candidate;
            }
        }

        if (deepest >= 0f)
        {
            center = rearCenter;
            return true;
        }

        return fewest < int.MaxValue;
    }

    /// <summary>This faction's mines within <paramref name="radius"/>, blueprints included, or one
    /// review queues a second mine on the spot the last one is still going up on. Same set
    /// <see cref="CountMines"/> counts, so the ladder and the spacing agree on what exists.</summary>
    private int CountMinesNear(FactionHQ hq, GlobalPosition position, float radius, CommanderBuildKind? kind = null)
    {
        int count = 0;
        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && entry.Key.NetworkHQ == hq
                && (kind == null || GetIncomeKind(entry.Key) == kind)
                && FastMath.InRange(entry.Key.transform.GlobalPosition(), position, radius))
            {
                count++;
            }
        }

        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite blueprint = constructionSites[i];
            if (IsIncomeKind(blueprint.Kind)
                && (kind == null || blueprint.Kind == kind)
                && ReferenceEquals(blueprint.Hq, hq)
                && FastMath.InRange(blueprint.Site, position, radius))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Puts the enemy's dock on the coast nearest a base it holds. The search starts from the map's
    /// own sea lanes rather than throwing darts at the ground: a sea lane point is water by
    /// definition, so walking inland from one until the terrain climbs above the sea plane finds the
    /// shoreline in a handful of probes. Darts would have to sample the whole build radius to find
    /// the coast at all, and most maps are mostly land.
    /// </summary>
    private bool HasGivenUpOnDock(FactionHQ hq)
    {
        return shoreSearchFailed.TryGetValue(hq, out int bases) && bases == CommanderVictoryService.CountBases(hq);
    }

    /// <summary>
    /// What the dock rung wants next: the dock itself, then each level up to
    /// <paramref name="upToLevel"/>, else 0. A commander whose map gave it no coast hands the money
    /// back rather than reserving for a dock it can never site, the same reason AccrueFund caps the
    /// air and naval funds.
    /// </summary>
    private float GetEnemyDockCost(FactionHQ hq, int upToLevel)
    {
        // No navy before the first gold mine and the income of a fully upgraded Gas Export Terminal,
        // or the dock starves the economy.
        if (GetIncomePerMinute(hq) < GetIncomePerMinute(CommanderBuildKind.GasTerminal, MaxLevel)
            || CountMines(hq, CommanderBuildKind.Mine) == 0)
        {
            return 0f;
        }

        int level = GetNavalDockLevel(hq);
        if (level <= 0)
        {
            return !HasGivenUpOnDock(hq) && CountBlueprints(hq, CommanderBuildKind.NavalDock) == 0
                ? NavalDockBuildCost
                : 0f;
        }

        return level < upToLevel ? GetNavalDockUpgradeCost(level) : 0f;
    }

    /// <summary>Builds or upgrades the dock, whichever <see cref="GetEnemyDockCost"/> asked for.</summary>
    private bool TryBuildEnemyDock(FactionHQ hq, int upToLevel)
    {
        if (GetEnemyDockCost(hq, upToLevel) <= 0f)
        {
            return false;
        }

        return GetNavalDockLevel(hq) <= 0 ? TryBuildEnemyNavalDock(hq) : TryUpgradeEnemyNavalDock(hq);
    }

    private bool TryBuildEnemyNavalDock(FactionHQ hq)
    {
        if (HasGivenUpOnDock(hq))
        {
            return false;
        }

        BuildingDefinition? dock = ResolveDefinition(CommanderBuildKind.NavalDock);
        if (dock == null || !TryFindShoreSite(hq, dock, out GlobalPosition site))
        {
            // Say it once. An enemy with no navy on a map whose coast it cannot reach looks exactly
            // like an enemy that forgot to build one, and only the console can tell them apart.
            if (dock != null)
            {
                shoreSearchFailed[hq] = CommanderVictoryService.CountBases(hq);
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) found no shoreline within "
                        + $"{CommanderSettings.NavalDockRadiusKm:0.#} km of a base it holds, so it has no navy. "
                        + "Raise Economy/NavalDockRadiusKm if this map keeps its coast further out.");
            }

            return false;
        }

        return QueueEnemyBlueprint(hq, CommanderBuildKind.NavalDock, dock, null, site, NavalDockBuildCost, NavalDockDisplayName);
    }

    private bool TryUpgradeEnemyNavalDock(FactionHQ hq)
    {
        foreach (KeyValuePair<Unit, int> entry in dockLevels)
        {
            if (entry.Value >= MaxLevel
                || entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq)
            {
                continue;
            }

            // The plain price: GetEnemyBuildReserve saved exactly this much for it.
            float cost = GetNavalDockUpgradeCost(entry.Value);
            if (!CommanderGameAccess.TrySpend(hq, cost))
            {
                continue;
            }

            dockLevels[entry.Key] = entry.Value + 1;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) upgraded its naval dock to level {entry.Value + 1}: "
                    + $"{CommanderNavalPurchaseService.GetLevelUnlockLabel(entry.Value + 1)}.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Marches out from every held base along <see cref="ShoreBearings"/> bearings until the ground
    /// drops below the sea plane, finds the water's edge between the last dry and first wet probe, then
    /// tries a few spots stepping back inland. Starting from sea lanes fails since they run far offshore.
    /// </summary>
    private bool TryFindShoreSite(FactionHQ hq, BuildingDefinition dock, out GlobalPosition site)
    {
        site = default;
        float reach = Mathf.Max(CommanderSettings.NavalDockRadiusKm, 0.1f) * 1000f;

        // The nearest coast over every bearing and base, not the first one found: the builder has to
        // get there, and the first bearing that reached water put the duel's dock 12 km out.
        float nearest = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!CommanderGameAccess.IsLandBase(airbase))
            {
                continue;
            }

            GlobalPosition anchor = airbase.center.GlobalPosition();
            for (int b = 0; b < ShoreBearings; b++)
            {
                float angle = b * Mathf.PI * 2f / ShoreBearings;
                Vector3 direction = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                float dry = 0f;
                for (float distance = ShoreMarchStepMeters; distance <= reach && distance - ShoreMarchStepMeters < nearest; distance += ShoreMarchStepMeters)
                {
                    if (!IsWater(anchor, direction, distance))
                    {
                        dry = distance;
                        continue;
                    }

                    // Any ground below sea level reads as water, so an inland pond passed and
                    // Primeva built its dock in one. The sea goes on; a pond is crossed and the
                    // march carries on toward the real coast.
                    if (!IsOpenWater(anchor, direction, distance)
                        || !ReachesSea(At(anchor, direction, distance)))
                    {
                        continue;
                    }

                    // Bisect the step down to a few metres, so the candidates start at the edge
                    // rather than anywhere inside a 250 m gap.
                    float wet = distance;
                    for (int i = 0; i < ShoreEdgeBisections; i++)
                    {
                        float middle = (dry + wet) * 0.5f;
                        if (IsWater(anchor, direction, middle))
                        {
                            wet = middle;
                        }
                        else
                        {
                            dry = middle;
                        }
                    }

                    for (int back = 0; back < ShoreSiteTries; back++)
                    {
                        float from = Mathf.Max(dry - (back * ShoreSiteBackoffMeters), 0f);
                        GlobalPosition candidate = At(anchor, direction, from);
                        if (from < nearest && preview.IsSiteAllowed(dock, candidate, hq, out _, navalDock: true))
                        {
                            site = candidate;
                            nearest = from;
                            break;
                        }
                    }

                    // The first coast on this bearing is taken; an island further out is not ours.
                    break;
                }
            }
        }

        return nearest < float.MaxValue;
    }

    /// <summary>
    /// A spot beside something that faction already owns, so a build lands in its own rear. Each
    /// candidate is put through the same <see cref="CommanderBuildPreview.IsSiteAllowed"/> the
    /// player's ghost uses, so the enemy cannot drop a factory across the highway or inside
    /// another building — which is what left its own convoys stuck and its aircraft taxiing into
    /// walls. Several tries, because one throw of the dart lands on a road often enough.
    /// </summary>
    private bool TryFindEnemyBuildSite(FactionHQ hq, BuildingDefinition? definition, ref GlobalPosition site)
    {
        for (int attempt = 0; attempt < EnemySiteAttempts; attempt++)
        {
            if (!TryPickEnemyBuildSite(hq, ref site))
            {
                return false;
            }

            if (definition == null || preview.IsSiteAllowed(definition, site, hq, out _))
            {
                return true;
            }
        }

        return false;
    }
}
