using System;
using System.Collections.Generic;
using System.Globalization;
using Mirage;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderSpawnService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    private const float DepotRefreshIntervalSeconds = 5f;
    private const float SpawnUpdateIntervalSeconds = 0.3f;
    private const float DepotSpawnDetectionRadius = 180f;
    private const float RallyExitToleranceMeters = 12f;

    /// <summary><c>RearmVehicleAI.AIState.ReloadingFromRearmer</c>.</summary>
    private const int RearmAiQuietState = 3;

    /// <summary>How far from the depot door a new vehicle counts as out of it. The depot's own
    /// roll-off push lets go at 20 m.</summary>
    private const float DepotClearedMeters = 30f;
    private const float RallyFormationSpacingMeters = 25f;

    /// <summary>Slots an AUTO ATTACK squad spreads over round a base's hold point, so a long match
    /// does not push the later units out past the edge of a small capture ring.</summary>
    private const int AutoAttackSlots = 24;

    /// <summary>How far past the depot exit newly bought vehicles park when no rally point is set.</summary>
    private const float DefaultStagingDistanceMeters = 150f;

    /// <summary>Spacing of the samples along a drive that are checked for buildings.</summary>
    private const float DriveProbeStepMeters = 10f;

    /// <summary>Half the width of the lane a drive needs clear: a vehicle plus a margin.</summary>
    private const float DriveLaneRadiusMeters = 6f;

    /// <summary>How close a building may stand to a rally spot.</summary>
    private const float RallyClearanceMeters = 10f;

    /// <summary>How far down the exit lane a verge search starts. The depot's own exit push is
    /// 20 m, so this is past the doorway and, on any normal depot, outside its walls.</summary>
    private const float VergeLaneOffsetMeters = 30f;

    /// <summary>Formation slots a unit may skip past buildings before it takes the one it has.</summary>
    private const int MaxRallySlotSkips = 16;

    /// <summary>The centre plus two rings (50 m out); the slot count wraps after these.</summary>
    private const int RallySlotsKept = 16;

    /// <summary>Height change between two drive samples, 10 m apart, that no vehicle drives over
    /// (about 30 degrees). A staging point past a cliff edge was a unit driven off it.</summary>
    private const float MaxDriveStepRiseMeters = 6f;
    private const float StatusDurationSeconds = 4f;

    /// <summary>How long a guest waits for the host to answer a spawn before asking again, and how
    /// soon it asks again after the host said not yet (depot cooldown, empty shelves).</summary>
    private const float HostAnswerTimeoutSeconds = 5f;
    private const float HostRetrySeconds = 3f;

    /// <summary>Headings off the depot door tried for a straight, open drive to the staging block;
    /// straight out first, then fanning out to either side. The first one clear all the way wins.</summary>
    private static readonly System.Reflection.FieldInfo? RearmAiStateField =
        HarmonyLib.AccessTools.Field(typeof(RearmVehicleAI), "currentState");

    private static readonly float[] StagingHeadingsDegrees = { 0f, 20f, -20f, 40f, -40f };

    /// <summary>Headings off the exit lane tried for a parking spot beside it, widest turn first.
    /// All of them turn forward of the door line: a heading back past it is a heading into the
    /// depot compound.</summary>
    private static readonly float[] VergeHeadingsDegrees = { 90f, -90f, 70f, -70f, 50f, -50f };

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderFactionVehicleService factionVehicleService;
    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderMapClickTracker rallyClickTracker = new();
    private readonly List<VehicleDepotEntry> depots = new();
    private readonly List<VehicleDefinition> vehicleDefinitions = new();
    private readonly List<VehicleDefinition> productionVehicleDefinitions = new();
    private readonly List<VehicleDefinition> factionVehicleDefinitions = new();
    private readonly List<VehicleDefinition> emptyVehicleDefinitions = new();
    private readonly List<VehicleDefinition> filteredVehicleDefinitions = new();
    private readonly Dictionary<string, List<VehicleDefinition>> definitionsByCategory = new(StringComparer.Ordinal);
    private readonly List<string> productionCategories = new();
    private readonly List<string> factionCategories = new();
    private readonly Dictionary<VehicleDepot, DepotSpawnQueue> depotQueues = new();

    /// <summary>Host only: <c>Time.time</c> each depot's booked builds finish, whoever bought them.</summary>
    private readonly Dictionary<VehicleDepot, float> depotFreeAt = new();

    /// <summary>Host only: each guest's current build per depot, so a repeated request is the same booking.</summary>
    private readonly Dictionary<(VehicleDepot Depot, INetworkPlayer Guest), (string Key, float DoneAt, float Seconds)> guestBookings = new();
    private readonly Dictionary<Unit, Factory> productionFactories = new();
    private readonly List<Unit> friendlyUnitsScratch = new();
    private readonly List<VehicleDepot> queuesToRemove = new();
    private VehicleDepot? autoReserveDepot;
    private DepotSpawnQueue? rallySelectionQueue;
    private float nextDepotRefreshTime;
    private float nextSpawnUpdateTime;
    private FactionHQ? boundProductionHq;
    private bool productionCatalogDirty = true;
    private float statusUntil;
    private string statusText = string.Empty;
    private string[] categories = { "All" };

    internal CommanderSpawnService(
        CommanderSelectionService selectionService,
        CommanderFactionVehicleService factionVehicleService,
        CommanderTacticalMapService tacticalMapService)
    {
        this.selectionService = selectionService;
        this.factionVehicleService = factionVehicleService;
        this.tacticalMapService = tacticalMapService;
        Instance = this;
        CommanderNetwork.OnHost("depot", HostSpawn);
        CommanderNetwork.OnGuest("depot.ack", OnDepotAck);
        CommanderNetwork.OnHost("depot.slot", HostBookBuild);
        CommanderNetwork.OnGuest("depot.slot.ack", OnBuildBooked);
        CommanderQueuePayment.Initialize();
    }

    internal static CommanderSpawnService? Instance { get; private set; }

    internal VehicleDepot? SelectedDepot { get; private set; }

    internal bool AwaitingRallyPointSelection { get; private set; }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    public void Activate()
    {
        AwaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        SelectedDepot = null;
        nextDepotRefreshTime = CommanderScheduler.Stagger("spawn.depots", DepotRefreshIntervalSeconds, 0.8f);
        nextSpawnUpdateTime = CommanderScheduler.Stagger("spawn.queues", SpawnUpdateIntervalSeconds);
        RefreshProductionBindings();
        statusText = string.Empty;
        productionCatalogDirty = true;
        if (vehicleDefinitions.Count == 0)
        {
            RefreshVehicleDefinitions();
        }

        RefreshDepots();
        SyncSelectedDepotFromSelection();
    }

    public void Deactivate()
    {
        UnbindProductionHq();
        AwaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        SelectedDepot = null;
        statusText = string.Empty;
        depots.Clear();
        rallyClickTracker.Reset();
    }

    public void ResetSession()
    {
        Deactivate();
        CommanderQueuePayment.ResetSession();
        autoReserveDepot = null;
        depotQueues.Clear();
        depotFreeAt.Clear();
        guestBookings.Clear();
        vehicleDefinitions.Clear();
        productionVehicleDefinitions.Clear();
        factionVehicleDefinitions.Clear();
        productionCategories.Clear();
        factionCategories.Clear();
        definitionsByCategory.Clear();
        categories = new[] { "All" };
        productionCatalogDirty = true;
    }

    public void TickActive()
    {
        if (CommanderScheduler.IsDue(ref nextDepotRefreshTime, DepotRefreshIntervalSeconds))
        {
            RefreshDepots();
            RefreshProductionBindings();
        }

        SyncSelectedDepotFromSelection();
        HandleRallyPointMapSelection();
    }

    public void TickPersistent()
    {
        if (CommanderScheduler.IsDue(ref nextSpawnUpdateTime, SpawnUpdateIntervalSeconds))
        {
            TickAutoReserve();
            UpdateSpawnQueues();
        }
    }

    internal static void NotifyFactoryChanged(Factory factory)
    {
        Instance?.OnFactoryChanged(factory);
    }

    /// <summary>
    /// The clearest straight drive out of a depot's door: its heading and how far along it a
    /// vehicle can stand clear of whatever blocks it (<paramref name="maxMeters"/> when nothing does).
    /// Shared by the default staging block and the starting builders.
    /// </summary>
    internal static bool TryFindClearExit(VehicleDepot depot, float maxMeters, out GlobalPosition door, out Vector3 bestDirection, out float standMeters)
    {
        door = default;
        bestDirection = Vector3.forward;
        standMeters = 0f;
        Transform? spawnTransform = CommanderGameAccess.GetDepotSpawnTransform(depot);
        if (spawnTransform == null)
        {
            return false;
        }

        // New units drive to the staging block in a straight line, so the whole drive has to be
        // clear, not just the spot: checking only the spot put it past a building at an airport and
        // the units drove into the wall on the way. Straight out of the door first, then fanning out
        // to either side; if no heading is clear all the way, the longest clear drive wins.
        door = spawnTransform.position.ToGlobalPosition();
        Vector3 forward = spawnTransform.forward;
        forward.y = 0f;
        forward.Normalize();
        bestDirection = forward;
        float bestMeters = -1f;
        for (int i = 0; i < StagingHeadingsDegrees.Length && bestMeters < maxMeters; i++)
        {
            Vector3 direction = Quaternion.Euler(0f, StagingHeadingsDegrees[i], 0f) * forward;
            float clear = GetClearDriveMeters(door, direction, maxMeters, depot);
            if (clear > bestMeters)
            {
                bestMeters = clear;
                bestDirection = direction;
            }
        }

        // Short of whatever blocked the drive, so the spot itself stands clear of it too.
        standMeters = bestMeters >= maxMeters
            ? maxMeters
            : Mathf.Max(DriveProbeStepMeters, bestMeters - RallyClearanceMeters);
        return true;
    }

    /// <summary>
    /// A standing spot beside a depot's exit lane, for a vehicle that parks rather than drives away:
    /// <paramref name="origin"/> is out on the lane, and the spot is that far to the side of it. The
    /// lane itself stays clear for whatever the depot spawns next.
    /// </summary>
    internal static bool TryFindClearVerge(VehicleDepot depot, float maxMeters, out GlobalPosition origin, out Vector3 vergeDirection, out float standMeters)
    {
        vergeDirection = Vector3.forward;
        standMeters = 0f;
        if (!TryFindClearExit(depot, maxMeters, out origin, out Vector3 lane, out float laneClear))
        {
            return false;
        }

        // Out on the lane first, not sideways off the door. Stepping sideways from the door ran the
        // probe through the depot's own walls - and the depot is the one obstacle the drive search
        // ignores, because the lane starts inside it - so the builders were spawned in the compound
        // with no way out. From a point past the doorway the depot is an obstacle like any other,
        // and a verge that crosses it is rejected.
        origin = CommanderGameAccess.SnapToTerrain(origin + (lane * Mathf.Min(laneClear, VergeLaneOffsetMeters)));

        // Turned well off the lane, the widest angle that is clear winning, so the parked vehicle is
        // neither in the doorway nor in front of the drive out of it.
        float bestMeters = -1f;
        for (int i = 0; i < VergeHeadingsDegrees.Length; i++)
        {
            Vector3 direction = Quaternion.Euler(0f, VergeHeadingsDegrees[i], 0f) * lane;
            float clear = GetClearDriveMeters(origin, direction, maxMeters, null);
            if (clear > bestMeters)
            {
                bestMeters = clear;
                vergeDirection = direction;
            }
        }

        if (bestMeters < DriveProbeStepMeters)
        {
            // Hemmed in on both sides. Further down the lane, which the exit search has already
            // walked clear, beats a spot 10 m into whatever is standing there - the old floor under
            // this number is exactly how a builder ended up inside a wall.
            vergeDirection = lane;
            standMeters = Mathf.Max(0f, laneClear - VergeLaneOffsetMeters);
            return true;
        }

        standMeters = bestMeters >= maxMeters
            ? maxMeters
            : Mathf.Max(DriveProbeStepMeters, bestMeters - RallyClearanceMeters);
        return true;
    }

    internal static string GetFactionFundsLabel()
    {
        return UnitConverter.ValueReading(CommanderFactionVehicleService.FactionFunds) ?? CommanderFactionVehicleService.FactionFunds.ToString("F1", CultureInfo.CurrentCulture);
    }

    internal void CancelRallyPointSelection()
    {
        AwaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        rallyClickTracker.Reset();
    }

    internal bool IsMapInteractionActive()
    {
        return DynamicMap.mapMaximized && !tacticalMapService.IsOpen;
    }

    internal void ToggleMap()
    {
        tacticalMapService.Toggle();
    }

    internal DepotSpawnQueue? GetSelectedQueue()
    {
        return SelectedDepot != null ? GetOrCreateQueue(SelectedDepot) : null;
    }

    internal bool TryGetSelectedRallyPoint(out GlobalPosition rallyPoint)
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue != null && queue.HasRallyPoint)
        {
            rallyPoint = queue.RallyPoint;
            return true;
        }

        rallyPoint = default;
        return false;
    }

    internal string GetSelectedDepotLabel()
    {
        VehicleDepotEntry? entry = GetSelectedDepotEntry();
        if (entry != null)
        {
            return entry.Label;
        }

        Unit? focusedUnit = selectionService.FocusedSelection;
        return focusedUnit != null ? CommanderGameAccess.GetUnitLabel(focusedUnit) : "None";
    }

    internal void SelectNearestDepot()
    {
        if (depots.Count == 0)
        {
            RefreshDepots();
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null || depots.Count == 0)
        {
            CommanderNotice.Fail("No friendly depot is available.");
            SetStatus("No friendly depot is available.");
            return;
        }

        VehicleDepotEntry? nearest = null;
        float nearestSqrDistance = float.MaxValue;
        Vector3 cameraPosition = camera.transform.position;
        for (int i = 0; i < depots.Count; i++)
        {
            VehicleDepotEntry entry = depots[i];
            float sqrDistance = (entry.Owner.transform.position - cameraPosition).sqrMagnitude;
            if (sqrDistance < nearestSqrDistance)
            {
                nearestSqrDistance = sqrDistance;
                nearest = entry;
            }
        }

        if (nearest != null)
        {
            selectionService.SelectUnit(nearest.Owner, false);
            SetStatus($"Selected nearest depot: {nearest.Label}");
        }
    }

    internal string[] BuildCategories()
    {
        if (vehicleDefinitions.Count == 0)
        {
            RefreshVehicleDefinitions();
        }

        if (!CommanderSettings.LimitToFactoryVehicles)
        {
            return categories;
        }

        RefreshFactionVehicleDefinitions();
        factionCategories.Clear();
        for (int i = 0; i < factionVehicleDefinitions.Count; i++)
        {
            string category = CommanderGameAccess.GetVehicleCategoryLabel(factionVehicleDefinitions[i]);
            if (!factionCategories.Contains(category))
            {
                factionCategories.Add(category);
            }
        }

        factionCategories.Sort(StringComparer.OrdinalIgnoreCase);
        factionCategories.Add("All");
        return factionCategories.ToArray();
    }

    internal List<VehicleDefinition> GetFilteredVehicleDefinitions(string selectedCategory)
    {
        if (vehicleDefinitions.Count == 0)
        {
            RefreshVehicleDefinitions();
        }

        List<VehicleDefinition> source;
        if (string.IsNullOrWhiteSpace(selectedCategory)
            || string.Equals(selectedCategory, "All", StringComparison.Ordinal))
        {
            source = vehicleDefinitions;
        }
        else
        {
            if (!definitionsByCategory.TryGetValue(selectedCategory, out List<VehicleDefinition>? filtered))
            {
                return emptyVehicleDefinitions;
            }

            source = filtered;
        }

        // A draft's depot lists what is left in the drafted reserve and nothing else.
        if (CommanderDraftService.IsDraftMission)
        {
            filteredVehicleDefinitions.Clear();
            for (int i = 0; i < source.Count; i++)
            {
                if (CommanderFactionVehicleService.GetReserveCount(source[i]) > 0)
                {
                    filteredVehicleDefinitions.Add(source[i]);
                }
            }

            return filteredVehicleDefinitions;
        }

        bool factionOnly = CommanderSettings.LimitToFactoryVehicles;
        if (!factionOnly && !CommanderUnitBanService.AnyBanned)
        {
            return source;
        }

        if (factionOnly)
        {
            RefreshFactionVehicleDefinitions();
        }

        filteredVehicleDefinitions.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            VehicleDefinition definition = source[i];
            if ((!factionOnly || factionVehicleDefinitions.Contains(definition)) && !CommanderUnitBanService.IsBanned(definition))
            {
                filteredVehicleDefinitions.Add(definition);
            }
        }

        return filteredVehicleDefinitions;
    }

    internal bool IsCategoryHeld(string category)
    {
        return factionVehicleService.IsCategoryHeld(category);
    }

    internal void ToggleHeldCategory(string category)
    {
        factionVehicleService.ToggleCategory(category);
    }

    internal bool IsVehicleHeld(VehicleDefinition definition)
    {
        return factionVehicleService.IsDefinitionHeld(definition);
    }

    internal void ToggleHeldVehicle(VehicleDefinition definition)
    {
        factionVehicleService.ToggleDefinition(definition);
    }

    internal IReadOnlyList<VehicleDefinition> GetProductionVehicleDefinitions()
    {
        RefreshProductionVehicleDefinitions();
        return productionVehicleDefinitions;
    }

    internal IReadOnlyList<string> GetProductionCategories()
    {
        RefreshProductionVehicleDefinitions();
        return productionCategories;
    }

    internal string GetVehicleSpawnLabel(VehicleDefinition definition)
    {
        if (CommanderDraftService.IsDraftMission)
        {
            int reserve = CommanderFactionVehicleService.GetReserveCount(definition);
            int queued = CountQueued(definition);
            return $"{CommanderGameAccess.GetVehicleLabel(definition)}  |  {reserve} IN RESERVE"
                + (queued > 0 ? $"  |  {queued} QUEUED" : string.Empty);
        }

        string cost = UnitConverter.ValueReading(CommanderFactionVehicleService.GetPurchaseCost(definition))
            ?? CommanderFactionVehicleService.GetPurchaseCost(definition).ToString("F1", CultureInfo.CurrentCulture);

        // Rolling out also draws fuel and ordnance off the depots nearby; say how much, because that
        // refusal is the one a new player meets first.
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        float fuelKg = advanced ? CommanderSupplyStoreService.GetSpawnFuelKg(definition) : 0f;
        float ammoKg = advanced ? CommanderSupplyStoreService.GetSpawnAmmoKg(definition) : 0f;
        string supply = fuelKg <= 0f && ammoKg <= 0f ? string.Empty
            : Mathf.Approximately(fuelKg, ammoKg) ? $"  |  {fuelKg:0} kg fuel + ammo"
            : $"  |  {fuelKg:0} kg fuel, {ammoKg:0} kg ammo";
        int tier = CommanderEconomyService.GetRequiredDepotLevel(definition);
        string locked = IsLockedAtSelectedDepot(definition) ? $"LOCKED (TIER {tier})  |  " : string.Empty;
        return $"{locked}{CommanderGameAccess.GetVehicleLabel(definition)}  |  Tier {tier}  |  {cost}{supply}";
    }

    /// <summary>
    /// A vehicle is sold only by a depot at or above its tier (<see cref="CommanderEconomyService.GetRequiredDepotLevel"/>).
    /// </summary>
    internal bool IsLockedAtSelectedDepot(VehicleDefinition definition)
    {
        // Drafted units were paid for up front; the depot tiers pace an economy a draft does not have.
        VehicleDepot? depot = SelectedDepot;
        return depot != null
            && !CommanderDraftService.IsDraftMission
            && CommanderEconomyService.GetDepotLevel(CommanderGameAccess.GetDepotOwner(depot))
                < CommanderEconomyService.GetRequiredDepotLevel(definition);
    }

    /// <summary>
    /// One click on a vehicle: it goes straight into the selected depot's queue, paid for now
    /// unless a reserve unit nobody has queued yet will take its place at the door.
    /// </summary>
    /// <remarks>
    /// A guest's money is a host SyncVar it cannot spend, so a guest's unit joins the queue once the
    /// host has charged for it (<see cref="CommanderQueuePayment"/>).
    /// </remarks>
    internal void QueueVehicle(VehicleDefinition definition)
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        if (IsLockedAtSelectedDepot(definition))
        {
            int tier = CommanderEconomyService.GetRequiredDepotLevel(definition);
            SetStatus($"Upgrade this depot to level {tier} to buy that.");
            CommanderNotice.Fail($"{CommanderGameAccess.GetVehicleLabel(definition)} NEEDS A LEVEL {tier} DEPOT");
            return;
        }

        if (CommanderUnitCap.IsFull(CommanderGameAccess.GetLocalHq()))
        {
            SetStatus($"Unit cap reached ({CommanderUnitCap.Cap} units).");
            return;
        }

        if (CommanderUnitCap.IsKindFull(CommanderGameAccess.GetLocalHq(), definition))
        {
            SetStatus($"Artillery cap reached ({CommanderUnitCap.ArtilleryCap}).");
            return;
        }

        if (CommanderUnitBanService.Holds(definition))
        {
            SetStatus(CommanderUnitBanService.IsVoting
                ? "Nothing spawns until the unit ban vote locks."
                : $"{CommanderGameAccess.GetVehicleLabel(definition)} is banned this match.");
            return;
        }

        // The nuclear gate, checked here rather than only at the door. The queue drains strictly in order,
        // so a launcher that cannot be armed would stall every unit behind it.
        CommanderFactionVehicleService.FindSpawnWarheadStore(queue.Depot, definition, out string nuclearReason);
        if (nuclearReason.Length > 0)
        {
            SetStatus($"{CommanderGameAccess.GetVehicleLabel(definition)} cannot roll out of this depot: {nuclearReason}.");
            CommanderNotice.Fail($"{CommanderGameAccess.GetVehicleLabel(definition)} CANNOT SPAWN: {nuclearReason}");
            return;
        }

        // A queue longer than the reserve stalls at the door: the head waits for a unit that is gone.
        if (CommanderDraftService.IsDraftMission
            && CountQueued(definition) >= CommanderFactionVehicleService.GetReserveCount(definition))
        {
            SetStatus($"Every drafted {CommanderGameAccess.GetVehicleLabel(definition)} is already queued.");
            return;
        }

        float paid = 0f;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq != null
            && !CommanderDraftService.IsDraftMission
            && CommanderFactionVehicleService.GetReserveCount(definition) <= CountQueued(definition, unpaidOnly: true))
        {
            if (!CommanderFactionVehicleService.CanPurchase(definition, queue.Depot.NetworkHQ))
            {
                SetStatus($"{CommanderGameAccess.GetVehicleLabel(definition)} is not available to this faction.");
                return;
            }

            if (CommanderNetwork.IsGuest)
            {
                BuyThroughHost(queue.Depot, definition);
                return;
            }

            paid = CommanderFactionVehicleService.GetPurchaseCost(definition);
            if (!CommanderGameAccess.TrySpend(hq, paid))
            {
                SetStatus($"Insufficient faction funds for {CommanderGameAccess.GetVehicleLabel(definition)}.");
                CommanderNotice.Fail($"NOT ENOUGH FUNDS FOR {CommanderGameAccess.GetVehicleLabel(definition)}");
                return;
            }
        }

        AddToQueue(queue, definition, paid);
    }

    /// <summary>
    /// This machine's non-empty depot queues for the team list, three entries each: depot id,
    /// seconds left on the unit being built (-1 when its clock has not started), and the waiting
    /// units as <c>key:count</c> runs joined with commas.
    /// </summary>
    internal void CollectOwnQueues(List<string> args, int maxQueues, int maxRuns)
    {
        int queues = 0;
        foreach (DepotSpawnQueue queue in depotQueues.Values)
        {
            List<VehicleDefinition> pending = queue.PendingDefinitions;
            if (pending.Count == 0 || queue.Depot == null)
            {
                continue;
            }

            if (++queues > maxQueues)
            {
                return;
            }

            System.Text.StringBuilder runs = new();
            int runCount = 0;
            int runStart = 0;
            for (int i = 1; i <= pending.Count && runCount < maxRuns; i++)
            {
                if (i < pending.Count && pending[i] == pending[runStart])
                {
                    continue;
                }

                runs.Append(runCount++ > 0 ? "," : string.Empty)
                    .Append(pending[runStart].jsonKey)
                    .Append(':')
                    .Append(CommanderNetwork.Num(i - runStart));
                runStart = i;
            }

            args.Add(CommanderNetwork.Id(queue.Depot));
            args.Add(CommanderNetwork.Num(queue.BuildDoneAt > 0f ? Mathf.Max(0f, queue.BuildDoneAt - Time.time) : -1f));
            args.Add(runs.ToString());
        }
    }

    /// <summary>Takes the queued unit at <paramref name="index"/> back out of the selected depot's
    /// queue and refunds what it was bought for.</summary>
    internal void CancelQueued(int index)
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null || index < 0 || index >= queue.PendingDefinitions.Count)
        {
            return;
        }

        VehicleDefinition definition = queue.PendingDefinitions[index];
        float refund = queue.RemoveAt(index);
        Refund(refund);
        SetStatus(refund > 0f
            ? $"Cancelled {CommanderGameAccess.GetVehicleLabel(definition)}. Refunded {CommanderEconomyService.FundsLabel(refund)}."
            : $"Cancelled {CommanderGameAccess.GetVehicleLabel(definition)}.");
    }

    /// <summary>
    /// A vehicle the base game just rolled out of <paramref name="depot"/> from the factory reserve:
    /// expected by that depot's queue as if the player had bought it there, so the next queue tick
    /// sends it to the staging block, the rally point or AUTO ATTACK.
    /// </summary>
    /// <remarks>
    /// Left alone it had no player command, so <c>GroundVehicle.CheckObstacles</c> re-aimed it every
    /// 4 s at the nearest tracked enemy, measured from the vehicle itself, at that contact's last
    /// known position. A column passing one contact found the next nearest behind it and turned
    /// round, front first, into the vehicles still coming; and a contact last seen in open country
    /// is a frozen point every factory unit on the map drove to and parked on, hours later.
    /// The new vehicle is still on the spawn point, so everything else is marked seen first and the
    /// match in <see cref="TrackQueuedSpawnedUnits"/> can only take it.
    /// </remarks>
    internal void AdoptReserveSpawn(VehicleDepot depot, VehicleDefinition definition)
    {
        Transform? door = CommanderGameAccess.GetDepotSpawnTransform(depot);
        if (door == null)
        {
            return;
        }

        DepotSpawnQueue queue = GetOrCreateQueue(depot);
        CommanderGameAccess.CollectFriendlySurfaceUnits(friendlyUnitsScratch);
        for (int i = 0; i < friendlyUnitsScratch.Count; i++)
        {
            Unit unit = friendlyUnitsScratch[i];
            if (CommanderGameAccess.HorizontalDistance(unit.transform.position, door.position) < DriveProbeStepMeters)
            {
                queue.SeenUnitIds.Remove(unit.persistentID.Id);
            }
            else
            {
                queue.SeenUnitIds.Add(unit.persistentID.Id);
            }
        }

        queue.ExpectedSpawnDefinitions.Enqueue(definition);
    }

    /// <summary>
    /// SPAWN RESERVE: puts every reserve vehicle not yet queued into <paramref name="depot"/>'s
    /// queue, so that depot's rally point, AUTO ATTACK and REINFORCE apply to the lot. Idempotent
    /// (the reserve and the queues shrink together as units leave the door). Returns how many it
    /// queued.
    /// </summary>
    /// <remarks>
    /// A queued unit takes a reserve unit at the door and only pays when the reserve is empty, so
    /// <c>CommanderFactionVehicleService.ShouldBlockAutomaticDeployment</c> keeps the base game from
    /// rolling out a type that is queued here: it would empty the reserve under the queue, and the
    /// queue would then buy the same unit again.
    /// </remarks>
    internal int QueueReserve(VehicleDepot depot)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return 0;
        }

        // Fighting vehicles out of the door first, trucks behind them.
        List<VehicleDefinition> reserve = new();
        foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
        {
            if (entry.Key != null && entry.Value.Count > 0)
            {
                reserve.Add(entry.Key);
            }
        }

        reserve.Sort(static (a, b) => GetRollOutOrder(a).CompareTo(GetRollOutOrder(b)));
        DepotSpawnQueue queue = GetOrCreateQueue(depot);
        int queued = 0;
        for (int i = 0; i < reserve.Count; i++)
        {
            VehicleDefinition definition = reserve[i];
            int missing = hq.GetUnitSupply(definition) - CountQueued(definition, unpaidOnly: true);
            for (int n = 0; n < missing; n++)
            {
                queue.Add(definition, 0f);
                queued++;
            }
        }

        return queued;
    }

    /// <summary>Reserve vehicles not yet in any queue: what SPAWN RESERVE would add.</summary>
    internal int CountUnqueuedReserve()
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return 0;
        }

        int count = 0;
        foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
        {
            if (entry.Key != null && entry.Value.Count > 0)
            {
                count += Math.Max(0, entry.Value.Count - CountQueued(entry.Key, unpaidOnly: true));
            }
        }

        return count;
    }

    /// <summary>Waiting in any of this machine's depot queues. <paramref name="unpaidOnly"/> counts
    /// only the ones that will take a reserve unit at the door, not those already bought.</summary>
    internal int CountQueued(VehicleDefinition definition, bool unpaidOnly = false)
    {
        int count = 0;
        foreach (DepotSpawnQueue queue in depotQueues.Values)
        {
            for (int i = 0; i < queue.PendingDefinitions.Count; i++)
            {
                count += ReferenceEquals(queue.PendingDefinitions[i], definition) && (!unpaidOnly || queue.PendingPaid[i] <= 0f) ? 1 : 0;
            }
        }

        return count;
    }

    internal bool IsAutoReserveDepotSelected()
    {
        return SelectedDepot != null && ReferenceEquals(SelectedDepot, autoReserveDepot);
    }

    internal void ToggleAutoReserveAtSelectedDepot()
    {
        if (SelectedDepot == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        if (IsAutoReserveDepotSelected())
        {
            autoReserveDepot = null;
            SetStatus("Reserve vehicles wait in reserve again.");
            return;
        }

        autoReserveDepot = SelectedDepot;
        int queued = QueueReserve(SelectedDepot);
        SetStatus($"New reserve vehicles now roll out of this depot automatically. Queued {queued} now.");
    }

    internal void SpawnReserveAtSelectedDepot()
    {
        if (SelectedDepot == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        int queued = QueueReserve(SelectedDepot);
        SetStatus(queued == 0
            ? "Nothing in reserve that is not already queued."
            : $"Queued {queued} reserve unit{(queued == 1 ? string.Empty : "s")} at this depot.");
    }

    /// <summary>
    /// Throws away everything this depot has queued and refunds what was bought. This is also the
    /// way out of a queue whose head is waiting on something that has not arrived yet.
    /// </summary>
    internal void ClearQueue()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        int cleared = queue.PendingDefinitions.Count;
        Refund(queue.Clear());
        SetStatus(cleared == 0
            ? "Spawn queue is already empty."
            : $"Cleared {cleared} unit{(cleared == 1 ? string.Empty : "s")} from the spawn queue.");
    }

    /// <summary>Arms the rally click: the next left click, in the 3D world or on a map that is
    /// already open, is where this depot's new units gather. Right-click cancels.</summary>
    internal void BeginRallyPointSelection()
    {
        if (SelectedDepot == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        AwaitingRallyPointSelection = true;
        rallySelectionQueue = GetSelectedQueue();
        rallyClickTracker.Reset();
        SetStatus("Click where new units should gather. Right-click cancels.");
    }

    internal void ClearRallyPoint()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            return;
        }

        queue.HasRallyPoint = false;
        queue.PendingRallyUnits.Clear();
        queue.NextRallySlot = 0;
        ApplyDefaultRallyPoint(queue);
        SetStatus("Cleared rally point. New units stage beside the depot.");
    }

    internal bool TrySetRallyPointFromWorld(Vector2 screenPosition)
    {
        if (!AwaitingRallyPointSelection || rallySelectionQueue == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition rallyPoint))
        {
            SetStatus("No valid rally position under the cursor.");
            return true;
        }

        CompleteRallyPointSelection(rallyPoint);
        return true;
    }

    /// <summary>Right click with a depot selected: the same as SET RALLY and a click there.</summary>
    internal void SetRallyPoint(GlobalPosition rallyPoint)
    {
        rallySelectionQueue = GetSelectedQueue();
        CompleteRallyPointSelection(rallyPoint);
    }

    /// <summary>Control group new arrivals from this depot join automatically; 0 is off.</summary>
    internal int GetReinforceGroup()
    {
        return GetSelectedQueue()?.ReinforceGroup ?? 0;
    }

    internal void ClearReinforceGroup()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue != null)
        {
            queue.ReinforceGroup = 0;
            SetStatus("New units will not join a control group.");
        }
    }

    internal void CycleReinforceGroup()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        queue.ReinforceGroup = queue.ReinforceGroup >= CommanderGroupService.GroupCount
            ? 0
            : queue.ReinforceGroup + 1;
        SetStatus(queue.ReinforceGroup == 0
            ? "New units will not join a control group."
            : $"New units will join control group {queue.ReinforceGroup}.");
    }

    /// <summary>Whether the selected depot sends its new units straight at the nearest enemy base.</summary>
    internal bool IsAutoAttackOn()
    {
        return GetSelectedQueue()?.AutoAttack == true;
    }

    internal void ToggleAutoAttack()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        queue.AutoAttack = !queue.AutoAttack;
        SetStatus(queue.AutoAttack
            ? "New units from this depot drive at the nearest enemy base."
            : "New units from this depot gather at its rally point again.");
    }

    internal string GetRallyLabel()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null || !queue.HasRallyPoint)
        {
            return "Not set";
        }

        if (queue.IsDefaultRallyPoint)
        {
            return "Staging beside depot";
        }

        float km = CommanderGameAccess.HorizontalDistance(queue.Depot.transform.position, queue.RallyPoint.ToLocalPosition()) / 1000f;
        return $"Rally point {km:0.0} km from the depot";
    }

    internal List<string> GetPendingSummaryLines()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            return DepotSpawnQueue.EmptySummaryLines;
        }

        if (!queue.PendingSummaryDirty)
        {
            return queue.PendingSummaryLines;
        }

        queue.PendingSummaryCounts.Clear();
        queue.PendingSummaryLines.Clear();
        foreach (VehicleDefinition definition in queue.PendingDefinitions)
        {
            string label = CommanderGameAccess.GetVehicleLabel(definition);
            queue.PendingSummaryCounts.TryGetValue(label, out int count);
            queue.PendingSummaryCounts[label] = count + 1;
        }

        foreach (KeyValuePair<string, int> entry in queue.PendingSummaryCounts)
        {
            queue.PendingSummaryLines.Add($"{entry.Key} x{entry.Value}");
            if (queue.PendingSummaryLines.Count >= 6)
            {
                break;
            }
        }

        queue.PendingSummaryDirty = false;
        return queue.PendingSummaryLines;
    }

    /// <summary>
    /// A depot that could not pay for the unit it was about to roll out. Only ever shown for the
    /// local faction — the enemy's convoys run through the same door and the player is not their
    /// quartermaster.
    /// </summary>
    internal void ReportSpawnSupplyShortfall(FactionHQ? hq, VehicleDefinition definition, string reason)
    {
        if (!ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()))
        {
            return;
        }

        SetStatus($"{CommanderGameAccess.GetVehicleLabel(definition)} is waiting on supply: {reason}.");
        CommanderNotice.Fail($"{CommanderGameAccess.GetVehicleLabel(definition)} CANNOT SPAWN: {reason}");
    }

    /// <summary>
    /// Parks a depot's output in a staging block beside the depot until the player orders it
    /// somewhere.
    /// </summary>
    /// <remarks>
    /// Left alone, a bought vehicle drives itself at the enemy. <c>GroundVehicle.CheckObstacles</c>
    /// re-targets any vehicle that is not holding position and has no <i>player</i> command onto the
    /// nearest mission objective or tracked enemy, and <c>VehicleDepot.TrySpawnVehicle</c>'s exit
    /// nudge is issued with <c>playerCommand: false</c> — so the unit is out of the gate and off to
    /// the front before the player has seen it. A default rally point is a real player command, which
    /// is what pins it: no auto-walk, no held-position anchoring, and the first move order the player
    /// gives simply overrides it. Setting a rally point of your own replaces this; clearing one puts
    /// it back rather than turning staging off.
    /// </remarks>
    private static int GetRollOutOrder(VehicleDefinition definition)
    {
        return definition.vehicleType switch
        {
            VehicleType.MBT or VehicleType.AFV => 0,
            VehicleType.LCV or VehicleType.UGV => 1,
            VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM => 2,
            VehicleType.ART => 3,
            _ => 4,
        };
    }

    private static void ApplyDefaultRallyPoint(DepotSpawnQueue queue)
    {
        if (!TryFindClearExit(queue.Depot, DefaultStagingDistanceMeters, out GlobalPosition door, out Vector3 direction, out float clear))
        {
            return;
        }

        queue.RallyPoint = CommanderGameAccess.SnapToTerrain(door + (direction * Mathf.Min(clear, DefaultStagingDistanceMeters)));
        queue.HasRallyPoint = true;
        queue.IsDefaultRallyPoint = true;
        queue.NextRallySlot = 0;
    }

    /// <summary>
    /// How far a vehicle can drive from <paramref name="from"/> along <paramref name="direction"/>
    /// before a building, the sea or a cliff is in the way, up to <paramref name="maxMeters"/>.
    /// <paramref name="ignore"/> (the depot, when the drive starts at its door) does not count.
    /// </summary>
    private static float GetClearDriveMeters(GlobalPosition from, Vector3 direction, float maxMeters, Unit? ignore)
    {
        float lastY = CommanderGameAccess.SnapToTerrain(from).y;
        for (float meters = DriveProbeStepMeters; meters <= maxMeters; meters += DriveProbeStepMeters)
        {
            GlobalPosition probe = CommanderGameAccess.SnapToTerrain(from + (direction * meters));
            if (CommanderGameAccess.IsBelowSeaLevel(probe)
                || Mathf.Abs(probe.y - lastY) > MaxDriveStepRiseMeters
                || CommanderBuildPreview.IsBuiltOver(probe, DriveLaneRadiusMeters, ignore))
            {
                return meters - DriveProbeStepMeters;
            }

            lastY = probe.y;
        }

        return maxMeters;
    }

    /// <summary>
    /// A formation slot a new unit must not be sent to: inside a building, or, on the automatic
    /// staging block, behind one on the straight drive there. A rally point the player placed is
    /// theirs to route to, so only the slot itself is checked for it.
    /// </summary>
    private static bool IsBlockedRallySlot(DepotSpawnQueue queue, Unit unit, GlobalPosition slot)
    {
        if (CommanderBuildPreview.IsBuiltOver(slot, RallyClearanceMeters))
        {
            return true;
        }

        if (!queue.IsDefaultRallyPoint)
        {
            return false;
        }

        // Behind the door line is back past the depot: never a staging slot.
        Transform? door = CommanderGameAccess.GetDepotSpawnTransform(queue.Depot);
        if (door != null)
        {
            Vector3 fromDoor = slot.ToLocalPosition() - door.position;
            Vector3 doorForward = door.forward;
            fromDoor.y = 0f;
            doorForward.y = 0f;
            if (Vector3.Dot(fromDoor, doorForward.normalized) < DriveProbeStepMeters)
            {
                return true;
            }
        }

        // The unit is already out of the door, so the depot is an obstacle like any other now.
        Vector3 toSlot = slot.ToLocalPosition() - unit.transform.position;
        toSlot.y = 0f;
        float meters = toSlot.magnitude;
        return meters > 1f && GetClearDriveMeters(unit.GlobalPosition(), toSlot / meters, meters, null) < meters;
    }

    private static bool MatchesDefinition(Unit unit, VehicleDefinition expectedDefinition)
    {
        if (unit.definition == expectedDefinition)
        {
            return true;
        }

        return unit.definition != null
            && string.Equals(unit.definition.code, expectedDefinition.code, StringComparison.OrdinalIgnoreCase)
            && string.Equals(unit.definition.unitName, expectedDefinition.unitName, StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdatePendingRallyOrders(DepotSpawnQueue queue)
    {
        if (!queue.HasRallyPoint && !queue.AutoAttack)
        {
            queue.PendingRallyUnits.Clear();
            return;
        }

        for (int i = queue.PendingRallyUnits.Count - 1; i >= 0; i--)
        {
            PendingRallyUnit pendingUnit = queue.PendingRallyUnits[i];
            Unit? unit = pendingUnit.Unit;

            // Ordered before it cleared the door: the player's order is the "command changed" the
            // test below would otherwise answer by sending the unit back to its rally slot.
            if (unit == null || unit.disabled || CommanderMoveService.Instance?.HasOrder(unit) == true)
            {
                queue.PendingRallyUnits.RemoveAt(i);
                continue;
            }

            bool hasCurrentCommand = CommanderGameAccess.TryGetCurrentCommandPosition(unit, out GlobalPosition currentCommand);
            if (!pendingUnit.ExitCommandLocked && hasCurrentCommand)
            {
                pendingUnit.ExitCommand = currentCommand;
                pendingUnit.ExitCommandLocked = true;
            }

            // A guest can never read a unit's command: UnitCommand.currentCommand is written only
            // inside ServerSetDestination, so on a client it stays default and the exit test below
            // could never pass. Every unit a guest bought was therefore left with no rally order at
            // all, and CheckObstacles drove it at the enemy - the "rally does nothing, they attack
            // like factory units" report. Clearing the depot's own 20 m exit push answers the same
            // question without asking the server, and is a cheaper answer on the host too.
            bool reachedExit = HasClearedDepot(queue.Depot, unit)
                || (pendingUnit.ExitCommandLocked
                    && CommanderGameAccess.HorizontalDistance(
                        unit.transform.position,
                        pendingUnit.ExitCommand.ToLocalPosition()) <= RallyExitToleranceMeters);
            bool baseCommandChanged = hasCurrentCommand
                && pendingUnit.ExitCommandLocked
                && !CommanderGameAccess.ApproximatelyEqual(currentCommand, pendingUnit.ExitCommand, RallyExitToleranceMeters);
            if (!reachedExit && !baseCommandChanged)
            {
                continue;
            }

            // AUTO ATTACK: a real order at the enemy's nearest base instead of the rally slot, so the
            // unit drives there, fights what it meets and stays in the capture ring when it arrives.
            // It goes through the move service like a player's capture click: a bare destination sent
            // every unit to the one hold point, where they queued up behind each other, largely
            // outside a small ring, and nothing re-sent it when something else steered them.
            if (queue.AutoAttack && TryGetAutoAttackPoint(queue.Depot, out GlobalPosition attackPoint))
            {
                if (CommanderMoveService.Instance?.IssueCaptureOrder(
                        unit, attackPoint, pendingUnit.RallySlot % AutoAttackSlots) == true)
                {
                    ReleaseFromRearmAi(unit);
                    queue.PendingRallyUnits.RemoveAt(i);
                }

                continue;
            }

            if (!queue.HasRallyPoint)
            {
                queue.PendingRallyUnits.RemoveAt(i);
                continue;
            }

            GlobalPosition rallyDestination = CommanderDestinationFormation.ApplyOffset(
                queue.RallyPoint,
                pendingUnit.RallySlot,
                RallyFormationSpacingMeters);

            // A slot inside a building, or behind one, is a unit driven into its wall, so it takes
            // the next free slot instead. Slot numbers only ever count up, so this never doubles one up.
            int tries = 0;
            for (; tries < MaxRallySlotSkips
                && IsBlockedRallySlot(queue, unit, rallyDestination); tries++)
            {
                pendingUnit.RallySlot = queue.NextRallySlot++;
                rallyDestination = CommanderDestinationFormation.ApplyOffset(
                    queue.RallyPoint,
                    pendingUnit.RallySlot,
                    RallyFormationSpacingMeters);
            }

            if (tries == MaxRallySlotSkips && queue.IsDefaultRallyPoint)
            {
                // Nothing around it is clear: the staging point itself was checked when it was chosen.
                rallyDestination = queue.RallyPoint;
            }

            if (!CommanderGameAccess.TrySetDestination(unit, rallyDestination))
            {
                continue;
            }

            queue.PendingRallyUnits.RemoveAt(i);
        }
    }

    /// <summary>
    /// Whether a new vehicle is out of its depot's doorway. The depot pushes every spawn 20 m along
    /// the door before letting go, so past that it is on open ground and safe to re-route.
    /// </summary>
    private static bool HasClearedDepot(VehicleDepot depot, Unit unit)
    {
        return CommanderGameAccess.HorizontalDistance(
            unit.transform.position,
            CommanderGameAccess.GetDepotSpawnPosition(depot)) > DepotClearedMeters;
    }

    /// <summary>
    /// A munitions truck carries the base game's <c>RearmVehicleAI</c>, whose opening
    /// <c>LeaveDepot</c> loop calls <c>StopImmediately</c> the first 5 s check the truck is under
    /// 1 m/s and then offers it to rearm missions back at base. So an AUTO ATTACK truck stopped on the
    /// road at its first pause and never followed the convoy. Moving the AI to a state with no loop
    /// (<c>ReloadingFromRearmer</c>, only a transient step in the base game) ends <c>LeaveDepot</c>
    /// without calling <c>Wait</c> and leaves <c>AvailableForMission</c> false. The truck still rearms
    /// units beside it; RESUME AI hands it back to the base game's logistics.
    /// </summary>
    private static void ReleaseFromRearmAi(Unit unit)
    {
        if (RearmAiStateField == null || !unit.TryGetComponent(out RearmVehicleAI rearmAi))
        {
            return;
        }

        RearmAiStateField.SetValue(rearmAi, Enum.ToObject(RearmAiStateField.FieldType, RearmAiQuietState));
    }

    /// <summary>The hold point of the enemy-held airbase nearest the depot.</summary>
    private static bool TryGetAutoAttackPoint(VehicleDepot depot, out GlobalPosition point)
    {
        point = default;
        FactionHQ? own = depot.NetworkHQ;
        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null
                || airbase.disabled
                || airbase.CurrentHQ == null
                || ReferenceEquals(airbase.CurrentHQ, own)
                || airbase.SavedAirbase?.Capturable != true
                || !CommanderGameAccess.IsLandBase(airbase))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(depot.transform.position, airbase.center.position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        if (best == null)
        {
            return false;
        }

        point = CommanderCaptureService.GetHoldPoint(best);
        return true;
    }

    private static float BuildSecondsFor(VehicleDefinition definition)
    {
        return CommanderFactionVehicleService.GetReserveCount(definition) > 0 ? 0f : CommanderBuildTimes.For(definition);
    }

    private static void Refund(float amount)
    {
        if (amount > 0f && !CommanderQueuePayment.Return(amount))
        {
            CommanderGameAccess.GetLocalHq()?.AddFunds(amount);
        }
    }

    private static void MarkSpawned(DepotSpawnQueue queue)
    {
        VehicleDefinition definition = queue.PendingDefinitions[0];
        queue.RemoveAt(0);
        queue.ExpectedSpawnDefinitions.Enqueue(definition);
    }

    private void AddToQueue(DepotSpawnQueue queue, VehicleDefinition definition, float paid)
    {
        if (queue.PendingDefinitions.Count == 0
            && queue.ExpectedSpawnDefinitions.Count == 0
            && queue.PendingRallyUnits.Count == 0)
        {
            queue.NextRallySlot = 0;
        }

        queue.Add(definition, paid);
        SetStatus($"Queued {CommanderGameAccess.GetVehicleLabel(definition)}.");
    }

    /// <summary>A guest's purchase: into the queue once the host has taken the money.</summary>
    private void BuyThroughHost(VehicleDepot depot, VehicleDefinition definition)
    {
        string label = CommanderGameAccess.GetVehicleLabel(definition);
        SetStatus($"Buying {label}...");
        CommanderQueuePayment.Buy(definition, paid =>
        {
            if (paid <= 0f)
            {
                SetStatus($"Insufficient faction funds for {label}.");
            }
            else if (depot == null)
            {
                Refund(paid);
            }
            else
            {
                AddToQueue(GetOrCreateQueue(depot), definition, paid);
            }
        });
    }

    private void HandleRallyPointMapSelection()
    {
        if (!AwaitingRallyPointSelection || !DynamicMap.mapMaximized)
        {
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            AwaitingRallyPointSelection = false;
            SetStatus("Could not open the map.");
            return;
        }

        DepotSpawnQueue? queue = rallySelectionQueue;
        if (queue == null)
        {
            AwaitingRallyPointSelection = false;
            rallySelectionQueue = null;
            SetStatus("No depot selected.");
            return;
        }

        if (!rallyClickTracker.Tick(dynamicMap, out GlobalPosition rallyPoint))
        {
            return;
        }

        CompleteRallyPointSelection(rallyPoint);
    }

    private void CompleteRallyPointSelection(GlobalPosition rallyPoint)
    {
        DepotSpawnQueue? queue = rallySelectionQueue;
        if (queue == null)
        {
            return;
        }

        queue.HasRallyPoint = true;
        queue.IsDefaultRallyPoint = false;
        queue.RallyPoint = rallyPoint;
        queue.NextRallySlot = 0;
        for (int i = 0; i < queue.PendingRallyUnits.Count; i++)
        {
            queue.PendingRallyUnits[i].RallySlot = queue.NextRallySlot++;
        }

        AwaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        rallyClickTracker.Reset();
        SetStatus("Rally point set.");
    }

    private void RefreshVehicleDefinitions()
    {
        vehicleDefinitions.Clear();
        CommanderGameAccess.TryGetLocalVehicleDefinitions(vehicleDefinitions);
        vehicleDefinitions.Sort(static (left, right) =>
        {
            int categoryCompare = string.Compare(
                CommanderGameAccess.GetVehicleCategoryLabel(left),
                CommanderGameAccess.GetVehicleCategoryLabel(right),
                StringComparison.OrdinalIgnoreCase);
            if (categoryCompare != 0)
            {
                return categoryCompare;
            }

            return string.Compare(
                CommanderGameAccess.GetVehicleLabel(left),
                CommanderGameAccess.GetVehicleLabel(right),
                StringComparison.OrdinalIgnoreCase);
        });

        RebuildVehicleCategoryCache();
    }

    private void RefreshDepots()
    {
        depots.Clear();

        VehicleDepot[] sceneDepots = UnityEngine.Object.FindObjectsOfType<VehicleDepot>();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        for (int i = 0; i < sceneDepots.Length; i++)
        {
            VehicleDepot depot = sceneDepots[i];
            Unit? owner = CommanderGameAccess.GetDepotOwner(depot);
            if (owner == null || !CommanderGameAccess.IsFriendlyUnit(owner, localHq))
            {
                continue;
            }

            depots.Add(new VehicleDepotEntry(depot, owner, CommanderGameAccess.GetUnitLabel(owner)));
            GetOrCreateQueue(depot);
        }
    }

    private void RebuildVehicleCategoryCache()
    {
        definitionsByCategory.Clear();
        List<string> categoryNames = new();

        for (int i = 0; i < vehicleDefinitions.Count; i++)
        {
            VehicleDefinition definition = vehicleDefinitions[i];
            string category = CommanderGameAccess.GetVehicleCategoryLabel(definition);
            if (!definitionsByCategory.TryGetValue(category, out List<VehicleDefinition> definitions))
            {
                definitions = new List<VehicleDefinition>();
                definitionsByCategory[category] = definitions;
                categoryNames.Add(category);
            }

            definitions.Add(definition);
        }

        categoryNames.Add("All");
        categories = categoryNames.ToArray();
    }

    private void RefreshProductionVehicleDefinitions()
    {
        RefreshProductionBindings();
        if (!productionCatalogDirty)
        {
            return;
        }

        productionCatalogDirty = false;
        productionVehicleDefinitions.Clear();
        foreach (Factory factory in productionFactories.Values)
        {
            if (factory == null
                || factory.attachedUnit == null
                || factory.attachedUnit.disabled
                || factory.attachedUnit.NetworkHQ != boundProductionHq
                || factory.ProductionUnit is not VehicleDefinition definition
                || !CommanderGameAccess.IsSpawnableVehicleDefinition(definition)
                || productionVehicleDefinitions.Contains(definition))
            {
                continue;
            }

            productionVehicleDefinitions.Add(definition);
        }

        productionVehicleDefinitions.Sort(static (left, right) =>
        {
            int categoryCompare = string.Compare(
                CommanderGameAccess.GetVehicleCategoryLabel(left),
                CommanderGameAccess.GetVehicleCategoryLabel(right),
                StringComparison.OrdinalIgnoreCase);
            return categoryCompare != 0
                ? categoryCompare
                : string.Compare(CommanderGameAccess.GetVehicleLabel(left), CommanderGameAccess.GetVehicleLabel(right), StringComparison.OrdinalIgnoreCase);
        });

        productionCategories.Clear();
        for (int i = 0; i < productionVehicleDefinitions.Count; i++)
        {
            string category = CommanderGameAccess.GetVehicleCategoryLabel(productionVehicleDefinitions[i]);
            if (!productionCategories.Contains(category))
            {
                productionCategories.Add(category);
            }
        }
    }

    private void RefreshProductionBindings()
    {
        FactionHQ? hq = CommanderGameAccess.GetSupplyHq();
        if (ReferenceEquals(hq, boundProductionHq))
        {
            return;
        }

        UnbindProductionHq();
        boundProductionHq = hq;
        if (boundProductionHq?.factionUnits == null)
        {
            return;
        }

        boundProductionHq.onRegisterUnit += OnProductionUnitRegistered;
        boundProductionHq.onRemoveUnit += OnProductionUnitRemoved;
        foreach (PersistentID unitId in boundProductionHq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit))
            {
                OnProductionUnitRegistered(unit);
            }
        }
    }

    private void UnbindProductionHq()
    {
        if (boundProductionHq != null)
        {
            boundProductionHq.onRegisterUnit -= OnProductionUnitRegistered;
            boundProductionHq.onRemoveUnit -= OnProductionUnitRemoved;
        }

        boundProductionHq = null;
        productionFactories.Clear();
        productionCatalogDirty = true;
    }

    private void OnProductionUnitRegistered(Unit unit)
    {
        if (unit == null || unit.disabled || unit.NetworkHQ != boundProductionHq || !unit.TryGetComponent(out Factory factory))
        {
            return;
        }

        productionFactories[unit] = factory;
        productionCatalogDirty = true;
    }

    private void OnProductionUnitRemoved(Unit unit)
    {
        if (productionFactories.Remove(unit))
        {
            productionCatalogDirty = true;
        }
    }

    private void OnFactoryChanged(Factory factory)
    {
        Unit? unit = factory.attachedUnit;
        if (unit == null || unit.NetworkHQ != boundProductionHq)
        {
            return;
        }

        productionFactories[unit] = factory;
        productionCatalogDirty = true;
    }

    private void RefreshFactionVehicleDefinitions()
    {
        factionVehicleDefinitions.Clear();
        FactionHQ? hq = SelectedDepot == null ? CommanderGameAccess.GetLocalHq() : SelectedDepot.NetworkHQ;
        for (int i = 0; i < vehicleDefinitions.Count; i++)
        {
            VehicleDefinition definition = vehicleDefinitions[i];
            if (CommanderFactionVehicleService.CanPurchase(definition, hq)
                || (hq != null && hq.GetUnitSupply(definition) > 0))
            {
                factionVehicleDefinitions.Add(definition);
            }
        }
    }

    private void SyncSelectedDepotFromSelection()
    {
        Unit? focusedUnit = selectionService.FocusedSelection;
        VehicleDepot? resolvedDepot = null;

        // The dock's stand-in prefab carries a VehicleDepot of its own, so without this a selected
        // dock opened DEPOT CONTROL. Its menu is NAVAL REINFORCEMENTS (CommanderNavalPurchaseUi.TrackDock).
        if (focusedUnit != null && CommanderEconomyService.Instance?.IsBuiltNavalDock(focusedUnit) != true)
        {
            for (int i = 0; i < depots.Count; i++)
            {
                VehicleDepotEntry entry = depots[i];
                if (ReferenceEquals(entry.Owner, focusedUnit))
                {
                    resolvedDepot = entry.Depot;
                    break;
                }
            }

            if (resolvedDepot == null)
            {
                resolvedDepot = CommanderGameAccess.GetFriendlyDepotFromUnit(focusedUnit, CommanderGameAccess.GetLocalHq());
            }
        }

        if (!ReferenceEquals(SelectedDepot, resolvedDepot))
        {
            SelectedDepot = resolvedDepot;
            if (SelectedDepot != null)
            {
                GetOrCreateQueue(SelectedDepot);
            }
        }
    }

    /// <summary>AUTO: queues new reserve at the chosen depot as it arrives, until the depot is lost.</summary>
    private void TickAutoReserve()
    {
        if (autoReserveDepot == null)
        {
            return;
        }

        if (autoReserveDepot.disabled || !CommanderGameAccess.IsFriendlyUnit(autoReserveDepot, CommanderGameAccess.GetLocalHq()))
        {
            autoReserveDepot = null;
            return;
        }

        if (CountUnqueuedReserve() > 0)
        {
            QueueReserve(autoReserveDepot);
        }
    }

    private void UpdateSpawnQueues()
    {
        queuesToRemove.Clear();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (KeyValuePair<VehicleDepot, DepotSpawnQueue> entry in depotQueues)
        {
            DepotSpawnQueue queue = entry.Value;

            // A captured depot is lost as surely as a destroyed one: nothing can roll out of it again.
            if (queue.Depot == null
                || queue.Depot.disabled
                || (localHq != null && !CommanderGameAccess.IsFriendlyUnit(queue.Depot, localHq)))
            {
                Refund(queue.Clear());
                queuesToRemove.Add(entry.Key);
                continue;
            }

            TrackQueuedSpawnedUnits(queue);
            UpdatePendingRallyOrders(queue);

            if (queue.PendingDefinitions.Count == 0 || queue.ExpectedSpawnDefinitions.Count > 0)
            {
                continue;
            }

            VehicleDefinition definition = queue.PendingDefinitions[0];
            bool prepaid = queue.PendingPaid[0] > 0f;
            if (!prepaid && !CommanderFactionVehicleService.CanAcquire(definition, out string reason))
            {
                SetStatus(reason);
                CommanderNotice.Fail($"{CommanderGameAccess.GetVehicleLabel(definition)} CANNOT SPAWN: {reason}");
                continue;
            }

            // The build clock starts once the head is paid for. A unit bought when it was queued is
            // charged nothing more here; one queued against the reserve takes its reserve unit (or,
            // if the base game deployed that meanwhile, pays) when it rolls out.
            // A depot builds one unit at a time for the whole team, so a guest's clock comes from the host.
            if (queue.BuildDoneAt <= 0f)
            {
                if (CommanderNetwork.IsGuest)
                {
                    if (Time.unscaledTime >= queue.HostAnswerDueAt)
                    {
                        queue.HostAnswerDueAt = Time.unscaledTime + HostAnswerTimeoutSeconds;
                        CommanderNetwork.Forward("depot.slot", CommanderNetwork.Id(queue.Depot), definition.jsonKey);
                    }

                    continue;
                }

                queue.BuildSeconds = BuildSecondsFor(definition);
                queue.BuildDoneAt = Time.time + BookBuild(queue.Depot, queue.BuildSeconds);
            }

            if (Time.time < queue.BuildDoneAt)
            {
                continue;
            }

            SnapshotKnownFriendlyUnits(queue);

            // A guest's depot is the host's to spawn from. One request in flight per depot; the
            // host's depot.ack moves the queue on, or the timeout asks again.
            if (CommanderNetwork.IsGuest)
            {
                if (Time.unscaledTime >= queue.HostAnswerDueAt)
                {
                    queue.HostAnswerDueAt = Time.unscaledTime + HostAnswerTimeoutSeconds;
                    CommanderNetwork.Forward(
                        "depot",
                        CommanderNetwork.Id(queue.Depot),
                        definition.jsonKey,
                        CommanderQueuePayment.Text(queue.PendingPaid[0]));
                }

                continue;
            }

            if (!queue.Depot.TrySpawnVehicle(definition))
            {
                continue;
            }

            if (!prepaid)
            {
                CommanderFactionVehicleService.CommitAcquisition(definition);
            }

            MarkSpawned(queue);
        }

        for (int i = 0; i < queuesToRemove.Count; i++)
        {
            depotQueues.Remove(queuesToRemove[i]);
        }
    }

    private void TrackQueuedSpawnedUnits(DepotSpawnQueue queue)
    {
        if (queue.ExpectedSpawnDefinitions.Count == 0)
        {
            return;
        }

        Transform? spawnTransform = CommanderGameAccess.GetDepotSpawnTransform(queue.Depot);
        if (spawnTransform == null)
        {
            return;
        }

        CommanderGameAccess.CollectFriendlySurfaceUnits(friendlyUnitsScratch);
        VehicleDefinition expectedDefinition = queue.ExpectedSpawnDefinitions.Peek();
        for (int i = 0; i < friendlyUnitsScratch.Count; i++)
        {
            Unit unit = friendlyUnitsScratch[i];
            uint persistentId = unit.persistentID.Id;
            if (queue.SeenUnitIds.Contains(persistentId))
            {
                continue;
            }

            queue.SeenUnitIds.Add(persistentId);
            if (CommanderGameAccess.HorizontalDistance(unit.transform.position, spawnTransform.position) > DepotSpawnDetectionRadius
                || !MatchesDefinition(unit, expectedDefinition))
            {
                continue;
            }

            queue.ExpectedSpawnDefinitions.Dequeue();
            if (queue.OffRoad && unit is GroundVehicle vehicle)
            {
                CommanderDirectPathService.ForceEnabled(vehicle);
            }

            if (queue.RadarOff)
            {
                CommanderRadarService.Instance?.SetUnitRadar(unit, false);
            }

            if (queue.ReinforceGroup > 0)
            {
                CommanderGroupService.Instance?.AddUnitToGroup(unit, queue.ReinforceGroup);
            }

            if (queue.HasRallyPoint || queue.AutoAttack)
            {
                queue.PendingRallyUnits.Add(new PendingRallyUnit(unit, queue.NextRallySlot++));
            }

            break;
        }
    }

    private DepotSpawnQueue GetOrCreateQueue(VehicleDepot depot)
    {
        if (depotQueues.TryGetValue(depot, out DepotSpawnQueue queue))
        {
            return queue;
        }

        queue = new DepotSpawnQueue(depot);
        ApplyDefaultRallyPoint(queue);
        depotQueues[depot] = queue;
        SnapshotKnownFriendlyUnits(queue);
        return queue;
    }

    private void SnapshotKnownFriendlyUnits(DepotSpawnQueue queue)
    {
        CommanderGameAccess.CollectFriendlySurfaceUnits(friendlyUnitsScratch);
        for (int i = 0; i < friendlyUnitsScratch.Count; i++)
        {
            queue.SeenUnitIds.Add(friendlyUnitsScratch[i].persistentID.Id);
        }
    }

    private VehicleDepotEntry? GetSelectedDepotEntry()
    {
        if (SelectedDepot == null)
        {
            return null;
        }

        for (int i = 0; i < depots.Count; i++)
        {
            if (ReferenceEquals(depots[i].Depot, SelectedDepot))
            {
                return depots[i];
            }
        }

        return null;
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    /// <summary>
    /// Puts a build behind whatever the depot already has booked and returns the seconds until it
    /// is done. Queues stay on the machine that clicked, because rally points and refunds belong to
    /// the buyer; only the build time is shared. A cancelled build keeps its booking, so a teammate
    /// behind it waits out the time it had left.
    /// </summary>
    private float BookBuild(VehicleDepot depot, float seconds)
    {
        depotFreeAt.TryGetValue(depot, out float freeAt);
        float doneAt = Mathf.Max(Time.time, freeAt) + seconds;
        depotFreeAt[depot] = doneAt;
        return doneAt - Time.time;
    }

    private void HostBookBuild(FactionHQ hq, string[] args)
    {
        // Always answered: with no answer a refused queue asked again for ever. An empty booking
        // sends it on to the door, where the depot request refuses it and says why.
        float left = 0f;
        float seconds = 0f;
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is VehicleDepot depot
            && !depot.disabled
            && ReferenceEquals(depot.NetworkHQ, hq)
            && CommanderNetwork.Sender != null
            && CommanderNetwork.Definition<VehicleDefinition>(CommanderNetwork.Arg(args, 2)) is VehicleDefinition definition)
        {
            // A guest asks again after 5 s, and a slow answer is not a second unit.
            (VehicleDepot, INetworkPlayer) key = (depot, CommanderNetwork.Sender);
            if (guestBookings.TryGetValue(key, out (string Key, float DoneAt, float Seconds) booked)
                && booked.Key == definition.jsonKey
                && booked.DoneAt > Time.time)
            {
                left = booked.DoneAt - Time.time;
                seconds = booked.Seconds;
            }
            else
            {
                seconds = BuildSecondsFor(definition);
                left = BookBuild(depot, seconds);
                guestBookings[key] = (definition.jsonKey, Time.time + left, seconds);
            }
        }

        CommanderNetwork.Reply(
            "depot.slot.ack",
            CommanderNetwork.Arg(args, 1),
            CommanderNetwork.Arg(args, 2),
            CommanderNetwork.Num(left),
            CommanderNetwork.Num(seconds));
    }

    private void OnBuildBooked(string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is VehicleDepot depot
            && depotQueues.TryGetValue(depot, out DepotSpawnQueue queue)
            && queue.BuildDoneAt <= 0f
            && queue.PendingDefinitions.Count > 0
            && queue.PendingDefinitions[0].jsonKey == CommanderNetwork.Arg(args, 2))
        {
            queue.BuildSeconds = CommanderNetwork.Float(args, 4);
            queue.BuildDoneAt = Time.time + CommanderNetwork.Float(args, 3);
            queue.HostAnswerDueAt = 0f;
        }
    }

    /// <summary>A guest's depot queue asking for its next vehicle. Same checks, same door.</summary>
    private void HostSpawn(FactionHQ hq, string[] args)
    {
        bool spawned = false;
        string hostStatus = statusText;
        float hostUntil = statusUntil;
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is VehicleDepot depot
            && !depot.disabled
            && ReferenceEquals(depot.NetworkHQ, hq)
            && CommanderNetwork.Definition<VehicleDefinition>(CommanderNetwork.Arg(args, 2)) is VehicleDefinition definition)
        {
            // Bought when it was queued: the deposit pays, and the balance has no say in it now.
            float paid = CommanderNetwork.Float(args, 3);
            bool prepaid = CommanderQueuePayment.Holds(paid);
            if (prepaid && !CommanderFactionVehicleService.CanPurchase(definition, hq))
            {
                CommanderNotice.Fail($"{CommanderGameAccess.GetVehicleLabel(definition)} is not available to this faction.");
            }
            else if (!prepaid && !CommanderFactionVehicleService.CanAcquire(definition, out string reason))
            {
                CommanderNotice.Fail($"{CommanderGameAccess.GetVehicleLabel(definition)} CANNOT SPAWN: {reason}");
            }
            else if (depot.TrySpawnVehicle(definition))
            {
                if (prepaid)
                {
                    CommanderQueuePayment.Draw(paid);
                }
                else
                {
                    CommanderFactionVehicleService.CommitAcquisition(definition);
                }

                spawned = true;
            }
        }

        statusText = hostStatus;
        statusUntil = hostUntil;
        CommanderNetwork.Reply("depot.ack", CommanderNetwork.Arg(args, 1), CommanderNetwork.Arg(args, 2), CommanderNetwork.Flag(spawned));
    }

    private void OnDepotAck(string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit)
            || unit is not VehicleDepot depot
            || !depotQueues.TryGetValue(depot, out DepotSpawnQueue queue))
        {
            return;
        }

        bool spawned = CommanderNetwork.Bool(args, 3);
        queue.HostAnswerDueAt = spawned ? 0f : Time.unscaledTime + HostRetrySeconds;
        if (spawned
            && queue.PendingDefinitions.Count > 0
            && queue.PendingDefinitions[0].jsonKey == CommanderNetwork.Arg(args, 2))
        {
            MarkSpawned(queue);
        }
    }

    internal sealed class DepotSpawnQueue
    {
        internal static readonly List<string> EmptySummaryLines = new();

        private int nextRallySlot;

        internal DepotSpawnQueue(VehicleDepot depot)
        {
            Depot = depot;
        }

        internal VehicleDepot Depot { get; }

        /// <summary>In roll-out order. Change only through <see cref="Add"/>, <see cref="RemoveAt"/>
        /// and <see cref="Clear"/>, which keep <see cref="PendingPaid"/> in step.</summary>
        internal List<VehicleDefinition> PendingDefinitions { get; } = new();

        /// <summary>What each pending unit was bought for when it was queued, 0 for one that takes a
        /// reserve unit at the door.</summary>
        internal List<float> PendingPaid { get; } = new();

        internal Queue<VehicleDefinition> ExpectedSpawnDefinitions { get; } = new();

        internal HashSet<uint> SeenUnitIds { get; } = new();

        internal List<PendingRallyUnit> PendingRallyUnits { get; } = new();

        internal Dictionary<string, int> PendingSummaryCounts { get; } = new(StringComparer.Ordinal);

        internal List<string> PendingSummaryLines { get; } = new();

        internal bool PendingSummaryDirty { get; set; } = true;

        internal int ReinforceGroup { get; set; }

        /// <summary>Vehicles from this depot drive straight across country instead of keeping to the roads.</summary>
        internal bool OffRoad { get; set; }

        /// <summary>Vehicles from this depot roll out with their radar switched off.</summary>
        internal bool RadarOff { get; set; }

        /// <summary>New units drive at the nearest enemy base instead of gathering at the rally point.</summary>
        internal bool AutoAttack { get; set; }

        internal bool HasRallyPoint { get; set; }

        /// <summary>True while the rally point is the automatic staging block, not one the player placed.</summary>
        internal bool IsDefaultRallyPoint { get; set; }

        internal GlobalPosition RallyPoint { get; set; }

        /// <summary>
        /// Wraps after <see cref="RallySlotsKept"/>, so later units are not sent to ever-wider rings. The
        /// units that took the first slots have long since been ordered away.
        /// </summary>
        internal int NextRallySlot
        {
            get => nextRallySlot;
            set => nextRallySlot = value > RallySlotsKept ? 0 : value;
        }

        /// <summary>Guest only: when a spawn request to the host may be sent again.</summary>
        internal float HostAnswerDueAt { get; set; }

        /// <summary><c>Time.time</c> the head of the queue is built, 0 while its clock has not started.</summary>
        internal float BuildDoneAt { get; set; }

        internal float BuildSeconds { get; set; }

        internal void Add(VehicleDefinition definition, float paid)
        {
            PendingDefinitions.Add(definition);
            PendingPaid.Add(paid);
            PendingSummaryDirty = true;
        }

        /// <summary>Returns what the removed unit had been paid for.</summary>
        internal float RemoveAt(int index)
        {
            float paid = PendingPaid[index];
            PendingDefinitions.RemoveAt(index);
            PendingPaid.RemoveAt(index);
            PendingSummaryDirty = true;
            if (index == 0)
            {
                BuildDoneAt = 0f;
            }

            return paid;
        }

        /// <summary>Returns what the removed units had been paid for, in total.</summary>
        internal float Clear()
        {
            float paid = 0f;
            for (int i = 0; i < PendingPaid.Count; i++)
            {
                paid += PendingPaid[i];
            }

            PendingDefinitions.Clear();
            PendingPaid.Clear();
            PendingSummaryDirty = true;
            BuildDoneAt = 0f;
            return paid;
        }
    }

    internal sealed class PendingRallyUnit
    {
        internal PendingRallyUnit(Unit unit, int rallySlot)
        {
            Unit = unit;
            RallySlot = rallySlot;
        }

        internal Unit Unit { get; }

        internal int RallySlot { get; set; }

        internal GlobalPosition ExitCommand { get; set; }

        internal bool ExitCommandLocked { get; set; }
    }

    private sealed class VehicleDepotEntry
    {
        internal VehicleDepotEntry(VehicleDepot depot, Unit owner, string label)
        {
            Depot = depot;
            Owner = owner;
            Label = label;
        }

        internal VehicleDepot Depot { get; }

        internal Unit Owner { get; }

        internal string Label { get; }
    }
}
