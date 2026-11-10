using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Team pings and the tutorial objective, drawn in the RTS view and on its map, and the cockpit
/// markers drawn while flying: those two plus every target the commander's units were ordered at
/// and every base being fought over, each with its distance. It is what lets a player fly to the
/// strike they just planned, and it reads only state the services already keep.
/// </summary>
internal static class CommanderCockpitMarkerUi
{
    private const float PulseSeconds = 2f;
    private const float FadeSeconds = 5f;
    private const float EdgeMargin = 56f;
    private const float RefreshSeconds = 0.5f;
    private const int MaxTargets = 12;

    // Indexed by CommanderPingService.PingKind: attack, defend, move, danger, scout, support.
    private static readonly Color[] KindColors =
    {
        new(1f, 0.32f, 0.26f, 0.95f),
        new(0.38f, 0.62f, 1f, 0.95f),
        new(0.42f, 1f, 0.46f, 0.95f),
        new(1f, 0.62f, 0.12f, 0.95f),
        new(0.35f, 0.95f, 1f, 0.95f),
        new(0.82f, 0.5f, 1f, 0.95f),
    };

    private static readonly Color ObjectiveColor = new(1f, 0.86f, 0.25f, 0.95f);
    private static readonly Color TargetColor = new(1f, 0.42f, 0.24f, 0.9f);
    private static readonly Color CapturingColor = new(0.35f, 1f, 0.5f, 0.95f);
    private static readonly Color LosingColor = new(1f, 0.3f, 0.3f, 0.95f);

    private static readonly List<Unit> Targets = new();
    private static readonly List<CommanderCaptureService.CaptureTarget> Bases = new();
    private static float nextRefreshAt;

    /// <summary>RTS mode: pings and the objective, in the 3D view unless a map covers it, and on the map when one is open.</summary>
    internal static void DrawCommanderView(Camera camera, bool sceneHidden)
    {
        CommanderPingService.Prune();
        bool mapOpen = DynamicMap.mapMaximized;
        IReadOnlyList<CommanderPingService.Ping> pings = CommanderPingService.Pings;
        for (int i = 0; i < pings.Count; i++)
        {
            CommanderPingService.Ping ping = pings[i];
            if (!CommanderPingService.IsVisible(ping))
            {
                continue;
            }

            GlobalPosition position = CommanderPingService.CurrentPosition(ping);
            Color color = PingColor(ping);
            string label = PingLabel(ping);
            if (!sceneHidden)
            {
                DrawPing(ping, DrawTracked(camera, position, label, color, PingSize(ping), clampToEdge: true), color, onMap: false);
                DrawPulse(camera, position, ping.Age, color);
            }

            if (mapOpen)
            {
                DrawPing(ping, DrawOnMap(position, label, color), color, onMap: true);
            }
        }

        if (CommanderPingService.Objective is GlobalPosition objective)
        {
            string label = $"OBJECTIVE  {CommanderPingService.ObjectiveLabel}";
            if (!sceneHidden)
            {
                DrawTracked(camera, objective, label, ObjectiveColor, 40f, clampToEdge: true);
            }

            if (mapOpen)
            {
                DrawOnMap(objective, label, ObjectiveColor);
            }
        }
    }

    /// <summary>Outside RTS mode: the cockpit, or the game's own map.</summary>
    internal static void DrawCockpit()
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        if (!CommanderSettings.CockpitMarkers)
        {
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        FactionHQ? hq = CommanderGameAccess.GetPlayerHq();
        if (camera == null || hq == null)
        {
            return;
        }

        CommanderPingService.Prune();
        bool mapOpen = DynamicMap.mapMaximized;
        GlobalPosition eye = camera.transform.position.ToGlobalPosition();
        IReadOnlyList<CommanderPingService.Ping> pings = CommanderPingService.Pings;
        for (int i = 0; i < pings.Count; i++)
        {
            CommanderPingService.Ping ping = pings[i];
            if (!CommanderPingService.IsVisible(ping))
            {
                continue;
            }

            GlobalPosition position = CommanderPingService.CurrentPosition(ping);
            Color color = PingColor(ping);
            string label = $"{PingLabel(ping)}  {Distance(eye, position)}";
            if (mapOpen)
            {
                DrawPing(ping, DrawOnMap(position, label, color), color, onMap: true);
                continue;
            }

            DrawPing(ping, DrawTracked(camera, position, label, color, PingSize(ping), clampToEdge: true), color, onMap: false);
            DrawPulse(camera, position, ping.Age, color);
        }

        if (CommanderPingService.Objective is GlobalPosition objective)
        {
            string label = $"OBJECTIVE  {CommanderPingService.ObjectiveLabel}  {Distance(eye, objective)}";
            if (mapOpen)
            {
                DrawOnMap(objective, label, ObjectiveColor);
            }
            else
            {
                DrawTracked(camera, objective, label, ObjectiveColor, 40f, clampToEdge: true);
            }
        }

        // The game's map already shows units and bases; these are for the view out of the canopy.
        if (mapOpen)
        {
            return;
        }

        RefreshTargets(hq);
        for (int i = 0; i < Targets.Count; i++)
        {
            Unit target = Targets[i];
            if (target != null && !target.disabled && hq.TryGetKnownPosition(target, out GlobalPosition position))
            {
                string label = $"TARGET  {CommanderGameAccess.GetUnitLabel(target).ToUpperInvariant()}  {Distance(eye, position)}";
                DrawTracked(camera, position, label, TargetColor, 30f, clampToEdge: false);
            }
        }

        for (int i = 0; i < Bases.Count; i++)
        {
            CommanderCaptureService.CaptureTarget target = Bases[i];
            if (target.Airbase == null || target.CaptureProgress <= 0.005f)
            {
                continue;
            }

            bool ours = target.IsCapturedBy(hq);
            string label = $"{(ours ? "CAPTURING" : "ENEMY CAPTURING")}  {target.Label.ToUpperInvariant()}  "
                + $"{Mathf.RoundToInt(target.CaptureProgress * 100f)}%  {Distance(eye, target.HoldPoint)}";
            DrawTracked(camera, target.HoldPoint, label, ours ? CapturingColor : LosingColor, 34f, clampToEdge: false);
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase?.center == null || CommanderCaptureService.GetOwnerGrip(airbase) >= 0.995f)
            {
                continue;
            }

            GlobalPosition point = airbase.center.GlobalPosition();
            string label = $"BEING CAPTURED  {CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant()}  {Distance(eye, point)}";

            // Losing a base is the one thing worth an edge marker without anyone pinging it.
            DrawTracked(camera, point, label, LosingColor, 34f, clampToEdge: true);
        }
    }

    /// <summary>The ping wheel while the key is held: six kinds round the press point, the one under
    /// the cursor bright, and whether the release leaves a temporary or a permanent marker. Called last
    /// in the frame's GUI, in and out of command mode, so no window or marker covers it.</summary>
    internal static void DrawWheel()
    {
        if (Event.current.type != EventType.Repaint || !CommanderPingService.WheelOpen)
        {
            return;
        }

        Vector2 center = CommanderPingService.WheelCenter;
        CommanderPingService.PingKind? hover = CommanderPingService.WheelHover;
        for (int i = 0; i < KindColors.Length; i++)
        {
            CommanderPingService.PingKind kind = (CommanderPingService.PingKind)i;
            float radians = i * 60f * Mathf.Deg2Rad;
            Vector2 point = center + (new Vector2(Mathf.Sin(radians), -Mathf.Cos(radians)) * CommanderPingService.WheelRadius);
            bool lit = hover == kind;
            Color color = KindColor(kind);
            color.a = lit ? 1f : 0.55f;
            CommanderUiTheme.DrawWorldMarker(point, string.Empty, color, lit ? 30f : 20f);
            CommanderUiTheme.DrawWorldLabel(point + new Vector2(0f, lit ? 34f : 28f), CommanderPingService.KindName(kind), color);
            if (lit)
            {
                CommanderUiTheme.DrawLine(center, point, color);
            }
        }

        bool permanent = CommanderPingService.ShiftHeld;
        string mode = hover == null
            ? "RELEASE HERE TO CANCEL"
            : permanent ? "PERMANENT MARKER" : "TEMPORARY, SHIFT FOR PERMANENT";
        CommanderUiTheme.DrawWorldMarker(center, string.Empty, new Color(1f, 1f, 1f, 0.6f), 10f);
        CommanderUiTheme.DrawWorldLabel(
            center + new Vector2(0f, CommanderPingService.WheelRadius + 58f),
            mode,
            permanent && hover != null ? ObjectiveColor : new Color(1f, 1f, 1f, 0.85f));
    }

    private static void RefreshTargets(FactionHQ hq)
    {
        if (!CommanderScheduler.IsDueRealtime(ref nextRefreshAt, RefreshSeconds))
        {
            return;
        }

        Targets.Clear();
        CommanderMoveService.Instance?.AppendOrderedTargets(hq, Targets);
        CommanderAirCommandService.Instance?.AppendOrderedTargets(hq, Targets);
        if (Targets.Count > MaxTargets)
        {
            Targets.RemoveRange(MaxTargets, Targets.Count - MaxTargets);
        }

        Bases.Clear();
        CommanderCaptureService.Instance?.CopyDiscoveredTargets(Bases);
    }

    private static string Distance(GlobalPosition from, GlobalPosition to)
    {
        float meters = FastMath.Distance(from, to);
        return meters < 1000f
            ? $"{meters.ToString("0", CultureInfo.InvariantCulture)} M"
            : $"{(meters / 1000f).ToString("0.0", CultureInfo.InvariantCulture)} KM";
    }

    private static Color KindColor(CommanderPingService.PingKind kind)
    {
        return KindColors[(int)kind];
    }

    /// <summary>The kind's colour, fading out over a temporary ping's last seconds.</summary>
    private static Color PingColor(CommanderPingService.Ping ping)
    {
        Color color = KindColor(ping.Kind);
        color.a *= Mathf.Clamp01(ping.Remaining / FadeSeconds);
        return color;
    }

    private static float PingSize(CommanderPingService.Ping ping)
    {
        return ping.Permanent ? 40f : 34f;
    }

    /// <summary>"#3 ATTACK  T-80  SIMON": the number only on a permanent marker, the unit when it is on one.</summary>
    private static string PingLabel(CommanderPingService.Ping ping)
    {
        string number = ping.Permanent ? $"#{ping.Number} " : string.Empty;
        string unit = string.Empty;
        if (ping.Unit != null)
        {
            unit = ping.Unit.disabled
                ? "  DESTROYED"
                : $"  {CommanderGameAccess.GetUnitLabel(ping.Unit).ToUpperInvariant()}";
        }

        return $"{number}{CommanderPingService.NameOf(ping)}{unit}{CommanderPingService.SenderLabel(ping, "  ")}";
    }

    /// <summary>
    /// Notes where the ping landed on screen, for the ping key to remove it, and puts a diamond round a
    /// permanent one so it reads as standing at a glance. The map and the 3D view keep separate
    /// points: with the compact map open a ping is drawn in both, and one shared point left only the
    /// map's, so the key did nothing on the marker in the 3D view.
    /// </summary>
    private static void DrawPing(CommanderPingService.Ping ping, Vector2? point, Color color, bool onMap)
    {
        if (point is not Vector2 at)
        {
            return;
        }

        if (onMap)
        {
            ping.LastMapGui = at;
            ping.LastMapDrawnAt = Time.unscaledTime;
        }
        else
        {
            ping.LastGui = at;
            ping.LastDrawnAt = Time.unscaledTime;
        }

        if (!ping.Permanent)
        {
            return;
        }

        const float reach = 13f;
        Vector2 up = at + new Vector2(0f, -reach);
        Vector2 right = at + new Vector2(reach, 0f);
        Vector2 down = at + new Vector2(0f, reach);
        Vector2 left = at + new Vector2(-reach, 0f);
        CommanderUiTheme.DrawLine(up, right, color);
        CommanderUiTheme.DrawLine(right, down, color);
        CommanderUiTheme.DrawLine(down, left, color);
        CommanderUiTheme.DrawLine(left, up, color);
    }

    /// <summary>A shrinking bracket for the first two seconds, so a new ping catches the eye.</summary>
    private static void DrawPulse(Camera camera, GlobalPosition position, float age, Color color)
    {
        if (age >= PulseSeconds)
        {
            return;
        }

        Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
        if (screen.z <= 0f)
        {
            return;
        }

        float progress = age / PulseSeconds;
        color.a *= 1f - progress;
        CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(screen), string.Empty, color, Mathf.Lerp(140f, 34f, progress));
    }

    private static Vector2? DrawOnMap(GlobalPosition position, string label, Color color)
    {
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        if (map == null || !map.TryWorldToMapScreen(position, out Vector2 mapPoint))
        {
            return null;
        }

        Vector2 point = CommanderUiScale.ScreenToGui(mapPoint);
        CommanderUiTheme.DrawWorldMarker(point, label, color, 22f);
        return point;
    }

    /// <summary>
    /// A bracket on the point, or with <paramref name="clampToEdge"/> a smaller one on the screen edge
    /// in its direction, with a tick pointing out toward it, so a ping behind you still says where to turn.
    /// Returns where it drew, or null when it drew nothing.
    /// </summary>
    private static Vector2? DrawTracked(Camera camera, GlobalPosition position, string label, Color color, float size, bool clampToEdge)
    {
        Vector3 world = position.ToLocalPosition();
        Vector3 screen = camera.WorldToScreenPoint(world);
        if (screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height)
        {
            Vector2 onScreen = CommanderUiScale.ScreenToGui(screen);
            CommanderUiTheme.DrawWorldMarker(onScreen, label, color, size);
            return onScreen;
        }

        if (!clampToEdge)
        {
            return null;
        }

        Vector3 local = camera.transform.InverseTransformPoint(world);
        Vector2 direction = new(local.x, -local.y);
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = Vector2.up;
        }

        direction.Normalize();
        float halfWidth = (CommanderUiScale.Width * 0.5f) - EdgeMargin;
        float halfHeight = (CommanderUiScale.Height * 0.5f) - EdgeMargin;
        float reach = Mathf.Min(
            halfWidth / Mathf.Max(Mathf.Abs(direction.x), 0.0001f),
            halfHeight / Mathf.Max(Mathf.Abs(direction.y), 0.0001f));
        Vector2 point = new Vector2(CommanderUiScale.Width * 0.5f, CommanderUiScale.Height * 0.5f) + (direction * reach);

        // The label is centred over the bracket, so on a side edge half of it would be off screen.
        float halfLabel = (CommanderUiTheme.WorldLabel.CalcSize(new GUIContent(label)).x * 0.5f) + 8f;
        point.x = Mathf.Clamp(point.x, halfLabel, Mathf.Max(halfLabel, CommanderUiScale.Width - halfLabel));
        float edgeSize = size * 0.6f;
        CommanderUiTheme.DrawWorldMarker(point, label, color, edgeSize);
        Vector2 tick = point + (direction * (edgeSize * 0.5f));
        CommanderUiTheme.DrawLine(tick, tick + (direction * 14f), color, 3f);
        return point;
    }
}
