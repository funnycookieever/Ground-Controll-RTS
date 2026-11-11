using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// The highest an AI aeroplane may aim for. <c>AIPilotCombatModes.FixedUpdateState</c> clamps
    /// <c>targetHeight</c> to 8 km, and <c>CommanderAirCommandPatches.RaiseHeightCeiling</c> swaps that
    /// for this. A bomber wants most of it, and <see cref="GovernClimb"/> decides how much of it the
    /// airframe actually gets.
    /// </summary>
    internal const float HighestAiHeightMeters = MaxTargetAltitudeMeters;

    private const float AutopilotHuntRefreshSeconds = 3f;

    /// <summary>Close enough to a last known position that a unit still there would have been seen.</summary>
    private const float AutopilotSearchedMeters = 1500f;

    /// <summary>Throttle per m/s of speed error, and per metre away from the station.</summary>
    private const float EscortSpeedGain = 0.02f;
    private const float EscortDistanceGain = 0.0004f;
    private const float EscortStationMeters = 800f;

    /// <summary><c>AIPilotCombatModes.AttackMode.FlyingToTarget</c>, the private enum's first value.</summary>
    private const int FlyingToTargetMode = 0;

    /// <summary><c>AIHeloCombatState.CombatMode.NoTarget</c>; FlyingToTarget is 0 there as well.</summary>
    private const int HeloNoTargetMode = 3;

    // A guess at what a loaded helicopter can reach, the slider goes to 12 km.
    private const float MaxHeloAltitudeMeters = 3000f;

    /// <summary><c>AttackMode.GlideBombing</c>. The game enters it as soon as a glide bomb is picked,
    /// 40 km out, and flies straight in from there.</summary>
    private const int GlideBombingMode = 5;

    /// <summary>Share of a missile's reach a commanded aircraft waits at between shots.</summary>
    private const float MissileStandoffFraction = 0.75f;

    /// <summary>How far out an enemy free-fall bomber circles while it climbs, and how much of its
    /// cruise height it needs before it goes in. Half, because a loaded Alkyon tops out near 8.5 km
    /// of a 10-12 km cruise; 5 km is over the guns and short-range SAMs that killed it at 400 m.</summary>
    private const float BomberClimbRingMeters = 15000f;
    private const float BomberClimbShare = 0.5f;

    /// <summary>A glide-bomb carrier circles this far out until it has <see cref="GlideReleaseMeters"/>
    /// (or nine tenths of its cruise height, if that is lower), then goes in and releases from standoff.
    /// Otherwise it releases the moment the target comes into glide range mid-climb, low and close.
    /// From 6 km a glide bomb reaches about 35-40 km, so the ring sits just outside that.</summary>
    private const float GlideClimbRingMeters = 40000f;
    private const float GlideReleaseMeters = 6000f;

    /// <summary>Height error at which a cruise climb or descent reaches <see cref="MaxCruiseSlope"/>.</summary>
    private const float RoutePitchMeters = 300f;

    /// <summary>Steepest cruise climb, height per metre flown: about 11 degrees.</summary>
    private const float MaxCruiseSlope = 0.2f;

    /// <summary>An AI aircraft fires a scouting store once inside this of its target or mission area,
    /// and at most once per <see cref="ScoutIntervalSeconds"/> (scaled).</summary>
    private const float ScoutLaunchMeters = 15000f;
    private const float ScoutIntervalSeconds = 45f;

    /// <summary>Most glide bombs or Piledrivers an enemy airframe releases in one pass.</summary>
    private const int SwarmSize = 12;

    /// <summary>Other targets a swarm takes round the one the pilot picked. The game's own list only
    /// looks 1 km round it and inside the nose cone, so every swarm went "on 1 target".</summary>
    private const float SwarmSpreadMeters = 1500f;

    /// <summary>The same 0-1 rating floor <c>IsAirToAir</c> uses for "this store is for that".</summary>
    private const float MinForcedPunch = 0.05f;

    private const float HardenedArmorTier = 5f;

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, Unit> CurrentTargetRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, Unit>("currentTarget");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, WeaponInfo> CurrentWeaponInfoRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, WeaponInfo>("currentWeaponInfo");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, float> TargetHeightRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, float>("targetHeight");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, bool> FollowTerrainRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, bool>("followTerrain");

    private static readonly FieldInfo? AttackModeField = HarmonyLib.AccessTools.Field(typeof(AIPilotCombatModes), "attackMode");
    private static readonly FieldInfo? HeloCombatModeField = HarmonyLib.AccessTools.Field(typeof(AIHeloCombatState), "combatMode");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, float> TargetDistRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, float>("targetDist");

    private static readonly HarmonyLib.AccessTools.FieldRef<AIPilotCombatModes, bool> TargetObscuredRef =
        HarmonyLib.AccessTools.FieldRefAccess<AIPilotCombatModes, bool>("targetObscured");

    private static readonly List<(Unit Unit, float Distance)> SwarmSpread = new();

    private static readonly Dictionary<Aircraft, float> NextScoutAt = new();

    private static readonly Dictionary<Aircraft, (float NextAt, bool Found, GlobalPosition Point)> AutopilotHuntPoints = new();

    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.HandleAircraftReturned(aircraft);
    }

    internal static void NotifyUnitDisabled(Unit unit)
    {
        if (unit is Aircraft aircraft)
        {
            AutopilotHuntPoints.Remove(aircraft);
            CommanderAirTerrainGuard.Forget(aircraft);
            CommanderHeloRoadRoute.Forget(aircraft);
            Instance?.RemoveMission(aircraft);
        }
    }

    internal static bool TryChooseMissionTarget(
        Unit searcher,
        List<WeaponStation> stations,
        out CombatAI.TargetSearchResults result)
    {
        result = default;
        if (Instance == null || searcher is not Aircraft aircraft)
        {
            return false;
        }

        if (!Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            // HOLD FIRE with no mission (autopilot): no target at all, so the base game picks none.
            // outOfAmmo stays false: true is "rack empty", and the pilot and the resupply sweep
            // both send the aircraft home on it.
            if (!CommanderMoveService.IsHoldingFire(aircraft))
            {
                return false;
            }

            result = new CombatAI.TargetSearchResults(null!, null!, 0f, false);
            return true;
        }

        // A landing order is a place to be, not a fight to pick: left target-hunting, the aircraft chases
        // the first thing it sees and never reaches the field. Going quiet also lets AIHeloCombatState
        // fly the route, since it only steers toward the mission point from NoTarget.
        //
        // A loaded cargo run is the same: the drop needs the aircraft to actually overfly the point.
        if (mission.Returning
            || mission.LandingBase != null
            || CommanderSupplyHeliService.Instance?.IsCargoRunAircraft(aircraft) == true)
        {
            result = new CombatAI.TargetSearchResults(null!, null!, 0f, true);
            return true;
        }

        // A bomber that has dropped everything goes home to rearm, even with a jammer pod or heat
        // seeker still aboard. DropsBombs is only set once the stores have loaded (they spawn
        // asynchronously), so a fresh airframe is never sent straight back. The player's bombers get
        // the same rule once they are committed by an attack order. Piledrivers and Tusko-Bs count.
        if (!IsPlayerMission(mission) || mission.IgnoreGroundThreats)
        {
            if (HasBombAmmo(aircraft, glideOnly: false) || CarriesStrategicStore(aircraft, includeNuclear: true))
            {
                mission.DropsBombs = true;
            }
            else if (mission.DropsBombs)
            {
                // Its forced target too, or the forced-target path strafes it with the gun.
                mission.ForcedTarget = null;
                mission.WantsResupply = true;
                result = new CombatAI.TargetSearchResults(null!, null!, 0f, true);
                return true;
            }
        }

        result = ChooseMissionTarget(aircraft, stations, mission);

        // An empty rack ends a sortie, not an order: a commanded aircraft with travel points left keeps
        // flying them.
        //
        // It also does not end the aircraft. The flag asks the mission sweep for a resupply run instead
        // of handing the airframe to the base-game landing (which returns it to inventory); afterwards the
        // same aeroplane goes back to the same mission area.
        //
        // "No usable station" and "empty rack" share one flag, so the aircraft's actual ammunition breaks
        // the tie. Otherwise an airframe with nothing the mode accepts loops between resupply and re-task.
        if (result.outOfAmmo
            && mission.RouteIndex >= mission.Route.Count
            && mission.LandingBase == null
            && CommanderSupplyStoreService.GetMissingAmmoKg(aircraft) > 0f)
        {
            mission.WantsResupply = true;
        }

        return true;
    }

    /// <summary>
    /// True when a pilot is about to hand itself to the base game's cargo-delivery state on an
    /// aircraft the player has given a mission to. See the patch that calls this.
    /// </summary>
    internal static bool ShouldRefuseTransportState(Pilot pilot, PilotBaseState state)
    {
        if (Instance == null
            || Instance.missions.Count == 0
            || pilot == null)
        {
            return false;
        }

        // A modded fixed-wing transport is refused even on a cargo run: Air Command steers that run
        // and the mod's state would fly its own delivery instead of the player's LZ.
        bool moddedTransport = IsModdedTransportState(state);
        if (!moddedTransport && state is not AIHeloTransportState)
        {
            return false;
        }

        Aircraft? aircraft = pilot.aircraft;
        return aircraft != null
            && Instance.missions.ContainsKey(aircraft)
            && (moddedTransport || CommanderSupplyHeliService.Instance?.IsCargoRunAircraft(aircraft) != true);
    }

    internal static bool TryBuildAradSaturationTargets(
        Aircraft aircraft,
        WeaponStation station,
        out int targetCount)
    {
        targetCount = 0;
        if (Instance == null
            || aircraft == null
            || station == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || !UsesSaturationAttack(mission.Mode)
            || !mission.SaturationAttack
            || station.WeaponInfo == null
            || !IsAradWeapon(station.WeaponInfo))
        {
            return false;
        }

        WeaponManager manager = aircraft.weaponManager;
        if (station.SalvoInProgress)
        {
            targetCount = manager.GetTargetList().Count;
            return true;
        }

        FactionHQ? hq = aircraft.NetworkHQ;
        WeaponInfo? info = station.WeaponInfo;
        if (hq == null || info == null || station.Ammo <= 0)
        {
            manager.ClearTargetList();
            return true;
        }

        List<Unit> candidates = new();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (!tracking.TryGetUnit(out Unit target)
                || target == null
                || target.disabled
                || target.NetworkHQ == null
                || target.NetworkHQ == hq
                || !IsTargetEligible(target, AirCommandMode.Arad, targetOrdnance: false)
                || !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)
                || !hq.IsTargetPositionAccurate(target, 1000f))
            {
                continue;
            }

            float range = FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition());
            Vector3 direction = tracking.GetPosition() - aircraft.GlobalPosition();
            if (range < info.targetRequirements.minRange
                || range > info.targetRequirements.maxRange
                || Vector3.Angle(direction, aircraft.transform.forward) >= info.targetRequirements.minAlignment
                || (info.targetRequirements.lineOfSight && !target.LineOfSight(aircraft.transform.position, 1000f))
                || station.CalcOpportunityThreat(target.definition, aircraft).opportunity <= 0f)
            {
                continue;
            }

            candidates.Add(target);
        }

        manager.ClearTargetList();
        if (candidates.Count == 0)
        {
            return true;
        }

        int missileCount = Mathf.Min(station.Ammo, 32);
        for (int i = 0; i < missileCount; i++)
        {
            manager.AddTargetList(candidates[i % candidates.Count]);
        }

        targetCount = missileCount;
        return true;
    }

    /// <summary>
    /// Where a player's AUTOPILOT helicopter goes when it has nothing to shoot. The stock
    /// <c>AIHeloCombatState.NoTarget</c> hovers over the nearest mission objective until the first
    /// contact, and once <c>flightInfo.EnemyContact</c> is set (never cleared) it flies home instead,
    /// lands after 15 s idle, and its own ejection check hands it back to stock: the airframe the
    /// player bought "parks, hovers, goes home and exits". While it still has rounds aboard it is
    /// sent at the nearest thing it can actually hurt, then at the nearest enemy base; a dry one
    /// is marked as having had contact, which is what sends the stock state home to land and rearm.
    /// Duel and Champions only, where every aircraft is commander-bought, so a mission-less airframe
    /// on a human faction is always one the player released to autopilot.
    /// </summary>
    internal static bool TryGetAutopilotHuntPoint(PilotBaseState state, out GlobalPosition point)
    {
        point = default;
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null
            || hq == null
            || aircraft.Player != null
            || !CommanderEnemyCommanderService.IsDuelMission
            || (Instance != null && Instance.missions.ContainsKey(aircraft))
            || (!ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()) && !CommanderEnemyCommanderService.HasHumanPlayers(hq)))
        {
            return false;
        }

        if (!HasCombatAmmo(aircraft))
        {
            // Without this an unarmed Ibis that has seen nothing hovers on the objective for good.
            aircraft.pilots[0].flightInfo.EnemyContact = true;
            return false;
        }

        // NoTarget runs every physics tick and the search walks the whole tracking database.
        float now = Time.timeSinceLevelLoad;
        if (!AutopilotHuntPoints.TryGetValue(aircraft, out (float NextAt, bool Found, GlobalPosition Point) hunt) || now >= hunt.NextAt)
        {
            bool found = TryFindHuntPoint(aircraft, hq, out GlobalPosition next);
            hunt = (now + AutopilotHuntRefreshSeconds, found, next);
            AutopilotHuntPoints[aircraft] = hunt;
        }

        point = hunt.Point;
        return hunt.Found;
    }

    internal static bool TryGetMissionHoldPoint(PilotBaseState state, out GlobalPosition point)
    {
        point = default;
        if (Instance == null || Instance.missions.Count == 0)
        {
            return false;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        if (mission.Returning)
        {
            // Only the combat brain calls this, so a returning aircraft that is here is one whose
            // landing did not take (no runway it fits, a waved-off approach). Left alone, the base
            // game's NoTarget flies it at the mission's objective, which is the enemy base: the
            // Chimera and Medusa that circled it. Point it home and let the sweep send it again.
            mission.RtbIssued = false;
            Airbase? home = mission.LandingBase != null && FitsField(aircraft, mission.LandingBase)
                ? mission.LandingBase
                : FindNearestOwnedAirbase(aircraft, mustFit: true) ?? mission.LandingBase ?? FindNearestOwnedAirbase(aircraft);
            if (home == null || home.center == null)
            {
                return false;
            }

            point = home.center.GlobalPosition();
            return true;
        }

        point = GetActiveRoutePoint(aircraft, mission);
        if (mission.RouteIndex >= mission.Route.Count && aircraft.NetworkHQ != null && !mission.IgnoreGroundThreats)
        {
            // Nothing left to fly to but the area itself: wait on the near side of any ground fire
            // the faction knows about there, not over it. Once the target search stopped taking
            // defended targets, an area on an enemy base would otherwise be an aircraft circling
            // over its guns with nothing to shoot. A travel point is still flown to the letter.
            // Never past the edge of the area. The depth is up to 1.2x the longest SAM's reach, and
            // pushed out that far the loiter point sat well outside a CAS or CAP box: the aircraft
            // flew off toward it and read as ignoring its mission area.
            float depth = Mathf.Min(
                CombatAI.GetSafeStandoffDist(point, aircraft.NetworkHQ),
                Mathf.Max(mission.Radius, 1000f) * 0.9f);
            Vector3 back = aircraft.GlobalPosition() - point;
            back.y = 0f;
            if (depth > 0f && back.sqrMagnitude > 1f)
            {
                point += back.normalized * depth;
            }
        }

        return true;
    }

    /// <summary>
    /// Matches an escort's speed to the thing it is escorting. The base game flies its no-target
    /// cruise at a fixed throttle, which is the wrong one twice over: a fighter on cruise runs
    /// away from a laden bomber, and a helicopter on cruise never catches a fast one. Runs only
    /// while the aircraft has nothing to shoot at — once it is engaging, the base game's own
    /// throttle work is what wins the fight.
    /// </summary>
    /// <remarks>
    /// Proportional on the speed error with a distance term, so an escort that has fallen behind
    /// opens up and one that is overtaking backs off.
    /// </remarks>
    internal static void ApplyEscortThrottle(PilotBaseState state)
    {
        if (Instance == null || Instance.missions.Count == 0)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        Unit? escorted = mission.EscortTarget;
        if (escorted == null
            || escorted.disabled
            || mission.Returning
            || mission.LandingBase != null)
        {
            return;
        }

        ControlInputs? inputs = aircraft.GetInputs();
        if (inputs == null)
        {
            return;
        }

        inputs.throttle = EscortThrottle(
            aircraft.GetAircraftParameters()?.cruiseThrottle ?? 0.8f,
            escorted.speed - aircraft.speed,
            FastMath.Distance(escorted.GlobalPosition(), aircraft.GlobalPosition()));
    }

    /// <summary>
    /// The escort throttle law, split out of the patch so <see cref="SelfCheck"/> can run it with
    /// no scene. Pure maths on plain numbers.
    /// </summary>
    // Proportional only. If escorts hunt around their station, this is where a
    // derivative term goes.
    internal static float EscortThrottle(float cruise, float speedError, float distance)
    {
        return Mathf.Clamp01(
            cruise + (speedError * EscortSpeedGain) + ((distance - EscortStationMeters) * EscortDistanceGain));
    }

    /// <summary>Glide bombs, or any bomb (free-fall, glide, laser-guided) when
    /// <paramref name="glideOnly"/> is false.</summary>
    internal static bool IsBombStore(WeaponInfo? weapon, bool glideOnly)
    {
        return weapon != null
            && (weapon.glideBomb || (!glideOnly && (weapon.bomb || (weapon.laserGuided && !weapon.missile))));
    }

    /// <summary>At least one loaded station carries a store <see cref="IsBombStore"/> accepts.</summary>
    internal static bool HasBombAmmo(Aircraft aircraft, bool glideOnly)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station != null && station.Ammo > 0 && IsBombStore(station.WeaponInfo, glideOnly))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Half as fast again as the airframe's own takeoff speed: a stall margin, read off its asset
    /// data so it scales with the aircraft. 0 (never governed) for anything that has none.
    /// </summary>
    internal static float GetSafeClimbSpeed(UnitDefinition? definition)
    {
        return definition is AircraftDefinition aircraft && aircraft.aircraftParameters != null
            ? Mathf.Max(0f, aircraft.aircraftParameters.takeoffSpeed * 1.5f)
            : 0f;
    }

    /// <summary>
    /// An enemy glide-bomb or Piledriver pass releases the whole station at once over every target
    /// the game found round its aim point; the empty rack then sends it home
    /// to rearm (<c>TryChooseMissionTarget</c>). Nuclear stores stay at one round a target.
    /// <c>WeaponManager.Fire</c> salvoes one round per entry in the target list, which
    /// <c>LookForMissileTargets</c> fills with the attacks one target needs, usually one, so the AI
    /// would drop a single bomb, break off and come round again for the next. Padding the list
    /// with the same targets, round robin, turns that pass into a swarm.
    /// </summary>
    internal static void SwarmRelease(Aircraft aircraft, WeaponStation station, List<Unit> targets)
    {
        if (Instance == null
            || aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || IsPlayerMission(mission))
        {
            return;
        }

        int want = Mathf.Min(station.Ammo, SwarmSize);
        AddSwarmSpread(aircraft, station.WeaponInfo, targets, want);
        int picked = targets.Count;
        for (int i = 0; targets.Count < want; i = (i + 1) % picked)
        {
            targets.Add(targets[i]);
        }

        // Logged whenever more than one round goes: a pass spread over twelve separate targets pads
        // nothing, and the old "padded" test hid exactly the swarms the spread was for.
        if (targets.Count > 1)
        {
            CommanderPlugin.Log.LogInfo(
                $"{aircraft.unitName} releases a swarm of {targets.Count} {station.WeaponInfo.weaponName} on {picked} target(s).");
        }
    }

    /// <summary>An ordered missile intercept: the aircraft skips missile evasion while it lasts.</summary>
    internal static bool IsChasingMissile(Aircraft? aircraft)
    {
        return aircraft != null
            && Instance != null
            && Instance.missions.Count > 0
            && Instance.GetForcedTarget(aircraft) is Missile;
    }

    /// <summary>
    /// After the base game's missile evasion: an enemy bomb carrier keeps the height it has. Evading
    /// a radar missile, <c>EvadeModeRadar</c> sets <c>targetHeight</c> to 10 m with terrain following,
    /// after the mod's bombing floor has run, which dives a fast bomber into the ground. It still
    /// turns and drops chaff; it only does not dive.
    /// </summary>
    internal static void HoldBomberEvasionHeight(AIPilotCombatModes state)
    {
        if (Instance == null || Instance.missions.Count == 0)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.TargetAltitude <= 0f
            || IsPlayerMission(mission)
            || !HasBombAmmo(aircraft, glideOnly: false))
        {
            return;
        }

        float floor = Mathf.Min(GovernClimb(aircraft, mission.TargetAltitude), aircraft.radarAlt);
        if (TargetHeightRef(state) < floor)
        {
            TargetHeightRef(state) = floor;
        }
    }

    internal static void ApplyMissionTargetAltitude(AIPilotCombatModes state, FieldInfo? targetHeightField)
    {
        // A cruise height, never an attack height: while the pilot has a target the base game owns
        // the altitude, so dives of several kilometres for speed and bombing runs from any height
        // play out as they would without the mod. Writing it every frame regardless is what made
        // the enemy's heights look hard-set.
        if (Instance == null
            || (Instance.missions.Count == 0 && Instance.autopilotAltitudes.Count == 0)
            || targetHeightField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null)
        {
            return;
        }

        // No mission and no height set: AUTOPILOT at AUTO, the base game's own.
        Instance.missions.TryGetValue(aircraft, out AirMission? mission);
        float altitude = Instance.GetMissionAltitude(aircraft);
        if (mission == null && altitude == 0f)
        {
            return;
        }

        // LOW LEVEL: the airframe's own minimum radar altitude, the floor the base game's
        // ManageAltitude already lifts it back to, so the terrain warning still pulls it up over a
        // ridge. Only on the way in: the attack itself keeps the base game's heights. followTerrain
        // is forced because NoTarget never sets it, and with it off AutoAim ignores the height.
        Unit? target = CurrentTargetRef(state);
        if (altitude < 0f
            && (target == null || (target is not Aircraft and not Missile && IsInTransit(state))))
        {
            TargetHeightRef(state) = aircraft.GetAircraftParameters().minimumRadarAlt;
            FollowTerrainRef(state) = true;
            return;
        }

        if (CurrentTargetRef(state) != null)
        {
            if (mission != null)
            {
                ApplyEnemyBombingFloor(state, aircraft, mission);
            }

            if (mission == null || IsPlayerMission(mission))
            {
                ApplyPlayerAttackFloor(state, aircraft, altitude);
            }

            return;
        }

        if (altitude > 0f)
        {
            TargetHeightRef(state) = GovernClimb(aircraft, altitude);
        }
    }

    /// <summary>
    /// The rotary twin of <see cref="ApplyMissionTargetAltitude"/>: prefix on
    /// <c>AutopilotHelo.AutoAim</c>, because <c>AIHeloCombatState</c> clamps <c>desiredHeight</c> to
    /// 1000 m just before calling it. A cruise height only: the attack modes keep their own. Not for
    /// the enemy's helicopters, whose commander hands out heights meant for aeroplanes.
    /// </summary>
    internal static void HoldHeloAltitude(Autopilot autopilot, ref float altitudeHold)
    {
        if (Instance == null
            || (Instance.missions.Count == 0 && Instance.autopilotAltitudes.Count == 0)
            || HeloCombatModeField == null)
        {
            return;
        }

        Aircraft? aircraft = autopilot.aircraft;
        if (aircraft == null
            || aircraft.Player != null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || aircraft.pilots[0].currentState is not AIHeloCombatState state)
        {
            return;
        }

        float altitude = Instance.GetMissionAltitude(aircraft);
        if (altitude == 0f
            || (Instance.missions.TryGetValue(aircraft, out AirMission mission) && !IsPlayerMission(mission)))
        {
            return;
        }

        int mode = Convert.ToInt32(HeloCombatModeField.GetValue(state), CultureInfo.InvariantCulture);
        if (mode != FlyingToTargetMode && mode != HeloNoTargetMode)
        {
            return;
        }

        altitudeHold = altitude < 0f
            ? aircraft.GetAircraftParameters().minimumRadarAlt
            : Mathf.Min(altitude, MaxHeloAltitudeMeters)
                + ((aircraft.persistentID.Id % 4) * CommanderAirCommandPatches.HeloTransitStaggerMeters);
    }

    /// <summary>
    /// Flies an ordered cruise height on the destination's height, not by terrain following. With
    /// <c>followTerrain</c> on (left over from any attack mode), <c>AutopilotPlane.AutoAim</c> scales
    /// the held height by airspeed, down to a tenth near landing speed: a Compass slowed in the climb,
    /// the height it held dropped, it dived, gained speed and climbed again, a kilometre down and up
    /// the whole way home. A Brawler on AUTOPILOT did the same on the game's own height,
    /// which <c>ManageAltitude</c> keeps 200 m over the aircraft. The terrain warning still pulls it
    /// up over a ridge. LOW LEVEL keeps terrain following.
    /// The leg in to a surface target still out of reach is the same cruise when the player set a
    /// height: <c>FlyToTarget</c> turns terrain following back on every second, and a Brawler sent at
    /// a Radar Station with 12000 m set waved all the way there. With the game's choice
    /// that leg is the base game's low-level run in, which needs terrain following, so it is left.
    /// Every frame, after <c>RunAttackMode</c> has written the destination (a ground point).
    /// </summary>
    internal static void HoldCruiseHeight(AIPilotCombatModes state)
    {
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null)
        {
            return;
        }

        float altitude = Instance?.GetMissionAltitude(aircraft) ?? 0f;
        if (altitude < 0f)
        {
            return;
        }

        // The player's height holds on the way to an aircraft too; the enemy's own intercepts are left be.
        Unit? target = CurrentTargetRef(state);
        AirMission? mission = null;
        Instance?.missions.TryGetValue(aircraft, out mission);
        if (target != null
            && (altitude <= 0f
                || target is Missile
                || (target is Aircraft && mission != null && !IsPlayerMission(mission))
                || AttackModeField == null
                || Convert.ToInt32(AttackModeField.GetValue(state), CultureInfo.InvariantCulture) != FlyingToTargetMode
                || !IsInTransit(state)))
        {
            return;
        }

        float wanted = altitude > 0f ? GovernClimb(aircraft, altitude) : TargetHeightRef(state);

        FollowTerrainRef(state) = false;
        GlobalPosition destination = CommanderAirCommandPatches.DestinationRef(state);
        destination.y = CruisePointHeight(aircraft.GlobalPosition(), destination, wanted - aircraft.radarAlt);
        CommanderAirCommandPatches.DestinationRef(state) = destination;
    }

    internal static void ConstrainMissionDestination(AIPilotCombatModes state, FieldInfo? destinationField)
    {
        AimMissileIntercept(state);
        HoldMissileStandoff(state, destinationField);
        HoldBomberClimb(state);
        ConstrainMissionDestination((PilotBaseState)state, destinationField);
        HoldCruiseHeight(state);
    }

    /// <summary>
    /// The way in to a missile. <c>FlyToTarget</c> leads every target by its velocity times
    /// <c>min(distance / own speed, 20 s)</c>, which assumes the target is slower than the chaser: a
    /// Cricket at 80 m/s 5 km from a 600 m/s missile aimed 12 km down the missile's track, which for a
    /// missile coming its way is behind the Cricket, so it turned its back on it. Aims at the real
    /// intercept point instead, or straight at the missile when it cannot be caught.
    /// </summary>
    internal static void AimMissileIntercept(AIPilotCombatModes state)
    {
        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || CurrentTargetRef(state) is not Missile missile
            || missile == null
            || missile.disabled
            || missile.rb == null
            || AttackModeField == null
            || Convert.ToInt32(AttackModeField.GetValue(state), CultureInfo.InvariantCulture) != FlyingToTargetMode
            || !aircraft.NetworkHQ.TryGetKnownPosition(missile, out GlobalPosition known))
        {
            return;
        }

        Vector3 velocity = missile.rb.velocity;
        float time = InterceptTime(known - aircraft.GlobalPosition(), velocity, aircraft.speed);
        CommanderAirCommandPatches.DestinationRef(state) = known + (velocity * time);
    }

    /// <summary>
    /// Seconds until a chaser at <paramref name="speed"/> can meet a target at offset
    /// <paramref name="offset"/> moving at <paramref name="velocity"/>; 0 (fly at it) when it never can.
    /// </summary>
    internal static float InterceptTime(Vector3 offset, Vector3 velocity, float speed)
    {
        // |offset + velocity t| = speed t
        float a = velocity.sqrMagnitude - (speed * speed);
        float b = 2f * Vector3.Dot(offset, velocity);
        float c = offset.sqrMagnitude;
        float time;
        if (Mathf.Abs(a) < 0.001f)
        {
            time = b < 0f ? -c / b : 0f;
        }
        else
        {
            float discriminant = (b * b) - (4f * a * c);
            if (discriminant < 0f)
            {
                return 0f;
            }

            float root = Mathf.Sqrt(discriminant);
            float t1 = (-b - root) / (2f * a);
            float t2 = (-b + root) / (2f * a);
            float low = Mathf.Min(t1, t2);
            time = low > 0f ? low : Mathf.Max(t1, t2);
        }

        return time > 0f ? Mathf.Min(time, 20f) : 0f;
    }

    /// <summary>
    /// Keeps a commanded aircraft on the point it was given and inside the box it was given, every
    /// frame, after whichever attack mode has just written its own destination.
    /// </summary>
    /// <remarks>
    /// Takes the state as a <c>PilotBaseState</c> so the fixed-wing and rotary brains share it:
    /// <c>destination</c> and <c>aircraft</c> both live on the base class, and the rule is the same
    /// for both. The rotary half had none of this — <c>ConstrainMissionDestination</c> only ever ran
    /// off <c>AIPilotCombatModes.RunAttackMode</c> — so a helicopter or tiltwing was leashed to
    /// nothing at all the moment it had a target.
    /// </remarks>
    internal static void ConstrainMissionDestination(PilotBaseState state, FieldInfo? destinationField)
    {
        if (Instance == null || Instance.missions.Count == 0 || destinationField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning)
        {
            return;
        }

        // A travel point is an order; a target is an opportunity; the order wins. Every attack mode
        // writes its own destination each frame, so while a route is unfinished the route stays the
        // destination: shoot what you pass, still go where you were sent.
        //
        // This includes a commanded target queued behind a route. ChooseMissionTarget answers
        // ForcedTarget first, so the package flies the route and releases on the target as soon as it is
        // inside a weapon's reach. Once the route runs out the attack owns the steering.
        if (mission.RouteIndex < mission.Route.Count)
        {
            CommanderAirCommandPatches.DestinationRef(state) = GetActiveRoutePoint(aircraft, mission);
            return;
        }

        // An ordered missile intercept is not leashed. The area was centred where the missile was at
        // the click and AIR SUP holds the inner 75 % of it, so a missile flying on at several hundred
        // metres a second left the box in seconds and the interceptor was pulled back to the centre
        // instead of chasing it. A missile lives for under a minute, so the chase is bounded.
        if (mission.ForcedTarget is Missile missile && missile != null && !missile.disabled)
        {
            return;
        }

        bool station;
        if (!((station = KeepsStationInMissionArea(mission.Mode)) || TargetsRestrictedToMissionArea(mission.Mode)))
        {
            return;
        }

        GlobalPosition destination = CommanderAirCommandPatches.DestinationRef(state);
        Vector3 destinationOffset = destination - mission.AreaCenter;
        destinationOffset.y = 0f;
        Vector3 aircraftOffset = aircraft.GlobalPosition() - mission.AreaCenter;
        aircraftOffset.y = 0f;
        if (!ClampToMissionArea(aircraftOffset, destinationOffset, mission.Radius, station, out Vector3 clamped))
        {
            return;
        }

        CommanderAirCommandPatches.DestinationRef(state) = mission.AreaCenter + clamped;
    }

    /// <summary>
    /// Where inside its own area an aircraft is allowed to be steered, as an offset from the area
    /// centre. Returns false when the destination it was given is already legal.
    /// </summary>
    /// <remarks>
    /// A station orbits the inner three quarters so it does not drift off the edge of its own box.
    /// A strike package may work the whole area — its targets are already filtered to it — but it
    /// may not leave: CAS with nothing left in the box kept whatever destination the Basegame attack
    /// run had written and flew straight on to the enemy's main base.
    /// <para>
    /// Pure maths, split out of the patch so <see cref="SelfCheck"/> can run it with no scene.
    /// </para>
    /// </remarks>
    internal static bool ClampToMissionArea(
        Vector3 aircraftOffset, Vector3 destinationOffset, float missionRadius, bool station, out Vector3 clamped)
    {
        float radius = Mathf.Max(missionRadius, 1000f);
        float recallLimit = station ? radius * 0.9f : radius;
        float destinationLimit = station ? radius * 0.75f : radius;
        if (aircraftOffset.magnitude > recallLimit)
        {
            clamped = Vector3.zero;
            return true;
        }

        if (destinationOffset.magnitude > destinationLimit)
        {
            clamped = destinationOffset.normalized * destinationLimit;
            return true;
        }

        clamped = destinationOffset;
        return false;
    }

    /// <summary>
    /// The pure part of the mission-area leash, run once at load. The repo has nowhere else to run
    /// a test, and a strike package that can leave its own box is the bug this exists to catch.
    /// </summary>
    internal static void SelfCheck()
    {
        const float radius = 20000f;
        bool failed = false;

        // A strike destination past the edge is pulled back onto it, never further out.
        if (!ClampToMissionArea(Vector3.zero, new Vector3(radius * 3f, 0f, 0f), radius, station: false, out Vector3 pulled)
            || pulled.magnitude > radius + 1f)
        {
            failed = true;
        }

        // An aircraft that has left the box is sent back to the middle of it.
        if (!ClampToMissionArea(new Vector3(radius * 2f, 0f, 0f), new Vector3(radius * 2f, 0f, 0f), radius, station: false, out Vector3 recalled)
            || recalled.sqrMagnitude > 1f)
        {
            failed = true;
        }

        // A destination already inside the box is left alone.
        if (ClampToMissionArea(Vector3.zero, new Vector3(radius * 0.5f, 0f, 0f), radius, station: false, out _))
        {
            failed = true;
        }

        // A station is held tighter than a strike package, not looser.
        if (!ClampToMissionArea(Vector3.zero, new Vector3(radius * 0.9f, 0f, 0f), radius, station: true, out Vector3 held)
            || held.magnitude >= radius)
        {
            failed = true;
        }

        // A Cricket meets an oncoming missile in front of itself, and flies straight at one it cannot catch.
        Vector3 ahead = new(5000f, 0f, 0f);
        float headOn = InterceptTime(ahead, new Vector3(-600f, 0f, 0f), 80f);
        if (headOn <= 0f || (ahead + (new Vector3(-600f, 0f, 0f) * headOn)).x <= 0f
            || InterceptTime(ahead, new Vector3(600f, 0f, 0f), 80f) != 0f)
        {
            failed = true;
        }

        // The two area-less modes are the whole reason the launch button can skip the map click.
        if (RequiresMissionArea(AirCommandMode.Autopilot)
            || RequiresMissionArea(AirCommandMode.Standby)
            || !RequiresMissionArea(AirCommandMode.Hybrid))
        {
            failed = true;
        }

        // The altitude stepper runs LOW LEVEL, STANDARD, 250 m and back, never skipping one.
        if (StepAltitude(0f, -250f) != LowLevelAltitude
            || StepAltitude(LowLevelAltitude, -250f) != LowLevelAltitude
            || StepAltitude(LowLevelAltitude, 250f) != 0f
            || StepAltitude(LowLevelAltitude, 750f) != 500f
            || StepAltitude(500f, -750f) != LowLevelAltitude
            || StepAltitude(0f, 250f) != TargetAltitudeStepMeters
            || StepAltitude(TargetAltitudeStepMeters, -250f) != 0f)
        {
            failed = true;
        }

        // An untouched slider must read back its own notch, or drawing it would change the altitude.
        for (int notch = -1; notch <= MaxAltitudeNotch; notch++)
        {
            if (AltitudeNotchAt(AltitudeSliderPosition(notch)) != notch)
            {
                failed = true;
            }
        }

        // A cruise climb is the same pitch at any distance: a far point must not flatten it out.
        GlobalPosition start = new(0f, 100f, 0f);
        if (!Mathf.Approximately(CruisePointHeight(start, new GlobalPosition(40000f, 0f, 0f), 2000f), 8100f)
            || !Mathf.Approximately(CruisePointHeight(start, new GlobalPosition(40000f, 0f, 0f), -2000f), -7900f)
            || !Mathf.Approximately(CruisePointHeight(start, new GlobalPosition(40000f, 0f, 0f), 0f), 100f))
        {
            failed = true;
        }

        // A runway's takeoff queue must lose exactly the entry named, wherever it sits, and keep the
        // order of the rest. Miss one and the strip is blocked the moment that ghost reaches the
        // front; drop the wrong one and two aeroplanes roll at once.
        Queue<string> queue = new();
        queue.Enqueue("head");
        queue.Enqueue("stale");
        queue.Enqueue("tail");
        if (!RemoveFromQueue(queue, "stale")
            || RemoveFromQueue(queue, "stale")
            || queue.Count != 2
            || queue.Dequeue() != "head"
            || queue.Dequeue() != "tail"
            || RemoveFromQueue(null, "head"))
        {
            failed = true;
        }

        if (failed)
        {
            CommanderPlugin.Log.LogError(
                "Air Command self-check FAILED: a mission can be steered outside its own area, "
                    + "a mode disagrees about whether it needs one, a runway booking cannot be released, "
                    + "or the altitude stepper skips a setting.");
            return;
        }

        CommanderPlugin.Log.LogInfo("Air Command self-check passed: missions stay inside their area.");
        SelfCheckPresets();
    }

    /// <summary>
    /// The station a commanded target is answered with: missiles, rockets and bombs before the gun
    /// (station 0 is usually the cannon), then whichever is best against that kind of target.
    /// </summary>
    internal static bool CanDefeatArmor(WeaponInfo? info, Unit? target)
    {
        if (info == null)
        {
            return false;
        }

        if (target == null || target.definition == null)
        {
            return true;
        }

        float tier = target.definition.armorTier;

        // A hardened building (tier 5 and up, the game's own "armoured" line in
        // ShouldUseTopAttack) is a job for bombs: an 8-rated missile passes the tier test alone,
        // and Brawlers kept emptying their racks into a munitions bunker.
        if (target is Building && tier >= HardenedArmorTier && !info.bomb && !info.glideBomb)
        {
            return false;
        }

        return info.armorTierEffectiveness >= tier;
    }

    /// <summary>
    /// An AI aircraft carrying a scouting store (the Eyeball Mk.II) fires one ahead of it once it is
    /// near its target or mission area, so what the Eyeball sees goes into the faction's tracking
    /// for every other unit. At the forced target if it has one, else at
    /// nothing, which sends it straight ahead. The same launch the player's FIRE makes.
    /// </summary>
    internal void TryLaunchAiScout(Aircraft aircraft)
    {
        if (aircraft.weaponStations == null
            || aircraft.weaponManager == null
            || !missions.TryGetValue(aircraft, out AirMission mission)
            || IsPlayerMission(mission)
            || mission.Returning
            || (NextScoutAt.TryGetValue(aircraft, out float next) && Time.time < next))
        {
            return;
        }

        WeaponStation? scout = null;
        for (int i = 0; i < aircraft.weaponStations.Count && scout == null; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station?.WeaponInfo != null && station.Ammo > 0 && CommanderFireNow.IsSensorStore(station.WeaponInfo))
            {
                scout = station;
            }
        }

        Unit? target = mission.ForcedTarget != null && !mission.ForcedTarget.disabled ? mission.ForcedTarget : null;
        GlobalPosition aim = target != null && aircraft.NetworkHQ != null && aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition known)
            ? known
            : mission.AreaCenter;
        if (scout == null
            || scout.SafetyIsOn(aircraft)
            || !scout.Ready()
            || !FastMath.InRange(aircraft.GlobalPosition(), aim, ScoutLaunchMeters))
        {
            return;
        }

        NextScoutAt[aircraft] = Time.time + ScoutIntervalSeconds;
        WeaponManager manager = aircraft.weaponManager;
        List<Unit> targets = manager.GetTargetList();
        targets.Clear();
        if (target != null)
        {
            targets.Add(target);
        }

        manager.TargetListChanged();
        manager.SetActiveStation(scout.Number);
        manager.Fire();
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(aircraft)} launches a {scout.WeaponInfo.weaponName} to scout"
                + (target != null ? $" {CommanderGameAccess.GetUnitLabel(target)}." : " ahead."));
    }

    /// <summary>What a commanded aircraft is attacking: the ordered target, else the one its pilot has.</summary>
    internal Unit? GetAttackTarget(Aircraft aircraft)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission) && mission.ForcedTarget != null && !mission.ForcedTarget.disabled)
        {
            return mission.ForcedTarget;
        }

        Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        return pilot?.currentState is AIPilotCombatModes combat ? CurrentTargetRef(combat) : null;
    }

    /// <summary>
    /// Every target this aircraft has been ordered at, the one it is working first: what FIRE
    /// salvos at and what the route line brackets. Dead entries are dropped on the way past.
    /// </summary>
    /// <summary>Every live target any of <paramref name="hq"/>'s tasked aircraft was ordered at,
    /// appended without duplicates. The cockpit markers read it a few times a second.</summary>
    internal void AppendOrderedTargets(FactionHQ hq, List<Unit> into)
    {
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Key == null || !ReferenceEquals(entry.Key.NetworkHQ, hq))
            {
                continue;
            }

            AppendLive(entry.Value.ForcedTarget);
            for (int i = 0; i < entry.Value.QueuedTargets.Count; i++)
            {
                AppendLive(entry.Value.QueuedTargets[i]);
            }
        }

        void AppendLive(Unit? target)
        {
            if (target != null && !target.disabled && !into.Contains(target))
            {
                into.Add(target);
            }
        }
    }

    internal void CollectAttackTargets(Aircraft aircraft, List<Unit> into)
    {
        into.Clear();
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        if (mission.ForcedTarget != null && !mission.ForcedTarget.disabled)
        {
            into.Add(mission.ForcedTarget);
        }

        for (int i = mission.QueuedTargets.Count - 1; i >= 0; i--)
        {
            Unit queued = mission.QueuedTargets[i];
            if (queued == null || queued.disabled)
            {
                mission.QueuedTargets.RemoveAt(i);
            }
        }

        into.AddRange(mission.QueuedTargets);
    }

    /// <summary>
    /// Every other known ground target or building round the first one that this store defeats,
    /// nearest first, one round each, up to <paramref name="want"/>. No reach test: the pass is
    /// released at the first target's range, often the store's limit, and testing each extra one
    /// against it would throw away everything just beyond.
    /// </summary>
    private static void AddSwarmSpread(Aircraft aircraft, WeaponInfo info, List<Unit> targets, int want)
    {
        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq == null || targets.Count >= want || !hq.TryGetKnownPosition(targets[0], out GlobalPosition aim))
        {
            return;
        }

        SwarmSpread.Clear();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            GlobalPosition at = entry.Value.lastKnownPosition;
            if (!entry.Value.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not (Building or GroundVehicle)
                || unit.NetworkHQ == null
                || unit.NetworkHQ == hq
                || !FastMath.InRange(at, aim, SwarmSpreadMeters)
                || targets.Contains(unit)
                || !CanDefeatArmor(info, unit))
            {
                continue;
            }

            SwarmSpread.Add((unit, FastMath.Distance(at, aim)));
        }

        SwarmSpread.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        for (int i = 0; i < SwarmSpread.Count && targets.Count < want; i++)
        {
            targets.Add(SwarmSpread[i].Unit);
        }

        SwarmSpread.Clear();
    }

    /// <summary>
    /// A mission a human gave, the host's own or a multiplayer guest's. Guest aircraft fly on the host,
    /// so "is this the local HQ" would treat them as enemy AI.
    /// </summary>
    private static bool IsPlayerMission(AirMission mission)
    {
        return !CommanderEnemyCommanderService.IsAiFaction(mission.Hq, CommanderGameAccess.GetLocalHq());
    }

    /// <summary>
    /// How much of a wanted height the airframe gets right now: all of it, unless it is climbing and
    /// has slowed below a safe speed, in which case it levels off where it is until the speed comes
    /// back. The safe speed is the airframe's own data (<see cref="GetSafeClimbSpeed"/>), so a heavy
    /// Brawler stops climbing low and a fighter goes all the way; no per-type height table. It pauses a climb, it never lowers a height: the player's ordered altitude is still
    /// where the aircraft ends up if the airframe can get there at all.
    /// </summary>
    private static float GovernClimb(Aircraft aircraft, float wanted)
    {
        return wanted > aircraft.radarAlt && aircraft.speed < GetSafeClimbSpeed(aircraft.definition)
            ? aircraft.radarAlt
            : wanted;
    }

    /// <summary>
    /// The one exception to "the base game owns an attack's altitude": an enemy airframe dropping
    /// bombs. <c>FlyToTarget</c> follows the terrain and <c>ManageAltitude</c> takes 10 m off the
    /// height every tick while the target is in view, so the base game's bomb run is flown at the
    /// airframe's minimum height and released nearly overhead, straight through the player's air
    /// defence: the Alkyons that "suicide dived" into the player's base. So the run keeps the cruise
    /// height the commander chose for the situation (<c>GetCruiseHeight</c>), but never climbs in the
    /// run: the floor is at most the height the aircraft already has. Forcing a climb mid-run is what
    /// bled a loaded Brawler from 184 to 51 m/s with its nose 96 degrees off the target. Guns and
    /// missiles are left alone; a gun run has to dive.
    /// </summary>
    private static void ApplyEnemyBombingFloor(AIPilotCombatModes state, Aircraft aircraft, AirMission mission)
    {
        // Any bomb carrier on a surface target, not only one whose current store is the bomb: the target
        // is handed over at takeoff, so the no-target cruise height would never apply. GovernClimb keeps
        // a slow airframe from bleeding speed in the climb.
        // A missile carrier is held too while its target is out of reach (the transit leg); the base
        // game flies that at minimum height and into hills.
        // A guided missile is held for the whole attack, not only the leg in, so it sees the target
        // from standoff range instead of running in low over the air defence.
        WeaponInfo? weapon = CurrentWeaponInfoRef(state);
        bool inTransit = IsInTransit(state);
        if ((weapon != null && weapon.gun)
            || CurrentTargetRef(state) is Aircraft or Missile
            || mission.TargetAltitude <= 0f
            || IsPlayerMission(mission)
            || !(inTransit || HasBombAmmo(aircraft, glideOnly: false) || IsGuidedMissile(weapon)))
        {
            return;
        }

        float floor = GovernClimb(aircraft, mission.TargetAltitude);
        if (TargetHeightRef(state) < floor)
        {
            TargetHeightRef(state) = floor;
        }
    }

    /// <summary>
    /// The height to put a point at so the nose goes to the climb wanted. With terrain following off,
    /// <c>AutoAim</c> flies the straight line to the point, so the lift has to grow with distance: a
    /// flat 300 m on a point 40 km off was half a degree, and a set height took twenty minutes to
    /// show at all.
    /// </summary>
    private static float CruisePointHeight(GlobalPosition at, GlobalPosition point, float heightError)
    {
        float distance = FastMath.Distance(new GlobalPosition(point.x, at.y, point.z), at);
        return at.y + (Mathf.Max(distance, 1000f) * MaxCruiseSlope * Mathf.Clamp(heightError / RoutePitchMeters, -1f, 1f));
    }

    private static bool IsGuidedMissile(WeaponInfo? weapon)
    {
        return weapon != null && weapon.missile && !weapon.boresight && !weapon.gun && !weapon.glideBomb && !weapon.bomb;
    }

    /// <summary>The pilot holds a target still out of its current store's reach: the leg in, not the attack.</summary>
    private static bool IsInTransit(AIPilotCombatModes state)
    {
        WeaponInfo? weapon = CurrentWeaponInfoRef(state);
        return weapon != null
            && weapon.targetRequirements.maxRange > 0f
            && TargetDistRef(state) > weapon.targetRequirements.maxRange;
    }

    /// <summary>
    /// The player's own aircraft attacking a surface target with anything but a gun. The altitude the
    /// player ordered comes first, always (the base game takes 10 m off the height every second it
    /// has a target, so an order of 8 km was a dive into the target's air defence). With no order a
    /// glide bomber simply keeps the height it has, since a glide bomb reaches further from higher
    /// up; nothing else is held. A gun run still dives, it has to.
    /// </summary>
    private static void ApplyPlayerAttackFloor(AIPilotCombatModes state, Aircraft aircraft, float altitude)
    {
        WeaponInfo? weapon = CurrentWeaponInfoRef(state);
        if (weapon == null
            || weapon.gun
            || CurrentTargetRef(state) is Aircraft or Missile)
        {
            return;
        }

        float floor = altitude > 0f
            ? GovernClimb(aircraft, altitude)
            : weapon.glideBomb ? aircraft.radarAlt : 0f;
        if (TargetHeightRef(state) < floor)
        {
            TargetHeightRef(state) = floor;
        }
    }

    /// <summary>
    /// A commanded aircraft with missiles on a surface target circles at standoff range between
    /// shots instead of flying at the target. The base game's <c>FlyToTarget</c> keeps closing
    /// while its missiles are in the air (it only fires again once they are spent), so an Ifrit
    /// that could hit from far out ended up over the target's air defence. The firing pass itself
    /// (<c>UsingMissiles</c>) is left alone: it turns in, fires, and comes back to this.
    /// Not for glide bombs: <c>UseGlideBombs</c> releases from <c>FlyingToTarget</c> only with the
    /// nose on the target, so circling inside the ring meant never releasing at all.
    /// </summary>
    private static void HoldMissileStandoff(AIPilotCombatModes state, FieldInfo? destinationField)
    {
        if (Instance == null || Instance.missions.Count == 0 || destinationField == null || AttackModeField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        Unit? target = CurrentTargetRef(state);
        WeaponInfo? weapon = CurrentWeaponInfoRef(state);

        // An obscured target releases the hold so the aircraft can close until it sees it. An AI
        // airframe still climbing to its cruise height holds instead: the climb is what brings the
        // target into view, and closing at low level flies it into the air defence.
        if (aircraft == null
            || target == null
            || target is Aircraft or Missile
            || weapon == null
            || !weapon.missile
            || weapon.glideBomb
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || (!weapon.overHorizon
                && TargetObscuredRef(state)
                && (IsPlayerMission(mission) || aircraft.radarAlt >= mission.TargetAltitude * 0.9f))
            || Convert.ToInt32(AttackModeField.GetValue(state), CultureInfo.InvariantCulture) != FlyingToTargetMode
            || aircraft.NetworkHQ == null
            || !aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition targetPosition))
        {
            return;
        }

        float standoff = weapon.targetRequirements.maxRange * MissileStandoffFraction;
        float distance = TargetDistRef(state);
        if (distance >= standoff || standoff <= 0f)
        {
            return;
        }

        Vector3 away = aircraft.GlobalPosition() - targetPosition;
        away.y = 0f;
        if (away.sqrMagnitude < 1f)
        {
            away = aircraft.transform.forward;
            away.y = 0f;
        }

        away.Normalize();

        // Round the circle the way the aircraft is already turning, pushed outward the deeper inside
        // the standoff ring it is.
        Vector3 tangent = Vector3.Cross(Vector3.up, away);
        if (aircraft.rb != null && Vector3.Dot(tangent, aircraft.rb.velocity) < 0f)
        {
            tangent = -tangent;
        }

        Vector3 heading = (tangent + (away * (2f * (1f - (distance / standoff))))).normalized;
        CommanderAirCommandPatches.DestinationRef(state) = aircraft.GlobalPosition() + (heading * 2000f);
    }

    /// <summary>
    /// An enemy free-fall bomber circles <see cref="BomberClimbRingMeters"/> out until it has half its
    /// cruise height. The bomb run starts inside <c>radar altitude x 2 + 5 km</c> of the target, so on
    /// a map with close bases a bomber would reach that ring still low and climbing.
    /// </summary>
    private static void HoldBomberClimb(AIPilotCombatModes state)
    {
        if (Instance == null || AttackModeField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        Unit? target = CurrentTargetRef(state);
        WeaponInfo? weapon = CurrentWeaponInfoRef(state);
        if (aircraft == null
            || target == null
            || target is Aircraft or Missile
            || weapon == null
            || !(weapon.bomb || weapon.glideBomb)
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || IsPlayerMission(mission))
        {
            return;
        }

        float ring = weapon.glideBomb ? GlideClimbRingMeters : BomberClimbRingMeters;
        float climbTo = weapon.glideBomb
            ? Mathf.Min(GlideReleaseMeters, mission.TargetAltitude * 0.9f)
            : mission.TargetAltitude * BomberClimbShare;

        // Circling keeps the nose off the target, and UseGlideBombs only releases inside its
        // minAlignment, so nothing goes until the climb is done.
        int attackMode = Convert.ToInt32(AttackModeField.GetValue(state), CultureInfo.InvariantCulture);
        if (aircraft.radarAlt >= climbTo
            || TargetDistRef(state) >= ring
            || !(attackMode == FlyingToTargetMode || (weapon.glideBomb && attackMode == GlideBombingMode))
            || aircraft.NetworkHQ == null
            || !aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition targetPosition))
        {
            return;
        }

        Vector3 away = aircraft.GlobalPosition() - targetPosition;
        away.y = 0f;
        if (away.sqrMagnitude < 1f)
        {
            away = -aircraft.transform.forward;
            away.y = 0f;
        }

        // Straight back out, then round the ring: the same steering HoldMissileStandoff uses.
        away.Normalize();
        Vector3 tangent = Vector3.Cross(Vector3.up, away);
        if (aircraft.rb != null && Vector3.Dot(tangent, aircraft.rb.velocity) < 0f)
        {
            tangent = -tangent;
        }

        Vector3 heading = (tangent + (away * (2f * (1f - (TargetDistRef(state) / ring))))).normalized;
        CommanderAirCommandPatches.DestinationRef(state) = aircraft.GlobalPosition() + (heading * 2000f);
    }

    /// <summary>
    /// Which store answers a target the player pointed at. Two rules, so an aircraft inside the
    /// reach of its stand-off munitions fires them.
    /// <para>
    /// The mission's weapon filter does not apply to a commanded target. CAS excludes strategic stores
    /// and strategic strike excludes the rest, so a mixed load could only ever use half. Nuclear and
    /// jammer stores stay excluded.
    /// </para>
    /// <para>
    /// Reach decides before effectiveness. A store that reaches the target from here wins, longest
    /// reach first; among those, punch breaks the tie. If nothing reaches yet, the longest-ranged one,
    /// so the aircraft closes only to the first release. Picked again every tick, so the aeroplane
    /// works down its load as range closes and racks empty.
    /// </para>
    /// </summary>
    private static WeaponStation? PickForcedStation(Aircraft aircraft, List<WeaponStation> stations, AirMission mission)
    {
        bool airTarget = mission.ForcedTarget is Aircraft or Missile;

        // The faction's own reading, not the unit's: a contact the radar has lost is attacked at
        // where it was last seen, which is the same position the attack run itself flies to.
        float targetRange = float.MaxValue;
        if (mission.ForcedTarget != null)
        {
            GlobalPosition targetPosition =
                aircraft.NetworkHQ != null
                && aircraft.NetworkHQ.TryGetKnownPosition(mission.ForcedTarget, out GlobalPosition known)
                    ? known
                    : mission.ForcedTarget.GlobalPosition();
            targetRange = FastMath.Distance(targetPosition, aircraft.GlobalPosition());
        }

        WeaponStation? best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station == null || station.Cargo || station.Ammo <= 0)
            {
                continue;
            }

            // The base game's own armour rule (CombatAI.AnalyzeTarget): a weapon rated below the
            // target's armour tier does nothing to it. This path skips AnalyzeTarget, which is how
            // the enemy's jets emptied their missiles into a munitions bunker. An Eyeball carries
            // the AGM-48's numbers, so its 10 km reach won this pick for the player's aircraft too:
            // a Brawler sent at a radar station flew a missile run with a camera and broke off for
            // its home field after every launch. FIRE still launches one on request.
            WeaponInfo info = station.WeaponInfo;
            if (info == null
                || (info.nuclear && IsPlayerMission(mission))
                || info.jammer
                || CommanderFireNow.IsSensorStore(info)
                || !CanDefeatArmor(info, mission.ForcedTarget))
            {
                continue;
            }

            // Anti-radar counts as surface punch: an ARAD missile aimed at a SAM can rate near
            // zero on antiSurface, and filtering on that alone would drop the one store that is
            // right for the job.
            float reach = info.targetRequirements.maxRange;
            float punch = airTarget
                ? info.effectiveness.antiAir
                : Mathf.Max(info.effectiveness.antiSurface, info.effectiveness.antiRadar);

            // Reach outranks punch below, so stores that cannot hurt this class of target are dropped before
            // scoring. Guns stay in as a last resort.
            if (!info.gun && (punch <= MinForcedPunch || (!airTarget && info.effectiveness.antiAir > punch)))
            {
                continue;
            }

            // Reach is metres and punch is a 0-1 rating, so the two are kept in separate bands
            // rather than added: in range at all outranks everything, then reach, then punch.
            float score = (reach >= targetRange ? 1e9f : 0f) + reach + (punch * 100f) + (info.gun ? -50f : 0f);
            if (score > bestScore)
            {
                bestScore = score;
                best = station;
            }
        }

        return best;
    }

    /// <summary>Current travel point, advancing the route as the aircraft reaches each one.</summary>
    private static GlobalPosition GetActiveRoutePoint(Aircraft aircraft, AirMission mission)
    {
        bool wrapped = false;
        while (mission.RouteIndex < mission.Route.Count)
        {
            // Travel points sit on the ground. Measured in 3D, an aircraft at height never comes within
            // the radius, and steering at the point itself flies it into the terrain. Reached is
            // horizontal, and the point is lifted to the height wanted.
            GlobalPosition point = mission.Route[mission.RouteIndex];
            GlobalPosition at = aircraft.GlobalPosition();
            if (FastMath.Distance(new GlobalPosition(point.x, at.y, point.z), at) > AircraftWaypointRadiusMeters)
            {
                if (mission.TargetAltitude >= 0f)
                {
                    float wanted = mission.TargetAltitude > 0f ? GovernClimb(aircraft, mission.TargetAltitude) : aircraft.radarAlt;
                    point.y = CruisePointHeight(at, point, wanted - aircraft.radarAlt);
                }

                return point;
            }

            mission.RouteIndex++;

            // Once per call: every point inside the radius would otherwise wrap forever.
            if (!wrapped)
            {
                wrapped = mission.TryWrapRoute();
            }
        }

        return mission.AreaCenter;
    }

    /// <summary>
    /// The nearest surface unit this airframe has a weapon for, skipping any it has gone to look at
    /// and not found. <c>ChooseHQTarget</c> only takes a target whose position is still good, so a
    /// convoy that moved on, or a building the gun cannot hurt, would otherwise be a spot it flies
    /// to and hovers over forever, the nearest "enemy" and never a target.
    /// </summary>
    private static bool TryFindHuntPoint(Aircraft aircraft, FactionHQ hq, out GlobalPosition point)
    {
        point = default;
        bool found = false;
        float nearest = float.MaxValue;
        GlobalPosition from = aircraft.GlobalPosition();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (!tracking.TryGetUnit(out Unit unit)
                || unit.disabled
                || unit.NetworkHQ == null
                || unit.NetworkHQ == hq
                || unit is Aircraft
                || unit is Missile)
            {
                continue;
            }

            float distance = FastMath.Distance(tracking.lastKnownPosition, from);
            if (distance < nearest
                && (distance > AutopilotSearchedMeters || hq.IsTargetPositionAccurate(unit, 1000f))
                && CanAttack(aircraft, tracking, distance))
            {
                nearest = distance;
                point = tracking.lastKnownPosition;
                found = true;
            }
        }

        if (found)
        {
            return true;
        }

        // Nothing it can hit is known: go and find some at the enemy's nearest base.
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || airbase.CurrentHQ == null
                || airbase.CurrentHQ == hq)
            {
                continue;
            }

            float distance = FastMath.Distance(airbase.center.GlobalPosition(), from);
            if (distance < nearest)
            {
                nearest = distance;
                point = airbase.center.GlobalPosition();
                found = true;
            }
        }

        return found || MissionPosition.TryGetClosestPosition(aircraft, out point);
    }

    private static bool CanAttack(Aircraft aircraft, TrackingInfo tracking, float distance)
    {
        foreach (WeaponStation station in aircraft.weaponStations)
        {
            if (IsCombatStation(station) && CombatAI.AnalyzeTarget(station, aircraft, tracking, 0f, distance, 100f).opportunity > 0f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCombatAmmo(Aircraft aircraft)
    {
        foreach (WeaponStation station in aircraft.weaponStations)
        {
            if (IsCombatStation(station))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCombatStation(WeaponStation station)
    {
        return station != null && !station.Cargo && station.Ammo > 0 && station.WeaponInfo != null && !station.WeaponInfo.sling;
    }

    private static CombatAI.TargetSearchResults ChooseMissionTarget(
        Aircraft aircraft,
        List<WeaponStation> stations,
        AirMission mission)
    {
        Unit? bestTarget = null;
        WeaponStation? bestStation = null;
        float bestOpportunity = 0f;
        float bestScore = 0f;
        bool outOfAmmo = mission.Mode != AirCommandMode.AwacsJammer || aircraft.radar == null;
        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq == null)
        {
            return new CombatAI.TargetSearchResults(null!, null!, 0f, true);
        }

        // Dead target: step to the next one the player queued behind it before falling back to the
        // mission's own search, or a package sent at three things stopped after the first. The
        // mission area moves with it, because that box is what clamps the attack destination once
        // the route is flown — left on the first target, anything queued beyond the mission radius
        // would be clamped out of reach.
        if (mission.ForcedTarget != null && mission.ForcedTarget.disabled && mission.TakeNextTarget())
        {
            mission.AreaCenter = mission.ForcedTarget!.GlobalPosition();
            DestroyMissionMapVisual(mission);
        }

        // A commanded target is an order, not a candidate. Scoring it 1000x below is not enough on
        // its own: AnalyzeTarget can score a target at zero for the weapon in hand (wrong class,
        // out of envelope), and zero times a thousand is still zero — so the loop dropped it and the
        // aircraft went hunting something else. IsTargetPositionAccurate rejected it too, for a
        // target the player can see and the faction has not pinned down. Answered here instead.
        if (mission.ForcedTarget != null && PickForcedStation(aircraft, stations, mission) is WeaponStation forcedStation)
        {
            return new CombatAI.TargetSearchResults(mission.ForcedTarget, forcedStation, 1f, false);
        }

        // HOLD FIRE: the commanded target above is all it goes after, and only the FIRE button shoots.
        // Not out of ammo, or it flies home to rearm with full racks.
        if (CommanderMoveService.IsHoldingFire(aircraft))
        {
            return new CombatAI.TargetSearchResults(null!, null!, 0f, false);
        }

        for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
        {
            WeaponStation station = stations[stationIndex];

            // An AI commander's GPO-N carrier was armed with it on purpose (BuildThemedLoadout, which
            // only offers one when the faction holds warheads); every other mission keeps nuclear
            // stores out of the hunt.
            bool aiNuke = station?.WeaponInfo != null && station.WeaponInfo.nuclear && !IsPlayerMission(mission);
            if (station == null || station.Cargo || !(aiNuke || IsStationEligible(station, mission.Mode)))
            {
                continue;
            }

            // Eyeballs are launched to look (TryLaunchAiScout, or the player's FIRE), not picked as an AGM.
            if (CommanderFireNow.IsSensorStore(station.WeaponInfo))
            {
                continue;
            }

            if (station.Ammo <= 0)
            {
                continue;
            }

            outOfAmmo = false;

            // The area says where the aircraft flies, not how far its weapons reach: a cruise or
            // ballistic missile may take anything in its range from inside the circle. The aircraft itself is still clamped to the area by ConstrainMissionDestination.
            float strategicReach = IsStrategicStrikeWeapon(station.WeaponInfo)
                ? station.WeaponInfo.targetRequirements.maxRange
                : 0f;

            // RADAR's anti-radiation missiles are ARAD's: surface emitters only.
            bool surfaceOnly = mission.Mode == AirCommandMode.AwacsJammer && IsAradWeapon(station.WeaponInfo);
            foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
            {
                TrackingInfo tracking = entry.Value;
                if (!tracking.TryGetUnit(out Unit target)
                    || target == null
                    || target.disabled
                    || target.NetworkHQ == null
                    || target.NetworkHQ == hq
                    || (surfaceOnly && target is Aircraft)
                    || !IsTargetEligible(target, mission.Mode, mission.TargetOrdnance)
                    || (TargetsRestrictedToMissionArea(mission.Mode)
                        && !ReferenceEquals(target, mission.ForcedTarget)
                        && !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)
                        && !FastMath.InRange(tracking.GetPosition(), aircraft.GlobalPosition(), strategicReach)))
                {
                    continue;
                }

                float range = Mathf.Max(FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition()), 100f);
                if (mission.Mode == AirCommandMode.AirGuard
                    && range > station.WeaponInfo.targetRequirements.maxRange * 1.05f)
                {
                    continue;
                }

                if (mission.Mode == AirCommandMode.AwacsJammer
                    && (range > station.WeaponInfo.targetRequirements.maxRange
                        || !target.LineOfSight(aircraft.transform.position, 1000f)))
                {
                    continue;
                }

                OpportunityThreat assessment = CombatAI.AnalyzeTarget(
                    station,
                    aircraft,
                    tracking,
                    0f,
                    range,
                    maxRangeMultiplier: 100f);
                float score = assessment.GetCombinedScore() / range;
                if (ReferenceEquals(target, mission.ForcedTarget))
                {
                    // A commanded target outranks anything the automatic search finds.
                    score *= 1000f;
                }

                float requiredAccuracy = mission.Mode == AirCommandMode.AwacsJammer ? 100f : 1000f;
                if (score <= bestScore || !hq.IsTargetPositionAccurate(target, requiredAccuracy))
                {
                    continue;
                }

                // Keep out of known ground fire. The base game's own ChooseHQTarget drops a target
                // whose threat beats the pilot's bravery, and this replacement had no threat test at
                // all, so a jet with a box over an enemy base took the first thing in it and strafed
                // it through every gun and SAM there. A surface target is only taken when this station
                // reaches it from outside the known ground weapons around it. Checked last, on the few
                // candidates that would become the best, because it walks the database itself.
                if (target is not Aircraft
                    && target is not Missile
                    && !mission.IgnoreGroundThreats
                    && CombatAI.GetSafeStandoffDist(tracking.GetPosition(), hq)
                        > station.WeaponInfo.targetRequirements.maxRange)
                {
                    continue;
                }

                bestScore = score;
                bestOpportunity = assessment.opportunity;
                bestTarget = target;
                bestStation = station;
            }
        }

        return new CombatAI.TargetSearchResults(bestTarget!, bestStation!, bestOpportunity, outOfAmmo);
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission)
            && mission.PurchasedWithFunds
            && mission.Intent != LandingIntent.Reserve)
        {
            // Basegame ReturnToInventory has just restored one airframe. Convert
            // that temporary purchased airframe back into its original funds.
            mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
            mission.Hq.AddFunds(mission.PurchaseCost);
        }

        RemoveMission(aircraft);
    }
}
