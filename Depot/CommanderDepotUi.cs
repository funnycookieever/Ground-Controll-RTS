using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderDepotUi
{
    private const int WindowId = 0x434F4D44;

    /// <summary>Wide enough for "SPAWN RESERVE (00)" on one line; at 120 it wrapped and was clipped.</summary>
    private const float QueueButtonWidth = 170f;

    /// <summary>Teammates' queues the window has room for, one line each.</summary>
    private const int MaxTeamLines = 4;
    private const float TeamLineHeight = 18f;

    private const int DetailSlot = 2;
    private const int QueueSlot = 3;
    private const float DefaultQueueHeight = 78f;
    private const float DefaultDetailWidth = 214f;

    private static readonly Vector2 MinSize = new(480f, 500f);

    private static GUIStyle? rightCaption;
    private static GUIStyle? rightLabel;
    private static GUIStyle? descriptionStyle;

    private readonly CommanderSpawnService spawnService;
    private VehicleDepot? trackedDepot;
    private Rect windowRect;
    private Vector2 vehicleScroll;
    private string selectedCategory = "All";
    private bool positionInitialized;
    private VehicleDefinition? detailDefinition;
    private bool helpVisible;

    internal CommanderDepotUi(CommanderSpawnService spawnService)
    {
        this.spawnService = spawnService;
    }

    internal bool Visible { get; private set; }

    private static GUIStyle RightCaption => rightCaption ??= new GUIStyle(CommanderUiTheme.Caption) { alignment = TextAnchor.MiddleRight };

    private static GUIStyle RightLabel => rightLabel ??= new GUIStyle(CommanderUiTheme.Label) { alignment = TextAnchor.MiddleRight, wordWrap = false };

    private static GUIStyle DescriptionStyle => descriptionStyle ??= new GUIStyle(CommanderUiTheme.MutedLabel) { alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip };

    /// <summary>One queue picture. <paramref name="buildLeft"/> is the head's build countdown in
    /// seconds, negative for any other tile; the naval dock's queue draws with this too.
    /// <paramref name="clicked"/> is true when the player clicked the tile to cancel one. A tile with
    /// an <paramref name="owner"/> is a teammate's, drawn dimmed, and cannot be cancelled from here.</summary>
    internal static bool DrawQueueTile(ref float x, float y, float width, float height, float limit, UnitDefinition definition, int count, out bool clicked, float buildLeft = -1f, float buildSeconds = 0f, string? owner = null)
    {
        clicked = false;
        if (x + width > limit)
        {
            GUI.Label(new Rect(x, y, 40f, height), "...", CommanderUiTheme.MutedLabel);
            return false;
        }

        Rect tile = new(x, y, width, height);
        Color tint = GUI.color;
        if (owner != null)
        {
            GUI.color = new Color(tint.r, tint.g, tint.b, tint.a * 0.6f);
        }

        GUI.Box(tile, GUIContent.none, CommanderUiTheme.Panel);
        CommanderUnitPreview.DrawStill(tile, definition);
        if (count > 1)
        {
            GUI.Label(new Rect(tile.x, tile.yMax - 17f, tile.width - 4f, 16f), $"x{count}", RightCaption);
        }

        if (buildLeft >= 0f)
        {
            GUI.Label(new Rect(tile.x + 4f, tile.y + 1f, tile.width - 8f, 16f), CommanderBuildTimes.Format(buildLeft), CommanderUiTheme.Caption);
            float done = buildSeconds > 0f ? 1f - Mathf.Clamp01(buildLeft / buildSeconds) : 1f;
            GUI.Box(new Rect(tile.x + 1f, tile.yMax - 4f, (tile.width - 2f) * done, 3f), GUIContent.none, CommanderUiTheme.SelectedButton);
        }

        GUI.color = tint;
        string tooltip = owner != null ? $"Queued by {owner}." : "Click to cancel one and get its money back.";
        bool pressed = GUI.Button(tile, new GUIContent(string.Empty, $"{CommanderGameAccess.GetVehicleLabel(definition)}\n{tooltip}"), GUIStyle.none);
        clicked = pressed && owner == null;
        x += width + 4f;
        return true;
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    internal void Reset()
    {
        trackedDepot = null;
        Visible = false;
        helpVisible = false;
    }

    internal void Tick()
    {
        VehicleDepot? selectedDepot = spawnService.SelectedDepot;
        if (!ReferenceEquals(selectedDepot, trackedDepot))
        {
            trackedDepot = selectedDepot;
            Visible = selectedDepot != null;
            helpVisible = false;
        }
        else if (selectedDepot == null)
        {
            Visible = false;
        }

        Vector2 size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(640f, CommanderUiScale.Width - 24f), Mathf.Min(720f, CommanderUiScale.Height - 64f)),
            MinSize);
        if (!positionInitialized)
        {
            windowRect = new Rect(12f, 52f, size.x, size.y);
            positionInitialized = true;
        }
        else
        {
            windowRect.size = size;
        }

        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
    }

    /// <summary>Enter queues the vehicle in the detail panel: the one hovered, or last clicked.</summary>
    internal void PressBuy()
    {
        if (Visible && detailDefinition != null)
        {
            spawnService.QueueVehicle(detailDefinition);
        }
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return Visible && CommanderWindowScale.Visual(WindowId, windowRect).Contains(guiPoint);
    }

    internal void Draw()
    {
        if (!Visible || spawnService.SelectedDepot == null)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "VEHICLE DEPOT");
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
    }

    private static void DrawCategoryButton(string label, bool active, ref float x, float y, float width, Action action)
    {
        if (GUI.Button(new Rect(x, y, width, 26f), label, active ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            action();
        }

        x += width + 3f;
    }

    private static float DrawStat(float x, float y, float width, string label, string value)
    {
        GUI.Label(new Rect(x, y, width, 18f), label, CommanderUiTheme.Caption);
        GUI.Label(new Rect(x, y, width, 18f), value, RightLabel);
        return y + 20f;
    }

    /// <summary>What teammates have queued: theirs to cancel, so this only reads.</summary>
    private static void DrawTeamQueues(Rect rect, List<string> lines, int rows)
    {
        GUI.Box(rect, string.Empty, CommanderUiTheme.Inset);
        int hidden = lines.Count - rows;
        GUI.Label(new Rect(rect.x + 10f, rect.y + 2f, rect.width - 20f, 18f), hidden > 0 ? $"TEAM QUEUES  (+{hidden} MORE)" : "TEAM QUEUES", CommanderUiTheme.Caption);
        for (int i = 0; i < rows; i++)
        {
            CommanderUiTheme.FitLabel(new Rect(rect.x + 10f, rect.y + 20f + (i * TeamLineHeight), rect.width - 20f, TeamLineHeight), lines[i], CommanderUiTheme.MutedLabel);
        }
    }

    private void DrawWindow(int windowId)
    {
        float y = 36f;
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            string help = CommanderDraftService.IsDraftMission
                ? "DRAFT: nothing is for sale. The cards are what is left of the army you drafted; the battle queued all of it across your depots when it started. Click a vehicle to roll another one out of this depot. CLEAR keeps the queued units in reserve instead, to send out later. New units wait beside the depot until ordered, or go to the rally point you set."
                : $"Click a vehicle to queue it at this depot; the queue at the bottom shows what is waiting. Each unit takes a build time, longer for better units (up to 3 minutes), counted down on the first picture. Each unit costs its price and also draws the fuel and ammo shown beside its picture from the depots within {CommanderSettings.SupplyStoreRangeKm:0} km, so buy supply first. New units wait beside the depot until ordered, or go to the rally point you set: SET RALLY, or right-click with the depot selected. A dimmed card needs this depot upgraded to that level first: select the depot and press UPGRADE DEPOT. REINFORCE puts every unit this depot builds straight into that control group. CLEAR empties the queue, which is the way out of a queue whose head is waiting on funds or on enriched uranium.";
            y = 36f + CommanderUiTheme.DrawHelpOverlay(new Rect(12f, 36f, windowRect.width - 24f, 92f), help) + 10f;
        }

        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            Visible = false;
            return;
        }

        VehicleDepot? depot = spawnService.SelectedDepot;
        int level = depot != null ? CommanderEconomyService.GetDepotLevel(CommanderGameAccess.GetDepotOwner(depot)) : 1;
        GUI.Label(new Rect(12f, y, windowRect.width - 120f, 22f), spawnService.GetSelectedDepotLabel(), CommanderUiTheme.Header);
        GUI.Label(new Rect(windowRect.width - 108f, y, 96f, 22f), $"LEVEL {level}", RightCaption);
        y += 22f;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 20f), $"Rally: {spawnService.GetRallyLabel()}", CommanderUiTheme.MutedLabel);
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            GUI.Label(new Rect(windowRect.width - 220f, y, 208f, 20f), $"ENRICHED URANIUM {CommanderEconomyService.GetFriendlyWarheads()}", RightCaption);
        }

        y += 24f;

        float third = (windowRect.width - 24f - 8f) / 3f;
        if (GUI.Button(new Rect(12f, y, third, 28f), new GUIContent("SET RALLY", "Click, then click where this depot's new units should gather. A right click with the depot selected does the same."), CommanderUiTheme.Button))
        {
            spawnService.BeginRallyPointSelection();
        }

        if (GUI.Button(new Rect(16f + third, y, third, 28f), new GUIContent("CLEAR RALLY", "New units wait beside the depot."), CommanderUiTheme.Button))
        {
            spawnService.ClearRallyPoint();
        }

        int reinforceGroup = spawnService.GetReinforceGroup();
        if (GUI.Button(
            new Rect(20f + (third * 2f), y, third, 28f),
            new GUIContent(reinforceGroup == 0 ? "REINFORCE: OFF" : $"REINFORCE: GROUP {reinforceGroup}", "Puts every unit this depot builds into that control group. Click to cycle."),
            reinforceGroup == 0 ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            if (reinforceGroup == 9)
            {
                spawnService.ClearReinforceGroup();
            }
            else
            {
                spawnService.CycleReinforceGroup();
            }
        }

        y += 36f;
        y = DrawCategories(y);
        y += 6f;

        float detailWidth = CommanderWindowScale.Section(WindowId, DetailSlot, DefaultDetailWidth, 160f, windowRect.width - 200f);
        float statusHeight = string.IsNullOrWhiteSpace(spawnService.StatusText) ? 0f : 24f;
        List<string>? teamLines = CommanderTeamQueueService.Instance?.Lines;
        int teamRows = Mathf.Min(teamLines?.Count ?? 0, MaxTeamLines);
        float teamHeight = teamRows > 0 ? 28f + (teamRows * TeamLineHeight) : 0f;

        // The queue bar trades with the vehicle list, so a drag never pushes the rows below it out.
        float maxQueue = Mathf.Max(DefaultQueueHeight, windowRect.height - y - teamHeight - statusHeight - 16f - 120f);
        float queueHeight = CommanderWindowScale.Section(WindowId, QueueSlot, DefaultQueueHeight, DefaultQueueHeight, maxQueue);
        float listHeight = Mathf.Max(120f, windowRect.height - y - queueHeight - teamHeight - statusHeight - 16f);
        DrawVehicleList(new Rect(12f, y, windowRect.width - 24f - detailWidth - 8f, listHeight));
        DrawDetail(new Rect(windowRect.width - 12f - detailWidth, y, detailWidth, listHeight));
        CommanderWindowScale.Splitter(
            WindowId, DetailSlot, new Rect(windowRect.width - 20f - detailWidth, y, 8f, listHeight), alongX: true, inverted: true, detailWidth, 160f, windowRect.width - 200f);
        y += listHeight + 8f;
        CommanderWindowScale.Splitter(
            WindowId, QueueSlot, new Rect(12f, y - 8f, windowRect.width - 24f, 8f), alongX: false, inverted: true, queueHeight, DefaultQueueHeight, maxQueue);
        DrawQueue(new Rect(12f, y, windowRect.width - 24f, queueHeight - 8f));
        if (teamRows > 0)
        {
            DrawTeamQueues(new Rect(12f, y + queueHeight, windowRect.width - 24f, teamHeight - 8f), teamLines!, teamRows);
        }

        if (statusHeight > 0f)
        {
            GUI.Label(new Rect(12f, windowRect.height - 30f, windowRect.width - 24f, 22f), spawnService.StatusText, CommanderUiTheme.MutedLabel);
        }

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, windowRect.width, windowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawCategories(float y)
    {
        string[] categories = spawnService.BuildCategories();
        bool categoryExists = false;
        for (int i = 0; i < categories.Length; i++)
        {
            if (string.Equals(categories[i], selectedCategory, StringComparison.Ordinal))
            {
                categoryExists = true;
                break;
            }
        }

        if (!categoryExists)
        {
            selectedCategory = "All";
        }

        // "All" leads the row (the list is built with it last): it is the one most players stay on.
        float x = 12f;
        for (int n = 0; n < categories.Length; n++)
        {
            string category = categories[(n + categories.Length - 1) % categories.Length];
            float width = Mathf.Max(44f, CommanderUiTheme.Button.CalcSize(new GUIContent(category)).x + 4f);
            if (x + width > windowRect.width - 12f)
            {
                x = 12f;
                y += 29f;
            }

            DrawCategoryButton(category, string.Equals(category, selectedCategory, StringComparison.Ordinal), ref x, y, width, () => selectedCategory = category);
        }

        return y + 29f;
    }

    private void DrawVehicleList(Rect view)
    {
        List<VehicleDefinition> vehicles = spawnService.GetFilteredVehicleDefinitions(selectedCategory);
        const float gap = CommanderUiTheme.CardGap;
        float innerWidth = view.width - 14f;
        float cardWidth = CommanderUiTheme.CardWidth(innerWidth, out int columns);
        float cardHeight = CommanderUiTheme.CardHeight(cardWidth);
        int rows = (vehicles.Count + columns - 1) / columns;
        Rect inner = new(0f, 0f, innerWidth, Mathf.Max(view.height, (rows * (cardHeight + gap)) - gap));
        vehicleScroll = GUI.BeginScrollView(view, vehicleScroll, inner);
        Vector2 mouse = Event.current.mousePosition;
        bool draft = CommanderDraftService.IsDraftMission;
        bool mouseInView = mouse.y >= vehicleScroll.y && mouse.y <= vehicleScroll.y + view.height;
        for (int i = 0; i < vehicles.Count; i++)
        {
            Rect card = new((i % columns) * (cardWidth + gap), (i / columns) * (cardHeight + gap), cardWidth, cardHeight);
            if (card.yMax < vehicleScroll.y || card.y > vehicleScroll.y + view.height)
            {
                continue;
            }

            bool hovered = mouseInView && card.Contains(mouse);
            if (hovered)
            {
                detailDefinition = vehicles[i];
            }

            VehicleDefinition vehicle = vehicles[i];
            int tier = CommanderEconomyService.GetRequiredDepotLevel(vehicle);
            if (CommanderUiTheme.DrawCard(
                card,
                vehicle,
                CommanderGameAccess.GetVehicleLabel(vehicle),
                draft ? $"{CommanderFactionVehicleService.GetReserveCount(vehicle)} LEFT" : CommanderEconomyService.FundsLabel(CommanderFactionVehicleService.GetPurchaseCost(vehicle)),
                draft ? string.Empty : $"T{tier}",
                selected: false,
                spawnService.IsLockedAtSelectedDepot(vehicle) ? $"DEPOT LEVEL {tier}" : null,
                CommanderBuildTimes.Format(CommanderFactionVehicleService.GetReserveCount(vehicle) > 0 ? 0f : CommanderBuildTimes.For(vehicle))))
            {
                detailDefinition = vehicles[i];
                spawnService.QueueVehicle(vehicles[i]);
            }
        }

        GUI.EndScrollView();
        if ((detailDefinition == null || !vehicles.Contains(detailDefinition)) && vehicles.Count > 0)
        {
            detailDefinition = vehicles[0];
        }
    }

    /// <summary>The hovered vehicle, larger: its turning model, what it costs, and what it is.</summary>
    private void DrawDetail(Rect rect)
    {
        GUI.Box(rect, string.Empty, CommanderUiTheme.Inset);
        VehicleDefinition? definition = detailDefinition;
        if (definition == null)
        {
            return;
        }

        Rect model = new(rect.x + 1f, rect.y + 1f, rect.width - 2f, Mathf.Min(rect.width * 0.8f, rect.height - 60f));
        CommanderUnitPreview.DrawLive(model, definition, 1);
        float y = model.yMax + 4f;
        float x = rect.x + 10f;
        float width = rect.width - 20f;
        CommanderUiTheme.FitLabel(new Rect(x, y, width, 22f), CommanderGameAccess.GetVehicleLabel(definition), CommanderUiTheme.Header);
        y += 22f;
        GUI.Label(new Rect(x, y, width, 18f), CommanderGameAccess.GetVehicleCategoryLabel(definition).ToUpperInvariant(), CommanderUiTheme.Caption);
        y += 24f;

        if (CommanderDraftService.IsDraftMission)
        {
            y = DrawStat(x, y, width, "IN RESERVE", CommanderFactionVehicleService.GetReserveCount(definition).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            y = DrawStat(x, y, width, "PRICE", CommanderEconomyService.FundsLabel(CommanderFactionVehicleService.GetPurchaseCost(definition)));
            y = DrawStat(x, y, width, "DEPOT LEVEL", CommanderEconomyService.GetRequiredDepotLevel(definition).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            y = DrawStat(x, y, width, "FUEL DRAW", $"{CommanderSupplyStoreService.GetSpawnFuelKg(definition):0} kg");
            y = DrawStat(x, y, width, "AMMO DRAW", $"{CommanderSupplyStoreService.GetSpawnAmmoKg(definition):0} kg");
        }

        int warheads = CommanderFactionVehicleService.GetWarheadCost(definition);
        if (warheads > 0)
        {
            y = DrawStat(x, y, width, "WARHEADS", $"{warheads} of {CommanderEconomyService.GetFriendlyWarheads()}");
        }

        if (!string.IsNullOrWhiteSpace(definition.description) && rect.yMax - y > 40f)
        {
            GUI.Label(new Rect(x, y + 4f, width, rect.yMax - y - 8f), definition.description, DescriptionStyle);
        }
    }

    /// <summary>The waiting units as small pictures, one per run of the same type, with a count.</summary>
    private void DrawQueue(Rect rect)
    {
        GUI.Box(rect, string.Empty, CommanderUiTheme.Inset);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 2f, 80f, 18f), "QUEUE", CommanderUiTheme.Caption);
        if (GUI.Button(new Rect(rect.xMax - QueueButtonWidth - 8f, rect.y + 6f, QueueButtonWidth, 24f), new GUIContent("CLEAR", "Empties this depot's queue and refunds every unit in it."), CommanderUiTheme.Button))
        {
            spawnService.ClearQueue();
        }

        int reserve = spawnService.CountUnqueuedReserve();
        GUI.enabled = reserve > 0;
        if (GUI.Button(
            new Rect(rect.xMax - QueueButtonWidth - 8f, rect.y + 36f, QueueButtonWidth - 62f, 24f),
            new GUIContent($"RESERVE ({reserve})", "Queues every vehicle in your reserve (factory output and the like) at this depot, free. They follow this depot's rally point, AUTO ATTACK and REINFORCE."),
            CommanderUiTheme.Button))
        {
            spawnService.SpawnReserveAtSelectedDepot();
        }

        GUI.enabled = true;
        bool auto = spawnService.IsAutoReserveDepotSelected();
        if (GUI.Button(
            new Rect(rect.xMax - 64f, rect.y + 36f, 56f, 24f),
            new GUIContent("AUTO", "On (highlighted): keeps sending new reserve vehicles out of this depot as factories and the like deliver them. One depot at a time."),
            auto ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            spawnService.ToggleAutoReserveAtSelectedDepot();
        }

        CommanderSpawnService.DepotSpawnQueue? queue = spawnService.GetSelectedQueue();
        List<CommanderTeamQueueService.TeamRun>? teamRuns = CommanderTeamQueueService.Instance?.Runs;
        bool teamQueued = false;
        for (int i = 0; teamRuns != null && i < teamRuns.Count && !teamQueued; i++)
        {
            teamQueued = ReferenceEquals(teamRuns[i].Depot, spawnService.SelectedDepot);
        }

        if (!teamQueued && (queue == null || queue.PendingDefinitions.Count == 0))
        {
            GUI.Label(new Rect(rect.x + 10f, rect.y + 22f, rect.width - QueueButtonWidth - 26f, rect.height - 26f), CommanderDraftService.IsDraftMission ? "Nothing waiting. Click a vehicle above to roll it out." : "Nothing waiting. Click a vehicle above to queue it.", CommanderUiTheme.MutedLabel);
            return;
        }

        float x = rect.x + 10f;
        float tileY = rect.y + 20f;
        float tileHeight = rect.height - 24f;
        float tileWidth = tileHeight * 1.4f;
        float limit = rect.xMax - QueueButtonWidth - 16f;
        if (queue != null && !DrawOwnQueue(queue, ref x, tileY, tileWidth, tileHeight, limit))
        {
            return;
        }

        for (int i = 0; teamRuns != null && i < teamRuns.Count; i++)
        {
            CommanderTeamQueueService.TeamRun run = teamRuns[i];
            if (ReferenceEquals(run.Depot, spawnService.SelectedDepot))
            {
                float teamLeft = run.BuildDoneAt >= 0f ? Mathf.Max(0f, run.BuildDoneAt - Time.time) : -1f;
                if (!DrawQueueTile(ref x, tileY, tileWidth, tileHeight, limit, run.Definition, run.Count, out _, teamLeft, 0f, run.Owner))
                {
                    return;
                }
            }
        }
    }

    /// <summary>False when the row is full or a click just changed the queue.</summary>
    private bool DrawOwnQueue(CommanderSpawnService.DepotSpawnQueue queue, ref float x, float tileY, float tileWidth, float tileHeight, float limit)
    {
        int runStart = 0;

        // The countdown rides on the first picture, the unit being built. A click on a run cancels its
        // last unit, so the one being built keeps its clock unless it is the only one in the run.
        float buildLeft = queue.BuildDoneAt > 0f ? queue.BuildDoneAt - Time.time : -1f;
        List<VehicleDefinition> pending = queue.PendingDefinitions;
        for (int i = 1; i <= pending.Count; i++)
        {
            if (i < pending.Count && pending[i] == pending[runStart])
            {
                continue;
            }

            bool fits = DrawQueueTile(ref x, tileY, tileWidth, tileHeight, limit, pending[runStart], i - runStart, out bool clicked, buildLeft, queue.BuildSeconds);
            if (clicked)
            {
                spawnService.CancelQueued(i - 1);
                return false;
            }

            if (!fits)
            {
                return false;
            }

            buildLeft = -1f;
            runStart = i;
        }

        return true;
    }
}
