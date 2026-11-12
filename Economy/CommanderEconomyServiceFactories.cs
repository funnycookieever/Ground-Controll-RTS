using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>What a factory may be told to make: the ground roster, every aircraft and every ship.</summary>
internal sealed partial class CommanderEconomyService
{
    private static readonly List<VehicleDefinition> FactoryVehicleScratch = new();

    private static readonly Comparer<UnitDefinition> AircraftProductOrder = Comparer<UnitDefinition>.Create(CompareAircraftProducts);

    /// <summary>
    /// What a factory may make: ground vehicles under the depot's faction purchase rule, then every aircraft the
    /// encyclopedia holds, modded ones included, helicopters before aeroplanes and cheapest first,
    /// then every ship the NAVAL window sells. Aircraft and ships are not filtered by faction, the
    /// same as the AIR window with no hangar picked and the NAVAL window.
    /// Only a definition in <c>Encyclopedia.Lookup</c> is offered, because that is the table
    /// <c>Factory.SetFactory</c> resolves the product key in; anything else would build a plant
    /// that silently never produces.
    /// </summary>
    internal static void CollectFactoryProducts(List<UnitDefinition> into, FactionHQ? hq)
    {
        into.Clear();
        if (hq?.faction == null || Encyclopedia.Lookup == null)
        {
            return;
        }

        CommanderGameAccess.TryGetLocalVehicleDefinitions(FactoryVehicleScratch);
        for (int i = 0; i < FactoryVehicleScratch.Count; i++)
        {
            VehicleDefinition definition = FactoryVehicleScratch[i];
            if (!string.IsNullOrEmpty(definition.jsonKey)
                && Encyclopedia.Lookup.TryGetValue(definition.jsonKey, out UnitDefinition listed)
                && ReferenceEquals(listed, definition)
                && !CommanderUnitBanService.IsBanned(definition)
                && CommanderFactionVehicleService.CanPurchase(definition, hq))
            {
                into.Add(definition);
            }
        }

        List<AircraftDefinition>? aircraft = Encyclopedia.i?.aircraft;
        if (aircraft == null)
        {
            return;
        }

        int firstAircraft = into.Count;
        for (int i = 0; i < aircraft.Count; i++)
        {
            AircraftDefinition definition = aircraft[i];
            if (definition != null
                && definition.unitPrefab != null
                && definition.aircraftParameters != null
                && !string.IsNullOrEmpty(definition.jsonKey)
                && Encyclopedia.Lookup.TryGetValue(definition.jsonKey, out UnitDefinition listed)
                && ReferenceEquals(listed, definition)
                && !CommanderUnitBanService.IsBanned(definition)
                && !into.Contains(definition))
            {
                into.Add(definition);
            }
        }

        into.Sort(firstAircraft, into.Count - firstAircraft, AircraftProductOrder);

        // The NAVAL window's own list (already allowed and sorted cheapest first), so a ship plant
        // offers exactly the hulls the dock can launch.
        IReadOnlyList<ShipDefinition>? ships = CommanderNavalPurchaseService.Instance?.ShipDefinitions;
        for (int i = 0; ships != null && i < ships.Count; i++)
        {
            ShipDefinition ship = ships[i];
            if (!string.IsNullOrEmpty(ship.jsonKey)
                && Encyclopedia.Lookup.TryGetValue(ship.jsonKey, out UnitDefinition listed)
                && ReferenceEquals(listed, ship)
                && !CommanderUnitBanService.IsBanned(ship)
                && !into.Contains(ship))
            {
                into.Add(ship);
            }
        }
    }

    /// <summary>The PRODUCES list's section for a product: ground, air (helicopters and aeroplanes) or naval.</summary>
    internal static string GetProductCategory(UnitDefinition? product)
    {
        return product switch
        {
            AircraftDefinition => "AIRCRAFT",
            ShipDefinition => "NAVAL",
            _ => "GROUND VEHICLES",
        };
    }

    private static int CompareAircraftProducts(UnitDefinition a, UnitDefinition b)
    {
        bool aRotary = CommanderAirCommandService.IsRotaryAirframe((AircraftDefinition)a);
        bool bRotary = CommanderAirCommandService.IsRotaryAirframe((AircraftDefinition)b);
        return aRotary != bRotary ? (aRotary ? -1 : 1) : a.value.CompareTo(b.value);
    }
}
