using System.Collections.Generic;
using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Base capture by head count, in every mission. Replaces the balance the game's
/// <c>Capture.GetChange</c> works out once a second; <c>Capture.ApplyChange</c> still applies it,
/// so owner changes, capture credit and the synced <c>controlBalance</c> are all stock.
/// </summary>
/// <remarks>
/// The stock rule is attacker strength minus defender strength, and a half-way bar never recovers
/// once the attackers die. Here:
/// <list type="bullet">
/// <item>Every ground vehicle inside the ring is one head, capture-capable or not, and so is every
/// aircraft below <see cref="AircraftHeadCeilingMeters"/>: one landed, taking off, hovering or
/// strafing is at the base, one passing over at altitude is not, and counted it froze captures.</item>
/// <item>Equal heads on both sides is contested: the bar does not move.</item>
/// <item>The side with more heads pushes the bar at its average capture strength times the head
/// margin, over the same defence the game divides by. Taking ground needs at least one
/// capture-capable unit, so aircraft alone can deny a base but never take one.</item>
/// <item>Nobody pushing and the bar drifts back at <see cref="CommanderSettings.CaptureRecoveryPerSecond"/>:
/// a held base regains its grip, a neutral base loses the challenger's progress.</item>
/// </list>
/// <c>Capture.Update</c> only runs on the server, so this needs no networking.
/// </remarks>
[HarmonyPatch]
internal static class CommanderCapturePatches
{
    /// <summary>Radar altitude above which an aircraft in the ring is only passing over.</summary>
    private const float AircraftHeadCeilingMeters = 300f;

    private const float StalemateLogSeconds = 60f;

    private static readonly AccessTools.FieldRef<Capture, ICapturable> TargetRef =
        AccessTools.FieldRefAccess<Capture, ICapturable>("target");

    private static readonly AccessTools.FieldRef<Capture, float> LastCaptureCheckRef =
        AccessTools.FieldRefAccess<Capture, float>("lastCaptureCheck");

    private static readonly Dictionary<FactionHQ, Side> Sides = new();
    private static readonly Dictionary<Capture, float> StalemateLoggedAt = new();
    private static readonly HashSet<Capture> Contested = new();
    private static readonly HashSet<Capture> Recovering = new();

    /// <summary>True while both sides have the same number of units in this ring.</summary>
    internal static bool IsContested(Capture? capture)
    {
        return capture != null && Contested.Contains(capture);
    }

    /// <summary>True while a held base's grip is climbing back: attackers gone or outnumbered.</summary>
    internal static bool IsRecovering(Capture? capture)
    {
        return capture != null && Recovering.Contains(capture);
    }

    /// <summary>How many heads <paramref name="hq"/> has in the ring, counted as GetChangePrefix counts them: the "no one is
    /// left to stop a landing" test for the enemy's capture landers.</summary>
    internal static int CountHeads(ICapturable target, FactionHQ hq)
    {
        int count = 0;
        float range = target.CaptureRange;
        UnityEngine.Vector3 center = target.center.position;
        foreach (GridSquare square in target.gridSquares)
        {
            foreach (Unit unit in square.units)
            {
                if (unit != null && !unit.disabled && unit.NetworkHQ == hq
                    && (unit is GroundVehicle || (unit is Aircraft && unit.radarAlt < AircraftHeadCeilingMeters))
                    && !FastMath.OutOfRange(unit.transform.position, center, range))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Puts base capture on the same clock as everything else. <c>Capture.Update</c> measures its
    /// own interval with <c>Time.realtimeSinceStartup</c>, which ignores <c>Time.timeScale</c>
    /// altogether: pressing Esc in single player sets the scale to zero, and the bar went on
    /// climbing behind the menu until a base fell while the game was paused. Dragging the
    /// last-checked stamp forward by the part of each frame the game did not play leaves the game's
    /// own subtraction holding scaled time, so a pause stops capture dead and the CMD panel's 2x/4x
    /// speeds it up with the rest of the mod. Nothing is skipped, so no pause is repaid as one jump
    /// on the way out of the menu.
    /// </summary>
    [HarmonyPatch(typeof(Capture), nameof(Capture.Update))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void UpdatePrefix(Capture __instance)
    {
        ref float lastCheck = ref LastCaptureCheckRef(__instance);

        // Zero is the game's "never checked" sentinel, which it answers with a whole interval.
        if (lastCheck > 0f)
        {
            lastCheck += UnityEngine.Time.unscaledDeltaTime * (1f - UnityEngine.Time.timeScale);
        }
    }

    [HarmonyPatch(typeof(Capture), "GetChange")]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static bool GetChangePrefix(Capture __instance, ref FactionHQ? highestHQ, ref float __result)
    {
        ICapturable target = TargetRef(__instance);
        highestHQ = null;
        Contested.Remove(__instance);
        Recovering.Remove(__instance);
        if (CommanderSettings.DisableCapture)
        {
            __result = 0f;
            return false;
        }

        CountUnits(target, out int totalCount, out float totalStrength);

        FactionHQ? owner = target.CurrentHQ;
        FactionHQ? leader = owner ?? __instance.capturingHQ;
        bool idle = leader == null;
        if (idle)
        {
            // Neutral and unclaimed: whoever has the most heads may start a capture.
            foreach (KeyValuePair<FactionHQ, Side> entry in Sides)
            {
                if (leader == null || entry.Value.Count > Sides[leader].Count)
                {
                    leader = entry.Key;
                }
            }

            if (leader == null)
            {
                __result = 0f;
                return false;
            }
        }

        Sides.TryGetValue(leader!, out Side mine);
        int theirCount = totalCount - mine.Count;
        float theirStrength = totalStrength - mine.Strength;
        float recovery = UnityEngine.Mathf.Max(0f, CommanderSettings.CaptureRecoveryPerSecond);
        float drift = idle ? 0f : owner != null ? recovery : -recovery;
        float defence = target.CaptureDefense + GetDefence(target, owner);
        if (defence <= 0f)
        {
            defence = 1f;
        }

        if (mine.Count == theirCount)
        {
            if (mine.Count > 0)
            {
                Contested.Add(__instance);
                __result = 0f;
                LogStalemate(__instance, target);
            }
            else
            {
                __result = drift;
            }
        }
        else if (mine.Count > theirCount)
        {
            float push = mine.Strength / mine.Count * (mine.Count - theirCount) / defence;
            if (owner != null)
            {
                __result = UnityEngine.Mathf.Max(push, recovery);
            }
            else if (push > 0f)
            {
                highestHQ = idle ? leader : null;
                __result = push;
            }
            else
            {
                __result = drift;
            }
        }
        else
        {
            // Outnumbered. An unclaimed base has no bar to push down, so only a claimed one moves.
            float push = theirStrength / theirCount * (theirCount - mine.Count) / defence;
            __result = idle ? 0f : push > 0f ? -push : drift;
        }

        if (owner != null && __result > 0f && __instance.controlBalance < 1f)
        {
            Recovering.Add(__instance);
        }

        return false;
    }

    /// <summary>Tallies every live ground vehicle and aircraft inside the ring into <see cref="Sides"/>,
    /// with the same range test as <c>Capture.GetInRangeUnits</c>.</summary>
    private static void CountUnits(ICapturable target, out int totalCount, out float totalStrength)
    {
        Sides.Clear();
        totalCount = 0;
        totalStrength = 0f;
        float range = target.CaptureRange;
        UnityEngine.Vector3 center = target.center.position;
        foreach (GridSquare square in target.gridSquares)
        {
            foreach (Unit unit in square.units)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null
                    || !(unit is GroundVehicle || (unit is Aircraft && unit.radarAlt < AircraftHeadCeilingMeters))
                    || FastMath.OutOfRange(unit.transform.position, center, range))
                {
                    continue;
                }

                float strength = UnityEngine.Mathf.Max(0f, unit.CaptureStrength);
                Sides.TryGetValue(unit.NetworkHQ, out Side side);
                side.Count++;
                side.Strength += strength;
                side.Defense += UnityEngine.Mathf.Max(0f, unit.CaptureDefense);
                Sides[unit.NetworkHQ] = side;
                totalCount++;
                totalStrength += strength;
            }
        }
    }

    /// <summary>
    /// Who is holding a ring to a stalemate, once a minute per base: a capture that "shows no
    /// progress" is otherwise indistinguishable from one that is not happening at all.
    /// </summary>
    private static void LogStalemate(Capture capture, ICapturable target)
    {
        if (StalemateLoggedAt.TryGetValue(capture, out float at) && UnityEngine.Time.time - at < StalemateLogSeconds)
        {
            return;
        }

        StalemateLoggedAt[capture] = UnityEngine.Time.time;
        System.Text.StringBuilder sides = new();
        foreach (KeyValuePair<FactionHQ, Side> entry in Sides)
        {
            sides.Append(sides.Length > 0 ? ", " : string.Empty)
                .Append(entry.Key.faction?.name)
                .Append(' ')
                .Append(entry.Value.Count)
                .Append(" unit(s) with capture strength ")
                .Append(entry.Value.Strength.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        string name = target is Airbase airbase ? CommanderCaptureService.GetAirbaseLabel(airbase) : "a base";
        CommanderPlugin.Log.LogInfo($"Capture of {name} is contested, equal numbers in the ring: {sides}.");
    }

    /// <summary>Same defence as the game: the owner's units in the ring, or an empty base's defence units.</summary>
    private static float GetDefence(ICapturable target, FactionHQ? owner)
    {
        if (owner != null)
        {
            return Sides.TryGetValue(owner, out Side side) ? side.Defense : 0f;
        }

        float defence = 0f;
        foreach (Unit unit in target.GetDefenseUnits())
        {
            if (unit != null && !unit.disabled)
            {
                defence += unit.CaptureDefense;
            }
        }

        return defence;
    }

    private struct Side
    {
        public int Count;
        public float Strength;
        public float Defense;
    }
}
