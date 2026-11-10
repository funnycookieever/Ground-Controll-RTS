using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Hitches a static trailer to an idle tractor or flatbed. The hauler drives to the trailer and hitches
/// it; from then on the player drives the hauler with ordinary move orders, the trailer following
/// behind, until DROP TRAILER sets it down wherever it is.
/// </summary>
internal sealed class CommanderMobileEmplacementService : ICommanderActivate, ICommanderTickPersistent, ICommanderResetSession
{
    private const float TractorRange = 300f;
    private const float ArrivalDistance = 12f;
    private const float StagingDistance = 18f;
    private const float HitchSeconds = 10f;

    /// <summary>A stopped hauler this close hitches from where it stands; the trailer glides over.</summary>
    private const float GlideRange = 60f;
    private const float StallSeconds = 5f;
    private const float UpdateIntervalSeconds = 0.5f;
    private const float StatusDurationSeconds = 5f;

    // Hitch geometry, tuned by eye on the HLT Tractor; adjust here if a load sits wrong.
    // A wheeled trailer's front box rides over the hauler's rear axles: this far forward of
    // bumper-to-bumper, and lifted this much so the box clears the chassis.
    private const float GooseneckOverlap = 3f;
    private const float GooseneckLift = 1f;

    // A wheelless container rides on the bed, its rear flush with the hauler's, this high up.
    private const float BedHeight = 1.4f;

    private static readonly FieldInfo? DeployablePartsField = AccessTools.Field(typeof(GroundVehicle), "deployableParts");
    private static readonly FieldInfo? WheelsField = AccessTools.Field(typeof(GroundVehicle), "wheels");
    private static readonly MethodInfo? DeployPartsMethod = AccessTools.Method(typeof(GroundVehicle), "DeployParts");
    private static readonly FieldInfo? ElevationField = AccessTools.Field(typeof(Turret), "elevationAngle");
    private static readonly FieldInfo? ElevationPivotField = AccessTools.Field(typeof(Turret), "elevationTransform");

    private readonly CommanderSelectionService selectionService;
    private readonly List<TowJob> jobs = new();

    private float nextUpdateAt;
    private float statusUntil;
    private string statusText = string.Empty;
    private GroundVehicle? availabilityTrailer;
    private bool cachedHaulerAvailable;
    private float nextAvailabilityCheckAt;

    private bool issuingInternalHaulerCommand;

    internal CommanderMobileEmplacementService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
        CommanderNetwork.OnHost("emplace", HostHitch);
        CommanderNetwork.OnHost("tow-drop", HostDrop);
    }

    private enum TowStage
    {
        ApproachingPickup,
        Hitching,
        Towing,
    }

    internal static CommanderMobileEmplacementService? Instance { get; private set; }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    public void Activate()
    {
        nextUpdateAt = CommanderScheduler.Stagger("mobile-emplacements", UpdateIntervalSeconds, 0.2f);
    }

    public void ResetSession()
    {
        jobs.Clear();
        statusText = string.Empty;
        availabilityTrailer = null;
        cachedHaulerAvailable = false;
        nextAvailabilityCheckAt = 0f;
    }

    public void TickPersistent()
    {
        if (!CommanderScheduler.IsDue(ref nextUpdateAt, UpdateIntervalSeconds))
        {
            return;
        }

        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            if (UpdateJob(jobs[i]))
            {
                jobs.RemoveAt(i);
            }
        }
    }

    /// <summary>A hauler on its way to hitch: the player's move orders would pull it off the trailer.</summary>
    internal static bool IsReservedHauler(Unit? unit)
    {
        return Instance?.FindJob(unit) is { Stage: not TowStage.Towing } job && ReferenceEquals(job.Tractor, unit);
    }

    /// <summary>
    /// Whether <paramref name="obstacle"/> is the trailer <paramref name="tractor"/> is hitching or
    /// towing. Avoiding it would leave a tractor parked against it unable to pull up to it.
    /// </summary>
    internal static bool IsTowedBy(GroundVehicle tractor, Transform? obstacle)
    {
        return obstacle != null
            && Instance?.FindJob(tractor) is { } job
            && ReferenceEquals(job.Tractor, tractor)
            && job.Trailer.transform == obstacle;
    }

    /// <summary>A hauler that is towing, so its systems window offers DROP TRAILER.</summary>
    internal static bool IsTowingHauler(Unit? unit)
    {
        return Instance?.FindJob(unit) is { Stage: TowStage.Towing } job && ReferenceEquals(job.Tractor, unit);
    }

    internal static bool ShouldBlockDestination(UnitCommand command)
    {
        CommanderMobileEmplacementService? service = Instance;
        return service != null && !service.issuingInternalHaulerCommand && IsReservedHauler(command.GetComponent<Unit>());
    }

    internal static bool IsMoveableTrailer(Unit? unit)
    {
        return unit is GroundVehicle vehicle
            && CommanderGameAccess.IsFriendlyUnit(vehicle, CommanderGameAccess.GetLocalHq())
            && CommanderGameAccess.IsTrailerVehicleDefinition(vehicle.definition as VehicleDefinition);
    }

    /// <summary>Whether the trailer or hauler is in a hitch job, and whether that job is already towing.</summary>
    internal bool IsInTowJob(Unit? unit, out bool towing)
    {
        TowJob? job = FindJob(unit);
        towing = job?.Stage == TowStage.Towing;
        return job != null;
    }

    internal bool HasAvailableHauler(Unit? unit)
    {
        if (unit is not GroundVehicle trailer || !IsMoveableTrailer(trailer))
        {
            return false;
        }

        if (ReferenceEquals(availabilityTrailer, trailer) && Time.unscaledTime < nextAvailabilityCheckAt)
        {
            return cachedHaulerAvailable;
        }

        availabilityTrailer = trailer;
        cachedHaulerAvailable = TryFindTractor(trailer, out _);
        nextAvailabilityCheckAt = Time.unscaledTime + UpdateIntervalSeconds;
        return cachedHaulerAvailable;
    }

    internal void BeginHitch()
    {
        // The tractor is driven by the host; a guest's hitch is asked for and the host picks the tractor.
        if (NetworkManagerNuclearOption.i == null
            || (!NetworkManagerNuclearOption.i.Server.Active && !CommanderNetwork.HostHasMod))
        {
            SetStatus(CommanderNetwork.HostMissingReason);
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (!IsMoveableTrailer(selected) || selected is not GroundVehicle trailer)
        {
            SetStatus("Select a friendly static trailer first.");
            return;
        }

        if (FindJob(trailer) != null)
        {
            SetStatus("This trailer is already being hitched.");
            return;
        }

        if (!TryFindTractor(trailer, out GroundVehicle tractor))
        {
            SetStatus("Requires an idle HLT/MSV Tractor or Flatbed within 300 m.");
            return;
        }

        availabilityTrailer = null;
        if (CommanderNetwork.Forward("emplace", CommanderNetwork.Id(trailer)))
        {
            SetStatus("Hitching.");
            return;
        }

        StartHitch(trailer, tractor);
    }

    /// <summary>Sets the trailer down where it is; <paramref name="unit"/> is the trailer or its hauler.</summary>
    internal void DropTrailer(Unit? unit)
    {
        if (CommanderNetwork.Forward("tow-drop", CommanderNetwork.Id(unit)))
        {
            SetStatus("Dropping trailer.");
            return;
        }

        if (FindJob(unit) is not { Stage: TowStage.Towing } job)
        {
            return;
        }

        // Set down clear of the hauler's tail: in place, a container lands under the bed and the
        // hauler is thrown up off it.
        Transform tractor = job.Tractor.transform;
        float clear = HalfLength(job.Tractor) + HalfLength(job.Trailer) + 1f;
        EndTow(job, (tractor.position - (tractor.forward * clear)).ToGlobalPosition());
        jobs.Remove(job);
        SetStatus("Trailer dropped.");
    }

    private static bool IsHauler(GroundVehicle vehicle)
    {
        string name = vehicle.definition?.unitName ?? vehicle.unitName ?? string.Empty;
        string key = vehicle.definition?.jsonKey ?? string.Empty;
        return name.EndsWith("Tractor", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Flatbed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "HLT-T", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Truck2-T", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "HLT-L", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Truck2-L", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdle(GroundVehicle vehicle)
    {
        if (Mathf.Abs(vehicle.speed) > 1.5f)
        {
            return false;
        }

        return !CommanderGameAccess.TryGetCurrentCommandPosition(vehicle, out GlobalPosition command)
            || CommanderGameAccess.HorizontalDistance(vehicle.transform.position, command.ToLocalPosition()) <= 20f;
    }

    private static bool HasArrived(GroundVehicle vehicle, GlobalPosition destination)
    {
        return CommanderGameAccess.HorizontalDistance(vehicle.transform.position, destination.ToLocalPosition()) <= ArrivalDistance
            && Mathf.Abs(vehicle.speed) <= 2f;
    }

    /// <summary>
    /// Hitches the trailer behind the tractor the way the game handles a slung vehicle: its own
    /// GroundVehicle driver is switched off (so its physics job cannot declare it moving and drop it),
    /// it is kinematic and collider-free, and it is pinned to the tractor's tail every physics step.
    /// </summary>
    private static void BeginTow(TowJob job)
    {
        GroundVehicle trailer = job.Trailer;
        float tractorHalf = HalfLength(job.Tractor);
        float trailerHalf = HalfLength(trailer);
        bool container = WheelsField?.GetValue(trailer) is GameObject[] { Length: 0 };
        job.TowOffset = container
            ? new Vector3(0f, BedHeight, -(tractorHalf - trailerHalf))
            : new Vector3(0f, GooseneckLift, -(tractorHalf + trailerHalf - GooseneckOverlap));
        job.TowColliders = Array.FindAll(trailer.GetComponentsInChildren<Collider>(), collider => collider.enabled);
        foreach (Collider collider in job.TowColliders)
        {
            collider.enabled = false;
        }

        // Only the BDF StratoLance needs its launcher elevation preserved for now but can be expanded later if needed.
        if (trailer.definition?.jsonKey is "SAMTrailer1")
        {
            CaptureLauncherElevation(job);
        }

        trailer.StowTurrets(true);

        // Stabiliser legs are authored to stay down once deployed; let them fold for the tow.
        job.StaysDeployed = SetPartsStayDeployed(trailer, null);
        CommanderPlugin.Log.LogInfo($"Hitch: {CommanderGameAccess.GetUnitLabel(trailer)} has {job.StaysDeployed.Length} deployable part(s) to fold.");

        // Not stationary, so the transform syncs to guests as a slung vehicle's does; this also folds
        // the deployed parts away.
        trailer.NetworknetworkStationary = false;

        // The setter only folds the parts when the flag actually changes; fold them regardless.
        DeployPartsMethod?.Invoke(trailer, new object[] { false });
        trailer.enabled = false;
        if (trailer.rb != null)
        {
            trailer.rb.isKinematic = true;
        }

        CommanderPlugin.Instance?.StartCoroutine(Tow(job));
    }

    private static System.Collections.IEnumerator Tow(TowJob job)
    {
        // Global, so a floating-origin shift during the glide does not throw the start off.
        GlobalPosition from = job.Trailer.GlobalPosition();
        Quaternion fromRotation = job.Trailer.transform.rotation;
        float startedAt = Time.time;
        while (job.Stage != TowStage.ApproachingPickup && job.Trailer != null && job.Tractor != null)
        {
            // Glides from where the trailer stood onto the hauler over the hitch, then rides there.
            float t = Mathf.SmoothStep(0f, 1f, (Time.time - startedAt) / HitchSeconds);
            Transform tractor = job.Tractor.transform;
            Vector3 position = Vector3.Lerp(from.ToLocalPosition(), tractor.TransformPoint(job.TowOffset), t);
            Quaternion rotation = Quaternion.Slerp(fromRotation, tractor.rotation, t);
            if (job.Trailer.rb != null)
            {
                job.Trailer.rb.MovePosition(position);
                job.Trailer.rb.MoveRotation(rotation);
            }
            else
            {
                job.Trailer.transform.SetPositionAndRotation(position, rotation);
            }

            // Its own Update, which turns the wheels, is off while it rides; roll them at the hauler's pace.
            job.Trailer.AnimateWheels(job.Tractor.speed);
            yield return new WaitForFixedUpdate();
        }
    }

    /// <summary>Unhitches the trailer and emplaces it on the terrain at <paramref name="position"/>.</summary>
    private static void EndTow(TowJob job, GlobalPosition position)
    {
        job.Stage = TowStage.ApproachingPickup;
        GroundVehicle trailer = job.Trailer;
        if (trailer == null)
        {
            return;
        }

        foreach (Collider collider in job.TowColliders)
        {
            if (collider != null)
            {
                collider.enabled = true;
            }
        }

        trailer.enabled = true;
        bool restoreLauncher = job.LauncherElevations.Count != 0;
        if (!restoreLauncher)
        {
            trailer.StowTurrets(false);
        }

        CommanderGameAccess.TeleportGroundVehicle(trailer, position);
        CommanderGameAccess.SetUnitHoldPosition(trailer, hold: true);

        // Keep the existing deployment behavior for every trailer.
        trailer.NetworknetworkStationary = true;
        SetPartsStayDeployed(trailer, job.StaysDeployed);

        if (restoreLauncher)
        {
            if (CommanderPlugin.Instance is { } plugin)
            {
                plugin.StartCoroutine(FinishUnstow(job));
            }
            else
            {
                trailer.StowTurrets(false);
                RestoreLauncherElevation(job);
            }
        }
    }

    /// <summary>
    /// Sets every deployable part's staysDeployed flag: to the saved values, or all off when
    /// <paramref name="values"/> is null. Returns what the flags were.
    /// </summary>
    private static bool[] SetPartsStayDeployed(GroundVehicle vehicle, bool[]? values)
    {
        if (DeployablePartsField?.GetValue(vehicle) is not Array parts || parts.Length == 0)
        {
            return Array.Empty<bool>();
        }

        FieldInfo? flag = AccessTools.Field(parts.GetType().GetElementType(), "staysDeployed");
        bool[] old = new bool[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            object? part = parts.GetValue(i);
            if (part == null || flag == null)
            {
                continue;
            }

            old[i] = (bool)flag.GetValue(part);
            flag.SetValue(part, values != null && i < values.Length && values[i]);
        }

        return old;
    }

    private static void CaptureLauncherElevation(TowJob job)
    {
        GroundVehicle trailer = job.Trailer;
        if (ElevationField == null || ElevationPivotField == null || trailer.weaponStations == null)
            return;

        foreach (WeaponStation station in trailer.weaponStations)
        {
            if (station?.Turrets == null) continue;
            foreach (Turret turret in station.Turrets)
            {
                if (turret != null && ElevationField.GetValue(turret) is float angle
                    && ElevationPivotField?.GetValue(turret) is Transform pivot)
                    job.LauncherElevations[turret] = (angle, pivot.localRotation);
            }
        }
    }

    private static void RestoreLauncherElevation(TowJob job)
    {
        foreach (KeyValuePair<Turret, (float Angle, Quaternion PivotRotation)> entry in job.LauncherElevations)
        {
            if (entry.Key == null) continue;
            ElevationField?.SetValue(entry.Key, entry.Value.Angle);
            if (ElevationPivotField?.GetValue(entry.Key) is Transform pivot)
                pivot.localRotation = entry.Value.PivotRotation;
        }
    }

    private static System.Collections.IEnumerator FinishUnstow(TowJob job)
    {
        GroundVehicle trailer = job.Trailer;
        yield return new WaitForFixedUpdate();

        if (trailer == null || trailer.disabled || !trailer.NetworknetworkStationary
            || Instance?.FindJob(trailer) != null)
            yield break;

        // Retain the folded pose so unstowing does not visibly snap the launcher upright.
        var folded = new Dictionary<Turret, (float Angle, Quaternion Rotation)>();
        foreach (var entry in job.LauncherElevations)
        {
            Turret turret = entry.Key;
            if (turret == null || ElevationPivotField?.GetValue(turret) is not Transform pivot)
                continue;

            float angle = ElevationField?.GetValue(turret) is float current
                ? current : entry.Value.Angle;
            folded[turret] = (angle, pivot.localRotation);
        }

        trailer.StowTurrets(false);

        // Undo any immediate visual pose change caused by StowTurrets(false).
        foreach (var entry in folded)
        {
            if (entry.Key == null) continue;
            ElevationField?.SetValue(entry.Key, entry.Value.Angle);
            if (ElevationPivotField?.GetValue(entry.Key) is Transform pivot)
                pivot.localRotation = entry.Value.Rotation;
        }

        const float liftSpeed = 30f; // Degrees per second.
        float liftDeadline = Time.time + 15f;
        while (Time.time < liftDeadline)
        {
            if (trailer == null || trailer.disabled || !trailer.NetworknetworkStationary
                || Instance?.FindJob(trailer) != null)
                yield break;

            bool finished = true;
            float step = liftSpeed * Time.fixedDeltaTime;
            foreach (var entry in job.LauncherElevations)
            {
                Turret turret = entry.Key;
                if (turret == null || ElevationPivotField?.GetValue(turret) is not Transform pivot)
                    continue;

                pivot.localRotation = Quaternion.RotateTowards(
                    pivot.localRotation, entry.Value.PivotRotation, step);
                if (ElevationField?.GetValue(turret) is float current)
                {
                    float next = Mathf.MoveTowardsAngle(current, entry.Value.Angle, step);
                    ElevationField.SetValue(turret, next);
                    if (Mathf.Abs(Mathf.DeltaAngle(next, entry.Value.Angle)) > 0.1f)
                        finished = false;
                }

                if (Quaternion.Angle(pivot.localRotation, entry.Value.PivotRotation) > 0.1f)
                    finished = false;
            }

            if (finished) break;
            yield return new WaitForFixedUpdate();
        }

        // Restore the exact saved angle after the animation.
        if (trailer != null && !trailer.disabled && trailer.NetworknetworkStationary
            && Instance?.FindJob(trailer) == null)
            RestoreLauncherElevation(job);
    }

    private static float HalfLength(Unit unit)
    {
        return unit.definition != null && unit.definition.length > 0f ? unit.definition.length * 0.5f : 4f;
    }

    private TowJob? FindJob(Unit? unit)
    {
        if (unit == null)
        {
            return null;
        }

        foreach (TowJob job in jobs)
        {
            if (ReferenceEquals(job.Trailer, unit) || ReferenceEquals(job.Tractor, unit))
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>Advances one job; true when it is over.</summary>
    private bool UpdateJob(TowJob job)
    {
        bool trailerLost = job.Trailer == null || job.Trailer.disabled;
        if (job.Tractor == null || job.Tractor.disabled)
        {
            if (!trailerLost && job.Stage != TowStage.ApproachingPickup)
            {
                EndTow(job, job.Trailer!.GlobalPosition());
            }

            SetStatus("Hauler lost.");
            return true;
        }

        if (trailerLost)
        {
            job.Stage = TowStage.ApproachingPickup;
            CommanderGameAccess.SetUnitHoldPosition(job.Tractor, hold: false);
            SetStatus("Trailer lost.");
            return true;
        }

        switch (job.Stage)
        {
            case TowStage.ApproachingPickup:
                // The trailer can move under the tractor's feet: a freshly bought one sits inside its
                // depot until CommanderDepotLane lifts it outside, and the old spot is then unreachable.
                if (!CommanderGameAccess.ApproximatelyEqual(job.AimedAtTrailer, job.Trailer!.GlobalPosition(), 5f))
                {
                    job.AimPickup();
                    IssueHaulerDestination(job.Tractor, job.PickupStagingPoint);
                    CommanderPlugin.Log.LogInfo("Hitch: trailer moved, tractor re-aimed.");
                }
                else if (CommanderGameAccess.TryGetCurrentCommandPosition(job.Tractor, out GlobalPosition ordered)
                    && !CommanderGameAccess.ApproximatelyEqual(ordered, job.PickupStagingPoint, 5f))
                {
                    // Something else (a depot lane clear, a pin) replaced the pickup order.
                    IssueHaulerDestination(job.Tractor, job.PickupStagingPoint);
                    CommanderPlugin.Log.LogInfo("Hitch: tractor's order was replaced, pickup re-sent.");
                }

                // The game's pathfinder drops a last waypoint that is behind the truck and within
                // 60 m, so a truck can park short of the trailer and never move again. Stopped and
                // close enough counts as there: the trailer glides the rest of the way.
                float distance = CommanderGameAccess.HorizontalDistance(job.Tractor.transform.position, job.Trailer!.transform.position);
                bool stopped = Mathf.Abs(job.Tractor.speed) <= 1f;
                job.StoppedSince = stopped ? Mathf.Min(job.StoppedSince, Time.unscaledTime) : float.MaxValue;
                bool parkedNear = distance <= GlideRange && Time.unscaledTime - job.StoppedSince >= StallSeconds;
                if (parkedNear || HasArrived(job.Tractor, job.PickupStagingPoint))
                {
                    CommanderPlugin.Log.LogInfo($"Hitch: {CommanderGameAccess.GetUnitLabel(job.Tractor)} stopped {distance:0} m from the trailer; hitching.");
                    job.Stage = TowStage.Hitching;
                    job.StageCompleteAt = Time.unscaledTime + HitchSeconds;
                    IssueHaulerDestination(job.Tractor, job.Tractor.GlobalPosition());
                    BeginTow(job);
                    SetStatus("Hitching trailer (10 seconds).");
                }

                break;

            case TowStage.Hitching:
                if (Time.unscaledTime < job.StageCompleteAt)
                {
                    break;
                }

                job.Stage = TowStage.Towing;
                CommanderPlugin.Log.LogInfo($"Hitch: {CommanderGameAccess.GetUnitLabel(job.Trailer)} hitched.");
                CommanderGameAccess.SetUnitHoldPosition(job.Tractor, hold: false);
                SetStatus("Trailer hitched. Move the hauler, then DROP TRAILER.");
                break;
        }

        return false;
    }

    private void StartHitch(GroundVehicle trailer, GroundVehicle tractor)
    {
        TowJob job = new(trailer, tractor);
        jobs.Add(job);
        CommanderGameAccess.SetUnitHoldPosition(tractor, hold: true);
        IssueHaulerDestination(tractor, job.PickupStagingPoint);
        CommanderPlugin.Log.LogInfo(
            $"Hitch: {CommanderGameAccess.GetUnitLabel(tractor)} sent to {CommanderGameAccess.GetUnitLabel(trailer)}, "
            + $"{CommanderGameAccess.HorizontalDistance(tractor.transform.position, trailer.transform.position):0} m away.");
        SetStatus($"{CommanderGameAccess.GetUnitLabel(tractor)} dispatched to hitch the trailer.");
    }

    /// <summary>A guest's hitch request: the host picks the tractor, as the guest's own window did.</summary>
    private void HostHitch(FactionHQ hq, string[] args)
    {
        string hostStatus = statusText;
        float hostUntil = statusUntil;
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit)
            || unit is not GroundVehicle trailer
            || !ReferenceEquals(trailer.NetworkHQ, hq)
            || !IsMoveableTrailer(trailer))
        {
            return;
        }

        if (FindJob(trailer) != null)
        {
            CommanderNotice.Fail("This trailer is already being hitched.");
        }
        else if (!TryFindTractor(trailer, out GroundVehicle tractor))
        {
            CommanderNotice.Fail("Requires an idle HLT/MSV Tractor or Flatbed within 300 m.");
        }
        else
        {
            StartHitch(trailer, tractor);
        }

        statusText = hostStatus;
        statusUntil = hostUntil;
    }

    private void HostDrop(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit) && ReferenceEquals(unit.NetworkHQ, hq))
        {
            string hostStatus = statusText;
            float hostUntil = statusUntil;
            DropTrailer(unit);
            statusText = hostStatus;
            statusUntil = hostUntil;
        }
    }

    private bool TryFindTractor(GroundVehicle trailer, out GroundVehicle tractor)
    {
        tractor = null!;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return false;
        }

        float tractorDistance = float.MaxValue;
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle vehicle
                || vehicle.disabled
                || FindJob(vehicle) != null
                || !IsHauler(vehicle)
                || !IsIdle(vehicle))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(vehicle.transform.position, trailer.transform.position);
            if (distance <= TractorRange && distance < tractorDistance)
            {
                tractorDistance = distance;
                tractor = vehicle;
            }
        }

        return tractor != null;
    }

    private void IssueHaulerDestination(GroundVehicle hauler, GlobalPosition destination)
    {
        issuingInternalHaulerCommand = true;
        try
        {
            hauler.UnitCommand?.SetDestination(destination, playerCommand: false);
        }
        finally
        {
            issuingInternalHaulerCommand = false;
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    private sealed class TowJob
    {
        internal TowJob(GroundVehicle trailer, GroundVehicle tractor)
        {
            Trailer = trailer;
            Tractor = tractor;
            AimPickup();
        }

        internal GroundVehicle Trailer { get; }

        internal GroundVehicle Tractor { get; }

        internal GlobalPosition PickupStagingPoint { get; private set; }

        /// <summary>Where the trailer was when <see cref="PickupStagingPoint"/> was picked.</summary>
        internal GlobalPosition AimedAtTrailer { get; private set; }

        internal TowStage Stage { get; set; }

        internal float StageCompleteAt { get; set; }

        internal float StoppedSince { get; set; } = float.MaxValue;

        /// <summary>Where the load rides, in the hauler's local frame.</summary>
        internal Vector3 TowOffset { get; set; }

        internal Collider[] TowColliders { get; set; } = Array.Empty<Collider>();

        internal bool[] StaysDeployed { get; set; } = Array.Empty<bool>();

        internal Dictionary<Turret, (float Angle, Quaternion PivotRotation)> LauncherElevations { get; } = new();

        /// <summary>The pickup spot: <see cref="StagingDistance"/> out from the trailer on the tractor's side.</summary>
        internal void AimPickup()
        {
            Vector3 pickupDirection = Tractor.transform.position - Trailer.transform.position;
            pickupDirection.y = 0f;
            if (pickupDirection.sqrMagnitude < 0.1f)
            {
                pickupDirection = Trailer.transform.forward;
            }

            AimedAtTrailer = Trailer.GlobalPosition();
            PickupStagingPoint = (Trailer.transform.position + (pickupDirection.normalized * StagingDistance)).ToGlobalPosition();
        }
    }
}
