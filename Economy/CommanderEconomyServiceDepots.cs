using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GroundControlRts;

/// <summary>
/// Vehicle depot levels and the ground vehicle price list. Every depot, mission-authored or built,
/// is level 1; upgrades take it to 2 and then 3, and <see cref="GetRequiredDepotLevel"/> says which
/// level sells what. The gate is on the two commander purchases, the player's depot queue and
/// <c>CommanderEnemyCommanderService.Choose</c>, never on the base game's own reserve deployment,
/// so a stock mission's authored reserves still roll out.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    internal const int DepotMaxLevel = 3;

    /// <summary>Two depots this close are at the same base, for the enemy's upgrade order.</summary>
    internal const float EnemyDepotBaseMeters = 3000f;

    /// <summary>The mod's own price per unit name, before the player's profile: the PRICES tab's DEFAULT column.</summary>
    internal static readonly Dictionary<string, float> ModPrices = new(StringComparer.Ordinal);

    /// <summary>The depot tier each ground vehicle has without the player's profile.</summary>
    internal static readonly Dictionary<string, int> ModTiers = new(StringComparer.Ordinal);

    /// <summary>Every definition the last price pass saw, for the PRICES tab's list.</summary>
    internal static readonly List<UnitDefinition> PricedUnits = new();

    /// <summary>
    /// The profile's aircraft prices. Kept off <c>definition.value</c> because pilot mode's own
    /// purchase, sale and menus read that, and a commander's price edit must not reach a pilot.
    /// </summary>
    private static readonly Dictionary<UnitDefinition, float> CommanderAircraftPrices = new();

    /// <summary>
    /// Each definition's encyclopedia value before the mod touched it. Definitions outlive the scene,
    /// so every reprice starts from this and never from the live value, or the AFV +1 would stack on
    /// every mission load. The old "dear units need level 2" rule reads it too, so a vehicle this
    /// balance pass repriced without placing it in a tier keeps the tier it had.
    /// </summary>
    private static readonly Dictionary<UnitDefinition, float> AuthoredValues = new();

    /// <summary>Depot level per definition, for the vehicles the balance pass placed by hand.</summary>
    private static readonly Dictionary<VehicleDefinition, int> TierOverrides = new();

    /// <summary>Cleared on every scene change, so a mission load re-applies (and re-logs) them.</summary>
    private static bool pricesApplied;

    /// <summary>A profile edit re-runs the pass without writing every price to the log again.</summary>
    private static bool pricesLogged;

    /// <summary>Upgraded depots only; anything missing is level 1.</summary>
    private readonly Dictionary<Unit, int> depotLevels = new();

    /// <summary>Level 1 to 2. The enemy only ever saves for this one.</summary>
    internal static float DepotUpgradeCost => Mathf.Max(0f, CommanderSettings.DepotUpgradeCost);

    /// <summary>The economy profile changed: the next frame's pass writes the new prices.</summary>
    internal static void InvalidatePrices()
    {
        pricesApplied = false;
    }

    /// <summary>The CI-22 Cricket, the enemy commander's early-game airframe only.</summary>
    internal static bool IsCricket(UnitDefinition definition)
    {
        return definition.unitName == "CI-22 Cricket";
    }

    /// <summary>What a commander pays for a unit: the profile's aircraft price, else the definition's value.</summary>
    internal static float PriceOf(UnitDefinition definition)
    {
        return CommanderAircraftPrices.TryGetValue(definition, out float price) ? price : definition.value;
    }

    /// <summary>What taking a depot from <paramref name="level"/> to the next one costs.</summary>
    internal static float GetDepotUpgradeCost(int level)
    {
        return level >= 2 ? Mathf.Max(0f, CommanderSettings.DepotLevel3UpgradeCost) : DepotUpgradeCost;
    }

    internal static int GetDepotLevel(Unit? depot)
    {
        return depot != null && Instance != null && Instance.depotLevels.TryGetValue(depot, out int level) ? level : 1;
    }

    /// <summary>The highest level any live depot of this faction has reached, 1 with none upgraded.</summary>
    internal static int GetBestDepotLevel(FactionHQ? hq)
    {
        int best = 1;
        if (Instance == null || hq == null)
        {
            return best;
        }

        foreach (KeyValuePair<Unit, int> entry in Instance.depotLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq && entry.Value > best)
            {
                best = entry.Value;
            }
        }

        return best;
    }

    internal static int GetRequiredDepotLevel(VehicleDefinition definition)
    {
        if (TierOverrides.TryGetValue(definition, out int tier))
        {
            return tier;
        }

        float value = AuthoredValues.TryGetValue(definition, out float authored) ? authored : definition.value;
        return definition.vehicleType == VehicleType.ART || value >= CommanderSettings.DepotLevel2MinValue ? 2 : 1;
    }

    internal static bool NeedsUpgradedDepot(VehicleDefinition definition)
    {
        return GetRequiredDepotLevel(definition) >= 2;
    }

    /// <summary>
    /// The ground price list, written over the encyclopedia value so the depot list, the charge, the
    /// factory prices and the enemy's budget all agree. Code rather than config on purpose, the same
    /// call as the income table: it is one balance pass, and a config copy would keep stale prices.
    /// Keyed on the in-game unit names; the log line says whether each one was found.
    /// </summary>
    internal static void EnsureVehiclePrices()
    {
        if (pricesApplied)
        {
            return;
        }

        VehicleDefinition[] definitions = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
        if (definitions.Length == 0)
        {
            return;
        }

        AircraftDefinition[] aircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        ShipDefinition[] ships = Resources.FindObjectsOfTypeAll<ShipDefinition>();
        TierOverrides.Clear();
        foreach (VehicleDefinition definition in definitions)
        {
            if (definition == null)
            {
                continue;
            }

            // Everything under the depot's AFV filter costs 1 more.
            definition.value = Authored(definition)
                + (CommanderGameAccess.GetVehicleCategoryLabel(definition) == "AFV" ? 1f : 0f);

            if (definition.vehicleType == VehicleType.RDR && !CommanderGameAccess.IsTrailerVehicleDefinition(definition))
            {
                // HLT Radar Truck and MSV Radar.
                SetPrice(definition, 100f, 2);
            }
            else if (IsSupplyTruck(definition))
            {
                SetPrice(definition, 20f, 2);
            }
        }

        // Aircraft and ships start from their authored value too, or a price the player has since
        // taken out of their profile would stay on the definition for the rest of the session.
        ResetToAuthored(aircraft);
        ResetToAuthored(ships);

        // Unit names are the game's own, read out of the part table in resources.assets.
        // UNITS.md at the repo root says what each one is and what it costs.
        Reprice(definitions, "HLT Mobile Artillery", CommanderSettings.MobileArtilleryCost, 0);
        Reprice(definitions, "MSV MLRS", 250f, 3);
        Reprice(definitions, "FGA-57 Anvil", 75f, 2);
        Reprice(definitions, "Spearhead MBT", 25f, 2);
        Reprice(definitions, "Type-12 MBT", 10f, 2);
        Reprice(definitions, "T9K41 Boltstrike", 300f, 2);
        Reprice(definitions, "AFV6 AA", 7.5f, 0);
        Reprice(definitions, "AFV8 Mobile Air Defense", 15f, 0);
        Reprice(definitions, "LCV25 AA", 1.5f, 0);
        Reprice(definitions, "M12 Jackknife", 20f, 0);
        Reprice(definitions, "AeroSentry SPAAG", 100f, 3);
        Reprice(definitions, "MSV LADS", 200f, 0);
        Reprice(definitions, "MSV CRAM", 100f, 0);
        Reprice(definitions, "MSV Ballistic Missile Launcher", 250f, 2);
        Reprice(definitions, "MSV Nuclear Ballistic Missile Launcher", 500f, 3);
        Reprice(definitions, "MSV R9 Stratolance Launcher", 700f, 3);
        Reprice(definitions, "MSV Fire Control", 30f, 3);
        Reprice(definitions, "HLT Fire Control", 30f, 3);
        Reprice(definitions, "Boltstrike RAM45 Launcher", 175f, 2);
        Reprice(definitions, "StratoLance R9 Launcher", 600f, 3);
        Reprice(definitions, "HLT-CRAM", 80f, 2);
        Reprice(definitions, "HLT-HEL", 150f, 2);

        // Aircraft have no depot tier; the price is all there is.
        Reprice(aircraft, "CI-22 Cricket", 7.5f, 0);
        Reprice(aircraft, "T/A-30 Compass", 30f, 0);
        Reprice(aircraft, "VT-7 Vagrant", 40f, 0);
        Reprice(aircraft, "A-19 Brawler", 32f, 0);
        Reprice(aircraft, "FS-20 Vortex", 72.5f, 0);
        Reprice(aircraft, "KR-67 Ifrit", 90f, 0);
        Reprice(aircraft, "EW-25 Medusa", 85f, 0);
        Reprice(aircraft, "Alkyon AB-4", 275f, 0);
        Reprice(aircraft, "VL-49 Tarantula", 90f, 0);

        // Ships are gated by dock level, not depot tier.
        Reprice(ships, "Surf Class Patrol Boat", 50f, 0);
        Reprice(ships, "Shard Class Corvette", 625f, 0);
        Reprice(ships, "Argus Class Frigate", 1000f, 0);
        Reprice(ships, "Cursor Class LFD", 1050f, 0);
        Reprice(ships, "Dynamo Class Destroyer", 1600f, 0);
        Reprice(ships, "Annex Class Carrier", 3750f, 0);
        Reprice(ships, "Hyperion Class Carrier", 3750f, 0);

        // The player's profile goes on last, over the mod's prices, and covers whatever the game
        // has loaded, modded aircraft included: it is keyed on the same unit names.
        ModPrices.Clear();
        ModTiers.Clear();
        PricedUnits.Clear();
        CommanderAircraftPrices.Clear();
        ApplyProfile(definitions);
        ApplyProfile(aircraft);
        ApplyProfile(ships);
        int changes = CommanderPriceProfile.ChangeCount(CommanderPriceProfile.Active);
        if (changes > 0)
        {
            string profile = CommanderNetwork.IsGuest ? CommanderPriceProfile.HostProfileName : CommanderPriceProfile.ProfileName;
            CommanderPlugin.Log.LogInfo($"Economy profile '{profile}' applied: {changes} changes.");
        }

        pricesApplied = true;
        pricesLogged = true;
    }

    internal void UpgradeDepot(Unit depot)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (GetDepotLevel(depot) >= DepotMaxLevel || hq == null || depot.NetworkHQ != hq)
        {
            return;
        }

        // Depot levels are the host's to keep: a guest asks, and the level comes back in the sync.
        if (CommanderNetwork.Forward("depot.up", CommanderNetwork.Id(depot)))
        {
            return;
        }

        int level = GetDepotLevel(depot) + 1;
        float cost = GetDepotUpgradeCost(level - 1);
        if (hq.factionFunds < cost)
        {
            CommanderNotice.Fail("NOT ENOUGH FUNDS FOR THE DEPOT UPGRADE");
            return;
        }

        hq.AddFunds(-cost);
        depotLevels[depot] = level;
        StatusText = $"{CommanderGameAccess.GetUnitLabel(depot)} at level {level}: tier {level} vehicles unlocked.";
    }

    private static float Authored(UnitDefinition definition)
    {
        if (!AuthoredValues.TryGetValue(definition, out float authored))
        {
            authored = definition.value;
            AuthoredValues[definition] = authored;
        }

        return authored;
    }

    private static void ResetToAuthored(UnitDefinition[] definitions)
    {
        foreach (UnitDefinition definition in definitions)
        {
            if (definition != null)
            {
                definition.value = Authored(definition);
            }
        }
    }

    /// <summary>Records the mod's price and tier per name, then writes the profile's over them.</summary>
    private static void ApplyProfile(UnitDefinition[] definitions)
    {
        foreach (UnitDefinition definition in definitions)
        {
            if (definition == null || definition.unitPrefab == null || string.IsNullOrEmpty(definition.unitName))
            {
                continue;
            }

            string name = definition.unitName;
            PricedUnits.Add(definition);
            ModPrices[name] = definition.value;
            if (definition is AircraftDefinition)
            {
                // Commanders pay a multiple of the pilot price: an airframe ordered from the map
                // costs no pilot's time or risk. Off `value`, so pilot mode keeps its own price.
                float commanderPrice = definition.value * (name == "CI-22 Cricket" ? 2.5f : 3f);
                ModPrices[name] = commanderPrice;
                CommanderAircraftPrices[definition] = commanderPrice;
            }

            if (definition is VehicleDefinition vehicle)
            {
                ModTiers[name] = GetRequiredDepotLevel(vehicle);
                if (CommanderPriceProfile.TryGetTier(name, out int tier))
                {
                    TierOverrides[vehicle] = tier;
                }
            }

            if (CommanderPriceProfile.TryGetUnitPrice(name, out float price))
            {
                if (definition is AircraftDefinition)
                {
                    CommanderAircraftPrices[definition] = price;
                }
                else
                {
                    definition.value = price;
                }
            }
        }
    }

    private static void Reprice(UnitDefinition[] definitions, string unitName, float value, int tier)
    {
        bool found = false;
        foreach (UnitDefinition definition in definitions)
        {
            if (definition != null && definition.unitName == unitName)
            {
                SetPrice(definition, value, tier);
                found = true;
            }
        }

        // The prices are constants, so only a name the game no longer has is worth a line: that is
        // a game update renaming a unit, and its override silently stops working.
        if (!found && !pricesLogged)
        {
            CommanderPlugin.Log.LogWarning($"{unitName} is not in this game version; its price override does nothing.");
        }
    }

    /// <summary>
    /// A munitions or fuel truck, either faction's. Munitions trucks carry the game's
    /// <c>Rearmer</c>; the fuel ones are matched by name because nothing else sets them apart.
    /// The builder carries a <c>Repairer</c> and is priced on its own; a trailer is not a truck.
    /// </summary>
    private static bool IsSupplyTruck(VehicleDefinition definition)
    {
        if (definition.unitPrefab == null
            || CommanderGameAccess.IsTrailerVehicleDefinition(definition)
            || definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null)
        {
            return false;
        }

        string name = definition.unitName ?? string.Empty;
        return name.IndexOf("Fuel", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Munitions", StringComparison.OrdinalIgnoreCase) >= 0
            || (definition.vehicleType == VehicleType.TRUCK
                && definition.unitPrefab.GetComponentInChildren<Rearmer>(true) != null);
    }

    /// <summary>A tier of 0 leaves the vehicle on the value rule.</summary>
    private static void SetPrice(UnitDefinition definition, float value, int tier)
    {
        definition.value = value;
        if (tier > 0 && definition is VehicleDefinition vehicle)
        {
            TierOverrides[vehicle] = tier;
        }
    }

    /// <summary>
    /// The enemy's upgrades, one depot, one level at a time. Level 2 is saved for through
    /// <see cref="GetEnemyBuildReserve"/> right after the mines, because without it the SPEARHEAD
    /// and FIRE SUPPORT plans have almost nothing to buy. Level 3 is never saved for: holding its
    /// price back would stop the convoys for most of a match, so it is bought only out of money the
    /// commander happens to have left over.
    /// </summary>
    private bool TryUpgradeEnemyDepot(FactionHQ hq)
    {
        Unit? depot = FindEnemyDepotToUpgrade(hq);
        if (depot == null)
        {
            return false;
        }

        int level = GetDepotLevel(depot) + 1;
        if (!CommanderGameAccess.TrySpend(hq, GetDepotUpgradeCost(level - 1)))
        {
            return false;
        }

        depotLevels[depot] = level;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) upgraded {CommanderGameAccess.GetUnitLabel(depot)} to level {level}.");
        return true;
    }

    /// <summary>
    /// The faction's highest-level depot while it is below the top, so one depot climbs rather than
    /// several stopping at level 2. A naval dock wears the depot prefab and is skipped.
    /// </summary>
    private Unit? FindEnemyDepotToUpgrade(FactionHQ hq)
    {
        // A base with no depot at level 2 first, so it can field its own StratoLances, Boltstrikes
        // and MBTs. Then the highest below the top, so one depot climbs rather than several stopping
        // at level 2.
        Unit? best = null;
        int bestRank = int.MinValue;
        List<Unit> owned = new();
        foreach (VehicleDepot depot in Object.FindObjectsOfType<VehicleDepot>())
        {
            Unit? owner = depot == null ? null : CommanderGameAccess.GetDepotOwner(depot);
            if (owner != null && !owner.disabled && owner.NetworkHQ == hq && !IsBuiltNavalDock(owner))
            {
                owned.Add(owner);
            }
        }

        for (int i = 0; i < owned.Count; i++)
        {
            int level = GetDepotLevel(owned[i]);
            if (level >= DepotMaxLevel)
            {
                continue;
            }

            bool baseUpgraded = false;
            for (int j = 0; j < owned.Count && !baseUpgraded; j++)
            {
                baseUpgraded = GetDepotLevel(owned[j]) >= 2
                    && FastMath.InRange(owned[j].GlobalPosition(), owned[i].GlobalPosition(), EnemyDepotBaseMeters);
            }

            int rank = (baseUpgraded ? 0 : DepotMaxLevel) + level;
            if (rank > bestRank)
            {
                bestRank = rank;
                best = owned[i];
            }
        }

        return best;
    }

    /// <summary>A base without a level-2 depot is one the next upgrade goes to and is saved for.</summary>
    private bool HasBaseWithoutUpgradedDepot(FactionHQ hq)
    {
        return FindEnemyDepotToUpgrade(hq) is Unit next && GetDepotLevel(next) < 2;
    }
}
