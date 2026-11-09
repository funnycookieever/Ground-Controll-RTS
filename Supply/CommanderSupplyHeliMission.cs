using System;
using System.Collections;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>False for a cargo run launched with "Follow roads" off.</summary>
    internal static bool FollowsRoads(Aircraft aircraft)
    {
        return Instance == null
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive
            || mission.FollowRoads;
    }

    private static int GetSamSiteId(string supportSummary)
    {
        int siteId = ParseFoundationSiteId(supportSummary);
        if (siteId < 0)
        {
            siteId = ParseCargoSiteId(supportSummary);
        }

        if (siteId < 0)
        {
            siteId = ParseJacknifeSiteId(supportSummary);
        }

        return siteId;
    }

    private static int GetSamSiteId(CargoMission mission)
    {
        if (mission.FoundationSiteId >= 0)
        {
            return mission.FoundationSiteId;
        }

        if (mission.DepositSiteId >= 0)
        {
            return mission.DepositSiteId;
        }

        return mission.JacknifeSiteId;
    }

    private static Airbase? ResolveSpawnAirbase(QueuedCargoSpawn request, FactionHQ hq)
    {
        bool protectedSamRun = RequiresProtectedSamAirbase(request.SupportSummary);
        if (IsAvailableAirbase(request.RequestedAirbase, hq, request.Aircraft.Definition)
            && (!protectedSamRun
                || !request.UseOtherAirfields
                || IsSamSupplyAirbaseSafe(request.RequestedAirbase, request.Target)))
        {
            return request.RequestedAirbase;
        }

        if (!request.UseOtherAirfields)
        {
            return null;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        Airbase? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (!IsAvailableAirbase(candidate, hq, request.Aircraft.Definition))
            {
                continue;
            }

            if (protectedSamRun && !IsSamSupplyAirbaseSafe(candidate, request.Target))
            {
                continue;
            }

            Transform positionTransform = candidate.center != null ? candidate.center : candidate.transform;
            float distance = Vector3.SqrMagnitude(positionTransform.position - cameraPosition);
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static void IssueSupplyReturnToBase(Aircraft? aircraft, CargoMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return;
        }

        CloseCargoDoors(mission);
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            pilot.AILandingState ??= new AIPilotLandingState();
            pilot.SwitchState(pilot.AILandingState);
            return;
        }
    }

    /// <summary>
    /// The pilot types on an airframe, for the log. `Pilot.pilotType` is serialized prefab data, so
    /// which flight AI an aircraft gets (and whether the mod can steer it) is only known at runtime.
    /// </summary>
    private static string DescribePilotTypes(Aircraft aircraft)
    {
        if (aircraft.pilots == null || aircraft.pilots.Length == 0)
        {
            return "none";
        }

        System.Text.StringBuilder types = new();
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            if (aircraft.pilots[i] == null)
            {
                continue;
            }

            if (types.Length > 0)
            {
                types.Append('+');
            }

            types.Append(aircraft.pilots[i].pilotType);
        }

        return types.Length > 0 ? types.ToString() : "none";
    }

    private static IEnumerator ReleaseSamJacknifeAfterUnloadDelay(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float releaseAt = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < releaseAt)
        {
            vehicle.SetHoldPosition(true);
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle == null || vehicle.disabled)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        NotifyFoundationCargoActivated(mission, vehicle);

        float clearance = aircraft != null
            ? Mathf.Max(12f, aircraft.maxRadius + vehicle.maxRadius + 2f)
            : 12f;
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (aircraft != null
            && vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                vehicle.transform.position) < clearance)
        {
            yield return new WaitForSeconds(0.25f);
        }

        mission.CargoClearancePending = false;
    }

    private static void NotifyFoundationCargoActivated(CargoMission mission, Unit? cargo)
    {
        if (cargo == null || cargo.disabled)
        {
            return;
        }

        // Every unload path ends here with the vehicle stopped and on hold. It is based where it was set
        // down, like a sling lift's load. A builder given a blueprint on the way carries on to it off
        // road, since the roads near a shore blueprint are often not connected to the LZ.
        // An AI builder's send decides off road by distance itself (SendBuilder), so it is not left on.
        if (cargo is GroundVehicle vehicle
            && CommanderRepairService.IsRepairUnit(cargo)
            && !CommanderEnemyCommanderService.IsAiFaction(cargo.NetworkHQ, CommanderGameAccess.GetLocalHq()))
        {
            CommanderDirectPathService.ForceEnabled(vehicle);
        }

        CommanderRepairService.Instance?.SetHome(cargo, cargo.GlobalPosition());
        CommanderEconomyService.ResumeBuilder(cargo);

        if (mission.FoundationSiteId >= 0)
        {
            CommanderSamSiteService.NotifyFoundationCargoActivated(
                mission.FoundationSiteId,
                cargo);
        }
        else if (mission.JacknifeSiteId >= 0)
        {
            CommanderSamSiteService.NotifySiteJacknifeActivated(
                mission.JacknifeSiteId,
                cargo);
        }
    }

    private static void HoldLandingTimerWhileCargoProcesses(
        AIHeloTransportState state,
        Aircraft aircraft,
        CargoMission mission)
    {
        if (mission.Airdrop)
        {
            return;
        }

        bool waitingForReleasedCargo = mission.ActivatedCargoCount < mission.ReleasedCargoCount;
        bool moreCargoAtThisLz = HasDeployableCargo(aircraft)
            || mission.ReleasedCargoCount < mission.ExpectedCargoLoads;
        if (waitingForReleasedCargo || moreCargoAtThisLz || mission.CargoClearancePending)
        {
            TouchedDownTimeField?.SetValue(state, 0f);
        }
    }

    private static IEnumerator ClearGroundVehicleFromRamp(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (vehicle != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }

        if (vehicle == null || aircraft == null
            || !TryFindCargoClearanceDirection(aircraft, vehicle, 10f, out Vector3 direction))
        {
            if (vehicle != null)
            {
                vehicle.SetHoldPosition(true);
                NotifyFoundationCargoActivated(mission, vehicle);
            }

            mission.CargoClearancePending = false;
            yield break;
        }

        GlobalPosition clearPoint = (vehicle.transform.position + (direction * 6f)).ToGlobalPosition();
        vehicle.SetHoldPosition(false);
        vehicle.UnitCommand?.SetDestination(clearPoint, playerCommand: true);
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(vehicle.transform.position, clearPoint.ToLocalPosition()) > 1.25f)
        {
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle != null && !vehicle.disabled)
        {
            vehicle.SetHoldPosition(true);
            vehicle.UnitCommand?.SetDestination(vehicle.GlobalPosition(), playerCommand: false);
            NotifyFoundationCargoActivated(mission, vehicle);
        }

        mission.CargoClearancePending = false;
    }

    private static IEnumerator ClearStaticCargoFromRamp(Aircraft aircraft, Unit cargo, CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (cargo != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }

        if (cargo == null || aircraft == null)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        if (!TryFindCargoClearanceDirection(aircraft, cargo, 10f, out Vector3 direction))
        {
            CommanderPlugin.Log.LogWarning(
                $"Supply cargo could not find 10m ramp clearance: {CommanderGameAccess.GetUnitLabel(cargo)}");
            NotifyFoundationCargoActivated(mission, cargo);
            mission.CargoClearancePending = false;
            yield break;
        }

        // Global: the camera can shift the floating origin during the glide, and a local start would
        // then put the load that far off, usually in the air.
        GlobalPosition start = cargo.GlobalPosition();
        Vector3 move = direction.normalized * 6f;
        const float moveDuration = 5f;
        float startedAt = Time.timeSinceLevelLoad;
        while (cargo != null && Time.timeSinceLevelLoad - startedAt < moveDuration)
        {
            float t = Mathf.SmoothStep(0f, 1f, (Time.timeSinceLevelLoad - startedAt) / moveDuration);
            Vector3 position = (start + (move * t)).ToLocalPosition();
            if (cargo.rb != null)
            {
                cargo.rb.MovePosition(position);
                cargo.rb.velocity = Vector3.zero;
                cargo.rb.angularVelocity = Vector3.zero;
            }
            else
            {
                cargo.transform.position = position;
            }

            yield return new WaitForFixedUpdate();
        }

        // Zero velocity for 5 s reads as landed, so a container froze itself partway through the glide
        // (Container.FixedUpdate). Let it settle again from where the flat glide ended, over a slope too.
        if (cargo is Container container && container != null)
        {
            container.Networkkinematic = false;
        }

        NotifyFoundationCargoActivated(mission, cargo);
        mission.CargoClearancePending = false;
    }

    private static bool TryFindCargoClearanceDirection(
        Aircraft aircraft,
        Unit cargo,
        float scanDistance,
        out Vector3 direction)
    {
        Vector3 rear = -aircraft.transform.forward;
        rear.y = 0f;
        rear = rear.sqrMagnitude > 0.01f ? rear.normalized : Vector3.back;
        Vector3[] candidates =
        {
            Quaternion.AngleAxis(-70f, Vector3.up) * rear,
            Quaternion.AngleAxis(70f, Vector3.up) * rear,
            rear,
        };
        Vector3 origin = cargo.transform.position + (Vector3.up * Mathf.Max(cargo.maxRadius * 0.25f, 0.5f));
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 candidate = candidates[i].normalized;
            if (!Physics.Raycast(origin, candidate, scanDistance, 2112, QueryTriggerInteraction.Ignore))
            {
                direction = candidate;
                return true;
            }
        }

        direction = Vector3.zero;
        return false;
    }

    private static bool TryGetApproachRouteTarget(
        Aircraft aircraft,
        CargoMission mission,
        out GlobalPosition target)
    {
        const float waypointReachDistance = 400f;
        target = default;
        while (mission.ApproachRouteIndex < mission.ApproachRoute.Count)
        {
            GlobalPosition waypoint = mission.ApproachRoute[mission.ApproachRouteIndex];
            Vector3 previous = GetPreviousRoutePoint(mission).ToLocalPosition();
            Vector3 waypointPosition = waypoint.ToLocalPosition();
            if (CommanderGameAccess.HorizontalDistance(aircraft.transform.position, waypointPosition) > waypointReachDistance
                && !HasPassedRoutePoint(aircraft.transform.position, previous, waypointPosition))
            {
                break;
            }

            mission.ApproachRouteIndex++;
        }

        if (mission.ApproachRouteIndex < mission.ApproachRoute.Count)
        {
            target = mission.ApproachRoute[mission.ApproachRouteIndex];
            return true;
        }

        return false;
    }

    private static GlobalPosition GetTurnAnticipationTarget(
        Aircraft aircraft,
        CargoMission mission,
        GlobalPosition currentWaypoint)
    {
        int nextIndex = mission.ApproachRouteIndex + 1;
        if (nextIndex >= mission.ApproachRoute.Count)
        {
            return currentWaypoint;
        }

        float distance = CommanderGameAccess.HorizontalDistance(
            aircraft.transform.position,
            currentWaypoint.ToLocalPosition());
        float turnLead = Mathf.Clamp(aircraft.speed * 8f, 500f, 1600f);
        if (distance >= turnLead)
        {
            return currentWaypoint;
        }

        float blend = Mathf.InverseLerp(turnLead, 0f, distance) * 0.75f;
        GlobalPosition nextWaypoint = mission.ApproachRoute[nextIndex];
        return new GlobalPosition(
            Mathf.Lerp(currentWaypoint.x, nextWaypoint.x, blend),
            Mathf.Lerp(currentWaypoint.y, nextWaypoint.y, blend),
            Mathf.Lerp(currentWaypoint.z, nextWaypoint.z, blend));
    }

    private static GlobalPosition GetPreviousRoutePoint(CargoMission mission)
    {
        if (mission.ApproachRouteIndex > 0)
        {
            return mission.ApproachRoute[mission.ApproachRouteIndex - 1];
        }

        Transform origin = mission.OriginAirbase.center != null ? mission.OriginAirbase.center : mission.OriginAirbase.transform;
        return origin.GlobalPosition();
    }

    private static bool HasPassedRoutePoint(Vector3 aircraft, Vector3 previous, Vector3 waypoint)
    {
        Vector3 segment = waypoint - previous;
        Vector3 beyondWaypoint = aircraft - waypoint;
        segment.y = 0f;
        beyondWaypoint.y = 0f;
        return segment.sqrMagnitude > 1f && Vector3.Dot(segment, beyondWaypoint) >= 0f;
    }

    private static bool TryConsumeVirtualSamCargo(
        Aircraft aircraft,
        WeaponStation station,
        MountedCargo cargo,
        CargoMission mission,
        Pilot pilot)
    {
        if (MountedCargoRemoveMethod == null)
        {
            return false;
        }

        float supply = GetCargoSupply(cargo.cargo);
        if (supply <= 0f)
        {
            return false;
        }

        if (mission.FoundationSiteId >= 0)
        {
            CommanderSamSiteService.NotifyFoundationAmmunitionDelivered(
                mission.FoundationSiteId,
                supply);
        }
        else if (mission.DepositSiteId >= 0)
        {
            CommanderSamSiteService.TryDepositAmmunitionAmount(
                mission.DepositSiteId,
                supply,
                out _);
        }

        aircraft.weaponManager.currentWeaponStation = station;
        station.LaunchMount(aircraft, null!, default);
        MountedCargoRemoveMethod.Invoke(cargo, null);
        UnityEngine.Object.Destroy(cargo.gameObject);
        mission.ReleasedCargoCount++;
        mission.ActivatedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + 1f;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        UpdateDeliveryCompleted(aircraft, mission);
        return true;
    }

    private static bool IsJacknifeCargoDefinition(UnitDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        string identity = $"{definition.unitName} {definition.code} {definition.jsonKey}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || definition.unitPrefab?.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool IsSamLogisticsMission(CargoMission mission)
    {
        return mission.FoundationSiteId >= 0
            || mission.DepositSiteId >= 0
            || mission.JacknifeSiteId >= 0;
    }

    private static void OpenCargoDoors(Aircraft aircraft, CargoMission mission)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation station = aircraft.weaponStations[stationIndex];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = station.Weapons[weaponIndex];
                if (weapon != null && WeaponHardpointField?.GetValue(weapon) is Hardpoint hardpoint)
                {
                    hardpoint.SpringOpenBayDoors();
                    for (int doorIndex = 0; doorIndex < hardpoint.bayDoors.Length; doorIndex++)
                    {
                        BayDoor door = hardpoint.bayDoors[doorIndex];
                        if (door != null && !mission.CargoDoors.Contains(door))
                        {
                            mission.CargoDoors.Add(door);
                        }
                    }
                }
            }
        }
    }

    private static bool CloseCargoDoors(CargoMission mission)
    {
        bool closed = true;
        for (int i = mission.CargoDoors.Count - 1; i >= 0; i--)
        {
            BayDoor door = mission.CargoDoors[i];
            if (door == null)
            {
                mission.CargoDoors.RemoveAt(i);
                continue;
            }

            BayDoorOpenTimerField?.SetValue(door, 0f);
            door.enabled = true;
            float openAmount = BayDoorOpenAmountField?.GetValue(door) is float value ? value : 0f;
            closed &= openAmount <= 0.01f;
        }

        return closed;
    }

    private static void UpdateDeliveryCompleted(Aircraft aircraft, CargoMission mission)
    {
        if (mission.ActivatedCargoCount >= mission.ExpectedCargoLoads
            && !HasDeployableCargo(aircraft)
            && !mission.CargoClearancePending)
        {
            mission.DeliveryCompleted = true;
        }
    }

    private static float GetCargoSupply(Unit cargo)
    {
        float supply = 0f;
        Rearmer[] rearmers = cargo.GetComponentsInChildren<Rearmer>(true);
        for (int i = 0; i < rearmers.Length; i++)
        {
            supply += Mathf.Max(0f, rearmers[i].Capacity);
        }

        return supply;
    }

    private static float GetCargoSupply(UnitDefinition? definition)
    {
        float supply = 0f;
        Rearmer[] rearmers = definition?.unitPrefab != null
            ? definition.unitPrefab.GetComponentsInChildren<Rearmer>(true)
            : Array.Empty<Rearmer>();
        for (int i = 0; i < rearmers.Length; i++)
        {
            supply += Mathf.Max(0f, rearmers[i].Capacity);
        }

        return supply;
    }

    /// <summary>
    /// Where a parachuted load actually came down. The release point says nothing about that: the
    /// load carries forward and drifts under its canopy, and how far is prefab data. This line is
    /// what <see cref="RotaryAirdropLeadSeconds"/> is tuned from.
    /// </summary>
    private static IEnumerator LogAirdropLanding(Unit cargo, GlobalPosition target)
    {
        float timeout = Time.timeSinceLevelLoad + 120f;
        yield return new WaitForSeconds(3f);
        while (cargo != null
            && !cargo.disabled
            && Time.timeSinceLevelLoad < timeout
            && cargo.rb != null
            && cargo.rb.velocity.sqrMagnitude > 1f)
        {
            yield return new WaitForSeconds(0.5f);
        }

        if (cargo == null || cargo.disabled)
        {
            CommanderPlugin.Log.LogInfo("An airdropped load was destroyed before it came to rest.");
            yield break;
        }

        CommanderPlugin.Log.LogInfo(
            $"Airdropped {CommanderGameAccess.GetUnitLabel(cargo)} came to rest "
                + $"{CommanderGameAccess.HorizontalDistance(cargo.transform.position, target.ToLocalPosition()):0} m from its LZ.");
    }

    private static void DestroyCargoUnit(Unit cargo)
    {
        if (cargo.Identity != null && NetworkManagerNuclearOption.i?.ServerObjectManager != null)
        {
            NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(
                cargo.Identity,
                !cargo.Identity.IsSceneObject);
        }
    }

    private static bool TrySelectCargoStation(Aircraft aircraft)
    {
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station != null && station.Cargo && station.WeaponInfo != null && station.WeaponInfo.cargo && HasDeployableCargo(station))
            {
                aircraft.weaponManager.currentWeaponStation = station;
                return true;
            }
        }

        return false;
    }

    private static bool TrySelectNextCargoWeapon(
        Aircraft aircraft,
        out WeaponStation station,
        out Weapon cargoWeapon)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation candidate = aircraft.weaponStations[stationIndex];
            if (candidate == null || !candidate.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < candidate.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = candidate.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    station = candidate;
                    cargoWeapon = weapon;
                    return true;
                }
            }
        }

        station = null!;
        cargoWeapon = null!;
        return false;
    }

    private static bool HasDeployableCargo(Aircraft aircraft)
    {
        return CountDeployableCargo(aircraft) > 0;
    }

    private static int CountDeployableCargo(Aircraft aircraft)
    {
        int count = 0;
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon? weapon = station.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    count += weapon.GetAmmoLoaded();
                }
            }
        }

        return count;
    }

    private static bool HasDeployableCargo(WeaponStation station)
    {
        for (int i = 0; i < station.Weapons.Count; i++)
        {
            if (station.Weapons[i] != null && station.Weapons[i].GetAmmoLoaded() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        IReadOnlyList<GlobalPosition> targets,
        bool followRoads = true)
    {
        if (CommanderNetwork.IsGuest)
        {
            ForwardCargoRun(
                aircraftOption,
                cargoLoadout,
                cargoLabel,
                airbase,
                useHighTerrainClearance,
                terrainClearanceMeters,
                useAirdrop,
                useOtherAirfields,
                targets,
                followRoads);
            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return;
        }

        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("The selected airbase no longer supports this aircraft.");
            return;
        }

        QueuedCargoSpawn request = new(
            hq!,
            aircraftOption,
            CloneLoadout(cargoLoadout),
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            targets,
            followRoads);
        Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq!);
        if (spawnAirbase == null
            || pendingAircraftSpawn != null
            || IsSamHelipadBusy(request))
        {
            queuedCargoSpawns.Enqueue(request);
            SetStatus(useOtherAirfields
                ? $"Supply run queued. Waiting for a compatible friendly airfield ({queuedCargoSpawns.Count} queued)."
                : $"Supply run queued for {GetAirbaseLabel(airbase)} ({queuedCargoSpawns.Count} queued).");
            return;
        }

        TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq!);
    }

    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        GlobalPosition target)
    {
        SpawnCargoRun(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            new[] { target });
    }

    private void TryProcessQueuedCargoSpawns()
    {
        if (pendingAircraftSpawn != null || queuedCargoSpawns.Count == 0)
        {
            return;
        }

        if (!CanHostSpawn(out _, out _))
        {
            return;
        }

        int attempts = queuedCargoSpawns.Count;
        while (attempts-- > 0)
        {
            // Each run keeps the faction that asked for it: a multiplayer guest's queue shares this one.
            QueuedCargoSpawn request = queuedCargoSpawns.Dequeue();
            FactionHQ hq = request.Hq;
            if (!IsCompatibleAirbase(request.RequestedAirbase, hq, request.Aircraft.Definition)
                && !request.UseOtherAirfields)
            {
                SetStatus("A queued supply run was cancelled because its airbase is no longer friendly or compatible.");
                continue;
            }

            if (IsSamHelipadBusy(request))
            {
                queuedCargoSpawns.Enqueue(request);
                continue;
            }

            Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq);
            if (spawnAirbase == null)
            {
                queuedCargoSpawns.Enqueue(request);
                continue;
            }

            TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq);
            return;
        }
    }

    /// <summary>
    /// A guest's cargo run: the loadout goes as one jsonKey per pylon, like an Air Command launch,
    /// and the host rebuilds it against the airframe's own options.
    /// </summary>
    private void ForwardCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        bool useOtherAirfields,
        IReadOnlyList<GlobalPosition> targets,
        bool followRoads)
    {
        string[] keys = new string[cargoLoadout.weapons.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = cargoLoadout.weapons[i]?.jsonKey ?? string.Empty;
        }

        List<string> args = new()
        {
            aircraftOption.Definition.jsonKey,
            CommanderNetwork.Key(airbase),
            string.Join(",", keys),
            cargoLabel,
            CommanderNetwork.Flag(useHighTerrainClearance),
            CommanderNetwork.Num(terrainClearanceMeters),
            CommanderNetwork.Flag(useAirdrop),
            CommanderNetwork.Flag(useOtherAirfields),
            CommanderNetwork.Flag(followRoads),
        };
        for (int i = 0; i < targets.Count; i++)
        {
            args.Add(CommanderNetwork.Position(targets[i]));
        }

        if (CommanderNetwork.Forward("cargo", args.ToArray()) && CommanderNetwork.HostHasMod)
        {
            SetStatus($"Supply run with {cargoLabel} launching.");
        }
    }

    /// <summary>The host's half of <see cref="ForwardCargoRun"/>, run with the guest's faction acting.</summary>
    private void HostCargoRun(FactionHQ hq, string[] args)
    {
        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        string key = CommanderNetwork.Arg(args, 1);
        CargoAircraftOption? option = null;
        for (int i = 0; i < aircraftOptions.Count && option == null; i++)
        {
            if (aircraftOptions[i].Definition.jsonKey == key)
            {
                option = aircraftOptions[i];
            }
        }

        if (option == null || !CommanderNetwork.TryAirbase(args, 2, out Airbase airbase) || airbase.CurrentHQ != hq)
        {
            CommanderNotice.Fail("The host could not start that supply run.");
            return;
        }

        // Only mounts the pylon actually offers, and only what this faction and field may carry.
        string[] keys = CommanderNetwork.Arg(args, 3).Split(',');
        Loadout loadout = CreateEmptyLoadout(option.HardpointSets.Length);
        for (int i = 0; i < option.HardpointSets.Length; i++)
        {
            string mountKey = i < keys.Length ? keys[i] : string.Empty;
            if (mountKey.Length == 0 || option.HardpointSets[i]?.weaponOptions == null)
            {
                continue;
            }

            foreach (WeaponMount mount in option.HardpointSets[i].weaponOptions)
            {
                if (mount != null
                    && mount.jsonKey == mountKey
                    && WeaponChecker.MountAllowedHQ(mount, hq)
                    && WeaponChecker.MountAllowedAirbase(mount, airbase))
                {
                    loadout.weapons[i] = mount;
                    break;
                }
            }
        }

        List<GlobalPosition> targets = new();
        for (int i = 10; i < args.Length; i++)
        {
            if (CommanderNetwork.TryPosition(args, i, out GlobalPosition target))
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        SpawnCargoRun(
            option,
            loadout,
            CommanderNetwork.Arg(args, 4),
            airbase,
            CommanderNetwork.Bool(args, 5),
            Mathf.Max(0f, CommanderNetwork.Float(args, 6)),
            CommanderNetwork.Bool(args, 7),
            string.Empty,
            CommanderNetwork.Bool(args, 8),
            targets,
            CommanderNetwork.Bool(args, 9));
    }

    private bool IsSamHelipadBusy(QueuedCargoSpawn request)
    {
        int siteId = GetSamSiteId(request.SupportSummary);
        if (siteId < 0)
        {
            return false;
        }

        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && entry.Value.TargetOverrideActive
                && GetSamSiteId(entry.Value) == siteId)
            {
                return true;
            }
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return false;
        }

        Vector3 target = request.Target.ToLocalPosition();
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not Aircraft aircraft
                || aircraft.disabled)
            {
                continue;
            }

            Vector3 position = aircraft.transform.position;
            if (CommanderGameAccess.HorizontalDistance(position, target) <= 60f
                && Mathf.Abs(position.y - target.y) <= 80f)
            {
                return true;
            }
        }

        return false;
    }

    private void TrySpawnCargoRunAtAirbase(QueuedCargoSpawn request, Airbase airbase, FactionHQ hq)
    {
        CargoAircraftOption aircraftOption = request.Aircraft;

        // The cargo is paid on every launch, stocked airframe or not, like an AIR launch's munitions.
        float cargoCost = IsSamSiteRun(request.SupportSummary) ? 0f : GetCargoCost(aircraftOption.Definition, request.Loadout);
        bool fromStock = hq.GetUnitSupply(aircraftOption.Definition) > 0;
        float airframeCost = fromStock ? 0f : CommanderEconomyService.PriceOf(aircraftOption.Definition);
        if (hq.factionFunds < airframeCost + cargoCost)
        {
            SetStatus(cargoCost > 0f
                ? $"The faction cannot afford this supply aircraft and its {CommanderEconomyService.FundsLabel(cargoCost)} cargo."
                : "The faction cannot afford this supply aircraft.");
            return;
        }

        hq.AddFunds(-cargoCost);
        bool purchased = false;
        if (!fromStock)
        {
            float cost = airframeCost;
            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(aircraftOption.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            aircraftOption.Definition,
            airbase,
            request.CargoLabel,
            request.Target,
            request.HighTerrainClearance,
            request.TerrainClearanceMeters,
            request.Airdrop,
            request.SupportSummary,
            purchased,
            purchased ? CommanderEconomyService.PriceOf(aircraftOption.Definition) : 0f,
            request.Targets,
            Time.unscaledTime + PendingSpawnTimeoutSeconds,
            followRoads: request.FollowRoads);

        AircraftDefinition definition = aircraftOption.Definition;
        int liveryIndex = definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = CloneLoadout(request.Loadout);

        Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(
            null,
            definition,
            new LiveryKey(liveryIndex),
            loadout,
            definition.aircraftParameters.DefaultFuelLevel);

        if (!result.Allowed)
        {
            NotifySamMissionFailed(request.SupportSummary);
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(definition, -1);
                hq.AddFunds(CommanderEconomyService.PriceOf(definition));
            }

            hq.AddFunds(cargoCost);
            SetStatus("The airbase rejected the supply aircraft spawn.");
            return;
        }

        string purchaseLabel = purchased ? " Purchased from faction funds." : string.Empty;
        if (cargoCost > 0f)
        {
            purchaseLabel += $" Cargo cost {CommanderEconomyService.FundsLabel(cargoCost)}.";
        }

        SetStatus($"Spawned {aircraftOption.Label} with {request.CargoLabel}.{purchaseLabel}");
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Definition))
        {
            return;
        }

        CargoMission mission = new(
            pending.Hq,
            pending.CargoLabel,
            pending.HighTerrainClearance,
            pending.TerrainClearanceMeters,
            pending.Airdrop,
            pending.PurchasedWithFunds,
            pending.PurchaseCost,
            CountDeployableCargo(aircraft),
            pending.Targets,
            pending.SupportSummary == SamSiteCargoSupportSummary || ParseCargoSiteId(pending.SupportSummary) >= 0,
            ParseCargoSiteId(pending.SupportSummary),
            ParseFoundationSiteId(pending.SupportSummary),
            ParseJacknifeSiteId(pending.SupportSummary),
            pending.OriginAirbase,
            pending.NavalTarget,
            pending.FollowRoads);
        assignedMissions[aircraft] = mission;
        int cachedRouteSiteId = mission.DepositSiteId >= 0
            ? mission.DepositSiteId
            : mission.JacknifeSiteId;
        if (cachedRouteSiteId >= 0
            && CommanderSamSiteService.TryCopyCachedSupplyRoute(
                cachedRouteSiteId,
                pending.OriginAirbase,
                mission.ApproachRoute,
                out bool cachedSteepLanding))
        {
            mission.RoutePlanned = true;
            mission.SteepLanding = cachedSteepLanding;
        }
        else if (mission.FoundationSiteId >= 0)
        {
            mission.RoutePlanned = CommanderTerrainFlightPlanner.TryBuildRoute(
                aircraft.GlobalPosition(),
                mission.Target,
                Mathf.Max(60f, mission.TerrainClearanceMeters),
                mission.ApproachRoute,
                out bool steepLanding);
            mission.SteepLanding = steepLanding;
        }

        string missionLabel = pending.NavalTarget != null
            ? "Naval Supply"
            : pending.Airdrop
                ? $"Airdrop: {pending.CargoLabel}"
                : $"Cargo Delivery: {pending.CargoLabel}";
        if (CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq()))
        {
            CommanderSelectionService.PinMissionUnit(aircraft, "SUPPLY", missionLabel);
        }

        if (pending.HighTerrainClearance && !TryBindTerrainAutopilot(aircraft, mission))
        {
            pendingTerrainAutopilotBindings.Add(aircraft);
        }

        // A fixed-wing transport never enters AIHeloTransportState, so none of the hooks above will
        // ever fire for it. It is steered as an ordinary Air Command travel order instead, and the
        // drop is run from this service's own tick.
        mission.AssignedAt = Time.timeSinceLevelLoad;
        mission.FixedWing = !CommanderAirCommandService.IsRotaryAirframe(aircraft.definition);
        if (mission.FixedWing)
        {
            mission.Airdrop = true;
            SteerFixedWingToLz(aircraft, mission);
        }

        // One line per run: which half of the feature has it and whether Air Command took it.
        CommanderPlugin.Log.LogInfo(
            $"Cargo run assigned: {CommanderGameAccess.GetUnitLabel(aircraft)} "
                + $"({(mission.FixedWing ? "fixed-wing, airdrop" : "rotary")}, pilots "
                + $"{DescribePilotTypes(aircraft)}), cargo "
                + $"{mission.CargoLabel}, LZ {CommanderGameAccess.HorizontalDistance(aircraft.transform.position, mission.Target.ToLocalPosition()) / 1000f:0.0} km, "
                + (mission.FixedWing
                    ? $"Air Command {(CommanderAirCommandService.Instance?.IsCommandedAircraft(aircraft) == true ? "took it" : "REFUSED IT")}."
                    : "flown by the transport AI."));

        pendingAircraftSpawn = null;
    }

    private bool OverrideTransportTarget(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        if (Mathf.Approximately(mission.LastTransportOverrideFixedTime, Time.fixedTime))
        {
            return true;
        }

        mission.LastTransportOverrideFixedTime = Time.fixedTime;

        float lastCheck = LastLandingSpotCheckField?.GetValue(state) is float value ? value : 0f;
        bool routeNeedsUpdate = mission.RouteTransitActive
            || mission.ApproachRouteIndex < mission.ApproachRoute.Count;
        if (Time.timeSinceLevelLoad - lastCheck < 3f && !routeNeedsUpdate)
        {
            return true;
        }

        if (mission.NavalTarget != null)
        {
            return OverrideNavalSupplyTarget(state, aircraft, mission);
        }

        if (!TrySelectCargoStation(aircraft))
        {
            if (PilotField?.GetValue(state) is Pilot unloadingPilot
                && unloadingPilot.flightInfo.LastCargoDelivery > 0f)
            {
                LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
                TimeWithoutMissionField?.SetValue(state, 0f);
                state.stateDisplayName = "Unloading cargo";
                return true;
            }

            CommanderPlugin.Log.LogWarning($"Assigned supply aircraft has no active cargo station: {CommanderGameAccess.GetUnitLabel(aircraft)}");
            return false;
        }

        LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
        TimeWithoutMissionField?.SetValue(state, 0f);
        TransportModeField?.SetValue(state, AIHeloTransportState.TransportMode.LandSuppy);
        state.stateDisplayName = $"Delivering {mission.CargoLabel}";

        if (PilotField?.GetValue(state) is Pilot pilot)
        {
            pilot.flightInfo.EnemyContact = true;
        }

        object? destination = TransportDestinationField?.GetValue(state);
        if (destination == null)
        {
            return false;
        }

        bool intermediateRouteTarget = TryGetApproachRouteTarget(aircraft, mission, out GlobalPosition assignedTarget);
        mission.RouteTransitActive = intermediateRouteTarget;
        AirdropField?.SetValue(state, mission.Airdrop);
        if (intermediateRouteTarget)
        {
            if (aircraft.autopilot is not AutopilotTiltwing)
            {
                assignedTarget = GetTurnAnticipationTarget(aircraft, mission, assignedTarget);
                GlobalPosition aircraftPosition = aircraft.GlobalPosition();
                Vector3 routeDirection = assignedTarget - aircraftPosition;
                routeDirection.y = 0f;
                if (routeDirection.sqrMagnitude > 1f)
                {
                    routeDirection.Normalize();
                    assignedTarget = new GlobalPosition(
                        aircraftPosition.x + (routeDirection.x * 10000f),
                        assignedTarget.y,
                        aircraftPosition.z + (routeDirection.z * 10000f));
                }
            }
        }
        else
        {
            assignedTarget = mission.Target;
        }

        bool foundationLandingSearch = !intermediateRouteTarget && mission.FoundationSiteId >= 0;
        if (foundationLandingSearch)
        {
            if (!mission.FoundationLandingSearchInitialized)
            {
                Vector3 approach = aircraft.GlobalPosition() - mission.Target;
                approach.y = 0f;
                if (approach.sqrMagnitude < 1f)
                {
                    approach = -aircraft.transform.forward;
                    approach.y = 0f;
                }

                approach.Normalize();
                assignedTarget = mission.Target + (approach * 120f);
                DestinationLzField?.SetValue(destination, assignedTarget);
                DestinationTouchdownField?.SetValue(destination, assignedTarget);
                DestinationSlopeField?.SetValue(destination, 90f);
                DestinationAttemptsField?.SetValue(destination, 0);
                mission.FoundationLandingSearchInitialized = true;
            }

            UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 300f, aircraft });
            if (DestinationTouchdownField?.GetValue(destination) is GlobalPosition foundationTouchdown)
            {
                assignedTarget = foundationTouchdown;
            }
        }

        bool elevatedPlatformApproach = !intermediateRouteTarget
            && mission.FoundationSiteId < 0
            && (mission.DepositSiteId >= 0 || mission.JacknifeSiteId >= 0);
        if (elevatedPlatformApproach && !mission.PlatformArrivalInitialized)
        {
            Vector3 awayFromPlatform = aircraft.GlobalPosition() - mission.Target;
            awayFromPlatform.y = 0f;
            if (awayFromPlatform.sqrMagnitude < 1f)
            {
                awayFromPlatform = -aircraft.transform.forward;
                awayFromPlatform.y = 0f;
            }

            awayFromPlatform.Normalize();
            float arrivalDistance = Mathf.Max(150f, aircraft.maxRadius * 5f);
            const float arrivalClearance = 20f;
            mission.PlatformArrivalPoint = new GlobalPosition(
                mission.Target.x + (awayFromPlatform.x * arrivalDistance),
                mission.Target.y + arrivalClearance,
                mission.Target.z + (awayFromPlatform.z * arrivalDistance));
            mission.PlatformArrivalInitialized = true;
        }

        if (elevatedPlatformApproach && !mission.PlatformArrivalReached)
        {
            float arrivalDistance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                mission.PlatformArrivalPoint.ToLocalPosition());
            float heightAboveDeck = aircraft.GlobalPosition().y - mission.Target.y;
            if (arrivalDistance <= 50f && heightAboveDeck >= 10f && aircraft.speed <= 25f)
            {
                mission.PlatformArrivalReached = true;
            }
            else
            {
                assignedTarget = mission.PlatformArrivalPoint;
            }
        }

        if (elevatedPlatformApproach
            && mission.PlatformArrivalReached
            && !mission.PlatformApproachComplete)
        {
            const float deckClearance = 20f;
            float horizontalDistance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                mission.Target.ToLocalPosition());
            float heightAboveDeck = aircraft.GlobalPosition().y - mission.Target.y;
            if (horizontalDistance <= 30f
                && heightAboveDeck >= deckClearance * 0.6f
                && aircraft.speed <= 20f)
            {
                mission.PlatformApproachComplete = true;
            }
            else
            {
                assignedTarget = new GlobalPosition(
                    mission.Target.x,
                    mission.Target.y + deckClearance,
                    mission.Target.z);
            }
        }

        DestinationValidMissionField?.SetValue(destination, true);
        DestinationDropConditionsField?.SetValue(destination, false);
        DestinationEnemyPositionField?.SetValue(destination, mission.Target);
        if (!foundationLandingSearch)
        {
            DestinationLzField?.SetValue(destination, assignedTarget);
        }

        if (!mission.Initialized)
        {
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 90f);
            DestinationAttemptsField?.SetValue(destination, 0);
            mission.Initialized = true;
        }

        // An airdrop aims at the exact click. The base game's touchdown search wanders up to 150 m
        // looking for flat, clear ground to land on, which a drop that never lands has no use for.
        if (intermediateRouteTarget || elevatedPlatformApproach || mission.Airdrop)
        {
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 0f);
        }
        else if (!foundationLandingSearch)
        {
            UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 150f, aircraft });
            KeepTouchdownLevel(destination, aircraft, mission);
        }

        TransportDestinationField?.SetValue(state, destination);

        // What the state is about to write here itself, one line into FixedUpdateState. It matters
        // which of the two points this is: FixedUpdateState measures the distance that gates the
        // descent off `destination` at the *top* of the tick, before it re-reads the touchdown
        // point, and only lets its hover height fall once that reads under 20 m. The touchdown
        // search wanders up to 150 m off the click looking for flat, clear ground, so writing the
        // click here made every override pass read "still a long way to run", snap the hover back
        // to 20 m and cancel the descent that had begun. The helicopter held over its zone and
        // never put its load down until the player moved the LZ under it, which is the only thing
        // that brought the two points back together.
        GlobalPosition setDown = DestinationTouchdownField?.GetValue(destination) is GlobalPosition touchdown
            ? touchdown
            : assignedTarget;
        StateDestinationField?.SetValue(state, setDown);
        return true;
    }

    private void EndTargetOverrideForState(AIHeloTransportState state)
    {
        if (AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && mission.TargetOverrideActive)
        {
            mission.TargetOverrideActive = false;

            // From here the game's own transport logic owns the aircraft, and with cargo still
            // aboard it lands it at the nearest objective or enemy instead of the player's LZ.
            if (HasDeployableCargo(aircraft) && !mission.Cancelled)
            {
                CommanderPlugin.Log.LogWarning(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} left its cargo run with "
                        + $"{CountDeployableCargo(aircraft)} load(s) aboard, "
                        + $"{CommanderGameAccess.HorizontalDistance(aircraft.transform.position, mission.Target.ToLocalPosition()):0} m "
                        + "from its LZ; the game's transport AI has it now.");
            }
        }
    }

    private bool ShouldDelayAssignedCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        if (pilot.aircraft != null
            && assignedMissions.TryGetValue(pilot.aircraft, out CargoMission assignedMission)
            && assignedMission.Cancelled)
        {
            return false;
        }

        // Tell `pilot.currentState == pilot.AIHeloTakeoffState` apart from "no state yet": two nulls
        // compare equal, which would refuse the first SwitchState inside `Pilot.SetStartingAiState` and
        // leave a fixed-wing supply aircraft with no state at all (it never taxis or trips a stuck timer).
        // `SetStartingAiState` only creates `AIHeloTakeoffState` for rotary pilots, so it is null for a
        // Plane. The ground-rescue lift is a SwitchState too.
        if (pilot.aircraft != null
            && pilot.currentState != null
            && pilot.AIHeloTakeoffState != null
            && assignedMissions.ContainsKey(pilot.aircraft)
            && pilot.currentState == pilot.AIHeloTakeoffState
            && requestedState != pilot.currentState
            && pilot.aircraft.radarAlt < TakeoffStateExitMeters)
        {
            return true;
        }

        // Same null trap a second time: SetStartingAiState never creates AIHeloTransportState, so
        // before the first state both are null and compare equal. The airdrop rule below then
        // refused the starting SwitchState and the helicopter sat on its pad with no state at all.
        if (requestedState == pilot.currentState
            || pilot.currentState == null
            || pilot.currentState != pilot.AIHeloTransportState
            || pilot.aircraft == null
            || !assignedMissions.TryGetValue(pilot.aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        // Keep an airdrop in transport state until every cargo station has fired. The base game
        // switches to combat straight after every DeployCargo call, including the ones
        // DeployNextAssignedCargo holds back until the aircraft is over the zone, and leaving the
        // state ends the override, so the game's own transport logic would take the run elsewhere.
        if (mission.Airdrop)
        {
            return HasDeployableCargo(pilot.aircraft);
        }

        return mission.CargoClearancePending
            || (mission.LastCargoReleasedAt > 0f
                && Time.timeSinceLevelLoad - mission.LastCargoReleasedAt < 8f);
    }

    private void HoldDeployedCargo(Aircraft aircraft, Unit cargoUnit)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return;
        }

        if (mission.Cancelled)
        {
            DestroyCargoUnit(cargoUnit);
            return;
        }

        mission.ActivatedCargoCount++;
        if (mission.Airdrop)
        {
            CommanderPlugin.Instance?.StartCoroutine(LogAirdropLanding(cargoUnit, mission.Target));
        }

        if (IsSamLogisticsMission(mission)
            && !IsJacknifeUnit(cargoUnit))
        {
            float supply = GetCargoSupply(cargoUnit);
            if (mission.FoundationSiteId >= 0)
            {
                CommanderSamSiteService.NotifyFoundationAmmunitionDelivered(
                    mission.FoundationSiteId,
                    supply);
            }
            else if (mission.DepositSiteId >= 0)
            {
                CommanderSamSiteService.TryDepositAmmunitionAmount(
                    mission.DepositSiteId,
                    supply,
                    out _);
            }

            DestroyCargoUnit(cargoUnit);
            UpdateDeliveryCompleted(aircraft, mission);
            return;
        }

        if (mission.DepositAtSamCore && pendingSamCargoDeposits.Add(cargoUnit))
        {
            CommanderPlugin.Instance?.StartCoroutine(
                DepositSamCargoWhenStationary(cargoUnit, mission.DepositSiteId));
        }

        bool cargoStillOnAircraft = HasDeployableCargo(aircraft);
        if (cargoUnit is GroundVehicle groundVehicle)
        {
            if ((mission.FoundationSiteId >= 0 || mission.JacknifeSiteId >= 0)
                && IsJacknifeUnit(groundVehicle)
                && !mission.Airdrop)
            {
                int siteId = mission.FoundationSiteId >= 0
                    ? mission.FoundationSiteId
                    : mission.JacknifeSiteId;
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(
                    groundVehicle.GlobalPosition(),
                    playerCommand: false);
                CommanderSamSiteService.ReserveDeliveredJacknife(siteId, groundVehicle);
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(
                    ReleaseSamJacknifeAfterUnloadDelay(
                        aircraft,
                        groundVehicle,
                        mission));
                return;
            }

            if (cargoStillOnAircraft && !mission.Airdrop)
            {
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(ClearGroundVehicleFromRamp(aircraft, groundVehicle, mission));
            }
            else
            {
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(groundVehicle.GlobalPosition(), playerCommand: false);
                NotifyFoundationCargoActivated(mission, cargoUnit);
            }
        }
        else if (cargoStillOnAircraft
            && !mission.Airdrop
            && !mission.CargoClearancePending)
        {
            mission.CargoClearancePending = true;
            CommanderPlugin.Instance?.StartCoroutine(ClearStaticCargoFromRamp(aircraft, cargoUnit, mission));
        }
        else
        {
            NotifyFoundationCargoActivated(mission, cargoUnit);
        }

        UpdateDeliveryCompleted(aircraft, mission);
    }

    private IEnumerator DepositSamCargoWhenStationary(Unit cargo, int siteId)
    {
        float removeAt = Time.timeSinceLevelLoad + 45f;
        yield return new WaitForSeconds(3f);
        float stableSince = -1f;
        float timeout = Time.timeSinceLevelLoad + 60f;
        while (cargo != null && !cargo.disabled && Time.timeSinceLevelLoad < timeout)
        {
            Rigidbody? body = cargo.rb;
            bool stationary = body == null
                || (body.velocity.sqrMagnitude < 0.25f && body.angularVelocity.sqrMagnitude < 0.25f);
            if (stationary)
            {
                if (stableSince < 0f)
                {
                    stableSince = Time.timeSinceLevelLoad;
                }
                else if (Time.timeSinceLevelLoad - stableSince >= 2f)
                {
                    break;
                }
            }
            else
            {
                stableSince = -1f;
            }

            yield return new WaitForSeconds(0.25f);
        }

        pendingSamCargoDeposits.Remove(cargo!);
        if (cargo == null || cargo.disabled)
        {
            yield break;
        }

        if (!CommanderSamSiteService.TryDepositAmmunition(
                siteId,
                cargo,
                out float transferred))
        {
            DestroyCargoUnit(cargo);
            yield break;
        }

        CommanderPlugin.Log.LogInfo(
            $"SAM cargo deposited: cargo={CommanderGameAccess.GetUnitLabel(cargo)}, ammunition={transferred:0.0}.");
        while (cargo != null && !cargo.disabled && Time.timeSinceLevelLoad < removeAt)
        {
            yield return new WaitForSeconds(0.5f);
        }

        if (cargo != null
            && NetworkManagerNuclearOption.i?.ServerObjectManager != null
            && cargo.Identity != null)
        {
            NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(
                cargo.Identity,
                !cargo.Identity.IsSceneObject);
        }
    }

    private bool DeployNextAssignedCargo(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return false;
        }

        if (mission.Cancelled)
        {
            return true;
        }

        if (mission.RouteTransitActive)
        {
            return true;
        }

        HoldLandingTimerWhileCargoProcesses(state, aircraft, mission);

        if (!mission.Airdrop && IsSamLogisticsMission(mission))
        {
            bool unloading = HasDeployableCargo(aircraft)
                || mission.ReleasedCargoCount < mission.ExpectedCargoLoads
                || mission.ReleasedCargoCount > mission.ActivatedCargoCount
                || mission.CargoClearancePending;
            if (unloading)
            {
                OpenCargoDoors(aircraft, mission);
                if (mission.LandingUnloadAt <= 0f)
                {
                    mission.LandingUnloadAt = Time.timeSinceLevelLoad + 30f;
                }

                if (Time.timeSinceLevelLoad < mission.LandingUnloadAt)
                {
                    TouchedDownTimeField?.SetValue(state, 0f);
                    return true;
                }
            }
            else if (!CloseCargoDoors(mission))
            {
                TouchedDownTimeField?.SetValue(state, 0f);
                return true;
            }
        }

        if (Time.timeSinceLevelLoad < mission.NextCargoReleaseAt)
        {
            return true;
        }

        // The base game calls DeployCargo from four seconds of flight out (about 240 m at cruise).
        // Hold the first load until the aircraft is RotaryAirdropLeadSeconds from the zone or at
        // its closest approach; the rest follow on the release interval so the stick stays together.
        if (mission.Airdrop && mission.ReleasedCargoCount == 0)
        {
            float distance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position, mission.Target.ToLocalPosition());
            float previousDistance = mission.LastLzDistance;
            mission.LastLzDistance = distance;
            if (distance > aircraft.speed * RotaryAirdropLeadSeconds && distance < previousDistance)
            {
                return true;
            }
        }

        if (!mission.Airdrop
            && (mission.ReleasedCargoCount > mission.ActivatedCargoCount || mission.CargoClearancePending))
        {
            return true;
        }

        if (!TrySelectNextCargoWeapon(aircraft, out WeaponStation station, out Weapon cargoWeapon))
        {
            if (!mission.Airdrop && IsSamLogisticsMission(mission))
            {
                mission.DeliveryCompleted = mission.ActivatedCargoCount >= mission.ExpectedCargoLoads;
                mission.VerticalDepartureActive = true;
            }

            return true;
        }

        Pilot? pilot = PilotField?.GetValue(state) as Pilot;
        if (pilot == null)
        {
            return false;
        }

        if (!mission.Airdrop
            && IsSamLogisticsMission(mission)
            && cargoWeapon is MountedCargo mountedCargo
            && !IsJacknifeCargoDefinition(mountedCargo.cargo)
            && TryConsumeVirtualSamCargo(aircraft, station, mountedCargo, mission, pilot))
        {
            return true;
        }

        aircraft.weaponManager.currentWeaponStation = station;
        cargoWeapon.Fire(aircraft, null!, aircraft.rb.velocity, station, default);
        station.UpdateLastFired(1);
        mission.ReleasedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + (mission.Airdrop ? 1.5f : 2.5f);
        if (mission.Airdrop)
        {
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} dropped load {mission.ReleasedCargoCount} "
                    + $"{CommanderGameAccess.HorizontalDistance(aircraft.transform.position, mission.Target.ToLocalPosition()):0} m "
                    + $"from its LZ, from {aircraft.radarAlt:0} m at {aircraft.speed:0} m/s.");
        }

        return true;
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.PurchasedWithFunds
            || mission.PurchaseRefunded
            || mission.Hq == null)
        {
            return;
        }

        mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
        mission.Hq.AddFunds(mission.PurchaseCost);
        mission.PurchaseRefunded = true;
    }

    private bool SuppressEjectionAtAssignedSamSite(AIHeloTransportState state)
    {
        return AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && IsSamLogisticsMission(mission)
            && !mission.Airdrop
            && !mission.RouteTransitActive
            && aircraft.radarAlt < 15f
            && FastMath.InRange(aircraft.GlobalPosition(), mission.Target, 500f);
    }

    private bool OverrideAssignedReturnAirbase(AIHeloLandingState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        Airbase? origin = GetAssignedReturnField(aircraft);
        if (aircraft == null || origin == null)
        {
            return false;
        }

        RunwayQuery query = new()
        {
            RunwayType = RunwayQueryType.Vertical,
            MinSize = aircraft.maxRadius,
        };
        if (!origin.TryRequestVerticalLanding(
            aircraft,
            query,
            out Airbase.VerticalLandingPoint landingPoint))
        {
            return false;
        }

        StateNearestAirbaseField?.SetValue(state, origin);
        LandingStatePointField?.SetValue(state, landingPoint);
        LandingStateReachedApproachField?.SetValue(state, false);
        StateDestinationField?.SetValue(state, landingPoint.GetApproachPoint(aircraft));
        return true;
    }

    private bool OverrideAssignedNearestAirbase(PilotBaseState state)
    {
        Airbase? origin = GetAssignedReturnField(AircraftField?.GetValue(state) as Aircraft);
        if (origin == null)
        {
            return false;
        }

        StateNearestAirbaseField?.SetValue(state, origin);
        return true;
    }

    /// <summary>
    /// The field a cargo run goes home to, or null when it no longer decides that. The record outlives
    /// the run, and this runs after Air Command's own <c>AIHeloLandingState</c> postfix, so answering
    /// for a finished run would send a capture landing home.
    /// </summary>
    private Airbase? GetAssignedReturnField(Aircraft? aircraft)
    {
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || mission.Cancelled
            || CommanderAirCommandService.Instance?.TryGetLandingOrder(aircraft, out _, out _) == true)
        {
            return null;
        }

        Airbase? origin = mission.OriginAirbase;
        return origin != null && !origin.disabled && origin.CurrentHQ == mission.Hq ? origin : null;
    }

    private void PruneFinishedMissions()
    {
        if (assignedMissions.Count == 0)
        {
            return;
        }

        List<Aircraft>? stale = null;
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled)
            {
                continue;
            }

            stale ??= new List<Aircraft>();
            stale.Add(entry.Key!);
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            Aircraft aircraft = stale[i];
            if (ReferenceEquals(aircraft, null))
            {
                continue;
            }

            if (aircraft.autopilot != null)
            {
                terrainClearanceAutopilots.Remove(aircraft.autopilot);
                assignedAutopilotAircraft.Remove(aircraft.autopilot);
            }

            pendingTerrainAutopilotBindings.Remove(aircraft);

            if (assignedMissions.TryGetValue(aircraft, out CargoMission failedMission)
                && !failedMission.Cancelled
                && !failedMission.DeliveryCompleted)
            {
                CommanderSamSiteService.NotifySupplyMissionFailed(
                    failedMission.FoundationSiteId,
                    failedMission.DepositSiteId,
                    failedMission.JacknifeSiteId);
            }

            assignedMissions.Remove(aircraft);
        }
    }

    private sealed class PendingTargetSelection
    {
        internal PendingTargetSelection(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase airbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields,
            bool followRoads = true)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            Airbase = airbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
            FollowRoads = followRoads;
        }

        internal bool FollowRoads { get; }

        internal CargoAircraftOption Aircraft { get; }

        internal Loadout Loadout { get; }

        internal string CargoLabel { get; }

        internal Airbase Airbase { get; }

        internal bool HighTerrainClearance { get; }

        internal float TerrainClearanceMeters { get; }

        internal bool Airdrop { get; }

        internal string SupportSummary { get; }

        internal bool UseOtherAirfields { get; }

        internal List<GlobalPosition> Targets { get; } = new();

        internal string GetTargetPrompt()
        {
            string targetType = Airdrop ? "airdrop point" : "landing point";
            return $"Click a {targetType} in the 3D world. The game's Cancel binding cancels.";
        }
    }

    private sealed class QueuedCargoSpawn
    {
        internal QueuedCargoSpawn(
            FactionHQ hq,
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase requestedAirbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields,
            IReadOnlyList<GlobalPosition> targets,
            bool followRoads)
        {
            FollowRoads = followRoads;
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            RequestedAirbase = requestedAirbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
            Targets = new List<GlobalPosition>(targets);
            Hq = hq;
        }

        /// <summary>The faction the run is for: the host's, or a multiplayer guest's.</summary>
        internal FactionHQ Hq { get; }

        internal CargoAircraftOption Aircraft { get; }

        internal Loadout Loadout { get; }

        internal string CargoLabel { get; }

        internal Airbase RequestedAirbase { get; }

        internal bool HighTerrainClearance { get; }

        internal float TerrainClearanceMeters { get; }

        internal bool Airdrop { get; }

        internal string SupportSummary { get; }

        internal bool UseOtherAirfields { get; }

        internal List<GlobalPosition> Targets { get; }

        internal bool FollowRoads { get; }

        internal GlobalPosition Target => Targets[0];
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(
            FactionHQ hq,
            AircraftDefinition definition,
            Airbase originAirbase,
            string cargoLabel,
            GlobalPosition target,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool purchasedWithFunds,
            float purchaseCost,
            IReadOnlyList<GlobalPosition> targets,
            float expiresAt,
            Ship? navalTarget = null,
            bool followRoads = true)
        {
            FollowRoads = followRoads;
            Hq = hq;
            Definition = definition;
            OriginAirbase = originAirbase;
            CargoLabel = cargoLabel;
            Target = target;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            Targets = new List<GlobalPosition>(targets);
            ExpiresAt = expiresAt;
            NavalTarget = navalTarget;
        }

        internal FactionHQ Hq { get; }

        internal AircraftDefinition Definition { get; }

        internal Airbase OriginAirbase { get; }

        internal string CargoLabel { get; }

        internal GlobalPosition Target { get; }

        internal bool HighTerrainClearance { get; }

        internal float TerrainClearanceMeters { get; }

        internal bool Airdrop { get; }

        internal string SupportSummary { get; }

        internal bool PurchasedWithFunds { get; }

        internal float PurchaseCost { get; }

        internal List<GlobalPosition> Targets { get; }

        internal float ExpiresAt { get; }

        internal Ship? NavalTarget { get; }

        internal bool FollowRoads { get; }
    }

    private sealed class CargoMission
    {
        internal CargoMission(
            FactionHQ hq,
            string cargoLabel,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            bool purchasedWithFunds,
            float purchaseCost,
            int expectedCargoLoads,
            IReadOnlyList<GlobalPosition> deliveryTargets,
            bool depositAtSamCore,
            int depositSiteId,
            int foundationSiteId,
            int jacknifeSiteId,
            Airbase originAirbase,
            Ship? navalTarget = null,
            bool followRoads = true)
        {
            FollowRoads = followRoads;
            Hq = hq;
            DeliveryTargets = new List<GlobalPosition>(deliveryTargets);
            CargoLabel = cargoLabel;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpectedCargoLoads = expectedCargoLoads;
            DepositAtSamCore = depositAtSamCore;
            DepositSiteId = depositSiteId;
            FoundationSiteId = foundationSiteId;
            JacknifeSiteId = jacknifeSiteId;
            OriginAirbase = originAirbase;
            NavalTarget = navalTarget;
        }

        internal List<GlobalPosition> ApproachRoute { get; } = new();

        internal List<BayDoor> CargoDoors { get; } = new();

        internal FactionHQ Hq { get; }

        internal GlobalPosition Target => DeliveryTargets[0];

        internal List<GlobalPosition> DeliveryTargets { get; }

        internal string CargoLabel { get; }

        internal bool HighTerrainClearance { get; }

        internal float TerrainClearanceMeters { get; }

        /// <summary>Fly-by delivery rather than a touchdown. The SET LZ button toggles it in flight.</summary>
        internal bool Airdrop { get; set; }

        /// <summary>Last sampled horizontal distance to the zone, so the drop can be timed on
        /// closest approach instead of on crossing a ring. Starts at infinity so the first sample
        /// never reads as "already opening".</summary>
        internal float LastLzDistance { get; set; } = float.PositiveInfinity;

        /// <summary>
        /// True for an airframe with no rotary pilot. It has no <c>AIHeloTransportState</c>, so it is
        /// steered by an Air Command route and drops its load over the LZ instead of landing on it.
        /// </summary>
        internal bool FixedWing { get; set; }

        /// <summary>Set once the run has emptied its bays and gone to reserve, so the tick stops looking at it.</summary>
        internal bool DropRunComplete { get; set; }

        /// <summary>`Time.timeSinceLevelLoad` the airframe was handed this run.</summary>
        internal float AssignedAt { get; set; }

        /// <summary>Set once the launch recorder has seen the run 60 m up or a minute old.</summary>
        internal bool LaunchLogDone { get; set; }

        internal bool PurchasedWithFunds { get; }

        internal float PurchaseCost { get; }

        /// <summary>
        /// Loads the airframe left with. Counted again once the weapons exist: `WeaponManager`
        /// spawns them from the loadout asynchronously, so at the moment the unit registers — which
        /// is when the mission is created — every cargo pylon still reads empty.
        /// </summary>
        internal int ExpectedCargoLoads { get; set; }

        internal bool DepositAtSamCore { get; }

        internal int DepositSiteId { get; }

        internal int FoundationSiteId { get; }

        internal int JacknifeSiteId { get; }

        internal Airbase OriginAirbase { get; }

        internal Ship? NavalTarget { get; }

        internal bool PurchaseRefunded { get; set; }

        internal bool Initialized { get; set; }

        internal bool TargetOverrideActive { get; set; } = true;

        internal int ActivatedCargoCount { get; set; }

        internal int ReleasedCargoCount { get; set; }

        internal float NextCargoReleaseAt { get; set; }

        internal float LastCargoReleasedAt { get; set; }

        internal bool CargoClearancePending { get; set; }

        internal bool Cancelled { get; set; }

        internal float LandingUnloadAt { get; set; }

        internal bool VerticalDepartureActive { get; set; }

        internal bool FoundationLandingSearchInitialized { get; set; }

        internal bool PlatformArrivalInitialized { get; set; }

        internal bool PlatformArrivalReached { get; set; }

        internal GlobalPosition PlatformArrivalPoint { get; set; }

        internal bool PlatformApproachComplete { get; set; }

        internal bool RoutePlanned { get; set; }

        internal bool SteepLanding { get; set; }

        internal bool RouteTransitActive { get; set; }

        internal float LastTransportOverrideFixedTime { get; set; } = float.NegativeInfinity;

        internal int ApproachRouteIndex { get; set; }

        internal bool DeliveryCompleted { get; set; }

        /// <summary>Highest ground or rooftop ahead of the helicopter on its way in, see <see cref="GetClearTopY"/>.</summary>
        internal float HoverClearTopY { get; set; } = float.NegativeInfinity;

        internal float NextHoverClearanceAt { get; set; }

        /// <summary>The touchdown <see cref="KeepTouchdownLevel"/> last judged, and its verdict.</summary>
        internal GlobalPosition CheckedTouchdown { get; set; }

        internal bool CheckedTouchdownLevel { get; set; }

        internal int RestingPasses { get; set; }

        internal int RejectedTouchdowns { get; set; }

        internal bool HoverUnload { get; set; }

        internal bool FollowRoads { get; }
    }
}
