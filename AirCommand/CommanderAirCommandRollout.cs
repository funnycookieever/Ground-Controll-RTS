using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// Below this the approach flares: the sink rate is held to <see cref="FlareSinkPerMeter"/> x
    /// height, between the two limits. A Brawler was still coming down at 9 m/s at 5.6 m, and
    /// another dived through 40 m at 18.7 m/s and could not be pulled out, so the limit starts
    /// well up the approach and only tightens near the wheels.
    /// </summary>
    private const float FlareStartMeters = 100f;
    private const float FlareSinkPerMeter = 0.4f;
    private const float FlareMinSink = 1.5f;

    /// <summary>
    /// Max sink rate (m/s) allowed near the deck. A 3.4 degree glideslope at 70 m/s is about 4 m/s,
    /// so this only bends finals that are steeper than intended; 8 m/s broke main gear.
    /// </summary>
    private const float FlareMaxSink = 5f;
    private const float FlareGain = 2f;

    /// <summary>An approach whose track is further off the runway than this, or slower than this share of the landing speed, goes round.</summary>
    private const float LostApproachDegrees = 60f;
    private const float StallSpeedShare = 0.85f;

    /// <summary>
    /// Slow alone is not a stall: a Brawler's ordinary final is 54 m/s against a landing speed of
    /// 64, and counting that sent every approach round. The stalled ones fell at 18-28 m/s.
    /// </summary>
    private const float StallSinkRate = 8f;

    /// <summary>Still this high over the runway past the touchdown point is a go-around.</summary>
    private const float LongLandingMeters = 15f;

    /// <summary>
    /// Past the touchdown point, an approach goes round when the runway left is less than this
    /// many metres of glide per metre of height (3.4 degrees) plus a rollout. A fixed margin sent a
    /// Brawler round at 28 m with 1080 m of Sandrift's runway left, and 17 + 600 m sent every
    /// Compass round at Boscali's 834 m strip (31 m up, 767 m left). The rollout brake assist stops
    /// a landing-speed jet in about 300 m, so a late touchdown is fine.
    /// </summary>
    private const float GlideRunPerMeter = 12f;
    private const float RolloutNeedMeters = 300f;

    /// <summary>Below the flare height, coming down faster than this is a go-around, not a flare.</summary>
    private const float GoAroundSinkRate = 12f;

    /// <summary>Vertical deceleration (m/s²) above which a flare cannot stop the sink in time.</summary>
    private const float DiveArrestMax = 3f;

    /// <summary><c>AIPilotLandingState.LandingMode</c> values (enum order).</summary>
    private const int StabilizedApproachMode = 2;
    private const int TurningToFinalMode = 1;
    private const int TouchedDownMode = 4;
    private const int AbortingLandingMode = 5;

    /// <summary>A go-around below this near the runway still holds takeoffs there.</summary>
    private const float GoAroundTrafficMeters = 300f;

    /// <summary>Longest an aeroplane circles off final, for a stand or for traffic, before it comes in anyway.</summary>
    private const float MaxStandHoldSeconds = 300f;

    /// <summary>Height over the terrain an aeroplane held off final circles at.</summary>
    private const float HoldingHeightMeters = 500f;
    private const float HoldingStackMeters = 100f;

    /// <summary>An aeroplane turns final once no higher than this over the pattern point, or after the time.</summary>
    private const float FinalTurnHighMeters = 150f;
    private const float DescendFromHoldSeconds = 120f;
    private const int HoldingLevels = 5;

    /// <summary>
    /// A takeoff only waits for landers this close to the runway. A takeoff roll takes about 25 s;
    /// waiting for landers further out left departures blocking the runway and landers going round.
    /// </summary>
    private const float ShortFinalMeters = 3000f;

    /// <summary>
    /// A second lander may turn final behind one already on final this far ahead of it down the
    /// runway line (about 30 s at landing speed), and lands while the one ahead is past where it
    /// would stop (<see cref="FindAircraftOnRunway"/>); the rollout brakes for anything left ahead.
    /// </summary>
    private const float InTrailFinalMeters = 2000f;

    /// <summary>A lone lander flies its landing speed from this far (beyond 500 m) out from touchdown; further out the game's faster approach, less this.</summary>
    private const float ApproachSlowMeters = 4000f;

    /// <summary>How long a runway's traffic scan is reused.</summary>
    private const float RunwayTrafficSeconds = 0.5f;

    /// <summary>A stand farther than this that the network gives no route to is not taxied to.</summary>
    private const float UnroutedStandMeters = 60f;

    /// <summary>A taxi whose route has run out steers straight at its destination only this close.</summary>
    private const float RouteEndSteerMeters = 150f;

    /// <summary>
    /// Below this height an approach that is not lined up, or is still diving, goes round. The
    /// autopilot loses most pitch authority while rolling to correct, so it would never flare.
    /// </summary>
    private const float GoAroundBelowMeters = 30f;
    private const float GoAroundAboveMeters = 3f;
    private const float GoAroundWidthShare = 0.3f;
    private const float TrafficCommitMeters = 10f;

    /// <summary>
    /// The game's go-around aims 8 m above level over a kilometre, so a jet that was sinking 9 m/s
    /// at 30 m went on down to 0.7 m before it climbed. A go-around under this height aims at least
    /// <see cref="GoAroundClimb"/> (rise over run) up.
    /// </summary>
    private const float GoAroundClimbBelowMeters = 150f;
    private const float GoAroundClimb = 0.1f;

    /// <summary>Short final with the ground track or the bank further off than this goes round.</summary>
    private const float GoAroundTrackDegrees = 10f;
    private const float GoAroundBankDegrees = 10f;
    private const float GoAroundBankTrackDegrees = 3f;
    private const float GoAroundSteepBankDegrees = 20f;

    private const float FinalTurnLowMeters = 200f;
    private const float FinalTurnLowBank = 45f;

    /// <summary>A go-around keeps its gear down below this.</summary>
    private const float GoAroundGearUpMeters = 15f;
    private const int MaxGoArounds = 3;

    /// <summary>What the wheel brakes manage on their own, nose down (an A-19 Brawler, measured).</summary>
    private const float RolloutWheelBraking = 3f;
    private const float RolloutAssistMax = 5f;
    private const float RolloutAssistAfterSeconds = 0.5f;
    private const float RolloutTaxiSpeed = 15f;

    /// <summary>A landed taxi turns on the spot until within this of its next waypoint, and not for one closer than the distance.</summary>
    private const float TaxiPivotDoneDegrees = 15f;
    private const float TaxiPivotMinMeters = 8f;

    /// <summary>Below this the rollout hands the aeroplane to the taxi.</summary>
    private const float RolloutHandoverSpeed = 6f;

    /// <summary>Clear space a rollout keeps past its exit, or past where it can stop, before it brakes for traffic ahead.</summary>
    private const float RolloutBlockMarginMeters = 60f;
    private const float RolloutEndMarginMeters = 60f;

    /// <summary>A landed aeroplane this close to an exit is at it, whichever way the exit faces.</summary>
    private const float ExitReachedMeters = 30f;

    /// <summary>How far a refuelled aeroplane backs out of its stand before it turns, at walking pace.</summary>
    private const float PushbackMinMeters = 15f;
    private const float PushbackSpeed = 2f;

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotLandingState, float> LandingTimeOnGroundRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotLandingState, float>("timeOnGround");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotLandingState, Airbase.Runway.RunwayUsage> LandingRunwayUsageRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotLandingState, Airbase.Runway.RunwayUsage>("runwayUsage");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotLandingState, Transform?> LandingExitPointRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotLandingState, Transform?>("runwayExitPoint");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotLandingState, float> LandingSpeedRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotLandingState, float>("adjustedLandingSpeed");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, Transform?> TaxiExitPointRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, Transform?>("exitTaxiPoint");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, bool> TaxiDisembarkingRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("disembarking");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, float> TaxiStuckSpeedRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, float>("stuckTimerSpeed");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, float> TaxiStuckYawRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, float>("stuckTimerYaw");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, bool> TaxiYieldingRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("yielding");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, Transform?> TaxiDestinationRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, Transform?>("destinationPoint");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, PathfindingAgent?> TaxiPathfinderRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, PathfindingAgent?>("pathfinder");

    private static readonly FieldInfo? LandingModeField = HarmonyLib.AccessTools.Field(typeof(AIPilotLandingState), "landingMode");

    /// <summary>
    /// Per runway: who is on final or rolling out, who is lined up or rolling for takeoff. Not who is
    /// still turning onto final: a Brawler lined up at Agrapol waited 90 s for a stream of those,
    /// while each lander that reached short final went round because it stood on the runway.
    /// </summary>
    private static readonly Dictionary<Airbase.Runway, RunwayTraffic> TrafficByRunway = new();

    private static readonly HarmonyLib.AccessTools.FieldRef<PathfindingAgent, List<RoadPathfinding.Node>> PathNodesRef =
        HarmonyLib.AccessTools.FieldRefAccess<PathfindingAgent, List<RoadPathfinding.Node>>("nodes");

    private static readonly HarmonyLib.AccessTools.FieldRef<PathfindingAgent, Transform?> PathMovingTargetRef =
        HarmonyLib.AccessTools.FieldRefAccess<PathfindingAgent, Transform?>("movingTarget");

    private static readonly HarmonyLib.AccessTools.FieldRef<PathfindingAgent, List<GlobalPosition>> PathWaypointsRef =
        HarmonyLib.AccessTools.FieldRefAccess<PathfindingAgent, List<GlobalPosition>>("waypoints");

    /// <summary>Landed aeroplanes being turned on the spot towards their taxi route.</summary>
    private static readonly HashSet<Aircraft> TaxiPivots = new();

    private static readonly RaycastHit[] PushbackHits = new RaycastHit[16];

    /// <summary>Per field, the one lander let onto final; nobody else is until it is down.</summary>
    private static readonly Dictionary<Airbase, Aircraft> ClearedForFinal = new();

    /// <summary>Per runway, the exit departures enter by (null: none to rule out). Scene-scoped.</summary>
    private static readonly Dictionary<Airbase.Runway, Transform?> EntryExits = new();

    /// <summary>Aeroplanes kept off final by takeoff traffic (see <see cref="MayTurnFinal"/>).</summary>
    private static readonly HashSet<Aircraft> HeldOffFinal = new();

    /// <summary>Aeroplanes let off the hold and coming down to the pattern before they turn final, with when.</summary>
    private static readonly Dictionary<Aircraft, float> DescendingSince = new();

    /// <summary>When each aeroplane held off final was first held.</summary>
    private static readonly Dictionary<Aircraft, float> HeldSince = new();

    /// <summary>Aeroplanes whose current flare has been logged; dropped at touchdown.</summary>
    private static readonly HashSet<Aircraft> FlareLogged = new();

    /// <summary>Go-arounds flown since the last touchdown, per aeroplane.</summary>
    private static readonly Dictionary<Aircraft, int> GoArounds = new();

    private static readonly MethodInfo? LandingSwitchMode = HarmonyLib.AccessTools.Method(typeof(AIPilotLandingState), "SwitchMode");

    private static readonly Type? LandingModeType = HarmonyLib.AccessTools.Inner(typeof(AIPilotLandingState), "LandingMode");

    private static readonly List<Aircraft> AirborneLanders = new();

    /// <summary>The aeroplane whose approach check is running, for <see cref="FilterLandingList"/>, or null.</summary>
    private static Aircraft? separating;

    /// <summary>The landing whose speed <see cref="HoldInTrailSpeed"/> lowered this frame, and its own value.</summary>
    private static AIPilotLandingState? trailState;
    private static float trailSpeed;

    /// <summary>The aeroplane whose approach frame is running and should flare, or null.</summary>
    private static Aircraft? flaring;

    /// <summary>The flaring aeroplane's height over its runway (<see cref="HeightOverRunway"/>).</summary>
    private static float flaringHeight;

    /// <summary>The aeroplane whose turn-to-final frame is running, or null.</summary>
    private static Aircraft? turningFinal;

    /// <summary>The aeroplane whose go-around frame is running low, or null.</summary>
    private static Aircraft? climbing;

    /// <summary>The aeroplane whose pattern frame is running while it is held off final, or null.</summary>
    private static Aircraft? holding;

    /// <summary>
    /// Replaces the rollout frame of <c>AIPilotLandingState.TouchedDown</c> for AI aeroplanes on land
    /// runways. True skips the original. Identical except for the aim point: the game aims about 18 m
    /// above the deck, holding the nose up and unloading the brakes. Aiming at the deck puts the nose
    /// wheel down and the weight on the brakes.
    /// </summary>
    internal static bool RunRollout(AIPilotLandingState state, bool checkMode)
    {
        if (LandingSwitchMode == null || LandingModeType == null)
        {
            return false;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        Airbase? airbase = LandingAirbaseRef(state);
        Airbase.Runway.RunwayUsage usage = LandingRunwayUsageRef(state);
        if (aircraft == null
            || aircraft.Player != null
            || airbase == null
            || airbase.AttachedAirbase
            || usage.Runway == null)
        {
            return false;
        }

        // The game's check picks the exit and hands anything under 15 m/s to the taxi, wherever
        // it is, and never a type that can land vertically. Keep the exit pick; the handover is
        // below, at walking pace.
        if (checkMode)
        {
            if (LandingExitPointRef(state) == null && usage.Runway.TryGetExitTaxiPoint(aircraft.transform, aircraft.speed, out Transform exitPoint))
            {
                LandingExitPointRef(state) = exitPoint;
            }

            return true;
        }

        ref float timeOnGround = ref LandingTimeOnGroundRef(state);
        timeOnGround += Time.fixedDeltaTime;
        GoArounds.Remove(aircraft);
        FlareLogged.Remove(aircraft);
        HeldOffFinal.Remove(aircraft);
        HeldSince.Remove(aircraft);
        ControlInputs inputs = aircraft.GetInputs();
        if (aircraft.radarAlt > 1f)
        {
            // A bounce: the game goes round again from here.
            timeOnGround = 0f;
            LandingSwitchMode.Invoke(state, new[] { Enum.Parse(LandingModeType, "Aborting_Landing") });
            return true;
        }

        if (timeOnGround > 10f && (!usage.Runway.AircraftOnRunway(aircraft) || aircraft.speed < 1f))
        {
            if (aircraft.speed < 1f)
            {
                aircraft.StartEjectionSequence();
            }

            inputs.brake = 1f;
            inputs.throttle = 0f;
            return true;
        }

        inputs.brake = Mathf.Clamp01(timeOnGround * 2f);
        inputs.throttle = 0f;
        inputs.customAxis1 = 1f;
        Vector3 direction = usage.Runway.GetDirection(usage.Reverse).normalized;
        Vector3 aim = usage.Runway.GetNearestPoint(aircraft.transform.position, extend: false) + (direction * 100f);
        Transform? exit = LandingExitPointRef(state);
        if (exit != null && Vector3.Dot(exit.position - aircraft.transform.position, direction) < 0f)
        {
            // Rolled past the exit: turning back for it is a U-turn across the grass, so carry on
            // to the next one down the runway, which matters most for a heavy jet that needs the
            // length anyway. The game's check picks it (exits ahead only); with none left the
            // taxi takes over where the aeroplane stops.
            LandingExitPointRef(state) = null;
            exit = null;
        }

        if (exit != null && timeOnGround > 2f)
        {
            float distance = Vector3.Distance(exit.position, aircraft.transform.position);
            float exitSpeed = Mathf.Min(RolloutHandoverSpeed + Mathf.Sqrt(Mathf.Max(distance - 30f, 1f)), RolloutTaxiSpeed);
            inputs.throttle = aircraft.speed < exitSpeed ? 0.5f : 0f;
            inputs.brake = aircraft.speed > exitSpeed ? 1f : 0f;
        }

        // Brake for anyone stopped or slow ahead on the strip; the taxi's following distance takes over
        // at walking pace. Only count the distance still to roll (to the exit, or the stopping distance
        // plus a margin), otherwise a lander stops mid-runway for a takeoff at the far threshold.
        float stillToRoll = exit != null
            ? Vector3.Dot(exit.position - aircraft.transform.position, direction) + aircraft.maxRadius + RolloutBlockMarginMeters
            : (aircraft.speed * aircraft.speed / (2f * RolloutAssistMax)) + RolloutBlockMarginMeters;
        Aircraft? blocker = timeOnGround > 2f ? FindAircraftAhead(usage, aircraft, stillToRoll) : null;
        float stopWithin = float.MaxValue;
        if (blocker != null)
        {
            inputs.throttle = 0f;
            inputs.brake = 1f;
            if (aircraft.speed < RolloutHandoverSpeed)
            {
                HandToTaxi(aircraft, usage);
                return true;
            }

            // The wheel brakes alone are about 3 m/s^2: stop short of it with the assist as well,
            // which is what lets a lander touch down behind one still rolling out.
            stopWithin = Vector3.Dot(blocker.transform.position - aircraft.transform.position, direction)
                + RollingLead(blocker, direction) - RolloutBlockMarginMeters;
        }

        // The game hands over at the exit at whatever speed is left, and the first taxi turn is often
        // just past it. Hand over at walking pace at the exit, or wherever it stops with no exit ahead.
        if (timeOnGround > 2f
            && aircraft.speed < RolloutHandoverSpeed
            && (exit == null || Vector3.Distance(exit.position, aircraft.transform.position) < aircraft.maxRadius + 20f))
        {
            HandToTaxi(aircraft, usage);
            return true;
        }

        AssistRolloutBraking(aircraft, usage, direction, exit, timeOnGround, stopWithin);
        aircraft.autopilot.AutoAim(aim.ToGlobalPosition(), aimVelocity: false, ignoreCollisions: true, runwayAlign: true, 1.01f, 5f, followTerrain: false, 0f, Vector3.zero);
        return true;
    }

    /// <summary>
    /// Postfix on <c>AIPilotTaxiState.SearchForAirbase</c>. After landing the game routes to the
    /// runway exit with a null network, which yields no waypoints, so the taxi rolls on with a frozen
    /// yaw. Skip that leg and route straight to a stand over the taxi network instead.
    /// </summary>
    internal static void RouteLandedTaxi(AIPilotTaxiState taxi)
    {
        Airbase? field = TaxiAirbaseRef(taxi);
        PathfindingAgent? pathfinder = TaxiPathfinderRef(taxi);
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(taxi);
        if (field == null || pathfinder == null || aircraft == null || aircraft.Player != null)
        {
            return;
        }

        // Handed over on the runway past the last exit: the game's search finds nothing and stops on the
        // runway. Route back along the runway to the nearest exit rather than across the grass.
        if (TaxiExitPointRef(taxi) == null
            && TaxiDisembarkingRef(taxi)
            && aircraft.pilots[0].flightInfo.HasTakenOff
            && field.AircraftIsOnRunway(aircraft, landingRunwaysOnly: true, out Airbase.Runway runway)
            && runway.exitPoints != null)
        {
            Transform? nearest = null;
            float best = float.MaxValue;
            Transform? entry = GetEntryExit(runway);
            foreach (Transform exitPoint in runway.exitPoints)
            {
                float distance = exitPoint == null || ReferenceEquals(exitPoint, entry)
                    ? float.MaxValue
                    : (exitPoint.position - aircraft.transform.position).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    nearest = exitPoint;
                }
            }

            if (nearest == null)
            {
                return;
            }

            TaxiDisembarkingRef(taxi) = false;
            TaxiExitPointRef(taxi) = nearest;

            // Already at the exit: route to the stand from here and let the landed pivot turn it on the
            // spot. A U-turn here can run it off the runway.
            if (best > ExitReachedMeters * ExitReachedMeters)
            {
                // The taxi disembarks on a null destination once under 1 m/s, so a stopped aeroplane
                // given only an exit gave up at once, was re-taxied and gave up again, for good. The
                // game swaps in the nearest stand when it reaches the exit anyway.
                TaxiDestinationRef(taxi) = field.TryGetNearestServicePoint(aircraft.transform.position, out Transform exitStand)
                    ? exitStand
                    : nearest;
                pathfinder.Shortcut(nearest.GlobalPosition());
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} stopped on the runway with no exit ahead; taxiing back to the one {Mathf.Sqrt(best):0} m away.");
                return;
            }
        }

        // Handed over just off the runway, the game's search finds no runway and shuts down in place,
        // which can block the departure entry. Send it to a stand like the rest.
        bool strandedOffRunway = TaxiExitPointRef(taxi) == null
            && TaxiDisembarkingRef(taxi)
            && TaxiDestinationRef(taxi) == null
            && aircraft.pilots[0].flightInfo.HasTakenOff;
        if (TaxiExitPointRef(taxi) == null && !strandedOffRunway)
        {
            return;
        }

        RoadPathfinding.RoadNetwork network = field.GetTaxiNetwork();
        if (!network.Exists() || !field.TryGetNearestServicePoint(aircraft.transform.position, out Transform stand))
        {
            return;
        }

        TaxiExitPointRef(taxi) = null;
        TaxiDisembarkingRef(taxi) = false;

        // A single straight waypoint means NoRoadNearTarget: the stand is off the taxi network. Drop it
        // and ask again; if nothing else is free, queue for the game's nearest stand.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            TaxiDestinationRef(taxi) = stand;
            pathfinder.Pathfind(network, stand.GlobalPosition(), null);
            if (PathWaypointsRef(pathfinder).Count > 1
                || (stand.position - aircraft.transform.position).sqrMagnitude < UnroutedStandMeters * UnroutedStandMeters)
            {
                break;
            }

            MarkStandUnreachable(stand);
            if (!field.TryGetNearestServicePoint(aircraft.transform.position, out stand))
            {
                break;
            }
        }

        // Every free stand is off the network: queue behind whoever holds a reachable one instead of
        // driving a straight line across the dirt.
        Transform[]? allStands = ServicePointsRef(field);
        if (PathWaypointsRef(pathfinder).Count <= 1
            && (TaxiDestinationRef(taxi)!.position - aircraft.transform.position).sqrMagnitude >= UnroutedStandMeters * UnroutedStandMeters
            && allStands != null)
        {
            for (int i = 0; i < allStands.Length; i++)
            {
                Transform candidate = allStands[i];
                if (candidate == null || UnreachableStands.Contains(candidate) || IsBesideRunway(field, candidate.position))
                {
                    continue;
                }

                TaxiDestinationRef(taxi) = candidate;
                pathfinder.Pathfind(network, candidate.GlobalPosition(), null);
                if (PathWaypointsRef(pathfinder).Count > 1)
                {
                    break;
                }
            }
        }

        // Where the route goes first, in the aeroplane's frame (metres right, ahead).
        List<GlobalPosition> points = PathWaypointsRef(pathfinder);
        System.Text.StringBuilder text = new();
        for (int i = 0; i < points.Count && i < 6; i++)
        {
            Vector3 local = aircraft.transform.InverseTransformPoint(points[i].ToLocalPosition());
            text.Append($" ({local.x:0},{local.z:0})");
        }

        Vector3 standLocal = aircraft.transform.InverseTransformPoint(TaxiDestinationRef(taxi)!.position);
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} taxiing to stand at ({standLocal.x:0},{standLocal.z:0}) right/ahead, {points.Count} waypoints:{text}");
    }

    /// <summary>
    /// Prefix on <c>AIPilotLandingState.StabilizedApproach</c>. The game flies the glideslope
    /// straight onto the deck with almost no flare, which breaks gear on heavy aircraft. Marks the
    /// aeroplane so <see cref="ApplyFlare"/> can bend this frame's aim point.
    /// </summary>
    internal static void BeginApproachFrame(AIPilotLandingState state, bool checkMode)
    {
        flaring = null;
        separating = null;
        if (checkMode)
        {
            Aircraft? checking = CommanderAirCommandPatches.GetStateAircraft(state);
            separating = checking != null && checking.Player == null ? checking : null;
        }

        Aircraft? aircraft = checkMode ? null : CommanderAirCommandPatches.GetStateAircraft(state);
        Airbase? airbase = LandingAirbaseRef(state);
        if (aircraft == null
            || aircraft.Player != null
            || aircraft.rb == null
            || airbase == null
            || airbase.AttachedAirbase
            || aircraft.GetAircraftParameters().verticalLanding)
        {
            return;
        }

        // Or lost: the game cuts throttle when time to touchdown is under 2 s, and flying away from the
        // touchdown point makes that time negative, so the aircraft stalls.
        Airbase.Runway.RunwayUsage usage = LandingRunwayUsageRef(state);
        float height = HeightOverRunway(aircraft, usage);
        Vector3 track = aircraft.rb.velocity;
        track.y = 0f;
        float trackOff = usage.Runway == null ? 0f : Vector3.Angle(track, usage.Runway.GetDirection(usage.Reverse));
        string? reason = null;
        if (usage.Runway != null && height > GoAroundAboveMeters)
        {
            if (trackOff > LostApproachDegrees)
            {
                reason = $"heading {trackOff:0} deg off the runway";
            }
            else if (aircraft.speed < LandingSpeedRef(state) * StallSpeedShare && -aircraft.rb.velocity.y > StallSinkRate)
            {
                reason = "stalling";
            }
        }

        // Past the touchdown point and still well up, the game's approach is at idle the same way
        // (a Brawler that floated over Sandrift's touchdown point at 60 m slowed to 50 m/s and
        // dropped a wing): it cannot land in what is left, so it goes round.
        if (reason == null && usage.Runway != null && height > LongLandingMeters)
        {
            Vector3 threshold = usage.GetStart().position;
            Vector3 along = usage.Runway.GetDirection(usage.Reverse).normalized;
            float touchdown = Vector3.Dot(usage.GetTouchdownPoint().ToLocalPosition() - threshold, along);
            float travelled = Vector3.Dot(aircraft.transform.position - threshold, along);
            float left = usage.Runway.Length - travelled;
            if (travelled > touchdown && left > 0f && left < (height * GlideRunPerMeter) + RolloutNeedMeters)
            {
                reason = $"long, {travelled:0} m past the threshold with {left:0} m left";
            }
        }

        // Diving is a sink the flare cannot take out in the height left. A flat 12 m/s from 100 m sent
        // ~40 % of all landers round the moment they reached flare height, at 1-2 m/s² to arrest.
        float sinking = -aircraft.rb.velocity.y;
        if (reason == null
            && height < FlareStartMeters
            && sinking > GoAroundSinkRate
            && sinking * sinking / (2f * Mathf.Max(height, 1f)) > DiveArrestMax)
        {
            reason = "diving";
        }

        if (((height < GoAroundBelowMeters && height > GoAroundAboveMeters) || reason != null) && ShouldGoAround(state, aircraft, reason))
        {
            return;
        }

        flaring = aircraft;
        flaringHeight = height;
        if (height < FlareStartMeters)
        {
            if (FlareLogged.Count > 256)
            {
                FlareLogged.Clear();
            }

            if (FlareLogged.Add(aircraft))
            {
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} flaring at {height:0.0} m, {aircraft.speed:0} m/s, sinking {-aircraft.rb.velocity.y:0.0} m/s, landing speed {LandingSpeedRef(state):0}.");
            }
        }
    }

    /// <summary>
    /// Postfix on <c>Runway.GetLandingList</c>, a no-op outside an AI approach check. That check
    /// goes round when the relative speed to any lander ahead exceeds a tenth of the gap less 200 m,
    /// so the second of two landers in trail went round the moment the first braked on the runway. It sees only the airborne ones here; one on the ground is left to
    /// <see cref="ShouldGoAround"/>, which knows whether it is far enough down the runway.
    /// </summary>
    internal static void FilterLandingList(ref List<Aircraft> landings)
    {
        if (separating == null)
        {
            return;
        }

        AirborneLanders.Clear();
        for (int i = 0; i < landings.Count; i++)
        {
            Aircraft other = landings[i];
            if (other == null || other.radarAlt > 5f)
            {
                AirborneLanders.Add(other!);
            }
        }

        landings = AirborneLanders;
    }

    /// <summary>
    /// Prefix on <c>FinalTurn</c> and <c>StabilizedApproach</c>, after the frame markers: a lander
    /// with another ahead of it on the same final flies its landing speed, not the game's
    /// <c>landing speed + 0.015 x (distance - 500)</c>. That term made a follower 3 km back 45 m/s
    /// faster than the one ahead, and two Compasses cleared in trail at Dustbowl flared 6 s apart.
    /// A lone lander gets the term trimmed by <see cref="ApproachSlowMeters"/>. Undone in <see cref="EndApproachFrame"/>.
    /// </summary>
    internal static void HoldInTrailSpeed(AIPilotLandingState state, bool checkMode)
    {
        trailState = null;
        Aircraft? aircraft = checkMode ? null : CommanderAirCommandPatches.GetStateAircraft(state);
        Airbase.Runway.RunwayUsage usage = LandingRunwayUsageRef(state);
        if (aircraft == null || aircraft.Player != null || usage.Runway == null || aircraft.GetAircraftParameters().verticalLanding)
        {
            return;
        }

        // Alone on final, still slow to landing speed by the last ApproachSlowMeters: the game's term
        // brought Compasses over Boscali's threshold at 95 m/s against 66, 30 m high, every time.
        RunwayTraffic traffic = GetTraffic(usage.Runway);
        bool inTrail = HasLanderAhead(traffic.Finals, aircraft, usage) || HasLanderAhead(traffic.Rolling, aircraft, usage);
        float distance = FastMath.Distance(usage.GetTouchdownPoint(), aircraft.GlobalPosition());
        float excess = Mathf.Max(distance - 500f, 0f);
        trailState = state;
        trailSpeed = LandingSpeedRef(state);
        LandingSpeedRef(state) = trailSpeed - (0.015f * (inTrail ? excess : Mathf.Min(excess, ApproachSlowMeters)));
    }

    internal static void EndApproachFrame()
    {
        if (trailState != null)
        {
            LandingSpeedRef(trailState) = trailSpeed;
            trailState = null;
        }

        separating = null;
        flaring = null;
        turningFinal = null;
        climbing = null;
        holding = null;
    }

    /// <summary>
    /// Prefix on <c>AIPilotLandingState.FinalTurn</c>: marks the frame for <see cref="ApplyFlare"/>.
    /// The game aims straight at a glideslope point however far below it is, ignoring terrain.
    /// </summary>
    internal static void BeginFinalTurnFrame(AIPilotLandingState state, bool checkMode)
    {
        Aircraft? aircraft = checkMode ? null : CommanderAirCommandPatches.GetStateAircraft(state);
        turningFinal = aircraft != null && aircraft.Player == null ? aircraft : null;
    }

    /// <summary>
    /// Prefix on <c>AIPilotLandingState.AbortingLanding</c>: marks a low go-around for
    /// <see cref="ApplyFlare"/>. True skips the original. The game raises the gear on the first frame
    /// of a go-around; close to the ground this runs the same frame with the gear left down.
    /// </summary>
    internal static bool BeginGoAroundFrame(AIPilotLandingState state, bool checkMode)
    {
        Aircraft? aircraft = checkMode ? null : CommanderAirCommandPatches.GetStateAircraft(state);
        climbing = aircraft != null && aircraft.Player == null && aircraft.radarAlt < GoAroundClimbBelowMeters ? aircraft : null;
        if (climbing == null || aircraft!.rb == null || aircraft.radarAlt > GoAroundGearUpMeters)
        {
            return false;
        }

        Vector3 velocity = aircraft.rb.velocity;
        velocity.y = 0f;
        GlobalPosition aim = aircraft.GlobalPosition() + (velocity.normalized * 1000f) + (Vector3.up * 8f);
        aircraft.autopilot.AutoAim(aim, aimVelocity: true, ignoreCollisions: true, runwayAlign: false, 0.95f, 135f, followTerrain: true, 200f, Vector3.zero);
        ControlInputs inputs = aircraft.GetInputs();
        inputs.throttle = 1f;
        inputs.customAxis1 = 1f;
        aircraft.SetFlightAssist(enabled: true);
        return true;
    }

    /// <summary>
    /// Prefix on <c>AutopilotPlane.AutoAim</c>, a no-op unless <see cref="BeginApproachFrame"/>
    /// marked this aeroplane. The approach aims the velocity vector, so an aim point no lower than
    /// <c>sink / speed</c> of its horizontal distance below is a flight path that comes down no
    /// faster than <c>sink</c> m/s; tying that to the height left rounds the descent off.
    /// </summary>
    internal static void ApplyFlare(AutopilotPlane autopilot, ref GlobalPosition destination, ref float altitudeHold, bool aimVelocity, ref float bankAllowed)
    {
        // Held off final, the pattern point is only a few hundred metres over the terrain and the
        // game orbits it at full throttle: a Brawler held there went round it at 207 m/s and 87
        // degrees of bank, lost height and hit a hill. Hold well above it instead.
        Aircraft? held = holding;
        if (held != null && ReferenceEquals(autopilot.aircraft, held))
        {
            // Stack holding heights by airframe so aircraft in the same hold do not collide.
            float height = HoldingHeightMeters + ((held.persistentID.Id % HoldingLevels) * HoldingStackMeters);
            altitudeHold = Mathf.Max(altitudeHold, height);
            destination += Vector3.up * height;
            return;
        }

        Aircraft? turner = turningFinal;
        if (turner != null && ReferenceEquals(autopilot.aircraft, turner))
        {
            // Limit bank when low; the game allows up to 81 degrees in this turn.
            if (turner.radarAlt < FinalTurnLowMeters)
            {
                bankAllowed = Mathf.Min(bankAllowed, FinalTurnLowBank);
            }

            Vector3 ahead = destination - turner.GlobalPosition();
            float run = new Vector2(ahead.x, ahead.z).magnitude;

            // Point behind: the autopilot would swing the flight path through straight down to reach it.
            // Aim level, off to the side the point is on, and turn back flat.
            Vector3 flatAhead = new(ahead.x, 0f, ahead.z);
            Vector3 nose = turner.transform.forward;
            nose.y = 0f;
            if (Vector3.Dot(flatAhead, nose) < 0f && run > 1f)
            {
                Vector3 sideways = flatAhead - (nose.normalized * Vector3.Dot(flatAhead, nose.normalized));
                if (sideways.sqrMagnitude < 1f)
                {
                    sideways = turner.transform.right;
                    sideways.y = 0f;
                }

                destination = turner.GlobalPosition() + (sideways.normalized * Mathf.Max(run, 1000f));
            }

            return;
        }

        Aircraft? climber = climbing;
        if (climber != null && ReferenceEquals(autopilot.aircraft, climber))
        {
            Vector3 ahead = destination - climber.GlobalPosition();
            float run = new Vector2(ahead.x, ahead.z).magnitude;
            if (ahead.y < run * GoAroundClimb)
            {
                destination += Vector3.up * ((run * GoAroundClimb) - ahead.y);
            }

            return;
        }

        Aircraft? aircraft = flaring;
        if (aircraft == null || !aimVelocity || !ReferenceEquals(autopilot.aircraft, aircraft))
        {
            return;
        }

        // Only ever raise the game's aim point, never move it sideways: it carries the line-up onto
        // the centreline, and the first version, aimed along the ground track, touched down 12 m
        // off a highway strip. Nor lower it: a shallow approach is left to touch down where it would.
        Vector3 offset = destination - aircraft.GlobalPosition();
        float across = new Vector2(offset.x, offset.z).magnitude;
        float speed = aircraft.speed;
        if (across < 1f || speed < 1f)
        {
            return;
        }

        // The autopilot lags its aim by several degrees, and the glideslope it already aims at is
        // only a little steeper than the flare's target: a Brawler aimed at 4 m/s down kept coming
        // down at 7. So aim above the target path by twice what it is sinking too fast, which is
        // an error the autopilot answers.
        if (flaringHeight >= FlareStartMeters)
        {
            return;
        }

        float sink = Mathf.Clamp(flaringHeight * FlareSinkPerMeter, FlareMinSink, FlareMaxSink);
        float excess = Mathf.Max(-aircraft.rb.velocity.y - sink, 0f);
        float lowest = -(sink - (FlareGain * excess)) / speed * across;
        if (offset.y < lowest)
        {
            destination += Vector3.up * (lowest - offset.y);
        }
    }

    /// <summary>
    /// Called after every base-game taxi frame. A network route ends at the road point nearest the
    /// stand, after which the taxi has no steerpoint and rolls on with a frozen yaw. Steer the last
    /// stretch straight at the destination, but only when close.
    /// </summary>
    internal static void SteerPastRouteEnd(AIPilotTaxiState taxi)
    {
        PathfindingAgent? pathfinder = TaxiPathfinderRef(taxi);
        Transform? destination = TaxiDestinationRef(taxi);
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(taxi);
        if (pathfinder == null
            || destination == null
            || aircraft == null
            || PathWaypointsRef(pathfinder).Count > 0
            || PathNodesRef(pathfinder).Count > 0
            || PathMovingTargetRef(pathfinder) != null
            || (destination.position - aircraft.transform.position).sqrMagnitude > RouteEndSteerMeters * RouteEndSteerMeters)
        {
            return;
        }

        pathfinder.SetMovingTarget(destination);
    }

    /// <summary>
    /// Called after every base-game taxi frame, after <c>LimitTaxiSpeed</c>. A landed or refuelled
    /// aeroplane whose next waypoint is far off its nose is stopped and pivoted towards it, since the
    /// taxi's turning circle is too wide for routes that start behind it or turn sharply.
    /// </summary>
    internal static void PivotLandedTaxi(AIPilotTaxiState taxi)
    {
        PathfindingAgent? pathfinder = TaxiPathfinderRef(taxi);
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(taxi);
        if (aircraft == null)
        {
            return;
        }

        if (pathfinder == null
            || aircraft.disabled
            || aircraft.Player != null
            || GetFlyingPilotType(aircraft) != Pilot.PilotType.Plane
            || !(aircraft.pilots[0].flightInfo.HasTakenOff || IsStandDeparture(aircraft, taxi))
            || PathWaypointsRef(pathfinder).Count == 0)
        {
            TaxiPivots.Remove(aircraft);
            return;
        }

        Vector3 toWaypoint = PathWaypointsRef(pathfinder)[0].ToLocalPosition() - aircraft.transform.position;
        toWaypoint.y = 0f;
        Vector3 facing = aircraft.transform.forward;
        facing.y = 0f;
        float offDegrees = Vector3.Angle(facing, toWaypoint);
        bool pivoting = TaxiPivots.Contains(aircraft);
        if (!pivoting && offDegrees >= LineUpTurnDegrees && TryPushBack(aircraft, taxi))
        {
            return;
        }

        if (toWaypoint.magnitude < TaxiPivotMinMeters || offDegrees < (pivoting ? TaxiPivotDoneDegrees : LineUpTurnDegrees))
        {
            TaxiPivots.Remove(aircraft);
            return;
        }

        ControlInputs inputs = aircraft.GetInputs();
        inputs.throttle = 0f;
        inputs.brake = 1f;
        HoldStuckTimers(taxi);
        if (aircraft.speed > LineUpStopSpeed)
        {
            return;
        }

        if (!pivoting)
        {
            if (TaxiPivots.Count > 256)
            {
                TaxiPivots.Clear();
            }

            TaxiPivots.Add(aircraft);
        }

        float step = Mathf.Min(offDegrees, LineUpPivotDegreesPerSecond * Time.fixedDeltaTime);
        Quaternion pivot = Quaternion.AngleAxis(Mathf.Sign(Vector3.SignedAngle(facing, toWaypoint, Vector3.up)) * step, Vector3.up);
        CommanderGameAccess.MoveAircraft(aircraft, aircraft.transform.position, pivot * aircraft.transform.rotation, Vector3.zero);
    }

    /// <summary>
    /// Prefix on <c>AIPilotLandingState.JoinPattern</c>'s 2 s check: an aeroplane is not let onto
    /// final while another is lined up or rolling for takeoff on that runway. Landings and takeoffs
    /// can use opposite ends of the same strip, so the runway is shared in turns. True lets the check
    /// run.
    /// </summary>
    internal static bool MayTurnFinal(AIPilotLandingState state, bool checkMode)
    {
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        Airbase.Runway.RunwayUsage usage = LandingRunwayUsageRef(state);
        if (aircraft == null || usage.Runway == null)
        {
            return true;
        }

        if (!checkMode)
        {
            holding = HeldOffFinal.Contains(aircraft) ? aircraft : null;
            return true;
        }

        // Nor at its own field while every stand is taken. The state's field can be unset (landings
        // ordered by the mod), so fall back to the runway's owner.
        Airbase? field = LandingAirbaseRef(state) ?? FindRunwayOwner(usage.Runway);

        // One at a time onto final; the rest wait in the stacked hold.
        RunwayTraffic traffic = GetTraffic(usage.Runway);

        // Once cleared, a lander keeps its clearance through ground traffic and stand changes; only a
        // takeoff on its runway or another final holds it again. Re-holding it sends it back up to the
        // hold, and as the field's cleared lander it would keep every other lander off final.
        bool cleared = field != null && ClearedForFinal.TryGetValue(field, out Aircraft clearedLander) && ReferenceEquals(clearedLander, aircraft);

        // Nor while anything at the field is moving on the ground: taxiways can be single lane, and a
        // fresh lander may still be on the runway. Unless the one ahead is on final far enough ahead
        // to land in trail behind.
        bool departing = traffic.HasOther(departing: true, aircraft)
            || HasFinalTrafficFor(traffic.Finals, aircraft, usage)
            || (!cleared && field != null && IsGroundTrafficFor(field, usage.Runway, aircraft))
            || (field != null && IsOtherCleared(field, aircraft) && !IsLandingAhead(aircraft, usage, ClearedForFinal[field], InTrailFinalMeters))

            // A refuelled aeroplane waiting to taxi out goes before the next lander.
            || (!cleared && field != null && HasQueuedDeparture(field, aircraft));
        bool noStand = !departing
            && !cleared
            && field != null
            && field.CurrentHQ == aircraft.NetworkHQ
            && !HasFreeStand(field, aircraft);
        if (!departing && !noStand)
        {
            // The runway belongs to this aircraft until it is down; descending from the hold to pattern
            // height is not yet "on final".
            if (field != null && (!ClearedForFinal.TryGetValue(field, out Aircraft previous) || !ReferenceEquals(previous, aircraft)))
            {
                ClearedForFinal[field] = aircraft;
                CommanderPlugin.Log.LogInfo($"{CommanderGameAccess.GetUnitLabel(aircraft)} cleared onto final at {field.name}, {aircraft.radarAlt:0} m up.");
            }

            // Come down near the game's own pattern point first, otherwise it arrives too high, dives
            // and goes round. Measured against the pattern point, not the radar: over K92's terrain a
            // radar gate let Brawlers turn final 500 m up 1.5 km out. At most DescendFromHoldSeconds.
            HeldOffFinal.Remove(aircraft);
            Vector3 patternPoint = usage.GetGlideslopeAimpoint(aircraft, aircraft.GetAircraftParameters().turningRadius * 3f, 30f);
            if (aircraft.transform.position.y - patternPoint.y > FinalTurnHighMeters)
            {
                if (!DescendingSince.TryGetValue(aircraft, out float descending))
                {
                    DescendingSince[aircraft] = descending = Time.time;
                }

                if (Time.time - descending < DescendFromHoldSeconds)
                {
                    return false;
                }
            }

            DescendingSince.Remove(aircraft);
            HeldSince.Remove(aircraft);
            return true;
        }

        DescendingSince.Remove(aircraft);

        if (!HeldSince.TryGetValue(aircraft, out float since))
        {
            HeldSince[aircraft] = since = Time.time;
        }

        if (Time.time - since > MaxStandHoldSeconds)
        {
            // Fuel is running down up there; land and queue rather than circle until it is gone.
            HeldOffFinal.Remove(aircraft);
            return true;
        }

        if (HeldOffFinal.Count < 256 && HeldOffFinal.Add(aircraft))
        {
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} holding off final at {HoldingHeightMeters:0} m: {(departing ? "the runway is used for a takeoff or another aircraft is on final" : "every stand is taken")}.");
        }

        return false;
    }

    /// <summary>
    /// Postfix on <c>Airbase.RequestLanding</c>. Land toward the hangars so the rollout ends near
    /// the stands; takeoffs already leave from that end and <see cref="MayTurnFinal"/> keeps the two
    /// apart.
    /// </summary>
    internal static void ChooseLandingEnd(Airbase airbase, ref Airbase.Runway.RunwayUsage? usage)
    {
        Airbase.Runway? runway = usage?.Runway;
        if (runway == null || !runway.Reversable || runway.Start == null || runway.End == null || airbase.AttachedAirbase || !TryGetApron(airbase, out Vector3 apron))
        {
            return;
        }

        // Unreversed, the rollout ends at End; reversed, at Start.
        bool reverse = (runway.Start.position - apron).sqrMagnitude < (runway.End.position - apron).sqrMagnitude;
        if (reverse != usage!.Value.Reverse)
        {
            usage = new Airbase.Runway.RunwayUsage(runway, reverse);
        }
    }

    /// <summary>
    /// The game's exit pick (<c>Runway.TryGetExitTaxiPoint</c>), excluding the exit nearest the
    /// takeoff end on runways with more than one, since that is the departures' entry. Also used for
    /// the taxi's own search and the turn back to a passed exit.
    /// </summary>
    internal static bool TryGetLandingExit(Airbase.Runway runway, Transform fromTransform, float speed, out Transform? exitPoint)
    {
        exitPoint = null;
        if (runway.exitPoints == null || fromTransform == null)
        {
            return false;
        }

        Transform? entry = GetEntryExit(runway);
        float best = float.MaxValue;
        for (int i = 0; i < runway.exitPoints.Length; i++)
        {
            Transform candidate = runway.exitPoints[i];
            if (candidate == null || ReferenceEquals(candidate, entry))
            {
                continue;
            }

            Vector3 offset = candidate.position - fromTransform.position;
            if (Vector3.Dot(candidate.forward, fromTransform.forward) > 0f && Vector3.Dot(offset, fromTransform.forward) > 0f)
            {
                float distance = offset.magnitude;
                if (distance < best && (speed * speed) - (12f * distance) < 225f)
                {
                    best = distance;
                    exitPoint = candidate;
                }
            }
        }

        return exitPoint != null;
    }

    /// <summary>The exit departures use to get onto this runway, or null when it has only one exit.</summary>
    internal static Transform? GetEntryExit(Airbase.Runway runway)
    {
        if (EntryExits.TryGetValue(runway, out Transform? cached))
        {
            return cached;
        }

        Transform? entry = null;
        Airbase? owner = FindRunwayOwner(runway);
        if (owner != null
            && !owner.AttachedAirbase
            && runway.exitPoints != null
            && runway.exitPoints.Length > 1
            && runway.Start != null
            && runway.End != null
            && TryGetApron(owner, out Vector3 apron))
        {
            Vector3 takeoffEnd = (runway.Start.position - apron).sqrMagnitude < (runway.End.position - apron).sqrMagnitude
                ? runway.Start.position
                : runway.End.position;
            float best = float.MaxValue;
            foreach (Transform candidate in runway.exitPoints)
            {
                float distance = candidate == null ? float.MaxValue : (candidate.position - takeoffEnd).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    entry = candidate;
                }
            }

            CommanderPlugin.Log.LogInfo(
                $"{owner.name}: the runway exit {Mathf.Sqrt(best):0} m from the takeoff end is the departures' way in; landed aircraft never use it.");
        }

        if (EntryExits.Count > 64)
        {
            EntryExits.Clear();
        }

        EntryExits[runway] = entry;
        return entry;
    }

    /// <summary>The other half of <see cref="MayTurnFinal"/>: no takeoff roll while anyone is on final or rolling out.</summary>
    internal static bool IsLandingTrafficFor(Airbase.Runway runway, Aircraft aircraft, bool groundOnly)
    {
        RunwayTraffic traffic = GetTraffic(runway);
        return groundOnly ? RunwayTraffic.HasOtherIn(traffic.Rolling, aircraft) : traffic.HasOther(departing: false, aircraft);
    }

    /// <summary>
    /// Heavy aircraft cannot stop for the exit on short strips with wheel brakes alone, and the AI has
    /// no reverse thrust. Adds exactly the deceleration needed to reach taxi speed at the exit (or
    /// <see cref="RolloutEndMarginMeters"/> short of the end), capped at <see cref="RolloutAssistMax"/>.
    /// </summary>
    private static void AssistRolloutBraking(Aircraft aircraft, Airbase.Runway.RunwayUsage usage, Vector3 direction, Transform? exit, float timeOnGround, float stopWithin)
    {
        if (timeOnGround < RolloutAssistAfterSeconds || aircraft.rb == null || aircraft.speed < RolloutTaxiSpeed)
        {
            return;
        }

        Vector3 position = aircraft.transform.position;
        float remaining = exit != null && Vector3.Dot(exit.position - position, direction) > 0f
            ? Vector3.Distance(exit.position, position)
            : Vector3.Dot(usage.GetEnd().position - position, direction) - RolloutEndMarginMeters;
        remaining = Mathf.Min(remaining, stopWithin);
        float needed = ((aircraft.speed * aircraft.speed) - (RolloutTaxiSpeed * RolloutTaxiSpeed)) / (2f * Mathf.Max(remaining, 30f));
        float assist = Mathf.Clamp(needed - RolloutWheelBraking, 0f, RolloutAssistMax);
        if (assist > 0f)
        {
            aircraft.rb.AddForce(-aircraft.rb.velocity.normalized * (assist * aircraft.GetMass()), ForceMode.Force);
        }
    }

    /// <summary>Switches an unstable short final to the game's own go-around, at most <see cref="MaxGoArounds"/> times.</summary>
    private static bool ShouldGoAround(AIPilotLandingState state, Aircraft aircraft, string? urgent)
    {
        GoArounds.TryGetValue(aircraft, out int flown);
        Airbase.Runway.RunwayUsage usage = LandingRunwayUsageRef(state);

        // A stalled, diving or lost approach always goes round, even past the go-around limit.
        if ((flown >= MaxGoArounds && urgent == null) || usage.Runway == null || LandingSwitchMode == null || LandingModeType == null)
        {
            return false;
        }

        Vector3 offset = usage.Runway.GetNearestPoint(aircraft.transform, extend: true) - aircraft.transform.position;
        offset.y = 0f;
        float allowed = usage.Runway.GetWidth() * GoAroundWidthShare;

        // Below this, traffic ahead is braked for on the rollout, not gone round for: a go-around
        // at 5 m settles onto the runway at full throttle and runs into it.
        Aircraft? onRunway = HeightOverRunway(aircraft, usage) < TrafficCommitMeters ? null : FindAircraftOnRunway(usage.Runway, aircraft, usage);

        // Inside the width but still turning: touching down at an angle runs it off sideways.
        Vector3 track = aircraft.rb.velocity;
        track.y = 0f;
        Vector3 runwayLine = usage.Runway.GetDirection(usage.Reverse);
        runwayLine.y = 0f;
        float trackOff = Vector3.Angle(track, runwayLine);
        float bank = Mathf.Abs(Vector3.SignedAngle(Vector3.ProjectOnPlane(aircraft.transform.up, aircraft.transform.forward), Vector3.up, aircraft.transform.forward));

        // Bank alone is not a reason to go round; only when drifting too, or banked enough to drag a wing.
        bool drifting = trackOff > GoAroundBankTrackDegrees || offset.magnitude > allowed * 0.5f;
        bool unsettled = trackOff > GoAroundTrackDegrees
            || (bank > GoAroundBankDegrees && drifting)
            || bank > GoAroundSteepBankDegrees;
        if (offset.magnitude < allowed && onRunway == null && !unsettled && urgent == null)
        {
            return false;
        }

        GoArounds[aircraft] = flown + 1;
        if (GoArounds.Count > 256)
        {
            GoArounds.Clear();
        }

        LandingSwitchMode.Invoke(state, new[] { Enum.Parse(LandingModeType, "Aborting_Landing") });
        Vector3 along = runwayLine.normalized;
        Vector3 touchdown = usage.GetTouchdownPoint().ToLocalPosition();
        string blocker = onRunway == null
            ? string.Empty
            : $" {Vector3.Dot(onRunway.transform.position - aircraft.transform.position, along):0} m ahead at {onRunway.speed:0} m/s ({onRunway.pilots?[0]?.currentState?.GetType().Name})";
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} going round at {aircraft.radarAlt:0} m: "
                + (urgent ?? (onRunway != null ? $"{CommanderGameAccess.GetUnitLabel(onRunway)} is on the runway{blocker}" : $"{offset.magnitude:0.0} m off the centreline (allowed {allowed:0.0}), track {trackOff:0} deg off, bank {bank:0} deg"))
                + $", sinking {-aircraft.rb.velocity.y:0.0} m/s at {aircraft.speed:0} m/s (landing speed {LandingSpeedRef(state):0}), {HeightOverRunway(aircraft, usage):0} m over the runway"
                + $" {Vector3.Dot(touchdown - aircraft.transform.position, along):0} m before touchdown, glideslope {usage.GetGlideslopeError(aircraft, 0f):+0;-0} m"
                + $" at {LandingAirbaseRef(state)?.name}. Go-around {flown + 1} of {MaxGoArounds}.");
        return true;
    }

    /// <summary>
    /// Another aeroplane on the ground on this runway's pavement. The game lets a second lander onto
    /// final while the first is still rolling out. Given the asker's landing, a lander ahead of it in
    /// trail by its stopping room does not count, nor anything else standing or rolling away past it.
    /// </summary>
    private static Aircraft? FindAircraftOnRunway(Airbase.Runway runway, Aircraft asker, Airbase.Runway.RunwayUsage? landing = null)
    {
        // Far enough ahead is past where the asker stops: to its touchdown point, then its stopping
        // distance, as the rollout reckons it. A fixed 1.5 km is longer than Dustbowl's whole strip,
        // and every Compass in trail went round at 30 m for the one rolling out ahead.
        float clear = 0f;
        Vector3 along = Vector3.zero;
        if (landing != null)
        {
            along = runway.GetDirection(landing.Value.Reverse).normalized;
            float toTouchdown = Mathf.Max(0f, Vector3.Dot(landing.Value.GetTouchdownPoint().ToLocalPosition() - asker.transform.position, along));

            // The rollout brakes for traffic ahead with the wheel brakes and the full assist.
            clear = toTouchdown + (asker.speed * asker.speed / (2f * (RolloutWheelBraking + RolloutAssistMax))) + RolloutBlockMarginMeters;
        }

        List<Aircraft> all = UnitRegistry.allAircraft;
        for (int i = 0; i < all.Count; i++)
        {
            Aircraft other = all[i];
            if (other != null
                && !ReferenceEquals(other, asker)
                && !other.disabled
                && other.radarAlt < 5f
                && IsOnPavement(runway, other)

                // Anything past where the asker stops does not count, counting where one rolling away
                // will at least have got to, unless it is coming this way: a lander still rolling out,
                // or one parked or lined up far down the runway, sent every follower round at 30 m.
                && (landing == null || !IsPastStop(asker, other, along, clear)))
            {
                return other;
            }
        }

        return null;
    }

    private static bool IsPastStop(Aircraft asker, Aircraft other, Vector3 along, float clear)
    {
        bool coming = other.rb != null && Vector3.Dot(other.rb.velocity, along) < -1f;
        return !coming && Vector3.Dot(other.transform.position - asker.transform.position, along) + RollingLead(other, along) > clear;
    }

    /// <summary>
    /// How much further along <paramref name="along"/> (normalised) an aeroplane rolling that way
    /// goes at the hardest a rollout brakes: it cannot stop any shorter than this.
    /// </summary>
    private static float RollingLead(Aircraft other, Vector3 along)
    {
        float speed = other.rb == null ? 0f : Mathf.Max(Vector3.Dot(other.rb.velocity, along), 0f);
        return speed * speed / (2f * (RolloutWheelBraking + RolloutAssistMax));
    }

    private static bool HasLanderAhead(List<Aircraft> landers, Aircraft asker, Airbase.Runway.RunwayUsage usage)
    {
        for (int i = 0; i < landers.Count; i++)
        {
            if (landers[i] != null && !ReferenceEquals(landers[i], asker) && IsLandingAhead(asker, usage, landers[i], 0f))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Anyone on final who is not far enough ahead to follow in trail.</summary>
    private static bool HasFinalTrafficFor(List<Aircraft> finals, Aircraft asker, Airbase.Runway.RunwayUsage usage)
    {
        for (int i = 0; i < finals.Count; i++)
        {
            Aircraft other = finals[i];
            if (other != null && !ReferenceEquals(other, asker) && !other.disabled && !IsLandingAhead(asker, usage, other, InTrailFinalMeters))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="other"/> is landing the same way on the same runway, established on final or
    /// rolling out, or has landed and is taxiing in, and is at least <paramref name="meters"/>
    /// further down the runway line than <paramref name="follower"/>.
    /// </summary>
    private static bool IsLandingAhead(Aircraft follower, Airbase.Runway.RunwayUsage usage, Aircraft? other, float meters)
    {
        if (other == null || other.disabled || other.pilots == null || other.pilots.Length == 0 || other.pilots[0] == null || usage.Runway == null)
        {
            return false;
        }

        PilotBaseState? state = other.pilots[0].currentState;
        if (state is AIPilotLandingState landing)
        {
            Airbase.Runway.RunwayUsage ahead = LandingRunwayUsageRef(landing);
            int mode = LandingModeField == null ? -1 : Convert.ToInt32(LandingModeField.GetValue(landing), System.Globalization.CultureInfo.InvariantCulture);
            if (!ReferenceEquals(ahead.Runway, usage.Runway) || ahead.Reverse != usage.Reverse || mode is not (StabilizedApproachMode or TouchedDownMode))
            {
                return false;
            }
        }
        else if (state is not AIPilotTaxiState || !other.pilots[0].flightInfo.HasTakenOff)
        {
            return false;
        }

        Vector3 along = usage.Runway.GetDirection(usage.Reverse).normalized;
        return Vector3.Dot(other.transform.position - follower.transform.position, along) > meters;
    }

    /// <summary>
    /// Height over the runway, not over whatever is below. Radar altitude reads the sea on a final
    /// over water, so on the causeway strips east of North Boscali (road deck ~40 m over the sea) the
    /// flare and every low go-around rule ran 40 m high: "flaring at 96 m" over the threshold, and a
    /// touchdown flown as if still 40 m up. The lower of the two, so a final
    /// over a rise before the threshold still reads the rise.
    /// </summary>
    private static float HeightOverRunway(Aircraft aircraft, Airbase.Runway.RunwayUsage usage)
    {
        if (usage.Runway == null || usage.Runway.Start == null)
        {
            return aircraft.radarAlt;
        }

        Vector3 nearest = usage.Runway.GetNearestPoint(aircraft.transform.position, extend: true);
        return Mathf.Clamp(aircraft.transform.position.y - nearest.y, 0f, aircraft.radarAlt);
    }

    private static bool IsGroundTrafficFor(Airbase field, Airbase.Runway runway, Aircraft asker)
    {
        return CommanderForwardBase.IsForwardBase(field)
            ? IsStripInUse(runway, asker)
            : IsTaxiingAt(field, asker, inbound: false, nearRunway: runway)

                // Only a lander still rolling in: one stopped in the queue behind the stand held
                // every aeroplane in the air for minutes over a clear runway. Only near the runway too.
                || IsTaxiingAt(field, asker, inbound: true, minSpeed: 1f, nearRunway: runway);
    }

    /// <summary>
    /// At a forward base each hangar has its own strip, so a lander waits only for its strip: anyone
    /// on its pavement, or taxiing or rolling out to it from its hangar. The field-wide taxi test held
    /// every lander off final while any hangar launched, and released them 500-700 m up over the
    /// strip, where they dived at 14-44 m/s and went round.
    /// </summary>
    private static bool IsStripInUse(Airbase.Runway runway, Aircraft asker)
    {
        if (FindAircraftOnRunway(runway, asker) != null)
        {
            return true;
        }

        List<Aircraft> all = UnitRegistry.allAircraft;
        for (int i = 0; i < all.Count; i++)
        {
            Aircraft other = all[i];
            if (other != null
                && !ReferenceEquals(other, asker)
                && !other.disabled
                && other.radarAlt < 5f
                && other.pilots?.Length > 0
                && other.pilots[0] != null
                && !other.pilots[0].flightInfo.HasTakenOff
                && other.pilots[0].currentState is AIPilotTaxiState or AIPilotTakeoffState
                && CommanderForwardBase.PeekHomeStrip(other) == runway)
            {
                return true;
            }
        }

        return false;
    }

    private static void HandToTaxi(Aircraft aircraft, Airbase.Runway.RunwayUsage usage)
    {
        Pilot pilot = aircraft.pilots[0];
        pilot.AITaxiState ??= new AIPilotTaxiState();
        usage.Runway.DeregisterLanding(aircraft);
        pilot.SwitchState(pilot.AITaxiState);
    }

    private static RunwayTraffic GetTraffic(Airbase.Runway runway)
    {
        if (!TrafficByRunway.TryGetValue(runway, out RunwayTraffic? traffic))
        {
            if (TrafficByRunway.Count > 64)
            {
                TrafficByRunway.Clear();
            }

            traffic = new RunwayTraffic();
            TrafficByRunway[runway] = traffic;
        }

        if (Time.time < traffic.ScannedAt + RunwayTrafficSeconds)
        {
            return traffic;
        }

        traffic.ScannedAt = Time.time;
        traffic.Landing.Clear();
        traffic.Departing.Clear();
        traffic.Finals.Clear();
        traffic.Rolling.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft other || other.disabled || other.pilots == null || other.pilots.Length == 0)
                {
                    continue;
                }

                PilotBaseState? state = other.pilots[0].currentState;
                if (state is AIPilotLandingState landing
                    && ReferenceEquals(LandingRunwayUsageRef(landing).Runway, runway)
                    && LandingModeField != null)
                {
                    int mode = Convert.ToInt32(LandingModeField.GetValue(landing), System.Globalization.CultureInfo.InvariantCulture);
                    if (mode is TurningToFinalMode or StabilizedApproachMode)
                    {
                        traffic.Finals.Add(other);
                    }
                    else if (mode == TouchedDownMode)
                    {
                        traffic.Rolling.Add(other);
                    }

                    // Also anything going round low over the strip, which climbs out on the departure's heading.
                    bool near = (runway.GetNearestPoint(other.transform.position, extend: false) - other.transform.position).sqrMagnitude < ShortFinalMeters * ShortFinalMeters;
                    if (mode == TouchedDownMode
                        || (mode == StabilizedApproachMode && near)
                        || (mode == AbortingLandingMode && near && other.radarAlt < GoAroundTrafficMeters))
                    {
                        traffic.Landing.Add(other);
                    }
                }
                else if (state is AIPilotTakeoffState takeoff && ReferenceEquals(TakeoffRunwayRef(takeoff)?.Runway, runway))
                {
                    traffic.Departing.Add(other);
                }
            }
        }

        return traffic;
    }

    /// <summary>
    /// A refuelled aeroplane that must turn round to leave its stand first backs straight out, then
    /// turns on the open apron; turning in a revetment hits the walls. True while backing.
    /// </summary>
    private static bool TryPushBack(Aircraft aircraft, AIPilotTaxiState taxi)
    {
        if (!IsStandDeparture(aircraft, taxi) || !TryGetTaxiOrigin(aircraft, taxi, out Vector3 origin))
        {
            return false;
        }

        Vector3 moved = aircraft.transform.position - origin;
        moved.y = 0f;
        float distance = Mathf.Max(PushbackMinMeters, aircraft.definition.length);
        if (moved.magnitude >= distance || IsBlockedBehind(aircraft, distance - moved.magnitude))
        {
            return false;
        }

        ControlInputs inputs = aircraft.GetInputs();
        inputs.throttle = 0f;
        inputs.brake = 1f;
        HoldStuckTimers(taxi);
        Vector3 back = -aircraft.transform.forward;
        back.y = 0f;
        CommanderGameAccess.MoveAircraft(
            aircraft,
            aircraft.transform.position + (back.normalized * PushbackSpeed * Time.fixedDeltaTime),
            aircraft.transform.rotation,
            Vector3.zero);
        return true;
    }

    /// <summary>
    /// True if something built stands within <paramref name="meters"/> behind the tail. Casts one ray
    /// from each wingtip and one from the middle, ignoring the aeroplane's own parts and bare ground.
    /// </summary>
    private static bool IsBlockedBehind(Aircraft aircraft, float meters)
    {
        Vector3 back = -aircraft.transform.forward;
        back.y = 0f;
        back.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, back);
        float halfSpan = aircraft.definition.width * 0.5f;
        float reach = meters + (aircraft.definition.length * 0.5f);
        for (int lane = -1; lane <= 1; lane++)
        {
            Vector3 from = aircraft.transform.position + (Vector3.up * 1.5f) + (side * (halfSpan * lane));
            int hits = Physics.RaycastNonAlloc(from, back, PushbackHits, reach, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider collider = PushbackHits[i].collider;
                Unit? unit = collider.GetComponentInParent<Unit>();
                if (unit == null)
                {
                    UnitPart? part = collider.GetComponentInParent<UnitPart>();
                    unit = part != null ? part.parentUnit : null;
                }

                if (unit != null && !ReferenceEquals(unit, aircraft))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Another lander was cleared for this field and is still in the air on its approach.</summary>
    private static bool IsOtherCleared(Airbase field, Aircraft asker)
    {
        if (!ClearedForFinal.TryGetValue(field, out Aircraft cleared) || ReferenceEquals(cleared, asker))
        {
            return false;
        }

        bool approaching = cleared != null
            && !cleared.disabled
            && cleared.radarAlt > ParkedRadarAltMeters
            && cleared.pilots != null
            && cleared.pilots.Length > 0
            && cleared.pilots[0] != null
            && cleared.pilots[0].currentState is AIPilotLandingState landing
            && ReferenceEquals(LandingAirbaseRef(landing), field);
        if (!approaching)
        {
            ClearedForFinal.Remove(field);
        }

        return approaching;
    }

    /// <summary>
    /// Holding still on purpose during a pushback or pivot is not stuck; keeps the taxi's 30 s stuck
    /// timer from firing.
    /// </summary>
    private static void HoldStuckTimers(AIPilotTaxiState taxi)
    {
        TaxiStuckSpeedRef(taxi) = 0f;
        TaxiStuckYawRef(taxi) = 0f;
    }

    /// <summary>The airbase a runway belongs to; a scan of every field, so callers are rare.</summary>
    private static Airbase? FindRunwayOwner(Airbase.Runway runway)
    {
        foreach (KeyValuePair<string, Airbase> pair in FactionRegistry.airbaseLookup)
        {
            if (pair.Value != null && pair.Value.runways != null && System.Array.IndexOf(pair.Value.runways, runway) >= 0)
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>The mean position of a field's hangars, where its departures start from.</summary>
    private static bool TryGetApron(Airbase airbase, out Vector3 apron)
    {
        apron = Vector3.zero;
        if (airbase.hangars == null)
        {
            return false;
        }

        int count = 0;
        foreach (Hangar hangar in airbase.hangars)
        {
            if (hangar != null)
            {
                apron += hangar.transform.position;
                count++;
            }
        }

        if (count == 0)
        {
            return false;
        }

        apron /= count;
        return true;
    }

    private sealed class RunwayTraffic
    {
        internal float ScannedAt { get; set; } = float.MinValue;

        internal List<Aircraft> Landing { get; } = new();

        internal List<Aircraft> Departing { get; } = new();

        /// <summary>Everyone turning onto or flying final, however far out.</summary>
        internal List<Aircraft> Finals { get; } = new();

        /// <summary>Landers on the ground, rolling out.</summary>
        internal List<Aircraft> Rolling { get; } = new();

        internal static bool HasOtherIn(List<Aircraft> list, Aircraft asker)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && !ReferenceEquals(list[i], asker) && !list[i].disabled)
                {
                    return true;
                }
            }

            return false;
        }

        internal bool HasOther(bool departing, Aircraft asker)
        {
            return HasOtherIn(departing ? Departing : Landing, asker);
        }
    }
}
