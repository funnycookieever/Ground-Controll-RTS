using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    /// <summary>The building whose DESTROY button is armed, so a stray click cannot level a mine.</summary>
    private Unit? demolishArmedUnit;

    /// <summary>When the selection bar's DELETE button stops being armed. Arm-then-confirm, the same
    /// two-click rule DESTROY BUILDING uses, because this one takes the whole selection.</summary>
    private float deleteArmedUntil;

    /// <summary>
    /// Time left on the factory's current production run. <c>Factory.GetNextProduction(true)</c> is
    /// the game's own answer, off the same <c>lastProductionTime</c> SyncVar the production
    /// SlowUpdate writes, so this stays right on a client as well as on the host.
    /// </summary>
    private static string FormatCountdown(Factory factory)
    {
        if (factory.ProductionInterval <= 0f)
        {
            return "--:--";
        }

        // Factory.ProduceWarhead never writes lastProductionTime (ProduceUnit does), so after an
        // enrichment plant's first warhead the countdown goes negative and read as a stuck 00:00.
        // The SlowUpdate still fires every interval, so wrapping onto the interval is the true time left.
        return FormatDuration(Mathf.Repeat(factory.GetNextProduction(true), factory.ProductionInterval));
    }

    private static string FormatDuration(float seconds)
    {
        int total = Mathf.Max(Mathf.RoundToInt(seconds), 0);
        return $"{total / 60}:{total % 60:00}";
    }

    private static float DrawUpgradeButton(
        float x,
        float width,
        float y,
        int level,
        float cost,
        float funds,
        bool friendly,
        bool oldEnabled,
        Action upgrade,
        int maxLevel = CommanderEconomyService.MaxLevel)
    {
        if (level >= maxLevel)
        {
            GUI.Label(new Rect(x, y, width, 36f), "FULLY UPGRADED", CommanderUiTheme.MutedLabel);
            return y + 42f;
        }

        GUI.enabled = oldEnabled && friendly && funds >= cost;
        if (GUI.Button(new Rect(x, y, width, 36f), $"UPGRADE   {CommanderEconomyService.FundsLabel(cost)}", CommanderUiTheme.PrimaryButton))
        {
            upgrade();
        }

        GUI.enabled = oldEnabled;
        return y + 42f;
    }

    private void DrawPinnedWindow(int windowId)
    {
        bool hasManualPins = selectionService.PinnedUnits.Count > 0;
        bool hasMissions = selectionService.MissionUnits.Count > 0;
        bool hasSamSites = selectionService.SamSiteUnits.Count > 0;
        if ((pinnedTab == 0 && !hasManualPins)
            || (pinnedTab == 1 && !hasMissions)
            || (pinnedTab == 2 && !hasSamSites))
        {
            pinnedTab = hasMissions ? 1 : hasSamSites ? 2 : 0;
        }

        CommanderUiTheme.DrawHelpButton(pinnedWindowRect.width, ref pinnedHelpVisible);
        float y = 36f;
        if (pinnedHelpVisible)
        {
            y = 34f + CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, pinnedWindowRect.width - 24f, 62f),
                "PINS contains manual pins. MISSIONS tracks Supply and Air Command aircraft. SAM SITES tracks active site cores. Click to select; X removes only the list entry, not the unit.") + 10f;
        }

        int tabCount = (hasManualPins ? 1 : 0) + (hasMissions ? 1 : 0) + (hasSamSites ? 1 : 0);
        float tabWidth = (pinnedWindowRect.width - 24f - (Mathf.Max(0, tabCount - 1) * 6f)) / Mathf.Max(1, tabCount);
        float tabX = 12f;
        if (hasManualPins && GUI.Button(
            new Rect(tabX, y, tabWidth, 30f),
            "PINS",
            pinnedTab == 0 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            pinnedTab = 0;
            pinnedScroll = Vector2.zero;
        }

        if (hasManualPins)
        {
            tabX += tabWidth + 6f;
        }

        if (hasMissions && GUI.Button(
            new Rect(tabX, y, tabWidth, 30f),
            "MISSIONS",
            pinnedTab == 1 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            pinnedTab = 1;
            pinnedScroll = Vector2.zero;
        }

        if (hasMissions)
        {
            tabX += tabWidth + 6f;
        }

        if (hasSamSites && GUI.Button(
            new Rect(tabX, y, tabWidth, 30f),
            "SAM SITES",
            pinnedTab == 2 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            pinnedTab = 2;
            pinnedScroll = Vector2.zero;
        }

        y += 38f;

        if (pinnedTab == 1)
        {
            float filterWidth = (pinnedWindowRect.width - 30f) * 0.5f;
            showSupplyMissions = CommanderUiTheme.Check(
                new Rect(12f, y, filterWidth, 26f),
                showSupplyMissions,
                "SUPPLY",
                CommanderUiTheme.Toggle);
            showAirCommandMissions = CommanderUiTheme.Check(
                new Rect(18f + filterWidth, y, filterWidth, 26f),
                showAirCommandMissions,
                "AIR COMMAND",
                CommanderUiTheme.Toggle);
            y += 32f;
        }

        List<Unit> visibleUnits = new();
        IReadOnlyList<Unit> source = pinnedTab == 1
            ? selectionService.MissionUnits
            : pinnedTab == 2
                ? selectionService.SamSiteUnits
                : selectionService.PinnedUnits;
        for (int i = 0; i < source.Count; i++)
        {
            Unit unit = source[i];
            if (pinnedTab != 1)
            {
                visibleUnits.Add(unit);
                continue;
            }

            CommanderSelectionService.MissionPinInfo info = selectionService.GetMissionInfo(unit);
            if ((showSupplyMissions && info.Source == "SUPPLY")
                || (showAirCommandMissions && info.Source == "AIR COMMAND"))
            {
                visibleUnits.Add(unit);
            }
        }

        Rect view = new(10f, y, pinnedWindowRect.width - 20f, pinnedWindowRect.height - y - 12f);
        float rowHeight = pinnedTab == 1 ? 58f : 40f;
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, (visibleUnits.Count * rowHeight) + 4f));
        pinnedScroll = GUI.BeginScrollView(view, pinnedScroll, inner);
        for (int i = 0; i < visibleUnits.Count; i++)
        {
            Unit unit = visibleUnits[i];
            float rowY = 2f + (i * rowHeight);
            if (GUI.Button(
                new Rect(4f, rowY, inner.width - 44f, rowHeight - 6f),
                string.Empty,
                pinnedTab == 1 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                selectionService.SelectPinnedUnit(unit);
            }

            string unitLabel = pinnedTab == 2
                ? selectionService.GetSamSiteLabel(unit)
                : CommanderGameAccess.GetUnitLabel(unit);
            GUI.Label(new Rect(12f, rowY + 5f, inner.width - 64f, 22f), unitLabel, CommanderUiTheme.Header);
            if (pinnedTab == 1)
            {
                CommanderSelectionService.MissionPinInfo info = selectionService.GetMissionInfo(unit);
                GUI.Label(
                    new Rect(12f, rowY + 28f, inner.width - 64f, 20f),
                    $"{info.Source} | {info.Mission}",
                    CommanderUiTheme.MutedLabel);
            }

            if (GUI.Button(new Rect(inner.width - 36f, rowY, 32f, rowHeight - 6f), "X", CommanderUiTheme.Button))
            {
                selectionService.RemovePinnedUnit(unit);
                break;
            }
        }

        GUI.EndScrollView();
        GUI.DragWindow(new Rect(0f, 0f, pinnedWindowRect.width - 44f, 28f));
    }

    private void DrawRadarWindow(int windowId)
    {
        if (!TryGetUnitSystemsTarget(out Unit focusedUnit))
        {
            return;
        }

        CommanderUiTheme.DrawHelpButton(radarWindowRect.width, ref radarHelpVisible);
        float y = 38f;
        if (radarHelpVisible)
        {
            string help = samSiteService.IsConstructionCore(focusedUnit)
                ? "Build defenses from stored supply. Logistics routes from nearby airbases are planned once from terrain and faction influence, then reused. Show Route displays the selected cached route. Automatic deliveries prefer safer viable routes."
                : CommanderRadarService.IsRadarGuidedLauncher(focusedUnit)
                ? "A radar-guided launcher is aimed by a fire-control truck within 300 m, and that truck needs a radar within 300 m of itself - any radar vehicle or radar station on the base counts. Without both it tracks nothing and never fires."
                : CommanderMobileEmplacementService.IsMoveableTrailer(focusedUnit) || CommanderMobileEmplacementService.IsTowingHauler(focusedUnit)
                    ? "HITCH TO HAULER sends the nearest idle HLT/MSV Tractor or Flatbed within 300 m to hitch this trailer. Once hitched, move the hauler with normal orders; DROP TRAILER sets the trailer down where it is."
                : CommanderRepairService.IsRepairUnit(focusedUnit)
                    ? "Basegame repair targeting weighs damage, structure value and distance. NEAREST REPAIR instead targets the closest damaged friendly repairable structure on each Basegame repair scan. AFTER BUILD: STAY keeps a builder where its build finished instead of driving back to its post."
                : focusedUnit is Ship
                    ? "Request a paid Basegame UH-90K naval-supply run for this ship. Purchased airframes are refunded after a successful return. Enemy ships cannot request supply."
                : string.Empty;
            y = 32f + 6f + CommanderUiTheme.DrawHelpOverlay(
                new Rect(10f, 32f, radarWindowRect.width - 20f, 108f),
                help);
        }

        bool friendly = CommanderGameAccess.IsFriendlyUnit(focusedUnit, CommanderGameAccess.GetLocalHq());
        if (!friendly)
        {
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 24f), "ENEMY UNIT  |  CONTROLS UNAVAILABLE", CommanderUiTheme.MutedLabel);
            y += 30f;
        }

        // A radar-guided launcher with nothing aiming it never fires and says nothing about why.
        if (CommanderRadarService.IsRadarGuidedLauncher(focusedUnit))
        {
            string shortfall = CommanderRadarService.GetLauncherLinkShortfall(focusedUnit);
            GUI.Label(
                new Rect(12f, y, radarWindowRect.width - 24f, 22f),
                shortfall.Length == 0 ? "FIRE CONTROL LINKED" : $"WILL NOT FIRE: {shortfall.ToUpperInvariant()}",
                shortfall.Length == 0 ? CommanderUiTheme.Header : CommanderUiTheme.WarningLabel);
            y += 30f;
        }

        bool oldEnabled = GUI.enabled;
        if (samSiteService.IsConstructionCore(focusedUnit))
        {
            y = DrawSamSiteLogistics(focusedUnit, friendly, oldEnabled, y);
        }

        if (CommanderRepairService.IsRepairUnit(focusedUnit))
        {
            GUI.enabled = oldEnabled && friendly;
            bool nearest = repairService.UsesNearestTarget(focusedUnit);
            if (GUI.Button(
                new Rect(12f, y, radarWindowRect.width - 24f, 36f),
                nearest ? "REPAIR: NEAREST" : "REPAIR: PRIORITY",
                nearest ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                repairService.ToggleNearestTarget(focusedUnit);
            }

            y += 40f;
            bool stay = repairService.StaysAfterBuild(focusedUnit);
            if (GUI.Button(
                new Rect(12f, y, radarWindowRect.width - 24f, 36f),
                stay ? "AFTER BUILD: STAY" : "AFTER BUILD: RETURN",
                stay ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                repairService.ToggleStayAfterBuild(focusedUnit);
            }

            GUI.enabled = oldEnabled;
            y += 40f;
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 38f), repairService.StatusText, CommanderUiTheme.MutedLabel);
            y += 42f;
        }

        if (focusedUnit is Ship ship)
        {
            GUI.enabled = oldEnabled && friendly;
            if (GUI.Button(
                new Rect(12f, y, radarWindowRect.width - 24f, 36f),
                supplyHeliService.GetNavalSupplyButtonLabel(),
                CommanderUiTheme.PrimaryButton))
            {
                supplyHeliService.RequestNavalSupply(ship);
            }

            GUI.enabled = oldEnabled;
            y += 40f;
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 38f), supplyHeliService.StatusText, CommanderUiTheme.MutedLabel);
            y += 42f;
        }
        else if (CommanderMobileEmplacementService.IsMoveableTrailer(focusedUnit) || CommanderMobileEmplacementService.IsTowingHauler(focusedUnit))
        {
            Rect hitchButtonRect = new(12f, y, radarWindowRect.width - 24f, 36f);
            bool inJob = mobileEmplacementService.IsInTowJob(focusedUnit, out bool towing);
            if (towing)
            {
                GUI.enabled = oldEnabled && friendly;
                if (GUI.Button(hitchButtonRect, "DROP TRAILER", CommanderUiTheme.PrimaryButton))
                {
                    mobileEmplacementService.DropTrailer(focusedUnit);
                }
            }
            else
            {
                GUI.enabled = oldEnabled && friendly && !inJob && mobileEmplacementService.HasAvailableHauler(focusedUnit);
                if (GUI.Button(
                    hitchButtonRect,
                    new GUIContent(inJob ? "HITCHING..." : "HITCH TO HAULER", "Idle Tractor or Flatbed required within 300 m."),
                    CommanderUiTheme.PrimaryButton))
                {
                    mobileEmplacementService.BeginHitch();
                }
            }

            GUI.enabled = oldEnabled;
            y += 42f;
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 42f), mobileEmplacementService.StatusText, CommanderUiTheme.MutedLabel);
            y += 46f;
        }

        // Laid out top down, so y is what the content needed; TickActive sizes the window to it.
        radarContentHeight = y + 8f;
        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, radarWindowRect.width, radarWindowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, radarWindowRect.width - 44f, 28f));
    }

    private float DrawSamSiteLogistics(
        Unit focusedUnit,
        bool friendly,
        bool oldEnabled,
        float y)
    {
        if (!ReferenceEquals(siteUiTarget, focusedUnit))
        {
            siteUiTarget = focusedUnit;
            siteAirbaseDropdownOpen = false;
            siteThresholdDropdownOpen = false;
        }

        float contentWidth = radarWindowRect.width - 24f;
        string supplyLabel = $"{samSiteService.GetConstructionSiteSupply(focusedUnit)}"
            + $"     QUEUE  {samSiteService.GetConstructionQueueCount(focusedUnit)}";
        GUI.Label(
            new Rect(12f, y, contentWidth, 26f),
            supplyLabel,
            CommanderUiTheme.Header);
        y += 34f;

        float buttonWidth = (contentWidth - 8f) / 3f;
        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanQueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.SamBattery);
        if (GUI.Button(
            new Rect(12f, y, buttonWidth, 36f),
            "SAM 40K",
            CommanderUiTheme.PrimaryButton))
        {
            samSiteService.QueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.SamBattery);
        }

        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanQueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Irm);
        if (GUI.Button(
            new Rect(16f + buttonWidth, y, buttonWidth, 36f),
            "IR 2K",
            CommanderUiTheme.Button))
        {
            samSiteService.QueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Irm);
        }

        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanQueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Gun23mm);
        if (GUI.Button(
            new Rect(20f + (buttonWidth * 2f), y, buttonWidth, 36f),
            "23MM 2K",
            CommanderUiTheme.Button))
        {
            samSiteService.QueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Gun23mm);
        }

        GUI.enabled = oldEnabled;
        y += 48f;

        IReadOnlyList<CommanderSupplyHeliService.SamSiteAirbaseOption> airbases =
            samSiteService.GetConstructionSiteAirbases(focusedUnit);
        Airbase? selectedAirbase = samSiteService.GetConstructionSiteAirbase(focusedUnit);
        CommanderSupplyHeliService.SamSiteAirbaseOption? selectedOption = null;
        for (int i = 0; i < airbases.Count; i++)
        {
            if (ReferenceEquals(airbases[i].Airbase, selectedAirbase))
            {
                selectedOption = airbases[i];
                break;
            }
        }

        GUI.Label(new Rect(12f, y, contentWidth, 20f), "LOGISTICS AIRBASE", CommanderUiTheme.MutedLabel);
        y += 22f;
        string selectedLabel = selectedOption != null
            ? $"{selectedOption.Label}  ({selectedOption.Distance / 1000f:0.0} km)"
            : "NO COMPATIBLE AIRBASE";
        if (GUI.Button(
            new Rect(12f, y, contentWidth, 34f),
            selectedLabel,
            siteAirbaseDropdownOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            siteAirbaseDropdownOpen = !siteAirbaseDropdownOpen;
            siteThresholdDropdownOpen = false;
        }

        y += 38f;

        if (siteAirbaseDropdownOpen)
        {
            for (int i = 0; i < airbases.Count; i++)
            {
                CommanderSupplyHeliService.SamSiteAirbaseOption option = airbases[i];
                string capability = option.SupportsSupply && option.SupportsJacknife
                    ? "SUPPLY + JACKNIFE"
                    : option.SupportsSupply ? "SUPPLY" : "JACKNIFE";
                string safety = option.Safe ? "SAFE" : "FORWARD";
                if (GUI.Button(
                    new Rect(12f, y, contentWidth, 30f),
                    $"{option.Label}  |  {option.Distance / 1000f:0.0} km  |  {capability}  |  {safety} {option.Risk:P0}",
                    ReferenceEquals(option.Airbase, selectedAirbase) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
                {
                    samSiteService.SelectConstructionSiteAirbase(focusedUnit, i);
                    siteAirbaseDropdownOpen = false;
                }

                y += 32f;
            }
        }

        float halfWidth = (contentWidth - 6f) * 0.5f;
        bool automaticSupply = samSiteService.GetAutomaticSupplyEnabled(focusedUnit);
        GUI.enabled = oldEnabled && friendly;
        if (GUI.Button(
            new Rect(12f, y, halfWidth, 34f),
            automaticSupply ? "AUTO SUPPLY: ON" : "AUTO SUPPLY: OFF",
            automaticSupply ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            samSiteService.ToggleAutomaticSupply(focusedUnit);
        }

        float threshold = samSiteService.GetAutomaticSupplyThreshold(focusedUnit);
        if (GUI.Button(
            new Rect(18f + halfWidth, y, halfWidth, 34f),
            $"BELOW {threshold:0}",
            siteThresholdDropdownOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            siteThresholdDropdownOpen = !siteThresholdDropdownOpen;
            siteAirbaseDropdownOpen = false;
        }

        GUI.enabled = oldEnabled;
        y += 38f;

        bool customRoute = samSiteService.GetConstructionCustomRouteEnabled(focusedUnit);
        bool routeVisible = samSiteService.IsConstructionSupplyRouteVisible(focusedUnit);
        GUI.enabled = oldEnabled && friendly;
        if (GUI.Button(
            new Rect(12f, y, halfWidth, 32f),
            customRoute ? "CUSTOM ROUTE: ON" : "CUSTOM ROUTE: OFF",
            customRoute ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            samSiteService.ToggleConstructionCustomRoute(focusedUnit);
        }

        GUI.enabled = oldEnabled
            && friendly
            && customRoute
            && samSiteService.CanShowConstructionSupplyRoute(focusedUnit);
        if (GUI.Button(
            new Rect(18f + halfWidth, y, halfWidth, 32f),
            routeVisible ? "HIDE SUPPLY ROUTE" : "SHOW SUPPLY ROUTE",
            routeVisible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            samSiteService.ToggleConstructionSupplyRoute(focusedUnit);
        }

        GUI.enabled = oldEnabled;
        y += 36f;

        if (siteThresholdDropdownOpen)
        {
            float optionWidth = contentWidth / CommanderSamSiteService.SupplyThresholdOptions.Length;
            for (int i = 0; i < CommanderSamSiteService.SupplyThresholdOptions.Length; i++)
            {
                float option = CommanderSamSiteService.SupplyThresholdOptions[i];
                if (GUI.Button(
                    new Rect(12f + (optionWidth * i), y, optionWidth - 2f, 30f),
                    option >= 1000f ? $"{option / 1000f:0}K" : $"{option:0}",
                    Mathf.Approximately(option, threshold) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
                {
                    samSiteService.SetAutomaticSupplyThreshold(focusedUnit, option);
                    siteThresholdDropdownOpen = false;
                }
            }

            y += 34f;
        }

        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanRequestConstructionSupply(focusedUnit);
        if (GUI.Button(
            new Rect(12f, y, contentWidth, 36f),
            "REQUEST SUPPLY",
            CommanderUiTheme.PrimaryButton))
        {
            samSiteService.RequestConstructionSupply(focusedUnit);
        }

        GUI.enabled = oldEnabled;
        y += 46f;

        int incomingJacknifes = samSiteService.GetIncomingConstructionJacknifes(focusedUnit);
        string incomingLabel = incomingJacknifes > 0 ? $"  +{incomingJacknifes} INBOUND" : string.Empty;
        GUI.Label(
            new Rect(12f, y, halfWidth, 34f),
            $"JACKNIFE  {samSiteService.GetConstructionSiteJacknifes(focusedUnit)}/2{incomingLabel}",
            CommanderUiTheme.Header);
        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanRequestConstructionJacknife(focusedUnit);
        if (GUI.Button(
            new Rect(18f + halfWidth, y, halfWidth, 34f),
            "REQUEST JACKNIFE",
            CommanderUiTheme.Button))
        {
            samSiteService.RequestConstructionJacknife(focusedUnit);
        }

        GUI.enabled = oldEnabled;
        y += 44f;

        GUI.Label(
            new Rect(12f, y, contentWidth, 42f),
            samSiteService.GetConstructionJacknifeStatus(focusedUnit),
            CommanderUiTheme.Label);
        y += 44f;

        GUI.Label(
            new Rect(12f, y, contentWidth, 70f),
            samSiteService.GetConstructionSiteStatus(focusedUnit),
            CommanderUiTheme.MutedLabel);
        return y + 74f;
    }

    /// <summary>Radar is switched from the selection bar, so a radar alone no longer opens this window.</summary>
    private bool TryGetUnitSystemsTarget(out Unit unit)
    {
        unit = selectionService.FocusedSelection!;
        if (unit == null || unit.disabled)
        {
            return false;
        }

        return unit is Ship
            || CommanderRadarService.IsRadarGuidedLauncher(unit)
            || CommanderRepairService.IsRepairUnit(unit)
            || CommanderMobileEmplacementService.IsMoveableTrailer(unit)
            || CommanderMobileEmplacementService.IsTowingHauler(unit)
            || samSiteService.IsConstructionCore(unit);
    }

    /// <summary>
    /// Fuel and ammunition around every airbase the faction holds, nearest first. A resupply is
    /// drawn from every depot within range of the field, so the shelf that emptied is often not the
    /// building the player happens to have selected — this is the readout that says so.
    /// </summary>
    private void DrawBaseSupplyWindow(int windowId)
    {
        float y = 38f;
        if (CommanderUiTheme.DrawHelpButton(baseSupplyWindowRect.width, ref baseSupplyHelpVisible))
        {
            y = 34f + CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, baseSupplyWindowRect.width - 24f, 82f),
                $"Every supply depot within {CommanderSettings.SupplyStoreRangeKm:0} km of a base counts toward that base — a depot anywhere inside the build radius feeds the field, it does not have to sit beside the strip. Refuelling and rearming spend these totals, nearest depot first, and so does every unit you put on the field. FIELD DEPOTS are stores that stand away from any airbase. Select a depot to buy more.") + 10f;
        }

        if (GUI.Button(new Rect(baseSupplyWindowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            baseSupplyWindowVisible = false;
            return;
        }

        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        if (stores == null)
        {
            GUI.Label(new Rect(12f, y, baseSupplyWindowRect.width - 24f, 24f), "No supply service.", CommanderUiTheme.Label);
            return;
        }

        // Walked a few times a second rather than every OnGUI pass: OnGUI runs more than once per
        // frame and this sweeps every faction unit.
        if (CommanderScheduler.IsDueRealtime(ref nextBaseSupplyAt, 0.5f))
        {
            stores.CollectBaseSupply(baseSupplyRows);
        }

        if (baseSupplyRows.Count == 0)
        {
            GUI.Label(
                new Rect(12f, y, baseSupplyWindowRect.width - 24f, 40f),
                "No airbase held, or no supply depot on this mission. Resupply is free until the faction owns one.",
                CommanderUiTheme.Label);
            return;
        }

        // One picker for the whole window: the BUY prices in the rows below are quoted at whatever
        // it says, so it has to be read before them rather than beside each one.
        DrawPurchaseSizeButton(new Rect(12f, y, 200f, 24f), true);
        GUI.Label(
            new Rect(220f, y, baseSupplyWindowRect.width - 232f, 24f),
            "of a shelf per BUY.",
            CommanderUiTheme.MutedLabel);
        y += 28f;

        Rect view = new(12f, y, baseSupplyWindowRect.width - 24f, baseSupplyWindowRect.height - y - 14f);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, (baseSupplyRows.Count * 78f) + 6f));
        baseSupplyScroll = GUI.BeginScrollView(view, baseSupplyScroll, inner);
        for (int i = 0; i < baseSupplyRows.Count; i++)
        {
            CommanderSupplyStoreService.BaseSupply row = baseSupplyRows[i];
            Rect box = new(4f, 3f + (i * 78f), inner.width - 8f, 72f);
            GUI.Box(box, string.Empty, CommanderUiTheme.Panel);
            string distance = row.DistanceKm >= float.MaxValue * 0.5f ? string.Empty : $"   {row.DistanceKm:0.0} km";
            GUI.Label(
                new Rect(box.x + 8f, box.y + 4f, box.width - 16f, 22f),
                $"{row.Name.ToUpperInvariant()}{distance}   |   {row.Stores} DEPOT{(row.Stores == 1 ? string.Empty : "S")}",
                CommanderUiTheme.Header);
            float barWidth = (box.width - 24f) * 0.5f;

            // The FIELD DEPOTS row is scattered across the map, so levelling it would teleport
            // supply between fronts; only a real base gets BUY buttons.
            float buyWidth = row.Units == null ? 0f : 86f;
            DrawSupplyBar(new Rect(box.x + 8f, box.y + 28f, barWidth - buyWidth, 20f), "FUEL", row.FuelFraction);
            DrawSupplyBar(new Rect(box.x + 16f + barWidth, box.y + 28f, barWidth - buyWidth, 20f), "AMMO", row.AmmoFraction);
            if (row.Units != null)
            {
                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && row.FuelPrice > 0f;

                // Same as the selection bar: the price is in the label, so the button shrinks the
                // label rather than wrapping it into a 24 px row.
                if (CommanderUiTheme.FitButton(
                        new Rect(box.x + 12f + barWidth - buyWidth, box.y + 26f, buyWidth - 4f, 24f),
                        new GUIContent(
                            $"BUY {CommanderEconomyService.FundsLabel(row.FuelPrice)}",
                            $"Delivers {CommanderSupplyStoreService.PurchaseSizeLabel} of this base's fuel shelf and levels every depot here to the same fill."),
                        CommanderUiTheme.Button))
                {
                    stores.TryBuyBaseFuel(row.Name, row.Units, out string fuelMessage);

                    // A guest's buy is answered by the host later, empty here.
                    if (fuelMessage.Length > 0)
                    {
                        CommanderAlertService.Instance?.NotifySupply(fuelMessage, row.Units.Count > 0 ? row.Units[0] : null);
                    }

                    nextBaseSupplyAt = 0f;
                }

                GUI.enabled = oldEnabled && row.AmmoPrice > 0f;
                if (CommanderUiTheme.FitButton(
                        new Rect(box.x + 20f + (barWidth * 2f) - buyWidth, box.y + 26f, buyWidth - 4f, 24f),
                        new GUIContent(
                            $"BUY {CommanderEconomyService.FundsLabel(row.AmmoPrice)}",
                            $"Delivers {CommanderSupplyStoreService.PurchaseSizeLabel} of this base's ammunition shelf and levels every munitions store here to the same fill."),
                        CommanderUiTheme.Button))
                {
                    stores.TryBuyBaseAmmo(row.Name, row.Units, out string ammoMessage);
                    if (ammoMessage.Length > 0)
                    {
                        CommanderAlertService.Instance?.NotifySupply(ammoMessage, row.Units.Count > 0 ? row.Units[0] : null);
                    }

                    nextBaseSupplyAt = 0f;
                }

                GUI.enabled = oldEnabled;
            }

            GUI.Label(
                new Rect(box.x + 8f, box.y + 50f, barWidth, 20f),
                $"{row.FuelKg:0} / {row.FuelMaxKg:0} kg",
                CommanderUiTheme.MutedLabel);
            GUI.Label(
                new Rect(box.x + 16f + barWidth, box.y + 50f, barWidth, 20f),
                $"{row.AmmoKg:0} / {row.AmmoMaxKg:0} kg",
                CommanderUiTheme.MutedLabel);
        }

        GUI.EndScrollView();
        GUI.DragWindow(new Rect(0f, 0f, baseSupplyWindowRect.width - 72f, 28f));
    }
}
