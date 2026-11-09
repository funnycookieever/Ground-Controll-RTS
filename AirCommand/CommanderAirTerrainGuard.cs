using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Pulls an AI aeroplane up when its flight path meets the ground sooner than it could climb away.
/// The base game's terrain warning only nudges the aim point, is off during a gun run
/// (<c>AIPilotCombatModes.UseFixedGuns</c> sets <c>ignoreCollision</c>), and looks a fixed 5 s ahead.
/// </summary>
internal static class CommanderAirTerrainGuard
{
    /// <summary>Above this nothing near the flight path needs a raycast.</summary>
    private const float CheckBelowRadarAltMeters = 600f;

    /// <summary>A steep dive reaches that height too late to pull out, so it is checked this far out.</summary>
    private const float DiveCheckSeconds = 10f;

    private const float CheckSeconds = 0.2f;

    /// <summary>How long a pull-up holds after the path last read clear, so it does not flutter.</summary>
    private const float HoldSeconds = 1.5f;

    /// <summary>Reaction allowance on top of the time the pull itself takes.</summary>
    private const float ReactSeconds = 1.5f;

    /// <summary>A building is usually the target, so it only counts this close: a gun run needs its last seconds.</summary>
    private const float BuildingSeconds = 2.5f;

    /// <summary>The flight path angle change a pull has to find, in radians (about 35 degrees).</summary>
    private const float PullRadians = 0.6f;

    /// <summary>Climb the pull aims for, as a slope.</summary>
    private const float PullSlope = 0.7f;

    /// <summary>Near the stall the same pull would only bleed the last of the speed away.</summary>
    private const float SlowPullSlope = 0.2f;

    private const float PullBankDegrees = 30f;

    /// <summary>A helicopter this close to hitting the ground at its present sink is recovered.</summary>
    private const float RotorImpactSeconds = 5f;
    private const float RotorSinkMetersPerSecond = 6f;

    /// <summary>Below this it is landing or already down, and collective is not the answer.</summary>
    private const float RotorGroundMeters = 2f;

    private const float RotorRecoveryHeightMeters = 50f;

    private const float TransportFinalMeters = 30f;
    private const float TransportLookSeconds = 3f;
    private const float TransportLookMeters = 60f;
    private const float TransportClearanceMeters = 12f;

    private static readonly Dictionary<Aircraft, (float NextCheckAt, float PullUntil)> States = new();

    /// <summary>Prefix on <c>AutopilotPlane.AutoAim</c>; a no-op outside the combat state.</summary>
    internal static void Apply(
        AutopilotPlane autopilot,
        ref GlobalPosition destination,
        ref bool aimVelocity,
        ref bool ignoreCollisions,
        ref float effort,
        ref float bankAllowed,
        ref bool followTerrain)
    {
        Aircraft? aircraft = autopilot.aircraft;
        if (aircraft == null
            || aircraft.Player != null
            || aircraft.rb == null
            || aircraft.radarAlt > Mathf.Max(CheckBelowRadarAltMeters, -aircraft.rb.velocity.y * DiveCheckSeconds)
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || aircraft.pilots[0].currentState is not AIPilotCombatModes)
        {
            return;
        }

        float now = Time.time;
        States.TryGetValue(aircraft, out (float NextCheckAt, float PullUntil) state);
        if (now >= state.NextCheckAt)
        {
            state.NextCheckAt = now + CheckSeconds;
            if (PathMeetsGround(aircraft, out _))
            {
                state.PullUntil = now + HoldSeconds;
            }

            States[aircraft] = state;
        }

        if (now >= state.PullUntil)
        {
            return;
        }

        Vector3 ahead = aircraft.rb.velocity;
        ahead.y = 0f;
        if (ahead.sqrMagnitude < 1f)
        {
            ahead = aircraft.transform.forward;
            ahead.y = 0f;
        }

        AircraftParameters parameters = aircraft.GetAircraftParameters();
        float slope = aircraft.speed < parameters.landingSpeed * 1.2f ? SlowPullSlope : PullSlope;
        destination = aircraft.GlobalPosition() + ((ahead.normalized + (Vector3.up * slope)) * 2000f);
        aimVelocity = true;
        ignoreCollisions = false;
        followTerrain = false;
        effort = 1.01f;
        bankAllowed = Mathf.Min(bankAllowed, PullBankDegrees);
        aircraft.GetInputs().throttle = 1f;
    }

    /// <summary>
    /// Prefix on <c>AutopilotHelo.AutoAim</c>; false skips it and flies a level recovery instead.
    /// The base game drops collective to nothing while rotor RPM sags (<c>num2</c> in AutoAim) and
    /// only gives it back under 7 m, so a gunship that bleeds RPM in a hard turn falls out of 70 m
    /// and hits at 20-30 m/s with the tail rotor first. <c>Hover</c> levels it and pulls collective.
    /// </summary>
    internal static bool ApplyRotor(Autopilot autopilot)
    {
        Aircraft? aircraft = autopilot.aircraft;
        if (aircraft == null
            || aircraft.Player != null
            || aircraft.rb == null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || aircraft.pilots[0].currentState is not (AIHeloCombatState or AIHeloTransportState))
        {
            return true;
        }

        float sink = -aircraft.rb.velocity.y;
        if (aircraft.radarAlt < RotorGroundMeters || sink < RotorSinkMetersPerSecond || aircraft.radarAlt > sink * RotorImpactSeconds)
        {
            return true;
        }

        float now = Time.time;
        States.TryGetValue(aircraft, out (float NextCheckAt, float PullUntil) state);
        state.PullUntil = now + HoldSeconds;
        States[aircraft] = state;
        Vector3 ahead = aircraft.transform.forward;
        ahead.y = 0f;
        autopilot.Hover(aircraft.GlobalPosition(), RotorRecoveryHeightMeters, ahead);
        return false;
    }

    /// <summary>
    /// Prefix on <c>Autopilot.Hover</c>. A transport's last 300 m hold a height over the landing
    /// point, not over the ground, so rising ground on the way in takes it nose first at 7 m/s,
    /// and stuck there more than 200 m short it is abandoned. Clears what lies under its path;
    /// the last metres stay the game's own so it still touches down. Those metres are measured flat:
    /// held up over a roof it was more than 30 m from the point in 3D even straight above it, the
    /// guard never let go, and an Ibis hovered over a base it should have landed on and captured.
    /// </summary>
    internal static void ApplyTransportHover(Autopilot autopilot, ref GlobalPosition destination, ref float altitudeHold)
    {
        Aircraft? aircraft = autopilot.aircraft;
        if (aircraft == null
            || aircraft.Player != null
            || aircraft.rb == null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || aircraft.pilots[0].currentState is not AIHeloTransportState
            || CommanderGameAccess.HorizontalDistance(destination.ToLocalPosition(), aircraft.transform.position) < TransportFinalMeters)
        {
            return;
        }

        Vector3 here = aircraft.transform.position;
        Vector3 ahead = here + (aircraft.rb.velocity * TransportLookSeconds);
        ahead.y = here.y;
        Vector3 toPoint = destination.ToLocalPosition() - here;
        toPoint.y = 0f;
        float ground = Mathf.Max(GroundHeight(here), GroundHeight(ahead), GroundHeight(here + Vector3.ClampMagnitude(toPoint, TransportLookMeters)));
        altitudeHold = Mathf.Max(altitudeHold, ground + TransportClearanceMeters - destination.ToLocalPosition().y);

        // At full collective it climbs 3-4 m/s, so a slope outruns it unless it stops to climb.
        if (here.y < ground + (TransportClearanceMeters * 0.7f))
        {
            GlobalPosition stop = aircraft.GlobalPosition();
            destination.x = stop.x;
            destination.z = stop.z;
        }
    }

    internal static void Forget(Aircraft aircraft)
    {
        States.Remove(aircraft);
    }

    internal static void ResetSession()
    {
        States.Clear();
    }

    private static float GroundHeight(Vector3 at)
    {
        return Physics.Raycast(at + (Vector3.up * 3000f), Vector3.down, out RaycastHit hit, 6000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
            ? hit.point.y
            : float.MinValue;
    }

    /// <summary>
    /// Casts the velocity out for as long as this airframe needs to roll level and pull clear. The
    /// G it can pull falls with the square of its speed below corner speed, which is why a laden
    /// Brawler at 160 m/s needs far more warning than the fixed look-ahead gave it.
    /// </summary>
    private static bool PathMeetsGround(Aircraft aircraft, out float distance)
    {
        distance = 0f;
        float speed = aircraft.rb.velocity.magnitude;
        if (speed < 1f)
        {
            return false;
        }

        AircraftParameters parameters = aircraft.GetAircraftParameters();
        float corner = Mathf.Max(parameters.cornerSpeed, 1f);
        float g = Mathf.Max(1.5f, parameters.aircraftGLimit * Mathf.Min(1f, (speed / corner) * (speed / corner)));
        float dive = Mathf.Asin(Mathf.Clamp(-aircraft.rb.velocity.y / speed, 0f, 1f));
        float pull = (PullRadians + dive) * speed / (9.81f * (g - 1f));
        float roll = Mathf.Abs(Vector3.Dot(aircraft.transform.right, Vector3.up));
        float seconds = ReactSeconds + pull + roll;

        Vector3 from = aircraft.transform.position - (Vector3.up * aircraft.maxRadius);
        Vector3 path = aircraft.rb.velocity / speed;
        if (!Physics.Raycast(from, path, out RaycastHit hit, speed * seconds, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        distance = hit.distance;
        return hit.collider.GetComponentInParent<Unit>() == null || hit.distance < speed * BuildingSeconds;
    }
}
