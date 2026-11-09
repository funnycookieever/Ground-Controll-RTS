using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    private static float DrawChannel(Rect rect, string channel, float value)
    {
        GUI.Label(new Rect(rect.x, rect.y, 16f, rect.height), channel, CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(rect.x + 18f, rect.y + 8f, rect.width - 30f, 20f),
            value,
            0f,
            1f);

        // Untouched, the value comes back as it went in: rounding it would read as a change
        // every frame and rebuild the theme forever.
        if (slid == value)
        {
            return value;
        }

        return Mathf.Round(Mathf.Clamp01(slid) * 255f) / 255f;
    }

    private static KeyboardShortcut GetBinding(string binding)
    {
        return binding switch
        {
            "forward" => CommanderSettings.CameraForward,
            "backward" => CommanderSettings.CameraBackward,
            "left" => CommanderSettings.CameraLeft,
            "right" => CommanderSettings.CameraRight,
            "up" => CommanderSettings.CameraUp,
            "down" => CommanderSettings.CameraDown,
            "look" => CommanderSettings.CameraFreeLook,
            "boost" => CommanderSettings.CameraBoost,
            "primary" => CommanderSettings.PrimaryAction,
            "secondary" => CommanderSettings.SecondaryAction,
            "add_selection" => CommanderSettings.AddToSelection,
            "repeat_deploy" => CommanderSettings.RepeatDeployment,
            "open_build" => CommanderSettings.OpenBuild,
            "open_ground" => CommanderSettings.OpenGroundUnits,
            "open_air" => CommanderSettings.OpenAirCommand,
            "open_naval" => CommanderSettings.OpenNaval,
            "open_oob" => CommanderSettings.OpenOrderOfBattle,
            "toggle_map" => CommanderSettings.ToggleTacticalMap,
            "speed_1" => CommanderSettings.GameSpeed1,
            "speed_2" => CommanderSettings.GameSpeed2,
            "speed_4" => CommanderSettings.GameSpeed4,
            "center_follow" => CommanderSettings.CameraCenterFollow,
            "toggle_ui" => CommanderSettings.ToggleUi,
            "toggle_commander" => CommanderSettings.ToggleCommander,
            "queue_waypoint" => CommanderSettings.QueueWaypoint,
            "assign_group" => CommanderSettings.AssignGroupModifier,
            "stop_order" => CommanderSettings.StopOrder,
            "same_type" => CommanderSettings.SelectSameType,
            "cycle_idle" => CommanderSettings.CycleIdleUnit,
            "ping" => CommanderSettings.PingKey,
            "clear_selection" => CommanderSettings.ClearSelection,
            "night_vision" => CommanderSettings.ToggleNightVision,
            "rotate_building" => CommanderSettings.RotateBuilding,
            "cancel_blueprint" => CommanderSettings.CancelBlueprint,
            "map_box_select" => CommanderSettings.MapBoxSelect,
            _ => new KeyboardShortcut(KeyCode.None),
        };
    }

    private static void SetBinding(string binding, KeyboardShortcut shortcut)
    {
        switch (binding)
        {
            case "forward": CommanderSettings.CameraForward = shortcut; break;
            case "backward": CommanderSettings.CameraBackward = shortcut; break;
            case "left": CommanderSettings.CameraLeft = shortcut; break;
            case "right": CommanderSettings.CameraRight = shortcut; break;
            case "up": CommanderSettings.CameraUp = shortcut; break;
            case "down": CommanderSettings.CameraDown = shortcut; break;
            case "look": CommanderSettings.CameraFreeLook = shortcut; break;
            case "boost": CommanderSettings.CameraBoost = shortcut; break;
            case "primary": CommanderSettings.PrimaryAction = shortcut; break;
            case "secondary": CommanderSettings.SecondaryAction = shortcut; break;
            case "add_selection": CommanderSettings.AddToSelection = shortcut; break;
            case "repeat_deploy": CommanderSettings.RepeatDeployment = shortcut; break;
            case "open_build": CommanderSettings.OpenBuild = shortcut; break;
            case "open_ground": CommanderSettings.OpenGroundUnits = shortcut; break;
            case "open_air": CommanderSettings.OpenAirCommand = shortcut; break;
            case "open_naval": CommanderSettings.OpenNaval = shortcut; break;
            case "open_oob": CommanderSettings.OpenOrderOfBattle = shortcut; break;
            case "toggle_map": CommanderSettings.ToggleTacticalMap = shortcut; break;
            case "speed_1": CommanderSettings.GameSpeed1 = shortcut; break;
            case "speed_2": CommanderSettings.GameSpeed2 = shortcut; break;
            case "speed_4": CommanderSettings.GameSpeed4 = shortcut; break;
            case "center_follow": CommanderSettings.CameraCenterFollow = shortcut; break;
            case "toggle_ui": CommanderSettings.ToggleUi = shortcut; break;
            case "toggle_commander": CommanderSettings.ToggleCommander = shortcut; break;
            case "queue_waypoint": CommanderSettings.QueueWaypoint = shortcut; break;
            case "assign_group": CommanderSettings.AssignGroupModifier = shortcut; break;
            case "stop_order": CommanderSettings.StopOrder = shortcut; break;
            case "same_type": CommanderSettings.SelectSameType = shortcut; break;
            case "cycle_idle": CommanderSettings.CycleIdleUnit = shortcut; break;
            case "ping": CommanderSettings.PingKey = shortcut; break;
            case "clear_selection": CommanderSettings.ClearSelection = shortcut; break;
            case "night_vision": CommanderSettings.ToggleNightVision = shortcut; break;
            case "rotate_building": CommanderSettings.RotateBuilding = shortcut; break;
            case "cancel_blueprint": CommanderSettings.CancelBlueprint = shortcut; break;
            case "map_box_select": CommanderSettings.MapBoxSelect = shortcut; break;
        }
    }

    private static void ResetCameraBindings()
    {
        CommanderSettings.CameraForward = new KeyboardShortcut(KeyCode.W);
        CommanderSettings.CameraBackward = new KeyboardShortcut(KeyCode.S);
        CommanderSettings.CameraLeft = new KeyboardShortcut(KeyCode.A);
        CommanderSettings.CameraRight = new KeyboardShortcut(KeyCode.D);
        CommanderSettings.CameraUp = new KeyboardShortcut(KeyCode.Q);
        CommanderSettings.CameraDown = new KeyboardShortcut(KeyCode.E);
        CommanderSettings.CameraFreeLook = new KeyboardShortcut(KeyCode.Mouse2);
        CommanderSettings.CameraBoost = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.CameraCenterFollow = new KeyboardShortcut(KeyCode.Space);
        CommanderSettings.OpenBuild = new KeyboardShortcut(KeyCode.B);
        CommanderSettings.OpenGroundUnits = new KeyboardShortcut(KeyCode.G);
        CommanderSettings.OpenAirCommand = new KeyboardShortcut(KeyCode.F);
        CommanderSettings.OpenNaval = new KeyboardShortcut(KeyCode.V);
        CommanderSettings.OpenOrderOfBattle = new KeyboardShortcut(KeyCode.O);
        CommanderSettings.ToggleTacticalMap = new KeyboardShortcut(KeyCode.J);
    }

    private static void ResetActionBindings()
    {
        CommanderSettings.PrimaryAction = new KeyboardShortcut(KeyCode.Mouse0);
        CommanderSettings.SecondaryAction = new KeyboardShortcut(KeyCode.Mouse1);
        CommanderSettings.AddToSelection = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.RepeatDeployment = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.ToggleUi = new KeyboardShortcut(KeyCode.H);
        CommanderSettings.ToggleCommander = new KeyboardShortcut(KeyCode.F8);
        CommanderSettings.QueueWaypoint = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.AssignGroupModifier = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.StopOrder = new KeyboardShortcut(KeyCode.X);
        CommanderSettings.SelectSameType = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.CycleIdleUnit = new KeyboardShortcut(KeyCode.Period);
        CommanderSettings.PingKey = new KeyboardShortcut(KeyCode.P);
        CommanderSettings.ClearSelection = new KeyboardShortcut(KeyCode.C);
        CommanderSettings.ToggleNightVision = new KeyboardShortcut(KeyCode.N);
        CommanderSettings.RotateBuilding = new KeyboardShortcut(KeyCode.R);
        CommanderSettings.CancelBlueprint = new KeyboardShortcut(KeyCode.Delete);
        CommanderSettings.MapBoxSelect = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.GameSpeed1 = new KeyboardShortcut(KeyCode.F5);
        CommanderSettings.GameSpeed2 = new KeyboardShortcut(KeyCode.F6);
        CommanderSettings.GameSpeed4 = new KeyboardShortcut(KeyCode.F7);
    }

    private void DrawSettingsWindowIfVisible()
    {
        if (settingsVisible)
        {
            settingsWindowRect = CommanderWindowScale.Window(SettingsWindowId, settingsWindowRect, DrawSettingsWindow, "COMMANDER SETTINGS");
        }
    }

    private void DrawSettingsWindow(int windowId)
    {
        CaptureBindingInput();
        float y = 38f;
        if (CommanderUiTheme.DrawHelpButton(settingsWindowRect.width, ref settingsHelpVisible))
        {
            y = 34f + CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, settingsWindowRect.width - 24f, 74f),
                "Settings are saved in the BepInEx configuration. RTS camera bindings are read only while RTS mode is active and do not alter aircraft controls. MATCH RULES change the game itself, not just the interface.") + 10f;
        }

        if (GUI.Button(new Rect(settingsWindowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            settingsVisible = false;
            bindingCapture = null;
        }

        // Game speed moved here from the command bar, to keep the bar short.
        DrawSpeedButtons(settingsWindowRect.width - 126f - ((TopBarSpeedWidth + 2f) * 3f), 3f, 22f);

        float tabWidth = (settingsWindowRect.width - 30f) / 6f;
        DrawSettingsTab(new Rect(12f, y, tabWidth, 32f), "GAMEPLAY", 0);
        DrawSettingsTab(new Rect(12f + tabWidth, y, tabWidth, 32f), "UI / HIDE", 1);
        DrawSettingsTab(new Rect(12f + (tabWidth * 2f), y, tabWidth, 32f), "CONTROLS", 2);
        DrawSettingsTab(new Rect(12f + (tabWidth * 3f), y, tabWidth, 32f), "SHORTCUTS", 3);
        DrawSettingsTab(new Rect(12f + (tabWidth * 4f), y, tabWidth, 32f), "CAMERA", 4);
        DrawSettingsTab(new Rect(12f + (tabWidth * 5f), y, tabWidth, 32f), "VISUALS", 5);
        y += 44f;

        if (settingsTab == 0)
        {
            DrawGameplaySettings(y);
        }
        else if (settingsTab == 1)
        {
            DrawUiSettings(y);
        }
        else if (settingsTab == 3)
        {
            DrawShortcutList(y);
        }
        else if (settingsTab == 4)
        {
            DrawCameraSettings(y);
        }
        else if (settingsTab == 5)
        {
            DrawVisualSettings(y);
        }
        else
        {
            DrawControlSettings(y);
        }

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, settingsWindowRect.width, settingsWindowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, settingsWindowRect.width - 126f - ((TopBarSpeedWidth + 2f) * 3f), 28f));
    }

    /// <summary>
    /// Read-only reference of every shortcut, including the ones that are not remappable
    /// (control groups, camera bookmarks, double-click). Rebuilt on layout events only, so the
    /// per-entry strings are not rebuilt twice a frame while the tab sits open.
    /// </summary>
    private void DrawShortcutList(float y)
    {
        if (Event.current.type == EventType.Layout)
        {
            CommanderShortcutReference.Collect(shortcutEntries);
        }

        float width = settingsWindowRect.width - 24f;
        float height = settingsWindowRect.height - y - 16f;
        GUI.Box(new Rect(12f, y, width, height), string.Empty, CommanderUiTheme.Panel);

        Rect view = new(16f, y + 8f, width - 8f, height - 16f);
        float innerWidth = view.width - 20f;
        float keyColumn = innerWidth * 0.34f;
        float noteColumn = innerWidth * 0.30f;
        float actionWidth = innerWidth - keyColumn - noteColumn - 16f;
        float contentHeight = 0f;
        for (int i = 0; i < shortcutEntries.Count; i++)
        {
            contentHeight += shortcutEntries[i].IsSection ? 34f : ShortcutRowHeight(shortcutEntries[i], actionWidth, keyColumn, noteColumn) + 2f;
        }

        Rect inner = new(0f, 0f, innerWidth, Mathf.Max(view.height, contentHeight + 4f));
        shortcutScroll = GUI.BeginScrollView(view, shortcutScroll, inner);
        float rowY = 0f;
        for (int i = 0; i < shortcutEntries.Count; i++)
        {
            CommanderShortcutReference.Entry entry = shortcutEntries[i];
            if (entry.IsSection)
            {
                GUI.Label(new Rect(4f, rowY + 8f, inner.width - 8f, 24f), entry.Action, CommanderUiTheme.Header);
                rowY += 34f;
                continue;
            }

            float rowHeight = ShortcutRowHeight(entry, actionWidth, keyColumn, noteColumn);
            GUI.Label(new Rect(8f, rowY, actionWidth, rowHeight), entry.Action, CommanderUiTheme.Label);
            GUI.Label(new Rect(8f + actionWidth, rowY, keyColumn, rowHeight), entry.Keys, CommanderUiTheme.Header);
            if (entry.Note.Length > 0)
            {
                GUI.Label(new Rect(12f + actionWidth + keyColumn, rowY, noteColumn, rowHeight), entry.Note, CommanderUiTheme.MutedLabel);
            }

            rowY += rowHeight + 2f;
        }

        GUI.EndScrollView();
    }

    // Rows grow to their tallest wrapped column; a fixed 28 px row cut long notes off after one line.
    private float ShortcutRowHeight(CommanderShortcutReference.Entry entry, float actionWidth, float keyColumn, float noteColumn)
    {
        shortcutMeasure.text = entry.Action;
        float height = Mathf.Max(28f, CommanderUiTheme.Label.CalcHeight(shortcutMeasure, actionWidth));
        shortcutMeasure.text = entry.Keys;
        height = Mathf.Max(height, CommanderUiTheme.Header.CalcHeight(shortcutMeasure, keyColumn));
        if (entry.Note.Length > 0)
        {
            shortcutMeasure.text = entry.Note;
            height = Mathf.Max(height, CommanderUiTheme.MutedLabel.CalcHeight(shortcutMeasure, noteColumn));
        }

        return height;
    }

    private void DrawSettingsTab(Rect rect, string label, int tab)
    {
        if (GUI.Button(rect, label, settingsTab == tab ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            settingsTab = tab;
            bindingCapture = null;
        }
    }

    private void DrawGameplaySettings(float y)
    {
        float commandY = y;
        GUI.Box(new Rect(12f, commandY, settingsWindowRect.width - 24f, 350f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, commandY + 10f, settingsWindowRect.width - 48f, 22f), "COMMAND", CommanderUiTheme.Header);
        CommanderSettings.GroupHotkeys = CommanderUiTheme.Check(
            new Rect(24f, commandY + 40f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.GroupHotkeys,
            "Control group hotkeys 1-9",
            CommanderUiTheme.Toggle);
        CommanderSettings.AttackMoveIntoRange = CommanderUiTheme.Check(
            new Rect(24f, commandY + 74f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AttackMoveIntoRange,
            "Attack orders stop at weapon range",
            CommanderUiTheme.Toggle);
        CommanderSettings.RetargetAfterKill = CommanderUiTheme.Check(
            new Rect(24f, commandY + 108f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.RetargetAfterKill,
            "Keep attacking after the target dies, then hold the ground",
            CommanderUiTheme.Toggle);
        CommanderSettings.CameraBookmarks = CommanderUiTheme.Check(
            new Rect(24f, commandY + 142f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.CameraBookmarks,
            "Camera bookmarks F1-F4 (assign key + F1-F4 stores)",
            CommanderUiTheme.Toggle);
        CommanderSettings.OrderFeedback = CommanderUiTheme.Check(
            new Rect(24f, commandY + 176f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.OrderFeedback,
            "Flash a marker where an order was given",
            CommanderUiTheme.Toggle);
        CommanderSettings.AttackMoveRoutes = CommanderUiTheme.Check(
            new Rect(24f, commandY + 210f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AttackMoveRoutes,
            "Attack-move: Free Fire units engage hostiles they pass",
            CommanderUiTheme.Toggle);
        CommanderSettings.GuardOrders = CommanderUiTheme.Check(
            new Rect(24f, commandY + 244f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.GuardOrders,
            "Right click a friendly unit to guard it",
            CommanderUiTheme.Toggle);
        CommanderSettings.AutoRetreatDamaged = CommanderUiTheme.Check(
            new Rect(24f, commandY + 278f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AutoRetreatDamaged,
            $"Retreat to repair below {CommanderSettings.RetreatConditionPercent:0}% condition",
            CommanderUiTheme.Toggle);
        CommanderSettings.CombatAlerts = CommanderUiTheme.Check(
            new Rect(24f, commandY + 312f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.CombatAlerts,
            "Alert when units are attacked or lost",
            CommanderUiTheme.Toggle);

        // The rest change the match itself, not how it is played, so they get a panel that says so.
        float rulesY = commandY + 358f;
        GUI.Box(new Rect(12f, rulesY, settingsWindowRect.width - 24f, 118f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, rulesY + 10f, settingsWindowRect.width - 48f, 22f), "MATCH RULES", CommanderUiTheme.Header);

        // Cycles MATCHED -> MISSION FUNDS -> OFF (this match only). Whether there is an AI at all
        // is AI Commanders' call, so an off one says why.
        int enemyMode = CommanderEnemyCommanderService.EffectiveMode;
        bool enemyLocked = CommanderEnemyCommanderService.IsChampionsMission;
        string enemyLabel = enemyLocked
            ? $"{CommanderEnemyCommanderService.GetModeLabel(enemyMode)} (LOCKED BY MISSION)"
            : CommanderEnemyCommanderService.OffThisMatch
            ? "OFF (THIS MATCH ONLY)"
            : enemyMode != CommanderEnemyCommanderService.ModeOff
            ? CommanderEnemyCommanderService.GetModeLabel(enemyMode)
            : "OFF ON THIS MISSION";
        int teamsLeftToPlayers = enemyMode > 0 ? CommanderEnemyCommanderService.CountTeamsLeftToPlayers() : 0;
        string buttonLabel = $"ENEMY COMMANDER: {enemyLabel}"
            + (enemyMode > 0 ? $" ({CommanderEnemyCommanderService.Instance?.TotalPurchases ?? 0} bought)" : string.Empty)
            + (teamsLeftToPlayers > 0 ? $" ({teamsLeftToPlayers} team(s) left to players)" : string.Empty);
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && !enemyLocked;
        const float DifficultyWidth = 230f;
        bool enemyClicked = GUI.Button(
            new Rect(24f, rulesY + 40f, settingsWindowRect.width - 56f - DifficultyWidth, 32f),
            buttonLabel,
            enemyMode > 0 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button);
        GUI.enabled = wasEnabled;
        if (enemyClicked)
        {
            CommanderEnemyCommanderService.CycleSetting();
        }

        // Not locked on Champions: the mission fixes the enemy's money, not how well it spends it.
        int difficulty = CommanderSettings.EnemyDifficulty;
        if (GUI.Button(
            new Rect(settingsWindowRect.width - 24f - DifficultyWidth, rulesY + 40f, DifficultyWidth, 32f),
            new GUIContent(
                $"DIFFICULTY: {CommanderEnemyCommanderService.GetDifficultyLabel(difficulty)}",
                "NORMAL buys a mixed army and attacks in small squads. CHALLENGING has the same money, buys what counters your army and attacks your base in one massed push."),
            difficulty == CommanderEnemyCommanderService.DifficultyChallenging ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            CommanderSettings.EnemyDifficulty = difficulty == CommanderEnemyCommanderService.DifficultyChallenging
                ? CommanderEnemyCommanderService.DifficultyNormal
                : CommanderEnemyCommanderService.DifficultyChallenging;
        }

        CommanderSettings.EnemyCommanderHumanTeams = CommanderUiTheme.Check(
            new Rect(24f, rulesY + 78f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.EnemyCommanderHumanTeams,
            "Enemy commander also runs teams with players on them (they fly for it)",
            CommanderUiTheme.Toggle);
    }

    /// <summary>
    /// Everything about how the camera feels. It is a whole tab rather than a few config lines
    /// because a camera is tuned by moving it, not by reading numbers: every value here wants to
    /// be dragged while the game is running.
    /// </summary>
    private void DrawCameraSettings(float y)
    {
        float width = settingsWindowRect.width - 24f;
        GUI.Box(new Rect(12f, y, width, 268f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, width - 24f, 22f), "MOVEMENT", CommanderUiTheme.Header);
        CommanderSettings.CameraPanSpeed = DrawCameraSlider(
            y + 40f, "Pan speed", CommanderSettings.CameraPanSpeed, 50f, 1200f, "0", " m/s");
        CommanderSettings.CameraZoomSpeed = DrawCameraSlider(
            y + 78f, "Zoom speed", CommanderSettings.CameraZoomSpeed, 0.2f, 3f, "0.0#", "x");
        CommanderSettings.CameraLookSensitivity = DrawCameraSlider(
            y + 116f, "Look sensitivity", CommanderSettings.CameraLookSensitivity, 0.1f, 4f, "0.0#", "x");
        CommanderSettings.CameraSmoothing = DrawCameraSlider(
            y + 154f, "Smoothing", CommanderSettings.CameraSmoothing, 0f, 0.4f, "0.00", " s");
        CommanderSettings.CameraHeightScaledSpeed = CommanderUiTheme.Check(
            new Rect(24f, y + 190f, width - 48f, 30f),
            CommanderSettings.CameraHeightScaledSpeed,
            "Pan speed scales with height above ground",
            CommanderUiTheme.Toggle);
        CommanderSettings.CameraEdgeScroll = CommanderUiTheme.Check(
            new Rect(24f, y + 224f, width - 48f, 30f),
            CommanderSettings.CameraEdgeScroll,
            "Edge scrolling (push the cursor into a screen edge)",
            CommanderUiTheme.Toggle);

        float lookY = y + 280f;
        GUI.Box(new Rect(12f, lookY, width, 82f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, lookY + 10f, width - 24f, 22f), "LOOKING AROUND", CommanderUiTheme.Header);
        string lookHint = $"Hold {CommanderSettings.CameraFreeLook} and move the mouse. The camera turns on the "
            + "spot, so it stays where you put it. The wheel zooms toward the cursor.";
        GUI.Label(
            new Rect(24f, lookY + 40f, width - 48f, 34f),
            lookHint,
            CommanderUiTheme.MutedLabel);

        float followY = lookY + 94f;
        GUI.Box(new Rect(12f, followY, width, 152f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, followY + 10f, width - 24f, 22f), "SELECTION AND FOLLOW", CommanderUiTheme.Header);
        CommanderSettings.AutoFollowSelection = CommanderUiTheme.Check(
            new Rect(24f, followY + 40f, width - 48f, 30f),
            CommanderSettings.AutoFollowSelection,
            "Follow the camera on the selected unit",
            CommanderUiTheme.Toggle);
        CommanderSettings.AutoFrameSelection = CommanderUiTheme.Check(
            new Rect(24f, followY + 74f, width - 48f, 30f),
            CommanderSettings.AutoFrameSelection,
            "Travel to a selected unit only when it is off screen",
            CommanderUiTheme.Toggle);
        string followHint = $"Selecting something you can already see leaves the camera alone. {CommanderSettings.CameraCenterFollow} "
            + "always centres on the selection immediately.";
        GUI.Label(
            new Rect(24f, followY + 108f, width - 48f, 34f),
            followHint,
            CommanderUiTheme.MutedLabel);

        float mapY = followY + 164f;
        GUI.Box(new Rect(12f, mapY, width, 192f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, mapY + 10f, width - 24f, 22f), "TACTICAL MAP", CommanderUiTheme.Header);
        CommanderSettings.MapDragSpeed = DrawCameraSlider(
            mapY + 40f, "Map drag speed", CommanderSettings.MapDragSpeed, 0.25f, 4f, "0.0#", "x");
        CommanderSettings.MapZoomSpeed = DrawCameraSlider(
            mapY + 78f, "Map zoom speed", CommanderSettings.MapZoomSpeed, 0.25f, 4f, "0.0#", "x");
        CommanderSettings.MapJumpKeepHeight = CommanderUiTheme.Check(
            new Rect(24f, mapY + 112f, width - 48f, 30f),
            CommanderSettings.MapJumpKeepHeight,
            "Keep the camera's current height when jumping from the map",
            CommanderUiTheme.Toggle);
        GUI.enabled = !CommanderSettings.MapJumpKeepHeight;
        CommanderSettings.MapJumpHeight = Mathf.Round(DrawCameraSlider(
            mapY + 152f, "Map jump height", CommanderSettings.MapJumpHeight, 100f, 5000f, "0", " m") / 50f) * 50f;
        GUI.enabled = true;
    }

    /// <summary>A labelled slider that shows the value it is about to write.</summary>
    private float DrawCameraSlider(float y, string label, float value, float min, float max, string format, string suffix)
    {
        GUI.Label(
            new Rect(24f, y, 230f, 24f),
            $"{label}   {value.ToString(format, CultureInfo.CurrentCulture)}{suffix}",
            CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(260f, y + 6f, settingsWindowRect.width - 284f, 20f),
            value,
            min,
            max);
        return Mathf.Clamp(slid, min, max);
    }

    /// <summary>
    /// Plain UI and the theme colours. Every change rebuilds the theme, which takes effect on
    /// the next frame.
    /// </summary>
    private void DrawVisualSettings(float y)
    {
        float width = settingsWindowRect.width - 24f;
        GUI.Box(new Rect(12f, y, width, 112f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, width - 24f, 22f), "ACCESSIBILITY", CommanderUiTheme.Header);
        bool plain = CommanderSettings.PlainUi;
        bool newPlain = CommanderUiTheme.Check(
            new Rect(24f, y + 40f, width - 24f, 30f),
            plain,
            "Plain UI: solid high-contrast plates and larger text",
            CommanderUiTheme.Toggle);
        if (newPlain != plain)
        {
            CommanderSettings.PlainUi = newPlain;
            CommanderUiTheme.Refresh();
        }

        GUI.Label(
            new Rect(24f, y + 74f, width - 24f, 32f),
            "For low vision. Windows become fully opaque with thick edges, brighter text and larger sliders.",
            CommanderUiTheme.MutedLabel);

        float colourY = y + 124f;
        const int rows = 2;
        GUI.Box(new Rect(12f, colourY, width, 48f + (rows * 36f) + 44f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, colourY + 10f, width - 24f, 22f), "COLOURS", CommanderUiTheme.Header);
        float rowY = colourY + 44f;
        CommanderSettings.AccentColor = DrawColourRow(rowY, "Accent", CommanderSettings.AccentColor);
        CommanderSettings.BackgroundColor = DrawColourRow(rowY + 36f, "Background", CommanderSettings.BackgroundColor);

        float nightY = colourY + 48f + (rows * 36f) + 56f;
        GUI.Box(new Rect(12f, nightY, width, 298f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, nightY + 10f, width - 24f, 22f), "NIGHT VISION", CommanderUiTheme.Header);
        CommanderSettings.EnhanceNightVision = CommanderUiTheme.Check(
            new Rect(24f, nightY + 40f, width - 48f, 30f),
            CommanderSettings.EnhanceNightVision,
            "Improve the game's night vision",
            CommanderUiTheme.Toggle);
        CommanderSettings.NightVisionGain = DrawCameraSlider(
            nightY + 76f, "Brightness", CommanderSettings.NightVisionGain, 0f, 6f, "0.0", " stops");
        CommanderSettings.NightVisionGlareCutoff = DrawCameraSlider(
            nightY + 112f, "Glare cutoff", CommanderSettings.NightVisionGlareCutoff, 0.5f, 4f, "0.0#", "x");
        CommanderSettings.NightVisionContrast = DrawCameraSlider(
            nightY + 148f, "Contrast", CommanderSettings.NightVisionContrast, 0f, 80f, "0", string.Empty);
        CommanderSettings.NightVisionFill = DrawCameraSlider(
            nightY + 184f, "Fill light", CommanderSettings.NightVisionFill, 0f, 1.5f, "0.00", string.Empty);
        string tintName = CommanderSettings.NightVisionTint switch { 1 => "White phosphor", 2 => "Full colour", _ => "Green phosphor" };
        if (GUI.Button(new Rect(24f, nightY + 220f, width - 24f, 30f), "Tint: " + tintName, CommanderUiTheme.Button))
        {
            CommanderSettings.NightVisionTint = (CommanderSettings.NightVisionTint + 1) % 3;
        }

        if (GUI.Button(new Rect(24f, nightY + 258f, width - 24f, 30f), "RESET NIGHT VISION", CommanderUiTheme.Button))
        {
            CommanderSettings.EnhanceNightVision = true;
            CommanderSettings.NightVisionGain = 2.5f;
            CommanderSettings.NightVisionGlareCutoff = 1.8f;
            CommanderSettings.NightVisionContrast = 30f;
            CommanderSettings.NightVisionFill = 0.5f;
            CommanderSettings.NightVisionTint = 2;
        }

        if (GUI.Button(new Rect(24f, rowY + (rows * 36f) + 4f, width - 24f, 30f), "RESET COLOURS", CommanderUiTheme.Button))
        {
            CommanderSettings.AccentColor = CommanderSettings.DefaultAccentColor;
            CommanderSettings.BackgroundColor = CommanderSettings.DefaultBackgroundColor;
            CommanderUiTheme.Refresh();
        }
    }

    /// <summary>Label, swatch and one slider per channel. Rebuilds the theme only on a change.</summary>
    private Color DrawColourRow(float y, string label, Color colour)
    {
        GUI.Label(new Rect(24f, y, 96f, 28f), label, CommanderUiTheme.Label);
        Rect swatch = new(122f, y + 2f, 40f, 24f);
        Color previous = GUI.color;
        GUI.color = colour;
        GUI.DrawTexture(swatch, Texture2D.whiteTexture);
        GUI.color = previous;
        CommanderUiTheme.DrawFrame(swatch, 1f);

        float channelWidth = (settingsWindowRect.width - 196f) / 3f;
        Color result = colour;
        result.r = DrawChannel(new Rect(174f, y, channelWidth, 28f), "R", colour.r);
        result.g = DrawChannel(new Rect(174f + channelWidth, y, channelWidth, 28f), "G", colour.g);
        result.b = DrawChannel(new Rect(174f + (channelWidth * 2f), y, channelWidth, 28f), "B", colour.b);
        if (result != colour)
        {
            CommanderUiTheme.Refresh();
        }

        return result;
    }

    private void DrawUiSettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 412f), string.Empty, CommanderUiTheme.Panel);
        float left = 28f;
        float right = (settingsWindowRect.width * 0.5f) + 10f;
        float width = (settingsWindowRect.width * 0.5f) - 40f;
        showCommandButton = CommanderUiTheme.Check(new Rect(left, y + 16f, width, 28f), showCommandButton, "Command button", CommanderUiTheme.Toggle);
        showFactionMoney = CommanderUiTheme.Check(new Rect(right, y + 16f, width, 28f), showFactionMoney, "Faction funds", CommanderUiTheme.Toggle);
        showTacticalMap = CommanderUiTheme.Check(new Rect(left, y + 50f, width, 28f), showTacticalMap, "Tactical map", CommanderUiTheme.Toggle);
        showSelectionBar = CommanderUiTheme.Check(new Rect(right, y + 50f, width, 28f), showSelectionBar, "Selection bar", CommanderUiTheme.Toggle);
        showPinnedUnits = CommanderUiTheme.Check(new Rect(left, y + 84f, width, 28f), showPinnedUnits, "Unit / mission list", CommanderUiTheme.Toggle);
        showUnitSystems = CommanderUiTheme.Check(new Rect(right, y + 84f, width, 28f), showUnitSystems, "Unit systems", CommanderUiTheme.Toggle);
        showDepotUi = CommanderUiTheme.Check(new Rect(left, y + 118f, width, 28f), showDepotUi, "Depot UI", CommanderUiTheme.Toggle);
        showSupplyUi = CommanderUiTheme.Check(new Rect(right, y + 118f, width, 28f), showSupplyUi, "Supply UI", CommanderUiTheme.Toggle);
        showAirCommandUi = CommanderUiTheme.Check(new Rect(left, y + 152f, width, 28f), showAirCommandUi, "Air Command UI", CommanderUiTheme.Toggle);
        showNavalUi = CommanderUiTheme.Check(new Rect(right, y + 152f, width, 28f), showNavalUi, "Naval UI", CommanderUiTheme.Toggle);
        showWorldMarkers = CommanderUiTheme.Check(new Rect(left, y + 186f, width, 28f), showWorldMarkers, "World markers", CommanderUiTheme.Toggle);
        showUnitListUi = CommanderUiTheme.Check(new Rect(right, y + 186f, width, 28f), showUnitListUi, "Order of battle", CommanderUiTheme.Toggle);
        showBuildUi = CommanderUiTheme.Check(new Rect(left, y + 220f, width, 28f), showBuildUi, "Build UI", CommanderUiTheme.Toggle);
        CommanderSettings.AlertWindow = CommanderUiTheme.Check(new Rect(right, y + 220f, width, 28f), CommanderSettings.AlertWindow, "Alerts window", CommanderUiTheme.Toggle);

        CommanderSettings.CockpitMarkers = CommanderUiTheme.Check(
            new Rect(left, y + 254f, settingsWindowRect.width - 56f, 28f),
            CommanderSettings.CockpitMarkers,
            "Cockpit markers: pings, attack targets and contested bases while flying",
            CommanderUiTheme.Toggle);

        SaveUiVisibilitySettings();

        // Applied on release: rescaling mid-drag would move the slider out from under the cursor.
        float shownScale = pendingUiScale > 0f ? pendingUiScale : CommanderSettings.UiScale;
        GUI.Label(
            new Rect(28f, y + 292f, 260f, 24f),
            $"UI scale   {shownScale:0.00}x  (x{CommanderUiScale.ResolutionScale:0.##} for {Screen.height}p)",
            CommanderUiTheme.Label);
        float slidScale = Mathf.Round(GUI.HorizontalSlider(
            new Rect(296f, y + 298f, settingsWindowRect.width - 324f, 20f),
            shownScale,
            0.5f,
            2f) * 20f) / 20f;
        if (!Mathf.Approximately(slidScale, shownScale))
        {
            pendingUiScale = slidScale;
        }

        if (pendingUiScale > 0f && !Input.GetMouseButton(0))
        {
            CommanderSettings.UiScale = pendingUiScale;
            pendingUiScale = -1f;
        }

        GUI.Label(
            new Rect(28f, y + 328f, settingsWindowRect.width - 56f, 20f),
            "The UI grows with screen height; the slider multiplies on top.",
            CommanderUiTheme.MutedLabel);
        GUI.Label(
            new Rect(28f, y + 352f, settingsWindowRect.width - 56f, 20f),
            $"{CommanderSettings.ToggleUi} cycles visible, RTS UI hidden, and all UI hidden.",
            CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(28f, y + 376f, settingsWindowRect.width - 56f, 30f), "RESET UI LAYOUT", CommanderUiTheme.Button))
        {
            ResetUiLayout();
        }
    }

    private void SaveUiVisibilitySettings()
    {
        CommanderSettings.ShowCommandButton = showCommandButton;
        CommanderSettings.ShowFactionMoney = showFactionMoney;
        CommanderSettings.ShowTacticalMap = showTacticalMap;
        CommanderSettings.ShowSelectionBar = showSelectionBar;
        CommanderSettings.ShowPinnedUnits = showPinnedUnits;
        CommanderSettings.ShowUnitSystems = showUnitSystems;
        CommanderSettings.ShowDepotUi = showDepotUi;
        CommanderSettings.ShowSupplyUi = showSupplyUi;
        CommanderSettings.ShowAirCommandUi = showAirCommandUi;
        CommanderSettings.ShowNavalUi = showNavalUi;
        CommanderSettings.ShowSamAnalyzerUi = showSamAnalyzerUi;
        CommanderSettings.ShowWorldMarkers = showWorldMarkers;
        CommanderSettings.ShowUnitListUi = showUnitListUi;
        CommanderSettings.ShowBuildUi = showBuildUi;
    }

    // Scrolls so the reset buttons stay reachable when the window is clamped to a short screen.
    private void DrawControlSettings(float top)
    {
        Rect view = new(0f, top, settingsWindowRect.width, settingsWindowRect.height - top - 8f);
        Rect inner = new(0f, 0f, settingsWindowRect.width - 20f, 700f);
        controlsScroll = GUI.BeginScrollView(view, controlsScroll, inner);
        DrawControlBindings(0f);
        GUI.EndScrollView();
    }

    private void DrawControlBindings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 696f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(
            new Rect(24f, y + 8f, settingsWindowRect.width - 48f, 32f),
            "Bindings are active only in RTS mode. Click one, then press a keyboard or mouse button. Escape cancels.",
            CommanderUiTheme.MutedLabel);

        float columnWidth = (settingsWindowRect.width - 66f) * 0.5f;
        float left = 24f;
        float right = 42f + columnWidth;
        GUI.Label(new Rect(left, y + 42f, columnWidth, 22f), "CAMERA", CommanderUiTheme.Header);
        GUI.Label(new Rect(right, y + 42f, columnWidth, 22f), "COMMANDER ACTIONS", CommanderUiTheme.Header);
        float rowY = y + 68f;
        DrawBinding(new Rect(left, rowY, columnWidth, 30f), "Forward", "forward");
        DrawBinding(new Rect(left, rowY + 32f, columnWidth, 30f), "Backward", "backward");
        DrawBinding(new Rect(left, rowY + 64f, columnWidth, 30f), "Move left", "left");
        DrawBinding(new Rect(left, rowY + 96f, columnWidth, 30f), "Move right", "right");
        DrawBinding(new Rect(left, rowY + 128f, columnWidth, 30f), "Move up", "up");
        DrawBinding(new Rect(left, rowY + 160f, columnWidth, 30f), "Move down", "down");
        DrawBinding(new Rect(left, rowY + 192f, columnWidth, 30f), "Free look", "look");
        DrawBinding(new Rect(left, rowY + 224f, columnWidth, 30f), "Speed boost", "boost");
        DrawBinding(
            new Rect(left, rowY + 256f, columnWidth, 30f),
            "Center / follow",
            "center_follow",
            "Press briefly to center on the selected unit. Hold to center and follow it.");

        GUI.Label(new Rect(left, rowY + 294f, columnWidth, 22f), "WINDOWS", CommanderUiTheme.Header);
        DrawBinding(new Rect(left, rowY + 320f, columnWidth, 30f), "Build", "open_build");
        DrawBinding(
            new Rect(left, rowY + 352f, columnWidth, 30f),
            "Ground units",
            "open_ground",
            "Selects the nearest depot, which opens its purchase list.");
        DrawBinding(new Rect(left, rowY + 384f, columnWidth, 30f), "Air Command", "open_air");
        DrawBinding(new Rect(left, rowY + 416f, columnWidth, 30f), "Ships", "open_naval");
        DrawBinding(new Rect(left, rowY + 448f, columnWidth, 30f), "Order of battle", "open_oob");

        DrawBinding(new Rect(right, rowY, columnWidth, 30f), "Select / place", "primary");
        DrawBinding(new Rect(right, rowY + 32f, columnWidth, 30f), "Move / order", "secondary");
        DrawBinding(new Rect(right, rowY + 64f, columnWidth, 30f), "Add selection", "add_selection");
        DrawBinding(new Rect(right, rowY + 96f, columnWidth, 30f), "Repeat deploy", "repeat_deploy");
        DrawBinding(new Rect(right, rowY + 128f, columnWidth, 30f), "UI cycle", "toggle_ui");
        DrawBinding(new Rect(right, rowY + 160f, columnWidth, 30f), "Queue point", "queue_waypoint");
        DrawBinding(new Rect(right, rowY + 192f, columnWidth, 30f), "Assign group", "assign_group");
        DrawBinding(new Rect(right, rowY + 224f, columnWidth, 30f), "Stop order", "stop_order");
        DrawBinding(new Rect(right, rowY + 256f, columnWidth, 30f), "Same type", "same_type");
        DrawBinding(new Rect(right, rowY + 288f, columnWidth, 30f), "Cycle idle", "cycle_idle");
        DrawBinding(new Rect(right, rowY + 320f, columnWidth, 30f), "Map box select", "map_box_select");
        DrawBinding(
            new Rect(right, rowY + 352f, columnWidth, 30f),
            "Commander view",
            "toggle_commander",
            "Enter or leave the Commander view, also from the cockpit.");
        DrawBinding(new Rect(right, rowY + 384f, columnWidth, 30f), "Deselect all", "clear_selection");
        DrawBinding(
            new Rect(right, rowY + 416f, columnWidth, 30f),
            "Night vision",
            "night_vision",
            "The game's own night vision, usable from the RTS camera.");
        DrawBinding(
            new Rect(right, rowY + 448f, columnWidth, 30f),
            "Rotate building",
            "rotate_building",
            "While placing a building, turn the blueprint 45 degrees.");
        DrawBinding(
            new Rect(right, rowY + 480f, columnWidth, 30f),
            "Cancel build",
            "cancel_blueprint",
            "Over one of your blueprints, in the world or on the map: removes it for a full refund.");

        // The last free row; the right column runs into the reset buttons.
        DrawBinding(
            new Rect(left, rowY + 480f, columnWidth, 30f),
            "Ping",
            "ping",
            "Mark the spot or unit under the cursor for your whole team. Pings show in the cockpit too.");

        DrawBinding(new Rect(left, rowY + 512f, columnWidth, 30f), "Tactical map", "toggle_map");
        DrawBinding(new Rect(left, rowY + 544f, columnWidth, 30f), "Speed 1x", "speed_1", "Host only.");
        DrawBinding(new Rect(right, rowY + 512f, columnWidth, 30f), "Speed 2x", "speed_2", "Host only.");
        DrawBinding(new Rect(right, rowY + 544f, columnWidth, 30f), "Speed 4x", "speed_4", "Host only.");

        if (GUI.Button(new Rect(left, y + 652f, columnWidth, 32f), "RESET CAMERA + WINDOWS", CommanderUiTheme.Button))
        {
            ResetCameraBindings();
            bindingCapture = null;
        }

        if (GUI.Button(new Rect(right, y + 652f, columnWidth, 32f), "RESET ACTIONS", CommanderUiTheme.Button))
        {
            ResetActionBindings();
            bindingCapture = null;
        }
    }

    private void DrawBinding(Rect rect, string label, string binding, string tooltip = "")
    {
        float labelWidth = Mathf.Min(94f, rect.width * 0.36f);
        GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), new GUIContent(label, tooltip), CommanderUiTheme.Label);
        string buttonText = bindingCapture == binding ? "PRESS KEY..." : GetBinding(binding).ToString();
        if (GUI.Button(
            new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth - 34f, rect.height),
            buttonText,
            bindingCapture == binding ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            bindingCapture = binding;
        }

        if (GUI.Button(new Rect(rect.xMax - 28f, rect.y, 28f, rect.height), "X", CommanderUiTheme.Button))
        {
            SetBinding(binding, new KeyboardShortcut(KeyCode.None));
            bindingCapture = null;
        }
    }

    private void CaptureBindingInput()
    {
        if (bindingCapture == null)
        {
            return;
        }

        Event current = Event.current;
        KeyCode key;
        if (current.type == EventType.KeyDown)
        {
            if (current.keyCode == KeyCode.Escape)
            {
                bindingCapture = null;
                current.Use();
                return;
            }

            key = current.keyCode;
            if (key == KeyCode.None)
            {
                return;
            }
        }
        else if (current.type == EventType.MouseDown)
        {
            key = (KeyCode)((int)KeyCode.Mouse0 + current.button);
        }
        else
        {
            return;
        }

        List<KeyCode> modifiers = new();
        if (current.shift && key != KeyCode.LeftShift && key != KeyCode.RightShift)
        {
            modifiers.Add(KeyCode.LeftShift);
        }

        if (current.control && key != KeyCode.LeftControl && key != KeyCode.RightControl)
        {
            modifiers.Add(KeyCode.LeftControl);
        }

        if (current.alt && key != KeyCode.LeftAlt && key != KeyCode.RightAlt)
        {
            modifiers.Add(KeyCode.LeftAlt);
        }

        SetBinding(bindingCapture, new KeyboardShortcut(key, modifiers.ToArray()));
        bindingCapture = null;
        current.Use();
    }

    private void ResetUiLayout()
    {
        positionsInitialized = false;
        CommanderWindowScale.ResetExtents();
        supplyHeliUi.ResetPosition();
        airCommandUi.ResetPosition();
        navalPurchaseUi.ResetPosition();
        samSiteAnalyzerUi.ResetPosition();
        economyUi.ResetPosition();
        depotUi.ResetPosition();
        unitListUi.ResetPosition();
        CommanderAlertUi.Instance?.ResetPosition();
        CommanderTacticalMapService.Instance?.ResetLayoutPosition();
    }
}
