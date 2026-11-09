using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderResetSession
{
    private const int WindowId = 0x434F4D4D;
    private const int PinnedWindowId = 0x434F4D50;

    // The UNIT LIST (pins) window and its PINS launcher are hidden; the code
    // is kept. Set to true to bring them back.
    private const bool PinnedListEnabled = false;

    // Window ids must be unique across the mod: IMGUI draws only one window per id, so a clash
    // reads as one window closing the other (BASE SUPPLY shared BUILD's, UNIT SYSTEMS the depot's).
    private const int RadarWindowId = 0x434F5253;
    private const int SettingsWindowId = 0x434F4D53;
    private const int BaseSupplyWindowId = 0x434F4253;
    private const int SelectionBarScaleId = 0x434F4D5A;

    private const float LoadoutRowHeight = 22f;
    private const float SupplyRowHeight = 92f;
    private const float StructureRowHeight = 64f;
    private const float HangarRowHeight = 62f;
    private const float CargoRunRowHeight = 62f;
    private const float SlingRowHeight = 94f;
    private const float DepotRowHeight = 100f;

    /// <summary>What the altitude row adds to the bar: its 20 px less the gap above the order row.</summary>
    private const float AltitudeRowHeight = 12f;
    private const float PortraitSize = 176f;

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderGroupService groupService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderRadarService radarService;
    private readonly CommanderMobileEmplacementService mobileEmplacementService;
    private readonly CommanderRepairService repairService;
    private readonly CommanderDirectPathService directPathService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderAirCommandService airCommandService;
    private readonly CommanderSamSiteService samSiteService;
    private readonly CommanderEconomyService economyService;
    private readonly CommanderSupplyHeliUi supplyHeliUi;
    private readonly CommanderAirCommandUi airCommandUi;
    private readonly CommanderNavalPurchaseUi navalPurchaseUi;
    private readonly CommanderSamSiteAnalyzerUi samSiteAnalyzerUi;
    private readonly CommanderEconomyUi economyUi;
    private readonly CommanderDepotUi depotUi;
    private readonly CommanderDraftUi draftUi = new();
    private readonly CommanderUnitBanUi unitBanUi = new();
    private readonly CommanderUnitListUi unitListUi;
    private readonly CommanderWorldMarkerRenderer worldMarkerRenderer;
    private readonly Action unlockAdvancedFeatures;

    // Fixed at construction: the analyzer window has no button to open it any more, so nothing
    // toggles this while the mode is up.
    private readonly bool showSamAnalyzerUi = CommanderSettings.ShowSamAnalyzerUi;

    private readonly Dictionary<Canvas, bool> screenshotCanvasStates = new();
    private readonly List<string> chipTypes = new();
    private readonly List<int> chipCounts = new();
    private readonly List<string> chipLabels = new();
    private readonly List<CommanderShortcutReference.Entry> shortcutEntries = new();
    private readonly GUIContent shortcutMeasure = new();
    private readonly List<CommanderSupplyStoreService.BaseSupply> baseSupplyRows = new();
    private readonly List<string> loadoutRows = new();
    private readonly List<string> loadoutNames = new();
    private readonly List<int> loadoutAmmo = new();
    private readonly List<int> loadoutFullAmmo = new();

    /// <summary>Per loadout row, the key its FIRE button sends; empty for a gun row, which has none.</summary>
    private readonly List<string> loadoutFireKeys = new();

    private bool panelVisible;
    private float pendingUiScale = -1f;
    private bool baseSupplyWindowVisible;
    private float panelContentHeight = 420f;
    private float radarContentHeight = 200f;
    private bool baseSupplyHelpVisible;
    private bool pinnedHelpVisible;
    private bool radarHelpVisible;
    private bool selectionHelpVisible;
    private bool settingsVisible;
    private bool settingsHelpVisible;
    private bool advancedUnlockConfirmation;
    private int settingsTab;
    private string? bindingCapture;
    private bool pinnedWindowVisible = true;
    private int pinnedTab = 1;
    private bool siteAirbaseDropdownOpen;
    private bool siteThresholdDropdownOpen;
    private bool showSupplyMissions = true;
    private bool showAirCommandMissions = true;
    private int screenshotUiStage;
    private bool reopenTacticalMapAfterScreenshot;
    private bool showCommandButton = CommanderSettings.ShowCommandButton;
    private bool showFactionMoney = CommanderSettings.ShowFactionMoney;
    private bool showTacticalMap = CommanderSettings.ShowTacticalMap;
    private bool showSelectionBar = CommanderSettings.ShowSelectionBar;
    private bool showPinnedUnits = CommanderSettings.ShowPinnedUnits;
    private bool showUnitSystems = CommanderSettings.ShowUnitSystems;
    private bool showDepotUi = CommanderSettings.ShowDepotUi;
    private bool showSupplyUi = CommanderSettings.ShowSupplyUi;
    private bool showAirCommandUi = CommanderSettings.ShowAirCommandUi;
    private bool showNavalUi = CommanderSettings.ShowNavalUi;
    private bool showWorldMarkers = CommanderSettings.ShowWorldMarkers;
    private bool showUnitListUi = CommanderSettings.ShowUnitListUi;
    private bool showBuildUi = CommanderSettings.ShowBuildUi;
    private bool positionsInitialized;
    private Rect launcherRect;
    private Rect panelRect;
    private Rect baseSupplyWindowRect;
    private Rect selectionBarRect;
    private Rect selectionPortraitRect;
    private int chipRevision = -1;
    private int chipCount = -1;
    private Vector2 shortcutScroll;
    private Vector2 controlsScroll;
    private Rect pinnedWindowRect;
    private Rect radarWindowRect;
    private Rect selectionHelpRect;

    /// <summary>The MORE row above the selection bar: the orders most players rarely need.</summary>
    private Rect selectionMoreRect;
    private bool selectionMoreVisible;
    private Rect settingsWindowRect;
    private Rect pinnedLauncherRect;
    private Vector2 baseSupplyScroll;
    private float nextBaseSupplyAt;
    private Vector2 pinnedScroll;
    private Unit? siteUiTarget;
    private string selectionCardText = string.Empty;
    private bool selectionCardWarning;
    private float nextSelectionCardAt;
    private Unit? loadoutUnit;
    private float nextLoadoutAt;
    private float fireHeldUntil;
    private float nextHeldFireAt;

    internal CommanderOverlayUi(
        CommanderSelectionService selectionService,
        CommanderMoveService moveService,
        CommanderGroupService groupService,
        CommanderSpawnService spawnService,
        CommanderRadarService radarService,
        CommanderMobileEmplacementService mobileEmplacementService,
        CommanderRepairService repairService,
        CommanderDirectPathService directPathService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderAirCommandService airCommandService,
        CommanderNavalPurchaseService navalPurchaseService,
        CommanderSamSiteAnalyzerService samSiteAnalyzerService,
        CommanderSamSiteService samSiteService,
        CommanderEconomyService economyService,
        Action unlockAdvancedFeatures)
    {
        Instance = this;
        this.selectionService = selectionService;
        this.moveService = moveService;
        this.groupService = groupService;
        this.spawnService = spawnService;
        this.radarService = radarService;
        this.mobileEmplacementService = mobileEmplacementService;
        this.repairService = repairService;
        this.directPathService = directPathService;
        this.supplyHeliService = supplyHeliService;
        this.airCommandService = airCommandService;
        this.samSiteService = samSiteService;
        this.economyService = economyService;
        this.unlockAdvancedFeatures = unlockAdvancedFeatures;
        supplyHeliUi = new CommanderSupplyHeliUi(supplyHeliService);
        airCommandUi = new CommanderAirCommandUi(airCommandService);
        airCommandUi.OpenTransport = definition =>
        {
            airCommandUi.Hide();
            supplyHeliUi.ShowFor(definition);
        };
        supplyHeliUi.OpenAirCommand = () =>
        {
            supplyHeliUi.Hide();
            airCommandUi.Show();
        };
        navalPurchaseUi = new CommanderNavalPurchaseUi(navalPurchaseService);
        samSiteAnalyzerUi = new CommanderSamSiteAnalyzerUi(
            samSiteAnalyzerService,
            samSiteService,
            supplyHeliService);
        economyUi = new CommanderEconomyUi(economyService, repairService);
        depotUi = new CommanderDepotUi(spawnService);
        unitListUi = new CommanderUnitListUi(selectionService, groupService);
        worldMarkerRenderer = new CommanderWorldMarkerRenderer(
            selectionService,
            moveService,
            spawnService,
            supplyHeliService,
            samSiteAnalyzerService,
            samSiteService);
    }

    internal static CommanderOverlayUi? Instance { get; private set; }

    internal bool CommanderUiHidden => ScreenshotUiHidden;

    internal bool ShowTacticalMapUi => CommanderFeatureGate.AdvancedFeaturesEnabled
        && (showTacticalMap || airCommandUi.Visible)
        && !ScreenshotUiHidden;

    private bool ScreenshotUiHidden => screenshotUiStage != 0;

    private bool HasPinEntries => selectionService.PinnedUnits.Count > 0
        || selectionService.MissionUnits.Count > 0
        || selectionService.SamSiteUnits.Count > 0;

    public void Activate()
    {
        panelVisible = false;
        baseSupplyWindowVisible = false;
        supplyHeliUi.Hide();
        airCommandUi.Hide();
        navalPurchaseUi.Hide();
        samSiteAnalyzerUi.Hide();
        economyUi.Hide();
        depotUi.Reset();
        unitListUi.Hide();
        ResetScreenshotUi();
        settingsVisible = false;
        bindingCapture = null;
        advancedUnlockConfirmation = false;

        // Entering RTS mode during a draft is entering the draft.
        if (CommanderDraftService.IsDrafting)
        {
            draftUi.Show();
        }

        if (CommanderUnitBanService.IsVoting)
        {
            unitBanUi.Show();
        }
    }

    public void Deactivate()
    {
        ResetScreenshotUi();
        panelVisible = false;
        baseSupplyWindowVisible = false;
        settingsVisible = false;
        bindingCapture = null;
        advancedUnlockConfirmation = false;
        supplyHeliUi.Hide();
        airCommandUi.Hide();
        navalPurchaseUi.Hide();
        samSiteAnalyzerUi.Hide();
        economyUi.Hide();
        depotUi.Reset();
        unitListUi.Hide();
    }

    /// <summary>
    /// Leaving RTS mode to fly keeps the speed the player chose; only a new mission puts the clock
    /// back, so nobody drops into the next one already sped up.
    /// </summary>
    public void ResetSession()
    {
        if (!Mathf.Approximately(Time.timeScale, 1f))
        {
            TimeScaleManager.Scale = 1f;
        }
    }

    public void TickActive()
    {
        if (screenshotUiStage == 2)
        {
            MaintainAllUiHidden();
        }

        // Air Command places its mission areas on this map, so it keeps it up whatever the
        // Tactical map setting says — closing it here would fight that window's own reopen.
        if (!showTacticalMap && !airCommandUi.Visible && CommanderTacticalMapService.Instance?.IsOpen == true)
        {
            CommanderTacticalMapService.Instance.Close();
        }

        LayoutTopBar();

        if (!positionsInitialized)
        {
            panelRect = new Rect(12f, TopBarHeight + 12f, 420f, panelContentHeight);
            baseSupplyWindowRect = new Rect(
                Mathf.Max(12f, (CommanderUiScale.Width * 0.5f) - 230f),
                Mathf.Max(58f, CommanderUiScale.Height * 0.28f),
                460f,
                Mathf.Min(420f, CommanderUiScale.Height - 24f));
            pinnedWindowRect = new Rect(
                Mathf.Max(12f, CommanderUiScale.Width - 354f),
                Mathf.Clamp((CommanderUiScale.Height * 0.66f) - 170f, 58f, CommanderUiScale.Height - 352f),
                342f,
                340f);

            // Under the tactical map on the right, clear of the game's own weapon readout below it.
            radarWindowRect = new Rect(
                Mathf.Max(12f, CommanderUiScale.Width - 392f),
                Mathf.Min(640f, CommanderUiScale.Height - 440f),
                380f,
                radarContentHeight);
            float settingsWidth = Mathf.Min(680f, CommanderUiScale.Width - 24f);
            float settingsHeight = Mathf.Min(890f, CommanderUiScale.Height - 24f);
            settingsWindowRect = new Rect(
                Mathf.Max(12f, (CommanderUiScale.Width - settingsWidth) * 0.5f),
                Mathf.Max(12f, (CommanderUiScale.Height - settingsHeight) * 0.5f),
                settingsWidth,
                settingsHeight);
            positionsInitialized = true;
        }
        else
        {
            panelRect.height = Mathf.Min(panelContentHeight, CommanderUiScale.Height - 64f);
            settingsWindowRect.width = Mathf.Min(680f, CommanderUiScale.Width - 24f);
            settingsWindowRect.height = Mathf.Min(890f, CommanderUiScale.Height - 24f);
        }

        panelRect.size = CommanderWindowScale.Size(WindowId, new Vector2(420f, panelRect.height), new Vector2(320f, 200f));
        baseSupplyWindowRect.size = CommanderWindowScale.Size(BaseSupplyWindowId, new Vector2(460f, Mathf.Min(420f, CommanderUiScale.Height - 24f)), new Vector2(340f, 220f));
        pinnedWindowRect.size = CommanderWindowScale.Size(PinnedWindowId, new Vector2(342f, 340f), new Vector2(240f, 160f));
        settingsWindowRect.size = CommanderWindowScale.Size(SettingsWindowId, settingsWindowRect.size, new Vector2(480f, 360f));
        panelRect = CommanderWindowScale.Clamp(WindowId, panelRect);
        baseSupplyWindowRect = CommanderWindowScale.Clamp(BaseSupplyWindowId, baseSupplyWindowRect);
        pinnedWindowRect = CommanderWindowScale.Clamp(PinnedWindowId, pinnedWindowRect);
        settingsWindowRect = CommanderWindowScale.Clamp(SettingsWindowId, settingsWindowRect);
        pinnedLauncherRect = new Rect(
            Mathf.Min(CommanderUiScale.Width - 70f, pinnedWindowRect.xMax + 6f),
            pinnedWindowRect.y,
            62f,
            28f);
        bool samSiteFocused = samSiteService.IsConstructionCore(selectionService.FocusedSelection);
        radarWindowRect.width = Mathf.Min(
            samSiteFocused ? 430f : 380f,
            CommanderUiScale.Width - 24f);
        radarWindowRect.height = Mathf.Min(
            samSiteFocused ? 734f : radarContentHeight,
            CommanderUiScale.Height - 64f);
        radarWindowRect.size = CommanderWindowScale.Size(RadarWindowId, radarWindowRect.size, new Vector2(300f, 200f));
        radarWindowRect = CommanderWindowScale.Clamp(RadarWindowId, radarWindowRect);

        // A mixed selection grows the bar upward to fit the per-type chip row; a single unit grows
        // it to fit its loadout instead, two weapons per row.
        RefreshLoadout();

        // A selected supply depot gets its own row: two stock bars and the two BUY buttons. A
        // building this commander put down gets one more: its level, UPGRADE and DESTROY.
        float supplyHeight = IsSupplyDepotSelected ? SupplyRowHeight : 0f;
        float structureHeight = IsStructureSelected ? StructureRowHeight : 0f;
        float hangarHeight = SelectedHangar != null ? HangarRowHeight : 0f;
        float cargoRunHeight = IsCargoRunSelected ? CargoRunRowHeight : 0f;
        float slingHeight = IsSlingHelicopterSelected ? SlingRowHeight : 0f;
        float depotHeight = IsVehicleDepotSelected ? DepotRowHeight : 0f;
        float selectionBarHeight =
            (selectionService.SelectedUnits.Count > 1
                ? 112f
                : 74f + LoadoutHeight + supplyHeight + structureHeight + hangarHeight + depotHeight + cargoRunHeight + slingHeight)
            + (HasAltitudeRow ? AltitudeRowHeight : 0f)
            + 44f;
        selectionBarRect = new Rect(
            Mathf.Max(12f, (CommanderUiScale.Width - 680f) * 0.5f),
            CommanderUiScale.Height - 84f - selectionBarHeight,
            Mathf.Min(680f, CommanderUiScale.Width - 24f),
            selectionBarHeight);

        // The portrait stands to the left of the bar, and the pair is centred together.
        selectionBarRect.x += (PortraitSize + 6f) * 0.5f;
        selectionPortraitRect = new Rect(
            selectionBarRect.x - PortraitSize - 6f,
            selectionBarRect.yMax - Mathf.Min(PortraitSize, selectionBarRect.height),
            PortraitSize,
            Mathf.Min(PortraitSize, selectionBarRect.height));
        selectionMoreRect = new Rect(selectionBarRect.x, selectionBarRect.y - 46f, selectionBarRect.width, 42f);
        if (showUnitListUi)
        {
            unitListUi.Tick();
        }

        unitBanUi.Tick();

        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            supplyHeliUi.Tick();
            airCommandUi.Tick();
            depotUi.Tick();
            draftUi.Tick();
            Unit? focused = selectionService.FocusedSelection;
            navalPurchaseUi.TrackDock(economyService.IsBuiltNavalDock(focused)
                && CommanderGameAccess.IsFriendlyUnit(focused, CommanderGameAccess.GetLocalHq()) ? focused : null);
        }
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        Vector2 selectionPoint = ToSelectionSpace(guiPoint);
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        if (ScreenshotUiHidden)
        {
            return false;
        }

        if (unitBanUi.ContainsScreenPoint(screenPoint) || (advanced && draftUi.ContainsScreenPoint(screenPoint)))
        {
            return true;
        }

        if (airCommandUi.Visible)
        {
            return (advanced && showAirCommandUi && airCommandUi.ContainsScreenPoint(screenPoint))
                || topBarRect.Contains(guiPoint)
                || (showSelectionBar && selectionService.SelectedUnits.Count > 0 && (selectionBarRect.Contains(selectionPoint) || selectionPortraitRect.Contains(selectionPoint)))
                || (showSelectionBar && selectionMoreVisible && selectionService.SelectedUnits.Count > 0 && selectionMoreRect.Contains(selectionPoint))
                || (settingsVisible && CommanderWindowScale.Visual(SettingsWindowId, settingsWindowRect).Contains(guiPoint));
        }

        return CommanderAlertUi.Instance?.ContainsScreenPoint(screenPoint) == true
            || CommanderTutorialUi.Instance?.ContainsScreenPoint(screenPoint) == true
            || topBarRect.Contains(guiPoint)
            || (panelVisible && CommanderWindowScale.Visual(WindowId, panelRect).Contains(guiPoint))
            || (advanced && baseSupplyWindowVisible && CommanderWindowScale.Visual(BaseSupplyWindowId, baseSupplyWindowRect).Contains(guiPoint))
            || (showSelectionBar && selectionService.SelectedUnits.Count > 0 && (selectionBarRect.Contains(selectionPoint) || selectionPortraitRect.Contains(selectionPoint)))
            || (showSelectionBar && selectionHelpVisible && selectionService.SelectedUnits.Count > 0 && selectionHelpRect.Contains(selectionPoint))
            || (showSelectionBar && selectionMoreVisible && selectionService.SelectedUnits.Count > 0 && selectionMoreRect.Contains(selectionPoint))
            || (PinnedListEnabled && showPinnedUnits && HasPinEntries && (pinnedLauncherRect.Contains(guiPoint) || (pinnedWindowVisible && CommanderWindowScale.Visual(PinnedWindowId, pinnedWindowRect).Contains(guiPoint))))
            || (advanced && showUnitSystems && TryGetUnitSystemsTarget(out _) && CommanderWindowScale.Visual(RadarWindowId, radarWindowRect).Contains(guiPoint))
            || (showUnitListUi && unitListUi.ContainsScreenPoint(screenPoint))
            || (advanced && showDepotUi && depotUi.ContainsScreenPoint(screenPoint))
            || (advanced && showSupplyUi && supplyHeliUi.ContainsScreenPoint(screenPoint))
            || (advanced && showAirCommandUi && airCommandUi.ContainsScreenPoint(screenPoint))
            || (advanced && showNavalUi && navalPurchaseUi.ContainsScreenPoint(screenPoint))
            || (advanced && showSamAnalyzerUi && samSiteAnalyzerUi.ContainsScreenPoint(screenPoint))
            || (advanced && showBuildUi && economyUi.ContainsScreenPoint(screenPoint))
            || (settingsVisible && CommanderWindowScale.Visual(SettingsWindowId, settingsWindowRect).Contains(guiPoint));
    }

    internal void DrawInactiveLauncher(Action activateCommander)
    {
        CommanderUiTheme.Ensure();
        float centerY = CommanderUiScale.Height * 0.5f;
        launcherRect = new Rect(10f, centerY - 42f, 52f, 84f);
        EventType activationEvent = Event.current.type;
        if (GUI.Button(launcherRect, "CMD", CommanderUiTheme.PrimaryButton)
            && activationEvent == EventType.MouseUp)
        {
            GUI.FocusControl(null);
            activateCommander();
        }

        if (CommanderUnitBanService.IsVoting)
        {
            unitBanUi.DrawInactiveBanner(activateCommander);
        }
        else if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            draftUi.DrawInactiveBanner(activateCommander);
        }
    }

    internal void Draw()
    {
        if (ScreenshotUiHidden)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        if (showWorldMarkers)
        {
            worldMarkerRenderer.Draw(supplyHeliUi.Visible && supplyHeliUi.ShowLz);
        }

        // Drawn before the Air Command early-out: the balance is what the player spends in that window.
        DrawTopBar();

        // Before the Air Command early-out, for the same reason the funds box is: the window a
        // refusal came from is usually the window that is covering the screen.
        CommanderNotice.Draw();

        // So is the unit ban vote, which comes first: nothing spawns until it locks.
        if (CommanderUnitBanService.IsVoting)
        {
            unitBanUi.Draw();
            if (unitBanUi.Visible)
            {
                DrawSettingsWindowIfVisible();
                return;
            }
        }

        // The draft is the whole game until the battle starts: while its window is up nothing else
        // is drawn beside it, and hidden it leaves a pill at the top of the screen to bring it back.
        if (advanced && CommanderDraftService.IsDrafting)
        {
            draftUi.Draw();
            if (draftUi.Visible)
            {
                DrawSettingsWindowIfVisible();
                return;
            }
        }

        if (advanced && airCommandUi.Visible)
        {
            if (showAirCommandUi)
            {
                airCommandUi.Draw();
            }

            if (showSelectionBar)
            {
                DrawSelectionBar();
            }

            DrawSettingsWindowIfVisible();
            return;
        }

        if (panelVisible)
        {
            panelRect = CommanderWindowScale.Window(WindowId, panelRect, DrawPanelWindow, "COMMAND GUIDE");
        }

        if (advanced && baseSupplyWindowVisible)
        {
            baseSupplyWindowRect = CommanderWindowScale.Window(BaseSupplyWindowId, baseSupplyWindowRect, DrawBaseSupplyWindow, "BASE SUPPLY");
        }

        if (PinnedListEnabled && showPinnedUnits && HasPinEntries)
        {
            if (GUI.Button(pinnedLauncherRect, pinnedWindowVisible ? "PINS <" : "PINS >", CommanderUiTheme.Button))
            {
                pinnedWindowVisible = !pinnedWindowVisible;
            }

            if (pinnedWindowVisible)
            {
                pinnedWindowRect = CommanderWindowScale.Window(PinnedWindowId, pinnedWindowRect, DrawPinnedWindow, "UNIT LIST");
            }
        }

        if (advanced && showUnitSystems && TryGetUnitSystemsTarget(out _))
        {
            string title = samSiteService.IsConstructionCore(selectionService.FocusedSelection)
                ? "SAM SITE LOGISTICS"
                : "UNIT SYSTEMS";
            radarWindowRect = CommanderWindowScale.Window(RadarWindowId, radarWindowRect, DrawRadarWindow, title);
        }

        if (showUnitListUi)
        {
            unitListUi.Draw();
        }

        if (advanced && showDepotUi)
        {
            depotUi.Draw();
        }

        if (advanced && showSupplyUi)
        {
            supplyHeliUi.Draw();
        }

        if (advanced && showAirCommandUi)
        {
            airCommandUi.Draw();
        }

        if (advanced && showNavalUi)
        {
            navalPurchaseUi.Draw();
        }

        if (advanced && showSamAnalyzerUi)
        {
            samSiteAnalyzerUi.Draw();
        }

        if (advanced && showBuildUi)
        {
            economyUi.Draw();
        }

        if (showSelectionBar)
        {
            DrawSelectionBar();
        }

        DrawSettingsWindowIfVisible();
    }

    internal void ToggleScreenshotUi()
    {
        if (screenshotUiStage == 0)
        {
            screenshotUiStage = 1;
            reopenTacticalMapAfterScreenshot = CommanderTacticalMapService.Instance?.IsOpen == true;
            if (reopenTacticalMapAfterScreenshot)
            {
                CommanderTacticalMapService.Instance?.Close();
            }

            return;
        }

        if (screenshotUiStage == 1)
        {
            screenshotUiStage = 2;
            MaintainAllUiHidden();
            return;
        }

        RestoreBaseUi();
        screenshotUiStage = 0;
        if (reopenTacticalMapAfterScreenshot && showTacticalMap)
        {
            CommanderTacticalMapService.Instance?.Open();
        }

        reopenTacticalMapAfterScreenshot = false;
    }

    /// <summary>Opens AIR COMMAND already pointed at one set of hangar doors.</summary>
    private void ShowAirCommandForHangar(Hangar hangar)
    {
        airCommandUi.ShowForHangar(hangar);
    }

    private void MaintainAllUiHidden()
    {
        Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (!screenshotCanvasStates.ContainsKey(canvas))
            {
                screenshotCanvasStates.Add(canvas, canvas.enabled);
            }

            canvas.enabled = false;
        }
    }

    private void RestoreBaseUi()
    {
        foreach (KeyValuePair<Canvas, bool> entry in screenshotCanvasStates)
        {
            if (entry.Key != null)
            {
                entry.Key.enabled = entry.Value;
            }
        }

        screenshotCanvasStates.Clear();
    }

    private void ResetScreenshotUi()
    {
        RestoreBaseUi();
        screenshotUiStage = 0;
        reopenTacticalMapAfterScreenshot = false;
    }
}
