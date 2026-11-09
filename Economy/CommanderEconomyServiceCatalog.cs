using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The building catalogue: every structure the game ships, buildable for a price. The income
/// buildings and the factory keep their own buttons because they carry economy behaviour the mod adds on top; the
/// catalogue is everything else, and each entry does whatever its own prefab does — a radar sees, a
/// depot supplies, a hangar repairs aircraft, a bunker soaks damage.
/// </summary>
/// <remarks>
/// Prices come from <c>UnitDefinition.value</c> times a config multiplier rather than a
/// hand-written table. The game already ranks its own buildings by worth, and a table would go stale
/// the first time a patch adds a building. Retune the multiplier if the ladder feels wrong.
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>Nothing is free, however cheap the encyclopedia says a shed is.</summary>
    private const float MinimumStructureCost = 25f;

    private readonly List<BuildingDefinition> catalog = new();
    private bool catalogResolved;

    /// <summary>Every buildable structure, cheapest first inside each category.</summary>
    internal IReadOnlyList<BuildingDefinition> Catalog
    {
        get
        {
            RefreshCatalog();
            return catalog;
        }
    }

    internal BuildingDefinition? PendingStructure { get; private set; }

    /// <summary>The player's profile price if it has one, else the mod's.</summary>
    internal static float GetStructureCost(BuildingDefinition? definition)
    {
        return definition != null && CommanderPriceProfile.TryGetBuildingPrice(GetStructureLabel(definition), out float price)
            ? price
            : GetModStructureCost(definition);
    }

    internal static float GetModStructureCost(BuildingDefinition? definition)
    {
        if (definition == null)
        {
            return 0f;
        }

        // The enrichment plant is the warhead factory; it is priced for what it makes, not its value.
        Factory? factory = definition.unitPrefab == null ? null : definition.unitPrefab.GetComponentInChildren<Factory>(true);
        if (factory != null && factory.factoryType == Factory.FactoryType.Nukes)
        {
            return Mathf.Max(0f, CommanderSettings.EnrichmentPlantCost);
        }

        if (definition.unitPrefab != null && definition.unitPrefab.GetComponentInChildren<VehicleDepot>(true) != null)
        {
            return 100f;
        }

        // Hand-set prices; jsonKeys from the game's mission data. Aircraft spawn points go by unit name
        // from the part table.
        return definition.jsonKey switch
        {
            "ammunitionBunker" => 300f,
            "radarStation1" => 50f,
            "controlTower1" => 750f,
            _ => definition.unitName switch
            {
                "Helipad" => 100f,
                "Medium Aircraft Hangar" => 750f,
                "Hardened Aircraft Shelter" => 500f,
                _ => Mathf.Max(MinimumStructureCost, definition.value * Mathf.Max(0f, CommanderSettings.BuildingCostMultiplier)),
            },
        };
    }

    internal static string GetCategoryLabel(BuildingType type)
    {
        return type switch
        {
            BuildingType.CIV => "CIVILIAN",
            BuildingType.FAC => "INDUSTRY",
            BuildingType.RDR => "RADAR",
            BuildingType.DEP => "DEPOT",
            BuildingType.HGR => "HANGAR",
            BuildingType.DEF => "DEFENCE",
            BuildingType.AMMO => "AMMUNITION",
            _ => "OTHER",
        };
    }

    internal static string GetStructureLabel(BuildingDefinition definition)
    {
        return string.IsNullOrWhiteSpace(definition.unitName) ? definition.jsonKey : definition.unitName;
    }

    internal void BeginStructureBuild(BuildingDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
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

        if (hq.factionFunds < GetStructureCost(definition))
        {
            StatusText = $"Insufficient faction funds for a {GetStructureLabel(definition)}.";
            return;
        }

        PendingStructure = definition;
        PendingBuild = CommanderBuildKind.Structure;
        StatusText = $"Select the {GetStructureLabel(definition)} site in the 3D world. {CommanderSettings.RotateBuilding.MainKey} rotates it.";
    }

    /// <summary>A hall the FACTORY button can put up, whichever product it is for.</summary>
    private static bool IsFactoryPrefab(BuildingDefinition definition)
    {
        for (int i = 0; i < FactoryPrefabKeys.Length; i++)
        {
            if (string.Equals(definition.jsonKey, FactoryPrefabKeys[i], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < LightFactoryPrefabKeys.Length; i++)
        {
            if (string.Equals(definition.jsonKey, LightFactoryPrefabKeys[i], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The catalogue is the encyclopedia minus the buildings that already have their own button, so a
    /// player cannot buy the refinery prefab here and wonder why it pays no income. That is *every*
    /// factory hall, not only the one the FACTORY button happens to resolve to: the tall vertical
    /// plant was listed here, where it was a factory bought without choosing a product, and the
    /// FACTORY button now builds it for the cheaper products instead.
    /// </summary>
    private void RefreshCatalog()
    {
        if (catalogResolved)
        {
            return;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia == null || encyclopedia.buildings == null)
        {
            return;
        }

        // The income buildings are ECONOMY buttons: bought here they would be the bare prefab,
        // a fuel depot that pays nothing.
        BuildingDefinition? mine = ResolveDefinition(CommanderBuildKind.Mine);
        BuildingDefinition? fuelExchange = ResolveDefinition(CommanderBuildKind.FuelExchange);
        BuildingDefinition? gasTerminal = ResolveDefinition(CommanderBuildKind.GasTerminal);

        // The dock's prefab stays listed: on the current game it is the vehicle depot, and a depot
        // bought here is a depot, which is the only way to open a second front for ground units.
        catalog.Clear();
        for (int i = 0; i < encyclopedia.buildings.Count; i++)
        {
            BuildingDefinition candidate = encyclopedia.buildings[i];
            if (candidate == null
                || candidate.unitPrefab == null
                || !candidate.IsAllowed(includeEventContent: false)
                || ReferenceEquals(candidate, mine)
                || ReferenceEquals(candidate, fuelExchange)
                || ReferenceEquals(candidate, gasTerminal)
                || IsFactoryPrefab(candidate))
            {
                continue;
            }

            catalog.Add(candidate);
        }

        catalog.Sort((a, b) => a.buildingType != b.buildingType
            ? a.buildingType.CompareTo(b.buildingType)
            : a.value.CompareTo(b.value));
        catalogResolved = true;
    }

    /// <summary>
    /// Places a catalogue building. A prefab that happens to carry a <see cref="Factory"/> component
    /// is wired up like a bought factory rather than left inert, because an idle factory building is
    /// exactly the kind of silent dud that reads as a broken mod.
    /// </summary>
    private Unit? PlaceStructure(
        FactionHQ hq,
        GlobalPosition position,
        BuildingDefinition definition,
        UnitDefinition? production,
        Quaternion? facing = null)
    {
        Unit? unit = SpawnBuilding(hq, position, definition, GetStructureLabel(definition), facing: facing);
        if (unit == null)
        {
            return null;
        }

        CommanderSupplyStoreService.Instance?.EmptyNewStore(unit);

        if (unit.TryGetComponent(out Factory builtFactory)
            && builtFactory.factoryType == Factory.FactoryType.Nukes)
        {
            // The nuclear enrichment plant. Its own prefab says which kind of plant it is, so ask
            // that rather than the FACTORY button: handing it the button's selected vehicle is what
            // had an enrichment plant turning out main battle tanks
            // (CommanderEconomyServiceEnrichment.cs). No factory level either - the warhead line has
            // no batch size to upgrade.
            builtFactory.SetFactory(NuclearWarheadProduction, WarheadProductionSeconds);
            StatusText = $"{GetStructureLabel(definition)} built, enriching one warhead every "
                + $"{WarheadProductionSeconds / 60f:0.#} min.";
            ReportWarheadStorage(hq);
        }
        else if (builtFactory != null && production != null)
        {
            builtFactory.SetFactory(production.jsonKey, FactoryProductionSeconds);
            factoryLevels[unit] = 1;
            StatusText = $"{GetStructureLabel(definition)} built, producing "
                + $"{CommanderGameAccess.GetVehicleLabel(production)}.";
        }
        else
        {
            StatusText = $"{GetStructureLabel(definition)} built.";
        }

        return unit;
    }
}
