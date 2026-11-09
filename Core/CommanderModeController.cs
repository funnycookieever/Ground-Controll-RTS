using System.Diagnostics.CodeAnalysis;
using NuclearOption.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GroundControlRts;

/// <summary>
/// The RTS mode itself: it owns the camera/cursor takeover, the draw pass, and a
/// <see cref="CommanderServiceRegistry"/> holding everything else.
/// <para>
/// Adding a feature means writing a service, implementing the lifecycle interfaces it
/// actually needs, and adding one <c>Register</c> line in <see cref="Awake"/>. Nothing else
/// in this file should have to change. The order of those Register calls is the per-frame
/// schedule, so read <see cref="Awake"/> top to bottom to see what runs when.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Unity creates it: CommanderPlugin.Awake calls AddComponent.")]
internal sealed class CommanderModeController : MonoBehaviour
{
    private readonly CommanderServiceRegistry services = new();

    // The camera and cursor takeover brackets every service, so it is driven by hand rather
    // than registered.
    private CommanderCameraController? cameraController;
    private CommanderCursorController? cursorController;

    // Registered services this file still talks to directly, because something about them is
    // conditional: physics-step camera work, the map window's open/close, the draw pass, and
    // the scene-change teardown that has to run even when the mode was never entered.
    private CommanderCameraFollowService? cameraFollowService;
    private CommanderTacticalMapService? tacticalMapService;
    private CommanderBoxSelectService? boxSelectService;
    private CommanderOverlayUi? overlayUi;

    // Draw-only and input-only surfaces: no lifecycle of their own.
    private CommanderPovCrewUi? povCrewUi;
    private CommanderAlertUi? alertUi;
    private CommanderTutorialUi? tutorialUi;
    private CommanderInputController? inputController;

    // The aircraft the player was flying when the mode opened. A different one appearing means
    // the player just spawned from the map, which still hands them the cockpit.
    private Aircraft? flyingOnEntry;

    internal bool IsActive { get; private set; }

    internal void Toggle()
    {
        if (IsActive)
        {
            Deactivate($"toggle key {CommanderSettings.ToggleCommander}");
            return;
        }

        Activate();
    }

    private static bool IsInGame()
    {
        return GameManager.gameState is GameState.SinglePlayer or GameState.Multiplayer;
    }

    private static bool IsPlayerInOperationalAircraft()
    {
        return GetOperationalLocalAircraft() != null;
    }

    private static bool ShouldShowCommanderEntry()
    {
        if (IsPlayerInOperationalAircraft() || !IsInGame())
        {
            return false;
        }

        if (DynamicMap.mapMaximized)
        {
            return true;
        }

        return CommanderAircraftMenuPatches.IsOpen;
    }

    private static Aircraft? GetOperationalLocalAircraft()
    {
        return GameManager.GetLocalAircraft(out Aircraft aircraft) && aircraft != null && !aircraft.disabled
            ? aircraft
            : null;
    }

    private void Awake()
    {
        cameraController = new CommanderCameraController();
        cursorController = new CommanderCursorController();

        // First, so a guest's request is heard however the rest of the frame goes. Core tier: a
        // guest may be commanding while the host is flying and never opens RTS mode.
        services.Register(new CommanderNetwork());

        CommanderSelectionService selectionService = services.Register(new CommanderSelectionService());
        CommanderGroupService groupService = services.Register(new CommanderGroupService(selectionService));
        cameraFollowService = services.Register(new CommanderCameraFollowService(selectionService));
        CommanderMarkerService markerService = services.Register(new CommanderMarkerService(selectionService));
        boxSelectService = services.Register(new CommanderBoxSelectService(selectionService, markerService));

        // Core tier: a pilot on a commanded team has to receive pings whatever mode the mission is in.
        services.Register(new CommanderPingService(markerService));
        tacticalMapService = services.Register(new CommanderTacticalMapService(cameraFollowService));

        // Alerts run first among the persistent ticks and outside the feature gate: their whole
        // point is telling the player what happened while they were flying.
        CommanderAlertService alertService = services.Register(new CommanderAlertService());

        CommanderRadarService radarService =
            services.Register(new CommanderRadarService(selectionService), CommanderTier.Advanced);
        CommanderMobileEmplacementService mobileEmplacementService =
            services.Register(new CommanderMobileEmplacementService(selectionService), CommanderTier.Advanced);
        CommanderDirectPathService directPathService =
            services.Register(new CommanderDirectPathService(selectionService), CommanderTier.Advanced);
        services.Register(new CommanderTerrainRouteService(), CommanderTier.Advanced);
        CommanderSupplyHeliService supplyHeliService =
            services.Register(new CommanderSupplyHeliService(), CommanderTier.Advanced);

        // Fuel and ammunition stocks tick before Air Command because Air Command spends them: a
        // resupply landing asks this service whether there is anything on the shelf.
        services.Register(new CommanderSupplyStoreService(), CommanderTier.Advanced);
        CommanderAirCommandService airCommandService =
            services.Register(new CommanderAirCommandService(tacticalMapService), CommanderTier.Advanced);

        // After Air Command, so a sweep of its that switched a lifting helicopter's pilot state is
        // undone in the same frame.
        services.Register(new CommanderSlingLiftService(), CommanderTier.Advanced);

        // After the lift it hands its pickups to, so a rescue that ended this frame is seen as ended.
        services.Register(new CommanderRecoveryService(), CommanderTier.Advanced);
        CommanderNavalPurchaseService navalPurchaseService =
            services.Register(new CommanderNavalPurchaseService(), CommanderTier.Advanced);
        CommanderSamSiteAnalyzerService samSiteAnalyzerService =
            services.Register(new CommanderSamSiteAnalyzerService(), CommanderTier.Advanced);
        CommanderSamSiteService samSiteService = services.Register(
            new CommanderSamSiteService(samSiteAnalyzerService, supplyHeliService),
            CommanderTier.Advanced);
        CommanderFactionVehicleService factionVehicleService = services.Register(new CommanderFactionVehicleService());
        CommanderSpawnService spawnService = services.Register(
            new CommanderSpawnService(selectionService, factionVehicleService, tacticalMapService),
            CommanderTier.Advanced);
        services.Register(new CommanderTeamQueueService(spawnService), CommanderTier.Advanced);
        CommanderEconomyService economyService =
            services.Register(new CommanderEconomyService(), CommanderTier.Advanced);

        // After the economy (it puts the draft's bunker down) and before the enemy commander, so
        // the enemy's first review after the battle starts already sees its drafted air wing.
        services.Register(new CommanderDraftService(), CommanderTier.Advanced);

        // Core tier: bans are a rule of the match on every mission, RTS mode open or not.
        services.Register(new CommanderUnitBanService());

        // Only does anything on Ground Control Tutorial; it reads the economy, supply and orders.
        services.Register(new CommanderTutorialService(), CommanderTier.Advanced);
        services.Register(new CommanderEnemyCommanderService(), CommanderTier.Advanced);

        // Core tier: the round has to be able to end whether or not RTS mode is open.
        services.Register(new CommanderVictoryService());

        // GOD DIFFICULTY's voice. Core tier: it has to speak whether or not RTS mode is open.
        services.Register(new CommanderGodService());

        CommanderRepairService repairService = services.Register(new CommanderRepairService());

        // Routes tick last so orders act on this frame's spawns and kills.
        CommanderMoveService moveService = services.Register(new CommanderMoveService(selectionService));

        // Capture ticks after the move service because a capture order is a move order: it hands
        // the destination to that service rather than issuing its own for the player's units.
        services.Register(new CommanderCaptureService(selectionService));

        // Route lines live inside the map's icon layer, so they tick after the routes they draw.
        services.Register(new CommanderMapRouteRenderer(selectionService, moveService));

        // The overlay reads every service's state, so it ticks after all of them.
        overlayUi = services.Register(new CommanderOverlayUi(
            selectionService,
            moveService,
            groupService,
            spawnService,
            radarService,
            mobileEmplacementService,
            repairService,
            directPathService,
            supplyHeliService,
            airCommandService,
            navalPurchaseService,
            samSiteAnalyzerService,
            samSiteService,
            economyService,
            UnlockAdvancedFeatures));

        povCrewUi = new CommanderPovCrewUi(cameraFollowService);
        alertUi = new CommanderAlertUi(alertService, selectionService, () => IsActive);
        tutorialUi = new CommanderTutorialUi(() => IsActive);
        inputController = new CommanderInputController(
            overlayUi,
            selectionService,
            spawnService,
            markerService,
            moveService,
            tacticalMapService,
            supplyHeliService,
            airCommandService,
            economyService,
            boxSelectService);
        inputController.SetPovCrewUi(povCrewUi);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        // The persistent tick runs before RTS mode is ever opened, and the enemy commander has to
        // know which mission it is in to decide whether the duel rules apply, so the mission name
        // is refreshed here rather than only on activation.
        CommanderFeatureGate.RefreshMission();
        if (CommanderNetwork.HostFeaturesUnlocked && !CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            UnlockAdvancedFeatures();
        }

        services.TickPersistent(CommanderFeatureGate.AdvancedFeaturesEnabled);
        CommanderUnitPreview.Tick();
        if (IsInGame())
        {
            CommanderEconomyService.EnsureVehiclePrices();
            if (CommanderShortcutInput.IsDown(CommanderSettings.ToggleCommander))
            {
                Toggle();
            }
        }

        if (!IsActive)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.ToggleUi))
        {
            overlayUi?.ToggleScreenshotUi();
        }

        Aircraft? flying = GetOperationalLocalAircraft();
        if (flying != null && flying != flyingOnEntry)
        {
            Deactivate(
                $"new local aircraft '{flying.name}', was '{(flyingOnEntry != null ? flyingOnEntry.name : "none")}' on entry",
                restorePreviousCamera: false);
            return;
        }

        if (cursorController != null)
        {
            CommanderCursorController.TickActive();
        }

        services.TickActive(CommanderFeatureGate.AdvancedFeaturesEnabled);

        // The pause menu owns the mouse while it is up: a click on RESUME must not also select or
        // order whatever sits under it in the world.
        if (!LeaderboardMenu.IsOpen())
        {
            inputController?.Tick();
        }
    }

    private void FixedUpdate()
    {
        if (IsActive)
        {
            cameraFollowService?.FixedTick();
        }
    }

    private void OnGUI()
    {
        // IMGUI always draws above every uGUI canvas, so there is no "behind the pause overlay"
        // to put the command UI in. Not drawing it while the Esc menu is up is the equivalent.
        if (LeaderboardMenu.IsOpen())
        {
            return;
        }

        // IMGUI calls this several times a frame (layout, repaint, input).
        Matrix4x4 previousMatrix = CommanderUiScale.Begin();
        CommanderUiTheme.Ensure();

        // Themed scrollbars/sliders come from the skin, so install it for our draw pass only.
        GUISkin previousSkin = GUI.skin;
        GUI.skin = CommanderUiTheme.Skin;
        try
        {
            CommanderGodUi.Draw();
            CommanderPriceEditorUi.Draw();
            if (!IsActive)
            {
                CommanderCockpitMarkerUi.DrawCockpit();
                alertUi?.Draw();
                tutorialUi?.Draw();
                if (ShouldShowCommanderEntry())
                {
                    overlayUi?.DrawInactiveLauncher(Activate);
                }

                return;
            }

            overlayUi?.Draw();
            if (overlayUi?.CommanderUiHidden != true)
            {
                alertUi?.Draw();
                tutorialUi?.Draw();
                povCrewUi?.Draw();
                boxSelectService?.Draw();
            }

            if (overlayUi?.ShowTacticalMapUi == true)
            {
                tacticalMapService?.DrawControls();
            }
        }
        finally
        {
            CommanderCockpitMarkerUi.DrawWheel();
            GUI.skin = previousSkin;
            CommanderUiScale.End(previousMatrix);
        }
    }

    private void OnDisable()
    {
        Deactivate("mod component disabled");
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        Deactivate("mod destroyed", restorePreviousCamera: false);
    }

    private void OnApplicationQuit()
    {
        Deactivate("game quitting");
    }

    private void Activate()
    {
        if (IsActive)
        {
            return;
        }

        flyingOnEntry = GetOperationalLocalAircraft();
        if (CommanderAircraftMenuPatches.IsOpen)
        {
            CommanderAircraftMenuPatches.Current!.ReturnToMap();
        }

        if (cameraController == null || !cameraController.TryActivate())
        {
            CommanderPlugin.Log.LogWarning("RTS mode could not start because the free camera is not available yet.");
            return;
        }

        CommanderFeatureGate.RefreshMission();
        cursorController?.Activate();
        IsActive = true;
        services.Activate(CommanderFeatureGate.AdvancedFeaturesEnabled);
        OpenTacticalMapIfRequested();
        CommanderPlugin.Log.LogInfo(
            $"RTS mode enabled: mission={CommanderFeatureGate.MissionName}, features={(CommanderFeatureGate.AdvancedFeaturesEnabled ? "full" : "core")}, "
            + $"mission AI Commanders={CommanderMapSettings.OverrideInt(CommanderMapSettings.AiCommandersPrefix) ?? -1} (-1 not set), enemy mode={CommanderEnemyCommanderService.EffectiveMode}.");
    }

    private void UnlockAdvancedFeatures()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            return;
        }

        CommanderFeatureGate.UnlockAdvancedFeatures();
        if (IsActive)
        {
            services.ActivateAdvanced();
            OpenTacticalMapIfRequested();
        }

        CommanderPlugin.Log.LogWarning(
            $"Advanced RTS features manually unlocked for mission '{CommanderFeatureGate.MissionName}'.");
    }

    private void OpenTacticalMapIfRequested()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled && overlayUi?.ShowTacticalMapUi == true)
        {
            tacticalMapService?.Open();
        }
    }

    private void Deactivate(string reason, bool restorePreviousCamera = true)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        services.Deactivate();
        tacticalMapService?.Close();
        cursorController?.Deactivate();

        // Back to the cockpit: the RTS camera will have followed whatever was selected, and the
        // previous camera state (cockpit, orbit) is relative to the followed unit.
        Aircraft? flying = GetOperationalLocalAircraft();
        CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
        if (restorePreviousCamera && flying != null && camera != null && camera.followingUnit != flying)
        {
            camera.SetFollowingUnit(flying);
        }

        cameraController?.Deactivate(restorePreviousCamera);
        flyingOnEntry = null;
        CommanderPlugin.Log.LogInfo($"RTS mode disabled ({reason}).");
    }

    private void OnActiveSceneChanged(Scene previousScene, Scene newScene)
    {
        Deactivate("scene changed", restorePreviousCamera: false);

        // Deactivate() is a no-op when the mode was never entered, so these two still have to
        // be nudged by hand: a stale camera follow or drag would otherwise survive the load.
        cameraFollowService?.Deactivate();
        boxSelectService?.Cancel();
        CommanderFeatureGate.ResetSession();
        CommanderNetwork.HostFeaturesUnlocked = false;
        CommanderUnitPreview.Reset();
        services.ResetSession();
    }
}
