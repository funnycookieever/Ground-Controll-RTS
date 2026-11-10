using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's factories. Its air force now comes out of them, so up to
/// <see cref="MaxFactoryBases"/> of the land bases it holds fill up to <see cref="EnemyGroundFactoriesPerBase"/> ground,
/// <see cref="EnemyAirFactoriesPerBase"/> aircraft and <see cref="EnemyNavalFactoriesPerBase"/>
/// ship plants, a mix rather than one kind first. An aircraft plant is only built for an airframe
/// a field it holds can launch, and a ship plant only for a hull its dock's level unlocks: a plant
/// whose output can never leave the hangar is money spent on nothing.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    private const int EnemyGroundFactoriesPerBase = 5;
    private const int EnemyAirFactoriesPerBase = 5;
    private const int EnemyNavalFactoriesPerBase = 2;

    /// <summary>Most bases that hold plants: half the bases held, rounded up, at most this many.</summary>
    private const int MaxFactoryBases = 3;

    /// <summary>Share of the enemy's income put by for its next factory. See <see cref="GetEnemyFactoryFund"/>.</summary>
    private const float FactoryFundIncomeShare = 0.25f;

    /// <summary>A plant this close to a base counts as that base's.</summary>
    private const float FactoryCoverageMeters = 3000f;

    /// <summary>How long a faction's product lists stand. They only change with the bases, the
    /// fields and the dock it holds, and building them asks every field about every airframe.</summary>
    private const float FactoryOptionsRefreshSeconds = 10f;

    private static Factory[] factoriesThisFrame = System.Array.Empty<Factory>();
    private static int factoriesFrame = -1;

    private readonly Dictionary<FactionHQ, EnemyFactoryOptions> enemyFactoryOptions = new();
    private readonly Dictionary<FactionHQ, (float Fund, float At)> enemyFactoryFunds = new();
    private readonly List<(GlobalPosition Position, UnitDefinition Product)> enemyFactorySites = new();
    private readonly HashSet<UnitDefinition> enemyFactoryProducts = new();
    private readonly List<(GlobalPosition Center, int Ground, int Air, int Naval)> factoryBaseScratch = new();

    /// <summary>
    /// The enemy's factory savings: <see cref="FactoryFundIncomeShare"/> of what its income buildings
    /// pay goes here every minute and nowhere else, and once it covers the next plant that plant is
    /// queued out of turn, whatever rung the ladder is on, so plants are not starved behind the dock,
    /// mines and income upgrades. A mark on the balance like the mine fund, not money moved: clamped to the
    /// balance and the plant's price, and <c>Review</c> holds it back from the unit spender.
    /// </summary>
    internal static float GetEnemyFactoryFund(FactionHQ hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return 0f;
        }

        float price = service.GetEnemyFactoryReserve(hq);
        float now = Time.time;
        service.enemyFactoryFunds.TryGetValue(hq, out (float Fund, float At) saved);
        float fund = saved.Fund;
        if (saved.At > 0f)
        {
            fund += service.GetIncomePerMinute(hq) * FactoryFundIncomeShare * ((now - saved.At) / 60f);
        }

        fund = price <= 0f ? 0f : Mathf.Min(fund, price, Mathf.Max(0f, hq.factionFunds));
        service.enemyFactoryFunds[hq] = (fund, now);
        return fund;
    }

    /// <summary>Every <c>Factory</c> in the scene, found once a frame: a review asks the ladder
    /// several times, and <c>FindObjectsOfType</c> walks every object each time.</summary>
    private static Factory[] GetFactories()
    {
        if (factoriesFrame != Time.frameCount)
        {
            factoriesThisFrame = Object.FindObjectsOfType<Factory>();
            factoriesFrame = Time.frameCount;
        }

        return factoriesThisFrame;
    }

    private static int CountFactories(FactionHQ hq)
    {
        return CountBuiltFactories(hq) + CountBlueprints(hq, CommanderBuildKind.Factory);
    }

    private static int CountBuiltFactories(FactionHQ hq)
    {
        int count = 0;
        Factory[] factories = GetFactories();
        for (int i = 0; i < factories.Length; i++)
        {
            Unit? attached = factories[i] == null ? null : factories[i].attachedUnit;
            if (attached != null && !attached.disabled && attached.NetworkHQ == hq)
            {
                count++;
            }
        }

        return count;
    }

    private static void ConsiderFactoryKind(
        List<UnitDefinition> kindOptions, int built, int perBase, ref List<UnitDefinition>? options, ref int fewest)
    {
        if (kindOptions.Count > 0 && built < perBase && built < fewest)
        {
            options = kindOptions;
            fewest = built;
        }
    }

    /// <summary>Queues the next plant when the factory fund covers it. See <see cref="GetEnemyFactoryFund"/>.</summary>
    private bool TryBuildFundedFactory(FactionHQ hq)
    {
        float price = GetEnemyFactoryReserve(hq);
        if (price <= 0f
            || GetEnemyFactoryFund(hq) < price
            || hq.factionFunds < price
            || CountBlueprints(hq) >= GetEnemyBlueprintLimit(hq)
            || !TryBuildEnemyFactory(hq))
        {
            return false;
        }

        enemyFactoryFunds[hq] = (0f, Time.time);
        CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) paid for a factory out of its factory savings ({price:0}).");
        return true;
    }

    /// <summary>What the plant the commander wants next costs, 0 when every base is full.
    /// This is what the build reserve saves for.</summary>
    private float GetEnemyFactoryReserve(FactionHQ hq)
    {
        UnitDefinition? production = TryPickEnemyFactory(hq, out _, out List<UnitDefinition>? options)
            ? ChooseEnemyProduction(hq, options!)
            : null;
        return production == null ? 0f : GetFactoryBuildCost(production);
    }

    /// <summary>Queues the next plant at the base that wants one.</summary>
    private bool TryBuildEnemyFactory(FactionHQ hq)
    {
        if (!TryPickEnemyFactory(hq, out GlobalPosition anchor, out List<UnitDefinition>? options))
        {
            return false;
        }

        UnitDefinition? production = ChooseEnemyProduction(hq, options!);
        BuildingDefinition? plant = production == null ? null : ResolveFactoryDefinition(production);
        return production != null
            && plant != null
            && TryFindSiteNear(hq, plant, anchor, out GlobalPosition site)
            && QueueEnemyBlueprint(
                hq,
                CommanderBuildKind.Factory,
                plant,
                production,
                site,
                GetFactoryBuildCost(production),
                $"{production.unitName} factory");
    }

    /// <summary>
    /// The first base with room for a plant, and the kind it has fewest of, so a base gets air,
    /// ground, naval, air, ground... rather than five ground plants before its first hangar line.
    /// </summary>
    private bool TryPickEnemyFactory(FactionHQ hq, out GlobalPosition anchor, out List<UnitDefinition>? options)
    {
        anchor = default;
        options = null;
        EnemyFactoryOptions kinds = GetEnemyFactoryOptions(hq);
        CollectEnemyFactorySites(hq);
        factoryBaseScratch.Clear();
        int factoryBases = 0;
        int bases = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!CommanderGameAccess.IsLandBase(airbase))
            {
                continue;
            }

            bases++;
            GlobalPosition center = airbase.center.GlobalPosition();
            int ground = 0;
            int air = 0;
            int naval = 0;
            for (int i = 0; i < enemyFactorySites.Count; i++)
            {
                Vector3 offset = enemyFactorySites[i].Position - center;
                offset.y = 0f;
                if (offset.sqrMagnitude > FactoryCoverageMeters * FactoryCoverageMeters)
                {
                    continue;
                }

                switch (enemyFactorySites[i].Product)
                {
                    case AircraftDefinition:
                        air++;
                        break;
                    case ShipDefinition:
                        naval++;
                        break;
                    default:
                        ground++;
                        break;
                }
            }

            factoryBases += ground + air + naval > 0 ? 1 : 0;
            if (!CommanderCaptureService.IsBeingCaptured(airbase))
            {
                factoryBaseScratch.Add((center, ground, air, naval));
            }
        }

        // Plants on a few bases, not every one: a base with none may only open while fewer than
        // MaxFactoryBases have any, and the next plant goes to the base with the fewest, so the few
        // spread their lines between them.
        int maxFactoryBases = Mathf.Clamp((bases + 1) / 2, 1, MaxFactoryBases);

        // GOD: the opening plants are all aircraft lines, the ground and the fleet come after.
        bool airOnly = IsGodEconomy(hq) && kinds.Air.Count > 0 && CountFactories(hq) < GodEnemyFactoryTarget;
        int fewestPlants = int.MaxValue;
        foreach ((GlobalPosition center, int ground, int air, int naval) in factoryBaseScratch)
        {
            int plants = ground + air + naval;
            if ((plants == 0 && factoryBases >= maxFactoryBases) || plants >= fewestPlants)
            {
                continue;
            }

            // Air first on a tie, so a base's first plant is an aircraft line. The wing is what needs the
            // plants; the depots already field ground units.
            List<UnitDefinition>? baseOptions = null;
            int fewest = int.MaxValue;
            ConsiderFactoryKind(kinds.Air, air, CommanderUnitCap.FactoriesPerBase(EnemyAirFactoriesPerBase), ref baseOptions, ref fewest);

            // Ground plants wait with the army, for a base near enough to attack from.
            if (!airOnly && CommanderCaptureService.HasForwardBase(hq))
            {
                ConsiderFactoryKind(
                    kinds.Ground, ground, CommanderUnitCap.FactoriesPerBase(EnemyGroundFactoriesPerBase), ref baseOptions, ref fewest);
            }

            if (!airOnly)
            {
                ConsiderFactoryKind(
                    kinds.Naval, naval, CommanderUnitCap.FactoriesPerBase(EnemyNavalFactoriesPerBase), ref baseOptions, ref fewest);
            }

            if (baseOptions != null)
            {
                options = baseOptions;
                anchor = center;
                fewestPlants = plants;
            }
        }

        return options != null;
    }

    /// <summary>What each kind of plant may make right now: the faction's ground roster, the
    /// airframes a held field can launch, the hulls the dock's level unlocks.</summary>
    private EnemyFactoryOptions GetEnemyFactoryOptions(FactionHQ hq)
    {
        if (!enemyFactoryOptions.TryGetValue(hq, out EnemyFactoryOptions kinds))
        {
            kinds = new EnemyFactoryOptions();
            enemyFactoryOptions[hq] = kinds;
        }

        if (Time.time < kinds.RefreshAt)
        {
            return kinds;
        }

        kinds.RefreshAt = Time.time + FactoryOptionsRefreshSeconds;
        CommanderGameAccess.CollectFactionVehicleDefinitions(FactoryVehicleScratch, hq);
        kinds.Ground.Clear();
        for (int i = 0; i < FactoryVehicleScratch.Count; i++)
        {
            kinds.Ground.Add(FactoryVehicleScratch[i]);
        }

        kinds.Air.Clear();
        CommanderEnemyCommanderService.CollectFactoryAirframes(hq, kinds.Air);
        kinds.Naval.Clear();
        CommanderEnemyCommanderService.CollectFactoryShips(hq, kinds.Naval);
        return kinds;
    }

    /// <summary>Every producing plant this faction has, built or still a blueprint.</summary>
    private void CollectEnemyFactorySites(FactionHQ hq)
    {
        enemyFactorySites.Clear();
        enemyFactoryProducts.Clear();
        Factory[] factories = GetFactories();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            Unit? attached = factory == null ? null : factory.attachedUnit;
            if (attached != null && !attached.disabled && attached.NetworkHQ == hq && factory!.ProductionUnit != null)
            {
                enemyFactorySites.Add((attached.GlobalPosition(), factory.ProductionUnit));
                enemyFactoryProducts.Add(factory.ProductionUnit);
            }
        }

        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite site = constructionSites[i];
            if (ReferenceEquals(site.Hq, hq) && site.Kind == CommanderBuildKind.Factory && site.Production != null)
            {
                enemyFactorySites.Add((site.Site, site.Production));
                enemyFactoryProducts.Add(site.Production);
            }
        }
    }

    /// <summary>
    /// The cheapest plant none of its plants makes yet, else the dearest plant: the commander climbs
    /// the ladder one new line at a time (Cricket, Brawler, Compass...) and, once it has every line,
    /// adds the best one. Chosen without looking at the balance, and the reserve saves for exactly
    /// this plant: picking the dearest *affordable* one while the reserve saved for the cheapest
    /// meant funds never rose past the cheapest, since the unit spender takes the rest every review,
    /// so nearly every plant was a Cricket line.
    /// </summary>
    private UnitDefinition? ChooseEnemyProduction(FactionHQ hq, List<UnitDefinition> options)
    {
        // GOD: price is no object, so the best new line first rather than the cheapest.
        bool god = IsGodEconomy(hq);
        UnitDefinition? fresh = null;
        UnitDefinition? dearest = null;
        for (int i = 0; i < options.Count; i++)
        {
            UnitDefinition candidate = options[i];

            // The Cricket is early game only, so its line is the last resort, never the first rung.
            if (IsCricket(candidate) && options.Count > 1)
            {
                continue;
            }

            float cost = GetFactoryBuildCost(candidate);
            if (dearest == null || cost > GetFactoryBuildCost(dearest))
            {
                dearest = candidate;
            }

            if (!enemyFactoryProducts.Contains(candidate)
                && (fresh == null || (god ? cost > GetFactoryBuildCost(fresh) : cost < GetFactoryBuildCost(fresh))))
            {
                fresh = candidate;
            }
        }

        return fresh ?? dearest;
    }

    /// <summary>What each kind of plant may make for one faction, and when to ask again.</summary>
    private sealed class EnemyFactoryOptions
    {
        internal List<UnitDefinition> Ground { get; } = new();

        internal List<UnitDefinition> Air { get; } = new();

        internal List<UnitDefinition> Naval { get; } = new();

        internal float RefreshAt { get; set; }
    }
}
