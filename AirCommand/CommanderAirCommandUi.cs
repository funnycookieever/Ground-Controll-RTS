using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The AIR COMMAND window: buy an aircraft, say what it is for, arm it, launch it.
/// </summary>
/// <remarks>
/// Read top to bottom it is the order a player actually thinks in — <b>aircraft first</b>, then the
/// job, then the weapons, then the field. The old panel asked for the mission type first, then the
/// weapon, and only then offered the aeroplanes that happened to fit both; changing any one answer
/// re-sorted the list under the cursor and threw the other two away. Two things carry most of the
/// simplification: AREA with FOCUS ANY (<see cref="CommanderAirCommandService.AirCommandMode.Hybrid"/>)
/// needs no thought, and the preset tab means a loadout is one click unless the player wants
/// otherwise. The service's modes outnumber the buttons on purpose; the enemy commander uses them all.
/// </remarks>
internal sealed partial class CommanderAirCommandUi
{
    internal const int WindowId = 0x434F4143;
    private const int MissionWindowId = 0x434F414D;
    private const int AircraftSlot = 2;
    private const int AirbaseSlot = 3;
    private const int PreviewSlot = 4;
    private const float MinAircraftHeight = (3 * 52f) + 4f;
    private const float MinLoadoutHeight = 220f;
    private const float MinAirbaseHeight = 76f;
    private const float MaxSectionHeight = 900f;
    private const float DefaultPreviewWidth = 230f;
    private const float MinPreviewWidth = 140f;
    private const float MinAircraftListWidth = 240f;

    private static readonly Vector2 MinSize = new(560f, 420f);
    private static readonly Vector2 MinMissionSize = new(300f, 160f);

    private readonly CommanderAirCommandService service;
    private readonly List<Aircraft> missionAircraft = new();
    private readonly List<Aircraft> idleAircraft = new();
    private Rect windowRect = new(0f, 0f, 820f, 760f);
    private float missionContentHeight = 160f;
    private Vector2 aircraftScroll;
    private Vector2 presetScroll;
    private Vector2 loadoutDropdownScroll;
    private Vector2 airbaseScroll;
    private Vector2 windowScroll;
    private int openHardpointGroup = -1;
    private bool positionInitialized;

    /// <summary>
    /// What the window's content asked for last frame, which <see cref="Tick"/> sizes it to. Sections
    /// have fixed minimum heights (the ADVANCED loadout tab needs ~220 px, plus 68 for a CARGO BAY row),
    /// so a fixed window can push LAUNCH off the bottom. The window grows to its content, capped by the
    /// screen. Seeded at 900 so a window that already fitted keeps its size.
    /// </summary>
    private float requiredHeight = 900f;
    private bool helpVisible;
    private Rect missionWindowRect;
    private Vector2 missionScroll;
    private bool missionPositionInitialized;
    private bool openedMap;
    private string hoverTooltip = string.Empty;

    /// <summary>The FOCUS that AREA comes back to after the player picked something else.</summary>
    private CommanderAirCommandService.AirCommandMode lastAreaMode = CommanderAirCommandService.AirCommandMode.Hybrid;

    internal CommanderAirCommandUi(CommanderAirCommandService service)
    {
        this.service = service;
        Instance = this;
    }

    internal static CommanderAirCommandUi? Instance { get; private set; }

    internal bool Visible { get; private set; }

    /// <summary>The TRANSPORT button's handoff: cargo runs have their own planner (SUPPLY RUN), and
    /// the overlay that owns both windows supplies how to open it on an airframe.</summary>
    internal System.Action<AircraftDefinition?>? OpenTransport { get; set; }

    // The mission list is its own window: it outlives the AIR COMMAND window so the player can see
    // and re-task what is already flying without reopening the whole launch panel. It starts
    // closed — at mission start there is nothing flying for it to list.
    internal bool MissionsVisible { get; private set; }

    /// <summary>Once the player has set a height or moved a bar, the window keeps its height and
    /// the loadout box fills what is left. Sizing it to content as well would chase its own tail.</summary>
    private static bool IsStretched => CommanderWindowScale.HasHeight(WindowId)
        || CommanderWindowScale.Section(WindowId, AircraftSlot) > 0f
        || CommanderWindowScale.Section(WindowId, AirbaseSlot) > 0f;

    internal void ShowMissions()
    {
        MissionsVisible = true;
    }

    internal void ToggleMissions()
    {
        MissionsVisible = !MissionsVisible;
    }

    internal void Toggle()
    {
        if (Visible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    internal void Show()
    {
        ShowInternal(null);
    }

    /// <summary>
    /// Opens the window armed for one set of hangar doors: the aircraft list becomes that hangar's
    /// own, so a landing pad offers helicopters and a hangar offers aeroplanes, and the sortie rolls
    /// out of those doors rather than out of whichever hangar the airbase felt like using.
    /// </summary>
    internal void ShowForHangar(Hangar hangar)
    {
        ShowInternal(hangar);
    }

    internal void Hide()
    {
        if (service.AwaitingAreaSelection)
        {
            service.CancelAreaSelection();
        }

        Visible = false;
        EndRename();
        service.SetUiVisible(false);
        service.SetHangarFilter(null);

        // Only put the map away if this window is what put it up, and only when the player was not
        // running the tactical map anyway.
        if (openedMap && !CommanderSettings.ShowTacticalMap)
        {
            CommanderTacticalMapService.Instance?.Close();
        }

        openedMap = false;
    }

    internal bool HandleMapKey()
    {
        if (!Visible)
        {
            return false;
        }

        Hide();
        return true;
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
        missionPositionInitialized = false;
    }

    /// <summary>Enter on the buy button. A mission with an area to place is left to the mouse, and
    /// false hands the key to the next open shop. While a dropdown or an area pick is open the key
    /// goes nowhere, the same as the disabled button.</summary>
    internal bool PressBuy()
    {
        if (!Visible || CommanderAirCommandService.RequiresMissionArea(service.SelectedMode))
        {
            return false;
        }

        if (openHardpointGroup < 0 && !service.AwaitingAreaSelection)
        {
            service.BeginAreaSelection();
        }

        return true;
    }

    internal void Tick()
    {
        if (Visible && CommanderTacticalMapService.Instance?.IsOpen != true)
        {
            openedMap |= CommanderTacticalMapService.Instance?.Open() == true;
        }

        // The cap is in the window's own unscaled units, so shrinking the window with its grip
        // really does make room for the whole content; a screen-sized cap in those units left
        // LAUNCH clipped however small the player made it.
        Vector2 size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(
                Mathf.Min(760f, CommanderUiScale.Width - 390f),
                Mathf.Min(IsStretched ? 900f : Mathf.Max(760f, requiredHeight), (CommanderUiScale.Height - 60f) / CommanderWindowScale.Get(WindowId))),
            MinSize);
        if (!positionInitialized)
        {
            windowRect = new Rect(12f, 52f, size.x, size.y);
            positionInitialized = true;
        }
        else
        {
            windowRect.size = size;
            windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        }

        // Edges and grip like AIR COMMAND: width and height drag on their own, the list fills what is left.
        Vector2 missionSize = CommanderWindowScale.Size(
            MissionWindowId,
            new Vector2(326f, Mathf.Min(missionContentHeight, (CommanderUiScale.Height - 64f) / CommanderWindowScale.Get(MissionWindowId))),
            MinMissionSize);
        if (!missionPositionInitialized)
        {
            // Beside the AIR COMMAND window rather than against the right edge, which the tactical map uses.
            missionWindowRect = new Rect(
                Mathf.Min(windowRect.xMax + 8f, Mathf.Max(12f, CommanderUiScale.Width - missionSize.x - 16f)),
                52f,
                missionSize.x,
                missionSize.y);
            missionPositionInitialized = true;
        }
        else
        {
            missionWindowRect.size = missionSize;
            missionWindowRect = CommanderWindowScale.Clamp(MissionWindowId, missionWindowRect);
        }

        service.CollectMissionAircraft(missionAircraft);
        if (Visible || MissionsVisible)
        {
            // Scans every faction unit, so keep it to the frames the list is actually shown.
            service.CollectIdleAircraft(idleAircraft);
        }
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return (Visible && CommanderWindowScale.Visual(WindowId, windowRect).Contains(guiPoint))
            || (MissionsVisible && CommanderWindowScale.Visual(MissionWindowId, missionWindowRect).Contains(guiPoint));
    }

    internal void Draw()
    {
        if (!Visible && !MissionsVisible)
        {
            return;
        }

        SetBlockingRects();
        if (Visible)
        {
            windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "AIR COMMAND");
        }

        if (MissionsVisible)
        {
            missionWindowRect = CommanderWindowScale.Window(MissionWindowId, missionWindowRect, DrawMissionWindow, "AIR MISSIONS");
        }

        SetBlockingRects();
    }

    private void ShowInternal(Hangar? hangar)
    {
        bool wasVisible = Visible;
        Visible = true;
        service.SetUiVisible(true);
        service.SetHangarFilter(hangar);
        helpVisible = false;
        aircraftScroll = Vector2.zero;
        presetScroll = Vector2.zero;
        airbaseScroll = Vector2.zero;
        openHardpointGroup = -1;
        if (wasVisible)
        {
            return;
        }

        // The mod's own map, not the game's fullscreen map, so picking an area stays inside the RTS layer.
        openedMap = CommanderTacticalMapService.Instance?.OpenForPlacement() == true;
    }

    /// <summary>A window that is not drawn must not swallow an area-selection click, so a hidden one
    /// hands over an empty rect rather than its last position.</summary>
    private void SetBlockingRects()
    {
        service.SetAreaSelectionBlockingRects(
            Visible ? CommanderWindowScale.Visual(WindowId, windowRect) : default,
            MissionsVisible ? CommanderWindowScale.Visual(MissionWindowId, missionWindowRect) : default);
    }

    private void DrawWindow(int windowId)
    {
        hoverTooltip = string.Empty;
        CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible);
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            if (service.AwaitingAreaSelection)
            {
                service.CancelAreaSelection();
            }

            Hide();
            return;
        }

        bool dropdownOpen = openHardpointGroup >= 0;
        bool oldEnabled = GUI.enabled;
        bool interactive = !dropdownOpen && !service.AwaitingAreaSelection;

        // The section floors can add up to more than a short screen has, so past that the content
        // scrolls rather than pushing LAUNCH off the bottom. The view rect starts at the same y as
        // the content, so every coordinate below stays window-local.
        const float viewTop = 28f;
        bool scrolls = requiredHeight > windowRect.height + 1f;
        float innerWidth = windowRect.width - (scrolls ? 16f : 0f);
        windowScroll = GUI.BeginScrollView(
            new Rect(0f, viewTop, windowRect.width, windowRect.height - viewTop - 4f),
            windowScroll,
            new Rect(0f, viewTop, innerWidth, Mathf.Max(requiredHeight, windowRect.height) - viewTop - 4f));
        float contentWidth = innerWidth - 24f;
        float y = 34f;

        if (helpVisible)
        {
            const string help = "Pick the aircraft, then what it is for, then how it is armed. With no mission picked "
                + "it is STANDBY: it spawns and stays on the deck until you give it an order. AREA works "
                + "the circle you place, and FOCUS says against what (ANY needs no thought). RADAR orbits "
                + "with radar and jammers and fires anti-radiation missiles at enemy radars. AUTOPILOT "
                + "hands the aircraft to the game's own AI. TRANSPORT opens cargo runs and pilot rescue. "
                + "The mission picks the matching preset until you click one yourself; "
                + "PYLONS starts from that preset and sets every pylon by hand. A yellow line above the button warns when the sortie cannot do its job.";
            y += CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, y, contentWidth, 104f),
                help) + 8f;
        }

        // Armed from one set of doors: say which, and give a way back to the whole air force.
        if (service.HangarFilter != null)
        {
            GUI.Box(new Rect(12f, y, contentWidth, 30f), string.Empty, CommanderUiTheme.Panel);
            GUI.Label(
                new Rect(20f, y + 4f, contentWidth - 150f, 22f),
                $"ARMING {service.GetHangarFilterLabel()}",
                CommanderUiTheme.Header);
            GUI.enabled = oldEnabled && interactive;
            if (GUI.Button(new Rect(innerWidth - 142f, y + 3f, 122f, 24f), "ALL AIRBASES", CommanderUiTheme.Button))
            {
                service.SetHangarFilter(null);
            }

            GUI.enabled = oldEnabled;
            y += 36f;
        }

        // The loadout box gets first claim on the flexible space: the PYLONS tab's footer (the
        // description line, the cannon toggle and SAVE AS PRESET) is absolute-positioned under its
        // pylon list, so a box shorter than ~220 px leaves the list no room. The aircraft list
        // absorbs whatever is left, down to its own floor, because it already scrolls.
        bool needsArea = CommanderAirCommandService.RequiresMissionArea(service.SelectedMode);
        float missionBlock = 22f + 40f + (CommanderAirCommandService.IsAreaMode(service.SelectedMode) ? 40f : 0f)
            + (needsArea ? 46f : 0f) + (needsArea && service.SupportsTargetAltitude() ? 46f : 0f)
            + (needsArea && ShowsModeToggle() ? 38f : 0f);
        string warning = service.LaunchWarning;
        float warningHeight = warning.Length > 0
            ? CommanderUiTheme.WarningLabel.CalcHeight(new GUIContent(warning), contentWidth) + 4f
            : 0f;
        float footer = 24f + 42f + 38f + warningHeight;
        float headers = 22f + 8f + 22f + 30f + 8f;

        // All three stretching sections share this. Each bar trades with the loadout box, which
        // takes whatever is left, so a drag moves space around and never pushes LAUNCH off the
        // bottom; only the floors can make the body scroll.
        float shared = windowRect.height - y - missionBlock - 30f - footer - headers - 16f;
        float maxAirbase = Mathf.Max(MinAirbaseHeight, shared - MinAircraftHeight - MinLoadoutHeight);
        float airbaseHeight = CommanderWindowScale.Section(WindowId, AirbaseSlot, MinAirbaseHeight, MinAirbaseHeight, maxAirbase);
        float flexible = shared - airbaseHeight;
        float maxAircraft = Mathf.Max(MinAircraftHeight, flexible - MinLoadoutHeight);

        bool stretched = IsStretched;
        float loadoutShare = Mathf.Clamp(flexible * 0.6f, MinLoadoutHeight, stretched ? MaxSectionHeight : 260f);

        // Floor of three whole 52 px rows: at 76 only two aircraft showed at once. The window grows
        // to fit (requiredHeight), so the floor costs height rather than squeezing the loadout box.
        float aircraftHeight = CommanderWindowScale.Section(
            WindowId,
            AircraftSlot,
            Mathf.Clamp(flexible - loadoutShare, MinAircraftHeight, stretched ? MaxSectionHeight : 240f),
            MinAircraftHeight,
            maxAircraft);
        float loadoutHeight = stretched ? Mathf.Max(MinLoadoutHeight, flexible - aircraftHeight) : loadoutShare;

        GUI.enabled = oldEnabled && interactive;
        y = DrawAircraftSection(y, contentWidth, aircraftHeight);
        CommanderWindowScale.Splitter(WindowId, AircraftSlot, new Rect(12f, y - 8f, contentWidth, 8f), alongX: false, inverted: false, aircraftHeight, MinAircraftHeight, maxAircraft);
        GUI.enabled = oldEnabled;

        y = DrawMissionSection(y, contentWidth, oldEnabled, interactive, needsArea);

        GUI.enabled = oldEnabled && interactive;
        y = DrawLoadoutSection(y, contentWidth, loadoutHeight, out Rect loadoutPanel);
        CommanderWindowScale.Splitter(WindowId, AirbaseSlot, new Rect(12f, y - 8f, contentWidth, 8f), alongX: false, inverted: true, airbaseHeight, MinAirbaseHeight, maxAirbase);
        y = DrawAirbaseSection(y, contentWidth, airbaseHeight);
        GUI.enabled = oldEnabled;

        // The number the player is actually deciding on, right above the button that spends it.
        GUI.Label(new Rect(12f, y, contentWidth, 22f), service.GetLaunchCostLabel(), CommanderUiTheme.Header);
        y += 24f;

        // Loadout versus mission, and whether the depots can pay for the launch. A warning, not a
        // refusal: the player may know better, but should not find out after paying.
        if (warning.Length > 0)
        {
            GUI.Label(new Rect(12f, y, contentWidth, warningHeight), "! " + warning, CommanderUiTheme.WarningLabel);
            y += warningHeight;
        }

        GUI.enabled = oldEnabled && (service.AwaitingAreaSelection || (interactive && service.CanLaunchSelected));
        if (GUI.Button(
            new Rect(12f, y, contentWidth, 38f),
            GetLaunchButtonLabel(),
            service.AwaitingAreaSelection ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            if (service.AwaitingAreaSelection)
            {
                service.CancelAreaSelection();
            }
            else
            {
                service.BeginAreaSelection();
            }
        }

        GUI.enabled = oldEnabled;
        y += 42f;

        string status = service.AwaitingAreaSelection
            ? "Click the tactical map or 3D terrain. The game's Cancel binding cancels."
            : service.StatusText;
        GUI.Label(
            new Rect(12f, y, contentWidth, 34f),
            $"ACTIVE {service.ActiveMissionCount} | {status}",
            CommanderUiTheme.MutedLabel);

        // Everything above is laid out from the top down, so where y ended up is exactly what the
        // content needed. Tick reads it next frame; the clamps on the sections make that settle in
        // one step rather than growing every frame.
        requiredHeight = y + 34f + 12f;

        DrawLoadoutDropdown(loadoutPanel);
        GUI.EndScrollView();
        DrawTooltip();
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 44f, 28f));
    }

    private string GetLaunchButtonLabel()
    {
        if (service.AwaitingAreaSelection)
        {
            return "CANCEL AREA SELECTION";
        }

        return service.SelectedMode switch
        {
            CommanderAirCommandService.AirCommandMode.Autopilot => "BUY AND RELEASE TO AUTOPILOT",
            CommanderAirCommandService.AirCommandMode.Standby => "BUY AND HOLD ON THE DECK",
            _ => "LAUNCH, THEN CLICK ITS AREA ON THE MAP",
        };
    }

    private bool ShowsModeToggle()
    {
        return service.SelectedMode == CommanderAirCommandService.AirCommandMode.AirGuard;
    }

    /// <summary>
    /// Step one. Everything else in the window depends on what is picked here, so the pick is shown
    /// large beside the list as a turning model.
    /// </summary>
    private float DrawAircraftSection(float y, float width, float height)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), "1  AIRCRAFT");
        const float rowHeight = 58f;
        float maxPreview = Mathf.Max(MinPreviewWidth, width - MinAircraftListWidth - 8f);
        float previewWidth = CommanderWindowScale.Section(WindowId, PreviewSlot, DefaultPreviewWidth, MinPreviewWidth, maxPreview);
        Rect view = new(12f, y, width - previewWidth - 8f, height);
        GUI.Box(view, string.Empty, CommanderUiTheme.Inset);
        float innerHeight = Mathf.Max(view.height - 4f, (service.Options.Count * rowHeight) + 4f);
        aircraftScroll = GUI.BeginScrollView(view, aircraftScroll, new Rect(0f, 0f, view.width - 18f, innerHeight));
        for (int i = 0; i < service.Options.Count; i++)
        {
            CommanderAirCommandService.AirMissionOption option = service.Options[i];
            Rect row = new(3f, 3f + (i * rowHeight), view.width - 24f, rowHeight - 4f);
            bool selected = i == service.SelectedOptionIndex;
            if (GUI.Button(row, GUIContent.none, selected ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectOption(i);
                airbaseScroll = Vector2.zero;
                presetScroll = Vector2.zero;
                openHardpointGroup = -1;
            }

            Rect picture = new(row.x + 2f, row.y + 2f, (row.height - 4f) * 1.5f, row.height - 4f);
            CommanderUnitPreview.DrawStill(picture, option.Definition);
            string[] lines = CommanderAirCommandService.GetOptionLabel(option).Split('\n');
            float textX = picture.xMax + 10f;
            GUI.Label(new Rect(textX, row.y + 6f, row.xMax - textX - 8f, 22f), lines[0], CommanderUiTheme.Label);
            if (lines.Length > 1)
            {
                GUI.Label(new Rect(textX, row.y + 27f, row.xMax - textX - 8f, 20f), lines[1], CommanderUiTheme.Caption);
            }
        }

        GUI.EndScrollView();
        if (service.Options.Count == 0)
        {
            GUI.Label(
                new Rect(20f, y + 8f, view.width - 32f, 22f),
                "No aircraft available here.",
                CommanderUiTheme.MutedLabel);
        }

        Rect preview = new(view.xMax + 8f, y, previewWidth, height);
        CommanderWindowScale.Splitter(WindowId, PreviewSlot, new Rect(view.xMax, y, 8f, height), alongX: true, inverted: true, previewWidth, MinPreviewWidth, maxPreview);
        GUI.Box(preview, string.Empty, CommanderUiTheme.Inset);
        if (service.SelectedOptionIndex >= 0 && service.SelectedOptionIndex < service.Options.Count)
        {
            CommanderAirCommandService.AirMissionOption picked = service.Options[service.SelectedOptionIndex];
            CommanderUnitPreview.DrawLive(new Rect(preview.x + 1f, preview.y + 1f, preview.width - 2f, preview.height - 30f), picked.Definition, 2);
            CommanderUiTheme.FitLabel(
                new Rect(preview.x + 10f, preview.yMax - 28f, preview.width - 20f, 22f),
                CommanderAirCommandService.GetOptionLabel(picked).Split('\n')[0],
                CommanderUiTheme.Header);
        }

        return y + height + 8f;
    }

    /// <summary>
    /// Step two: AREA, RADAR, AUTOPILOT, or nothing at all, which is STANDBY. AREA's FOCUS row is
    /// where the old specialist modes went (AIR is air superiority, GROUND is CAS, STRIKE is
    /// buildings and ships); ANY is HYBRID and needs no thought.
    /// </summary>
    private float DrawMissionSection(
        float y, float width, bool oldEnabled, bool interactive, bool needsArea)
    {
        bool standby = service.SelectedMode == CommanderAirCommandService.AirCommandMode.Standby;
        y = CommanderUiTheme.DrawSection(
            new Rect(12f, y, width, 18f),
            standby ? "2  MISSION  |  NONE PICKED: STANDBY, HOLDS ON THE DECK" : "2  MISSION");

        GUI.enabled = oldEnabled && interactive;
        if (CommanderAirCommandService.IsAreaMode(service.SelectedMode))
        {
            lastAreaMode = service.SelectedMode;
        }

        float quarter = (width - 12f) / 4f;
        DrawMissionButton(
            lastAreaMode,
            CommanderAirCommandService.IsAreaMode(service.SelectedMode),
            "AREA",
            12f,
            y,
            quarter,
            "AREA | Flies to the circle you place and stays inside it, attacking what it can hit there. Pick a FOCUS below. Strategic missiles may hit targets outside the circle that are in their range. Click again for STANDBY.");
        DrawMissionButton(
            CommanderAirCommandService.AirCommandMode.AwacsJammer,
            service.SelectedMode == CommanderAirCommandService.AirCommandMode.AwacsJammer,
            "RADAR",
            16f + quarter,
            y,
            quarter,
            "RADAR | Orbits inside the circle with its radar and jammers on, and fires any anti-radiation missiles it carries at enemy radars. With those gone it stays up as a radar picket. Click again for STANDBY.");
        DrawMissionButton(
            CommanderAirCommandService.AirCommandMode.Autopilot,
            service.SelectedMode == CommanderAirCommandService.AirCommandMode.Autopilot,
            "AUTOPILOT",
            20f + (quarter * 2f),
            y,
            quarter,
            "NO AREA | Bought and handed straight to the game's own AI with no restraint at all: it picks its own targets and flies itself, exactly like any other AI aircraft. Click again for STANDBY.");

        // Not a mode: cargo runs and pilot rescue have their own planner, and this is the door to it.
        Rect transportRect = new(24f + (quarter * 3f), y, quarter - 4f, 36f);
        if (GUI.Button(transportRect, "TRANSPORT", CommanderUiTheme.Button))
        {
            OpenTransport?.Invoke(service.SelectedOption?.Definition);
        }

        if (transportRect.Contains(Event.current.mousePosition))
        {
            hoverTooltip = "CARGO, TROOPS AND RESCUE | Opens the TRANSPORT window, with this aircraft picked if it can carry cargo. Pilot rescue helicopters are launched from there too.";
        }

        y += 40f;

        if (CommanderAirCommandService.IsAreaMode(service.SelectedMode))
        {
            y = DrawFocusRow(y, width);
        }

        GUI.enabled = oldEnabled;

        if (!needsArea)
        {
            return y;
        }

        // A slider rather than a list of six preset heights: "fly higher than that" is a thing a
        // player wants to nudge, and the same control is repeated per aircraft in AIR MISSIONS so a
        // package already up can be raised without relaunching it.
        float sliderWidth = Mathf.Max(60f, width - 320f);
        if (service.SupportsTargetAltitude())
        {
            Rect altitudePanel = new(12f, y, width, 38f);
            GUI.Box(altitudePanel, string.Empty, CommanderUiTheme.Panel);
            GUI.Label(new Rect(altitudePanel.x + 12f, altitudePanel.y + 8f, 180f, 24f), "TARGET ALTITUDE", CommanderUiTheme.Header);
            GUI.enabled = oldEnabled && interactive;
            float altitudeDelta = AltitudeSlider(new Rect(altitudePanel.x + 200f, altitudePanel.y + 14f, sliderWidth, 12f), service.SelectedTargetAltitude);
            if (altitudeDelta != 0f)
            {
                service.StepTargetAltitude(altitudeDelta);
            }

            // STANDARD is wider than 100 px at the header size, so it wrapped and the row showed
            // half of each line.
            CommanderUiTheme.FitLabel(
                new Rect(altitudePanel.x + 210f + sliderWidth, altitudePanel.y + 8f, 100f, 24f),
                CommanderAirCommandService.FormatTargetAltitude(service.SelectedTargetAltitude),
                CommanderUiTheme.Header);
            GUI.enabled = oldEnabled;
            y += 46f;
        }

        Rect radiusPanel = new(12f, y, width, 38f);
        GUI.Box(radiusPanel, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(radiusPanel.x + 12f, radiusPanel.y + 8f, 180f, 24f), "MISSION RADIUS", CommanderUiTheme.Header);
        GUI.enabled = oldEnabled && interactive;
        float radiusDelta = RadiusSlider(new Rect(radiusPanel.x + 200f, radiusPanel.y + 14f, sliderWidth, 12f), service.SelectedMissionRadiusKm);
        if (radiusDelta != 0f)
        {
            service.StepMissionRadius(radiusDelta);
        }

        CommanderUiTheme.FitLabel(new Rect(radiusPanel.x + 210f + sliderWidth, radiusPanel.y + 8f, 100f, 24f), $"{service.SelectedMissionRadiusKm:0} km", CommanderUiTheme.Header);
        GUI.enabled = oldEnabled;
        y += 46f;

        if (service.SelectedMode == CommanderAirCommandService.AirCommandMode.AirGuard)
        {
            GUI.enabled = oldEnabled && interactive;
            CommanderAirCommandService.TargetOrdnance = CommanderUiTheme.Check(
                new Rect(12f, y, width, 32f),
                CommanderAirCommandService.TargetOrdnance,
                "TARGET ORDNANCE | include hostile missiles",
                CommanderUiTheme.Toggle);
            GUI.enabled = oldEnabled;
            y += 38f;
        }

        return y;
    }

    /// <summary>
    /// Step three, in two tabs. PRESETS is the default and is one click: role sets the mod builds
    /// from the mounts this airframe actually carries, the game's own standard loadouts, and
    /// anything saved. PYLONS opens on the chosen preset and sets every set of hardpoints by hand.
    /// </summary>
    private float DrawLoadoutSection(float y, float width, float height, out Rect loadoutPanel)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), "3  LOADOUT");

        float half = (width - 6f) / 2f;
        bool presets = !service.AdvancedLoadout;
        if (GUI.Button(
            new Rect(12f, y, half, 26f),
            new GUIContent($"PRESETS ({service.Presets.Count})", "One click. The mission picks the best one until you choose."),
            presets ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SetAdvancedLoadout(false);
            presetScroll = Vector2.zero;
        }

        if (GUI.Button(
            new Rect(18f + half, y, half, 26f),
            new GUIContent("PYLONS", "Every set of hardpoints by hand, starting from the chosen preset."),
            !presets ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SetAdvancedLoadout(true);
            pylonScroll = Vector2.zero;
        }

        y += 30f;

        // Another aircraft or the PYLONS tab takes the row away, and the keyboard must come back.
        if (renamingPreset != null && (service.AdvancedLoadout || !service.HasPreset(renamingPreset)))
        {
            EndRename();
        }

        loadoutPanel = new Rect(12f, y, width, height);
        GUI.Box(loadoutPanel, string.Empty, CommanderUiTheme.Panel);
        if (presets)
        {
            DrawPresetList(loadoutPanel);
        }
        else
        {
            DrawPylonLoadout(loadoutPanel);
        }

        return y + height + 8f;
    }

    /// <summary>Step four. One row when the window was opened from a specific hangar.</summary>
    private float DrawAirbaseSection(float y, float width, float height)
    {
        y = CommanderUiTheme.DrawSection(
            new Rect(12f, y, width, 18f),
            service.HangarFilter != null ? "4  DEPARTURE HANGAR" : "4  DEPARTURE AIRBASE");
        Rect view = new(12f, y, width, height);
        float innerHeight = Mathf.Max(view.height, (service.Airbases.Count * 40f) + 4f);
        airbaseScroll = GUI.BeginScrollView(view, airbaseScroll, new Rect(0f, 0f, view.width - 18f, innerHeight));
        for (int i = 0; i < service.Airbases.Count; i++)
        {
            CommanderAirCommandService.AirbaseOption airbase = service.Airbases[i];
            if (GUI.Button(
                new Rect(2f, 2f + (i * 40f), view.width - 24f, 34f),
                CommanderAirCommandService.GetAirbaseLabel(airbase),
                i == service.SelectedAirbaseIndex ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectAirbase(i);
            }
        }

        GUI.EndScrollView();
        return y + height + 8f;
    }

    /// <summary>The mode buttons' own hover text first, then any button's <c>GUIContent</c> tooltip.</summary>
    private void DrawTooltip()
    {
        CommanderUiTheme.DrawTooltip(
            new Rect(0f, 0f, windowRect.width, windowRect.height),
            hoverTooltip.Length > 0 ? hoverTooltip : null);
    }

    /// <summary>What AREA, RADAR and AUTOPILOT do on a click: pick it, or, already picked, go back
    /// to STANDBY, which has no button because it is what "nothing picked" means.</summary>
    private void DrawMissionButton(
        CommanderAirCommandService.AirCommandMode mode,
        bool selected,
        string label,
        float x,
        float y,
        float width,
        string description)
    {
        Rect rect = new(x, y, width - 4f, 36f);
        if (GUI.Button(rect, label, selected ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SelectMode(selected ? CommanderAirCommandService.AirCommandMode.Standby : mode);
            openHardpointGroup = -1;
        }

        if (rect.Contains(Event.current.mousePosition))
        {
            hoverTooltip = description;
        }
    }

    /// <summary>AREA's second row: what in the circle it goes after.</summary>
    private float DrawFocusRow(float y, float width)
    {
        float fifth = (width - 16f) / 5f;
        GUI.Label(new Rect(12f, y + 8f, fifth, 22f), "FOCUS", CommanderUiTheme.Header);
        DrawFocusButton(
            CommanderAirCommandService.AirCommandMode.Hybrid,
            "ANY",
            16f + fifth,
            y,
            fifth,
            "ANY | Attacks anything it can hit in the circle, aircraft, vehicles, ships and buildings, with whatever it is carrying.");
        DrawFocusButton(
            CommanderAirCommandService.AirCommandMode.AirGuard,
            "AIR",
            20f + (fifth * 2f),
            y,
            fifth,
            "AIR | Holds inside the circle and attacks aircraft, including ones just outside it that are in weapon range.");
        DrawFocusButton(
            CommanderAirCommandService.AirCommandMode.Cas,
            "GROUND",
            24f + (fifth * 3f),
            y,
            fifth,
            "GROUND | Attacks vehicles, ships and buildings in the circle with conventional weapons.");
        DrawFocusButton(
            CommanderAirCommandService.AirCommandMode.StrategicStrike,
            "STRIKE",
            28f + (fifth * 4f),
            y,
            fifth,
            "STRIKE | Attacks buildings and ships only, with any surface weapon aboard, cruise and ballistic missiles included.");
        return y + 40f;
    }

    private void DrawFocusButton(
        CommanderAirCommandService.AirCommandMode mode,
        string label,
        float x,
        float y,
        float width,
        string description)
    {
        Rect rect = new(x, y, width - 4f, 36f);
        if (GUI.Button(rect, label, service.SelectedMode == mode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SelectMode(mode);
            openHardpointGroup = -1;
        }

        if (rect.Contains(Event.current.mousePosition))
        {
            hoverTooltip = description;
        }
    }
}
