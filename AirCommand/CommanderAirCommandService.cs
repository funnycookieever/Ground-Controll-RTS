using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Altitude slider granularity for the mission tab and the AIR MISSIONS window.</summary>
    internal const float TargetAltitudeStepMeters = 250f;

    /// <summary>The altitude slider's top notch (12 km); LOW LEVEL is notch -1 and STANDARD notch 0.</summary>
    internal const int MaxAltitudeNotch = 48;

    private const float PendingSpawnTimeoutSeconds = 45f;
    private const float StatusDurationSeconds = 6f;
    private const float MissionPruneIntervalSeconds = 2f;
    private const float AircraftWaypointRadiusMeters = 1500f;
    private const float MaxTargetAltitudeMeters = MaxAltitudeNotch * TargetAltitudeStepMeters;

    /// <summary>The TARGET ALTITUDE sentinel for "hug the ground", below STANDARD (0).</summary>
    private const float LowLevelAltitude = -1f;

    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderMapClickTracker mapClickTracker = new();
    private readonly List<AirMissionOption> options = new();
    private readonly List<WeaponMount> weaponOptions = new();

    /// <summary>The cargo and troop pods this airframe will take, the subset of
    /// <see cref="weaponOptions"/> the CARGO row in the ADVANCED tab picks from.</summary>
    private readonly List<WeaponMount> cargoOptions = new();
    private readonly List<AirbaseOption> airbases = new();
    private readonly Dictionary<Aircraft, AirMission> missions = new();

    /// <summary>Heights set on AUTOPILOT aircraft, which have no mission to hold one. AUTO is no entry.</summary>
    private readonly Dictionary<Aircraft, float> autopilotAltitudes = new();

    /// <summary>The loadout each Air Command airframe launched with, put back on it at every resupply.
    /// Kept apart from <see cref="missions"/> because AUTOPILOT has no mission and an order released
    /// to autopilot drops its mission, and both still land and rearm.</summary>
    private readonly Dictionary<Aircraft, Loadout> launchLoadouts = new();
    private readonly List<Aircraft> staleAircraft = new();

    /// <summary>Launches waiting for their airframe to register. A list, not a slot: a carrier's
    /// hangar can hold its spawn for a while, and one slot refused every other launch on the map
    /// until that airframe appeared.</summary>
    private readonly List<PendingAircraftSpawn> pendingAircraftSpawns = new();
    private PendingAreaSelection? pendingAreaSelection;
    private Aircraft? pendingMissionRelocation;
    private Aircraft? pendingAdoption;

    private float nextAirbaseRefreshAt;
    private float nextMissionPruneAt;
    private float statusUntil;
    private string statusText = string.Empty;
    private bool openedMapForOrder;
    private float selectedMunitionsCost;
    private float nextMunitionsCostAt;
    private Rect areaSelectionBlockingRect;
    private Rect areaSelectionSecondaryBlockingRect;
    private Aircraft? selectedMissionAircraft;

    internal CommanderAirCommandService(CommanderTacticalMapService tacticalMapService)
    {
        this.tacticalMapService = tacticalMapService;
        if (Enum.TryParse(CommanderSettings.AirCommandMode, out AirCommandMode savedMode))
        {
            // ARAD has no button of its own any more: RADAR fires the anti-radiation missiles.
            SelectedMode = savedMode == AirCommandMode.Arad ? AirCommandMode.AwacsJammer : savedMode;
        }

        if (Enum.TryParse(CommanderSettings.AirLoadoutBalance, out LoadoutBalance savedBalance))
        {
            SelectedLoadoutBalance = savedBalance;
        }

        SelectedTargetAltitude = CommanderSettings.AirTargetAltitude;
        Instance = this;
        RegisterRemote();
    }

    internal static CommanderAirCommandService? Instance { get; private set; }

    internal static bool TargetOrdnance { get => CommanderSettings.AirGuardTargetOrdnance; set => CommanderSettings.AirGuardTargetOrdnance = value; }

    // Saturation attack is retired: always off, no toggle.
    internal static bool SaturationAttack => false;

    /// <summary>STANDBY is "no mission picked": the AIR window has no button for it.</summary>
    internal AirCommandMode SelectedMode { get; private set; } = AirCommandMode.Standby;

    internal IReadOnlyList<AirMissionOption> Options => options;

    internal IReadOnlyList<AirbaseOption> Airbases => airbases;

    internal IReadOnlyList<WeaponMount> WeaponOptions => weaponOptions;

    internal IReadOnlyList<WeaponMount> CargoOptions => cargoOptions;

    internal int SelectedOptionIndex { get; private set; }

    internal int SelectedAirbaseIndex { get; private set; }

    internal int SelectedPrimaryWeaponIndex { get; private set; } = -1;

    internal int SelectedSecondaryWeaponIndex { get; private set; } = -1;

    internal int SelectedCargoWeaponIndex { get; private set; } = -1;

    internal LoadoutBalance SelectedLoadoutBalance { get; private set; } = LoadoutBalance.Primary;

    internal float SelectedTargetAltitude { get; private set; }

    internal bool IncludeInternalCannons
    {
        get => CommanderSettings.AirIncludeInternalCannons;
        set
        {
            if (CommanderSettings.AirIncludeInternalCannons == value)
            {
                return;
            }

            CommanderSettings.AirIncludeInternalCannons = value;

            // The role presets are generated with or without the cannons, so they are rebuilt too;
            // in the editor this also re-lays the pylons.
            RefreshPresets();
        }
    }

    internal bool AwaitingAreaSelection => pendingAreaSelection != null
        || pendingMissionRelocation != null
        || pendingAdoption != null;

    internal float PendingMissionRadius => pendingAreaSelection != null
        ? GetMissionRadius(pendingAreaSelection.Option.Mode)
        : pendingMissionRelocation != null && missions.TryGetValue(pendingMissionRelocation, out AirMission relocationMission)
            ? relocationMission.Radius
            : 0f;

    internal int ActiveMissionCount
    {
        get
        {
            FactionHQ? hq = CommanderGameAccess.GetLocalHq();
            int count = 0;
            foreach (Aircraft aircraft in missions.Keys)
            {
                if (CommanderGameAccess.IsFriendlyUnit(aircraft, hq))
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal bool IsUiVisible { get; private set; }

    /// <summary>
    /// Gets the hangar or landing pad the window is arming, rather than a whole airbase: the aircraft
    /// list is that hangar's own <c>availableAircraft</c> and the spawn goes out of its doors. That
    /// single property is the whole of "helicopters only at the pad" — the base game already files each
    /// airframe under the hangars that can hold it.
    /// </summary>
    internal Hangar? HangarFilter { get; private set; }

    internal bool CanLaunchSelected => SelectedOption != null
        && HasUsableLoadout(SelectedOption)
        && SelectedAirbase != null;

    /// <summary>What pressing the launch button will actually take out of the treasury.</summary>
    internal float SelectedLaunchCost
    {
        get
        {
            AirMissionOption? option = SelectedOption;
            FactionHQ? hq = CommanderGameAccess.GetLocalHq();
            if (option == null || hq == null)
            {
                return 0f;
            }

            // BuildLoadout allocates and OnGUI asks every frame, so the munitions price is refreshed
            // a few times a second like the launch warning.
            if (Time.unscaledTime >= nextMunitionsCostAt)
            {
                nextMunitionsCostAt = Time.unscaledTime + 0.25f;
                selectedMunitionsCost = GetMunitionsCost(option.Definition, option.BuildLoadout());
            }

            float airframe = hq.GetUnitSupply(option.Definition) > 0 ? 0f : CommanderEconomyService.PriceOf(option.Definition);
            return airframe + selectedMunitionsCost;
        }
    }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    internal WeaponMount? SelectedPrimaryWeapon => SelectedPrimaryWeaponIndex >= 0 && SelectedPrimaryWeaponIndex < weaponOptions.Count
        ? weaponOptions[SelectedPrimaryWeaponIndex]
        : null;

    internal WeaponMount? SelectedSecondaryWeapon => SelectedSecondaryWeaponIndex >= 0 && SelectedSecondaryWeaponIndex < weaponOptions.Count
        ? weaponOptions[SelectedSecondaryWeaponIndex]
        : null;

    internal WeaponMount? SelectedCargoWeapon => SelectedCargoWeaponIndex >= 0 && SelectedCargoWeaponIndex < cargoOptions.Count
        ? cargoOptions[SelectedCargoWeaponIndex]
        : null;

    internal AirMissionOption? SelectedOption => options.Count == 0
        ? null
        : options[Mathf.Clamp(SelectedOptionIndex, 0, options.Count - 1)];

    internal AirbaseOption? SelectedAirbase => airbases.Count == 0
        ? null
        : airbases[Mathf.Clamp(SelectedAirbaseIndex, 0, airbases.Count - 1)];

    internal float SelectedMissionRadiusKm => GetMissionRadius(SelectedMode) / 1000f;

    public void Activate()
    {
        nextMissionPruneAt = CommanderScheduler.Stagger("air-command.prune", MissionPruneIntervalSeconds, 0.7f);
        nextAirbaseRefreshAt = Time.unscaledTime;
        RefreshMissionMapVisuals();
    }

    public void Deactivate()
    {
        IsUiVisible = false;
        CancelAreaSelection(showStatus: false);
        ClearMissionMapVisuals();
    }

    public void ResetSession()
    {
        ResetDepartures();
        IsUiVisible = false;
        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        pendingAircraftSpawns.Clear();
        options.Clear();
        weaponOptions.Clear();
        cargoOptions.Clear();
        airbases.Clear();
        ClearMissionMapVisuals();
        missions.Clear();
        autopilotAltitudes.Clear();
        NextScoutAt.Clear();
        AutopilotHuntPoints.Clear();
        CommanderAirTerrainGuard.ResetSession();
        CommanderHeloRoadRoute.ResetSession();
        launchLoadouts.Clear();
        FailedTakeoffs.Clear();
        UntaskedRescues.Clear();
        UntaskedRescued.Clear();
        AbandonLogged.Clear();
        ResetRunwayOverruns();
        ResetServiceStands();
        staleAircraft.Clear();
        mapClickTracker.Reset();
        SelectedPrimaryWeaponIndex = -1;
        SelectedSecondaryWeaponIndex = -1;
        SelectedCargoWeaponIndex = -1;
        presets.Clear();
        SelectedPresetIndex = -1;
        HangarFilter = null;
        statusText = string.Empty;
    }

    public void TickActive()
    {
        if (AwaitingAreaSelection)
        {
            if (CommanderGameInput.CancelDown)
            {
                CancelAreaSelection(showStatus: true);
            }
            else
            {
                TryHandleTacticalMapClick();
            }
        }

        if (IsUiVisible && Time.unscaledTime >= nextAirbaseRefreshAt)
        {
            nextAirbaseRefreshAt = Time.unscaledTime + 0.5f;
            RefreshAirbases();
        }
    }

    public void TickPersistent()
    {
        // A guest's missions are the host's, copied here to be drawn. Flying them happens there.
        if (CommanderNetwork.IsGuest)
        {
            if (CommanderScheduler.IsDue(ref nextMissionPruneAt, MissionPruneIntervalSeconds))
            {
                PruneMissions();
                RefreshMissionMapVisuals();
            }

            return;
        }

        for (int i = pendingAircraftSpawns.Count - 1; i >= 0; i--)
        {
            PendingAircraftSpawn timedOutSpawn = pendingAircraftSpawns[i];
            if (Time.unscaledTime <= timedOutSpawn.ExpiresAt)
            {
                continue;
            }

            pendingAircraftSpawns.RemoveAt(i);
            if (timedOutSpawn.PurchasedWithFunds)
            {
                timedOutSpawn.Hq.AddFunds(timedOutSpawn.PurchaseCost);
            }
            else
            {
                timedOutSpawn.Hq.ModifyUnitSupply(timedOutSpawn.Option.Definition, 1);
            }

            SetStatus($"Aircraft spawn timed out for {GetAircraftLabel(timedOutSpawn.Option.Definition)}; cost restored.");
        }

        if (CommanderScheduler.IsDue(ref nextMissionPruneAt, MissionPruneIntervalSeconds))
        {
            PruneMissions();
            RefreshMissionMapVisuals();

            // Rescues first: an airframe the base-game AI abandoned on the strip has to be flying
            // again before anything else decides what it should be doing.
            ProcessGroundRescues();
            TickLandingOrders();
            ProcessResupplyRequests();
            ProcessReturningMissions();
            WatchCommandedRoutes();
        }
    }

    /// <summary>
    /// Whether this mode needs the player to place a circle on the map. AUTOPILOT and STANDBY do
    /// not: one has no restraint at all and the other never leaves the deck.
    /// </summary>
    internal static bool RequiresMissionArea(AirCommandMode mode)
    {
        return mode is not AirCommandMode.Autopilot and not AirCommandMode.Standby;
    }

    /// <summary>The AIR window's four AREA focuses: the old HYBRID, AIR SUP, CAS and STRIKE modes.</summary>
    internal static bool IsAreaMode(AirCommandMode mode)
    {
        return mode is AirCommandMode.Hybrid
            or AirCommandMode.AirGuard
            or AirCommandMode.Cas
            or AirCommandMode.StrategicStrike;
    }

    /// <summary>RADAR took ARAD's anti-radiation missiles, and its SATURATION ATTACK with them.</summary>
    internal static bool UsesSaturationAttack(AirCommandMode mode)
    {
        return mode is AirCommandMode.Arad or AirCommandMode.AwacsJammer;
    }

    /// <summary>
    /// 0 means "whatever the base-game AI wants", <see cref="LowLevelAltitude"/> means "as low as the
    /// airframe safely flies", anything else is a hard floor it holds.
    /// </summary>
    internal static float ClampTargetAltitude(float altitude)
    {
        return altitude < 0f ? LowLevelAltitude
            : altitude == 0f ? 0f
            : Mathf.Clamp(altitude, TargetAltitudeStepMeters, MaxTargetAltitudeMeters);
    }

    /// <summary>One click of an altitude stepper: LOW LEVEL, STANDARD, then 250 m steps.</summary>
    internal static float StepAltitude(float current, float deltaMeters)
    {
        // LOW LEVEL sits one step below STANDARD; adding the delta to the -1 sentinel would skip it.
        // A slider drag can move several steps at once, so the first step up lands on STANDARD.
        if (current < 0f)
        {
            return deltaMeters > 0f ? ClampTargetAltitude(Mathf.Max(0f, deltaMeters - TargetAltitudeStepMeters)) : LowLevelAltitude;
        }

        return ClampTargetAltitude(current + deltaMeters);
    }

    /// <summary>Where an altitude sits on the slider, in <see cref="StepAltitude"/> steps.</summary>
    internal static int AltitudeNotch(float altitude)
    {
        return altitude < 0f ? -1 : Mathf.RoundToInt(altitude / TargetAltitudeStepMeters);
    }

    /// <summary>
    /// Where a notch sits along the slider, 0 to 1. The travel is a square curve because 50 evenly
    /// spaced notches left about 2 px each on a 100 px slider, and the low heights were unpickable.
    /// </summary>
    internal static float AltitudeSliderPosition(int notch)
    {
        return Mathf.Sqrt((notch + 1f) / (MaxAltitudeNotch + 1f));
    }

    /// <summary>The notch at a slider position, the inverse of <see cref="AltitudeSliderPosition"/>.</summary>
    internal static int AltitudeNotchAt(float position)
    {
        return Mathf.RoundToInt(position * position * (MaxAltitudeNotch + 1f)) - 1;
    }

    internal static string FormatTargetAltitude(float altitude)
    {
        return altitude < 0f ? "LOW LEVEL" : altitude == 0f ? "AUTO" : $"{altitude:0} m";
    }

    /// <summary>
    /// Every mission that has an area also has a cruise altitude. It is written into
    /// <c>AIPilotCombatModes.targetHeight</c> after the base game's <c>ManageAltitude</c> pass, so it
    /// is a cruise floor, not a leash: attack runs still descend and terrain following and threat
    /// reactions still lower it.
    /// </summary>
    internal static bool SupportsTargetAltitude(AirCommandMode mode)
    {
        return RequiresMissionArea(mode);
    }

    /// <summary>
    /// The airfield a set of doors belongs to. Normally that is <c>Hangar.parentAirbase</c>; a pad
    /// the commander built and that has not been filed under an airbase yet falls back to the
    /// nearest field its faction holds, because everything downstream of a launch — the supply
    /// draw, the departure row, the airborne fallback — needs an <c>Airbase</c> to hang off.
    /// </summary>
    internal static Airbase? ResolveHangarField(Hangar? hangar)
    {
        if (hangar == null || hangar.attachedUnit == null)
        {
            return null;
        }

        if (hangar.parentAirbase != null)
        {
            return hangar.parentAirbase;
        }

        FactionHQ? hq = hangar.attachedUnit.NetworkHQ;
        if (hq == null)
        {
            return null;
        }

        Airbase? nearest = null;
        float bestDistance = float.MaxValue;
        Vector3 position = hangar.attachedUnit.transform.position;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = (airbase.center.position - position).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = airbase;
            }
        }

        return nearest;
    }

    internal static string GetModeLabel(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => "RADAR",
            AirCommandMode.Cas => "AREA: GROUND",
            AirCommandMode.AirGuard => "AREA: AIR",
            AirCommandMode.Arad => "ARAD",
            AirCommandMode.StrategicStrike => "AREA: STRIKE",
            AirCommandMode.Hybrid => "AREA",
            AirCommandMode.Autopilot => "AUTOPILOT",
            AirCommandMode.Standby => "STANDBY",
            _ => mode.ToString().ToUpperInvariant(),
        };
    }

    /// <summary>The label on the mission button, used wherever there is room for only a word.</summary>
    internal static string GetModeShortLabel(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.AirGuard => "AREA AIR",
            AirCommandMode.Cas => "AREA GROUND",
            AirCommandMode.StrategicStrike => "AREA STRIKE",
            _ => GetModeLabel(mode),
        };
    }

    /// <summary>The one line a player reads before committing: price, balance, balance after.</summary>
    internal string GetLaunchCostLabel()
    {
        AirMissionOption? option = SelectedOption;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (option == null || hq == null)
        {
            return "COST  -";
        }

        if (CommanderDraftService.IsDraftMission)
        {
            int held = hq.GetUnitSupply(option.Definition);
            return held > 0
                ? $"DRAFTED AIR WING  |  {held} IN STOCK"
                : "DRAFTED AIR WING  |  NONE LEFT OF THIS TYPE";
        }

        float funds = hq.factionFunds;
        float cost = SelectedLaunchCost;
        if (cost <= 0f)
        {
            return $"COST  FREE (from stock, {hq.GetUnitSupply(option.Definition)} held)"
                + $"  |  FUNDS {CommanderEconomyService.FundsLabel(funds)}";
        }

        string breakdown = selectedMunitionsCost > 0f
            ? $" (munitions {CommanderEconomyService.FundsLabel(selectedMunitionsCost)})"
            : string.Empty;
        return $"COST  {CommanderEconomyService.FundsLabel(cost)}{breakdown}"
            + $"  |  FUNDS {CommanderEconomyService.FundsLabel(funds)}"
            + $" -> {CommanderEconomyService.FundsLabel(funds - cost)}";
    }

    internal void SetUiVisible(bool visible)
    {
        IsUiVisible = visible;
        if (visible)
        {
            // Each time the window opens the mission picks the preset again until a click says otherwise.
            presetChosenByPlayer = false;
            RefreshOptions();
        }
    }

    /// <summary>
    /// Changes what the sortie is for. It deliberately keeps the aircraft, the loadout and the
    /// departure field: the mission type is the second question now, not the first, and wiping the
    /// answers to the other three every time it changed is what made the old panel feel like a
    /// form to fill in twice.
    /// </summary>
    internal void SelectMode(AirCommandMode mode)
    {
        if (SelectedMode == mode && options.Count > 0)
        {
            return;
        }

        SelectedMode = mode;
        CommanderSettings.AirCommandMode = mode.ToString();
        RefreshOptions();
    }

    internal void SelectLoadoutBalance(LoadoutBalance balance)
    {
        if (SelectedLoadoutBalance == balance)
        {
            return;
        }

        SelectedLoadoutBalance = balance;
        CommanderSettings.AirLoadoutBalance = balance.ToString();
        ApplySelectedWeapons();
    }

    internal void CancelAreaSelection()
    {
        CancelAreaSelection(showStatus: true);
    }

    internal void SetTargetAltitude(float altitude)
    {
        SelectedTargetAltitude = SupportsTargetAltitude(SelectedMode) ? ClampTargetAltitude(altitude) : 0f;
        CommanderSettings.AirTargetAltitude = SelectedTargetAltitude;
    }

    internal void StepTargetAltitude(float deltaMeters)
    {
        SetTargetAltitude(StepAltitude(SelectedTargetAltitude, deltaMeters));
    }

    internal bool SupportsTargetAltitude()
    {
        return SupportsTargetAltitude(SelectedMode);
    }

    internal void SetAreaSelectionBlockingRects(Rect primary, Rect secondary)
    {
        areaSelectionBlockingRect = primary;
        areaSelectionSecondaryBlockingRect = secondary;
    }

    internal float GetMissionAltitude(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) ? mission.TargetAltitude
            : autopilotAltitudes.TryGetValue(aircraft, out float altitude) ? altitude
            : 0f;
    }

    /// <summary>Re-flies a package that is already up at a different height.</summary>
    internal void StepMissionAltitude(Aircraft aircraft, float deltaMeters)
    {
        // Stepped on a guest too: autopilot heights are not synced, and its slider reads this copy.
        missions.TryGetValue(aircraft, out AirMission? mission);
        if (mission == null)
        {
            float stepped = StepAltitude(GetMissionAltitude(aircraft), deltaMeters);
            if (stepped == 0f)
            {
                autopilotAltitudes.Remove(aircraft);
            }
            else
            {
                autopilotAltitudes[aircraft] = stepped;
            }
        }

        if (ForwardAir("air.alt", aircraft, CommanderNetwork.Num(deltaMeters)))
        {
            return;
        }

        if (mission != null)
        {
            mission.TargetAltitude = StepAltitude(mission.TargetAltitude, deltaMeters);
        }

        float altitude = GetMissionAltitude(aircraft);
        SetStatus(altitude == 0f
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)} altitude back to AUTO."
            : $"{CommanderGameAccess.GetUnitLabel(aircraft)} altitude set to {FormatTargetAltitude(altitude)}.");
    }

    /// <summary>
    /// Picking the aeroplane is step one, so it rebuilds everything that depends on it: the weapons
    /// it can actually carry, its presets, and the fields that will take it.
    /// </summary>
    internal void SelectOption(int index)
    {
        if (index < 0 || index >= options.Count)
        {
            return;
        }

        string? primaryKey = SelectedPrimaryWeaponKey;
        string? secondaryKey = SelectedSecondaryWeaponKey;
        SelectedOptionIndex = index;
        RebuildWeaponSelection(primaryKey, secondaryKey);
        RefreshPresets();
        RefreshAirbases();
    }

    /// <summary>
    /// Points the window at one hangar or landing pad instead of the whole air force. Passing null
    /// puts it back to the full list.
    /// </summary>
    internal void SetHangarFilter(Hangar? hangar)
    {
        HangarFilter = hangar;
        SelectedOptionIndex = 0;
        SelectedAirbaseIndex = 0;
        RefreshOptions();
    }

    internal string GetHangarFilterLabel()
    {
        Hangar? hangar = HangarFilter;
        if (hangar == null || hangar.attachedUnit == null)
        {
            return string.Empty;
        }

        Airbase? airbase = ResolveHangarField(hangar);
        string field = airbase != null ? CommanderCaptureService.GetAirbaseLabel(airbase) : "FIELD";
        return $"{CommanderGameAccess.GetUnitLabel(hangar.attachedUnit)} @ {field}";
    }

    internal void SelectAirbase(int index)
    {
        if (index >= 0 && index < airbases.Count)
        {
            SelectedAirbaseIndex = index;
        }
    }

    internal bool TrySelectAirbaseFromMap(Airbase? airbase)
    {
        if (!IsUiVisible || airbase == null)
        {
            return false;
        }

        for (int i = 0; i < airbases.Count; i++)
        {
            if (ReferenceEquals(airbases[i].Airbase, airbase))
            {
                SelectedAirbaseIndex = i;
                SetStatus($"Departure airbase selected: {airbases[i].Label}.");
                return true;
            }
        }

        SetStatus("This airbase cannot currently spawn the selected aircraft.");
        return false;
    }

    internal bool IsSelectableAirbase(Airbase? airbase)
    {
        if (!IsUiVisible || airbase == null)
        {
            return false;
        }

        for (int i = 0; i < airbases.Count; i++)
        {
            if (ReferenceEquals(airbases[i].Airbase, airbase))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsSelectedAirbase(Airbase? airbase)
    {
        return airbase != null
            && SelectedAirbase is AirbaseOption selected
            && ReferenceEquals(selected.Airbase, airbase);
    }

    internal void StepMissionRadius(float deltaKm)
    {
        SetMissionRadius(SelectedMode, Mathf.Clamp(SelectedMissionRadiusKm + deltaKm, 5f, 150f));
    }

    internal void CollectMissionAircraft(List<Aircraft> aircraft)
    {
        aircraft.Clear();

        // The enemy commander tasks its own airframes through this same dictionary, so the player's
        // mission list has to filter by faction or it offers an RTB button for their bombers.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (Aircraft unit in missions.Keys)
        {
            if (unit != null && !unit.disabled && CommanderGameAccess.IsFriendlyUnit(unit, localHq))
            {
                aircraft.Add(unit);
            }
        }

        aircraft.Sort((left, right) => string.Compare(
            CommanderGameAccess.GetUnitLabel(left), CommanderGameAccess.GetUnitLabel(right), StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsMissionAircraftSelected(Aircraft aircraft)
    {
        return ReferenceEquals(selectedMissionAircraft, aircraft);
    }

    internal void ToggleMissionAircraft(Aircraft aircraft)
    {
        selectedMissionAircraft = ReferenceEquals(selectedMissionAircraft, aircraft) ? null : aircraft;
        RefreshMissionMapVisuals();
    }

    internal void ClearMissionAircraftSelection()
    {
        selectedMissionAircraft = null;
        RefreshMissionMapVisuals();
    }

    internal string GetMissionAircraftLabel(Aircraft aircraft)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return CommanderGameAccess.GetUnitLabel(aircraft);
        }

        string state = mission.Intent == LandingIntent.Reserve
            ? "TO RESERVE"
            : mission.Intent == LandingIntent.Hold
            ? mission.Parked ? "ON PAD" : "TO PAD"
            : mission.Parked && mission.Intent == LandingIntent.Resupply
            ? mission.ReportedShortfall ? "GROUNDED" : "SERVICING"
            : mission.LandingBase != null
                ? mission.Intent == LandingIntent.Capture ? "LANDING" : "REARMING"
                : mission.Returning
                    ? "RTB"
                    : "ACTIVE";
        return $"{CommanderGameAccess.GetUnitLabel(aircraft)}\n{GetModeLabel(mission.Mode)}  |  {state}";
    }

    /// <summary>
    /// RTB is a resupply run: the aircraft is routed to the nearest field its faction holds, put
    /// down there, refuelled and rearmed out of a supply depot, and launched back onto its mission.
    /// The bare landing-state switch is the fallback for a faction with no base left to go home to.
    /// </summary>
    internal void RequestReturnToBase(Aircraft aircraft)
    {
        if (ForwardAir("air.rtb", aircraft))
        {
            return;
        }

        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        if (ResupplyAircraft(aircraft))
        {
            return;
        }

        mission.Returning = true;
        IssueReturnToBase(aircraft, mission);
        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} ordered to RTB.");
    }

    internal AirCommandMode? GetMissionMode(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) ? mission.Mode : null;
    }

    internal float GetMissionRadiusKm(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission)
            ? mission.Radius / 1000f
            : 0f;
    }

    internal void StepMissionRadius(Aircraft aircraft, float deltaKm)
    {
        if (ForwardAir("air.radius", aircraft, CommanderNetwork.Num(deltaKm)))
        {
            return;
        }

        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        mission.Radius = Mathf.Clamp((mission.Radius / 1000f) + deltaKm, 5f, 150f) * 1000f;
        DestroyMissionMapVisual(mission);
        if (ReferenceEquals(selectedMissionAircraft, aircraft))
        {
            EnsureMissionMapVisual(mission);
        }

        SetStatus($"{GetModeLabel(mission.Mode)} radius set to {mission.Radius / 1000f:0} km.");
    }

    /// <summary>A preset arms the pylons directly; the editor needs something on at least one of
    /// them. Anything counts, cargo and a hand-set pylon included.</summary>
    private static bool HasUsableLoadout(AirMissionOption option)
    {
        if (option.Preset != null)
        {
            return true;
        }

        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            if (option.HardpointGroups[i].SelectedMount != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends an aircraft home. Rotary airframes carry missions now (the enemy's convoy escort), and
    /// <c>AIPilotLandingState</c> is the **fixed-wing** approach — a helicopter put into it flies a
    /// runway circuit it cannot complete. Every RTB path routes through here, so the pilot-type
    /// branch belongs here rather than at any one caller.
    /// </summary>
    private static void IssueReturnToBase(Aircraft aircraft, AirMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            if (pilot.pilotType is Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing)
            {
                if (pilot.AIHeloLandingState == null)
                {
                    pilot.AIHeloLandingState = new AIHeloLandingState();
                }

                pilot.SwitchState(pilot.AIHeloLandingState);
            }
            else
            {
                if (pilot.AILandingState == null)
                {
                    pilot.AILandingState = new AIPilotLandingState();
                }

                pilot.SwitchState(pilot.AILandingState);
            }

            mission.RtbIssued = true;
            return;
        }
    }

    private static bool KeepsStationInMissionArea(AirCommandMode mode)
    {
        return mode is AirCommandMode.AwacsJammer or AirCommandMode.AirGuard;
    }

    private static bool TargetsRestrictedToMissionArea(AirCommandMode mode)
    {
        return mode is AirCommandMode.Cas
            or AirCommandMode.Arad
            or AirCommandMode.StrategicStrike
            or AirCommandMode.Hybrid;
    }

    private static string GetAircraftLabel(AircraftDefinition definition)
    {
        return !string.IsNullOrWhiteSpace(definition.unitName)
            ? definition.unitName
            : !string.IsNullOrWhiteSpace(definition.code)
                ? definition.code
                : definition.name;
    }

    private static string GetAirbaseName(Airbase airbase)
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

    private void ProcessReturningMissions()
    {
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Value.Returning && !entry.Value.RtbIssued)
            {
                IssueReturnToBase(entry.Key, entry.Value);
            }
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    /// <summary>A sortie that did not launch. Same status line, plus the red banner, because a
    /// refusal that only whispers reads as a dead LAUNCH button.</summary>
    private void RefuseLaunch(string text)
    {
        SetStatus(text);
        CommanderNotice.Fail(text);
    }

    /// <summary>
    /// Logs, once per mission, an aircraft that has spent 90 s flying away from the travel point it was
    /// ordered to, with its pilot state, mode, height and speed.
    /// </summary>
    private void WatchCommandedRoutes()
    {
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            AirMission mission = entry.Value;
            if (aircraft == null
                || aircraft.disabled
                || mission.RouteIndex >= mission.Route.Count
                || mission.LandingIssued
                || aircraft.radarAlt < 5f)
            {
                mission.RecedingSince = -1f;
                continue;
            }

            float distance = FastMath.Distance(mission.Route[mission.RouteIndex], aircraft.GlobalPosition());
            if (distance < 5000f || distance <= mission.RouteDistance)
            {
                mission.RecedingSince = -1f;
            }
            else if (mission.RecedingSince < 0f)
            {
                mission.RecedingSince = Time.time;
            }

            mission.RouteDistance = distance;
            if (mission.RecedeLogged || mission.RecedingSince < 0f || Time.time - mission.RecedingSince < 90f)
            {
                continue;
            }

            mission.RecedeLogged = true;
            Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            CommanderPlugin.Log.LogWarning(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} ({aircraft.NetworkHQ?.faction?.name}) has flown away from its "
                    + $"ordered point for 90 s: {distance / 1000f:0.0} km from point {mission.RouteIndex + 1} of {mission.Route.Count}, "
                    + $"{aircraft.radarAlt:0} m up at {aircraft.speed:0} m/s, mode {mission.Mode}, "
                    + $"pilot state {pilot?.currentState?.GetType().Name ?? "none"}, fuel {aircraft.GetFuelLevel() * 100f:0} %.");
        }
    }
}
