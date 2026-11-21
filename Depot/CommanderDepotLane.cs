using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Keeps a depot's exit lane clear. <c>VehicleDepot.MoveVehicleFromDepot</c> pushes every new
/// vehicle 20 m straight out of the door, and a vehicle that stops there (nowhere to drive, or the
/// end of its order) is what the next one is pushed into: ten of an AI side's vehicles jammed one
/// island depot in Champions. Before a depot spawns, any stopped friendly vehicle
/// in the lane is ordered out to the side, every side, at most once per depot per
/// <see cref="CheckSeconds"/>.
/// </summary>
internal static class CommanderDepotLane
{
    private const float CheckSeconds = 10f;
    private const float LaneLengthMeters = 45f;
    private const float LaneHalfWidthMeters = 7f;
    private const float StoppedSpeed = 1f;

    /// <summary>Where a cleared vehicle goes: this far out of the door and this far to the side.</summary>
    private const float ParkAheadMeters = 35f;
    private const float ParkSideMeters = 22f;

    private const float StuckSweepSeconds = 5f;

    /// <summary>How long a vehicle stays inside a depot's walls before it is moved out.</summary>
    private const float StuckSeconds = 20f;

    /// <summary>Less than the shuffle of a vehicle backing up and retrying against a wall.</summary>
    private const float StuckMoveMeters = 4f;

    private const float WallMarginMeters = 1f;

    /// <summary>The first ring of rescue spots, past the depot's furthest corner, and the next.</summary>
    private const float RescueRingMeters = 15f;
    private const float RescueRingStepMeters = 25f;

    /// <summary>No building or vehicle this close to a rescue spot.</summary>
    private const float RescueClearMeters = 8f;

    private static readonly Dictionary<VehicleDepot, float> NextCheck = new();
    private static readonly Dictionary<Unit, (GlobalPosition Position, float Since)> Inside = new();
    private static readonly HashSet<Unit> InsideNow = new();
    private static readonly List<Unit> Gone = new();
    private static readonly Collider[] RescueHits = new Collider[64];
    private static float nextStuckSweep;

    internal static void Reset()
    {
        NextCheck.Clear();
        Inside.Clear();
        nextStuckSweep = 0f;
    }

    internal static void Clear(VehicleDepot depot)
    {
        FactionHQ? hq = depot != null ? depot.NetworkHQ : null;
        if (hq == null || (NextCheck.TryGetValue(depot!, out float due) && Time.time < due))
        {
            return;
        }

        NextCheck[depot!] = Time.time + CheckSeconds;
        Transform? door = CommanderGameAccess.GetDepotSpawnTransform(depot!);
        if (door == null)
        {
            return;
        }

        Vector3 forward = door.forward;
        forward.y = 0f;
        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);
        int moved = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not GroundVehicle vehicle || vehicle.disabled
                || CommanderMobileEmplacementService.IsReservedHauler(vehicle)
                || (vehicle.rb != null && vehicle.rb.velocity.sqrMagnitude > StoppedSpeed * StoppedSpeed))
            {
                continue;
            }

            Vector3 offset = vehicle.transform.position - door.position;
            offset.y = 0f;
            float ahead = Vector3.Dot(offset, forward);
            float side = Vector3.Dot(offset, right);
            if (ahead < -5f || ahead > LaneLengthMeters || Mathf.Abs(side) > LaneHalfWidthMeters)
            {
                continue;
            }

            // Off to whichever side it already leans, alternating for the dead-centre ones.
            float sign = Mathf.Abs(side) > 0.5f ? Mathf.Sign(side) : ((moved & 1) == 0 ? 1f : -1f);
            Vector3 spot = door.position + (forward * Mathf.Max(ahead, ParkAheadMeters)) + (right * (sign * (ParkSideMeters + (moved * 6f))));
            CommanderEnemyCommanderService.PinTo(vehicle, spot.ToGlobalPosition());

            // A repair vehicle's standby post would otherwise send it straight back after its next job.
            CommanderRepairService.Instance?.SetHome(vehicle, spot.ToGlobalPosition());
            moved++;
        }

        if (moved > 0)
        {
            CommanderPlugin.Log.LogInfo($"{hq.faction.factionName}'s {depot!.name}: moved {moved} stopped vehicle(s) out of the exit lane.");
        }
    }

    /// <summary>
    /// Host only. Any ground vehicle, on any side, that has sat inside a depot's walls for
    /// <see cref="StuckSeconds"/> is moved to clear ground outside. A vehicle shoved off the door's
    /// rail ends up in a wall where the base game's back-up-and-retry drives the same line for ever.
    /// </summary>
    internal static void FreeStuck(CommanderBuildPreview preview)
    {
        if (!CommanderScheduler.IsDue(ref nextStuckSweep, StuckSweepSeconds))
        {
            return;
        }

        InsideNow.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (!hq.IsServer)
            {
                continue;
            }

            List<(VehicleDepot, float)> depots = CommanderCaptureSpoils.DepotsRef(hq);
            for (int i = 0; i < depots.Count; i++)
            {
                VehicleDepot depot = depots[i].Item1;
                if (depot != null && !depot.disabled && depot.definition is BuildingDefinition definition)
                {
                    FreeStuckAt(depot, definition, preview);
                }
            }
        }

        Gone.Clear();
        foreach (Unit unit in Inside.Keys)
        {
            if (!InsideNow.Contains(unit))
            {
                Gone.Add(unit);
            }
        }

        for (int i = 0; i < Gone.Count; i++)
        {
            Inside.Remove(Gone[i]);
        }
    }

    /// <summary>Host, at spawn: sets a trailer down beside the exit lane, clear of the ones already there.</summary>
    internal static void ParkTrailer(VehicleDepot depot, GroundVehicle trailer)
    {
        if (!CommanderSpawnService.TryFindClearVerge(depot, 60f, out GlobalPosition origin, out Vector3 verge, out float clear))
        {
            return;
        }

        // Down the verge from its far end, one trailer length at a time, till a spot is free.
        GlobalPosition spot = origin + (verge * clear);
        for (float meters = clear; meters >= 0f; meters -= 12f)
        {
            spot = CommanderGameAccess.SnapToTerrain(origin + (verge * meters));
            if (!IsVehicleNear(spot, trailer))
            {
                break;
            }
        }

        CommanderGameAccess.TeleportGroundVehicle(trailer, spot);
        CommanderRepairService.Instance?.SetHome(trailer, spot);
    }

    internal static bool IsVehicleNear(GlobalPosition spot, Unit? except = null)
    {
        int hits = Physics.OverlapSphereNonAlloc(spot.ToLocalPosition(), RescueClearMeters, RescueHits, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            if (RescueHits[i] != null && RescueHits[i].GetComponentInParent<Unit>() is GroundVehicle vehicle && vehicle != except)
            {
                return true;
            }
        }

        return false;
    }

    private static void FreeStuckAt(VehicleDepot depot, BuildingDefinition definition, CommanderBuildPreview preview)
    {
        Vector3 half = preview.GetFootprint(definition);
        Vector3 centre = preview.GetFootprintCentre(definition);
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit) || unit is not GroundVehicle vehicle || vehicle.disabled || vehicle.IsSlung())
                {
                    continue;
                }

                Vector3 local = depot.transform.InverseTransformPoint(vehicle.transform.position) - centre;
                if (Mathf.Abs(local.x) > half.x + WallMarginMeters || Mathf.Abs(local.z) > half.z + WallMarginMeters)
                {
                    continue;
                }

                InsideNow.Add(vehicle);
                GlobalPosition here = vehicle.GlobalPosition();
                if (!Inside.TryGetValue(vehicle, out (GlobalPosition Position, float Since) seen)
                    || !CommanderGameAccess.ApproximatelyEqual(seen.Position, here, StuckMoveMeters))
                {
                    Inside[vehicle] = (here, Time.time);
                    continue;
                }

                if (Time.time - seen.Since < StuckSeconds)
                {
                    continue;
                }

                // No room found keeps the clock running from now, so it asks again rather than every sweep.
                Inside[vehicle] = (here, Time.time);
                if (TryFindClearSpot(depot, preview.GetReach(definition), out GlobalPosition spot))
                {
                    CommanderGameAccess.TeleportGroundVehicle(vehicle, spot);
                    CommanderRepairService.Instance?.SetHome(vehicle, spot);
                    CommanderPlugin.Log.LogInfo($"{vehicle.definition.unitName} was stuck inside {hq.faction.factionName}'s {depot.name}: moved it outside.");
                }
            }
        }
    }

    /// <summary>Two rings round the depot, nearest first, starting from the door side.</summary>
    private static bool TryFindClearSpot(VehicleDepot depot, float reach, out GlobalPosition spot)
    {
        Transform? door = CommanderGameAccess.GetDepotSpawnTransform(depot);
        Vector3 forward = door != null ? door.forward : depot.transform.forward;
        forward.y = 0f;
        forward.Normalize();
        for (int ring = 0; ring < 2; ring++)
        {
            float distance = reach + RescueRingMeters + (ring * RescueRingStepMeters);
            for (int i = 0; i < 12; i++)
            {
                Vector3 point = depot.transform.position + (Quaternion.Euler(0f, i * 30f, 0f) * forward * distance);
                spot = CommanderGameAccess.SnapToTerrain(point.ToGlobalPosition());
                if (!CommanderGameAccess.IsBelowSeaLevel(spot)
                    && !CommanderBuildPreview.IsBuiltOver(spot, RescueClearMeters)
                    && !IsVehicleNear(spot)
                    && CommanderGameAccess.ApproximatelyEqual(CommanderEconomyService.MoveOutOfDepotLanes(spot), spot, 1f))
                {
                    return true;
                }
            }
        }

        spot = default;
        return false;
    }
}
