using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The single list of every shortcut the mod responds to, rendered by the SHORTCUTS tab.
/// Keys are read live from <see cref="CommanderSettings"/>, so a rebound key shows its new
/// value here without any extra bookkeeping.
/// </summary>
/// <remarks>
/// Adding a binding means adding a row here as well, or the reference quietly goes stale.
/// </remarks>
internal static class CommanderShortcutReference
{
    internal static void Collect(List<Entry> buffer)
    {
        buffer.Clear();

        buffer.Add(Entry.Section("CAMERA"));
        buffer.Add(new Entry("Move camera", Keys(
            CommanderSettings.CameraForward,
            CommanderSettings.CameraLeft,
            CommanderSettings.CameraBackward,
            CommanderSettings.CameraRight)));
        buffer.Add(new Entry("Rise / descend", Keys(CommanderSettings.CameraUp, CommanderSettings.CameraDown)));
        buffer.Add(new Entry("Speed boost", Key(CommanderSettings.CameraBoost), "Hold."));
        buffer.Add(new Entry("Zoom", "Mouse wheel", "Moves toward whatever the cursor is over."));
        buffer.Add(new Entry(
            "Look around",
            Key(CommanderSettings.CameraFreeLook),
            "Hold: turns the camera in place."));
        buffer.Add(new Entry(
            "Edge scroll",
            "Cursor to a screen edge",
            CommanderSettings.CameraEdgeScroll ? "On." : "Off in Settings > Camera."));
        buffer.Add(new Entry(
            "Centre on selection",
            Key(CommanderSettings.CameraCenterFollow),
            "Tap to centre, hold to centre and follow."));
        buffer.Add(new Entry(
            "Recall camera view",
            "F1 - F4",
            CommanderSettings.CameraBookmarks ? "Four saved viewpoints." : "Disabled in Settings > Gameplay."));
        buffer.Add(new Entry(
            "Store camera view",
            Modified(CommanderSettings.AssignGroupModifier, "F1 - F4"),
            "Saves where the camera is now."));
        buffer.Add(new Entry("Fullscreen map", "M", "Base-game binding, not remapped here."));

        buffer.Add(Entry.Section("SELECTION"));
        buffer.Add(new Entry(
            "Select a unit",
            Key(CommanderSettings.PrimaryAction),
            "Works in the 3D view, on the map and in the Order of Battle."));
        buffer.Add(new Entry(
            "Box select (3D view)",
            "Drag " + Key(CommanderSettings.PrimaryAction),
            "A box that catches any friendly selects only friendlies."));
        buffer.Add(new Entry(
            "Box select (map)",
            Modified(CommanderSettings.MapBoxSelect, "Drag " + Key(CommanderSettings.PrimaryAction)),
            "A plain drag on the map pans it instead."));
        buffer.Add(new Entry("Add to selection", Modified(CommanderSettings.AddToSelection, Key(CommanderSettings.PrimaryAction))));
        buffer.Add(new Entry(
            "Remove one unit",
            Modified(CommanderSettings.AddToSelection, Key(CommanderSettings.PrimaryAction)),
            "On a unit that is already selected."));
        buffer.Add(new Entry("Select same type on screen", "Double-click " + Key(CommanderSettings.PrimaryAction)));
        buffer.Add(new Entry(
            "Select same type anywhere",
            Modified(CommanderSettings.SelectSameType, Key(CommanderSettings.PrimaryAction)),
            "Every one the faction owns."));
        buffer.Add(new Entry(
            "Narrow to one type",
            "Click a type chip",
            "In the selection bar. Hold the add key to drop that type instead."));
        buffer.Add(new Entry(
            "Jump to an idle unit",
            Key(CommanderSettings.CycleIdleUnit),
            "Next friendly ground or naval unit with no RTS order."));
        buffer.Add(new Entry(
            "Ping for your team",
            Key(CommanderSettings.PingKey),
            "On a spot or a unit, in the 3D view or on the map. Teammates see it in the cockpit too."));
        buffer.Add(new Entry(
            "Clear the selection",
            Key(CommanderSettings.ClearSelection),
            "Or " + Key(CommanderSettings.PrimaryAction) + " on empty ground."));

        buffer.Add(Entry.Section("CONTROL GROUPS"));
        buffer.Add(new Entry(
            "Recall group",
            "1 - 9",
            CommanderSettings.GroupHotkeys ? "A unit belongs to exactly one group." : "Disabled in Settings > Gameplay."));
        buffer.Add(new Entry("Store group", Modified(CommanderSettings.AssignGroupModifier, "1 - 9")));
        buffer.Add(new Entry("Add group to selection", Modified(CommanderSettings.AddToSelection, "1 - 9")));

        buffer.Add(Entry.Section("ORDERS"));
        buffer.Add(new Entry(
            "Travel point",
            Key(CommanderSettings.SecondaryAction),
            "Replaces the route with a single point."));
        buffer.Add(new Entry(
            "Queue travel point",
            Modified(CommanderSettings.QueueWaypoint, Key(CommanderSettings.SecondaryAction)),
            "Appends, so a full path can be planned."));
        buffer.Add(new Entry(
            "Attack order",
            Key(CommanderSettings.SecondaryAction),
            "On a hostile unit, building or objective."));
        buffer.Add(new Entry(
            "Guard order",
            Key(CommanderSettings.SecondaryAction),
            CommanderSettings.GuardOrders ? "On a friendly unit; the selection escorts it." : "Disabled in Settings > Gameplay."));
        buffer.Add(new Entry(
            "Stop and hold",
            Key(CommanderSettings.StopOrder),
            "Cancels RTS orders and holds the ground."));

        buffer.Add(Entry.Section("PRODUCTION AND UNITS"));
        buffer.Add(new Entry(
            "Repeat deployment",
            Key(CommanderSettings.RepeatDeployment),
            "Hold while placing a supply target, siting a building or placing an air mission area."));
        buffer.Add(new Entry(
            "Rotate building",
            Key(CommanderSettings.RotateBuilding),
            "Turns the blueprint 45 degrees while you place it. The next one keeps the same facing."));
        buffer.Add(new Entry(
            "Cancel blueprint",
            Key(CommanderSettings.CancelBlueprint),
            "Over one of your blueprints, in the world or on the map: removes it for a full refund."));
        buffer.Add(new Entry(
            "Cancel placement",
            Key(CommanderSettings.SecondaryAction),
            "Backs out of an armed build, supply or area placement. Escape also works."));
        buffer.Add(new Entry(
            "Set depot rally point",
            Key(CommanderSettings.SecondaryAction),
            "With only a vehicle depot selected, in the world or on the map."));

        buffer.Add(Entry.Section("WINDOWS"));
        buffer.Add(new Entry("Build", Key(CommanderSettings.OpenBuild), "Opens or closes BUILD."));
        buffer.Add(new Entry("Buy ground units", Key(CommanderSettings.OpenGroundUnits), "Selects the nearest depot."));
        buffer.Add(new Entry("Air Command", Key(CommanderSettings.OpenAirCommand), "Opens or closes AIR COMMAND."));
        buffer.Add(new Entry("Buy ships", Key(CommanderSettings.OpenNaval), "Needs a naval dock."));
        buffer.Add(new Entry("Order of Battle", Key(CommanderSettings.OpenOrderOfBattle), "Opens or closes the unit list."));
        buffer.Add(new Entry("Tactical map", Key(CommanderSettings.ToggleTacticalMap), "Opens or closes the map window."));
        buffer.Add(new Entry("Buy", "Enter", "Presses the buy button in a depot, the naval window, or AIR COMMAND when no area is needed."));

        buffer.Add(Entry.Section("INTERFACE"));
        buffer.Add(new Entry(
            "Game speed 1x / 2x / 4x",
            Keys(CommanderSettings.GameSpeed1, CommanderSettings.GameSpeed2, CommanderSettings.GameSpeed4),
            "Host only."));
        buffer.Add(new Entry("Night vision", Key(CommanderSettings.ToggleNightVision), "The game's own night vision."));
        buffer.Add(new Entry(
            "Cycle UI visibility",
            Key(CommanderSettings.ToggleUi),
            "Everything, RTS UI hidden, all UI hidden."));
        buffer.Add(new Entry(
            "Enter / leave Commander",
            Key(CommanderSettings.ToggleCommander) + " or CMD button",
            "The key also works in the cockpit; the aircraft holds its course until you return."));
    }

    private static string Key(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey == KeyCode.None ? "unbound" : shortcut.ToString();
    }

    private static string Keys(params KeyboardShortcut[] shortcuts)
    {
        string result = string.Empty;
        for (int i = 0; i < shortcuts.Length; i++)
        {
            result += i == 0 ? Key(shortcuts[i]) : " / " + Key(shortcuts[i]);
        }

        return result;
    }

    private static string Modified(KeyboardShortcut modifier, string key)
    {
        return modifier.MainKey == KeyCode.None ? key : $"{modifier.MainKey} + {key}";
    }

    internal readonly struct Entry
    {
        internal Entry(string action, string keys, string note = "")
            : this(action, keys, note, false)
        {
        }

        private Entry(string action, string keys, string note, bool section)
        {
            Action = action;
            Keys = keys;
            Note = note;
            IsSection = section;
        }

        internal string Action { get; }

        internal string Keys { get; }

        internal string Note { get; }

        internal bool IsSection { get; }

        internal static Entry Section(string title)
        {
            return new(title, string.Empty, string.Empty, true);
        }
    }
}
