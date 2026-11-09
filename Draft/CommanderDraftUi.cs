using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The Ground Control Draft screen: a roster down the middle, the army being drafted on the right,
/// the points budget and the opponent's state across the top, and READY at the bottom. Opens by
/// itself when RTS mode is entered during the draft; outside RTS mode a banner on the map offers it.
/// </summary>
/// <remarks>
/// A view only: every rule (budget, shelf caps, what is on the roster, who is ready) is
/// <see cref="CommanderDraftService"/>'s, and every click goes through it, so a guest's screen and
/// the host's agree on what a pick costs.
/// </remarks>
internal sealed class CommanderDraftUi
{
    private const int WindowId = 0x44524654;
    private const float HeaderHeight = 74f;
    private const float FooterHeight = 58f;
    private const float TabWidth = 164f;
    private const float SummaryWidth = 330f;
    private const float CardHeight = 124f;
    private const float CardGap = 6f;
    private const int ShiftAddCount = 5;
    private const float SummaryRefreshSeconds = 0.25f;

    private const string HelpText =
        "Spend the points on everything your side will have: there is no money and no production once the battle starts. "
        + "Ground units roll out of your depots when it begins; aircraft wait in stock and launch from AIR COMMAND. "
        + "Your base's bunker never runs out of fuel or ammunition; units away from it rearm from supply trucks. "
        + "Repair vehicles, supply trucks and RECOVERY helicopters (a rescued pilot returns his airframe to stock) are the only ways to keep the army going. "
        + "The side that runs out of fighting units loses. Click a card to draft one, Shift-click for five; the - on a drafted card takes one back. AUTO-FILL spends what is left the way the enemy drafts.";

    private static readonly DraftGroup?[] Tabs =
    {
        null,
        DraftGroup.Armor,
        DraftGroup.Light,
        DraftGroup.Artillery,
        DraftGroup.AirDefence,
        DraftGroup.Support,
        DraftGroup.Aircraft,
    };

    private static GUIStyle? rightFigure;

    private readonly List<DraftEntry> filtered = new();
    private readonly List<string> filteredPrices = new();
    private readonly List<(DraftEntry Entry, int Count)> picked = new();
    private readonly List<string> warnings = new();
    private readonly float[] groupCost = new float[Enum.GetValues(typeof(DraftGroup)).Length];
    private readonly int[] groupCount = new int[Enum.GetValues(typeof(DraftGroup)).Length];

    private Rect windowRect;
    private Rect pillRect;
    private Vector2 rosterScroll;
    private Vector2 pickedScroll;
    private DraftGroup? tab;
    private bool sortByName;
    private bool positioned;
    private bool userHidden;
    private bool helpVisible;
    private bool clearArmed;
    private float clearArmedUntil;
    private float nextSummaryAt;
    private IReadOnlyList<DraftEntry>? filteredFrom;
    private int filteredFromCount = -1;
    private DraftGroup? filteredTab;
    private bool filteredByName;
    private int groundUnits;
    private int airUnits;
    private float spent;

    internal bool Visible { get; private set; }

    private static GUIStyle RightFigure => rightFigure ??= new GUIStyle(CommanderUiTheme.Header) { alignment = TextAnchor.MiddleRight };

    internal static Color GetGroupColor(DraftGroup group)
    {
        return group switch
        {
            DraftGroup.Armor => new Color(0.86f, 0.47f, 0.30f, 1f),
            DraftGroup.Light => new Color(0.92f, 0.78f, 0.36f, 1f),
            DraftGroup.Artillery => new Color(0.82f, 0.38f, 0.60f, 1f),
            DraftGroup.AirDefence => new Color(0.36f, 0.70f, 0.96f, 1f),
            DraftGroup.Support => new Color(0.62f, 0.67f, 0.72f, 1f),
            _ => new Color(0.52f, 0.88f, 0.58f, 1f),
        };
    }

    internal static string GetGroupLabel(DraftGroup? group)
    {
        return group switch
        {
            null => "ALL",
            DraftGroup.Armor => "ARMOR",
            DraftGroup.Light => "LIGHT",
            DraftGroup.Artillery => "ARTILLERY",
            DraftGroup.AirDefence => "AIR DEFENCE",
            DraftGroup.Support => "SUPPORT",
            _ => "AIRCRAFT",
        };
    }

    internal static string Points(float points)
    {
        return Mathf.RoundToInt(points).ToString("N0", CultureInfo.CurrentCulture);
    }

    internal void Show()
    {
        Visible = true;
        userHidden = false;
    }

    internal void Hide()
    {
        Visible = false;
        userHidden = true;
    }

    /// <summary>Opens itself for the draft and closes itself once the battle starts.</summary>
    internal void Tick()
    {
        // Outside a draft (another mission, or the battle has started) it is closed, and the next
        // draft opens it again whatever the player did with this one.
        if (!CommanderDraftService.IsDrafting)
        {
            Visible = false;
            userHidden = false;
            clearArmed = false;
            return;
        }

        if (!Visible && !userHidden)
        {
            Visible = true;
        }

        Vector2 size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(1140f, CommanderUiScale.Width - 32f), Mathf.Min(740f, CommanderUiScale.Height - 32f)),
            new Vector2(600f, 400f));
        float width = size.x;
        float height = size.y;
        if (!positioned)
        {
            windowRect = new Rect((CommanderUiScale.Width - width) * 0.5f, (CommanderUiScale.Height - height) * 0.5f, width, height);
            positioned = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
        }

        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        if (!CommanderDraftService.IsDrafting)
        {
            return false;
        }

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return Visible
            ? CommanderWindowScale.Visual(WindowId, windowRect).Contains(guiPoint)
            : pillRect.Contains(guiPoint);
    }

    /// <summary>The window, or while it is hidden a pill at the top of the screen that brings it back.</summary>
    internal void Draw()
    {
        CommanderDraftService? service = CommanderDraftService.Instance;
        if (service == null || !CommanderDraftService.IsDrafting)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        if (!Visible)
        {
            DrawPill(service);
            return;
        }

        windowRect = CommanderWindowScale.Window(WindowId, windowRect, id => DrawWindow(service), "ARMY DRAFT");
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
    }

    /// <summary>Outside RTS mode, on the map: the draft has to be findable before anyone knows the key.</summary>
    internal void DrawInactiveBanner(Action openDraft)
    {
        CommanderDraftService? service = CommanderDraftService.Instance;
        if (service == null || !CommanderDraftService.IsDrafting)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        float width = Mathf.Min(560f, CommanderUiScale.Width - 32f);
        Rect plate = new((CommanderUiScale.Width - width) * 0.5f, 14f, width, 70f);
        GUI.Box(plate, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(plate, 1f);
        GUI.Label(new Rect(plate.x + 14f, plate.y + 10f, width - 180f, 24f), "ARMY DRAFT", CommanderUiTheme.Header);
        GUI.Label(
            new Rect(plate.x + 14f, plate.y + 36f, width - 180f, 22f),
            service.Mine.Ready ? "Your army is locked in. Waiting for the other side." : "Pick your army before the battle starts.",
            CommanderUiTheme.MutedLabel);
        EventType activation = Event.current.type;
        if (GUI.Button(new Rect(plate.xMax - 158f, plate.y + 14f, 144f, 42f), "OPEN DRAFT", CommanderUiTheme.PrimaryButton)
            && activation == EventType.MouseUp)
        {
            GUI.FocusControl(null);
            openDraft();
            Show();
        }
    }

    private static void DrawSwatch(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static string FormatClock(float seconds)
    {
        int whole = Mathf.CeilToInt(seconds);
        return $"{whole / 60}:{whole % 60:00}";
    }

    private static void DrawRosterCard(CommanderDraftService service, DraftEntry entry, string price, Rect card)
    {
        int count = service.GetCount(entry.Key);
        int room = service.GetRoomFor(entry);
        int step = Event.current.shift ? ShiftAddCount : 1;
        if (count > 0 && GUI.Button(new Rect(card.xMax - 30f, card.y + 4f, 26f, 22f), new GUIContent("-", "Take one back. Shift takes five."), CommanderUiTheme.Button))
        {
            service.Remove(entry, step);
        }

        string? lockText = room > 0 || count > 0 ? null : "NO POINTS";
        string tip = $"{entry.Label}: {entry.Detail}. Shift-click adds {ShiftAddCount}.";
        bool clicked = CommanderUiTheme.DrawCard(card, entry.Definition, entry.Label, price, GetGroupLabel(entry.Group), count > 0, lockText) && room > 0;
        DrawSwatch(new Rect(card.x + 1f, card.y + 1f, card.width - 2f, 2f), GetGroupColor(entry.Group));
        if (count > 0)
        {
            GUI.Label(new Rect(card.x, card.yMax - 21f, card.width - 8f, 18f), $"x{count}", RightFigure);
        }

        GUI.Label(card, new GUIContent(string.Empty, tip), GUIStyle.none);
        if (clicked)
        {
            service.Add(entry, step);
        }
    }

    private void DrawPill(CommanderDraftService service)
    {
        float width = 360f;
        pillRect = new Rect((CommanderUiScale.Width - width) * 0.5f, 48f, width, 34f);
        string clock = service.SecondsLeft >= 0f ? $"  |  {FormatClock(service.SecondsLeft)}" : string.Empty;
        string text = $"ARMY DRAFT  {Points(service.GetSpent())} / {Points(service.Budget)} PTS{clock}   OPEN";
        if (GUI.Button(pillRect, text, service.Mine.Ready ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            Show();
        }
    }

    private void DrawWindow(CommanderDraftService service)
    {
        IReadOnlyList<DraftEntry> roster = service.GetLocalRoster();
        RefreshSummary(service, roster);

        float width = windowRect.width;
        float height = windowRect.height;
        float y = 36f;
        if (CommanderUiTheme.DrawHelpButton(width, ref helpVisible))
        {
            y += CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 36f, width - 24f, 90f),
                HelpText) + 8f;
        }

        if (GUI.Button(new Rect(width - 34f, 3f, 26f, 22f), new GUIContent("X", "Hide the draft. It stays open: bring it back from the DRAFT tab on the command bar."), CommanderUiTheme.ChromeButton))
        {
            Hide();
            return;
        }

        DrawHeader(service, y, width);
        y += HeaderHeight;
        CommanderUiTheme.DrawRule(new Rect(12f, y - 6f, width - 24f, 1f));

        float bodyHeight = Mathf.Max(120f, height - y - FooterHeight - 8f);
        DrawTabs(y, bodyHeight);
        float rosterX = 12f + TabWidth + 8f;
        float rosterWidth = Mathf.Max(260f, width - rosterX - SummaryWidth - 20f);
        DrawRoster(service, roster, new Rect(rosterX, y, rosterWidth, bodyHeight));
        DrawSummary(service, new Rect(rosterX + rosterWidth + 8f, y, width - rosterX - rosterWidth - 20f, bodyHeight));

        DrawFooter(service, height - FooterHeight + 6f, width);

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, width, height));
        GUI.DragWindow(new Rect(0f, 0f, width - 72f, 28f));
    }

    private void DrawHeader(CommanderDraftService service, float y, float width)
    {
        float budget = Mathf.Max(1f, service.Budget);
        float left = service.Budget - spent;

        // Points: the one number the whole screen is about.
        GUI.Label(new Rect(12f, y, 200f, 18f), "POINTS SPENT", CommanderUiTheme.Caption);
        GUI.Label(new Rect(12f, y + 16f, 300f, 28f), $"{Points(spent)}  /  {Points(service.Budget)}", CommanderUiTheme.Figure);
        GUI.Label(new Rect(200f, y + 18f, 170f, 24f), $"{Points(left)} LEFT", left <= 0f ? CommanderUiTheme.WarningLabel : CommanderUiTheme.Label);
        CommanderUiTheme.DrawBar(new Rect(12f, y + 48f, 360f, 8f), spent / budget, CommanderUiTheme.Accent);

        // What the rest of the match is waiting on.
        float midX = 392f;
        float midWidth = Mathf.Max(120f, width - midX - 230f);
        string clock = service.SecondsLeft >= 0f
            ? $"LOCKS IN {FormatClock(service.SecondsLeft)}"
            : CommanderDraftService.IsSolo ? "SINGLEPLAYER" : "HOSTED";
        GUI.Label(new Rect(midX, y, midWidth, 18f), clock, CommanderUiTheme.Caption);
        GUI.Label(new Rect(midX, y + 18f, midWidth, 24f), service.OpponentStatus, service.OpponentReady ? CommanderUiTheme.Label : CommanderUiTheme.MutedLabel);
        GUI.Label(
            new Rect(midX, y + 40f, midWidth, 20f),
            service.Mine.Ready ? "YOU: READY" : "YOU: DRAFTING",
            service.Mine.Ready ? CommanderUiTheme.Label : CommanderUiTheme.MutedLabel);

        // The budget is the host's to set, and changing it unreadies everyone.
        Rect budgetRect = new(width - 222f, y + 6f, 210f, 40f);
        if (service.CanChangeBudget)
        {
            if (GUI.Button(
                budgetRect,
                new GUIContent($"BUDGET  {Points(service.Budget)}", "Cycle the points each side gets. Every side is fitted to the new budget and has to ready up again."),
                CommanderUiTheme.Button))
            {
                service.CycleBudget();
            }
        }
        else
        {
            GUI.Label(budgetRect, $"BUDGET {Points(service.Budget)}\nSET BY THE HOST", CommanderUiTheme.MutedLabel);
        }
    }

    private void DrawTabs(float y, float height)
    {
        for (int i = 0; i < Tabs.Length; i++)
        {
            DraftGroup? group = Tabs[i];
            int count = group.HasValue ? groupCount[(int)group.Value] : groundUnits + airUnits;
            string label = count > 0 ? $"{GetGroupLabel(group)}   {count}" : GetGroupLabel(group);
            Rect rect = new(12f, y + (i * 34f), TabWidth, 30f);
            if (GUI.Button(rect, label, tab == group ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                tab = group;
                rosterScroll = Vector2.zero;
            }

            if (group.HasValue)
            {
                DrawSwatch(new Rect(rect.x + 3f, rect.y + 5f, 3f, rect.height - 10f), GetGroupColor(group.Value));
            }
        }

        float sortY = y + (Tabs.Length * 34f) + 10f;
        if (sortY + 30f <= y + height
            && GUI.Button(
                new Rect(12f, sortY, TabWidth, 30f),
                new GUIContent(sortByName ? "SORT: NAME" : "SORT: PRICE", "Order the roster by price or by name."),
                CommanderUiTheme.Button))
        {
            sortByName = !sortByName;
        }
    }

    private void RefreshFiltered(IReadOnlyList<DraftEntry> roster)
    {
        if (ReferenceEquals(filteredFrom, roster)
            && filteredFromCount == roster.Count
            && filteredTab == tab
            && filteredByName == sortByName)
        {
            return;
        }

        filteredFrom = roster;
        filteredFromCount = roster.Count;
        filteredTab = tab;
        filteredByName = sortByName;
        filtered.Clear();
        for (int i = 0; i < roster.Count; i++)
        {
            if (!tab.HasValue || roster[i].Group == tab.Value)
            {
                filtered.Add(roster[i]);
            }
        }

        if (sortByName)
        {
            filtered.Sort(static (a, b) =>
            {
                int group = a.Group.CompareTo(b.Group);
                return group != 0 ? group : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
        }

        filteredPrices.Clear();
        for (int i = 0; i < filtered.Count; i++)
        {
            filteredPrices.Add($"{Points(filtered[i].Cost)} PTS");
        }
    }

    private void DrawRoster(CommanderDraftService service, IReadOnlyList<DraftEntry> roster, Rect view)
    {
        RefreshFiltered(roster);
        GUI.Box(view, string.Empty, CommanderUiTheme.Inset);
        if (filtered.Count == 0)
        {
            GUI.Label(
                new Rect(view.x + 14f, view.y + 12f, view.width - 28f, 44f),
                roster.Count == 0 ? "Waiting for the mission to finish loading your side." : "Nothing on this tab can be drafted here.",
                CommanderUiTheme.MutedLabel);
            return;
        }

        Rect scrollView = new(view.x + 6f, view.y + 6f, view.width - 8f, view.height - 12f);
        float innerWidth = scrollView.width - 16f;
        int columns = Mathf.Max(1, Mathf.FloorToInt((innerWidth + CardGap) / (150f + CardGap)));
        float cardWidth = (innerWidth - (CardGap * (columns - 1))) / columns;
        int rows = (filtered.Count + columns - 1) / columns;
        Rect inner = new(0f, 0f, innerWidth, Mathf.Max(scrollView.height, (rows * (CardHeight + CardGap)) - CardGap));
        rosterScroll = GUI.BeginScrollView(scrollView, rosterScroll, inner);
        for (int i = 0; i < filtered.Count; i++)
        {
            Rect card = new((i % columns) * (cardWidth + CardGap), (i / columns) * (CardHeight + CardGap), cardWidth, CardHeight);

            // Only cards in view are drawn: a full roster is well over a hundred of them.
            if (card.yMax < rosterScroll.y || card.y > rosterScroll.y + scrollView.height)
            {
                continue;
            }

            DrawRosterCard(service, filtered[i], filteredPrices[i], card);
        }

        GUI.EndScrollView();
    }

    /// <summary>
    /// One roster card: click drafts one (Shift five), the - in its corner takes one back. The -
    /// is drawn before the card because IMGUI gives a click to the control drawn first.
    /// </summary>
    private void DrawSummary(CommanderDraftService service, Rect view)
    {
        GUI.Box(view, string.Empty, CommanderUiTheme.Inset);
        float x = view.x + 12f;
        float width = view.width - 24f;
        float y = CommanderUiTheme.DrawSection(new Rect(x, view.y + 8f, width, 18f), "YOUR ARMY");

        // Where the points went, as one bar: the shape of the army at a glance.
        float total = Mathf.Max(1f, spent);
        float barX = x;
        for (int i = 0; i < groupCost.Length; i++)
        {
            float share = groupCost[i] / total;
            if (share <= 0f)
            {
                continue;
            }

            float segment = width * share;
            DrawSwatch(new Rect(barX, y, Mathf.Max(1f, segment - 1f), 10f), GetGroupColor((DraftGroup)i));
            barX += segment;
        }

        if (spent <= 0f)
        {
            CommanderUiTheme.DrawBar(new Rect(x, y, width, 10f), 0f, CommanderUiTheme.Accent);
        }

        y += 16f;
        GUI.Label(new Rect(x, y, width * 0.5f, 20f), $"GROUND  {groundUnits}", CommanderUiTheme.Label);
        GUI.Label(new Rect(x + (width * 0.5f), y, width * 0.5f, 20f), $"AIR  {airUnits}", CommanderUiTheme.Label);
        y += 20f;
        GUI.Label(new Rect(x, y, width, 20f), "BUNKER  UNLIMITED FUEL AND AMMO", CommanderUiTheme.MutedLabel);
        y += 26f;
        CommanderUiTheme.DrawRule(new Rect(x, y - 3f, width, 1f));

        // Warnings take the bottom of the column; the picked list gets what is left.
        float warningHeight = 0f;
        for (int i = 0; i < warnings.Count; i++)
        {
            warningHeight += CommanderUiTheme.WarningLabel.CalcHeight(new GUIContent(warnings[i]), width) + 4f;
        }

        float listHeight = Mathf.Max(40f, view.yMax - y - warningHeight - 12f);
        Rect listView = new(x - 4f, y, width + 8f, listHeight);
        Rect inner = new(0f, 0f, listView.width - 20f, Mathf.Max(listHeight, (picked.Count * 28f) + 4f));
        pickedScroll = GUI.BeginScrollView(listView, pickedScroll, inner);
        if (picked.Count == 0)
        {
            GUI.Label(new Rect(4f, 4f, inner.width - 8f, 40f), "Nothing drafted yet. Pick from the roster, or press AUTO-FILL.", CommanderUiTheme.MutedLabel);
        }

        for (int i = 0; i < picked.Count; i++)
        {
            (DraftEntry entry, int count) = picked[i];
            float rowY = 2f + (i * 28f);
            DrawSwatch(new Rect(4f, rowY + 5f, 3f, 16f), GetGroupColor(entry.Group));
            CommanderUiTheme.FitLabel(new Rect(12f, rowY + 2f, inner.width - 118f, 22f), $"{count} x {entry.Label}", CommanderUiTheme.Label);
            GUI.Label(new Rect(inner.width - 104f, rowY + 2f, 70f, 22f), Points(entry.Cost * count), CommanderUiTheme.MutedLabel);
            if (GUI.Button(new Rect(inner.width - 30f, rowY + 1f, 26f, 24f), "-", CommanderUiTheme.Button))
            {
                service.Remove(entry, Event.current.shift ? ShiftAddCount : 1);
            }
        }

        GUI.EndScrollView();

        float warnY = view.yMax - warningHeight - 6f;
        for (int i = 0; i < warnings.Count; i++)
        {
            float lineHeight = CommanderUiTheme.WarningLabel.CalcHeight(new GUIContent(warnings[i]), width);
            GUI.Label(new Rect(x, warnY, width, lineHeight), warnings[i], CommanderUiTheme.WarningLabel);
            warnY += lineHeight + 4f;
        }
    }

    private void DrawFooter(CommanderDraftService service, float y, float width)
    {
        FactionDraft mine = service.Mine;
        bool enabled = GUI.enabled;
        bool editable = !mine.Ready;
        if (clearArmed && Time.unscaledTime > clearArmedUntil)
        {
            clearArmed = false;
        }

        GUI.enabled = enabled && editable && mine.Picks.Count > 0;
        if (GUI.Button(
            new Rect(12f, y, 130f, 40f),
            new GUIContent(clearArmed ? "CLEAR? CONFIRM" : "CLEAR", "Empty the whole draft. Click twice."),
            CommanderUiTheme.DangerButton))
        {
            if (clearArmed)
            {
                service.Clear();
                clearArmed = false;
            }
            else
            {
                clearArmed = true;
                clearArmedUntil = Time.unscaledTime + 3f;
            }
        }

        GUI.enabled = enabled && editable && CommanderDraftService.HasLastDraft;
        if (GUI.Button(
            new Rect(150f, y, 130f, 40f),
            new GUIContent("LOAD LAST", "The army you took into your last draft battle, fitted to this budget."),
            CommanderUiTheme.Button))
        {
            service.LoadLast();
        }

        GUI.enabled = enabled && editable && service.Budget - spent > 0f;
        if (GUI.Button(
            new Rect(288f, y, 150f, 40f),
            new GUIContent("AUTO-FILL", "Spend the points you have left the way the enemy commander drafts, keeping what you picked."),
            CommanderUiTheme.Button))
        {
            service.AutoFill();
        }

        GUI.enabled = enabled;
        string readyLabel = mine.Ready
            ? (CommanderDraftService.IsSolo ? "STARTING..." : "READY  (CLICK TO EDIT)")
            : CommanderDraftService.IsSolo ? "START BATTLE" : "READY";
        string readyTip = mine.Ready
            ? "Unlock the draft to change it."
            : CommanderDraftService.IsSolo
                ? "Lock in this army and start the battle."
                : "Lock in this army. The battle starts when every side is ready, or when the clock runs out.";
        if (GUI.Button(
            new Rect(width - 232f, y, 220f, 40f),
            new GUIContent(readyLabel, readyTip),
            mine.Ready ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            service.ToggleReady();
        }

        GUI.enabled = enabled;
    }

    /// <summary>The right-hand column's numbers, rebuilt a few times a second rather than every OnGUI event.</summary>
    private void RefreshSummary(CommanderDraftService service, IReadOnlyList<DraftEntry> roster)
    {
        if (!CommanderScheduler.IsDueRealtime(ref nextSummaryAt, SummaryRefreshSeconds) && Event.current.type != EventType.MouseUp)
        {
            return;
        }

        Array.Clear(groupCost, 0, groupCost.Length);
        Array.Clear(groupCount, 0, groupCount.Length);
        picked.Clear();
        warnings.Clear();
        groundUnits = 0;
        airUnits = 0;
        spent = 0f;
        bool repair = false;
        bool capture = false;
        bool airDefence = false;
        bool radarSam = false;
        bool fireControl = false;
        bool radar = false;
        bool supplyTruck = false;
        int combat = 0;
        Dictionary<string, int> picks = service.Mine.Picks;
        for (int i = 0; i < roster.Count; i++)
        {
            DraftEntry entry = roster[i];
            if (!picks.TryGetValue(entry.Key, out int count) || count <= 0)
            {
                continue;
            }

            picked.Add((entry, count));
            spent += entry.Cost * count;
            groupCost[(int)entry.Group] += entry.Cost * count;
            groupCount[(int)entry.Group] += count;

            if (entry.Definition is AircraftDefinition)
            {
                airUnits += count;
                continue;
            }

            if (entry.Definition is not VehicleDefinition vehicle)
            {
                continue;
            }

            groundUnits += count;
            GameObject? prefab = vehicle.unitPrefab;
            repair |= prefab != null && prefab.GetComponentInChildren<Repairer>(true) != null;
            supplyTruck |= prefab != null && prefab.GetComponentInChildren<Rearmer>(true) != null;
            capture |= vehicle.captureStrength > 0f;
            bool ad = vehicle.vehicleType is VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM;
            airDefence |= ad;
            bool guided = ad && CommanderRadarService.IsRadarGuidedLauncherDefinition(vehicle);
            radarSam |= guided;
            fireControl |= prefab != null
                && prefab.GetComponentInChildren<FireControl>(true) != null
                && prefab.GetComponentInChildren<MissileLauncher>(true) == null;
            radar |= vehicle.vehicleType == VehicleType.RDR;
            combat += vehicle.vehicleType is VehicleType.MBT or VehicleType.AFV or VehicleType.LCV or VehicleType.ART ? count : 0;
        }

        if (picked.Count == 0)
        {
            return;
        }

        if (!capture)
        {
            warnings.Add("Nothing here can capture a base.");
        }

        if (!airDefence && airUnits == 0)
        {
            warnings.Add("No air defence and no aircraft: the enemy's air wing will be unopposed.");
        }

        if (!repair)
        {
            warnings.Add("No repair vehicle: nothing can fix a damaged depot or bunker.");
        }

        if (!supplyTruck && combat >= 6)
        {
            warnings.Add("No munitions truck: units away from the base cannot rearm.");
        }

        if (radarSam && (!fireControl || !radar))
        {
            warnings.Add("A radar SAM will not fire without a fire-control truck and a radar within 300 m.");
        }
    }
}
