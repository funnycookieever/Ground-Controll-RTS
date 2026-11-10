using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderSelectionService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderResetSession
{
    private readonly List<Unit> selectedUnits = new();

    // Mirrors selectedUnits for IsSelected, which every world marker asks every frame: a list scan
    // there is markers x selection per frame once a large group is selected.
    private readonly HashSet<Unit> selectedSet = new();
    private readonly List<Unit> pinnedUnits = new();
    private readonly List<Unit> missionUnits = new();
    private readonly List<Unit> samSiteUnits = new();
    private readonly Dictionary<Unit, MissionPinInfo> missionInfo = new();
    private readonly Dictionary<Unit, string> samSiteLabels = new();
    private readonly List<Unit> typeMatches = new();
    private readonly List<Unit> screenScratch = new();
    private readonly List<Unit> idleScratch = new();
    private DynamicMap? boundMap;
    private Unit? commanderDetailUnit;
    private Unit? lastIdleUnit;

    internal CommanderSelectionService()
    {
        Instance = this;
        CommanderNetwork.OnHost("delete", (hq, args) =>
        {
            foreach (string id in CommanderNetwork.Arg(args, 1).Split(' '))
            {
                if (CommanderNetwork.TryUnit(id, out Unit unit) && !unit.disabled && !IsPlayerAircraft(unit) && ReferenceEquals(unit.NetworkHQ, hq))
                {
                    unit.DisableUnit();
                }
            }
        });
    }

    internal static CommanderSelectionService? Instance { get; private set; }

    internal IReadOnlyList<Unit> SelectedUnits => selectedUnits;

    internal IReadOnlyList<Unit> PinnedUnits => pinnedUnits;

    internal IReadOnlyList<Unit> MissionUnits => missionUnits;

    internal IReadOnlyList<Unit> SamSiteUnits => samSiteUnits;

    internal Unit? PrimarySelection => selectedUnits.Count > 0 ? selectedUnits[0] : null;

    /// <summary>Bumped whenever the selected set changes, so other services can react once per change.</summary>
    internal int SelectionRevision { get; private set; }

    internal Unit? FocusedSelection => GetDetailTargetUnit();

    internal bool IsCurrentSelectionPinned
    {
        get
        {
            if (selectedUnits.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < selectedUnits.Count; i++)
            {
                if (!pinnedUnits.Contains(selectedUnits[i])
                    && !missionUnits.Contains(selectedUnits[i])
                    && !samSiteUnits.Contains(selectedUnits[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal bool CanDeleteSelection
    {
        get
        {
            FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
            if (selectedUnits.Count == 0 || localHq == null)
            {
                return false;
            }

            for (int i = 0; i < selectedUnits.Count; i++)
            {
                if (!CommanderGameAccess.IsFriendlyUnit(selectedUnits[i], localHq) || IsPlayerAircraft(selectedUnits[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void Activate()
    {
        commanderDetailUnit = null;
        BindMap();
        DeselectAll();
    }

    public void Deactivate()
    {
        DeselectAll();
        ClearCommanderDetailUnit();
        CommanderGameAccess.RaiseFollowingUnitSet(null);
        UnbindMap();
    }

    public void TickActive()
    {
        BindMap();
        PruneDisabledUnits();
        SyncDetailUi();
    }

    public void ResetSession()
    {
        DeselectAll();
        pinnedUnits.Clear();
        missionUnits.Clear();
        samSiteUnits.Clear();
        missionInfo.Clear();
        samSiteLabels.Clear();
        lastIdleUnit = null;
        UnbindMap();
    }

    internal static void PinMissionUnit(Unit unit, string source, string mission)
    {
        // The host flies a guest's aircraft through the same Air Command code, and those aircraft
        // do not belong in the host's own unit list. The real local faction, not GetLocalHq: the
        // enemy commander orders its airlift helicopters under RunAs(its faction), where that
        // answers the AI's side and its Ibis landed in the player's list.
        if (Instance == null
            || unit == null
            || unit.disabled
            || CommanderNetwork.IsHandlingGuest
            || !GameManager.GetLocalHQ(out FactionHQ localHq)
            || !CommanderGameAccess.IsFriendlyUnit(unit, localHq))
        {
            return;
        }

        if (!Instance.missionUnits.Contains(unit))
        {
            Instance.pinnedUnits.Remove(unit);
            Instance.samSiteUnits.Remove(unit);
            Instance.samSiteLabels.Remove(unit);
            Instance.missionUnits.Add(unit);
        }

        Instance.missionInfo[unit] = new MissionPinInfo(source, mission);
    }

    internal static void PinSamSiteUnit(Unit unit, string label)
    {
        if (Instance == null || unit == null || unit.disabled)
        {
            return;
        }

        Instance.pinnedUnits.Remove(unit);
        Instance.missionUnits.Remove(unit);
        Instance.missionInfo.Remove(unit);
        if (!Instance.samSiteUnits.Contains(unit))
        {
            Instance.samSiteUnits.Add(unit);
        }

        Instance.samSiteLabels[unit] = label;
    }

    internal static void RemoveSamSiteUnit(Unit? unit)
    {
        if (Instance == null || unit == null)
        {
            return;
        }

        Instance.samSiteUnits.Remove(unit);
        Instance.samSiteLabels.Remove(unit);
    }

    /// <summary>Groups units by what they are rather than which one they are.</summary>
    internal static string GetTypeKey(Unit? unit)
    {
        if (unit == null)
        {
            return string.Empty;
        }

        return !string.IsNullOrWhiteSpace(unit.unitName) ? unit.unitName : unit.name;
    }

    /// <summary>An aircraft a human is flying. Never scrapped: it would kill that player.</summary>
    internal static bool IsPlayerAircraft(Unit unit)
    {
        return unit is Aircraft aircraft && aircraft.Player != null;
    }

    /// <summary>Drops a single unit out of the selection, leaving the rest alone.</summary>
    internal static void DeselectUnit(Unit unit)
    {
        unit = CommanderSamSiteCoreRegistry.ResolveSelection(unit) ?? unit;
        SceneSingleton<DynamicMap>.i?.DeselectIcon(unit);
    }

    internal void TogglePinSelected()
    {
        if (selectedUnits.Count == 0)
        {
            return;
        }

        bool remove = IsCurrentSelectionPinned;
        for (int i = 0; i < selectedUnits.Count; i++)
        {
            Unit unit = selectedUnits[i];
            if (remove)
            {
                pinnedUnits.Remove(unit);
                missionUnits.Remove(unit);
                samSiteUnits.Remove(unit);
                missionInfo.Remove(unit);
                samSiteLabels.Remove(unit);
            }
            else if (!pinnedUnits.Contains(unit))
            {
                pinnedUnits.Add(unit);
            }
        }
    }

    internal void SelectPinnedUnit(Unit unit)
    {
        if (unit == null || unit.disabled)
        {
            if (unit != null)
            {
                RemovePinnedUnit(unit);
            }

            return;
        }

        SelectUnit(unit, false);
    }

    internal void RemovePinnedUnit(Unit unit)
    {
        pinnedUnits.Remove(unit);
        missionUnits.Remove(unit);
        missionInfo.Remove(unit);
        samSiteUnits.Remove(unit);
        samSiteLabels.Remove(unit);
    }

    internal string GetSamSiteLabel(Unit unit)
    {
        return samSiteLabels.TryGetValue(unit, out string label) ? label : "SAM SITE";
    }

    internal MissionPinInfo GetMissionInfo(Unit unit)
    {
        return missionInfo.TryGetValue(unit, out MissionPinInfo? info)
            ? info
            : new MissionPinInfo("MISSION", "Active mission");
    }

    internal void DeleteSelectedUnits()
    {
        if (selectedUnits.Count == 0)
        {
            return;
        }

        List<Unit> unitsToDelete = new(selectedUnits);
        DeselectAll();

        // Scrapping a unit is a server call, so a guest's list goes to the host in one request.
        List<string>? forwarded = CommanderNetwork.IsGuest ? new List<string>() : null;
        for (int i = 0; i < unitsToDelete.Count; i++)
        {
            Unit unit = unitsToDelete[i];
            if (!CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq()) || IsPlayerAircraft(unit))
            {
                continue;
            }

            pinnedUnits.Remove(unit);
            missionUnits.Remove(unit);
            samSiteUnits.Remove(unit);
            missionInfo.Remove(unit);
            samSiteLabels.Remove(unit);
            if (forwarded != null)
            {
                forwarded.Add(CommanderNetwork.Id(unit));
                continue;
            }

            unit.DisableUnit();
        }

        if (forwarded != null && forwarded.Count > 0)
        {
            CommanderNetwork.Forward("delete", string.Join(" ", forwarded.ToArray()));
        }
    }

    internal bool IsSelected(Unit unit)
    {
        return selectedSet.Contains(unit);
    }

    internal void SelectUnit(Unit unit, bool additive)
    {
        unit = CommanderSamSiteCoreRegistry.ResolveSelection(unit) ?? unit;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (!CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
        {
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return;
        }

        if (!additive)
        {
            selectedUnits.Clear();
            selectedSet.Clear();
            dynamicMap.DeselectAllIcons();
        }

        dynamicMap.SelectIcon(unit);
    }

    /// <summary>Selects a batch of units in one pass (box select, unit list, control groups).</summary>
    internal void SelectUnits(IReadOnlyList<Unit> units, bool additive)
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return;
        }

        if (!additive)
        {
            selectedUnits.Clear();
            selectedSet.Clear();
            dynamicMap.DeselectAllIcons();
        }

        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = CommanderSamSiteCoreRegistry.ResolveSelection(units[i]) ?? units[i];
            if (CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq) && !selectedSet.Contains(unit))
            {
                dynamicMap.SelectIcon(unit);
            }
        }
    }

    /// <summary>
    /// Clears the selection, on the map and here. The map half is best effort: callers include
    /// <see cref="Deactivate"/> and <see cref="ResetSession"/>, which run from
    /// <c>OnActiveSceneChanged</c> while the old scene's <c>MapIcon</c> objects are half torn down, and
    /// <c>DeselectAllIcons</c> can throw there. The exception is logged and skipped so the rest of
    /// <c>Deactivate</c> still runs.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Base-game map icons throw whatever they like while the scene is being torn down; the rest of the shutdown must still run.")]
    internal void DeselectAll()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap != null)
        {
            try
            {
                dynamicMap.DeselectAllIcons();
            }
            catch (Exception error)
            {
                CommanderPlugin.Log.LogDebug($"Map icons were already gone when the selection was cleared: {error.Message}");
            }
        }

        selectedUnits.Clear();
        selectedSet.Clear();
        SelectionRevision++;
        NotifyCoverageSelectionChanged();
        SyncDetailUi();
    }

    /// <summary>
    /// The RTS "select every one of these" gesture. <paramref name="onScreenOnly"/> limits it to
    /// units whose world marker is currently visible, which is what double-click does; the
    /// modifier click takes every one the faction owns.
    /// </summary>
    internal void SelectAllOfType(Unit template, bool additive, bool onScreenOnly)
    {
        if (template == null || template.disabled)
        {
            return;
        }

        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq?.factionUnits == null || !CommanderGameAccess.IsFriendlyUnit(template, localHq))
        {
            // Selecting every hostile of a type would leak units the faction has not spotted.
            SelectUnit(template, additive);
            return;
        }

        string typeKey = GetTypeKey(template);
        typeMatches.Clear();
        if (onScreenOnly)
        {
            screenScratch.Clear();
            CommanderMarkerService.Instance?.CollectUnitsInScreenRect(
                new Rect(0f, 0f, Screen.width, Screen.height), screenScratch);
            for (int i = 0; i < screenScratch.Count; i++)
            {
                Unit unit = screenScratch[i];
                if (CommanderGameAccess.IsFriendlyUnit(unit, localHq)
                    && GetTypeKey(unit) == typeKey
                    && CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
                {
                    typeMatches.Add(unit);
                }
            }
        }
        else
        {
            foreach (PersistentID unitId in localHq.factionUnits)
            {
                if (unitId.TryGetUnit(out Unit unit)
                    && GetTypeKey(unit) == typeKey
                    && CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
                {
                    typeMatches.Add(unit);
                }
            }
        }

        if (typeMatches.Count == 0)
        {
            SelectUnit(template, additive);
            return;
        }

        SelectUnits(typeMatches, additive);
    }

    /// <summary>Narrows the current selection to one unit type (selection-bar chip click).</summary>
    internal void KeepOnlyType(string typeKey)
    {
        typeMatches.Clear();
        for (int i = 0; i < selectedUnits.Count; i++)
        {
            if (GetTypeKey(selectedUnits[i]) == typeKey)
            {
                typeMatches.Add(selectedUnits[i]);
            }
        }

        if (typeMatches.Count > 0)
        {
            SelectUnits(typeMatches, false);
        }
    }

    /// <summary>Removes one unit type from the current selection (selection-bar chip, add key held).</summary>
    internal void RemoveType(string typeKey)
    {
        typeMatches.Clear();
        for (int i = 0; i < selectedUnits.Count; i++)
        {
            if (GetTypeKey(selectedUnits[i]) == typeKey)
            {
                typeMatches.Add(selectedUnits[i]);
            }
        }

        for (int i = 0; i < typeMatches.Count; i++)
        {
            DeselectUnit(typeMatches[i]);
        }
    }

    /// <summary>
    /// Selects the next friendly ground/naval unit that holds no RTS order, so units
    /// left behind at a depot or at the end of a route can be found without hunting the map.
    /// Returns false when everything is busy.
    /// </summary>
    internal bool SelectNextIdleUnit()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq?.factionUnits == null)
        {
            return false;
        }

        idleScratch.Clear();
        CommanderMoveService? moveService = CommanderMoveService.Instance;
        foreach (PersistentID unitId in localHq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || !CommanderGameAccess.ShouldAllowCommanderMove(unit)
                || !CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
            {
                continue;
            }

            if (moveService != null
                && (moveService.TryGetOrder(unit, out _, out _, out _) || moveService.IsStopped(unit)))
            {
                continue;
            }

            idleScratch.Add(unit);
        }

        if (idleScratch.Count == 0)
        {
            return false;
        }

        // Instance ids are stable for the session, so the cycle order does not jump around
        // as units gain and lose orders.
        idleScratch.Sort(static (left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));

        int next = 0;
        if (lastIdleUnit != null)
        {
            int current = idleScratch.IndexOf(lastIdleUnit);
            if (current >= 0)
            {
                next = (current + 1) % idleScratch.Count;
            }
        }

        lastIdleUnit = idleScratch[next];
        SelectUnit(lastIdleUnit, false);
        return true;
    }

    private void BindMap()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == boundMap || dynamicMap == null)
        {
            return;
        }

        UnbindMap();
        boundMap = dynamicMap;
        boundMap.onUnitSelected += OnUnitSelected;
        boundMap.onUnitDeselected += OnUnitDeselected;
        boundMap.onAllDeselected += OnAllDeselected;
    }

    private void UnbindMap()
    {
        if (boundMap == null)
        {
            return;
        }

        boundMap.onUnitSelected -= OnUnitSelected;
        boundMap.onUnitDeselected -= OnUnitDeselected;
        boundMap.onAllDeselected -= OnAllDeselected;
        boundMap = null;
        selectedUnits.Clear();
        selectedSet.Clear();
    }

    private void OnUnitSelected(Unit unit)
    {
        Unit resolvedUnit = CommanderSamSiteCoreRegistry.ResolveSelection(unit) ?? unit;
        if (!ReferenceEquals(resolvedUnit, unit))
        {
            boundMap?.DeselectIcon(unit);
            boundMap?.SelectIcon(resolvedUnit);
            return;
        }

        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (!CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
        {
            return;
        }

        if (selectedSet.Add(unit))
        {
            selectedUnits.Add(unit);
            SelectionRevision++;
        }

        NotifyCoverageSelectionChanged();
        SyncDetailUi();
    }

    private void OnUnitDeselected(Unit unit)
    {
        selectedSet.Remove(unit);
        if (selectedUnits.Remove(unit))
        {
            SelectionRevision++;
        }

        NotifyCoverageSelectionChanged();
        SyncDetailUi();
    }

    private void OnAllDeselected()
    {
        selectedUnits.Clear();
        selectedSet.Clear();
        SelectionRevision++;
        NotifyCoverageSelectionChanged();
        SyncDetailUi();
    }

    private void NotifyCoverageSelectionChanged()
    {
        CommanderSamSiteAnalyzerService.Instance?.RetainCoverageForSelection(selectedUnits);
    }

    private void PruneDisabledUnits()
    {
        bool selectionChanged = false;
        for (int i = selectedUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = selectedUnits[i];
            if (unit == null || unit.disabled)
            {
                selectedUnits.RemoveAt(i);
                selectedSet.Remove(unit!);
                selectionChanged = true;
            }
        }

        if (selectionChanged)
        {
            NotifyCoverageSelectionChanged();
        }

        for (int i = pinnedUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = pinnedUnits[i];
            if (unit == null || unit.disabled)
            {
                pinnedUnits.RemoveAt(i);
            }
        }

        for (int i = missionUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = missionUnits[i];
            if (unit == null || unit.disabled)
            {
                missionUnits.RemoveAt(i);
                if (!ReferenceEquals(unit, null))
                {
                    missionInfo.Remove(unit);
                }
            }
        }

        for (int i = samSiteUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = samSiteUnits[i];
            if (unit == null || unit.disabled)
            {
                samSiteUnits.RemoveAt(i);
                if (!ReferenceEquals(unit, null))
                {
                    samSiteLabels.Remove(unit);
                }
            }
        }
    }

    private void SyncDetailUi()
    {
        Unit? targetUnit = GetDetailTargetUnit();
        if (ReferenceEquals(targetUnit, commanderDetailUnit))
        {
            return;
        }

        ClearCommanderDetailUnit();
        commanderDetailUnit = targetUnit;

        if (commanderDetailUnit == null)
        {
            CommanderGameAccess.RaiseFollowingUnitSet(null);
            return;
        }

        CommanderGameAccess.RaiseFollowingUnitSet(commanderDetailUnit);
    }

    private Unit? GetDetailTargetUnit()
    {
        for (int i = selectedUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = selectedUnits[i];
            if (unit != null && !unit.disabled)
            {
                return unit;
            }
        }

        return null;
    }

    private void ClearCommanderDetailUnit()
    {
        if (commanderDetailUnit == null)
        {
            return;
        }

        commanderDetailUnit = null;
    }

    internal sealed class MissionPinInfo
    {
        internal MissionPinInfo(string source, string mission)
        {
            Source = source;
            Mission = mission;
        }

        internal string Source { get; }

        internal string Mission { get; }
    }
}
