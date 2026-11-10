using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// How fast a building comes back. The base rate is the prefab's <c>repairTime</c>:
/// <see cref="Repairer"/> ticks every five seconds adding <c>repairRate / repairTime</c> to every
/// part, which takes tens of minutes for a flattened building. This rewrites the strength so one
/// crew takes <see cref="CommanderRepairService.RepairSeconds"/> from wreck to whole; lighter damage
/// is proportionally quicker and a second crew halves it, like a construction site.
/// </summary>
/// <remarks>
/// On <c>Building.Repair</c> rather than the repairer, since every repair routes through it. It is
/// also the only place that sees <c>repairTime</c> (prefab data), which is logged once per type.
/// </remarks>
[HarmonyPatch(typeof(Building), nameof(Building.Repair))]
internal static class CommanderRepairRatePatches
{
    /// <summary>The interval <c>Repairer</c> registers its repair loop on, which makes one tick a
    /// known fraction of the whole job.</summary>
    private const float RepairerTickSeconds = 5f;

    /// <summary>At or above this the building is marked unrepairable and <c>Building.Repair</c>
    /// returns having done nothing, so the rate is none of our business.</summary>
    private const float NotRepairable = 999999f;

    private static readonly AccessTools.FieldRef<Building, float> RepairTimeRef =
        AccessTools.FieldRefAccess<Building, float>("repairTime");

    private static readonly HashSet<UnitDefinition> ReportedRepairTimes = new();

    private static readonly List<Hangar> HangarBuffer = new();

    private static readonly List<WarheadStorage> StorageBuffer = new();

    private static readonly AccessTools.FieldRef<WarheadStorage, bool> StorageSelfDisabledRef =
        AccessTools.FieldRefAccess<WarheadStorage, bool>("selfDisabled");

    [HarmonyPrefix]
    internal static void Prefix(Building __instance, ref float strength)
    {
        float repairTime = RepairTimeRef(__instance);
        if (repairTime > NotRepairable)
        {
            return;
        }

        if (ReportedRepairTimes.Add(__instance.definition))
        {
            float basegameMinutes = repairTime * 100f / Mathf.Max(0.01f, strength) / 60f;
            CommanderPlugin.Log.LogInfo(
                $"{__instance.definition.unitName} as authored: repairTime={repairTime:0.##}, i.e. "
                    + $"{basegameMinutes:0.#} min from a wreck at the Basegame rate; repaired in "
                    + $"{CommanderRepairService.RepairSeconds / 60f:0.#} min instead.");
        }

        strength = GetRepairStrength(repairTime);
        CommanderRepairService.Instance?.NoteRepairTick(__instance);
    }

    /// <summary>
    /// A hangar or pad whose critical part was destroyed sets its private <c>selfDisabled</c>, and only
    /// <c>Hangar.Repair</c> clears it, which the base game never calls. Once the building is whole
    /// again, clear it here.
    /// </summary>
    [HarmonyPostfix]
    internal static void Postfix(Building __instance)
    {
        if (__instance.needsRepair || __instance.disabled)
        {
            return;
        }

        __instance.GetComponentsInChildren(true, HangarBuffer);
        foreach (Hangar hangar in HangarBuffer)
        {
            if (hangar.Disabled)
            {
                hangar.Repair();
            }
        }

        HangarBuffer.Clear();

        __instance.GetComponentsInChildren(true, StorageBuffer);
        foreach (WarheadStorage storage in StorageBuffer)
        {
            if (StorageSelfDisabledRef(storage))
            {
                StorageSelfDisabledRef(storage) = false;
                storage.Repair();
            }
        }

        StorageBuffer.Clear();
    }

    /// <summary>
    /// One runnable check on the rate arithmetic, run at plugin load: a wreck takes
    /// <see cref="CommanderRepairService.RepairSeconds"/>, whatever the prefab's repairTime is.
    /// </summary>
    internal static void SelfCheck()
    {
        // Any prefab value at all: dropping out of the answer is the whole point of the formula.
        const float AuthoredRepairTime = 37f;

        float perTick = GetRepairStrength(AuthoredRepairTime) / AuthoredRepairTime;
        float hitPoints = 0f;
        float elapsed = 0f;
        float ceiling = CommanderRepairService.RepairSeconds * 3f;
        while (hitPoints < 100f && elapsed <= ceiling)
        {
            hitPoints += perTick;
            elapsed += RepairerTickSeconds;
        }

        if (Mathf.Abs(elapsed - CommanderRepairService.RepairSeconds) > RepairerTickSeconds)
        {
            CommanderPlugin.Log.LogError(
                $"Repair rate self-check FAILED: a wreck took {elapsed:0} s to rebuild, not "
                    + $"{CommanderRepairService.RepairSeconds:0} s.");
        }
    }

    /// <summary>What one <c>Repairer</c> tick must be worth: <c>Building.Repair</c> divides it by
    /// the prefab's repairTime, so multiplying it back in is what makes the prefab irrelevant.</summary>
    private static float GetRepairStrength(float repairTime)
    {
        return repairTime * 100f * RepairerTickSeconds / CommanderRepairService.RepairSeconds;
    }
}
