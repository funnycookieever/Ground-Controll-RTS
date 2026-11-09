using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderSamSiteAnalyzerUi
{
    private const int WindowId = 0x434F5341;

    private static readonly float[] RangeOptions = { 0f, 5000f, 10000f, 20000f, 40000f, 80000f };
    private static readonly float[] PercentOptions = { 0f, 0.1f, 0.25f, 0.4f, 0.5f, 0.6f, 0.75f, 0.9f, 1f };

    private readonly CommanderSamSiteAnalyzerService service;
    private readonly CommanderSamSiteService siteService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private bool helpVisible;
    private bool automaticLocalCandidatePass = true;
    private bool positionInitialized;
    private int openFilterDropdown = -1;
    private Rect filterDropdownAnchor;
    private string filterTooltip = string.Empty;
    private Rect filterTooltipAnchor;
    private Rect windowRect;
    private Vector2 scroll;

    internal CommanderSamSiteAnalyzerUi(
        CommanderSamSiteAnalyzerService service,
        CommanderSamSiteService siteService,
        CommanderSupplyHeliService supplyHeliService)
    {
        this.service = service;
        this.siteService = siteService;
        this.supplyHeliService = supplyHeliService;
    }

    internal bool Visible { get; private set; }

    internal void Toggle()
    {
        Visible = !Visible;
        helpVisible = false;
        openFilterDropdown = -1;
        EnsurePosition();
        service.SetUiVisible(Visible);
    }

    internal void Hide()
    {
        Visible = false;
        helpVisible = false;
        openFilterDropdown = -1;
        service.SetUiVisible(false);
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        EnsurePosition();
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "SAM SITE ANALYZER [EXPERIMENTAL]");
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

    private static string GetFilterTooltip(int filter)
    {
        return filter switch
        {
            0 => "Distance from the camera. MIN keeps distant sites; MAX keeps nearby sites.",
            1 => "Share of surrounding terrain visible to the radar out to 50 km.",
            2 => "Coverage of contested or enemy-influenced terrain within radar range.",
            3 => "Enemy influence at the site. Higher values mean greater exposure.",
            _ => "Terrain visibility inside the 5 km cone pointing toward enemy influence.",
        };
    }

    private static string FormatRange(float value)
    {
        return value <= 0f ? "ANY" : $"{value / 1000f:0} KM";
    }

    private static string FormatPercent(
        float value,
        CommanderSamSiteAnalyzerService.FilterComparison comparison)
    {
        if ((comparison == CommanderSamSiteAnalyzerService.FilterComparison.Minimum && value <= 0f)
            || (comparison == CommanderSamSiteAnalyzerService.FilterComparison.Maximum && value >= 0.999f))
        {
            return "ANY";
        }

        return comparison == CommanderSamSiteAnalyzerService.FilterComparison.Minimum
            ? $">= {value:P0}"
            : $"<= {value:P0}";
    }

    private void DrawWindow(int windowId)
    {
        filterTooltip = string.Empty;
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, windowRect.width - 24f, 132f),
                "WORK IN PROGRESS: This window is primarily built for development and debugging. It scans terrain for radar-site candidates, filters and ranks them by visibility, operational relevance and exposure, then previews or starts construction. MIN/MAX selects whether each filter is a lower or upper limit. AI LOCAL PASS tests a nearby alternative from the best 30%.");
        }

        if (GUI.Button(
            new Rect(windowRect.width - 34f, 3f, 26f, 22f),
            "X",
            CommanderUiTheme.ChromeButton))
        {
            Hide();
            return;
        }

        float y = helpVisible ? 176f : 38f;
        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 24f),
            service.State.ToString().ToUpperInvariant(),
            CommanderUiTheme.Header);
        y += 28f;

        if (service.State is CommanderSamSiteAnalyzerService.AnalyzerState.Sampling
            or CommanderSamSiteAnalyzerService.AnalyzerState.Coverage
            or CommanderSamSiteAnalyzerService.AnalyzerState.Refining)
        {
            GUI.HorizontalSlider(
                new Rect(12f, y, windowRect.width - 24f, 18f),
                service.Progress,
                0f,
                1f);
            y += 22f;
        }

        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 42f),
            service.StatusText,
            CommanderUiTheme.MutedLabel);
        y += 46f;

        bool filterPopupOpen = openFilterDropdown >= 0;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !filterPopupOpen;
        if (GUI.Button(new Rect(12f, y, windowRect.width - 24f, 32f), "REBUILD", CommanderUiTheme.Button))
        {
            service.RebuildAnalysis();
        }

        y += 40f;

        GUI.enabled = oldEnabled && !filterPopupOpen && service.SuggestedSites.Count > 0;
        if (GUI.Button(
            new Rect(12f, y, windowRect.width - 24f, 32f),
            service.ShowProposalMarkers ? "SHOW 12 SITE MARKERS: ON" : "SHOW 12 SITE MARKERS: OFF",
            service.ShowProposalMarkers ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            service.SetProposalMarkersVisible(!service.ShowProposalMarkers);
        }

        GUI.enabled = oldEnabled;
        y += 40f;

        y = DrawFilterControls(y, oldEnabled);

        GUI.enabled = oldEnabled && !filterPopupOpen;
        bool roadLimit = CommanderUiTheme.Check(
            new Rect(12f, y, windowRect.width - 24f, 26f),
            service.LimitRoadDistance,
            "WITHIN 500 M OF ROAD",
            CommanderUiTheme.Toggle);
        service.SetLimitRoadDistance(roadLimit);
        y += 34f;

        bool localPass = CommanderUiTheme.Check(
            new Rect(12f, y, windowRect.width - 24f, 26f),
            automaticLocalCandidatePass,
            "AI LOCAL TOP-30% PASS (1 KM)",
            CommanderUiTheme.Toggle);
        automaticLocalCandidatePass = localPass;
        y += 34f;

        float testButtonWidth = (windowRect.width - 28f) / 2f;
        GUI.enabled = oldEnabled && !filterPopupOpen && service.ActiveSiteReady;
        if (GUI.Button(
            new Rect(12f, y, testButtonWidth, 34f),
            "DEBUG: SPAWN COMPLETE",
            CommanderUiTheme.Button))
        {
            siteService.SpawnCompleteDebugSite();
        }

        GUI.enabled = oldEnabled && !filterPopupOpen && service.IsReady;
        if (GUI.Button(
            new Rect(16f + testButtonWidth, y, testButtonWidth, 34f),
            "AI: BUILD COMPLETE",
            CommanderUiTheme.PrimaryButton))
        {
            siteService.StartAutomaticSiteConstruction(automaticLocalCandidatePass);
        }

        GUI.enabled = oldEnabled;
        y += 40f;

        GUI.enabled = oldEnabled
            && !filterPopupOpen
            && (siteService.HasActiveConstructionSite || service.ActiveSiteReady);
        if (GUI.Button(
            new Rect(12f, y, windowRect.width - 24f, 34f),
            siteService.HasActiveConstructionSite ? "REMOVE ACTIVE SITE" : "START SITE DELIVERY",
            siteService.HasActiveConstructionSite ? CommanderUiTheme.DangerButton : CommanderUiTheme.PrimaryButton))
        {
            siteService.Toggle();
        }

        GUI.enabled = oldEnabled && !filterPopupOpen;
        y += 38f;
        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 34f),
            siteService.StatusText,
            CommanderUiTheme.MutedLabel);
        y += 36f;

        if (siteService.TryGetPlatformTarget(out GlobalPosition platformTarget))
        {
            if (GUI.Button(
                new Rect(12f, y, windowRect.width - 24f, 34f),
                "REQUEST CARGO RUN",
                CommanderUiTheme.PrimaryButton))
            {
                supplyHeliService.RequestAutomaticCargoRun(platformTarget);
            }

            y += 38f;
            GUI.Label(
                new Rect(12f, y, windowRect.width - 24f, 30f),
                supplyHeliService.StatusText,
                CommanderUiTheme.MutedLabel);
            y += 32f;
        }

        y = DrawCandidateTabs(y, oldEnabled && !filterPopupOpen);

        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 22f),
            "RADAR | SITE CORE | 23MM | IRM | STRATOLANCE | AMMO | FIRE CONTROL",
            CommanderUiTheme.MutedLabel);
        y += 24f;

        Rect listRect = new(12f, y, windowRect.width - 24f, windowRect.height - y - 14f);
        GUI.Box(listRect, string.Empty, CommanderUiTheme.Panel);
        float contentHeight = Mathf.Max(
            listRect.height - 8f,
            (service.SuggestedSites.Count * 62f) + 8f);
        Rect viewRect = new(0f, 0f, listRect.width - 22f, contentHeight);
        scroll = GUI.BeginScrollView(listRect, scroll, viewRect);
        for (int i = 0; i < service.SuggestedSites.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteCandidate candidate = service.SuggestedSites[i];
            CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
            GlobalPosition cameraPosition = cameraManager == null
                ? candidate.Position
                : cameraManager.transform.position.ToGlobalPosition();
            float deltaX = cameraPosition.x - candidate.Position.x;
            float deltaZ = cameraPosition.z - candidate.Position.z;
            float distance = Mathf.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
            string label =
                $"SITE {i + 1}  {distance / 1000f:0.0} km  |  X {candidate.Position.x:0}  Z {candidate.Position.z:0}\n"
                + $"Height {candidate.Height:0} m  |  Area LOS {candidate.Coverage:P0}  |  Threat Coverage {candidate.StrategicCoverage:P0}\n"
                + $"Forward 5 km {candidate.ForwardCoverage:P0}  |  Risk {candidate.Risk:P0}  |  Rating {candidate.Score:0}";
            GUI.Label(
                new Rect(6f, 4f + (i * 62f), viewRect.width - 86f, 56f),
                label,
                i == 0 ? CommanderUiTheme.Header : CommanderUiTheme.Label);
            if (GUI.Button(
                new Rect(viewRect.width - 76f, 12f + (i * 62f), 68f, 36f),
                service.ActiveSiteIndex == i ? "HIDE" : "JUMP",
                service.ActiveSiteIndex == i ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
            {
                service.JumpToSite(i);
            }
        }

        GUI.EndScrollView();

        GUI.enabled = oldEnabled;
        DrawFilterDropdown();
        DrawFilterTooltip();

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawCandidateTabs(float y, bool enabled)
    {
        GUI.enabled = enabled;
        float tabWidth = (windowRect.width - 28f) * 0.5f;
        GUIStyle nearbyStyle = service.ListMode == CommanderSamSiteAnalyzerService.CandidateListMode.Nearby
            ? CommanderUiTheme.SelectedButton
            : CommanderUiTheme.Button;
        if (GUI.Button(
            new Rect(12f, y, tabWidth, 30f),
            "NEARBY",
            nearbyStyle))
        {
            service.SetCandidateListMode(CommanderSamSiteAnalyzerService.CandidateListMode.Nearby);
            scroll = Vector2.zero;
        }

        GUIStyle rankedStyle = service.ListMode == CommanderSamSiteAnalyzerService.CandidateListMode.Ranked
            ? CommanderUiTheme.SelectedButton
            : CommanderUiTheme.Button;
        if (GUI.Button(
            new Rect(16f + tabWidth, y, tabWidth, 30f),
            "RANKED",
            rankedStyle))
        {
            service.SetCandidateListMode(CommanderSamSiteAnalyzerService.CandidateListMode.Ranked);
            scroll = Vector2.zero;
        }

        y += 34f;

        if (service.ListMode == CommanderSamSiteAnalyzerService.CandidateListMode.Ranked)
        {
            CommanderSamSiteAnalyzerService.CandidateSortMode[] modes =
            {
                CommanderSamSiteAnalyzerService.CandidateSortMode.Rating,
                CommanderSamSiteAnalyzerService.CandidateSortMode.AreaLos,
                CommanderSamSiteAnalyzerService.CandidateSortMode.FrontEnemy,
                CommanderSamSiteAnalyzerService.CandidateSortMode.Risk,
                CommanderSamSiteAnalyzerService.CandidateSortMode.Forward5Km,
                CommanderSamSiteAnalyzerService.CandidateSortMode.Height,
            };
            string[] labels = { "RATING", "AREA", "THREAT", "RISK", "FWD 5K", "HEIGHT" };
            float width = (windowRect.width - 34f) / modes.Length;
            for (int i = 0; i < modes.Length; i++)
            {
                if (GUI.Button(
                    new Rect(12f + (i * (width + 2f)), y, width, 28f),
                    labels[i],
                    service.SortMode == modes[i] ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
                {
                    service.SetCandidateSortMode(modes[i]);
                    scroll = Vector2.zero;
                }
            }

            y += 32f;
        }

        return y;
    }

    private float DrawFilterControls(float y, bool enabled)
    {
        GUI.enabled = enabled && openFilterDropdown < 0;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 20f), "CANDIDATE FILTERS", CommanderUiTheme.MutedLabel);
        y += 22f;
        float width = (windowRect.width - 32f) / 3f;
        DrawFilterControl(
            0,
            new Rect(12f, y, width, 30f),
            "RANGE",
            service.MaximumCandidateRange,
            service.RangeComparison,
            range: true);
        DrawFilterControl(
            1,
            new Rect(16f + width, y, width, 30f),
            "AREA",
            service.MinimumAreaCoverage,
            service.AreaComparison);
        DrawFilterControl(
            2,
            new Rect(20f + (width * 2f), y, width, 30f),
            "THREAT",
            service.MinimumFrontShare,
            service.FrontComparison);
        y += 34f;
        DrawFilterControl(
            3,
            new Rect(12f, y, width, 30f),
            "RISK",
            service.MaximumRisk,
            service.RiskComparison);
        DrawFilterControl(
            4,
            new Rect(16f + width, y, width, 30f),
            "FWD 5K",
            service.MinimumForwardCoverage,
            service.ForwardComparison);
        GUI.enabled = enabled && openFilterDropdown < 0;
        if (GUI.Button(new Rect(20f + (width * 2f), y, width, 30f), "RESET FILTERS", CommanderUiTheme.Button))
        {
            service.ResetCandidateFilters();
        }

        GUI.enabled = enabled;
        return y + 38f;
    }

    private void DrawFilterControl(
        int index,
        Rect rect,
        string label,
        float value,
        CommanderSamSiteAnalyzerService.FilterComparison comparison,
        bool range = false)
    {
        const float operatorWidth = 46f;
        Rect operatorRect = new(rect.x, rect.y, operatorWidth, rect.height);
        Rect valueRect = new(rect.x + operatorWidth + 2f, rect.y, rect.width - operatorWidth - 2f, rect.height);
        if (GUI.Button(
            operatorRect,
            comparison == CommanderSamSiteAnalyzerService.FilterComparison.Minimum ? "MIN" : "MAX",
            CommanderUiTheme.SelectedButton))
        {
            CommanderSamSiteAnalyzerService.FilterComparison nextComparison = comparison == CommanderSamSiteAnalyzerService.FilterComparison.Minimum
                ? CommanderSamSiteAnalyzerService.FilterComparison.Maximum
                : CommanderSamSiteAnalyzerService.FilterComparison.Minimum;
            service.SetFilterComparison(
                index,
                nextComparison);
        }

        if (GUI.Button(
            valueRect,
            $"{label}  {(range ? FormatRange(value) : FormatPercent(value, comparison))}  v",
            openFilterDropdown == index ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            openFilterDropdown = openFilterDropdown == index ? -1 : index;
            filterDropdownAnchor = valueRect;
        }

        if (openFilterDropdown < 0 && rect.Contains(Event.current.mousePosition))
        {
            filterTooltip = GetFilterTooltip(index);
            filterTooltipAnchor = rect;
        }
    }

    private void DrawFilterTooltip()
    {
        if (string.IsNullOrEmpty(filterTooltip) || openFilterDropdown >= 0)
        {
            return;
        }

        float width = Mathf.Min(360f, windowRect.width - 24f);
        const float height = 48f;
        float x = Mathf.Clamp(filterTooltipAnchor.x, 12f, windowRect.width - width - 12f);
        float y = filterTooltipAnchor.yMax + 4f;
        Rect tooltip = new(x, y, width, height);
        GUI.Box(tooltip, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(x + 8f, y + 5f, width - 16f, height - 10f), filterTooltip, CommanderUiTheme.Label);
    }

    private void DrawFilterDropdown()
    {
        if (openFilterDropdown < 0)
        {
            return;
        }

        float[] options = openFilterDropdown switch
        {
            0 => RangeOptions,
            _ => PercentOptions,
        };
        const float rowHeight = 30f;
        Rect popup = new(
            filterDropdownAnchor.x,
            filterDropdownAnchor.yMax + 2f,
            Mathf.Max(170f, filterDropdownAnchor.width),
            (options.Length * rowHeight) + 8f);
        if (popup.xMax > windowRect.width - 8f)
        {
            popup.x = windowRect.width - popup.width - 8f;
        }

        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        for (int i = 0; i < options.Length; i++)
        {
            float value = options[i];
            string label = openFilterDropdown switch
            {
                0 => FormatRange(value),
                _ => FormatPercent(value, GetFilterComparison(openFilterDropdown)),
            };
            if (GUI.Button(
                new Rect(popup.x + 4f, popup.y + 4f + (i * rowHeight), popup.width - 8f, rowHeight - 2f),
                label,
                CommanderUiTheme.Button))
            {
                ApplyFilterValue(openFilterDropdown, value);
                openFilterDropdown = -1;
                break;
            }
        }
    }

    private void ApplyFilterValue(int filter, float value)
    {
        service.SetCandidateFilters(
            filter == 0 ? value : service.MaximumCandidateRange,
            filter == 1 ? value : service.MinimumAreaCoverage,
            filter == 2 ? value : service.MinimumFrontShare,
            filter == 3 ? value : service.MaximumRisk,
            filter == 4 ? value : service.MinimumForwardCoverage);
    }

    private CommanderSamSiteAnalyzerService.FilterComparison GetFilterComparison(int filter)
    {
        return filter switch
        {
            0 => service.RangeComparison,
            1 => service.AreaComparison,
            2 => service.FrontComparison,
            3 => service.RiskComparison,
            _ => service.ForwardComparison,
        };
    }

    private void EnsurePosition()
    {
        windowRect.size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(560f, CommanderUiScale.Width - 24f), Mathf.Min(650f, CommanderUiScale.Height - 24f)),
            new Vector2(420f, 320f));
        if (positionInitialized)
        {
            return;
        }

        float width = Mathf.Min(560f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(650f, CommanderUiScale.Height - 24f);
        windowRect = new Rect(
            Mathf.Max(12f, CommanderUiScale.Width - width - 24f),
            Mathf.Max(12f, (CommanderUiScale.Height - height) * 0.5f),
            width,
            height);
        positionInitialized = true;
    }
}
