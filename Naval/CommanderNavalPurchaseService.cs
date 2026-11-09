using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NuclearOption.Networking;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderNavalPurchaseService : ICommanderActivate, ICommanderTickPersistent, ICommanderResetSession
{
    private const float MinimumEntryBandMeters = 2000f;
    private const float EntryBandMapFraction = 0.08f;
    private const float MaximumRallySnapMeters = 12000f;

    // Open water a launched hull keeps between its sides and the nearest land, on every side.
    private const float ShoreClearMeters = 500f;

    // Gaps between shore probes, so a spit or islet inside the ring cannot slip between them.
    private const float ShoreProbeStepMeters = 100f;

    // A clear circle is at least 1 km across, so slots this far apart cannot step over one.
    private const float DocksideStepMeters = 200f;

    /// <summary>
    /// How far from the dock a hull may be put in the water. Past this the dock is not the harbour
    /// it came from, and the purchase is refused instead of launching the ship out in the ocean.
    /// </summary>
    private const float MaximumDockEntryMeters = 3000f;
    private const float StatusDurationSeconds = 7f;

    // A finished hull that cannot launch yet (funds spent meanwhile, no clear water) asks again this often.
    private const float LaunchRetrySeconds = 1f;

    // ShipAI.Steer pushes away from every hull within r = both radii + 50 m with strength (r / d)^2,
    // against a pull of 1 toward its order. Hulls launched 20 m and parked 80 m apart pushed each
    // other harder than any order pulled: the outer one sailed, the rest spun in place. Twice r
    // keeps a neighbour's push at a quarter.
    private const float ShipAvoidMeters = 50f;

    // Rings of slots tried around a rally point before the last resort of the point itself.
    private const int RallyRings = 8;

    // Rally slots handed out to hulls still sailing there. A hull on its way is not yet at its slot,
    // so its live position alone would let the next hull be sent to the same water.
    private static readonly List<RallyClaim> RallyClaims = new();

    private readonly List<ShipDefinition> shipDefinitions = new();
    private readonly List<EntryCandidate> entryCandidates = new();

    // Factory-built hulls waiting to launch. The game's own reserve cannot hold them:
    // FactionHQ.ModifyUnitSupply throws for anything but a vehicle or an aircraft. The host's count
    // is the real one; a guest holds the copy SyncReserve sends.
    private readonly Dictionary<FactionHQ, Dictionary<ShipDefinition, int>> reserve = new();

    // One build queue per dock, as each vehicle depot has its own: one hull at a time per dock,
    // launched at the dock it was ordered from. A list rather than a dictionary because the null
    // dock (none clicked, the best one launches) is a queue of its own.
    private readonly List<DockQueue> dockQueues = new();

    // Each dock's rally point, as each vehicle depot has its own. Unset: hulls wait beside the dock.
    private readonly Dictionary<Unit, GlobalPosition> rallyPoints = new();

    private readonly CommanderMapClickTracker rallyClickTracker = new();
    private Unit? rallySelectionDock;
    private int selectedIndex;
    private Unit? selectedDock;
    private float statusUntil;
    private string statusText = string.Empty;

    internal CommanderNavalPurchaseService()
    {
        Instance = this;
        CommanderNetwork.OnHost("ship", HostPurchase);
        CommanderNetwork.OnGuest("st.naval", args => SetStatus(CommanderNetwork.Arg(args, 1)));
        CommanderNetwork.OnSync(SyncReserve);
        CommanderNetwork.OnGuest("ships", ApplyReserve);
    }

    internal static CommanderNavalPurchaseService? Instance { get; private set; }

    /// <summary>The level of the dock a purchase launches from (the selected one, else the best). Zero
    /// means no naval purchases at all.</summary>
    internal static int DockLevel => CommanderEconomyService.GetNavalDockLevel(CommanderGameAccess.GetLocalHq(), Instance?.DockPoint(null));

    internal IReadOnlyList<ShipDefinition> ShipDefinitions
    {
        get
        {
            // The factory product list asks for this on a host that may never have entered RTS mode.
            if (shipDefinitions.Count == 0)
            {
                RefreshDefinitions();
            }

            return shipDefinitions;
        }
    }

    internal ShipDefinition? SelectedDefinition => shipDefinitions.Count == 0
        ? null
        : shipDefinitions[Mathf.Clamp(selectedIndex, 0, shipDefinitions.Count - 1)];

    /// <summary>The dock the player last selected: a purchase launches beside it, the way a depot's
    /// vehicles roll out of the depot that was clicked.</summary>
    internal Unit? SelectedDock
    {
        get => selectedDock != null && !selectedDock.disabled ? selectedDock : null;
        set => selectedDock = value;
    }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    /// <summary>The selected dock's build queue.</summary>
    internal IReadOnlyList<ShipDefinition> BuildQueue => FindQueue(SelectedDock)?.Ships ?? (IReadOnlyList<ShipDefinition>)Array.Empty<ShipDefinition>();

    /// <summary>Seconds until the head of the selected dock's queue is ready, negative while its clock has not started.</summary>
    internal float BuildLeft => FindQueue(SelectedDock) is { DoneAt: > 0f } queue ? queue.DoneAt - Time.time : -1f;

    internal float BuildSeconds => FindQueue(SelectedDock)?.Seconds ?? 0f;

    /// <summary>The head of the selected dock's queue is built but has no clear water to launch into.</summary>
    internal bool LaunchBlocked => FindQueue(SelectedDock)?.Blocked == true;

    /// <summary>Why the head of the selected dock's queue is refused, null while it can be bought.</summary>
    internal string? HeldReason => FindQueue(SelectedDock)?.Held;

    internal bool AwaitingRallySelection => rallySelectionDock != null;

    public void Activate()
    {
        RefreshDefinitions();
    }

    public void ResetSession()
    {
        shipDefinitions.Clear();
        reserve.Clear();
        dockQueues.Clear();
        entryCandidates.Clear();
        rallyPoints.Clear();
        RallyClaims.Clear();
        rallySelectionDock = null;
        selectedIndex = 0;
        statusUntil = 0f;
        statusText = string.Empty;
    }

    public void TickPersistent()
    {
        if (rallySelectionDock != null)
        {
            TickRallyMapSelection();
        }

        if (dockQueues.Count == 0)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        for (int i = dockQueues.Count - 1; i >= 0; i--)
        {
            DockQueue queue = dockQueues[i];

            // A lost dock's queue can no longer be seen or cleared, so what it held is refunded.
            bool dockLost = queue.Dock is { } dock && (!dock || dock.disabled);
            if (!dockLost)
            {
                TickQueue(queue, hq);
            }
            else
            {
                Refund(hq, queue.Clear());
            }

            if (dockLost || queue.Ships.Count == 0)
            {
                dockQueues.RemoveAt(i);
            }
        }
    }

    /// <summary>How many factory-built <paramref name="definition"/> hulls <paramref name="hq"/> holds.</summary>
    internal static int GetReserve(FactionHQ? hq, ShipDefinition? definition)
    {
        return hq != null
            && definition != null
            && Instance != null
            && Instance.reserve.TryGetValue(hq, out Dictionary<ShipDefinition, int> held)
            && held.TryGetValue(definition, out int count)
            ? count
            : 0;
    }

    /// <summary>A ship factory's batch. Host only, the guests get the count from <see cref="SyncReserve"/>.</summary>
    internal static void AddReserve(FactionHQ hq, ShipDefinition definition, int count)
    {
        if (Instance == null || count == 0)
        {
            return;
        }

        if (!Instance.reserve.TryGetValue(hq, out Dictionary<ShipDefinition, int> held))
        {
            held = new Dictionary<ShipDefinition, int>();
            Instance.reserve[hq] = held;
        }

        held[definition] = Mathf.Max(0, GetReserve(hq, definition) + count);
    }

    /// <summary>
    /// The dock level a hull needs before anyone may buy it. This is the whole ladder, and it is
    /// keyed on <see cref="ShipType"/> rather than on a per-ship table so a patch that adds a hull
    /// files itself under the class it already belongs to instead of falling off the list.
    /// </summary>
    internal static int GetRequiredDockLevel(ShipDefinition? definition)
    {
        return definition == null ? CommanderEconomyService.MaxLevel : GetRequiredDockLevel(definition.shipType);
    }

    internal static bool IsUnlocked(ShipDefinition? definition, int dockLevel)
    {
        return definition != null && dockLevel >= GetRequiredDockLevel(definition);
    }

    internal static string GetLevelUnlockLabel(int level)
    {
        return level switch
        {
            <= 0 => "nothing",
            1 => "patrol boats and landing craft",
            2 => "corvettes and frigates",
            _ => "destroyers, carriers and assault ships",
        };
    }

    /// <summary>
    /// Ship classes a dock at <paramref name="level"/> unlocks, for the economy self-check. The
    /// ladder has to start at nothing and end at everything, or the dock is either pointless or a
    /// hull nobody can ever buy.
    /// </summary>
    internal static int CountShipTypesAtLevel(int level)
    {
        int count = 0;
        foreach (ShipType type in Enum.GetValues(typeof(ShipType)))
        {
            if (level >= GetRequiredDockLevel(type))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The sea-lane point nearest <paramref name="position"/>: where a hull sent there
    /// actually ends up.</summary>
    internal static bool TryGetSeaLanePoint(GlobalPosition position, out GlobalPosition lanePoint)
    {
        lanePoint = default;
        RoadNetwork? seaLanes = NetworkSceneSingleton<LevelInfo>.i?.seaLanes;
        return seaLanes != null && seaLanes.Exists() && seaLanes.TryGetNearestPoint(position, out lanePoint, out _);
    }

    internal void SelectDefinition(int index)
    {
        if (index >= 0 && index < shipDefinitions.Count)
        {
            selectedIndex = index;
        }
    }

    /// <summary>Queues the selected hull at the selected dock, paid for now unless a reserve hull
    /// nobody has queued yet takes its place. It launches when its build time is up. A guest's
    /// money is the host's to spend, so a guest's hull joins the queue once the host has charged
    /// for it (<see cref="CommanderQueuePayment"/>).</summary>
    internal void BeginPurchase()
    {
        ShipDefinition? definition = SelectedDefinition;
        Unit? dock = SelectedDock;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (CanBuy(definition, hq, dock) && HasLaunchWater(definition!, hq!, dock))
        {
            float paid = 0f;
            if (GetReserve(hq, definition) <= CountUnpaidQueued(definition!))
            {
                if (CommanderNetwork.IsGuest)
                {
                    BuyThroughHost(definition!, dock);
                    return;
                }

                paid = Mathf.Max(0f, definition!.value);
                if (!CommanderGameAccess.TrySpend(hq!, paid))
                {
                    SetStatus($"Insufficient faction funds for {definition.unitName}.");
                    CommanderNotice.Fail(statusText);
                    return;
                }
            }

            AddToQueue(definition!, dock, paid);
        }
        else
        {
            // The status line alone read as "nothing happened when I pressed buy".
            CommanderNotice.Fail(statusText);
        }
    }

    /// <summary>Empties the selected dock's queue and refunds it; the other docks keep building.</summary>
    internal void ClearBuildQueue()
    {
        DockQueue? queue = FindQueue(SelectedDock);
        if (queue != null)
        {
            Refund(CommanderGameAccess.GetLocalHq(), queue.Clear());
            dockQueues.Remove(queue);
        }

        SetStatus("Naval build queue cleared and refunded.");
    }

    /// <summary>Takes the hull at <paramref name="index"/> off the selected dock's queue and refunds it.</summary>
    internal void CancelQueued(int index)
    {
        DockQueue? queue = FindQueue(SelectedDock);
        if (queue == null || index < 0 || index >= queue.Ships.Count)
        {
            return;
        }

        string name = queue.Ships[index].unitName;
        float refund = queue.Paid[index];
        queue.Ships.RemoveAt(index);
        queue.Paid.RemoveAt(index);
        if (index == 0)
        {
            queue.DoneAt = 0f;
            queue.Blocked = false;
            queue.Held = null;
        }

        Refund(CommanderGameAccess.GetLocalHq(), refund);
        SetStatus(refund > 0f
            ? $"Cancelled {name}. Refunded {CommanderEconomyService.FundsLabel(refund)}."
            : $"Cancelled {name}.");
    }

    /// <summary>Arms the rally click for the selected dock: the next left click, in the 3D world or
    /// on the map, is where its new hulls sail to.</summary>
    internal void BeginRallySelection()
    {
        rallySelectionDock = SelectedDock;
        rallyClickTracker.Reset();
        SetStatus(rallySelectionDock == null
            ? "Select a naval dock first."
            : "Click the water where new ships should gather. Right-click cancels.");
    }

    internal void CancelRallySelection()
    {
        rallySelectionDock = null;
        rallyClickTracker.Reset();
    }

    internal void ClearRallyPoint()
    {
        Unit? dock = SelectedDock;
        if (dock != null)
        {
            rallyPoints.Remove(dock);
        }

        SetStatus("Cleared rally point. New ships wait beside the dock.");
    }

    internal bool TrySetRallyFromWorld(Vector2 screenPosition)
    {
        if (rallySelectionDock == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition point))
        {
            SetStatus("No valid rally position under the cursor.");
            return false;
        }

        CompleteRallySelection(point);
        return true;
    }

    internal bool TryGetSelectedRallyPoint(out GlobalPosition rally)
    {
        Unit? dock = SelectedDock;
        rally = default;
        return dock != null && rallyPoints.TryGetValue(dock, out rally);
    }

    internal string GetRallyLabel()
    {
        Unit? dock = SelectedDock;
        if (dock == null || !rallyPoints.TryGetValue(dock, out GlobalPosition rally))
        {
            return "none, new ships wait beside the dock";
        }

        return $"{FastMath.Distance(dock.GlobalPosition(), rally) / 1000f:0.0} km from the dock";
    }

    /// <summary>
    /// Buys and launches a ship for a faction that is not the player's. Same sea-lane entry, same
    /// dock gate, same funds — the enemy commander calls this rather than carrying a second copy of
    /// the entry search, which is the part that knows where a hull can actually be put in the water.
    /// A hull from the faction's factory reserve launches free. Returns what it spent: 0 for a
    /// reserve hull, -1 when nothing was launched.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Spawning runs base-game code that can throw anything; the purchase is refused and logged instead.")]
    internal float TryPurchaseForHq(FactionHQ hq, ShipDefinition definition, GlobalPosition rallyPoint, out Ship? ship, GlobalPosition? dockNear = null)
    {
        ship = null;
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        bool fromReserve = GetReserve(hq, definition) > 0;
        if (!hq.IsServer
            || spawner == null
            || (!fromReserve && hq.factionFunds < definition.value)
            || !IsUnlocked(definition, CommanderEconomyService.GetNavalDockLevel(hq, dockNear))
            || CommanderUnitCap.IsFull(hq)
            || CommanderUnitCap.IsKindFull(hq, definition)
            || CommanderUnitCap.IsAiTypeFull(hq, definition)
            || CommanderUnitBanService.Holds(definition))
        {
            return -1f;
        }

        if (!TryResolveRally(rallyPoint, out GlobalPosition lanePoint)
            || !TryFindEntry(definition, hq, lanePoint, dockNear, out GlobalPosition spawnPosition, out Quaternion rotation))
        {
            return -1f;
        }

        try
        {
            ship = spawner.SpawnShip(definition.unitPrefab, spawnPosition, rotation, hq, NewShipName(definition), 1f, holdPosition: false);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Enemy naval reinforcement spawn failed: {exception}");
            return -1f;
        }

        if (ship == null)
        {
            return -1f;
        }

        CommanderSupplyStoreService.Instance?.FillNewStore(ship);

        // A spaced slot rather than the point itself: every hull sent to one spot rammed the last.
        CommanderGameAccess.TrySetDestination(ship, FindRallySlot(definition, lanePoint, ship));
        if (fromReserve)
        {
            AddReserve(hq, definition, -1);
            return 0f;
        }

        float cost = Mathf.Max(0f, definition.value);
        hq.AddFunds(-cost);
        return cost;
    }

    private static void Refund(FactionHQ? hq, float amount)
    {
        if (hq != null && amount > 0f && !CommanderQueuePayment.Return(amount))
        {
            hq.AddFunds(amount);
        }
    }

    /// <summary>The ladder itself, in one place: the self-check below reads the same table the
    /// purchase gate does, or it would only ever be checking a copy of it.</summary>
    private static int GetRequiredDockLevel(ShipType type)
    {
        return type switch
        {
            // Small craft: what a quayside can service on day one.
            ShipType.PB or ShipType.LC => 1,

            // Escorts.
            ShipType.FFL or ShipType.FFG => 2,

            // Capital ships and amphibious warfare.
            _ => 3,
        };
    }

    private static Vector3 GetInwardRoadDirection(Road road, int pointIndex, GlobalPosition point)
    {
        Vector3 direction = Vector3.forward;
        if (pointIndex + 1 < road.points.Count)
        {
            direction = FastMath.NormalizedDirection(point, road.points[pointIndex + 1]);
        }
        else if (pointIndex > 0)
        {
            direction = FastMath.NormalizedDirection(point, road.points[pointIndex - 1]);
        }

        direction.y = 0f;
        Vector3 towardMapCenter = new(-point.x, 0f, -point.z);
        if (direction.sqrMagnitude < 0.01f)
        {
            direction = towardMapCenter;
        }

        if (Vector3.Dot(direction, towardMapCenter) < 0f)
        {
            direction = -direction;
        }

        return direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.forward;
    }

    private static float GetFriendlyEntryScore(
        FactionHQ hq,
        GlobalPosition point,
        GlobalPosition rallyPoint,
        float edgeDistance)
    {
        float friendlyDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            Transform anchor = airbase.center != null ? airbase.center : airbase.transform;
            friendlyDistance = Mathf.Min(
                friendlyDistance,
                FastMath.Distance(point, anchor.GlobalPosition()));
        }

        if (friendlyDistance == float.MaxValue)
        {
            friendlyDistance = FastMath.Distance(point, rallyPoint);
        }

        return friendlyDistance
            + (FastMath.Distance(point, rallyPoint) * 0.15f)
            + (edgeDistance * 2f);
    }

    /// <summary>
    /// Where a hull is sent: the clicked point itself when it is water (a creek or a harbour has no
    /// sea lane, and snapping to the nearest one sent the ship out to sea), else the nearest lane.
    /// </summary>
    private static bool TryResolveRally(GlobalPosition requested, out GlobalPosition rally)
    {
        rally = requested;
        if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(requested)))
        {
            return true;
        }

        RoadNetwork? seaLanes = NetworkSceneSingleton<LevelInfo>.i?.seaLanes;
        return seaLanes != null
            && seaLanes.Exists()
            && seaLanes.TryGetNearestPoint(requested, out rally, out _)
            && FastMath.Distance(requested, rally) <= MaximumRallySnapMeters;
    }

    /// <summary>
    /// The open water nearest the dock, in rings outward: a dock on a bay launches into the bay, not
    /// at the sea lane nearest it, which is out in the ocean. A dock stands on the shore, so nothing
    /// nearer than <see cref="ShoreClearMeters"/> can pass.
    /// </summary>
    private static bool TryFindDockside(ShipDefinition definition, GlobalPosition dock, out GlobalPosition spawnPosition, out Quaternion rotation)
    {
        float step = Mathf.Max(DocksideStepMeters, definition.width);
        for (float radius = ShoreClearMeters; radius <= MaximumDockEntryMeters; radius += step)
        {
            int slots = Mathf.CeilToInt(2f * Mathf.PI * radius / step);
            for (int i = 0; i < slots; i++)
            {
                float angle = i * 2f * Mathf.PI / slots;
                Vector3 outward = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                GlobalPosition probe = dock + (outward * radius);
                if (!CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(probe)))
                {
                    continue;
                }

                rotation = Quaternion.LookRotation(outward, Vector3.up);
                if (TryValidateEntry(definition, probe, rotation, out spawnPosition)
                    && HasOpenWaterToLane(spawnPosition))
                {
                    return true;
                }
            }
        }

        spawnPosition = default;
        rotation = Quaternion.identity;
        return false;
    }

    /// <summary>
    /// <see cref="ShoreClearMeters"/> of water past the hull on every side. ShipAI's shore probe and
    /// the dock push a hull off land, so one launched by the shore goes round in circles. Only the
    /// hull's own footprint must be deep. The whole disc is probed, outermost ring first, because a
    /// single ring misses a spit or island inside it.
    /// </summary>
    private static bool IsClearOfShore(ShipDefinition definition, GlobalPosition spawn)
    {
        float reach = (Mathf.Max(definition.length, definition.width) * 0.5f) + ShoreClearMeters;
        int rings = Mathf.CeilToInt(reach / ShoreProbeStepMeters);
        for (int ring = rings; ring > 0; ring--)
        {
            for (int i = 0; i < 24; i++)
            {
                GlobalPosition probe = spawn + (Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward * (reach * ring / rings));
                probe.y = -10000f;
                if (!CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(probe)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// ShipAI only steers along sea lanes, starting with a straight run to the nearest lane point.
    /// A hull launched behind a spit or a jetty from that point drove into the shore, was pushed off
    /// it and circled there, every order ignored. The same underwater sight line the base game uses
    /// to shortcut a path decides it, so a dock with no such water falls back to launching on a lane.
    /// </summary>
    private static bool HasOpenWaterToLane(GlobalPosition spawn)
    {
        return TryGetSeaLanePoint(spawn, out GlobalPosition lane)
            && FastMath.Distance(spawn, lane) <= MaximumDockEntryMeters
            && RoadPathfinder.VisibleUnderwater(spawn, lane);
    }

    /// <summary>
    /// Every bought hull needs a name of its own. A carrier's deck registers as the airbase
    /// "&lt;UNIT_AIRBASE&gt;++" + the ship's unique name (<c>Airbase.SetupAttachedAirbase</c>), into a
    /// dictionary that throws on a repeat, so with no name the second carrier or frigate bought had no
    /// deck to spawn on and the first one's spawn list broke with it.
    /// </summary>
    private static string NewShipName(ShipDefinition definition)
    {
        return $"GC_{definition.jsonKey}_{System.Guid.NewGuid():N}";
    }

    private static bool TryValidateEntry(
        ShipDefinition definition,
        GlobalPosition seaLanePoint,
        Quaternion rotation,
        out GlobalPosition spawnPosition)
    {
        Vector3 localPosition = seaLanePoint.ToLocalPosition();
        localPosition.y = Datum.LocalSeaY + definition.spawnOffset.y;
        spawnPosition = localPosition.ToGlobalPosition();

        float requiredDepth = Mathf.Max(8f, definition.height * 0.2f);
        Vector3 depthRayOrigin = new(localPosition.x, Datum.LocalSeaY + 5f, localPosition.z);
        if (Physics.Raycast(
            depthRayOrigin,
            Vector3.down,
            out RaycastHit seabedHit,
            2000f,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore)
            && Datum.LocalSeaY - seabedHit.point.y < requiredDepth)
        {
            return false;
        }

        // The centre alone let a carrier launch with its bow on the beach: 330 m of hull reaches
        // well past one depth probe, and a grounded ship never moves again. Every corner and edge of
        // the hull, plus room to turn, must be deep water. SnapToTerrain reads terrain from above, so
        // dry land counts as shallow; its miss (no seabed collider) falls back below this height.
        Vector3 forward = rotation * Vector3.forward;
        Vector3 right = rotation * Vector3.right;
        float margin = Mathf.Max(30f, definition.length * 0.2f);
        float halfLength = (definition.length * 0.5f) + margin;
        float halfWidth = (definition.width * 0.5f) + margin;
        for (int along = -1; along <= 1; along++)
        {
            for (int across = -1; across <= 1; across++)
            {
                if (along == 0 && across == 0)
                {
                    continue;
                }

                GlobalPosition probe = seaLanePoint + (forward * (along * halfLength)) + (right * (across * halfWidth));
                probe.y = -10000f;
                if (CommanderGameAccess.SnapToTerrain(probe).y > -requiredDepth)
                {
                    return false;
                }
            }
        }

        // Here rather than in the search, so a sea-lane launch cannot skip it.
        if (!IsClearOfShore(definition, spawnPosition))
        {
            return false;
        }

        // The box below missed a landing craft idling at the dock (it rides on an air cushion, its
        // colliders clear of the box), and a carrier spawned on top of it was thrown into the sky.
        // Every live hull is checked by its footprint instead of trusting its colliders. Just past
        // ShipAI's avoidance reach, not the doubled parking gap: that kept a launch refused (NO WATER)
        // with open water beside the dock, while the hull before sat parked where it launched.
        float hullRadius = Mathf.Max(definition.length, definition.width) * 0.5f;
        List<Unit> units = UnitRegistry.allUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i] is Ship other && other != null && !other.disabled)
            {
                GlobalPosition at = other.GlobalPosition();
                float clearance = hullRadius + other.maxRadius + ShipAvoidMeters;
                float dx = at.x - spawnPosition.x;
                float dz = at.z - spawnPosition.z;
                if ((dx * dx) + (dz * dz) < clearance * clearance)
                {
                    return false;
                }
            }
        }

        Vector3 halfExtents = new(
            Mathf.Max(20f, definition.width * 0.65f),
            Mathf.Max(8f, definition.height * 0.35f),
            Mathf.Max(30f, definition.length * 0.65f));
        Vector3 overlapCenter = localPosition + (Vector3.up * halfExtents.y);
        int obstructionMask = PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask;
        return !Physics.CheckBox(
            overlapCenter,
            halfExtents,
            rotation,
            obstructionMask,
            QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// The nearest water around <paramref name="center"/> with room for this hull: rings outward,
    /// each slot <see cref="ShipClearance"/> clear of every ship there and
    /// every slot already promised to one on its way. The first slot is the point itself, so a hull
    /// with nowhere else to go still stays where it was sent.
    /// </summary>
    private static GlobalPosition FindRallySlot(ShipDefinition definition, GlobalPosition center, Ship self)
    {
        RallyClaims.RemoveAll(claim => claim.Ship == null
            || claim.Ship.disabled
            || FastMath.InRange(claim.Ship.GlobalPosition(), claim.At, claim.Radius));

        float radius = Mathf.Max(definition.length, definition.width) * 0.5f;
        float step = ShipClearance(radius, radius);
        for (int ring = 0; ring <= RallyRings; ring++)
        {
            int count = ring == 0 ? 1 : ring * 6;
            for (int i = 0; i < count; i++)
            {
                float angle = i * 2f * Mathf.PI / count;
                GlobalPosition slot = center + (new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * step));
                if (IsFreeRallySlot(definition, slot, radius, self))
                {
                    RallyClaims.Add(new RallyClaim(self, slot, radius));
                    return slot;
                }
            }
        }

        return center;
    }

    /// <summary>Centre-to-centre distance at which two hulls' avoidance no longer traps either.</summary>
    private static float ShipClearance(float radius, float otherRadius)
    {
        return 2f * (radius + otherRadius + ShipAvoidMeters);
    }

    private static bool IsFreeRallySlot(ShipDefinition definition, GlobalPosition slot, float radius, Ship self)
    {
        // Deep water under the middle and out to the hull's reach on every side, the same test the
        // launch makes: a hull parked on a sandbar never moves again.
        float requiredDepth = Mathf.Max(8f, definition.height * 0.2f);
        for (int i = 0; i < 5; i++)
        {
            GlobalPosition probe = i == 0 ? slot : slot + (Quaternion.Euler(0f, i * 90f, 0f) * Vector3.forward * radius);
            probe.y = -10000f;
            if (CommanderGameAccess.SnapToTerrain(probe).y > -requiredDepth)
            {
                return false;
            }
        }

        List<Unit> units = UnitRegistry.allUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i] is Ship other && other != null && other != self && !other.disabled
                && FastMath.InRange(other.GlobalPosition(), slot, ShipClearance(radius, other.maxRadius)))
            {
                return false;
            }
        }

        for (int i = 0; i < RallyClaims.Count; i++)
        {
            if (RallyClaims[i].Ship != self
                && FastMath.InRange(RallyClaims[i].At, slot, ShipClearance(radius, RallyClaims[i].Radius)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNavalEntryNetwork()
    {
        return NetworkSceneSingleton<LevelInfo>.i?.seaLanes?.Exists() == true;
    }

    /// <summary>Every gate on a purchase; says why on the status line when one is shut.</summary>
    private bool CanBuy(ShipDefinition? definition, FactionHQ? hq, Unit? dock, bool prepaid = false)
    {
        if (definition == null)
        {
            SetStatus("No purchasable ships are available.");
            return false;
        }

        if (hq == null)
        {
            SetStatus("No friendly faction is available.");
            return false;
        }

        int dockLevel = CommanderEconomyService.GetNavalDockLevel(hq, DockPoint(dock));
        if (dockLevel <= 0)
        {
            SetStatus("No naval dock. Build one on the shore from the BUILD window before buying ships.");
            return false;
        }

        if (!IsUnlocked(definition, dockLevel))
        {
            SetStatus($"{definition.unitName} needs a level {GetRequiredDockLevel(definition)} naval dock.");
            return false;
        }

        if (!prepaid && GetReserve(hq, definition) <= 0 && hq.factionFunds < definition.value)
        {
            SetStatus($"Insufficient faction funds for {definition.unitName}.");
            return false;
        }

        if (CommanderUnitCap.IsFull(hq))
        {
            SetStatus($"Unit cap reached ({CommanderUnitCap.Cap} units).");
            return false;
        }

        if (CommanderUnitCap.IsKindFull(hq, definition))
        {
            SetStatus($"Ship cap reached ({CommanderUnitCap.ShipCap}).");
            return false;
        }

        if (CommanderUnitBanService.Holds(definition))
        {
            SetStatus(CommanderUnitBanService.IsVoting
                ? "Nothing spawns until the unit ban vote locks."
                : $"{definition.unitName} is banned this match.");
            return false;
        }

        if (!HasNavalEntryNetwork())
        {
            SetStatus("This map has no valid naval reinforcement route.");
            return false;
        }

        return true;
    }

    /// <summary>The launch's own entry search, run before any money moves: a hull with nowhere to go
    /// is refused at the click rather than paid for and left stuck on the queue.</summary>
    private bool HasLaunchWater(ShipDefinition definition, FactionHQ hq, Unit? dock)
    {
        if (CommanderEconomyService.TryGetNavalDockPosition(hq, DockPoint(dock), out GlobalPosition dockAt)
            && TryFindEntry(definition, hq, dockAt, dockAt, out _, out _))
        {
            return true;
        }

        SetStatus($"No open water {ShoreClearMeters:0} m from the shore near this dock for a {definition.unitName}.");
        return false;
    }

    /// <summary>False only when there is no clear water yet, which is worth asking again; every other
    /// outcome, a refusal included, takes the hull off the build queue.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Spawning runs base-game code that can throw anything; the purchase is refused and logged instead.")]
    private bool CompletePurchase(ShipDefinition definition, FactionHQ hq, Unit? dock, float paid)
    {
        bool prepaid = paid > 0f;

        // The hull launches at the dock and waits there: with the dock deciding where it enters, a
        // rally click only chose which way it sailed off afterwards.
        GlobalPosition? dockNear = DockPoint(dock);
        if (!CommanderEconomyService.TryGetNavalDockPosition(hq, dockNear, out GlobalPosition dockAt)
            || !TryFindEntry(definition, hq, dockAt, dockAt, out GlobalPosition spawnPosition, out Quaternion rotation))
        {
            SetStatus($"No open water {ShoreClearMeters:0} m from the shore near the naval dock right now.");
            CommanderNotice.Fail($"{definition.unitName} CANNOT LAUNCH: NO OPEN WATER NEAR THE NAVAL DOCK");
            return false;
        }

        bool hasRally = TryGetRally(dock, out GlobalPosition rally);

        // A guest's hull is bought and launched by the host, through the same entry search the
        // enemy commander's purchases use. With no rally it is sent to where it launched, which
        // keeps it there.
        if (CommanderNetwork.Forward(
            "ship",
            definition.jsonKey,
            CommanderNetwork.Position(hasRally ? rally : spawnPosition),
            CommanderNetwork.Position(dockAt),
            CommanderQueuePayment.Text(paid)))
        {
            SetStatus(CommanderNetwork.HostHasMod
                ? $"{definition.unitName} on its way."
                : CommanderNetwork.HostMissingReason);
            return true;
        }

        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (spawner == null || NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Ship purchasing is only available to the host.");
            return true;
        }

        Ship? ship;
        try
        {
            ship = spawner.SpawnShip(
                definition.unitPrefab,
                spawnPosition,
                rotation,
                hq,
                NewShipName(definition),
                1f,
                holdPosition: false);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Naval reinforcement spawn failed: {exception}");
            SetStatus("The ship could not be spawned. No funds were deducted.");
            return true;
        }

        if (ship == null)
        {
            SetStatus("The ship could not be spawned. No funds were deducted.");
            return true;
        }

        CommanderSupplyStoreService.Instance?.FillNewStore(ship);

        // Bought when it was queued: nothing more to take, from the reserve or the funds.
        bool fromReserve = !prepaid && GetReserve(hq, definition) > 0;
        if (fromReserve)
        {
            AddReserve(hq, definition, -1);
        }
        else if (!prepaid)
        {
            hq.AddFunds(-definition.value);
        }

        if (hasRally)
        {
            CommanderGameAccess.TrySetDestination(ship, FindRallySlot(definition, rally, ship));
        }

        SetStatus($"{definition.unitName} {(fromReserve ? "launched from reserve" : "purchased")} at the naval dock.");
        return true;
    }

    /// <summary>Where the hull ordered at <paramref name="dock"/> launches: that dock, else the selected
    /// one, else null (the faction's best dock). The level gate and the launch both read this.</summary>
    private GlobalPosition? DockPoint(Unit? dock)
    {
        if (dock == null || dock.disabled)
        {
            dock = SelectedDock;
        }

        return dock != null ? dock.GlobalPosition() : null;
    }

    /// <summary>The water a hull from <paramref name="dock"/> sails to, when that dock has a rally point.</summary>
    private bool TryGetRally(Unit? dock, out GlobalPosition water)
    {
        water = default;
        dock ??= SelectedDock;
        return dock != null && rallyPoints.TryGetValue(dock, out GlobalPosition rally) && TryResolveRally(rally, out water);
    }

    private void TickRallyMapSelection()
    {
        if (rallySelectionDock == null || rallySelectionDock.disabled)
        {
            CancelRallySelection();
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (DynamicMap.mapMaximized && dynamicMap != null && rallyClickTracker.Tick(dynamicMap, out GlobalPosition point))
        {
            CompleteRallySelection(point);
        }
    }

    /// <summary>Stores the water nearest the click: the creek itself when it was water, else the sea lane.</summary>
    private void CompleteRallySelection(GlobalPosition point)
    {
        Unit? dock = rallySelectionDock;
        CancelRallySelection();
        if (dock == null)
        {
            return;
        }

        if (!TryResolveRally(point, out GlobalPosition water))
        {
            SetStatus("No navigable water near there. Click on the sea.");
            return;
        }

        rallyPoints[dock] = water;
        SetStatus("Naval rally point set.");
    }

    private bool TryFindEntry(
        ShipDefinition definition,
        FactionHQ hq,
        GlobalPosition rallyPoint,
        GlobalPosition? dockNear,
        out GlobalPosition spawnPosition,
        out Quaternion rotation)
    {
        spawnPosition = default;
        rotation = Quaternion.identity;
        LevelInfo? levelInfo = NetworkSceneSingleton<LevelInfo>.i;
        RoadNetwork? seaLanes = levelInfo?.seaLanes;
        MapSettings? mapSettings = levelInfo?.LoadedMapSettings;
        if (seaLanes == null || mapSettings == null || !seaLanes.Exists())
        {
            return false;
        }

        float halfWidth = mapSettings.MapSize.x * 0.5f;
        float halfHeight = mapSettings.MapSize.y * 0.5f;
        float entryBand = Mathf.Max(
            MinimumEntryBandMeters,
            Mathf.Min(mapSettings.MapSize.x, mapSettings.MapSize.y) * EntryBandMapFraction);

        // A hull belongs beside the harbour that unlocked it. Without a dock there is nothing to
        // anchor to and the ship still has to come from somewhere, so it sails in off the map edge
        // the way it always did; with one, water right beside the dock comes first, then every sea
        // lane within MaximumDockEntryMeters of it, nearest to the dock first. The edge band was what
        // put a purchase on the far coast: it threw away every lane point near the dock before the
        // score was ever asked. If nothing near the dock is clear (its creek filled in), the
        // purchase is refused rather than launched somewhere else.
        bool hasDock = CommanderEconomyService.TryGetNavalDockPosition(hq, dockNear, out GlobalPosition dock);
        if (hasDock && TryFindDockside(definition, dock, out spawnPosition, out rotation))
        {
            return true;
        }

        entryCandidates.Clear();
        foreach (Road road in seaLanes.roads)
        {
            if (road?.points == null)
            {
                continue;
            }

            for (int i = 0; i < road.points.Count; i++)
            {
                GlobalPosition point = road.points[i];
                float edgeDistance = Mathf.Min(
                    halfWidth - Mathf.Abs(point.x),
                    halfHeight - Mathf.Abs(point.z));
                if (edgeDistance < 0f
                    || (!hasDock && edgeDistance > entryBand)
                    || (hasDock && FastMath.Distance(point, dock) > MaximumDockEntryMeters))
                {
                    continue;
                }

                Vector3 direction = GetInwardRoadDirection(road, i, point);
                float score = hasDock
                    ? FastMath.Distance(point, dock) + (FastMath.Distance(point, rallyPoint) * 0.15f)
                    : GetFriendlyEntryScore(hq, point, rallyPoint, edgeDistance);
                entryCandidates.Add(new EntryCandidate(point, direction, score));
            }
        }

        entryCandidates.Sort(static (left, right) => left.Score.CompareTo(right.Score));
        for (int i = 0; i < entryCandidates.Count; i++)
        {
            EntryCandidate candidate = entryCandidates[i];
            Quaternion candidateRotation = Quaternion.LookRotation(candidate.Direction, Vector3.up);
            if (!TryValidateEntry(definition, candidate.Position, candidateRotation, out GlobalPosition validatedPosition))
            {
                continue;
            }

            spawnPosition = validatedPosition;
            rotation = candidateRotation;
            return true;
        }

        return false;
    }

    private void RefreshDefinitions()
    {
        ShipDefinition? previouslySelected = SelectedDefinition;
        shipDefinitions.Clear();
        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.ships != null)
        {
            for (int i = 0; i < encyclopedia.ships.Count; i++)
            {
                ShipDefinition definition = encyclopedia.ships[i];
                if (definition != null
                    && definition.unitPrefab != null
                    && definition.IsAllowed(includeEventContent: false)
                    && definition.unitPrefab.GetComponent<Ship>() != null)
                {
                    shipDefinitions.Add(definition);
                }
            }
        }

        shipDefinitions.Sort(static (left, right) =>
        {
            int valueComparison = left.value.CompareTo(right.value);
            return valueComparison != 0
                ? valueComparison
                : string.Compare(left.unitName, right.unitName, StringComparison.OrdinalIgnoreCase);
        });
        int preservedIndex = previouslySelected == null ? -1 : shipDefinitions.IndexOf(previouslySelected);
        selectedIndex = preservedIndex >= 0
            ? preservedIndex
            : Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, shipDefinitions.Count - 1));
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    /// <summary>A guest's ship purchase. Dock gate, funds and sea-lane entry all checked here.</summary>
    private void HostPurchase(FactionHQ hq, string[] args)
    {
        ShipDefinition? definition = CommanderNetwork.Definition<ShipDefinition>(CommanderNetwork.Arg(args, 1));
        if (definition == null || !CommanderNetwork.TryPosition(args, 2, out GlobalPosition rally))
        {
            return;
        }

        // A hull bought when it was queued: its deposit goes back to the faction and the purchase
        // below takes it again. If the launch then fails, the money is simply back with the team,
        // which is right, because a guest's hull has left its queue by now.
        float paid = CommanderNetwork.Float(args, 4);
        if (CommanderQueuePayment.Draw(paid))
        {
            hq.AddFunds(paid);
        }

        GlobalPosition? dockNear = CommanderNetwork.TryPosition(args, 3, out GlobalPosition dockAt) ? dockAt : null;
        if (TryPurchaseForHq(hq, definition, rally, out _, dockNear) >= 0f)
        {
            CommanderNetwork.Reply("st.naval", $"{definition.unitName} launched at the naval dock.");
        }
        else
        {
            CommanderNotice.Fail($"{definition.unitName} could not be bought: check the naval dock level, "
                + $"the funds, and that there is open water {ShoreClearMeters:0} m from the shore near the dock.");
        }
    }

    private void SyncReserve(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "ships" };
        if (reserve.TryGetValue(hq, out Dictionary<ShipDefinition, int> held))
        {
            foreach (KeyValuePair<ShipDefinition, int> entry in held)
            {
                if (entry.Value > 0)
                {
                    args.Add(entry.Key.jsonKey);
                    args.Add(CommanderNetwork.Num(entry.Value));
                }
            }
        }

        messages.Add(args.ToArray());
    }

    /// <summary>The guest's side of <see cref="SyncReserve"/>: the host's count replaces its own.</summary>
    private void ApplyReserve(string[] args)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        reserve.Remove(hq);
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            ShipDefinition? definition = CommanderNetwork.Definition<ShipDefinition>(args[i]);
            if (definition != null)
            {
                AddReserve(hq, definition, CommanderNetwork.Int(args, i + 1));
            }
        }
    }

    /// <summary>
    /// <see cref="CanBuy"/> for the head of a queue, remembering why it was refused. A refusal once
    /// the clock had run left the tile at 0:01 with the reason only on a status line, read as a hang.
    /// </summary>
    private bool CanLaunch(DockQueue queue, ShipDefinition definition, FactionHQ? hq)
    {
        if (CanBuy(definition, hq, queue.Dock, queue.Paid[0] > 0f))
        {
            queue.Held = null;
            return true;
        }

        if (queue.Held != statusText)
        {
            queue.Held = statusText;
            CommanderPlugin.Log.LogInfo($"Naval queue holds {definition.unitName}: {statusText}");
        }

        return false;
    }

    private void TickQueue(DockQueue queue, FactionHQ? hq)
    {
        // The clock starts once the hull can be bought, the same rule as a depot queue.
        ShipDefinition definition = queue.Ships[0];
        if (queue.DoneAt <= 0f)
        {
            if (!CanLaunch(queue, definition, hq))
            {
                return;
            }

            queue.Seconds = GetReserve(hq, definition) > 0 ? 0f : CommanderBuildTimes.For(definition);
            queue.DoneAt = Time.time + queue.Seconds;
        }

        if (Time.time < queue.DoneAt)
        {
            return;
        }

        bool canBuy = CanLaunch(queue, definition, hq);
        if (!canBuy || !CompletePurchase(definition, hq!, queue.Dock, queue.Paid[0]))
        {
            if (canBuy && !queue.Blocked)
            {
                CommanderPlugin.Log.LogInfo($"Naval queue holds {definition.unitName}: no clear water beside the dock.");
            }

            queue.Blocked = canBuy;
            queue.DoneAt = Time.time + LaunchRetrySeconds;
            return;
        }

        queue.Ships.RemoveAt(0);
        queue.Paid.RemoveAt(0);
        queue.DoneAt = 0f;
        queue.Blocked = false;
    }

    private void AddToQueue(ShipDefinition definition, Unit? dock, float paid)
    {
        DockQueue? queue = FindQueue(dock);
        if (queue == null)
        {
            queue = new DockQueue(dock);
            dockQueues.Add(queue);
        }

        queue.Ships.Add(definition);
        queue.Paid.Add(paid);
        SetStatus($"{definition.unitName} queued at the naval dock.");
    }

    /// <summary>A guest's purchase: into the queue once the host has taken the money.</summary>
    private void BuyThroughHost(ShipDefinition definition, Unit? dock)
    {
        SetStatus($"Buying {definition.unitName}...");
        CommanderQueuePayment.Buy(definition, paid =>
        {
            if (paid > 0f)
            {
                AddToQueue(definition, dock, paid);
            }
            else
            {
                SetStatus($"Insufficient faction funds for {definition.unitName}.");
            }
        });
    }

    /// <summary>Hulls queued at any dock that will take a reserve hull when they launch.</summary>
    private int CountUnpaidQueued(ShipDefinition definition)
    {
        int count = 0;
        for (int q = 0; q < dockQueues.Count; q++)
        {
            DockQueue queue = dockQueues[q];
            for (int i = 0; i < queue.Ships.Count; i++)
            {
                count += queue.Ships[i] == definition && queue.Paid[i] <= 0f ? 1 : 0;
            }
        }

        return count;
    }

    private DockQueue? FindQueue(Unit? dock)
    {
        for (int i = 0; i < dockQueues.Count; i++)
        {
            if (dockQueues[i].Dock == dock)
            {
                return dockQueues[i];
            }
        }

        return null;
    }

    private readonly struct EntryCandidate
    {
        internal EntryCandidate(GlobalPosition position, Vector3 direction, float score)
        {
            Position = position;
            Direction = direction;
            Score = score;
        }

        internal GlobalPosition Position { get; }

        internal Vector3 Direction { get; }

        internal float Score { get; }
    }

    private readonly struct RallyClaim
    {
        internal RallyClaim(Ship ship, GlobalPosition at, float radius)
        {
            Ship = ship;
            At = at;
            Radius = radius;
        }

        internal Ship Ship { get; }

        internal GlobalPosition At { get; }

        internal float Radius { get; }
    }

    private sealed class DockQueue
    {
        internal DockQueue(Unit? dock)
        {
            Dock = dock;
        }

        internal Unit? Dock { get; }

        internal List<ShipDefinition> Ships { get; } = new();

        /// <summary>What each hull in <see cref="Ships"/> was bought for when queued, 0 for one that
        /// takes a reserve hull.</summary>
        internal List<float> Paid { get; } = new();

        internal float DoneAt { get; set; }

        internal float Seconds { get; set; }

        internal bool Blocked { get; set; }

        /// <summary>Why the head hull is being refused right now, null while it can be bought.</summary>
        internal string? Held { get; set; }

        /// <summary>Empties the queue; returns what its hulls had been paid for, in total.</summary>
        internal float Clear()
        {
            float paid = 0f;
            for (int i = 0; i < Paid.Count; i++)
            {
                paid += Paid[i];
            }

            Ships.Clear();
            Paid.Clear();
            return paid;
        }
    }
}
