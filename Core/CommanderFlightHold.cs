using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Flies the player's own aircraft while the Commander view is open from the cockpit.
/// <c>PilotPlayerState</c> reads the same keys and mouse the RTS camera uses (WASD is pitch and
/// roll, the left button is Fire), so both of its ticks are skipped and the game's own autopilot
/// holds the course and height the aircraft had when the view opened: <c>AutoAim</c> for an
/// aeroplane, <c>Hover</c> for anything with auto-hover. On the ground it just sits on its brakes.
/// </summary>
[HarmonyPatch(typeof(PilotPlayerState))]
internal static class CommanderFlightHold
{
    private const float HoldLookAheadMeters = 5000f;
    private const float HoldBankDegrees = 45f;

    /// <summary>Floor on the terrain-follow height, so a low entry is not flown at treetop level.</summary>
    private const float MinHoldHeightAboveGround = 150f;

    private static Aircraft? held;
    private static GlobalPosition holdPoint;
    private static Vector3 holdHeading;
    private static float holdHeightAboveGround;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PilotPlayerState.UpdateState))]
    private static bool UpdatePrefix(Pilot pilot)
    {
        return !IsHolding(pilot);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PilotPlayerState.FixedUpdateState))]
    private static bool FixedUpdatePrefix(Pilot pilot)
    {
        if (!IsHolding(pilot))
        {
            return true;
        }

        Hold(pilot.aircraft);
        return false;
    }

    private static bool IsHolding(Pilot pilot)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true
            || pilot.aircraft == null
            || !GameManager.GetLocalAircraft(out Aircraft local)
            || local != pilot.aircraft)
        {
            held = null;
            return false;
        }

        return true;
    }

    private static void Hold(Aircraft aircraft)
    {
        if (held != aircraft)
        {
            held = aircraft;
            holdPoint = aircraft.GlobalPosition();
            holdHeightAboveGround = Mathf.Max(aircraft.radarAlt, MinHoldHeightAboveGround);
            Vector3 forward = aircraft.cockpit.xform.forward;
            holdHeading = new Vector3(forward.x, 0f, forward.z).normalized;
            if (holdHeading == Vector3.zero)
            {
                holdHeading = Vector3.forward;
            }
        }

        ControlInputs inputs = aircraft.GetInputs();
        if (aircraft.radarAlt < aircraft.definition.spawnOffset.y + 1f)
        {
            inputs.pitch = 0f;
            inputs.roll = 0f;
            inputs.yaw = 0f;
            inputs.throttle = 0f;
            inputs.brake = 1f;
            aircraft.FilterInputs();
            return;
        }

        if (aircraft.IsAutoHoverEnabled())
        {
            aircraft.autopilot.Hover(holdPoint, 0f, holdHeading);
            return;
        }

        // The point slides ahead with the aircraft, so it never arrives and never turns round.
        GlobalPosition here = aircraft.GlobalPosition();
        GlobalPosition ahead = new(
            here.x + (holdHeading.x * HoldLookAheadMeters),
            holdPoint.y,
            here.z + (holdHeading.z * HoldLookAheadMeters));
        inputs.throttle = Mathf.Max(inputs.throttle, aircraft.GetAircraftParameters().cruiseThrottle);

        // Without followTerrain AutoAim flies dead level and only nudges up at the last moment, so
        // rising ground took the aircraft. Following terrain holds the height above ground it opened at.
        aircraft.autopilot.AutoAim(ahead, true, false, false, 1f, HoldBankDegrees, true, holdHeightAboveGround, Vector3.zero);
    }
}
