using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// How close an escort is kept to the thing it is guarding. It is the mission radius, so the
    /// existing station leash does the work: destinations clamp to three quarters of it and the
    /// aircraft is recalled past nine tenths, which is a fighter weaving around its bomber rather
    /// than one wandering off after the first contact it sees.
    /// </summary>
    private const float EscortRadiusMeters = 3000f;

    /// <summary>
    /// Anything friendly the AI is flying and the player may give orders to. Rotary airframes count
    /// now: the <c>AIHeloCombatState.NoTarget</c> postfix already reads the same mission hold point
    /// the fixed-wing patch does, so a helicopter bought from a landing pad takes travel and attack
    /// points exactly as an aeroplane does. Only <c>PilotType.VTOL</c>, which the base game gives no
    /// AI state at all, still cannot be commanded — and it cannot be bought either.
    /// </summary>
    internal static bool IsTaskableAircraft(Aircraft? aircraft)
    {
        return aircraft != null
            && !aircraft.disabled
            && aircraft.Player == null
            && aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && HasAiPilot(aircraft)
            && CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq());
    }

    /// <summary>
    /// True when an aircraft is on HOLD FIRE. An attack order does not lift it: the aircraft flies at
    /// the target, and only the selection bar's FIRE shoots.
    /// </summary>
    internal static bool IsHoldingAiFire(Aircraft aircraft)
    {
        if (!CommanderMoveService.IsHoldingFire(aircraft))
        {
            return false;
        }

        CommanderMovePatches.CommanderHoldFirePatch.LogOnce(aircraft, "trigger");
        return true;
    }

    /// <summary>
    /// The aircraft row. It says what the aeroplane is, what it costs and whether one is already in
    /// stock — not how many of the weapon currently picked in a different panel it happens to fit,
    /// which is what the old row said and nobody could act on.
    /// </summary>
    internal static string GetOptionLabel(AirMissionOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = supply > 0
            ? $"IN STOCK x{supply}"
            : CommanderDraftService.IsDraftMission
                ? "NONE LEFT"
                : CommanderEconomyService.FundsLabel(CommanderEconomyService.PriceOf(option.Definition));
        return $"{GetAircraftLabel(option.Definition)}\n{GetAirframeRole(option.Definition)}  |  {cost}";
    }

    internal static string GetAirbaseLabel(AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{option.Label}  |  {distance}";
    }

    /// <summary>Friendly AI aircraft that are airborne or parked and not already on a Commander mission.</summary>
    internal void CollectIdleAircraft(List<Aircraft> aircraft)
    {
        aircraft.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit)
                && unit is Aircraft candidate
                && IsTaskableAircraft(candidate)
                && !missions.ContainsKey(candidate))
            {
                aircraft.Add(candidate);
            }
        }

        aircraft.Sort((left, right) => string.Compare(
            CommanderGameAccess.GetUnitLabel(left), CommanderGameAccess.GetUnitLabel(right), StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsCommandedAircraft(Aircraft aircraft)
    {
        return missions.ContainsKey(aircraft);
    }

    /// <summary>Starts placing a mission area for an aircraft that is already in the air.</summary>
    internal void BeginAdoption(Aircraft aircraft)
    {
        if (!IsTaskableAircraft(aircraft))
        {
            SetStatus("That aircraft cannot be tasked.");
            return;
        }

        if (TryAreaLessOrder(aircraft, SelectedMode))
        {
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = aircraft;
        OpenOrderMap();
        UpdatePendingAreaPreview();
        SetStatus($"Select the {GetModeLabel(SelectedMode)} mission area for {CommanderGameAccess.GetUnitLabel(aircraft)}.");
    }

    /// <summary>
    /// Re-tasks a package that is already flying as another mission type over the same area, so
    /// STANDBY to AIR SUPERIORITY is one click rather than a new circle. AUTOPILOT and STANDBY have
    /// no area and take the same paths TASK gives them.
    /// </summary>
    internal void ChangeMissionMode(Aircraft aircraft, AirCommandMode mode)
    {
        if (TryAreaLessOrder(aircraft, mode) || !missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        if (ForwardAir(
            "air.adopt",
            aircraft,
            CommanderNetwork.Num((int)mode),
            CommanderNetwork.Position(mission.AreaCenter),
            CommanderNetwork.Num(mission.Radius)))
        {
            return;
        }

        SetStatus(TryAdoptAircraft(aircraft, mode, mission.AreaCenter, mission.Radius)
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)} re-tasked with a {GetModeLabel(mode)} mission."
            : "That aircraft can no longer be tasked.");
    }

    /// <summary>
    /// Puts a mission on an aircraft of a faction the player does not command; this is how the enemy
    /// commander tasks its airframes. Everything downstream is faction-agnostic, so this is only the
    /// mission record: no unit list pin, status line or map circle. Without a mission the base-game
    /// <c>NoTarget</c> timer flies a fresh AI aircraft home after 15 ticks.
    /// <para>
    /// <paramref name="allowRotary"/> opens it to helicopters and tiltwings. Normally they must not
    /// carry one, since every flight patch targets <c>AIPilotCombatModes</c> and a rotary airframe
    /// would get a target with no steering. The convoy escort is the exception: there the mission is
    /// only a target filter, and <c>AIHeloCombatState</c> does all the flying.
    /// </para>
    /// </summary>
    internal bool TryTaskAiAircraft(
        Aircraft? aircraft,
        AirCommandMode mode,
        GlobalPosition center,
        float radius,
        bool allowRotary = false)
    {
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null
            || hq == null
            || aircraft.disabled
            || aircraft.Player != null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || (!HasPlanePilot(aircraft) && !allowRotary)
            || missions.ContainsKey(aircraft))
        {
            return false;
        }

        missions[aircraft] = new AirMission(hq, mode, center, radius, 0f, false, false, false, 0f);
        return true;
    }

    /// <summary>Sets a non-player airframe's cruise height; 0 hands the height back to the base game.</summary>
    internal void SetAiCruiseAltitude(Aircraft aircraft, float meters)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission)
            && !CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            mission.TargetAltitude = meters;
        }
    }

    /// <summary>The centre of the area an aircraft's mission works, if it has one.</summary>
    internal bool TryGetMissionAreaCenter(Aircraft aircraft, out GlobalPosition centre)
    {
        centre = default;
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        centre = mission.AreaCenter;
        return true;
    }

    /// <summary>
    /// Hands a non-player airframe a target of the commander's choosing once it has none left. The
    /// enemy's heavy bombers use it to go after the player's most valuable building rather than
    /// whatever the box search finds nearest. True when a new target was set.
    /// </summary>
    internal bool TrySetAiForcedTarget(Aircraft aircraft, Unit? target)
    {
        if (target == null
            || !missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq())
            || (mission.ForcedTarget != null && !mission.ForcedTarget.disabled))
        {
            return false;
        }

        mission.ForcedTarget = target;
        return true;
    }

    /// <summary>The live target a mission was given, or null.</summary>
    internal Unit? GetForcedTarget(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) && mission.ForcedTarget != null && !mission.ForcedTarget.disabled
            ? mission.ForcedTarget
            : null;
    }

    /// <summary>Commits a non-player airframe to its attack or holds it back; see
    /// <see cref="AirMission.IgnoreGroundThreats"/>.</summary>
    internal void SetAiCommitted(Aircraft aircraft, bool committed)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission)
            && !CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            mission.IgnoreGroundThreats = committed;
        }
    }

    /// <summary>Gives a non-player airframe one travel point to fly through before its mission area,
    /// the enemy's flank approach. The route steering already outranks the attack (see
    /// <c>ConstrainMissionDestination</c>), so the wave goes round and then turns in.</summary>
    internal void SetAiRoute(Aircraft aircraft, GlobalPosition point)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission)
            && mission.LandingBase == null
            && !CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            mission.Route.Clear();
            mission.Route.Add(point);
            mission.RouteIndex = 0;
        }
    }

    /// <summary>Drops whatever is left of a non-player airframe's travel points.</summary>
    internal void ClearAiRoute(Aircraft aircraft)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission)
            && mission.LandingBase == null
            && !CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            mission.Route.Clear();
            mission.RouteIndex = 0;
        }
    }

    /// <summary>
    /// Moves a non-player airframe's mission area without re-issuing the mission. The convoy escort
    /// needs it every review, because the thing it is escorting drives away.
    /// </summary>
    internal bool RetargetAiMission(Aircraft? aircraft, AirCommandMode mode, GlobalPosition center, float radius)
    {
        if (aircraft == null
            || aircraft.disabled
            || aircraft.Player != null
            || !missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || mission.LandingBase != null
            || CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        mission.Mode = mode;
        mission.AreaCenter = center;
        mission.Radius = radius;
        return true;
    }

    /// <summary>
    /// Sends a non-player airframe that already has a mission back out, when the mission it has is
    /// one it will never fly again. Returns false for anything still doing its job.
    /// </summary>
    /// <remarks>
    /// An enemy sortie that runs dry sets <c>Returning</c> and hands the aeroplane to the base-game
    /// landing, meant to end in <c>ReturnToInventory</c>. That does not always complete (e.g. no taxi
    /// network on a highway strip), leaving a <c>Returning</c> mission nothing clears, which
    /// <see cref="TryTaskAiAircraft"/> then refuses.
    /// <para>
    /// An airframe with fuel and ordnance left is put back on the board. One that is genuinely empty is
    /// left alone: it needs the recovery, not another sortie.
    /// </para>
    /// </remarks>
    internal bool TryRelaunchIdleAiAircraft(
        Aircraft? aircraft, AirCommandMode mode, GlobalPosition center, float radius)
    {
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null
            || hq == null
            || aircraft.disabled
            || aircraft.Player != null
            || !HasPlanePilot(aircraft)
            || !missions.TryGetValue(aircraft, out AirMission mission)
            || CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        // Only a stood-down mission. Anything still flying its area is already doing the job, and
        // an airframe on a capture landing is Returning on purpose.
        if ((!mission.Returning && !mission.WantsResupply) || mission.LandingBase != null)
        {
            return false;
        }

        // Genuinely dry: leave it to the recovery it is already flying.
        if (aircraft.GetFuelLevel() < 0.25f || CommanderSupplyStoreService.GetMissingAmmoKg(aircraft) > 0f)
        {
            return false;
        }

        mission.Returning = false;
        mission.RtbIssued = false;
        mission.WantsResupply = false;
        mission.Mode = mode;
        mission.AreaCenter = center;
        mission.Radius = radius;
        mission.Route.Clear();
        mission.RouteIndex = 0;

        // On the deck it needs lifting clear, not just a state change: the aeroplane got here
        // because the field's taxi and takeoff states already gave up on it once.
        if (aircraft.radarAlt <= GroundEjectionAltitudeMeters)
        {
            ReleaseFromGround(aircraft, null);
        }
        else
        {
            ReturnToCombatState(aircraft);
        }

        return true;
    }

    /// <summary>
    /// Applies a Commander travel or attack point to a single aircraft, tasking it first if
    /// it was still flying its own Basegame orders.
    /// </summary>
    internal void SetAircraftOrder(
        Aircraft aircraft, GlobalPosition point, Unit? attackTarget, bool append, AirCommandMode? modeOverride = null)
    {
        if (!IsTaskableAircraft(aircraft))
        {
            return;
        }

        // The mode is decided where the click was, so a guest's order carries the guest's pick.
        AirCommandMode mode = modeOverride ?? (attackTarget != null ? GetModeForTarget(attackTarget) : GetOrderMode());
        if (ForwardAir(
            "air.order",
            aircraft,
            CommanderNetwork.Position(point),
            CommanderNetwork.Id(attackTarget),
            CommanderNetwork.Flag(append),
            CommanderNetwork.Num((int)mode)))
        {
            return;
        }

        // A second target with the queue key held is added behind the one already ordered rather
        // than replacing it, the same way a second map click adds a travel point. The mission area
        // stays on the target actually being attacked: it is what clamps the attack destination, so
        // moving it to a target still queued would pull the aircraft off the one it is working.
        GlobalPosition center = attackTarget != null ? attackTarget.GlobalPosition() : point;
        bool queueTarget = append
            && attackTarget != null
            && missions.TryGetValue(aircraft, out AirMission ordered)
            && ordered.ForcedTarget != null
            && !ordered.ForcedTarget.disabled
            && !ReferenceEquals(ordered.ForcedTarget, attackTarget)
            && !ordered.QueuedTargets.Contains(attackTarget);
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            if (!TryAdoptAircraft(aircraft, mode, center, GetMissionRadius(mode)))
            {
                return;
            }

            mission = missions[aircraft];
        }
        else if (attackTarget != null && !queueTarget)
        {
            mission.Mode = mode;
            mission.AreaCenter = center;
            DestroyMissionMapVisual(mission);
        }

        // A player order outranks a low tank, on the way to the pumps or parked at them. Without the flag the next resupply sweep, or the base game's own 20 %
        // check, saw the same low fuel and turned it straight back for the field.
        // A new airframe's tanks hold nothing until its first Refuel, which comes after registration,
        // and a cargo run gives its order at registration: that read 0 % on a full Chimera.
        float fuel = aircraft.GetFuelLevel();
        bool tanksNotFilledYet = fuel <= 0f && aircraft.pilots?[0]?.flightInfo?.HasTakenOff != true;
        mission.IgnoreLowFuel = fuel < ResupplyFuelFloor && !tanksNotFilledYet;

        // Any plain order supersedes a landing order, which is what gets an aircraft parked on a
        // base it was told to take — or already on the approach to one — back into the air the
        // moment the player wants it elsewhere. The remembered mission goes with it: the player
        // has just replaced the thing it was going to resume.
        ClearLandingOrder(aircraft, mission, release: true);

        LeaveBaseGameLanding(aircraft);

        mission.WantsResupply = false;
        mission.HasResumeState = false;
        mission.ResumeRoute.Clear();

        if (!append)
        {
            mission.Route.Clear();
            mission.RouteIndex = 0;
            mission.RouteLoop = false;
        }

        if (queueTarget)
        {
            mission.QueuedTargets.Add(attackTarget!);
        }
        else
        {
            mission.ForcedTarget = attackTarget;
        }

        mission.Returning = false;

        // An attack order on something on the ground commits the aircraft: once that target is
        // gone it keeps working the area around it, inside the enemy's air defence cover if need
        // be, until its bombs are spent, then rearms. Without it a bomber dropped two or three on
        // the target and turned away with most of its load, because everything else nearby sat
        // under a SAM umbrella the automatic search refuses to enter. Any other order uncommits it.
        mission.IgnoreGroundThreats = attackTarget != null && attackTarget is not Aircraft and not Missile;

        // A point on the map is a new job; it ends the escort rather than fighting it for the
        // station every frame.
        mission.EscortTarget = null;
        if (attackTarget == null)
        {
            mission.Route.Add(point);

            // The last travel point doubles as the station the aircraft settles on.
            mission.AreaCenter = point;
            DestroyMissionMapVisual(mission);
        }

        RefreshMissionMapVisuals();
        SetStatus(attackTarget != null
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)} ordered to attack "
                + $"{CommanderGameAccess.GetUnitLabel(attackTarget)}"
                + (mission.QueuedTargets.Count > 0 ? $" (+{mission.QueuedTargets.Count} queued)." : ".")
            : $"{CommanderGameAccess.GetUnitLabel(aircraft)} travel point set.");

        if (mission.IgnoreLowFuel)
        {
            CommanderNotice.Fail(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} is low on fuel ({fuel * 100f:0}%): "
                    + "it flies this order first, then goes to refuel.");
        }
    }

    /// <summary>
    /// Escort order: the aircraft stays with <paramref name="escortTarget"/>, shoots down what
    /// comes at it — aircraft and incoming missiles both, which is what AIR SUPERIORITY plus
    /// ordnance targeting already is — and is leashed to the escortee's own position rather than
    /// to a fixed box, so it goes where the escortee goes.
    /// </summary>
    internal bool SetAircraftEscortOrder(Aircraft aircraft, Unit escortTarget)
    {
        if (ReferenceEquals(aircraft, escortTarget)
            || !IsTaskableAircraft(aircraft)
            || escortTarget == null
            || escortTarget.disabled)
        {
            return false;
        }

        if (ForwardAir("air.escort", aircraft, CommanderNetwork.Id(escortTarget)))
        {
            return true;
        }

        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            if (!TryAdoptAircraft(
                aircraft, AirCommandMode.AirGuard, escortTarget.GlobalPosition(), EscortRadiusMeters))
            {
                return false;
            }

            mission = missions[aircraft];
        }

        // Same reset a plain travel point does: an escort supersedes a landing order, a resupply
        // request and whatever route the aircraft was still flying.
        ClearLandingOrder(aircraft, mission, release: true);
        LeaveBaseGameLanding(aircraft);
        mission.WantsResupply = false;
        mission.HasResumeState = false;
        mission.ResumeRoute.Clear();
        mission.Route.Clear();
        mission.RouteIndex = 0;
        mission.RouteLoop = false;
        mission.ForcedTarget = null;
        mission.IgnoreGroundThreats = false;
        mission.Returning = false;
        mission.Mode = AirCommandMode.AirGuard;
        mission.Radius = EscortRadiusMeters;
        mission.TargetOrdnance = true;
        mission.EscortTarget = escortTarget;
        mission.IgnoreLowFuel = aircraft.GetFuelLevel() < ResupplyFuelFloor;
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();
        CommanderSelectionService.PinMissionUnit(aircraft, "AIR COMMAND", "ESCORT");
        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} escorting "
            + $"{CommanderGameAccess.GetUnitLabel(escortTarget)}.");
        return true;
    }

    /// <param name="aircraft">The aircraft whose route is cleared.</param>
    /// <param name="releaseParked">False for the player's STOP: an aircraft parked on a landing
    /// order is already stopped, and releasing it is a takeoff roll, a runway line-up or the 350 m
    /// airborne lift, which is what "press stop and it blows up" was.</param>
    internal void ClearAircraftRoute(Aircraft aircraft, bool releaseParked = false)
    {
        if (ForwardAir("air.clear", aircraft))
        {
            return;
        }

        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        if (mission.Parked && !releaseParked)
        {
            return;
        }

        ClearLandingOrder(aircraft, mission, release: true);
        mission.WantsResupply = false;
        mission.HasResumeState = false;
        mission.ResumeRoute.Clear();
        mission.Route.Clear();
        mission.RouteIndex = 0;
        mission.RouteLoop = false;
        mission.ForcedTarget = null;
        mission.IgnoreGroundThreats = false;
        mission.AreaCenter = aircraft.GlobalPosition();
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();
    }

    /// <summary>
    /// Host only. Drops every Air Command order so a cargo run can fly the aircraft, lifting it off
    /// if it is parked. A helicopter leaves Air Command altogether, as on any cargo run:
    /// <c>AIHeloTransportState</c> flies the delivery. A fixed-wing run is steered by Air Command, so
    /// it keeps its mission. Nothing else takes a helicopter out of a landing the base game began.
    /// </summary>
    internal void ReleaseForCargoRun(Aircraft aircraft)
    {
        ClearAircraftRoute(aircraft, releaseParked: true);
        if (!IsRotary(aircraft))
        {
            return;
        }

        RemoveMission(aircraft);
        Pilot pilot = aircraft.pilots[0];
        if (pilot != null && pilot.currentState is AIHeloLandingState)
        {
            ReturnToCombatState(aircraft);
        }
    }

    internal bool TryGetAircraftRoute(
        Aircraft aircraft,
        out IReadOnlyList<GlobalPosition> route,
        out int index,
        out Unit? attackTarget)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission))
        {
            route = mission.Route;
            index = mission.RouteIndex;
            attackTarget = mission.ForcedTarget;
            return true;
        }

        route = Array.Empty<GlobalPosition>();
        index = 0;
        attackTarget = null;
        return false;
    }

    internal bool IsAircraftPatrolling(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) && mission.RouteLoop;
    }

    /// <summary>True when the aircraft's own route (or the one a resupply will resume) has two or more points.</summary>
    internal bool CanAircraftPatrol(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission)
            && (mission.HasResumeState ? mission.ResumeRoute.Count : mission.LandingBase == null ? mission.Route.Count : 0) > 1;
    }

    /// <summary>PATROL for an aircraft: its travel points become a loop, or back into a one-shot route.</summary>
    internal void SetAircraftPatrol(Aircraft aircraft, bool enable)
    {
        if (ForwardAir("air.patrol", aircraft, CommanderNetwork.Flag(enable)))
        {
            return;
        }

        if (CanAircraftPatrol(aircraft))
        {
            missions[aircraft].RouteLoop = enable;
        }
    }

    /// <summary>The unit an escort order has this aircraft guarding, if any.</summary>
    internal Unit? GetEscortTarget(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) ? mission.EscortTarget : null;
    }

    /// <summary>
    /// The travel point an aircraft flown by someone other than Air Command should head for,
    /// advancing past each one within <paramref name="arrivalMeters"/> (horizontally). False once the
    /// route is done. The sling lift uses it, so travel points given to a helicopter carrying a load
    /// are the same route, line and numbering as for any other aircraft.
    /// </summary>
    internal bool TryGetNextRoutePoint(Aircraft aircraft, float arrivalMeters, out GlobalPosition point)
    {
        point = default;
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        GlobalPosition at = aircraft.GlobalPosition();
        bool wrapped = false;
        while (mission.RouteIndex < mission.Route.Count)
        {
            point = mission.Route[mission.RouteIndex];
            float dx = point.x - at.x;
            float dz = point.z - at.z;
            if ((dx * dx) + (dz * dz) > arrivalMeters * arrivalMeters)
            {
                return true;
            }

            mission.RouteIndex++;
            if (!wrapped)
            {
                wrapped = mission.TryWrapRoute();
            }
        }

        return false;
    }

    internal void BeginMissionAreaEdit(Aircraft aircraft)
    {
        if (!missions.ContainsKey(aircraft))
        {
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = aircraft;
        selectedMissionAircraft = aircraft;
        OpenOrderMap();
        UpdatePendingAreaPreview();
        RefreshMissionMapVisuals();
        SetStatus("Select the new mission-area center on the tactical map or in the 3D world.");
    }

    internal void SelectPrimaryWeapon(int index)
    {
        SelectedPrimaryWeaponIndex = index >= 0 && index < weaponOptions.Count ? index : -1;
        if (SelectedPrimaryWeapon != null && SameMountType(SelectedPrimaryWeapon, SelectedSecondaryWeapon))
        {
            SelectedSecondaryWeaponIndex = -1;
        }

        ApplySelectedWeapons();
    }

    internal void SelectSecondaryWeapon(int index)
    {
        WeaponMount? candidate = index >= 0 && index < weaponOptions.Count ? weaponOptions[index] : null;
        SelectedSecondaryWeaponIndex = candidate != null && !SameMountType(candidate, SelectedPrimaryWeapon) ? index : -1;
        ApplySelectedWeapons();
    }

    /// <summary>
    /// Fills the airframe's cargo bays, or empties them with -1. Kept apart from the primary and
    /// secondary weapon picks because a cargo bay is not a pylon: a transport wants weapons on its
    /// hardpoints *and* a load in the bay, and choosing cargo as the "primary weapon" is the only
    /// way the two-dropdown editor could express it, which threw the weapons away.
    /// </summary>
    internal void SelectCargoWeapon(int index)
    {
        SelectedCargoWeaponIndex = index >= 0 && index < cargoOptions.Count ? index : -1;
        ApplySelectedWeapons();
    }

    internal void BeginAreaSelection()
    {
        AirMissionOption? option = SelectedOption;
        AirbaseOption? airbase = SelectedAirbase;
        if (option == null)
        {
            RefuseLaunch("No aircraft with configurable hardpoints is available.");
            return;
        }

        if (!HasUsableLoadout(option))
        {
            RefuseLaunch("Pick a preset, or a primary weapon this aircraft can carry.");
            return;
        }

        if (airbase == null)
        {
            RefuseLaunch("No compatible friendly airbase is available.");
            return;
        }

        // Refuse before the map opens, not after the area is placed. SpawnMission checks the same
        // thing, but by then the player has picked a target and watched nothing happen, which reads
        // as a broken button rather than an empty treasury.
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq != null && CommanderDraftService.IsDraftMission && hq.GetUnitSupply(option.Definition) <= 0)
        {
            RefuseLaunch($"DRAFT: no {GetAircraftLabel(option.Definition)} left in your air wing. "
                + "Only what you drafted flies; a pilot brought home by RECOVERY returns his airframe to it.");
            return;
        }

        if (hq != null
            && hq.GetUnitSupply(option.Definition) <= 0
            && hq.factionFunds < CommanderEconomyService.PriceOf(option.Definition))
        {
            string price = UnitConverter.ValueReading(CommanderEconomyService.PriceOf(option.Definition))
                ?? CommanderEconomyService.PriceOf(option.Definition).ToString("F1", CultureInfo.CurrentCulture);
            RefuseLaunch($"Cannot afford {GetAircraftLabel(option.Definition)} ({price}). "
                + $"Faction funds {CommanderEconomyService.FundsLabel(hq.factionFunds)}.");
            return;
        }

        // AUTOPILOT and STANDBY have no circle to place, so the button is the whole order: buy it
        // and put it on the map. Making the player click a target area they had already been told
        // would be ignored was half the ceremony this rework removes.
        if (!RequiresMissionArea(option.Mode))
        {
            Transform? field = airbase.Airbase.center != null ? airbase.Airbase.center : airbase.Airbase.transform;
            SpawnMission(option, airbase.Airbase, field.GlobalPosition());
            return;
        }

        pendingMissionRelocation = null;
        pendingAreaSelection = new PendingAreaSelection(option, airbase.Airbase);
        OpenOrderMap();
        UpdatePendingAreaPreview();
        SetStatus("Select the mission area on the tactical map or in the 3D world. The game's Cancel binding cancels.");
    }

    internal bool TrySetAreaFromWorld(Vector2 screenPosition)
    {
        if (!AwaitingAreaSelection)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid mission area was found under the cursor.");
            return true;
        }

        CompleteAreaSelection(target);
        return true;
    }

    /// <summary>
    /// The base game's own trip home is no landing order of ours, so nothing else cancels it, and an
    /// aircraft on it would fly home ignoring a new order.
    /// Not on the ground, where the rollout and taxi own the aeroplane.
    /// </summary>
    private static void LeaveBaseGameLanding(Aircraft aircraft)
    {
        if (aircraft.radarAlt > 5f
            && aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && (aircraft.pilots[0]?.currentState is AIHeloLandingState or AIPilotLandingState or AIPilotShortLandingState
                || IsModdedTransportState(aircraft.pilots[0]?.currentState)))
        {
            ReturnToCombatState(aircraft);
        }
    }

    /// <summary>
    /// A cargo AI an aircraft mod brought with it, such as the Chimera's <c>AIFixedWingTransportState</c>.
    /// Matched by name because that assembly is not always loaded. It picks its own drop point, so
    /// a commanded aircraft must not be in one.
    /// </summary>
    private static bool IsModdedTransportState(PilotBaseState? state)
    {
        return state != null
            && state is not AIHeloTransportState
            && state.GetType().Name.EndsWith("TransportState", StringComparison.Ordinal);
    }

    private static AirCommandMode GetModeForTarget(Unit target)
    {
        return target is Aircraft or Missile ? AirCommandMode.AirGuard : AirCommandMode.Cas;
    }

    /// <summary>Rotary or fixed wing, and what it is carrying it for.</summary>
    private static string GetAirframeRole(AircraftDefinition definition)
    {
        return IsRotaryAirframe(definition) ? "ROTARY" : "FIXED WING";
    }

    /// <summary>The two area-less modes, answered here rather than by asking for a circle nobody uses.
    /// True when <paramref name="mode"/> was one of them.</summary>
    private bool TryAreaLessOrder(Aircraft aircraft, AirCommandMode mode)
    {
        if (mode == AirCommandMode.Autopilot)
        {
            if (ForwardAir("air.autopilot", aircraft))
            {
                return true;
            }

            RemoveMission(aircraft);
            CommanderSelectionService.Instance?.RemovePinnedUnit(aircraft);
            ReturnToCombatState(aircraft);
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} released to autopilot.");
            return true;
        }

        if (mode == AirCommandMode.Standby)
        {
            if (ForwardAir("air.standby", aircraft))
            {
                return true;
            }

            Airbase? field = FindNearestOwnedAirbase(aircraft);
            if (field == null || !SetAircraftLandingOrder(aircraft, field, LandingIntent.Hold, append: false))
            {
                SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no friendly field to stand by on.");
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The mission a plain travel or attack point should carry. AUTOPILOT and STANDBY are launch
    /// settings, not target filters, so an order given while one of them is selected falls back to
    /// HYBRID — which is "shoot what you can reach", i.e. what a player clicking a point means.
    /// </summary>
    private AirCommandMode GetOrderMode()
    {
        return RequiresMissionArea(SelectedMode) ? SelectedMode : AirCommandMode.Hybrid;
    }

    private bool TryAdoptAircraft(Aircraft aircraft, AirCommandMode mode, GlobalPosition center, float radius)
    {
        if (!IsTaskableAircraft(aircraft))
        {
            return false;
        }

        FactionHQ? hq = aircraft.NetworkHQ ?? CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return false;
        }

        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            // Adopted airframes were never bought by the Commander, so nothing is refunded on return.
            // They keep any height set on them, else start at AUTO, not the AIR window's launch slider.
            mission = new AirMission(hq, mode, center, radius, GetMissionAltitude(aircraft), OrderTargetOrdnance, OrderSaturationAttack, false, 0f);
            missions[aircraft] = mission;
            autopilotAltitudes.Remove(aircraft);
        }
        else
        {
            mission.Mode = mode;
            mission.AreaCenter = center;
            mission.Radius = radius;

            // A re-task (CHANGE MISSION) outranks a standby or resupply landing, the same as any plain order.
            ClearLandingOrder(aircraft, mission, release: true);
            LeaveBaseGameLanding(aircraft);
        }

        mission.Returning = false;
        mission.RtbIssued = false;
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();
        CommanderSelectionService.PinMissionUnit(aircraft, "AIR COMMAND", GetModeLabel(mode));
        return true;
    }

    /// <summary>
    /// Arms the map an order is placed on. That is the mod's own tactical map, never the fullscreen
    /// game map: an order that swapped the whole screen out from under the player and back again is
    /// the reason this pair exists. The flag records whether this order is what opened the map, so
    /// only an order that opened it puts it away.
    /// </summary>
    private void OpenOrderMap()
    {
        openedMapForOrder = tacticalMapService.OpenForPlacement();
        tacticalMapService.SuppressMapFollow = true;
        mapClickTracker.Reset();
    }

    private void CloseOrderMap()
    {
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (openedMapForOrder)
        {
            tacticalMapService.Close();
        }

        openedMapForOrder = false;
    }
}
