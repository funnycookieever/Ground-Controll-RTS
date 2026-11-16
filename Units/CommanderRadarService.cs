using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderRadarService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderResetSession
{
    /// <summary>Radar SAMs told to let artillery rounds (shells, MLRS rockets) through. Read by
    /// <see cref="CommanderIgnoreArtilleryPatches"/> on the host, where the targeting runs.</summary>
    internal static readonly HashSet<Unit> IgnoreArtilleryUnits = new();

    private const float ControlRangeMeters = 300f;
    private const float RefreshIntervalSeconds = 1f;

    /// <summary>How often the radar-guided launchers are checked for a fire-control link.</summary>
    private const float LauncherCheckIntervalSeconds = 5f;

    private static readonly FieldInfo? FireControlModeField = AccessTools.Field(typeof(FireControl), "targetAcquisitionMode");
    private static readonly FieldInfo? FireControlTurretsField = AccessTools.Field(typeof(FireControl), "turrets");
    private static readonly FieldInfo? TurretModeField = AccessTools.Field(typeof(Turret), "targetAcquisitionMode");
    private static readonly MethodInfo? AircraftRpcToggleRadarMethod = AccessTools.Method(typeof(Aircraft), "RpcToggleRadar");

    /// <summary>Artillery-round answer per missile type, so the targeting hot path asks the prefab once.</summary>
    private static readonly Dictionary<UnitDefinition, bool> ArtilleryRounds = new();

    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<Unit> offlineRadarUnits = new();
    private readonly HashSet<Unit> nearbyRadarUnits = new();
    private readonly HashSet<Unit> nearbyLauncherUnits = new();
    private readonly Dictionary<Unit, Radar[]> radarsByUnit = new();
    private readonly Dictionary<Unit, Turret[]> turretsByUnit = new();
    private readonly Dictionary<Unit, string> hostLauncherLinks = new();

    /// <summary>What each radar-guided launcher was last warned about, so one toast is one shortfall
    /// rather than one every sweep. Cleared when the launcher is linked up.</summary>
    private readonly Dictionary<Unit, string> launcherWarnings = new();

    private FactionHQ? boundHq;
    private Unit? inspectedUnit;
    private RadarState? focusedState;
    private float nextRefreshTime;
    private float nextLauncherCheckTime;

    internal CommanderRadarService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
        CommanderNetwork.OnHost("radar", HostRadar);
        CommanderNetwork.OnHost("ignoreart", HostIgnoreArtillery);
        CommanderNetwork.OnSync(SyncLauncherLinks);
        CommanderNetwork.OnGuest("radar.links", ApplyLauncherLinks);
    }

    internal static CommanderRadarService? Instance { get; private set; }

    public void Activate()
    {
        nextRefreshTime = CommanderScheduler.Stagger("radar.nearby", RefreshIntervalSeconds, 0.4f);
        nextLauncherCheckTime = CommanderScheduler.Stagger("radar.launchers", LauncherCheckIntervalSeconds, 0.6f);
        RefreshBindings();
        SyncFocusedUnit();
    }

    public void Deactivate()
    {
        UnbindHq();
        inspectedUnit = null;
        focusedState = null;
    }

    public void ResetSession()
    {
        UnbindHq();
        inspectedUnit = null;
        focusedState = null;
        offlineRadarUnits.Clear();
        nearbyRadarUnits.Clear();
        nearbyLauncherUnits.Clear();
        launcherWarnings.Clear();
        hostLauncherLinks.Clear();
        IgnoreArtilleryUnits.Clear();
    }

    public void TickActive()
    {
        RefreshBindings();
        SyncFocusedUnit();
        if (CommanderScheduler.IsDue(ref nextLauncherCheckTime, LauncherCheckIntervalSeconds))
        {
            ReviewLauncherLinks();
        }

        if (focusedState == null
            || !focusedState.IsCommandTruck
            || !CommanderScheduler.IsDue(ref nextRefreshTime, RefreshIntervalSeconds))
        {
            return;
        }

        RefreshNearbyCounts(focusedState);
    }

    internal static bool IsUnitRadarOffline(Unit unit)
    {
        return Instance?.offlineRadarUnits.Contains(unit) == true;
    }

    /// <summary>
    /// Why a radar-guided launcher cannot shoot, or empty when it can. This is the game own rule,
    /// not a guess: <c>Turret.SearchForRadar</c> asks <c>FactionHQ.TryGetFireControl</c> for a fire
    /// control within <see cref="ControlRangeMeters"/> and then that truck for a radar, or links
    /// directly to a nearby radar.
    /// <c>FireControl.SearchForRadar</c> asks <c>FactionHQ.TryGetRadar</c> over the same 300 m - which
    /// is why a radar station on the base counts, its <c>Radar</c> is registered with the faction like
    /// any other.
    /// </summary>
    internal static string GetLauncherLinkShortfall(Unit launcher)
    {
        if (!launcher.IsServer)
        {
            return Instance != null && Instance.hostLauncherLinks.TryGetValue(launcher, out string shortfall) ? shortfall : string.Empty;
        }

        FactionHQ? hq = launcher.NetworkHQ;
        if (hq == null)
        {
            return string.Empty;
        }

        GlobalPosition position = launcher.GlobalPosition();
        if (hq.TryGetFireControl(position, ControlRangeMeters, out FireControl fireControl)
            && fireControl != null && fireControl.TryGetRadar(out _))
        {
            return string.Empty;
        }

        return hq.TryGetRadar(position, ControlRangeMeters, out _)
            ? string.Empty
            : fireControl != null ? "its fire control has no radar within 300 m" : "no fire control or radar within 300 m";
    }

    /// <summary>
    /// A launcher that is aimed by a fire-control truck: a StratoLance and its kin. Answered off the
    /// faction's own turret cache rather than by walking the prefab, because the RADAR window asks
    /// this every frame for whatever is selected.
    /// </summary>
    internal static bool IsRadarGuidedLauncher(Unit? unit)
    {
        CommanderRadarService? service = Instance;
        return service != null
            && unit != null
            && service.turretsByUnit.TryGetValue(unit, out Turret[] turrets)
            && HasRadarSeekingTurret(turrets);
    }

    /// <summary>
    /// The same question about a unit the local faction does not own, and so is not in the turret
    /// cache: the enemy commander asks it about its own launchers, to work out which of them is
    /// waiting on a fire-control truck. It walks the unit instead of the cache, so it is not for a
    /// per-frame path.
    /// </summary>
    internal static bool IsRadarGuidedLauncherUncached(Unit? unit)
    {
        return unit != null && !unit.disabled && HasRadarSeekingTurret(unit.GetComponentsInChildren<Turret>(true));
    }

    /// <summary>
    /// The same question about a vehicle nobody has bought yet, off the prefab: the enemy commander
    /// has to know which of the definitions it can buy is a StratoLance before it buys one. The
    /// turret's mode is a serialized field, so the prefab answers it exactly as a live unit does.
    /// </summary>
    internal static bool IsRadarGuidedLauncherDefinition(VehicleDefinition? definition)
    {
        return definition?.unitPrefab != null
            && HasRadarSeekingTurret(definition.unitPrefab.GetComponentsInChildren<Turret>(true));
    }

    internal static bool IsRadarSam(Unit? unit)
    {
        return unit != null && unit.definition is VehicleDefinition { vehicleType: VehicleType.R_SAM };
    }

    /// <summary>An unguided round that only knows a map point: the shell seeker the 155 mm and the
    /// MLRS rockets fly on. Ballistic missiles (Piledrivers) home, so they are still shot at.</summary>
    internal static bool IsArtilleryRound(Unit? unit)
    {
        if (unit is not Missile missile || missile.definition == null)
        {
            return false;
        }

        if (!ArtilleryRounds.TryGetValue(missile.definition, out bool artillery))
        {
            artillery = missile.GetComponentInChildren<InertialSeekerShell>(true) != null;
            ArtilleryRounds[missile.definition] = artillery;
        }

        return artillery;
    }

    internal bool TryGetFocusedState(out RadarState state)
    {
        state = focusedState!;
        return state != null && state.Unit != null && !state.Unit.disabled;
    }

    /// <summary>
    /// Whether any selected unit or building carries a radar, and whether any of them is emitting.
    /// Read from OnGUI, so it only looks units up in the cache.
    /// </summary>
    internal bool TryGetSelectionRadar(out bool anyOnline)
    {
        anyOnline = false;
        bool any = false;
        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (!radarsByUnit.TryGetValue(units[i], out Radar[] radars))
            {
                continue;
            }

            any = true;
            if (units[i] is Aircraft aircraft && aircraft.radar is Radar aircraftRadar
                    ? aircraftRadar.activated
                    : IsAnyRadarOnline(radars))
            {
                anyOnline = true;
            }
        }

        return any;
    }

    /// <summary>Switches every selected radar off when any is on, otherwise on.</summary>
    internal void ToggleSelectionRadar()
    {
        if (!TryGetSelectionRadar(out bool anyOnline))
        {
            return;
        }

        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (radarsByUnit.ContainsKey(units[i]))
            {
                SetUnitRadar(units[i], !anyOnline);
            }
        }
    }

    /// <summary>Whether any selected radar SAM exists, and whether any of them ignores artillery.</summary>
    internal bool TryGetSelectionIgnoreArtillery(out bool anyOn)
    {
        anyOn = false;
        bool any = false;
        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (IsRadarSam(units[i]))
            {
                any = true;
                anyOn |= IgnoreArtilleryUnits.Contains(units[i]);
            }
        }

        return any;
    }

    /// <summary>Turns IGNORE ARTILLERY off for every selected radar SAM when any has it, otherwise on.</summary>
    internal void ToggleSelectionIgnoreArtillery()
    {
        if (!TryGetSelectionIgnoreArtillery(out bool anyOn))
        {
            return;
        }

        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (IsRadarSam(units[i]))
            {
                SetIgnoreArtillery(units[i], !anyOn);
            }
        }
    }

    /// <summary>
    /// Switches one unit's radars without it being selected, so a waypoint EMCON action can
    /// run on a unit the player is not currently looking at.
    /// </summary>
    internal bool SetUnitRadar(Unit? unit, bool enable)
    {
        if (unit == null
            || unit.disabled
            || !CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        CommanderNetwork.Forward("radar", CommanderNetwork.Id(unit), CommanderNetwork.Flag(enable));

        // An aircraft's radar is switched through its own networked toggle, so the pilot's HUD agrees.
        if (unit is Aircraft aircraft && aircraft.radar is Radar aircraftRadar)
        {
            if (enable && !aircraftRadar.IsOperational())
            {
                return false;
            }

            if (AircraftRpcToggleRadarMethod != null && aircraft.IsServer)
            {
                AircraftRpcToggleRadarMethod.Invoke(aircraft, new object[] { enable });
            }
            else
            {
                aircraftRadar.activated = enable;
            }

            if (enable)
            {
                offlineRadarUnits.Remove(unit);
            }
            else
            {
                offlineRadarUnits.Add(unit);
            }

            return true;
        }

        if (radarsByUnit.TryGetValue(unit, out Radar[] cached))
        {
            if (enable && !HasOperationalRadar(cached))
            {
                return false;
            }

            ApplyRadarState(unit, cached, enable);
            return true;
        }

        Radar[] radars = unit.GetComponentsInChildren<Radar>(true);
        if (radars.Length == 0 || (enable && !HasOperationalRadar(radars)))
        {
            return false;
        }

        ApplyRadarState(unit, radars, enable);
        return true;
    }

    internal void OnRegisterUnit(Unit unit)
    {
        CacheUnitSystems(unit);
    }

    private static void SyncLauncherLinks(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "radar.links" };
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && IsRadarGuidedLauncherUncached(unit))
            {
                args.Add($"{CommanderNetwork.Id(unit)} {GetLauncherLinkShortfall(unit)}");
            }
        }

        messages.Add(args.ToArray());
    }

    private static void SetIgnoreArtillery(Unit unit, bool enable)
    {
        CommanderNetwork.Forward("ignoreart", CommanderNetwork.Id(unit), CommanderNetwork.Flag(enable));
        if (enable)
        {
            IgnoreArtilleryUnits.Add(unit);
        }
        else
        {
            IgnoreArtilleryUnits.Remove(unit);
        }
    }

    private static FireControl? FindBatteryController(Unit unit)
    {
        FireControl[] fireControls = unit.GetComponentsInChildren<FireControl>(true);
        for (int i = 0; i < fireControls.Length; i++)
        {
            if (string.Equals(FireControlModeField?.GetValue(fireControls[i])?.ToString(), "searchForRadar", System.StringComparison.Ordinal))
            {
                return fireControls[i];
            }
        }

        return null;
    }

    private static bool IsRadarSeekingTurret(Turret turret)
    {
        return string.Equals(TurretModeField?.GetValue(turret)?.ToString(), "searchForRadar", System.StringComparison.Ordinal);
    }

    private static bool HasRadarSeekingTurret(Turret[] turrets)
    {
        for (int i = 0; i < turrets.Length; i++)
        {
            if (turrets[i] != null && IsRadarSeekingTurret(turrets[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAnyRadarOnline(Radar[] radars)
    {
        for (int i = 0; i < radars.Length; i++)
        {
            if (radars[i] != null && radars[i].IsOperational() && radars[i].activated)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOperationalRadar(Radar[] radars)
    {
        for (int i = 0; i < radars.Length; i++)
        {
            if (radars[i] != null && radars[i].IsOperational())
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearTargetsUsingRadar(Radar radar)
    {
        FireControl[] fireControls = Object.FindObjectsOfType<FireControl>();
        for (int i = 0; i < fireControls.Length; i++)
        {
            FireControl fireControl = fireControls[i];
            if (fireControl == null || !fireControl.TryGetRadar(out Radar linkedRadar) || linkedRadar != radar)
            {
                continue;
            }

            if (FireControlTurretsField?.GetValue(fireControl) is not List<Turret> turrets)
            {
                continue;
            }

            for (int turretIndex = 0; turretIndex < turrets.Count; turretIndex++)
            {
                turrets[turretIndex]?.SetTargetFromController(null);
            }
        }

        Turret[] sceneTurrets = Object.FindObjectsOfType<Turret>();
        for (int i = 0; i < sceneTurrets.Length; i++)
        {
            Unit? turretUnit = sceneTurrets[i]?.GetAttachedUnit();
            if (turretUnit != null && turretUnit.radar == radar)
            {
                sceneTurrets[i].SetTargetFromController(null);
            }
        }
    }

    private void ApplyRadarState(Unit unit, Radar[] radars, bool enable)
    {
        for (int i = 0; i < radars.Length; i++)
        {
            Radar radar = radars[i];
            if (radar == null || !radar.IsOperational())
            {
                continue;
            }

            radar.activated = enable;
            radar.enabled = enable;
            if (!enable)
            {
                radar.detectedTargets.Clear();
                radar.ResetRotators();
                ClearTargetsUsingRadar(radar);
            }
        }

        if (enable)
        {
            offlineRadarUnits.Remove(unit);
        }
        else
        {
            offlineRadarUnits.Add(unit);
        }

        if (focusedState != null && ReferenceEquals(focusedState.Unit, unit))
        {
            focusedState.IsRadarOnline = enable;
        }
    }

    private void SyncFocusedUnit()
    {
        Unit? unit = selectionService.FocusedSelection;
        if (unit == null || unit.disabled)
        {
            inspectedUnit = null;
            focusedState = null;
            return;
        }

        if (ReferenceEquals(unit, inspectedUnit))
        {
            UpdateFocusedRadarStatus(unit);
            return;
        }

        inspectedUnit = unit;

        Radar[] radars = unit.GetComponentsInChildren<Radar>(true);
        FireControl? batteryController = FindBatteryController(unit);
        if (radars.Length == 0 && batteryController == null)
        {
            focusedState = null;
            return;
        }

        focusedState = new RadarState(unit, radars, batteryController);
        nextRefreshTime = CommanderScheduler.Stagger("radar.nearby.focus", RefreshIntervalSeconds, 0.2f);
        UpdateFocusedRadarStatus(unit);
    }

    private void UpdateFocusedRadarStatus(Unit unit)
    {
        if (focusedState == null || !ReferenceEquals(focusedState.Unit, unit))
        {
            return;
        }

        focusedState.IsRadarOnline = unit is Aircraft aircraft && aircraft.radar is Radar aircraftRadar
            ? aircraftRadar.IsOperational() && aircraftRadar.activated
            : IsAnyRadarOnline(focusedState.Radars);
        if (focusedState.Radars.Length > 0 && !focusedState.IsRadarOnline)
        {
            offlineRadarUnits.Add(unit);
        }
        else
        {
            offlineRadarUnits.Remove(unit);
        }
    }

    private void RefreshNearbyCounts(RadarState state)
    {
        nearbyRadarUnits.Clear();
        nearbyLauncherUnits.Clear();

        FactionHQ? hq = state.Unit.NetworkHQ;
        if (hq == null)
        {
            state.NearbyRadarCount = 0;
            state.NearbyLauncherCount = 0;
            return;
        }

        GlobalPosition center = state.Unit.GlobalPosition();
        foreach (KeyValuePair<Unit, Radar[]> entry in radarsByUnit)
        {
            Unit radarUnit = entry.Key;
            if (!radarUnit.disabled
                && radarUnit.NetworkHQ == hq
                && FastMath.InRange(center, radarUnit.GlobalPosition(), ControlRangeMeters))
            {
                nearbyRadarUnits.Add(radarUnit);
            }
        }

        foreach (KeyValuePair<Unit, Turret[]> entry in turretsByUnit)
        {
            Unit launcherUnit = entry.Key;
            if (launcherUnit.disabled
                || launcherUnit.NetworkHQ != hq
                || !FastMath.InRange(center, launcherUnit.GlobalPosition(), ControlRangeMeters))
            {
                continue;
            }

            if (HasRadarSeekingTurret(entry.Value))
            {
                nearbyLauncherUnits.Add(launcherUnit);
            }
        }

        state.NearbyRadarCount = nearbyRadarUnits.Count;
        state.NearbyLauncherCount = nearbyLauncherUnits.Count;
    }

    /// <summary>
    /// Warns once for every radar-guided launcher that cannot shoot, because the failure is silent:
    /// a StratoLance with no fire-control truck beside it sits on station, tracks nothing and never
    /// fires, which reads as a broken launcher rather than a missing truck.
    /// </summary>
    private void ReviewLauncherLinks()
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        foreach (KeyValuePair<Unit, Turret[]> entry in turretsByUnit)
        {
            Unit launcher = entry.Key;
            if (launcher == null || launcher.disabled || launcher.NetworkHQ != hq || !HasRadarSeekingTurret(entry.Value))
            {
                continue;
            }

            string shortfall = GetLauncherLinkShortfall(launcher);
            if (shortfall.Length == 0)
            {
                launcherWarnings.Remove(launcher);
                continue;
            }

            if (launcherWarnings.TryGetValue(launcher, out string warned) && warned == shortfall)
            {
                continue;
            }

            launcherWarnings[launcher] = shortfall;
            CommanderAlertService.Instance?.NotifySupply(
                $"{CommanderGameAccess.GetUnitLabel(launcher).ToUpperInvariant()} CANNOT FIRE - {shortfall.ToUpperInvariant()}",
                launcher);
        }
    }

    private void RefreshBindings()
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (ReferenceEquals(hq, boundHq))
        {
            return;
        }

        UnbindHq();
        boundHq = hq;
        if (boundHq?.factionUnits == null)
        {
            return;
        }

        boundHq.onRegisterUnit += OnRegisterUnit;
        boundHq.onRemoveUnit += OnRemoveUnit;
        foreach (PersistentID unitId in boundHq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit))
            {
                CacheUnitSystems(unit);
            }
        }
    }

    private void UnbindHq()
    {
        if (boundHq != null)
        {
            boundHq.onRegisterUnit -= OnRegisterUnit;
            boundHq.onRemoveUnit -= OnRemoveUnit;
        }

        boundHq = null;
        radarsByUnit.Clear();
        turretsByUnit.Clear();
    }

    private void OnRemoveUnit(Unit unit)
    {
        radarsByUnit.Remove(unit);
        turretsByUnit.Remove(unit);
    }

    private void CacheUnitSystems(Unit unit)
    {
        if (unit == null || unit.disabled || unit.NetworkHQ != boundHq)
        {
            return;
        }

        // Radars are cached off any unit, radar stations included: the fire-control network links to any
        // emitter within 300 m, and a building Radar registers with the faction like a truck one.
        Radar[] radars = unit.GetComponentsInChildren<Radar>(true);
        if (radars.Length > 0)
        {
            radarsByUnit[unit] = radars;
        }

        if (unit is not GroundVehicle)
        {
            return;
        }

        Turret[] turrets = unit.GetComponentsInChildren<Turret>(true);
        if (turrets.Length > 0)
        {
            turretsByUnit[unit] = turrets;
        }
    }

    private void ApplyLauncherLinks(string[] args)
    {
        hostLauncherLinks.Clear();
        for (int i = 1; i < args.Length; i++)
        {
            int separator = args[i].IndexOf(' ');
            if (separator > 0 && CommanderNetwork.TryUnit(args[i].Substring(0, separator), out Unit unit))
            {
                hostLauncherLinks[unit] = args[i].Substring(separator + 1);
            }
        }
    }

    /// <summary>A guest's radar switch, on the host, where the radar actually emits.</summary>
    private void HostRadar(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit) || !ReferenceEquals(unit.NetworkHQ, hq))
        {
            return;
        }

        bool enable = CommanderNetwork.Bool(args, 2);
        if (unit is Aircraft aircraft && aircraft.radar is Radar && AircraftRpcToggleRadarMethod != null)
        {
            AircraftRpcToggleRadarMethod.Invoke(aircraft, new object[] { enable });
            return;
        }

        SetUnitRadar(unit, enable);
    }

    /// <summary>A guest's IGNORE ARTILLERY switch, on the host, where the SAM picks its targets.</summary>
    private void HostIgnoreArtillery(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit) && ReferenceEquals(unit.NetworkHQ, hq) && IsRadarSam(unit))
        {
            SetIgnoreArtillery(unit, CommanderNetwork.Bool(args, 2));
        }
    }

    internal sealed class RadarState
    {
        internal RadarState(Unit unit, Radar[] radars, FireControl? batteryController)
        {
            Unit = unit;
            Radars = radars;
            BatteryController = batteryController;
            IsRadarOnline = unit is Aircraft aircraft && aircraft.radar is Radar aircraftRadar
                ? aircraftRadar.IsOperational() && aircraftRadar.activated
                : IsAnyRadarOnline(radars);
        }

        internal Unit Unit { get; }

        internal Radar[] Radars { get; }

        internal FireControl? BatteryController { get; }

        internal bool IsCommandTruck => BatteryController != null;

        internal bool HasRadar => Radars.Length > 0;

        internal bool IsRadarOnline { get; set; }

        internal int NearbyRadarCount { get; set; }

        internal int NearbyLauncherCount { get; set; }
    }
}
