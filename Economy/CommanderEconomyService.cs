using System.Collections.Generic;
using System.Globalization;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>What the next placement click builds.</summary>
internal enum CommanderBuildKind
{
    None,
    Mine,
    Factory,

    /// <summary>Any other building out of the encyclopedia catalogue.</summary>
    Structure,

    /// <summary>The shoreline dock that unlocks naval purchases, three levels of them.</summary>
    NavalDock,

    /// <summary>The cheapest income building, on the aviation fuel depot prefab.</summary>
    FuelExchange,

    /// <summary>The middle income building, on the in-ground storage tank prefab.</summary>
    GasTerminal,
}

/// <summary>
/// The economy layer: income buildings (fuel exchange, gas terminal, gold mine) that pay their
/// owner a standing income, factories that feed the
/// unit reserve, and the upgrades that multiply what each of them produces.
/// <para>
/// Both sides play by these rules. The player spends through the BUILD window; hostile factions
/// spend through <see cref="ReviewEnemy"/>, gated on the same ENEMY COMMANDER setting as
/// <see cref="CommanderEnemyCommanderService"/>. The two AI spenders deliberately share one pot
/// and take separate slices of it — one buys units, this one buys the ability to buy more units.
/// </para>
/// </summary>
/// <remarks>
/// Levels are keyed on the live <see cref="Unit"/>, so they die with the building and do
/// not survive a mission reload. That is the intended stake — bomb the mine, take the income away.
/// Persisting them would mean writing our own state into the mission save.
/// </remarks>
internal sealed partial class CommanderEconomyService
    : ICommanderTickActive, ICommanderTickPersistent, ICommanderDeactivate, ICommanderResetSession
{
    internal const int MaxLevel = 3;

    /// <summary>
    /// In code, not config, like the income table, so old config values cannot override it.
    /// </summary>
    internal const float FactoryBuildCost = 250f;

    /// <summary>What a built mine calls itself, whatever industrial prefab it is wearing.</summary>
    internal const string MineDisplayName = "Gold Mine";

    /// <summary>What a built naval dock calls itself, whatever prefab it is wearing.</summary>
    internal const string NavalDockDisplayName = "Naval Dock";

    private const float IncomeIntervalSeconds = 15f;

    /// <summary>Income ticks between economy log lines: once a minute.</summary>
    private const float EnemyReviewIntervalSeconds = 30f;

    /// <summary>
    /// The economy review builds one thing. A Champions commander holding seven bases has a shelf,
    /// a depot, a mine, a radar and an air-defence post to put on each of them, which at half a
    /// minute apiece is most of a match before its far bases can field anything - so on that
    /// mission it reviews three times as often. Each build still has to be paid for.
    /// </summary>
    private const float ChampionsEnemyReviewIntervalSeconds = 10f;
    private const float FactoryRefreshIntervalSeconds = 5f;

    /// <summary>How long a destroyed commander-built building stands before it is cleared.</summary>
    private const float BuiltWreckSeconds = 60f;

    /// <summary>Mines an enemy commander builds before it starts upgrading what it has.</summary>
    private const int EnemyMineTarget = 2;

    /// <summary>The enemy's opening plants, before its second AA post; the rest of its factories
    /// (<see cref="EnemyGroundFactoriesPerBase"/> and the rest) come after it.</summary>
    private const int EnemyFactoryTarget = 2;

    /// <summary>
    /// The duel's targets. A commander capped at two mines and one factory falls behind a player
    /// who keeps building, so on the mod's own map it is allowed to keep pace. The mine count is
    /// rolled per commander (<see cref="RollEnemyMines"/>) so no two matches open the same way.
    /// </summary>
    private const int DuelEnemyFactoryTarget = 3;

    /// <summary>GOD DIFFICULTY's opening plants, all aircraft lines (see <c>TryPickEnemyFactory</c>).</summary>
    private const int GodEnemyFactoryTarget = 6;

    /// <summary>Longest GOD DIFFICULTY holds its convoys for the opening plants (see <c>IsGodOpening</c>).</summary>
    private const float GodOpeningMaxSeconds = 600f;

    /// <summary>
    /// Reserve multiple a commander keeps clear of an economy purchase. Faction balances are
    /// mission-authored and scale-free, so this is a ratio rather than an absolute floor.
    /// </summary>
    private const float EnemyReserveMultiple = 4f;

    /// <summary>How far a factory's price may fall below and rise above <see cref="FactoryBuildCost"/>
    /// for a cheap or a heavy product. Bounded so no roster price can make a plant free or absurd.</summary>
    /// <summary>Factories priced above this get the wide <c>factory_large</c> hall, the rest the vertical one.</summary>
    private const float LargeFactoryMinCost = 550f;
    private const float MinFactoryCostScale = 0.6f;
    private const float MaxFactoryCostScale = 2f;

    /// <summary>How many of its product a factory costs, at least: a dear line pays for itself in this many batches.</summary>
    private const float FactoryPaybackUnits = 3f;

    /// <summary>
    /// A ground plant costs this many of its product. At the 240 s cycle that is an 8 minute payback,
    /// level with a Fuel Exchange and a little behind a Gold Mine (6), since what a plant makes is
    /// units, not money it could spend elsewhere or put against upkeep.
    /// </summary>
    private const float GroundFactoryPaybackUnits = 2f;

    /// <summary>Least a ground plant costs, so a near-worthless product does not make one free.</summary>
    private const float MinGroundFactoryBuildCost = 20f;

    /// <summary>Buildings a built factory prefers to look like, best first.</summary>
    private static readonly string[] FactoryPrefabKeys =
    {
        "factory_large",
        "factory_tall",
    };

    /// <summary>
    /// The hall a light product is built in, best first. "Vertical Factory" (<c>factory_tall</c>) used
    /// to sit in the STRUCTURES catalogue, where it was a factory bought without choosing a product.
    /// It is listed nowhere now and is only ever seen as the plant behind the cheaper vehicles, which
    /// is what the request asked for: the model is reused rather than thrown away.
    /// </summary>
    private static readonly string[] LightFactoryPrefabKeys =
    {
        "factory_tall",
        "factory_large",
    };

    /// <summary>
    /// Buildings a naval dock prefers to look like, best first. The game ships no dock, so the
    /// harbour crane stands in for one — it is the only structure that reads as a quayside.
    /// </summary>
    private static readonly string[] VehicleDepotKeys = { "VehicleDepot1" };

    private static readonly string[] NavalDockPrefabKeys =
    {
        "HarborCrane",
        "TowerCrane",
        "Platform_large",
        "VehicleDepot1",
    };

    private readonly Dictionary<Unit, int> incomeLevels = new();
    private readonly Dictionary<Unit, int> factoryLevels = new();
    private readonly Dictionary<Unit, int> dockLevels = new();
    private readonly List<Factory> friendlyFactories = new();
    private readonly List<Unit> friendlyMines = new();
    private readonly List<Unit> friendlyDocks = new();
    private readonly List<Unit> staleUnits = new();
    private readonly List<UnitDefinition> productionOptions = new();
    private readonly HashSet<Unit> builtUnits = new();
    private readonly Dictionary<Unit, float> wreckedSince = new();

    private readonly CommanderBuildPreview preview = new();

    private BuildingDefinition? factoryDefinition;
    private bool factoryDefinitionResolved;
    private BuildingDefinition? lightFactoryDefinition;
    private bool lightFactoryDefinitionResolved;
    private BuildingDefinition? dockDefinition;
    private bool dockDefinitionResolved;
    private int productionIndex;
    private int builtNameCounter;
    private float nextIncomeAt;
    private float nextEnemyReviewAt;
    private float nextFactoryRefreshAt;

    internal CommanderEconomyService()
    {
        Instance = this;
        nextIncomeAt = CommanderScheduler.Stagger("economy.income", IncomeIntervalSeconds);
        nextEnemyReviewAt = 0f;
        RegisterRemote();
    }

    internal static CommanderEconomyService? Instance { get; private set; }

    internal static float NavalDockBuildCost => Mathf.Max(0f, CommanderSettings.NavalDockCost);

    /// <summary>Seconds between production cycles at a factory this mod built.</summary>
    internal static float FactoryProductionSeconds => Mathf.Max(30f, CommanderSettings.FactoryProductionSeconds);

    internal string StatusText { get; private set; } = string.Empty;

    internal CommanderBuildKind PendingBuild { get; private set; }

    internal bool AwaitingPlacement => PendingBuild != CommanderBuildKind.None;

    /// <summary>
    /// What the status line says while a placement is armed: the reason the site under the cursor
    /// is blocked, or the go-ahead. Falls back to <see cref="StatusText"/> when nothing is armed.
    /// </summary>
    internal string PlacementStatus
    {
        get
        {
            if (!AwaitingPlacement)
            {
                return StatusText;
            }

            return preview.SiteValid
                ? $"Click to site the {PendingBuildLabel()}."
                : preview.BlockedReason;
        }
    }

    /// <summary>Naval docks the local faction owns, for the BUILD window list.</summary>
    internal IReadOnlyList<Unit> NavalDocks => friendlyDocks;

    /// <summary>Friendly factories that actually produce a vehicle, refreshed while RTS mode is up.</summary>
    internal IReadOnlyList<Factory> Factories => friendlyFactories;

    /// <summary>Every income building the local faction owns, whichever of the three it is.</summary>
    internal IReadOnlyList<Unit> Mines => friendlyMines;

    /// <summary>What the armed placement will charge, catalogue prices included.</summary>
    internal float PendingBuildCost => PendingBuild switch
    {
        CommanderBuildKind.Structure => GetStructureCost(PendingStructure),
        CommanderBuildKind.Factory => GetFactoryBuildCost(SelectedProduction),
        _ => GetBuildCost(PendingBuild),
    };

    /// <summary>What a factory built now would produce; null when the faction offers nothing.</summary>
    internal UnitDefinition? SelectedProduction => productionOptions.Count == 0
                ? null
                : productionOptions[Mathf.Clamp(productionIndex, 0, productionOptions.Count - 1)];

    /// <summary>What a new factory may be told to build: the faction's ground vehicles, then every aircraft.</summary>
    internal IReadOnlyList<UnitDefinition> ProductionOptions => productionOptions;

    /// <summary>0..1 toward the next income payout, or -1 when the clock is not running here
    /// (a guest, whose host pays the income).</summary>
    internal float IncomeTickProgress
    {
        get
        {
            float remaining = nextIncomeAt - Time.time;
            return remaining < -1f || remaining > IncomeIntervalSeconds ? -1f : 1f - Mathf.Clamp01(remaining / IncomeIntervalSeconds);
        }
    }

    /// <summary>The building the armed placement would put down, whichever button armed it.</summary>
    private BuildingDefinition? PendingDefinition => PendingBuild switch
    {
        CommanderBuildKind.None => null,
        CommanderBuildKind.Structure => PendingStructure,
        _ => ResolveBuildDefinition(PendingBuild, SelectedProduction),
    };

    public void TickActive()
    {
        // Same escape hatch the supply and trailer placements have, so every armed placement in the
        // mod backs out the same way.
        if (AwaitingPlacement && CommanderGameInput.CancelDown)
        {
            CancelBuild();
        }

        // No ghost while the cursor is parked on a Commander window: the click cannot place there,
        // so a preview skidding around behind the BUILD list only reads as a bug.
        BuildingDefinition? pending = PendingDefinition;
        if (pending != null)
        {
            Vector2 cursor = Input.mousePosition;
            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(cursor) == true)
            {
                preview.Suspend();
            }
            else
            {
                preview.Tick(
                    pending, CommanderGameAccess.GetLocalHq(), cursor, PendingBuild == CommanderBuildKind.NavalDock);
            }
        }

        if (CommanderScheduler.IsDue(ref nextFactoryRefreshAt, FactoryRefreshIntervalSeconds))
        {
            RefreshFriendlyLists();
        }
    }

    public void TickPersistent()
    {
        // A dedicated server has no team of its own; the team with players stands in for it.
        CommanderNetwork.RunAsHostTeam(TickPersistentAsHost);
        CommanderDepotLane.FreeStuck(preview);
    }

    public void Deactivate()
    {
        PendingBuild = CommanderBuildKind.None;
        PendingStructure = null;
        preview.Hide();
    }

    public void ResetSession()
    {
        incomeLevels.Clear();
        incomeKinds.Clear();
        incomeDefinitions.Clear();
        loggedWarheadStores = false;
        depotLevels.Clear();
        pricesApplied = false;
        CommanderPriceProfile.ResetSession();
        CommanderForwardBase.ResetSession();
        depotSiteFailures.Clear();
        enrichmentGate.Clear();
        EnemyMineRolls.Clear();
        EnemyMinePeak.Clear();
        EnemyFreeMinesBuilt.Clear();
        factoryLevels.Clear();
        enemyFactoryOptions.Clear();
        dockLevels.Clear();
        friendlyFactories.Clear();
        friendlyMines.Clear();
        friendlyDocks.Clear();
        staleUnits.Clear();
        productionOptions.Clear();
        builtUnits.Clear();
        wreckedSince.Clear();
        constructionSites.Clear();
        ghostedBuilders.Clear();
        builderProgress.Clear();
        builderResting.Clear();
        queuedBuildOrders.Clear();
        championsDealt = false;
        startingBuildersGiven.Clear();
        championsBuildersDealt.Clear();
        warnedNoStartingBuilders = false;
        startingBuildersDepotSeenAt.Clear();
        championsBuilderBases.Clear();
        startingBuildersDeadline = -1f;
        preview.Clear();
        catalog.Clear();
        catalogResolved = false;
        PendingBuild = CommanderBuildKind.None;
        PendingStructure = null;
        productionIndex = 0;
        builtNameCounter = 0;
        factoryDefinition = null;
        factoryDefinitionResolved = false;
        dockDefinition = null;
        dockDefinitionResolved = false;
        shoreSearchFailed.Clear();
        StatusText = string.Empty;
        nextIncomeAt = CommanderScheduler.Stagger("economy.income", IncomeIntervalSeconds);
        nextEnemyReviewAt = 0f;
    }

    /// <summary>
    /// Anything either commander built. The marker layer reads this so a mine you put down can be
    /// clicked and boxed like a vehicle — without it, the Basegame rule that buildings are not
    /// selectable made your own economy invisible to selection.
    /// </summary>
    internal static bool IsCommanderBuilt(Unit? unit)
    {
        return unit != null && Instance != null && Instance.builtUnits.Contains(unit);
    }

    /// <summary>
    /// True within <see cref="CommanderBuildPreview.TowerBuildRadiusMeters"/> of a standing control
    /// tower <paramref name="hq"/> built. Towers anchor building like an airbase does, and can be
    /// chained outward on purpose: that is the forward base.
    /// </summary>
    internal static bool IsNearBuiltTower(FactionHQ hq, GlobalPosition target)
    {
        if (Instance == null)
        {
            return false;
        }

        float radiusSquared = CommanderBuildPreview.TowerBuildRadiusMeters * CommanderBuildPreview.TowerBuildRadiusMeters;
        foreach (Unit unit in Instance.builtUnits)
        {
            if (unit == null
                || unit.disabled
                || unit.NetworkHQ != hq
                || unit.definition is not BuildingDefinition built
                || !CommanderBuildPreview.IsControlTower(built))
            {
                continue;
            }

            Vector3 offset = unit.transform.GlobalPosition().AsVector3() - target.AsVector3();
            offset.y = 0f;
            if (offset.sqrMagnitude <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="position"/> is inside a forward base <paramref name="hq"/> built (a
    /// standing control tower) that also holds a built landing spot for that airframe: a helipad for
    /// a helicopter or tiltwing, a hangar for anything else. A landed ejection there is a finished
    /// sortie rather than a crash, the same as at an airbase.
    /// </summary>
    /// <remarks>
    /// The base game's question only carries a position, so the aircraft is the one sitting exactly
    /// there. Nothing is (a walking pilot): either kind of landing spot will do.
    /// </remarks>
    internal static bool IsAtForwardAirfield(FactionHQ hq, Vector3 position)
    {
        if (Instance == null)
        {
            return false;
        }

        bool? rotary = null;
        List<Aircraft> aircraft = UnitRegistry.allAircraft;
        for (int i = 0; i < aircraft.Count; i++)
        {
            if (aircraft[i] != null && (aircraft[i].transform.position - position).sqrMagnitude < 1f)
            {
                rotary = CommanderAirCommandService.HasRotaryPilot(aircraft[i]);
                break;
            }
        }

        float radiusSquared = CommanderBuildPreview.TowerBuildRadiusMeters * CommanderBuildPreview.TowerBuildRadiusMeters;
        foreach (Unit tower in Instance.builtUnits)
        {
            if (tower == null
                || tower.disabled
                || tower.NetworkHQ != hq
                || tower.definition is not BuildingDefinition towerDefinition
                || !CommanderBuildPreview.IsControlTower(towerDefinition)
                || FlatDistanceSquared(tower.transform.position, position) > radiusSquared)
            {
                continue;
            }

            foreach (Unit pad in Instance.builtUnits)
            {
                if (pad == null
                    || pad.disabled
                    || pad.NetworkHQ != hq
                    || FlatDistanceSquared(tower.transform.position, pad.transform.position) > radiusSquared)
                {
                    continue;
                }

                Hangar? hangar = pad.GetComponentInChildren<Hangar>(true);
                if (hangar != null && (rotary == null || rotary == CommanderGameAccess.IsRotaryHangar(hangar)))
                {
                    return true;
                }
            }
        }

        return false;

        static float FlatDistanceSquared(Vector3 a, Vector3 b)
        {
            Vector3 offset = a - b;
            offset.y = 0f;
            return offset.sqrMagnitude;
        }
    }

    /// <summary>
    /// True when a building either commander put down stands where <paramref name="candidate"/>
    /// would go. The physics test in the site rule cannot see a building spawned this frame, and the
    /// Champions kit puts both its depots down in one frame, so the second landed inside the first.
    /// Wrecks count until <c>RemoveBuiltWrecks</c> clears them. Footprints are compared as circles, which
    /// refuses a few tight diagonal fits - every site search throws plenty of darts.
    /// </summary>
    internal static bool OverlapsBuiltUnit(CommanderBuildPreview preview, BuildingDefinition candidate, GlobalPosition target)
    {
        if (Instance == null)
        {
            return false;
        }

        Vector3 size = preview.GetFootprint(candidate);
        float reach = Mathf.Max(size.x, size.z);
        foreach (Unit unit in Instance.builtUnits)
        {
            if (unit == null || unit.definition is not BuildingDefinition built)
            {
                continue;
            }

            Vector3 other = preview.GetFootprint(built);
            float apart = reach + Mathf.Max(other.x, other.z);
            Vector3 offset = unit.transform.GlobalPosition().AsVector3() - target.AsVector3();
            offset.y = 0f;
            if (offset.sqrMagnitude < apart * apart)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The best dock a faction holds, 0 for none. This is the whole naval gate: level 0 buys no
    /// ships at all, and each level up opens the next class. Both commanders read it.
    /// </summary>
    internal static int GetNavalDockLevel(FactionHQ? hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return 0;
        }

        int best = 0;
        foreach (KeyValuePair<Unit, int> entry in service.dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq && entry.Value > best)
            {
                best = entry.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// Where a faction's best dock stands. Ships enter at the sea lane nearest the dock, since the dock
    /// is the harbour.
    /// </summary>
    internal static bool TryGetNavalDockPosition(FactionHQ? hq, out GlobalPosition position)
    {
        return TryGetNavalDockPosition(hq, null, out position);
    }

    /// <summary>The faction's dock nearest <paramref name="near"/> (the one the player selected), or
    /// with no point the highest-level one.</summary>
    internal static bool TryGetNavalDockPosition(FactionHQ? hq, GlobalPosition? near, out GlobalPosition position)
    {
        return TryGetNavalDock(hq, near, out position, out _);
    }

    /// <summary>
    /// The level of the dock a hull bought near <paramref name="near"/> launches from. The gate has to
    /// read that dock, not the faction's best: with the best, a level 1 dock built carriers as soon as
    /// any other dock reached level 3.
    /// </summary>
    internal static int GetNavalDockLevel(FactionHQ? hq, GlobalPosition? near)
    {
        return TryGetNavalDock(hq, near, out _, out int level) ? level : 0;
    }

    internal static bool TryGetNavalDock(FactionHQ? hq, GlobalPosition? near, out GlobalPosition position, out int best)
    {
        position = default;
        best = 0;
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return false;
        }

        float nearest = float.MaxValue;
        foreach (KeyValuePair<Unit, int> entry in service.dockLevels)
        {
            if (entry.Key == null || entry.Key.disabled || entry.Key.NetworkHQ != hq || entry.Value <= 0)
            {
                continue;
            }

            GlobalPosition at = entry.Key.GlobalPosition();
            if (near is GlobalPosition target)
            {
                float distance = FastMath.Distance(at, target);
                if (distance < nearest)
                {
                    nearest = distance;
                    best = entry.Value;
                    position = at;
                }
            }
            else if (entry.Value > best)
            {
                best = entry.Value;
                position = at;
            }
        }

        return best > 0;
    }

    /// <summary>Cost of taking a dock from <paramref name="level"/> to the next one.</summary>
    internal static float GetNavalDockUpgradeCost(int level)
    {
        return Mathf.Max(0f, level >= 2 ? CommanderSettings.NavalDockLevel3UpgradeCost : CommanderSettings.NavalDockUpgradeCost);
    }

    /// <summary>
    /// What a factory costs to put up, scaled by what it is going to build: a plant that turns out
    /// main battle tanks is not the price of one that turns out light carriers. The scale is the
    /// product's own <c>value</c> against <see cref="CommanderSettings.FactoryProductReferenceValue"/>,
    /// clamped, so the game's own ranking does the balancing and no price table goes stale - the same
    /// reasoning as the structure catalogue's prices.
    /// </summary>
    internal static float GetFactoryBuildCost(UnitDefinition? production)
    {
        return production != null && CommanderPriceProfile.TryGetBuildingPrice(GetFactoryLabel(production), out float price)
            ? price
            : GetModFactoryBuildCost(production);
    }

    /// <summary>A factory's name in the Unit Prices list and the editor's GC RTS tab.</summary>
    internal static string GetFactoryLabel(UnitDefinition production)
    {
        return production.unitName + " Factory";
    }

    /// <summary>The mod's own factory price, before the player's Unit Prices profile.</summary>
    internal static float GetModFactoryBuildCost(UnitDefinition? production)
    {
        // The scale is clamped, so on its own a plant for an expensive unit costs the same as one for a
        // cheap one. Past that a plant costs what FactoryPaybackUnits of its product would. Aircraft plants
        // are hand-set so they stay affordable for an early air wing.
        if (production == null)
        {
            return FactoryBuildCost;
        }

        // Ground plants are priced as the economy buildings they are: a set number of their product,
        // no flat floor. The 250 x scale floor made a Type-12 plant pay back in over two hours.
        if (production is not AircraftDefinition)
        {
            return Mathf.Max(MinGroundFactoryBuildCost, PriceOf(production) * GroundFactoryPaybackUnits);
        }

        return production.unitName switch
        {
            "CI-22 Cricket" => 200f,
            "A-19 Brawler" => 350f,
            "T/A-30 Compass" => 400f,
            _ => Mathf.Max(FactoryBuildCost * GetFactoryCostScale(PriceOf(production)), PriceOf(production) * FactoryPaybackUnits),
        };
    }

    /// <summary>The price multiplier a product of this value puts on its plant. Split out so
    /// <see cref="SelfCheck"/> has something to call without a <c>UnitDefinition</c> to hand.</summary>
    internal static float GetFactoryCostScale(float productValue)
    {
        float reference = Mathf.Max(1f, CommanderSettings.FactoryProductReferenceValue);
        return Mathf.Clamp(productValue / reference, MinFactoryCostScale, MaxFactoryCostScale);
    }

    internal static float GetBuildCost(CommanderBuildKind kind)
    {
        return kind switch
        {
            CommanderBuildKind.Factory => FactoryBuildCost,
            CommanderBuildKind.NavalDock => NavalDockBuildCost,
            CommanderBuildKind.FuelExchange or CommanderBuildKind.GasTerminal => GetIncomeCost(kind, 0),
            _ => GetIncomeCost(CommanderBuildKind.Mine, 0),
        };
    }

    /// <summary>
    /// Aircraft and helicopter factories stop at level 2 (one upgrade, double output); ground
    /// factories go to 3 like mines and docks.
    /// </summary>
    internal static int GetFactoryMaxLevel(Factory factory)
    {
        return factory.ProductionUnit is AircraftDefinition ? 2 : MaxLevel;
    }

    /// <summary>
    /// Cost of taking a factory from <paramref name="level"/> to the next one. Each level adds one unit
    /// a cycle, as much as a second plant would, so a ground plant's upgrade costs one plant and pays
    /// back as fast as the plant did. Aircraft plants keep the rising ladder: their price times the
    /// level they start from.
    /// </summary>
    internal static float GetFactoryUpgradeCost(Factory factory, int level)
    {
        float buildCost = GetFactoryBuildCost(factory.ProductionUnit);
        return factory.ProductionUnit is AircraftDefinition ? GetFactoryUpgradeCost(buildCost, level) : buildCost;
    }

    internal static float GetFactoryUpgradeCost(float buildCost, int level)
    {
        return buildCost * level;
    }

    /// <summary>Units a factory adds to the reserve per production cycle. Stock factories are 1.</summary>
    internal static int GetFactoryOutput(Unit? attachedUnit)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || attachedUnit == null)
        {
            return 1;
        }

        return service.factoryLevels.TryGetValue(attachedUnit, out int level) ? level : 1;
    }

    /// <summary>
    /// One runnable check for the price ladder, run at plugin load next to the other self-checks
    /// because a Unity plugin has nowhere else to run a test. The two things that actually break
    /// the economy if a default is retuned badly: an upgrade that gets cheaper as it gets stronger,
    /// and a factory nobody upgraded reporting anything other than its stock output of one.
    /// </summary>
    internal static void SelfCheck()
    {
        for (int level = 1; level < MaxLevel; level++)
        {
            if (GetFactoryUpgradeCost(FactoryBuildCost, level + 1) <= GetFactoryUpgradeCost(FactoryBuildCost, level))
            {
                CommanderPlugin.Log.LogError(
                    $"Economy self-check FAILED: the level {level} price ladder is not increasing. "
                        + "Check the Economy section of the config.");
                return;
            }
        }

        CheckIncomeLadder();
        CheckEnemyIncomeChoice();

        if (GetFactoryOutput(null) != 1)
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: an unupgraded factory is not stock.");
        }

        // The build buttons charge whatever this returns, so a swapped case would bill a factory
        // at mine prices.
        if (GetBuildCost(CommanderBuildKind.Mine) != GetIncomeCost(CommanderBuildKind.Mine, 0)
            || GetBuildCost(CommanderBuildKind.FuelExchange) != GetIncomeCost(CommanderBuildKind.FuelExchange, 0)
            || GetBuildCost(CommanderBuildKind.GasTerminal) != GetIncomeCost(CommanderBuildKind.GasTerminal, 0)
            || GetBuildCost(CommanderBuildKind.Factory) != FactoryBuildCost
            || GetBuildCost(CommanderBuildKind.NavalDock) != NavalDockBuildCost)
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: build costs are crossed.");
        }

        // A factory's price rides on its product's value, so the thing that breaks the economy if
        // that is retuned badly is a heavy plant that costs no more than a light one.
        if (GetFactoryCostScale(1f) >= GetFactoryCostScale(MaxFactoryCostScale * CommanderSettings.FactoryProductReferenceValue)
            || GetFactoryCostScale(0f) < MinFactoryCostScale
            || GetFactoryCostScale(float.MaxValue) > MaxFactoryCostScale)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the factory price does not rise with its product's value. "
                    + "Check Economy/FactoryProductReferenceValue.");
        }

        // The dock ladder is the naval gate: every ship class has to be reachable at some level,
        // and level 0 has to buy nothing, or the dock is either pointless or a hard wall.
        if (CommanderNavalPurchaseService.CountShipTypesAtLevel(0) != 0
            || CommanderNavalPurchaseService.CountShipTypesAtLevel(MaxLevel)
                != System.Enum.GetValues(typeof(ShipType)).Length)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the naval dock ladder does not run from nothing to every ship.");
        }

        // The mine ring has to hold the duel's biggest roll at its own spacing, or the spread
        // search can never place the last mines and they all fall back to the old dart beside the
        // home base - which is the clustering this ring exists to break, back again and silent.
        int mostMines = 0;
        for (int i = 0; i < DuelEnemyMineRolls.Length; i++)
        {
            mostMines = Mathf.Max(mostMines, DuelEnemyMineRolls[i]);
        }

        if (StarterRingMinShare >= MineRingMinShare
            || MineRingMinShare >= MineRingMaxShare
            || MineRingMaxShare > 1f
            || (2f * MineRingMinShare * Mathf.Sin(Mathf.PI / mostMines)) < MineSpacingShare)
        {
            CommanderPlugin.Log.LogError(
                $"Economy self-check FAILED: the mine ring cannot hold {mostMines} mines at its spacing. "
                    + "Check MineRingMinShare, MineRingMaxShare and MineSpacingShare.");
        }

        // The catalogue files every building under its own BuildingType, so a game patch that adds
        // one would silently file a whole category under OTHER.
        foreach (BuildingType type in System.Enum.GetValues(typeof(BuildingType)))
        {
            if (GetCategoryLabel(type) == "OTHER")
            {
                CommanderPlugin.Log.LogError(
                    $"Economy self-check FAILED: building category {type} has no label.");
                return;
            }
        }
    }

    internal static string FundsLabel(float funds)
    {
        return UnitConverter.ValueReading(funds) ?? funds.ToString("F1", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The opening balance <paramref name="hq"/>'s faction was given, read off the loaded mission
    /// itself - the same <c>MissionFaction.startingBalance</c> <c>FactionHQ.OnMissionLoad</c> copies.
    /// Never off the HQ's live fields: the treasury hold above raises the threshold all match, and
    /// by the enemy's first review it already read 2970 and 2760 on a mission authored at 1000,
    /// which is what handed the enemy a triple head start.
    /// </summary>
    internal static float GetAuthoredThreshold(FactionHQ hq)
    {
        return TryGetAuthoredBalance(hq, out float balance) ? balance : hq.excessFundsThreshold;
    }

    /// <summary>
    /// Whether the loaded mission authors <paramref name="hq"/>'s faction at all. An authored 0 is a
    /// real answer: reading it as "none" handed an enemy set to 0 the player's whole balance.
    /// </summary>
    internal static bool TryGetAuthoredBalance(FactionHQ hq, out float balance)
    {
        NuclearOption.SavedMission.Mission? mission = MissionManager.CurrentMission;
        if (mission != null
            && hq.faction != null
            && mission.TryGetFaction(hq.faction.factionName, out NuclearOption.SavedMission.MissionFaction faction)
            && faction != null)
        {
            balance = faction.startingBalance;
            return true;
        }

        balance = 0f;
        return false;
    }

    /// <summary>The building a BUILD card pictures: what that kind would land as right now.</summary>
    internal BuildingDefinition? GetPreviewDefinition(CommanderBuildKind kind)
    {
        return ResolveBuildDefinition(kind, SelectedProduction);
    }

    internal bool IsBuiltNavalDock(Unit? unit)
    {
        return unit != null && dockLevels.ContainsKey(unit);
    }

    internal int GetNavalDockLevel(Unit dock)
    {
        return dockLevels.TryGetValue(dock, out int level) ? level : 0;
    }

    internal int GetFactoryLevel(Factory factory)
    {
        return factory.attachedUnit != null && factoryLevels.TryGetValue(factory.attachedUnit, out int level)
            ? level
            : 1;
    }

    internal void BeginBuild(CommanderBuildKind kind)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        string label = BuildLabel(kind);
        if (hq == null)
        {
            StatusText = "No faction HQ.";
            return;
        }

        // Buildings are spawned through the server object manager, so a guest asks the host.
        if (!hq.IsServer && !CommanderNetwork.HostHasMod)
        {
            StatusText = CommanderNetwork.HostMissingReason;
            return;
        }

        if (ResolveBuildDefinition(kind, SelectedProduction) == null)
        {
            StatusText = $"No building prefab available for a {label}.";
            return;
        }

        if (kind == CommanderBuildKind.Factory && SelectedProduction == null)
        {
            StatusText = "This faction has no ground unit a factory could produce.";
            return;
        }

        if (hq.factionFunds < (kind == CommanderBuildKind.Factory ? GetFactoryBuildCost(SelectedProduction) : GetBuildCost(kind)))
        {
            StatusText = $"Insufficient faction funds for a {label}.";
            return;
        }

        PendingBuild = kind;
        PendingStructure = null;
        StatusText = $"Select the {label} site in the 3D world. {CommanderSettings.RotateBuilding.MainKey} rotates it.";
    }

    /// <summary>The rotate key while a building is being placed.</summary>
    internal void RotatePlacement(float degrees)
    {
        preview.Rotate(degrees);
    }

    /// <summary>Draws the armed control tower's build ring and a hangar's exit arrow. GUI pass, RTS view.</summary>
    internal void DrawPlacementRing(Camera camera)
    {
        if (AwaitingPlacement)
        {
            preview.DrawTowerRing(camera);
            preview.DrawExitArrow(camera);
        }
    }

    internal void CancelBuild()
    {
        if (PendingBuild == CommanderBuildKind.None)
        {
            return;
        }

        StatusText = $"{PendingBuildLabel()} placement cancelled.";
        PendingBuild = CommanderBuildKind.None;
        PendingStructure = null;
        preview.Hide();
    }

    /// <summary>Placement click from <see cref="CommanderInputController"/>. True once handled.</summary>
    internal bool TryPlaceBuildingFromWorld(Vector2 screenPosition)
    {
        CommanderBuildKind kind = PendingBuild;
        if (kind == CommanderBuildKind.None)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition position))
        {
            StatusText = "No valid ground under the cursor.";
            return true;
        }

        string label = PendingBuildLabel();
        BuildingDefinition? structure = PendingStructure;
        BuildingDefinition? placed = PendingDefinition;
        float cost = PendingBuildCost;
        Quaternion rotation = preview.Rotation;

        // Same snap the ghost made, so a hangar lands where it was drawn.
        if (placed != null
            && preview.TrySnapToRoad(placed, position, out GlobalPosition roadside, out Quaternion roadFacing))
        {
            position = roadside;
            rotation = roadFacing;
        }

        // The ghost has already said whether this site is legal; the click obeys it rather than
        // building a refinery through the highway because the player was quick on the mouse.
        if (placed != null
            && !preview.IsSiteAllowed(
                placed, position, CommanderGameAccess.GetLocalHq(), out string blocked, kind == CommanderBuildKind.NavalDock, rotation))
        {
            StatusText = blocked;
            return true;
        }

        // The same repeat key the supply window uses keeps the placement armed, so a row of
        // bunkers is one trip to the BUILD window instead of five.
        bool repeat = CommanderShortcutInput.IsPressed(CommanderSettings.RepeatDeployment);
        if (!repeat)
        {
            PendingBuild = CommanderBuildKind.None;
            PendingStructure = null;
            preview.Hide();
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || hq.factionFunds < cost)
        {
            PendingBuild = CommanderBuildKind.None;
            PendingStructure = null;
            preview.Hide();
            StatusText = $"{label} could not be built.";
            return true;
        }

        UnitDefinition? production = SelectedProduction;
        if (CommanderNetwork.Forward(
            "build",
            CommanderNetwork.Num((int)kind),
            structure?.jsonKey ?? string.Empty,
            production?.jsonKey ?? string.Empty,
            CommanderNetwork.Position(position),
            CommanderNetwork.Num(rotation.eulerAngles.y)))
        {
            StatusText = CommanderNetwork.HostHasMod ? $"{label}: blueprint placed." : CommanderNetwork.HostMissingReason;
            return true;
        }

        if (!CommitBuild(hq, kind, structure, production, position, rotation))
        {
            PendingBuild = CommanderBuildKind.None;
            PendingStructure = null;
            preview.Hide();
        }

        return true;
    }

    internal void SelectProduction(int index)
    {
        if (index >= 0 && index < productionOptions.Count)
        {
            productionIndex = index;
        }
    }

    internal void UpgradeFactory(Factory factory)
    {
        Unit? attached = factory.attachedUnit;
        int level = GetFactoryLevel(factory);
        if (attached == null || level >= GetFactoryMaxLevel(factory))
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetFactoryUpgradeCost(factory, level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        if (CommanderNetwork.Forward("upgrade", CommanderNetwork.Id(attached)))
        {
            StatusText = CommanderNetwork.HostHasMod ? "Upgrading." : CommanderNetwork.HostMissingReason;
            return;
        }

        hq.AddFunds(-cost);
        factoryLevels[attached] = level + 1;
        StatusText = $"{CommanderGameAccess.GetUnitLabel(attached)} at level {level + 1}: "
            + $"{level + 1} units per cycle.";
    }

    internal void UpgradeNavalDock(Unit dock)
    {
        int level = GetNavalDockLevel(dock);
        if (level is <= 0 or >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetNavalDockUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        if (CommanderNetwork.Forward("upgrade", CommanderNetwork.Id(dock)))
        {
            StatusText = CommanderNetwork.HostHasMod ? "Upgrading." : CommanderNetwork.HostMissingReason;
            return;
        }

        hq.AddFunds(-cost);
        dockLevels[dock] = level + 1;
        StatusText = $"Naval dock at level {level + 1}: "
            + $"{CommanderNavalPurchaseService.GetLevelUnlockLabel(level + 1)}.";
    }

    /// <summary>
    /// Tears a commander-built building down. There is no refund: the money went into the ground,
    /// and a full refund would make a mine a free scouting tool.
    /// </summary>
    internal void Demolish(Unit? unit)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (unit == null || hq == null)
        {
            return;
        }

        if (CommanderNetwork.Forward("demolish", CommanderNetwork.Id(unit)))
        {
            StatusText = CommanderNetwork.HostHasMod ? "Demolishing." : CommanderNetwork.HostMissingReason;
            return;
        }

        // Despawning goes through the server object manager, same as building does.
        if (!hq.IsServer)
        {
            StatusText = "Only the host can demolish buildings.";
            return;
        }

        string label = CommanderGameAccess.GetUnitLabel(unit);
        StatusText = Despawn(unit) ? $"{label} demolished." : $"{label} could not be demolished.";
    }

    private static string BuildLabel(CommanderBuildKind kind)
    {
        return kind switch
        {
            CommanderBuildKind.Factory => "factory",
            CommanderBuildKind.NavalDock => "naval dock",
            CommanderBuildKind.Structure => "structure",
            _ => GetIncomeName(kind),
        };
    }

    /// <summary>
    /// Stops the Basegame paying the mod's economy out to individual pilots' wallets.
    /// </summary>
    /// <remarks>
    /// <c>FactionHQ.DistributeFunds</c> runs every 30 s and hands each player
    /// <c>(factionFunds - excessFundsThreshold) * excessFundsDistributePercent</c> — a quarter of
    /// everything the faction holds above the balance the mission was authored with — on top of the
    /// mission's own regular income. In a normal match that is the point: the faction bankrolls its
    /// pilots. Under a commander it is a leak straight out of the treasury the player is trying to
    /// build up, which is exactly what "the gold mine says +20/min and the faction balance does not
    /// move" was: the mines paid in, and the very next distribution took a quarter of it back out
    /// to the personal account.
    /// <para>
    /// The threshold is a plain float, so raising it to whatever the faction now holds makes the
    /// treasury never look excessive. The mission's authored <c>regularIncome</c> allowance is left
    /// alone, so a player who also flies still gets their own money the normal way.
    /// </para>
    /// </remarks>
    private static void HoldFundsInTreasury()
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer)
            {
                continue;
            }

            if (hq.factionFunds > hq.excessFundsThreshold)
            {
                hq.excessFundsThreshold = hq.factionFunds;
            }
        }
    }

    /// <summary>
    /// Files a newly built hangar or landing pad under the nearest airfield its faction holds.
    /// </summary>
    /// <remarks>
    /// The base game only calls <c>Airbase.AddBuilding</c> for mission-authored buildings. Unlinked, a
    /// built pad has a null <c>Hangar.parentAirbase</c>, so AIR COMMAND cannot filter by hangar, the
    /// departure list has no row for it, and launches fall back to <c>Airbase.TrySpawnAircraft</c>
    /// which picks its own doors. Linking it also adds it to the field's aircraft list.
    /// </remarks>
    private static void AttachHangarToNearestAirbase(FactionHQ hq, Unit spawned)
    {
        if (spawned is not Building building || building.GetComponentInChildren<Hangar>(true) == null)
        {
            return;
        }

        Vector3 position = spawned.transform.position;
        Airbase? nearest = CommanderForwardBase.FindNear(hq, position);
        float bestDistance = nearest != null ? 0f : float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = (airbase.center.position - position).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = airbase;
            }
        }

        if (nearest == null)
        {
            CommanderPlugin.Log.LogWarning(
                $"{spawned.name} carries a hangar but its faction holds no airbase to file it under; "
                + "aircraft cannot be spawned from it.");
            return;
        }

        building.SetAirbase(nearest);
        LogHangarRoster(building, nearest);
    }

    /// <summary>
    /// Names the airframes a built pad will actually take. <c>Hangar.availableAircraft</c> is a
    /// serialized field on the prefab, i.e. asset data a decompile cannot read, so an empty list is
    /// otherwise a pad that silently refuses everything.
    /// </summary>
    private static void LogHangarRoster(Building building, Airbase airbase)
    {
        Hangar[] hangars = building.GetComponentsInChildren<Hangar>(true);
        for (int i = 0; i < hangars.Length; i++)
        {
            if (hangars[i] == null)
            {
                continue;
            }

            AircraftDefinition[] available = hangars[i].GetAvailableAircraft();
            System.Text.StringBuilder names = new();
            for (int j = 0; j < available.Length; j++)
            {
                if (available[j] == null)
                {
                    continue;
                }

                if (names.Length > 0)
                {
                    names.Append(", ");
                }

                names.Append(available[j].unitName);
            }

            CommanderPlugin.Log.LogInfo(
                $"Built hangar {building.name} filed under {airbase.name}; airframes: "
                + (names.Length > 0 ? names.ToString() : "NONE (this pad can spawn nothing)"));
        }
    }

    private static BuildingDefinition? FindBuilding(Encyclopedia encyclopedia, string[] keys)
    {
        for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            for (int i = 0; i < encyclopedia.buildings.Count; i++)
            {
                BuildingDefinition candidate = encyclopedia.buildings[i];
                if (candidate != null
                    && candidate.unitPrefab != null
                    && candidate.jsonKey == keys[keyIndex])
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static BuildingDefinition? FindAnyBuilding(Encyclopedia encyclopedia)
    {
        for (int i = 0; i < encyclopedia.buildings.Count; i++)
        {
            if (encyclopedia.buildings[i] != null && encyclopedia.buildings[i].unitPrefab != null)
            {
                return encyclopedia.buildings[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Champions: every AI commander gets a flat income on top of its buildings. A faction with no
    /// human player gets no mission <c>regularIncome</c> at all. Team Duel PvE pays only the
    /// per-opponent share.
    /// </summary>
    private static void PayChampionsStipend()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            float perMinute = hq != null && hq.IsServer ? GetAiStipendPerMinute(hq, localHq) : 0f;
            if (perMinute > 0f)
            {
                hq!.AddFunds(perMinute * IncomeIntervalSeconds / 60f);
            }
        }
    }

    /// <summary>What <see cref="PayChampionsStipend"/> pays this faction a minute, 0 for a human side.</summary>
    private static float GetAiStipendPerMinute(FactionHQ hq, FactionHQ? localHq)
    {
        if (!CommanderEnemyCommanderService.IsAiFaction(hq, localHq))
        {
            return 0f;
        }

        // A GC RTS map may set the AI's flat income in the editor, on a duel map as well; only
        // Champions and Team Duel PvE add the per-opponent share.
        bool champions = CommanderEnemyCommanderService.IsChampionsMission;
        bool teamPve = CommanderEnemyCommanderService.IsTeamDuelPveMission;
        float flat = CommanderMapSettings.AiIncomePerMinute ?? (champions ? ChampionsEnemyIncomePerMinute : 0f);
        float perOpponent = teamPve ? TeamPveEnemyIncomePerOpponentPerMinute : champions ? ChampionsEnemyIncomePerOpponentPerMinute : 0f;

        // Scales with the lobby: every human on another team adds to the AI's income.
        int opponents = 0;
        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            if (perOpponent > 0f && other != null && other != hq)
            {
                opponents += other.GetPlayers(sortByScore: false).Count;
            }
        }

        return Mathf.Max(0f, flat) + (perOpponent * opponents);
    }

    /// <summary>The map's base income: every side is paid it like an income building it does not have.</summary>
    private static void PayBaseIncome()
    {
        float perMinute = GetBaseIncomePerMinute();
        if (perMinute <= 0f)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.IsServer)
            {
                hq.AddFunds(perMinute * IncomeIntervalSeconds / 60f);
            }
        }
    }

    /// <summary>The map's base income a minute, with the income multiplier buildings pay under.</summary>
    private static float GetBaseIncomePerMinute()
    {
        return CommanderMapSettings.BaseIncomePerMinute * CommanderPriceProfile.IncomeMultiplier;
    }

    /// <summary>
    /// Pays for one blueprint; repair vehicles do the building (CommanderEconomyServiceConstruction.cs).
    /// The host's own placement click and a guest's forwarded one both end here, so a guest builds
    /// under exactly the host's rules and prices.
    /// </summary>
    private bool CommitBuild(
        FactionHQ hq,
        CommanderBuildKind kind,
        BuildingDefinition? structure,
        UnitDefinition? production,
        GlobalPosition position,
        Quaternion rotation)
    {
        float cost = kind switch
        {
            CommanderBuildKind.Structure => GetStructureCost(structure),
            CommanderBuildKind.Factory => GetFactoryBuildCost(production),
            _ => GetBuildCost(kind),
        };
        string label = kind == CommanderBuildKind.Structure
            ? structure != null ? GetStructureLabel(structure) : "Structure"
            : BuildLabel(kind);
        BuildingDefinition? placed = kind == CommanderBuildKind.Structure ? structure : ResolveBuildDefinition(kind, production);
        if (!hq.IsServer
            || hq.factionFunds < cost
            || placed == null
            || (kind == CommanderBuildKind.Factory && production == null))
        {
            StatusText = $"{label} could not be built.";
            return false;
        }

        // A blueprint is not spawned until it is finished, so nothing downstream checks the site
        // before the money is taken; a guest's forwarded click has had no ghost to check it either.
        if (!preview.IsSiteAllowed(placed, position, hq, out string blocked, kind == CommanderBuildKind.NavalDock, rotation))
        {
            StatusText = blocked;
            return false;
        }

        hq.AddFunds(-cost);
        PlaceBlueprint(kind, placed, production, hq, position, cost, label, rotation);
        return true;
    }

    private string PendingBuildLabel()
    {
        return PendingBuild == CommanderBuildKind.Structure && PendingStructure != null
            ? GetStructureLabel(PendingStructure)
            : BuildLabel(PendingBuild);
    }

    /// <summary>
    /// Pays every faction's income buildings. <c>factionFunds</c> is a server SyncVar, so this is a no-op on a
    /// pure multiplayer client — the host runs the economy for everyone.
    /// </summary>
    /// <summary>Forgets a built building and removes it from the world. Host only.</summary>
    private bool Despawn(Unit unit)
    {
        builtUnits.Remove(unit);
        wreckedSince.Remove(unit);
        incomeLevels.Remove(unit);
        incomeKinds.Remove(unit);
        factoryLevels.Remove(unit);
        dockLevels.Remove(unit);
        depotLevels.Remove(unit);

        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (unit.Identity == null || manager?.ServerObjectManager == null)
        {
            return false;
        }

        manager.ServerObjectManager.Destroy(unit.Identity, !unit.Identity.IsSceneObject);
        RefreshFriendlyLists();
        return true;
    }

    /// <summary>
    /// A commander-built building cannot be repaired (<c>CommanderEconomyPatches</c>), so once
    /// destroyed it is gone: the wreck stands for <see cref="BuiltWreckSeconds"/> and is then
    /// despawned, which also frees the site. Host only, runs on the income tick.
    /// </summary>
    private void RemoveBuiltWrecks()
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !hq.IsServer)
        {
            return;
        }

        staleUnits.Clear();
        foreach (Unit unit in builtUnits)
        {
            if (unit == null)
            {
                continue;
            }

            if (!unit.disabled)
            {
                wreckedSince.Remove(unit);
                continue;
            }

            if (!wreckedSince.TryGetValue(unit, out float since))
            {
                wreckedSince[unit] = Time.time;
            }
            else if (Time.time - since >= BuiltWreckSeconds)
            {
                staleUnits.Add(unit);
            }
        }

        for (int i = 0; i < staleUnits.Count; i++)
        {
            Despawn(staleUnits[i]);
        }

        staleUnits.Clear();
    }

    private void PayIncome()
    {
        HoldFundsInTreasury();
        PayChampionsStipend();
        PayBaseIncome();
        if (incomeLevels.Count == 0)
        {
            return;
        }

        float share = IncomeIntervalSeconds / 60f;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        float enemyMultiplier = CommanderSettings.EnemyDifficulty == CommanderEnemyCommanderService.DifficultyChallenging
            ? Mathf.Max(0f, CommanderSettings.EnemyMineIncomeMultiplier)
            : 1f;
        staleUnits.Clear();
        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            Unit mine = entry.Key;
            if (mine == null || mine.disabled)
            {
                if (mine != null && mine.NetworkHQ != null && mine.NetworkHQ.faction != null)
                {
                    CommanderPlugin.Log.LogInfo(
                        $"Economy: {mine.NetworkHQ.faction.name} lost a level {entry.Value} {GetIncomeName(GetIncomeKind(mine))}.");
                }

                staleUnits.Add(mine!);
                continue;
            }

            FactionHQ hq = mine.NetworkHQ;
            if (hq != null && hq.IsServer)
            {
                // An AI commander's mine pays more: it has no pilot's kill reward and no second
                // job, so parity mines leave it behind over a long match. A human guest's do not.
                float rate = CommanderEnemyCommanderService.IsAiFaction(hq, localHq) ? enemyMultiplier : 1f;
                hq.AddFunds(GetIncomePerMinute(GetIncomeKind(mine), entry.Value) * share * rate);
            }
        }

        for (int i = 0; i < staleUnits.Count; i++)
        {
            incomeLevels.Remove(staleUnits[i]);
            incomeKinds.Remove(staleUnits[i]);
        }

        staleUnits.Clear();
    }

    /// <summary>One of the three income buildings.</summary>
    private Unit? SpawnIncome(
        FactionHQ hq, CommanderBuildKind kind, GlobalPosition position, bool randomRotation = false, Quaternion? facing = null)
    {
        Unit? building = SpawnBuilding(
            hq, position, ResolveDefinition(kind), GetIncomeName(kind), randomRotation, facing: facing);
        if (building == null)
        {
            return null;
        }

        incomeLevels[building] = 1;
        incomeKinds[building] = kind;
        return building;
    }

    /// <summary>
    /// The dock that gates naval purchases. It carries no game behaviour of its own — the prefab is
    /// scenery — so everything it does lives in <see cref="GetNavalDockLevel(FactionHQ)"/>, which
    /// both the player's naval window and the enemy commander read before they buy a ship.
    /// </summary>
    private Unit? SpawnNavalDock(FactionHQ hq, GlobalPosition position, bool randomRotation = false, Quaternion? facing = null)
    {
        Unit? dock = SpawnBuilding(
            hq,
            position,
            ResolveDefinition(CommanderBuildKind.NavalDock),
            NavalDockDisplayName,
            randomRotation,
            navalDock: true,
            facing: facing);
        if (dock == null)
        {
            return null;
        }

        dockLevels[dock] = 1;
        ForgetDockDepot(hq, dock);
        return dock;
    }

    /// <summary>
    /// Builds a working factory: the prefab carries the <see cref="Factory"/> component, and
    /// <c>SetFactory</c> is what registers the production cycle — the same call the game makes for
    /// a mission-authored factory. Its cadence cannot be changed afterwards, so the interval is
    /// chosen here and the upgrades move batch size instead.
    /// </summary>
    private Unit? SpawnFactory(
        FactionHQ hq,
        GlobalPosition position,
        UnitDefinition production,
        bool randomRotation = false,
        Quaternion? facing = null)
    {
        Unit? unit = SpawnBuilding(
            hq,
            position,
            ResolveFactoryDefinition(production),
            $"{production.code} Factory",
            randomRotation,
            facing: facing);
        if (unit == null)
        {
            return null;
        }

        if (!unit.TryGetComponent(out Factory factory))
        {
            CommanderPlugin.Log.LogWarning(
                "The factory building prefab has no Factory component, so it will never produce.");
            return null;
        }

        factory.SetFactory(production.jsonKey, FactoryProductionSeconds);
        return unit;
    }

    /// <summary>
    /// Spawns the building. The player's placements are unrotated so the ghost preview is exactly
    /// what lands; the enemy commander still scatters its rotations, because nobody is watching a
    /// ghost for those.
    /// </summary>
    /// <summary>
    /// Spawns the building under <paramref name="displayName"/>. The player's placements are
    /// unrotated so the ghost preview is exactly what lands; the enemy commander still scatters
    /// its rotations, because nobody is watching a ghost for those.
    /// </summary>
    /// <remarks>
    /// The name is written twice on purpose. <c>UniqueName</c> is the id the game registers the
    /// unit under and the first thing <c>GetUnitLabel</c> reads, so it gets a numbered copy;
    /// <c>unitName</c> is the SyncVar the Basegame map-icon hover reads
    /// (<c>UnitMapIcon.GetInfoText</c>), so it gets the plain one, through
    /// <c>BuildingStartServerPrefix</c> because the spawn message is the only time it is sent. Set
    /// neither and a gold mine reports itself as whichever industrial prefab it happens to be wearing.
    /// </remarks>
    private Unit? SpawnBuilding(
        FactionHQ hq,
        GlobalPosition position,
        BuildingDefinition? definition,
        string displayName,
        bool randomRotation = false,
        bool navalDock = false,
        Quaternion? facing = null)
    {
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || spawner == null)
        {
            return null;
        }

        Quaternion rotation = facing
            ?? (randomRotation ? Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) : Quaternion.identity);

        // Every build path in the mod ends here, so the site rules are enforced once, in the one
        // place both commanders route through, rather than at each caller.
        if (!preview.IsSiteAllowed(definition, position, hq, out _, navalDock, rotation))
        {
            return null;
        }

        GlobalPosition ground = preview.GetBuildGround(definition, position, rotation);
        Vector3 local = ground.ToLocalPosition() + (rotation * definition.spawnOffset);
        Unit? spawned;
        spawningBuildingName = displayName;
        try
        {
            spawned = spawner.SpawnFromUnitDefinitionInEditor(
                definition,
                local.ToGlobalPosition(),
                rotation,
                hq,
                $"{displayName} {++builtNameCounter}");
        }
        finally
        {
            spawningBuildingName = null;
        }

        if (spawned != null)
        {
            builtUnits.Add(spawned);
            CommanderForwardBase.OnBuilt(hq, spawned, builtUnits);
            AttachHangarToNearestAirbase(hq, spawned);
            CommanderBuiltHangar.EnsureFloor(spawned, preview);
            CommanderForwardBase.ConnectHangar(spawned, preview);
        }

        return spawned;
    }

    /// <summary>Income buildings of every kind, or of <paramref name="kind"/> only.</summary>
    private int CountMines(FactionHQ hq, CommanderBuildKind? kind = null)
    {
        int count = 0;
        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && entry.Key.NetworkHQ == hq
                && (kind == null || GetIncomeKind(entry.Key) == kind))
            {
                count++;
            }
        }

        // A mine going up counts, or the enemy ladder queues another every review until it rises.
        for (int i = 0; i < IncomeKinds.Count; i++)
        {
            if (kind == null || IncomeKinds[i] == kind)
            {
                count += CountBlueprints(hq, IncomeKinds[i]);
            }
        }

        return count;
    }

    private void RefreshFriendlyLists()
    {
        // While the host builds for a guest, "friendly" would mean the guest's faction: those lists
        // are the host's BUILD window, and the production roster below is cached on first fill.
        if (CommanderNetwork.IsHandlingGuest)
        {
            return;
        }

        friendlyFactories.Clear();
        friendlyMines.Clear();
        friendlyDocks.Clear();
        builtUnits.RemoveWhere(unit => unit == null);
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        // The factory production list is collected here rather than from the BUILD window, so the
        // roster walk happens on this 5 s tick instead of every OnGUI frame. It is per faction and
        // does not change mid-mission, so once it has entries it is left alone.
        if (productionOptions.Count == 0)
        {
            CollectFactoryProducts(productionOptions, CommanderGameAccess.GetSupplyHq());
        }

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

            friendlyFactories.Add(factory);
        }

        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                friendlyMines.Add(entry.Key);
            }
        }

        foreach (KeyValuePair<Unit, int> entry in dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                friendlyDocks.Add(entry.Key);
            }
        }
    }

    /// <summary>The prefab a build kind lands as, the factory's depending on what it will produce.</summary>
    private BuildingDefinition? ResolveBuildDefinition(CommanderBuildKind kind, UnitDefinition? production)
    {
        return kind == CommanderBuildKind.Factory
            ? ResolveFactoryDefinition(production)
            : ResolveDefinition(kind);
    }

    /// <summary>
    /// Which hall a factory wears, by its price: a plant costing more than
    /// <see cref="LargeFactoryMinCost"/> gets the wide shed, every cheaper one the tall vertical plant.
    /// </summary>
    private BuildingDefinition? ResolveFactoryDefinition(UnitDefinition? production)
    {
        if (production == null || GetFactoryBuildCost(production) > LargeFactoryMinCost)
        {
            return ResolveDefinition(CommanderBuildKind.Factory);
        }

        if (lightFactoryDefinitionResolved)
        {
            return lightFactoryDefinition ?? ResolveDefinition(CommanderBuildKind.Factory);
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.buildings == null)
        {
            return null;
        }

        lightFactoryDefinition = FindBuilding(encyclopedia, LightFactoryPrefabKeys);
        lightFactoryDefinitionResolved = true;
        CommanderPlugin.Log.LogInfo(lightFactoryDefinition != null
            ? $"Light unit factories will be built from the '{lightFactoryDefinition.jsonKey}' prefab."
            : "No tall factory prefab is available, so every factory will be the wide one.");
        return lightFactoryDefinition ?? ResolveDefinition(CommanderBuildKind.Factory);
    }

    private BuildingDefinition? ResolveDefinition(CommanderBuildKind kind)
    {
        if (kind == CommanderBuildKind.NavalDock)
        {
            if (dockDefinitionResolved)
            {
                return dockDefinition;
            }

            Encyclopedia? dockEncyclopedia = Encyclopedia.i;
            if (dockEncyclopedia?.buildings == null)
            {
                return null;
            }

            dockDefinition = FindBuilding(dockEncyclopedia, NavalDockPrefabKeys)
                ?? FindAnyBuilding(dockEncyclopedia);
            dockDefinitionResolved = true;

            // The game ships no dock prefab, so which structure stands in for one is a guess that
            // a patch can invalidate. Say which one won in the console rather than silently
            // building a shed and calling it a harbour.
            CommanderPlugin.Log.LogInfo(dockDefinition != null
                ? $"Naval dock will be built from the '{dockDefinition.jsonKey}' prefab."
                : "No building prefab is available for a naval dock, so naval purchases stay locked.");
            return dockDefinition;
        }

        if (IsIncomeKind(kind))
        {
            return ResolveIncomeDefinition(kind);
        }

        if (factoryDefinitionResolved)
        {
            return factoryDefinition;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia == null || encyclopedia.buildings == null)
        {
            return null;
        }

        // A factory has to be the factory prefab (the Factory component is what produces), so there
        // is no fall back to whatever building happens to be first.
        factoryDefinition = FindBuilding(encyclopedia, FactoryPrefabKeys);
        factoryDefinitionResolved = true;
        return factoryDefinition;
    }

    private void TickPersistentAsHost()
    {
        // Ahead of everything, and this service is registered ahead of the enemy commander, so the
        // enemy's first review already sees the dealt map.
        TickChampionsStart();
        TickStartingBuilders();

        // A draft has no economy: no income, and no enemy build ladder spending money it cannot have.
        bool draft = CommanderDraftService.IsDraftMission;
        if (CommanderScheduler.IsDueHeavy(ref nextIncomeAt, IncomeIntervalSeconds))
        {
            AdoptMissionBuildings();
            RemoveBuiltWrecks();
            if (!draft)
            {
                PayIncome();
            }

            PayDefenceUpkeep();
        }

        // Same ordering rule as the enemy commander: do not burn a review while no mission is
        // loaded, or the enemy's first mine lands a review after the match already started.
        float reviewInterval = CommanderEnemyCommanderService.IsChampionsMission
            ? ChampionsEnemyReviewIntervalSeconds
            : EnemyReviewIntervalSeconds;
        if (!draft
            && CommanderEnemyCommanderService.EffectiveMode != CommanderEnemyCommanderService.ModeOff
            && CommanderGameAccess.GetLocalHq() != null
            && CommanderScheduler.IsDueHeavy(ref nextEnemyReviewAt, reviewInterval))
        {
            ReviewEnemies();
        }

        TickConstruction();
    }
}
