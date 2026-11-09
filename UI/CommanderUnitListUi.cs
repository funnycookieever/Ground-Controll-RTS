using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Order of battle: every unit the faction owns (plus tracked hostiles) in one scrollable
/// list, with filters, multi-select and the control-group controls.
/// </summary>
internal sealed class CommanderUnitListUi
{
    private const int WindowId = 0x434F4D4C;
    private const float RefreshIntervalSeconds = 0.5f;
    private const float RowHeight = 42f;

    private static GUIStyle? rightCaption;

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderGroupService groupService;
    private readonly List<Unit> entries = new();
    private readonly List<string> entryLabels = new();
    private readonly List<Unit> scratch = new();

    private Rect windowRect;
    private Vector2 scroll;
    private bool positionInitialized;
    private bool helpVisible;
    private float nextRefreshAt;
    private Filter filter = Filter.All;

    internal CommanderUnitListUi(
        CommanderSelectionService selectionService,
        CommanderGroupService groupService)
    {
        this.selectionService = selectionService;
        this.groupService = groupService;
    }

    private enum Filter
    {
        All,
        Ground,
        Air,
        Naval,
        Structures,
        Enemy,
        Log,
    }

    internal bool Visible { get; private set; }

    private static GUIStyle RightCaption => rightCaption ??= new GUIStyle(CommanderUiTheme.Caption) { alignment = TextAnchor.MiddleRight };

    internal void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            nextRefreshAt = 0f;
        }
    }

    internal void Hide()
    {
        Visible = false;
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return Visible && CommanderWindowScale.Visual(WindowId, windowRect).Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void Tick()
    {
        if (!Visible)
        {
            return;
        }

        Vector2 size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(520f, CommanderUiScale.Width - 24f), Mathf.Min(620f, CommanderUiScale.Height - 24f)),
            new Vector2(340f, 260f));
        float width = size.x;
        float height = size.y;
        if (!positionInitialized)
        {
            windowRect = new Rect(Mathf.Max(12f, 486f), Mathf.Max(12f, (CommanderUiScale.Height * 0.5f) - (height * 0.5f)), width, height);
            positionInitialized = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
        }

        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);

        if (CommanderScheduler.IsDueRealtime(ref nextRefreshAt, RefreshIntervalSeconds))
        {
            Refresh();
        }
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "ORDER OF BATTLE");
    }

    private static Color GetLogColor(CommanderAlertService.LogKind kind)
    {
        return kind switch
        {
            CommanderAlertService.LogKind.Kill => new Color(0.6f, 1f, 0.65f, 1f),
            CommanderAlertService.LogKind.Arrival => new Color(0.7f, 0.88f, 1f, 1f),
            CommanderAlertService.LogKind.Loss => new Color(1f, 0.6f, 0.5f, 1f),
            CommanderAlertService.LogKind.Capture => new Color(1f, 0.9f, 0.35f, 1f),
            _ => new Color(1f, 0.82f, 0.5f, 1f),
        };
    }

    private static string GetTypeLabel(Unit unit)
    {
        return unit switch
        {
            Aircraft => "AIR",
            Ship => "NAVAL",
            GroundVehicle vehicle => CommanderGameAccess
                .GetVehicleCategoryLabel(vehicle.definition as VehicleDefinition).ToUpperInvariant(),
            Building => "STRUCTURE",
            _ => "UNIT",
        };
    }

    private void Refresh()
    {
        entries.Clear();
        entryLabels.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || filter == Filter.Log)
        {
            return;
        }

        if (filter == Filter.Enemy)
        {
            foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
            {
                if (entry.Key.TryGetUnit(out Unit unit)
                    && CommanderGameAccess.ShouldAllowCommanderSelection(unit, hq)
                    && !CommanderGameAccess.IsFriendlyUnit(unit, hq))
                {
                    entries.Add(unit);
                }
            }
        }
        else if (hq.factionUnits != null)
        {
            foreach (PersistentID unitId in hq.factionUnits)
            {
                if (unitId.TryGetUnit(out Unit unit) && Matches(unit, hq))
                {
                    entries.Add(unit);
                }
            }
        }

        entries.Sort(static (left, right) => string.Compare(
            CommanderGameAccess.GetUnitLabel(left),
            CommanderGameAccess.GetUnitLabel(right),
            StringComparison.OrdinalIgnoreCase));

        // Condition walks every damageable part, so the row text is built once per refresh
        // rather than on every OnGUI pass.
        entryLabels.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            entryLabels.Add(BuildRowLabel(entries[i]));
        }
    }

    /// <summary>Kills, losses and arrivals, newest first.</summary>
    private void DrawBattleLog(float y)
    {
        CommanderAlertService? alertService = CommanderAlertService.Instance;
        IReadOnlyList<CommanderAlertService.LogEntry> log = alertService != null
            ? alertService.Log
            : Array.Empty<CommanderAlertService.LogEntry>();

        Rect view = new(10f, y, windowRect.width - 20f, windowRect.height - y - 12f);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, (log.Count * 26f) + 4f));
        scroll = GUI.BeginScrollView(view, scroll, inner);
        if (log.Count == 0)
        {
            GUI.Label(new Rect(6f, 6f, inner.width - 12f, 22f), "NO EVENTS YET", CommanderUiTheme.MutedLabel);
        }

        for (int i = 0; i < log.Count; i++)
        {
            CommanderAlertService.LogEntry entry = log[i];
            float rowY = 2f + (i * 26f);
            GUI.Label(new Rect(6f, rowY, 60f, 22f), entry.Timestamp, CommanderUiTheme.MutedLabel);
            Color previous = GUI.color;
            GUI.color = GetLogColor(entry.Kind);
            if (entry.Unit != null && !entry.Unit.disabled)
            {
                if (GUI.Button(new Rect(68f, rowY, inner.width - 74f, 22f), entry.Text, CommanderUiTheme.Button))
                {
                    scratch.Clear();
                    scratch.Add(entry.Unit);
                    selectionService.SelectUnits(scratch, false);
                    CommanderCameraFollowService.Instance?.FocusSelection();
                }
            }
            else
            {
                GUI.Label(new Rect(72f, rowY, inner.width - 78f, 22f), entry.Text, CommanderUiTheme.Label);
            }

            GUI.color = previous;
        }

        GUI.EndScrollView();
    }

    private bool Matches(Unit? unit, FactionHQ hq)
    {
        if (unit == null || unit.disabled || !CommanderGameAccess.ShouldAllowCommanderSelection(unit, hq))
        {
            return false;
        }

        return filter switch
        {
            Filter.Ground => unit is GroundVehicle,
            Filter.Air => unit is Aircraft,
            Filter.Naval => unit is Ship,
            Filter.Structures => unit is Building,
            _ => true,
        };
    }

    private void DrawWindow(int windowId)
    {
        CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible);
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            Visible = false;
            return;
        }

        float y = 34f;
        if (helpVisible)
        {
            const string help = "Click a row to select, hold the add-selection key to extend. SELECT ALL takes every unit that passes the current filter. "
                + "Control groups: click a number to recall it, hold the assign key (Ctrl by default) and click to store the current selection, "
                + "or use the 1-9 keys directly. Orders issued to a selection apply to every unit in it. LOG lists kills, losses and arrivals; click an entry to jump to the unit.";
            y += CommanderUiTheme.DrawHelpOverlay(
                new Rect(10f, y, windowRect.width - 20f, 96f),
                help);
            y += 8f;
        }

        float tabWidth = (windowRect.width - 20f - (4f * 6f)) / 7f;
        DrawFilterTab(new Rect(10f, y, tabWidth, 26f), "ALL", Filter.All);
        DrawFilterTab(new Rect(10f + (tabWidth + 4f), y, tabWidth, 26f), "GND", Filter.Ground);
        DrawFilterTab(new Rect(10f + ((tabWidth + 4f) * 2f), y, tabWidth, 26f), "AIR", Filter.Air);
        DrawFilterTab(new Rect(10f + ((tabWidth + 4f) * 3f), y, tabWidth, 26f), "SEA", Filter.Naval);
        DrawFilterTab(new Rect(10f + ((tabWidth + 4f) * 4f), y, tabWidth, 26f), "BLD", Filter.Structures);
        DrawFilterTab(new Rect(10f + ((tabWidth + 4f) * 5f), y, tabWidth, 26f), "ENY", Filter.Enemy);
        DrawFilterTab(new Rect(10f + ((tabWidth + 4f) * 6f), y, tabWidth, 26f), "LOG", Filter.Log);
        y += 32f;

        if (filter == Filter.Log)
        {
            DrawBattleLog(y);
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
            return;
        }

        float groupWidth = (windowRect.width - 20f - (8f * 3f)) / 9f;
        bool assigning = CommanderShortcutInput.IsPressed(CommanderSettings.AssignGroupModifier);
        for (int id = 1; id <= CommanderGroupService.GroupCount; id++)
        {
            Rect rect = new(10f + ((groupWidth + 3f) * (id - 1)), y, groupWidth, 26f);
            int count = groupService.GetGroupCount(id);
            GUIStyle style = count > 0 ? CommanderUiTheme.PrimaryButton : CommanderUiTheme.Button;
            if (GUI.Button(rect, assigning ? $"+{id}" : id.ToString(CultureInfo.CurrentCulture), style))
            {
                if (assigning)
                {
                    groupService.AssignSelection(id);
                }
                else
                {
                    groupService.SelectGroup(id, CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection));
                }
            }
        }

        y += 30f;
        GUI.Label(
            new Rect(10f, y, windowRect.width - 20f, 18f),
            assigning ? "RELEASE TO STORE SELECTION IN A GROUP" : "CONTROL GROUPS  |  HOLD ASSIGN KEY TO STORE",
            CommanderUiTheme.MutedLabel);
        y += 22f;

        float actionWidth = (windowRect.width - 26f) * 0.5f;
        if (GUI.Button(new Rect(10f, y, actionWidth, 28f), $"SELECT ALL ({entries.Count})", CommanderUiTheme.PrimaryButton))
        {
            scratch.Clear();
            scratch.AddRange(entries);
            selectionService.SelectUnits(scratch, CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection));
        }

        if (GUI.Button(new Rect(16f + actionWidth, y, actionWidth, 28f), "CLEAR SELECTION", CommanderUiTheme.Button))
        {
            selectionService.DeselectAll();
        }

        y += 34f;

        Rect view = new(10f, y, windowRect.width - 20f, windowRect.height - y - 12f);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, (entries.Count * RowHeight) + 4f));
        scroll = GUI.BeginScrollView(view, scroll, inner);
        for (int i = 0; i < entries.Count; i++)
        {
            Unit unit = entries[i];
            if (unit == null || unit.disabled)
            {
                continue;
            }

            float rowY = 2f + (i * RowHeight);
            if (rowY + RowHeight < scroll.y || rowY > scroll.y + view.height)
            {
                continue;
            }

            // A picture, the name, and the rest of the line (type, condition, ammunition) beside it.
            bool selected = selectionService.IsSelected(unit);
            Rect row = new(2f, rowY, inner.width - 4f, RowHeight - 4f);
            bool clicked = GUI.Button(row, GUIContent.none, selected ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button);
            Rect picture = new(row.x + 2f, row.y + 2f, (row.height - 4f) * 1.5f, row.height - 4f);
            CommanderUnitPreview.DrawStill(picture, unit.definition);
            string line = i < entryLabels.Count ? entryLabels[i] : CommanderGameAccess.GetUnitLabel(unit);
            int split = line.IndexOf("   |   ", System.StringComparison.Ordinal);
            float textX = picture.xMax + 10f;
            GUI.Label(new Rect(textX, row.y, row.width * 0.5f, row.height), split > 0 ? line.Substring(0, split) : line, CommanderUiTheme.Label);
            if (split > 0)
            {
                GUI.Label(new Rect(row.center.x, row.y, (row.width * 0.5f) - 8f, row.height), line.Substring(split + 7).Replace("   |   ", "    "), RightCaption);
            }

            if (clicked)
            {
                scratch.Clear();
                scratch.Add(unit);
                selectionService.SelectUnits(scratch, CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection));

                // Picking a unit out of a list should show you the unit, not leave you
                // hunting for the CENTER button afterwards.
                CommanderCameraFollowService.Instance?.FocusSelection();
            }
        }

        GUI.EndScrollView();
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private void DrawFilterTab(Rect rect, string label, Filter value)
    {
        if (GUI.Button(rect, label, filter == value ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            filter = value;
            scroll = Vector2.zero;
            nextRefreshAt = 0f;
        }
    }

    private string BuildRowLabel(Unit unit)
    {
        int group = groupService.GetGroupOf(unit);
        string prefix = group > 0 ? $"[{group}] " : string.Empty;
        string state = string.Empty;
        if (filter != Filter.Enemy)
        {
            float ammo = CommanderGameAccess.GetUnitAmmo(unit);
            string ammoText = ammo >= 0f ? $"  AMMO {ammo * 100f:0}%" : string.Empty;
            float fuel = CommanderGameAccess.GetUnitFuel(unit);
            string fuelText = fuel >= 0f ? $"  FUEL {fuel * 100f:0}%" : string.Empty;
            state = $"   |   COND {CommanderGameAccess.GetUnitCondition(unit) * 100f:0}%{ammoText}{fuelText}";
        }

        return $"{prefix}{CommanderGameAccess.GetUnitLabel(unit)}   |   {GetTypeLabel(unit)}{state}";
    }
}
