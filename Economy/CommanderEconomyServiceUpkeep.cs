using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Defence and artillery cost money to hold. Every armed building, air-defence vehicle and
/// artillery piece (the depot's Artillery filter) pays <c>DefenceUpkeepPercentPerMinute</c> of its price in upkeep, and every armed
/// building restocks what it fired from the nearest store of its base, which the owner then has
/// to buy back. A defender forced to fire a thousand interceptors is losing money.
/// </summary>
/// <remarks>
/// The base game only rearms a building from a <c>Rearmer</c> inside that rearmer's own
/// serialized <c>Range</c> (<c>RearmMissionController.TryGetRearmer</c>), so an AA post standing
/// away from the bunker either ran dry for good or, beside one, drew on it for free as far as the
/// commander could tell. This widens that to the same <c>SupplyStoreRangeKm</c> every other draw
/// in the mod uses and goes through <c>ProcessRearmRequest</c>, the game's own draw, so the shelf,
/// its network write and the shortfall alert are all stock. Vehicles are left to the base game's
/// rearm path, which already draws on the shelves. Every mission with the RTS economy on.
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>The upkeep sheet's lines, in the order <see cref="GetUpkeepLine"/> numbers them.</summary>
    internal static readonly string[] UpkeepLineNames = { "Defence structures", "Air defence vehicles", "Artillery" };

    private readonly List<Unit> upkeepStores = new();

    // The local faction's ledger for the readouts, rebuilt once a second rather than walking every
    // unit the faction owns on every OnGUI pass.
    private readonly int[] ledgerUpkeepCount = new int[3];
    private readonly float[] ledgerUpkeepCost = new float[3];
    private readonly float[] ledgerUpkeepValue = new float[3];
    private readonly int[] ledgerIncomeCount = new int[IncomeKinds.Count];
    private readonly int[] ledgerIncomeLevels = new int[IncomeKinds.Count];
    private readonly float[] ledgerIncome = new float[IncomeKinds.Count];
    private float nextLedgerAt;
    private float ledgerGross;
    private float ledgerUpkeep;

    /// <summary>What the local faction actually gains a minute: income less defence upkeep.</summary>
    internal float FriendlyNetIncomePerMinute
    {
        get
        {
            RefreshLedger();
            return ledgerGross - ledgerUpkeep;
        }
    }

    internal float FriendlyGrossIncomePerMinute
    {
        get
        {
            RefreshLedger();
            return ledgerGross;
        }
    }

    internal float FriendlyUpkeepPerMinute
    {
        get
        {
            RefreshLedger();
            return ledgerUpkeep;
        }
    }

    /// <summary>The single piece costing the most to hold, so the sheet can name it; empty when none.</summary>
    internal string LedgerDearestLabel { get; private set; } = string.Empty;

    internal float LedgerDearestUpkeep { get; private set; }

    /// <summary>A rate with its sign, "+12" or "-3": the net figure goes negative once defence outgrows income.</summary>
    internal static string SignedFundsLabel(float amount)
    {
        return (amount < 0f ? "-" : "+") + FundsLabel(Mathf.Abs(amount));
    }

    /// <summary>0 an armed building, 1 an air-defence vehicle, 2 artillery. Only for a held defence.</summary>
    internal static int GetUpkeepLine(Unit unit)
    {
        if (unit is Building)
        {
            return 0;
        }

        return unit.definition is VehicleDefinition vehicle
            && vehicle.vehicleType is VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM
            ? 1
            : 2;
    }

    /// <summary>An armed building, an air-defence vehicle, or anything the depot files under Artillery.</summary>
    internal static bool IsHeldDefence(Unit unit)
    {
        if (unit is Building)
        {
            return unit.weaponStations.Count > 0;
        }

        return unit is GroundVehicle
            && unit.definition is VehicleDefinition vehicle
            && (vehicle.vehicleType is VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM
                || CommanderGameAccess.GetVehicleCategoryLabel(vehicle) == "Artillery");
    }

    internal static float GetDefencePrice(Unit unit)
    {
        return unit.definition is BuildingDefinition building
            ? GetStructureCost(building)
            : unit.definition != null ? unit.definition.value : 0f;
    }

    /// <summary>What a whole defence costs per minute to hold, for the selection bar.</summary>
    internal static float GetUpkeepPerMinute(Unit unit)
    {
        return CommanderFeatureGate.AdvancedFeaturesEnabled && IsHeldDefence(unit)
            ? GetDefencePrice(unit) * Mathf.Max(0f, CommanderSettings.DefenceUpkeepPercentPerMinute) / 100f
            : 0f;
    }

    internal int GetLedgerUpkeepCount(int line)
    {
        return ledgerUpkeepCount[line];
    }

    internal float GetLedgerUpkeepCost(int line)
    {
        return ledgerUpkeepCost[line];
    }

    /// <summary>What the line's pieces cost to buy, which is what its upkeep is a share of.</summary>
    internal float GetLedgerUpkeepValue(int line)
    {
        return ledgerUpkeepValue[line];
    }

    internal int GetLedgerIncomeCount(int kindIndex)
    {
        return ledgerIncomeCount[kindIndex];
    }

    internal int GetLedgerIncomeLevels(int kindIndex)
    {
        return ledgerIncomeLevels[kindIndex];
    }

    internal float GetLedgerIncome(int kindIndex)
    {
        return ledgerIncome[kindIndex];
    }

    private void RefreshLedger()
    {
        if (!CommanderScheduler.IsDueRealtime(ref nextLedgerAt, 1f))
        {
            return;
        }

        System.Array.Clear(ledgerUpkeepCount, 0, ledgerUpkeepCount.Length);
        System.Array.Clear(ledgerUpkeepCost, 0, ledgerUpkeepCost.Length);
        System.Array.Clear(ledgerUpkeepValue, 0, ledgerUpkeepValue.Length);
        System.Array.Clear(ledgerIncomeCount, 0, ledgerIncomeCount.Length);
        System.Array.Clear(ledgerIncomeLevels, 0, ledgerIncomeLevels.Length);
        System.Array.Clear(ledgerIncome, 0, ledgerIncome.Length);
        ledgerGross = GetBaseIncomePerMinute();
        ledgerUpkeep = 0f;
        LedgerDearestUpkeep = 0f;
        LedgerDearestLabel = string.Empty;

        for (int i = 0; i < friendlyMines.Count; i++)
        {
            Unit building = friendlyMines[i];
            CommanderBuildKind kind = GetIncomeKind(building);
            int level = GetIncomeLevel(building);
            float pay = GetIncomePerMinute(kind, level);
            ledgerGross += pay;
            for (int k = 0; k < IncomeKinds.Count; k++)
            {
                if (IncomeKinds[k] == kind)
                {
                    ledgerIncomeCount[k]++;
                    ledgerIncomeLevels[k] += level;
                    ledgerIncome[k] += pay;
                    break;
                }
            }
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit.disabled)
            {
                continue;
            }

            float upkeep = GetUpkeepPerMinute(unit);
            if (upkeep <= 0f)
            {
                continue;
            }

            int line = GetUpkeepLine(unit);
            ledgerUpkeepCount[line]++;
            ledgerUpkeepCost[line] += upkeep;
            ledgerUpkeepValue[line] += GetDefencePrice(unit);
            ledgerUpkeep += upkeep;
            if (upkeep > LedgerDearestUpkeep)
            {
                LedgerDearestUpkeep = upkeep;
                LedgerDearestLabel = CommanderGameAccess.GetUnitLabel(unit);
            }
        }
    }

    /// <summary>Upkeep and restock, both on the income tick. Host only, every faction it runs.</summary>
    private void PayDefenceUpkeep()
    {
        if (!CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            return;
        }

        float rate = Mathf.Max(0f, CommanderSettings.DefenceUpkeepPercentPerMinute) / 100f * (IncomeIntervalSeconds / 60f);
        float rangeMeters = CommanderSettings.SupplyStoreRangeKm * 1000f;
        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer || hq.factionUnits == null)
            {
                continue;
            }

            float upkeep = 0f;
            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit) || unit.disabled || !IsHeldDefence(unit))
                {
                    continue;
                }

                upkeep += GetDefencePrice(unit) * rate;
                if (unit is Building && stores != null)
                {
                    RestockFromBase(hq, unit, stores, rangeMeters);
                }
            }

            // A faction that cannot pay pays what it has: going negative would lock every purchase.
            upkeep = Mathf.Min(upkeep, Mathf.Max(0f, hq.factionFunds));
            if (upkeep > 0f)
            {
                hq.AddFunds(-upkeep);
            }
        }
    }

    private void RestockFromBase(FactionHQ hq, Unit building, CommanderSupplyStoreService stores, float rangeMeters)
    {
        if (CommanderSupplyStoreService.GetMissingAmmoKg(building) <= 0f)
        {
            return;
        }

        // ProcessRearmRequest reads WeaponInfo on every station with no null check.
        for (int i = 0; i < building.weaponStations.Count; i++)
        {
            if (building.weaponStations[i]?.WeaponInfo == null)
            {
                return;
            }
        }

        stores.CollectNearbyStores(hq, building.GlobalPosition(), rangeMeters, upkeepStores);
        for (int i = 0; i < upkeepStores.Count; i++)
        {
            Rearmer? rearmer = CommanderSupplyStoreService.GetRearmer(upkeepStores[i]);
            if (upkeepStores[i] != building && rearmer != null && rearmer.Capacity > 0f)
            {
                rearmer.ProcessRearmRequest(building, out _);
                break;
            }
        }

        upkeepStores.Clear();
    }
}
