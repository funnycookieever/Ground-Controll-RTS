using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// AREA FIRE: selected artillery shells a patch of ground until it is given another order, and
/// the moment anything hostile inside that patch is known to the faction (radar, datalink, a
/// spotter), the guns switch to it.
/// </summary>
/// <remarks>
/// Two halves, and the second is the base game's own. With a real target the turret is left
/// entirely to <c>Turret.FixedUpdate</c> and its aim solver; the only change is
/// <see cref="FilterPrefix"/>, which refuses every candidate outside the area, so "the enemy shows
/// up in the box" needs no code of its own. With no target the turret is disabled by the base game
/// (<c>SetTarget</c> writes <c>enabled = target != null</c>), so <see cref="Tick"/> switches it back
/// on and <see cref="FixedUpdatePrefix"/> aims it at a random point in the area and pulls the
/// trigger. Only unguided rounds qualify: a gun whose aim solver is an artillery solver (or any gun
/// on an ART vehicle) firing plain shells, and a launcher whose rocket carries the game's
/// inertial "shell" seeker, which is told the point in <see cref="InertialInitializePostfix"/>,
/// and a ballistic missile launcher (the nuclear ones), told in <see cref="BallisticInitializePostfix"/>.
/// Everything is host-side; a guest's order is forwarded.
/// </remarks>
internal static class CommanderAreaFire
{
    internal const float MinDiameterMeters = 1000f;
    internal const float MaxDiameterMeters = 3000f;
    private const float RadiusSendIntervalSeconds = 0.25f;
    private const float TickIntervalSeconds = 1f;
    private const float RaiseRetrySeconds = 30f;

    /// <summary>Smallest the ring is drawn on screen. A 500 m circle on a whole-map view is a few
    /// pixels; a CAS ring is kilometres across, which is why that one is always visible.</summary>
    private const float MinRingPixels = 14f;

    private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit = AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");
    private static readonly AccessTools.FieldRef<Turret, Unit> TurretTarget = AccessTools.FieldRefAccess<Turret, Unit>("target");
    private static readonly AccessTools.FieldRef<Turret, bool> TurretStowed = AccessTools.FieldRefAccess<Turret, bool>("stowed");
    private static readonly AccessTools.FieldRef<Turret, bool> TurretDisabled = AccessTools.FieldRefAccess<Turret, bool>("disabled");
    private static readonly AccessTools.FieldRef<Turret, bool> TurretOnTarget = AccessTools.FieldRefAccess<Turret, bool>("onTarget");
    private static readonly AccessTools.FieldRef<Turret, float> TurretTraverseError = AccessTools.FieldRefAccess<Turret, float>("traverseError");
    private static readonly AccessTools.FieldRef<Turret, bool> TurretFiresWithoutAiming = AccessTools.FieldRefAccess<Turret, bool>("firesWithoutAiming");
    private static readonly AccessTools.FieldRef<Turret, AimSolver> TurretAimSolver = AccessTools.FieldRefAccess<Turret, AimSolver>("aimSolver");
    private static readonly AccessTools.FieldRef<AimSolver, bool> SolverArtillery = AccessTools.FieldRefAccess<AimSolver, bool>("artillery");
    private static readonly AccessTools.FieldRef<AimSolver, float> SolverDragCoef = AccessTools.FieldRefAccess<AimSolver, float>("artilleryDragCoef");
    private static readonly AccessTools.FieldRef<Gun, MissileDefinition> GunGuidedProjectile = AccessTools.FieldRefAccess<Gun, MissileDefinition>("guidedProjectile");
    private static readonly AccessTools.FieldRef<MissileSeeker, Missile> SeekerMissile = AccessTools.FieldRefAccess<MissileSeeker, Missile>("missile");
    private static readonly AccessTools.FieldRef<MissileSeeker, Unit> SeekerTargetUnit = AccessTools.FieldRefAccess<MissileSeeker, Unit>("targetUnit");
    private static readonly AccessTools.FieldRef<InertialSeekerShell, GlobalPosition> InertialKnownPos = AccessTools.FieldRefAccess<InertialSeekerShell, GlobalPosition>("knownPos");
    private static readonly AccessTools.FieldRef<InertialSeekerShell, Vector3> InertialKnownVel = AccessTools.FieldRefAccess<InertialSeekerShell, Vector3>("knownVel");
    private static readonly AccessTools.FieldRef<BallisticMissileGuidance, GlobalPosition> BallisticKnownPos = AccessTools.FieldRefAccess<BallisticMissileGuidance, GlobalPosition>("knownPos");
    private static readonly AccessTools.FieldRef<BallisticMissileGuidance, Vector3> BallisticKnownVel = AccessTools.FieldRefAccess<BallisticMissileGuidance, Vector3>("knownVel");
    private static readonly AccessTools.FieldRef<BallisticMissileGuidance, float> BallisticTargetAngle = AccessTools.FieldRefAccess<BallisticMissileGuidance, float>("targetAngle");
    private static readonly AccessTools.FieldRef<FireControl, bool> FireControlDeployed = AccessTools.FieldRefAccess<FireControl, bool>("deployed");
    private static readonly System.Reflection.FieldInfo FireControlDeployables = AccessTools.Field(typeof(FireControl), "deployables");
    private static readonly Action<Turret, Vector3> AimTurret =
        AccessTools.MethodDelegate<Action<Turret, Vector3>>(AccessTools.Method(typeof(Turret), "AimTurret", new[] { typeof(Vector3) }));

    private static readonly Dictionary<Unit, AreaOrder> Orders = new();
    private static readonly Dictionary<WeaponInfo, bool> CapableStores = new();
    private static readonly HashSet<WeaponInfo> BallisticStores = new();
    private static readonly Dictionary<Missile, GlobalPosition> RoundsInFlight = new();
    private static readonly List<Missile> LandedRounds = new();
    private static readonly List<Unit> Stale = new();
    private static readonly HashSet<string> ReportedSkips = new();
    private static readonly Color AreaColor = new(1f, 0.34f, 0.1f, 0.38f);
    private static float nextTickAt;
    private static bool openedMap;
    private static GameObject? preview;
    private static bool radiusSendPending;
    private static float nextRadiusSendAt;

    /// <summary>True while the AREA FIRE button is waiting for the click that places the area.</summary>
    internal static bool AwaitingTarget { get; private set; }

    /// <summary>The diameter a new area is placed at: the player's last slider setting.</summary>
    internal static float DefaultDiameter => Mathf.Clamp(CommanderSettings.AreaFireDiameterMeters, MinDiameterMeters, MaxDiameterMeters);

    /// <summary>True when every selected unit is shelling an area, which is when the selection bar
    /// trades its movement orders for the diameter slider.</summary>
    internal static bool SelectionInAreaMode(IReadOnlyList<Unit> selection)
    {
        if (selection.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < selection.Count; i++)
        {
            if (!IsAreaFiring(selection[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The diameter the slider shows: the selection's area, or the next one's while placing.</summary>
    internal static float GetSelectionDiameter(IReadOnlyList<Unit> selection)
    {
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] != null && Orders.TryGetValue(selection[i], out AreaOrder order))
            {
                return order.Area.Radius * 2f;
            }
        }

        return DefaultDiameter;
    }

    /// <summary>The slider: remembered for the next area, and applied at once to every area the
    /// selection is shelling. A guest's change goes to the host a few times a second while dragging.</summary>
    internal static void SetSelectionDiameter(IReadOnlyList<Unit> selection, float diameter)
    {
        diameter = Mathf.Clamp(diameter, MinDiameterMeters, MaxDiameterMeters);
        CommanderSettings.AreaFireDiameterMeters = diameter;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] != null && Orders.TryGetValue(selection[i], out AreaOrder order))
            {
                order.Area.Radius = diameter * 0.5f;
                radiusSendPending = true;
            }
        }
    }

    internal static bool IsAreaFiring(Unit? unit)
    {
        return unit != null && Orders.ContainsKey(unit);
    }

    internal static bool CanAreaFire(Unit? unit)
    {
        if (unit == null || unit.disabled || unit is Aircraft)
        {
            return false;
        }

        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            if (IsAreaStation(unit, unit.weaponStations[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A ground launcher of ballistic missiles: the MSV launchers, whose own fire control
    /// never picks a target below the strategic escalation.</summary>
    internal static bool IsBallisticLauncher(Unit? unit)
    {
        if (!CanAreaFire(unit))
        {
            return false;
        }

        for (int i = 0; i < unit!.weaponStations.Count; i++)
        {
            WeaponInfo? info = unit.weaponStations[i]?.WeaponInfo;
            if (info != null && BallisticStores.Contains(info))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool SelectionCanAreaFire(IReadOnlyList<Unit> selection)
    {
        for (int i = 0; i < selection.Count; i++)
        {
            if (CanPlayerAreaFire(selection[i]) && CommanderGameAccess.IsFriendlyUnit(selection[i], CommanderGameAccess.GetLocalHq()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The AREA FIRE button: opens the tactical map with the area circle on the cursor,
    /// the same way an Air Command CAS mission is placed.</summary>
    internal static void Arm()
    {
        AwaitingTarget = true;
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        if (map != null)
        {
            openedMap = map.OpenForPlacement();
            map.SuppressMapFollow = true;
        }
    }

    internal static void Cancel()
    {
        if (!AwaitingTarget)
        {
            return;
        }

        AwaitingTarget = false;
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        if (map != null)
        {
            map.SuppressMapFollow = false;
            if (openedMap)
            {
                map.Close();
            }
        }

        openedMap = false;
        preview = DestroyVisual(preview);
    }

    /// <summary>Per frame, from the move service: the placement preview under the cursor, and the
    /// circle of every area a selected unit is shelling.</summary>
    internal static void TickVisuals()
    {
        SendPendingRadius();
        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (AwaitingTarget && map != null && map.TryGetCursorCoordinates(out GlobalPosition cursor))
        {
            preview = CommanderAirCommandService.EnsureMapRadiusVisual(preview, map, cursor, VisibleRadius(map, DefaultDiameter * 0.5f), AreaColor, "RTS Area Fire Preview");
            preview?.SetActive(true);
        }
        else
        {
            preview?.SetActive(false);
        }

        if (Orders.Count == 0)
        {
            return;
        }

        foreach (AreaOrder order in Orders.Values)
        {
            order.Area.Shown = false;
        }

        IReadOnlyList<Unit>? selection = CommanderSelectionService.Instance?.SelectedUnits;
        if (selection != null)
        {
            for (int i = 0; i < selection.Count; i++)
            {
                if (selection[i] != null && Orders.TryGetValue(selection[i], out AreaOrder order))
                {
                    order.Area.Shown = true;
                }
            }
        }

        foreach (AreaOrder order in Orders.Values)
        {
            MapArea area = order.Area;
            if (area.Shown && map != null)
            {
                area.Visual = CommanderAirCommandService.EnsureMapRadiusVisual(area.Visual, map, area.Center, VisibleRadius(map, area.Radius), AreaColor, "RTS Area Fire");
                ReportRingSize(map, area);
                area.Visual?.SetActive(true);
            }
            else
            {
                area.Visual?.SetActive(false);
            }
        }
    }

    /// <summary>The click that places the area, from the 3D view or the map. Consumes the arming.</summary>
    internal static void Place(IReadOnlyList<Unit> selection, GlobalPosition center)
    {
        Cancel();

        // A map click has no height, and the shells and rockets aim at the height they are given.
        center = CommanderGameAccess.SnapToTerrain(center);
        float radius = DefaultDiameter * 0.5f;
        List<string> args = new() { CommanderNetwork.Position(center), CommanderNetwork.Num(radius) };
        for (int i = 0; i < selection.Count; i++)
        {
            if (CanPlayerAreaFire(selection[i]))
            {
                args.Add(CommanderNetwork.Id(selection[i]));
            }
        }

        if (args.Count == 2)
        {
            CommanderNotice.Fail("AREA FIRE: nothing selected carries unguided artillery.");
            return;
        }

        // A guest sends the order and also keeps a copy of its own, which does no shooting (that
        // is the host's) but is what the selection bar, the map circle and the stop test read.
        CommanderOrderPing.Show(center, attack: true);
        CommanderNetwork.Forward("areafire", args.ToArray());
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        MapArea area = new(center, radius);
        int given = 0;
        for (int i = 2; i < args.Count; i++)
        {
            if (CommanderNetwork.TryUnit(args[i], out Unit unit) && Begin(unit, hq, area))
            {
                given++;
            }
        }

        if (given == 0)
        {
            CommanderNotice.Fail("AREA FIRE: the area is out of reach of every selected gun.");
        }
    }

    /// <summary>A guest's order, ownership-checked, then the same path as the host's own.</summary>
    internal static void HostAreaFire(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryPosition(args, 1, out GlobalPosition center))
        {
            return;
        }

        MapArea area = new(center, ClampRadius(CommanderNetwork.Float(args, 2)));
        int given = 0;
        for (int i = 3; i < args.Length; i++)
        {
            if (CommanderNetwork.TryUnit(args[i], out Unit unit)
                && ReferenceEquals(unit.NetworkHQ, hq)
                && CanPlayerAreaFire(unit)
                && Begin(unit, hq, area))
            {
                given++;
            }
        }

        if (given == 0)
        {
            CommanderNotice.Fail("AREA FIRE: the area is out of reach of every selected gun.");
        }
    }

    /// <summary>An attack order on ballistic launchers: they stop where they are, raise and fire at
    /// the target instead of driving at it. Same host/guest split as <see cref="Place"/>.</summary>
    internal static void PlaceTargeted(IReadOnlyList<Unit> launchers, Unit target)
    {
        List<string> args = new() { CommanderNetwork.Id(target) };
        for (int i = 0; i < launchers.Count; i++)
        {
            args.Add(CommanderNetwork.Id(launchers[i]));
        }

        CommanderNetwork.Forward("areafire.target", args.ToArray());
        BeginTargeted(CommanderGameAccess.GetLocalHq(), args.ToArray(), 0);
    }

    /// <summary>A guest's targeted launch, ownership-checked inside <see cref="Begin"/>.</summary>
    internal static void HostTargetedFire(FactionHQ hq, string[] args)
    {
        BeginTargeted(hq, args, 1);
    }

    /// <summary>A guest's slider, ownership-checked.</summary>
    internal static void HostAreaRadius(FactionHQ hq, string[] args)
    {
        float radius = ClampRadius(CommanderNetwork.Float(args, 1));
        for (int i = 2; i < args.Length; i++)
        {
            if (CommanderNetwork.TryUnit(args[i], out Unit unit)
                && ReferenceEquals(unit.NetworkHQ, hq)
                && Orders.TryGetValue(unit, out AreaOrder order))
            {
                order.Area.Radius = radius;
            }
        }
    }

    /// <summary>A guest gave its guns another order. The host's move service never sees a guest's
    /// orders, so without this the host would go on shelling.</summary>
    internal static void HostAreaStop(FactionHQ hq, string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (CommanderNetwork.TryUnit(args[i], out Unit unit) && ReferenceEquals(unit.NetworkHQ, hq))
            {
                Remove(unit);
            }
        }
    }

    /// <summary>An area the host's own AI shells: GOD DIFFICULTY's nuclear launchers. Host only,
    /// and false while the point is out of the unit's reach.</summary>
    internal static bool TryBeginForAi(Unit unit, GlobalPosition center, float radius)
    {
        return unit.IsServer
            && Begin(unit, unit.NetworkHQ, new MapArea(CommanderGameAccess.SnapToTerrain(center), ClampRadius(radius)));
    }

    /// <summary>Stops shelling. Every other order the unit is given goes through here or
    /// through <see cref="Tick"/> noticing the unit has a move order again.</summary>
    internal static void Clear(Unit unit)
    {
        if (Remove(unit))
        {
            CommanderNetwork.Forward("areafire.stop", CommanderNetwork.Id(unit));
        }
    }

    internal static void Reset()
    {
        foreach (AreaOrder order in Orders.Values)
        {
            order.Area.Visual = DestroyVisual(order.Area.Visual);
        }

        Orders.Clear();
        RoundsInFlight.Clear();
        Cancel();
        preview = DestroyVisual(preview);
    }

    /// <summary>Host upkeep, from the move service's persistent tick.</summary>
    internal static void Tick(CommanderMoveService moves)
    {
        if (Orders.Count == 0 || !CommanderScheduler.IsDue(ref nextTickAt, TickIntervalSeconds))
        {
            return;
        }

        Stale.Clear();
        foreach (KeyValuePair<Unit, AreaOrder> entry in Orders)
        {
            Unit unit = entry.Key;

            // A move, attack, guard or retreat order is "another order", and ends the shelling.
            if (unit == null || unit.disabled || moves.HasOrder(unit) || entry.Value.TargetLost)
            {
                Stale.Add(unit!);
                continue;
            }

            if (!unit.IsServer)
            {
                continue;
            }

            // The base game switches a turret off whenever it has no target; this puts it back so
            // the FixedUpdate prefix has something to run in. A stowed turret is raised once the
            // vehicle is stopped (FIRE's IsSettingUp cannot gate this: it is true for any stowed
            // turret, so it never let one up); the deploy animation is still waited on per shot.
            for (int i = 0; i < unit.weaponStations.Count; i++)
            {
                WeaponStation station = unit.weaponStations[i];
                if (!IsAreaStation(unit, station))
                {
                    continue;
                }

                for (int t = 0; t < station.Turrets.Count; t++)
                {
                    Turret turret = station.Turrets[t];
                    if (turret == null || TurretDisabled(turret))
                    {
                        continue;
                    }

                    if (TurretStowed(turret))
                    {
                        if (Mathf.Abs(unit.speed) < 1f)
                        {
                            turret.SetStowed(false);
                        }

                        continue;
                    }

                    turret.enabled = true;
                }
            }
        }

        for (int i = 0; i < Stale.Count; i++)
        {
            Clear(Stale[i]);
        }
    }

    internal static bool FixedUpdatePrefix(Turret turret)
    {
        if (Orders.Count == 0)
        {
            return true;
        }

        Unit unit = TurretAttachedUnit(turret);
        if (!TryGetOrder(unit, out AreaOrder order)
            || !unit.IsServer
            || unit.disabled
            || TurretDisabled(turret)
            || TurretStowed(turret)
            || (TurretTarget(turret) is Unit target && !target.disabled)
            || CommanderMoveService.IsHoldingFire(unit))
        {
            // A real target in the area is the base game's to shoot, with its own aim solver.
            return true;
        }

        WeaponStation? station = null;
        WeaponStation[] stations = turret.GetWeaponStations();
        for (int i = 0; i < stations.Length; i++)
        {
            if (stations[i].Ammo > 0 && IsAreaStation(unit, stations[i]))
            {
                station = stations[i];
                break;
            }
        }

        if (station == null)
        {
            // Nothing to shoot with on this turret: stay switched on only while there is a target.
            return true;
        }

        TargetRequirements reach = station.WeaponInfo.targetRequirements;
        float range = FastMath.Distance(unit.GlobalPosition(), order.AimPoint);
        if (range > reach.maxRange || range < reach.minRange)
        {
            order.Report(unit, "aim point out of range");
            order.PickAimPoint();
            return false;
        }

        bool aimed = TurretFiresWithoutAiming(turret);
        if (!aimed)
        {
            AimTurret(turret, GetAimVector(turret, station, order.AimPoint, range));

            // A launcher's elevation is the rocket's business (the base game zeroes that error for
            // anything that is not a gun or boresight), so only its traverse has to line up.
            aimed = station.Weapons[0] is Gun ? TurretOnTarget(turret) : Mathf.Abs(TurretTraverseError(turret)) < 1f;
        }

        if (!aimed)
        {
            order.Report(unit, "laying the gun");
        }
        else if (!station.Ready())
        {
            order.Report(unit, "reloading");
        }
        else if (Mathf.Abs(unit.speed) >= 1f)
        {
            order.Report(unit, "stopping");
            order.ResetSetupWait();
        }
        else if (BallisticStores.Contains(station.WeaponInfo) && !IsLauncherRaised(unit, order))
        {
            order.Report(unit, "raising the launcher");
        }
        else if (CommanderFireNow.IsSettingUp(unit, station) && order.StillSettingUp())
        {
            // Some part on the MLRS never reads fully deployed, and the base game fires it with no
            // such check once radar hands it a target, so the wait is capped rather than absolute.
            order.Report(unit, "setting up");
        }
        else
        {
            station.Fire(unit, null!);
            order.Report(unit, "firing");
            order.PickAimPoint();
        }

        return false;
    }

    internal static bool FilterPrefix(Turret turret, Unit targetCandidate)
    {
        if (Orders.Count == 0
            || targetCandidate == null
            || !TryGetOrder(TurretAttachedUnit(turret), out AreaOrder order))
        {
            return true;
        }

        // Only the artillery is held to the area; a machine gun on the same hull still defends it.
        Unit unit = TurretAttachedUnit(turret);
        bool areaTurret = false;
        WeaponStation[] stations = turret.GetWeaponStations();
        for (int i = 0; i < stations.Length && !areaTurret; i++)
        {
            areaTurret = IsAreaStation(unit, stations[i]);
        }

        if (!areaTurret)
        {
            return true;
        }

        FactionHQ? hq = unit.NetworkHQ;
        return hq != null
            && hq.TryGetKnownPosition(targetCandidate, out GlobalPosition known)
            && CommanderGameAccess.HorizontalDistance(known.ToLocalPosition(), order.Center.ToLocalPosition()) <= order.Area.Radius;
    }

    /// <summary>
    /// Points an area-fire round at its spot in the area. A gun hands its shell whatever target it
    /// last had (<c>Gun.proximityFuseTarget</c>), which can be a unit long gone from the area, so
    /// anything that is not a live target inside the area is replaced, and the seeker's own target
    /// is dropped with it or its datalink would steer the round back to that unit.
    /// </summary>
    internal static void InertialInitializePostfix(InertialSeekerShell seeker, Unit target)
    {
        if (Orders.Count == 0)
        {
            return;
        }

        Missile missile = SeekerMissile(seeker);
        if (missile == null || !TryGetOrder(missile.owner, out AreaOrder order))
        {
            return;
        }

        FactionHQ? hq = missile.owner.NetworkHQ;
        if (target != null
            && !target.disabled
            && hq != null
            && hq.TryGetKnownPosition(target, out GlobalPosition known)
            && CommanderGameAccess.HorizontalDistance(known.ToLocalPosition(), order.Center.ToLocalPosition()) <= order.Area.Radius)
        {
            return;
        }

        // The velocity goes too: Initialize copied the old target's, and GetBallisticAimPoint leads
        // the aim point by it for the whole flight, which put rounds kilometres off the area.
        InertialKnownPos(seeker) = order.AimPoint;
        InertialKnownVel(seeker) = Vector3.zero;
        SeekerTargetUnit(seeker) = null!;
        TrackRound(missile, order.AimPoint);
    }

    /// <summary>
    /// The ballistic missile's twin of <see cref="InertialInitializePostfix"/>. With no target,
    /// <c>BallisticMissileGuidance.Initialize</c> aims 100 km straight ahead of the launcher and
    /// every later step flies at that <c>knownPos</c> (it only refreshes it from a live target), so
    /// writing the area point there, with the launch angle recomputed the way Initialize does, is
    /// the whole of it. A live target inside the area is left alone, as for the shells.
    /// </summary>
    internal static void BallisticInitializePostfix(BallisticMissileGuidance seeker)
    {
        if (Orders.Count == 0)
        {
            return;
        }

        Missile missile = SeekerMissile(seeker);
        if (missile == null || !TryGetOrder(missile.owner, out AreaOrder order))
        {
            return;
        }

        FactionHQ? hq = missile.owner.NetworkHQ;
        Unit live = SeekerTargetUnit(seeker);
        if (live != null
            && !live.disabled
            && hq != null
            && hq.TryGetKnownPosition(live, out GlobalPosition known)
            && CommanderGameAccess.HorizontalDistance(known.ToLocalPosition(), order.Center.ToLocalPosition()) <= order.Area.Radius)
        {
            return;
        }

        GlobalPosition aim = order.AimPoint;
        BallisticKnownPos(seeker) = aim;
        BallisticKnownVel(seeker) = Vector3.zero;
        SeekerTargetUnit(seeker) = null!;
        Vector3 flat = aim - missile.GlobalPosition();
        flat.y = 0f;
        float deltaV = missile.GetRemainingDeltaV();
        float angle = Mathf.Asin(flat.magnitude * 9.81f / (deltaV * deltaV)) * 0.5f * Mathf.Rad2Deg;
        angle = angle > 45f ? angle : 90f - angle;
        BallisticTargetAngle(seeker) = float.IsNaN(angle) ? 45f : angle;
        missile.SetAimpoint(aim, Vector3.zero);
        TrackRound(missile, aim);
    }

    /// <summary>Logs where each area round lands against where it was sent.</summary>
    internal static void MissileDetonatePostfix(Missile missile)
    {
        if (RoundsInFlight.Count == 0 || !RoundsInFlight.TryGetValue(missile, out GlobalPosition aim))
        {
            return;
        }

        RoundsInFlight.Remove(missile);
        float miss = CommanderGameAccess.HorizontalDistance(missile.GlobalPosition().AsVector3(), aim.AsVector3());
        CommanderPlugin.Log.LogInfo($"AREA FIRE: round landed {miss:0} m from its aim point.");
    }

    /// <summary>
    /// <c>InertialSeekerShell.UpdateTargetPosition</c> calls <c>IsTargetBeingTracked(targetUnit)</c>
    /// without a null check, so a round with no target throws every physics step and never runs
    /// <c>UpdateTrajectory</c>: it flies ballistic off the barrel, kilometres from the aim point.
    /// With no target there is nothing to update, so skipping it is the whole fix.
    /// </summary>
    internal static bool InertialUpdateTargetPrefix(InertialSeekerShell seeker)
    {
        return SeekerTargetUnit(seeker) != null;
    }

    /// <summary>The same null in <c>SlowChecks</c>, whose last lines only tighten the error for a
    /// tracked or lased target. Everything above them has run by the time it throws.</summary>
    internal static Exception? InertialSlowChecksFinalizer(InertialSeekerShell seeker, Exception? exception)
    {
        return exception != null && SeekerTargetUnit(seeker) == null ? null : exception;
    }

    /// <summary>The AREA FIRE button's units: ballistic launchers take an attack order instead.</summary>
    private static bool CanPlayerAreaFire(Unit? unit)
    {
        return CanAreaFire(unit) && !IsBallisticLauncher(unit);
    }

    private static void BeginTargeted(FactionHQ? hq, string[] args, int first)
    {
        if (hq == null || !CommanderNetwork.TryUnit(args[first], out Unit target) || target.disabled)
        {
            return;
        }

        GlobalPosition center = hq.TryGetKnownPosition(target, out GlobalPosition known) ? known : target.GlobalPosition();
        MapArea area = new(center, MinDiameterMeters * 0.5f);
        int given = 0;
        for (int i = first + 1; i < args.Length; i++)
        {
            if (CommanderNetwork.TryUnit(args[i], out Unit unit) && IsBallisticLauncher(unit) && Begin(unit, hq, area, target))
            {
                given++;
            }
        }

        if (given == 0)
        {
            CommanderNotice.Fail("LAUNCH: the target is out of reach of every selected launcher.");
        }
    }

    private static bool Begin(Unit unit, FactionHQ? hq, MapArea area, Unit? target = null)
    {
        GlobalPosition center = area.Center;
        if (!CanAreaFire(unit) || hq == null || !ReferenceEquals(unit.NetworkHQ, hq) || !InReachOfAnyStation(unit, center, target != null ? 0f : area.Radius))
        {
            return false;
        }

        // Shelling is done from where the unit stands: stop it first (which also drops any other
        // order), then record the area. The stop is what makes the tick's "has an order again"
        // test mean "the player gave it something else".
        CommanderMoveService.Instance?.HoldForAreaFire(unit);
        Remove(unit);
        area.Units++;
        Orders[unit] = new AreaOrder(area, target, hq);
        CommanderPlugin.Log.LogInfo($"AREA FIRE: {CommanderGameAccess.GetUnitLabel(unit)} shelling {CommanderNetwork.Position(center)}.");
        return true;
    }

    /// <summary>The ring's radius in metres, but never smaller than <see cref="MinRingPixels"/> on
    /// screen at the map's current zoom.</summary>
    private static float VisibleRadius(DynamicMap map, float radius)
    {
        float pixelsPerMeter = map.mapDisplayFactor * (map.iconLayer != null ? map.iconLayer.transform.lossyScale.x : 0f);
        return pixelsPerMeter > 0f ? Mathf.Max(radius, MinRingPixels / pixelsPerMeter) : radius;
    }

    private static void TrackRound(Missile missile, GlobalPosition aim)
    {
        if (RoundsInFlight.Count > 64)
        {
            // A round that is destroyed without detonating never reaches the postfix.
            LandedRounds.Clear();
            foreach (Missile round in RoundsInFlight.Keys)
            {
                if (round == null)
                {
                    LandedRounds.Add(round!);
                }
            }

            for (int i = 0; i < LandedRounds.Count; i++)
            {
                RoundsInFlight.Remove(LandedRounds[i]);
            }
        }

        RoundsInFlight[missile] = aim;
    }

    /// <summary>Whenever an area's diameter changes, logs the ring's drawn width against the intended
    /// width. The ring is the exclusion-zone prefab, whose image size is asset data.</summary>
    private static void ReportRingSize(DynamicMap map, MapArea area)
    {
        if (Mathf.Approximately(area.ReportedRadius, area.Radius) || area.Visual == null || area.Visual.transform is not RectTransform rect || map.iconLayer == null)
        {
            return;
        }

        area.ReportedRadius = area.Radius;
        float drawn = rect.rect.width * rect.lossyScale.x;
        float wanted = area.Radius * 2f * map.mapDisplayFactor * map.iconLayer.transform.lossyScale.x;
        CommanderPlugin.Log.LogInfo($"AREA FIRE: ring drawn {drawn:0.0} px wide for a {area.Radius * 2f:0} m area that should be {wanted:0.0} px (prefab rect {rect.rect.width:0.##}).");
    }

    private static bool Remove(Unit unit)
    {
        if (!Orders.TryGetValue(unit, out AreaOrder order))
        {
            return false;
        }

        Orders.Remove(unit);
        ReleaseArea(order.Area);

        // Put the erector back down if this order raised it, as the base game's own salvo does.
        if (order.RaiseRequestedAt > 0f && unit != null && !unit.disabled && unit.IsServer && unit is GroundVehicle vehicle)
        {
            vehicle.RpcDeployFireControl(deploy: false);
        }

        return true;
    }

    /// <summary>
    /// A strategic launcher's erector is a <c>FireControl</c> deployable that the base game raises
    /// only inside its own <c>LaunchSalvo</c> (<c>RpcDeployFireControl</c>, then waits for
    /// <c>deployed</c>). Fired stowed, the missile would leave the rail flat. Re-asked every
    /// <see cref="RaiseRetrySeconds"/> in case the base game's own salvo stowed it again.
    /// </summary>
    private static bool IsLauncherRaised(Unit unit, AreaOrder order)
    {
        if (unit is not GroundVehicle vehicle
            || !unit.TryGetComponent(out FireControl fireControl)
            || FireControlDeployables.GetValue(fireControl) is not Array { Length: > 0 }
            || FireControlDeployed(fireControl))
        {
            return true;
        }

        if (Time.time - order.RaiseRequestedAt > RaiseRetrySeconds)
        {
            order.RaiseRequestedAt = Time.time;
            vehicle.RpcDeployFireControl(deploy: true);
        }

        return false;
    }

    private static float ClampRadius(float radius)
    {
        return Mathf.Clamp(radius, MinDiameterMeters * 0.5f, MaxDiameterMeters * 0.5f);
    }

    private static void SendPendingRadius()
    {
        if (!radiusSendPending || !CommanderNetwork.IsGuest || Time.unscaledTime < nextRadiusSendAt)
        {
            radiusSendPending &= CommanderNetwork.IsGuest;
            return;
        }

        radiusSendPending = false;
        nextRadiusSendAt = Time.unscaledTime + RadiusSendIntervalSeconds;
        IReadOnlyList<Unit>? selection = CommanderSelectionService.Instance?.SelectedUnits;
        if (selection == null)
        {
            return;
        }

        List<string> args = new() { CommanderNetwork.Num(GetSelectionDiameter(selection) * 0.5f) };
        for (int i = 0; i < selection.Count; i++)
        {
            if (IsAreaFiring(selection[i]))
            {
                args.Add(CommanderNetwork.Id(selection[i]));
            }
        }

        CommanderNetwork.Forward("areafire.radius", args.ToArray());
    }

    private static void ReleaseArea(MapArea area)
    {
        area.Units--;
        if (area.Units <= 0)
        {
            area.Visual = DestroyVisual(area.Visual);
        }
    }

    private static GameObject? DestroyVisual(GameObject? visual)
    {
        if (visual != null)
        {
            UnityEngine.Object.Destroy(visual);
        }

        return null;
    }

    private static bool InReachOfAnyStation(Unit unit, GlobalPosition center, float radius)
    {
        float distance = FastMath.Distance(unit.GlobalPosition(), center);
        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            WeaponStation station = unit.weaponStations[i];
            if (IsAreaStation(unit, station)
                && distance <= station.WeaponInfo.targetRequirements.maxRange + radius
                && distance >= station.WeaponInfo.targetRequirements.minRange - radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Unguided indirect fire: an artillery gun firing plain shells, or a rocket that
    /// only knows a map point.</summary>
    private static bool IsAreaStation(Unit unit, WeaponStation? station)
    {
        if (station?.WeaponInfo == null || !station.HasTurret() || station.Weapons.Count == 0 || station.Weapons[0] == null)
        {
            // Diagnostic: log why a station is refused.
            string key = unit.definition?.jsonKey + "#" + (station?.Number ?? 255);
            if (station != null && ReportedSkips.Add(key))
            {
                CommanderPlugin.Log.LogInfo(
                    $"AREA FIRE: {CommanderGameAccess.GetUnitLabel(unit)} station {station.Number} skipped: info {(station.WeaponInfo == null ? "none" : station.WeaponInfo.weaponName)}, "
                    + $"turrets {station.Turrets?.Count ?? -1}, weapons {station.Weapons?.Count ?? -1}, first {(station.Weapons?.Count > 0 && station.Weapons[0] != null ? station.Weapons[0].GetType().Name : "none")}.");
            }

            return false;
        }

        WeaponInfo info = station.WeaponInfo;
        if (!CapableStores.TryGetValue(info, out bool capable))
        {
            capable = Classify(unit, station);
            CapableStores[info] = capable;

            // Which gun has an artillery solver and which rocket an inertial seeker is prefab data,
            // so this line is the only way to see what the button will take.
            CommanderPlugin.Log.LogInfo($"AREA FIRE: {info.weaponName} {(capable ? "can" : "cannot")} shell an area.");
        }

        return capable;
    }

    private static bool Classify(Unit unit, WeaponStation station)
    {
        Weapon weapon = station.Weapons[0];
        Turret first = station.Turrets[0];
        CommanderPlugin.Log.LogInfo(
            $"AREA FIRE: {CommanderGameAccess.GetUnitLabel(unit)} {station.WeaponInfo.weaponName}: {weapon.GetType().Name}, gun {station.WeaponInfo.gun}, missile {station.WeaponInfo.missile}, "
            + $"artillery solver {first != null && TurretAimSolver(first) != null && SolverArtillery(TurretAimSolver(first))}, "
            + $"type {(unit.definition as VehicleDefinition)?.vehicleType.ToString() ?? "none"}"
            + (weapon is Gun g ? $", shell seeker {GunGuidedProjectile(g)?.unitPrefab?.GetComponentInChildren<MissileSeeker>(true)?.GetType().Name ?? "none"}" : string.Empty)
            + (weapon is MissileLauncher l ? $", seeker {l.missile?.unitPrefab?.GetComponentInChildren<MissileSeeker>(true)?.GetType().Name ?? "none"}" : string.Empty)
            + ".");
        if (weapon is Gun gun)
        {
            // The HLT's 155 mm fires its shell as a missile with the inertial "shell" seeker, which
            // only knows a map point, the same class as the MLRS rocket. Anything that homes is out.
            MissileDefinition? shell = GunGuidedProjectile(gun);
            if (shell != null && !HasInertialSeeker(shell))
            {
                return false;
            }

            Turret turret = station.Turrets[0];
            return (turret != null && TurretAimSolver(turret) != null && SolverArtillery(TurretAimSolver(turret)))
                || (unit.definition is VehicleDefinition { vehicleType: VehicleType.ART });
        }

        if (weapon is not MissileLauncher launcher || launcher.missile?.unitPrefab == null)
        {
            return false;
        }

        // A ballistic missile (the nuclear launchers) flies at a map point with no target just as
        // happily; its point is written in BallisticInitializePostfix.
        if (launcher.missile.unitPrefab.GetComponentInChildren<MissileSeeker>(true) is BallisticMissileGuidance)
        {
            BallisticStores.Add(station.WeaponInfo);
            return true;
        }

        return HasInertialSeeker(launcher.missile);
    }

    private static bool HasInertialSeeker(MissileDefinition missile)
    {
        return missile.unitPrefab != null
            && missile.unitPrefab.GetComponentInChildren<MissileSeeker>(true) is InertialSeekerShell;
    }

    private static bool TryGetOrder(Unit? unit, out AreaOrder order)
    {
        order = null!;
        return unit != null && Orders.TryGetValue(unit, out order);
    }

    /// <summary>The base game's own artillery elevation (<c>AimSolver.GetAimVector</c>'s artillery
    /// branch) for a point rather than a unit, without its "is the ground flat" test: an area shoot
    /// is already scattered over a quarter of a kilometre. Missiles aim straight at the point, as
    /// the base game does, and let the seeker fly the arc.</summary>
    private static Vector3 GetAimVector(Turret turret, WeaponStation station, GlobalPosition point, float range)
    {
        WeaponInfo info = station.WeaponInfo;
        GlobalPosition muzzle = station.Weapons[0].transform.GlobalPosition();
        Vector3 toPoint = point - muzzle;
        if (info.missile)
        {
            return toPoint;
        }

        float speed = info.GetMaxSpeed();
        AimSolver solver = TurretAimSolver(turret);
        if (solver != null && SolverArtillery(solver) && range / info.targetRequirements.maxRange > 0.2f)
        {
            float drag = SolverDragCoef(solver);
            float effective = Mathf.Lerp(range * (1f + (0.5f * drag)), range * (1f + (1.5f * drag)), range / info.targetRequirements.maxRange);
            float elevation = 0.5f * Mathf.Asin(Mathf.Min(effective * 9.81f / (speed * speed), 1f));
            Vector3 flat = new(toPoint.x, 0f, toPoint.z);
            return flat.normalized + (Vector3.up * Mathf.Tan(elevation));
        }

        if (station.Weapons[0] is not Gun)
        {
            return toPoint;
        }

        float flight = range / Mathf.Max(speed * 0.9f, 1f);
        return toPoint + (flight * flight * 4.905f * info.gravMult * Vector3.up);
    }

    private sealed class MapArea
    {
        internal MapArea(GlobalPosition center, float radius)
        {
            Center = center;
            Radius = radius;
        }

        internal GlobalPosition Center { get; }

        internal float Radius { get; set; }

        internal int Units { get; set; }

        internal bool Shown { get; set; }

        internal float ReportedRadius { get; set; }

        internal GameObject? Visual { get; set; }
    }

    private sealed class AreaOrder
    {
        private const float SetupWaitSeconds = 10f;
        private string lastReport = string.Empty;
        private float settingUpSince = -1f;

        internal AreaOrder(MapArea area, Unit? target, FactionHQ hq)
        {
            Area = area;
            Target = target;
            Hq = hq;
            AimPoint = area.Center;
            PickAimPoint();
        }

        /// <summary>Set for a launcher's attack order: every round goes at this unit's last known spot.</summary>
        internal Unit? Target { get; }

        internal FactionHQ Hq { get; }

        internal bool TargetLost => Target is not null && (Target == null || Target.disabled);

        internal MapArea Area { get; }

        internal GlobalPosition Center => Area.Center;

        internal GlobalPosition AimPoint { get; private set; }

        /// <summary><see cref="Time.time"/> this order last asked the erector up; 0 if never.</summary>
        internal float RaiseRequestedAt { get; set; }

        /// <summary>Logs what the gun is doing whenever that changes, so it is clear why an area order is
        /// not firing.</summary>
        internal void Report(Unit unit, string state)
        {
            if (state != lastReport)
            {
                lastReport = state;
                CommanderPlugin.Log.LogInfo($"AREA FIRE: {CommanderGameAccess.GetUnitLabel(unit)} {state}.");
            }
        }

        /// <summary>True for the first <c>SetupWaitSeconds</c> after the gun stops, false after that.</summary>
        internal bool StillSettingUp()
        {
            if (settingUpSince < 0f)
            {
                settingUpSince = Time.time;
            }

            return Time.time - settingUpSince < SetupWaitSeconds;
        }

        internal void ResetSetupWait()
        {
            settingUpSince = -1f;
        }

        internal void PickAimPoint()
        {
            if (Target is not null)
            {
                if (Target != null && Hq.TryGetKnownPosition(Target, out GlobalPosition known))
                {
                    AimPoint = known;
                }

                return;
            }

            Vector2 offset = UnityEngine.Random.insideUnitCircle * Area.Radius;
            AimPoint = Center + new Vector3(offset.x, 0f, offset.y);
        }
    }
}
