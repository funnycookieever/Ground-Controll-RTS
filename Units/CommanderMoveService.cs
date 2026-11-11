using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>Per-unit engagement stance. Applies whether or not the unit holds an order.</summary>
internal enum CommanderStance
{
    /// <summary>Shoots freely and, while travelling, diverts to hostiles that come into range.</summary>
    FreeEngage,

    /// <summary>Turrets acquire nothing and the unit never diverts.</summary>
    HoldFire,

    /// <summary>Holds the ground it stands on and ignores travel orders, but still shoots.</summary>
    HoldPosition,
}

/// <summary>Action a unit performs on arrival at a travel point.</summary>
internal enum CommanderWaypointAction
{
    None,
    Hold,
    RadarOff,
    RadarOn,
}

/// <summary>
/// Owns every Commander-issued ground/ship order: multi-point travel routes, patrol loops,
/// attack and guard orders, stances and the stop/resume states. Aircraft orders are forwarded
/// to <see cref="CommanderAirCommandService"/>, which drives the AI pilot instead.
/// </summary>
internal sealed class CommanderMoveService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>A ground unit parked this close to a store at the end of its route rearms from it.
    /// The store's selection ring draws it.</summary>
    internal const float GroundRearmReachMeters = 300f;

    private const float GroundFormationSpacingMeters = 25f;
    private const float ShipFormationSpacingMeters = 80f;
    private const float RouteTickIntervalSeconds = 0.25f;
    private const float StuckTimeoutSeconds = 60f;
    private const float GuardArrivalMeters = 120f;
    private const float SpotStaleSeconds = 12f;
    private const float EngagementLeash = 1.6f;
    private const float RetreatTimeoutSeconds = 240f;

    /// <summary>Damage, 0-1, a unit has to take after a retreat before it auto-retreats again.</summary>
    private const float RetreatAgainDamage = 0.1f;

    /// <summary>How close a ship has to get to a store to rearm from it. A bunker stands on land, so
    /// the ship only ever reaches the shore in front of it.</summary>
    private const float ShipRearmReachMeters = 1500f;
    private const float TruckRefillReachMeters = 150f;
    private const float StopPinSpeedSq = 2f * 2f;

    private static readonly MethodInfo? RearmVehicleWaitMethod = AccessTools.Method(typeof(RearmVehicleAI), "Wait");
    private static readonly MethodInfo? RearmVehicleRestockMethod = AccessTools.Method(typeof(RearmVehicleAI), "DriveToRestock");

    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<Unit> stoppedUnits = new();
    private readonly Dictionary<Unit, GlobalPosition> playerDestinations = new();
    private readonly Dictionary<Unit, UnitOrder> orders = new();
    private readonly Dictionary<Unit, CommanderStance> stances = new();

    /// <summary>IR SAMs told to shoot only at missiles, leaving aircraft and ground units alone.</summary>
    private readonly HashSet<Unit> missileOnlyUnits = new();
    private readonly List<Unit> staleOrders = new();
    private readonly List<Unit> hostiles = new();
    private readonly List<Unit> staleStances = new();
    private readonly List<Unit> attackScratch = new();
    private float nextRouteTickAt;
    private int routeTick;

    internal CommanderMoveService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
        CommanderNetwork.OnHost("stance", HostStance);
        CommanderNetwork.OnHost("missileonly", HostMissileOnly);
        CommanderNetwork.OnHost("dest", HostDestinations);
        CommanderNetwork.OnHost("fire", CommanderFireNow.HostFire);
        CommanderNetwork.OnHost("areafire", CommanderAreaFire.HostAreaFire);
        CommanderNetwork.OnHost("areafire.radius", CommanderAreaFire.HostAreaRadius);
        CommanderNetwork.OnHost("areafire.stop", CommanderAreaFire.HostAreaStop);
    }

    internal static CommanderMoveService? Instance { get; private set; }

    internal static CommanderFormationShape Formation
    {
        get => (CommanderFormationShape)Mathf.Clamp(CommanderSettings.FormationShape, 0, 3);
        set => CommanderSettings.FormationShape = (int)value;
    }

    internal bool HasCommandableSelection
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                if (CommanderGameAccess.ShouldAllowCommanderMove(selectionService.SelectedUnits[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>True while at least one unit holds an attack order, so the turret focus-fire patch can bail out cheaply.</summary>
    internal bool HasAttackOrders { get; private set; }

    /// <summary>True while at least one unit is told to hold fire, so the turret patch can bail out cheaply.</summary>
    internal bool HasHoldFireUnits { get; private set; }

    /// <summary>How many units hold an RTS order of any kind. The tutorial asks.</summary>
    internal int OrderCount => orders.Count;

    /// <summary>Action attached to the next travel point the player places.</summary>
    internal CommanderWaypointAction PendingWaypointAction { get; private set; }

    /// <summary>True when the selection holds at least one aircraft the commander can task.</summary>
    internal bool HasSelectedAircraft
    {
        get
        {
            IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
            for (int i = 0; i < selection.Count; i++)
            {
                if (selection[i] is Aircraft aircraft && CommanderAirCommandService.IsTaskableAircraft(aircraft))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Stance of the focused unit, used to label the selection-bar button.</summary>
    internal CommanderStance SelectionStance => GetStance(selectionService.FocusedSelection);

    internal bool IsSelectionPatrolling
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                Unit unit = selectionService.SelectedUnits[i];
                if ((orders.TryGetValue(unit, out UnitOrder order) && order.Loop)
                    || (unit is Aircraft aircraft && CommanderAirCommandService.Instance?.IsAircraftPatrolling(aircraft) == true))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>True when at least one selected unit has a route long enough to patrol.</summary>
    internal bool CanSelectionPatrol
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                Unit unit = selectionService.SelectedUnits[i];
                if ((orders.TryGetValue(unit, out UnitOrder order) && order.Waypoints.Count > 1)
                    || (unit is Aircraft aircraft && CommanderAirCommandService.Instance?.CanAircraftPatrol(aircraft) == true))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void TickPersistent()
    {
        stoppedUnits.RemoveWhere(static unit => unit == null || unit.disabled);
        List<Unit>? staleDestinations = null;
        foreach (KeyValuePair<Unit, GlobalPosition> entry in playerDestinations)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && CommanderGameAccess.HorizontalDistance(entry.Key.transform.position, entry.Value.ToLocalPosition()) > 25f)
            {
                continue;
            }

            staleDestinations ??= new List<Unit>();
            staleDestinations.Add(entry.Key!);
        }

        if (staleDestinations != null)
        {
            for (int i = 0; i < staleDestinations.Count; i++)
            {
                playerDestinations.Remove(staleDestinations[i]);
            }
        }

        AdvanceRoutes(force: false);
        CommanderAreaFire.Tick(this);
        CommanderAreaFire.TickVisuals();

        if (stoppedUnits.Count == 0)
        {
            return;
        }

        foreach (Unit unit in stoppedUnits)
        {
            // A kinematic body is already still, and writing to one logs a Unity warning every frame
            // (24,000 lines in one Champions match).
            if (unit.rb == null || unit.rb.isKinematic)
            {
                continue;
            }

            // Only pin a unit that has already braked. Zeroing a moving one is a 100+ G stop in one
            // physics step, and a vehicle that was ever sling-lifted may still carry the game's
            // ImpactDetector, which destroys it for exactly that.
            if (unit.rb.velocity.sqrMagnitude > StopPinSpeedSq)
            {
                continue;
            }

            unit.rb.velocity = Vector3.zero;
            unit.rb.angularVelocity = Vector3.zero;
        }
    }

    public void ResetSession()
    {
        orders.Clear();
        stances.Clear();
        missileOnlyUnits.Clear();
        stoppedUnits.Clear();
        playerDestinations.Clear();
        hostiles.Clear();
        CommanderOrderPing.Clear();
        CommanderAreaFire.Reset();
        PendingWaypointAction = CommanderWaypointAction.None;
        HasAttackOrders = false;
        HasHoldFireUnits = false;
    }

    /// <summary>True when the unit was told to hold fire, so its turrets acquire nothing.</summary>
    internal static bool IsHoldingFire(Unit? unit)
    {
        return unit != null
            && Instance != null
            && Instance.stances.TryGetValue(unit, out CommanderStance stance)
            && stance == CommanderStance.HoldFire;
    }

    internal static bool IsIrSam(Unit? unit)
    {
        return unit?.definition is VehicleDefinition { vehicleType: VehicleType.IR_SAM };
    }

    /// <summary>True when the unit's turrets may only take missiles as targets.</summary>
    internal static bool IsMissileOnly(Unit? unit)
    {
        return unit != null && Instance != null && Instance.missileOnlyUnits.Contains(unit);
    }

    /// <summary>True when <paramref name="candidate"/> is the unit that <paramref name="shooter"/> was ordered to attack.</summary>
    internal static bool IsOrderedTarget(Unit? shooter, Unit? candidate)
    {
        return shooter != null
            && candidate != null
            && Instance != null
            && Instance.orders.TryGetValue(shooter, out UnitOrder order)
            && ReferenceEquals(order.EffectiveTarget, candidate);
    }

    /// <summary>
    /// Where <paramref name="candidate"/> sits in a ship's ordered target list (0 the current
    /// target, then the queue), or -1. A ship fires on its whole list at once; a ground unit works it
    /// in turn, so only a ship is asked.
    /// </summary>
    internal static int GetShipTargetRank(Unit? shooter, Unit? candidate)
    {
        if (shooter is not Ship
            || candidate == null
            || Instance == null
            || !Instance.orders.TryGetValue(shooter, out UnitOrder order))
        {
            return -1;
        }

        if (ReferenceEquals(order.EffectiveTarget, candidate))
        {
            return 0;
        }

        int queued = order.QueuedTargets.IndexOf(candidate);
        return queued < 0 ? -1 : queued + 1;
    }

    /// <summary>True when the shooter has been ordered onto a target other than the one it holds, so
    /// its turrets must re-scan now rather than on the slowed big-battle cadence.</summary>
    internal static bool WantsRetarget(Unit? shooter, Unit? current)
    {
        return shooter != null
            && Instance is { HasAttackOrders: true }
            && Instance.orders.TryGetValue(shooter, out UnitOrder order)
            && order.EffectiveTarget != null
            && !ReferenceEquals(order.EffectiveTarget, current)
            && !(shooter is Ship && current != null && order.QueuedTargets.Contains(current));
    }

    internal static void NotifyPlayerDestination(UnitCommand command, GlobalPosition destination, Player? player)
    {
        if (player == null || Instance == null)
        {
            return;
        }

        Unit? unit = command.GetComponent<Unit>();
        if (unit == null || !CommanderGameAccess.ShouldAllowCommanderMove(unit))
        {
            return;
        }

        Instance.stoppedUnits.Remove(unit);
        Instance.playerDestinations[unit] = destination;
    }

    internal static void CycleFormation()
    {
        Formation = Formation switch
        {
            CommanderFormationShape.Ring => CommanderFormationShape.Line,
            CommanderFormationShape.Line => CommanderFormationShape.Column,
            CommanderFormationShape.Column => CommanderFormationShape.Wedge,
            _ => CommanderFormationShape.Ring,
        };
    }

    /// <summary>
    /// Resolves a screen click into a travel point, into an attack order when a hostile unit
    /// sits under the cursor, or into a guard order on a friendly one, and applies it to the
    /// whole selection.
    /// </summary>
    internal void TryIssueMoveOrder(Vector2 screenPosition)
    {
        if (selectionService.SelectedUnits.Count == 0)
        {
            return;
        }

        bool append = CommanderShortcutInput.IsPressed(CommanderSettings.QueueWaypoint);
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();

        // Only repair vehicles selected and the click on a blueprint: that is a build order.
        if (CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition buildPoint)
            && CommanderEconomyService.Instance?.TryAssignBuilders(selectionService.SelectedUnits, buildPoint, append) == true)
        {
            return;
        }

        // The world marker is the only thing you can actually hit when the target is a
        // distant contact or an aircraft: its collider is a few pixels wide at command
        // altitude, so a raycast alone made attack orders feel like they did nothing.
        Unit? hovered = CommanderMarkerService.Instance?.TryGetMarkerUnitAt(screenPosition, out Unit markerUnit) == true
            ? markerUnit
            : null;
        if (hovered == null && CommanderGameAccess.TryRaycastHostileUnit(screenPosition, out Unit clickedUnit))
        {
            hovered = clickedUnit;
        }

        Unit? attackTarget = hovered != null && !CommanderGameAccess.IsFriendlyUnit(hovered, localHq)
            ? hovered
            : null;
        Unit? friendly = attackTarget == null
            && (hovered ?? (CommanderGameAccess.TryRaycastSelectableUnit(screenPosition, out Unit friendlyUnit) ? friendlyUnit : null)) is Unit clickedFriendly
            && CommanderGameAccess.IsFriendlyUnit(clickedFriendly, localHq)
            ? clickedFriendly
            : null;

        // A friendly store under the cursor (munitions bunker, depot, naval supply pallet) means "go and
        // rearm there", not "escort it".
        if (friendly != null && IssueResupplyOrder(friendly, append))
        {
            return;
        }

        if (friendly != null && CommanderSettings.GuardOrders && IssueGuardOrder(friendly))
        {
            return;
        }

        bool hasGroundDestination = CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition groundDestination);
        bool hasWaterDestination = CommanderGameAccess.TryRaycastWaterPosition(screenPosition, out GlobalPosition waterDestination);

        // Resolved once, before the attack decision. Not a pure query (an order near a base being taken
        // reports leaving it), so asking twice would invert its answer. Skipped when the cursor is on a
        // mobile unit, so a plain attack order is unchanged.
        GlobalPosition captureGround = groundDestination;
        CommanderCaptureService.CaptureTarget target = default;
        bool captureResolved = (attackTarget == null || attackTarget is Building)
            && hasGroundDestination
            && TryClaimCapturePoint(ref captureGround, append, out target);

        // A base you are about to take is a field of hostile buildings, and every one of them is a
        // unit under the cursor, so the attack branch above swallowed nearly every capture click:
        // aiming at the marker only worked where it landed on bare ground between hangars. Inside
        // the ring a civilian building is scenery. A mobile unit, or a building that is a target in
        // its own right — hangar, radar, depot, air-defence post, bunker, factory — is still an
        // attack: treating those as scenery too left units trying to capture a defended base when
        // the player had clicked on its defences.
        if (captureResolved
            && attackTarget is Building
            && attackTarget.definition is BuildingDefinition { buildingType: BuildingType.CIV })
        {
            attackTarget = null;
        }

        if (attackTarget != null)
        {
            groundDestination = attackTarget.GlobalPosition();
            waterDestination = groundDestination;
            hasGroundDestination = true;
            hasWaterDestination = true;
        }

        GlobalPosition ground = groundDestination;
        GlobalPosition water = waterDestination;
        bool groundValid = hasGroundDestination;
        bool waterValid = hasWaterDestination;
        bool capture = attackTarget == null && captureResolved;
        if (capture)
        {
            // Ships have no capture strength, so a mixed selection snaps its ground units onto the ring but
            // leaves a ship's own destination alone.
            ground = captureGround;
        }

        ApplyOrder(
            unit => unit is Ship ? (waterValid, water) : (groundValid, ground),
            attackTarget,
            append,
            capture ? target : null);
        if (capture)
        {
            CommanderCaptureService.Instance?.AnnounceCaptureOrder(target, selectionService.SelectedUnits[0]);
        }
    }

    /// <summary>Same as the world order, but from a tactical-map coordinate.</summary>
    internal void IssueOrderAt(GlobalPosition point, Unit? attackTarget, bool append)
    {
        if (selectionService.SelectedUnits.Count == 0)
        {
            return;
        }

        if (attackTarget == null
            && CommanderEconomyService.Instance?.TryAssignBuilders(selectionService.SelectedUnits, point, append) == true)
        {
            return;
        }

        GlobalPosition destination = attackTarget != null ? attackTarget.GlobalPosition() : point;
        CommanderCaptureService.CaptureTarget target = default;
        bool capture = attackTarget == null
            && TryClaimCapturePoint(ref destination, append, out target);

        // Ships have no capture strength: on a capture order they keep the clicked point.
        ApplyOrder(
            unit => (true, capture && unit is Ship ? point : destination),
            attackTarget,
            append,
            capture ? target : null);
        if (capture)
        {
            CommanderCaptureService.Instance?.AnnounceCaptureOrder(target, selectionService.SelectedUnits[0]);
        }
    }

    /// <summary>Sends every aircraft in the selection to the nearest field its faction holds.</summary>
    internal int ResupplySelectedAircraft()
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return 0;
        }

        int sent = 0;
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] is Aircraft aircraft && airCommand.ResupplyAircraft(aircraft))
            {
                sent++;
            }
        }

        return sent;
    }

    /// <summary>Lands every aircraft in the selection at the nearest held field and stocks it.</summary>
    internal void ReserveSelectedAircraft()
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return;
        }

        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] is Aircraft aircraft)
            {
                airCommand.ReserveAircraft(aircraft);
            }
        }
    }

    /// <summary>
    /// Escort order: the selection follows <paramref name="guardTarget"/> in formation and
    /// engages whatever shoots at it, using the same re-tracking as an attack order.
    /// </summary>
    /// <remarks>
    /// Aircraft go to Air Command, exactly as they do for a travel or attack point. This service
    /// steers with <c>SetDestination</c>, which an AI pilot does not take orders from, so before
    /// this an escorting fighter got a formation slot it could not fly and simply carried on with
    /// whatever the base game had it doing.
    /// </remarks>
    internal bool IssueGuardOrder(Unit guardTarget)
    {
        // A downed pilot is someone to pick up, not to defend. False lets anything that is not on
        // rescue duty fall through to a plain move order.
        if (guardTarget is PilotDismounted)
        {
            bool picked = false;
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                if (selectionService.SelectedUnits[i] is Aircraft helo)
                {
                    picked |= CommanderRecoveryService.Instance?.TryOrderPickup(helo, guardTarget, guardTarget.GlobalPosition()) == true;
                }
            }

            return picked;
        }

        int slot = 0;
        bool issued = false;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (ReferenceEquals(unit, guardTarget))
            {
                continue;
            }

            if (unit is Aircraft aircraft)
            {
                issued |= CommanderAirCommandService.Instance?.SetAircraftEscortOrder(aircraft, guardTarget) == true;
                continue;
            }

            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            UnitOrder order = new()
            {
                GuardTarget = guardTarget,
                FormationSlot = ++slot,
                Spacing = unit is Ship ? ShipFormationSpacingMeters : GroundFormationSpacingMeters,
                Shape = Formation == CommanderFormationShape.Ring ? CommanderFormationShape.Wedge : Formation,
            };
            orders[unit] = order;
            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            issued = true;
        }

        if (issued)
        {
            CommanderOrderPing.Show(guardTarget.GlobalPosition(), false);
            RefreshOrderFlags();
            AdvanceRoutes(force: true);
        }

        return issued;
    }

    /// <summary>
    /// Resupply order: every selected ship and ground vehicle goes to <paramref name="store"/> and
    /// draws ammunition from it on arrival (<see cref="TryRearmOnArrival"/>). False when the unit is
    /// not a store or nothing selected can use one, so the click falls through to an escort order.
    /// </summary>
    internal bool IssueResupplyOrder(Unit store, bool append)
    {
        // A fuel farm has no Rearmer but is still where a fuel truck fills up.
        if (!CommanderSupplyStoreService.IsSupplyStore(store))
        {
            return false;
        }

        bool any = false;
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            any |= selection[i] is not Aircraft
                && !ReferenceEquals(selection[i], store)
                && CommanderGameAccess.ShouldAllowCommanderMove(selection[i]);
        }

        if (!any)
        {
            return false;
        }

        GlobalPosition point = store.GlobalPosition();
        ApplyOrder(_ => (true, point), null, append);
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] is not Aircraft && orders.TryGetValue(selection[i], out UnitOrder order))
            {
                order.RearmFrom = store;

                // The base game's own polling also rearms it the moment it is inside the store's
                // reach on the way, rather than only at the end of the route.
                selection[i].RequestRearm();
            }
        }

        CommanderAlertService.Instance?.NotifySupply(
            $"RESUPPLY AT {CommanderGameAccess.GetUnitLabel(store).ToUpperInvariant()}", selection[0]);
        return true;
    }

    /// <summary>True while the unit is held in place by a STOP order.</summary>
    internal bool IsStopped(Unit unit)
    {
        return stoppedUnits.Contains(unit);
    }

    internal CommanderStance GetStance(Unit? unit)
    {
        return unit != null && stances.TryGetValue(unit, out CommanderStance stance)
            ? stance
            : CommanderStance.FreeEngage;
    }

    internal void SetSelectionStance(CommanderStance stance)
    {
        List<string> request = new() { CommanderNetwork.Num((int)stance) };
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit)
                && !(unit is Aircraft aircraft && CommanderAirCommandService.IsTaskableAircraft(aircraft)))
            {
                continue;
            }

            ApplyStance(unit, stance);
            request.Add(CommanderNetwork.Id(unit));
        }

        RefreshOrderFlags();

        // HOLD FIRE is a turret patch, and turrets are aimed on the host: a guest keeps its own copy
        // for the selection bar and route handling, and the host keeps the one that matters.
        if (request.Count > 1)
        {
            CommanderNetwork.Forward("stance", request.ToArray());
        }
    }

    internal void ToggleMissileOnly(Unit unit)
    {
        bool enabled = !missileOnlyUnits.Remove(unit);
        if (enabled)
        {
            missileOnlyUnits.Add(unit);
        }

        // Turret targeting runs on the host; a guest's toggle is only a label without this.
        CommanderNetwork.Forward("missileonly", CommanderNetwork.Id(unit), CommanderNetwork.Flag(enabled));
    }

    internal void CycleSelectionStance()
    {
        SetSelectionStance(SelectionStance switch
        {
            CommanderStance.FreeEngage => CommanderStance.HoldFire,
            CommanderStance.HoldFire => CommanderStance.HoldPosition,
            _ => CommanderStance.FreeEngage,
        });
    }

    internal void CyclePendingWaypointAction()
    {
        PendingWaypointAction = PendingWaypointAction switch
        {
            CommanderWaypointAction.None => CommanderWaypointAction.Hold,
            CommanderWaypointAction.Hold => CommanderWaypointAction.RadarOff,
            CommanderWaypointAction.RadarOff => CommanderWaypointAction.RadarOn,
            _ => CommanderWaypointAction.None,
        };
    }

    /// <summary>Turns the selection's routes into patrols, or back into one-shot routes.</summary>
    internal void ToggleSelectionPatrol()
    {
        bool enable = !IsSelectionPatrolling;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit is Aircraft aircraft)
            {
                CommanderAirCommandService.Instance?.SetAircraftPatrol(aircraft, enable);
            }
            else if (orders.TryGetValue(unit, out UnitOrder order) && order.Waypoints.Count > 1)
            {
                order.Loop = enable;
            }
        }
    }

    internal void RetreatSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            bool existing = orders.TryGetValue(unit, out UnitOrder order);
            if (!existing)
            {
                order = new UnitOrder();
                orders[unit] = order;
            }

            // Nothing to retreat to (a ship with no support near water, say) leaves the unit on
            // whatever it was doing, rather than wiping its route and leaving it idle.
            if (!BeginRetreat(unit, order))
            {
                if (!existing)
                {
                    orders.Remove(unit);
                }

                CommanderAlertService.Instance?.NotifySupply(
                    $"{CommanderGameAccess.GetUnitLabel(unit)} HAS NOWHERE TO RETREAT TO", unit);
                continue;
            }

            order.Waypoints.Clear();
            order.Actions.Clear();
            order.Index = 0;
            order.Loop = false;
            order.GuardTarget = null;
            stoppedUnits.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
        }

        RefreshOrderFlags();
    }

    internal bool TryGetOrder(Unit unit, out IReadOnlyList<GlobalPosition> waypoints, out int index, out Unit? attackTarget)
    {
        if (orders.TryGetValue(unit, out UnitOrder order))
        {
            waypoints = order.Waypoints;
            index = order.Index;
            attackTarget = order.EffectiveTarget;
            return true;
        }

        waypoints = System.Array.Empty<GlobalPosition>();
        index = 0;
        attackTarget = null;
        return false;
    }

    /// <summary>
    /// Every target this unit has been ordered at, the one it is working first: what FIRE reaches
    /// for and what the route line brackets. Dead entries are dropped on the way past.
    /// </summary>
    internal void CollectAttackTargets(Unit unit, List<Unit> into)
    {
        into.Clear();
        if (unit is Aircraft aircraft)
        {
            CommanderAirCommandService.Instance?.CollectAttackTargets(aircraft, into);
            return;
        }

        if (!orders.TryGetValue(unit, out UnitOrder order))
        {
            return;
        }

        if (order.EffectiveTarget is Unit current && !current.disabled)
        {
            into.Add(current);
        }

        for (int i = order.QueuedTargets.Count - 1; i >= 0; i--)
        {
            Unit queued = order.QueuedTargets[i];
            if (queued == null || queued.disabled)
            {
                order.QueuedTargets.RemoveAt(i);
            }
        }

        into.AddRange(order.QueuedTargets);
    }

    /// <summary>Every live target any of <paramref name="hq"/>'s ground units or ships was ordered at,
    /// appended without duplicates. The cockpit markers read it a few times a second.</summary>
    internal void AppendOrderedTargets(FactionHQ hq, List<Unit> into)
    {
        foreach (KeyValuePair<Unit, UnitOrder> entry in orders)
        {
            if (entry.Key == null || !ReferenceEquals(entry.Key.NetworkHQ, hq))
            {
                continue;
            }

            // The commanded target only: one picked up while attack-moving is the unit's own choice.
            AppendLive(entry.Value.AttackTarget);
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

    /// <summary>The unit this one is defending: a ground guard order, or an aircraft's escort.</summary>
    internal bool TryGetDefendTarget(Unit unit, out Unit defended)
    {
        Unit? target = unit is Aircraft aircraft
            ? CommanderAirCommandService.Instance?.GetEscortTarget(aircraft)
            : orders.TryGetValue(unit, out UnitOrder order) ? order.GuardTarget : null;
        defended = target!;
        return target != null && !target.disabled;
    }

    /// <summary>One-line order summary for the unit cards.</summary>
    internal string GetOrderLabel(Unit? unit)
    {
        if (unit == null)
        {
            return string.Empty;
        }

        if (!orders.TryGetValue(unit, out UnitOrder order))
        {
            return !stoppedUnits.Contains(unit) ? string.Empty
                : CommanderAreaFire.IsAreaFiring(unit) ? "AREA FIRE"
                : "HOLDING";
        }

        if (order.Retreating)
        {
            return "RETREATING";
        }

        if (order.GuardTarget != null)
        {
            return $"GUARDING {CommanderGameAccess.GetUnitLabel(order.GuardTarget)}";
        }

        if (order.EffectiveTarget != null)
        {
            return $"ATTACKING {CommanderGameAccess.GetUnitLabel(order.EffectiveTarget)}"
                + (order.QueuedTargets.Count > 0 ? $" +{order.QueuedTargets.Count}" : string.Empty);
        }

        if (order.Waypoints.Count == 0)
        {
            return "HOLDING";
        }

        return order.Loop
            ? $"PATROL {Mathf.Min(order.Index + 1, order.Waypoints.Count)}/{order.Waypoints.Count}"
            : $"MOVING {Mathf.Min(order.Index + 1, order.Waypoints.Count)}/{order.Waypoints.Count}";
    }

    internal bool HasOrder(Unit unit)
    {
        return orders.ContainsKey(unit);
    }

    /// <summary>The unit's current order as an opaque handle: a fresh order is a different object,
    /// an appended travel point is the same one.</summary>
    internal object? GetOrderHandle(Unit unit)
    {
        return orders.TryGetValue(unit, out UnitOrder order) ? order : null;
    }

    internal void ClearOrder(Unit unit)
    {
        if (orders.Remove(unit))
        {
            RefreshOrderFlags();
        }
    }

    /// <summary>
    /// A capture order for one unit that is not in the selection: the depot's AUTO ATTACK. It is the
    /// same order a player's capture click writes (the hold point as the last waypoint, a slot on a
    /// ring round it), so the unit is re-asserted if anything steers it away and spreads out inside
    /// the capture ring instead of queueing for one point behind the unit already standing on it.
    /// </summary>
    internal bool IssueCaptureOrder(Unit unit, GlobalPosition holdPoint, int slot)
    {
        if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
        {
            return false;
        }

        UnitOrder order = new()
        {
            FormationSlot = slot,
            Spacing = GroundFormationSpacingMeters,
            Shape = CommanderFormationShape.Ring,
        };
        order.Waypoints.Add(holdPoint);
        order.Actions.Add(CommanderWaypointAction.None);
        orders[unit] = order;
        stoppedUnits.Remove(unit);
        playerDestinations.Remove(unit);
        CommanderGameAccess.SetUnitHoldPosition(unit, false);
        RefreshOrderFlags();
        AdvanceRoutes(force: true);
        return true;
    }

    internal void StopSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit is Aircraft aircraft)
            {
                CommanderAirCommandService.Instance?.ClearAircraftRoute(aircraft);
                continue;
            }

            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            HoldInPlace(unit);
            orders.Remove(unit);
            CommanderAreaFire.Clear(unit);
        }

        RefreshOrderFlags();
    }

    /// <summary>Area fire is shot from where the unit stands, so it starts as a stop.</summary>
    internal void HoldForAreaFire(Unit unit)
    {
        HoldInPlace(unit);
        if (orders.Remove(unit))
        {
            RefreshOrderFlags();
        }
    }

    internal void ResumeAiForSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            orders.Remove(unit);
            stances.Remove(unit);
            CommanderAreaFire.Clear(unit);
            if (TryReturnToBasegameLogistics(unit))
            {
                continue;
            }

            if (MissionPosition.TryGetClosestPosition(unit, out GlobalPosition destination))
            {
                CommanderGameAccess.SendDestination(unit, destination, playerCommand: false);
            }
        }

        RefreshOrderFlags();
    }

    internal bool TryGetPlayerDestination(Unit unit, out GlobalPosition destination)
    {
        return playerDestinations.TryGetValue(unit, out destination);
    }

    private static bool ShouldRetreat(Unit unit, UnitOrder order)
    {
        float condition = CommanderGameAccess.GetUnitCondition(unit);
        return CommanderSettings.AutoRetreatDamaged
            && order.GuardTarget == null
            && condition <= CommanderSettings.RetreatConditionPercent * 0.01f
            && condition < order.RetreatEndedCondition - RetreatAgainDamage;
    }

    /// <summary>Nearest friendly unit that can repair or rearm this one, and the point to drive to.</summary>
    private static Unit? FindNearestSupportUnit(Unit unit, out GlobalPosition point)
    {
        point = default;
        FactionHQ? hq = unit.NetworkHQ ?? CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return null;
        }

        Unit? best = null;
        float bestDistance = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate.disabled
                || ReferenceEquals(candidate, unit)
                || (candidate.GetComponentInChildren<Repairer>(true) == null
                    && candidate.GetComponentInChildren<Rearmer>(true) == null))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                unit.transform.position, candidate.transform.position);
            if (distance >= bestDistance)
            {
                continue;
            }

            GlobalPosition candidatePoint = candidate.GlobalPosition();
            if (unit is Ship && !TryGetShipRetreatPoint(unit, candidate, out candidatePoint))
            {
                continue;
            }

            bestDistance = distance;
            best = candidate;
            point = candidatePoint;
        }

        return best;
    }

    /// <summary>
    /// End of a resupply route: draws from the store through the base game's own
    /// <c>Rearmer.ProcessRearmRequest</c> (which charges the store and sends the rearm to clients)
    /// if the unit got close enough. A ship ordered onto a bunker on land stops at the shore, hence
    /// the generous reach; a ground vehicle has to be at the store.
    /// </summary>
    private static void TryRearmOnArrival(Unit unit, Unit store)
    {
        Rearmer? rearmer = CommanderSupplyStoreService.GetRearmer(store);
        if (!IsServer())
        {
            return;
        }

        float reach = Mathf.Max(rearmer != null ? rearmer.Range : 0f, unit is Ship ? ShipRearmReachMeters : GroundRearmReachMeters);
        string label = CommanderGameAccess.GetUnitLabel(unit);
        GlobalPosition storePosition = rearmer != null ? rearmer.GetPosition() : store.GlobalPosition();
        if (!FastMath.InRange(unit.GlobalPosition(), storePosition, reach))
        {
            CommanderAlertService.Instance?.NotifySupply($"{label} COULD NOT GET CLOSE ENOUGH TO REARM", unit);
            return;
        }

        // A supply truck cannot be bought into, so this is where it fills up: from a bunker, never
        // from another truck, or two trucks could pass the same load back and forth. An ammo dump
        // is the other way round: nothing else can stock it, so a truck that arrives unloads.
        if (CommanderSupplyStoreService.IsMobileStore(unit) && CommanderSupplyStoreService.IsDump(store))
        {
            string unloaded = CommanderSupplyStoreService.Instance?.UnloadTruck(unit, store) ?? string.Empty;
            CommanderAlertService.Instance?.NotifySupply(
                unloaded.Length > 0 ? unloaded : $"{label} IS EMPTY, OR THE DUMP IS FULL", unit);
        }
        else if (CommanderSupplyStoreService.IsMobileStore(unit) && store is Building)
        {
            string loaded = CommanderSupplyStoreService.Instance?.RefillTruck(unit, store) ?? string.Empty;
            CommanderAlertService.Instance?.NotifySupply(
                loaded.Length > 0 ? loaded : $"{label} IS FULL, OR THE STORE IS EMPTY", unit);
        }

        if (rearmer == null)
        {
            return;
        }

        float ammoBefore = CommanderGameAccess.GetUnitAmmo(unit);
        bool rearmed = rearmer.ProcessRearmRequest(unit, out int shortfall)
            || CommanderGameAccess.GetUnitAmmo(unit) > ammoBefore;
        CommanderPlugin.Log.LogInfo(
            $"{label} rearmed at {CommanderGameAccess.GetUnitLabel(store)}: "
                + (rearmed ? "loaded" : "nothing loaded") + $", {shortfall} round(s) short.");
        CommanderAlertService.Instance?.NotifySupply(
            rearmed ? $"{label} REARMED" : $"{label} NEEDED NOTHING, OR THE STORE IS EMPTY", unit);
    }

    /// <summary>
    /// A supply truck passing a travel point on a bunker loads up, and on an ammo dump unloads, so a
    /// patrol between the two is a cargo line. A patrol has no last travel point, which is the only
    /// place <see cref="TryRearmOnArrival"/> runs. Quiet when nothing moved, or every lap would alert.
    /// </summary>
    private static void ServiceTruckAtStop(Unit unit)
    {
        if (!IsServer()
            || !CommanderSupplyStoreService.IsMobileStore(unit)
            || CommanderSupplyStoreService.Instance is not { } stores
            || stores.FindRefillStore(unit, TruckRefillReachMeters, includeDumps: true) is not Unit store)
        {
            return;
        }

        string moved = CommanderSupplyStoreService.IsDump(store) ? stores.UnloadTruck(unit, store) : stores.RefillTruck(unit, store);
        if (moved.Length > 0)
        {
            CommanderAlertService.Instance?.NotifySupply(moved, unit);
        }
    }

    /// <summary>Parks the unit without cancelling its order, for waypoint holds and cohesion waits.</summary>
    private static void HoldQuietly(Unit unit, UnitOrder order)
    {
        Issue(unit, order, unit.transform.GlobalPosition());
    }

    private static void Issue(Unit unit, UnitOrder order, GlobalPosition destination, bool force = false)
    {
        // Re-issuing every tick would spam the networked command RPC, so only send when the
        // wanted destination actually moved (route advance, or a target that is driving away).
        if (!force
            && order.HasIssuedDestination
            && CommanderGameAccess.ApproximatelyEqual(order.IssuedDestination, destination, 40f)
            && !WasOverridden(unit, order))
        {
            return;
        }

        order.IssuedDestination = destination;
        order.HasIssuedDestination = true;
        CommanderGameAccess.TrySetDestination(unit, destination);

        // What the unit actually took (the game moves a player point off water), so a later
        // comparison is against that and not against our own request.
        GlobalPosition accepted = order.AcceptedDestination;
        order.HasAcceptedDestination = IsServer()
            && CommanderGameAccess.TryGetCurrentCommandPosition(unit, out accepted);
        order.AcceptedDestination = accepted;
    }

    /// <summary>
    /// Something else (a depot's rally hand-off, a repair search, the base game) re-steered a unit
    /// that is under a player order. Without this the order was sent once and never again, so a
    /// fresh unit ordered before it cleared the depot drove to its rally slot, parked and deployed.
    /// Host only: a client's cached command lags its own request and would read as overridden.
    /// </summary>
    private static bool WasOverridden(Unit unit, UnitOrder order)
    {
        return order.HasAcceptedDestination
            && CommanderGameAccess.TryGetCurrentCommandPosition(unit, out GlobalPosition current)
            && !CommanderGameAccess.ApproximatelyEqual(current, order.AcceptedDestination, 40f);
    }

    /// <summary>A guest's batched move orders: unit id, position, player-command flag, repeated.</summary>
    private static void HostDestinations(FactionHQ hq, string[] args)
    {
        // Any player of that team will do: the game only asks whether an order came from one.
        Player? player = CommanderNetwork.SenderPlayer;
        if (player == null)
        {
            List<Player> players = hq.GetPlayers(sortByScore: false);
            player = players.Count > 0 ? players[0] : null;
        }

        for (int i = 1; i + 2 < args.Length; i += 3)
        {
            if (CommanderNetwork.TryUnit(args, i, out Unit unit)
                && ReferenceEquals(unit.NetworkHQ, hq)
                && CommanderNetwork.TryPosition(args, i + 1, out GlobalPosition destination))
            {
                CommanderGameAccess.SetDestinationOnServer(unit, destination, CommanderNetwork.Bool(args, i + 2) ? player : null);
            }
        }
    }

    private static bool IsServer()
    {
        return NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;
    }

    /// <summary>
    /// Whether any weapon aboard could ever shoot at the target, by the game's own per-station test
    /// (role effectiveness and altitude ceiling). Without it an artillery piece picked the nearest
    /// aircraft as its target of opportunity because it was the nearest thing inside gun range.
    /// </summary>
    private static bool CanEngage(Unit unit, Unit target)
    {
        if (target.definition == null)
        {
            return false;
        }

        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            WeaponStation station = unit.weaponStations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (info != null
                && target.radarAlt <= info.targetRequirements.maxAltitude
                && station!.CalcOpportunityThreat(target.definition, unit).opportunity > 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A stand-off unit (air defence or artillery) with a weapon that reaches the target's last
    /// known position from where it stands, by the store's own range, altitude, role and
    /// line-of-sight requirements. Artillery stores do not ask for line of sight, so they fire blind.
    /// </summary>
    private static bool CanFireFromHere(Unit unit, Unit target)
    {
        if (unit.definition is not VehicleDefinition { vehicleType: VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM or VehicleType.ART }
            || target.definition == null
            || unit.NetworkHQ == null)
        {
            return false;
        }

        GlobalPosition targetPosition = unit.NetworkHQ.GetKnownPosition(target) ?? target.GlobalPosition();
        float range = FastMath.Distance(targetPosition, unit.GlobalPosition());
        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            WeaponStation station = unit.weaponStations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (info != null
                && station!.Ammo > 0
                && range >= info.targetRequirements.minRange
                && range <= info.targetRequirements.maxRange
                && target.radarAlt <= info.targetRequirements.maxAltitude
                && (!info.targetRequirements.lineOfSight || target.LineOfSight(unit.transform.position, 1000f))
                && station.CalcOpportunityThreat(target.definition, unit).opportunity > 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stops just short of the target so the unit's own fire control can engage instead of
    /// driving on top of it.
    /// </summary>
    private static GlobalPosition GetAttackStandoff(Unit unit, Unit target)
    {
        // The last sighting, not the live position: a unit sent at a contact that went dark drives to
        // where it was seen and waits there instead of homing on it through the fog. Nothing known
        // at all means hold.
        GlobalPosition targetPosition = unit.NetworkHQ?.GetKnownPosition(target) ?? unit.GlobalPosition();
        if (!CommanderSettings.AttackMoveIntoRange)
        {
            return targetPosition;
        }

        float standoff = unit.GetMaxRange() > 0f
            ? Mathf.Clamp(unit.GetMaxRange() * 0.7f, 150f, 6000f)
            : 150f;
        Vector3 offset = unit.GlobalPosition() - targetPosition;
        offset.y = 0f;
        if (offset.sqrMagnitude < 1f || offset.magnitude <= standoff)
        {
            // Already inside the engagement envelope; hold this spot instead of closing further.
            return unit.GlobalPosition();
        }

        return targetPosition + (offset.normalized * standoff);
    }

    private static float ArrivalRadius(Unit unit)
    {
        return unit is Ship
            ? Mathf.Max(150f, unit.maxRadius * 4f)
            : Mathf.Max(45f, unit.maxRadius * 4f);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Invokes a private base-game method by reflection; any failure stops the vehicle instead of throwing.")]
    private static bool TryReturnToBasegameLogistics(Unit unit)
    {
        if (!unit.TryGetComponent(out RearmVehicleAI rearmAi)
            || !unit.TryGetComponent(out Rearmer rearmer))
        {
            return false;
        }

        RearmMissionController? controller = unit.NetworkHQ?.RearmMissionController;
        if (controller != null)
        {
            for (int i = controller.Missions.Count - 1; i >= 0; i--)
            {
                RearmMissionController.RearmMission mission = controller.Missions[i];
                if (ReferenceEquals(mission.Rearmer, rearmer))
                {
                    mission.AssignRearmer(null);
                }
            }
        }

        rearmAi.AssignMission(null!);
        bool needsRestock = rearmer.GetMaxCapacity() > 0f
            && rearmer.Capacity < rearmer.GetMaxCapacity() * 0.5f;
        MethodInfo? stateMethod = needsRestock ? RearmVehicleRestockMethod : RearmVehicleWaitMethod;
        try
        {
            stateMethod?.Invoke(rearmAi, null);
        }
        catch (System.Exception exception)
        {
            CommanderPlugin.Log.LogWarning($"Failed to return {unit.unitName} to Basegame rearm AI: {exception.Message}");
            if (unit is GroundVehicle vehicle)
            {
                vehicle.StopImmediately();
            }

            rearmer.AvailableForMission = !needsRestock;
        }

        return true;
    }

    /// <summary>
    /// An aircraft's half of an order. Three cases, in order: an order that resolved as a capture
    /// puts the aircraft down inside the ring and holds the base; an order dropped on a field the
    /// faction already holds is a rearm run; anything else is an ordinary travel or attack point.
    /// </summary>
    /// <remarks>
    /// The capture case exists because an aircraft handed the ground squad's hold point simply flew
    /// over the airfield and went home — a travel point is a place to be, not a place to land. The
    /// resupply case is the same order read the other way round, which is what the player expects
    /// when they right-click their own base with a fighter selected.
    /// </remarks>
    private static void IssueAircraftOrder(
        Aircraft aircraft,
        GlobalPosition point,
        Unit? attackTarget,
        bool append,
        CommanderCaptureService.CaptureTarget? capture)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return;
        }

        // A rescue helicopter sent at a downed pilot, any side, goes for that one and stays on duty.
        // Any other order is the end of the duty.
        CommanderRecoveryService? recovery = CommanderRecoveryService.Instance;
        if (recovery != null && recovery.TryOrderPickup(aircraft, attackTarget, point))
        {
            return;
        }

        recovery?.Stop(aircraft);

        // A helicopter on a sling lift takes every click as a plain travel point, which the lift
        // flies with the load hanging under it; SET LZ is the only way to say where it goes down.
        // The landing and capture readings below would put the pilot into a landing the lift then
        // fights, so they are skipped.
        if (CommanderSlingLiftService.Instance?.IsLifting(aircraft) == true)
        {
            airCommand.SetAircraftOrder(aircraft, attackTarget?.GlobalPosition() ?? point, null, append);
            return;
        }

        // A cargo run owns its own steering — the rotary half through AIHeloTransportState, the
        // fixed-wing half through this service — so an Air Command travel point handed to one was
        // simply overwritten on the next override pass, which is what "supply helicopters do not
        // listen to move orders" was. The point becomes a leg on the way to the LZ instead; SET LZ
        // in the selection bar is how the destination itself is moved. A capture landing is not a
        // leg on the way there, so it comes first and ends the run instead.
        if (attackTarget == null
            && capture.HasValue
            && capture.Value.Airbase != null
            && airCommand.SetAircraftLandingOrder(
                aircraft,
                capture.Value.Airbase,
                CommanderAirCommandService.LandingIntent.Capture,
                append))
        {
            return;
        }

        if (attackTarget == null
            && CommanderSupplyHeliService.Instance?.TryAddCargoRunWaypoint(aircraft, point, append) == true)
        {
            return;
        }

        if (attackTarget == null
            && !capture.HasValue
            && CommanderCaptureService.TryResolveOwnedAirfield(point, aircraft.NetworkHQ, out Airbase home)
            && airCommand.SetAircraftLandingOrder(
                aircraft,
                home,
                CommanderAirCommandService.LandingIntent.Resupply,
                append))
        {
            return;
        }

        airCommand.SetAircraftOrder(aircraft, point, attackTarget, append);
    }

    /// <summary>Sends the unit to the nearest friendly repair or rearm source it can reach.</summary>
    private static bool BeginRetreat(Unit unit, UnitOrder order)
    {
        Unit? support = FindNearestSupportUnit(unit, out GlobalPosition point);
        if (support == null)
        {
            return false;
        }

        order.Retreating = true;
        order.RetreatTarget = support;
        order.RetreatPoint = point;
        order.RetreatStartedAt = Time.unscaledTime;
        order.AttackTarget = null;
        order.EngagedTarget = null;
        order.ResetProgress();
        Issue(unit, order, point);
        return true;
    }

    /// <summary>
    /// A ship cannot drive to a bunker or repair truck on land; ordered onto dry ground it never
    /// arrives. It retreats to the water beside the support unit instead, found by walking from the
    /// support toward the ship until the seabed drops below sea level. A support with no water within
    /// rearm reach is no use to it.
    /// </summary>
    private static bool TryGetShipRetreatPoint(Unit ship, Unit support, out GlobalPosition point)
    {
        point = support.GlobalPosition();
        if (support is Ship)
        {
            return true;
        }

        GlobalPosition from = point;
        GlobalPosition to = ship.GlobalPosition();
        Vector2 delta = new(to.x - from.x, to.z - from.z);
        float distance = delta.magnitude;
        if (distance < 1f)
        {
            return false;
        }

        Vector2 direction = delta / distance;

        // Short of the full reach, so the 150 m off the waterline below still lands inside it.
        float span = Mathf.Min(distance, ShipRearmReachMeters - 200f);
        for (float d = 100f; d <= span; d += 100f)
        {
            GlobalPosition probe = new(from.x + (direction.x * d), from.y, from.z + (direction.y * d));
            if (!CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(probe)))
            {
                continue;
            }

            // Stand a little way off the waterline so the hull is not beached on the shelf.
            float off = Mathf.Min(d + 150f, distance);
            point = new GlobalPosition(from.x + (direction.x * off), to.y, from.z + (direction.y * off));
            return true;
        }

        return false;
    }

    /// <summary>True while the retreat is still running.</summary>
    private static bool RunRetreat(Unit unit, UnitOrder order)
    {
        Unit? support = order.RetreatTarget;
        if (support == null || support.disabled)
        {
            support = FindNearestSupportUnit(unit, out GlobalPosition point);
            order.RetreatTarget = support;
            order.RetreatPoint = point;
        }

        // Only Building is IRepairable in the Basegame, so a vehicle's condition never climbs
        // back. The retreat therefore ends on arrival plus a restock, or on a timeout, rather
        // than on the health bar recovering.
        float ammo = CommanderGameAccess.GetUnitAmmo(unit);
        bool recovered = CommanderGameAccess.GetUnitCondition(unit)
            >= (CommanderSettings.RetreatConditionPercent + 25f) * 0.01f;
        bool arrived = support != null
            && CommanderGameAccess.HorizontalDistance(unit.transform.position, support.transform.position) <= 200f;
        bool restocked = ammo is < 0f or >= 0.95f;

        // A ship's retreat point is the nearest water, so it can get no closer: restock through
        // the store's own rearm at ship reach and end the retreat there, rather than holding
        // offshore until the timeout.
        if (unit is Ship
            && support != null
            && FastMath.InRange(unit.GlobalPosition(), order.RetreatPoint, 250f))
        {
            TryRearmOnArrival(unit, support);
            arrived = true;
            restocked = true;
        }

        if (support == null
            || recovered
            || (arrived && restocked)
            || Time.unscaledTime - order.RetreatStartedAt > RetreatTimeoutSeconds)
        {
            // A vehicle is never repaired, so it is still under the threshold when the retreat
            // ends: without this it retreated again on the next tick and its route never resumed.
            order.Retreating = false;
            order.RetreatTarget = null;
            order.RetreatEndedCondition = CommanderGameAccess.GetUnitCondition(unit);
            order.ResetProgress();
            return false;
        }

        // A ground unit follows the live support (a truck moves); a ship keeps its water point.
        Issue(unit, order, unit is Ship ? order.RetreatPoint : support.GlobalPosition());
        return true;
    }

    private static void RunWaypointAction(Unit unit, UnitOrder order)
    {
        switch (order.ActionAt(order.Index))
        {
            case CommanderWaypointAction.Hold:
                order.HoldUntil = Time.unscaledTime + CommanderSettings.WaypointHoldSeconds;
                break;
            case CommanderWaypointAction.RadarOff:
                CommanderRadarService.Instance?.SetUnitRadar(unit, false);
                break;
            case CommanderWaypointAction.RadarOn:
                CommanderRadarService.Instance?.SetUnitRadar(unit, true);
                break;
        }
    }

    /// <summary>
    /// An order that lands on a base this faction could take is a capture order: the destination
    /// snaps to the middle of the ring, so the units stop somewhere that actually captures rather
    /// than wherever the cursor was. Both order paths ask, so a capture works the same from the 3D
    /// view and from the map, on its own or as the last of a queued string of travel points.
    /// </summary>
    private bool TryClaimCapturePoint(
        ref GlobalPosition point, bool append, out CommanderCaptureService.CaptureTarget target)
    {
        CommanderCaptureService? capture = CommanderCaptureService.Instance;

        // Queueing more targets behind an attack: a click near a base is the next target or a plain
        // point, never a capture. A ships-only selection never captures (no capture strength). A mixed
        // selection still resolves one; SelectionExcludingNaval keeps the ships off the ring.
        if (capture == null || (append && SelectionHasAttackOrder()) || SelectionIsNavalOnly())
        {
            target = default;
            return false;
        }

        return capture.TryResolveCaptureOrder(ref point, out target);
    }

    /// <summary>A ship has no capture strength, so it never snaps onto a capture ring.</summary>
    private bool SelectionIsNavalOnly()
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] is not Ship)
            {
                return false;
            }
        }

        return selection.Count > 0;
    }

    private bool SelectionHasAttackOrder()
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            Unit unit = selection[i];
            if (unit is Aircraft)
            {
                CollectAttackTargets(unit, attackScratch);
                if (attackScratch.Count > 0)
                {
                    return true;
                }
            }
            else if (orders.TryGetValue(unit, out UnitOrder order)
                && order.AttackTarget != null
                && !order.AttackTarget.disabled)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyOrder(
        System.Func<Unit, (bool Valid, GlobalPosition Point)> resolvePoint,
        Unit? attackTarget,
        bool append,
        CommanderCaptureService.CaptureTarget? capture = null)
    {
        if (selectionService.SelectedUnits.Count > 0)
        {
            (bool valid, GlobalPosition point) = resolvePoint(selectionService.SelectedUnits[0]);

            // A pilot under the cursor gets the rescue's own yellow ping from the pickup instead.
            if (valid && attackTarget is not PilotDismounted)
            {
                CommanderOrderPing.Show(point, attackTarget != null);
            }
        }

        CommanderWaypointAction action = PendingWaypointAction;
        PendingWaypointAction = CommanderWaypointAction.None;
        CommanderFormationShape shape = Formation;
        float heading = ResolveHeading(resolvePoint);
        OrderGroup group = new();
        int groundSlot = 0;
        int shipSlot = 0;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit is Aircraft aircraft)
            {
                IssueAircraftOrder(aircraft, resolvePoint(unit).Point, attackTarget, append, capture);
                continue;
            }

            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            (bool valid, GlobalPosition point) = resolvePoint(unit);
            if (!valid)
            {
                continue;
            }

            bool ship = unit is Ship;
            int slot = ship ? shipSlot++ : groundSlot++;
            float spacing = ship ? ShipFormationSpacingMeters : GroundFormationSpacingMeters;

            if (!append || !orders.TryGetValue(unit, out UnitOrder order))
            {
                order = new UnitOrder();
                orders[unit] = order;
            }

            order.FormationSlot = slot;
            order.Spacing = spacing;
            order.Shape = shape;
            order.Heading = heading;
            order.Group = group;

            // A second target with the queue key held goes behind the one already ordered instead
            // of replacing it, and is a target rather than a travel point: it gets no waypoint, so
            // the unit works down the list rather than driving to each corpse in turn.
            bool queueTarget = append
                && attackTarget != null
                && order.AttackTarget != null
                && !order.AttackTarget.disabled
                && !ReferenceEquals(order.AttackTarget, attackTarget)
                && !order.QueuedTargets.Contains(attackTarget);
            if (queueTarget)
            {
                order.QueuedTargets.Add(attackTarget!);
            }
            else
            {
                order.AttackTarget = attackTarget;
            }

            order.GuardTarget = null;
            order.EngagedTarget = null;
            order.Retreating = false;

            // A ship's attack order gets no travel point either. The target is almost always on land
            // or in the air, so sailing at it read as a plain move order: the ship never arrived,
            // never reached the stand-off branch, and only got there after the 60 s stuck skip.
            bool shipAttack = ship && attackTarget != null;
            if (!queueTarget && !shipAttack)
            {
                order.Waypoints.Add(point);
                order.Actions.Add(action);
            }

            order.ResetProgress();

            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
        }

        RefreshOrderFlags();
        AdvanceRoutes(force: true);
    }

    /// <summary>Direction the group travels in, so line, wedge and column face the right way.</summary>
    private float ResolveHeading(System.Func<Unit, (bool Valid, GlobalPosition Point)> resolvePoint)
    {
        Vector3 centre = Vector3.zero;
        int count = 0;
        Vector3 destination = Vector3.zero;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            (bool valid, GlobalPosition point) = resolvePoint(unit);
            if (!valid)
            {
                continue;
            }

            centre += unit.transform.position;
            destination = point.ToLocalPosition();
            count++;
        }

        if (count == 0)
        {
            return 0f;
        }

        Vector3 travel = destination - (centre / count);
        travel.y = 0f;
        return travel.sqrMagnitude < 1f ? 0f : Quaternion.LookRotation(travel).eulerAngles.y;
    }

    private void AdvanceRoutes(bool force)
    {
        if (!force && !CommanderScheduler.IsDue(ref nextRouteTickAt, RouteTickIntervalSeconds))
        {
            return;
        }

        PruneStances();
        if (orders.Count == 0)
        {
            return;
        }

        routeTick++;
        RefreshHostiles();
        UpdateGroupCohesion();

        staleOrders.Clear();
        bool targetsChanged = false;
        foreach (KeyValuePair<Unit, UnitOrder> entry in orders)
        {
            Unit unit = entry.Key;
            UnitOrder order = entry.Value;
            if (unit == null || unit.disabled || stoppedUnits.Contains(unit))
            {
                staleOrders.Add(unit!);
                continue;
            }

            if (GetStance(unit) == CommanderStance.HoldPosition)
            {
                HoldInPlace(unit);
                staleOrders.Add(unit);
                continue;
            }

            if (order.AttackTarget != null && order.AttackTarget.disabled)
            {
                // The next target the player queued behind it comes first: an order given against
                // three things should finish all three before the automatic search has a say.
                if (!order.TakeNextTarget())
                {
                    order.AttackTarget = CommanderSettings.RetargetAfterKill ? FindTargetInRange(unit, 0f) : null;
                }

                order.ResetProgress();
                targetsChanged = true;
                if (order.AttackTarget == null && order.Index >= order.Waypoints.Count && order.GuardTarget == null)
                {
                    // Target destroyed and nothing else in reach: hold the ground that was just
                    // taken instead of letting Basegame tasking drive the unit back out of it.
                    HoldInPlace(unit);
                    staleOrders.Add(unit);
                    continue;
                }
            }

            if (order.Retreating)
            {
                if (RunRetreat(unit, order))
                {
                    continue;
                }

                targetsChanged = true;
            }
            else if (ShouldRetreat(unit, order) && BeginRetreat(unit, order))
            {
                continue;
            }

            if (order.GuardTarget != null)
            {
                if (order.GuardTarget.disabled)
                {
                    order.GuardTarget = null;
                    staleOrders.Add(unit);
                    continue;
                }

                RunGuard(unit, order);
                continue;
            }

            if (RunOpportunityEngagement(unit, order, ref targetsChanged))
            {
                continue;
            }

            // An attack order's last travel point is the target itself. Air defence and artillery
            // that can already hit it from here stay put and shoot instead of driving at it; out of
            // reach they close as before. Convoys are left alone: they move and fight.
            if (order.AttackTarget != null
                && order.Index >= order.Waypoints.Count - 1
                && CanFireFromHere(unit, order.AttackTarget))
            {
                HoldQuietly(unit, order);
                continue;
            }

            if (order.Index < order.Waypoints.Count)
            {
                if (Time.unscaledTime < order.HoldUntil)
                {
                    HoldQuietly(unit, order);
                    continue;
                }

                GlobalPosition waypoint = CommanderDestinationFormation.ApplyOffset(
                    order.Waypoints[order.Index], order.FormationSlot, order.Spacing, order.Shape, order.Heading);
                float distance = CommanderGameAccess.HorizontalDistance(
                    unit.transform.position, waypoint.ToLocalPosition());
                bool waiting = distance > ArrivalRadius(unit) && !order.Loop && order.Group.ShouldWait(order.Remaining);
                if (waiting)
                {
                    // Waiting for the group is not being stuck. Counting it as stuck skipped the
                    // travel point after 60 s, and the route fell apart.
                    order.PauseProgressClock();
                }

                // A final travel point already inside the arrival radius (a short nudge) would count as reached
                // before it was sent. Send it once, exactly, then let the order finish; the base game drives the
                // last few metres.
                if (distance <= ArrivalRadius(unit)
                    && order.Index == order.Waypoints.Count - 1
                    && !order.Loop
                    && order.AttackTarget == null
                    && !(order.HasIssuedDestination
                        && CommanderGameAccess.ApproximatelyEqual(order.IssuedDestination, waypoint, 5f)))
                {
                    Issue(unit, order, waypoint, force: true);
                }

                if (distance <= ArrivalRadius(unit) || (!waiting && order.IsStuck(distance)))
                {
                    RunWaypointAction(unit, order);
                    order.Index++;
                    order.ResetProgress();
                    if (order.Index >= order.Waypoints.Count && order.Loop && order.Waypoints.Count > 1)
                    {
                        // Patrol: the route restarts instead of expiring, so it keeps
                        // running while the player is away flying.
                        order.Index = 0;
                    }

                    if (order.Index < order.Waypoints.Count)
                    {
                        ServiceTruckAtStop(unit);

                        // Route continues. Do not fall through to the attack-target test below, which would drop the
                        // order at the first travel point.
                        continue;
                    }

                    if (order.RearmFrom != null)
                    {
                        TryRearmOnArrival(unit, order.RearmFrom);
                        order.RearmFrom = null;
                    }
                    else if (IsServer()
                        && CommanderSupplyStoreService.IsMobileStore(unit)
                        && CommanderSupplyStoreService.Instance?.FindRefillStore(unit, TruckRefillReachMeters) is Unit bunker)
                    {
                        // A plain travel point on a bunker is a refill too, not only a right-click on the store itself.
                        TryRearmOnArrival(unit, bunker);
                    }
                    else if (IsServer()
                        && unit is not Ship
                        && CommanderGameAccess.GetUnitAmmo(unit) is >= 0f and < 0.999f
                        && CommanderSupplyStoreService.Instance?.FindRefillStore(unit, GroundRearmReachMeters, includeDumps: true) is Unit store)
                    {
                        // Parked beside a bunker or an ammo dump is a resupply too, for any armed unit:
                        // a weapon that never asks the base game for a rearm would otherwise wait forever.
                        TryRearmOnArrival(unit, store);
                    }

                    if (order.AttackTarget == null)
                    {
                        staleOrders.Add(unit);
                        continue;
                    }
                }
                else
                {
                    if (waiting)
                    {
                        // Arrive together: anything well ahead of the rearmost unit waits
                        // instead of letting the group string out along the route.
                        HoldQuietly(unit, order);
                        continue;
                    }

                    Issue(unit, order, waypoint);
                    continue;
                }
            }

            if (order.AttackTarget == null)
            {
                staleOrders.Add(unit);
                continue;
            }

            Issue(unit, order, GetAttackStandoff(unit, order.AttackTarget));
        }

        for (int i = 0; i < staleOrders.Count; i++)
        {
            orders.Remove(staleOrders[i]);
        }

        if (staleOrders.Count > 0 || targetsChanged)
        {
            RefreshOrderFlags();
        }
    }

    /// <summary>Records how far each unit still has to travel and the worst case per order group.</summary>
    private void UpdateGroupCohesion()
    {
        foreach (KeyValuePair<Unit, UnitOrder> entry in orders)
        {
            Unit unit = entry.Key;
            UnitOrder order = entry.Value;

            // A patrol has no end to arrive at together. Measured to the end of the list, the unit
            // that had just started its next lap read as the straggler, so every other truck on the
            // line parked and waited most of a lap for it, then the next one did the same.
            if (unit == null || unit.disabled || order.Loop || order.Index >= order.Waypoints.Count)
            {
                order.Remaining = 0f;
                continue;
            }

            GlobalPosition waypoint = CommanderDestinationFormation.ApplyOffset(
                order.Waypoints[order.Index], order.FormationSlot, order.Spacing, order.Shape, order.Heading);
            float remaining = CommanderGameAccess.HorizontalDistance(
                unit.transform.position, waypoint.ToLocalPosition());

            // The whole route still ahead, not just the current leg. Measured per leg, a unit that
            // had passed the first travel point looked like the straggler (its next leg is long)
            // and everything still behind it stopped to wait, which is the multi-point convoy stall.
            for (int i = order.Index + 1; i < order.Waypoints.Count; i++)
            {
                remaining += CommanderGameAccess.HorizontalDistance(
                    order.Waypoints[i - 1].ToLocalPosition(), order.Waypoints[i].ToLocalPosition());
            }

            order.Remaining = remaining;
            order.Group.Accumulate(routeTick, order.Remaining);
        }
    }

    private void RefreshHostiles()
    {
        hostiles.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            if (Time.timeSinceLevelLoad - entry.Value.lastSpottedTime > SpotStaleSeconds
                || !entry.Key.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate.disabled
                || candidate.NetworkHQ == null
                || CommanderGameAccess.IsFriendlyUnit(candidate, hq))
            {
                continue;
            }

            hostiles.Add(candidate);
        }
    }

    /// <summary>Nearest tracked hostile inside <paramref name="range"/>; 0 uses the unit's own weapon range.</summary>
    private Unit? FindTargetInRange(Unit unit, float range)
    {
        if (range <= 0f)
        {
            range = unit.GetMaxRange();
            if (range <= 0f)
            {
                range = 2000f;
            }
        }

        Unit? best = null;
        float bestDistance = range;
        for (int i = 0; i < hostiles.Count; i++)
        {
            Unit candidate = hostiles[i];
            if (candidate == null || candidate.disabled || !CanEngage(unit, candidate))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                unit.transform.position, candidate.transform.position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Attack-move. A unit travelling on Free Engage picks up a hostile that comes inside its
    /// weapon range and shoots at it, and holds at its standoff once the route is finished.
    /// </summary>
    /// <remarks>
    /// A target of opportunity never overrides travel points the player placed. Otherwise a unit
    /// already in range of something would get an attack standoff of "stay where you are" and ignore
    /// the move order. The target is still recorded, so focus fire and route markers work the same.
    /// </remarks>
    private bool RunOpportunityEngagement(Unit unit, UnitOrder order, ref bool targetsChanged)
    {
        bool travelling = order.Index < order.Waypoints.Count;
        if (order.EngagedTarget != null)
        {
            float leash = Mathf.Max(unit.GetMaxRange(), 500f) * EngagementLeash;
            if (order.EngagedTarget.disabled
                || CommanderGameAccess.HorizontalDistance(
                    unit.transform.position, order.EngagedTarget.transform.position) > leash)
            {
                order.EngagedTarget = null;
                order.ResetProgress();
                targetsChanged = true;
                return false;
            }

            if (travelling)
            {
                // Shoot on the move; the waypoint below still owns where this unit is going.
                return false;
            }

            Issue(unit, order, GetAttackStandoff(unit, order.EngagedTarget));
            return true;
        }

        if (!CommanderSettings.AttackMoveRoutes
            || order.AttackTarget != null
            || !travelling
            || GetStance(unit) != CommanderStance.FreeEngage)
        {
            return false;
        }

        Unit? threat = FindTargetInRange(unit, 0f);
        if (threat == null)
        {
            return false;
        }

        order.EngagedTarget = threat;
        targetsChanged = true;
        return false;
    }

    /// <summary>Escort: sit in formation on the guarded unit and shoot back on its behalf.</summary>
    private void RunGuard(Unit unit, UnitOrder order)
    {
        Unit guarded = order.GuardTarget!;
        Unit? threat = order.EngagedTarget;
        if (threat != null && threat.disabled)
        {
            threat = null;
        }

        if (threat == null && GetStance(unit) != CommanderStance.HoldFire)
        {
            threat = CommanderAlertService.GetRecentAttacker(guarded) ?? CommanderAlertService.GetRecentAttacker(unit);
            if (threat != null
                && (!CanEngage(unit, threat) || CommanderGameAccess.HorizontalDistance(
                    unit.transform.position, threat.transform.position) > Mathf.Max(unit.GetMaxRange(), 500f) * EngagementLeash))
            {
                threat = null;
            }
        }

        order.EngagedTarget = threat;
        if (threat != null)
        {
            Issue(unit, order, GetAttackStandoff(unit, threat));
            return;
        }

        float heading = guarded.transform.eulerAngles.y;
        GlobalPosition station = CommanderDestinationFormation.ApplyOffset(
            guarded.GlobalPosition(), order.FormationSlot, order.Spacing, order.Shape, heading);
        if (CommanderGameAccess.HorizontalDistance(unit.transform.position, station.ToLocalPosition())
            <= Mathf.Max(GuardArrivalMeters, ArrivalRadius(unit)))
        {
            return;
        }

        order.Heading = heading;
        Issue(unit, order, station);
    }

    private void ApplyStance(Unit unit, CommanderStance stance)
    {
        // An aircraft cannot hold a position, so for it the cycle is FREE FIRE and HOLD FIRE only.
        if (unit is Aircraft && stance == CommanderStance.HoldPosition)
        {
            stance = CommanderStance.FreeEngage;
        }

        if (unit is Aircraft && GetStance(unit) != stance)
        {
            CommanderPlugin.Log.LogInfo($"Stance: {CommanderGameAccess.GetUnitLabel(unit)} set to {stance}.");
        }

        if (stance == CommanderStance.FreeEngage)
        {
            stances.Remove(unit);
        }
        else
        {
            stances[unit] = stance;
        }

        if (stance == CommanderStance.HoldPosition)
        {
            orders.Remove(unit);
            HoldInPlace(unit);
        }
        else
        {
            stoppedUnits.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
        }
    }

    private void HostMissileOnly(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit) || !ReferenceEquals(unit.NetworkHQ, hq))
        {
            return;
        }

        if (CommanderNetwork.Bool(args, 2))
        {
            missileOnlyUnits.Add(unit);
        }
        else
        {
            missileOnlyUnits.Remove(unit);
        }
    }

    private void HostStance(FactionHQ hq, string[] args)
    {
        CommanderStance stance = (CommanderStance)CommanderNetwork.Int(args, 1);
        if (!System.Enum.IsDefined(typeof(CommanderStance), stance))
        {
            return;
        }

        for (int i = 2; i < args.Length; i++)
        {
            if (CommanderNetwork.TryUnit(args, i, out Unit unit) && ReferenceEquals(unit.NetworkHQ, hq))
            {
                ApplyStance(unit, stance);
            }
        }

        RefreshOrderFlags();
    }

    private void HoldInPlace(Unit unit)
    {
        CommanderGameAccess.SetUnitHoldPosition(unit, true);
        CommanderGameAccess.SendDestination(unit, unit.transform.GlobalPosition(), playerCommand: false);
        stoppedUnits.Add(unit);
        playerDestinations.Remove(unit);
    }

    private void RefreshOrderFlags()
    {
        HasAttackOrders = false;
        foreach (UnitOrder order in orders.Values)
        {
            if (order.EffectiveTarget != null)
            {
                HasAttackOrders = true;
                break;
            }
        }

        HasHoldFireUnits = false;
        foreach (CommanderStance stance in stances.Values)
        {
            if (stance == CommanderStance.HoldFire)
            {
                HasHoldFireUnits = true;
                break;
            }
        }
    }

    private void PruneStances()
    {
        if (stances.Count == 0)
        {
            return;
        }

        staleStances.Clear();
        foreach (KeyValuePair<Unit, CommanderStance> entry in stances)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                staleStances.Add(entry.Key!);
                continue;
            }

            if (entry.Value == CommanderStance.HoldPosition && !stoppedUnits.Contains(entry.Key))
            {
                // Hold Position is sticky: reassert it if anything else moved the unit.
                HoldInPlace(entry.Key);
            }
        }

        for (int i = 0; i < staleStances.Count; i++)
        {
            stances.Remove(staleStances[i]);
        }

        if (staleStances.Count > 0)
        {
            RefreshOrderFlags();
        }
    }

    /// <summary>Shared by every unit ordered in one click, so the group can wait for stragglers.</summary>
    private sealed class OrderGroup
    {
        private int tick = -1;
        private float pending;
        private float laggardDistance;

        internal void Accumulate(int currentTick, float remaining)
        {
            if (tick != currentTick)
            {
                tick = currentTick;
                laggardDistance = pending;
                pending = 0f;
            }

            if (remaining > pending)
            {
                pending = remaining;
            }
        }

        /// <summary>True when this unit is far enough ahead of the rearmost member to wait for it.</summary>
        internal bool ShouldWait(float remaining)
        {
            float tolerance = CommanderSettings.FormationCohesionMeters;
            return tolerance > 0f && laggardDistance - remaining > tolerance;
        }
    }

    private sealed class UnitOrder
    {
        private float bestDistance = float.MaxValue;
        private float lastProgressAt;
        private Unit? attackTarget;

        internal List<GlobalPosition> Waypoints { get; } = new();

        internal List<CommanderWaypointAction> Actions { get; } = new();

        internal int Index { get; set; }

        internal bool Loop { get; set; }

        /// <summary>
        /// The target this unit is working. Assigning one replaces the whole queue behind it, so
        /// every path that ends or retargets an attack drops the queued targets with it.
        /// </summary>
        internal Unit? AttackTarget
        {
            get => attackTarget;

            set
            {
                attackTarget = value;
                QueuedTargets.Clear();
            }
        }

        /// <summary>Targets queued behind <see cref="AttackTarget"/> by shift-clicking more of them,
        /// worked one after another.</summary>
        internal List<Unit> QueuedTargets { get; } = new();

        internal Unit? EngagedTarget { get; set; }

        internal Unit? GuardTarget { get; set; }

        internal Unit? RetreatTarget { get; set; }

        /// <summary>Where the retreat drives to: the support unit itself, or for a ship the water
        /// beside it.</summary>
        internal GlobalPosition RetreatPoint { get; set; }

        /// <summary>The store a resupply order draws from once the route is finished.</summary>
        internal Unit? RearmFrom { get; set; }

        internal bool Retreating { get; set; }

        internal float RetreatStartedAt { get; set; }

        /// <summary>Condition when the last retreat on this order ended; another needs fresh damage.</summary>
        internal float RetreatEndedCondition { get; set; } = float.MaxValue;

        internal int FormationSlot { get; set; }

        internal float Spacing { get; set; }

        internal CommanderFormationShape Shape { get; set; }

        internal float Heading { get; set; }

        internal float HoldUntil { get; set; }

        internal float Remaining { get; set; }

        internal OrderGroup Group { get; set; } = new();

        internal GlobalPosition IssuedDestination { get; set; }

        internal bool HasIssuedDestination { get; set; }

        internal GlobalPosition AcceptedDestination { get; set; }

        internal bool HasAcceptedDestination { get; set; }

        /// <summary>A commanded attack target outranks a target picked up while travelling.</summary>
        internal Unit? EffectiveTarget => attackTarget ?? EngagedTarget;

        /// <summary>Steps to the next queued target, keeping the rest of the queue. False when the
        /// queue is empty, which is when the attack order is finished.</summary>
        internal bool TakeNextTarget()
        {
            while (QueuedTargets.Count > 0)
            {
                Unit next = QueuedTargets[0];
                QueuedTargets.RemoveAt(0);
                if (next != null && !next.disabled)
                {
                    attackTarget = next;
                    return true;
                }
            }

            attackTarget = null;
            return false;
        }

        internal CommanderWaypointAction ActionAt(int index)
        {
            return index >= 0 && index < Actions.Count ? Actions[index] : CommanderWaypointAction.None;
        }

        internal void ResetProgress()
        {
            PauseProgressClock();
            HasIssuedDestination = false;
        }

        /// <summary>Restarts the stuck timer without re-sending the order.</summary>
        internal void PauseProgressClock()
        {
            bestDistance = float.MaxValue;
            lastProgressAt = Time.time;
        }

        /// <summary>Lets a blocked unit skip a waypoint instead of parking on the route forever.</summary>
        internal bool IsStuck(float distance)
        {
            if (distance < bestDistance - 5f)
            {
                bestDistance = distance;
                lastProgressAt = Time.time;
                return false;
            }

            // Scaled time: a paused game is not a stuck unit.
            return Time.time - lastProgressAt > StuckTimeoutSeconds;
        }
    }
}
