using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RoadPathfinding;
using UnityEngine;
using UnityEngine.Rendering;

namespace GroundControlRts;

/// <summary>
/// The see-through building that follows the cursor while a placement is armed, green where the
/// site is legal and red where it is not.
/// </summary>
/// <remarks>
/// The ghost is drawn with <c>Graphics.DrawMesh</c> straight off the prefab's own
/// meshes rather than by instantiating the prefab. Instantiating a building prefab runs its
/// <c>Awake</c>, which registers a networked <c>Unit</c> with the server — so the safe preview is
/// the one that never creates a GameObject at all. The cost is that a building whose look comes
/// from something other than a MeshRenderer (a particle effect, a light) does not show in the
/// preview, which for static structures is nothing.
/// </remarks>
internal sealed class CommanderBuildPreview
{
    /// <summary>A built control tower is a forward base: the faction may build this far round it.</summary>
    internal const float TowerBuildRadiusMeters = 1000f;

    /// <summary>Clear space demanded between a road centreline and the building's walls.</summary>
    private const float RoadClearanceMeters = 8f;

    /// <summary>How far past its roadside spot a hangar is pulled onto the road.</summary>
    private const float RoadSnapMeters = 40f;

    /// <summary>Clear space between a taxiway centreline and a hangar's walls. Wider than a road's
    /// because a taxiway is, and its traffic has wings.</summary>
    private const float TaxiClearanceMeters = 14f;

    /// <summary>
    /// Clear space demanded around a runway or taxiway, on top of half the runway's own width and
    /// the building's footprint. Tighter than a road's because the whole build radius sits on top
    /// of an airfield — this has to keep structures off the tarmac without banning building at the
    /// base at all.
    /// </summary>
    private const float RunwayClearanceMeters = 30f;

    /// <summary>Extra reach past the capture ring within which an airbase's surfaces are checked at
    /// all. A cheap early-out so a build site nowhere near an airfield costs one distance test.</summary>
    private const float AirfieldCheckMarginMeters = 2000f;

    /// <summary>How far from the cursor a hangar looks for the edge of map pavement.</summary>
    private const float PavementSnapMeters = 60f;

    private const float PavementStepMeters = 2f;

    /// <summary>Between a snapped hangar's front wall and the pavement edge, so the walls never
    /// stand on the pavement mesh, which the site check reads as a map structure.</summary>
    private const float PavementGapMeters = 2f;

    /// <summary>Highest a flat surface may stand over the ground or sea and still be pavement:
    /// a causeway deck is, a roof is not.</summary>
    private const float PavementMaxHeightMeters = 8f;

    /// <summary>Smallest footprint a definition is treated as having, for definitions that author none.</summary>
    private const float MinimumFootprintMeters = 10f;

    /// <summary>How much the ground may rise under a depot's exit, per metre out from the door.</summary>
    private const float ExitGradeAllowance = 0.2f;

    /// <summary>Smaller than this and it is a tree trunk or a fence post, not a building.</summary>
    private const float MapStructureMinMeters = 4f;

    /// <summary>Compass points probed around a dock site looking for water. Eight is enough to
    /// find a coastline; a finer sweep only costs raycasts to answer the same question.</summary>
    private static readonly Vector2[] ShoreProbeDirections =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        new(0.7071f, 0.7071f), new(-0.7071f, 0.7071f), new(0.7071f, -0.7071f), new(-0.7071f, -0.7071f),
    };

    private static readonly Color ValidTint = new(0.30f, 1f, 0.45f, 0.40f);
    private static readonly Color BlockedTint = new(1f, 0.28f, 0.22f, 0.40f);
    private static readonly Color BlueprintTint = new(0.35f, 0.70f, 1f, 0.30f);

    private static readonly float[] ExitProbeMeters = { 0f, 10f, 20f };

    private static readonly Collider[] StandProbeHits = new Collider[32];

    private static readonly List<RoadNetwork> TaxiScratch = new();

    private static readonly FieldInfo? RearmerSingleUseField = AccessTools.Field(typeof(Rearmer), "singleUse");
    private static readonly Dictionary<BuildingDefinition, bool> DumpDefinitions = new();

    private static readonly GlobalPosition[] TowerRingPoints = new GlobalPosition[72];

    /// <summary>One crossing distance per compass ray of the pavement snap (11.25 degrees apart).</summary>
    private static readonly float[] PavementEdges = new float[32];

    private static readonly RaycastHit[] PavementHits = new RaycastHit[16];

    /// <summary>Padded road bounding boxes, rebuilt when the level's road network changes. Static
    /// because the home guard's post search asks the same question with no preview instance.</summary>
    private static RoadNetwork? boundsNetwork;
    private static RoadBounds[] roadBounds = System.Array.Empty<RoadBounds>();

    private readonly Dictionary<BuildingDefinition, List<Part>> parts = new();
    private readonly Dictionary<BuildingDefinition, Vector3> footprints = new();
    private readonly Dictionary<BuildingDefinition, Vector3> centres = new();
    private readonly Dictionary<BuildingDefinition, float> reaches = new();
    private readonly Collider[] overlapHits = new Collider[32];
    private readonly MaterialPropertyBlock tintBlock = new();
    private readonly MaterialPropertyBlock blueprintBlock = new();

    private Material? ghostMaterial;
    private bool hasSite;
    private GlobalPosition site;
    private bool towerRing;
    private bool hasExitArrow;
    private GlobalPosition exitDoor;
    private Vector3 exitForward;
    private bool towerRingSampled;
    private GlobalPosition towerRingCentre;
    private GlobalPosition pavementCursor = new(float.MaxValue, 0f, float.MaxValue);
    private float pavementFront;
    private bool pavementFound;
    private GlobalPosition pavementSite;
    private Vector3 pavementNormal;

    /// <summary>The site under the cursor is legal to build on.</summary>
    internal bool SiteValid { get; private set; }

    /// <summary>
    /// The facing the next building is put down at. The rotate key steps it, and it is kept between
    /// placements so a row of buildings faces the same way.
    /// </summary>
    internal Quaternion Rotation { get; private set; } = Quaternion.identity;

    /// <summary>Why the site is blocked, or empty when it is not.</summary>
    internal string BlockedReason { get; private set; } = string.Empty;

    /// <summary>
    /// The door a vehicle depot rolls its vehicles out of, or a hangar its aircraft, in the prefab's
    /// own frame, or false for anything with no door.
    /// </summary>
    internal static bool TryGetDoor(BuildingDefinition candidate, out Vector3 localPosition, out Vector3 localForward)
    {
        GameObject prefab = candidate.unitPrefab;
        Transform? exit = null;
        if (prefab != null)
        {
            VehicleDepot? depot = prefab.GetComponentInChildren<VehicleDepot>(true);
            Hangar? hangar = prefab.GetComponentInChildren<Hangar>(true);
            if (depot != null)
            {
                exit = CommanderGameAccess.GetDepotSpawnTransform(depot);
            }
            else if (hangar != null)
            {
                exit = hangar.GetSpawnTransform();
            }
        }

        if (exit == null)
        {
            localPosition = default;
            localForward = Vector3.forward;
            return false;
        }

        localPosition = prefab!.transform.InverseTransformPoint(exit.position);
        localForward = prefab.transform.InverseTransformDirection(exit.forward);
        return true;
    }

    /// <summary>The yaw that turns a prefab so its <paramref name="localForward"/> points along
    /// <paramref name="toward"/>. Both are flattened first.</summary>
    internal static Quaternion FaceToward(Vector3 localForward, Vector3 toward)
    {
        localForward.y = 0f;
        toward.y = 0f;
        if (localForward.sqrMagnitude < 0.0001f || toward.sqrMagnitude < 0.0001f)
        {
            return Quaternion.identity;
        }

        return Quaternion.LookRotation(toward) * Quaternion.Inverse(Quaternion.LookRotation(localForward));
    }

    /// <summary>
    /// True when a building, the mod's or the map's own, stands within <paramref name="radius"/> of
    /// this spot. The probe starts a metre above the ground, so a road or an apron does not count,
    /// only something a vehicle would drive into. Vehicles never count, they move away on their own,
    /// and neither does <paramref name="ignore"/> (the depot a drive starts from).
    /// </summary>
    internal static bool IsBuiltOver(GlobalPosition point, float radius, Unit? ignore = null)
    {
        Vector3 ground = CommanderGameAccess.SnapToTerrain(point).ToLocalPosition();
        int hits = Physics.OverlapSphereNonAlloc(
            ground + (Vector3.up * (radius + 1f)),
            radius,
            StandProbeHits,
            ~0,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider hit = StandProbeHits[i];
            Unit? occupant = hit == null ? null : hit.GetComponentInParent<Unit>();
            if (occupant != null && ReferenceEquals(occupant, ignore))
            {
                continue;
            }

            if (occupant is Building ? !occupant.disabled : occupant == null && IsMapStructure(hit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the site sits within the build radius of an airbase this faction holds. A faction
    /// that holds no airbase can build nothing — which is also the point at which it has lost.
    /// </summary>
    internal static bool IsInsideBuildRadius(FactionHQ hq, GlobalPosition target)
    {
        return IsInsideBuildRadius(hq, target, CommanderSettings.BuildRadiusKm);
    }

    internal static bool IsInsideBuildRadius(FactionHQ hq, GlobalPosition target, float radiusKm)
    {
        return IsInsideBuildRadius(hq, target, radiusKm, out _);
    }

    /// <summary>
    /// As above, but a base that is being captured does not count, and <paramref name="contested"/>
    /// says that was the only reason the site failed.
    /// </summary>
    internal static bool IsInsideBuildRadius(FactionHQ hq, GlobalPosition target, float radiusKm, out bool contested)
    {
        contested = false;
        float radius = Mathf.Max(radiusKm, 0.1f) * 1000f;
        float radiusSquared = radius * radius;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            // A forward base keeps the tower's own 1 km, below.
            if (airbase == null || airbase.disabled || airbase.center == null || CommanderForwardBase.IsForwardBase(airbase))
            {
                continue;
            }

            Vector3 offset = airbase.center.position - target.ToLocalPosition();
            offset.y = 0f;
            if (offset.sqrMagnitude > radiusSquared)
            {
                continue;
            }

            if (CommanderCaptureService.IsBeingCaptured(airbase))
            {
                contested = true;
                continue;
            }

            return true;
        }

        return CommanderEconomyService.IsNearBuiltTower(hq, target);
    }

    /// <summary>
    /// True for an ammo dump's prefab: a building whose rearmer is <c>singleUse</c>, the same flag
    /// <c>CommanderSupplyStoreService.DumpRearmers</c> records on a live one. Asked of the prefab
    /// because a site is checked before anything is spawned; cached because the ghost asks per frame.
    /// </summary>
    internal static bool IsDumpDefinition(BuildingDefinition candidate)
    {
        if (!DumpDefinitions.TryGetValue(candidate, out bool dump))
        {
            Rearmer? rearmer = candidate.unitPrefab != null ? candidate.unitPrefab.GetComponentInChildren<Rearmer>(true) : null;
            dump = rearmer != null && RearmerSingleUseField?.GetValue(rearmer) is true;
            DumpDefinitions[candidate] = dump;
        }

        return dump;
    }

    /// <summary>True for the game's control tower. In the base game a tower only switches its
    /// airbase's runway lights, so a built one did nothing until it became a forward base.</summary>
    internal static bool IsControlTower(BuildingDefinition candidate)
    {
        return string.Equals(candidate.jsonKey, "controlTower1", System.StringComparison.OrdinalIgnoreCase)
            || (candidate.unitName != null
                && candidate.unitName.IndexOf("Control Tower", System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A standing building that is a real control tower. The Leviathan's Shield Generators and Deep
    /// beacons are spawned on the tower prefab, so their definition says tower, but they are not one.
    /// </summary>
    internal static bool IsControlTowerUnit(Unit unit)
    {
        return unit.definition is BuildingDefinition definition
            && IsControlTower(definition)
            && !CommanderLeviathanDeepService.IsTowerLookalike(unit);
    }

    /// <summary>
    /// True when the site sits on an airbase's operating surface — a runway or a taxiway, for any
    /// airbase on the map, not only the ones this faction holds.
    /// </summary>
    /// <remarks>
    /// Runways and taxiways are terrain, not <see cref="Unit"/>s, so the overlap test above never
    /// saw them and neither did the road test: an airfield's taxi network is its own
    /// <c>RoadNetwork</c>, not part of <c>LevelInfo.roadNetwork</c>. That is how a gold mine ended
    /// up on the landing strip, which then had nowhere to land. Every airbase counts, because a
    /// base you are about to capture is one you are about to want to fly from.
    /// </remarks>
    internal static bool IsOnAirfieldSurface(GlobalPosition target, float clearance, bool taxiways = true)
    {
        if (FactionRegistry.airbaseLookup == null)
        {
            return false;
        }

        Vector3 local = target.ToLocalPosition();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float reach = airbase.GetRadius() + AirfieldCheckMarginMeters;
            Vector3 offset = airbase.center.position - local;
            offset.y = 0f;
            if (offset.sqrMagnitude > reach * reach)
            {
                continue;
            }

            // A forward base's runways run out of its hangars' doors, with a taxi line along each, and
            // the next hangar belongs beside that line; the taxiway rule keeps its walls off it.
            bool forwardBase = CommanderForwardBase.IsForwardBase(airbase);
            Airbase.Runway[]? runways = forwardBase && !taxiways ? null : airbase.runways;
            for (int i = 0; runways != null && i < runways.Length; i++)
            {
                Airbase.Runway runway = runways[i];
                if (runway?.Start == null || runway.End == null)
                {
                    continue;
                }

                float limit = clearance + (runway.GetWidth() * 0.5f);
                if (SegmentDistanceSquared(
                        target,
                        runway.Start.position.ToGlobalPosition(),
                        runway.End.position.ToGlobalPosition())
                    <= limit * limit)
                {
                    return true;
                }
            }

            // A forward base's taxiways are copies of the level's roads, which the road rule already
            // keeps clear; the runway margin on them would ban building round the tower.
            RoadNetwork? taxi = taxiways && !forwardBase ? airbase.GetTaxiNetwork() : null;
            if (taxi != null && taxi.Exists() && IsOnNetwork(taxi, target, clearance))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for a piece of the map itself that a building must not be put inside: an airport's own
    /// hangars and terminal, and the concrete it stands on. They carry no <see cref="Unit"/> and
    /// <see cref="CommanderGameAccess.SnapToTerrain"/> reads only the terrain. Terrain never counts.
    /// </summary>
    internal static bool IsMapStructure(Collider? hit)
    {
        if (hit == null || hit.gameObject.layer != PhysicsLayers.Statics || GameAssets.i == null
            || hit.sharedMaterial == GameAssets.i.terrainMaterial)
        {
            return false;
        }

        Vector3 size = hit.bounds.size;
        return Mathf.Max(size.x, size.z) >= MapStructureMinMeters;
    }

    /// <summary>
    /// The highest ground a vehicle could be under here: terrain, or the first static surface above
    /// it that is not a unit. A kit depot faces a road, and a road on an embankment is a mesh, not
    /// terrain, so a terrain-only probe passed a door that opened into the bank.
    /// With <paramref name="overUnits"/> a building's roof counts as the surface too. A ring wants that:
    /// under a building on an apron there is no terrain collider, and the terrain fallback is sea level.
    /// </summary>
    internal static float GetSurfaceY(GlobalPosition point, bool overUnits = false)
    {
        float y = CommanderGameAccess.SnapToTerrain(point).y;
        Vector3 local = point.ToLocalPosition();
        if (Physics.Raycast(
                new Vector3(local.x, Datum.LocalSeaY + 10000f, local.z),
                Vector3.down,
                out RaycastHit hit,
                20000f,
                PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore)
            && (overUnits || hit.collider.GetComponentInParent<Unit>() == null))
        {
            y = Mathf.Max(y, hit.point.ToGlobalPosition().y);
        }

        return y;
    }

    /// <summary>
    /// True over map pavement: the top static surface here is flat, is not terrain and belongs to no
    /// unit (so a built hangar's own floor is not pavement), and stands within
    /// <see cref="PavementMaxHeightMeters"/> of the terrain or the sea under it, or is one of the map's road meshes.
    /// </summary>
    internal static bool IsPavement(GlobalPosition point)
    {
        return SurfaceKind(point, out _) == 'P';
    }

    /// <summary>What <see cref="IsPavement"/> saw: P pavement, T terrain on top, H too high over the
    /// ground, S not flat, U no game assets yet. <paramref name="top"/> is the top
    /// non-terrain surface.</summary>
    internal static char SurfaceKind(GlobalPosition point, out string top)
    {
        top = string.Empty;
        if (GameAssets.i == null)
        {
            return 'U';
        }

        Vector3 local = point.ToLocalPosition();
        int count = Physics.RaycastNonAlloc(
            new Vector3(local.x, Datum.LocalSeaY + 10000f, local.z),
            Vector3.down,
            PavementHits,
            20000f,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore);
        float terrainY = Datum.LocalSeaY;
        int best = -1;
        for (int i = 0; i < count; i++)
        {
            Collider collider = PavementHits[i].collider;
            if (collider == null)
            {
                continue;
            }

            if (collider.sharedMaterial == GameAssets.i.terrainMaterial)
            {
                terrainY = Mathf.Max(terrainY, PavementHits[i].point.y);
            }
            else if (collider.GetComponentInParent<Unit>() == null
                && (best < 0 || PavementHits[i].point.y > PavementHits[best].point.y))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return 'T';
        }

        RaycastHit hit = PavementHits[best];
        top = $"{hit.collider.name} {hit.point.y - terrainY:0.0} m up, normal {hit.normal.y:0.000}";

        // The map's road meshes are named for their terrain tile (terrain2_roads69) and stand well
        // above the terrain collider under them (16-20 m on Heartland's highway grid), so the name,
        // not the height, is what tells a road from a roof.
        bool mapRoad = hit.collider.name.StartsWith("terrain", System.StringComparison.OrdinalIgnoreCase);
        return !mapRoad && hit.point.y < terrainY - 0.5f ? 'T'
            : !mapRoad && hit.point.y - terrainY > PavementMaxHeightMeters ? 'H'
            : hit.normal.y <= 0.97f ? 'S'
            : 'P';
    }

    /// <summary>
    /// True on dry land with navigable water within reach: the site itself has to sit above the sea
    /// plane, and at least one probe around it has to come back below it. Ground height comes from
    /// <see cref="CommanderGameAccess.SnapToTerrain"/>, so over water it returns the seabed and
    /// <see cref="CommanderGameAccess.IsBelowSeaLevel"/> is the whole water test — no water
    /// collider needed.
    /// </summary>
    internal static bool IsShoreline(GlobalPosition target)
    {
        if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(target)))
        {
            return false;
        }

        float reach = Mathf.Max(20f, CommanderSettings.NavalDockShoreMeters);
        for (int i = 0; i < ShoreProbeDirections.Length; i++)
        {
            Vector2 direction = ShoreProbeDirections[i];
            GlobalPosition probe = new(
                target.x + (direction.x * reach),
                target.y,
                target.z + (direction.y * reach));
            if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(probe)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Is this spot on the level's road network? Same bounded test as the build ghost, with an early
    /// out, since the home guard asks it about a thousand times per ring rebuild.
    /// </summary>
    internal static bool IsOnLevelRoad(GlobalPosition target, float clearance)
    {
        return IsOnRoad(target, clearance);
    }

    /// <summary>Horizontal distance squared from a point to a segment. Height is irrelevant here.</summary>
    internal static float SegmentDistanceSquared(GlobalPosition point, GlobalPosition a, GlobalPosition b)
    {
        float abX = b.x - a.x;
        float abZ = b.z - a.z;
        float lengthSquared = (abX * abX) + (abZ * abZ);
        float t = lengthSquared <= 0.0001f
            ? 0f
            : Mathf.Clamp01((((point.x - a.x) * abX) + ((point.z - a.z) * abZ)) / lengthSquared);
        float dx = point.x - (a.x + (abX * t));
        float dz = point.z - (a.z + (abZ * t));
        return (dx * dx) + (dz * dz);
    }

    /// <summary>
    /// The road early-out is the whole reason a building could sit on a road, so it gets the one
    /// runnable check. A road running due north has a bounding box with no width at all, and the
    /// margin is what has to make it reach the site beside it.
    /// </summary>
    /// <summary>
    /// False for the prefab's effect meshes: pieces switched off until the game needs them,
    /// shadow-only proxies, and anything queued after opaque geometry. The Radar Station's red lamps
    /// are glow cards at queue 2950, and drawn into a blueprint they came out as a dark smudge
    /// floating over the building. A prefab asset is in no scene, so <c>activeInHierarchy</c> is
    /// always false there and the chain has to be walked by hand.
    /// </summary>
    internal static bool IsSolidPart(Transform part, Transform root, Renderer renderer)
    {
        if (!renderer.enabled || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
        {
            return false;
        }

        for (Transform? t = part; t != null; t = t == root ? null : t.parent)
        {
            if (!t.gameObject.activeSelf)
            {
                return false;
            }
        }

        Material[] materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] != null && materials[i].renderQueue <= (int)RenderQueue.GeometryLast)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The renderers that only exist as a lower detail copy: every LOD past the first draws the
    /// model again, so drawing them all would stack it on top of itself. A renderer the first LOD
    /// also lists is not one of them: the Chimera mod's FRCV and CRV-L keep their hull in both
    /// levels, and leaving it out drew the turret and tracks with no body.
    /// </summary>
    internal static HashSet<Renderer> CollectLowDetail(GameObject prefab)
    {
        HashSet<Renderer> lowDetail = new();
        LODGroup[] groups = prefab.GetComponentsInChildren<LODGroup>(true);
        for (int i = 0; i < groups.Length; i++)
        {
            LOD[] levels = groups[i].GetLODs();
            for (int level = levels.Length - 1; level >= 0; level--)
            {
                Renderer[] renderers = levels[level].renderers;
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r] == null)
                    {
                        continue;
                    }

                    if (level == 0)
                    {
                        lowDetail.Remove(renderers[r]);
                    }
                    else
                    {
                        lowDetail.Add(renderers[r]);
                    }
                }
            }
        }

        return lowDetail;
    }

    internal static void SelfCheck()
    {
        RoadBounds straight = new(100f, 100f, 0f, 500f);
        GlobalPosition beside = new(120f, 0f, 250f);
        if (!straight.Contains(beside, 30f))
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: a site 20 m off a straight road is not caught by a "
                    + "30 m clearance, so buildings can be placed on roads.");
        }

        if (straight.Contains(beside, 10f))
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: the road bounds margin is not being applied.");
        }

        // The runway rule uses the same segment measure, so a point beside the middle of a runway counts.
        GlobalPosition threshold = new(0f, 0f, 0f);
        GlobalPosition farEnd = new(0f, 0f, 2000f);
        GlobalPosition onTheStrip = new(10f, 0f, 1000f);
        if (SegmentDistanceSquared(onTheStrip, threshold, farEnd) > 40f * 40f)
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: a point 10 m off the middle of a 2 km runway does not "
                    + "measure as being on it, so buildings can be placed on runways.");
        }

        GlobalPosition wellClear = new(200f, 0f, 1000f);
        if (SegmentDistanceSquared(wellClear, threshold, farEnd) <= 40f * 40f)
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: the runway measure is not bounded, so nothing can be "
                    + "built anywhere near an airfield.");
        }

        // A roadside depot is turned so its door faces the road; turned wrong, the door faces a field.
        Vector3 turned = FaceToward(Vector3.right, Vector3.back) * Vector3.right;
        if (Vector3.Distance(turned, Vector3.back) > 0.01f)
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: a roadside depot is not turned to face the road.");
        }

        // The road rule measures from the walls. A hangar snapped beside a road has its front wall
        // 8 m off the centreline, and a road through the middle of it must still be caught.
        Vector2 half = new(20f, 10f);
        float besideWall = SegmentBoxDistanceSquared(new Vector2(-50f, 18f), new Vector2(50f, 18f), half);
        if (Mathf.Abs(besideWall - 64f) > 0.1f
            || SegmentBoxDistanceSquared(new Vector2(-50f, 0f), new Vector2(50f, 3f), half) > 0f
            || SegmentBoxDistanceSquared(new Vector2(30f, 20f), new Vector2(40f, 50f), half) < 100f)
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: the road distance to a building's walls is wrong, so "
                    + "hangars cannot sit beside a road or buildings can sit on one.");
        }
    }

    /// <summary>Turns the ghost by <paramref name="degrees"/> about the vertical.</summary>
    internal void Rotate(float degrees)
    {
        Rotation = Quaternion.Euler(0f, Mathf.Repeat(Rotation.eulerAngles.y + degrees, 360f), 0f);
    }

    /// <summary>
    /// The ring the armed control tower would open for building, drawn in the GUI pass. The points
    /// follow the terrain so the ring reads on a hillside; they are only re-sampled when the cursor
    /// has moved, since that is a raycast per point.
    /// </summary>
    internal void DrawTowerRing(Camera camera)
    {
        if (!hasSite || !towerRing)
        {
            return;
        }

        Color color = SiteValid ? new Color(0.35f, 1f, 0.5f, 0.9f) : new Color(1f, 0.35f, 0.28f, 0.9f);
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        for (int i = 0; i < TowerRingPoints.Length; i++)
        {
            GlobalPosition a = TowerRingPoints[i];
            GlobalPosition b = TowerRingPoints[(i + 1) % TowerRingPoints.Length];
            Vector3 screenA = camera.WorldToScreenPoint(a.ToLocalPosition());
            Vector3 screenB = camera.WorldToScreenPoint(b.ToLocalPosition());
            if (screenA.z > 0f && screenB.z > 0f)
            {
                CommanderUiTheme.DrawLine(CommanderUiScale.ScreenToGui(screenA), CommanderUiScale.ScreenToGui(screenB), color);
            }

            if (map != null && map.TryWorldToMapScreen(a, out Vector2 mapA) && map.TryWorldToMapScreen(b, out Vector2 mapB))
            {
                CommanderUiTheme.DrawLine(CommanderUiScale.ScreenToGui(mapA), CommanderUiScale.ScreenToGui(mapB), color);
            }
        }
    }

    internal void Hide()
    {
        hasSite = false;
        towerRing = false;
        hasExitArrow = false;
        SiteValid = false;
        BlockedReason = string.Empty;
    }

    /// <summary>Drops every cached mesh list and the material the ghost is drawn with.</summary>
    internal void Clear()
    {
        Hide();
        parts.Clear();
        footprints.Clear();
        centres.Clear();
        reaches.Clear();
        boundsNetwork = null;
        roadBounds = System.Array.Empty<RoadBounds>();
        if (ghostMaterial != null)
        {
            Object.Destroy(ghostMaterial);
            ghostMaterial = null;
        }
    }

    /// <summary>
    /// Per frame while a placement is armed: re-aim at the cursor, re-check the site and draw.
    /// The caller passes the armed definition every frame rather than arming the preview
    /// separately, so a new way to start a build cannot forget to switch the ghost with it.
    /// </summary>
    internal void Tick(BuildingDefinition definition, FactionHQ? hq, Vector2 screenPosition, bool navalDock = false)
    {
        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition ground))
        {
            hasSite = false;
            SiteValid = false;
            BlockedReason = "No ground under the cursor.";
            return;
        }

        Quaternion facing = Rotation;
        if (TrySnapToRoad(definition, ground, out GlobalPosition roadside, out Quaternion roadFacing))
        {
            ground = roadside;
            facing = roadFacing;
        }

        site = CommanderGameAccess.SnapToTerrain(ground);
        hasSite = true;
        SiteValid = Evaluate(definition, site, hq, navalDock, facing, out string reason);
        BlockedReason = reason;
        towerRing = IsControlTower(definition);
        if (towerRing)
        {
            UpdateTowerRing();
        }

        GlobalPosition buildGround = GetBuildGround(definition, site, facing);
        hasExitArrow = false;
        if (CommanderBuiltHangar.IsHangar(definition) && TryGetDoor(definition, out Vector3 doorLocal, out Vector3 doorForward))
        {
            hasExitArrow = true;

            // SpawnBuilding places the prefab at ground + rotation * spawnOffset.
            exitDoor = buildGround + (facing * (definition.spawnOffset + doorLocal));
            exitForward = facing * doorForward;
            exitForward.y = 0f;
            exitForward.Normalize();
        }

        Draw(definition, buildGround, facing);
    }

    /// <summary>
    /// An arrow out of an armed hangar's doors, the way its aircraft will roll out, drawn in the GUI
    /// pass. Snapping to a road turns the hangar for the player, so the doors are not always where
    /// they rotated them.
    /// </summary>
    internal void DrawExitArrow(Camera camera)
    {
        if (!hasSite || !hasExitArrow)
        {
            return;
        }

        Color color = SiteValid ? new Color(0.35f, 1f, 0.5f, 0.95f) : new Color(1f, 0.35f, 0.28f, 0.95f);
        Vector3 side = Vector3.Cross(Vector3.up, exitForward);
        GlobalPosition tail = exitDoor + (Vector3.up * 1.5f);
        GlobalPosition tip = tail + (exitForward * 45f);
        GlobalPosition headLeft = tip - (exitForward * 12f) + (side * 8f);
        GlobalPosition headRight = tip - (exitForward * 12f) - (side * 8f);
        DrawWorldLine(camera, tail, tip, color);
        DrawWorldLine(camera, tip, headLeft, color);
        DrawWorldLine(camera, tip, headRight, color);

        static void DrawWorldLine(Camera camera, GlobalPosition a, GlobalPosition b, Color color)
        {
            Vector3 screenA = camera.WorldToScreenPoint(a.ToLocalPosition());
            Vector3 screenB = camera.WorldToScreenPoint(b.ToLocalPosition());
            if (screenA.z > 0f && screenB.z > 0f)
            {
                CommanderUiTheme.DrawLine(CommanderUiScale.ScreenToGui(screenA), CommanderUiScale.ScreenToGui(screenB), color, 3f);
            }
        }
    }

    /// <summary>
    /// The ground a building at <paramref name="target"/> is put down on: the terrain under its
    /// middle, or for a hangar the level floor over its whole footprint.
    /// </summary>
    internal GlobalPosition GetBuildGround(BuildingDefinition candidate, GlobalPosition target, Quaternion rotation)
    {
        return CommanderBuiltHangar.HasFloor(candidate)
            ? CommanderBuiltHangar.GetFloorGround(this, candidate, target, rotation, out _)
            : CommanderGameAccess.SnapToTerrain(target);
    }

    /// <summary>
    /// Pulls a hangar onto the nearest road within reach of <paramref name="cursor"/>, front wall
    /// beside the tarmac and door facing it, so what it launches taxis straight out onto the road.
    /// False, and nothing moved, for anything else or with no road near.
    /// </summary>
    internal bool TrySnapToRoad(
        BuildingDefinition candidate,
        GlobalPosition cursor,
        out GlobalPosition site,
        out Quaternion rotation)
    {
        site = cursor;
        rotation = Rotation;
        if (!CommanderBuiltHangar.IsHangar(candidate) || !TryGetDoor(candidate, out _, out Vector3 door))
        {
            return false;
        }

        door.y = 0f;
        if (door.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        door.Normalize();

        // Pivot to front wall along the door's axis; the clearance of whichever road wins goes on top.
        Vector3 half = GetFootprint(candidate);
        Vector3 middle = GetFootprintCentre(candidate) + candidate.spawnOffset;
        float front = (middle.x * door.x) + (middle.z * door.z)
            + (Mathf.Abs(door.x) * half.x) + (Mathf.Abs(door.z) * half.z);
        float taxiSetback = Mathf.Max(TaxiClearanceMeters, CommanderBuiltHangar.GetTaxiwaySetback(candidate));
        float reach = front + taxiSetback + 1f + RoadSnapMeters;

        float best = reach * reach;
        GlobalPosition a = default;
        GlobalPosition b = default;
        bool found = false;
        bool taxiway = false;
        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network != null)
        {
            EnsureRoadBounds(network);
            for (int roadIndex = 0; roadIndex < network.roads.Count && roadIndex < roadBounds.Length; roadIndex++)
            {
                if (roadBounds[roadIndex].Contains(cursor, reach)
                    && ClosestSegment(network.roads[roadIndex], cursor, ref best, ref a, ref b))
                {
                    found = true;
                }
            }
        }

        // The roads round an airfield are its taxi network, not the level's, and a hangar belongs
        // on those most of all.
        // A forward base's taxi lines are drawn, not paved (the runway down the middle of a road, spurs
        // across the grass), so a hangar snapped to one stood facing another hangar's doors; the
        // pavement snap finds the road's edge instead.
        CollectTaxiNetworks(cursor, reach, forwardBases: false);
        for (int n = 0; n < TaxiScratch.Count; n++)
        {
            List<Road> roads = TaxiScratch[n].roads;
            for (int roadIndex = 0; roadIndex < roads.Count; roadIndex++)
            {
                if (ClosestSegment(roads[roadIndex], cursor, ref best, ref a, ref b))
                {
                    found = true;
                    taxiway = true;
                }
            }
        }

        float setback = front + (taxiway ? taxiSetback : RoadClearanceMeters) + 1f;
        Vector3 along = new(b.x - a.x, 0f, b.z - a.z);
        float length = along.magnitude;
        float snapReach = setback + RoadSnapMeters;
        if (!found || length < 0.1f || best > snapReach * snapReach)
        {
            return TrySnapToPavement(cursor, door, front, out site, out rotation);
        }

        along /= length;
        float t = Mathf.Clamp(((cursor.x - a.x) * along.x) + ((cursor.z - a.z) * along.z), 0f, length);
        GlobalPosition onRoad = new(a.x + (along.x * t), cursor.y, a.z + (along.z * t));
        Vector3 side = Vector3.Cross(Vector3.up, along);
        if (((cursor.x - onRoad.x) * side.x) + ((cursor.z - onRoad.z) * side.z) < 0f)
        {
            side = -side;
        }

        // A highway's pavement reaches well past the clearance, which left the hangar standing on it
        // or, read from above, nowhere near the road. The walls go to the pavement's edge.
        float edge = 0f;
        while (edge < PavementSnapMeters && IsPavement(onRoad + (side * (edge + 1f))))
        {
            edge += 1f;
        }

        setback = Mathf.Max(setback, front + edge + PavementGapMeters);
        site = onRoad + (side * setback);
        rotation = FaceToward(door, -side);
        return true;
    }

    /// <summary>Stops drawing for this frame without disarming the placement.</summary>
    internal void Suspend()
    {
        hasSite = false;
    }

    /// <summary>True when a placement click at <paramref name="target"/> should be allowed.</summary>
    internal bool IsSiteAllowed(
        BuildingDefinition candidate,
        GlobalPosition target,
        FactionHQ? hq,
        out string reason,
        bool navalDock = false,
        Quaternion? rotation = null)
    {
        return Evaluate(
            candidate, CommanderGameAccess.SnapToTerrain(target), hq, navalDock, rotation ?? Quaternion.identity, out reason);
    }

    /// <summary>
    /// A site beside a road within <paramref name="maxMeters"/> of <paramref name="anchor"/>, turned
    /// so a depot's door faces the road, set back just far enough to pass the road rule. Every try
    /// goes through <see cref="Evaluate"/>, so it is only ever a site any other build could use.
    /// </summary>
    internal bool TryFindRoadsideSite(
        BuildingDefinition candidate,
        FactionHQ hq,
        GlobalPosition anchor,
        float maxMeters,
        System.Random dice,
        int attempts,
        out GlobalPosition site,
        out Quaternion rotation)
    {
        site = default;
        rotation = Quaternion.identity;
        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network == null)
        {
            return false;
        }

        List<GlobalPosition> segments = new();
        float maxSquared = maxMeters * maxMeters;
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];
            for (int i = 1; road?.points != null && i < road.points.Count; i++)
            {
                if (SegmentDistanceSquared(anchor, road.points[i - 1], road.points[i]) <= maxSquared)
                {
                    segments.Add(road.points[i - 1]);
                    segments.Add(road.points[i]);
                }
            }
        }

        if (segments.Count == 0)
        {
            return false;
        }

        TryGetDoor(candidate, out _, out Vector3 localForward);
        Vector3 footprint = GetFootprint(candidate);
        float setback = Mathf.Max(footprint.x, footprint.z) + RoadClearanceMeters + 2f;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int pick = dice.Next(segments.Count / 2) * 2;
            GlobalPosition a = segments[pick];
            GlobalPosition b = segments[pick + 1];
            Vector3 along = new(b.x - a.x, 0f, b.z - a.z);
            if (along.sqrMagnitude < 1f)
            {
                continue;
            }

            float t = (float)dice.NextDouble();
            Vector3 side = Vector3.Cross(Vector3.up, along.normalized) * (dice.Next(2) == 0 ? 1f : -1f);
            GlobalPosition roadPoint = new(a.x + (along.x * t), a.y, a.z + (along.z * t));
            GlobalPosition candidateSite = roadPoint + (side * setback);

            // The segment only has to pass near the anchor; a long one reaches kilometres past it.
            if (FastMath.Distance(candidateSite, anchor) > maxMeters)
            {
                continue;
            }

            Quaternion facing = FaceToward(localForward, -side);
            if (Evaluate(candidate, CommanderGameAccess.SnapToTerrain(candidateSite), hq, false, facing, out _))
            {
                site = candidateSite;
                rotation = facing;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Half-extents of the building. The authored <c>width</c>/<c>length</c>/<c>height</c> are the
    /// numbers the game shows in its encyclopedia, and for most buildings they are either far
    /// smaller than the structure or not authored at all — which is how a refinery whose authored
    /// footprint fell back to the 10 m minimum was allowed to straddle a highway. So the prefab's
    /// own meshes get a vote: whichever is larger wins. Cached per definition; a prefab does not
    /// change size.
    /// </summary>
    internal Vector3 GetFootprint(BuildingDefinition candidate)
    {
        if (footprints.TryGetValue(candidate, out Vector3 cached))
        {
            return cached;
        }

        Vector3 footprint = new(
            Mathf.Max(candidate.width, MinimumFootprintMeters) * 0.5f,
            Mathf.Max(candidate.height, MinimumFootprintMeters) * 0.5f,
            Mathf.Max(candidate.length, MinimumFootprintMeters) * 0.5f);

        List<Part> meshes = GetParts(candidate);
        bool any = false;
        Bounds bounds = default;
        for (int i = 0; i < meshes.Count; i++)
        {
            Bounds local = meshes[i].Mesh.bounds;

            // Eight corners rather than the centre and extents: the part matrix can rotate.
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = local.center + Vector3.Scale(
                    local.extents,
                    new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                point = meshes[i].Local.MultiplyPoint3x4(point);
                if (any)
                {
                    bounds.Encapsulate(point);
                }
                else
                {
                    bounds = new Bounds(point, Vector3.zero);
                    any = true;
                }
            }
        }

        if (any)
        {
            footprint = Vector3.Max(footprint, bounds.extents);
        }

        footprints[candidate] = footprint;
        centres[candidate] = any ? bounds.center : Vector3.zero;
        return footprint;
    }

    /// <summary>Middle of the prefab's meshes, from its pivot: the footprint box is centred here.</summary>
    internal Vector3 GetFootprintCentre(BuildingDefinition candidate)
    {
        GetFootprint(candidate);
        return centres[candidate];
    }

    /// <summary>
    /// How far the building reaches from the point it is sited on, in any direction. The footprint
    /// is half the mesh box, which is right only when the box is centred on the pivot: the Fuel
    /// Exchange's corner walls sit off-centre, so a builder parked at "half the box plus a margin"
    /// ended up inside them when the building rose and was boxed in. This measures every mesh corner
    /// from the pivot, plus the spawn offset, so a ring of this radius clears the whole structure.
    /// </summary>
    internal float GetReach(BuildingDefinition candidate)
    {
        if (reaches.TryGetValue(candidate, out float cached))
        {
            return cached;
        }

        Vector3 footprint = GetFootprint(candidate);
        float reach = new Vector2(footprint.x, footprint.z).magnitude;
        List<Part> meshes = GetParts(candidate);
        for (int i = 0; i < meshes.Count; i++)
        {
            Bounds local = meshes[i].Mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = meshes[i].Local.MultiplyPoint3x4(local.center + Vector3.Scale(
                    local.extents,
                    new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f)));
                reach = Mathf.Max(reach, new Vector2(point.x, point.z).magnitude);
            }
        }

        reach += new Vector2(candidate.spawnOffset.x, candidate.spawnOffset.z).magnitude;
        reaches[candidate] = reach;
        return reach;
    }

    /// <summary>
    /// A placed blueprint: the see-through outline where the building will stand, and the building
    /// itself in its own materials, sunk into the ground by the share still to be built. The terrain
    /// hides whatever is still underground, and that is the whole "rising out of the ground" effect.
    /// </summary>
    internal void DrawBlueprint(BuildingDefinition candidate, GlobalPosition target, float progress, Quaternion rotation)
    {
        blueprintBlock.SetColor("_BaseColor", BlueprintTint);
        blueprintBlock.SetColor("_Color", BlueprintTint);

        // SpawnBuilding places the prefab at ground + rotation * spawnOffset, so the picture does too.
        GlobalPosition ground = CommanderBuiltHangar.HasFloor(candidate)
            ? CommanderBuiltHangar.GetFloorGround(this, candidate, target, rotation, out _)
            : target;
        Vector3 origin = ground.ToLocalPosition() + (rotation * candidate.spawnOffset);
        DrawParts(candidate, origin, rotation, blueprintBlock);
        if (progress > 0f)
        {
            // Full height plus a metre, so nothing pokes out of the ground before the work starts.
            float depth = ((GetFootprint(candidate).y * 2f) + 1f) * (1f - progress);
            DrawParts(candidate, origin + (Vector3.down * depth), rotation, null);
        }
    }

    /// <summary>
    /// True when a depot here would put its vehicles into the ground. A building is dropped onto the
    /// terrain under its middle only, so on a slope the end with the door is buried, and
    /// <c>VehicleDepot.TrySpawnVehicle</c> spawns at that door and pushes 20 m along its forward
    /// axis: the Champions kit's depots did exactly that. The ground is sampled at the door and along
    /// that 20 m, allowed to rise a little with distance.
    /// </summary>
    private static bool IsDepotExitBuried(BuildingDefinition candidate, GlobalPosition ground, Quaternion rotation)
    {
        if (!TryGetDoor(candidate, out Vector3 localPosition, out Vector3 localForward))
        {
            return false;
        }

        // SpawnBuilding places the prefab at ground + rotation * spawnOffset.
        GlobalPosition door = ground + (rotation * (candidate.spawnOffset + localPosition));
        Vector3 forward = rotation * localForward;
        forward.y = 0f;
        forward.Normalize();
        for (int i = 0; i < ExitProbeMeters.Length; i++)
        {
            float along = ExitProbeMeters[i];
            if (GetSurfaceY(door + (forward * along)) > door.y + 0.5f + (along * ExitGradeAllowance))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Fills <see cref="TaxiScratch"/> with the taxi networks of every airbase whose
    /// field reaches within <paramref name="reach"/> of the point.</summary>
    private static void CollectTaxiNetworks(GlobalPosition target, float reach, bool forwardBases = true)
    {
        TaxiScratch.Clear();
        if (FactionRegistry.airbaseLookup == null)
        {
            return;
        }

        Vector3 local = target.ToLocalPosition();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || (!forwardBases && CommanderForwardBase.IsForwardBase(airbase)))
            {
                continue;
            }

            float limit = airbase.GetRadius() + AirfieldCheckMarginMeters + reach;
            Vector3 offset = airbase.center.position - local;
            offset.y = 0f;
            RoadNetwork? taxi = airbase.GetTaxiNetwork();
            if (offset.sqrMagnitude <= limit * limit && taxi != null && taxi.Exists())
            {
                TaxiScratch.Add(taxi);
            }
        }
    }

    /// <summary>Keeps the segment of <paramref name="road"/> nearest the point if it beats
    /// <paramref name="best"/>; true when it did.</summary>
    private static bool ClosestSegment(Road? road, GlobalPosition point, ref float best, ref GlobalPosition a, ref GlobalPosition b)
    {
        bool closer = false;
        for (int i = 1; road?.points != null && i < road.points.Count; i++)
        {
            float distance = SegmentDistanceSquared(point, road.points[i - 1], road.points[i]);
            if (distance < best)
            {
                best = distance;
                a = road.points[i - 1];
                b = road.points[i];
                closer = true;
            }
        }

        return closer;
    }

    /// <summary>True when a segment of <paramref name="road"/> comes within the clearance of a
    /// footprint box; see <see cref="ToBoxFrame"/> for the frame.</summary>
    private static bool RoadTouchesBox(
        Road? road,
        GlobalPosition target,
        Quaternion inverse,
        Vector3 middle,
        Vector2 half,
        float clearanceSquared)
    {
        if (road?.points == null || road.points.Count < 2)
        {
            return false;
        }

        Vector2 previous = ToBoxFrame(road.points[0], target, inverse, middle);
        for (int i = 1; i < road.points.Count; i++)
        {
            Vector2 next = ToBoxFrame(road.points[i], target, inverse, middle);
            if (SegmentBoxDistanceSquared(previous, next, half) <= clearanceSquared)
            {
                return true;
            }

            previous = next;
        }

        return false;
    }

    /// <summary>Segment test against any road network, with no bounding-box cache. Taxi networks
    /// are a handful of short roads inside one airfield, so the cache the level network needs would
    /// cost more than it saves.</summary>
    private static bool IsOnNetwork(RoadNetwork network, GlobalPosition target, float clearance)
    {
        float clearanceSquared = clearance * clearance;
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];
            if (road?.points == null || road.points.Count < 2)
            {
                continue;
            }

            for (int i = 1; i < road.points.Count; i++)
            {
                if (SegmentDistanceSquared(target, road.points[i - 1], road.points[i]) <= clearanceSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A road point in the frame of a footprint box centred on the origin, axis aligned.</summary>
    private static Vector2 ToBoxFrame(GlobalPosition point, GlobalPosition site, Quaternion inverse, Vector3 middle)
    {
        Vector3 local = inverse * new Vector3(point.x - site.x, 0f, point.z - site.z);
        return new Vector2(local.x - middle.x, local.z - middle.z);
    }

    /// <summary>Distance squared from a segment to a box of half size <paramref name="half"/> at the origin.</summary>
    private static float SegmentBoxDistanceSquared(Vector2 a, Vector2 b, Vector2 half)
    {
        if (SegmentCrossesBox(a, b, half))
        {
            return 0f;
        }

        // Apart, the nearest pair is an end of the segment against the box, or a corner of the box
        // against the segment.
        float best = Mathf.Min(PointBoxDistanceSquared(a, half), PointBoxDistanceSquared(b, half));
        GlobalPosition from = new(a.x, 0f, a.y);
        GlobalPosition to = new(b.x, 0f, b.y);
        for (int corner = 0; corner < 4; corner++)
        {
            GlobalPosition point = new(
                (corner & 1) == 0 ? -half.x : half.x,
                0f,
                (corner & 2) == 0 ? -half.y : half.y);
            best = Mathf.Min(best, SegmentDistanceSquared(point, from, to));
        }

        return best;
    }

    private static float PointBoxDistanceSquared(Vector2 point, Vector2 half)
    {
        float dx = Mathf.Max(Mathf.Abs(point.x) - half.x, 0f);
        float dy = Mathf.Max(Mathf.Abs(point.y) - half.y, 0f);
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Slab clipping: where the segment runs inside each axis band, and whether those overlap.</summary>
    private static bool SegmentCrossesBox(Vector2 a, Vector2 b, Vector2 half)
    {
        Vector2 d = b - a;
        float enter = 0f;
        float exit = 1f;
        for (int axis = 0; axis < 2; axis++)
        {
            if (Mathf.Abs(d[axis]) < 0.0001f)
            {
                if (Mathf.Abs(a[axis]) > half[axis])
                {
                    return false;
                }

                continue;
            }

            float t0 = (-half[axis] - a[axis]) / d[axis];
            float t1 = (half[axis] - a[axis]) / d[axis];
            enter = Mathf.Max(enter, Mathf.Min(t0, t1));
            exit = Mathf.Min(exit, Mathf.Max(t0, t1));
            if (enter > exit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Distance to the nearest road centreline, measured against the segments between road points
    /// rather than the points themselves: road points are tens of metres apart, so a point test
    /// lets a building straddle the tarmac between two of them.
    /// </summary>
    private static bool IsOnRoad(GlobalPosition target, float clearance)
    {
        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network == null)
        {
            return false;
        }

        EnsureRoadBounds(network);
        float clearanceSquared = clearance * clearance;
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];

            // Road.InBounds is the road's own bounding box with no margin at all, so a straight
            // road has a box one point wide and every site beside it passed the test without the
            // clearance check ever running. That is how the enemy commander parked a refinery on
            // its own supply road. Same early-out, grown by the clearance being asked about.
            if (road == null
                || road.points.Count < 2
                || roadIndex >= roadBounds.Length
                || !roadBounds[roadIndex].Contains(target, clearance))
            {
                continue;
            }

            for (int i = 1; i < road.points.Count; i++)
            {
                if (SegmentDistanceSquared(target, road.points[i - 1], road.points[i]) <= clearanceSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Caches one horizontal bounding box per road. Roads are level data and never move, so this
    /// runs once per level; the alternative is scanning every point of every road on every frame
    /// the build ghost is up.
    /// </summary>
    private static void EnsureRoadBounds(RoadNetwork network)
    {
        if (ReferenceEquals(boundsNetwork, network) && roadBounds.Length == network.roads.Count)
        {
            return;
        }

        boundsNetwork = network;
        roadBounds = new RoadBounds[network.roads.Count];
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];
            if (road == null || road.points.Count == 0)
            {
                continue;
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < road.points.Count; i++)
            {
                GlobalPosition point = road.points[i];
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.z);
                maxZ = Mathf.Max(maxZ, point.z);
            }

            roadBounds[roadIndex] = new RoadBounds(minX, maxX, minZ, maxZ);
        }
    }

    /// <summary>
    /// With no road near, pulls a hangar to the nearest edge of map pavement within
    /// <see cref="PavementSnapMeters"/>, doors facing it. A highway strip with no airbase on it is
    /// pavement with no road data behind it, so the road snap never sees it.
    /// Cached on the cursor, because the ghost asks every frame and the search is ~1000 raycasts.
    /// </summary>
    private bool TrySnapToPavement(GlobalPosition cursor, Vector3 door, float front, out GlobalPosition site, out Quaternion rotation)
    {
        site = cursor;
        rotation = Rotation;
        if (FastMath.SquareDistance(cursor, pavementCursor) < 0.25f && Mathf.Approximately(front, pavementFront))
        {
            if (!pavementFound)
            {
                return false;
            }

            site = pavementSite;
            rotation = FaceToward(door, pavementNormal);
            return true;
        }

        pavementCursor = cursor;
        pavementFront = front;
        pavementFound = false;
        bool startPaved = IsPavement(cursor);
        int bestIndex = -1;
        for (int i = 0; i < PavementEdges.Length; i++)
        {
            Vector3 direction = Quaternion.Euler(0f, i * 360f / PavementEdges.Length, 0f) * Vector3.forward;
            PavementEdges[i] = float.MaxValue;
            for (float d = PavementStepMeters; d <= PavementSnapMeters; d += PavementStepMeters)
            {
                if (IsPavement(cursor + (direction * d)) != startPaved)
                {
                    // Halve the last step down to a quarter metre.
                    float inside = d - PavementStepMeters;
                    float outside = d;
                    while (outside - inside > 0.25f)
                    {
                        float mid = (inside + outside) * 0.5f;
                        if (IsPavement(cursor + (direction * mid)) != startPaved)
                        {
                            outside = mid;
                        }
                        else
                        {
                            inside = mid;
                        }
                    }

                    PavementEdges[i] = outside;
                    break;
                }
            }

            if (PavementEdges[i] < float.MaxValue && (bestIndex < 0 || PavementEdges[i] < PavementEdges[bestIndex]))
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        // The nearest crossing is roughly square to a straight edge; its neighbours give the edge's
        // own line, which is what the doors should face square on.
        int count = PavementEdges.Length;
        Vector3 nearest = EdgeOffset(bestIndex);
        Vector3 toPavement = Quaternion.Euler(0f, bestIndex * 360f / count, 0f) * Vector3.forward * (startPaved ? -1f : 1f);
        Vector3 normal = toPavement;
        int before = (bestIndex + count - 1) % count;
        int after = (bestIndex + 1) % count;
        if (PavementEdges[before] < float.MaxValue && PavementEdges[after] < float.MaxValue)
        {
            Vector3 along = EdgeOffset(after) - EdgeOffset(before);
            along.y = 0f;
            if (along.sqrMagnitude > 0.01f)
            {
                normal = Vector3.Cross(Vector3.up, along.normalized);
                if (Vector3.Dot(normal, toPavement) < 0f)
                {
                    normal = -normal;
                }
            }
        }

        pavementFound = true;
        pavementNormal = normal;
        pavementSite = cursor + nearest - (normal * (front + PavementGapMeters));
        site = pavementSite;
        rotation = FaceToward(door, normal);
        return true;

        // From the cursor to where ray index crossed the pavement edge.
        static Vector3 EdgeOffset(int index)
        {
            return Quaternion.Euler(0f, index * 360f / PavementEdges.Length, 0f) * Vector3.forward * PavementEdges[index];
        }
    }

    private void UpdateTowerRing()
    {
        float moved = new Vector2(site.x - towerRingCentre.x, site.z - towerRingCentre.z).sqrMagnitude;
        if (towerRingSampled && moved < 4f)
        {
            return;
        }

        towerRingSampled = true;
        towerRingCentre = site;
        for (int i = 0; i < TowerRingPoints.Length; i++)
        {
            float angle = i * Mathf.PI * 2f / TowerRingPoints.Length;
            GlobalPosition ground = new(
                site.x + (Mathf.Cos(angle) * TowerBuildRadiusMeters),
                site.y,
                site.z + (Mathf.Sin(angle) * TowerBuildRadiusMeters));

            // The top surface, not bare terrain: a runway or pad stands over the terrain collider and
            // a terrain-only ring dipped under it. Global y 0 is sea level; over water it is the seabed.
            TowerRingPoints[i] = new GlobalPosition(ground.x, Mathf.Max(GetSurfaceY(ground, overUnits: true), 0f) + 3f, ground.z);
        }
    }

    /// <summary>
    /// True when a road centreline passes within <paramref name="clearance"/> of the building's
    /// walls, the footprint box turned to its facing, so a long hangar can stand close to the road on
    /// its short side.
    /// </summary>
    private bool IsFootprintOnRoad(
        BuildingDefinition candidate,
        GlobalPosition target,
        Quaternion rotation,
        float clearance,
        bool taxiways = false)
    {
        Vector3 footprint = GetFootprint(candidate);
        Vector3 middle = GetFootprintCentre(candidate) + candidate.spawnOffset;
        Vector2 half = new(footprint.x, footprint.z);
        Quaternion inverse = Quaternion.Inverse(rotation);
        float reach = half.magnitude + new Vector2(middle.x, middle.z).magnitude + clearance;
        float clearanceSquared = clearance * clearance;

        // Taxiways: the airfield's own roads, which a hangar lines up along.
        if (taxiways)
        {
            CollectTaxiNetworks(target, reach);
            for (int n = 0; n < TaxiScratch.Count; n++)
            {
                List<Road> roads = TaxiScratch[n].roads;
                for (int roadIndex = 0; roadIndex < roads.Count; roadIndex++)
                {
                    if (RoadTouchesBox(roads[roadIndex], target, inverse, middle, half, clearanceSquared))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network == null)
        {
            return false;
        }

        EnsureRoadBounds(network);
        for (int roadIndex = 0; roadIndex < network.roads.Count && roadIndex < roadBounds.Length; roadIndex++)
        {
            if (roadBounds[roadIndex].Contains(target, reach)
                && RoadTouchesBox(network.roads[roadIndex], target, inverse, middle, half, clearanceSquared))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A site is blocked by another unit's footprint, by a building that is part of the map, by a
    /// road, or by being outside the build radius of every airbase the faction holds, and by nothing
    /// else. Trees and rocks are ignored on purpose: clearing scenery to build is normal, bulldozing
    /// the highway is not.
    /// </summary>
    /// <param name="candidate">The building being placed.</param>
    /// <param name="target">Where it would stand.</param>
    /// <param name="hq">The faction building it; with none, the build radius is not checked.</param>
    /// <param name="dock">True only for the naval dock button. The dock borrows an ordinary
    /// building's prefab (the vehicle depot, on the current game), so the shore rule follows what is
    /// being built, not which prefab it wears: keyed on the prefab, every depot was held to the
    /// shore and the Champions start could not place its two.</param>
    /// <param name="rotation">The building's facing, which the footprint test honours.</param>
    /// <param name="reason">Why the site is refused, for the status line.</param>
    /// <returns>True when the site is legal.</returns>
    private bool Evaluate(
        BuildingDefinition candidate,
        GlobalPosition target,
        FactionHQ? hq,
        bool dock,
        Quaternion rotation,
        out string reason)
    {
        // A naval dock has to reach the sea, which is usually outside the base perimeter, so it
        // gets its own radius and its own shoreline rule rather than loosening either for every
        // building. Everything else is the ordinary build radius.
        float radiusKm = dock ? CommanderSettings.NavalDockRadiusKm : CommanderSettings.BuildRadiusKm;

        // Building is tied to ground the faction actually holds, so a commander cannot drop a
        // refinery in the enemy's rear. Same rule for the enemy commander, which reaches this
        // through IsSiteAllowed. An ammo dump is the exception: it is a forward
        // store that trucks fill, and forward means outside the base. So is a control tower: it is
        // the forward base itself.
        if (hq != null && (dock || (!IsDumpDefinition(candidate) && !IsControlTower(candidate)))
            && !IsInsideBuildRadius(hq, target, radiusKm, out bool contested))
        {
            reason = contested
                ? "Blocked: that base is being captured."
                : $"Blocked: more than {radiusKm:0.#} km from a captured base or 1 km from a control tower.";
            return false;
        }

        // The site is snapped to the ground, and over water that is the seabed: the enemy's dart
        // once put an AA emplacement on the sea floor, where nothing could reach it.
        if (!dock && CommanderGameAccess.IsBelowSeaLevel(target))
        {
            reason = "Blocked: that is water.";
            return false;
        }

        if (dock && !IsShoreline(target))
        {
            reason = "Blocked: a naval dock has to stand on dry land at the water's edge.";
            return false;
        }

        if (OverlapsBlueprint(candidate, target))
        {
            reason = "Blocked by a blueprint.";
            return false;
        }

        if (CommanderEconomyService.OverlapsBuiltUnit(this, candidate, target))
        {
            reason = "Blocked by a building.";
            return false;
        }

        Vector3 footprint = GetFootprint(candidate);
        Vector3 center = target.ToLocalPosition() + (Vector3.up * footprint.y);
        Vector3 extents = new(footprint.x, Mathf.Max(footprint.y, 5f), footprint.z);

        int hits = Physics.OverlapBoxNonAlloc(
            center,
            extents,
            overlapHits,
            rotation,
            ~0,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider hit = overlapHits[i];
            Unit? occupant = hit == null ? null : hit.GetComponentInParent<Unit>();

            // A builder parks round the site it is building, so it must not block that site. A
            // wrecked building still does: repair crews bring it back, and a new one put down in
            // its rubble ends up standing inside it.
            if (occupant != null
                && (!occupant.disabled || occupant is Building)
                && !CommanderEconomyService.IsBuilder(occupant))
            {
                reason = $"Blocked by {CommanderGameAccess.GetUnitLabel(occupant)}.";
                return false;
            }

            if (occupant == null && IsMapStructure(hit))
            {
                reason = "Blocked by a building.";
                return false;
            }
        }

        float span = Mathf.Max(footprint.x, footprint.z);
        if (IsFootprintOnRoad(candidate, target, rotation, RoadClearanceMeters))
        {
            reason = "Blocked: too close to a road.";
            return false;
        }

        // A hangar is measured from its walls on a taxiway, the way it is on a road, because beside
        // the taxiway is where it belongs. Runways keep the wide margin for everything.
        bool hangar = CommanderBuiltHangar.IsHangar(candidate);
        if (IsOnAirfieldSurface(target, span + RunwayClearanceMeters, taxiways: !hangar)
            || (hangar && IsFootprintOnRoad(candidate, target, rotation, TaxiClearanceMeters, taxiways: true)))
        {
            reason = "Blocked: that is a runway or taxiway.";
            return false;
        }

        // A hangar is levelled over its whole footprint, so its door opens at the floor, not at the
        // terrain under its middle.
        GlobalPosition floor = target;
        if (CommanderBuiltHangar.HasFloor(candidate))
        {
            floor = CommanderBuiltHangar.GetFloorGround(this, candidate, target, rotation, out float step);
            if (step > CommanderBuiltHangar.MaxStepMeters)
            {
                reason = "Blocked: too steep for a level hangar floor.";
                return false;
            }
        }

        // The dock borrows the depot prefab but launches ships at the sea lane, never out of its door.
        if (!dock && IsDepotExitBuried(candidate, floor, rotation))
        {
            reason = "Blocked: too steep, vehicles would come out under the ground.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// A blueprint has no collider until it is finished, so the overlap test cannot see it; without
    /// this a second building could be put down on top of one still going up. It has to be at least
    /// as strict as the checks the site faces again when the other blueprint finishes first
    /// (<see cref="CommanderEconomyService.OverlapsBuiltUnit"/>'s circles and the rotated physics
    /// box), or two blueprints that fit side by side get the second refunded. So each footprint is
    /// its circumscribed circle: any rotation of either box fits inside it.
    /// </summary>
    private bool OverlapsBlueprint(BuildingDefinition candidate, GlobalPosition target)
    {
        IReadOnlyList<CommanderEconomyService.ConstructionSite>? blueprints = CommanderEconomyService.Blueprints;
        if (blueprints == null)
        {
            return false;
        }

        Vector3 size = GetFootprint(candidate);
        float reach = new Vector2(size.x, size.z).magnitude;
        for (int i = 0; i < blueprints.Count; i++)
        {
            CommanderEconomyService.ConstructionSite blueprint = blueprints[i];
            Vector3 other = GetFootprint(blueprint.Definition);
            float apart = reach + new Vector2(other.x, other.z).magnitude;
            float dx = blueprint.Site.x - target.x;
            float dz = blueprint.Site.z - target.z;
            if ((dx * dx) + (dz * dz) < apart * apart)
            {
                return true;
            }
        }

        return false;
    }

    private void Draw(BuildingDefinition candidate, GlobalPosition target, Quaternion rotation)
    {
        if (!hasSite)
        {
            return;
        }

        Color tint = SiteValid ? ValidTint : BlockedTint;
        tintBlock.SetColor("_BaseColor", tint);
        tintBlock.SetColor("_Color", tint);
        DrawParts(candidate, target.ToLocalPosition() + (rotation * candidate.spawnOffset), rotation, tintBlock);
    }

    /// <summary>The prefab's meshes at <paramref name="origin"/>: see-through when given a tint, in
    /// the building's own materials when not.</summary>
    private void DrawParts(BuildingDefinition candidate, Vector3 origin, Quaternion rotation, MaterialPropertyBlock? tint)
    {
        List<Part> meshes = GetParts(candidate);
        Material? ghost = tint == null ? null : GetGhostMaterial();
        if (tint != null && ghost == null)
        {
            return;
        }

        Matrix4x4 root = Matrix4x4.TRS(origin, rotation, Vector3.one);
        for (int i = 0; i < meshes.Count; i++)
        {
            Part part = meshes[i];
            Matrix4x4 matrix = root * part.Local;
            for (int sub = 0; sub < part.Solid.Length; sub++)
            {
                Material? solid = part.Solid[sub];
                Material? material = tint == null ? solid : ghost;

                // A submesh the building itself leaves unpainted is left out of the ghost too, so
                // the see-through picture shows exactly what will be built.
                if (material == null || solid == null || sub >= part.Mesh.subMeshCount)
                {
                    continue;
                }

                Graphics.DrawMesh(part.Mesh, matrix, material, 0, null, sub, tint);
            }
        }
    }

    private List<Part> GetParts(BuildingDefinition candidate)
    {
        if (parts.TryGetValue(candidate, out List<Part> cached))
        {
            return cached;
        }

        cached = new List<Part>();
        parts[candidate] = cached;
        GameObject prefab = candidate.unitPrefab;
        if (prefab == null)
        {
            return cached;
        }

        HashSet<Renderer> lowDetail = CollectLowDetail(prefab);
        Transform root = prefab.transform;
        MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter.sharedMesh == null
                || !filter.TryGetComponent(out MeshRenderer renderer)
                || lowDetail.Contains(renderer)
                || !IsSolidPart(filter.transform, root, renderer))
            {
                continue;
            }

            cached.Add(new Part(
                filter.sharedMesh!,
                renderer.sharedMaterials,
                root.worldToLocalMatrix * filter.transform.localToWorldMatrix));
        }

        return cached;
    }

    /// <summary>
    /// The one material every see-through draw uses, tinted per draw by the property block.
    /// </summary>
    /// <remarks>
    /// It deliberately does <em>not</em> clone the building's own material. The game's buildings are
    /// a mix of URP Lit, Simple Lit and Shader Graph materials, and a Shader Graph shader bakes its
    /// blend state into the generated pass: writing <c>_Surface</c>, <c>_SrcBlend</c> or
    /// <c>_ZWrite</c> on one is silently ignored, so those parts stayed solid while the URP ones
    /// went transparent and the ghost came out part building, part blueprint. <c>Internal-Colored</c>
    /// is the engine's own always-present unlit shader and its blend state <em>is</em> property
    /// driven, so every mesh gets the same flat see-through tint whatever the building is made of.
    /// </remarks>
    private Material? GetGhostMaterial()
    {
        if (ghostMaterial != null)
        {
            return ghostMaterial;
        }

        Shader shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            return null;
        }

        ghostMaterial = new Material(shader)
        {
            renderQueue = (int)RenderQueue.Transparent,
        };
        ghostMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        ghostMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        ghostMaterial.SetFloat("_ZWrite", 0f);

        // The shader culls nothing by default, which would blend the far side of every wall
        // through the near one and darken the ghost wherever it doubles up.
        ghostMaterial.SetFloat("_Cull", (float)CullMode.Back);
        return ghostMaterial;
    }

    /// <summary>A road's horizontal extent, tested with a margin the caller supplies.</summary>
    private readonly struct RoadBounds
    {
        private readonly float minX;
        private readonly float maxX;
        private readonly float minZ;
        private readonly float maxZ;

        internal RoadBounds(float minX, float maxX, float minZ, float maxZ)
        {
            this.minX = minX;
            this.maxX = maxX;
            this.minZ = minZ;
            this.maxZ = maxZ;
        }

        internal bool Contains(GlobalPosition point, float margin)
        {
            return point.x >= minX - margin
                && point.x <= maxX + margin
                && point.z >= minZ - margin
                && point.z <= maxZ + margin;
        }
    }

    private readonly struct Part
    {
        internal Part(Mesh mesh, Material?[] solid, Matrix4x4 local)
        {
            Mesh = mesh;
            Solid = solid;
            Local = local;
        }

        internal Mesh Mesh { get; }

        /// <summary>The prefab's own materials, for a blueprint that is going up. A see-through
        /// draw replaces them all with the one ghost material but still honours their count and
        /// their empty slots.</summary>
        internal Material?[] Solid { get; }

        internal Matrix4x4 Local { get; }
    }
}
