using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Per-window zoom: every Commander window gets a corner grip that scales the whole window,
/// text and all, the way the tactical map's grip scales the map. Right-click the grip for 100 %.
/// A window that asks for its size through <see cref="Size"/> also gets left, right and bottom edges
/// that change its width and height on their own, and <see cref="Splitter"/> bars between its
/// sections. Right-click any of those to hand it back to the window's own layout. On such a window
/// the grip drags width and height freely instead, and zooms with Ctrl held.
/// </summary>
/// <remarks>
/// The scale is a GUI matrix pivoted on the window's top-left corner, so a window's own layout
/// code keeps working in its unscaled units and only the hit tests need <see cref="Visual"/>.
/// Unity records the matrix per window at the <c>GUI.Window</c> call, which is also how the
/// global <see cref="CommanderUiScale"/> reaches windows drawn after it is popped.
/// </remarks>
internal static class CommanderWindowScale
{
    private const float MinScale = 0.6f;
    private const float MaxScale = 2.5f;
    private const float GripSize = 14f;
    private const float EdgeSize = 6f;
    private const float TitleHeight = 28f;
    private const float NotchLength = 48f;
    private const string SplitterHint = "Drag to give this space to the section beside it. Right-click to reset.";
    private const float NotchThickness = 3f;
    private const int WidthSlot = 0;
    private const int HeightSlot = 1;

    private static readonly Dictionary<int, float> Scales = new();
    private static readonly Dictionary<int, Rect> Rects = new();
    private static readonly Dictionary<int, (GUI.WindowFunction Inner, GUI.WindowFunction Outer)> Wrapped = new();
    private static readonly Dictionary<int, Vector2> MinSizes = new();

    /// <summary>What the player dragged: per window, slot 0 its width, 1 its height, 2 and up its sections.</summary>
    private static readonly Dictionary<(int Id, int Slot), float> Extents = new();
    private static bool loaded;

    private static int extentId;
    private static int extentSlot;
    private static float extentStart;
    private static float extentMin;
    private static float extentMax;
    private static bool extentAlongX;
    private static bool extentInverted;

    /// <summary>Dragging the left edge: the window's right side stays here, in GUI units.</summary>
    private static float extentRightEdge = -1f;
    private static Vector2 extentMouse;

    private static int resizingId;
    private static float resizeStartScale;
    private static Vector2 resizeStartMouse;
    private static bool resizingSize;
    private static Vector2 resizeStartSize;

    private static int draggingId;
    private static Vector2 dragOffset;

    /// <summary>The scaled window whose contents are drawing now, or 0 outside one.</summary>
    internal static int Current { get; private set; }

    /// <summary>The +/- buttons beside a window's "?": one zoom step, saved like the grip.</summary>
    internal static void Step(int id, float delta)
    {
        Scales[ScaleKey(id)] = Mathf.Clamp(Mathf.Round((Get(id) + delta) * 10f) / 10f, MinScale, MaxScale);
        Save();
    }

    internal static float Get(int id)
    {
        Load();
        return Scales.TryGetValue(ScaleKey(id), out float scale) ? scale : 1f;
    }

    /// <summary>
    /// The window's unscaled size: what the player dragged each axis to, else
    /// <paramref name="fallback"/>, the window's own layout. Asking is what gives it drag edges.
    /// </summary>
    internal static Vector2 Size(int id, Vector2 fallback, Vector2 min)
    {
        MinSizes[id] = min;
        float scale = Get(id);
        float width = Section(id, WidthSlot);
        float height = Section(id, HeightSlot);
        return new Vector2(
            width > 0f ? Mathf.Clamp(width, min.x, Mathf.Max(min.x, (CommanderUiScale.Width - 24f) / scale)) : fallback.x,
            height > 0f ? Mathf.Clamp(height, min.y, Mathf.Max(min.y, (CommanderUiScale.Height - 60f) / scale)) : fallback.y);
    }

    /// <summary>True once the player has set this window's height by hand.</summary>
    internal static bool HasHeight(int id)
    {
        return Section(id, HeightSlot) > 0f;
    }

    /// <summary>A section's dragged size, or 0 while the window still lays it out itself. Slots from 2 up are the window's own.</summary>
    internal static float Section(int id, int slot)
    {
        Load();
        return Extents.TryGetValue((id, slot), out float extent) ? extent : 0f;
    }

    /// <summary>A section's dragged size inside its limits, or <paramref name="fallback"/> if it was never dragged.</summary>
    internal static float Section(int id, int slot, float fallback, float min, float max)
    {
        float dragged = Section(id, slot);
        return dragged > 0f ? Mathf.Clamp(dragged, min, max) : fallback;
    }

    /// <summary>
    /// A drag bar inside a window, in the window's own coordinates, scroll views included.
    /// <paramref name="alongX"/> means the drag changes a width; <paramref name="inverted"/> is for
    /// a section after the bar, which grows as the bar moves back.
    /// </summary>
    internal static void Splitter(int id, int slot, Rect bar, bool alongX, bool inverted, float current, float min, float max)
    {
        Event input = Event.current;
        if (GUI.enabled && input.type == EventType.MouseDown && bar.Contains(input.mousePosition))
        {
            HandleExtentPress(id, slot, input, current, min, max, alongX, inverted);
        }

        GUI.Box(bar, new GUIContent(string.Empty, SplitterHint), GUIStyle.none);
        DrawNotch(bar, !alongX, extentId == id && extentSlot == slot);
    }

    /// <summary>RESET UI LAYOUT: every window back to its own width, height and sections.</summary>
    internal static void ResetExtents()
    {
        Load();
        Extents.Clear();
        Save();
    }

    /// <summary>Where the window really is on screen, in GUI units: its rect with the scale applied.</summary>
    internal static Rect Visual(int id, Rect rect)
    {
        float scale = Get(id);
        return new Rect(rect.x, rect.y, rect.width * scale, rect.height * scale);
    }

    /// <summary><see cref="CommanderUiTheme.ClampWindow"/> for a scaled window: keeps what is on screen on screen.</summary>
    internal static Rect Clamp(int id, Rect rect)
    {
        float scale = Get(id);
        Rect visual = CommanderUiTheme.ClampWindow(Visual(id, rect));
        return new Rect(visual.x, visual.y, visual.width / scale, visual.height / scale);
    }

    /// <summary>Drop-in for <c>GUI.Window(id, rect, draw, title, CommanderUiTheme.Window)</c>.</summary>
    internal static Rect Window(int id, Rect rect, GUI.WindowFunction draw, string title)
    {
        TickExtent();
        TickPointer(id, rect);
        float scale = Get(id);
        Rects[id] = rect;

        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = previous
            * Matrix4x4.TRS(rect.position, Quaternion.identity, new Vector3(scale, scale, 1f))
            * Matrix4x4.TRS(-(Vector3)rect.position, Quaternion.identity, Vector3.one);
        Rect result = GUI.Window(id, rect, Wrap(id, draw), title, CommanderUiTheme.Window);
        GUI.matrix = previous;

        if (extentId == id && extentRightEdge >= 0f)
        {
            result.x = extentRightEdge - (Section(id, WidthSlot) * scale);
        }

        // Unity's own title drag works in the window's scaled space, so a scaled window would
        // drift off the cursor. Once it starts moving, pin its corner to the cursor instead.
        if (result.position != rect.position || draggingId == id)
        {
            Vector2 mouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
            if (draggingId != id)
            {
                draggingId = id;
                dragOffset = mouse - rect.position;
            }

            result.position = mouse - dragOffset;
        }

        return result;
    }

    /// <summary>
    /// The same grip for top-level GUI that is not a <c>GUI.Window</c> (the selection bar): the
    /// caller applies <see cref="Get"/> as its own matrix and hit-tests through it.
    /// <paramref name="grip"/> is in the caller's unscaled space, <paramref name="size"/> its
    /// unscaled size, which is what the drag distance is measured against.
    /// </summary>
    internal static void Grip(int id, Rect grip, Vector2 size)
    {
        TickPointer(id, new Rect(Vector2.zero, size));
        DrawGrip(id, grip);
    }

    /// <summary>TRANSPORT is opened from AIR COMMAND, so it zooms with it: one scale for both.</summary>
    private static int ScaleKey(int id)
    {
        return id == CommanderSupplyHeliUi.WindowId ? CommanderAirCommandUi.WindowId : id;
    }

    private static void TickPointer(int id, Rect rect)
    {
        if (Input.GetMouseButton(0))
        {
            if (resizingId == id)
            {
                Vector2 delta = CommanderUiScale.ScreenToGui(Input.mousePosition) - resizeStartMouse;
                if (resizingSize)
                {
                    // Stops at the screen edge, as the edges do, or the clamp would slide the window.
                    float scale = Get(id);
                    Vector2 min = MinSizes[id];
                    Extents[(id, WidthSlot)] = Mathf.Clamp(resizeStartSize.x + (delta.x / scale), min.x, Mathf.Max(min.x, (CommanderUiScale.Width - 12f - rect.x) / scale));
                    Extents[(id, HeightSlot)] = Mathf.Clamp(resizeStartSize.y + (delta.y / scale), min.y, Mathf.Max(min.y, (CommanderUiScale.Height - 12f - rect.y) / scale));
                }
                else
                {
                    // Follows whichever axis the cursor pulled further, in or out. A plain Max only
                    // shrank when both axes were pulled in, so dragging left or up alone did nothing.
                    Vector2 start = rect.size * resizeStartScale;
                    float fx = (start.x + delta.x) / start.x;
                    float fy = (start.y + delta.y) / start.y;
                    float factor = Mathf.Abs(fx - 1f) >= Mathf.Abs(fy - 1f) ? fx : fy;
                    Scales[ScaleKey(id)] = Mathf.Clamp(resizeStartScale * factor, MinScale, MaxScale);
                }
            }

            return;
        }

        if (resizingId == id)
        {
            Save();
        }

        resizingId = resizingId == id ? 0 : resizingId;
        draggingId = draggingId == id ? 0 : draggingId;
    }

    private static void TickExtent()
    {
        if (extentId == 0)
        {
            return;
        }

        if (!Input.GetMouseButton(0))
        {
            extentId = 0;
            extentRightEdge = -1f;
            Save();
            return;
        }

        Vector2 delta = (CommanderUiScale.ScreenToGui(Input.mousePosition) - extentMouse) / Get(extentId);
        float moved = extentAlongX ? delta.x : delta.y;
        Extents[(extentId, extentSlot)] = Mathf.Clamp(extentStart + (extentInverted ? -moved : moved), extentMin, extentMax);
    }

    private static void HandleExtentPress(int id, int slot, Event input, float current, float min, float max, bool alongX, bool inverted)
    {
        if (input.button == 1)
        {
            Extents.Remove((id, slot));
            Save();
        }
        else if (input.button == 0)
        {
            extentId = id;
            extentSlot = slot;
            extentStart = current;
            extentMin = min;
            extentMax = max;
            extentAlongX = alongX;
            extentInverted = inverted;
            extentMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        }

        input.Use();
    }

    /// <summary>A short mark at the middle of a bar; under the cursor or dragged, the whole bar lights.</summary>
    private static void DrawNotch(Rect bar, bool lengthwise, bool dragging)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        Vector2 centre = bar.center;
        if (dragging || bar.Contains(Event.current.mousePosition))
        {
            CommanderUiTheme.DrawAccentLine(lengthwise
                ? new Rect(bar.x, centre.y - 1f, bar.width, 2f)
                : new Rect(centre.x - 1f, bar.y, 2f, bar.height));
            return;
        }

        // Accent even at rest: in the theme's rule colour the marks were lost beside a scroll bar.
        CommanderUiTheme.DrawAccentLine(
            lengthwise
                ? new Rect(centre.x - (NotchLength * 0.5f), centre.y - (NotchThickness * 0.5f), NotchLength, NotchThickness)
                : new Rect(centre.x - (NotchThickness * 0.5f), centre.y - (NotchLength * 0.5f), NotchThickness, NotchLength));
    }

    private static GUI.WindowFunction Wrap(int id, GUI.WindowFunction draw)
    {
        if (Wrapped.TryGetValue(id, out (GUI.WindowFunction Inner, GUI.WindowFunction Outer) cached) && cached.Inner.Equals(draw))
        {
            return cached.Outer;
        }

        void Outer(int windowId)
        {
            Rect known = Rects.TryGetValue(windowId, out Rect stored) ? stored : default;
            Vector2 size = known.size;
            bool edges = MinSizes.TryGetValue(windowId, out Vector2 min);
            Rect left = new(0f, TitleHeight, EdgeSize, size.y - TitleHeight - EdgeSize);
            Rect right = new(size.x - EdgeSize, TitleHeight, EdgeSize, size.y - TitleHeight - GripSize - 2f);
            Rect bottom = new(0f, size.y - EdgeSize, size.x - GripSize - 2f, EdgeSize);
            Rect grip = new(size.x - GripSize - 2f, size.y - GripSize - 2f, GripSize, GripSize);

            // Pressed before the window draws, unlike the plain grip: the AIR window's scroll bar
            // runs down the right edge into the corner and would take the press otherwise. An edge
            // stops at the screen edge, or the clamp would slide the whole window away from the cursor.
            Event input = Event.current;
            float scale = Get(windowId);
            if (edges && input.type == EventType.MouseDown)
            {
                if (grip.Contains(input.mousePosition))
                {
                    HandleGripPress(windowId, grip);
                }
                else if (left.Contains(input.mousePosition))
                {
                    float rightEdge = known.x + (size.x * scale);
                    HandleExtentPress(windowId, WidthSlot, input, size.x, min.x, Mathf.Max(min.x, (rightEdge - 12f) / scale), alongX: true, inverted: true);
                    extentRightEdge = input.button == 0 ? rightEdge : -1f;
                }
                else if (right.Contains(input.mousePosition))
                {
                    HandleExtentPress(windowId, WidthSlot, input, size.x, min.x, Mathf.Max(min.x, (CommanderUiScale.Width - 12f - known.x) / scale), alongX: true, inverted: false);
                }
                else if (bottom.Contains(input.mousePosition))
                {
                    HandleExtentPress(windowId, HeightSlot, input, size.y, min.y, Mathf.Max(min.y, (CommanderUiScale.Height - 12f - known.y) / scale), alongX: false, inverted: false);
                }
            }

            Current = windowId;
            try
            {
                draw(windowId);
            }
            finally
            {
                Current = 0;
            }

            if (edges)
            {
                DrawNotch(left, lengthwise: false, extentId == windowId && extentRightEdge >= 0f);
                DrawNotch(right, lengthwise: false, extentId == windowId && extentSlot == WidthSlot && extentRightEdge < 0f);
                DrawNotch(bottom, lengthwise: true, extentId == windowId && extentSlot == HeightSlot);
            }

            DrawGrip(windowId, grip);
        }

        Wrapped[id] = (draw, Outer);
        return Wrapped[id].Outer;
    }

    /// <summary>Drawn last, so a control under the corner still gets its click first.</summary>
    private static void DrawGrip(int id, Rect grip)
    {
        GUI.Box(grip, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(grip, 1f);
        HandleGripPress(id, grip);
    }

    private static void HandleGripPress(int id, Rect grip)
    {
        Event current = Event.current;
        if (current.type != EventType.MouseDown || !grip.Contains(current.mousePosition))
        {
            return;
        }

        if (current.button == 1)
        {
            Scales.Remove(ScaleKey(id));
            Extents.Remove((id, WidthSlot));
            Extents.Remove((id, HeightSlot));
            Save();
        }
        else if (current.button == 0)
        {
            resizingId = id;
            resizeStartScale = Get(id);
            resizeStartMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
            resizingSize = !current.control && MinSizes.ContainsKey(id);
            resizeStartSize = resizingSize ? Rects[id].size : Vector2.zero;
        }

        current.Use();
    }

    /// <summary>Saved as "id=scale;id=scale" under UI/WindowScales.</summary>
    private static void Load()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        foreach (string pair in CommanderSettings.WindowScales.Split(';'))
        {
            string[] parts = pair.Split('=');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale))
            {
                Scales[id] = Mathf.Clamp(scale, MinScale, MaxScale);
            }
        }

        foreach (string pair in CommanderSettings.WindowSizes.Split(';'))
        {
            string[] parts = pair.Split('=', ':');
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float extent))
            {
                Extents[(id, slot)] = extent;
            }
        }

        // AIR COMMAND used to be taller than the screen with only the zoom grip to fit it, so saved
        // configs hold it at the 0.6 floor with unreadable text. It has edges and a scroll bar now.
        if (!CommanderSettings.AirWindowZoomCleared)
        {
            CommanderSettings.AirWindowZoomCleared = true;
            Scales.Remove(CommanderAirCommandUi.WindowId);
            Extents.Remove((CommanderAirCommandUi.WindowId, HeightSlot));
            Save();
        }
    }

    private static void Save()
    {
        List<string> pairs = new();
        foreach (KeyValuePair<int, float> entry in Scales)
        {
            pairs.Add(entry.Key.ToString(CultureInfo.InvariantCulture) + "=" + entry.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        CommanderSettings.WindowScales = string.Join(";", pairs);

        pairs.Clear();
        foreach (KeyValuePair<(int Id, int Slot), float> entry in Extents)
        {
            pairs.Add(entry.Key.Id.ToString(CultureInfo.InvariantCulture) + ":" + entry.Key.Slot.ToString(CultureInfo.InvariantCulture) + "=" + entry.Value.ToString("0", CultureInfo.InvariantCulture));
        }

        CommanderSettings.WindowSizes = string.Join(";", pairs);
    }
}
