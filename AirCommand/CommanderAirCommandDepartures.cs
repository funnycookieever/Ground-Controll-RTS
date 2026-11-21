using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Getting a commanded aeroplane off a field it is standing on, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Capture landings park wherever the aeroplane stops, often on the runway. Releasing them all in
/// the same tick makes them block each other, trip the 30 s stuck timer and get lifted by the rescue.
/// </para>
/// <para>
/// Departures from one field are queued (<see cref="RequestDeparture"/>): the next goes when the
/// last is 50 m up, gone, or two minutes old. One on a runway is lined up at the start of the
/// takeoff runway and handed to <c>AIPilotTakeoffState</c>, after moving other parked aircraft of
/// its side off that runway (<see cref="TryLineUp"/>). One on the apron taxis normally. A stuck
/// taxi or takeoff gets the same line-up; the lift is kept as the last rescue.
/// </para>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>Taxi speed cap for the first stretch out of the hangar, and how long that stretch is.</summary>
    /// <summary>How long a deck stays busy after a launch, for the hangar door to open and the
    /// airframe to appear on deck.</summary>
    private const float DeckDoorSeconds = 20f;

    private const float TaxiHangarExitSpeed = 3f;
    private const float TaxiHangarExitMeters = 50f;

    /// <summary><c>Hangar.clearDistance</c>'s default, the floor of the radius the hangar waits for an airframe to leave.</summary>
    private const float TaxiHangarClearMeters = 30f;

    /// <summary>How far past the nose the straight-out-of-the-hangar hold looks for the edge of the pavement.</summary>
    private const float TaxiDirtLookAheadMeters = 4f;

    /// <summary>
    /// Never look less far ahead than this from the airframe's centre: a Cricket that got the wheel
    /// back 9 m short of the dirt still ran wide onto it at 3 m/s, a Brawler at 16 m made the turn.
    /// </summary>
    private const float TaxiDirtLookAheadMinMeters = 16f;

    /// <summary>Clearance beside and behind the airframe that must be free of buildings before the hangar hold lets go.</summary>
    private const float TaxiHangarSideMarginMeters = 3f;

    /// <summary>The base taxi's own straight-out distance, which the hangar hold never shortens.</summary>
    private const float TaxiHangarMinStraightMeters = 10f;

    /// <summary>Taxi speed cap while the nose wheel is steering harder than <see cref="TaxiTurnYaw"/>.</summary>
    private const float TaxiTurnSpeed = 4f;
    private const float TaxiTurnYaw = 0.15f;

    /// <summary>
    /// The game's taxi runs past 15 m/s on straights and its spacing check is too slow to stop a
    /// follower in time.
    /// </summary>
    private const float TaxiStraightSpeed = 8f;

    private const float TaxiFollowScanMeters = 100f;
    private const float TaxiFollowConeCos = 0.7f;
    private const float TaxiFollowMarginMeters = 12f;
    private const float TaxiFollowDeceleration = 1.5f;

    private const float DepartureClearAltitudeMeters = 50f;

    /// <summary>Ground traffic this far from a runway's edge holds landers on it; farther out on the
    /// apron it is no business of the runway's, and holding every lander for it ran landings one at a time.</summary>
    private const float RunwayTrafficMeters = 60f;

    /// <summary>A takeoff blocked this long by aircraft standing on the runway ahead gives up.</summary>
    private const float BlockedGiveUpSeconds = 90f;

    /// <summary>An aeroplane standing still this long on its way to the runway is lined up instead.</summary>
    private const float TaxiWaitGiveUpSeconds = 90f;

    /// <summary>The aeroplane whose turn it is on a ship's deck gives the turn up after standing this long.</summary>
    private const float DeckStuckSeconds = 45f;

    /// <summary>An aeroplane that gave its deck turn up takes it back after this long with no one else claiming it.</summary>
    private const float DeckReclaimSeconds = 3f;

    /// <summary>A takeoff held this long for landing traffic stops waiting for anyone still in the air.</summary>
    private const float TakeoffPrioritySeconds = 10f;

    /// <summary>A refuelled aeroplane leaves its stand unless a landed one is taxiing in this close.</summary>
    private const float StandDepartureClearMeters = 300f;

    /// <summary>Taxiing this close to a runway end is about to line up there (the taxi hands over at 12 m).</summary>
    private const float ThresholdBusyMeters = 150f;
    private const float DepartureTimeoutSeconds = 120f;

    /// <summary>How long a queued aircraft waits for a landing to finish before going anyway, in
    /// case a landing registration outlived its aircraft.</summary>
    private const float LandingWaitSeconds = 90f;

    /// <summary>How far from a field's centre an aeroplane on the ground counts as on its apron.</summary>
    private const float ApronMeters = 3000f;

    /// <summary>Farthest a line-up may move an aircraft: past this it taxis to the threshold instead.
    /// The line-up straightens an aircraft standing there, it does not carry one there.</summary>
    private const float LineUpMaxMoveMeters = 30f;

    /// <summary>How far off the runway heading a takeoff may start before it is stopped and turned on
    /// the spot, and the speed it counts as stopped at.</summary>
    private const float LineUpTurnDegrees = 60f;
    private const float LineUpStopSpeed = 0.5f;

    /// <summary>The first overrun drops a type to its standard fuel at that field, the second
    /// closes the field to it. Below the speed, a slow roll off the pavement is a taxi, not a run.</summary>
    private const int OverrunsBeforeRefusal = 2;
    private const float OverrunMinSpeed = 20f;

    /// <summary>How fast a stopped aeroplane is pivoted, and how close to the runway heading it is
    /// left for the base game's own takeoff to straighten the rest (it starts the run inside 18).</summary>
    private const float LineUpPivotDegreesPerSecond = 30f;
    private const float LineUpDoneDegrees = 5f;

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTakeoffState, Airbase.Runway.RunwayUsage?> TakeoffRunwayRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTakeoffState, Airbase.Runway.RunwayUsage?>("runwayUsage");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTakeoffState, bool> StartedTakeoffRunRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTakeoffState, bool>("startedTakeoffRun");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTakeoffState, Airbase?> TakeoffAirbaseRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTakeoffState, Airbase?>("airbase");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotLandingState, Airbase?> LandingAirbaseRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotLandingState, Airbase?>("airbase");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotShortLandingState, Airbase?> ShortLandingAirbaseRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotShortLandingState, Airbase?>("airbase");

    private static readonly HarmonyLib.AccessTools.FieldRef<Airbase.Runway, Queue<Aircraft>> TakeoffQueueRef =
        HarmonyLib.AccessTools.FieldRefAccess<Airbase.Runway, Queue<Aircraft>>("takeoffQueue");

    /// <summary>Rebuild buffer for a runway's takeoff queue; a Queue cannot drop a middle entry.</summary>
    private static readonly List<object> QueueScratch = new();

    /// <summary>How often each type has run off the end of a runway at each field, still on its wheels.</summary>
    private static readonly Dictionary<(Airbase, AircraftDefinition), int> RunwayOverruns = new();
    private static readonly HashSet<Aircraft> OverrunNoted = new();

    /// <summary>Aircraft being pivoted onto the runway heading right now.</summary>
    private static readonly HashSet<Aircraft> LineUpTurns = new();

    private static readonly Dictionary<Airbase, Aircraft> Departing = new();
    private static readonly Dictionary<Airbase, float> DepartingSince = new();
    private static readonly List<Aircraft> DepartureQueue = new();
    private static readonly Dictionary<Aircraft, Airbase> DepartureFields = new();
    private static readonly Dictionary<Aircraft, float> DepartureQueuedAt = new();

    private static readonly Dictionary<Aircraft, (float NextAt, float Speed)> Following = new();

    /// <summary>When each takeoff first reached its hold before the roll.</summary>
    private static readonly Dictionary<Aircraft, float> LinedUpSince = new();

    /// <summary>When a takeoff about to roll first found the runway ahead of it occupied.</summary>
    private static readonly Dictionary<Aircraft, float> BlockedSince = new();

    /// <summary>When each outbound taxiing aeroplane stopped moving; see <see cref="GiveUpTaxiWait"/>.</summary>
    private static readonly Dictionary<Aircraft, float> TaxiStoodSince = new();

    /// <summary>When each outbound aeroplane on a ship's deck last moved; see <see cref="FreeStuckDeck"/>.</summary>
    private static readonly Dictionary<Aircraft, float> DeckMovedAt = new();

    /// <summary>Whose turn it is to leave each ship's deck, and when the turn last changed hands. Keyed
    /// on the field, or at a forward base on the hangar's own strip (<see cref="GetTurnKey"/>).</summary>
    private static readonly Dictionary<object, (Aircraft? Leader, float Since)> DeckTurns = new();

    /// <summary>When each ship's deck last launched; see <c>IsDeckBusy</c>.</summary>
    private static readonly Dictionary<Airbase, float> DeckLaunchedAt = new();

    /// <summary>Aeroplanes that stood too long with the deck turn and gave it to the next one.</summary>
    private static readonly HashSet<Aircraft> DeckYielded = new();

    /// <summary>Where each aeroplane's current taxi run began, keyed to that taxi state.</summary>
    private static readonly Dictionary<Aircraft, (AIPilotTaxiState? Taxi, Vector3 Origin)> TaxiOrigins = new();

    /// <summary>Taxiing out of a hangar, the straight-ahead hold has met the edge of the pavement.</summary>
    private static readonly HashSet<Aircraft> HangarHoldReleased = new();

    /// <summary>Still in the doorway of the hangar it spawned in, so it has right of way over passing taxi traffic.</summary>
    private static readonly HashSet<Aircraft> LeavingHangar = new();

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotTaxiState, Airbase?> TaxiAirbaseRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotTaxiState, Airbase?>("airbase");

    /// <summary>Queued departures that must line up when their turn comes, never taxi: ground rescues.</summary>
    private static readonly HashSet<Aircraft> LineUpDepartures = new();
    private static readonly List<Aircraft> StaleScratch = new();

    /// <summary>
    /// Called after every base-game taxi frame: caps an AI aeroplane that has not flown yet to walking
    /// pace out of the hangar and through turns, by braking when over the limit. Light airframes reach
    /// 7-8 m/s right out of the hangar and break the nose wheel in the first turn. The taxi to the stand
    /// after landing gets the same caps. Steering, path and throttle stay the game's own.
    /// </summary>
    internal static void LimitTaxiSpeed(Pilot? pilot)
    {
        Aircraft? aircraft = pilot == null ? null : pilot.aircraft;
        if (Instance == null
            || aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || GetFlyingPilotType(aircraft) != Pilot.PilotType.Plane)
        {
            return;
        }

        ControlInputs inputs = aircraft.GetInputs();
        bool outbound = !pilot!.flightInfo.HasTakenOff;

        // Walking pace is measured from where this taxi run began, so leaving a refuelling stand is also
        // capped. The straight-out hold below applies from the spawn only: hangars face out, but stands
        // are parked nose in.
        AIPilotTaxiState? taxi = pilot.currentState as AIPilotTaxiState;
        if (!TaxiOrigins.TryGetValue(aircraft, out (AIPilotTaxiState? Taxi, Vector3 Origin) run) || !ReferenceEquals(run.Taxi, taxi))
        {
            if (TaxiOrigins.Count > 512)
            {
                TaxiOrigins.Clear();
            }

            run = (taxi, aircraft.transform.position);
            TaxiOrigins[aircraft] = run;
            HangarHoldReleased.Remove(aircraft);
        }

        float moved = outbound ? Vector3.Distance(aircraft.transform.position, aircraft.startPosition.ToLocalPosition()) : float.MaxValue;
        bool nearHangar = outbound && Vector3.Distance(aircraft.transform.position, run.Origin) < TaxiHangarExitMeters;

        // The game holds the nose wheel straight for only the first 10 m, while wide jets are still in
        // the doorway. Hold straight until the airframe passes the hangar's own "has left" test
        // (Hangar.WaitForUnitToLeave) plus half a wingspan.
        // Never straight into the dirt though: once the ground just past the nose is bare terrain the
        // pathfinder gets the wheel back and keeps it.
        AircraftDefinition definition = aircraft.definition;
        float clear = Mathf.Max(definition.length, definition.width, definition.height, TaxiHangarClearMeters) + (definition.width * 0.5f);

        // Not when leaving a refuelling stand, which is parked nose in.
        bool fromStand = taxi != null && IsStandDeparture(aircraft, taxi);
        if (moved < clear && !fromStand)
        {
            if (LeavingHangar.Count > 512)
            {
                LeavingHangar.Clear();
            }

            LeavingHangar.Add(aircraft);
        }
        else
        {
            LeavingHangar.Remove(aircraft);
        }

        if (moved < clear && !fromStand && !HangarHoldReleased.Contains(aircraft))
        {
            if (CommanderGameAccess.IsDirtUnder(aircraft.transform.position + (aircraft.transform.forward * Mathf.Max((definition.length * 0.5f) + TaxiDirtLookAheadMeters, TaxiDirtLookAheadMinMeters)))
                || (moved > TaxiHangarMinStraightMeters && IsClearOfBuildings(aircraft, definition)))
            {
                if (HangarHoldReleased.Count > 512)
                {
                    HangarHoldReleased.Clear();
                }

                HangarHoldReleased.Add(aircraft);
            }
            else
            {
                inputs.yaw = 0f;
            }
        }

        // The landing hands over at the exit at whatever speed braking left, and the taxi then steers
        // hard. Brake in a straight line first; any turn waits for the straight-line cap.
        bool turning = Mathf.Abs(inputs.yaw) > TaxiTurnYaw;
        if (!outbound && (aircraft.speed > TaxiStraightSpeed * 2f || (turning && aircraft.speed > TaxiStraightSpeed)))
        {
            inputs.yaw = 0f;
        }

        float limit = nearHangar ? TaxiHangarExitSpeed : turning ? TaxiTurnSpeed : TaxiStraightSpeed;
        float following = GetFollowingSpeed(aircraft, taxi);
        limit = Mathf.Min(limit, following);

        // Waiting for the aeroplane ahead is not stuck.
        if (taxi != null && following < TaxiTurnSpeed)
        {
            HoldStuckTimers(taxi);
        }

        if (aircraft.speed > limit)
        {
            inputs.throttle = 0.01f;
            inputs.brake = 1f;
        }

        if (outbound && taxi != null)
        {
            FreeStuckDeck(aircraft, taxi);
            GiveUpTaxiWait(aircraft, taxi);
        }
    }

    /// <summary>
    /// One aeroplane at a time leaves a ship's deck; the rest stand on the brakes until it has gone.
    /// Called before every base-game taxi frame; true skips that frame for an aeroplane that waits.
    /// </summary>
    /// <remarks>
    /// The game's taxi cannot share a deck: hangars sit side by side and runways cross, and each
    /// aeroplane books its runway at once, so they deadlock at the runway crossing. A waiting
    /// aeroplane runs no taxi frame at all, so it books nothing and its stuck timers do not run; the
    /// one whose turn it is clears every other booking on the deck's runways.
    /// </remarks>
    internal static bool HoldForDeckTurn(Pilot? pilot, AIPilotTaxiState taxi)
    {
        Aircraft? aircraft = pilot == null ? null : pilot.aircraft;
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || !aircraft.IsServer
            || pilot!.flightInfo.HasTakenOff
            || GetFlyingPilotType(aircraft) != Pilot.PilotType.Plane)
        {
            return false;
        }

        Airbase? field = TaxiAirbaseRef(taxi);
        if (field == null || !TakesTurns(field))
        {
            return false;
        }

        object key = GetTurnKey(field, aircraft);
        DeckTurns.TryGetValue(key, out (Aircraft? Leader, float Since) turn);
        if (!ReferenceEquals(turn.Leader, aircraft)
            && !HasDeckTurn(turn.Leader)
            && (!DeckYielded.Contains(aircraft) || Time.time - turn.Since > DeckReclaimSeconds))
        {
            DeckYielded.Remove(aircraft);
            DeckTurns[key] = turn = (aircraft, Time.time);
        }

        if (ReferenceEquals(turn.Leader, aircraft))
        {
            Airbase.Runway? ownStrip = key as Airbase.Runway;
            for (int i = 0; field.runways != null && i < field.runways.Length; i++)
            {
                Queue<Aircraft>? queue = field.runways[i] == null || (ownStrip != null && field.runways[i] != ownStrip)
                    ? null
                    : TakeoffQueueRef(field.runways[i]);
                if (queue == null || queue.Count == 0 || (queue.Count == 1 && ReferenceEquals(queue.Peek(), aircraft)))
                {
                    continue;
                }

                bool booked = queue.Contains(aircraft);
                queue.Clear();
                if (booked)
                {
                    queue.Enqueue(aircraft);
                }
            }

            return false;
        }

        ControlInputs inputs = aircraft.GetInputs();
        inputs.throttle = 0.01f;
        inputs.brake = 1f;
        inputs.yaw = 0f;
        return true;
    }

    /// <summary>This taxi run began away from the hangar the aeroplane spawned in: a departure from a
    /// stand after a resupply. Read after <see cref="LimitTaxiSpeed"/> has recorded the run.</summary>
    internal static bool TryGetTaxiOrigin(Aircraft aircraft, AIPilotTaxiState taxi, out Vector3 origin)
    {
        origin = default;
        if (!TaxiOrigins.TryGetValue(aircraft, out (AIPilotTaxiState? Taxi, Vector3 Origin) run) || !ReferenceEquals(run.Taxi, taxi))
        {
            return false;
        }

        origin = run.Origin;
        return true;
    }

    /// <summary>
    /// Another aeroplane of the same side on the ground at this field and moving: one that landed
    /// (rolling out or taxiing in), or with <paramref name="inbound"/> false one taxiing out to the
    /// runway. Dustbowl has one taxiway for both, and traffic in both directions at once met head on.
    /// </summary>
    internal static bool IsTaxiingAt(Airbase field, Aircraft asker, bool inbound, float nearAskerMeters = 0f, float minSpeed = 0f, Airbase.Runway? nearRunway = null)
    {
        if (field.center == null)
        {
            return false;
        }

        Vector3 centre = field.center.position;
        List<Aircraft> all = UnitRegistry.allAircraft;
        for (int i = 0; i < all.Count; i++)
        {
            Aircraft other = all[i];
            if (other == null
                || ReferenceEquals(other, asker)
                || other.disabled
                || other.radarAlt > 5f
                || other.speed < minSpeed
                || !ReferenceEquals(other.NetworkHQ, asker.NetworkHQ)
                || other.pilots == null
                || other.pilots.Length == 0
                || other.pilots[0] == null
                || (other.transform.position - centre).sqrMagnitude > ApronMeters * ApronMeters
                || (nearAskerMeters > 0f && (other.transform.position - asker.transform.position).sqrMagnitude > nearAskerMeters * nearAskerMeters)
                || (nearRunway != null && !IsBesideRunway(nearRunway, other)))
            {
                continue;
            }

            Pilot pilot = other.pilots[0];

            // Holding short of the runway is not in a lander's way: landers never use the entry.
            // Counted, a departure waiting there for the landers kept every lander off final.
            if (!inbound && other.speed < 1f && IsNearRunwayEnd(field, other))
            {
                continue;
            }

            bool moving = inbound
                ? (pilot.currentState is AIPilotTaxiState && pilot.flightInfo.HasTakenOff) || pilot.currentState is AIPilotLandingState
                : pilot.currentState is AIPilotTaxiState && !pilot.flightInfo.HasTakenOff;
            if (moving)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>On the runway or within <see cref="RunwayTrafficMeters"/> of its edge: entering, crossing or leaving it.</summary>
    internal static bool IsBesideRunway(Airbase.Runway runway, Aircraft other)
    {
        if (runway.Start == null)
        {
            return true;
        }

        Vector3 offset = runway.GetNearestPoint(other.transform.position, extend: false) - other.transform.position;
        offset.y = 0f;
        return offset.magnitude < (runway.GetWidth() * 0.5f) + RunwayTrafficMeters;
    }

    internal static bool IsStandDeparture(Aircraft aircraft, AIPilotTaxiState taxi)
    {
        // Outbound only: backing up a landed aeroplane can stop it on the runway.
        return aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && aircraft.pilots[0] != null
            && !aircraft.pilots[0].flightInfo.HasTakenOff
            && TaxiOrigins.TryGetValue(aircraft, out (AIPilotTaxiState? Taxi, Vector3 Origin) run)
            && ReferenceEquals(run.Taxi, taxi)
            && Vector3.Distance(run.Origin, aircraft.startPosition.ToLocalPosition()) > TaxiHangarExitMeters;
    }

    /// <summary>
    /// Called before every base-game takeoff frame: an AI aeroplane that reaches its runway pointing
    /// well away from the takeoff direction is braked to a stop and turned on the spot, instead of
    /// letting the takeoff state swing it round at taxi speed. True skips the base game's frame.
    /// </summary>
    /// <remarks>
    /// Where the taxi route runs up the runway itself, the aeroplane reaches the threshold facing off
    /// the end. <c>AIPilotTakeoffState</c> then makes a wide U-turn at 5-6 m/s off the paved end.
    /// </remarks>
    internal static bool HoldForLineUp(AIPilotTakeoffState state, Pilot? pilot)
    {
        Aircraft? aircraft = pilot == null ? null : pilot.aircraft;
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || aircraft.radarAlt > 1f
            || StartedTakeoffRunRef(state)
            || GetFlyingPilotType(aircraft) != Pilot.PilotType.Plane)
        {
            return false;
        }

        Airbase.Runway.RunwayUsage? usage = TakeoffRunwayRef(state);
        if (!usage.HasValue || usage.Value.Runway == null || usage.Value.Runway.SkiJump)
        {
            return false;
        }

        // Not onto a runway someone is landing on (see MayTurnFinal), nor down one with an aeroplane
        // standing on it. Only wait: shoving the blocker aside tips it over on rough ground. Past
        // BlockedGiveUpSeconds the airframe goes back to stock to break mutual waits.
        Aircraft? blocker = FindAircraftAhead(usage.Value, aircraft);
        bool blocked = blocker != null;
        if (!blocked)
        {
            BlockedSince.Remove(aircraft);
        }
        else if (!BlockedSince.TryGetValue(aircraft, out float since))
        {
            BlockedSince[aircraft] = Time.time;
        }
        else if (Time.time - since > BlockedGiveUpSeconds)
        {
            BlockedSince.Remove(aircraft);
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} could not take off past aircraft standing on runway {usage.Value.Runway.GetName(usage.Value.Reverse)} for {BlockedGiveUpSeconds:0} s ({CommanderGameAccess.GetUnitLabel(blocker!)}, {blocker!.pilots[0].currentState?.GetType().Name} '{blocker.pilots[0].currentState?.stateDisplayName}'); returned to stock.");
            aircraft.ReturnToInventory();
            return true;
        }

        // Lined up for long, the takeoff goes first and only waits for a lander already rolling out;
        // otherwise landers keep going round because it stands on the runway.
        if (!LinedUpSince.TryGetValue(aircraft, out float linedUp))
        {
            if (LinedUpSince.Count > 256)
            {
                LinedUpSince.Clear();
            }

            LinedUpSince[aircraft] = linedUp = Time.time;
        }

        bool priority = Time.time - linedUp > TakeoffPrioritySeconds;
        if (IsLandingTrafficFor(usage.Value.Runway, aircraft, groundOnly: priority) || blocked)
        {
            ControlInputs waiting = aircraft.GetInputs();
            waiting.throttle = 0f;
            waiting.brake = 1f;
            return true;
        }

        Vector3 runway = usage.Value.Runway.GetDirection(usage.Value.Reverse);
        runway.y = 0f;
        Vector3 facing = aircraft.transform.forward;
        facing.y = 0f;
        float offDegrees = Vector3.Angle(facing, runway);
        if (runway.sqrMagnitude < 0.01f || offDegrees < (LineUpTurns.Contains(aircraft) ? LineUpDoneDegrees : LineUpTurnDegrees))
        {
            LineUpTurns.Remove(aircraft);
            LinedUpSince.Remove(aircraft);
            return false;
        }

        if (aircraft.speed > LineUpStopSpeed)
        {
            ControlInputs inputs = aircraft.GetInputs();
            inputs.throttle = 0f;
            inputs.brake = 1f;
            return true;
        }

        // Stopped: pivot on the spot a little every physics step, the way a tug turns it, yaw only so
        // pitch and roll stay whatever the gear is sitting on. Every part moves with the airframe
        // (MoveAircraft); turning the airframe alone is what tore the first version's Ifrits apart.
        if (LineUpTurns.Add(aircraft))
        {
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} reached runway {usage.Value.Runway.GetName(usage.Value.Reverse)} "
                    + $"{offDegrees:0} deg off its heading; stopped and pivoting on the spot instead of swinging round at the threshold.");
        }

        float step = Mathf.Min(offDegrees - LineUpDoneDegrees, LineUpPivotDegreesPerSecond * Time.fixedDeltaTime);
        Quaternion pivot = Quaternion.AngleAxis(Mathf.Sign(Vector3.SignedAngle(facing, runway, Vector3.up)) * step, Vector3.up);
        CommanderGameAccess.MoveAircraft(aircraft, aircraft.transform.position, pivot * aircraft.transform.rotation, Vector3.zero);
        ControlInputs held = aircraft.GetInputs();
        held.throttle = 0f;
        held.brake = 1f;
        if (offDegrees - step > LineUpDoneDegrees)
        {
            return true;
        }

        LineUpTurns.Remove(aircraft);
        LinedUpSince.Remove(aircraft);
        return false;
    }

    /// <summary>
    /// Called before every base-game takeoff frame: notes an AI aeroplane that has reached the end of
    /// its runway without leaving the ground.
    /// </summary>
    /// <remarks>
    /// <c>AIPilotTakeoffState</c> holds the nose nearly level, so an aeroplane only unsticks well past
    /// its authored <c>takeoffSpeed</c>, and <c>takeoffDistance</c> does not reflect the real roll.
    /// The distance has to be measured.
    /// </remarks>
    internal static void WatchForOverrun(AIPilotTakeoffState state, Pilot? pilot)
    {
        Aircraft? aircraft = pilot == null ? null : pilot.aircraft;
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || aircraft.radarAlt > 1f
            || aircraft.speed < OverrunMinSpeed
            || OverrunNoted.Contains(aircraft)
            || GetFlyingPilotType(aircraft) != Pilot.PilotType.Plane)
        {
            return;
        }

        Airbase.Runway.RunwayUsage? usage = TakeoffRunwayRef(state);
        Airbase? field = TakeoffAirbaseRef(state);
        Airbase.Runway? runway = usage.HasValue ? usage.Value.Runway : null;
        if (field == null || runway == null || runway.Start == null || runway.End == null || runway.SkiJump)
        {
            return;
        }

        Transform end = usage!.Value.Reverse ? runway.Start : runway.End;
        if (runway.AircraftOnRunway(aircraft)
            || Vector3.Dot(aircraft.transform.position - end.position, runway.GetDirection(usage.Value.Reverse)) <= 0f)
        {
            return;
        }

        OverrunNoted.Add(aircraft);
        (Airbase, AircraftDefinition) key = (field, aircraft.definition);
        RunwayOverruns.TryGetValue(key, out int count);
        RunwayOverruns[key] = ++count;
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} ran off the end of runway {runway.GetName(usage.Value.Reverse)} "
                + $"at {CommanderCaptureService.GetAirbaseLabel(field)} ({runway.Length:0} m) still on its wheels at {aircraft.speed:0} m/s "
                + $"(overrun {count}); "
                + (count >= OverrunsBeforeRefusal
                    ? "that field no longer launches this type."
                    : "the next one leaves there with its standard fuel instead of full tanks."));
    }

    /// <summary>
    /// Called before every pilot state change: hands back the runway registers the state being left
    /// still holds, whenever the aeroplane is being taken out of the ground sequence altogether.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Airbase.Runway</c> keeps <c>landingList</c> and <c>takeoffQueue</c>, and <c>LeaveState</c>
    /// clears neither. Entries are only removed on the normal hand-overs, and the game's cleanup only
    /// drops <c>null</c> entries or a destroyed queue head, so a pilot pulled out of those states by
    /// the mod stays registered for the rest of the match.
    /// </para>
    /// <para>
    /// <c>IsAvailableForTakeoff</c> refuses everyone while <c>landingList</c> is not empty, and the
    /// taxi's <c>Wait()</c> zeroes its stuck timers, so a stale entry freezes every departure at that
    /// field with nothing logged.
    /// </para>
    /// <para>
    /// Only an exit out of the sequence releases. Taxi to takeoff deliberately keeps the queue entry,
    /// and landing to taxi deregisters itself, so releasing on those would open the runway under a
    /// rolling aeroplane or do nothing.
    /// </para>
    /// </remarks>
    internal static void ReleaseRunwayReservations(Pilot? pilot, PilotBaseState? next)
    {
        PilotBaseState? leaving = pilot == null ? null : pilot.currentState;
        Aircraft? aircraft = pilot == null ? null : pilot.aircraft;
        if (leaving == null
            || aircraft == null
            || ReferenceEquals(leaving, next)
            || next is AIPilotTaxiState or AIPilotTakeoffState or AIPilotLandingState or AIPilotShortLandingState)
        {
            return;
        }

        Airbase? field = leaving switch
        {
            AIPilotLandingState landing => LandingAirbaseRef(landing),
            AIPilotShortLandingState shortLanding => ShortLandingAirbaseRef(shortLanding),
            AIPilotTaxiState taxi => TaxiAirbaseRef(taxi),
            AIPilotTakeoffState takeoff => TakeoffAirbaseRef(takeoff),
            _ => null,
        };

        for (int i = 0; field != null && field.runways != null && i < field.runways.Length; i++)
        {
            Airbase.Runway runway = field.runways[i];
            if (runway == null)
            {
                continue;
            }

            bool held = runway.GetLandingList().Contains(aircraft);
            runway.DeregisterLanding(aircraft);
            if (!(RemoveFromQueue(TakeoffQueueRef(runway), aircraft) | held))
            {
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} left {leaving.GetType().Name} still booked on runway "
                    + $"{runway.GetName(reverse: false)} at {CommanderCaptureService.GetAirbaseLabel(field)}; "
                    + "released it so the field keeps launching.");
        }
    }

    /// <summary>
    /// Called after the base game's own 5 s runway sweep: drops every landing-list and takeoff-queue
    /// entry whose aircraft is gone, wrecked, or (on the host, where AI pilots run) no longer in a
    /// state that books the runway at all.
    /// </summary>
    /// <remarks>
    /// <see cref="ReleaseRunwayReservations"/> only catches state changes. An airframe written off on
    /// the taxiway keeps its entry as a disabled object. The game only drops a disabled queue head in
    /// <c>IsAvailableForTakeoff</c>, which the taxi asks within 100 m of the threshold; before that,
    /// <c>OtherAircraftUsingRunway</c> counts the wreck and the taxi waits forever.
    /// </remarks>
    internal static void PurgeStaleRunwayEntries(Airbase.Runway? runway, Airbase? field)
    {
        if (runway == null)
        {
            return;
        }

        List<Aircraft> landings = runway.GetLandingList();
        for (int i = landings.Count - 1; i >= 0; i--)
        {
            if (IsStaleBooking(landings[i], landing: true))
            {
                LogStaleBooking(landings[i], runway, field);
                landings.RemoveAt(i);
            }
        }

        Queue<Aircraft>? queue = TakeoffQueueRef(runway);
        if (queue == null || queue.Count == 0)
        {
            return;
        }

        StaleScratch.Clear();
        foreach (Aircraft queued in queue)
        {
            if (IsStaleBooking(queued, landing: false))
            {
                StaleScratch.Add(queued);
            }
        }

        for (int i = 0; i < StaleScratch.Count; i++)
        {
            LogStaleBooking(StaleScratch[i], runway, field);
            RemoveFromQueue(queue, StaleScratch[i]);
        }

        StaleScratch.Clear();
    }

    /// <summary>Another aeroplane on the ground on this runway, ahead of a takeoff about to roll.</summary>
    /// <summary>
    /// An AI aeroplane taxiing out may enter <paramref name="runway"/> although landings are booked on
    /// it. The game holds departures while anything is on the landing list, which includes landers
    /// 5 km out or in the hold. Only a committed lander holds it now: rolling out, on final within
    /// 3 km, or going round low over the strip. The takeoff queue's order is kept, and
    /// <see cref="MayTurnFinal"/> keeps the next lander off final while it rolls.
    /// </summary>
    internal static bool MayDepartBeforeLanders(Airbase.Runway runway, Aircraft? aircraft)
    {
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || aircraft.pilots[0] == null
            || aircraft.pilots[0].flightInfo.HasTakenOff
            || GetFlyingPilotType(aircraft) != Pilot.PilotType.Plane
            || IsLandingTrafficFor(runway, aircraft, groundOnly: false)
            || runway.CrossingRunwaysInUse())
        {
            return false;
        }

        Queue<Aircraft>? queue = TakeoffQueueRef(runway);
        Aircraft? head = queue == null || queue.Count == 0 ? null : queue.Peek();
        return head == null || head.disabled || ReferenceEquals(head, aircraft);
    }

    /// <summary>
    /// Something on the pavement ahead, and nearer than <paramref name="withinMeters"/> along the
    /// runway counting how far it must still roll (<see cref="RollingLead"/>). The direction must be
    /// normalised: the runway's own is as long as the runway, and unnormalised a rollout never braked
    /// for anyone ahead.
    /// </summary>
    internal static Aircraft? FindAircraftAhead(Airbase.Runway.RunwayUsage usage, Aircraft aircraft, float withinMeters = float.MaxValue)
    {
        Vector3 direction = usage.Runway.GetDirection(usage.Reverse).normalized;
        Vector3 position = aircraft.transform.position;
        List<Aircraft> all = UnitRegistry.allAircraft;
        for (int i = 0; i < all.Count; i++)
        {
            Aircraft other = all[i];
            if (other != null
                && !ReferenceEquals(other, aircraft)
                && !other.disabled
                && other.radarAlt < 5f
                && Vector3.Dot(other.transform.position - position, direction) is > 0f and var along
                && along + RollingLead(other, direction) < withinMeters
                && IsOnPavement(usage.Runway, other))
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>A refuelled aeroplane is queued to leave <paramref name="field"/>.</summary>
    internal static bool HasQueuedDeparture(Airbase field, Aircraft asker)
    {
        for (int i = 0; i < DepartureQueue.Count; i++)
        {
            Aircraft queued = DepartureQueue[i];
            if (queued != null
                && !queued.disabled
                && !ReferenceEquals(queued, asker)
                && DepartureFields.TryGetValue(queued, out Airbase queuedField)
                && ReferenceEquals(queuedField, field))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The fuel a resupply should fill this type to here: its standard load once it has run off this field's runway.</summary>
    internal static float GetSafeFuelLevel(Airbase? airbase, AircraftDefinition definition)
    {
        return airbase != null && GetRunwayOverruns(airbase, definition) > 0 ? definition.aircraftParameters.DefaultFuelLevel : 1f;
    }

    /// <summary>
    /// Centre on the runway itself. <c>Runway.AircraftOnRunway</c> adds the aeroplane's radius, so a
    /// taxi holding short at the edge counts, and it and the takeoff would each wait for the other.
    /// </summary>
    private static bool IsOnPavement(Airbase.Runway runway, Aircraft aircraft)
    {
        Vector3 offset = runway.GetNearestPoint(aircraft.transform.position, extend: false) - aircraft.transform.position;
        offset.y = 0f;
        return offset.magnitude < runway.GetWidth() * 0.5f;
    }

    /// <summary>
    /// A ship's deck, or a forward base: its aeroplanes leave one at a time. A forward base's hangars
    /// face each other across one road, so two aircraft would otherwise brake for each other for good.
    /// </summary>
    private static bool TakesTurns(Airbase field)
    {
        return field.TryGetAttachedUnit(out _) || CommanderForwardBase.IsForwardBase(field);
    }

    /// <summary>
    /// What an aeroplane takes its turn on. A forward base hangar with a strip of its own launches
    /// from it while another hangar launches from its own: one turn for the whole base held a shelter
    /// jet on a separate road behind the medium hangar's.
    /// </summary>
    private static object GetTurnKey(Airbase field, Aircraft aircraft)
    {
        return (CommanderForwardBase.IsForwardBase(field) ? CommanderForwardBase.GetHomeStrip(aircraft) : null) ?? (object)field;
    }

    /// <summary>Still on the deck, taxiing or rolling for its takeoff.</summary>
    private static bool HasDeckTurn(Aircraft? leader)
    {
        return leader != null
            && !leader.disabled
            && leader.pilots?.Length > 0
            && !leader.pilots[0].flightInfo.HasTakenOff
            && leader.pilots[0].currentState is AIPilotTaxiState or AIPilotTakeoffState;
    }

    /// <summary>
    /// The aeroplane whose deck turn it is gives the turn to the next one after standing still for
    /// <see cref="DeckStuckSeconds"/>, and waits behind it. Whatever holds it (a lander on a crossing
    /// runway, its nose against a neighbour) should not keep the whole deck on the brakes.
    /// </summary>
    private static void FreeStuckDeck(Aircraft aircraft, AIPilotTaxiState taxi)
    {
        Airbase? field = TaxiAirbaseRef(taxi);
        if (field == null
            || aircraft.speed > 0.5f
            || !DeckTurns.TryGetValue(GetTurnKey(field, aircraft), out (Aircraft? Leader, float Since) turn)
            || !ReferenceEquals(turn.Leader, aircraft))
        {
            DeckMovedAt.Remove(aircraft);
            return;
        }

        if (!DeckMovedAt.TryGetValue(aircraft, out float since))
        {
            if (DeckMovedAt.Count > 256)
            {
                DeckMovedAt.Clear();
            }

            DeckMovedAt[aircraft] = Time.time;
            return;
        }

        if (Time.time - since > DeckStuckSeconds)
        {
            DeckMovedAt.Remove(aircraft);
            if (DeckYielded.Count > 256)
            {
                DeckYielded.Clear();
            }

            DeckYielded.Add(aircraft);
            DeckTurns[GetTurnKey(field, aircraft)] = (null, Time.time);
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} stood on the deck of {field.name} for {DeckStuckSeconds:0} s "
                    + $"('{taxi.stateDisplayName}'); gave its turn to the next aeroplane and waits behind it.");
        }
    }

    /// <summary>
    /// An aeroplane that has stood still on its way out for <see cref="TaxiWaitGiveUpSeconds"/> is
    /// lined up on the runway instead, or sent to stock if there is none. The game's <c>Wait()</c>
    /// zeroes both stuck timers, so it never gives up on its own (e.g. at crossing runways).
    /// <see cref="FreeStuckDeck"/> handles ship decks.
    /// </summary>
    private static void GiveUpTaxiWait(Aircraft aircraft, AIPilotTaxiState taxi)
    {
        Airbase? field = TaxiAirbaseRef(taxi);
        if (aircraft.speed > LineUpStopSpeed
            || field == null
            || field.runways == null
            || TakesTurns(field)
            || DepartureFields.ContainsKey(aircraft))
        {
            TaxiStoodSince.Remove(aircraft);
            return;
        }

        if (!TaxiStoodSince.TryGetValue(aircraft, out float since))
        {
            if (TaxiStoodSince.Count > 512)
            {
                TaxiStoodSince.Clear();
            }

            TaxiStoodSince[aircraft] = Time.time;
            return;
        }

        if (Time.time - since < TaxiWaitGiveUpSeconds)
        {
            return;
        }

        TaxiStoodSince.Remove(aircraft);

        // Stopped mid-runway (a runway crossing) it can never be lined up from there, the line-up moves
        // it at most LineUpMaxMoveMeters, and parked in the queue it stands across that runway, blocking
        // every other departure and lander. Not a failure of the type at this field: no NoteFailedTakeoff.
        if (IsOnAnyRunway(aircraft, field) && !IsNearRunwayEnd(field, aircraft))
        {
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} stood {TaxiWaitGiveUpSeconds:0} s on a runway at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(field)} ('{taxi.stateDisplayName}'), too far from a threshold to line up; returned to stock.");
            aircraft.ReturnToInventory();
            return;
        }

        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} stood {TaxiWaitGiveUpSeconds:0} s on the way to the runway at "
                + $"{CommanderCaptureService.GetAirbaseLabel(field)} ('{taxi.stateDisplayName}'); lining it up instead.");

        // Parked first, which also drops its runway booking (ReleaseRunwayReservations): an aeroplane
        // heading one runway's queue while it waits at the crossing of another is what the one
        // heading that other queue is waiting for. And a live taxi would roll on by itself once the
        // other has gone, then be lined up again from the air when its queued departure came round.
        Pilot pilot = aircraft.pilots[0];
        pilot.SwitchState(pilot.parkedState);
        ControlInputs inputs = aircraft.GetInputs();
        inputs.throttle = 0f;
        inputs.brake = 1f;
        if (!TryRescueByLineUp(aircraft))
        {
            NoteFailedTakeoff(aircraft);
            aircraft.ReturnToInventory();
        }
    }

    /// <summary>
    /// The speed this taxiing aeroplane can still stop from short of whatever is ahead of it. The game's
    /// once-a-second spacing check is too coarse. Rescanned four times a second per aeroplane.
    /// </summary>
    private static float GetFollowingSpeed(Aircraft aircraft, AIPilotTaxiState? taxi)
    {
        if (Following.TryGetValue(aircraft, out (float NextAt, float Speed) cached) && Time.time < cached.NextAt)
        {
            return cached.Speed;
        }

        float speed = float.MaxValue;
        Airbase? field = taxi == null ? null : TaxiAirbaseRef(taxi);

        // Not on a ship's deck: hangars are closer together than any margin, so every aeroplane would
        // brake for its neighbour forever. The deck is left to the game's own taxi.
        if (field != null && !TakesTurns(field))
        {
            Vector3 position = aircraft.transform.position;
            Vector3 forward = aircraft.transform.forward;
            List<Aircraft> others = field.ControlledAircraft;
            for (int i = 0; i < others.Count; i++)
            {
                Aircraft other = others[i];
                if (other == null || ReferenceEquals(other, aircraft) || other.disabled || other.radarAlt > 5f)
                {
                    continue;
                }

                Vector3 offset = other.transform.position - position;
                float distance = offset.magnitude;
                if (distance > TaxiFollowScanMeters || Vector3.Dot(offset / Mathf.Max(distance, 0.01f), forward) < TaxiFollowConeCos)
                {
                    continue;
                }

                // The one still in its hangar goes first, otherwise both brake for each other at the margin.
                // It still stops short of touching; the one outside keeps the full margin and follows.
                // Also ignore a takeoff standing on the threshold: HoldForLineUp holds it until this one is
                // off the runway. Only while it is moving: one stopped in the doorway is waiting for this one.
                if (LeavingHangar.Contains(other) && !LeavingHangar.Contains(aircraft) && other.speed < 0.5f)
                {
                    continue;
                }

                bool rightOfWay = (LeavingHangar.Contains(aircraft) && !LeavingHangar.Contains(other))
                    || (aircraft.pilots[0].flightInfo.HasTakenOff && other.speed < 1f && IsHeldTakeoff(other));
                float gap = distance - aircraft.maxRadius - other.maxRadius - (rightOfWay ? 0f : TaxiFollowMarginMeters);
                speed = Mathf.Min(speed, Mathf.Sqrt(2f * TaxiFollowDeceleration * Mathf.Max(gap, 0f)));
            }

            if (MustGiveWay(aircraft, taxi!, field))
            {
                speed = 0f;
            }
        }

        // Keyed by airframe and never pruned per unit; a long match just starts the cache again.
        if (Following.Count > 512)
        {
            Following.Clear();
        }

        Following[aircraft] = (Time.time + 0.25f, speed);
        return speed;
    }

    private static bool IsHeldTakeoff(Aircraft aircraft)
    {
        return aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && aircraft.pilots[0] != null
            && aircraft.pilots[0].currentState is AIPilotTakeoffState;
    }

    private static bool IsStaleBooking(Aircraft? aircraft, bool landing)
    {
        if (aircraft == null || aircraft.disabled)
        {
            return true;
        }

        // A client never runs the AI pilot states, so only the host can judge by state.
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (!aircraft.IsServer || pilot == null)
        {
            return false;
        }

        PilotBaseState? state = pilot.currentState;
        return landing
            ? state is not (AIPilotLandingState or AIPilotShortLandingState)
            : state is not (AIPilotTaxiState or AIPilotTakeoffState);
    }

    private static void LogStaleBooking(Aircraft? aircraft, Airbase.Runway runway, Airbase? field)
    {
        if (aircraft == null)
        {
            return;
        }

        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} was still booked on runway {runway.GetName(reverse: false)}"
                + (field != null ? $" at {CommanderCaptureService.GetAirbaseLabel(field)}" : string.Empty)
                + (aircraft.disabled ? " as a wreck" : string.Empty)
                + "; released it so the field keeps launching.");
    }

    /// <summary>
    /// Takes one entry out of a queue wherever it sits in it, leaving the rest in order. True if it
    /// was in there. The base game can only ever dequeue the head of a runway's takeoff queue, so an
    /// entry left behind further down it blocks the strip the moment it reaches the front.
    /// </summary>
    private static bool RemoveFromQueue<T>(Queue<T>? queue, T entry)
        where T : class
    {
        if (queue == null || queue.Count == 0)
        {
            return false;
        }

        bool found = false;
        QueueScratch.Clear();
        while (queue.Count > 0)
        {
            T queued = queue.Dequeue();
            if (ReferenceEquals(queued, entry))
            {
                found = true;
            }
            else
            {
                QueueScratch.Add(queued);
            }
        }

        for (int i = 0; i < QueueScratch.Count; i++)
        {
            queue.Enqueue((T)QueueScratch[i]);
        }

        QueueScratch.Clear();
        return found;
    }

    private static int GetRunwayOverruns(Airbase airbase, AircraftDefinition definition)
    {
        return RunwayOverruns.TryGetValue((airbase, definition), out int count) ? count : 0;
    }

    private static void ResetRunwayOverruns()
    {
        RunwayOverruns.Clear();
        OverrunNoted.Clear();
    }

    /// <summary>Queues a parked fixed-wing aircraft to leave <paramref name="field"/>, which its side holds.</summary>
    private static void RequestDeparture(Aircraft aircraft, Airbase field, bool lineUp = false)
    {
        if (lineUp)
        {
            LineUpDepartures.Add(aircraft);
        }

        if (!DepartureFields.ContainsKey(aircraft))
        {
            DepartureQueue.Add(aircraft);
            DepartureQueuedAt[aircraft] = Time.time;
        }

        DepartureFields[aircraft] = field;
        TickDepartures();
    }

    /// <summary>On the 2 s landing sweep: starts the next departure at every field that is free.</summary>
    private static void TickDepartures()
    {
        for (int i = 0; i < DepartureQueue.Count; i++)
        {
            Aircraft aircraft = DepartureQueue[i];

            // The field has to still be the aircraft's: AIPilotTakeoffState ejects the pilot when it
            // is not, so a field that changed hands while the departure was queued is a departure
            // that must not start.
            if (aircraft == null
                || aircraft.disabled
                || !DepartureFields.TryGetValue(aircraft, out Airbase field)
                || field == null
                || field.disabled
                || field.CurrentHQ != aircraft.NetworkHQ)
            {
                DepartureQueue.RemoveAt(i);
                DepartureFields.Remove(aircraft!);
                DepartureQueuedAt.Remove(aircraft!);
                LineUpDepartures.Remove(aircraft!);
                i--;
                continue;
            }

            if (IsFieldBusy(field, aircraft))
            {
                continue;
            }

            DepartureQueue.RemoveAt(i);
            DepartureFields.Remove(aircraft);
            DepartureQueuedAt.Remove(aircraft);
            i--;
            StartDeparture(aircraft, field);
        }
    }

    private static bool IsFieldBusy(Airbase field, Aircraft aircraft)
    {
        // A refuelled aeroplane leaves its stand as soon as the timer ends and waits at the hold point by
        // the runway entry, not on the stand. Only a landed aeroplane still taxiing in close enough to
        // meet it keeps it on the stand. Once taxiing out, MayTurnFinal keeps the next lander off final.
        if (!LineUpDepartures.Contains(aircraft) && !IsOnAnyRunway(aircraft, field) && HasTaxiways(field))
        {
            // Only one actually rolling: a lander stopped behind, waiting for the stand this one is on,
            // kept a refuelled Compass there for minutes while a stream landed in trail.
            return IsTaxiingAt(field, aircraft, inbound: true, StandDepartureClearMeters, minSpeed: 1f);
        }

        bool waitForLandings = !DepartureQueuedAt.TryGetValue(aircraft, out float queuedAt)
            || Time.time - queuedAt < LandingWaitSeconds;
        for (int i = 0; waitForLandings && field.runways != null && i < field.runways.Length; i++)
        {
            if (field.runways[i] != null && field.runways[i].LandingsInProgress())
            {
                return true;
            }
        }

        if (Departing.TryGetValue(field, out Aircraft other)
            && other != null
            && !other.disabled
            && !ReferenceEquals(other, aircraft)
            && other.radarAlt < DepartureClearAltitudeMeters
            && Time.time - DepartingSince[field] < DepartureTimeoutSeconds)
        {
            return true;
        }

        // Not out of a stand while a landed aeroplane is taxiing in on the same taxiway, however long
        // this one has waited: an inbound taxi always ends.
        if (IsTaxiingAt(field, aircraft, inbound: true))
        {
            return true;
        }

        return IsRunwayInUseByOthers(field, aircraft, waitForLandings);
    }

    /// <summary>
    /// A hangar launch the mod did not start: rolling, lined up, or taxiing up to a threshold.
    /// <see cref="Departing"/> only holds the mod's own departures. A taxiing one only counts while
    /// <paramref name="countTaxiing"/> is set, since waiting on parked taxiers can deadlock.
    /// </summary>
    private static bool IsRunwayInUseByOthers(Airbase field, Aircraft aircraft, bool countTaxiing)
    {
        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq?.factionUnits == null || field.runways == null)
        {
            return false;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not Aircraft other
                || ReferenceEquals(other, aircraft)
                || other.disabled
                || other.radarAlt > DepartureClearAltitudeMeters
                || other.pilots == null
                || other.pilots.Length == 0)
            {
                continue;
            }

            PilotBaseState? state = other.pilots[0].currentState;
            bool rolling = state is AIPilotTakeoffState;
            bool taxiing = countTaxiing && state is AIPilotTaxiState;
            if (!rolling && !taxiing)
            {
                continue;
            }

            for (int i = 0; i < field.runways.Length; i++)
            {
                Airbase.Runway runway = field.runways[i];
                if (runway?.Start == null || runway.End == null)
                {
                    continue;
                }

                if ((rolling && runway.AircraftOnRunway(other))
                    || IsNear(other, runway.Start.position, ThresholdBusyMeters)
                    || IsNear(other, runway.End.position, ThresholdBusyMeters))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsNearRunwayEnd(Airbase field, Aircraft aircraft)
    {
        for (int i = 0; field.runways != null && i < field.runways.Length; i++)
        {
            Airbase.Runway runway = field.runways[i];
            if (runway?.Start != null
                && runway.End != null
                && (IsNear(aircraft, runway.Start.position, ThresholdBusyMeters) || IsNear(aircraft, runway.End.position, ThresholdBusyMeters)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNear(Aircraft aircraft, Vector3 point, float meters)
    {
        return (aircraft.transform.position - point).sqrMagnitude < meters * meters;
    }

    private static void StartDeparture(Aircraft aircraft, Airbase field)
    {
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (pilot == null)
        {
            return;
        }

        Departing[field] = aircraft;
        DepartingSince[field] = Time.time;
        aircraft.SetGear(deployed: true);

        // On a runway already, at a field with no taxiways, or a ground rescue: line it up and roll.
        // Without a taxi network AIPilotTaxiState drives a straight line to the threshold through
        // whatever is in between, and a rescue's taxi has already failed from where it stands.
        bool rescue = LineUpDepartures.Remove(aircraft);
        if ((rescue || IsOnAnyRunway(aircraft, field) || !HasTaxiways(field)) && TryLineUp(aircraft, field))
        {
            pilot.AITakeoffState = new AIPilotTakeoffState();
            pilot.SwitchState(pilot.AITakeoffState);
            return;
        }

        // Taxi to the threshold first. The takeoff state rolls from wherever the aeroplane stands,
        // and after a resupply that was the far end of the strip with no runway left. The base-game
        // taxi only heads for the runway on an airframe that has not flown yet (a flown one goes to
        // a stand and ejects), hence the flag; a fresh state because EnterState does not reset its
        // disembarking flag. It hands over to AIPilotTakeoffState 12 m from the threshold, and a
        // stuck taxi is caught by the ground rescue, which lines it up instead.
        pilot.flightInfo.HasTakenOff = false;
        pilot.AITaxiState = new AIPilotTaxiState();
        pilot.SwitchState(pilot.AITaxiState);
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} taxiing to take off from {CommanderCaptureService.GetAirbaseLabel(field)}.");
    }

    /// <summary>
    /// Whether this field has taxiways the base-game pathfinder can use. The taxi state asks
    /// <c>RoadNetwork.Exists()</c> for exactly this and falls back to driving straight at the
    /// threshold when the answer is no, which on a highway strip means across the verge.
    /// </summary>
    private static bool HasTaxiways(Airbase field)
    {
        RoadNetwork? network = field.GetTaxiNetwork();
        return network != null && network.Exists();
    }

    private static bool IsOnAnyRunway(Aircraft aircraft, Airbase field)
    {
        for (int i = 0; field.runways != null && i < field.runways.Length; i++)
        {
            if (field.runways[i]?.Start != null && field.runways[i].AircraftOnRunway(aircraft))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts the aircraft at the start of the runway the game would give it for takeoff, pointing down
    /// it and stopped, after moving its own side's parked aircraft off that runway. False when the
    /// field has no runway long enough, and the caller falls back to what it did before.
    /// </summary>
    private static bool TryLineUp(Aircraft aircraft, Airbase field)
    {
        Airbase.Runway.RunwayUsage? usage = field.GetTakeoffRunway(
            aircraft, aircraft.definition.aircraftParameters.takeoffDistance);
        if (!usage.HasValue || usage.Value.Runway?.Start == null || usage.Value.Runway.End == null)
        {
            return false;
        }

        Airbase.Runway runway = usage.Value.Runway;

        // Two directions on purpose. The flat one is only for "which side of the strip is that parked
        // aeroplane on"; placement uses the runway's 3D direction so the aeroplane sits on the surface
        // on sloping strips. Too high, DuctedThrust.AutoAngle swivels the nozzles to hover; too low, the
        // gear starts inside the road.
        Vector3 alongSurface = usage.Value.GetDirection();
        Vector3 along = alongSurface;
        along.y = 0f;
        if (along.sqrMagnitude < 1f || alongSurface.sqrMagnitude < 1f)
        {
            return false;
        }

        along.Normalize();
        alongSurface.Normalize();
        Vector3 start = usage.Value.GetStart().position + (alongSurface * (aircraft.maxRadius + 15f));

        // Aircraft never jump: they drive out and fly. A line-up
        // only straightens one already standing at the threshold; anything farther taxis there, and
        // parked aircraft on the strip are not shoved aside, the line-up waits for them.
        Vector3 offset = start - aircraft.transform.position;
        offset.y = 0f;
        if (offset.magnitude > LineUpMaxMoveMeters || !IsRunwayClear(runway, aircraft))
        {
            return false;
        }

        CommanderGameAccess.MoveAircraft(
            aircraft,
            start + (Vector3.up * aircraft.definition.spawnOffset.y),
            Quaternion.LookRotation(alongSurface, Vector3.up),
            Vector3.zero);

        // Then settle it onto whatever is actually under it. The runway line runs straight between
        // its two end transforms and a highway strip does not, so the chord can sit a few metres
        // over a dip. The game's own probe (a linecast down through the statics layer, minus the
        // airframe's spawn offset) is what every other height test in the engine uses, and reading
        // it here costs one raycast per departure.
        aircraft.CheckRadarAlt();
        if (aircraft.radarAlt > 0.25f)
        {
            CommanderGameAccess.MoveAircraft(
                aircraft,
                aircraft.transform.position - (Vector3.up * Mathf.Min(aircraft.radarAlt, 25f)),
                aircraft.transform.rotation,
                Vector3.zero);
            aircraft.CheckRadarAlt();
        }

        // The height is in the line because it is the whole point of all of the above: anything but
        // a fraction of a metre means the aeroplane was put down off the deck, and that is what
        // turns a takeoff roll into a hover (DuctedThrust.AutoAngle calls it airborne and swivels
        // the nozzles down while the takeoff state firewalls the throttle) or a collision.
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} lined up on runway {runway.GetName(usage.Value.Reverse)} at "
                + $"{CommanderCaptureService.GetAirbaseLabel(field)} ({runway.Length:0} m) and taking off, "
                + $"{aircraft.radarAlt:0.0} m over the deck.");
        return true;
    }

    /// <summary>No stopped aircraft of the departing one's side stands on the runway. They are never
    /// moved aside or into stock to make way.</summary>
    private static bool IsRunwayClear(Airbase.Runway runway, Aircraft departing)
    {
        FactionHQ? hq = departing.NetworkHQ;
        if (hq?.factionUnits == null)
        {
            return true;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is Aircraft other
                && !ReferenceEquals(other, departing)
                && !other.disabled
                && other.speed < 2f
                && other.radarAlt < 5f
                && runway.AircraftOnRunway(other))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The ground rescue's first answer: at a field its side holds, line the aircraft up and roll
    /// again rather than lift it. False when there is no such field or runway.
    /// </summary>
    private static bool TryRescueByLineUp(Aircraft aircraft)
    {
        FactionHQ? hq = aircraft.NetworkHQ;
        Airbase? field = hq == null ? null : hq.GetNearestAirbase(aircraft.transform.position, 3000f);
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (field == null || pilot == null)
        {
            return false;
        }

        // Respect the one-departure-per-field queue, or the rescue lines up on a threshold another
        // aeroplane is rolling off.
        if (IsFieldBusy(field, aircraft))
        {
            RequestDeparture(aircraft, field, lineUp: true);
            return true;
        }

        if (!TryLineUp(aircraft, field))
        {
            return false;
        }

        Departing[field] = aircraft;
        DepartingSince[field] = Time.time;
        aircraft.SetGear(deployed: true);
        pilot.AITakeoffState = new AIPilotTakeoffState();
        pilot.SwitchState(pilot.AITakeoffState);
        return true;
    }

    private static void ResetDepartures()
    {
        LineUpTurns.Clear();
        Departing.Clear();
        DepartingSince.Clear();
        DepartureQueue.Clear();
        DepartureFields.Clear();
        DepartureQueuedAt.Clear();
        LineUpDepartures.Clear();
        Following.Clear();
        HangarHoldReleased.Clear();
        LeavingHangar.Clear();
        TaxiOrigins.Clear();
        BlockedSince.Clear();
        TaxiStoodSince.Clear();
        DeckMovedAt.Clear();
        DeckTurns.Clear();
        DeckYielded.Clear();
        DeckLaunchedAt.Clear();
        GivingWay.Clear();
    }

    /// <summary>
    /// No building collider around the airframe (wings and tail plus a margin) from a metre above its
    /// origin up: it is out of the hangar and free to turn.
    /// </summary>
    private static bool IsClearOfBuildings(Aircraft aircraft, AircraftDefinition definition)
    {
        Transform t = aircraft.transform;
        Vector3 halfExtents = new(
            (definition.width * 0.5f) + TaxiHangarSideMarginMeters,
            definition.height * 0.5f,
            (definition.length * 0.5f) + TaxiHangarSideMarginMeters);
        Vector3 centre = t.position + (t.up * (1f + halfExtents.y));
        return !Physics.CheckBox(centre, halfExtents, t.rotation, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);
    }
}
