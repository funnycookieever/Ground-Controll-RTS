using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSupplyHeliService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Ceiling on the run's cruise/drop height. 1000 m because that is where
    /// <c>AIHeloCombatState</c> clamps <c>desiredHeight</c>, so anything above it is a number the
    /// flight AI will not honour.</summary>
    internal const float MaxTerrainClearanceMeters = 1000f;

    /// <summary>Notch of the TRANSPORT window's slider. Replaced a list of nine fixed heights, which
    /// is the same change the Air Command mission tab's altitude control got.</summary>
    internal const float TerrainClearanceStepMeters = 25f;

    private const string SamSiteCargoSupportSummary = "Automatic SAM-site ammunition run";
    private const string SamSiteCargoSupportPrefix = "SAM-site ammunition:";
    private const string SamSiteFoundationSupportPrefix = "SAM-site foundation:";
    private const string SamSiteJacknifeSupportPrefix = "SAM-site Jacknife:";
    private const float StatusDurationSeconds = 5f;
    private const float PendingSpawnTimeoutSeconds = 90f;

    private static readonly Dictionary<WeaponMount, UnitDefinition[]> MountCargo = new();

    private static readonly FieldInfo? AircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");
    private static readonly FieldInfo? PilotField = AccessTools.Field(typeof(PilotBaseState), "pilot");
    private static readonly FieldInfo? LastLandingSpotCheckField = AccessTools.Field(typeof(AIHeloTransportState), "lastLandingSpotCheck");
    private static readonly FieldInfo? TouchedDownTimeField = AccessTools.Field(typeof(AIHeloTransportState), "touchedDownTime");
    private static readonly FieldInfo? TimeWithoutMissionField = AccessTools.Field(typeof(AIHeloTransportState), "timeWithoutMission");
    private static readonly FieldInfo? AirdropField = AccessTools.Field(typeof(AIHeloTransportState), "airdrop");
    private static readonly FieldInfo? TransportModeField = AccessTools.Field(typeof(AIHeloTransportState), "transportMode");
    private static readonly FieldInfo? TransportDestinationField = AccessTools.Field(typeof(AIHeloTransportState), "transportDestination");
    private static readonly Type? TransportDestinationType = typeof(AIHeloTransportState).GetNestedType("TransportDestination", BindingFlags.NonPublic);
    private static readonly FieldInfo? DestinationValidMissionField = AccessTools.Field(TransportDestinationType, "validMission");
    private static readonly FieldInfo? DestinationDropConditionsField = AccessTools.Field(TransportDestinationType, "dropConditionsMet");
    private static readonly FieldInfo? DestinationTouchdownField = AccessTools.Field(TransportDestinationType, "touchdownPoint");
    private static readonly FieldInfo? DestinationEnemyPositionField = AccessTools.Field(TransportDestinationType, "enemyPosition");
    private static readonly FieldInfo? DestinationLzField = AccessTools.Field(TransportDestinationType, "LZ");
    private static readonly FieldInfo? DestinationSlopeField = AccessTools.Field(TransportDestinationType, "slope");
    private static readonly FieldInfo? DestinationAttemptsField = AccessTools.Field(TransportDestinationType, "touchdownPointAttempts");
    private static readonly MethodInfo? UpdateTouchdownPointMethod = AccessTools.Method(TransportDestinationType, "UpdateTouchdownPoint");
    private static readonly MethodInfo? UpdateLzForUnitMethod = AccessTools.Method(
        TransportDestinationType,
        "UpdateLZ",
        new[] { typeof(Aircraft), typeof(Unit) });

    private static readonly FieldInfo? GroundVehicleParachuteField = AccessTools.Field(typeof(GroundVehicle), "parachuteSystem");
    private static readonly FieldInfo? ContainerParachuteField = AccessTools.Field(typeof(Container), "parachuteSystem");
    private static readonly FieldInfo? LandingStatePointField =
        AccessTools.Field(typeof(AIHeloLandingState), "landingPoint");

    private static readonly FieldInfo? LandingStateReachedApproachField =
        AccessTools.Field(typeof(AIHeloLandingState), "reachedApproachPoint");

    private static readonly FieldInfo? StateNearestAirbaseField =
        AccessTools.Field(typeof(PilotBaseState), "nearestAirbase");

    private static readonly FieldInfo? StateDestinationField =
        AccessTools.Field(typeof(PilotBaseState), "destination");

    private static readonly FieldInfo? WeaponHardpointField = AccessTools.Field(typeof(Weapon), "hardpoint");
    private static readonly MethodInfo? MountedCargoRemoveMethod = AccessTools.Method(typeof(MountedCargo), "RemoveFromHardpoint");
    private static readonly FieldInfo? BayDoorOpenTimerField = AccessTools.Field(typeof(BayDoor), "openTimer");
    private static readonly FieldInfo? BayDoorOpenAmountField = AccessTools.Field(typeof(BayDoor), "openAmount");
    private static readonly FieldInfo? SwivelAircraftField = AccessTools.Field(typeof(SwivelDuctSystem), "aircraft");

    private static VehicleDefinition? radarTruck;

    private readonly List<CargoAircraftOption> aircraftOptions = new();
    private readonly List<AirbaseOption> airbaseOptions = new();
    private readonly Queue<QueuedCargoSpawn> queuedCargoSpawns = new();
    private readonly Dictionary<Aircraft, CargoMission> assignedMissions = new();
    private readonly Dictionary<Autopilot, float> terrainClearanceAutopilots = new();
    private readonly Dictionary<Autopilot, Aircraft> assignedAutopilotAircraft = new();
    private readonly HashSet<Aircraft> pendingTerrainAutopilotBindings = new();
    private readonly HashSet<Unit> pendingSamCargoDeposits = new();

    // One per airfield, not one for the whole service: a carrier's spawn waits on its elevator doors,
    // and a single slot held every other carrier's launch back until that one aircraft appeared.
    private readonly List<PendingAircraftSpawn> pendingAircraftSpawns = new();

    private PendingTargetSelection? pendingTargetSelection;
    private float terrainClearanceMeters = 250f;
    private bool airdropDelivery;
    private bool uiVisible;
    private bool cargoRosterLogged;
    private float nextAirbaseRefreshAt;
    private float nextMissionPruneAt;
    private float statusUntil;
    private string statusText = string.Empty;

    internal CommanderSupplyHeliService()
    {
        Instance = this;
        CommanderNetwork.OnHost("cargo", HostCargoRun);
        CommanderNetwork.OnHost("cargo.lz", HostSetLz);
        CommanderNetwork.OnHost("naval.supply", HostNavalSupply);
    }

    internal static CommanderSupplyHeliService? Instance { get; private set; }

    internal IReadOnlyList<CargoAircraftOption> AircraftOptions => aircraftOptions;

    internal IReadOnlyList<AirbaseOption> AirbaseOptions => airbaseOptions;

    internal bool AwaitingTargetSelection => pendingTargetSelection != null || AwaitingLzSelection;

    internal int QueuedSpawnCount => queuedCargoSpawns.Count;

    internal string StatusText => pendingTargetSelection != null
        ? pendingTargetSelection.GetTargetPrompt()
        : Time.unscaledTime <= statusUntil
            ? statusText
            : queuedCargoSpawns.Count > 0
                ? $"Waiting for an available supply hangar ({queuedCargoSpawns.Count} queued)."
                : string.Empty;

    internal int SelectedAircraftIndex { get; private set; }

    /// <summary>On whenever a height is set: one slider, where 0 reads STANDARD, instead of a
    /// toggle and a slider for the same number.</summary>
    internal bool HighTerrainClearance => terrainClearanceMeters > 0f;

    internal float TerrainClearanceMeters
    {
        get => terrainClearanceMeters;
        set => terrainClearanceMeters = Mathf.Clamp(value, 0f, MaxTerrainClearanceMeters);
    }

    /// <summary>
    /// Fly-by delivery. Compulsory for a fixed-wing transport, which has no way to put itself down
    /// on open ground.
    /// </summary>
    internal bool AirdropDelivery
    {
        get => airdropDelivery || SelectedAircraft?.Rotary == false;
        set => airdropDelivery = value && SelectedCargoSupportsAirdrop;
    }

    internal bool IncludeEcm { get; set; } = true;

    internal bool IncludeCountermeasures { get; set; } = true;

    internal bool FillRemainingHardpoints { get; set; }

    internal bool UseOtherAirfields { get; set; } = true;

    /// <summary>Off: the helicopter skips <see cref="CommanderHeloRoadRoute"/> and flies straight.</summary>
    internal bool FollowRoads { get; set; } = true;

    internal bool SelectedCargoSupportsAirdrop => SelectedAircraft != null
        && SelectedAircraft.CargoSlots.Exists(slot => slot.SelectedMount != null)
        && SelectedAircraft.CargoSlots.TrueForAll(slot => slot.SelectedMount == null || CargoMountSupportsAirdrop(slot.SelectedMount));

    internal bool HasSelectedCargo => SelectedAircraft != null
        && SelectedAircraft.CargoSlots.Exists(slot => slot.SelectedMount != null);

    internal Airbase? SelectedAirbase { get; private set; }

    internal CargoAircraftOption? SelectedAircraft => aircraftOptions.Count == 0
        ? null
        : aircraftOptions[Mathf.Clamp(SelectedAircraftIndex, 0, aircraftOptions.Count - 1)];

    public void Activate()
    {
        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }
    }

    public void Deactivate()
    {
        uiVisible = false;
        CancelTargetSelection(showStatus: false);
    }

    public void TickActive()
    {
        if (AwaitingTargetSelection && CommanderGameInput.CancelDown)
        {
            // The public overload, so the Cancel binding also drops an armed SET LZ.
            CancelTargetSelection();
        }
    }

    public void TickPersistent()
    {
        BindPendingTerrainAutopilots();

        for (int i = pendingAircraftSpawns.Count - 1; i >= 0; i--)
        {
            PendingAircraftSpawn pending = pendingAircraftSpawns[i];
            if (Time.unscaledTime > pending.ExpiresAt)
            {
                CommanderPlugin.Log.LogWarning($"Supply cargo run assignment timed out: aircraft={pending.Definition.name}");
                NotifySamMissionFailed(pending.SupportSummary);
                pendingAircraftSpawns.RemoveAt(i);
            }
        }

        if (CommanderScheduler.IsDue(ref nextAirbaseRefreshAt, 1f))
        {
            if (uiVisible)
            {
                RefreshAirbaseOptions();
            }

            if (queuedCargoSpawns.Count > 0)
            {
                TryProcessQueuedCargoSpawns();
            }
        }

        if (CommanderScheduler.IsDue(ref nextMissionPruneAt, 2f))
        {
            PruneFinishedMissions();
        }

        TickFixedWingDrops();
        LogRotaryLaunches();
    }

    public void ResetSession()
    {
        aircraftOptions.Clear();
        airbaseOptions.Clear();
        assignedMissions.Clear();
        terrainClearanceAutopilots.Clear();
        assignedAutopilotAircraft.Clear();
        pendingTerrainAutopilotBindings.Clear();
        pendingSamCargoDeposits.Clear();
        pendingTargetSelection = null;
        pendingAircraftSpawns.Clear();
        pendingLzAircraft = null;
        uiVisible = false;
        cargoRosterLogged = false;
        queuedCargoSpawns.Clear();
        SelectedAirbase = null;
        SelectedAircraftIndex = 0;
        terrainClearanceMeters = 0f;
        airdropDelivery = false;
        IncludeEcm = true;
        IncludeCountermeasures = true;
        FillRemainingHardpoints = false;
        UseOtherAirfields = true;
        FollowRoads = true;
        nextAirbaseRefreshAt = CommanderScheduler.Stagger("supply.airbases", 1f, 0.5f);
        nextMissionPruneAt = CommanderScheduler.Stagger("supply.prune", 2f, 0.8f);
        statusText = string.Empty;
    }

    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Called from a Harmony prefix; a throw would break the game's own transport AI, so the aircraft goes back to it instead.")]
    internal static bool TryOverrideTransportTarget(AIHeloTransportState state)
    {
        try
        {
            return Instance != null && Instance.OverrideTransportTarget(state);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Supply cargo target hook failed; returning this aircraft to Basegame cargo logic: {exception}");
            Instance?.EndTargetOverrideForState(state);
            return false;
        }
    }

    internal static void NotifyTransportStateLeft(AIHeloTransportState state)
    {
        Instance?.EndTargetOverrideForState(state);
    }

    internal static bool ShouldDelayCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        return Instance != null && Instance.ShouldDelayAssignedCargoTakeoff(pilot, requestedState);
    }

    internal static bool TryDeployAssignedCargo(AIHeloTransportState state)
    {
        return Instance != null && Instance.DeployNextAssignedCargo(state);
    }

    internal static bool ShouldSuppressAssignedEjection(AIHeloTransportState state)
    {
        return Instance != null && Instance.SuppressEjectionAtAssignedSamSite(state);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.HandleAircraftReturned(aircraft);
    }

    internal static bool TryOverrideAssignedReturnAirbase(AIHeloLandingState state)
    {
        return Instance != null && Instance.OverrideAssignedReturnAirbase(state);
    }

    internal static bool TryOverrideAssignedNearestAirbase(PilotBaseState state)
    {
        return Instance != null && Instance.OverrideAssignedNearestAirbase(state);
    }

    internal static void NotifyCargoActivated(Aircraft aircraft, Unit cargoUnit)
    {
        Instance?.HoldDeployedCargo(aircraft, cargoUnit);
    }

    internal static void PrepareAssignedTerrainFlight(
        Autopilot autopilot,
        GlobalPosition destination,
        ref float altitudeHold,
        bool followTerrain)
    {
        HoldCargoRunCruiseClearance(autopilot, destination, ref altitudeHold, followTerrain);
        if (Instance == null
            || !Instance.terrainClearanceAutopilots.TryGetValue(autopilot, out float clearance))
        {
            return;
        }

        if (followTerrain)
        {
            altitudeHold = Mathf.Max(altitudeHold, clearance);
        }

        if (!Instance.assignedAutopilotAircraft.TryGetValue(autopilot, out Aircraft aircraft)
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive
            || mission.FoundationSiteId < 0)
        {
            return;
        }

        altitudeHold = Mathf.Max(altitudeHold, mission.SteepLanding ? 100f : 60f);
    }

    internal static void ForceAssignedVerticalTakeoff(SwivelDuctSystem swivelDuct)
    {
        if (Instance == null
            || Instance.assignedMissions.Count == 0
            || SwivelAircraftField?.GetValue(swivelDuct) is not Aircraft aircraft
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.VerticalDepartureActive)
        {
            return;
        }

        if (aircraft.radarAlt >= 35f)
        {
            mission.VerticalDepartureActive = false;
            return;
        }

        aircraft.GetInputs().customAxis1 = 0f;
    }

    /// <summary>The same total as <see cref="GetCargoCost"/>, read off the window's bays.</summary>
    internal static float GetSelectedCargoCost(CargoAircraftOption aircraft)
    {
        float cost = 0f;
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CargoSlotOption slot = aircraft.CargoSlots[i];
            if (slot.SelectedMount != null)
            {
                cost += Mathf.Max(aircraft.HardpointSets[slot.HardpointIndex].hardpoints?.Count ?? 0, 1)
                    * GetMountCargoCost(slot.SelectedMount);
            }
        }

        return cost;
    }

    /// <summary>
    /// Loads the price editor has no vehicle row for, or whose vehicle row is not what they cost:
    /// containers, and the radar trailers priced off the truck.
    /// </summary>
    internal static List<UnitDefinition> FindCargoOnlyUnits()
    {
        List<UnitDefinition> units = new();
        foreach (WeaponMount mount in Resources.FindObjectsOfTypeAll<WeaponMount>())
        {
            if (mount == null)
            {
                continue;
            }

            foreach (UnitDefinition cargo in GetMountCargo(mount))
            {
                if (!string.IsNullOrEmpty(cargo.unitName)
                    && (cargo is not VehicleDefinition vehicle
                        || (vehicle.vehicleType == VehicleType.RDR && CommanderGameAccess.IsTrailerVehicleDefinition(vehicle))))
                {
                    units.Add(cargo);
                }
            }
        }

        return units;
    }

    internal static float GetDefaultCargoUnitCost(UnitDefinition cargo)
    {
        if (cargo is VehicleDefinition vehicle)
        {
            return vehicle.vehicleType == VehicleType.RDR ? GetRadarTruckPrice(vehicle) : CommanderEconomyService.PriceOf(vehicle);
        }

        // Against a bunker's shelf, so a tonne in a container costs what a tonne bought at the bunker does.
        Rearmer? rearmer = cargo.unitPrefab.GetComponentInChildren<Rearmer>(true);
        return rearmer != null && rearmer.Capacity > 0f
            ? CommanderSupplyStoreService.GetBaseCost(
                CommanderSettings.SupplyAmmoPurchaseCost, rearmer.Capacity, CommanderSupplyStoreService.GetBunkerAmmoKg())
            : CommanderEconomyService.PriceOf(cargo);
    }

    internal static string GetCargoSlotButtonLabel(CargoSlotOption slot)
    {
        string cargo = slot.SelectedMount != null
            ? GetCargoLabel(slot.SelectedMount, string.Empty)
            : "None";
        return $"{slot.Label}\n{cargo}";
    }

    internal static string GetCargoMountLabel(WeaponMount mount)
    {
        string suffix = CargoMountSupportsAirdrop(mount) ? " [Airdrop]" : string.Empty;
        return GetCargoLabel(mount, string.Empty) + suffix;
    }

    internal static string GetAircraftButtonLabel(CargoAircraftOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = UnitConverter.ValueReading(CommanderEconomyService.PriceOf(option.Definition)) ?? CommanderEconomyService.PriceOf(option.Definition).ToString("F1", CultureInfo.CurrentCulture);
        return $"{option.Label} | Supply {supply} | {cost}";
    }

    internal static string GetAirbaseButtonLabel(AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{(option.Ready ? "READY" : "WAIT")} | {option.Label} | {distance}";
    }

    internal void CopyActiveDeliveryTargets(List<GlobalPosition> targets)
    {
        targets.Clear();
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Value.TargetOverrideActive)
            {
                targets.Add(entry.Value.Target);
            }
        }
    }

    internal void SetUiVisible(bool visible)
    {
        uiVisible = visible;
    }

    internal void CancelTargetSelection()
    {
        CancelLzRetarget();
        if (pendingTargetSelection != null)
        {
            CancelTargetSelection(showStatus: true);
        }
    }

    internal void CancelDeploymentSelection()
    {
        CancelTargetSelection(showStatus: false);
        SetStatus("Deployment selection cleared.");
    }

    internal void CancelSamSiteMissions(int siteId)
    {
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            CargoMission mission = entry.Value;
            if (mission.FoundationSiteId != siteId
                && mission.DepositSiteId != siteId
                && mission.JacknifeSiteId != siteId)
            {
                continue;
            }

            CancelMission(entry.Key, mission);
            IssueSupplyReturnToBase(entry.Key, mission);
        }

        pendingAircraftSpawns.RemoveAll(pending => SupportSummaryMatchesSite(pending.SupportSummary, siteId));

        int queuedCount = queuedCargoSpawns.Count;
        for (int i = 0; i < queuedCount; i++)
        {
            QueuedCargoSpawn request = queuedCargoSpawns.Dequeue();
            if (!SupportSummaryMatchesSite(request.SupportSummary, siteId))
            {
                queuedCargoSpawns.Enqueue(request);
            }
        }
    }

    internal void RefreshOptions()
    {
        aircraftOptions.Clear();
        AircraftDefinition[] definitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        HashSet<AircraftDefinition> seenDefinitions = new();
        for (int i = 0; i < definitions.Length; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (!seenDefinitions.Add(definition))
            {
                continue;
            }

            CargoAircraftOption? option = CreateAircraftOption(definition);
            if (option != null)
            {
                aircraftOptions.Add(option);
            }
        }

        aircraftOptions.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        SelectedAircraftIndex = Mathf.Clamp(SelectedAircraftIndex, 0, Mathf.Max(aircraftOptions.Count - 1, 0));
        LogCargoRosterOnce();
        RefreshAirbaseOptions();
    }

    internal void SelectAircraft(int index)
    {
        if (index < 0 || index >= aircraftOptions.Count)
        {
            return;
        }

        SelectedAircraftIndex = index;
        RefreshAirbaseOptions();
    }

    internal void SelectAirbase(int index)
    {
        if (index < 0 || index >= airbaseOptions.Count)
        {
            return;
        }

        SelectedAirbase = airbaseOptions[index].Airbase;
    }

    internal void CycleCargoSlot(int slotIndex)
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null || slotIndex < 0 || slotIndex >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CargoSlotOption selectedSlot = aircraft.CargoSlots[slotIndex];
        selectedSlot.CycleSelection();
        if (selectedSlot.SelectedMount == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CargoSlotOption other = aircraft.CargoSlots[i];
            if (!ReferenceEquals(other, selectedSlot)
                && other.SelectedMount != null
                && SetsConflict(aircraft.HardpointSets, selectedSlot.HardpointIndex, other.HardpointIndex))
            {
                other.Clear();
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal void SelectCargoMount(int slotIndex, int mountIndex)
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null || slotIndex < 0 || slotIndex >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CargoSlotOption selectedSlot = aircraft.CargoSlots[slotIndex];
        selectedSlot.Select(mountIndex);
        if (selectedSlot.SelectedMount != null)
        {
            for (int i = 0; i < aircraft.CargoSlots.Count; i++)
            {
                CargoSlotOption other = aircraft.CargoSlots[i];
                if (!ReferenceEquals(other, selectedSlot)
                    && other.SelectedMount != null
                    && SetsConflict(aircraft.HardpointSets, selectedSlot.HardpointIndex, other.HardpointIndex))
                {
                    other.Clear();
                }
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal void ClearSelectedCargo()
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            aircraft.CargoSlots[i].Clear();
        }

        airdropDelivery = false;
    }

    internal void BeginSelectedCargoRun()
    {
        CargoAircraftOption? aircraftOption = SelectedAircraft;
        if (aircraftOption == null)
        {
            SetStatus("No cargo-capable aircraft is selected.");
            return;
        }

        // A guest plans the run in its own window and the host flies it (SpawnCargoRun forwards).
        FactionHQ? hq;
        if (CommanderNetwork.IsGuest)
        {
            hq = CommanderGameAccess.GetLocalHq();
            if (hq == null || !CommanderNetwork.HostHasMod)
            {
                SetStatus(CommanderNetwork.HostMissingReason);
                return;
            }
        }
        else if (!CanHostSpawn(out hq, out string error))
        {
            SetStatus(error);
            return;
        }

        Airbase? airbase = SelectedAirbase;
        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("Select a friendly airbase that supports this aircraft.");
            return;
        }

        Loadout cargoLoadout = BuildSelectedCargoLoadout(
            aircraftOption,
            airbase!,
            hq!,
            IncludeEcm,
            IncludeCountermeasures,
            FillRemainingHardpoints,
            out int cargoCount,
            out string cargoLabel,
            out string supportSummary);
        if (cargoCount <= 0)
        {
            SetStatus("Select cargo for at least one cargo bay.");
            return;
        }

        bool airdrop = AirdropDelivery;
        if (airdrop && !SelectedCargoSupportsAirdrop)
        {
            airdropDelivery = false;
            SetStatus(aircraftOption.Rotary
                ? "Airdrop is unavailable because selected cargo has no parachute system."
                : "This transport has no rotary pilot, so it can only airdrop — and this cargo has no parachute.");
            return;
        }

        float airframeCost = hq!.GetUnitSupply(aircraftOption.Definition) > 0 ? 0f : CommanderEconomyService.PriceOf(aircraftOption.Definition);
        if (hq.factionFunds < airframeCost + GetSelectedCargoCost(aircraftOption))
        {
            SetStatus("The faction cannot afford this supply aircraft and its cargo.");
            return;
        }

        pendingTargetSelection = new PendingTargetSelection(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase!,
            HighTerrainClearance,
            terrainClearanceMeters,
            airdrop,
            supportSummary,
            UseOtherAirfields,
            FollowRoads);
        SetStatus(pendingTargetSelection.GetTargetPrompt());
    }

    internal bool RequestAutomaticCargoRun(GlobalPosition target)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        List<(CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount, float Capacity)> choices = new();
        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition)
                    || !IsSamSupplyAirbaseSafe(airbase, target))
                {
                    continue;
                }

                for (int slotIndex = 0; slotIndex < aircraft.CargoSlots.Count; slotIndex++)
                {
                    CargoSlotOption slot = aircraft.CargoSlots[slotIndex];
                    for (int mountIndex = 0; mountIndex < slot.Mounts.Count; mountIndex++)
                    {
                        WeaponMount mount = slot.Mounts[mountIndex];
                        if (WeaponChecker.MountAllowedHQ(mount, hq)
                            && WeaponChecker.MountAllowedAirbase(mount, airbase)
                            && TryGetAmmunitionCargoCapacity(mount, out float capacity))
                        {
                            choices.Add((aircraft, airbase, slot, mount, capacity));
                        }
                    }
                }
            }
        }

        if (choices.Count == 0)
        {
            SetStatus("No compatible ammunition cargo helicopter and airbase combination is available.");
            return false;
        }

        float largestCapacity = 0f;
        for (int i = 0; i < choices.Count; i++)
        {
            largestCapacity = Mathf.Max(largestCapacity, choices[i].Capacity);
        }

        choices.RemoveAll(choice => choice.Capacity < largestCapacity - 0.01f);
        (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount, float Capacity) choice = choices[UnityEngine.Random.Range(0, choices.Count)];
        Loadout loadout = CreateEmptyLoadout(choice.Aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.Aircraft.HardpointSets,
            choice.Slot.HardpointIndex,
            choice.Mount);
        string cargoLabel = GetCargoLabel(choice.Mount, string.Empty);
        SpawnCargoRun(
            choice.Aircraft,
            loadout,
            cargoLabel,
            choice.Airbase,
            useHighTerrainClearance: true,
            terrainClearanceMeters: 100f,
            useAirdrop: false,
            supportSummary: SamSiteCargoSupportSummary,
            useOtherAirfields: true,
            target);
        return true;
    }

    internal bool RequestSamSiteFoundationDrop(int siteId, GlobalPosition target)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption JackSlot, WeaponMount JackMount,
            CargoSlotOption AmmoSlot, WeaponMount AmmoMount, float AmmoCapacity)? best = null;
        (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount)? bestJack = null;
        (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount, float Capacity)? bestAmmo = null;
        float bestDistance = float.MaxValue;
        float bestJackDistance = float.MaxValue;
        float bestAmmoDistance = float.MaxValue;
        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (!IsAvailableAirbase(airbase, hq, aircraft.Definition)
                    || !IsSamSupplyAirbaseSafe(airbase, target))
                {
                    continue;
                }

                Transform airbaseTransform = airbase.center != null ? airbase.center : airbase.transform;
                float airbaseDistance = HorizontalSquareDistance(
                    airbaseTransform.GlobalPosition(),
                    target);

                for (int jackSlotIndex = 0; jackSlotIndex < aircraft.CargoSlots.Count; jackSlotIndex++)
                {
                    CargoSlotOption jackSlot = aircraft.CargoSlots[jackSlotIndex];
                    for (int jackMountIndex = 0; jackMountIndex < jackSlot.Mounts.Count; jackMountIndex++)
                    {
                        WeaponMount jackMount = jackSlot.Mounts[jackMountIndex];
                        if (IsJacknifeCargo(jackMount)
                            && WeaponChecker.MountAllowedHQ(jackMount, hq)
                            && WeaponChecker.MountAllowedAirbase(jackMount, airbase))
                        {
                            if (bestJack == null || airbaseDistance < bestJackDistance)
                            {
                                bestJack = (aircraft, airbase, jackSlot, jackMount);
                                bestJackDistance = airbaseDistance;
                            }
                        }

                        if (TryGetAmmunitionCargoCapacity(jackMount, out float standaloneCapacity)
                            && WeaponChecker.MountAllowedHQ(jackMount, hq)
                            && WeaponChecker.MountAllowedAirbase(jackMount, airbase)
                            && (bestAmmo == null
                                || airbaseDistance < bestAmmoDistance - 1f
                                || (Mathf.Abs(airbaseDistance - bestAmmoDistance) <= 1f
                                    && standaloneCapacity > bestAmmo.Value.Capacity)))
                        {
                            bestAmmo = (aircraft, airbase, jackSlot, jackMount, standaloneCapacity);
                            bestAmmoDistance = airbaseDistance;
                        }

                        if (!IsJacknifeCargo(jackMount)
                            || !WeaponChecker.MountAllowedHQ(jackMount, hq)
                            || !WeaponChecker.MountAllowedAirbase(jackMount, airbase))
                        {
                            continue;
                        }

                        for (int ammoSlotIndex = 0; ammoSlotIndex < aircraft.CargoSlots.Count; ammoSlotIndex++)
                        {
                            CargoSlotOption ammoSlot = aircraft.CargoSlots[ammoSlotIndex];
                            if (ammoSlot.HardpointIndex == jackSlot.HardpointIndex
                                || SetsConflict(
                                    aircraft.HardpointSets,
                                    jackSlot.HardpointIndex,
                                    ammoSlot.HardpointIndex))
                            {
                                continue;
                            }

                            for (int ammoMountIndex = 0; ammoMountIndex < ammoSlot.Mounts.Count; ammoMountIndex++)
                            {
                                WeaponMount ammoMount = ammoSlot.Mounts[ammoMountIndex];
                                if (!TryGetAmmunitionCargoCapacity(ammoMount, out float capacity)
                                    || !WeaponChecker.MountAllowedHQ(ammoMount, hq)
                                    || !WeaponChecker.MountAllowedAirbase(ammoMount, airbase))
                                {
                                    continue;
                                }

                                if (best == null
                                    || airbaseDistance < bestDistance - 1f
                                    || (Mathf.Abs(airbaseDistance - bestDistance) <= 1f
                                        && capacity > best.Value.AmmoCapacity))
                                {
                                    best = (aircraft, airbase, jackSlot, jackMount, ammoSlot, ammoMount, capacity);
                                    bestDistance = airbaseDistance;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (best == null)
        {
            if (bestJack == null || bestAmmo == null)
            {
                SetStatus("No compatible Jacknife and ammunition cargo combination is available.");
                return false;
            }

            string supportSummary = SamSiteFoundationSupportPrefix + siteId;
            (CargoAircraftOption aircraft, Airbase airbase, CargoSlotOption slot, WeaponMount mount) = bestJack.Value;
            Loadout jackLoadout = CreateEmptyLoadout(aircraft.HardpointSets.Length);
            PlaceCargoAndClearNonCargo(
                jackLoadout,
                aircraft.HardpointSets,
                slot.HardpointIndex,
                mount);
            SpawnCargoRun(
                aircraft,
                jackLoadout,
                GetCargoLabel(mount, string.Empty),
                airbase,
                useHighTerrainClearance: true,
                terrainClearanceMeters: 100f,
                useAirdrop: false,
                supportSummary,
                useOtherAirfields: false,
                target);

            (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount, float Capacity) ammoChoice = bestAmmo.Value;
            Loadout ammoLoadout = CreateEmptyLoadout(ammoChoice.Aircraft.HardpointSets.Length);
            PlaceCargoAndClearNonCargo(
                ammoLoadout,
                ammoChoice.Aircraft.HardpointSets,
                ammoChoice.Slot.HardpointIndex,
                ammoChoice.Mount);
            SpawnCargoRun(
                ammoChoice.Aircraft,
                ammoLoadout,
                GetCargoLabel(ammoChoice.Mount, string.Empty),
                ammoChoice.Airbase,
                useHighTerrainClearance: true,
                terrainClearanceMeters: 100f,
                useAirdrop: false,
                supportSummary,
                useOtherAirfields: false,
                target);
            CommanderPlugin.Log.LogInfo(
                $"SAM foundation cargo split across two landing sorties: "
                + $"Jacknife={GetCargoLabel(mount, string.Empty)}, "
                + $"ammo={GetCargoLabel(ammoChoice.Mount, string.Empty)} ({ammoChoice.Capacity:0}).");
            return true;
        }

        (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption JackSlot, WeaponMount JackMount, CargoSlotOption AmmoSlot, WeaponMount AmmoMount, float AmmoCapacity) choice = best.Value;
        CommanderPlugin.Log.LogInfo(
            $"SAM foundation cargo selected: aircraft={choice.Aircraft.Label}, "
            + $"Jacknife={GetCargoLabel(choice.JackMount, string.Empty)}, "
            + $"ammo={GetCargoLabel(choice.AmmoMount, string.Empty)} ({choice.AmmoCapacity:0}).");
        Loadout loadout = CreateEmptyLoadout(choice.Aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.Aircraft.HardpointSets,
            choice.JackSlot.HardpointIndex,
            choice.JackMount);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.Aircraft.HardpointSets,
            choice.AmmoSlot.HardpointIndex,
            choice.AmmoMount);
        SpawnCargoRun(
            choice.Aircraft,
            loadout,
            "Jacknife + ammunition",
            choice.Airbase,
            useHighTerrainClearance: true,
            terrainClearanceMeters: 100f,
            useAirdrop: false,
            supportSummary: SamSiteFoundationSupportPrefix + siteId,
            useOtherAirfields: false,
            target);
        return true;
    }

    internal bool TrySpawnAtWorldPoint(Vector2 screenPosition)
    {
        // SET LZ shares the armed-click path, so it is answered before the launch selection.
        if (TryCompleteLzRetarget(screenPosition))
        {
            return true;
        }

        if (pendingTargetSelection == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid terrain point was found. Click visible terrain or a surface.");
            return true;
        }

        PendingTargetSelection selection = pendingTargetSelection;
        selection.Targets.Add(target);

        bool repeatSelection = CommanderShortcutInput.IsPressed(CommanderSettings.RepeatDeployment);
        if (!repeatSelection)
        {
            pendingTargetSelection = null;
        }

        SpawnCargoRun(
            selection.Aircraft,
            selection.Loadout,
            selection.CargoLabel,
            selection.Airbase,
            selection.HighTerrainClearance,
            selection.TerrainClearanceMeters,
            selection.Airdrop,
            selection.SupportSummary,
            selection.UseOtherAirfields,
            selection.Targets,
            selection.FollowRoads);
        if (repeatSelection)
        {
            selection.Targets.Clear();
            SetStatus("Supply run queued. Hold Shift and click another destination, or release Shift for the final run.");
        }

        return true;
    }

    private static bool TryGetAmmunitionCargoCapacity(WeaponMount mount, out float capacity)
    {
        capacity = 0f;
        if (!IsRuntimeCargoMount(mount) || mount.prefab == null)
        {
            return false;
        }

        MountedCargo[] mountedCargo = mount.prefab.GetComponentsInChildren<MountedCargo>(true);
        for (int i = 0; i < mountedCargo.Length; i++)
        {
            UnitDefinition? cargoDefinition = mountedCargo[i]?.cargo;
            Rearmer? rearmer = cargoDefinition?.unitPrefab != null
                ? cargoDefinition.unitPrefab.GetComponentInChildren<Rearmer>(true)
                : null;
            if (rearmer != null)
            {
                capacity += Mathf.Max(0f, rearmer.Capacity);
            }
        }

        return capacity > 0.01f;
    }

    private static bool IsJacknifeCargo(WeaponMount mount)
    {
        if (!TryGetMountedCargoDefinition(mount, out UnitDefinition? definition))
        {
            return false;
        }

        string identity = $"{definition!.unitName} {definition.code} {definition.jsonKey} {definition.name}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool TryGetMountedCargoDefinition(
        WeaponMount mount,
        out UnitDefinition? definition)
    {
        definition = null;
        if (!IsRuntimeCargoMount(mount) || mount.prefab == null)
        {
            return false;
        }

        MountedCargo? mountedCargo = mount.prefab.GetComponentInChildren<MountedCargo>(true);
        definition = mountedCargo?.cargo;
        return definition?.unitPrefab != null;
    }

    /// <summary>
    /// What the cargo itself costs (nothing rides free). A vehicle costs
    /// what the depot sells it for, live, so the Unit Prices profile reaches it. The depot sells no
    /// containers, so a load with a shelf is priced by the kilogram like a BUY.
    /// </summary>
    private static float GetCargoCost(AircraftDefinition aircraft, Loadout? loadout)
    {
        HardpointSet[]? sets = aircraft.unitPrefab != null
            ? aircraft.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets
            : null;
        if (loadout == null || sets == null)
        {
            return 0f;
        }

        float cost = 0f;
        for (int i = 0; i < loadout.weapons.Count && i < sets.Length; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount != null)
            {
                cost += sets[i].hardpoints.Count * GetMountCargoCost(mount);
            }
        }

        return cost;
    }

    /// <summary>A SAM site's own runs stay free: the player never picked that load.</summary>
    private static bool IsSamSiteRun(string supportSummary)
    {
        return supportSummary.IndexOf("SAM-site", StringComparison.Ordinal) >= 0;
    }

    private static float GetMountCargoCost(WeaponMount mount)
    {
        UnitDefinition[] cargo = GetMountCargo(mount);
        float cost = 0f;
        for (int i = 0; i < cargo.Length; i++)
        {
            cost += GetCargoUnitCost(cargo[i]);
        }

        return cost;
    }

    private static UnitDefinition[] GetMountCargo(WeaponMount mount)
    {
        // The window asks every frame, and a prefab's cargo never changes.
        if (!MountCargo.TryGetValue(mount, out UnitDefinition[] cargo))
        {
            List<UnitDefinition> found = new();
            if (IsRuntimeCargoMount(mount) && mount.prefab != null)
            {
                foreach (MountedCargo item in mount.prefab.GetComponentsInChildren<MountedCargo>(true))
                {
                    if (item.cargo?.unitPrefab != null)
                    {
                        found.Add(item.cargo);
                    }
                }
            }

            cargo = found.ToArray();
            MountCargo[mount] = cargo;
        }

        return cargo;
    }

    /// <summary>The profile's price wins for every load, whatever it would cost by default.</summary>
    private static float GetCargoUnitCost(UnitDefinition cargo)
    {
        return CommanderPriceProfile.TryGetUnitPrice(cargo.unitName, out float price) ? price : GetDefaultCargoUnitCost(cargo);
    }

    private static float GetRadarTruckPrice(VehicleDefinition cargo)
    {
        if (!CommanderGameAccess.IsTrailerVehicleDefinition(cargo))
        {
            return CommanderEconomyService.PriceOf(cargo);
        }

        // The cargo is the trailer the depot does not sell, priced at its authored value.
        if (radarTruck == null)
        {
            foreach (VehicleDefinition definition in Resources.FindObjectsOfTypeAll<VehicleDefinition>())
            {
                if (definition != null && definition.unitName == "HLT Radar Truck")
                {
                    radarTruck = definition;
                    break;
                }
            }
        }

        return CommanderEconomyService.PriceOf(radarTruck != null ? radarTruck : cargo);
    }

    private static int ParseFoundationSiteId(string supportSummary)
    {
        if (string.IsNullOrWhiteSpace(supportSummary)
            || !supportSummary.StartsWith(
                SamSiteFoundationSupportPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(
            supportSummary.Substring(SamSiteFoundationSupportPrefix.Length),
            out int siteId)
            ? siteId
            : -1;
    }

    private static int ParseJacknifeSiteId(string supportSummary)
    {
        if (string.IsNullOrWhiteSpace(supportSummary)
            || !supportSummary.StartsWith(
                SamSiteJacknifeSupportPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(
            supportSummary.Substring(SamSiteJacknifeSupportPrefix.Length),
            out int siteId)
            ? siteId
            : -1;
    }

    private static int ParseCargoSiteId(string supportSummary)
    {
        if (string.IsNullOrWhiteSpace(supportSummary)
            || !supportSummary.StartsWith(
                SamSiteCargoSupportPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(
            supportSummary.Substring(SamSiteCargoSupportPrefix.Length),
            out int siteId)
            ? siteId
            : -1;
    }

    private static bool SupportSummaryMatchesSite(string supportSummary, int siteId)
    {
        return ParseFoundationSiteId(supportSummary) == siteId
            || ParseCargoSiteId(supportSummary) == siteId
            || ParseJacknifeSiteId(supportSummary) == siteId;
    }

    private static void NotifySamMissionFailed(string supportSummary)
    {
        CommanderSamSiteService.NotifySupplyMissionFailed(
            ParseFoundationSiteId(supportSummary),
            ParseCargoSiteId(supportSummary),
            ParseJacknifeSiteId(supportSummary));
    }

    private static bool RequiresProtectedSamAirbase(string supportSummary)
    {
        return string.Equals(
                supportSummary,
                SamSiteCargoSupportSummary,
                StringComparison.OrdinalIgnoreCase)
            || ParseCargoSiteId(supportSummary) >= 0
            || ParseFoundationSiteId(supportSummary) >= 0
            || ParseJacknifeSiteId(supportSummary) >= 0;
    }

    private static bool IsJacknifeUnit(Unit unit)
    {
        string identity =
            $"{unit.unitName} {unit.definition?.unitName} {unit.definition?.code} {unit.definition?.jsonKey}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || unit.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool IsSamSupplyAirbaseSafe(Airbase airbase, GlobalPosition sitePosition)
    {
        Transform sourceTransform = airbase.center != null ? airbase.center : airbase.transform;
        GlobalPosition sourcePosition = sourceTransform.GlobalPosition();
        if (CommanderSamSiteAnalyzerService.TryEvaluateLogisticsRisk(
            sourcePosition,
            sitePosition,
            null,
            out float influenceRisk,
            out _))
        {
            return influenceRisk <= 0.62f;
        }

        GlobalPosition nearestEnemyPosition = default;
        float nearestEnemyDistance = float.MaxValue;

        foreach (FactionHQ candidateHq in FactionRegistry.GetAllHQs())
        {
            if (candidateHq == null || DynamicMap.GetFactionMode(candidateHq) != FactionMode.Enemy)
            {
                continue;
            }

            foreach (Airbase enemyAirbase in candidateHq.GetAirbases())
            {
                if (enemyAirbase == null || enemyAirbase.disabled)
                {
                    continue;
                }

                Transform enemyTransform = enemyAirbase.center != null
                    ? enemyAirbase.center
                    : enemyAirbase.transform;
                GlobalPosition enemyPosition = enemyTransform.GlobalPosition();
                float sourceDistance = HorizontalSquareDistance(sourcePosition, enemyPosition);
                if (sourceDistance < nearestEnemyDistance)
                {
                    nearestEnemyDistance = sourceDistance;
                    nearestEnemyPosition = enemyPosition;
                }
            }
        }

        return nearestEnemyDistance == float.MaxValue
            || nearestEnemyDistance >= HorizontalSquareDistance(sitePosition, nearestEnemyPosition);
    }

    private static float HorizontalSquareDistance(GlobalPosition left, GlobalPosition right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return (x * x) + (z * z);
    }

    private static bool CanHostSpawn(out FactionHQ? hq, out string error)
    {
        hq = null;
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            error = "Supply aircraft can only be spawned by the host.";
            return false;
        }

        hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            error = "No local faction HQ is available.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsAvailableAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        return airbase != null
            && !airbase.disabled
            && airbase.CurrentHQ == hq
            && airbase.CanSpawnAircraft(definition);
    }

    private static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || airbase.CurrentHQ != hq)
        {
            return false;
        }

        List<AircraftDefinition> availableAircraft = airbase.GetAvailableAircraft();
        return availableAircraft != null && availableAircraft.Contains(definition);
    }

    private static string GetAirbaseLabel(Airbase airbase)
    {
        if (airbase.SavedAirbase != null)
        {
            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.DisplayName))
            {
                return airbase.SavedAirbase.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.UniqueName))
            {
                return airbase.SavedAirbase.UniqueName;
            }
        }

        return airbase.name;
    }

    private static Loadout CloneLoadout(Loadout source)
    {
        return new Loadout
        {
            weapons = source.weapons != null ? new List<WeaponMount>(source.weapons) : new List<WeaponMount>(),
        };
    }

    /// <summary>
    /// Names every cargo-capable airframe the mission has, with its pilot types and which half of
    /// the feature will fly it. `Pilot.pilotType` is asset data on the prefab, so for a modded
    /// transport there is no other way to find out whether the mod can steer it — and a run that
    /// spawns and then sits in the hangar looks identical whichever the reason.
    /// </summary>
    private void LogCargoRosterOnce()
    {
        if (cargoRosterLogged || aircraftOptions.Count == 0)
        {
            return;
        }

        cargoRosterLogged = true;
        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            CargoAircraftOption option = aircraftOptions[i];
            Aircraft? prefab = option.Definition.unitPrefab != null
                ? option.Definition.unitPrefab.GetComponent<Aircraft>()
                : null;
            CommanderPlugin.Log.LogInfo(
                $"Cargo roster: {option.Label} pilots {(prefab != null ? DescribePilotTypes(prefab) : "none")}, "
                    + $"{(option.Rotary ? "rotary (lands at the LZ)" : "fixed-wing (airdrop only)")}, "
                    + $"{option.CargoSlots.Count} cargo bay(s), value {CommanderEconomyService.PriceOf(option.Definition)}.");
            for (int slot = 0; slot < option.CargoSlots.Count; slot++)
            {
                foreach (WeaponMount mount in option.CargoSlots[slot].Mounts)
                {
                    CommanderPlugin.Log.LogInfo($"Cargo price: {GetCargoLabel(mount, string.Empty)} {GetMountCargoCost(mount):0.#} per hardpoint.");
                }
            }
        }
    }

    private void BindPendingTerrainAutopilots()
    {
        if (pendingTerrainAutopilotBindings.Count == 0)
        {
            return;
        }

        List<Aircraft>? completed = null;
        foreach (Aircraft aircraft in pendingTerrainAutopilotBindings)
        {
            if (aircraft == null
                || aircraft.disabled
                || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
                || !mission.HighTerrainClearance)
            {
                completed ??= new List<Aircraft>();
                completed.Add(aircraft!);
                continue;
            }

            if (aircraft.autopilot == null)
            {
                continue;
            }

            TryBindTerrainAutopilot(aircraft, mission);
            completed ??= new List<Aircraft>();
            completed.Add(aircraft);
        }

        if (completed == null)
        {
            return;
        }

        for (int i = 0; i < completed.Count; i++)
        {
            pendingTerrainAutopilotBindings.Remove(completed[i]);
        }
    }

    private bool TryBindTerrainAutopilot(Aircraft aircraft, CargoMission mission)
    {
        Autopilot? autopilot = aircraft.autopilot;
        if (autopilot == null)
        {
            return false;
        }

        bool newlyBound = !assignedAutopilotAircraft.ContainsKey(autopilot);
        terrainClearanceAutopilots[autopilot] = mission.TerrainClearanceMeters;
        assignedAutopilotAircraft[autopilot] = aircraft;
        if (newlyBound)
        {
            CommanderPlugin.Log.LogInfo(
                $"Supply terrain profile bound: aircraft={CommanderGameAccess.GetUnitLabel(aircraft)}, "
                + $"clearance={mission.TerrainClearanceMeters:0}m, foundation={mission.FoundationSiteId >= 0}, "
                + $"steepLanding={mission.SteepLanding}, routeWaypoints={mission.ApproachRoute.Count}.");
        }

        return true;
    }

    /// <summary>Can launch this airframe right now: a hangar is open and no run of ours is still
    /// waiting to come out of this field.</summary>
    private bool IsFreeAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        return IsAvailableAirbase(airbase, hq, definition) && !HasPendingSpawnAt(airbase!);
    }

    private bool HasPendingSpawnAt(Airbase airbase)
    {
        for (int i = 0; i < pendingAircraftSpawns.Count; i++)
        {
            if (ReferenceEquals(pendingAircraftSpawns[i].OriginAirbase, airbase))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshAirbaseOptions()
    {
        Airbase? previousSelection = SelectedAirbase;
        airbaseOptions.Clear();

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (hq == null || aircraft == null)
        {
            SelectedAirbase = null;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            // Only fields that can launch it now (user's rule, 2026-11-06): a busy one listed as WAIT
            // took the order and then sat on it.
            if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition)
                || !IsFreeAirbase(airbase, hq, aircraft.Definition))
            {
                continue;
            }

            Transform positionTransform = airbase.center != null ? airbase.center : airbase.transform;
            float distance = Vector3.Distance(cameraPosition, positionTransform.position);
            airbaseOptions.Add(new AirbaseOption(airbase, GetAirbaseLabel(airbase), distance, true));
        }

        airbaseOptions.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        SelectedAirbase = null;
        for (int i = 0; i < airbaseOptions.Count; i++)
        {
            if (ReferenceEquals(airbaseOptions[i].Airbase, previousSelection))
            {
                SelectedAirbase = previousSelection;
                break;
            }
        }

        if (SelectedAirbase == null && airbaseOptions.Count > 0)
        {
            SelectedAirbase = airbaseOptions[0].Airbase;
        }
    }

    private void CancelTargetSelection(bool showStatus)
    {
        if (pendingTargetSelection == null)
        {
            return;
        }

        pendingTargetSelection = null;
        if (showStatus)
        {
            SetStatus("Cargo run target selection cancelled.");
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }
}
