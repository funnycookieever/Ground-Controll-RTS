using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Central IMGUI look for every Commander surface, modelled on the base game's own menus rather
/// than on a HUD overlay: solid slate plates with a 1px edge, a lighter header strip under a thin
/// accent line, the game's own fonts (Brass Mono for titles and figures, Barlow for everything a
/// player reads), and colour only where it means something: green to confirm, red to destroy,
/// the accent for whatever is switched on.
/// </summary>
internal static class CommanderUiTheme
{
    internal const float CardGap = 6f;
    private const float MinCardWidth = 150f;

    /// <summary>Fixed pixel rows at the top of the window texture: accent line, title band and its divider.</summary>
    private const int TitleBand = 28;

    /// <summary>Every texture and the skin this theme made, destroyed on a rebuild.</summary>
    private static readonly List<Object> Owned = new();

    // Fixed text colours. The player picks the accent and the plate; text is not themable,
    // because coloured body text on a coloured plate is the first thing that stops reading.
    private static readonly Color TextColor = new(0.91f, 0.93f, 0.94f, 1f);
    private static readonly Color MutedText = new(0.60f, 0.65f, 0.69f, 1f);
    private static readonly Color GreenFill = new(0.15f, 0.40f, 0.29f, 1f);
    private static readonly Color GreenEdge = new(0.34f, 0.68f, 0.51f, 1f);
    private static readonly Color RedFill = new(0.38f, 0.10f, 0.11f, 1f);
    private static readonly Color RedEdge = new(0.80f, 0.33f, 0.33f, 1f);

    private static Color neon;
    private static Color background;
    private static Color edge;

    /// <summary>Settings > Visuals > Plain UI: opaque plates, 2px solid edges, bigger text.</summary>
    private static bool plain;

    private static bool initialized;
    private static bool dirty;
    private static GUISkin? baseSkin;
    private static Font? titleFont;
    private static Font? bodyFont;
    private static float nextFontProbe;

    private static Texture2D? windowTexture;
    private static Texture2D? panelTexture;
    private static Texture2D? insetTexture;
    private static Texture2D? buttonTexture;
    private static Texture2D? buttonHoverTexture;
    private static Texture2D? buttonActiveTexture;
    private static Texture2D? primaryTexture;
    private static Texture2D? primaryHoverTexture;
    private static Texture2D? selectedTexture;
    private static Texture2D? dangerTexture;
    private static Texture2D? dangerHoverTexture;
    private static Texture2D? chromeHoverTexture;
    private static Texture2D? toastTexture;
    private static Texture2D? trackTexture;
    private static Texture2D? thumbTexture;
    private static Texture2D? toggleOnTexture;
    private static Texture2D? borderTexture;

    internal static GUIStyle Window { get; private set; } = null!;

    internal static GUIStyle Panel { get; private set; } = null!;

    /// <summary>A recessed well: lists, readouts and previews sit in one of these.</summary>
    internal static GUIStyle Inset { get; private set; } = null!;

    internal static GUIStyle Button { get; private set; } = null!;

    internal static GUIStyle PrimaryButton { get; private set; } = null!;

    internal static GUIStyle DangerButton { get; private set; } = null!;

    internal static GUIStyle SelectedButton { get; private set; } = null!;

    /// <summary>Flat title-bar button (close, help): no plate until hovered.</summary>
    internal static GUIStyle ChromeButton { get; private set; } = null!;

    internal static GUIStyle Label { get; private set; } = null!;

    internal static GUIStyle MutedLabel { get; private set; } = null!;

    internal static GUIStyle WarningLabel { get; private set; } = null!;

    internal static GUIStyle Header { get; private set; } = null!;

    /// <summary>Small section caption in the title font, drawn over a hairline.</summary>
    internal static GUIStyle Caption { get; private set; } = null!;

    /// <summary>Large figures: funds, counts, prices.</summary>
    internal static GUIStyle Figure { get; private set; } = null!;

    internal static GUIStyle Money { get; private set; } = null!;

    internal static GUIStyle HelpButton { get; private set; } = null!;

    internal static GUIStyle Toggle { get; private set; } = null!;

    /// <summary>The refusal banner: a red plate the player cannot mistake for a status line.</summary>
    internal static GUIStyle Alert { get; private set; } = null!;

    /// <summary>Plateless label for markers drawn over the 3D scene. It carries its own dark
    /// halo instead of a background box, because a box big enough to read is a box big enough
    /// to hide whatever the marker is pointing at.</summary>
    internal static GUIStyle WorldLabel { get; private set; } = null!;

    /// <summary>Neutral dark plate for alert toasts, which tint themselves with GUI.color.</summary>
    internal static GUIStyle Toast { get; private set; } = null!;

    /// <summary>Skin installed for the OnGUI pass so scrollbars and sliders match the theme.</summary>
    internal static GUISkin Skin { get; private set; } = null!;

    internal static Color Accent => neon;

    internal static Color Friendly => GameAssets.i != null ? GameAssets.i.HUDFriendly : Accent;

    internal static Texture2D BorderTexture
    {
        get
        {
            Ensure();
            return borderTexture!;
        }
    }

    /// <summary>Edge thickness of every plate, in texture pixels.</summary>
    private static int Edge => plain ? 2 : 1;

    /// <summary>Rebuild the theme from Settings > Visuals on the next draw pass.</summary>
    internal static void Refresh()
    {
        dirty = true;
    }

    internal static void Ensure()
    {
        // The game's fonts load with its menus, which can be after the first draw: keep looking,
        // once a second, and rebuild when they turn up.
        if ((titleFont == null || bodyFont == null) && Time.realtimeSinceStartup >= nextFontProbe)
        {
            nextFontProbe = Time.realtimeSinceStartup + 1f;
            if (FindFonts())
            {
                dirty = true;
            }
        }

        // A rebuild destroys the old skin, so it waits for a moment the skin is not installed:
        // the mode controller calls Ensure before it installs it, every frame.
        if (initialized && !(dirty && GUI.skin != Skin))
        {
            return;
        }

        baseSkin ??= GUI.skin;
        for (int i = 0; i < Owned.Count; i++)
        {
            Object.Destroy(Owned[i]);
        }

        Owned.Clear();
        initialized = true;
        dirty = false;

        plain = CommanderSettings.PlainUi;
        neon = Opaque(CommanderSettings.AccentColor);
        background = Opaque(CommanderSettings.BackgroundColor);
        edge = plain ? Color.Lerp(TextColor, background, 0.35f) : Lift(background, 0.20f);

        windowTexture = MakeWindowSlice();
        panelTexture = MakeSlice(Alpha(Lift(background, 0.035f), 0.96f), Lift(background, 0.13f));
        insetTexture = MakeSlice(Alpha(Lift(background, -0.03f), 0.96f), Lift(background, 0.08f));
        toastTexture = MakeSlice(Alpha(Lift(background, 0.03f), 0.96f), edge);
        buttonTexture = MakeSlice(Lift(background, 0.09f), Lift(background, 0.19f));
        buttonHoverTexture = MakeSlice(Lift(background, 0.16f), Lift(background, 0.34f));
        buttonActiveTexture = MakeSlice(Lift(background, 0.05f), neon);
        primaryTexture = MakeSlice(GreenFill, GreenEdge);
        primaryHoverTexture = MakeSlice(Color.Lerp(GreenFill, GreenEdge, 0.35f), Color.Lerp(GreenEdge, Color.white, 0.3f));
        selectedTexture = MakeSelectedSlice();
        dangerTexture = MakeSlice(RedFill, Color.Lerp(RedFill, RedEdge, 0.6f));
        dangerHoverTexture = MakeSlice(Color.Lerp(RedFill, RedEdge, 0.35f), RedEdge);
        chromeHoverTexture = MakeSlice(Lift(background, 0.22f), Lift(background, 0.22f));
        trackTexture = MakeSlice(Alpha(Lift(background, -0.02f), 0.9f), Lift(background, 0.08f));
        thumbTexture = MakeSlice(Lift(background, 0.30f), Lift(background, 0.42f));
        toggleOnTexture = MakeSlice(Shade(0.45f), neon, 16);
        borderTexture = MakeSlice(neon, neon, 2);

        // Plain text is one size up; more than that overflows the fixed-height rows.
        int bump = plain ? 1 : 0;
        Color muted = plain ? Color.Lerp(MutedText, TextColor, 0.5f) : MutedText;
        Font? title = titleFont != null ? titleFont : bodyFont;

        Label = new GUIStyle(baseSkin.label)
        {
            font = bodyFont,
            fontSize = 13 + bump,
            normal = { textColor = TextColor },
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft,
        };

        // Barlow drops its word spaces below 13 px ("Clickavehicle"), so nothing reads smaller.
        MutedLabel = new GUIStyle(Label)
        {
            fontSize = 13 + bump,
            normal = { textColor = muted },
        };
        WarningLabel = new GUIStyle(Label)
        {
            fontSize = 13 + bump,
            normal = { textColor = new Color(1f, 0.74f, 0.32f, 1f) },
        };
        Header = new GUIStyle(Label)
        {
            font = title,
            fontSize = 13 + bump,
            normal = { textColor = TextColor },
            alignment = TextAnchor.MiddleLeft,
        };
        Caption = new GUIStyle(Header)
        {
            fontSize = 11 + bump,
            wordWrap = false,
            normal = { textColor = muted },
        };
        Figure = new GUIStyle(Header)
        {
            fontSize = 17 + bump,
            wordWrap = false,
        };
        Window = new GUIStyle(baseSkin.window)
        {
            font = title,
            normal = { background = windowTexture, textColor = TextColor },
            onNormal = { background = windowTexture, textColor = TextColor },
            contentOffset = Vector2.zero,
            border = new RectOffset(Edge, Edge, TitleBand + 1, Edge),
            padding = new RectOffset(12, 10, 8, 10),
            fontSize = 13 + bump,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.UpperLeft,
        };
        Panel = new GUIStyle(baseSkin.box)
        {
            font = bodyFont,
            normal = { background = panelTexture, textColor = TextColor },
            border = EdgeOffset(),
            padding = new RectOffset(8, 8, 8, 8),
        };
        Inset = new GUIStyle(Panel)
        {
            normal = { background = insetTexture, textColor = TextColor },
        };
        Button = new GUIStyle(baseSkin.button)
        {
            font = bodyFont,
            normal = { background = buttonTexture, textColor = TextColor },
            hover = { background = buttonHoverTexture, textColor = Color.white },
            active = { background = buttonActiveTexture, textColor = Color.white },
            focused = { background = buttonTexture, textColor = TextColor },
            border = EdgeOffset(),
            padding = new RectOffset(8, 8, 4, 4),
            fontSize = 13 + bump,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };
        PrimaryButton = new GUIStyle(Button)
        {
            normal = { background = primaryTexture, textColor = Color.white },
            hover = { background = primaryHoverTexture, textColor = Color.white },
            focused = { background = primaryTexture, textColor = Color.white },
        };
        DangerButton = new GUIStyle(Button)
        {
            normal = { background = dangerTexture, textColor = new Color(1f, 0.88f, 0.86f) },
            hover = { background = dangerHoverTexture, textColor = Color.white },
            active = { background = dangerHoverTexture, textColor = Color.white },
            focused = { background = dangerTexture, textColor = new Color(1f, 0.88f, 0.86f) },
        };
        SelectedButton = new GUIStyle(Button)
        {
            normal = { background = selectedTexture, textColor = Color.white },
            hover = { background = selectedTexture, textColor = Color.white },
            focused = { background = selectedTexture, textColor = Color.white },
            border = new RectOffset(Edge, Edge, Edge, 2),
        };
        ChromeButton = new GUIStyle(Button)
        {
            font = title,
            normal = { background = null, textColor = muted },
            hover = { background = chromeHoverTexture, textColor = Color.white },
            active = { background = chromeHoverTexture, textColor = Color.white },
            focused = { background = null, textColor = muted },
            padding = new RectOffset(0, 0, 0, 0),
        };
        Toast = new GUIStyle(Button)
        {
            normal = { background = toastTexture, textColor = Color.white },
            hover = { background = toastTexture, textColor = Color.white },
            active = { background = toastTexture, textColor = Color.white },
            focused = { background = toastTexture, textColor = Color.white },
            border = EdgeOffset(),
        };
        Alert = new GUIStyle(Label)
        {
            normal = { background = dangerTexture, textColor = new Color(1f, 0.82f, 0.80f, 1f) },
            border = EdgeOffset(),
            padding = new RectOffset(16, 16, 9, 9),
            fontSize = 17 + bump,
            alignment = TextAnchor.MiddleCenter,
        };
        HelpButton = new GUIStyle(ChromeButton);

        // No plate of its own: Check draws the box, centred on the row, and this is the label.
        Toggle = new GUIStyle(baseSkin.toggle)
        {
            font = bodyFont,
            normal = { background = null, textColor = muted },
            hover = { background = null, textColor = Color.white },
            active = { background = null, textColor = TextColor },
            onNormal = { background = null, textColor = TextColor },
            onHover = { background = null, textColor = Color.white },
            onActive = { background = null, textColor = TextColor },
            border = new RectOffset(0, 0, 0, 0),
            overflow = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(30, 6, 2, 2),
            contentOffset = Vector2.zero,
            alignment = TextAnchor.MiddleLeft,
            fontSize = 13 + bump,
            wordWrap = true,
        };
        WorldLabel = new GUIStyle(baseSkin.label)
        {
            font = title,
            fontSize = 11 + bump,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = false,
            padding = new RectOffset(0, 0, 0, 0),
            normal = { background = null, textColor = Color.white },
        };
        Money = new GUIStyle(Panel)
        {
            font = title,
            fontSize = 15 + bump,
            alignment = TextAnchor.MiddleCenter,
        };

        BuildSkin();
        Owned.Add(Skin);
    }

    /// <summary>Keeps a window on screen and below the command bar, which owns the top 44 units.</summary>
    internal static Rect ClampWindow(Rect rect, float margin = 12f)
    {
        const float top = 48f;
        rect.width = Mathf.Min(rect.width, Mathf.Max(160f, CommanderUiScale.Width - (margin * 2f)));
        rect.height = Mathf.Min(rect.height, Mathf.Max(120f, CommanderUiScale.Height - top - margin));
        rect.x = Mathf.Clamp(rect.x, margin, Mathf.Max(margin, CommanderUiScale.Width - rect.width - margin));
        rect.y = Mathf.Clamp(rect.y, top, Mathf.Max(top, CommanderUiScale.Height - rect.height - margin));
        return rect;
    }

    /// <summary>Section caption: small title-font text with a hairline running out to the right.</summary>
    internal static float DrawSection(Rect rect, string title)
    {
        Ensure();
        Vector2 size = Caption.CalcSize(new GUIContent(title));
        GUI.Label(new Rect(rect.x, rect.y, size.x + 2f, 18f), title, Caption);
        Color previous = GUI.color;
        GUI.color = Lift(background, 0.20f);
        GUI.DrawTexture(new Rect(rect.x + size.x + 8f, rect.y + 9f, Mathf.Max(0f, rect.width - size.x - 8f), 1f), Texture2D.whiteTexture);
        GUI.color = previous;
        return rect.y + 22f;
    }

    /// <summary>Horizontal fill bar, for condition, ammunition, stock and progress.</summary>
    internal static void DrawBar(Rect rect, float fraction, Color fill)
    {
        Ensure();
        Color previous = GUI.color;
        GUI.color = Lift(background, 0.05f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fraction), rect.height), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    /// <summary>The width of a card in a grid filling <paramref name="width"/>: as many columns as
    /// fit at the smallest card, then shared out so the rows always reach both edges.</summary>
    internal static float CardWidth(float width, out int columns)
    {
        columns = Mathf.Max(1, Mathf.FloorToInt((width + CardGap) / (MinCardWidth + CardGap)));
        return (width - (CardGap * (columns - 1))) / columns;
    }

    /// <summary>A card's height follows its width, so a wider window gives bigger pictures and not
    /// stretched cards. <see cref="DrawCard"/> keeps 42 of it for the name and price.</summary>
    internal static float CardHeight(float cardWidth)
    {
        return Mathf.Clamp((cardWidth * 0.55f) + 42f, 124f, 200f);
    }

    /// <summary>
    /// A purchase card: the unit's picture, its name, a price line in the title font and a small
    /// corner tag. <paramref name="lockText"/> dims the card and says why it cannot be had;
    /// <paramref name="selected"/> lights it. Returns true on a click anywhere on the card.
    /// </summary>
    internal static bool DrawCard(Rect rect, UnitDefinition? definition, string title, string price, string corner, bool selected, string? lockText = null, string? buildTime = null)
    {
        Ensure();
        bool hovered = rect.Contains(Event.current.mousePosition) && GUI.enabled;
        GUI.Box(rect, string.Empty, selected ? SelectedButton : Inset);
        Rect picture = new(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 42f);
        CommanderUnitPreview.DrawStill(picture, definition);
        if (hovered && !selected)
        {
            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            DrawFrame(rect, 1f);
            GUI.color = previous;
        }

        if (corner.Length > 0)
        {
            GUI.Label(new Rect(rect.x + 7f, rect.y + 4f, rect.width - 14f, 16f), corner, Caption);
        }

        if (!string.IsNullOrEmpty(buildTime))
        {
            TextAnchor anchor = Caption.alignment;
            Caption.alignment = TextAnchor.UpperRight;
            GUI.Label(new Rect(rect.x + 7f, rect.y + 4f, rect.width - 14f, 16f), buildTime, Caption);
            Caption.alignment = anchor;
        }

        FitLabel(new Rect(rect.x + 8f, rect.yMax - 40f, rect.width - 16f, 20f), title, Label);
        GUI.Label(new Rect(rect.x + 8f, rect.yMax - 21f, rect.width - 16f, 18f), price, Header);
        if (!string.IsNullOrEmpty(lockText) || !GUI.enabled)
        {
            Color previous = GUI.color;
            GUI.color = new Color(background.r * 0.5f, background.g * 0.5f, background.b * 0.5f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
            if (!string.IsNullOrEmpty(lockText))
            {
                TextAnchor anchor = Caption.alignment;
                Caption.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(rect.x, picture.center.y - 10f, rect.width, 20f), lockText, Caption);
                Caption.alignment = anchor;
            }
        }

        return GUI.Button(rect, GUIContent.none, GUIStyle.none);
    }

    /// <summary>The colour a 0-1 health-style fraction reads as: green, amber, red.</summary>
    internal static Color StatusColor(float fraction)
    {
        return fraction > 0.6f ? new Color(0.36f, 0.76f, 0.52f, 1f)
            : fraction > 0.3f ? new Color(0.92f, 0.70f, 0.28f, 1f)
            : new Color(0.88f, 0.32f, 0.30f, 1f);
    }

    internal static void DrawFrame(Rect rect, float thickness = 2f)
    {
        Ensure();
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), borderTexture!);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), borderTexture!);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), borderTexture!);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), borderTexture!);
    }

    /// <summary>
    /// A check box: <see cref="Toggle"/> for the label and the click, and a box drawn at the row's
    /// vertical centre, filled with the accent when on. IMGUI's own toggle stretches its background
    /// over the whole row, which read as a text field rather than a switch.
    /// </summary>
    internal static bool Check(Rect rect, bool value, string text, GUIStyle style)
    {
        return Check(rect, value, new GUIContent(text), style);
    }

    internal static bool Check(Rect rect, bool value, GUIContent content, GUIStyle style)
    {
        Ensure();
        bool result = GUI.Toggle(rect, value, content, style);
        if (Event.current.type == EventType.Repaint)
        {
            float size = plain ? 18f : 16f;
            Rect box = new(rect.x + 6f, Mathf.Round(rect.center.y - (size * 0.5f)), size, size);
            bool hovered = GUI.enabled && rect.Contains(Event.current.mousePosition);
            Color previous = GUI.color;
            GUI.color = Lift(background, 0.04f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = result ? neon : hovered ? Lift(background, 0.45f) : Lift(background, 0.28f);
            GUI.DrawTexture(new Rect(box.x, box.y, box.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x, box.yMax - 1f, box.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x, box.y, 1f, box.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.xMax - 1f, box.y, 1f, box.height), Texture2D.whiteTexture);
            if (result)
            {
                GUI.DrawTexture(new Rect(box.x + 4f, box.y + 4f, box.width - 8f, box.height - 8f), Texture2D.whiteTexture);
            }

            GUI.color = previous;
        }

        return result;
    }

    /// <summary>A 1px frame in the plates' own edge colour: an outline that does not shout.</summary>
    internal static void DrawOutline(Rect rect)
    {
        Ensure();
        Color previous = GUI.color;
        GUI.color = edge;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    /// <summary>The accent line that tops every window, for plates that are not windows.</summary>
    internal static void DrawAccentLine(Rect rect)
    {
        Ensure();
        Color previous = GUI.color;
        GUI.color = neon;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    /// <summary>Hairline separator.</summary>
    internal static void DrawRule(Rect rect, float alpha = 0.30f)
    {
        Ensure();
        Color previous = GUI.color;
        Color line = Lift(background, 0.20f);
        line.a = plain ? 1f : Mathf.Clamp01(alpha * 3f);
        GUI.color = line;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    /// <summary>Straight line between two GUI-space points, used for order routes.</summary>
    internal static void DrawLine(Vector2 from, Vector2 to, Color color, float thickness = 2f)
    {
        Ensure();
        Vector2 delta = to - from;
        float length = delta.magnitude;
        if (length < 0.5f)
        {
            return;
        }

        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        GUI.color = color;

        // Rotate about `from` in GUI space. GUIUtility.RotateAroundPivot composes the pivot the
        // other way round - outside the UI scale matrix - so it swung every line away from its
        // own start point by however far the UI scale moved that point.
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        GUI.matrix = previousMatrix
            * Matrix4x4.TRS(from, Quaternion.Euler(0f, 0f, angle), Vector3.one)
            * Matrix4x4.TRS(-(Vector3)from, Quaternion.identity, Vector3.one);

        // White, not the border slice: GUI.color multiplies, so drawing an orange attack route on
        // a green texture produced muddy olive lines instead of orange.
        GUI.DrawTexture(new Rect(from.x, from.y - (thickness * 0.5f), length, thickness), Texture2D.whiteTexture);
        GUI.matrix = previousMatrix;
        GUI.color = previousColor;
    }

    /// <summary>
    /// Marks a point in the 3D scene: four corner brackets around it with the label floating
    /// above them. The middle stays empty on purpose - the old filled plate sat right on top
    /// of the target it was marking, so an ordered attack hid the enemy behind a black box.
    /// Pass an empty label for a bare bracket.
    /// </summary>
    internal static void DrawWorldMarker(Vector2 point, string label, Color color, float size, string? below = null)
    {
        Ensure();
        float half = size * 0.5f;
        float arm = Mathf.Max(3f, size * 0.34f);
        float thickness = size >= 34f ? 2f : 1f;
        float left = point.x - half;
        float right = point.x + half;
        float top = point.y - half;
        float bottom = point.y + half;

        Color previous = GUI.color;
        GUI.color = color;

        // Top-left, top-right, bottom-left, bottom-right: one horizontal arm and one vertical
        // arm each, nothing across the middle.
        Bar(left, top, arm, thickness);
        Bar(left, top, thickness, arm);
        Bar(right - arm, top, arm, thickness);
        Bar(right - thickness, top, thickness, arm);
        Bar(left, bottom - thickness, arm, thickness);
        Bar(left, bottom - arm, thickness, arm);
        Bar(right - arm, bottom - thickness, arm, thickness);
        Bar(right - thickness, bottom - arm, thickness, arm);
        GUI.color = previous;

        DrawWorldLabel(new Vector2(point.x, top - 2f), label, color);
        if (!string.IsNullOrEmpty(below))
        {
            DrawWorldLabel(new Vector2(point.x, bottom + 2f + WorldLabel.CalcSize(new GUIContent(below)).y), below!, color);
        }
    }

    /// <summary>Label with a dark halo, bottom-centred on <paramref name="anchor"/>.</summary>
    internal static void DrawWorldLabel(Vector2 anchor, string label, Color color)
    {
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        Ensure();
        Vector2 size = WorldLabel.CalcSize(new GUIContent(label));
        Rect rect = new(anchor.x - (size.x * 0.5f), anchor.y - size.y, size.x, size.y);
        Color previous = GUI.color;

        // Four-way halo instead of a plate: legible over sky, terrain or a unit, and it hides
        // nothing. 4 offsets, go to 8 only if it still smears on bright terrain.
        GUI.color = new Color(0f, 0f, 0f, (plain ? 1f : 0.9f) * color.a);
        GUI.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), label, WorldLabel);
        GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), label, WorldLabel);
        GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), label, WorldLabel);
        GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), label, WorldLabel);
        GUI.color = color;
        GUI.Label(rect, label, WorldLabel);
        GUI.color = previous;
    }

    internal static bool DrawHelpButton(float windowWidth, ref bool visible)
    {
        Ensure();
        if (GUI.Button(new Rect(windowWidth - 62f, 3f, 26f, 22f), "?", visible ? SelectedButton : HelpButton))
        {
            visible = !visible;
        }

        DrawZoomButtons(windowWidth - 62f);
        return visible;
    }

    /// <summary>The - and + title-bar buttons, ending left of <paramref name="right"/> (the next button's x).</summary>
    internal static void DrawZoomButtons(float right)
    {
        Ensure();
        int id = CommanderWindowScale.Current;
        if (id == 0)
        {
            return;
        }

        if (GUI.Button(new Rect(right - 56f, 3f, 26f, 22f), new GUIContent("-", "Zoom this window out."), HelpButton))
        {
            CommanderWindowScale.Step(id, -0.1f);
        }

        if (GUI.Button(new Rect(right - 28f, 3f, 26f, 22f), new GUIContent("+", "Zoom this window in."), HelpButton))
        {
            CommanderWindowScale.Step(id, 0.1f);
        }
    }

    /// <summary>
    /// The <c>?</c> text. The box grows to fit the text rather than clipping it, and returns the
    /// height it took so a window can lay its content out below it.
    /// </summary>
    internal static float DrawHelpOverlay(Rect rect, string text)
    {
        Ensure();
        rect.height = Mathf.Max(rect.height, Label.CalcHeight(new GUIContent(text), rect.width - 20f) + 16f);
        GUI.Box(rect, string.Empty, Inset);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f), text, Label);
        return rect.height;
    }

    /// <summary>
    /// The height a row has to be for every line of <paramref name="text"/> to read. A row too
    /// short for its own text does not clip it: the wrapped lines are centred on the row and each
    /// of them loses half its height, which is what "only half of each line is visible" looks
    /// like. Rows that can grow call this; rows wedged between other controls use
    /// <see cref="FitLabel"/> or <see cref="FitButton"/> instead.
    /// </summary>
    internal static float RowHeight(GUIStyle style, float width, string text, float minimum)
    {
        Ensure();
        return Mathf.Max(minimum, style.CalcHeight(new GUIContent(text), width));
    }

    /// <summary>Label at the largest font size whose text still fits <paramref name="rect"/>.</summary>
    internal static void FitLabel(Rect rect, string text, GUIStyle style)
    {
        int restore = Shrink(style, rect, new GUIContent(text));
        GUI.Label(rect, text, style);
        style.fontSize = restore;
    }

    /// <summary><see cref="FitLabel"/> held to one line: shrinks to fit the width, never wraps.</summary>
    internal static void FitLineLabel(Rect rect, string text, GUIStyle style)
    {
        Ensure();
        int restore = style.fontSize;
        bool wrap = style.wordWrap;
        style.wordWrap = false;
        GUIContent content = new(text);
        while (style.fontSize > 9 && style.CalcSize(content).x > rect.width)
        {
            style.fontSize--;
        }

        GUI.Label(rect, content, style);
        style.fontSize = restore;
        style.wordWrap = wrap;
    }

    /// <summary>Button at the largest font size whose label still fits <paramref name="rect"/>.</summary>
    internal static bool FitButton(Rect rect, GUIContent content, GUIStyle style)
    {
        int restore = Shrink(style, rect, content);
        bool clicked = GUI.Button(rect, content, style);
        style.fontSize = restore;
        return clicked;
    }

    /// <summary>
    /// <see cref="FitButton"/> for a one-word label in a narrow button. A wrapped word still fits
    /// the rect's height, so this measures its width on one line instead.
    /// </summary>
    internal static bool FitWordButton(Rect rect, GUIContent content, GUIStyle style)
    {
        Ensure();
        int restore = style.fontSize;
        bool wrap = style.wordWrap;
        style.wordWrap = false;
        while (style.fontSize > 9 && style.CalcSize(content).x > rect.width)
        {
            style.fontSize--;
        }

        bool clicked = GUI.Button(rect, content, style);
        style.fontSize = restore;
        style.wordWrap = wrap;
        return clicked;
    }

    /// <summary>
    /// Draws the hover text of whatever control is under the mouse, beside the cursor and kept
    /// inside <paramref name="bounds"/>. Call it last, after every control: IMGUI only knows the
    /// tooltip once the hovered control has been drawn. With no <paramref name="text"/> it reads
    /// <c>GUI.tooltip</c>, i.e. the tooltip of any button drawn with a <c>GUIContent</c>.
    /// </summary>
    internal static void DrawTooltip(Rect bounds, string? text = null)
    {
        text ??= GUI.tooltip;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Ensure();
        float width = Mathf.Min(360f, bounds.width - 16f);
        float height = Label.CalcHeight(new GUIContent(text), width - 20f) + 14f;
        Vector2 mouse = Event.current.mousePosition;
        Rect rect = new(
            Mathf.Clamp(mouse.x + 14f, bounds.x + 8f, Mathf.Max(bounds.x + 8f, bounds.xMax - width - 8f)),
            Mathf.Clamp(mouse.y + 18f, bounds.y + 8f, Mathf.Max(bounds.y + 8f, bounds.yMax - height - 8f)),
            width,
            height);
        GUI.Box(rect, string.Empty, Toast);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 7f, rect.width - 20f, rect.height - 14f), text, Label);
    }

    /// <summary>
    /// Drops <paramref name="style"/>'s font size a point at a time until <paramref name="content"/>
    /// fits, and returns the size the caller has to put back. The shared style is mutated for the
    /// one draw call that follows it: IMGUI is single threaded, and CalcHeight is the only way to
    /// ask whether a string fits a rect.
    /// </summary>
    private static int Shrink(GUIStyle style, Rect rect, GUIContent content)
    {
        Ensure();
        int restore = style.fontSize;
        while (style.fontSize > 9 && style.CalcHeight(content, rect.width) > rect.height)
        {
            style.fontSize--;
        }

        return restore;
    }

    /// <summary>Copy of the active skin with themed scrollbars and sliders, installed during OnGUI.</summary>
    private static void BuildSkin()
    {
        Skin = Object.Instantiate(baseSkin!);
        Skin.hideFlags = HideFlags.HideAndDontSave;
        Skin.font = bodyFont;
        Skin.label = Label;
        Skin.box = Panel;
        Skin.button = Button;
        Skin.window = Window;
        Skin.toggle = Toggle;
        Skin.textField = new GUIStyle(Skin.textField)
        {
            font = bodyFont,
            fontSize = Label.fontSize,
            normal = { background = insetTexture, textColor = TextColor },
            hover = { background = insetTexture, textColor = TextColor },
            focused = { background = insetTexture, textColor = Color.white },
            border = EdgeOffset(),
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(8, 8, 2, 2),
        };

        GUIStyle track = new(Skin.verticalScrollbar)
        {
            normal = { background = trackTexture },
            border = EdgeOffset(),
            fixedWidth = plain ? 10f : 8f,
        };
        GUIStyle thumb = new(Skin.verticalScrollbarThumb)
        {
            normal = { background = thumbTexture },
            hover = { background = thumbTexture },
            active = { background = thumbTexture },
            border = EdgeOffset(),
            fixedWidth = plain ? 10f : 8f,
        };
        Skin.verticalScrollbar = track;
        Skin.verticalScrollbarThumb = thumb;
        Skin.horizontalScrollbar = new GUIStyle(track) { fixedWidth = 0f, fixedHeight = plain ? 10f : 8f };
        Skin.horizontalScrollbarThumb = new GUIStyle(thumb) { fixedWidth = 0f, fixedHeight = plain ? 10f : 8f };

        // No arrow buttons: modern rail look, and the thumb gets the whole track.
        Skin.verticalScrollbarUpButton = Blank();
        Skin.verticalScrollbarDownButton = Blank();
        Skin.horizontalScrollbarLeftButton = Blank();
        Skin.horizontalScrollbarRightButton = Blank();

        Skin.horizontalSlider = new GUIStyle(Skin.horizontalSlider)
        {
            normal = { background = trackTexture },
            border = EdgeOffset(),
            fixedHeight = plain ? 8f : 5f,
        };
        Skin.horizontalSliderThumb = new GUIStyle(Skin.horizontalSliderThumb)
        {
            normal = { background = toggleOnTexture },
            hover = { background = toggleOnTexture },
            active = { background = toggleOnTexture },
            border = EdgeOffset(),
            fixedWidth = plain ? 14f : 10f,
            fixedHeight = plain ? 20f : 16f,
        };
    }

    private static bool FindFonts()
    {
        Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
        for (int i = 0; i < fonts.Length; i++)
        {
            Font font = fonts[i];
            if (font == null)
            {
                continue;
            }

            // "regular" is the game's Brass Mono, the HUD font; Barlow is its menu body font.
            if (font.name == "regular")
            {
                titleFont = font;
            }
            else if (font.name == "Barlow-Medium")
            {
                bodyFont = font;
            }
        }

        return titleFont != null || bodyFont != null;
    }

    private static RectOffset EdgeOffset()
    {
        return new RectOffset(Edge, Edge, Edge, Edge);
    }

    private static Color Opaque(Color color)
    {
        color.a = 1f;
        return color;
    }

    /// <summary>Plate translucency, which plain mode drops: the scene behind a plate is noise.</summary>
    private static Color Alpha(Color color, float alpha)
    {
        color.a = plain ? 1f : alpha;
        return color;
    }

    private static Color Lift(Color color, float amount)
    {
        return new Color(
            Mathf.Clamp01(color.r + amount),
            Mathf.Clamp01(color.g + amount),
            Mathf.Clamp01(color.b + amount),
            1f);
    }

    /// <summary>The accent darkened to <paramref name="brightness"/>, for lit plates.</summary>
    private static Color Shade(float brightness)
    {
        return new Color(neon.r * brightness, neon.g * brightness, neon.b * brightness, 1f);
    }

    private static GUIStyle Blank()
    {
        return new GUIStyle { fixedWidth = 0f, fixedHeight = 0f };
    }

    private static void Bar(float x, float y, float width, float height)
    {
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
    }

    /// <summary>Flat plate with an <see cref="Edge"/> px edge, 9-sliced with the same border.</summary>
    private static Texture2D MakeSlice(Color fill, Color edgeColour, int height = 8)
    {
        const int width = 6;
        Texture2D texture = NewTexture(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool onEdge = EdgeDistance(x, width) < Edge || EdgeDistance(y, height) < Edge;
                texture.SetPixel(x, y, onEdge ? edgeColour : fill);
            }
        }

        return Finish(texture);
    }

    /// <summary>On-state plate: the accent washed into the button, with a lit bottom rule.</summary>
    private static Texture2D MakeSelectedSlice()
    {
        const int width = 6;
        const int height = 8;
        Color fill = Color.Lerp(Lift(background, 0.10f), neon, 0.26f);
        Color side = Color.Lerp(Lift(background, 0.20f), neon, 0.50f);
        fill.a = 1f;
        side.a = 1f;
        Texture2D texture = NewTexture(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool onEdge = EdgeDistance(x, width) < Edge || height - 1 - y < Edge;
                texture.SetPixel(x, y, y < 2 ? neon : onEdge ? side : fill);
            }
        }

        return Finish(texture);
    }

    /// <summary>
    /// Window plate: a 2px accent line, the lighter title band, a divider, then the body, all
    /// inside an edge. The top slice is fixed, the rest stretches.
    /// </summary>
    private static Texture2D MakeWindowSlice()
    {
        const int bodyHeight = 4;
        int width = (Edge * 2) + 4;
        int height = TitleBand + 1 + bodyHeight + Edge;
        Color band = Lift(background, 0.07f);
        Color body = Alpha(background, 0.95f);
        Color divider = Lift(background, 0.17f);
        Texture2D texture = NewTexture(width, height);
        for (int y = 0; y < height; y++)
        {
            // Texture space is bottom-up, so the title band lives in the last rows.
            int fromTop = height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                Color colour = fromTop < 2 ? neon
                    : fromTop < TitleBand ? band
                    : fromTop == TitleBand ? divider
                    : body;
                bool onEdge = fromTop >= 2 && (EdgeDistance(x, width) < Edge || y < Edge);
                texture.SetPixel(x, y, onEdge ? edge : colour);
            }
        }

        return Finish(texture);
    }

    /// <summary>Distance in pixels to the nearer edge on this axis.</summary>
    private static int EdgeDistance(int coordinate, int size)
    {
        return Mathf.Min(coordinate, size - 1 - coordinate);
    }

    private static Texture2D NewTexture(int width, int height)
    {
        return new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
        };
    }

    private static Texture2D Finish(Texture2D texture)
    {
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        Owned.Add(texture);
        return texture;
    }
}
