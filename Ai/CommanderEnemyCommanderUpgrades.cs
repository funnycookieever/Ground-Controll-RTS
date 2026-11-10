using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Under a unit cap a slot is worth more than the vehicle in it: once the ground share is full, a
/// commander that can afford better air defence scraps its weakest launcher (the LCV25 AA, usually)
/// and buys the better one into the reserve, which the game deploys into the freed slot. And a
/// builder that has stood idle for ten minutes is scrapped, so the slot goes to something that fights.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>A replacement must be worth this many times the launcher it retires, so two
    /// launchers of nearly the same grade never trade places review after review.</summary>
    private const float AirDefenceUpgradeValueRatio = 2f;

    /// <summary>A base launcher is outclassed by one on the roster costing up to this many times
    /// more: the AFV6 AA (7.5) outclasses the LCV25 AA (1.5), while the 75-100 gun mounts are a
    /// different purchase and leave the AFV6 alone.</summary>
    private const float OutclassedAirDefenceRatio = 6f;

    /// <summary>Scaled seconds a builder may stand with no blueprint and nothing to repair before a
    /// capped commander scraps it.</summary>
    private const float IdleBuilderScrapSeconds = 600f;

    /// <summary>Uncapped, air defence stops at this share of the fighting vehicles (escorts included),
    /// above the 30% <see cref="AirDefenceShareOfForce"/> that forces a launcher, so the two never fight.</summary>
    private const float MaxAirDefenceShareOfForce = 0.35f;

    /// <summary>Air defence a commander may always field, however small its army.</summary>
    private const int MinAirDefenceVehicles = 3;

    /// <summary>Air defence a commander may always field per base it holds, whatever the share says.
    /// The share alone left NORMAL Primeva, seven Champions bases, about one AA vehicle a base: every
    /// ring wanted nine and the buy stopped at 35% of a 25-vehicle army.</summary>
    private const int MinAirDefencePerBase = 2;

    /// <summary>When each AI repair vehicle last had a job, by unit. Pruned as they die.</summary>
    private readonly Dictionary<Unit, float> builderBusyAt = new();

    private readonly List<Unit> builderScratch = new();

    /// <summary>Bases the reviewing commander holds, read with the owned vehicles.</summary>
    private int ownedBases;

    /// <summary>The reviewing commander is still short of a LADS and a CRAM per base.</summary>
    private bool ringGunsFirst;

    /// <summary>Base air defence a swap may touch: not convoy escorts, and not a radar SAM, which needs
    /// a fire control truck beside it. Checks the type as well as the prefab.</summary>
    private static bool IsRingAirDefence(VehicleDefinition definition)
    {
        return IsAirDefence(definition)
            && definition.vehicleType != VehicleType.R_SAM
            && !IsConvoyAirDefence(definition)
            && !CommanderRadarService.IsRadarGuidedLauncherDefinition(definition);
    }

    /// <summary>
    /// A base launcher the plan roll skips because the roster has a better one. Air defence is weighed
    /// by type, so without this the cheapest launcher in a class gets bought most.
    /// Only one the budget can pay for counts.
    /// </summary>
    private bool IsOutclassedAirDefence(VehicleDefinition definition, float budget)
    {
        // The forced buy's roll keeps the same order as the fund: LADS and CRAM first.
        if (ringGunsFirst && definition.unitName == BoltstrikeName)
        {
            return true;
        }

        if (!IsRingAirDefence(definition))
        {
            return false;
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            float value = catalog[i].value;
            if (value > definition.value
                && value <= definition.value * OutclassedAirDefenceRatio
                && value <= budget
                && IsRingAirDefence(catalog[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Under a unit cap an AI side stops buying air defence at <see cref="CommanderUnitCap.AirDefenceCap"/>
    /// (escorts included), so the guard does not eat the army's slots.
    /// </summary>
    private bool IsAirDefenceFull(VehicleDefinition definition)
    {
        return IsAirDefence(definition) && (IsAirDefenceShareFull() || IsAirDefenceAtMax());
    }

    /// <summary>
    /// <see cref="AirDefendersPerBase"/> is a ceiling, radar SAMs and counter-battery mounts included.
    /// Launchers ride on top of the ring's own count, so as a target it was never met.
    /// </summary>
    private bool IsAirDefenceAtMax()
    {
        return CountOwnedAirDefence() >= Mathf.Max(1, ownedBases) * AirDefendersPerBase;
    }

    /// <summary>
    /// Capped: the cap's air-defence slots. Uncapped: a share of the fighting vehicles, since the home
    /// guard otherwise forces one launcher a review per base. The floor keeps the first launchers
    /// coming before there is an army to share.
    /// </summary>
    private bool IsAirDefenceShareFull()
    {
        int owned = CountOwnedAirDefence() + ownedEscorts;
        return CommanderUnitCap.Enabled
            ? owned >= CommanderUnitCap.AirDefenceCap
            : owned >= Mathf.Max(
                Mathf.Max(MinAirDefenceVehicles, ownedBases * MinAirDefencePerBase),
                Mathf.CeilToInt(ownedCombat * MaxAirDefenceShareOfForce));
    }

    /// <summary>One swap a review, before the full-cap early-out: at the cap is exactly when it pays.
    /// Returns what it spent.</summary>
    private float TryUpgradeAirDefence(FactionHQ hq, float spendable)
    {
        if (spendable <= 0f || !CommanderUnitCap.Enabled || !CommanderUnitCap.IsGroundFull(hq))
        {
            return 0f;
        }

        Unit? weakest = null;
        float weakestValue = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && !unit.disabled
                && unit.definition is VehicleDefinition owned
                && IsRingAirDefence(owned)
                && owned.value < weakestValue)
            {
                weakest = unit;
                weakestValue = owned.value;
            }
        }

        if (weakest == null)
        {
            return 0f;
        }

        CollectCatalog(hq);
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition candidate = catalog[i];
            if (IsRingAirDefence(candidate)
                && candidate.value <= spendable
                && candidate.value >= weakestValue * AirDefenceUpgradeValueRatio
                && (best == null || candidate.value > best.value))
            {
                best = candidate;
            }
        }

        // One already waiting in the reserve means the last swap has not rolled out yet, and a unit
        // at its 60% share would only sit there: scrapping for either leaves a slot empty.
        if (best == null
            || hq.GetUnitSupply(best) > 0
            || CommanderUnitCap.IsAiTypeFull(hq, best)
            || !CommanderGameAccess.TrySpend(hq, Mathf.Max(0f, best.value)))
        {
            return 0f;
        }

        string retired = CommanderGameAccess.GetVehicleLabel((VehicleDefinition)weakest.definition);
        weakest.DisableUnit();
        hq.ModifyUnitSupply(best, 1);
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) scrapped a {retired} for a {CommanderGameAccess.GetVehicleLabel(best)} "
                + $"({best.value:0}) under the unit cap.");
        return best.value;
    }

    /// <summary>
    /// Under a unit cap with its ground share full, scraps one builder a review that has had no
    /// blueprint and nothing to repair for <see cref="IdleBuilderScrapSeconds"/>, down to
    /// <see cref="MinEnemyBuilders"/>. Never while a blueprint waits unattended, or it would scrap and
    /// rebuy every review.
    /// </summary>
    private void ScrapIdleBuilders(FactionHQ hq)
    {
        if (!CommanderUnitCap.Enabled || hq.factionUnits == null)
        {
            return;
        }

        float now = Time.time;
        builderScratch.Clear();
        foreach (KeyValuePair<Unit, float> entry in builderBusyAt)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                builderScratch.Add(entry.Key!);
            }
        }

        foreach (Unit gone in builderScratch)
        {
            builderBusyAt.Remove(gone);
        }

        builderScratch.Clear();
        CommanderRepairService? repair = CommanderRepairService.Instance;
        Unit? longestIdle = null;
        float idleSince = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not GroundVehicle || unit.disabled || !CommanderRepairService.IsRepairUnit(unit))
            {
                continue;
            }

            builderScratch.Add(unit);
            if (CommanderEconomyService.IsBuilder(unit)
                || repair?.TryGetAssignedTarget(unit, out _) == true
                || CommanderSamSiteService.IsReservedConstructionJacknife(unit)
                || !builderBusyAt.TryGetValue(unit, out float busyAt))
            {
                builderBusyAt[unit] = now;
                continue;
            }

            if (busyAt < idleSince)
            {
                idleSince = busyAt;
                longestIdle = unit;
            }
        }

        if (longestIdle == null
            || now - idleSince < IdleBuilderScrapSeconds
            || builderScratch.Count <= GetBuilderBound(hq, min: true)
            || !CommanderUnitCap.IsGroundFull(hq)
            || CommanderEconomyService.Instance?.HasBlueprintWithoutBuilders(hq) != false)
        {
            return;
        }

        builderBusyAt.Remove(longestIdle);
        longestIdle.DisableUnit();
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) scrapped a builder idle for {(now - idleSince) / 60f:0} min "
                + $"under the unit cap ({builderScratch.Count - 1} left).");
    }
}
