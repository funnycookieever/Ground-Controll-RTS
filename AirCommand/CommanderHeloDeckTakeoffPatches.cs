using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A helicopter lifting off a ship's deck keeps climbing straight up until it is clear of the
/// ship's island before it flies anywhere.
/// </summary>
/// <remarks>
/// <c>AIHeloTakeoffState.FixedUpdateState</c> hands over to <c>AIHeloCombatState</c> once
/// <c>radarAlt</c> passes 20 m, and <c>Aircraft.CheckRadarAlt</c> measures to <c>ShipsMask</c> too,
/// so on a carrier that is 20 m above the deck, under the top of the island: the Ibis turned for its
/// target and flew into its own carrier (user, 2026-11-06). Refusing the <c>SwitchState</c> instead
/// would skip the state's <c>Hover</c> call that frame (see Supply's <c>TakeoffStateExitMeters</c>),
/// so the state is shown a radar altitude just under its 20 m for the call and runs its own climb.
/// </remarks>
[HarmonyPatch(typeof(AIHeloTakeoffState), nameof(AIHeloTakeoffState.FixedUpdateState))]
internal static class CommanderHeloDeckTakeoffPatches
{
    /// <summary>Height over the deck before it may leave; a Hyperion's island is about 40 m over its deck. A guess, not measured.</summary>
    private const float DeckClearanceMeters = 60f;

    /// <summary>
    /// The state ejects at 30 s after it was entered, rotor spin-up included (~15 s on a carrier), so
    /// the extra climb ran out the clock: Ibises abandoned 50 m over the deck, undamaged (log 2026-11-06).
    /// </summary>
    private const float HeldSpawnTimeCap = 25f;

    private static readonly AccessTools.FieldRef<AIHeloTakeoffState, float> SpawnTime =
        AccessTools.FieldRefAccess<AIHeloTakeoffState, float>("spawnTime");

    private static float realRadarAlt = -1f;

    internal static void Prefix(AIHeloTakeoffState __instance, Pilot pilot)
    {
        realRadarAlt = -1f;
        Aircraft? aircraft = pilot?.aircraft;
        if (aircraft == null || aircraft.radarAlt <= 20f || aircraft.radarAlt >= DeckClearanceMeters)
        {
            return;
        }

        Vector3 from = aircraft.transform.position;
        if (Physics.Linecast(from, from - (Vector3.up * DeckClearanceMeters * 2f), (int)PhysicsLayers.ShipsMask))
        {
            realRadarAlt = aircraft.radarAlt;
            aircraft.radarAlt = 19.9f;
            ref float spawnTime = ref SpawnTime(__instance);
            spawnTime = Mathf.Min(spawnTime, HeldSpawnTimeCap);
        }
    }

    internal static void Finalizer(Pilot pilot)
    {
        if (realRadarAlt >= 0f && pilot?.aircraft != null)
        {
            pilot.aircraft.radarAlt = realRadarAlt;
        }

        realRadarAlt = -1f;
    }
}
