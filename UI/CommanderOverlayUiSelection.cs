using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    private static readonly string[] RecoveryButtons =
    {
        "TRANSPORT",
        "RESCUE TEAM",
        "RESCUE ENEMY",
        "RESCUE BOTH",
    };

    /// <summary>
    /// True when the one selected unit is a supply depot, i.e. carries the base game's
    /// <c>Rearmer</c>. Read from the layout pass, so it stays a component lookup and nothing more.
    /// </summary>
    internal bool IsSupplyDepotSelected => selectionService.SelectedUnits.Count == 1
        && CommanderSupplyStoreService.IsSupplyStore(selectionService.FocusedSelection);

    /// <summary>True when the one selected unit is a building this commander put down.</summary>
    internal bool IsStructureSelected => selectionService.SelectedUnits.Count == 1
        && CommanderEconomyService.IsCommanderBuilt(selectionService.FocusedSelection);

    /// <summary>
    /// The hangar or landing pad the player has clicked on, if it is one their faction holds. This
    /// is what puts a SPAWN AIRCRAFT button on a specific set of doors instead of on the airbase.
    /// </summary>
    internal Hangar? SelectedHangar
    {
        get
        {
            if (!CommanderFeatureGate.AdvancedFeaturesEnabled || selectionService.SelectedUnits.Count != 1)
            {
                return null;
            }

            Unit? unit = selectionService.FocusedSelection;
            Hangar? hangar = CommanderGameAccess.GetHangar(unit);
            return hangar != null && CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq())
                ? hangar
                : null;
        }
    }

    /// <summary>True when the one selected unit is an aircraft of ours with droppable cargo aboard.</summary>
    internal bool IsCargoRunSelected => CommanderFeatureGate.AdvancedFeaturesEnabled
        && selectionService.SelectedUnits.Count == 1
        && CommanderSupplyHeliService.CanSetLz(selectionService.FocusedSelection);

    /// <summary>True when the one selected unit is a helicopter of ours carrying the cargo hook.</summary>
    internal bool IsSlingHelicopterSelected => CommanderFeatureGate.AdvancedFeaturesEnabled
        && selectionService.SelectedUnits.Count == 1
        && CommanderSlingLiftService.CanLift(selectionService.FocusedSelection);

    /// <summary>True when the selection holds an aircraft of ours, which gets the altitude slider.</summary>
    internal bool HasAltitudeRow => CommanderFeatureGate.AdvancedFeaturesEnabled && moveService.HasSelectedAircraft;

    internal float LoadoutHeight =>
        loadoutRows.Count > 0 ? ((loadoutRows.Count + 1) / 2 * LoadoutRowHeight) + 4f : 0f;

    /// <summary>True when the one selected unit is a vehicle depot of ours (a naval dock is not).</summary>
    internal bool IsVehicleDepotSelected => selectionService.SelectedUnits.Count == 1
        && spawnService.SelectedDepot != null;

    /// <summary>
    /// The selection bar zooms about the bottom centre of the bar and its portrait, so it stays
    /// docked while it grows or shrinks. Its layout stays in unscaled units; hit tests go through
    /// <see cref="ToSelectionSpace"/>.
    /// </summary>
    private Vector2 SelectionPivot => new((selectionPortraitRect.x + selectionBarRect.xMax) * 0.5f, selectionBarRect.yMax);

    /// <summary>Top of the per-unit detail block: below the header, below the loadout rows.</summary>
    private float ContentY => selectionBarRect.y + 68f + LoadoutHeight;

    private static string GetStanceLabel(CommanderStance stance)
    {
        return stance switch
        {
            CommanderStance.HoldFire => "HOLD FIRE",
            CommanderStance.HoldPosition => "HOLD POS",
            _ => "FREE FIRE",
        };
    }

    private static string GetWaypointActionLabel(CommanderWaypointAction action)
    {
        return action switch
        {
            CommanderWaypointAction.Hold => $"NEXT WP: HOLD {CommanderSettings.WaypointHoldSeconds:0}s",
            CommanderWaypointAction.RadarOff => "NEXT WP: RADAR OFF",
            CommanderWaypointAction.RadarOn => "NEXT WP: RADAR ON",
            _ => "NEXT WP: NO ACTION",
        };
    }

    /// <summary>
    /// A ballistic missile launcher holds rearmed rounds outside its magazine until its 300 s reload
    /// ends, so a just-rearmed one read 0 / 2 as if nothing arrived. Only those: a vehicle depot
    /// reads as reloading too.
    /// </summary>
    private static bool IsReloading(Unit unit)
    {
        List<WeaponStation> stations = unit.weaponStations;
        for (int i = 0; unit is GroundVehicle && stations != null && i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            if (station?.WeaponInfo != null
                && station.WeaponInfo.massPerRound >= CommanderSupplyStoreService.StrategicRoundKg
                && station.GetAmmoTotal() > station.GetAmmoLoaded())
            {
                return true;
            }
        }

        return false;
    }

    private static string GetWeaponName(WeaponInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.weaponName))
        {
            return info.weaponName;
        }

        return string.IsNullOrWhiteSpace(info.shortName) ? "Weapon" : info.shortName;
    }

    private static void DrawSupplyBar(Rect rect, string label, float fraction)
    {
        // A caption and a thin gauge under it, the same bar the portrait's condition uses.
        GUI.Label(new Rect(rect.x, rect.y, rect.width, rect.height - 6f), $"{label}  {fraction * 100f:0}%", CommanderUiTheme.Caption);
        Color fill = fraction <= 0.25f ? CommanderUiTheme.StatusColor(fraction <= 0.01f ? 0f : 0.5f) : CommanderUiTheme.StatusColor(1f);
        CommanderUiTheme.DrawBar(new Rect(rect.x, rect.yMax - 5f, rect.width, 4f), fraction, fill);
    }

    /// <summary>
    /// How much one BUY delivers, any whole percentage of a shelf from 1 to 100. The price beside it
    /// moves with it and a kilogram costs the same however it is set. Stays enabled on a full
    /// depot: it is a setting, not a purchase.
    /// </summary>
    private static void DrawPurchaseSizeButton(Rect rect, bool enabled)
    {
        bool wasEnabled = GUI.enabled;
        GUI.enabled = enabled;
        GUI.Label(
            new Rect(rect.x, rect.y, 36f, rect.height),
            new GUIContent(
                CommanderSupplyStoreService.PurchaseSizeLabel,
                "How much of the shelf one BUY delivers, at the same price per kilogram. Drag to change."),
            CommanderUiTheme.MutedLabel);
        Rect slider = new(rect.x + 38f, rect.y + (rect.height * 0.5f) - 6f, rect.width - 38f, 12f);
        float picked = GUI.HorizontalSlider(slider, CommanderSupplyStoreService.PurchasePercent, 1f, 100f);
        if (!Mathf.Approximately(picked, CommanderSupplyStoreService.PurchasePercent))
        {
            CommanderSupplyStoreService.PurchasePercent = picked;
        }

        GUI.enabled = wasEnabled;
    }

    private static void DrawAutoBuy(CommanderSupplyStoreService stores, Unit depot, Rect rect, bool fuel)
    {
        bool on = stores.IsAutoBuying(depot, fuel);
        bool picked = CommanderUiTheme.Check(
            rect,
            on,
            new GUIContent(
                fuel ? "AUTO FUEL" : "AUTO AMMO",
                $"Keeps this depot's {(fuel ? "fuel" : "ammunition")} at the order size, buying whatever it falls short."),
            CommanderUiTheme.Toggle);
        if (picked != on)
        {
            stores.SetAutoBuy(depot, fuel, picked);
        }
    }

    /// <summary>
    /// One shelf of a selected store: the fill bar, the kilograms under it, and BUY at the chosen
    /// size beside BUY MAX (fill to the top), priced per kilogram alike.
    /// </summary>
    private static void DrawShelf(CommanderSupplyStoreService stores, Unit depot, Rect area, bool fuel, float haveKg, float shelfKg, bool owned, bool truck)
    {
        const float buttonWidth = 104f;
        float fraction = shelfKg > 0f ? haveKg / shelfKg : 0f;
        DrawSupplyBar(new Rect(area.x, area.y, area.width, 22f), fuel ? "FUEL" : "AMMO", fraction);
        float kgWidth = truck ? area.width : area.width - (buttonWidth * 2f) - 8f;

        // Wedged beside the buttons: a wrapped second line would be centred and cut in half.
        CommanderUiTheme.FitLabel(
            new Rect(area.x + 2f, area.y + 27f, kgWidth, 22f),
            $"{haveKg:#,0} / {shelfKg:#,0} kg",
            CommanderUiTheme.MutedLabel);
        if (truck)
        {
            return;
        }

        float roomKg = shelfKg - haveKg;
        float price = fuel ? CommanderSettings.SupplyFuelPurchaseCost : CommanderSettings.SupplyAmmoPurchaseCost;

        // Fuel is priced per kilogram of the standard shelf, so a bunker's bigger tank is not a discount.
        float priceShelfKg = fuel ? CommanderSupplyStoreService.FuelCapacityKg : shelfKg;
        float perKg = price / Mathf.Max(1f, priceShelfKg * CommanderSettings.SupplyPurchaseFraction);
        float maxCost = perKg * roomKg;
        float buyCost = perKg * shelfKg * CommanderSupplyStoreService.GetPurchaseFraction();
        string what = fuel ? "a tankful of fuel" : "a load of ammunition";
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && owned && roomKg > 1f;

        // The price is part of the label, so FitButton shrinks a long one rather than wrapping it.
        float buttonX = area.xMax - (buttonWidth * 2f) - 4f;
        bool buy = CommanderUiTheme.FitButton(
            new Rect(buttonX, area.y + 26f, buttonWidth, 26f),
            new GUIContent(
                $"BUY {CommanderEconomyService.FundsLabel(buyCost)}",
                $"Delivers {CommanderSupplyStoreService.PurchaseSizeLabel} of {what} to this depot."),
            CommanderUiTheme.Button);
        bool max = CommanderUiTheme.FitButton(
            new Rect(buttonX + buttonWidth + 4f, area.y + 26f, buttonWidth, 26f),
            new GUIContent(
                $"MAX {CommanderEconomyService.FundsLabel(maxCost)}",
                $"Fills this depot's {(fuel ? "fuel" : "ammunition")} shelf to the top."),
            CommanderUiTheme.Button);
        GUI.enabled = oldEnabled;

        if (!buy && !max)
        {
            return;
        }

        float percent = max ? 100f : 0f;
        _ = fuel
            ? stores.TryBuyFuel(depot, out string message, percent)
            : stores.TryBuyAmmo(depot, out message, percent);

        // A refusal (short of funds) says so too; a guest's forwarded buy answers later, empty here.
        if (message.Length > 0)
        {
            CommanderAlertService.Instance?.NotifySupply(message, depot);
        }
    }

    /// <summary>
    /// One chip per unit type in the selection. Clicking a chip narrows the selection to that
    /// type; holding the add-selection key drops it, so a box that caught the wrong thing does
    /// not have to be redrawn from scratch.
    /// </summary>
    private void DrawSelectionChips()
    {
        RefreshSelectionChips();

        float x = selectionBarRect.x + 14f;
        float maxX = selectionBarRect.xMax - 14f;
        float y = selectionBarRect.y + 70f;
        GUIStyle style = CommanderUiTheme.Button;
        for (int i = 0; i < chipLabels.Count; i++)
        {
            float width = Mathf.Min(190f, style.CalcSize(new GUIContent(chipLabels[i])).x + 18f);
            if (x + width > maxX)
            {
                GUI.Label(new Rect(x, y, maxX - x, 30f), $"+{chipLabels.Count - i}", CommanderUiTheme.MutedLabel);
                return;
            }

            if (GUI.Button(new Rect(x, y, width, 30f), chipLabels[i], style))
            {
                if (CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection))
                {
                    selectionService.RemoveType(chipTypes[i]);
                }
                else
                {
                    selectionService.KeepOnlyType(chipTypes[i]);
                }

                return;
            }

            x += width + 6f;
        }
    }

    private void RefreshSelectionChips()
    {
        // Units that die out of the selection are pruned without bumping the revision, so the
        // count is checked too or the chips would keep advertising a destroyed vehicle.
        if (chipRevision == selectionService.SelectionRevision
            && chipCount == selectionService.SelectedUnits.Count)
        {
            return;
        }

        chipRevision = selectionService.SelectionRevision;
        chipCount = selectionService.SelectedUnits.Count;
        chipTypes.Clear();
        chipCounts.Clear();
        chipLabels.Clear();
        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            string typeKey = CommanderSelectionService.GetTypeKey(units[i]);
            int index = chipTypes.IndexOf(typeKey);
            if (index < 0)
            {
                chipTypes.Add(typeKey);
                chipCounts.Add(1);
            }
            else
            {
                chipCounts[index]++;
            }
        }

        for (int i = 0; i < chipTypes.Count; i++)
        {
            chipLabels.Add(chipCounts[i] > 1 ? $"{chipTypes[i]} x{chipCounts[i]}" : chipTypes[i]);
        }
    }

    private Vector2 ToSelectionSpace(Vector2 guiPoint)
    {
        Vector2 pivot = SelectionPivot;
        return pivot + ((guiPoint - pivot) / CommanderWindowScale.Get(SelectionBarScaleId));
    }

    private void DrawSelectionBar()
    {
        int count = selectionService.SelectedUnits.Count;
        if (count == 0)
        {
            return;
        }

        float scale = CommanderWindowScale.Get(SelectionBarScaleId);
        Vector3 pivot = SelectionPivot;
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = previous
            * Matrix4x4.TRS(pivot, Quaternion.identity, new Vector3(scale, scale, 1f))
            * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
        try
        {
            DrawSelectionBarContent(count);
        }
        finally
        {
            GUI.matrix = previous;
        }
    }

    private void DrawSelectionBarContent(int count)
    {
        GUI.Box(selectionBarRect, string.Empty, CommanderUiTheme.Panel);
        DrawSelectionPortrait(count);
        GUI.Label(new Rect(selectionBarRect.x + 12f, selectionBarRect.y + 3f, 150f, 24f), "SELECTION", CommanderUiTheme.Caption);

        // One line only: a wrapped status is centred on the row and cut in half.
        CommanderUiTheme.FitLineLabel(
            new Rect(selectionBarRect.x + 166f, selectionBarRect.y + 3f, selectionBarRect.width - 210f, 24f),
            GetSelectionCardText(),
            selectionCardWarning ? CommanderUiTheme.WarningLabel : CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(selectionBarRect.xMax - 34f, selectionBarRect.y + 3f, 26f, 22f), "?", CommanderUiTheme.HelpButton))
        {
            selectionHelpVisible = !selectionHelpVisible;
        }

        Unit? focused = selectionService.FocusedSelection;
        string label = count == 1 && focused != null
            ? CommanderGameAccess.GetUnitLabel(focused)
            : $"{count} UNITS SELECTED";
        int group = groupService.GetGroupOf(focused);
        if (group > 0)
        {
            label = $"GROUP {group}  |  {label}";
        }

        float upkeep = count == 1 && focused != null ? CommanderEconomyService.GetUpkeepPerMinute(focused) : 0f;
        if (upkeep > 0f)
        {
            label += $"  |  UPKEEP {CommanderEconomyService.FundsLabel(upkeep)}/min";
        }

        GUI.Label(new Rect(selectionBarRect.x + 14f, selectionBarRect.y + 37f, selectionBarRect.width - 200f, 24f), label, CommanderUiTheme.Header);
        if (count > 1)
        {
            DrawSelectionChips();
        }
        else
        {
            DrawLoadout();
            DrawSupplyStore();
            DrawStructureRow();
            DrawHangarRow();
            DrawDepotRow();
            DrawCargoRunRow();
            DrawSlingRow();
        }

        bool oldEnabled = GUI.enabled;
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        GUI.enabled = oldEnabled && moveService.HasCommandableSelection;
        if (GUI.Button(
            new Rect(selectionBarRect.xMax - 176f, selectionBarRect.y + 32f, 78f, 34f),
            new GUIContent("STOP", $"Cancels orders and holds the selection where it stands ({CommanderSettings.StopOrder})."),
            CommanderUiTheme.Button))
        {
            moveService.StopSelectedUnits();
        }

        GUI.enabled = oldEnabled;
        if (GUI.Button(
            new Rect(selectionBarRect.xMax - 92f, selectionBarRect.y + 32f, 78f, 34f),
            new GUIContent(
                selectionMoreVisible ? "MORE  ^" : "MORE  v",
                "Less-used orders: hand back to the game's AI, road driving, pin, an action for the next travel point, and radar on or off."),
            selectionMoreVisible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            selectionMoreVisible = !selectionMoreVisible;
        }

        DrawOrderRow(oldEnabled);
        DrawAltitudeRow();
        if (selectionMoreVisible)
        {
            DrawMoreRow(oldEnabled, advanced);
        }

        if (selectionHelpVisible)
        {
            // Grows upward to fit, above the MORE row when that is open.
            const string help = "Hover any button to see what it does; MORE holds the less-used orders. The type chips narrow a mixed selection to one type; hold the add-selection key to drop that type instead. What else shows here depends on what you select: a supply depot shows its fuel and ammunition with BUY buttons, a building you put up shows its level, UPGRADE and DESTROY, a hangar or landing pad shows SPAWN AIRCRAFT HERE, a vehicle depot shows SET RALLY POINT (click it, then click where new units should gather), an aircraft shows its ALTITUDE slider, a cargo aircraft shows SET LZ and LAND / FLY-BY, and a helicopter with the cargo hook shows SLING LIFT.";
            float height = CommanderUiTheme.Label.CalcHeight(new GUIContent(help), selectionBarRect.width - 20f) + 16f;
            float bottom = selectionMoreVisible ? selectionMoreRect.y : selectionBarRect.y;
            selectionHelpRect = new Rect(selectionBarRect.x, bottom - height - 8f, selectionBarRect.width, height);
            CommanderUiTheme.DrawHelpOverlay(selectionHelpRect, help);
        }

        // Drag to resize, right-click for 100 %, like every window's corner.
        CommanderWindowScale.Grip(
            SelectionBarScaleId,
            new Rect(selectionBarRect.xMax - 12f, selectionBarRect.yMax - 12f, 10f, 10f),
            selectionBarRect.size);
        Vector2 screenMin = ToSelectionSpace(Vector2.zero);
        Vector2 screenMax = ToSelectionSpace(new Vector2(CommanderUiScale.Width, CommanderUiScale.Height));
        CommanderUiTheme.DrawTooltip(Rect.MinMaxRect(screenMin.x, screenMin.y, screenMax.x, screenMax.y));
    }

    /// <summary>The focused unit's turning model, with the head count of a group under it.</summary>
    private void DrawSelectionPortrait(int count)
    {
        GUI.Box(selectionPortraitRect, string.Empty, CommanderUiTheme.Inset);
        Unit? focused = selectionService.FocusedSelection;
        if (focused != null)
        {
            CommanderUnitPreview.DrawLive(
                new Rect(selectionPortraitRect.x + 1f, selectionPortraitRect.y + 1f, selectionPortraitRect.width - 2f, selectionPortraitRect.height - 2f),
                focused.definition);
        }

        float condition = focused != null ? CommanderGameAccess.GetUnitCondition(focused) : 0f;
        Rect bar = new(selectionPortraitRect.x + 8f, selectionPortraitRect.yMax - 10f, selectionPortraitRect.width - 16f, 4f);
        CommanderUiTheme.DrawBar(bar, condition, CommanderUiTheme.StatusColor(condition));
        if (count > 1)
        {
            GUI.Label(new Rect(selectionPortraitRect.x + 8f, selectionPortraitRect.y + 4f, 80f, 22f), $"x{count}", CommanderUiTheme.Figure);
        }
    }

    /// <summary>The orders most players rarely need, one click away instead of always on screen.</summary>
    private void DrawMoreRow(bool oldEnabled, bool advanced)
    {
        GUI.Box(selectionMoreRect, string.Empty, CommanderUiTheme.Panel);

        // A building cannot travel, so it has no next travel point to put an action on.
        // NEXT WP gets a column and a half: "NEXT WP: HOLD 30s" in a plain column wrapped onto a
        // second line, and a button too short for two lines shows the top and bottom halves of each.
        bool movable = moveService.HasCommandableSelection;
        bool radarSam = radarService.TryGetSelectionIgnoreArtillery(out bool ignoreArtillery);
        float columns = (movable ? 5.5f : 4f) + (radarSam ? 1f : 0f);
        float gaps = (movable ? 4f : 3f) + (radarSam ? 1f : 0f);
        float width = (selectionMoreRect.width - 28f - (gaps * 6f)) / columns;
        float x = selectionMoreRect.x + 14f;
        float y = selectionMoreRect.y + 5f;

        GUI.enabled = oldEnabled && advanced && moveService.HasCommandableSelection;
        if (CommanderUiTheme.FitWordButton(
            new Rect(x, y, width, 32f),
            new GUIContent("AI CONTROL", "Hands the selection back to the game's own AI, which picks its own moves and targets."),
            CommanderUiTheme.Button))
        {
            moveService.ResumeAiForSelectedUnits();
        }

        x += width + 6f;

        // A selected depot sets the choice for every vehicle it builds from now on.
        CommanderSpawnService.DepotSpawnQueue? depotQueue = spawnService.GetSelectedQueue();
        bool canToggleRoad = advanced && (depotQueue != null || directPathService.HasConfigurableSelection());
        bool roadEnabled = depotQueue != null ? !depotQueue.OffRoad : !directPathService.IsSelectionOffRoad();
        string roadTip = depotQueue != null
            ? "Whether the vehicles this depot builds keep to the roads or drive straight across country."
            : "Whether the selected ground vehicles keep to the roads or drive straight across country.";
        GUI.enabled = oldEnabled && canToggleRoad;
        if (CommanderUiTheme.FitWordButton(
            new Rect(x, y, width, 32f),
            new GUIContent(roadEnabled ? "ROADS: ON" : "ROADS: OFF", roadTip),
            roadEnabled ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            if (depotQueue != null)
            {
                depotQueue.OffRoad = !depotQueue.OffRoad;
            }
            else
            {
                directPathService.ToggleSelection();
            }
        }

        x += width + 6f;

        GUI.enabled = oldEnabled && advanced;
        if (CommanderUiTheme.FitWordButton(
            new Rect(x, y, width, 32f),
            new GUIContent(
                selectionService.IsCurrentSelectionPinned ? "UNPIN" : "PIN",
                "Keeps the selection in the UNIT LIST window, one click from selecting it again."),
            CommanderUiTheme.Button))
        {
            selectionService.TogglePinSelected();
        }

        x += width + 6f;

        if (movable)
        {
            GUI.enabled = oldEnabled;
            CommanderWaypointAction action = moveService.PendingWaypointAction;
            if (CommanderUiTheme.FitWordButton(
                new Rect(x, y, width * 1.5f, 32f),
                new GUIContent(
                    GetWaypointActionLabel(action) + "  >",
                    "Click to cycle the action for the NEXT travel point you place: hold there, radar off (drive in dark), radar on."),
                action == CommanderWaypointAction.None ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
            {
                moveService.CyclePendingWaypointAction();
            }

            x += (width * 1.5f) + 6f;
        }

        // Like ROADS, a selected depot sets the radar of every vehicle it builds from now on.
        bool hasRadar = radarService.TryGetSelectionRadar(out bool radarOn);
        if (depotQueue != null)
        {
            hasRadar = true;
            radarOn = !depotQueue.RadarOff;
        }

        string radarTip = depotQueue != null
            ? "Whether the vehicles this depot builds roll out with their radar on or off. Off, anti-radiation missiles cannot home on them."
            : "Switches the radar of every selected unit and building on or off. Off, anti-radiation missiles cannot home on them, but they see nothing on radar either.";
        GUI.enabled = oldEnabled && hasRadar;
        if (CommanderUiTheme.FitWordButton(
            new Rect(x, y, width, 32f),
            new GUIContent(!hasRadar ? "NO RADAR" : radarOn ? "RADAR: ON" : "RADAR: OFF", radarTip),
            hasRadar && !radarOn ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            if (depotQueue != null)
            {
                depotQueue.RadarOff = !depotQueue.RadarOff;
            }
            else
            {
                radarService.ToggleSelectionRadar();
            }
        }

        if (radarSam)
        {
            x += width + 6f;
            GUI.enabled = oldEnabled;
            if (CommanderUiTheme.FitWordButton(
                new Rect(x, y, width, 32f),
                new GUIContent(
                    "IGNORE ARTY",
                    "The selected radar SAMs let artillery shells and rockets through and keep their missiles for aircraft and guided missiles."),
                ignoreArtillery ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                radarService.ToggleSelectionIgnoreArtillery();
            }
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>
    /// One thin line just above the order row, the same slider as AIR MISSIONS. Every selected
    /// aircraft goes to the height picked, on a mission or on autopilot; AUTO leaves it to the game.
    /// </summary>
    private void DrawAltitudeRow()
    {
        CommanderAirCommandService? air = CommanderAirCommandService.Instance;
        if (!HasAltitudeRow || air == null)
        {
            return;
        }

        IReadOnlyList<Unit> selected = selectionService.SelectedUnits;
        Aircraft? lead = null;
        for (int i = 0; lead == null && i < selected.Count; i++)
        {
            if (selected[i] is Aircraft aircraft && CommanderAirCommandService.IsTaskableAircraft(aircraft))
            {
                lead = aircraft;
            }
        }

        if (lead == null)
        {
            return;
        }

        const float labelWidth = 70f;
        const float valueWidth = 80f;
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;
        float y = selectionBarRect.yMax - 62f;
        float altitude = air.GetMissionAltitude(lead);
        GUI.Label(
            new Rect(x, y, labelWidth, 20f),
            new GUIContent("ALTITUDE", "The height to cruise at, from LOW LEVEL up. AUTO leaves it to the game. Drag to change it at any time; it applies at once."),
            CommanderUiTheme.MutedLabel);
        float delta = CommanderAirCommandUi.AltitudeSlider(
            new Rect(x + labelWidth, y + 4f, width - labelWidth - valueWidth - 6f, 12f),
            altitude);
        CommanderUiTheme.FitLabel(
            new Rect(x + width - valueWidth, y, valueWidth, 20f),
            CommanderAirCommandService.FormatTargetAltitude(altitude),
            CommanderUiTheme.MutedLabel);
        if (delta == 0f)
        {
            return;
        }

        int picked = CommanderAirCommandService.AltitudeNotch(altitude)
            + Mathf.RoundToInt(delta / CommanderAirCommandService.TargetAltitudeStepMeters);
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] is Aircraft aircraft && CommanderAirCommandService.IsTaskableAircraft(aircraft))
            {
                int notch = CommanderAirCommandService.AltitudeNotch(air.GetMissionAltitude(aircraft));
                if (notch != picked)
                {
                    air.StepMissionAltitude(aircraft, (picked - notch) * CommanderAirCommandService.TargetAltitudeStepMeters);
                }
            }
        }
    }

    /// <summary>AREA FIRE's order row: the diameter slider, a button to place or move the area, and DELETE.</summary>
    private void DrawAreaFireRow(bool oldEnabled, float rowY, float buttonWidth)
    {
        IReadOnlyList<Unit> selected = selectionService.SelectedUnits;
        float left = selectionBarRect.x + 14f;
        float right = selectionBarRect.xMax - 14f;
        Rect deleteRect = new(right - buttonWidth, rowY, buttonWidth, 34f);
        Rect placeRect = new(deleteRect.x - 6f - 110f, rowY, 110f, 34f);

        GUI.enabled = oldEnabled;
        float diameter = CommanderAreaFire.GetSelectionDiameter(selected);
        GUI.Label(
            new Rect(left, rowY, 150f, 34f),
            new GUIContent($"AREA  {diameter:0} m", "Diameter of the area the guns shell. Drag to change; it applies at once and is kept for the next area."),
            CommanderUiTheme.Label);
        Rect slider = new(left + 156f, rowY + 11f, placeRect.x - 12f - (left + 156f), 12f);
        float picked = GUI.HorizontalSlider(slider, diameter, CommanderAreaFire.MinDiameterMeters, CommanderAreaFire.MaxDiameterMeters);
        if (!Mathf.Approximately(picked, diameter))
        {
            CommanderAreaFire.SetSelectionDiameter(selected, Mathf.Round(picked / 50f) * 50f);
        }

        bool armed = CommanderAreaFire.AwaitingTarget;
        if (GUI.Button(
            placeRect,
            new GUIContent(
                armed ? "CLICK AREA" : "MOVE AREA",
                armed ? "Click the map or the ground to place the area. Right-click cancels." : "Pick a new spot for the area, at the diameter set here."),
            armed ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            if (armed)
            {
                CommanderAreaFire.Cancel();
            }
            else
            {
                CommanderAreaFire.Arm();
            }
        }

        DrawDeleteButton(oldEnabled, deleteRect);
        GUI.enabled = oldEnabled;
    }

    /// <summary>Scraps the selection. Two clicks, because it takes the whole selection and there is
    /// no refund and no undo.</summary>
    private void DrawDeleteButton(bool oldEnabled, Rect rect)
    {
        bool deleteArmed = Time.unscaledTime <= deleteArmedUntil;
        GUI.enabled = oldEnabled && selectionService.CanDeleteSelection;
        if (GUI.Button(
            rect,
            new GUIContent(
                deleteArmed ? "CONFIRM" : "DELETE",
                "Scraps every selected unit. No refund. Click once to arm, again to confirm."),
            CommanderUiTheme.DangerButton))
        {
            if (deleteArmed)
            {
                selectionService.DeleteSelectedUnits();
                deleteArmedUntil = 0f;
            }
            else
            {
                deleteArmedUntil = Time.unscaledTime + 4f;
            }
        }
    }

    /// <summary>Patrol, stance, formation, retreat, and — only when the selection actually holds
    /// one — an aircraft's rearm run. The cycle buttons end in "&gt;" and say on hover what they cycle.</summary>
    private void DrawOrderRow(bool oldEnabled)
    {
        float rowY = selectionBarRect.yMax - 40f;

        // Guns shelling an area have nothing to do with patrols, formations or retreats: the row is
        // the area's diameter instead, and the same while the area is being placed.
        IReadOnlyList<Unit> selected = selectionService.SelectedUnits;
        if (CommanderAreaFire.AwaitingTarget || CommanderAreaFire.SelectionInAreaMode(selected))
        {
            DrawAreaFireRow(oldEnabled, rowY, 88f);
            return;
        }

        // The buttons share the bar's whole width, up to 120 each: at a fixed 88 "HOLD FIRE >",
        // "PATROL ON" and "CLICK AREA" wrapped onto a second line and were cut in half.
        bool hasAircraft = moveService.HasSelectedAircraft;
        bool hasArtillery = CommanderAreaFire.SelectionCanAreaFire(selectionService.SelectedUnits);
        float columns = 5f + (hasAircraft ? 2f : 0f) + (hasArtillery ? 1f : 0f);
        float buttonWidth = Mathf.Min(120f, (selectionBarRect.width - 28f - ((columns - 1f) * 6f)) / columns);
        float step = buttonWidth + 6f;
        float x = selectionBarRect.xMax - ((buttonWidth * columns) + ((columns - 1f) * 6f)) - 14f;
        bool commandable = moveService.HasCommandableSelection;

        GUI.enabled = oldEnabled && commandable && moveService.CanSelectionPatrol;
        bool patrolling = moveService.IsSelectionPatrolling;
        if (GUI.Button(
            new Rect(x, rowY, buttonWidth, 34f),
            new GUIContent(patrolling ? "PATROL ON" : "PATROL", "Turns a multi-point route into a loop the units keep driving while you are away."),
            patrolling ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            moveService.ToggleSelectionPatrol();
        }

        GUI.enabled = oldEnabled && (commandable || hasAircraft);
        CommanderStance stance = moveService.SelectionStance;
        if (GUI.Button(
            new Rect(x + step, rowY, buttonWidth, 34f),
            new GUIContent(
                GetStanceLabel(stance) + " >",
                "Stance, click to cycle. FREE FIRE shoots, and engages what it passes. HOLD FIRE: turrets stay quiet, and aircraft only shoot when you press FIRE. HOLD POS holds its ground but still shoots."),
            stance == CommanderStance.FreeEngage ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            moveService.CycleSelectionStance();
        }

        GUI.enabled = oldEnabled;
        if (GUI.Button(
            new Rect(x + (step * 2f), rowY, buttonWidth, 34f),
            new GUIContent(
                CommanderMoveService.Formation.ToString().ToUpperInvariant() + " >",
                "Formation, click to cycle: ring, line, column, wedge. Column is the one for roads."),
            CommanderUiTheme.Button))
        {
            CommanderMoveService.CycleFormation();
        }

        GUI.enabled = oldEnabled && commandable;
        if (GUI.Button(
            new Rect(x + (step * 3f), rowY, buttonWidth, 34f),
            new GUIContent("RETREAT", "Sends the selection to the nearest friendly vehicle that can repair or rearm it."),
            CommanderUiTheme.Button))
        {
            moveService.RetreatSelectedUnits();
        }

        GUI.enabled = oldEnabled;

        // Only drawn for a selection with aircraft in it: a permanently dead button is worse than a
        // row that changes width.
        if (hasAircraft
            && GUI.Button(
                new Rect(x + (step * 4f), rowY, buttonWidth, 34f),
                new GUIContent("RESUPPLY", "Sends the selected aircraft to the nearest airbase the faction holds, lands them, and refuels and rearms them out of a supply depot before they fly back out on the same mission. A depot with nothing left leaves them parked and says so."),
                CommanderUiTheme.Button))
        {
            moveService.ResupplySelectedAircraft();
        }

        if (hasAircraft
            && GUI.Button(
                new Rect(x + (step * 5f), rowY, buttonWidth, 34f),
                new GUIContent("RESERVE", "Sends the selected aircraft to the nearest airbase the faction holds and lands them, where the crew ejects and the aircraft goes back into reserve, ready to launch again. Aircraft already on that field do it where they stand."),
                CommanderUiTheme.Button))
        {
            moveService.ReserveSelectedAircraft();
        }

        float next = 4f + (hasAircraft ? 2f : 0f);
        if (hasArtillery)
        {
            bool armed = CommanderAreaFire.AwaitingTarget;
            if (GUI.Button(
                new Rect(x + (step * next), rowY, buttonWidth, 34f),
                new GUIContent(
                    armed ? "CLICK AREA" : "AREA FIRE",
                    "Click, then click the ground or the map: the selected artillery stops and shells that area until given another order. Anything hostile spotted inside it is targeted first. Unguided shells and rockets only. Right-click cancels."),
                armed ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                if (armed)
                {
                    CommanderAreaFire.Cancel();
                }
                else
                {
                    CommanderAreaFire.Arm();
                }
            }

            next++;
        }

        // Scrapping a unit is a command like any other, so it sits in the order row for every unit
        // type.
        DrawDeleteButton(oldEnabled, new Rect(x + (step * next), rowY, buttonWidth, 34f));
        GUI.enabled = oldEnabled;
    }

    /// <summary>
    /// Condition, ammo and current order for the selection. Rebuilt a few times a second
    /// because OnGUI runs more than once per frame and the condition walk is not free.
    /// </summary>
    private string GetSelectionCardText()
    {
        if (!CommanderScheduler.IsDueRealtime(ref nextSelectionCardAt, 0.25f))
        {
            return selectionCardText;
        }

        int count = selectionService.SelectedUnits.Count;
        if (count == 0)
        {
            selectionCardText = string.Empty;
            return selectionCardText;
        }

        float condition = 0f;
        float ammo = 0f;
        int armedUnits = 0;
        float fuel = 0f;
        int fuelledUnits = 0;
        for (int i = 0; i < count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            condition += CommanderGameAccess.GetUnitCondition(unit);
            float unitAmmo = CommanderGameAccess.GetUnitAmmo(unit);
            if (unitAmmo >= 0f)
            {
                ammo += unitAmmo;
                armedUnits++;
            }

            float unitFuel = CommanderGameAccess.GetUnitFuel(unit);
            if (unitFuel >= 0f)
            {
                fuel += unitFuel;
                fuelledUnits++;
            }
        }

        string ammoText = armedUnits > 0 ? $"   AMMO {ammo / armedUnits * 100f:0}%" : string.Empty;
        string fuelText = fuelledUnits > 0 ? $"   FUEL {fuel / fuelledUnits * 100f:0}%" : string.Empty;
        string orderText = string.Empty;
        string serviceText = string.Empty;
        Unit? focused = selectionService.FocusedSelection;
        if (count == 1 && focused != null)
        {
            string order = moveService.GetOrderLabel(focused);
            orderText = order.Length > 0 ? $"   |   {order}" : string.Empty;
            if (focused is Aircraft aircraft)
            {
                serviceText = CommanderAirCommandService.Instance?.GetServiceStatus(aircraft) ?? string.Empty;
                FlareEjector[] ejectors = aircraft.GetComponentsInChildren<FlareEjector>();
                if (ejectors.Length > 0)
                {
                    int flares = 0;
                    foreach (FlareEjector ejector in ejectors)
                    {
                        flares += ejector.GetAmmo();
                    }

                    fuelText += $"   FLARE : {flares}";
                }
            }
            else if (IsReloading(focused))
            {
                serviceText = "RELOADING";
            }
        }

        // Refuelling, grounded or low on fuel leads the card, in the warning colour.
        selectionCardWarning = serviceText.Length > 0;
        selectionCardText = (selectionCardWarning ? serviceText + "   |   " : string.Empty)
            + $"COND {condition / count * 100f:0}%{ammoText}{fuelText}{orderText}";
        return selectionCardText;
    }

    /// <summary>
    /// Rebuilds the per-weapon rows for a single selected unit. Called from Tick, not from OnGUI,
    /// because the bar's height is laid out from the row count before anything is drawn.
    /// </summary>
    private void RefreshLoadout()
    {
        Unit? focused = selectionService.SelectedUnits.Count == 1 ? selectionService.FocusedSelection : null;
        if (focused == null)
        {
            loadoutUnit = null;
            loadoutRows.Clear();
            return;
        }

        // Rounds change on every shot, so this is polled rather than event driven, but the walk
        // touches every weapon on the unit - a few times a second is enough for a readout.
        bool sameUnit = ReferenceEquals(focused, loadoutUnit);
        if (sameUnit && !CommanderScheduler.IsDueRealtime(ref nextLoadoutAt, 0.25f))
        {
            return;
        }

        loadoutUnit = focused;
        loadoutRows.Clear();
        loadoutNames.Clear();
        loadoutAmmo.Clear();
        loadoutFullAmmo.Clear();
        loadoutFireKeys.Clear();
        List<WeaponStation> stations = focused.weaponStations;
        if (stations == null)
        {
            return;
        }

        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (station == null || info == null || info.hideInDisplay || station.FullAmmo <= 0)
            {
                continue;
            }

            // One airframe carries the same missile on several stations; merging by name keeps
            // the readout to one row per weapon type instead of one row per pylon. Capped at full
            // because a Laser keeps a serialized ammo of 1000 against a GetFullAmmo of 1.
            string name = GetWeaponName(info);
            int index = loadoutNames.IndexOf(name);
            if (index < 0)
            {
                loadoutNames.Add(name);
                loadoutAmmo.Add(Mathf.Min(station.Ammo, station.FullAmmo));
                loadoutFullAmmo.Add(station.FullAmmo);
                loadoutFireKeys.Add(CommanderFireNow.GetFireKey(info));
            }
            else
            {
                loadoutAmmo[index] += Mathf.Min(station.Ammo, station.FullAmmo);
                loadoutFullAmmo[index] += station.FullAmmo;
            }
        }

        for (int i = 0; i < loadoutNames.Count; i++)
        {
            loadoutRows.Add($"{loadoutNames[i]}   {loadoutAmmo[i]} / {loadoutFullAmmo[i]}");
        }
    }

    /// <summary>
    /// Fuel and ammunition left in the selected depot, one BUY size for both shelves, and a BUY and
    /// BUY MAX per shelf. Every resupply either commander orders is paid out of these two bars, so
    /// this is where a player finds out why an aircraft is sitting on a runway instead of flying.
    /// A supply truck shows its load and no buttons: it fills up at a bunker.
    /// </summary>
    private void DrawSupplyStore()
    {
        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        Unit? depot = selectionService.FocusedSelection;
        if (stores == null || !IsSupplyDepotSelected || depot == null)
        {
            return;
        }

        float y = ContentY;
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;
        bool owned = CommanderGameAccess.IsFriendlyUnit(depot, CommanderGameAccess.GetLocalHq());
        bool truck = CommanderSupplyStoreService.IsTruckFed(depot);
        Rearmer? rearmer = CommanderSupplyStoreService.GetRearmer(depot);

        // Line one: how much a BUY delivers, shared by both shelves. A truck or a dump has no BUY,
        // so the line says how it does fill up instead.
        if (CommanderSupplyStoreService.IsDump(depot))
        {
            GUI.Label(
                new Rect(x, y, width, 22f),
                new GUIContent(
                    "STOCK: right-click this dump with a munitions or fuel truck selected.",
                    "An ammo dump cannot be bought into. A supply truck sent here unloads what it carries."),
                CommanderUiTheme.MutedLabel);
        }
        else if (truck)
        {
            const float loadWidth = 150f;
            GUI.Label(
                new Rect(x, y, width - loadWidth - 8f, 22f),
                new GUIContent(
                    "REFILL: right-click a munitions bunker or fuel depot. A dump is unloaded into.",
                    "A supply truck cannot be bought into. It drives to the building and fills up from that building's stock, or unloads into an ammo dump."),
                CommanderUiTheme.MutedLabel);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && owned;
            if (CommanderUiTheme.FitButton(
                new Rect(x + width - loadWidth, y - 1f, loadWidth, 24f),
                new GUIContent("LOAD FROM DUMP", "Takes stock back out of the nearest friendly ammo dump within 300 m."),
                CommanderUiTheme.Button))
            {
                stores.TryLoadFromDump(depot, out string message);
                if (message.Length > 0)
                {
                    CommanderAlertService.Instance?.NotifySupply(message, depot);
                }
            }

            GUI.enabled = oldEnabled;
        }
        else
        {
            GUI.Label(new Rect(x, y, 96f, 22f), "ORDER SIZE", CommanderUiTheme.MutedLabel);
            float sliderWidth = Mathf.Clamp(width - 100f - 240f, 80f, 260f);
            DrawPurchaseSizeButton(new Rect(x + 100f, y - 1f, sliderWidth, 24f), GUI.enabled && owned);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && owned;
            float checkX = x + 100f + sliderWidth + 12f;
            DrawAutoBuy(stores, depot, new Rect(checkX, y - 4f, 112f, 30f), fuel: true);
            if (rearmer != null)
            {
                DrawAutoBuy(stores, depot, new Rect(checkX + 116f, y - 4f, 112f, 30f), fuel: false);
            }

            GUI.enabled = oldEnabled;
        }

        // Lines two and three: one column per shelf, the bar with its percentage, and under it the
        // kilograms and the two buttons.
        y += 30f;
        float column = rearmer != null ? (width - 16f) * 0.5f : width;
        DrawShelf(stores, depot, new Rect(x, y, column, 52f), fuel: true, stores.GetFuelKg(depot), CommanderSupplyStoreService.GetFuelCapacityKg(depot), owned, truck);
        if (rearmer != null)
        {
            DrawShelf(stores, depot, new Rect(x + column + 16f, y, column, 52f), fuel: false, rearmer.Capacity, rearmer.GetMaxCapacity(), owned, truck);
        }
    }

    /// <summary>Two columns of "WEAPON  loaded / full" for the single selected unit.</summary>
    private void DrawLoadout()
    {
        if (loadoutRows.Count == 0)
        {
            return;
        }

        const float fireWidth = 52f;
        float columnWidth = (selectionBarRect.width - 34f) * 0.5f;
        float y = selectionBarRect.y + 68f;
        bool canFire = CommanderGameAccess.IsFriendlyUnit(loadoutUnit, CommanderGameAccess.GetLocalHq());
        for (int i = 0; i < loadoutRows.Count; i++)
        {
            float x = selectionBarRect.x + 14f + (i % 2 == 0 ? 0f : columnWidth + 6f);
            float rowY = y + (i / 2 * LoadoutRowHeight);
            bool hasFire = canFire && i < loadoutFireKeys.Count && loadoutFireKeys[i].Length > 0;
            GUI.Label(
                new Rect(x, rowY, hasFire ? columnWidth - fireWidth - 4f : columnWidth, LoadoutRowHeight),
                loadoutRows[i],
                CommanderUiTheme.MutedLabel);

            // RepeatButton reports true on every GUI pass while held. A gap of a few frames is a
            // new press and fires at once; holding on keeps firing until the rack is empty, with
            // the reloading and still-firing refusals in between kept quiet.
            if (hasFire
                && GUI.RepeatButton(
                    new Rect(x + columnWidth - fireWidth, rowY + 1f, fireWidth, LoadoutRowHeight - 2f),
                    new GUIContent("FIRE", "Fires this weapon now: at the unit's current target, or the nearest enemy it can reach. Hold it down to keep firing until this weapon is empty. An aircraft given several targets puts one round on each of them. A bomb drops on the target it was sent at, or on whatever is below. A scouting pod is sent wherever the unit is looking, in range or not."),
                    CommanderUiTheme.Button))
            {
                float now = Time.realtimeSinceStartup;
                bool held = now < fireHeldUntil;
                fireHeldUntil = now + 0.2f;
                if (!held)
                {
                    nextHeldFireAt = now + 0.4f;
                    CommanderFireNow.Request(loadoutUnit, loadoutFireKeys[i]);
                }
                else if (now >= nextHeldFireAt)
                {
                    nextHeldFireAt = now + 0.25f;
                    CommanderFireNow.Request(loadoutUnit, loadoutFireKeys[i], quiet: true);
                }
            }
        }
    }

    /// <summary>
    /// Level, upgrade and demolition for a building this commander put down — in the selection bar
    /// rather than a window of its own. A separate window holding one DESTROY button was a second
    /// thing to find and dismiss for every shed on the map.
    /// </summary>
    private void DrawStructureRow()
    {
        Unit? building = selectionService.FocusedSelection;
        if (!IsStructureSelected || building == null)
        {
            return;
        }

        float y = ContentY + (IsSupplyDepotSelected ? SupplyRowHeight : 0f);
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;
        float half = (width - 8f) * 0.5f;
        bool friendly = CommanderGameAccess.IsFriendlyUnit(building, CommanderGameAccess.GetLocalHq());
        bool oldEnabled = GUI.enabled;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float funds = hq != null ? hq.factionFunds : 0f;
        bool armed = ReferenceEquals(demolishArmedUnit, building);

        GUI.Label(
            new Rect(x, y, width, 22f),
            armed ? "NO REFUND. CLICK AGAIN TO LEVEL IT." : GetStructureSummary(building),
            armed ? CommanderUiTheme.Header : CommanderUiTheme.MutedLabel);
        y += 24f;

        if (economyService.IsIncomeBuilding(building))
        {
            DrawUpgradeButton(
                x,
                half,
                y,
                economyService.GetIncomeLevel(building),
                economyService.GetIncomeUpgradeCost(building),
                funds,
                friendly,
                oldEnabled,
                () => economyService.UpgradeIncome(building));
        }
        else if (economyService.IsBuiltNavalDock(building))
        {
            int level = economyService.GetNavalDockLevel(building);
            DrawUpgradeButton(
                x,
                half,
                y,
                level,
                CommanderEconomyService.GetNavalDockUpgradeCost(level),
                funds,
                friendly,
                oldEnabled,
                () => economyService.UpgradeNavalDock(building));
        }
        else if (building.TryGetComponent(out Factory factory) && factory.ProductionUnit != null)
        {
            int level = economyService.GetFactoryLevel(factory);
            DrawUpgradeButton(
                x,
                half,
                y,
                level,
                CommanderEconomyService.GetFactoryUpgradeCost(factory, level),
                funds,
                friendly,
                oldEnabled,
                () => economyService.UpgradeFactory(factory),
                CommanderEconomyService.GetFactoryMaxLevel(factory));
        }
        else
        {
            GUI.Label(new Rect(x, y, half, 36f), "NO UPGRADE", CommanderUiTheme.MutedLabel);
        }

        GUI.enabled = oldEnabled && friendly;
        if (GUI.Button(
            new Rect(x + half + 8f, y, half, 36f),
            armed ? "CONFIRM DEMOLITION" : "DESTROY BUILDING",
            CommanderUiTheme.DangerButton))
        {
            if (armed)
            {
                economyService.Demolish(building);
                demolishArmedUnit = null;
            }
            else
            {
                demolishArmedUnit = building;
            }
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>
    /// What a selected hangar or landing pad can put in the air, and the button that does it. The
    /// aircraft list is the hangar's own, so a pad offers helicopters and a hangar offers aeroplanes
    /// without this row knowing anything about aircraft types.
    /// </summary>
    private void DrawHangarRow()
    {
        Hangar? hangar = SelectedHangar;
        if (hangar == null)
        {
            return;
        }

        float y = ContentY
            + (IsSupplyDepotSelected ? SupplyRowHeight : 0f)
            + (IsStructureSelected ? StructureRowHeight : 0f);
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;
        bool disabled = hangar.Disabled;
        int types = 0;
        AircraftDefinition[] available = hangar.GetAvailableAircraft();
        for (int i = 0; i < available.Length; i++)
        {
            if (available[i] != null)
            {
                types++;
            }
        }

        string state = disabled ? "WRECKED" : hangar.Available ? "READY" : "DOORS BUSY";
        GUI.Label(
            new Rect(x, y, width, 22f),
            $"{(CommanderGameAccess.IsRotaryHangar(hangar) ? "LANDING PAD" : "HANGAR")}   {state}   |   {types} airframe type(s)",
            CommanderUiTheme.MutedLabel);
        y += 24f;

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !disabled && types > 0;
        if (GUI.Button(new Rect(x, y, width, 32f), "SPAWN AIRCRAFT HERE", CommanderUiTheme.PrimaryButton))
        {
            ShowAirCommandForHangar(hangar);
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>Where this depot's new units gather, and SET RALLY POINT: click it, then click the spot.</summary>
    private void DrawDepotRow()
    {
        if (!IsVehicleDepotSelected)
        {
            return;
        }

        float y = ContentY
            + (IsSupplyDepotSelected ? SupplyRowHeight : 0f)
            + (IsStructureSelected ? StructureRowHeight : 0f)
            + (SelectedHangar != null ? HangarRowHeight : 0f);
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;
        float half = (width - 8f) * 0.5f;

        const float autoWidth = 170f;
        bool autoAttack = spawnService.IsAutoAttackOn();
        GUI.Label(
            new Rect(x, y, width - autoWidth - 8f, 22f),
            autoAttack ? "NEW UNITS ATTACK:  nearest enemy base" : $"NEW UNITS GATHER:  {spawnService.GetRallyLabel()}",
            CommanderUiTheme.MutedLabel);
        if (GUI.Button(
            new Rect(x + width - autoWidth, y - 2f, autoWidth, 24f),
            new GUIContent(
                autoAttack ? "AUTO ATTACK: ON" : "AUTO ATTACK: OFF",
                "On: every new unit from this depot drives straight at the nearest enemy base and fights its way in. Off: new units wait at the rally point."),
            autoAttack ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            spawnService.ToggleAutoAttack();
        }

        y += 24f;

        bool armed = spawnService.AwaitingRallyPointSelection;
        if (GUI.Button(
            new Rect(x, y, half, 32f),
            new GUIContent(
                armed ? "CLICK THE SPOT  (CANCEL)" : "SET RALLY POINT",
                "Click this, then click where this depot's new units should drive to, in the 3D world or on the map. Right-click cancels."),
            armed ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            if (armed)
            {
                spawnService.CancelRallyPointSelection();
            }
            else
            {
                spawnService.BeginRallyPointSelection();
            }
        }

        if (GUI.Button(
            new Rect(x + half + 8f, y, half, 32f),
            new GUIContent("RESET RALLY POINT", "New units park on open ground beside the depot again."),
            CommanderUiTheme.Button))
        {
            spawnService.ClearRallyPoint();
        }

        y += 38f;

        Unit? depot = spawnService.SelectedDepot == null ? null : CommanderGameAccess.GetDepotOwner(spawnService.SelectedDepot);
        int level = CommanderEconomyService.GetDepotLevel(depot);
        if (level >= CommanderEconomyService.DepotMaxLevel)
        {
            GUI.Label(new Rect(x, y, width, 32f), $"DEPOT LEVEL {level}: every tier available.", CommanderUiTheme.MutedLabel);
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = CommanderEconomyService.GetDepotUpgradeCost(level);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && depot != null && hq != null && hq.factionFunds >= cost;
        if (GUI.Button(
            new Rect(x, y, width, 32f),
            new GUIContent(
                $"UPGRADE DEPOT TO LEVEL {level + 1}   {CommanderEconomyService.FundsLabel(cost)}",
                $"Level {level + 1} sells the tier {level + 1} vehicles. Until then they show as LOCKED in the purchase list."),
            CommanderUiTheme.PrimaryButton)
            && depot != null)
        {
            economyService.UpgradeDepot(depot);
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>
    /// The controls for a cargo run that is already in the air: where it is delivering, whether it
    /// lands or drops on the way past, and the button that moves the landing zone.
    /// </summary>
    private void DrawCargoRunRow()
    {
        Unit? unit = selectionService.FocusedSelection;
        if (!IsCargoRunSelected || unit == null)
        {
            return;
        }

        float y = ContentY
            + (IsSupplyDepotSelected ? SupplyRowHeight : 0f)
            + (IsStructureSelected ? StructureRowHeight : 0f)
            + (SelectedHangar != null ? HangarRowHeight : 0f);
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;
        float half = (width - 8f) * 0.5f;

        GUI.Label(new Rect(x, y, width, 22f), supplyHeliService.GetCargoRunLabel(unit), CommanderUiTheme.MutedLabel);
        y += 24f;

        bool oldEnabled = GUI.enabled;
        bool armed = supplyHeliService.AwaitingLzSelection;
        if (GUI.Button(
            new Rect(x, y, half, 32f),
            armed ? "CLICK THE NEW LZ" : "SET LZ",
            armed ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            if (armed)
            {
                supplyHeliService.CancelLzRetarget();
            }
            else
            {
                supplyHeliService.BeginLzRetarget(unit);
            }
        }

        // A fixed-wing transport has no landing option at all, so the toggle says so rather than
        // offering a choice that would be refused.
        bool canLand = CommanderSupplyHeliService.CargoRunCanLand(unit);
        bool airdrop = supplyHeliService.CargoRunIsAirdrop(unit);

        // Land or fly by belongs to a run, so with no LZ yet the button waits for SET LZ.
        GUI.enabled = oldEnabled && canLand && supplyHeliService.IsCargoRunAircraft(unit);
        if (GUI.Button(
            new Rect(x + half + 8f, y, half, 32f),
            !canLand ? "FLY-BY ONLY" : airdrop ? "FLY-BY DROP" : "LAND AT LZ",
            airdrop ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            supplyHeliService.ToggleCargoRunDelivery(unit);
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>SLING LIFT for a helicopter with the cargo hook: pick a vehicle, then where it goes.</summary>
    private void DrawSlingRow()
    {
        CommanderSlingLiftService? sling = CommanderSlingLiftService.Instance;
        if (!IsSlingHelicopterSelected || sling == null || selectionService.FocusedSelection is not Aircraft helo)
        {
            return;
        }

        float y = ContentY
            + (IsSupplyDepotSelected ? SupplyRowHeight : 0f)
            + (IsStructureSelected ? StructureRowHeight : 0f)
            + (SelectedHangar != null ? HangarRowHeight : 0f)
            + (IsCargoRunSelected ? CargoRunRowHeight : 0f);
        float x = selectionBarRect.x + 14f;
        float width = selectionBarRect.width - 28f;

        // RECOVERY: standing duty picking up downed pilots. On duty, it owns the hook.
        CommanderRecoveryService? recovery = CommanderRecoveryService.Instance;
        CommanderRecoveryService.RecoveryMode current = recovery?.GetMode(helo) ?? CommanderRecoveryService.RecoveryMode.Off;
        float quarter = (width - 12f) / 4f;
        for (int i = 0; i < RecoveryButtons.Length; i++)
        {
            CommanderRecoveryService.RecoveryMode mode = (CommanderRecoveryService.RecoveryMode)i;
            if (GUI.Button(
                new Rect(x + (i * (quarter + 4f)), y, quarter, 28f),
                RecoveryButtons[i],
                current == mode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                recovery?.SetMode(helo, mode);
            }
        }

        y += 32f;
        if (current != CommanderRecoveryService.RecoveryMode.Off)
        {
            GUI.Label(new Rect(x, y, width, 22f), recovery!.GetStatusLabel(helo), CommanderUiTheme.MutedLabel);
            return;
        }

        GUI.Label(new Rect(x, y, width, 22f), sling.GetRowLabel(helo), CommanderUiTheme.MutedLabel);
        y += 24f;

        if (!sling.IsLifting(helo))
        {
            bool armed = sling.IsArmedFor(helo);
            if (GUI.Button(
                new Rect(x, y, width, 32f),
                armed ? "CLICK THE VEHICLE (CANCEL)" : "SLING LIFT",
                armed ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
            {
                if (armed)
                {
                    sling.CancelSelection();
                }
                else
                {
                    sling.BeginSelection(helo);
                }
            }

            return;
        }

        // SET LZ is the cargo run's button: the lift starts without one, and the LZ can come any time.
        float half = (width - 8f) * 0.5f;
        bool lzArmed = sling.IsLzArmedFor(helo);
        if (GUI.Button(
            new Rect(x, y, half, 32f),
            lzArmed ? "CLICK THE LZ" : "SET LZ",
            lzArmed ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            if (lzArmed)
            {
                sling.CancelSelection();
            }
            else
            {
                sling.BeginLzSelection(helo);
            }
        }

        if (GUI.Button(
            new Rect(x + half + 8f, y, half, 32f),
            sling.IsCarrying(helo) ? "SET IT DOWN HERE" : "CANCEL LIFT",
            CommanderUiTheme.Button))
        {
            sling.CancelJob(helo);
        }
    }

    /// <summary>One line saying what this building is and what its level buys.</summary>
    private string GetStructureSummary(Unit building)
    {
        if (economyService.IsIncomeBuilding(building))
        {
            return economyService.DescribeIncome(building);
        }

        if (economyService.IsBuiltNavalDock(building))
        {
            int level = economyService.GetNavalDockLevel(building);
            return $"NAVAL DOCK   LVL {level}/{CommanderEconomyService.MaxLevel}   "
                + CommanderNavalPurchaseService.GetLevelUnlockLabel(level).ToUpperInvariant();
        }

        if (building.TryGetComponent(out Factory factory) && factory.ProductionUnit != null)
        {
            int level = economyService.GetFactoryLevel(factory);
            return $"{factory.ProductionUnit.code} FACTORY   LVL {level}/{CommanderEconomyService.GetFactoryMaxLevel(factory)}"
                + $"   NEXT {level} IN {FormatCountdown(factory)} (EVERY {FormatDuration(factory.ProductionInterval)})";
        }

        // A warhead plant has no ProductionUnit, so the vehicle line above skips it and this one would
        // have fallen through to the bare building name. "Production unit null, interval set" is the
        // game's own test for a warhead line (Factory.GetProduction), not a guess.
        if (building.TryGetComponent(out Factory plant) && plant.ProductionInterval > 0f)
        {
            return $"ENRICHMENT PLANT   NEXT WARHEAD IN {FormatCountdown(plant)}"
                + $" (EVERY {FormatDuration(plant.ProductionInterval)})"
                + $"   URANIUM IN STORE {CommanderEconomyService.GetFriendlyWarheads()}";
        }

        return CommanderGameAccess.GetUnitLabel(building).ToUpperInvariant();
    }
}
