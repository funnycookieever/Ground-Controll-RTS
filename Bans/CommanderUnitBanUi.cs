using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The UNIT BANS screen: the roster of ground units and aircraft as picture cards, your votes and
/// your side's tally on the right, the clock and who is ready across the top, and READY (CONTINUE in
/// singleplayer) at the bottom. Opens by itself when RTS mode is entered during the vote; outside RTS
/// mode a banner offers it.
/// </summary>
/// <remarks>
/// A view only, laid out like the ARMY DRAFT screen: every rule is
/// <see cref="CommanderUnitBanService"/>'s and every click goes through it.
/// </remarks>
internal sealed class CommanderUnitBanUi
{
    private const int WindowId = 0x42414E53;
    private const float HeaderHeight = 74f;
    private const float FooterHeight = 58f;
    private const float TabWidth = 164f;
    private const float SummaryWidth = 320f;
    private const float CardHeight = 124f;
    private const float CardGap = 6f;

    private const string HelpText =
        "Vote for the units you want banned: ground, air or naval, up to the number the host set. Each side's most voted units are banned, "
        + "and a banned unit is gone for both sides for the whole match: no depot, hangar or AI will field it. "
        + "Bans are blind: you see the other side's only when the vote locks. "
        + "Nothing spawns from a depot or hangar until it does. Click a card to vote, click it again to take the vote back.";

    // Ground, air, naval: the order of CommanderUnitBanService.KindOf.
    private static readonly Color[] KindColors =
    {
        new(0.86f, 0.47f, 0.30f, 1f),
        new(0.52f, 0.88f, 0.58f, 1f),
        new(0.36f, 0.70f, 0.96f, 1f),
    };

    private static readonly string[] TabLabels = { "ALL", "GROUND", "AIRCRAFT", "NAVAL" };

    private static GUIStyle? rightFigure;

    private readonly List<UnitDefinition> filtered = new();
    private readonly List<string> filteredPrices = new();

    private Rect windowRect;
    private Rect pillRect;
    private Vector2 rosterScroll;
    private int tab;
    private bool positioned;
    private bool userHidden;
    private bool helpVisible;
    private int filteredFromCount = -1;
    private int filteredTab = -1;

    internal bool Visible { get; private set; }

    private static GUIStyle RightFigure => rightFigure ??= new GUIStyle(CommanderUiTheme.Header) { alignment = TextAnchor.MiddleRight };

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

    /// <summary>Opens itself for the vote and closes itself once it locks.</summary>
    internal void Tick()
    {
        if (!CommanderUnitBanService.IsVoting)
        {
            Visible = false;
            userHidden = false;
            return;
        }

        if (!Visible && !userHidden)
        {
            Visible = true;
        }

        Vector2 size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(1100f, CommanderUiScale.Width - 32f), Mathf.Min(720f, CommanderUiScale.Height - 32f)),
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
        if (!CommanderUnitBanService.IsVoting)
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
        CommanderUnitBanService? service = CommanderUnitBanService.Instance;
        if (service == null || !CommanderUnitBanService.IsVoting)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        if (!Visible)
        {
            DrawPill(service);
            return;
        }

        windowRect = CommanderWindowScale.Window(WindowId, windowRect, id => DrawWindow(service), "UNIT BANS");
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
    }

    /// <summary>Outside RTS mode, on the map or the aircraft menu: nothing spawns until the vote locks, so it has to be found.</summary>
    internal void DrawInactiveBanner(Action openCommander)
    {
        CommanderUnitBanService? service = CommanderUnitBanService.Instance;
        if (service == null || !CommanderUnitBanService.IsVoting)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        float width = Mathf.Min(560f, CommanderUiScale.Width - 32f);
        Rect plate = new((CommanderUiScale.Width - width) * 0.5f, 14f, width, 70f);
        GUI.Box(plate, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(plate, 1f);
        string clock = service.SecondsLeft >= 0f ? $"   {FormatClock(service.SecondsLeft)}" : string.Empty;
        GUI.Label(new Rect(plate.x + 14f, plate.y + 10f, width - 180f, 24f), "UNIT BANS" + clock, CommanderUiTheme.Header);
        GUI.Label(
            new Rect(plate.x + 14f, plate.y + 36f, width - 180f, 22f),
            service.Mine.Ready ? "Your bans are locked in. Waiting for the others." : "Vote your bans. Nothing spawns until they lock.",
            CommanderUiTheme.MutedLabel);
        EventType activation = Event.current.type;
        if (GUI.Button(new Rect(plate.xMax - 158f, plate.y + 14f, 144f, 42f), "OPEN BANS", CommanderUiTheme.PrimaryButton)
            && activation == EventType.MouseUp)
        {
            GUI.FocusControl(null);
            openCommander();
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

    private static string ReadyText(int ready, int total)
    {
        return total == 0 ? "NO PLAYERS" : ready >= total ? "READY" : total == 1 ? "VOTING" : $"READY {ready}/{total}";
    }

    private static void DrawHeader(CommanderUnitBanService service, float y, float width)
    {
        List<string> mine = service.Mine.Keys;
        int max = service.MaxBans;
        GUI.Label(new Rect(12f, y, 220f, 18f), "YOUR VOTES", CommanderUiTheme.Caption);
        GUI.Label(new Rect(12f, y + 16f, 360f, 28f), $"{mine.Count} / {max}  BANS PER SIDE", CommanderUiTheme.Figure);
        CommanderUiTheme.DrawBar(new Rect(12f, y + 48f, 360f, 8f), max == 0 ? 1f : (float)mine.Count / max, CommanderUiTheme.Accent);

        float midX = 392f;
        float midWidth = Mathf.Max(120f, width - midX - 12f);
        string clock = service.SecondsLeft >= 0f
            ? $"LOCKS IN {FormatClock(service.SecondsLeft)}"
            : CommanderUnitBanService.IsSolo ? "SINGLEPLAYER" : "HOSTED";
        GUI.Label(new Rect(midX, y, midWidth, 18f), clock, CommanderUiTheme.Caption);
        if (CommanderUnitBanService.IsSolo)
        {
            GUI.Label(new Rect(midX, y + 18f, midWidth, 44f), "Your picks are the bans. CONTINUE starts the match.", CommanderUiTheme.MutedLabel);
            return;
        }

        bool othersReady = service.OthersTotal > 0 && service.OthersReady >= service.OthersTotal;
        GUI.Label(new Rect(midX, y + 18f, midWidth, 24f), "OPPONENT: " + ReadyText(service.OthersReady, service.OthersTotal), othersReady ? CommanderUiTheme.Label : CommanderUiTheme.MutedLabel);
        GUI.Label(
            new Rect(midX, y + 40f, midWidth, 20f),
            "YOUR SIDE: " + ReadyText(service.TeamReady, service.TeamTotal),
            service.Mine.Ready ? CommanderUiTheme.Label : CommanderUiTheme.MutedLabel);
    }

    private static void DrawCard(CommanderUnitBanService service, UnitDefinition definition, string price, Rect card)
    {
        List<string> mine = service.Mine.Keys;
        int kind = CommanderUnitBanService.KindOf(definition);
        bool voted = mine.Contains(definition.jsonKey);
        bool full = !voted && mine.Count >= service.MaxBans;
        string corner = definition is VehicleDefinition vehicle
            ? CommanderGameAccess.GetVehicleCategoryLabel(vehicle).ToUpperInvariant()
            : kind == 1 ? "AIR" : "NAVAL";
        string label = CommanderUnitBanService.GetLabel(definition);
        bool clicked = CommanderUiTheme.DrawCard(card, definition, label, price, corner, voted, full ? "ALL VOTES USED" : null);
        DrawSwatch(new Rect(card.x + 1f, card.y + 1f, card.width - 2f, 2f), KindColors[kind]);
        if (service.TeamTally.TryGetValue(definition.jsonKey, out int votes) && votes > 0 && !CommanderUnitBanService.IsSolo)
        {
            GUI.Label(new Rect(card.x, card.yMax - 21f, card.width - 8f, 18f), votes == 1 ? "1 VOTE" : $"{votes} VOTES", RightFigure);
        }

        GUI.Label(card, new GUIContent(string.Empty, voted ? $"Take back your vote to ban the {label}." : $"Vote to ban the {label}."), GUIStyle.none);
        if (clicked)
        {
            service.Toggle(definition);
        }
    }

    private static void DrawSummary(CommanderUnitBanService service, Rect view)
    {
        GUI.Box(view, string.Empty, CommanderUiTheme.Inset);
        float x = view.x + 12f;
        float width = view.width - 24f;
        float y = CommanderUiTheme.DrawSection(new Rect(x, view.y + 8f, width, 18f), "YOUR VOTES");
        List<string> mine = service.Mine.Keys;
        if (mine.Count == 0)
        {
            GUI.Label(new Rect(x, y, width, 40f), "No votes yet. Click a card to vote to ban it, or CONTINUE to ban nothing.", CommanderUiTheme.MutedLabel);
            y += 44f;
        }

        for (int i = 0; i < mine.Count; i++)
        {
            UnitDefinition? definition = CommanderNetwork.Definition<UnitDefinition>(mine[i]);
            if (definition == null)
            {
                continue;
            }

            DrawSwatch(new Rect(x, y + 5f, 3f, 16f), KindColors[CommanderUnitBanService.KindOf(definition)]);
            CommanderUiTheme.FitLabel(new Rect(x + 8f, y + 2f, width - 44f, 22f), CommanderUnitBanService.GetLabel(definition), CommanderUiTheme.Label);
            if (GUI.Button(new Rect(x + width - 28f, y + 1f, 26f, 24f), new GUIContent("-", "Take this vote back."), CommanderUiTheme.Button))
            {
                service.Toggle(definition);
            }

            y += 28f;
        }

        if (CommanderUnitBanService.IsSolo)
        {
            return;
        }

        // What the side would ban if the vote locked now: the most voted two of each kind.
        y = CommanderUiTheme.DrawSection(new Rect(x, y + 10f, width, 18f), "YOUR SIDE'S VOTES");
        if (service.TeamTally.Count == 0)
        {
            GUI.Label(new Rect(x, y, width, 40f), "Nobody on your side has voted yet.", CommanderUiTheme.MutedLabel);
            y += 44f;
        }

        foreach (KeyValuePair<string, int> vote in service.TeamTally)
        {
            UnitDefinition? definition = CommanderNetwork.Definition<UnitDefinition>(vote.Key);
            if (definition == null || y > view.yMax - 60f)
            {
                continue;
            }

            DrawSwatch(new Rect(x, y + 5f, 3f, 16f), KindColors[CommanderUnitBanService.KindOf(definition)]);
            CommanderUiTheme.FitLabel(new Rect(x + 8f, y + 2f, width - 70f, 22f), CommanderUnitBanService.GetLabel(definition), CommanderUiTheme.Label);
            GUI.Label(new Rect(x + width - 60f, y + 2f, 60f, 22f), vote.Value == 1 ? "1 VOTE" : $"{vote.Value} VOTES", CommanderUiTheme.MutedLabel);
            y += 26f;
        }

        GUI.Label(new Rect(x, view.yMax - 50f, width, 44f), "The other side's bans stay hidden until the vote locks.", CommanderUiTheme.MutedLabel);
    }

    private static void DrawFooter(CommanderUnitBanService service, float y, float width)
    {
        bool enabled = GUI.enabled;
        bool ready = service.Mine.Ready;
        GUI.enabled = enabled && service.Mine.Keys.Count > 0;
        if (GUI.Button(new Rect(12f, y, 130f, 40f), new GUIContent("CLEAR", "Take back every vote."), CommanderUiTheme.DangerButton))
        {
            service.Clear();
        }

        GUI.enabled = enabled;
        bool solo = CommanderUnitBanService.IsSolo;
        string label = solo ? "CONTINUE" : ready ? "READY  (CLICK TO EDIT)" : "READY";
        string tip = solo
            ? "Lock in these bans and start the match."
            : ready
                ? "Unlock your votes to change them."
                : "Lock in your votes. The bans lock when every player is ready, or when the clock runs out.";
        if (GUI.Button(new Rect(width - 232f, y, 220f, 40f), new GUIContent(label, tip), ready ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            service.ToggleReady();
        }
    }

    private void DrawPill(CommanderUnitBanService service)
    {
        float width = 360f;
        pillRect = new Rect((CommanderUiScale.Width - width) * 0.5f, 48f, width, 34f);
        string clock = service.SecondsLeft >= 0f ? $"  |  {FormatClock(service.SecondsLeft)}" : string.Empty;
        if (GUI.Button(pillRect, $"UNIT BANS  {service.Mine.Keys.Count} / {service.MaxBans}{clock}   OPEN", service.Mine.Ready ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            Show();
        }
    }

    private void DrawWindow(CommanderUnitBanService service)
    {
        float width = windowRect.width;
        float height = windowRect.height;
        float y = 36f;
        if (CommanderUiTheme.DrawHelpButton(width, ref helpVisible))
        {
            y += CommanderUiTheme.DrawHelpOverlay(new Rect(12f, 36f, width - 24f, 90f), HelpText) + 8f;
        }

        if (GUI.Button(new Rect(width - 34f, 3f, 26f, 22f), new GUIContent("X", "Hide the vote. It stays open: bring it back from the pill at the top of the screen."), CommanderUiTheme.ChromeButton))
        {
            Hide();
            return;
        }

        DrawHeader(service, y, width);
        y += HeaderHeight;
        CommanderUiTheme.DrawRule(new Rect(12f, y - 6f, width - 24f, 1f));

        float bodyHeight = Mathf.Max(120f, height - y - FooterHeight - 8f);
        DrawTabs(y);
        float rosterX = 12f + TabWidth + 8f;
        float rosterWidth = Mathf.Max(260f, width - rosterX - SummaryWidth - 20f);
        DrawRoster(service, new Rect(rosterX, y, rosterWidth, bodyHeight));
        DrawSummary(service, new Rect(rosterX + rosterWidth + 8f, y, width - rosterX - rosterWidth - 20f, bodyHeight));
        DrawFooter(service, height - FooterHeight + 6f, width);

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, width, height));
        GUI.DragWindow(new Rect(0f, 0f, width - 72f, 28f));
    }

    private void DrawTabs(float y)
    {
        for (int i = 0; i < TabLabels.Length; i++)
        {
            Rect rect = new(12f, y + (i * 34f), TabWidth, 30f);
            if (GUI.Button(rect, TabLabels[i], tab == i ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                tab = i;
                rosterScroll = Vector2.zero;
            }

            if (i > 0)
            {
                DrawSwatch(new Rect(rect.x + 3f, rect.y + 5f, 3f, rect.height - 10f), KindColors[i - 1]);
            }
        }
    }

    private void RefreshFiltered(IReadOnlyList<UnitDefinition> roster)
    {
        if (filteredFromCount == roster.Count && filteredTab == tab)
        {
            return;
        }

        filteredFromCount = roster.Count;
        filteredTab = tab;
        filtered.Clear();
        filteredPrices.Clear();
        for (int i = 0; i < roster.Count; i++)
        {
            if (tab == 0 || tab - 1 == CommanderUnitBanService.KindOf(roster[i]))
            {
                filtered.Add(roster[i]);
                filteredPrices.Add(CommanderEconomyService.FundsLabel(roster[i].value));
            }
        }
    }

    private void DrawRoster(CommanderUnitBanService service, Rect view)
    {
        RefreshFiltered(service.Roster);
        GUI.Box(view, string.Empty, CommanderUiTheme.Inset);
        if (filtered.Count == 0)
        {
            GUI.Label(new Rect(view.x + 14f, view.y + 12f, view.width - 28f, 44f), "Waiting for the mission to finish loading.", CommanderUiTheme.MutedLabel);
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
            if (card.yMax < rosterScroll.y || card.y > rosterScroll.y + scrollView.height)
            {
                continue;
            }

            DrawCard(service, filtered[i], filteredPrices[i], card);
        }

        GUI.EndScrollView();
    }
}
