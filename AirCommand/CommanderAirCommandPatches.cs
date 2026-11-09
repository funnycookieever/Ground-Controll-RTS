using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderAirCommandPatches
{
    internal const float HeloTransitStaggerMeters = 15f;

    // Typed field access for the per-physics-step hooks: FieldInfo.GetValue is reflection on every
    // call, and a GlobalPosition read or written through it is boxed.
    internal static readonly AccessTools.FieldRef<PilotBaseState, GlobalPosition> DestinationRef =
        AccessTools.FieldRefAccess<PilotBaseState, GlobalPosition>("destination");

    /// <summary>sin(20 deg): a wing this far down on the ground is a tip-over, not a sloping apron.</summary>
    private const float TaxiTipOverDot = 0.34f;

    private const float HeloTransitHeightMeters = 50f;

    private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> StateAircraftRef =
        AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");

    private static readonly AccessTools.FieldRef<AIPilotCombatModes, float> TimeWithoutTargetRef =
        AccessTools.FieldRefAccess<AIPilotCombatModes, float>("timeWithoutTarget");

    private static readonly AccessTools.FieldRef<AIHeloCombatState, float> HeloTimeWithoutTargetRef =
        AccessTools.FieldRefAccess<AIHeloCombatState, float>("timeWithoutTarget");

    private static readonly FieldInfo? StateAircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");
    private static readonly FieldInfo? DestinationField = AccessTools.Field(typeof(PilotBaseState), "destination");
    private static readonly FieldInfo? TargetHeightField = AccessTools.Field(typeof(AIPilotCombatModes), "targetHeight");
    private static readonly FieldInfo? LandingModeField = AccessTools.Field(typeof(AIPilotLandingState), "landingMode");
    private static readonly FieldInfo? LandingAirbaseField = AccessTools.Field(typeof(AIPilotLandingState), "airbase");
    private static readonly FieldInfo? LandingRunwayUsageField = AccessTools.Field(typeof(AIPilotLandingState), "runwayUsage");
    private static readonly FieldInfo? LandingSpeedField = AccessTools.Field(typeof(AIPilotLandingState), "adjustedLandingSpeed");
    private static readonly FieldInfo? NearestAirbaseField = AccessTools.Field(typeof(PilotBaseState), "nearestAirbase");
    private static readonly FieldInfo? HeloLandingPointField = AccessTools.Field(typeof(AIHeloLandingState), "landingPoint");
    private static readonly FieldInfo? HeloReachedApproachField = AccessTools.Field(typeof(AIHeloLandingState), "reachedApproachPoint");

    private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> TaxiYieldingRef =
        AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("yielding");

    private static readonly AccessTools.FieldRef<AIHeloCombatState, float> HeloDesiredHeightRef =
        AccessTools.FieldRefAccess<AIHeloCombatState, float>("desiredHeight");

    internal static Aircraft? GetStateAircraft(PilotBaseState state)
    {
        return StateAircraftRef(state);
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.ChooseHQTarget))]
    [HarmonyPrefix]
    private static bool ChooseHqTargetPrefix(
        Unit searcher,
        List<WeaponStation> stationList,
        ref CombatAI.TargetSearchResults __result)
    {
        if (!CommanderAirCommandService.TryChooseMissionTarget(searcher, stationList, out CombatAI.TargetSearchResults result))
        {
            return true;
        }

        __result = result;
        return false;
    }

    /// <summary>
    /// <c>FixedUpdateState</c> clamps <c>targetHeight</c> to 8 km before every autopilot call. Bombers
    /// cruise at 10 km and up, so the constant is swapped for the mod's own ceiling. It only lifts a
    /// cap; the base game never asks for more than 8 km by itself.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), nameof(AIPilotCombatModes.FixedUpdateState))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> RaiseHeightCeiling(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && instruction.operand is float value && value == 8000f)
            {
                instruction.operand = CommanderAirCommandService.HighestAiHeightMeters;
            }

            yield return instruction;
        }
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.LookForMissileTargets))]
    [HarmonyPrefix]
    private static bool LookForMissileTargetsPrefix(
        Aircraft aircraft,
        WeaponStation weaponStation,
        ref int __result)
    {
        if (!CommanderAirCommandService.TryBuildAradSaturationTargets(aircraft, weaponStation, out int targetCount))
        {
            return true;
        }

        __result = targetCount;
        return false;
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.LookForJammingTargets))]
    [HarmonyPostfix]
    private static void LookForJammingTargetsPostfix(
        Aircraft aircraft,
        WeaponStation weaponStation,
        List<Unit> outTargets,
        ref int __result)
    {
        CommanderAirCommandService.RotateJammingTargets(aircraft, weaponStation, outTargets, ref __result);
    }

    /// <summary>
    /// The Basegame idle timer is what was flying commanded aircraft home: <c>NoTarget</c> counts
    /// every tick without a target and switches to the landing state after 15 of them, before the
    /// postfix below ever gets to write the destination — which is why an aircraft ordered to the
    /// enemy base landed at its own with most of its fuel left. Zeroing the counter on the way in
    /// means a commanded aircraft never reaches that threshold.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "NoTarget")]
    [HarmonyPrefix]
    private static void NoTargetPrefix(AIPilotCombatModes __instance)
    {
        // HOLD FIRE never has a target, so a mission-less (autopilot) aircraft on it ran the idle
        // timer out and landed with full racks. The fuel check above it in NoTarget still sends it
        // home when it is actually short.
        if (CommanderAirCommandService.TryGetMissionHoldPoint(__instance, out _)
            || CommanderMoveService.IsHoldingFire(GetStateAircraft(__instance)))
        {
            TimeWithoutTargetRef(__instance) = 0f;
        }
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "NoTarget")]
    [HarmonyPostfix]
    private static void NoTargetPostfix(AIPilotCombatModes __instance)
    {
        if (!CommanderAirCommandService.TryGetMissionHoldPoint(__instance, out GlobalPosition point))
        {
            return;
        }

        DestinationRef(__instance) = point;
        TimeWithoutTargetRef(__instance) = 0f;
        CommanderAirCommandService.ApplyEscortThrottle(__instance);
    }

    /// <summary>
    /// The rotary twin of the two patches above, and the whole of the convoy escort's steering.
    /// <c>AIHeloCombatState.NoTarget</c> flies at the nearest friendly airbase and switches to the
    /// landing state after fifteen seconds within 3 km of it — which is a gunship that lifts off,
    /// notices nothing in range, and puts itself back down at home. With an escort mission it flies
    /// at the convoy instead and never runs the timer out. Everything else about the helicopter's
    /// flying is left to the base game: once it has a target, <c>AIHeloCombatState</c> attacks with
    /// its own gunship, bombing and missile modes, and the mission is only what keeps that target
    /// near the convoy.
    /// </summary>
    [HarmonyPatch(typeof(AIHeloCombatState), "NoTarget")]
    [HarmonyPostfix]
    private static void HeloNoTargetPostfix(AIHeloCombatState __instance)
    {
        if (CommanderAirCommandService.TryGetMissionHoldPoint(__instance, out GlobalPosition point))
        {
            DestinationRef(__instance) = point;
            HeloTimeWithoutTargetRef(__instance) = 0f;
            HeloTransitFloor(__instance);
            CommanderAirCommandService.ApplyEscortThrottle(__instance);
        }
        else if (CommanderAirCommandService.TryGetAutopilotHuntPoint(__instance, out point))
        {
            DestinationRef(__instance) = point;
            HeloTimeWithoutTargetRef(__instance) = 0f;
            HeloTransitFloor(__instance);
        }
        else if (CommanderMoveService.IsHoldingFire(GetStateAircraft(__instance)))
        {
            // HOLD FIRE: see NoTargetPrefix. Without this the helicopter lands itself at home.
            HeloTimeWithoutTargetRef(__instance) = 0f;
        }
    }

    /// <summary>
    /// <c>NoTarget</c> bleeds <c>desiredHeight</c> to 0, so a helicopter in transit cruises at
    /// <c>minimumRadarAlt</c> and terrain following only looks straight down, so it hits buildings. An
    /// ordered transit keeps a building's height in hand; attack modes still go as low as they like.
    /// </summary>
    /// <remarks>Each airframe gets one of four heights 15 m apart, so helicopters flying the same line
    /// do not collide.</remarks>
    private static void HeloTransitFloor(AIHeloCombatState state)
    {
        Aircraft? aircraft = GetStateAircraft(state);
        float stagger = aircraft == null ? 0f : (aircraft.persistentID.Id % 4) * HeloTransitStaggerMeters;
        ref float height = ref HeloDesiredHeightRef(state);
        height = UnityEngine.Mathf.Max(height, HeloTransitHeightMeters + stagger);
    }

    /// <summary>
    /// The rotary half of "land where you were told". Without it a helicopter handed a landing
    /// order flew to the field, was switched into the landing state, and that state picked its own
    /// faction's nearest airbase — so it turned round and came home, which is why a Chicane looked
    /// like it was ignoring capture orders.
    /// </summary>
    [HarmonyPatch(typeof(AIHeloLandingState), nameof(AIHeloLandingState.EnterState))]
    [HarmonyPostfix]
    private static void HeloLandingEnterPostfix(AIHeloLandingState __instance, Pilot pilot)
    {
        CommanderAirCommandService.OverrideHeloLandingAirbase(
            __instance,
            pilot,
            NearestAirbaseField,
            HeloLandingPointField,
            HeloReachedApproachField,
            DestinationField);
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.LookForMissileTargets))]
    [HarmonyPostfix]
    private static void LookForMissileTargetsPostfix(Aircraft aircraft, WeaponStation weaponStation, List<Unit> outTargets)
    {
        WeaponInfo? info = weaponStation?.WeaponInfo;
        if (info != null && (info.glideBomb || info.strategic) && !info.nuclear && outTargets.Count > 0)
        {
            CommanderAirCommandService.SwarmRelease(aircraft, weaponStation!, outTargets);
        }
    }

    /// <summary>
    /// The height again, after the rest of the one-second tick. <c>ChooseCombatMode</c> runs after
    /// <c>ManageAltitude</c> and its <c>FlyToTarget</c> sets <c>targetHeight</c> to the target's own
    /// radar altitude whenever that is over 10 m, so a bomber sent at a raised target such as the Gas
    /// Export Terminal would fly in at its height and never climb high enough to go in.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "AIPilotCombatState_On1sInterval")]
    [HarmonyPostfix]
    private static void OneSecondIntervalPostfix(AIPilotCombatModes __instance)
    {
        CommanderAirCommandService.ApplyMissionTargetAltitude(__instance, TargetHeightField);
    }

    /// <summary>
    /// An aircraft ordered onto a missile chases it instead of dodging. <c>RunEvadeMode</c> runs
    /// after <c>RunAttackMode</c> every physics step and its result is what <c>AutoAim</c> flies:
    /// radar evasion swaps the destination for a notch at 10 m, IR evasion cuts the throttle, so an
    /// interceptor with any missile warning turned away from the very missile it was sent at.
    /// Skipping it hands <c>AutoAim</c> the attack mode's own destination. The 1 s attack tick puts
    /// the height and throttle back; flares stay as they were.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "RunEvadeMode")]
    [HarmonyPrefix]
    private static bool RunEvadeModePrefix(AIPilotCombatModes __instance, ref GlobalPosition evadeDestination)
    {
        if (!CommanderAirCommandService.IsChasingMissile(StateAircraftRef(__instance)))
        {
            return true;
        }

        evadeDestination = DestinationRef(__instance);
        return false;
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "RunEvadeMode")]
    [HarmonyPostfix]
    private static void RunEvadeModePostfix(AIPilotCombatModes __instance)
    {
        CommanderAirCommandService.HoldBomberEvasionHeight(__instance);
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "ManageAltitude")]
    [HarmonyPostfix]
    private static void ManageAltitudePostfix(AIPilotCombatModes __instance)
    {
        CommanderAirCommandService.ApplyMissionTargetAltitude(__instance, TargetHeightField);
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "RunAttackMode")]
    [HarmonyPostfix]
    private static void RunAttackModePostfix(AIPilotCombatModes __instance)
    {
        CommanderAirCommandService.ConstrainMissionDestination(__instance, DestinationField);
    }

    /// <summary>
    /// The rotary twin of the postfix above. <c>AIHeloCombatState.FixedUpdateState</c> runs
    /// <c>RunCombatModes</c> and then flies at <c>destination</c>, and every one of those modes
    /// (gunship, bombing, missiles, break-off) writes that field itself — so a helicopter or
    /// tiltwing with any target at all ignored its travel points and its mission box completely.
    /// Postfixing the mode dispatcher lands between the two, which is the same slot
    /// <c>RunAttackMode</c> gives the fixed-wing brain. A prefix on <c>FixedUpdateState</c> would
    /// be overwritten by <c>RunCombatModes</c> a line later.
    /// </summary>
    [HarmonyPatch(typeof(AIHeloCombatState), "RunCombatModes")]
    [HarmonyPostfix]
    private static void HeloRunCombatModesPostfix(AIHeloCombatState __instance)
    {
        CommanderAirCommandService.ConstrainMissionDestination(__instance, DestinationField);
    }

    /// <summary>
    /// A commanded aircraft is not a delivery. <c>AIHeloCombatState.AssessHQTargets</c> hands any
    /// rotary airframe with a loaded cargo pylon to <c>AIHeloTransportState</c>, which then flies a
    /// whole delivery of its own choosing to the nearest thing it can see — so a transport given an
    /// ordinary Air Command mission left on its own errand and read as ignoring every order. A run
    /// the player actually asked for (SUPPLY RUN) is exempt: that service owns the transport state.
    /// </summary>
    [HarmonyPatch(typeof(Pilot), nameof(Pilot.SwitchState))]
    [HarmonyPrefix]
    private static bool SwitchStatePrefix(Pilot __instance, PilotBaseState state)
    {
        if (CommanderAirCommandService.ShouldRefuseTransportState(__instance, state))
        {
            return false;
        }

        // Every way this mod takes a pilot off a runway goes through here, so this is the one place
        // the runway's landing list and takeoff queue can be handed back. See the method's remarks:
        // a stale entry shuts the whole airfield down and nothing in the base game clears it.
        CommanderAirCommandService.ReleaseRunwayReservations(__instance, state);
        return true;
    }

    /// <summary>
    /// HOLD FIRE, the trigger. Every AI pilot state shoots through <c>Pilot.Fire</c>, but that
    /// method is a few instructions long and the JIT may inline it into the caller, where a patch
    /// never runs: a Vagrant on HOLD FIRE shot down a Brawler with not one HOLD FIRE line in the log.
    /// <c>WeaponManager.Fire</c> is what it calls, and too big to inline. Refusing the target search
    /// alone is not enough either: <c>AIPilotCombatModes</c> re-runs it every 5 s and never
    /// mid-attack (<c>allowTargetAssessment</c>). The selection bar's FIRE sets
    /// <c>CommanderFireNow.ManualFire</c> around its own call and is let through.
    /// </summary>
    [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.Fire))]
    [HarmonyPrefix]
    private static bool WeaponManagerFirePrefix(Aircraft ___aircraft)
    {
        return MayFire(___aircraft);
    }

    /// <summary>
    /// HOLD FIRE, the aircraft's turrets. Giving the pilot a target hands it to every turret on the
    /// station (<c>SetStationTargets</c>), and a turret fires by itself the moment it is on target,
    /// straight through the station and past <c>WeaponManager.Fire</c>. Patching only here would miss
    /// <c>WeaponManager.Fire</c>'s missiles, which go through <c>LaunchMount</c>.
    /// </summary>
    [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.Fire), new[] { typeof(Unit), typeof(Unit) })]
    [HarmonyPrefix]
    private static bool WeaponStationFirePrefix(Unit owner)
    {
        return owner is not Aircraft aircraft || MayFire(aircraft);
    }

    private static bool MayFire(Aircraft aircraft)
    {
        return CommanderMoveService.Instance?.HasHoldFireUnits != true
            || CommanderFireNow.ManualFire
            || !CommanderAirCommandService.IsHoldingAiFire(aircraft);
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RegisterFactionUnit))]
    [HarmonyPostfix]
    private static void RegisterFactionUnitPostfix(FactionHQ __instance, Unit unit)
    {
        CommanderAirCommandService.NotifyFactionUnitRegistered(__instance, unit);
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.ReturnToInventory))]
    [HarmonyPostfix]
    private static void ReturnToInventoryPostfix(Aircraft __instance)
    {
        CommanderAirCommandService.NotifyAircraftReturned(__instance);
    }

    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.FixedUpdateState))]
    [HarmonyPrefix]
    private static bool TaxiFixedUpdateStatePrefix(AIPilotTaxiState __instance, Pilot pilot)
    {
        CommanderAirCommandService.SetTaxiingAircraft(pilot);
        return !CommanderAirCommandService.HoldForDeckTurn(pilot, __instance);
    }

    /// <summary>
    /// <c>AIPilotTaxiState.EjectCheck</c> (every 5 s) abandons the aeroplane when the cockpit rolls more
    /// than ~3 degrees, which happens on an ordinary sloping apron. Only a real tip-over counts now.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotTaxiState), "EjectCheck")]
    [HarmonyPrefix]
    private static bool TaxiEjectCheckPrefix(AIPilotTaxiState __instance)
    {
        Aircraft? aircraft = GetStateAircraft(__instance);
        return aircraft == null
            || aircraft.cockpit == null
            || UnityEngine.Mathf.Abs(UnityEngine.Vector3.Dot(aircraft.cockpit.xform.right, UnityEngine.Vector3.up)) > TaxiTipOverDot;
    }

    /// <summary>See <c>CommanderAirCommandService.RunRollout</c>: the nose-up rollout overran short runways.</summary>
    [HarmonyPatch(typeof(AIPilotLandingState), "TouchedDown")]
    [HarmonyPrefix]
    private static bool LandingTouchedDownPrefix(AIPilotLandingState __instance, bool checkMode)
    {
        return !CommanderAirCommandService.RunRollout(__instance, checkMode);
    }

    /// <summary>See <c>CommanderAirCommandService.BeginApproachFrame</c>: the approach never flared.</summary>
    [HarmonyPatch(typeof(AIPilotLandingState), "StabilizedApproach")]
    [HarmonyPrefix]
    private static void LandingStabilizedApproachPrefix(AIPilotLandingState __instance, bool checkMode)
    {
        CommanderAirCommandService.BeginApproachFrame(__instance, checkMode);
        CommanderAirCommandService.HoldInTrailSpeed(__instance, checkMode);
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "StabilizedApproach")]
    [HarmonyPostfix]
    private static void LandingStabilizedApproachPostfix()
    {
        CommanderAirCommandService.EndApproachFrame();
    }

    /// <summary>See <c>CommanderAirCommandService.FilterLandingList</c>: landers in trail went round.</summary>
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.GetLandingList))]
    [HarmonyPostfix]
    private static void RunwayGetLandingListPostfix(ref List<Aircraft> __result)
    {
        CommanderAirCommandService.FilterLandingList(ref __result);
    }

    /// <summary>See <c>CommanderAirCommandService.MayTurnFinal</c>: the runway is shared in turns.</summary>
    [HarmonyPatch(typeof(AIPilotLandingState), "JoinPattern")]
    [HarmonyPrefix]
    private static bool LandingJoinPatternPrefix(AIPilotLandingState __instance, bool checkMode)
    {
        return CommanderAirCommandService.MayTurnFinal(__instance, checkMode);
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "JoinPattern")]
    [HarmonyPostfix]
    private static void LandingJoinPatternPostfix()
    {
        CommanderAirCommandService.EndApproachFrame();
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "FinalTurn")]
    [HarmonyPrefix]
    private static void LandingFinalTurnPrefix(AIPilotLandingState __instance, bool checkMode)
    {
        CommanderAirCommandService.BeginFinalTurnFrame(__instance, checkMode);
        CommanderAirCommandService.HoldInTrailSpeed(__instance, checkMode);
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "FinalTurn")]
    [HarmonyPostfix]
    private static void LandingFinalTurnPostfix()
    {
        CommanderAirCommandService.EndApproachFrame();
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "AbortingLanding")]
    [HarmonyPrefix]
    private static bool LandingAbortingPrefix(AIPilotLandingState __instance, bool checkMode)
    {
        return !CommanderAirCommandService.BeginGoAroundFrame(__instance, checkMode);
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "AbortingLanding")]
    [HarmonyPostfix]
    private static void LandingAbortingPostfix()
    {
        CommanderAirCommandService.EndApproachFrame();
    }

    [HarmonyPatch(
        typeof(AutopilotPlane),
        nameof(AutopilotPlane.AutoAim),
        new[] { typeof(GlobalPosition), typeof(bool), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(Vector3) })]
    [HarmonyPrefix]
    private static void AutopilotPlaneAutoAimPrefix(
        AutopilotPlane __instance,
        ref GlobalPosition destination,
        ref float altitudeHold,
        ref bool aimVelocity,
        ref bool ignoreCollisions,
        ref float effort,
        ref float bankAllowed,
        ref bool followTerrain)
    {
        CommanderAirCommandService.ApplyFlare(__instance, ref destination, ref altitudeHold, aimVelocity, ref bankAllowed);
        CommanderAirTerrainGuard.Apply(__instance, ref destination, ref aimVelocity, ref ignoreCollisions, ref effort, ref bankAllowed, ref followTerrain);
    }

    [HarmonyPatch(
        typeof(AutopilotHelo),
        nameof(AutopilotHelo.AutoAim),
        new[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPrefix]
    private static bool AutopilotHeloAutoAimPrefix(AutopilotHelo __instance, ref GlobalPosition destination, ref float altitudeHold)
    {
        CommanderHeloRoadRoute.Apply(__instance, ref destination);
        CommanderAirCommandService.HoldHeloAltitude(__instance, ref altitudeHold);
        return CommanderAirTerrainGuard.ApplyRotor(__instance);
    }

    [HarmonyPatch(typeof(Autopilot), nameof(Autopilot.Hover))]
    [HarmonyPrefix]
    private static void AutopilotHoverPrefix(Autopilot __instance, ref GlobalPosition destination, ref float altitudeHold)
    {
        CommanderAirTerrainGuard.ApplyTransportHover(__instance, ref destination, ref altitudeHold);
    }

    /// <summary>
    /// <c>AIPilotTaxiState.CheckObstacles</c> makes a landed aeroplane meeting another head on "yield":
    /// turn up to 90 degrees, drive 50 m and shut down, which on a narrow taxiway is the dirt.
    /// Cancelled, so it waits on its brakes instead (<c>LimitTaxiSpeed</c>'s following distance).
    /// </summary>
    [HarmonyPatch(typeof(AIPilotTaxiState), "CheckObstacles")]
    [HarmonyPostfix]
    private static void TaxiCheckObstaclesPostfix(AIPilotTaxiState __instance)
    {
        TaxiYieldingRef(__instance) = false;
    }

    /// <summary>See <c>CommanderAirCommandService.RouteLandedTaxi</c>.</summary>
    [HarmonyPatch(typeof(AIPilotTaxiState), "SearchForAirbase")]
    [HarmonyPostfix]
    private static void TaxiSearchForAirbasePostfix(AIPilotTaxiState __instance)
    {
        CommanderAirCommandService.RouteLandedTaxi(__instance);
    }

    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.FixedUpdateState))]
    [HarmonyPostfix]
    private static void TaxiFixedUpdateStatePostfix(Pilot pilot)
    {
        CommanderAirCommandService.SetTaxiingAircraft(null);
        CommanderAirCommandService.LimitTaxiSpeed(pilot);
        if (pilot != null && pilot.currentState is AIPilotTaxiState taxi)
        {
            CommanderAirCommandService.SteerPastRouteEnd(taxi);
            CommanderAirCommandService.PivotLandedTaxi(taxi);
        }
    }

    /// <summary>
    /// Arriving aircraft are spread across the aprons instead of all being sent to the same stand.
    /// See <c>CommanderAirCommandService.TryClaimServiceStand</c>.
    /// </summary>
    [HarmonyPatch(typeof(Airbase), nameof(Airbase.TryGetNearestServicePoint))]
    [HarmonyPrefix]
    private static bool TryGetNearestServicePointPrefix(
        Airbase __instance,
        UnityEngine.Vector3 fromPosition,
        ref UnityEngine.Transform nearestServicePoint,
        ref bool __result)
    {
        if (!CommanderAirCommandService.TryClaimServiceStand(__instance, fromPosition, out UnityEngine.Transform? stand))
        {
            return true;
        }

        nearestServicePoint = stand!;
        __result = true;
        return false;
    }

    [HarmonyPatch(typeof(AIPilotTakeoffState), nameof(AIPilotTakeoffState.FixedUpdateState))]
    [HarmonyPrefix]
    private static bool TakeoffFixedUpdateStatePrefix(AIPilotTakeoffState __instance, Pilot pilot)
    {
        CommanderAirCommandService.WatchForOverrun(__instance, pilot);
        return !CommanderAirCommandService.HoldForLineUp(__instance, pilot);
    }

    /// <summary>
    /// The one choke point every "the AI pilot gave up on the ground" path runs through. Taxi and
    /// takeoff both answer a stuck timer, a scrape or a stall by ejecting, and an ejection is fatal
    /// either way — <c>DisableUnit</c> away from a friendly base, <c>ReturnToInventory</c> beside
    /// one. Guarding here covers all of them at once, which is what lets commander launches come out
    /// of a hangar and take off for real instead of being spawned airborne over the field.
    /// </summary>
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.StartEjectionSequence))]
    [HarmonyPrefix]
    private static bool StartEjectionSequencePrefix(Aircraft __instance)
    {
        return !CommanderAirCommandService.TryBlockGroundEjection(__instance);
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.DisableUnit))]
    [HarmonyPostfix]
    private static void DisableUnitPostfix(Unit __instance)
    {
        CommanderAirCommandService.NotifyUnitDisabled(__instance);
    }

    /// <summary>
    /// The landing state only ever looks for an airbase its own faction holds, so an aircraft
    /// ordered onto a neutral field could never actually land on it. This substitutes the commanded
    /// field; with no commanded field it does nothing and the Basegame search runs unchanged.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotLandingState), "LandingState_SearchAirbase")]
    [HarmonyPrefix]
    private static bool LandingSearchAirbasePrefix(AIPilotLandingState __instance)
    {
        return !CommanderAirCommandService.TryOverrideLandingAirbase(
            __instance,
            StateAircraftField,
            LandingModeField,
            LandingAirbaseField,
            LandingRunwayUsageField,
            LandingSpeedField);
    }

    /// <summary>
    /// The base game's own 5 s runway sweep only drops destroyed landing entries; this drops wrecks
    /// and aircraft no longer landing or taking off from both registers, or the field stops launching.
    /// </summary>
    /// <summary>See <c>CommanderAirCommandService.MayDepartBeforeLanders</c>.</summary>
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.IsAvailableForTakeoff))]
    [HarmonyPostfix]
    private static void IsAvailableForTakeoffPostfix(Airbase.Runway __instance, Aircraft querier, ref bool __result)
    {
        if (!__result && querier != null)
        {
            __result = CommanderAirCommandService.MayDepartBeforeLanders(__instance, querier);
        }
    }

    /// <summary>
    /// The base game's pad check only reads its landing queue and crossing runways, so a helicopter
    /// set down on top of whatever was parked there. Anything standing on the pad counts too: the
    /// pad search then prefers a free one, and <c>AIHeloLandingState</c> holds off while it waits.
    /// </summary>
    [HarmonyPatch(typeof(Airbase.VerticalLandingPoint), nameof(Airbase.VerticalLandingPoint.IsOccupied))]
    [HarmonyPostfix]
    private static void VerticalLandingPointIsOccupiedPostfix(Airbase.VerticalLandingPoint __instance, Aircraft querier, ref bool __result)
    {
        if (!__result && querier != null && __instance.point != null)
        {
            __result = CommanderAirCommandService.IsPadPhysicallyOccupied(__instance.point.position, querier);
        }
    }

    /// <summary>The taxi's hold short of a runway in use, with the same exception as the takeoff clearance.</summary>
    [HarmonyPatch(typeof(Airbase), nameof(Airbase.AircraftApproachingRunwayInUse))]
    [HarmonyPostfix]
    private static void AircraftApproachingRunwayInUsePostfix(Airbase __instance, Aircraft aircraft, ref bool __result)
    {
        if (!__result || aircraft == null || __instance.runways == null)
        {
            return;
        }

        float reach = 40f + aircraft.maxRadius;
        foreach (Airbase.Runway runway in __instance.runways)
        {
            if (runway != null
                && runway.OtherAircraftUsingRunway(aircraft)
                && runway.AircraftApproachingRunway(aircraft, reach)
                && !CommanderAirCommandService.MayDepartBeforeLanders(runway, aircraft))
            {
                return;
            }
        }

        __result = false;
    }

    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.MonitorLandings))]
    [HarmonyPostfix]
    private static void MonitorLandingsPostfix(Airbase.Runway __instance, Airbase airbase)
    {
        CommanderAirCommandService.PurgeStaleRunwayEntries(__instance, airbase);
    }

    /// <summary>
    /// The base game locks a runway's direction for 30 s after any takeoff pick and renews the lock
    /// on every pick, so a stream of launches all taxi to whichever end the first one chose, however
    /// far round the field that is. The lock only matters while someone else is landing or rolling,
    /// so outside that the aircraft takes the nearer end. <c>GetDistance</c>'s only caller is
    /// <c>Airbase.GetTakeoffRunway</c>, which the taxi and takeoff states both ask.
    /// </summary>
    /// <summary>See <c>CommanderAirCommandService.TryGetLandingExit</c>: landers never get the departures' way in.</summary>
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.TryGetExitTaxiPoint))]
    [HarmonyPrefix]
    private static bool RunwayTryGetExitTaxiPointPrefix(Airbase.Runway __instance, Transform fromTransform, float speed, ref Transform? exitPoint, ref bool __result)
    {
        __result = CommanderAirCommandService.TryGetLandingExit(__instance, fromTransform, speed, out exitPoint);
        return false;
    }

    /// <summary>See <c>CommanderAirCommandService.ChooseLandingEnd</c>.</summary>
    [HarmonyPatch(typeof(Airbase), nameof(Airbase.RequestLanding))]
    [HarmonyPostfix]
    private static void AirbaseRequestLandingPostfix(Airbase __instance, ref Airbase.Runway.RunwayUsage? __result)
    {
        CommanderAirCommandService.ChooseLandingEnd(__instance, ref __result);
    }

    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.GetDistance))]
    [HarmonyPrefix]
    private static bool RunwayGetDistancePrefix(
        Airbase.Runway __instance,
        Transform fromTransform,
        ref Airbase.Runway.RunwayDistanceResult __result)
    {
        Aircraft? aircraft = fromTransform == null ? null : fromTransform.GetComponent<Aircraft>();
        if (aircraft != null && CommanderForwardBase.IsAnotherHangarsStrip(__instance, aircraft))
        {
            __result = new Airbase.Runway.RunwayDistanceResult(float.MaxValue, reverse: false);
            return false;
        }

        if (!__instance.Reversable || Time.timeSinceLevelLoad - __instance.LastUsed >= 30f)
        {
            return true;
        }

        if (aircraft == null || __instance.OtherAircraftUsingRunway(aircraft))
        {
            return true;
        }

        float toStart = Vector3.Distance(__instance.Start.position, fromTransform!.position);
        float toEnd = Vector3.Distance(__instance.End.position, fromTransform.position);
        __result = toEnd < toStart
            ? new Airbase.Runway.RunwayDistanceResult(toEnd, reverse: true)
            : new Airbase.Runway.RunwayDistanceResult(toStart, reverse: false);
        return false;
    }

    /// <summary>
    /// The base game drops a gear order given while the gear is still moving
    /// (<c>Aircraft.GearStateChanged</c> returns early on Extending / Retracting) but keeps the
    /// <c>gearDeployed</c> flag it set, and every AI state compares that flag, never the gear, before
    /// asking again. So a helicopter that takes off from its LZ (combat state raises the gear) and is
    /// handed back to <c>AIHeloTransportState</c> mid-retract (which lowers it) ends up gear locked
    /// up with the flag reading down, and lands on its belly every time after: the Ibis cargo runs
    /// that broke themselves. When the gear locks, follow the flag. Local on every machine, the same
    /// as the base game's own SyncVar hook, so it needs no RPC.
    /// The aircraft selection preview and the encyclopedia model are not networked: they lock the
    /// gear down without ever setting the flag, so following it there retracted the preview's gear
    /// and dropped it through the hangar floor.
    /// </summary>
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SetGear), new[] { typeof(LandingGear.GearState) })]
    [HarmonyPostfix]
    private static void SetGearStatePostfix(Aircraft __instance, LandingGear.GearState gearState)
    {
        if (!__instance.networked)
        {
            return;
        }

        if (gearState == LandingGear.GearState.LockedRetracted && __instance.gearDeployed)
        {
            __instance.SetGear(LandingGear.GearState.Extending);
        }
        else if (gearState == LandingGear.GearState.LockedExtended && !__instance.gearDeployed)
        {
            __instance.SetGear(LandingGear.GearState.Retracting);
        }
    }

    /// <summary>
    /// Backstop for the postfix above, which only acts the moment the gear locks.
    /// <c>AIHeloTransportState</c> only compares the flag (<c>AIHeloLandingState</c> reads the gear
    /// itself), so gear left locked up under a "down" flag is fixed here on every tick. Logged.
    /// </summary>
    [HarmonyPatch(typeof(AIHeloTransportState), nameof(AIHeloTransportState.FixedUpdateState))]
    [HarmonyPrefix]
    private static void TransportGearPrefix(Aircraft ___aircraft)
    {
        if (___aircraft == null
            || !___aircraft.gearDeployed
            || ___aircraft.gearState != LandingGear.GearState.LockedRetracted)
        {
            return;
        }

        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(___aircraft)} had its gear locked up while delivering with the gear "
                + $"ordered down, {___aircraft.radarAlt:0} m up; lowering it.");
        ___aircraft.SetGear(LandingGear.GearState.Extending);
    }

    /// <summary>
    /// The base game's 20 % fuel check sends any AI aircraft home from its combat state, whatever the
    /// player just ordered. An order given on a low tank is flown first.
    /// <c>FuelChecker</c> is a protected nested class, hence the <c>TargetMethod</c>.
    /// </summary>
    [HarmonyPatch]
    internal static class LowFuelOrderPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.Inner(typeof(PilotBaseState), "FuelChecker"), "HasEnoughFuel");
        }

        internal static bool Prefix(Aircraft ___aircraft, ref bool __result)
        {
            if (!CommanderAirCommandService.AnyLowFuelOrder
                || !CommanderAirCommandService.IsFlyingLowFuelOrder(___aircraft))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }
}
