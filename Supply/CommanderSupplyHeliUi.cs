using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The TRANSPORT window: one page, read top to bottom in the same order as AIR COMMAND — the
/// aircraft, what it carries, how it delivers, where it leaves from, then the button. It was a
/// three-page wizard (aircraft, loadout, deployment), and every change of mind meant paging back.
/// </summary>
internal sealed class CommanderSupplyHeliUi
{
    internal const int WindowId = 0x434F4D48;
    private const float BayHeight = 46f;
    private const string HelpText = "Pick the aircraft, fill its bays, choose how it delivers and where it leaves from, then press "
        + "LAUNCH and click the point in the 3D world. A combined bay clears the front and rear bays it "
        + "overlaps. Only cargo marked [Airdrop] has a parachute, and an aeroplane can only airdrop. READY fields have a "
        + "free hangar; WAIT queues the run. Hold Shift on the click to send the same run again. RESCUE TEAM and "
        + "RESCUE ENEMY launch a sling hook helicopter that picks up downed pilots instead; turn both on for anyone's.";

    private readonly CommanderSupplyHeliService service;

    /// <summary>Per airframe, whether a pylon offers the sling hook: the rescue list is drawn every
    /// frame and the answer walks the prefab's hardpoints.</summary>
    private readonly Dictionary<AircraftDefinition, bool> carriesHook = new();
    private Rect windowRect = new(0f, 0f, 760f, 760f);
    private Vector2 aircraftScroll;
    private Vector2 airbaseScroll;
    private Vector2 dropdownScroll;
    private Vector2 windowScroll;
    private bool positionInitialized;
    private bool helpVisible;
    private int openCargoSlot = -1;
    private bool rescueTeam;
    private bool rescueEnemy;

    /// <summary>Where the content ended last frame; the window grows to it, as AIR COMMAND does.</summary>
    private float requiredHeight = 760f;

    internal CommanderSupplyHeliUi(CommanderSupplyHeliService service)
    {
        this.service = service;
    }

    internal bool Visible { get; private set; }

    internal bool ShowLz { get; private set; } = true;

    /// <summary>The title bar's way back to AIR COMMAND, which is where this window is opened from.</summary>
    internal System.Action? OpenAirCommand { get; set; }

    /// <summary>RESCUE TEAM and RESCUE ENEMY are toggles; both on is BOTH, neither is a cargo run.</summary>
    private bool RescueMode => rescueTeam || rescueEnemy;

    private CommanderRecoveryService.RecoveryMode RescueRecovery => rescueTeam && rescueEnemy
        ? CommanderRecoveryService.RecoveryMode.Both
        : rescueTeam ? CommanderRecoveryService.RecoveryMode.Team : CommanderRecoveryService.RecoveryMode.Enemy;

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    /// <summary>Opens with this airframe picked when it carries cargo, the first one otherwise.</summary>
    internal void ShowFor(AircraftDefinition? definition)
    {
        Visible = true;
        service.SetUiVisible(true);
        service.RefreshOptions();
        helpVisible = false;
        openCargoSlot = -1;
        for (int i = 0; i < service.AircraftOptions.Count; i++)
        {
            if (ReferenceEquals(service.AircraftOptions[i].Definition, definition))
            {
                service.SelectAircraft(i);
                break;
            }
        }

        SelectFirstAirbaseIfNone();
    }

    internal void Hide()
    {
        Visible = false;
        service.SetUiVisible(false);
        openCargoSlot = -1;
    }

    internal void Tick()
    {
        Vector2 size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(760f, CommanderUiScale.Width - 32f), Mathf.Min(requiredHeight, (CommanderUiScale.Height - 60f) / CommanderWindowScale.Get(WindowId))),
            new Vector2(480f, 300f));
        float width = size.x;
        float height = size.y;
        if (!positionInitialized)
        {
            windowRect = new Rect(12f, 52f, width, height);
            positionInitialized = true;
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
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return Visible && CommanderWindowScale.Visual(WindowId, windowRect).Contains(guiPoint);
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "TRANSPORT");
    }

    private static string GetCostLabel(CommanderSupplyHeliService.CargoAircraftOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        return supply > 0
            ? $"FROM STOCK  |  {supply} {option.Label} IN STOCK"
            : $"COST  {CommanderEconomyService.FundsLabel(CommanderEconomyService.PriceOf(option.Definition))}";
    }

    private static string GetAircraftCaption(CommanderSupplyHeliService.CargoAircraftOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = supply > 0 ? $"IN STOCK x{supply}" : CommanderEconomyService.FundsLabel(CommanderEconomyService.PriceOf(option.Definition));
        string kind = option.Rotary ? "HELICOPTER" : "AIRDROP ONLY";
        int bays = option.CargoSlots.Count;
        return $"{kind}  |  {bays} {(bays == 1 ? "BAY" : "BAYS")}  |  {cost}";
    }

    private static string GetAirbaseLabel(CommanderSupplyHeliService.AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{(option.Ready ? "READY" : "WAIT")}  |  {option.Label}  |  {distance}";
    }

    private void SelectFirstAirbaseIfNone()
    {
        if (service.AirbaseOptions.Count == 0)
        {
            return;
        }

        for (int i = 0; i < service.AirbaseOptions.Count; i++)
        {
            if (ReferenceEquals(service.AirbaseOptions[i].Airbase, service.SelectedAirbase))
            {
                return;
            }
        }

        service.SelectAirbase(0);
    }

    private void DrawWindow(int windowId)
    {
        CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible);
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            Hide();
            return;
        }

        if (OpenAirCommand != null
            && GUI.Button(new Rect(windowRect.width - 228f, 3f, 104f, 22f), new GUIContent("< AIR COMMAND", "Back to buying combat aircraft."), CommanderUiTheme.ChromeButton))
        {
            OpenAirCommand();
            return;
        }

        CommanderSupplyHeliService.CargoAircraftOption? aircraft = service.SelectedAircraft;
        bool cargoPopupOpen = aircraft != null && openCargoSlot >= 0 && openCargoSlot < aircraft.CargoSlots.Count;
        bool oldEnabled = GUI.enabled;
        bool interactive = !cargoPopupOpen && !service.AwaitingTargetSelection;

        const float viewTop = 28f;
        bool scrolls = requiredHeight > windowRect.height + 1f;
        float innerWidth = windowRect.width - (scrolls ? 16f : 0f);
        windowScroll = GUI.BeginScrollView(
            new Rect(0f, viewTop, windowRect.width, windowRect.height - viewTop - 4f),
            windowScroll,
            new Rect(0f, viewTop, innerWidth, Mathf.Max(requiredHeight, windowRect.height) - viewTop - 4f));
        float width = innerWidth - 24f;
        float y = 34f;

        if (helpVisible)
        {
            y += CommanderUiTheme.DrawHelpOverlay(new Rect(12f, y, width, 104f), HelpText) + 8f;
        }

        GUI.enabled = oldEnabled && interactive;
        y = DrawModeRow(y, width);
        aircraft = service.SelectedAircraft;
        y = DrawAircraftSection(y, width);
        Rect cargoArea = default;
        if (aircraft != null)
        {
            if (!RescueMode)
            {
                y = DrawCargoSection(y, width, aircraft, out cargoArea);
                y = DrawDeliverySection(y, width, aircraft);
            }

            y = DrawAirbaseSection(y, width);
        }

        GUI.enabled = oldEnabled;
        y = RescueMode
            ? DrawRescueFooter(y, width, aircraft, oldEnabled)
            : DrawFooter(y, width, aircraft, oldEnabled, cargoPopupOpen);
        requiredHeight = y + 12f;

        if (aircraft != null && !RescueMode)
        {
            DrawCargoDropdown(aircraft, cargoArea);
        }

        GUI.EndScrollView();
        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, windowRect.width, windowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 236f, 28f));
    }

    /// <summary>
    /// The job: a cargo run, or a rescue helicopter on standing pilot-recovery duty. The two RESCUE
    /// buttons are toggles, so both on picks up anyone's pilots.
    /// </summary>
    private float DrawModeRow(float y, float width)
    {
        float third = (width - 12f) / 3f;
        if (GUI.Button(
            new Rect(12f, y, third, 36f),
            new GUIContent("TRANSPORT", "Cargo and troops, delivered to a point you click."),
            RescueMode ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            rescueTeam = false;
            rescueEnemy = false;
        }

        bool wasRescue = RescueMode;
        if (GUI.Button(
            new Rect(18f + third, y, third, 36f),
            new GUIContent("RESCUE TEAM", "A sling hook helicopter that picks up your own downed pilots. Each one brought back to a base you hold pays 10 M. Turn RESCUE ENEMY on as well to pick up anyone."),
            rescueTeam ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            rescueTeam = !rescueTeam;
        }

        if (GUI.Button(
            new Rect(24f + (third * 2f), y, third, 36f),
            new GUIContent("RESCUE ENEMY", "A sling hook helicopter that picks up enemy pilots. Each one brought back reveals every enemy unit within 15 km of where he was found. Turn RESCUE TEAM on as well to pick up anyone."),
            rescueEnemy ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            rescueEnemy = !rescueEnemy;
        }

        if (RescueMode && !wasRescue)
        {
            SelectFirstHookAircraftIfNone();
            openCargoSlot = -1;
        }

        return y + 44f;
    }

    private bool CarriesHook(AircraftDefinition definition)
    {
        if (!carriesHook.TryGetValue(definition, out bool hook))
        {
            hook = CommanderAirCommandService.BuildSlingLoadout(definition) != null;
            carriesHook[definition] = hook;
        }

        return hook;
    }

    private bool IsListed(CommanderSupplyHeliService.CargoAircraftOption option)
    {
        return !RescueMode || CarriesHook(option.Definition);
    }

    private void SelectFirstHookAircraftIfNone()
    {
        if (service.SelectedAircraft != null && CarriesHook(service.SelectedAircraft.Definition))
        {
            return;
        }

        for (int i = 0; i < service.AircraftOptions.Count; i++)
        {
            if (CarriesHook(service.AircraftOptions[i].Definition))
            {
                service.SelectAircraft(i);
                SelectFirstAirbaseIfNone();
                airbaseScroll = Vector2.zero;
                return;
            }
        }
    }

    /// <summary>Step one: the list beside a turning model of the pick, as in AIR COMMAND. On rescue it
    /// lists only the airframes that can carry the sling hook.</summary>
    private float DrawAircraftSection(float y, float width)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), RescueMode ? "1  RESCUE HELICOPTER" : "1  TRANSPORT AIRCRAFT");
        IReadOnlyList<CommanderSupplyHeliService.CargoAircraftOption> aircraft = service.AircraftOptions;
        int listed = 0;
        for (int i = 0; i < aircraft.Count; i++)
        {
            listed += IsListed(aircraft[i]) ? 1 : 0;
        }

        const float rowHeight = 58f;
        const float previewWidth = 230f;
        float height = Mathf.Clamp((listed * rowHeight) + 4f, (3 * rowHeight) + 4f, 240f);
        Rect view = new(12f, y, width - previewWidth - 8f, height);
        GUI.Box(view, string.Empty, CommanderUiTheme.Inset);
        float innerHeight = Mathf.Max(view.height - 4f, (listed * rowHeight) + 4f);
        aircraftScroll = GUI.BeginScrollView(view, aircraftScroll, new Rect(0f, 0f, view.width - 18f, innerHeight));
        int rowIndex = 0;
        for (int i = 0; i < aircraft.Count; i++)
        {
            CommanderSupplyHeliService.CargoAircraftOption option = aircraft[i];
            if (!IsListed(option))
            {
                continue;
            }

            Rect row = new(3f, 3f + (rowIndex++ * rowHeight), view.width - 24f, rowHeight - 4f);
            bool selected = i == service.SelectedAircraftIndex;
            if (GUI.Button(row, GUIContent.none, selected ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectAircraft(i);
                SelectFirstAirbaseIfNone();
                airbaseScroll = Vector2.zero;
                openCargoSlot = -1;
            }

            Rect picture = new(row.x + 2f, row.y + 2f, (row.height - 4f) * 1.5f, row.height - 4f);
            CommanderUnitPreview.DrawStill(picture, option.Definition);
            float textX = picture.xMax + 10f;
            CommanderUiTheme.FitLabel(new Rect(textX, row.y + 6f, row.xMax - textX - 8f, 22f), option.Label, CommanderUiTheme.Label);
            GUI.Label(new Rect(textX, row.y + 27f, row.xMax - textX - 8f, 20f), GetAircraftCaption(option), CommanderUiTheme.Caption);
        }

        GUI.EndScrollView();
        if (listed == 0)
        {
            GUI.Label(
                new Rect(20f, y + 8f, view.width - 32f, 22f),
                RescueMode ? "No helicopter that can carry the sling hook." : "No aircraft that can carry cargo.",
                CommanderUiTheme.MutedLabel);
        }

        Rect preview = new(view.xMax + 8f, y, previewWidth, height);
        GUI.Box(preview, string.Empty, CommanderUiTheme.Inset);
        CommanderSupplyHeliService.CargoAircraftOption? picked = service.SelectedAircraft;
        if (picked != null && IsListed(picked))
        {
            // Slot 2 is AIR COMMAND's, and the two windows are never open together.
            CommanderUnitPreview.DrawLive(new Rect(preview.x + 1f, preview.y + 1f, preview.width - 2f, preview.height - 30f), picked.Definition, 2);
            CommanderUiTheme.FitLabel(new Rect(preview.x + 10f, preview.yMax - 28f, preview.width - 20f, 22f), picked.Label, CommanderUiTheme.Header);
        }

        return y + height + 8f;
    }

    /// <summary>Step two: every bay is on the page at once, each opening its own list of loads.</summary>
    private float DrawCargoSection(float y, float width, CommanderSupplyHeliService.CargoAircraftOption aircraft, out Rect cargoArea)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width - 96f, 18f), "2  CARGO");
        if (GUI.Button(new Rect(width - 84f, y - 24f, 96f, 22f), new GUIContent("EMPTY ALL", "Clear every bay."), CommanderUiTheme.Button))
        {
            service.ClearSelectedCargo();
            openCargoSlot = -1;
        }

        float top = y;
        float half = (width - 6f) * 0.5f;
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            if (aircraft.CargoSlots[i].IsCombinedBay)
            {
                DrawCargoButton(new Rect(12f, y, width, BayHeight), i, aircraft.CargoSlots[i]);
                y += BayHeight + 6f;
            }
        }

        int column = 0;
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            if (aircraft.CargoSlots[i].IsCombinedBay)
            {
                continue;
            }

            DrawCargoButton(new Rect(column == 0 ? 12f : 18f + half, y, half, BayHeight), i, aircraft.CargoSlots[i]);
            if (++column == 2)
            {
                column = 0;
                y += BayHeight + 6f;
            }
        }

        if (column != 0)
        {
            y += BayHeight + 6f;
        }

        cargoArea = new Rect(12f, top, width, Mathf.Max(y - top, BayHeight));

        // The defensive stores ride on whatever hardpoints the cargo leaves free.
        float third = (width - 12f) / 3f;
        service.IncludeEcm = CommanderUiTheme.Check(
            new Rect(12f, y, third, 28f),
            service.IncludeEcm,
            new GUIContent("ECM", "Fit a jammer on a free hardpoint if this airframe has one."),
            CommanderUiTheme.Toggle);
        service.IncludeCountermeasures = CommanderUiTheme.Check(
            new Rect(18f + third, y, third, 28f),
            service.IncludeCountermeasures,
            new GUIContent("COUNTERMEASURES", "Flares and chaff."),
            CommanderUiTheme.Toggle);
        service.FillRemainingHardpoints = CommanderUiTheme.Check(
            new Rect(24f + (third * 2f), y, third, 28f),
            service.FillRemainingHardpoints,
            new GUIContent("FILL THE REST", "Arm every hardpoint the cargo leaves free with the game's own loadout for this airframe."),
            CommanderUiTheme.Toggle);
        return y + 36f;
    }

    /// <summary>Step three: land or drop, and how high it flies there.</summary>
    private float DrawDeliverySection(float y, float width, CommanderSupplyHeliService.CargoAircraftOption aircraft)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), "3  DELIVERY");
        float half = (width - 6f) * 0.5f;
        bool enabled = GUI.enabled;
        string landTip = aircraft.Rotary
            ? "Puts down on level ground near the point and unloads there."
            : "Only a helicopter can land at a zone; an aeroplane drops its load.";
        GUI.enabled = enabled && aircraft.Rotary;
        if (GUI.Button(
            new Rect(12f, y, half, 36f),
            new GUIContent("LAND AND UNLOAD", landTip),
            !service.AirdropDelivery ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.AirdropDelivery = false;
        }

        GUI.enabled = enabled && service.SelectedCargoSupportsAirdrop;
        if (GUI.Button(
            new Rect(18f + half, y, half, 36f),
            new GUIContent("AIRDROP", "Drops the load by parachute as it passes over. Only cargo marked [Airdrop] has one."),
            service.AirdropDelivery ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.AirdropDelivery = true;
        }

        GUI.enabled = enabled;
        y += 44f;

        // A slider, as AIR COMMAND's TARGET ALTITUDE: STANDARD (0) leaves the height to the game.
        Rect panel = new(12f, y, width, 38f);
        GUI.Box(panel, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 8f, 180f, 24f), new GUIContent("TERRAIN CLEARANCE", "The lowest it flies over the ground on the way."), CommanderUiTheme.Header);
        float sliderWidth = Mathf.Max(60f, width - 480f);
        float step = CommanderSupplyHeliService.TerrainClearanceStepMeters;
        service.TerrainClearanceMeters = Mathf.Round(
            GUI.HorizontalSlider(
                new Rect(panel.x + 200f, panel.y + 14f, sliderWidth, 12f),
                service.TerrainClearanceMeters,
                0f,
                CommanderSupplyHeliService.MaxTerrainClearanceMeters) / step) * step;
        CommanderUiTheme.FitLabel(
            new Rect(panel.x + 210f + sliderWidth, panel.y + 8f, 100f, 24f),
            service.TerrainClearanceMeters <= 0f ? "STANDARD" : $"{service.TerrainClearanceMeters:0} m",
            CommanderUiTheme.Header);
        ShowLz = CommanderUiTheme.Check(
            new Rect(panel.xMax - 160f, panel.y + 5f, 150f, 28f),
            ShowLz,
            new GUIContent("SHOW LZ", "Marks every active drop zone in the 3D view."),
            CommanderUiTheme.Toggle);
        y += 46f;

        // Only the helicopter autopilot is road-routed; a plane flies its Air Command route.
        if (!aircraft.Rotary)
        {
            return y;
        }

        service.FollowRoads = CommanderUiTheme.Check(
            new Rect(12f, y, width, 28f),
            service.FollowRoads,
            new GUIContent("Follow roads on long legs", "Off: a helicopter flies the straight line to the LZ, over whatever is in the way."),
            CommanderUiTheme.Toggle);
        return y + 36f;
    }

    /// <summary>Step four. A WAIT field queues the run until a hangar there is free.</summary>
    private float DrawAirbaseSection(float y, float width)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), "4  DEPARTURE AIRBASE");
        IReadOnlyList<CommanderSupplyHeliService.AirbaseOption> airbases = service.AirbaseOptions;
        Rect view = new(12f, y, width, Mathf.Clamp((airbases.Count * 40f) + 4f, 44f, 124f));
        float innerHeight = Mathf.Max(view.height, (airbases.Count * 40f) + 4f);
        airbaseScroll = GUI.BeginScrollView(view, airbaseScroll, new Rect(0f, 0f, view.width - 18f, innerHeight));
        for (int i = 0; i < airbases.Count; i++)
        {
            CommanderSupplyHeliService.AirbaseOption option = airbases[i];
            if (GUI.Button(
                new Rect(2f, 2f + (i * 40f), view.width - 24f, 34f),
                GetAirbaseLabel(option),
                ReferenceEquals(option.Airbase, service.SelectedAirbase) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectAirbase(i);
            }
        }

        GUI.EndScrollView();
        if (airbases.Count == 0)
        {
            GUI.Label(new Rect(20f, y + 8f, width - 16f, 22f), "No friendly airfield can launch this aircraft.", CommanderUiTheme.MutedLabel);
        }

        y += view.height + 4f;
        service.UseOtherAirfields = CommanderUiTheme.Check(
            new Rect(12f, y, width, 28f),
            service.UseOtherAirfields,
            new GUIContent("Use another friendly airfield if this one is busy", "Otherwise the run waits for a free hangar here."),
            CommanderUiTheme.Toggle);
        return y + 36f;
    }

    /// <summary>The rescue launch: no point to click, it goes straight onto pilot-recovery duty. Air
    /// Command flies it, because rescue is an Air Command STANDBY sortie with the hook aboard.</summary>
    private float DrawRescueFooter(float y, float width, CommanderSupplyHeliService.CargoAircraftOption? aircraft, bool oldEnabled)
    {
        bool hook = aircraft != null && CarriesHook(aircraft.Definition);
        if (hook)
        {
            GUI.Label(new Rect(12f, y, width, 22f), GetCostLabel(aircraft!), CommanderUiTheme.Header);
            y += 24f;
        }

        CommanderAirCommandService? air = CommanderAirCommandService.Instance;
        string warning = !hook ? "No helicopter that can carry the sling hook."
            : service.SelectedAirbase == null ? "No departure airbase."
            : air == null ? "Air Command is not running on this mission."
            : string.Empty;
        if (warning.Length > 0)
        {
            GUI.Label(new Rect(12f, y, width, 22f), "! " + warning, CommanderUiTheme.WarningLabel);
            y += 24f;
        }

        string who = RescueRecovery switch
        {
            CommanderRecoveryService.RecoveryMode.Both => "ANY PILOT",
            CommanderRecoveryService.RecoveryMode.Team => "YOUR PILOTS",
            _ => "ENEMY PILOTS",
        };
        GUI.enabled = oldEnabled && warning.Length == 0;
        if (GUI.Button(new Rect(12f, y, width, 38f), $"LAUNCH RESCUE HELICOPTER: {who}", CommanderUiTheme.PrimaryButton))
        {
            air!.LaunchRescue(aircraft!.Definition, service.SelectedAirbase!, RescueRecovery);
        }

        GUI.enabled = oldEnabled;
        y += 42f;
        GUI.Label(
            new Rect(12f, y, width, 34f),
            air != null && air.StatusText.Length > 0 ? air.StatusText : "It flies to each downed pilot, winches him aboard and brings him to your nearest base.",
            CommanderUiTheme.MutedLabel);
        return y + 34f;
    }

    private float DrawFooter(float y, float width, CommanderSupplyHeliService.CargoAircraftOption? aircraft, bool oldEnabled, bool cargoPopupOpen)
    {
        if (aircraft != null)
        {
            float cargo = CommanderSupplyHeliService.GetSelectedCargoCost(aircraft);
            CommanderUiTheme.FitLabel(
                new Rect(12f, y, width, 22f),
                cargo > 0f ? $"{GetCostLabel(aircraft)}  |  CARGO  {CommanderEconomyService.FundsLabel(cargo)}" : GetCostLabel(aircraft),
                CommanderUiTheme.Header);
            y += 24f;
        }

        string warning = GetWarning(aircraft);
        if (warning.Length > 0)
        {
            GUI.Label(new Rect(12f, y, width, 22f), "! " + warning, CommanderUiTheme.WarningLabel);
            y += 24f;
        }

        bool awaiting = service.AwaitingTargetSelection;
        GUI.enabled = oldEnabled && !cargoPopupOpen && (awaiting || warning.Length == 0);
        string label = awaiting
            ? "CANCEL TARGET SELECTION"
            : service.AirdropDelivery ? "LAUNCH, THEN CLICK THE DROP POINT" : "LAUNCH, THEN CLICK THE LANDING ZONE";
        if (GUI.Button(new Rect(12f, y, width, 38f), label, awaiting ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            if (awaiting)
            {
                service.CancelTargetSelection();
            }
            else
            {
                service.BeginSelectedCargoRun();
            }
        }

        GUI.enabled = oldEnabled;
        y += 42f;
        GUI.Label(
            new Rect(12f, y, width, 34f),
            $"QUEUED {service.QueuedSpawnCount} | {(service.StatusText.Length > 0 ? service.StatusText : "Shift+click the point to send another run like it.")}",
            CommanderUiTheme.MutedLabel);
        return y + 34f;
    }

    private string GetWarning(CommanderSupplyHeliService.CargoAircraftOption? aircraft)
    {
        if (aircraft == null)
        {
            return "No transport aircraft available.";
        }

        if (!service.HasSelectedCargo)
        {
            return "Pick a load for at least one bay.";
        }

        if (service.SelectedAirbase == null)
        {
            return "No departure airbase.";
        }

        return !aircraft.Rotary && !service.SelectedCargoSupportsAirdrop
            ? "An aeroplane can only airdrop, and this load has no parachute."
            : string.Empty;
    }

    private void DrawCargoButton(Rect rect, int slotIndex, CommanderSupplyHeliService.CargoSlotOption slot)
    {
        bool loaded = slot.SelectedMount != null;
        if (GUI.Button(rect, GUIContent.none, loaded ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            openCargoSlot = openCargoSlot == slotIndex ? -1 : slotIndex;
            dropdownScroll = Vector2.zero;
        }

        GUI.Label(new Rect(rect.x + 10f, rect.y + 4f, rect.width - 40f, 16f), slot.Label.ToUpperInvariant(), CommanderUiTheme.Caption);
        CommanderUiTheme.FitLabel(
            new Rect(rect.x + 10f, rect.y + 20f, rect.width - 40f, 22f),
            loaded ? CommanderSupplyHeliService.GetCargoMountLabel(slot.SelectedMount!) : "Empty",
            loaded ? CommanderUiTheme.Label : CommanderUiTheme.MutedLabel);
        GUI.Label(new Rect(rect.xMax - 26f, rect.y + 13f, 20f, 20f), "v", CommanderUiTheme.MutedLabel);
    }

    /// <summary>Drawn last so it sits over the page; everything under it is disabled while open.</summary>
    private void DrawCargoDropdown(CommanderSupplyHeliService.CargoAircraftOption aircraft, Rect cargoArea)
    {
        if (openCargoSlot < 0 || openCargoSlot >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CommanderSupplyHeliService.CargoSlotOption slot = aircraft.CargoSlots[openCargoSlot];
        float height = Mathf.Min(48f + ((slot.Mounts.Count + 1) * 36f), 330f);
        Rect popup = new(cargoArea.x + 28f, cargoArea.y + 8f, cargoArea.width - 56f, height);
        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        GUI.Label(new Rect(popup.x + 12f, popup.y + 8f, popup.width - 52f, 24f), slot.Label.ToUpperInvariant(), CommanderUiTheme.Header);
        if (GUI.Button(new Rect(popup.xMax - 34f, popup.y + 7f, 24f, 24f), "X", CommanderUiTheme.Button))
        {
            openCargoSlot = -1;
            return;
        }

        Rect view = new(popup.x + 10f, popup.y + 38f, popup.width - 20f, popup.height - 48f);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, ((slot.Mounts.Count + 1) * 36f) + 4f));
        dropdownScroll = GUI.BeginScrollView(view, dropdownScroll, inner);
        if (GUI.Button(new Rect(4f, 2f, inner.width - 8f, 32f), "EMPTY", slot.SelectedMount == null ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SelectCargoMount(openCargoSlot, -1);
            openCargoSlot = -1;
        }

        for (int i = 0; i < slot.Mounts.Count; i++)
        {
            if (GUI.Button(
                new Rect(4f, 38f + (i * 36f), inner.width - 8f, 32f),
                CommanderSupplyHeliService.GetCargoMountLabel(slot.Mounts[i]),
                ReferenceEquals(slot.Mounts[i], slot.SelectedMount) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectCargoMount(openCargoSlot, i);
                openCargoSlot = -1;
            }
        }

        GUI.EndScrollView();
    }
}
