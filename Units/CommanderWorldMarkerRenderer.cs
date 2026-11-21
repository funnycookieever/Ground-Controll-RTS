using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderWorldMarkerRenderer
{
    internal static readonly Color RescueColor = new(1f, 0.85f, 0.2f, 0.92f);

    /// <summary>Cells in the capture bar. ASCII, because the IMGUI font guarantees nothing else.</summary>
    private const int CaptureBarCells = 10;

    private static readonly Color LzColor = new(0.35f, 0.9f, 0.42f, 0.95f);
    private static readonly Color DefendColor = new(0.3f, 0.62f, 1f, 0.92f);
    private static readonly Color NeutralBaseColor = new(1f, 0.86f, 0.25f, 0.95f);
    private static readonly Color HostileBaseColor = new(1f, 0.48f, 0.28f, 0.95f);
    private static readonly Color CapturingBaseColor = new(0.35f, 1f, 0.5f, 0.98f);
    private static readonly Color LosingBaseColor = new(1f, 0.3f, 0.3f, 0.98f);
    private static readonly Color RepairColor = new(0.45f, 0.95f, 1f, 0.95f);
    private static readonly Color ResupplyColor = new(0.45f, 0.8f, 1f, 0.85f);

    private static readonly System.Text.StringBuilder CaptureBar = new();

    /// <summary>Terrain-following ring per store, sampled once: a building does not move, and a
    /// sample is a raycast per point.</summary>
    private static readonly Dictionary<Unit, GlobalPosition[]> ResupplyRings = new();
    private static readonly GlobalPosition[] MobileRingPoints = new GlobalPosition[72];

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderSamSiteAnalyzerService samSiteAnalyzerService;
    private readonly CommanderSamSiteService samSiteService;
    private readonly List<GlobalPosition> deliveryTargets = new();
    private readonly List<GlobalPosition> supplyRoute = new();
    private readonly List<GlobalPosition> routePoints = new();
    private readonly List<Unit> routeTargets = new();
    private readonly List<CommanderSamSiteAnalyzerService.SiteLayoutMarker> samSiteLayout = new();
    private readonly List<CommanderSamSiteAnalyzerService.SiteCandidate> samSiteProposals = new();
    private readonly List<CommanderCaptureService.CaptureTarget> captureTargets = new();
    private readonly List<GlobalPosition> slingZones = new();

    internal CommanderWorldMarkerRenderer(
        CommanderSelectionService selectionService,
        CommanderMoveService moveService,
        CommanderSpawnService spawnService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderSamSiteAnalyzerService samSiteAnalyzerService,
        CommanderSamSiteService samSiteService)
    {
        this.selectionService = selectionService;
        this.moveService = moveService;
        this.spawnService = spawnService;
        this.supplyHeliService = supplyHeliService;
        this.samSiteAnalyzerService = samSiteAnalyzerService;
        this.samSiteService = samSiteService;
    }

    /// <summary>
    /// True only while a map is actually covering the 3D view. The mod's own tactical map sets
    /// <c>DynamicMap.mapMaximized</c> as well, but it is a small window with the scene still
    /// visible behind it — testing that flag alone is what silently switched every route line and
    /// capture bracket off in the 3D view for anyone who leaves the RTS map open.
    /// </summary>
    private static bool IsSceneHiddenByMap =>
        DynamicMap.mapMaximized && CommanderTacticalMapService.Instance?.IsOpen != true;

    internal void Draw(bool supplyWindowVisible)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        CommanderOrderPing.Draw(camera);
        CommanderEconomyService.Instance?.DrawPlacementRing(camera);
        CommanderCockpitMarkerUi.DrawCommanderView(camera, IsSceneHiddenByMap);
        DrawCaptureTargets(camera);
        DrawRepairCountdowns(camera);
        DrawShieldCountdowns(camera, selectionService.SelectedUnits);
        CommanderRadarExposure.Draw();

        // An enemy can be selected to inspect it, but its orders (the AI commander's capture runs)
        // are not ours to see.
        FactionHQ? selectionHq = CommanderGameAccess.GetLocalHq();
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.IsFriendlyUnit(unit, selectionHq))
            {
                continue;
            }

            if (IsResupplyStore(unit))
            {
                DrawResupplyRing(camera, unit);
                continue;
            }

            if (unit is Aircraft helo && CommanderSlingLiftService.Instance?.TryGetRescueTarget(helo, out Unit pilot) == true)
            {
                DrawTargetLine(camera, unit, pilot, "RESCUE", RescueColor);
                continue;
            }

            if (moveService.TryGetDefendTarget(unit, out Unit defended))
            {
                DrawTargetLine(camera, unit, defended, "DEFEND", DefendColor);
                continue;
            }

            if (DrawOrderRoute(camera, unit))
            {
                continue;
            }

            if (moveService.TryGetPlayerDestination(unit, out GlobalPosition destination))
            {
                DrawMarker(camera, destination, "MOVE", new Color(0.2f, 0.85f, 0.82f, 0.9f));
            }
        }

        if (spawnService.SelectedDepot != null && spawnService.TryGetSelectedRallyPoint(out GlobalPosition rallyPoint))
        {
            DrawMarker(camera, rallyPoint, "RALLY", new Color(0.95f, 0.78f, 0.22f, 0.9f));
        }

        if (CommanderNavalPurchaseService.Instance?.TryGetSelectedRallyPoint(out GlobalPosition navalRally) == true)
        {
            DrawMarker(camera, navalRally, "NAVAL RALLY", new Color(0.95f, 0.78f, 0.22f, 0.9f));
        }

        if (supplyHeliService.AwaitingTargetSelection)
        {
            DrawCursorMarker("LZ", new Color(0.35f, 0.9f, 0.42f, 0.95f));
        }

        // Sling lifts get the same green LZ an air transport shows, for every lift, in the 3D view
        // and on the map.
        CommanderSlingLiftService? sling = CommanderSlingLiftService.Instance;
        if (sling != null)
        {
            if (sling.AwaitingLzClick)
            {
                DrawCursorMarker("LZ", LzColor);
            }

            sling.CopyLandingZones(slingZones);
            for (int i = 0; i < slingZones.Count; i++)
            {
                DrawLandingZone(camera, slingZones[i]);
            }
        }

        samSiteAnalyzerService.CopyProposalSites(samSiteProposals);
        for (int i = 0; i < samSiteProposals.Count; i++)
        {
            DrawLargeMarker(
                camera,
                samSiteProposals[i].Position,
                $"SAM SITE {i + 1}",
                new Color(0.1f, 0.82f, 1f, 0.95f));
        }

        samSiteAnalyzerService.CopyVisibleActiveLayout(samSiteLayout);
        for (int i = 0; i < samSiteLayout.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = samSiteLayout[i];
            if (marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower)
            {
                continue;
            }

            DrawMarker(camera, marker.Position, GetSamLabel(marker.Role), GetSamColor(marker.Role));
        }

        samSiteService.CopyVisibleSupplyRoute(supplyRoute);
        for (int i = 0; i < supplyRoute.Count; i++)
        {
            string label = i == 0
                ? "AIRBASE"
                : i == supplyRoute.Count - 1 ? "SAM SITE" : $"ROUTE {i}";
            DrawMarker(camera, supplyRoute[i], label, new Color(0.2f, 0.78f, 1f, 0.92f));
        }

        if (!supplyWindowVisible)
        {
            return;
        }

        supplyHeliService.CopyActiveDeliveryTargets(deliveryTargets);
        for (int i = 0; i < deliveryTargets.Count; i++)
        {
            DrawMarker(camera, deliveryTargets[i], "LZ", new Color(0.35f, 0.9f, 0.42f, 0.9f));
        }
    }

    /// <summary>
    /// How long is left on every building a crew is repairing, floated above the building itself.
    /// A wreck being rebuilt looks exactly like a wreck nobody has touched, so the scaffolding was
    /// the only sign the work was happening at all, and none of how far along it was.
    /// </summary>
    private static void DrawRepairCountdowns(Camera camera)
    {
        CommanderRepairService? repair = CommanderRepairService.Instance;
        if (repair == null || IsSceneHiddenByMap)
        {
            return;
        }

        IReadOnlyList<Unit> damaged = repair.DamagedBuildings;
        for (int i = 0; i < damaged.Count; i++)
        {
            Unit building = damaged[i];
            if (building == null || !repair.TryGetRepairCountdown(building, out float seconds))
            {
                continue;
            }

            int whole = Mathf.CeilToInt(seconds);
            string label = $"REPAIRING  {whole / 60}:{whole % 60:00}";
            GlobalPosition above = building.startPosition + (Vector3.up * (building.definition.height + 10f));
            DrawWorldPoint(camera, above, label, RepairColor, 30f);
        }
    }

    /// <summary>A shield bubble that is down, with how long until it is back, like a repair.</summary>
    private static void DrawShieldCountdowns(Camera camera, IReadOnlyList<Unit> selected)
    {
        if (IsSceneHiddenByMap)
        {
            return;
        }

        for (int i = 0; i < CommanderLeviathanShieldService.Count; i++)
        {
            if (!CommanderLeviathanShieldService.TryGetState(i, out Unit unit, out float health, out float max, out float seconds, out bool switchedOff))
            {
                continue;
            }

            string label;
            if (switchedOff)
            {
                label = "SHIELD OFF";
            }
            else if (health <= 0f)
            {
                int whole = Mathf.CeilToInt(seconds);
                label = $"SHIELD DOWN  {whole / 60}:{whole % 60:00}";
            }
            else if (IsSelected(selected, unit))
            {
                // A live bubble only shows its points on the unit the player has selected.
                label = $"SHIELD  {Mathf.CeilToInt(health)} / {Mathf.CeilToInt(max)}";
            }
            else
            {
                continue;
            }

            GlobalPosition above = unit.GlobalPosition() + (Vector3.up * (unit.definition.height + 30f));
            DrawWorldPoint(camera, above, label, RepairColor, 30f);
        }
    }

    private static bool IsSelected(IReadOnlyList<Unit> selected, Unit unit)
    {
        for (int i = 0; i < selected.Count; i++)
        {
            if (ReferenceEquals(selected[i], unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A munitions bunker, an ammo dump or a munitions truck: the stores a unit parked beside rearms from.</summary>
    private static bool IsResupplyStore(Unit unit)
    {
        if (CommanderSupplyStoreService.GetRearmer(unit) == null)
        {
            return false;
        }

        return CommanderSupplyStoreService.IsMobileStore(unit)
            || CommanderSupplyStoreService.IsDump(unit)
            || (unit.definition is BuildingDefinition building && building.buildingType == BuildingType.AMMO);
    }

    /// <summary>
    /// Where a unit rearms from this store on its own: the larger of the base game's own rearm range
    /// and the reach a unit parked at the end of a move order rearms within, the same measure
    /// <c>CommanderMoveService.TryRearmOnArrival</c> uses. 3D view and tactical map.
    /// </summary>
    private static void DrawResupplyRing(Camera camera, Unit store)
    {
        // A truck moves, so its ring is rebuilt every frame; a building's never changes and is cached.
        bool mobile = store is not Building;
        if (mobile || !ResupplyRings.TryGetValue(store, out GlobalPosition[] points))
        {
            Rearmer? rearmer = CommanderSupplyStoreService.GetRearmer(store);
            float reach = Mathf.Max(rearmer != null ? rearmer.Range : 0f, CommanderMoveService.GroundRearmReachMeters);
            GlobalPosition centre = rearmer != null ? rearmer.GetPosition() : store.GlobalPosition();
            points = mobile ? MobileRingPoints : new GlobalPosition[72];
            for (int i = 0; i < points.Length; i++)
            {
                float angle = i * Mathf.PI * 2f / points.Length;
                GlobalPosition ground = new(
                    centre.x + (Mathf.Cos(angle) * reach),
                    centre.y,
                    centre.z + (Mathf.Sin(angle) * reach));

                // The top surface, not bare terrain: a runway or pad stands over the terrain collider
                // and a terrain-only ring dipped under it. Global y 0 is sea level; over water it is the seabed.
                points[i] = new GlobalPosition(ground.x, Mathf.Max(CommanderBuildPreview.GetSurfaceY(ground, overUnits: true), 0f) + 3f, ground.z);
            }

            if (!mobile)
            {
                ResupplyRings[store] = points;
            }
        }

        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        bool sceneVisible = !IsSceneHiddenByMap;
        for (int i = 0; i < points.Length; i++)
        {
            GlobalPosition a = points[i];
            GlobalPosition b = points[(i + 1) % points.Length];
            if (sceneVisible)
            {
                Vector3 screenA = camera.WorldToScreenPoint(a.ToLocalPosition());
                Vector3 screenB = camera.WorldToScreenPoint(b.ToLocalPosition());
                if (screenA.z > 0f && screenB.z > 0f)
                {
                    CommanderUiTheme.DrawLine(CommanderUiScale.ScreenToGui(screenA), CommanderUiScale.ScreenToGui(screenB), ResupplyColor);
                }
            }

            if (map != null && map.TryWorldToMapScreen(a, out Vector2 mapA) && map.TryWorldToMapScreen(b, out Vector2 mapB))
            {
                CommanderUiTheme.DrawLine(CommanderUiScale.ScreenToGui(mapA), CommanderUiScale.ScreenToGui(mapB), ResupplyColor);
            }
        }
    }

    private static void DrawLandingZone(Camera camera, GlobalPosition position)
    {
        if (!IsSceneHiddenByMap)
        {
            DrawMarker(camera, position, "LZ", LzColor);
        }

        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        if (DynamicMap.mapMaximized && map != null && map.TryWorldToMapScreen(position, out Vector2 mapPoint))
        {
            CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(mapPoint), "LZ", LzColor, 18f);
        }
    }

    /// <summary>
    /// Projects one route leg into GUI space. A point behind the camera projects mirrored, so
    /// the leg is clipped against the camera plane instead of being dropped: dropping it is
    /// what made routes look like disconnected fragments whenever a waypoint went off screen.
    /// </summary>
    private static bool TryGetRouteSegment(
        Camera camera,
        GlobalPosition start,
        GlobalPosition end,
        out Vector2 from,
        out Vector2 to)
    {
        from = default;
        to = default;
        Vector3 a = start.ToLocalPosition();
        Vector3 b = end.ToLocalPosition();
        float aDepth = camera.transform.InverseTransformPoint(a).z;
        float bDepth = camera.transform.InverseTransformPoint(b).z;
        const float NearClip = 1f;

        if (aDepth < NearClip && bDepth < NearClip)
        {
            return false;
        }

        if (aDepth < NearClip)
        {
            a = Vector3.Lerp(a, b, (NearClip - aDepth) / (bDepth - aDepth));
        }
        else if (bDepth < NearClip)
        {
            b = Vector3.Lerp(b, a, (NearClip - bDepth) / (aDepth - bDepth));
        }

        from = CommanderUiScale.ScreenToGui(camera.WorldToScreenPoint(a));
        to = CommanderUiScale.ScreenToGui(camera.WorldToScreenPoint(b));
        return true;
    }

    /// <summary>
    /// Travel point: an open bracket with its number above it. Points off screen are clamped
    /// to the screen edge so the route still tells you which way it goes.
    /// </summary>
    private static void DrawRoutePoint(Camera camera, GlobalPosition position, string label, Color color, float size, string? below = null)
    {
        Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
        if (screen.z <= 0f)
        {
            return;
        }

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);

        // Clamped to the screen so a route that runs off the edge still shows which way it goes.
        guiPoint.x = Mathf.Clamp(guiPoint.x, 16f, Mathf.Max(16f, CommanderUiScale.Width - 16f));
        guiPoint.y = Mathf.Clamp(guiPoint.y, 16f, Mathf.Max(16f, CommanderUiScale.Height - 12f));
        CommanderUiTheme.DrawWorldMarker(guiPoint, label, color, size, below);
    }

    private static string FormatDistance(float metres)
    {
        return metres < 1000f
            ? $"{metres:0} m"
            : $"{metres / 1000f:0.0} km";
    }

    private static string GetSamLabel(CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => "RADAR",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform => "PLATFORM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower => "SITE CORE",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => "23MM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => "IRM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => "STRATOLANCE",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => "AMMO",
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => "FIRE CTRL",
            _ => "SITE",
        };
    }

    private static Color GetSamColor(CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => new Color(1f, 0.78f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform => new Color(0.72f, 0.7f, 1f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower => new Color(0.6f, 0.78f, 1f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => new Color(1f, 0.46f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => new Color(1f, 0.2f, 0.08f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => new Color(0.95f, 0.12f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => new Color(0.35f, 1f, 0.35f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => new Color(0.1f, 0.9f, 1f, 0.95f),
            _ => Color.white,
        };
    }

    /// <summary>Bracket parked below the cursor, so the cursor and what it is over stay clear.</summary>
    private static void DrawCursorMarker(string label, Color color)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(Input.mousePosition);
        CommanderUiTheme.DrawWorldMarker(new Vector2(guiPoint.x, guiPoint.y + 30f), label, color, 22f);
    }

    /// <summary>
    /// The other half: the player's own bases while the enemy drains them. The bar fills toward the
    /// moment the base is lost, which is the first of the game's two capture steps; after that the
    /// base is neutral and shows up above as CONTESTED.
    /// </summary>
    private static void DrawOwnBasesUnderCapture(
        Camera camera, bool mapOpen, bool sceneHidden, CommanderTacticalMapService? map)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        foreach (Airbase airbase in localHq.GetAirbases())
        {
            float grip = CommanderCaptureService.GetOwnerGrip(airbase);
            if (grip >= 0.995f || airbase.center == null)
            {
                continue;
            }

            string state = CommanderCapturePatches.IsContested(airbase.capture) ? "CONTESTED"
                : CommanderCapturePatches.IsRecovering(airbase.capture) ? "RECOVERING"
                : "BEING CAPTURED";
            string label = $"{state}  {CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant()}  "
                + BuildCaptureBar(1f - grip);
            GlobalPosition point = airbase.center.GlobalPosition();
            if (!sceneHidden)
            {
                DrawWorldPoint(camera, point, label, LosingBaseColor, 34f);
            }

            if (mapOpen && map != null && map.TryWorldToMapScreen(point, out Vector2 mapPoint))
            {
                CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(mapPoint), label, LosingBaseColor, 24f);
            }
        }
    }

    /// <summary>An ASCII progress bar, e.g. <c>[####------] 42%</c>.</summary>
    private static string BuildCaptureBar(float progress)
    {
        int filled = Mathf.Clamp(Mathf.RoundToInt(progress * CaptureBarCells), 0, CaptureBarCells);
        CaptureBar.Length = 0;
        CaptureBar.Append('[');
        for (int i = 0; i < CaptureBarCells; i++)
        {
            CaptureBar.Append(i < filled ? '#' : '-');
        }

        CaptureBar.Append("] ");
        CaptureBar.Append(Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f));
        CaptureBar.Append('%');
        return CaptureBar.ToString();
    }

    private static void DrawMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        DrawWorldPoint(camera, position, label, color, 26f);
    }

    private static void DrawLargeMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        DrawWorldPoint(camera, position, label, color, 44f);
    }

    private static void DrawWorldPoint(Camera camera, GlobalPosition position, string label, Color color, float size)
    {
        Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
        if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
        {
            return;
        }

        CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(screen), label, color, size);
    }

    /// <summary>
    /// Draws the remaining travel points of a RTS order in the 3D view, plus the attack marker
    /// when the route ends on a target. Under the <em>fullscreen</em> map the route lines come from
    /// <see cref="CommanderMapRouteRenderer"/> instead — camera-projected lines drawn over a map
    /// that fills the screen sweep across it as the camera turns, which is what made routes look
    /// wrong there — so only the numbered point labels are drawn on the map itself.
    /// </summary>
    private bool DrawOrderRoute(Camera camera, Unit unit)
    {
        IReadOnlyList<GlobalPosition> waypoints;
        int index;
        Unit? attackTarget;
        if (unit is Aircraft aircraft)
        {
            if (CommanderAirCommandService.Instance?.TryGetAircraftRoute(
                    aircraft, out waypoints, out index, out attackTarget) != true)
            {
                return false;
            }
        }
        else if (!moveService.TryGetOrder(unit, out waypoints, out index, out attackTarget))
        {
            return false;
        }

        routePoints.Clear();

        // The leg the unit is flying or driving right now starts at the unit, so the whole
        // plan reads as one unbroken line from the unit through every remaining point.
        routePoints.Add(unit.GlobalPosition());
        for (int i = Mathf.Max(0, index); i < waypoints.Count; i++)
        {
            routePoints.Add(waypoints[i]);
        }

        // Where the faction last saw each target, not where it really is: a contact that drops off
        // radar keeps its bracket on the last sighting until something sees it again. Every target
        // queued behind the first is bracketed too, so a multi-target order is visible as one.
        int firstAttackPoint = int.MaxValue;
        if (attackTarget != null && unit.NetworkHQ is FactionHQ hq)
        {
            moveService.CollectAttackTargets(unit, routeTargets);
            for (int i = 0; i < routeTargets.Count; i++)
            {
                if (hq.TryGetKnownPosition(routeTargets[i], out GlobalPosition attackPosition))
                {
                    firstAttackPoint = Mathf.Min(firstAttackPoint, routePoints.Count);
                    routePoints.Add(attackPosition);
                }
            }
        }

        bool attacking = firstAttackPoint < routePoints.Count;

        if (routePoints.Count < 2)
        {
            return false;
        }

        Color routeColor = attacking
            ? new Color(1f, 0.42f, 0.24f, 0.9f)
            : new Color(0.30f, 0.92f, 0.80f, 0.9f);
        bool mapOpen = DynamicMap.mapMaximized;
        bool sceneHidden = IsSceneHiddenByMap;
        if (!sceneHidden)
        {
            DrawRouteLines(camera, routeColor);
        }

        // Passed points still count, so the labels match the numbering the player placed.
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        for (int i = 1; i < routePoints.Count; i++)
        {
            bool attackPoint = i >= firstAttackPoint;

            // Several targets are numbered in the order they are worked; a single one is not, or
            // every ordinary attack order would read "ATTACK 1".
            string label = attackPoint
                ? (routePoints.Count - firstAttackPoint > 1
                    ? $"ATTACK {i - firstAttackPoint + 1}"
                    : "ATTACK")
                : (index + i).ToString(CultureInfo.CurrentCulture);

            // The attack bracket is sized to frame the target it is aimed at; travel points
            // stay small so a long route does not clutter the view.
            float size = attackPoint ? 48f : 14f;

            // Straight-line range from the unit now, not the distance along the route.
            string distance = FormatDistance(FastMath.Distance(routePoints[0], routePoints[i]));
            if (!sceneHidden)
            {
                DrawRoutePoint(camera, routePoints[i], label, routeColor, size, distance);
            }

            if (mapOpen && map != null && map.TryWorldToMapScreen(routePoints[i], out Vector2 mapPoint))
            {
                CommanderUiTheme.DrawWorldMarker(
                    CommanderUiScale.ScreenToGui(mapPoint), label, routeColor, attackPoint ? 20f : 14f, distance);
            }
        }

        return true;
    }

    /// <summary>A guard or escort order (blue DEFEND) or a pilot pickup (yellow RESCUE): a line from
    /// the unit to the one it is going for, bracketed the way an attack target is.</summary>
    private void DrawTargetLine(Camera camera, Unit unit, Unit target, string label, Color color)
    {
        routePoints.Clear();
        routePoints.Add(unit.GlobalPosition());
        routePoints.Add(target.GlobalPosition());
        if (!IsSceneHiddenByMap)
        {
            DrawRouteLines(camera, color);
            DrawRoutePoint(camera, routePoints[1], label, color, 48f);
        }

        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        if (DynamicMap.mapMaximized && map != null && map.TryWorldToMapScreen(routePoints[1], out Vector2 mapPoint))
        {
            CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(mapPoint), label, color, 20f);
        }
    }

    private void DrawRouteLines(Camera camera, Color color)
    {
        for (int i = 1; i < routePoints.Count; i++)
        {
            if (TryGetRouteSegment(camera, routePoints[i - 1], routePoints[i], out Vector2 from, out Vector2 to))
            {
                CommanderUiTheme.DrawLine(from, to, color, 2f);
            }
        }
    }

    /// <summary>
    /// Marks every capturable base the player has found — on the tactical map while it is open, in
    /// the 3D view while it is not, the same split the route markers use. Found bases stay marked
    /// for the rest of the mission, so the map is a record of where there is ground to take rather
    /// than of what happens to be in sensor range right now.
    /// </summary>
    private void DrawCaptureTargets(Camera camera)
    {
        CommanderCaptureService? capture = CommanderCaptureService.Instance;
        if (capture == null)
        {
            return;
        }

        capture.CopyDiscoveredTargets(captureTargets);
        bool mapOpen = DynamicMap.mapMaximized;
        bool sceneHidden = IsSceneHiddenByMap;
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        DrawOwnBasesUnderCapture(camera, mapOpen, sceneHidden, map);
        for (int i = 0; i < captureTargets.Count; i++)
        {
            CommanderCaptureService.CaptureTarget target = captureTargets[i];
            Color color = target.HeldByOther ? HostileBaseColor : NeutralBaseColor;

            // ASCII only: the IMGUI font has no guarantee about symbols.
            string label = $"CAPTURABLE  {target.Label.ToUpperInvariant()}";
            float progress = target.CaptureProgress;
            if (progress > 0.005f)
            {
                // The base game shows capture progress nowhere outside its debug overlay, so
                // without this a squad standing in the ring looks like a squad doing nothing.
                bool ours = target.IsCapturedBy(CommanderGameAccess.GetLocalHq());
                color = ours ? CapturingBaseColor : LosingBaseColor;

                // "CONTESTED" is a stalemate in the ring; an enemy taking the base is a moving bar.
                string step = CommanderCapturePatches.IsContested(target.Airbase?.capture) ? "CONTESTED"
                    : !ours ? "ENEMY CAPTURING"
                    : target.Neutralising ? "NEUTRALISING 1/2" : "CAPTURING 2/2";
                label = $"{step}  {target.Label.ToUpperInvariant()}  " + BuildCaptureBar(progress);
            }

            if (!sceneHidden)
            {
                DrawWorldPoint(camera, target.HoldPoint, label, color, 34f);
            }

            if (mapOpen && map != null && map.TryWorldToMapScreen(target.HoldPoint, out Vector2 mapPoint))
            {
                CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(mapPoint), label, color, 24f);
            }
        }
    }
}
