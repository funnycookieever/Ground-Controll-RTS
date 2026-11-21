using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A built control tower is an airbase of its own: pilots can pick it on the spawn map and take off
/// from the hangars and pads built round it.
/// </summary>
/// <remarks>
/// The spawn menu only lists <c>FactionHQ.GetAirbases()</c>, so a tower that is just a building
/// gives a pilot nothing to click. The airbase is the game's own mission-editor custom airbase
/// (<c>Mission.SpawnCustomAirbase</c>), not capturable: destroying the tower is how it is lost.
/// A guest finds a custom airbase's settings in its copy of the mission by name, and a runtime one is
/// not in it, so the name carries the centre and <see cref="RestoreOnGuest"/> rebuilds the entry.
/// </remarks>
internal static class CommanderForwardBase
{
    private const string NamePrefix = "GCFWD|";
    private const string DisplayName = "Forward Base";

    /// <summary>How far apart the ground is sampled along a door runway.</summary>
    private const float RunwayStepMeters = 10f;

    /// <summary>Longest door runway measured: past the longest takeoff roll in the game.</summary>
    private const float MaxRunwayMeters = 2000f;

    /// <summary>Shorter than this is not worth a runway at all: nothing lifts off in it.</summary>
    private const float MinRunwayMeters = 60f;

    /// <summary>Steepest change in ground height per step that still counts as one runway (6 degrees).</summary>
    private const float MaxRisePerStepMeters = 1f;

    /// <summary>Clear half-width either side of the centreline: a small jet's half span.</summary>
    private const float RunwayClearHalfWidthMeters = 6f;

    private const float RunwayWidthMeters = 20f;

    /// <summary>How far out of its doors a hangar looks for map pavement to make its runway.</summary>
    private const float PavementReachMeters = 120f;

    /// <summary>Along the road from where a hangar's taxiway meets it to the threshold, so a wide jet
    /// has turned onto the road before the takeoff state lines it up, instead of pivoting into its own
    /// hangar.</summary>
    private const float RunwayLeadInMeters = 50f;

    /// <summary>How near its hangar's parking spot a departure must be on its first ask to own the strip.</summary>
    private const float HomeStripReachMeters = 40f;

    private static readonly Collider[] ObstacleHits = new Collider[16];

    /// <summary>The taxi lines laid down forward base runways, which a later hangar joins. Kept apart
    /// from the spurs, which run to them.</summary>
    private static readonly HashSet<Road> RunwayLines = new();

    /// <summary>Each forward base runway and the parking spot of the hangar that laid it.</summary>
    private static readonly Dictionary<Airbase.Runway, GlobalPosition> HangarStrips = new();

    /// <summary>The strip each departure was parked at when it first asked, null for none.</summary>
    private static readonly Dictionary<Aircraft, Airbase.Runway?> HomeStrips = new();

    private static readonly Dictionary<Unit, Airbase> AirbasesByTower = new();
    private static int counter;

    internal static bool IsForwardBase(Airbase? airbase)
    {
        return airbase != null
            && airbase.SavedAirbase?.UniqueName?.StartsWith(NamePrefix, System.StringComparison.Ordinal) == true;
    }

    /// <summary>Host only. Called for every finished building; does nothing unless it is a tower.</summary>
    internal static void OnBuilt(FactionHQ hq, Unit tower, IEnumerable<Unit> builtUnits)
    {
        if (!CommanderBuildPreview.IsControlTowerUnit(tower) || AirbasesByTower.ContainsKey(tower))
        {
            return;
        }

        ServerObjectManager? som = NetworkManagerNuclearOption.i?.ServerObjectManager;
        Mission? mission = MissionManager.CurrentMission;
        if (som == null || mission == null)
        {
            return;
        }

        GlobalPosition centre = tower.transform.GlobalPosition();
        SavedAirbase saved = CreateSaved(
            $"{NamePrefix}{++counter}|{Format(centre.x)}|{Format(centre.y)}|{Format(centre.z)}",
            hq.faction.factionName,
            centre);
        mission.airbases.Add(saved);
        Airbase airbase = mission.SpawnCustomAirbase(saved, som);
        AirbasesByTower[tower] = airbase;
        tower.onDisableUnit += OnTowerDisabled;
        int taxiRoads = CopyRoadsAsTaxiways(airbase, centre);

        // Hangars built before the tower were filed under the nearest real airbase.
        float radiusSquared = CommanderBuildPreview.TowerBuildRadiusMeters * CommanderBuildPreview.TowerBuildRadiusMeters;
        foreach (Unit unit in builtUnits)
        {
            if (unit is Building building
                && !unit.disabled
                && unit.NetworkHQ == hq
                && building.GetComponentInChildren<Hangar>(true) is Hangar hangar
                && FlatDistanceSquared(unit.transform.position, tower.transform.position) <= radiusSquared)
            {
                Refile(building, hangar, airbase);
                ConnectHangar(building, null);
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"{hq.faction.factionName} control tower {tower.UniqueName} opened a forward base with {taxiRoads} taxiway(s) from the roads round it.");
    }

    /// <summary>The faction's forward base within reach of <paramref name="position"/>, if any.</summary>
    internal static Airbase? FindNear(FactionHQ hq, Vector3 position)
    {
        float radiusSquared = CommanderBuildPreview.TowerBuildRadiusMeters * CommanderBuildPreview.TowerBuildRadiusMeters;
        foreach (KeyValuePair<Unit, Airbase> entry in AirbasesByTower)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && entry.Key.NetworkHQ == hq
                && entry.Value != null
                && FlatDistanceSquared(entry.Key.transform.position, position) <= radiusSquared)
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// A guest looks the airbase's settings up in its own copy of the mission, which was loaded before
    /// the tower went up. Rebuild them from the name first.
    /// </summary>
    internal static void RestoreOnGuest(Airbase airbase)
    {
        string name = airbase.NetworknetworkUniqueName;
        Mission? mission = MissionManager.CurrentMission;
        if (airbase.IsServer
            || mission == null
            || name == null
            || !name.StartsWith(NamePrefix, System.StringComparison.Ordinal)
            || mission.airbases.Exists(x => x.UniqueName == name))
        {
            return;
        }

        string[] parts = name.Split('|');
        if (parts.Length != 5
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            return;
        }

        // The faction comes from the HQ's airbase list in OnStartClientOnly itself.
        mission.airbases.Add(CreateSaved(name, string.Empty, new GlobalPosition(x, y, z)));
    }

    /// <summary>
    /// Host only, for every finished building. Joins a hangar to its field's taxiways and, at a
    /// forward base, gives a fixed-wing hangar a runway of its own when nothing else would let its
    /// aircraft take off.
    /// </summary>
    internal static void ConnectHangar(Unit? unit, CommanderBuildPreview? preview)
    {
        bool connected = CommanderBuiltHangar.ConnectToTaxiway(unit);
        Hangar? hangar = unit != null ? unit.GetComponentInChildren<Hangar>(true) : null;
        Airbase? airbase = hangar != null ? hangar.parentAirbase : null;
        if (unit == null
            || airbase == null
            || !IsForwardBase(airbase)
            || CommanderGameAccess.IsRotaryHangar(hangar))
        {
            return;
        }

        // A later hangar whose doors face a straight road of its own gets its own strip and takes off
        // there. It still joins the first runway, for airframes too long for its strip. AddRunway
        // refuses a strip down a road that already has one: with two runways on one road the takeoff
        // state picks whichever threshold is nearest, driving aircraft across into another hangar.
        bool first = airbase.runways == null || airbase.runways.Length == 0;
        if (!first && !connected)
        {
            JoinRunwayOverPavement(unit, hangar!, airbase);
        }

        AddDoorRunway(unit, hangar!, airbase);

        if (preview != null)
        {
            CommanderBuiltHangar.EnsureApron(unit, preview);
        }
    }

    /// <summary>How far the ground runs straight, level and clear from <paramref name="start"/>.</summary>
    internal static float MeasureDoorRun(GlobalPosition start, Vector3 forward, Unit? own, bool pavedOnly, out float endY)
    {
        float length = 0f;
        float? previousY = null;
        endY = start.y;
        Quaternion facing = Quaternion.LookRotation(forward, Vector3.up);
        for (float d = RunwayStepMeters; d <= MaxRunwayMeters; d += RunwayStepMeters)
        {
            GlobalPosition point = start + (forward * d);
            float y = CommanderBuildPreview.GetSurfaceY(point);
            if (CommanderGameAccess.IsBelowSeaLevel(new GlobalPosition(point.x, y, point.z))
                || (pavedOnly && !CommanderBuildPreview.IsPavement(point))
                || (previousY.HasValue && Mathf.Abs(y - previousY.Value) > MaxRisePerStepMeters)
                || IsObstructed(new GlobalPosition(point.x, y, point.z), facing, own))
            {
                break;
            }

            previousY = y;
            endY = y;
            length = d;
        }

        return length;
    }

    internal static void ResetSession()
    {
        AirbasesByTower.Clear();
        RunwayLines.Clear();
        HangarStrips.Clear();
        HomeStrips.Clear();
        counter = 0;
    }

    /// <summary>
    /// <c>Airbase.GetTakeoffRunway</c> takes the nearest threshold, so with a strip per hangar a jet
    /// could still drive to a neighbour's. A departure parked at a hangar with its own strip, long
    /// enough for it, is kept to that strip: every other forward base strip reads as out of reach.
    /// Decided on the first ask, at the doors, so it cannot change mid roll past another hangar.
    /// </summary>
    internal static bool IsAnotherHangarsStrip(Airbase.Runway runway, Aircraft aircraft)
    {
        if (!HangarStrips.ContainsKey(runway))
        {
            return false;
        }

        Airbase.Runway? home = GetHomeStrip(aircraft);
        return home != null && home != runway;
    }

    /// <summary>The departure's own hangar strip, decided on the first ask (see <see cref="IsAnotherHangarsStrip"/>).</summary>
    internal static Airbase.Runway? GetHomeStrip(Aircraft aircraft)
    {
        if (HangarStrips.Count == 0)
        {
            return null;
        }

        if (!HomeStrips.TryGetValue(aircraft, out Airbase.Runway? home))
        {
            home = null;
            float best = HomeStripReachMeters * HomeStripReachMeters;
            float needed = aircraft.definition != null ? aircraft.definition.aircraftParameters.takeoffDistance : 0f;
            GlobalPosition at = aircraft.transform.GlobalPosition();
            foreach (KeyValuePair<Airbase.Runway, GlobalPosition> strip in HangarStrips)
            {
                float distance = FlatDistanceSquared(strip.Value, at);
                if (strip.Key != null && strip.Key.Length >= needed && distance < best)
                {
                    best = distance;
                    home = strip.Key;
                }
            }

            HomeStrips[aircraft] = home;
        }

        return home;
    }

    /// <summary>The strip already decided for this aircraft, without deciding one.</summary>
    internal static Airbase.Runway? PeekHomeStrip(Aircraft aircraft)
    {
        return HomeStrips.TryGetValue(aircraft, out Airbase.Runway? home) ? home : null;
    }

    /// <summary>
    /// A custom airbase starts with an empty taxi network, and everything that joins a hangar to the
    /// field (the road snap, the door's taxiway spur, the apron) reads only that network, so a hangar
    /// at a forward base never connected to anything. The level's roads round the tower become its
    /// taxiways. Copies, so the spurs a hangar splits into them never touch the level's convoy roads.
    /// </summary>
    private static int CopyRoadsAsTaxiways(Airbase airbase, GlobalPosition centre)
    {
        RoadNetwork? roads = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        RoadNetwork? taxi = airbase.GetTaxiNetwork();
        if (roads == null || taxi == null)
        {
            return 0;
        }

        // Past the tower's ring by a hangar's door spur, so a hangar built at the edge still finds a road.
        float reach = CommanderBuildPreview.TowerBuildRadiusMeters + 200f;
        float reachSquared = reach * reach;
        int added = 0;
        foreach (Road road in roads.roads)
        {
            List<GlobalPosition>? points = road?.points;
            if (points == null || points.Count < 2 || road!.IsBridge())
            {
                continue;
            }

            // Each run of points whose segments pass near the tower. Highways are long straight
            // segments, often with both ends outside the ring.
            Road? copy = null;
            for (int i = 0; i < points.Count; i++)
            {
                bool near = FlatDistanceSquared(points[i], centre) <= reachSquared
                    || (i > 0 && SegmentDistanceSquared(centre, points[i - 1], points[i]) <= reachSquared)
                    || (i + 1 < points.Count && SegmentDistanceSquared(centre, points[i], points[i + 1]) <= reachSquared);
                if (near)
                {
                    copy ??= new Road();
                    copy.AddPoint(points[i]);
                    continue;
                }

                added += Keep(taxi, copy);
                copy = null;
            }

            added += Keep(taxi, copy);
        }

        taxi.RegenerateNetwork();
        return added;
    }

    /// <summary>
    /// A forward base has no runway, and tarmac in the map that is no airfield (a highway strip with no
    /// airbase on it) carries no road data either, so the AI's takeoff length check refused every
    /// launch. If the doors face map pavement within <see cref="PavementReachMeters"/>, the pavement is
    /// the runway: along it when it crosses the doors' path (a hangar beside a strip), straight out
    /// when it runs away from them (a hangar at the end of one). Otherwise the clear, level ground out
    /// of the doors is. A taxi line runs down the runway and a spur joins the doors to it.
    /// </summary>
    private static void AddDoorRunway(Unit unit, Hangar hangar, Airbase airbase)
    {
        Transform? spawn = hangar.GetSpawnTransform();
        RoadNetwork? taxi = airbase.GetTaxiNetwork();
        if (spawn == null || taxi == null)
        {
            return;
        }

        Vector3 forward = spawn.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        forward.Normalize();
        GlobalPosition door = spawn.GlobalPosition();
        float reached = -1f;
        for (float d = 0f; d <= PavementReachMeters; d += 2f)
        {
            if (CommanderBuildPreview.IsPavement(door + (forward * d)))
            {
                reached = d;
                break;
            }
        }

        if (reached < 0f)
        {
            float length = MeasureDoorRun(door, forward, unit, false, out _);
            AddRunway(unit, airbase, taxi, door, forward, 0f, length, door, "out of its doors over open ground");
            return;
        }

        // How far the pavement runs straight on, and how far either way across the doors' path from
        // the middle of it.
        GlobalPosition edge = door + (forward * reached);
        float straight = MeasureDoorRun(edge, forward, unit, true, out _);
        GlobalPosition middle = edge + (forward * Mathf.Min(straight * 0.5f, 60f));
        Vector3 across = Vector3.Cross(Vector3.up, forward);
        float left = MeasureDoorRun(middle, -across, unit, true, out _);
        float right = MeasureDoorRun(middle, across, unit, true, out _);
        if (straight >= left + right)
        {
            AddRunway(unit, airbase, taxi, door, forward, 0f, reached + straight, door, "straight down the pavement out of its doors");
        }
        else
        {
            // One way only, from where the doors' taxiway meets it: a runway both ways would send
            // aircraft taxiing at each other down the same line.
            AddRunway(unit, airbase, taxi, middle, right >= left ? across : -across, RunwayLeadInMeters, Mathf.Max(left, right), door, "along the pavement its doors face");
        }
    }

    /// <summary>
    /// A later hangar whose doors do not look across the runway's taxi line: a spur out of its doors to
    /// the middle of the pavement they face, then along the pavement to the nearest point of the line.
    /// </summary>
    private static void JoinRunwayOverPavement(Unit unit, Hangar hangar, Airbase airbase)
    {
        Transform? spawn = hangar.GetSpawnTransform();
        RoadNetwork? taxi = airbase.GetTaxiNetwork();
        if (spawn == null || taxi == null)
        {
            return;
        }

        Vector3 forward = spawn.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        forward.Normalize();
        GlobalPosition door = spawn.GlobalPosition();
        float reached = -1f;
        for (float d = 0f; d <= PavementReachMeters; d += 2f)
        {
            if (CommanderBuildPreview.IsPavement(door + (forward * d)))
            {
                reached = d;
                break;
            }
        }

        if (reached < 0f)
        {
            CommanderPlugin.Log.LogInfo($"{unit.name}: no pavement ahead of its doors to reach the runway by.");
            return;
        }

        GlobalPosition edge = door + (forward * reached);
        float straight = MeasureDoorRun(edge, forward, unit, true, out _);
        GlobalPosition middle = OnSurface(edge + (forward * Mathf.Min(straight * 0.5f, 60f)));

        List<Road> lines = new();
        foreach (Road road in taxi.roads)
        {
            if (RunwayLines.Contains(road))
            {
                lines.Add(road);
            }
        }

        Road? best = null;
        int bestIndex = 0;
        List<GlobalPosition>? route = lines.Count > 0 ? CommanderPavementRoute.Find(middle, lines, out best, out bestIndex) : null;
        if (route == null || best == null)
        {
            CommanderPlugin.Log.LogInfo($"{unit.name}: no way over pavement to the runway, so it was not joined.");
            return;
        }

        GlobalPosition junction = route[route.Count - 1];
        CommanderBuiltHangar.JoinAt(taxi, best, bestIndex, junction);
        AddLine(taxi, door - (forward * 5f), middle, false);
        float length = 0f;
        for (int i = 1; i < route.Count; i++)
        {
            AddLine(taxi, route[i - 1], route[i], false);
            length += FastMath.Distance(route[i - 1], route[i]);
        }

        taxi.RegenerateNetwork();
        CommanderPlugin.Log.LogInfo($"{unit.name}: joined the runway over {length:0} m of pavement in {route.Count - 1} legs.");
    }

    /// <summary>A runway <paramref name="length"/> metres along <paramref name="direction"/> from
    /// <paramref name="from"/>, with a taxi line down it and a spur from the doors to its start.</summary>
    private static void AddRunway(
        Unit unit,
        Airbase airbase,
        RoadNetwork taxi,
        GlobalPosition from,
        Vector3 direction,
        float leadIn,
        float length,
        GlobalPosition door,
        string how)
    {
        length -= leadIn;
        if (length < MinRunwayMeters)
        {
            CommanderPlugin.Log.LogInfo($"{unit.name}: only {length:0} m of runway {how}, so none was laid.");
            return;
        }

        GlobalPosition join = OnSurface(from);
        GlobalPosition start = OnSurface(from + (direction * leadIn));
        GlobalPosition end = OnSurface(from + (direction * (leadIn + length)));
        if (RunsAlongRunway(taxi, start, end))
        {
            CommanderPlugin.Log.LogInfo($"{unit.name}: the road {how} already carries a runway, so it uses that one.");
            return;
        }

        SavedRunway saved = new()
        {
            Name = $"{unit.UniqueName} strip",
            Takeoff = true,
            Landing = true,
            Width = RunwayWidthMeters,
            Start = start,
            End = end,
        };
        Airbase.Runway[] old = airbase.runways ?? System.Array.Empty<Airbase.Runway>();
        Airbase.Runway runway = Airbase.Runway.FromSaved(airbase, saved);
        runway.Setup(airbase, old.Length);
        Airbase.Runway[] runways = new Airbase.Runway[old.Length + 1];
        old.CopyTo(runways, 0);
        runways[old.Length] = runway;
        airbase.runways = runways;
        airbase.SavedAirbase?.runways.Add(saved);
        HangarStrips[runway] = door;

        // Road ends within 10 m merge into one node: the doors' spur meets the lead-in, which meets the
        // runway at its threshold.
        AddLine(taxi, join, start, true);
        AddLine(taxi, start, end, true);

        // From a few metres behind the parking spot, so the aircraft is on the line, not past its end.
        Vector3 toJoin = join.AsVector3() - door.AsVector3();
        toJoin.y = 0f;
        Vector3 outward = toJoin.magnitude > 10f ? toJoin.normalized : direction;
        AddLine(taxi, door - (outward * 5f), join, false);

        taxi.RegenerateNetwork();
        CommanderPlugin.Log.LogInfo(
            $"{unit.name}: laid a {length:0} m runway {how} at {airbase.SavedAirbase?.UniqueName}, pos:{start.x:0},{start.z:0} to pos:{end.x:0},{end.z:0}.");
    }

    /// <summary>True when a new strip would lie down a road one of this base's strips already runs along.</summary>
    private static bool RunsAlongRunway(RoadNetwork taxi, GlobalPosition start, GlobalPosition end)
    {
        float reachSquared = RunwayWidthMeters * RunwayWidthMeters;
        foreach (Road road in taxi.roads)
        {
            if (!RunwayLines.Contains(road) || road.points.Count < 2)
            {
                continue;
            }

            for (float t = 0f; t <= 1f; t += 0.1f)
            {
                GlobalPosition point = start + ((end - start) * t);
                if (SegmentDistanceSquared(point, road.points[0], road.points[road.points.Count - 1]) <= reachSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddLine(RoadNetwork taxi, GlobalPosition a, GlobalPosition b, bool runwayLine)
    {
        if (FastMath.Distance(a, b) < 1f)
        {
            return;
        }

        Road line = new();
        line.AddPoint(a);
        line.AddPoint(b);
        line.CalcLength();
        taxi.roads.Add(line);
        if (runwayLine)
        {
            RunwayLines.Add(line);
        }
    }

    private static GlobalPosition OnSurface(GlobalPosition point)
    {
        return new GlobalPosition(point.x, CommanderBuildPreview.GetSurfaceY(point), point.z);
    }

    /// <summary>True when something of the map, or another building, stands on the strip here.
    /// The hangar's own walls, the ground and the pavement under the box do not count.</summary>
    private static bool IsObstructed(GlobalPosition ground, Quaternion facing, Unit? own)
    {
        Vector3 centre = ground.ToLocalPosition() + (Vector3.up * 7f);
        Vector3 half = new(RunwayClearHalfWidthMeters, 5f, RunwayStepMeters * 0.5f);
        int count = Physics.OverlapBoxNonAlloc(centre, half, ObstacleHits, facing, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider hit = ObstacleHits[i];
            if (hit == null
                || (GameAssets.i != null && hit.sharedMaterial == GameAssets.i.terrainMaterial)
                || (own != null && hit.GetComponentInParent<Unit>() == own))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static int Keep(RoadNetwork taxi, Road? copy)
    {
        if (copy == null || copy.points.Count < 2)
        {
            return 0;
        }

        copy.CalcLength();
        taxi.roads.Add(copy);
        return 1;
    }

    private static void OnTowerDisabled(Unit tower)
    {
        tower.onDisableUnit -= OnTowerDisabled;
        if (!AirbasesByTower.TryGetValue(tower, out Airbase airbase))
        {
            return;
        }

        AirbasesByTower.Remove(tower);
        if (airbase == null)
        {
            return;
        }

        airbase.SetDisabled(true);
        airbase.CurrentHQ?.RemoveAirbase(airbase);
        MissionManager.CurrentMission?.airbases.Remove(airbase.SavedAirbase);
        ServerObjectManager? som = NetworkManagerNuclearOption.i?.ServerObjectManager;
        if (som != null && airbase.Identity != null)
        {
            som.Destroy(airbase.Identity, true);
        }
    }

    private static void Refile(Building building, Hangar hangar, Airbase airbase)
    {
        Airbase? old = hangar.parentAirbase;
        if (old == airbase)
        {
            return;
        }

        if (old != null)
        {
            // RemoveHangar is internal to the game assembly.
            AccessTools.Method(typeof(Airbase), "RemoveHangar")?.Invoke(old, new object[] { hangar });
            old.RemoveBuilding(building);
        }

        building.SetAirbase(airbase);
    }

    private static SavedAirbase CreateSaved(string uniqueName, string faction, GlobalPosition centre)
    {
        return new SavedAirbase
        {
            UniqueName = uniqueName,
            DisplayName = DisplayName,
            faction = faction,
            Capturable = false,
            Center = centre,
            SelectionPosition = centre,
        };
    }

    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static float SegmentDistanceSquared(GlobalPosition point, GlobalPosition a, GlobalPosition b)
    {
        Vector2 ab = new(b.x - a.x, b.z - a.z);
        Vector2 ap = new(point.x - a.x, point.z - a.z);
        float t = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
        return (ap - (ab * t)).sqrMagnitude;
    }

    private static float FlatDistanceSquared(GlobalPosition a, GlobalPosition b)
    {
        return FlatDistanceSquared(a.AsVector3(), b.AsVector3());
    }

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        Vector3 offset = a - b;
        offset.y = 0f;
        return offset.sqrMagnitude;
    }
}
