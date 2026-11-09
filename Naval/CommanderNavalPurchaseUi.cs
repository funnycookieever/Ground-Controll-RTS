using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderNavalPurchaseUi
{
    private const int WindowId = 0x434F4D4E;
    private const int DetailSlot = 2;
    private const int QueueSlot = 3;
    private const float DefaultQueueHeight = 70f;
    private const float DefaultDetailWidth = 214f;

    private static readonly Vector2 MinSize = new(480f, 480f);

    private static GUIStyle? rightCaption;
    private static GUIStyle? rightLabel;
    private static GUIStyle? descriptionStyle;

    private readonly CommanderNavalPurchaseService service;
    private bool helpVisible;
    private Unit? trackedDock;
    private bool positionInitialized;
    private Rect windowRect;
    private Vector2 scroll;
    private ShipDefinition? hoveredDefinition;

    internal CommanderNavalPurchaseUi(CommanderNavalPurchaseService service)
    {
        this.service = service;
    }

    internal bool Visible { get; private set; }

    private static GUIStyle RightCaption => rightCaption ??= new GUIStyle(CommanderUiTheme.Caption) { alignment = TextAnchor.MiddleRight };

    private static GUIStyle RightLabel => rightLabel ??= new GUIStyle(CommanderUiTheme.Label) { alignment = TextAnchor.MiddleRight, wordWrap = false };

    private static GUIStyle DescriptionStyle => descriptionStyle ??= new GUIStyle(CommanderUiTheme.MutedLabel) { alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip };

    private static Vector2 DefaultSize => new(Mathf.Min(640f, CommanderUiScale.Width - 24f), Mathf.Min(680f, CommanderUiScale.Height - 64f));

    internal void Toggle()
    {
        Visible = !Visible;
        helpVisible = false;
        EnsurePosition();
    }

    /// <summary>
    /// Opens when a friendly naval dock is selected and closes when the selection moves off it, the
    /// way DEPOT CONTROL follows a depot.
    /// </summary>
    internal void TrackDock(Unit? dock)
    {
        if (ReferenceEquals(dock, trackedDock))
        {
            return;
        }

        if (dock != null)
        {
            service.SelectedDock = dock;
            Visible = true;
            EnsurePosition();
        }
        else if (trackedDock != null)
        {
            Visible = false;
            helpVisible = false;
        }

        trackedDock = dock;
    }

    internal void Hide()
    {
        Visible = false;
        helpVisible = false;
    }

    /// <summary>Enter on PURCHASE; false when this window is shut.</summary>
    internal bool PressBuy()
    {
        if (Visible)
        {
            service.BeginPurchase();
        }

        return Visible;
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        EnsurePosition();
        windowRect.size = CommanderWindowScale.Size(WindowId, DefaultSize, MinSize);
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "NAVAL REINFORCEMENTS");
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return Visible && CommanderWindowScale.Visual(WindowId, windowRect).Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
        EnsurePosition();
    }

    /// <summary>A hull a factory built is in reserve and launches free.</summary>
    private static string PriceLabel(ShipDefinition ship)
    {
        int held = CommanderNavalPurchaseService.GetReserve(CommanderGameAccess.GetLocalHq(), ship);
        return held > 0 ? $"FREE ({held} in reserve)" : CommanderEconomyService.FundsLabel(ship.value);
    }

    private static float DrawStat(float x, float y, float width, string label, string value)
    {
        GUI.Label(new Rect(x, y, width, 18f), label, CommanderUiTheme.Caption);
        GUI.Label(new Rect(x, y, width, 18f), value, RightLabel);
        return y + 20f;
    }

    /// <summary>Why a hull cannot be bought right now, or null when it can.</summary>
    private static string? LockText(ShipDefinition definition, int dockLevel)
    {
        if (CommanderUnitBanService.IsBanned(definition))
        {
            return "BANNED";
        }

        return CommanderNavalPurchaseService.IsUnlocked(definition, dockLevel)
            ? null
            : $"DOCK LEVEL {CommanderNavalPurchaseService.GetRequiredDockLevel(definition)}";
    }

    private void DrawWindow(int windowId)
    {
        float y = 36f;
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            y = 36f + 10f + CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 36f, windowRect.width - 24f, 92f),
                "A naval dock unlocks all of this. Build one from the BUILD window on dry land at the water's edge, within the naval build radius of a base you hold; each of its three levels opens a heavier class of hull, and the enemy commander is on exactly the same ladder. Click a ship, then PURCHASE to queue it. Each hull takes a build time, longer for bigger ships (up to 3 minutes), counted down on its picture in the build queue; it is paid for when it launches, in the water beside the dock it was ordered at, and waits there for orders.");
        }

        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            Hide();
            return;
        }

        int dockLevel = CommanderNavalPurchaseService.DockLevel;
        GUI.Label(
            new Rect(12f, y, windowRect.width - 120f, 22f),
            dockLevel <= 0 ? "NO NAVAL DOCK" : "NAVAL DOCK",
            CommanderUiTheme.Header);
        GUI.Label(new Rect(windowRect.width - 108f, y, 96f, 22f), $"LEVEL {dockLevel}/{CommanderEconomyService.MaxLevel}", RightCaption);
        y += 22f;
        string unlocks = dockLevel <= 0
            ? "Build one on the shore from the BUILD window to buy ships."
            : "Unlocked: " + CommanderNavalPurchaseService.GetLevelUnlockLabel(dockLevel) + ".";
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 20f), unlocks, CommanderUiTheme.MutedLabel);
        y += 24f;

        // A row of its own: beside the buttons it wrapped and was cut off, as the depot's would be.
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 20f), $"Rally: {service.GetRallyLabel()}", CommanderUiTheme.MutedLabel);
        y += 24f;

        float half = (windowRect.width - 24f - 4f) / 2f;
        if (GUI.Button(new Rect(12f, y, half, 28f), new GUIContent("SET RALLY", "Click, then click the water where this dock's new ships should gather. Each one gets its own berth, spaced so they never collide."), CommanderUiTheme.Button))
        {
            service.BeginRallySelection();
        }

        if (GUI.Button(new Rect(16f + half, y, half, 28f), new GUIContent("CLEAR RALLY", "New ships wait beside the dock."), CommanderUiTheme.Button))
        {
            service.ClearRallyPoint();
        }

        y += 36f;

        float detailWidth = CommanderWindowScale.Section(WindowId, DetailSlot, DefaultDetailWidth, 160f, windowRect.width - 200f);
        const float footerHeight = 46f;
        string status = service.StatusText;
        float statusHeight = string.IsNullOrWhiteSpace(status) ? 0f : 40f;

        // The queue bar trades with the ship list, so a drag never pushes PURCHASE out.
        float maxQueue = Mathf.Max(DefaultQueueHeight, windowRect.height - y - footerHeight - statusHeight - 12f - 120f);
        float queueHeight = CommanderWindowScale.Section(WindowId, QueueSlot, DefaultQueueHeight, DefaultQueueHeight, maxQueue);
        float listHeight = Mathf.Max(120f, windowRect.height - y - footerHeight - queueHeight - statusHeight - 12f);
        DrawShipList(new Rect(12f, y, windowRect.width - 24f - detailWidth - 8f, listHeight), dockLevel);
        DrawDetail(new Rect(windowRect.width - 12f - detailWidth, y, detailWidth, listHeight), dockLevel);
        CommanderWindowScale.Splitter(
            WindowId, DetailSlot, new Rect(windowRect.width - 20f - detailWidth, y, 8f, listHeight), alongX: true, inverted: true, detailWidth, 160f, windowRect.width - 200f);
        y += listHeight + 8f;
        CommanderWindowScale.Splitter(
            WindowId, QueueSlot, new Rect(12f, y - 8f, windowRect.width - 24f, 8f), alongX: false, inverted: true, queueHeight, DefaultQueueHeight, maxQueue);
        DrawBuildQueue(new Rect(12f, y, windowRect.width - 24f, queueHeight - 8f));
        y += queueHeight;

        ShipDefinition? selected = service.SelectedDefinition;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && selected != null && LockText(selected, dockLevel) == null;
        string purchase = selected != null ? $"PURCHASE   {PriceLabel(selected)}" : "NO SHIPS AVAILABLE";
        if (GUI.Button(new Rect(12f, y, windowRect.width - 24f, 38f), purchase, CommanderUiTheme.PrimaryButton))
        {
            service.BeginPurchase();
        }

        GUI.enabled = oldEnabled;
        if (statusHeight > 0f)
        {
            GUI.Label(new Rect(12f, windowRect.height - statusHeight - 8f, windowRect.width - 24f, statusHeight), status, CommanderUiTheme.MutedLabel);
        }

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, windowRect.width, windowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    /// <summary>Hulls waiting at the dock, as the depot window shows its queue: the first one
    /// carries the build countdown.</summary>
    private void DrawBuildQueue(Rect rect)
    {
        GUI.Box(rect, string.Empty, CommanderUiTheme.Inset);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 2f, 120f, 18f), "BUILD QUEUE", CommanderUiTheme.Caption);
        IReadOnlyList<ShipDefinition> queue = service.BuildQueue;
        const float clearWidth = 80f;
        if (GUI.Button(new Rect(rect.xMax - clearWidth - 8f, rect.y + 6f, clearWidth, 24f), new GUIContent("CLEAR", "Empties the build queue and refunds every hull in it."), CommanderUiTheme.Button))
        {
            service.ClearBuildQueue();
        }

        if (queue.Count == 0)
        {
            GUI.Label(new Rect(rect.x + 10f, rect.y + 22f, rect.width - clearWidth - 26f, rect.height - 26f), "Nothing building. Pick a ship and press PURCHASE.", CommanderUiTheme.MutedLabel);
            return;
        }

        float x = rect.x + 10f;
        float tileHeight = rect.height - 24f;
        bool blocked = service.LaunchBlocked;
        string? held = service.HeldReason;
        float buildLeft = blocked || held != null ? -1f : service.BuildLeft;
        for (int i = 0; i < queue.Count; i++)
        {
            float tileX = x;
            bool fits = CommanderDepotUi.DrawQueueTile(ref x, rect.y + 20f, tileHeight * 1.4f, tileHeight, rect.xMax - clearWidth - 16f, queue[i], 1, out bool clicked, i == 0 ? buildLeft : -1f, service.BuildSeconds);
            if (clicked)
            {
                service.CancelQueued(i);
                return;
            }

            if (!fits)
            {
                return;
            }

            // Built, but no clear water to launch into: a countdown stuck at 0:01 read as a hang.
            if (i == 0 && blocked)
            {
                GUI.Label(new Rect(tileX + 4f, rect.y + 21f, (tileHeight * 1.4f) - 8f, 16f), new GUIContent("NO WATER", "No clear water beside the dock. Move the ships waiting there, or set a rally point."), CommanderUiTheme.Caption);
            }
            else if (i == 0 && held != null)
            {
                GUI.Label(new Rect(tileX + 4f, rect.y + 21f, (tileHeight * 1.4f) - 8f, 16f), new GUIContent("ON HOLD", held), CommanderUiTheme.Caption);
            }
        }
    }

    private void DrawShipList(Rect view, int dockLevel)
    {
        IReadOnlyList<ShipDefinition> ships = service.ShipDefinitions;
        const float gap = CommanderUiTheme.CardGap;
        float innerWidth = view.width - 14f;
        float cardWidth = CommanderUiTheme.CardWidth(innerWidth, out int columns);
        float cardHeight = CommanderUiTheme.CardHeight(cardWidth);
        int rows = (ships.Count + columns - 1) / columns;
        Rect inner = new(0f, 0f, innerWidth, Mathf.Max(view.height, (rows * (cardHeight + gap)) - gap));
        scroll = GUI.BeginScrollView(view, scroll, inner);
        Vector2 mouse = Event.current.mousePosition;
        bool mouseInView = mouse.y >= scroll.y && mouse.y <= scroll.y + view.height;
        if (Event.current.type == EventType.Repaint)
        {
            hoveredDefinition = null;
        }

        ShipDefinition? selected = service.SelectedDefinition;
        for (int i = 0; i < ships.Count; i++)
        {
            Rect card = new((i % columns) * (cardWidth + gap), (i / columns) * (cardHeight + gap), cardWidth, cardHeight);
            if (card.yMax < scroll.y || card.y > scroll.y + view.height)
            {
                continue;
            }

            ShipDefinition ship = ships[i];
            if (mouseInView && card.Contains(mouse))
            {
                hoveredDefinition = ship;
            }

            // Locked hulls stay on the list, dimmed, so the dock ladder reads as something to
            // build toward instead of a list that mysteriously grows.
            if (CommanderUiTheme.DrawCard(
                card,
                ship,
                ship.unitName,
                PriceLabel(ship),
                $"T{CommanderNavalPurchaseService.GetRequiredDockLevel(ship)}",
                ReferenceEquals(ship, selected),
                LockText(ship, dockLevel),
                CommanderBuildTimes.Format(CommanderBuildTimes.For(ship))))
            {
                service.SelectDefinition(i);
            }
        }

        GUI.EndScrollView();
    }

    /// <summary>The hovered ship, or the selected one: its turning model, what it costs, and what it is.</summary>
    private void DrawDetail(Rect rect, int dockLevel)
    {
        GUI.Box(rect, string.Empty, CommanderUiTheme.Inset);
        ShipDefinition? definition = hoveredDefinition ?? service.SelectedDefinition;
        if (definition == null)
        {
            return;
        }

        Rect model = new(rect.x + 1f, rect.y + 1f, rect.width - 2f, Mathf.Min(rect.width * 0.8f, rect.height - 60f));
        CommanderUnitPreview.DrawLive(model, definition, 1);
        float y = model.yMax + 4f;
        float x = rect.x + 10f;
        float width = rect.width - 20f;
        CommanderUiTheme.FitLabel(new Rect(x, y, width, 22f), definition.unitName, CommanderUiTheme.Header);
        y += 22f;
        GUI.Label(new Rect(x, y, width, 18f), definition.shipType.ToString().ToUpperInvariant(), CommanderUiTheme.Caption);
        y += 24f;
        y = DrawStat(x, y, width, "PRICE", PriceLabel(definition));
        y = DrawStat(x, y, width, "DOCK LEVEL", CommanderNavalPurchaseService.GetRequiredDockLevel(definition).ToString(CultureInfo.InvariantCulture));
        string? lockText = LockText(definition, dockLevel);
        if (lockText != null)
        {
            y = DrawStat(x, y, width, "STATUS", lockText);
        }

        if (!string.IsNullOrWhiteSpace(definition.description) && rect.yMax - y > 40f)
        {
            GUI.Label(new Rect(x, y + 4f, width, rect.yMax - y - 8f), definition.description, DescriptionStyle);
        }
    }

    private void EnsurePosition()
    {
        if (positionInitialized)
        {
            return;
        }

        Vector2 size = CommanderWindowScale.Size(WindowId, DefaultSize, MinSize);
        windowRect = new Rect(74f, Mathf.Max(12f, (CommanderUiScale.Height - size.y) * 0.5f), size.x, size.y);
        positionInitialized = true;
    }
}
