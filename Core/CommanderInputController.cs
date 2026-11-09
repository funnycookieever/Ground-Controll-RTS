using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderInputController
{
    private const float DoubleClickSeconds = 0.35f;

    private readonly CommanderOverlayUi overlayUi;
    private readonly CommanderSelectionService selectionService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderMarkerService markerService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderAirCommandService airCommandService;
    private readonly CommanderEconomyService economyService;
    private readonly CommanderBoxSelectService boxSelectService;
    private CommanderPovCrewUi? povCrewUi;
    private Unit? lastClickedUnit;
    private float lastClickAt;
    private bool suppressPrimaryRelease;
    private bool primaryPressOverUi;
    private bool typedLastFrame;
    private string lastEatenBy = string.Empty;
    private int eatenInARow;
    private Rect eatenSpan;

    internal CommanderInputController(
        CommanderOverlayUi overlayUi,
        CommanderSelectionService selectionService,
        CommanderSpawnService spawnService,
        CommanderMarkerService markerService,
        CommanderMoveService moveService,
        CommanderTacticalMapService tacticalMapService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderAirCommandService airCommandService,
        CommanderEconomyService economyService,
        CommanderBoxSelectService boxSelectService)
    {
        this.overlayUi = overlayUi;
        this.selectionService = selectionService;
        this.spawnService = spawnService;
        this.markerService = markerService;
        this.moveService = moveService;
        this.tacticalMapService = tacticalMapService;
        this.supplyHeliService = supplyHeliService;
        this.airCommandService = airCommandService;
        this.economyService = economyService;
        this.boxSelectService = boxSelectService;
    }

    /// <summary>
    /// A placement mode owned the mouse this frame. The map's own click handling reads it: a
    /// placement disarms on the press, so by the release nothing else remembers it was armed.
    /// </summary>
    internal static bool PlacementActive { get; private set; }

    internal static bool TryGetMapUnitUnderCursor(DynamicMap dynamicMap, out Unit unit)
    {
        unit = null!;
        Vector2 mousePosition = Input.mousePosition;
        float bestDistance = float.PositiveInfinity;
        System.Collections.Generic.List<MapIcon> icons = dynamicMap.mapIcons;
        for (int i = 0; i < icons.Count; i++)
        {
            if (icons[i] is not UnitMapIcon unitIcon
                || unitIcon == null
                || !unitIcon.gameObject.activeInHierarchy
                || unitIcon.unit == null
                || !CommanderGameAccess.IsKnownToFaction(unitIcon.unit, CommanderGameAccess.GetLocalHq(), includeLostContacts: true))
            {
                continue;
            }

            float distance = CommanderTacticalMapService.IconPickDistanceSq(unitIcon, mousePosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                unit = unitIcon.unit;
            }
        }

        return unit != null;
    }

    internal void SetPovCrewUi(CommanderPovCrewUi ui)
    {
        povCrewUi = ui;
    }

    internal void Tick()
    {
        // Keyboard orders run before the cursor tests: a hotkey must not depend on whether the
        // mouse happens to be resting over a Commander window.
        TickKeyboardOrders();

        // A press that a placement mode swallowed must not come back as a selection on release —
        // dropping a mission area or a building on top of a unit was also selecting it, because
        // the placement disarms on the press and there was nothing left to stand selection down.
        // Latched here, before any early return, so a release that lands over a window or on the
        // map still clears it rather than eating the next click.
        bool placementActive = spawnService.IsMapInteractionActive()
            || IsPlacementArmed();
        PlacementActive = placementActive;
        suppressPrimaryRelease |= placementActive
            && CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction);
        bool consumedPress = false;
        if (CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            consumedPress = suppressPrimaryRelease;
            suppressPrimaryRelease = false;
        }

        if (spawnService.IsMapInteractionActive())
        {
            boxSelectService.Cancel();
            return;
        }

        Vector2 mousePosition = Input.mousePosition;
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        bool overMap = dynamicMap != null
            && DynamicMap.mapMaximized
            && dynamicMap.IsCursorInMapRectangle();

        // RTS windows always win, even when they sit on top of the fullscreen map: without
        // this, clicking a row in Order of Battle also panned the map underneath it.
        bool overUi = overlayUi.ContainsScreenPoint(mousePosition)
            || (!overMap
                && (povCrewUi?.ContainsScreenPoint(mousePosition) == true
                    || tacticalMapService.ContainsScreenPoint(mousePosition)));
        bool primaryDown = CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction);
        if (primaryDown)
        {
            primaryPressOverUi = overUi;
            TraceEatenPress(overUi ? "a window" : overMap && tacticalMapService.IsFullscreenOpen ? "the fullscreen map" : IsPlacementArmed() ? "a placement" : string.Empty, mousePosition);
        }

        if (overUi)
        {
            boxSelectService.Cancel();
            return;
        }

        // A button pressed on a window and released beside it is the window's click, not a
        // selection click in the world under the release.
        if (primaryPressOverUi && CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            primaryPressOverUi = false;
            return;
        }

        // Placement modes own the click outright; no selection or ordering while one is armed.
        if (TryHandlePlacementClick(mousePosition, overMap))
        {
            boxSelectService.Cancel();
            return;
        }

        bool boxSelected = boxSelectService.Tick(overMap);

        if (CommanderShortcutInput.IsDown(CommanderSettings.SecondaryAction))
        {
            if (IsOnlyDepotSelected())
            {
                if (overMap ? dynamicMap!.TryGetCursorCoordinates(out GlobalPosition rallyPoint) : CommanderGameAccess.TryRaycastWorldPosition(mousePosition, out rallyPoint))
                {
                    spawnService.SetRallyPoint(rallyPoint);
                }
            }
            else if (overMap)
            {
                HandleMapOrder(dynamicMap!);
            }
            else
            {
                moveService.TryIssueMoveOrder(mousePosition);
            }
        }

        // Selection resolves on release so a drag can become a box instead of a click.
        // Map clicks stay with the Basegame icon picker.
        if (!consumedPress
            && !boxSelected
            && !overMap
            && CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            HandlePrimaryClick(mousePosition);
        }
    }

    /// <summary>
    /// Players report that scrolling and selecting sometimes both stop until the Esc menu is opened
    /// and closed, and nobody has caught what holds them. Three clicks in a row eaten by the same
    /// gate log that gate and the state behind it, once, so the next log names the cause. Clicks
    /// on windows are normal, so those only count when they are spread over most of the screen.
    /// </summary>
    private void TraceEatenPress(string eatenBy, Vector2 mousePosition)
    {
        bool sameGate = eatenBy.Length > 0 && eatenBy == lastEatenBy;
        eatenInARow = sameGate ? eatenInARow + 1 : 1;
        eatenSpan = sameGate
            ? Rect.MinMaxRect(Mathf.Min(eatenSpan.xMin, mousePosition.x), Mathf.Min(eatenSpan.yMin, mousePosition.y), Mathf.Max(eatenSpan.xMax, mousePosition.x), Mathf.Max(eatenSpan.yMax, mousePosition.y))
            : new Rect(mousePosition, Vector2.zero);
        lastEatenBy = eatenBy;
        bool spread = eatenSpan.width > Screen.width * 0.6f || eatenSpan.height > Screen.height * 0.6f;
        if (eatenBy.Length == 0 || eatenInARow < 3 || (eatenBy == "a window" && !spread))
        {
            return;
        }

        eatenInARow = int.MinValue;

        CommanderPlugin.Log.LogWarning(
            $"Input: three clicks in a row went to {eatenBy} at {mousePosition}; mapMaximized={DynamicMap.mapMaximized}, "
            + $"tacticalOpen={tacticalMapService.IsOpen}, fullscreen={tacticalMapService.IsFullscreenOpen}, "
            + $"typing={CommanderShortcutInput.IsTyping}, cursorFlags={CursorManager.GetFlags()}, placement={IsPlacementArmed()}.");
    }

    private void TickKeyboardOrders()
    {
        // Enter also sends a chat line and ends a preset rename, and either can finish before this
        // runs, so the frame after typing still counts as typing for the buy key.
        bool typing = CommanderShortcutInput.IsTyping;
        bool wasTyping = typedLastFrame;
        typedLastFrame = typing;
        if (typing)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.StopOrder))
        {
            moveService.StopSelectedUnits();
        }

        if (economyService.AwaitingPlacement && CommanderShortcutInput.IsDown(CommanderSettings.RotateBuilding))
        {
            economyService.RotatePlacement(45f);
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.CancelBlueprint))
        {
            CancelBlueprintUnderCursor();
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.ClearSelection))
        {
            selectionService.DeselectAll();
        }

        // The game's own "Night Vis" binding also works here now (see CommanderNightVisionPatch);
        // if it shares this key it has already toggled this frame, and a second toggle would undo it.
        if (CommanderShortcutInput.IsDown(CommanderSettings.ToggleNightVision)
            && GameManager.playerInput?.GetButtonDown("Night Vis") != true)
        {
            NightVision.Toggle();
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.CycleIdleUnit)
            && selectionService.SelectNextIdleUnit())
        {
            CommanderCameraFollowService.Instance?.CenterOnSelection();
        }

        overlayUi.HandleWindowHotkeys();
        if (!wasTyping)
        {
            overlayUi.HandleBuyKey();
        }
    }

    /// <summary>The cancel key reads the cursor the way the build order does: map point over the map, else the world.</summary>
    private void CancelBlueprintUnderCursor()
    {
        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        bool overMap = map != null && DynamicMap.mapMaximized && map.IsCursorInMapRectangle();
        if (overMap ? map!.TryGetCursorCoordinates(out GlobalPosition point) : CommanderGameAccess.TryRaycastWorldPosition(Input.mousePosition, out point))
        {
            economyService.TryCancelBlueprintAt(point);
        }
    }

    /// <summary>A building cannot take an order, so a right click on its own sets its rally point.
    /// A ship or vehicle that carries a depot still moves.</summary>
    private bool IsOnlyDepotSelected()
    {
        return spawnService.SelectedDepot != null
            && selectionService.SelectedUnits.Count == 1
            && selectionService.SelectedUnits[0] is not (GroundVehicle or Ship);
    }

    private bool IsPlacementArmed()
    {
        return supplyHeliService.AwaitingTargetSelection
            || airCommandService.AwaitingAreaSelection
            || economyService.AwaitingPlacement
            || spawnService.AwaitingRallyPointSelection
            || CommanderNavalPurchaseService.Instance?.AwaitingRallySelection == true
            || CommanderSlingLiftService.Instance?.AwaitingSelection == true
            || CommanderAreaFire.AwaitingTarget;
    }

    private bool TryHandlePlacementClick(Vector2 mousePosition, bool overMap)
    {
        if (!IsPlacementArmed())
        {
            return false;
        }

        // Right-click backs out of an armed placement. Without it the only way out was finding the
        // CANCEL button again, which is the wrong instinct in an RTS.
        if (CommanderShortcutInput.IsDown(CommanderSettings.SecondaryAction))
        {
            supplyHeliService.CancelTargetSelection();
            airCommandService.CancelAreaSelection();
            economyService.CancelBuild();
            spawnService.CancelRallyPointSelection();
            CommanderNavalPurchaseService.Instance?.CancelRallySelection();
            CommanderSlingLiftService.Instance?.CancelSelection();
            CommanderAreaFire.Cancel();
            return true;
        }

        // Area fire is the one placement with no service watching the map, so it takes its map
        // click here, on the press like every world placement below.
        if (CommanderAreaFire.AwaitingTarget
            && CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction))
        {
            GlobalPosition areaCenter = default;
            bool placed = overMap
                ? SceneSingleton<DynamicMap>.i?.TryGetCursorCoordinates(out areaCenter) == true
                : CommanderGameAccess.TryRaycastWorldPosition(mousePosition, out areaCenter);
            if (placed)
            {
                CommanderAreaFire.Place(selectionService.SelectedUnits, areaCenter);
            }

            return true;
        }

        // Over the map, the click belongs to whichever service is watching the map — an air
        // mission area, a naval rally point — and never to the world raycast below, which would
        // read the terrain hidden behind the map canvas. Consumed either way, so nothing under the
        // map gets selected or ordered.
        if (overMap
            || !CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction)
            || overlayUi.ContainsScreenPoint(mousePosition))
        {
            return true;
        }

        CommanderSlingLiftService? slingLift = CommanderSlingLiftService.Instance;
        if (slingLift != null && slingLift.AwaitingSelection)
        {
            slingLift.TryHandleWorldClick(mousePosition);
        }
        else if (supplyHeliService.AwaitingTargetSelection)
        {
            supplyHeliService.TrySpawnAtWorldPoint(mousePosition);
        }
        else if (airCommandService.AwaitingAreaSelection)
        {
            airCommandService.TrySetAreaFromWorld(mousePosition);
        }
        else if (economyService.AwaitingPlacement)
        {
            economyService.TryPlaceBuildingFromWorld(mousePosition);
        }
        else if (CommanderNavalPurchaseService.Instance is { AwaitingRallySelection: true } naval)
        {
            naval.TrySetRallyFromWorld(mousePosition);
        }
        else
        {
            spawnService.TrySetRallyPointFromWorld(mousePosition);
        }

        return true;
    }

    private void HandlePrimaryClick(Vector2 mousePosition)
    {
        bool additive = CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection);

        Unit? clicked = null;
        if (markerService.TryGetMarkerUnitAt(mousePosition, out Unit markerUnit))
        {
            clicked = markerUnit;
        }
        else if (CommanderGameAccess.TryRaycastSelectableUnit(mousePosition, out Unit worldUnit))
        {
            clicked = worldUnit;
        }

        if (clicked == null)
        {
            lastClickedUnit = null;
            if (!additive)
            {
                selectionService.DeselectAll();
            }

            return;
        }

        bool doubleClick = ReferenceEquals(clicked, lastClickedUnit)
            && Time.unscaledTime - lastClickAt <= DoubleClickSeconds;
        lastClickedUnit = clicked;
        lastClickAt = Time.unscaledTime;

        bool sameTypeModifier = CommanderShortcutInput.IsPressed(CommanderSettings.SelectSameType);
        if (sameTypeModifier || doubleClick)
        {
            // Double click takes the units you can actually see; the modifier takes every one
            // of that type the faction owns, wherever it is.
            selectionService.SelectAllOfType(clicked, additive, onScreenOnly: !sameTypeModifier);
            return;
        }

        if (additive && selectionService.IsSelected(clicked))
        {
            CommanderSelectionService.DeselectUnit(clicked);
            return;
        }

        selectionService.SelectUnit(clicked, additive);
    }

    /// <summary>
    /// Right click on the tactical or fullscreen map. A hostile icon becomes an attack order,
    /// a friendly one a guard order, and anything else a travel point.
    /// </summary>
    private void HandleMapOrder(DynamicMap dynamicMap)
    {
        if (!dynamicMap.TryGetCursorCoordinates(out GlobalPosition position))
        {
            return;
        }

        Unit? attackTarget = null;
        if (TryGetMapUnitUnderCursor(dynamicMap, out Unit hoveredUnit))
        {
            if (CommanderGameAccess.IsFriendlyUnit(hoveredUnit, CommanderGameAccess.GetLocalHq()))
            {
                if (CommanderSettings.GuardOrders && moveService.IssueGuardOrder(hoveredUnit))
                {
                    return;
                }
            }
            else
            {
                attackTarget = hoveredUnit;
            }
        }

        moveService.IssueOrderAt(position, attackTarget, CommanderShortcutInput.IsPressed(CommanderSettings.QueueWaypoint));
    }
}
