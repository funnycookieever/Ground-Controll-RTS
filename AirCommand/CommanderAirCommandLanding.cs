using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Putting a commanded aircraft down on a named airbase — to take that base, or to hand the
/// airframe back for a fresh one.
/// </summary>
/// <remarks>
/// The base game has no "land there" order: <c>AIPilotLandingState</c> only picks fields the
/// faction already holds. Three things make it land:
/// <list type="number">
/// <item>the mission carries a <c>LandingBase</c>, and the route point is that base;</item>
/// <item><see cref="TryOverrideLandingAirbase"/> prefixes the landing state's private airbase
/// search, substituting the commanded field and requesting a runway from it;</item>
/// <item>on a capture the aircraft is parked by hand once it stops rolling. Otherwise
/// <c>AIPilotTaxiState</c> finds nothing to taxi to at a foreign field, calls itself stuck and
/// ejects the pilot, which destroys the aircraft. <c>PilotParkedState</c> does nothing.</item>
/// </list>
/// <para>
/// Capture strength is granted by the mod: <c>Unit.CaptureStrength</c> is 0 for aircraft without a
/// <c>MountedTroops</c> pod. <c>Unit.ModifyCaptureStrength</c> (the hook the troop pod uses) is
/// used, and the grant is handed back when the aircraft leaves.
/// </para>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>How close the aircraft has to be before the pilot is switched into the approach.</summary>
    private const float LandingHandoverMeters = 9000f;

    /// <summary>Rolling out: below this it is on the deck and should be parked, not taxied. The
    /// window has to be wide enough to beat the Basegame's own 2 s check, which hands anything under
    /// 15 m/s to the taxi state and from there to an ejection.</summary>
    private const float ParkedSpeedMetersPerSecond = 12f;
    private const float ParkedRadarAltMeters = 5f;

    /// <summary>
    /// How long a resupply landing may taxi toward a service stand before it is parked wherever it
    /// got to. The base-game taxi ends in its own (blocked) ejection, which parks it much sooner.
    /// </summary>
    private const float TaxiToStandTimeoutSeconds = 90f;

    /// <summary>A taxi to a stand stopped this long behind traffic is parked and serviced there.</summary>
    private const float TaxiQueueStopSeconds = 8f;

    /// <summary>The rotary version of the same window. A helicopter at four metres is still
    /// flying — cutting its collective there is a four-metre drop onto the pad, which is what
    /// destroyed a Chicane that had just "landed" at its own base. The base game's own parked
    /// state uses one metre for exactly this reason.</summary>
    private const float RotaryParkedRadarAltMeters = 1f;

    /// <summary>How near a field an aircraft already on the deck counts as being *on* that field.
    /// A highway strip is long, so this is generous on purpose.</summary>
    private const float OnFieldMeters = 3000f;

    /// <summary>How long a RESERVE landing waits for a safe ejection before stocking it without one.</summary>
    private const float ReserveEjectTimeoutSeconds = 15f;

    /// <summary>How close another aeroplane has to be to a service stand for that stand to count as
    /// taken. A fighter is about fifteen metres long, so this is one parked aircraft and room to
    /// turn in beside it.</summary>
    private const float StandOccupiedMeters = 40f;

    /// <summary>A stand closer than this to a runway's edge is not handed out.</summary>
    private const float StandRunwayClearMeters = 12f;

    /// <summary>Above this the aeroplane holding a stand has flown off it and the stand is free.</summary>
    private const float StandReleaseAltitudeMeters = 30f;

    /// <summary>Below this the aircraft is on the deck, so an ejection there is the AI giving up.</summary>
    private const float GroundEjectionAltitudeMeters = 60f;

    /// <summary>How often one airframe may be lifted off a strip it could not fly itself off.</summary>
    private const int MaxGroundRescues = 3;

    /// <summary>See <see cref="EjectStuckOnGround"/>. Longer than every other ground rescue (90 s), so
    /// this only catches what they missed.</summary>
    private const float StuckOnRunwaySeconds = 120f;
    private const float StuckOnGroundSeconds = 240f;
    private const float GroundStillMeters = 5f;

    /// <summary>How long an AI side's aeroplane waits on a stand for stock before going to reserve.</summary>
    private const float AiGroundedReserveSeconds = 60f;

    /// <summary>Fraction of an aircraft's parts detached or destroyed before it counts as a wreck. The
    /// base game's own figure, used by <c>AIPilotCombatModes</c> and <c>AIPilotLandingState</c> to give up.</summary>
    private const float WreckedDetachedRatio = 0.12f;

    /// <summary>How close to a field a stuck aircraft has to be for the failure to count against it.</summary>
    private const float FailedTakeoffFieldMeters = 4000f;

    /// <summary>Share of the capture radius a landing spot may be rolled in, so a helicopter parked
    /// at the far edge is still well inside the ring.</summary>
    private const float CaptureSpotRingShare = 0.85f;
    private const int CaptureSpotAttempts = 40;
    private const float CaptureSpotMarginMeters = 6f;
    private const float CaptureSpotMinClearMeters = 15f;
    private const float CaptureSpotSpacingMeters = 50f;

    /// <summary>
    /// Fields an airframe type has failed to take off from, for both sides. The AI taxi and takeoff
    /// states cannot be trusted on a highway strip, and every failure there ended in a rescue lift
    /// that the player saw as the aircraft vanishing a few metres out of its hangar. A type listed
    /// here is launched airborne over that field and released airborne after a service stop.
    /// </summary>
    private static readonly HashSet<(Airbase, AircraftDefinition)> FailedTakeoffs = new();

    /// <summary>AI launches stuck before their first tasking, lined up on the next sweep
    /// (<see cref="TryHoldUntaskedLaunch"/>).</summary>
    private static readonly List<Aircraft> UntaskedRescues = new();

    /// <summary>AI launches that have had that one line-up.</summary>
    private static readonly HashSet<Aircraft> UntaskedRescued = new();

    /// <summary>Airframes whose abandon has been logged, so a stuck one does not log every frame.</summary>
    private static readonly HashSet<Aircraft> AbandonLogged = new();

    private static readonly AccessTools.FieldRef<Unit, Dictionary<PersistentID, float>?> DamageCreditRef =
        AccessTools.FieldRefAccess<Unit, Dictionary<PersistentID, float>?>("damageCredit");

    /// <summary>Stands the taxi network does not lead to (see <c>RouteLandedTaxi</c>).</summary>
    private static readonly HashSet<Transform> UnreachableStands = new();

    private static readonly AccessTools.FieldRef<Airbase, Transform[]?> ServicePointsRef =
        AccessTools.FieldRefAccess<Airbase, Transform[]?>("servicePoints");

    /// <summary>Which stand each arriving aeroplane was given.</summary>
    private static readonly Dictionary<Transform, Aircraft> StandClaims = new();
    private static readonly List<Transform> StandScratch = new();

    /// <summary>Player aeroplanes whose stand ejection was blocked, and the field they stand on; the
    /// next resupply sweep parks and services them.</summary>
    private static readonly Dictionary<Aircraft, Airbase> LandedForService = new();

    /// <summary>The pad each helicopter on a capture landing was given, so a re-issue replaces it.</summary>
    private static readonly Dictionary<Aircraft, GameObject> CaptureLandingPoints = new();
    private static readonly Collider[] CaptureSpotHits = new Collider[32];

    /// <summary>The aeroplane whose taxi frame is running. <c>Airbase.TryGetNearestServicePoint</c>
    /// is not told who is asking, and both of its callers sit inside that one frame.</summary>
    private static Aircraft? taxiingAircraft;

    private readonly List<Aircraft> landingSweep = new();

    /// <summary>
    /// Aircraft that have run their racks dry, or are down to the fuel it takes to reach a field,
    /// are sent for a top-up. Runs on the same 2 s sweep as the landing orders themselves.
    /// </summary>
    /// <summary>
    /// The tank level that sends an aircraft for fuel. Never below the Basegame's own 20 % check:
    /// that one switches the pilot straight into AIPilotLandingState with a field of its own
    /// choosing, and this service would then be rescuing an aeroplane off the deck it just landed
    /// on, over and over.
    /// </summary>
    internal static float ResupplyFuelFloor =>
        Mathf.Clamp(CommanderSettings.AircraftResupplyFuelPercent / 100f, 0.22f, 0.9f);

    /// <summary>Any mission flying a player order on a low tank, refreshed by the resupply sweep: the
    /// cheap early-out for the base-game fuel check patch.</summary>
    internal static bool AnyLowFuelOrder { get; private set; }

    /// <summary>
    /// Answers the base game's own 20 % fuel check (<c>PilotBaseState.FuelChecker</c>) for an
    /// aircraft flying a low-fuel player order, which would otherwise switch it into the landing
    /// state no matter what the mission says.
    /// </summary>
    internal static bool IsFlyingLowFuelOrder(Aircraft? aircraft)
    {
        return aircraft != null
            && Instance != null
            && Instance.missions.TryGetValue(aircraft, out AirMission mission)
            && HoldsLowFuelOrder(mission);
    }

    internal static bool HasFailedTakeoff(Airbase airbase, AircraftDefinition definition)
    {
        return FailedTakeoffs.Contains((airbase, definition));
    }

    /// <summary>
    /// Called from the <c>Aircraft.StartEjectionSequence</c> prefix. True means "do not eject".
    /// </summary>
    /// <remarks>
    /// Every way the base-game AI gives up on the ground ends here — the taxi state's 30 s stuck
    /// timer, its wing-off-level check, a scrape on the way to the runway, the takeoff state's 12 s
    /// stall — and all of them are fatal, because an ejection away from a friendly base is
    /// <c>DisableUnit</c> and one beside a friendly base is <c>ReturnToInventory</c>. Neither is
    /// what should happen to an aeroplane the player just bought and gave an order to.
    /// <para>
    /// The rescue itself is deferred to the mission tick rather than done here, because both callers
    /// follow <c>StartEjectionSequence</c> with <c>SwitchState(parkedState)</c> — anything this
    /// method did to the pilot would be overwritten one line later.
    /// </para>
    /// </remarks>
    internal static bool TryBlockGroundEjection(Aircraft? aircraft)
    {
        LogAbandon(aircraft);
        CommanderAirCommandService? service = Instance;
        if (service == null
            || aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || aircraft.radarAlt > GroundEjectionAltitudeMeters)
        {
            return false;
        }

        // A wreck is let go. StartEjectionSequence is the *only* path that ever removes an abandoned
        // AI aircraft (ReturnToInventory beside a friendly base, DisableUnit anywhere else), so
        // blocking it on an airframe that has come apart leaves the hulk sitting on the runway for
        // the rest of the match — which is exactly what happened to two transports that crashed on
        // landing. 0.12 detached is the game's own "this one is finished" threshold, the same figure
        // AIPilotCombatModes and AIPilotLandingState bail out on.
        if (IsWrecked(aircraft))
        {
            return false;
        }

        // Already on a commanded field: the landing tick owns this aeroplane and is either parking
        // it on a capture or servicing it. Blocking the ejection is the whole job.
        bool hasMission = service.missions.TryGetValue(aircraft, out AirMission mission);
        if (hasMission && mission.LandingBase != null)
        {
            return mission.Intent != LandingIntent.Reserve;
        }

        // A player's aeroplane the base game flew home taxis to a stand and ejects, returning it to
        // stock. Service it on the spot instead.
        if (TryGetHomeFieldAfterLanding(aircraft, out Airbase home))
        {
            LandedForService[aircraft] = home;
            return true;
        }

        if (!hasMission)
        {
            return TryHoldUntaskedLaunch(aircraft);
        }

        // Deliberately gone home to be recovered — the enemy commander's airframes and the player's
        // fallback RTB both end this way, and ReturnToInventory is the point of the trip, not an
        // accident to be rescued from.
        if (mission.Returning)
        {
            return false;
        }

        // A helicopter standing still on the ground is not stuck: AIHeloCombatState ejects on
        // "speed < 1 and under 5 m", which is the base game recovering an airframe that has landed
        // (ReturnToInventory beside a friendly field). Blocking that on the enemy's rotary wing read
        // every one of them as a failed takeoff, and the Chicane was banned from its only field.
        if (HasRotaryPilot(aircraft)
            && !CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq())
            && !CommanderEnemyCommanderService.HasHumanPlayers(mission.Hq))
        {
            return false;
        }

        if (mission.GroundRescues >= MaxGroundRescues)
        {
            return false;
        }

        mission.GroundRescues++;
        mission.NeedsGroundRescue = true;
        return true;
    }

    /// <summary>
    /// Whether an airframe has come apart. Wings and other <c>AeroPart</c>s never detach however hard
    /// they are hit (<c>UnitPart.TakeDamage</c> skips them), so a crash that shreds the whole aircraft
    /// can leave <c>PartDamageTracker.GetDetachedRatio</c> near zero; a part at zero hit points counts
    /// as gone too. That tracker also only re-counts once a second, so it can answer for the aircraft
    /// as it was before the impact.
    /// </summary>
    internal static bool IsWrecked(Aircraft aircraft)
    {
        List<UnitPart> parts = aircraft.GetAllParts();
        if (parts == null || parts.Count == 0)
        {
            return false;
        }

        int gone = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            UnitPart part = parts[i];
            if (part == null || part.IsDetached() || part.hitPoints <= 0f)
            {
                gone++;
            }
        }

        return gone > parts.Count * WreckedDetachedRatio;
    }

    /// <summary>Nearest airbase the aircraft's own faction holds; with <paramref name="mustFit"/>,
    /// only one it can land on and leave again (<see cref="FitsField"/>).</summary>
    internal static Airbase? FindNearestOwnedAirbase(Aircraft aircraft, bool mustFit = false)
    {
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null || hq == null)
        {
            return null;
        }

        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || (mustFit && !FitsField(aircraft, airbase)))
            {
                continue;
            }

            float distance = FastMath.Distance(airbase.center.GlobalPosition(), aircraft.GlobalPosition());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        return best;
    }

    /// <summary>Puts a pilot back into the state the mod steers, whatever it was doing before.</summary>
    /// <summary>
    /// Gives each arriving aeroplane a service stand of its own.
    /// <c>Airbase.TryGetNearestServicePoint</c> hands every one of them the stand nearest where it
    /// left the runway, so two sent to refuel at the same time taxi to the same spot and drive into
    /// each other, and the third into the back of those two, which is the Boscali pile-up.
    /// A stand already claimed, or with someone parked on it, is passed over.
    /// <para>
    /// A field with a single stand, or one whose stands are all taken, falls through to the game's
    /// own answer. That is where this started, but the alternative is answering "no stand", which
    /// the taxi state reads as "stop here and shut down" — on the runway, if it has not left it yet.
    /// </para>
    /// </summary>
    internal static bool TryClaimServiceStand(Airbase airbase, Vector3 fromPosition, out Transform? stand)
    {
        stand = null;
        Aircraft? asker = taxiingAircraft;
        Transform[]? stands = ServicePointsRef(airbase);
        if (asker == null || asker.disabled || stands == null || stands.Length < 2)
        {
            return false;
        }

        ReleaseFlownStands();

        // Already holding one: keep it, or every taxi query would re-route the aeroplane.
        foreach (KeyValuePair<Transform, Aircraft> claim in StandClaims)
        {
            if (ReferenceEquals(claim.Value, asker))
            {
                stand = claim.Key;
                return true;
            }
        }

        // A stand inside the runway's edges parks the aeroplane where the next departure lines up.
        float nearest = float.MaxValue;
        for (int i = 0; i < stands.Length; i++)
        {
            Transform candidate = stands[i];
            if (candidate == null
                || StandClaims.ContainsKey(candidate)
                || IsStandOccupied(candidate, asker)
                || IsBesideRunway(airbase, candidate.position)
                || UnreachableStands.Contains(candidate))
            {
                continue;
            }

            float distance = (candidate.position - fromPosition).sqrMagnitude;
            if (distance < nearest)
            {
                nearest = distance;
                stand = candidate;
            }
        }

        if (stand == null)
        {
            return false;
        }

        StandClaims[stand] = asker;
        return true;
    }

    /// <summary>
    /// True when <paramref name="airbase"/> has a stand <paramref name="asker"/> could be given now,
    /// or has none the claims manage at all (fewer than two usable stands), in which case there is
    /// nothing to wait for.
    /// </summary>
    internal static bool HasFreeStand(Airbase airbase, Aircraft asker)
    {
        Transform[]? stands = ServicePointsRef(airbase);
        if (stands == null)
        {
            return true;
        }

        int usable = 0;
        bool free = false;
        for (int i = 0; i < stands.Length; i++)
        {
            Transform candidate = stands[i];
            if (candidate == null || UnreachableStands.Contains(candidate) || IsBesideRunway(airbase, candidate.position))
            {
                continue;
            }

            usable++;
            if (StandClaims.TryGetValue(candidate, out Aircraft holder) && ReferenceEquals(holder, asker))
            {
                return true;
            }

            free |= !StandClaims.ContainsKey(candidate) && !IsStandOccupied(candidate, asker);
        }

        return free || usable == 0;
    }

    /// <summary>An aircraft or vehicle on the deck within touching distance of a helipad. Anything
    /// more than 10 m above or below the pad is flying over it or on another level.</summary>
    internal static bool IsPadPhysicallyOccupied(Vector3 pad, Aircraft querier)
    {
        List<Unit> units = UnitRegistry.allUnits;
        for (int i = 0; i < units.Count; i++)
        {
            Unit other = units[i];
            if (other == null
                || other.disabled
                || ReferenceEquals(other, querier)
                || other is not (Aircraft or GroundVehicle))
            {
                continue;
            }

            Vector3 offset = other.transform.position - pad;
            float reach = querier.maxRadius + other.maxRadius + 3f;
            if (Mathf.Abs(offset.y) < 10f && (offset.x * offset.x) + (offset.z * offset.z) < reach * reach)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Takes a stand out of the claims for good: no taxiway leads to it.</summary>
    internal static void MarkStandUnreachable(Transform stand)
    {
        UnreachableStands.Add(stand);
        StandClaims.Remove(stand);
    }

    /// <summary>Drops every stand claim, for a scene change.</summary>
    internal static void ResetServiceStands()
    {
        StandClaims.Clear();
        StandScratch.Clear();
        UnreachableStands.Clear();
        LandedForService.Clear();
        EntryExits.Clear();
        ClearedForFinal.Clear();
        taxiingAircraft = null;
    }

    /// <summary>Set around the base-game taxi frame, from the patches either side of it.</summary>
    internal static void SetTaxiingAircraft(Pilot? pilot)
    {
        taxiingAircraft = pilot == null ? null : pilot.aircraft;
    }

    /// <summary>Not an AI faction and not a team nobody plays: its aircraft are serviced, not recovered.</summary>
    internal static bool IsHumanLed(FactionHQ hq, FactionHQ? localHq)
    {
        return !CommanderEnemyCommanderService.IsAiFaction(hq, localHq)
            && (hq == localHq || CommanderEnemyCommanderService.HasHumanPlayers(hq));
    }

    internal static void ReturnToCombatState(Aircraft aircraft)
    {
        if (aircraft.pilots == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            aircraft.SetGear(deployed: false);
            if (pilot.pilotType is Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing)
            {
                // AIPilotCombatModes is the fixed-wing brain; a rotary airframe put into it has no
                // collective and drops. AIHeloCombatState is its twin and reads the same mission.
                if (pilot.AIHeloCombatState == null)
                {
                    pilot.AIHeloCombatState = new AIHeloCombatState(pilot);
                }

                pilot.SwitchState(pilot.AIHeloCombatState);
                return;
            }

            if (pilot.AICombatState == null)
            {
                pilot.AICombatState = new AIPilotCombatModes(aircraft);
            }

            pilot.SwitchState(pilot.AICombatState);
            return;
        }
    }

    /// <summary>
    /// Prefix hook for <c>AIPilotLandingState.LandingState_SearchAirbase</c>: substitutes the
    /// commanded field for the one the Basegame would have found. Returns false — "run the original"
    /// — whenever there is no commanded field or that field will not take the aircraft, so a
    /// rejected approach still lands somewhere rather than being abandoned in the circuit.
    /// </summary>
    internal static bool TryOverrideLandingAirbase(
        AIPilotLandingState state,
        FieldInfo? aircraftField,
        FieldInfo? landingModeField,
        FieldInfo? airbaseField,
        FieldInfo? runwayUsageField,
        FieldInfo? landingSpeedField)
    {
        CommanderAirCommandService? service = Instance;
        if (service == null
            || service.missions.Count == 0
            || aircraftField == null
            || landingModeField == null
            || airbaseField == null
            || runwayUsageField == null
            || landingSpeedField == null)
        {
            return false;
        }

        // The original bails out unless it is still joining the pattern (mode 0); once established
        // the runway is chosen and re-picking it mid-approach would be a go-around.
        object? mode = landingModeField.GetValue(state);
        if (mode == null || System.Convert.ToInt32(mode, CultureInfo.InvariantCulture) != 0)
        {
            return false;
        }

        if (aircraftField.GetValue(state) is not Aircraft aircraft
            || !service.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.LandingBase == null
            || mission.LandingBase.disabled)
        {
            return false;
        }

        AircraftParameters parameters = aircraft.GetAircraftParameters();
        RunwayQuery query = BuildLandingQuery(aircraft, out float landingSpeed);
        Airbase.Runway.RunwayUsage? usage = mission.LandingBase.RequestLanding(aircraft, query);
        if (!usage.HasValue)
        {
            CommanderPlugin.Log.LogWarning(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} cannot land at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(mission.LandingBase)}: no runway it fits. "
                    + "Falling back to the Basegame choice.");
            return false;
        }

        airbaseField.SetValue(state, mission.LandingBase);
        runwayUsageField.SetValue(state, usage.Value);
        landingSpeedField.SetValue(state, parameters.verticalLanding ? 90f : landingSpeed);
        return true;
    }

    /// <summary>
    /// The rotary twin of <see cref="TryOverrideLandingAirbase"/>, and the reason a helicopter
    /// ignored a capture order. <c>AIHeloLandingState.EnterState</c> picks its field with
    /// <c>aircraft.NetworkHQ.TryGetNearestAirbase</c> — its own faction's nearest — so a Chicane
    /// told to take a neutral airfield flew to within the handover range of it, was handed to that
    /// state, and turned round and landed at home instead. Nothing about the landing point itself
    /// is ownership-gated (<c>TryRequestVerticalLanding</c> and <c>IsAvailable</c> both only ask
    /// about geometry and occupancy), so substituting the commanded field is all it takes.
    /// </summary>
    internal static void OverrideHeloLandingAirbase(
        AIHeloLandingState state,
        Pilot pilot,
        FieldInfo? nearestAirbaseField,
        FieldInfo? landingPointField,
        FieldInfo? reachedApproachField,
        FieldInfo? destinationField)
    {
        CommanderAirCommandService? service = Instance;
        Aircraft? aircraft = pilot?.aircraft;
        if (service == null
            || service.missions.Count == 0
            || aircraft == null
            || nearestAirbaseField == null
            || landingPointField == null
            || !service.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.LandingBase == null
            || mission.LandingBase.disabled)
        {
            return;
        }

        RunwayQuery query = new()
        {
            RunwayType = RunwayQueryType.Vertical,
            MinSize = aircraft.maxRadius,
        };

        Airbase.VerticalLandingPoint point;
        if (mission.Intent == LandingIntent.Capture)
        {
            point = CreateCaptureLandingPoint(aircraft, mission);
        }
        else if (!mission.LandingBase.TryRequestVerticalLanding(aircraft, query, out point))
        {
            CommanderPlugin.Log.LogWarning(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} cannot set down at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(mission.LandingBase)}: no landing pad it fits. "
                    + "Falling back to the Basegame choice.");
            return;
        }

        nearestAirbaseField.SetValue(state, mission.LandingBase);
        landingPointField.SetValue(state, point);
        reachedApproachField?.SetValue(state, false);
        destinationField?.SetValue(state, point.GetApproachPoint(aircraft));
    }

    /// <summary>
    /// Who did the damage, off the base game's own kill-credit ledger. Hits from a weapon are
    /// recorded against the shooter; hitting the ground records nothing, so "no weapon hits" on a
    /// wreck means it crashed by itself. Seekers log 0.001 per lock, hence the threshold.
    /// </summary>
    internal static string DescribeDamageTaken(Unit unit)
    {
        PersistentID worst = CommanderGameAccess.GetTopDealer(DamageCreditRef(unit));
        if (!worst.IsValid)
        {
            return "no weapon hits recorded (crashed on its own)";
        }

        return worst.TryGetUnit(out Unit dealer)
            ? $"hit mostly by {CommanderGameAccess.GetUnitLabel(dealer)}"
                + $" {FastMath.Distance(dealer.GlobalPosition(), unit.GlobalPosition()) / 1000f:0.0} km away"
            : "hit mostly by a unit that is already gone";
    }

    /// <summary>
    /// Orders one aircraft to put itself down on <paramref name="airbase"/>. The route point is the
    /// field itself, so the travel line drawn for the selection shows where it is going.
    /// </summary>
    internal bool SetAircraftLandingOrder(Aircraft aircraft, Airbase airbase, LandingIntent intent, bool append, bool announce = true)
    {
        if (!IsTaskableAircraft(aircraft) || airbase == null || airbase.center == null || intent == LandingIntent.None)
        {
            return false;
        }

        // Before the forward, so a guest sees it. True because the order is answered: false would
        // send the caller on to a plain travel point over the field. An Ifrit (1200 m) on South
        // Boscali's 1199 m runway was handed to the approach, refused, and flew off to land at home.
        // Resupply and reserve too, a click on a short field included; not for one already standing
        // there, which is serviced where it is.
        bool needsRunway = intent == LandingIntent.Capture
            || (intent is LandingIntent.Resupply or LandingIntent.Reserve && !IsStandingOn(aircraft, airbase));
        if (needsRunway && !HasLandingRunway(aircraft, airbase))
        {
            RefuseLaunch(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} cannot {(intent == LandingIntent.Capture ? "capture" : "land at")} "
                    + $"{CommanderCaptureService.GetAirbaseLabel(airbase)}: "
                    + $"no runway it can land on (needs {aircraft.GetAircraftParameters().takeoffDistance:0} m).");
            return true;
        }

        if (ForwardAir(
            "air.land",
            aircraft,
            CommanderNetwork.Key(airbase),
            CommanderNetwork.Num((int)intent),
            CommanderNetwork.Flag(append)))
        {
            return true;
        }

        // Here and not at the click: a guest's order only reaches the host's cargo runs through this.
        if (intent == LandingIntent.Capture || intent == LandingIntent.Reserve)
        {
            CommanderSupplyHeliService.Instance?.EndCargoRun(aircraft);
        }

        GlobalPosition field = airbase.center.GlobalPosition();
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            if (!TryAdoptAircraft(aircraft, AirCommandMode.Cas, field, GetMissionRadius(AirCommandMode.Cas)))
            {
                return false;
            }

            mission = missions[aircraft];
        }

        ApplyLandingOrder(aircraft, mission, airbase, intent, append, announce);
        return true;
    }

    /// <summary>
    /// Sends one of the enemy commander's airframes to land on a base and hold it, the same capture
    /// landing the player gives. The player's last base could otherwise only fall to ground units,
    /// which is a long wait when the convoys are held up. Quiet: no status line, no toast.
    /// </summary>
    internal bool TrySetAiCaptureLanding(Aircraft aircraft, Airbase airbase)
    {
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || airbase == null
            || airbase.center == null
            || CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq())
            || !missions.TryGetValue(aircraft, out AirMission mission)
            || mission.LandingBase != null)
        {
            return false;
        }

        ApplyLandingOrder(aircraft, mission, airbase, LandingIntent.Capture, append: false, announce: false);
        return true;
    }

    /// <summary>How many of this faction's airframes are landing on, or holding, that base.</summary>
    internal int CountAiCaptureLandings(FactionHQ hq, Airbase airbase)
    {
        int count = 0;
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && ReferenceEquals(entry.Value.Hq, hq)
                && ReferenceEquals(entry.Value.LandingBase, airbase)
                && entry.Value.Intent == LandingIntent.Capture)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Sends an aircraft to the nearest field its faction holds, puts it on the deck, refuels and
    /// rearms it out of a supply depot, and then launches it back onto the mission it was flying.
    /// </summary>
    /// <remarks>
    /// The base game does not refuel or rearm AI aircraft: <c>AIPilotTaxiState</c> parks it and ejects
    /// the pilot, which returns the airframe to inventory. Here the same aeroplane goes back out.
    /// </remarks>
    internal bool ResupplyAircraft(Aircraft aircraft)
    {
        if (ForwardAir("air.resupply", aircraft))
        {
            return true;
        }

        // Only a field it can land on and take off from again: the nearest one regardless sent a
        // Vortex to a frigate's helipad over and over ("no runway it fits").
        Airbase? nearest = FindNearestOwnedAirbase(aircraft, mustFit: true);
        if (nearest == null)
        {
            RefuseNoField(aircraft);
            return false;
        }

        if (missions.TryGetValue(aircraft, out AirMission existing))
        {
            SaveResumeState(existing);
        }

        return SetAircraftLandingOrder(aircraft, nearest, LandingIntent.Resupply, append: false);
    }

    /// <summary>
    /// Sends an aircraft to the nearest field its faction holds, where the crew ejects and the
    /// airframe goes back into stock. One already standing on that field does so where it is.
    /// </summary>
    internal bool ReserveAircraft(Aircraft aircraft, bool announce = true)
    {
        if (ForwardAir("air.reserve", aircraft))
        {
            return true;
        }

        Airbase? closest = FindNearestOwnedAirbase(aircraft);
        Airbase? nearest = closest != null && IsStandingOn(aircraft, closest)
            ? closest
            : FindNearestOwnedAirbase(aircraft, mustFit: true);
        if (nearest == null)
        {
            if (announce)
            {
                RefuseNoField(aircraft);
            }

            return false;
        }

        return SetAircraftLandingOrder(aircraft, nearest, LandingIntent.Reserve, append: false, announce);
    }

    /// <summary>
    /// What the selection bar says about an aircraft's tank and servicing, or empty when there is
    /// nothing to say, so a refuelling aeroplane does not look stuck.
    /// </summary>
    internal string GetServiceStatus(Aircraft aircraft)
    {
        if (missions.TryGetValue(aircraft, out AirMission reserving) && reserving.Intent == LandingIntent.Reserve)
        {
            return "RETURNING TO RESERVE";
        }

        if (missions.TryGetValue(aircraft, out AirMission mission) && mission.Intent == LandingIntent.Resupply)
        {
            if (!mission.Parked)
            {
                return "RETURNING TO REFUEL / REARM";
            }

            if (mission.ReportedShortfall)
            {
                return "GROUNDED - " + mission.ShortfallReason.ToUpperInvariant();
            }

            float left = mission.ResupplyReadyAt - Time.time;
            return left > 0f ? $"REFUELLING / REARMING - {left:0} s" : "REFUELLING / REARMING";
        }

        float fuel = aircraft.GetFuelLevel();
        return fuel < ResupplyFuelFloor ? $"LOW FUEL {fuel * 100f:0}% - NEEDS REFUELLING" : string.Empty;
    }

    /// <summary>The field this aircraft is parked on under a landing order, if it is.</summary>
    internal bool TryGetParkedField(Aircraft aircraft, out Airbase? airbase)
    {
        airbase = missions.TryGetValue(aircraft, out AirMission mission) && mission.Parked ? mission.LandingBase : null;
        return airbase != null;
    }

    /// <summary>True while this aircraft is on its way to, or sitting on, a commanded field.</summary>
    internal bool TryGetLandingOrder(Aircraft aircraft, out Airbase? airbase, out LandingIntent intent)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission) && mission.LandingBase != null)
        {
            airbase = mission.LandingBase;
            intent = mission.Intent;
            return true;
        }

        airbase = null;
        intent = LandingIntent.None;
        return false;
    }

    /// <summary>
    /// A landing pad of the aircraft's own, on open ground inside the capture ring. A field's real pads
    /// are unreliable for a capture: a highway strip may have none, and enemy-held pads can be blocked
    /// by busy runways (<c>IsOccupied</c>) or a stale landing queue. A private pad is never occupied,
    /// and <c>AIHeloLandingState</c> flies it like any other.
    /// </summary>
    private static Airbase.VerticalLandingPoint CreateCaptureLandingPoint(Aircraft aircraft, AirMission mission)
    {
        Airbase airbase = mission.LandingBase!;

        if (CaptureLandingPoints.TryGetValue(aircraft, out GameObject old) && old != null)
        {
            Object.Destroy(old);
        }

        CaptureLandingPoints.Remove(aircraft);
        if (!TryFindCaptureSpot(aircraft, airbase, out GlobalPosition spot))
        {
            spot = GetFallbackCaptureSpot(aircraft, airbase);
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} found no clear ground to land on at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(airbase)}; using the hold point.");
        }

        // Parented to the airbase so the floating origin carries it, as Airbase's own saved pads are.
        Transform pad = new GameObject("GroundControlCapturePad").transform;
        pad.parent = airbase.transform;
        pad.position = spot.ToLocalPosition();
        Vector3 fromPad = aircraft.transform.position - pad.position;
        fromPad.y = 0f;
        pad.rotation = fromPad.sqrMagnitude > 1f ? Quaternion.LookRotation(fromPad) : Quaternion.identity;
        CaptureLandingPoints[aircraft] = pad.gameObject;
        return new Airbase.VerticalLandingPoint { point = pad, approachAngleRange = 180f };
    }

    /// <summary>
    /// A random spot inside the capture ring with room to land: on land, off the runways and
    /// taxiways, level, and with nothing standing on it. Wrecks and debris have no <c>Unit</c> and
    /// are not map structures, so anything solid that is not terrain counts, which also turns down
    /// a road or an apron; open ground is what a capture needs and there is plenty inside a ring.
    /// </summary>
    private static bool TryFindCaptureSpot(Aircraft aircraft, Airbase airbase, out GlobalPosition spot)
    {
        GlobalPosition center = airbase.center.GlobalPosition();
        float reach = Mathf.Max(airbase.SavedAirbase?.CaptureRange ?? 0f, 100f) * CaptureSpotRingShare;
        float clearance = Mathf.Max(aircraft.maxRadius + CaptureSpotMarginMeters, CaptureSpotMinClearMeters);
        for (int i = 0; i < CaptureSpotAttempts; i++)
        {
            Vector2 offset = UnityEngine.Random.insideUnitCircle * reach;
            spot = CommanderGameAccess.SnapToTerrain(new GlobalPosition(center.x + offset.x, center.y, center.z + offset.y));
            if (!CommanderGameAccess.IsBelowSeaLevel(spot)
                && !CommanderBuildPreview.IsOnAirfieldSurface(spot, clearance)
                && !IsNearOtherCapturePad(spot)
                && CommanderSupplyHeliService.IsFootprintLevel(spot, Mathf.Max(aircraft.maxRadius, 4f))
                && !IsCaptureSpotObstructed(spot, clearance))
            {
                return true;
            }
        }

        spot = default;
        return false;
    }

    private static bool IsNearOtherCapturePad(GlobalPosition spot)
    {
        Vector3 local = spot.ToLocalPosition();
        foreach (GameObject pad in CaptureLandingPoints.Values)
        {
            if (pad != null && CommanderGameAccess.HorizontalDistance(pad.transform.position, local) < CaptureSpotSpacingMeters)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The probe sits a metre off the ground, so level terrain does not count.</summary>
    private static bool IsCaptureSpotObstructed(GlobalPosition spot, float clearance)
    {
        int hits = Physics.OverlapSphereNonAlloc(
            spot.ToLocalPosition() + (Vector3.up * (clearance + 1f)),
            clearance,
            CaptureSpotHits,
            ~PhysicsLayers.ExclusionZonesMask,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider hit = CaptureSpotHits[i];
            if (hit != null && (GameAssets.i == null || hit.sharedMaterial != GameAssets.i.terrainMaterial))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every landing inbound takes the next spot on a 50 m ring round the hold point, so a
    /// field with no clear ground still does not stack helicopters on top of each other.</summary>
    private static GlobalPosition GetFallbackCaptureSpot(Aircraft aircraft, Airbase airbase)
    {
        int slot = 0;
        foreach (KeyValuePair<Aircraft, AirMission> entry in Instance!.missions)
        {
            if (entry.Key != aircraft
                && entry.Key != null
                && entry.Value.LandingIssued
                && entry.Value.Intent == LandingIntent.Capture
                && ReferenceEquals(entry.Value.LandingBase, airbase))
            {
                slot++;
            }
        }

        GlobalPosition hold = CommanderCaptureService.GetHoldPoint(airbase);
        float angle = slot * 60f * Mathf.Deg2Rad;
        float radius = slot == 0 ? 0f : 50f;
        GlobalPosition spot = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            hold.x + (Mathf.Cos(angle) * radius), hold.y, hold.z + (Mathf.Sin(angle) * radius)));
        return CommanderGameAccess.IsBelowSeaLevel(spot) ? hold : spot;
    }

    /// <summary>The base game's own landing query: <c>MinSize</c> is the takeoff distance.</summary>
    private static RunwayQuery BuildLandingQuery(Aircraft aircraft, out float landingSpeed)
    {
        AircraftParameters parameters = aircraft.GetAircraftParameters();
        landingSpeed = Mathf.Sqrt(aircraft.GetMass() / aircraft.definition.aircraftInfo.maxWeight)
            * parameters.landingSpeed;
        return new RunwayQuery
        {
            RunwayType = RunwayQueryType.Landing,
            MinSize = parameters.verticalLanding ? aircraft.definition.length : parameters.takeoffDistance,
            LandingSpeed = parameters.verticalLanding ? 0f : landingSpeed,
            TailHook = aircraft.weaponManager.HasTailHook(),
        };
    }

    /// <summary>The field has a runway <see cref="TryOverrideLandingAirbase"/> would accept. A helicopter
    /// needs none: its capture landing brings its own pad.</summary>
    private static bool HasLandingRunway(Aircraft aircraft, Airbase airbase)
    {
        if (IsRotary(aircraft))
        {
            return true;
        }

        RunwayQuery query = BuildLandingQuery(aircraft, out _);
        Airbase.Runway[]? runways = airbase.runways;
        for (int i = 0; runways != null && i < runways.Length; i++)
        {
            if (runways[i] != null && runways[i].Landing && runways[i].IsSuitable(query))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>It can land here and leave again. Takeoff alone is not enough: a ski-jump passes that
    /// test and a takeoff-only strip has nowhere to land. Overruns do not count: they close a field to
    /// launches on full tanks, and a resupply there refuels only to the standard load
    /// (<see cref="GetSafeFuelLevel"/>). Counting them left a Brawler with no field at all for
    /// RESUPPLY or RESERVE once its only strip had two.</summary>
    private static bool FitsField(Aircraft aircraft, Airbase airbase)
    {
        return HasTakeoffRunway(airbase, aircraft.definition, countOverruns: false) && HasLandingRunway(aircraft, airbase);
    }

    private static bool IsStandingOn(Aircraft aircraft, Airbase airbase)
    {
        return IsOnDeck(aircraft)
            && FastMath.InRange(airbase.center.GlobalPosition(), aircraft.GlobalPosition(), OnFieldMeters);
    }

    /// <summary>A player order given on a low tank is still being flown: a travel point not yet
    /// reached, a live commanded target, or an escort.</summary>
    private static bool HoldsLowFuelOrder(AirMission mission)
    {
        return mission.IgnoreLowFuel
            && (mission.RouteIndex < mission.Route.Count
                || (mission.ForcedTarget != null && !mission.ForcedTarget.disabled)
                || mission.EscortTarget != null);
    }

    /// <summary>A human-led aeroplane that landed at a field its side holds and is being abandoned there.</summary>
    private static bool TryGetHomeFieldAfterLanding(Aircraft aircraft, out Airbase field)
    {
        field = null!;
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        FactionHQ? hq = aircraft.NetworkHQ;

        // The landing states eject too, not only the taxi: a carrier's arrestor hook stops the jet
        // short of the deck's exit point, so AIPilotLandingState.TouchedDown never hands it to the
        // taxi and ejects after 10 s standing still.
        // AIPilotShortLandingState does the same after 5 s.
        if (pilot == null
            || pilot.currentState is not (AIPilotTaxiState or AIPilotLandingState or AIPilotShortLandingState)
            || !pilot.flightInfo.HasTakenOff
            || IsRotary(aircraft)
            || hq == null
            || !IsHumanLed(hq, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        Airbase? nearest = FindNearestOwnedAirbase(aircraft);
        if (nearest == null
            || nearest.center == null
            || !FastMath.InRange(nearest.center.GlobalPosition(), aircraft.GlobalPosition(), OnFieldMeters))
        {
            return false;
        }

        field = nearest;
        return true;
    }

    /// <summary>
    /// An AI commander's new aeroplane, stuck on its way out before it has a mission: the enemy tasks
    /// a launch on its next review, up to a minute later, and the taxi gives up after 30 s. One
    /// line-up each; a second failure marks the field for that type and lets the game return it to stock.
    /// </summary>
    private static bool TryHoldUntaskedLaunch(Aircraft aircraft)
    {
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (pilot == null
            || pilot.flightInfo.HasTakenOff
            || HasRotaryPilot(aircraft)
            || !CommanderEnemyCommanderService.IsAiFaction(aircraft.NetworkHQ, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        if (!UntaskedRescued.Add(aircraft))
        {
            NoteFailedTakeoff(aircraft);
            return false;
        }

        UntaskedRescues.Add(aircraft);
        return true;
    }

    /// <summary>Remembers that this aeroplane could not get off the field it is standing on.</summary>
    private static void NoteFailedTakeoff(Aircraft aircraft)
    {
        Airbase? field = FindNearestOwnedAirbase(aircraft);
        if (field == null
            || field.center == null
            || !FastMath.InRange(field.center.GlobalPosition(), aircraft.GlobalPosition(), FailedTakeoffFieldMeters)
            || !FailedTakeoffs.Add((field, aircraft.definition)))
        {
            return;
        }

        CommanderPlugin.Log.LogInfo(
            $"{aircraft.definition.unitName} could not take off from {field.name} on its own; "
                + "the enemy launches this type from another field where it can.");
    }

    /// <summary>
    /// An AI or player airframe's pilot is giving up on it. Logged so it can be told apart from a
    /// shoot-down.
    /// </summary>
    private static void LogAbandon(Aircraft? aircraft)
    {
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || !AbandonLogged.Add(aircraft))
        {
            return;
        }

        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        float detached = aircraft.partDamageTracker != null ? aircraft.partDamageTracker.GetDetachedRatio() : 0f;
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} #{aircraft.persistentID.Id} is being abandoned by its pilot: "
                + $"{aircraft.radarAlt:0} m up at {aircraft.speed:0} m/s, fuel {aircraft.GetFuelLevel() * 100f:0} %, "
                + $"{detached * 100f:0} % of parts off, state {pilot?.currentState?.GetType().Name ?? "none"}, "
                + $"{DescribeDamageTaken(aircraft)}{DescribeTaxiGiveUp(pilot)}.");
    }

    /// <summary>Why a taxi is giving up, for the log; its own state does not say.</summary>
    private static string DescribeTaxiGiveUp(Pilot? pilot)
    {
        if (pilot?.currentState is not AIPilotTaxiState taxi)
        {
            return string.Empty;
        }

        return $"; taxi disembarking {TaxiDisembarkingRef(taxi)}, destination {(TaxiDestinationRef(taxi) == null ? "none" : "set")}, "
            + $"exit {(TaxiExitPointRef(taxi) == null ? "none" : "set")}, stuck {TaxiStuckSpeedRef(taxi):0} s / yaw {TaxiStuckYawRef(taxi):0} s, "
            + $"yielding {TaxiYieldingRef(taxi)}";
    }

    /// <summary>
    /// Remembers what the aircraft was doing so the trip to the pumps is an interruption rather than
    /// the end of the order. Only the first break-off is recorded: a second one while still parked
    /// would overwrite the real route with the single point that leads to the field.
    /// </summary>
    private static void SaveResumeState(AirMission mission)
    {
        if (mission.HasResumeState || mission.LandingBase != null)
        {
            return;
        }

        mission.ResumeRoute.Clear();
        mission.ResumeRoute.AddRange(mission.Route);
        mission.ResumeRouteIndex = mission.RouteIndex;
        mission.ResumeAreaCenter = mission.AreaCenter;
        mission.HasResumeState = true;
    }

    /// <summary>Hands back the stand of anything that has died, gone, or flown away again.</summary>
    private static void ReleaseFlownStands()
    {
        StandScratch.Clear();
        foreach (KeyValuePair<Transform, Aircraft> claim in StandClaims)
        {
            Aircraft holder = claim.Value;
            if (claim.Key == null || holder == null || holder.disabled || holder.radarAlt > StandReleaseAltitudeMeters)
            {
                StandScratch.Add(claim.Key!);
            }
        }

        for (int i = 0; i < StandScratch.Count; i++)
        {
            StandClaims.Remove(StandScratch[i]);
        }
    }

    /// <summary>Someone else is standing on it: an aeroplane that never asked for a stand, a
    /// player's, or one parked there before the first claim of the mission.</summary>
    private static bool IsBesideRunway(Airbase airbase, Vector3 point)
    {
        if (airbase.runways == null)
        {
            return false;
        }

        foreach (Airbase.Runway runway in airbase.runways)
        {
            if (runway?.Start == null || runway.End == null)
            {
                continue;
            }

            Vector3 offset = runway.GetNearestPoint(point, extend: false) - point;
            offset.y = 0f;
            if (offset.magnitude < (runway.GetWidth() * 0.5f) + StandRunwayClearMeters)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStandOccupied(Transform stand, Aircraft asker)
    {
        for (int i = 0; i < UnitRegistry.allAircraft.Count; i++)
        {
            Aircraft other = UnitRegistry.allAircraft[i];
            if (other != null
                && !other.disabled
                && !ReferenceEquals(other, asker)
                && other.radarAlt < StandReleaseAltitudeMeters
                && (other.transform.position - stand.position).sqrMagnitude < StandOccupiedMeters * StandOccupiedMeters)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True while a jet that landed to refuel is still taxiing off the runway, so it is not parked on
    /// the strip. The game taxi drives to the exit and a stand, then ejects;
    /// <see cref="TryBlockGroundEjection"/> blocks that for aircraft with a landing order and the taxi
    /// parks it, at which point this returns false.
    /// </summary>
    private static bool IsTaxiingToStand(Aircraft aircraft, AirMission mission)
    {
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;

        // Still rolling out in the landing state counts: it hands over to the taxi below 15 m/s,
        // and a strip with no exit leaves it there, stopped, which is when to park.
        bool rolling = pilot?.currentState is AIPilotLandingState && aircraft.speed >= 1f;
        if (mission.Intent != LandingIntent.Resupply
            || IsRotary(aircraft)
            || pilot == null
            || !(rolling || pilot.currentState is AIPilotTaxiState))
        {
            return false;
        }

        if (mission.TaxiToStandSince <= 0f)
        {
            mission.TaxiToStandSince = Time.time;
        }

        // Stopped in the queue behind the stand, it is serviced where it waits (off the
        // runway only, TryTaxiOffRunway). A pivot onto its route is not a queue.
        if (aircraft.speed >= 1f || TaxiPivots.Contains(aircraft))
        {
            mission.TaxiStoppedSince = 0f;
        }
        else if (mission.TaxiStoppedSince <= 0f)
        {
            mission.TaxiStoppedSince = Time.time;
        }
        else if (Time.time - mission.TaxiStoppedSince > TaxiQueueStopSeconds)
        {
            return false;
        }

        return Time.time - mission.TaxiToStandSince < TaxiToStandTimeoutSeconds;
    }

    /// <summary>
    /// A resupply landing is never serviced on a runway: it taxis off first, however long whoever is
    /// in front takes. Foxtrot's taxi to a stand at Dustbowl gave up
    /// on the runway facing a refuelled Compass taxiing out, was parked and refuelled 1.9 m off the
    /// centreline, and the one lined up behind it waited 90 s and went to stock. True while it is
    /// still on the pavement, with its taxi kept going or given a new one if the last gave up.
    /// </summary>
    private static bool TryTaxiOffRunway(Aircraft aircraft, AirMission mission, Airbase airbase)
    {
        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (mission.Intent != LandingIntent.Resupply || IsRotary(aircraft) || pilot == null || airbase.runways == null)
        {
            return false;
        }

        bool onRunway = false;
        for (int i = 0; i < airbase.runways.Length && !onRunway; i++)
        {
            onRunway = airbase.runways[i] != null && IsOnPavement(airbase.runways[i], aircraft);
        }

        if (!onRunway)
        {
            return false;
        }

        // A taxi still running is left to it. One that gave up (the ejection guard parks it) or a
        // rollout that stopped gets a fresh taxi: HasTakenOff sends it to an exit and a stand.
        if (pilot.currentState is not AIPilotTaxiState)
        {
            // As HandToTaxi: the switch to a taxi keeps a landing booking, which holds every takeoff.
            for (int i = 0; i < airbase.runways.Length; i++)
            {
                airbase.runways[i]?.DeregisterLanding(aircraft);
            }

            pilot.AITaxiState = new AIPilotTaxiState();
            pilot.SwitchState(pilot.AITaxiState);
            CommanderPlugin.Log.LogInfo($"{CommanderGameAccess.GetUnitLabel(aircraft)} is on the runway at {CommanderCaptureService.GetAirbaseLabel(airbase)}; taxiing off before it is serviced.");
        }

        return true;
    }

    /// <summary>
    /// Gets a parked aircraft flying again under its own power: a helicopter lifts off where it
    /// stands, an aeroplane taxis out at a field its side holds and otherwise stays parked.
    /// </summary>
    private static void ReleaseFromGround(Aircraft aircraft, Airbase? airbase)
    {
        Pilot? pilot = null;
        if (aircraft.pilots != null)
        {
            for (int i = 0; i < aircraft.pilots.Length && pilot == null; i++)
            {
                pilot = aircraft.pilots[i];
            }
        }

        if (pilot == null)
        {
            return;
        }

        bool rotary = pilot.pilotType is Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing;

        // Ownership is asked of the fixed-wing path only: `AIPilotTakeoffState.FixedUpdateState` ejects
        // the pilot on its first frame at a field its side does not hold. `AIHeloTakeoffState` has no
        // such rule, and a capture landing is by definition on a field the faction does not hold.
        // A helicopter lifts straight off wherever it stands; it has no runway to roll down, and the
        // fixed-wing takeoff state would drive it at one.
        if (rotary)
        {
            if (pilot.AIHeloTakeoffState == null)
            {
                pilot.AIHeloTakeoffState = new AIHeloTakeoffState();
            }

            pilot.SwitchState(pilot.AIHeloTakeoffState);
            return;
        }

        if (airbase != null && !airbase.disabled && airbase.CurrentHQ == aircraft.NetworkHQ)
        {
            // One departure per field at a time, taxied (CommanderAirCommandDepartures.cs).
            RequestDeparture(aircraft, airbase, lineUp: HasFailedTakeoff(airbase, aircraft.definition));
            return;
        }

        // Aircraft never jump: a fixed-wing stays parked and leaves by the runway once its side holds
        // the field.
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} stays parked: it cannot take off from a field its side does not hold.");
    }

    /// <summary>Stopped, on the ground. The rotary ceiling is lower for the reason
    /// <see cref="RotaryParkedRadarAltMeters"/> gives.</summary>
    private static bool IsOnDeck(Aircraft aircraft)
    {
        if (aircraft == null || aircraft.disabled)
        {
            return false;
        }

        float ceiling = IsRotary(aircraft) ? RotaryParkedRadarAltMeters : ParkedRadarAltMeters;
        return aircraft.radarAlt < ceiling && aircraft.speed < ParkedSpeedMetersPerSecond;
    }

    private static bool IsRotary(Aircraft aircraft)
    {
        if (aircraft.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot != null)
            {
                return pilot.pilotType is Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing;
            }
        }

        return false;
    }

    /// <summary>
    /// Drops a landing order, hands back any capture strength it granted, and gets the aircraft off
    /// the ground again if it is sitting on one.
    /// </summary>
    private static void ClearLandingOrder(Aircraft aircraft, AirMission mission, bool release)
    {
        if (mission.LandingBase == null)
        {
            return;
        }

        if (mission.GrantedCaptureStrength > 0f && aircraft != null && !aircraft.disabled)
        {
            aircraft.ModifyCaptureStrength(-mission.GrantedCaptureStrength);
        }

        mission.GrantedCaptureStrength = 0f;

        bool parked = mission.Parked;
        bool onApproach = mission.LandingIssued && !parked;
        Airbase airbase = mission.LandingBase;
        mission.LandingBase = null;
        mission.Intent = LandingIntent.None;
        mission.LandingIssued = false;
        mission.Parked = false;
        mission.Returning = false;
        mission.RtbIssued = false;
        mission.ResupplyReadyAt = 0f;
        mission.ShortfallReason = string.Empty;
        mission.TaxiToStandSince = 0f;
        mission.TaxiStoppedSince = 0f;

        if (!release || aircraft == null || aircraft.disabled)
        {
            return;
        }

        if (parked)
        {
            ReleaseFromGround(aircraft, airbase);
            return;
        }

        // Still flying the approach. Handover put the pilot into AIPilotLandingState, and nothing in
        // this service steers a pilot that is in it, so dropping the order alone left the aeroplane
        // to land at the field anyway and ignore the point the player had just clicked. Putting the
        // pilot back into AIPilotCombatModes is what makes a new order beat a landing already
        // underway — that state is the one every Air Command patch steers.
        if (onApproach)
        {
            ReturnToCombatState(aircraft);
        }
    }

    /// <summary>
    /// Ejects the crew of a parked RESERVE landing. The ejection only stocks the airframe under
    /// 2 m/s on a held field and destroys it anywhere else, and a sailing carrier's deck never gets
    /// that slow, so one that cannot pass the test in time is stocked without the ejection.
    /// </summary>
    private static void EjectIntoReserve(Aircraft aircraft, AirMission mission)
    {
        if (aircraft.HasEjected())
        {
            return;
        }

        if (aircraft.speed < 2f && mission.Hq.AnyNearAirbase(aircraft.transform.position, out _))
        {
            aircraft.StartEjectionSequence();
        }
        else if (Time.time >= mission.ResupplyReadyAt)
        {
            aircraft.ReturnToInventory();
        }
    }

    /// <summary>
    /// Stops the aircraft where it rolled out and, on a capture, makes it worth something to the
    /// ring it is standing in.
    /// </summary>
    private static void ParkOnDeck(Aircraft aircraft, AirMission mission, Airbase airbase)
    {
        mission.Parked = true;
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} down and parked at {CommanderCaptureService.GetAirbaseLabel(airbase)} "
                + $"({mission.Intent}).");

        // A resupply landing is parked too, and that is the change that stops a bought aircraft
        // disappearing at the end of every sortie. Left to the Basegame the taxi state walks it to a
        // service point, ejects the pilot, and the ejection beside a friendly base is
        // ReturnToInventory — the aeroplane deleted, a stock airframe credited. Parking it holds the
        // same airframe still while ServiceParkedAircraft refuels and rearms it out of a depot.
        //
        // A STANDBY hold gets neither branch: it is already full, it is not taking anything, and it
        // just sits on the pad until the player gives it something to do.
        if (mission.Intent == LandingIntent.Resupply)
        {
            mission.ResupplyReadyAt = Time.time + Mathf.Max(0f, CommanderSettings.AircraftResupplySeconds);
            mission.ShortfallReason = string.Empty;
            CommanderAlertService.Instance?.NotifySupply(
                $"{CommanderGameAccess.GetUnitLabel(aircraft).ToUpperInvariant()} DOWN AT "
                    + $"{CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant()} - SERVICING",
                aircraft);
        }
        else if (mission.Intent == LandingIntent.Reserve)
        {
            mission.ResupplyReadyAt = Time.time + ReserveEjectTimeoutSeconds;
        }

        if (aircraft.pilots != null)
        {
            for (int i = 0; i < aircraft.pilots.Length; i++)
            {
                Pilot pilot = aircraft.pilots[i];
                if (pilot != null)
                {
                    pilot.SwitchState(pilot.parkedState);

                    // The taxi that brought it here ended with its private disembarking flag set,
                    // and EnterState never clears it; the base game makes a fresh one when null.
                    pilot.AITaxiState = null;
                }
            }
        }

        // The parked state only stands on the brakes below 1 m radar altitude, and an aeroplane
        // sitting on its gear can read higher than that. Nothing else writes the controls once the
        // state machine is parked, so this is what actually stops it rolling off the strip.
        // Only once it is genuinely down. On a helicopter throttle is the collective, and zeroing
        // it in the air drops the airframe.
        if (aircraft.radarAlt < RotaryParkedRadarAltMeters)
        {
            ControlInputs inputs = aircraft.GetInputs();
            inputs.throttle = 0f;
            inputs.brake = 1f;
        }

        if (mission.Intent != LandingIntent.Capture)
        {
            return;
        }

        // Zero on the slider means "aircraft do not capture" — the order still lands them, they just
        // stop being worth anything to the ring, which is the honest way to switch this off.
        float strength = Mathf.Max(0f, CommanderSettings.AircraftCaptureStrength);
        if (strength > 0f)
        {
            aircraft.ModifyCaptureStrength(strength);
            mission.GrantedCaptureStrength = strength;
        }

        CommanderAlertService.Instance?.NotifyCaptureOrder(
            $"{CommanderGameAccess.GetUnitLabel(aircraft).ToUpperInvariant()} DOWN ON "
                + CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant(),
            aircraft);
    }

    /// <summary>
    /// RESUPPLY or RESERVE with no field to go to. A banner, not just the status line: that only shows
    /// in the AIR COMMAND window, so with it closed the button looked dead. Says why the nearest field
    /// this side holds was turned down.
    /// </summary>
    private void RefuseNoField(Aircraft aircraft)
    {
        Airbase? closest = FindNearestOwnedAirbase(aircraft);
        string why;
        if (closest == null)
        {
            why = "your side holds no airbase";
        }
        else
        {
            string field = CommanderCaptureService.GetAirbaseLabel(closest);
            why = !HasTakeoffRunway(closest, aircraft.definition, countOverruns: false)
                ? $"{field} has no runway long enough to take off from (needs {aircraft.GetAircraftParameters().takeoffDistance:0} m)"
                : $"{field} has no runway it can land on";
        }

        CommanderPlugin.Log.LogInfo($"{CommanderGameAccess.GetUnitLabel(aircraft)} refused a field: {why}.");
        RefuseLaunch($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no friendly airbase it can land on: {why}.");
    }

    private void ApplyLandingOrder(
        Aircraft aircraft, AirMission mission, Airbase airbase, LandingIntent intent, bool append, bool announce)
    {
        GlobalPosition field = airbase.center.GlobalPosition();

        // An aircraft already standing on the field it is being sent to is serviced where it is.
        // The release below is a taxi-and-takeoff run, and off a highway strip that is the aeroplane
        // that "took off and crashed" the moment RESUPPLY was pressed on one that had already landed.
        bool alreadyDown = IsOnDeck(aircraft)
            && FastMath.InRange(field, aircraft.GlobalPosition(), OnFieldMeters);

        // A redirect from one field to another has to let go of the first, which for an aircraft
        // already sitting on the deck means getting it airborne before it is told to fly anywhere.
        ClearLandingOrder(aircraft, mission, release: !alreadyDown);

        LeaveBaseGameLanding(aircraft);

        if (!append)
        {
            mission.Route.Clear();
            mission.RouteIndex = 0;

            // A resupply break-off saved the patrol to resume; any other landing order ends it.
            mission.RouteLoop &= mission.HasResumeState;
        }

        mission.Route.Add(field);
        mission.AreaCenter = field;
        mission.ForcedTarget = null;
        mission.Returning = false;
        mission.RtbIssued = false;
        mission.LandingBase = airbase;
        mission.Intent = intent;
        mission.LandingIssued = false;
        mission.Parked = false;
        CommanderPlugin.Log.LogInfo(
            $"Landing order: {CommanderGameAccess.GetUnitLabel(aircraft)} to {CommanderCaptureService.GetAirbaseLabel(airbase)} "
                + $"({intent}), {FastMath.Distance(field, aircraft.GlobalPosition()) / 1000f:0.0} km away"
                + (alreadyDown ? ", already on its deck." : "."));
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();

        if (alreadyDown)
        {
            // Skip straight to the state the approach would have reached: Returning stands the route
            // steering down, and ParkOnDeck starts the servicing clock.
            mission.Returning = true;
            mission.RtbIssued = true;
            mission.LandingIssued = true;
            ParkOnDeck(aircraft, mission, airbase);
        }

        if (!announce)
        {
            return;
        }

        string label = CommanderGameAccess.GetUnitLabel(aircraft);
        string field_ = CommanderCaptureService.GetAirbaseLabel(airbase);
        SetStatus(intent switch
        {
            LandingIntent.Capture => $"{label} ordered to land on {field_} and hold it.",
            LandingIntent.Hold => $"{label} standing by at {field_}.",
            LandingIntent.Reserve => $"{label} ordered to land at {field_} and return to reserve.",
            _ => $"{label} ordered to land at {field_} to rearm.",
        });
        CommanderAlertService.Instance?.NotifyCaptureOrder(
            intent switch
            {
                LandingIntent.Capture => $"AIR CAPTURE: {field_.ToUpperInvariant()}",
                LandingIntent.Hold => $"STANDBY: {field_.ToUpperInvariant()}",
                LandingIntent.Reserve => $"RESERVE: {field_.ToUpperInvariant()}",
                _ => $"RESUPPLY: {field_.ToUpperInvariant()}",
            },
            aircraft);
    }

    private void RestoreResumeState(Aircraft aircraft, AirMission mission)
    {
        if (!mission.HasResumeState)
        {
            return;
        }

        mission.Route.Clear();
        mission.Route.AddRange(mission.ResumeRoute);
        mission.RouteIndex = Mathf.Clamp(mission.ResumeRouteIndex, 0, mission.Route.Count);
        mission.AreaCenter = mission.ResumeAreaCenter;
        mission.ResumeRoute.Clear();
        mission.HasResumeState = false;
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();
        _ = aircraft;
    }

    private void ProcessResupplyRequests()
    {
        ServiceLandedAircraft();
        landingSweep.Clear();
        float fuelFloor = ResupplyFuelFloor;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        bool anyLowFuelOrder = false;
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            AirMission mission = entry.Value;
            if (aircraft == null || aircraft.disabled || mission.LandingBase != null)
            {
                continue;
            }

            // Returning normally means "the base game is flying it home, leave it alone". Once it is
            // sitting on the deck that is no longer true: the base game has nothing left to do with
            // it and no landing order was ever issued, so the airframe stands on the apron for the
            // rest of the match. Landing under the game's own fuel check ends up here too.
            if (mission.Returning && !IsOnDeck(aircraft))
            {
                continue;
            }

            // Once the order is flown the override is spent, and the low tank sends it to refuel.
            mission.IgnoreLowFuel = HoldsLowFuelOrder(mission);
            anyLowFuelOrder |= mission.IgnoreLowFuel;

            if (mission.WantsResupply || (aircraft.GetFuelLevel() < fuelFloor && !mission.IgnoreLowFuel))
            {
                landingSweep.Add(aircraft);
            }
        }

        AnyLowFuelOrder = anyLowFuelOrder;
        for (int i = 0; i < landingSweep.Count; i++)
        {
            Aircraft aircraft = landingSweep[i];
            if (!missions.TryGetValue(aircraft, out AirMission mission))
            {
                continue;
            }

            mission.WantsResupply = false;

            // The enemy commander's airframes keep the old behaviour: they go home, the Basegame
            // recovers them into that faction's stock, and its next review launches them again. The
            // service-and-relaunch cycle needs a landing order, and a landing order is only ever
            // issued for the acting faction (IsTaskableAircraft), so a guest's airframe, and on a
            // dedicated server everyone's, is serviced as its own faction.
            if (!IsHumanLed(mission.Hq, localHq))
            {
                mission.Returning = true;
                continue;
            }

            bool resupplying = false;
            CommanderNetwork.RunAs(mission.Hq, () => resupplying = ResupplyAircraft(aircraft));
            if (resupplying)
            {
                CommanderAlertService.Instance?.NotifySupply(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft).ToUpperInvariant()} BREAKING OFF TO REARM", aircraft);
            }
        }
    }

    /// <summary>Gives each aeroplane caught at its stand a resupply landing on the field it stands on,
    /// which parks and services it where it is. One that cannot take the order goes to stock, which
    /// is what the blocked ejection would have done.</summary>
    private void ServiceLandedAircraft()
    {
        foreach (KeyValuePair<Aircraft, Airbase> landed in LandedForService)
        {
            Aircraft aircraft = landed.Key;
            FactionHQ? hq = aircraft == null ? null : aircraft.NetworkHQ;
            if (aircraft == null || aircraft.disabled || hq == null)
            {
                continue;
            }

            if (missions.TryGetValue(aircraft, out AirMission existing))
            {
                SaveResumeState(existing);
            }

            bool serviced = false;
            CommanderNetwork.RunAs(
                hq, () => serviced = SetAircraftLandingOrder(aircraft, landed.Value, LandingIntent.Resupply, append: false));
            if (!serviced)
            {
                aircraft.ReturnToInventory();
            }
        }

        LandedForService.Clear();
    }

    /// <summary>
    /// Services a parked aircraft out of a supply depot and launches it back onto its mission. A
    /// depot with nothing on the shelf leaves it where it is and says so, once, which is the whole
    /// point of the stores existing.
    /// </summary>
    private void ServiceParkedAircraft(Aircraft aircraft, AirMission mission, Airbase airbase)
    {
        // Parked on the strip (an order given to one already down there): off it first, and the
        // landing sweep parks it again, with a fresh servicing clock, once it is clear.
        if (TryTaxiOffRunway(aircraft, mission, airbase))
        {
            mission.Parked = false;
            return;
        }

        if (Time.time < mission.ResupplyReadyAt)
        {
            return;
        }

        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        if (stores == null)
        {
            return;
        }

        launchLoadouts.TryGetValue(aircraft, out NuclearOption.SavedMission.Loadout? launchLoadout);
        if (!stores.TryResupplyAircraft(aircraft, airbase, launchLoadout, out string message))
        {
            // Parked and waiting: the moment the player buys stock the next sweep launches it.
            if (!mission.ReportedShortfall)
            {
                mission.ShortfallReason = message;
                SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} is held at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(airbase)}: {message}.");
                CommanderAlertService.Instance?.NotifySupply(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft).ToUpperInvariant()} GROUNDED - "
                        + message.ToUpperInvariant(),
                    aircraft);
            }

            mission.ResupplyReadyAt = Time.time + 15f;

            // An AI side never buys the stock it waits for, so it would sit on the stand for good and
            // keep the next lander off it. Its sortie is over: back to reserve.
            if (mission.GroundedSince <= 0f)
            {
                mission.GroundedSince = Time.time;
            }
            else if (Time.time - mission.GroundedSince > AiGroundedReserveSeconds
                && !IsHumanLed(mission.Hq, CommanderGameAccess.GetLocalHq()))
            {
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} grounded at {CommanderCaptureService.GetAirbaseLabel(airbase)} "
                        + $"({message}); its pilot ejects into reserve.");
                mission.Intent = LandingIntent.Reserve;
                mission.ResupplyReadyAt = Time.time + ReserveEjectTimeoutSeconds;
                EjectIntoReserve(aircraft, mission);
            }

            return;
        }

        mission.GroundedSince = 0f;
        mission.ShortfallReason = string.Empty;

        // A completed sortie is proof the airframe can fly off this field, so the rescue budget is
        // handed back. Without this an aeroplane that flies all match eventually spends its three
        // and is abandoned on a strip it has used a dozen times.
        mission.GroundRescues = 0;
        ClearLandingOrder(aircraft, mission, release: true);
        RestoreResumeState(aircraft, mission);
        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} {message} and is back on task.");
        CommanderAlertService.Instance?.NotifySupply(
            $"{CommanderGameAccess.GetUnitLabel(aircraft).ToUpperInvariant()} REARMED - BACK ON TASK", aircraft);
    }

    /// <summary>
    /// Lets the pilot out of an aircraft that was held on the ground and has since been wrecked. A
    /// blocked ejection leaves the pilot in <c>PilotParkedState</c>, which never checks anything
    /// again, so an airframe destroyed after it was parked (or one the old detached-only test passed
    /// as intact) sat in a capture ring for the rest of the match, counted as a head and taking the
    /// base. Ejecting it now ends in <c>DisableUnit</c> away from a friendly field, exactly as the
    /// base game would have done.
    /// </summary>
    private void ReleaseHeldWrecks()
    {
        landingSweep.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            if (aircraft != null
                && !aircraft.disabled
                && aircraft.Player == null
                && aircraft.radarAlt <= GroundEjectionAltitudeMeters
                && IsWrecked(aircraft))
            {
                landingSweep.Add(aircraft);
            }
        }

        // Collected first: the ejection ends in DisableUnit, which drops the mission.
        for (int i = 0; i < landingSweep.Count; i++)
        {
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(landingSweep[i])} is wrecked on the ground; its pilot bails out.");
            landingSweep[i].StartEjectionSequence();
        }
    }

    /// <summary>
    /// Last resort after every other rescue: an aeroplane that has not moved
    /// <see cref="GroundStillMeters"/> for <see cref="StuckOnRunwaySeconds"/> on a runway (or
    /// <see cref="StuckOnGroundSeconds"/> taxiing elsewhere) at a field its side holds ejects into
    /// reserve. Two that touch on the strip both brake for each other, and the game's taxi zeroes its
    /// stuck timer while braking, so they stood there blocking every lander for the rest of the match.
    /// </summary>
    private void EjectStuckOnGround()
    {
        landingSweep.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            AirMission mission = entry.Value;
            Pilot? pilot = aircraft == null || aircraft.pilots == null || aircraft.pilots.Length == 0 ? null : aircraft.pilots[0];
            if (pilot == null
                || aircraft!.disabled
                || aircraft.Player != null
                || aircraft.HasEjected()
                || aircraft.radarAlt > ParkedRadarAltMeters
                || IsRotary(aircraft)
                || mission.Intent == LandingIntent.Reserve
                || !mission.Hq.AnyNearAirbase(aircraft.transform.position, out Airbase field))
            {
                mission.GroundStillSince = 0f;
                continue;
            }

            Vector3 at = aircraft.transform.position;
            if (mission.GroundStillSince <= 0f || (at - mission.GroundStillAt).sqrMagnitude > GroundStillMeters * GroundStillMeters)
            {
                mission.GroundStillAt = at;
                mission.GroundStillSince = Time.time;
                continue;
            }

            // A capture or STANDBY park is meant to stand; one parked for service off the runway too.
            // Centre on the pavement: one holding short for clearance is waiting, not in the way.
            bool onRunway = false;
            for (int r = 0; field.runways != null && r < field.runways.Length && !onRunway; r++)
            {
                onRunway = field.runways[r]?.Start != null && IsOnPavement(field.runways[r], aircraft);
            }

            bool moving = pilot.currentState is AIPilotTaxiState or AIPilotTakeoffState;
            bool parkedInTheWay = onRunway && mission.Intent is not (LandingIntent.Capture or LandingIntent.Hold);
            float limit = onRunway ? StuckOnRunwaySeconds : StuckOnGroundSeconds;
            if ((moving || parkedInTheWay) && Time.time - mission.GroundStillSince > limit)
            {
                landingSweep.Add(aircraft);
            }
        }

        for (int i = 0; i < landingSweep.Count; i++)
        {
            Aircraft aircraft = landingSweep[i];
            if (aircraft == null
                || aircraft.disabled
                || !missions.TryGetValue(aircraft, out AirMission mission)
                || !mission.Hq.AnyNearAirbase(aircraft.transform.position, out Airbase field))
            {
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} stood stuck {Time.time - mission.GroundStillSince:0} s at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(field)} ('{aircraft.pilots[0].currentState?.GetType().Name}'); its pilot ejects into reserve.");

            // As a RESERVE landing, so the ejection guard lets it go and the landing tick finishes it
            // (stocked without the ejection if it is not still in time).
            aircraft.pilots[0].SwitchState(aircraft.pilots[0].parkedState);
            ControlInputs inputs = aircraft.GetInputs();
            inputs.throttle = 0f;
            inputs.brake = 1f;
            mission.LandingBase = field;
            mission.Intent = LandingIntent.Reserve;
            mission.LandingIssued = true;
            mission.Parked = true;
            mission.ResupplyReadyAt = Time.time + ReserveEjectTimeoutSeconds;
            EjectIntoReserve(aircraft, mission);
        }
    }

    /// <summary>Lifts clear anything the ejection prefix caught on the ground this sweep.</summary>
    private void ProcessGroundRescues()
    {
        for (int i = 0; i < UntaskedRescues.Count; i++)
        {
            Aircraft aircraft = UntaskedRescues[i];
            if (aircraft == null || aircraft.disabled)
            {
                continue;
            }

            if (TryRescueByLineUp(aircraft))
            {
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} could not taxi out on its own; back on the runway queue.");
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} could not taxi out on its own and has no runway to line up on; returned to stock.");
            NoteFailedTakeoff(aircraft);
            aircraft.ReturnToInventory();
        }

        UntaskedRescues.Clear();
        ReleaseHeldWrecks();
        EjectStuckOnGround();
        landingSweep.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Value.NeedsGroundRescue)
            {
                landingSweep.Add(entry.Key);
            }
        }

        for (int i = 0; i < landingSweep.Count; i++)
        {
            Aircraft aircraft = landingSweep[i];
            if (!missions.TryGetValue(aircraft, out AirMission mission))
            {
                continue;
            }

            mission.NeedsGroundRescue = false;
            if (aircraft == null || aircraft.disabled)
            {
                continue;
            }

            // First answer: line it up on the runway and roll again. The taxi is what usually fails, and a
            // rolling start from the threshold is a real takeoff. The airborne lift is the last resort.
            Airbase? homeField = aircraft.NetworkHQ?.GetNearestAirbase(aircraft.transform.position, 3000f);
            bool failedLineUp = mission.GroundRescues > 1 && homeField != null && IsOnAnyRunway(aircraft, homeField);
            if (!failedLineUp && mission.GroundRescues < MaxGroundRescues && !HasRotaryPilot(aircraft) && TryRescueByLineUp(aircraft))
            {
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} could not get off the ground on its own "
                        + $"(rescue {mission.GroundRescues} of {MaxGroundRescues}); back on the runway queue.");
                continue;
            }

            // A fixed-wing airframe whose line-up already failed once goes back into stock at the field: it
            // is standing on the threshold where the next takeoff would hit it, and something is broken that
            // another line-up will not fix. A stocked airframe can be launched again; a wreck cannot.
            if (!HasRotaryPilot(aircraft) && homeField != null)
            {
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} could not get off the strip on its own "
                        + $"(rescue {mission.GroundRescues} of {MaxGroundRescues}); returned to stock.");
                NoteFailedTakeoff(aircraft);
                aircraft.ReturnToInventory();
                continue;
            }

            // The airborne release and not AIPilotTakeoffState: the takeoff has failed repeatedly,
            // so putting the aeroplane back into it would loop.
            ReleaseFromGround(aircraft, null);
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} could not get off the strip on its own "
                    + $"(rescue {mission.GroundRescues} of {MaxGroundRescues}); lifted clear.");
            if (!HasRotaryPilot(aircraft))
            {
                NoteFailedTakeoff(aircraft);
            }
        }
    }

    /// <summary>
    /// Per persistent tick: fly the approach, park on touchdown, and let go once the base is taken.
    /// </summary>
    private void TickLandingOrders()
    {
        TickDepartures();
        landingSweep.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Value.LandingBase != null)
            {
                landingSweep.Add(entry.Key);
            }
        }

        for (int i = 0; i < landingSweep.Count; i++)
        {
            Aircraft aircraft = landingSweep[i];
            if (aircraft == null || aircraft.disabled || !missions.TryGetValue(aircraft, out AirMission mission))
            {
                continue;
            }

            Airbase? airbase = mission.LandingBase;
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                ClearLandingOrder(aircraft, mission, release: true);
                continue;
            }

            // The base fell while the aircraft was holding it: the order is done, so give the
            // airframe back rather than leaving it parked on a field it no longer has to sit on.
            if (mission.Intent == LandingIntent.Capture && airbase.CurrentHQ == mission.Hq)
            {
                CommanderAlertService.Instance?.NotifyCaptureOrder(
                    $"{CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant()} TAKEN — AIRBORNE AGAIN",
                    aircraft);

                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} held {CommanderCaptureService.GetAirbaseLabel(airbase)} "
                        + "until it fell; queued to take off.");
                ClearLandingOrder(aircraft, mission, release: true);
                continue;
            }

            if (mission.Parked)
            {
                if (mission.Intent == LandingIntent.Resupply)
                {
                    ServiceParkedAircraft(aircraft, mission, airbase);
                }
                else if (mission.Intent == LandingIntent.Reserve)
                {
                    EjectIntoReserve(aircraft, mission);
                }

                continue;
            }

            float deckCeiling = IsRotary(aircraft) ? RotaryParkedRadarAltMeters : ParkedRadarAltMeters;

            // A STANDBY spawn is already on its field. Handing it to the base game's landing approach
            // there sent it through the landing and taxi states to a service stand, which the player
            // saw as a fresh aircraft refuelling; it is parked where it stands instead, even while it
            // is still rolling out of the hangar (ParkOnDeck stands it on the brakes).
            if (mission.Intent == LandingIntent.Hold
                && !mission.LandingIssued
                && aircraft.radarAlt < deckCeiling
                && FastMath.InRange(airbase.center.GlobalPosition(), aircraft.GlobalPosition(), OnFieldMeters))
            {
                mission.LandingIssued = true;
                ParkOnDeck(aircraft, mission, airbase);
                continue;
            }

            bool onDeck = aircraft.radarAlt < deckCeiling && aircraft.speed < ParkedSpeedMetersPerSecond;
            if (onDeck && mission.LandingIssued)
            {
                if (!IsTaxiingToStand(aircraft, mission) && !TryTaxiOffRunway(aircraft, mission, airbase))
                {
                    ParkOnDeck(aircraft, mission, airbase);
                }

                continue;
            }

            if (!mission.LandingIssued
                && FastMath.InRange(airbase.center.GlobalPosition(), aircraft.GlobalPosition(), LandingHandoverMeters))
            {
                // The route has done its job; from here the Basegame approach flies the aeroplane,
                // and Returning stands the route steering down so the two do not fight.
                mission.Returning = true;
                mission.RtbIssued = true;
                mission.LandingIssued = true;
                IssueReturnToBase(aircraft, mission);
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} handed to the landing approach at "
                        + $"{CommanderCaptureService.GetAirbaseLabel(airbase)} ({mission.Intent}).");
            }
        }
    }
}
