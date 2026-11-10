using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Keeps a cargo helicopter off the ground on its way in. The base game's transport state flies a
/// landing profile meant for flat ground, see the two hooks below.
/// </summary>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>
    /// Where <c>AIHeloTakeoffState</c> hands over to combat (radar altitude over 20 m). A refused
    /// handover returns before the state's <c>Hover</c> call, so holding past this leaves nothing
    /// flying the aircraft.
    /// </summary>
    private const float TakeoffStateExitMeters = 20f;

    private const float HoverObstacleClearanceMeters = 20f;
    private const float ClearanceFinalMeters = 40f;
    private const float ClearanceStepMeters = 20f;
    private const float CruiseLookAheadSeconds = 10f;

    /// <summary>About 6 deg across the airframe: at 12 deg a Tarantula unloaded, slid and rolled over.</summary>
    private const float LevelTolerancePerMeter = 0.1f;

    /// <summary>
    /// Extra height over anything static that is not terrain, e.g. wind turbine blades that probes
    /// can miss.
    /// </summary>
    private const float StructureMarginMeters = 30f;
    private const float ScanRadiusMeters = 10f;
    private const int TouchdownSearchesPerPass = 6;
    private const int MaxRejectedTouchdowns = 36;

    /// <summary>
    /// <c>AIHeloTransportState</c> cuts its terrain-following height to 30 % inside 1500 m of the
    /// touchdown and to nothing inside 200 m, a profile for flat ground. A cargo run keeps the full
    /// cruise clearance and holds it over the highest ground in the next 10 s of its track, since
    /// radar altitude alone reacts too late on steep slopes.
    /// </summary>
    internal static void HoldCargoRunCruiseClearance(Autopilot autopilot, GlobalPosition destination, ref float altitudeHold, bool followTerrain)
    {
        if (!followTerrain || !TryGetTransportRun(autopilot, out Aircraft? aircraft, out CargoMission? mission))
        {
            return;
        }

        altitudeHold = Mathf.Max(altitudeHold, aircraft!.definition.aircraftParameters.minimumRadarAlt);
        float lookAhead = Mathf.Max(aircraft.speed * CruiseLookAheadSeconds, 300f);
        float clearTop = GetClearTopY(aircraft, mission!, destination, lookAhead);
        if (clearTop > float.NegativeInfinity)
        {
            float groundBelow = aircraft.GlobalPosition().y - aircraft.radarAlt;
            altitudeHold = Mathf.Max(altitudeHold, clearTop - groundBelow + altitudeHold);
        }
    }

    /// <summary>
    /// Inside 300 m the transport state hovers at the touchdown's own height plus 20 m, straight
    /// through whatever stands between: a ridge, a treeline, a roof. The hover height is raised over
    /// the highest of those, so it still descends normally once the way to the point is clear.
    /// </summary>
    internal static void HoldCargoRunHoverClearance(Autopilot autopilot, GlobalPosition destination, ref float altitudeHold)
    {
        if (!TryGetTransportRun(autopilot, out Aircraft? aircraft, out CargoMission? mission))
        {
            return;
        }

        float clearTop = GetClearTopY(aircraft!, mission!, destination, float.MaxValue);
        if (clearTop > float.NegativeInfinity)
        {
            altitudeHold = Mathf.Max(altitudeHold, clearTop + HoverObstacleClearanceMeters - destination.y);
        }
    }

    /// <summary>
    /// The transport state only unloads, and only counts down to its takeoff, below 2 m of radar
    /// altitude, so a helicopter set on unloading from the hover is run here: one load per
    /// <c>DeployNextAssignedCargo</c> call, as on the ground, then the base game's own takeoff once
    /// the last has cleared (the takeoff guard holds it in the state for 8 s after a release).
    /// </summary>
    internal static void TickHoverUnload(AIHeloTransportState state)
    {
        if (Instance == null
            || Instance.assignedMissions.Count == 0
            || AircraftField?.GetValue(state) is not Aircraft aircraft
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.HoverUnload
            || !mission.TargetOverrideActive)
        {
            return;
        }

        if (HasDeployableCargo(aircraft) || mission.ReleasedCargoCount > mission.ActivatedCargoCount)
        {
            Instance.DeployNextAssignedCargo(state);
            return;
        }

        if (Time.timeSinceLevelLoad - mission.LastCargoReleasedAt > 9f
            && PilotField?.GetValue(state) is Pilot pilot
            && pilot.AIHeloTakeoffState != null)
        {
            mission.HoverUnload = false;
            pilot.SwitchState(pilot.AIHeloTakeoffState);
        }
    }

    internal static bool IsFootprintLevel(GlobalPosition center, float radius)
    {
        float low = float.MaxValue;
        float high = float.MinValue;
        Vector3 local = center.ToLocalPosition();
        for (int i = 0; i <= 8; i++)
        {
            Vector3 offset = i == 0 ? Vector3.zero : Quaternion.Euler(0f, i * 45f, 0f) * (Vector3.forward * radius);
            Vector3 sample = local + offset;
            sample.y = Datum.LocalSeaY + 5000f;
            if (!Physics.Raycast(sample, Vector3.down, out RaycastHit hit, 10000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            low = Mathf.Min(low, hit.point.y);
            high = Mathf.Max(high, hit.point.y);
        }

        return high - low <= Mathf.Max(radius * LevelTolerancePerMeter, 0.75f);
    }

    /// <summary>
    /// The game's touchdown search accepts any spot whose single ground probe reads under 20 deg, which
    /// on a slope can leave the belly above the 2 m unload height forever. A spot is kept only if the
    /// ground under the whole airframe is level to 2 m; otherwise the search is sent on (slope 90 makes
    /// <c>UpdateTouchdownPoint</c> look again). A helicopter resting on its spot still above 2 m has
    /// that spot thrown out the same way.
    /// </summary>
    private static void KeepTouchdownLevel(object destination, Aircraft aircraft, CargoMission mission)
    {
        if (DestinationTouchdownField == null || DestinationSlopeField == null || UpdateTouchdownPointMethod == null)
        {
            return;
        }

        float radius = Mathf.Max(aircraft.maxRadius, 4f);
        if (DestinationTouchdownField.GetValue(destination) is GlobalPosition current
            && IsRestingAbove(aircraft, mission, current))
        {
            // Nowhere level left to try: put the load down from where it is, 2-10 m up.
            if (mission.RejectedTouchdowns >= MaxRejectedTouchdowns)
            {
                if (!mission.HoverUnload)
                {
                    mission.HoverUnload = true;
                    CommanderPlugin.Log.LogInfo(
                        $"{CommanderGameAccess.GetUnitLabel(aircraft)} found no level ground near its LZ; unloading from {aircraft.radarAlt:0} m up.");
                }

                return;
            }

            if (mission.CheckedTouchdownLevel)
            {
                mission.CheckedTouchdownLevel = false;
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} is resting {aircraft.radarAlt:0} m up on its touchdown spot; looking for another.");
            }
        }

        for (int i = 0; i < TouchdownSearchesPerPass; i++)
        {
            if (DestinationTouchdownField.GetValue(destination) is not GlobalPosition touchdown)
            {
                return;
            }

            if (!CommanderGameAccess.ApproximatelyEqual(touchdown, mission.CheckedTouchdown, 0.5f))
            {
                mission.CheckedTouchdown = touchdown;
                mission.CheckedTouchdownLevel = IsFootprintLevel(touchdown, radius);
                mission.RestingPasses = 0;
            }

            // Nothing level within 150 m: take the base game's spot rather than wander for ever.
            if (mission.CheckedTouchdownLevel || mission.RejectedTouchdowns >= MaxRejectedTouchdowns)
            {
                return;
            }

            mission.RejectedTouchdowns++;
            DestinationSlopeField.SetValue(destination, 90f);
            UpdateTouchdownPointMethod.Invoke(destination, new object[] { 150f, aircraft });
        }
    }

    /// <summary>Two override passes (about 3 s) stopped right over the spot, unable to get lower.</summary>
    private static bool IsRestingAbove(Aircraft aircraft, CargoMission mission, GlobalPosition touchdown)
    {
        bool resting = aircraft.radarAlt > 2f
            && aircraft.radarAlt < 10f
            && aircraft.speed < 1.5f
            && CommanderGameAccess.HorizontalDistance(aircraft.transform.position, touchdown.ToLocalPosition()) < 30f;
        mission.RestingPasses = resting ? mission.RestingPasses + 1 : 0;
        return mission.RestingPasses >= 2;
    }

    private static bool TryGetTransportRun(Autopilot autopilot, out Aircraft? aircraft, out CargoMission? mission)
    {
        aircraft = autopilot.aircraft;
        mission = null;
        return Instance != null
            && Instance.assignedMissions.Count > 0
            && aircraft != null
            && Instance.assignedMissions.TryGetValue(aircraft, out mission)
            && mission.TargetOverrideActive
            && !mission.FixedWing
            && aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && aircraft.pilots[0].currentState is AIHeloTransportState;
    }

    /// <summary>
    /// The highest static hit (terrain, buildings) every 20 m along the line toward the
    /// destination, up to <paramref name="maxDistance"/> and short of its last 40 m, refreshed five
    /// times a second. NegativeInfinity once only the final 40 m is left, so the descent onto the
    /// point is the base game's own. Cruise and hover never run in the same tick, so one cache serves both.
    /// </summary>
    private static float GetClearTopY(Aircraft aircraft, CargoMission mission, GlobalPosition destination, float maxDistance)
    {
        if (Time.time < mission.NextHoverClearanceAt)
        {
            return mission.HoverClearTopY;
        }

        mission.NextHoverClearanceAt = Time.time + 0.2f;
        Vector3 from = aircraft.transform.position;
        Vector3 flat = destination.ToLocalPosition() - from;
        flat.y = 0f;
        float length = Mathf.Min(flat.magnitude - ClearanceFinalMeters, maxDistance);
        float top = float.NegativeInfinity;
        if (length > 0f)
        {
            Vector3 step = flat.normalized;
            for (float d = 0f; d <= length; d += ClearanceStepMeters)
            {
                Vector3 sample = from + (step * d);
                sample.y = Datum.LocalSeaY + 5000f;

                // Ships too: off a carrier the cruise hold is radar altitude over the deck, under the
                // island, and Ibises flew into their own carrier (Leviathan, 2026-11-06).
                if (Physics.SphereCast(sample, ScanRadiusMeters, Vector3.down, out RaycastHit hit, 10000f, PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore))
                {
                    bool terrain = GameAssets.i != null && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial;
                    top = Mathf.Max(top, hit.point.ToGlobalPosition().y + (terrain ? 0f : StructureMarginMeters));
                }
            }
        }

        mission.HoverClearTopY = top;
        return top;
    }
}
