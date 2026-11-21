using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Ground Control Champions: the player holds one airbase, dealt at random, and the enemy commander
/// holds every other one. A mission file cannot say "a random base" (no variables, no dice), so the
/// JSON hands the player's faction one fixed base and no buildings, and this deals the real start
/// once per mission. Winning needs nothing new: <see cref="CommanderVictoryService"/> already ends
/// the match when either side is left holding no airbase.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    /// <summary>
    /// The kit is placed once, and a base without its two depots cannot field a single vehicle, so
    /// it throws far more darts than an enemy review does.
    /// </summary>
    private const int KitSiteAttempts = 60;

    /// <summary>The most bases the Champions deal hands one side.</summary>
    private const int ChampionsMaxDealtBases = 8;

    /// <summary>How long the builders may take to become spawnable before the log complains.</summary>
    private const float NoBuilderWarnSeconds = 10f;

    /// <summary>How far down the door lane the builders' spot search looks.</summary>
    private const float StartingBuilderMaxMeters = 60f;

    /// <summary>Vehicle depots every enemy base opens Champions with.</summary>
    private const int EnemyStartingDepotsPerBase = 2;

    /// <summary>A builder closer than this to a depot's exit lane is parked in its door.</summary>
    private const float DepotLaneHalfWidthMeters = 12f;

    /// <summary>How far out of the door a depot's lane runs: the 20 m push plus room to drive off.</summary>
    private const float DepotLaneLengthMeters = 45f;

    /// <summary>How far from a spot the depot lane tests look for doors.</summary>
    private const float DoorSearchMeters = 150f;

    /// <summary>How long after the match starts the free builders may still be handed out.</summary>
    private const float StartingBuilderWindowSeconds = 60f;

    /// <summary>
    /// The duel's opening buildings, by <c>jsonKey</c>, put down beside the dealt base. Spawned
    /// rather than authored and re-owned, because a <c>VehicleDepot</c> only registers with the
    /// faction that owned it when it spawned (<c>OnStartClient</c> → <c>FactionHQ.AddDepot</c>) and
    /// nothing moves it on an owner change, so an authored depot handed to the player would go on
    /// deploying the enemy's reserve.
    /// </summary>
    private static readonly string[] ChampionKitKeys =
    {
        "VehicleDepot1",
        "VehicleDepot1",
        "Emplacement1_23mm",
        "Emplacement1_23mm",
        "Emplacement1_MANPADS",
    };

    /// <summary>
    /// The dice the deal and every roadside site search share. Not <c>UnityEngine.Random</c>: the
    /// map's <c>TerrainScatter</c> seeds that with 42 while the level loads, so every match drew the
    /// same numbers and dealt the same base.
    /// </summary>
    private static readonly System.Random SiteDice = new();

    /// <summary>
    /// The bases a player may be dealt. Every base on the map is the enemy's and capturable, but the
    /// three the duel leaves disabled (North Boscali, Agrapol, the Vigil Cay naval base) are excluded
    /// as start bases.
    /// </summary>
    private static readonly string[] ChampionsStartBases =
    {
        "K92 Highway Strip",
        "Maris Airport",
        "Sandrift Airbase",
        "South Boscali General Aviation",
    };

    /// <summary>GOD DIFFICULTY always deals the player Maris Airport, the one large airport in the
    /// Champions pool (Sandrift read as a small field in the server test).</summary>
    private static readonly string[] GodStartBases =
    {
        "Maris Airport",
    };

    /// <summary>Factions already handed their builders this mission: the host's own and, in
    /// multiplayer, every other team a player commands.</summary>
    private readonly HashSet<FactionHQ> startingBuildersGiven = new();

    /// <summary>Builders the Champions deal has handed each AI faction so far.</summary>
    private readonly Dictionary<FactionHQ, int> championsBuildersDealt = new();

    /// <summary>When each depot was first seen, for the one-second physics wait.</summary>
    private readonly Dictionary<VehicleDepot, float> startingBuildersDepotSeenAt = new();

    /// <summary>The enemy's bases at the Champions deal still owed their two builders. Fixed at the
    /// deal, so a base captured later never hands out free builders.</summary>
    private readonly List<Airbase> championsBuilderBases = new();

    private bool championsDealt;

    private bool warnedNoStartingBuilders;

    /// <summary>
    /// When the starting-builder grant closes, on <c>Time.timeSinceLevelLoad</c>; -1 until it opens
    /// (the first tick on the duel, the deal on Champions). A grant still pending then is dropped:
    /// a base whose depot was bombed stayed on the list and paid out the day a depot stood there
    /// again, which the player saw as free builders appearing mid-match.
    /// </summary>
    private float startingBuildersDeadline = -1f;

    /// <summary>
    /// A standby spot moved off every depot's exit lane: beside the lane, either side, else past its
    /// end, so builders without orders do not block the depot door.
    /// </summary>
    internal static GlobalPosition MoveOutOfDepotLanes(GlobalPosition spot)
    {
        Vector3 point = spot.ToLocalPosition();
        List<Transform> doors = DoorsNear(point, DoorSearchMeters);
        if (!IsInDepotLane(spot, doors))
        {
            return spot;
        }

        foreach (Transform door in doors)
        {
            Vector3 forward = door.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 offset = point - door.position;
            offset.y = 0f;
            float along = Vector3.Dot(offset, forward);
            if (along <= -DepotLaneHalfWidthMeters || along >= DepotLaneLengthMeters)
            {
                continue;
            }

            Vector3 side = Vector3.Cross(Vector3.up, forward) * (Vector3.Dot(offset, Vector3.Cross(Vector3.up, forward)) >= 0f ? 1f : -1f);
            Vector3 alongLane = door.position + (forward * Mathf.Clamp(along, 10f, DepotLaneLengthMeters));
            Vector3[] candidates =
            {
                alongLane + (side * (DepotLaneHalfWidthMeters + 10f)),
                alongLane - (side * (DepotLaneHalfWidthMeters + 10f)),
                door.position + (forward * (DepotLaneLengthMeters + 15f)),
            };
            foreach (Vector3 candidate in candidates)
            {
                GlobalPosition moved = CommanderGameAccess.SnapToTerrain(candidate.ToGlobalPosition());
                if (!IsInDepotLane(moved, doors))
                {
                    return moved;
                }
            }
        }

        return spot;
    }

    /// <summary>
    /// Host: a free vehicle depot for <paramref name="hq"/> beside <paramref name="anchor"/>, placed like
    /// the Champions kit. The Leviathan's Deep raises one on every base it takes.
    /// </summary>
    internal Unit? PlaceFreeDepot(FactionHQ hq, GlobalPosition anchor)
    {
        BuildingDefinition? depot = Encyclopedia.i != null ? FindBuilding(Encyclopedia.i, VehicleDepotKeys) : null;
        return depot != null ? PlaceKitBuilding(hq, depot, anchor, out _) : null;
    }

    /// <summary>Host: a free control tower for <paramref name="hq"/> beside <paramref name="anchor"/>: the Deep's healing beacon.</summary>
    internal Unit? PlaceFreeTower(FactionHQ hq, GlobalPosition anchor, string label)
    {
        BuildingDefinition? tower = Encyclopedia.i?.buildings.Find(d => d != null && d.unitPrefab != null && CommanderBuildPreview.IsControlTower(d));
        return tower != null ? PlaceKitBuilding(hq, tower, anchor, out _, label) : null;
    }

    /// <summary>
    /// Ground Control Draft's one piece of setup: a munitions bunker near <paramref name="anchor"/>
    /// (the side's first depot), because the duel's buildings carry no ammunition shelf and a draft
    /// cannot build one. It never runs dry once the battle starts (<c>RefillDraftBuildings</c>).
    /// </summary>
    internal bool PlaceDraftKit(FactionHQ hq, GlobalPosition anchor)
    {
        BuildingDefinition? bunker = ResolveCategoryDefinition(BuildingType.AMMO, preferDearest: true);
        return bunker != null && TryPlaceKitBuilding(hq, bunker, anchor, out _);
    }

    private static List<Transform> DoorsNear(Vector3 point, float meters)
    {
        List<Transform> doors = new();
        foreach (VehicleDepot depot in Object.FindObjectsOfType<VehicleDepot>())
        {
            Transform? door = depot != null ? CommanderGameAccess.GetDepotSpawnTransform(depot) : null;
            if (door != null && FastMath.InRange(door.position, point, meters))
            {
                doors.Add(door);
            }
        }

        return doors;
    }

    private static bool IsInDepotLane(GlobalPosition spot, List<Transform> doors)
    {
        Vector3 point = spot.ToLocalPosition();
        for (int i = 0; i < doors.Count; i++)
        {
            Vector3 forward = doors[i].forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 offset = point - doors[i].position;
            offset.y = 0f;
            float along = Vector3.Dot(offset, forward);
            if (along > -DepotLaneHalfWidthMeters
                && along < DepotLaneLengthMeters
                && (offset - (forward * along)).magnitude < DepotLaneHalfWidthMeters)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The map's radar stations belong to whichever side the map author gave them, not to any airbase
    /// the deal re-owns. The one nearest the player's dealt base is the player's, every other is the
    /// enemy's. Runs after the base captures, which re-own everything on a base's building list.
    /// </summary>
    private static void DealRadarStations(GlobalPosition home, FactionHQ localHq, FactionHQ enemyHq)
    {
        Building[] buildings = Object.FindObjectsOfType<Building>();
        Building? nearest = null;
        float best = float.MaxValue;
        foreach (Building building in buildings)
        {
            float distance = IsRadarStation(building) ? FastMath.Distance(building.transform.GlobalPosition(), home) : float.MaxValue;
            if (distance < best)
            {
                nearest = building;
                best = distance;
            }
        }

        int enemy = 0;
        foreach (Building building in buildings)
        {
            if (IsRadarStation(building))
            {
                bool player = ReferenceEquals(building, nearest);
                enemy += player ? 0 : 1;
                building.NetworkHQ = player ? localHq : enemyHq;
            }
        }

        CommanderPlugin.Log.LogInfo(nearest == null
            ? "Champions: this map has no radar stations to deal."
            : $"Champions: {localHq.faction.factionName} gets the radar station {best / 1000f:0.#} km from its base, {enemyHq.faction.factionName} the other {enemy}.");
    }

    private static bool IsRadarStation(Building building)
    {
        return building != null && building.definition is BuildingDefinition { buildingType: BuildingType.RDR };
    }

    private void TickChampionsStart()
    {
        if (championsDealt
            || !CommanderEnemyCommanderService.IsChampionsMission
            || FactionRegistry.airbaseLookup == null
            || Encyclopedia.i?.buildings == null)
        {
            return;
        }

        // The Leviathan event opens at sea with every base neutral (user, 2026-11-05): nothing to deal.
        if (CommanderLeviathanService.IsLeviathanMission)
        {
            championsDealt = true;
            return;
        }

        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        FactionHQ? enemyHq = null;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && !ReferenceEquals(hq, localHq) && hq.faction != null)
            {
                enemyHq = hq;
                break;
            }
        }

        // The enemy's authored depots are what mark its capital, so wait until they have spawned:
        // dealt before that, the capital would be in the draw and the player could start on top of
        // the enemy's army.
        if (localHq == null
            || !localHq.IsServer
            || enemyHq == null
            || !enemyHq.TryGetNearestDepot(Vector3.zero, float.MaxValue, out _))
        {
            return;
        }

        string[] startBases = CommanderGodService.IsGodBudget() ? GodStartBases : ChampionsStartBases;
        List<Airbase> pool = new();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase != null
                && !airbase.disabled
                && airbase.center != null
                && airbase.SavedAirbase?.Capturable == true
                && System.Array.IndexOf(startBases, airbase.SavedAirbase.UniqueName) >= 0
                && !enemyHq.TryGetNearestDepot(airbase.center.position, airbase.GetRadius(), out _))
            {
                pool.Add(airbase);
            }
        }

        if (pool.Count == 0)
        {
            return;
        }

        championsDealt = true;
        Airbase home = pool[SiteDice.Next(pool.Count)];

        // Dealt first, taken second, so the player never holds nothing in between and the victory
        // check cannot call the match before it starts.
        if (!ReferenceEquals(home.CurrentHQ, localHq))
        {
            home.capture.ForceCapture(localHq);
        }

        // Every other base is the enemy's, whoever the JSON or the map left holding it: a base nobody
        // held sat neutral beside the player's for the whole match. Listed afterwards, any base the
        // war leaves out, because a disabled or uncapturable one is a JSON fault this cannot fix.
        List<string> leftOut = new();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || ReferenceEquals(airbase, home))
            {
                continue;
            }

            if (airbase.disabled || airbase.SavedAirbase?.Capturable != true)
            {
                leftOut.Add($"{CommanderCaptureService.GetAirbaseLabel(airbase)} ({(airbase.disabled ? "disabled" : "not capturable")})");
            }
            else if (!ReferenceEquals(airbase.CurrentHQ, enemyHq))
            {
                airbase.capture.ForceCapture(enemyHq);
            }
        }

        CommanderPlugin.Log.LogInfo(leftOut.Count == 0
            ? "Champions: every other base is the enemy's and capturable."
            : $"Champions: bases left out of the war: {string.Join(", ", leftOut)}.");

        GlobalPosition anchor = home.center.GlobalPosition();
        DealRadarStations(anchor, localHq, enemyHq);
        int built = 0;
        int roadside = 0;
        for (int i = 0; i < ChampionKitKeys.Length; i++)
        {
            BuildingDefinition? definition = FindBuilding(Encyclopedia.i, new[] { ChampionKitKeys[i] });
            if (definition != null && TryPlaceKitBuilding(localHq, definition, anchor, out bool byRoad))
            {
                built++;
                roadside += byRoad ? 1 : 0;
            }
        }

        string label = CommanderCaptureService.GetAirbaseLabel(home);
        CommanderPlugin.Log.LogInfo(
            $"Champions: {localHq.faction.factionName} was dealt {label} out of {pool.Count} bases; "
                + $"{built}/{ChampionKitKeys.Length} opening buildings placed, {roadside} depots beside a road.");
        CommanderAlertService.Instance?.NotifyCapture($"YOUR BASE: {label.ToUpperInvariant()}");

        GiveEnemyBasesDepots(enemyHq);
    }

    /// <summary>
    /// Every base the enemy holds after the deal gets two vehicle depots, topping up to two where the
    /// mission already authored some (its capital), a munitions bunker if it has none, and is queued
    /// for two starting builders. Without this the enemy opened with one fielding base and six it had
    /// to build depots at, one blueprint at a time. The bunker is what lets those depots field
    /// anything: every spawn draws fuel and ordnance from a store near the depot, and without one the
    /// whole army rolled out of the capital and drove across the map past the empty bases.
    /// </summary>
    private void GiveEnemyBasesDepots(FactionHQ enemyHq)
    {
        BuildingDefinition? depot = FindBuilding(Encyclopedia.i, VehicleDepotKeys);
        BuildingDefinition? bunker = ResolveCategoryDefinition(BuildingType.AMMO, preferDearest: true);
        int built = 0;
        int bunkers = 0;
        foreach (Airbase airbase in enemyHq.GetAirbases())
        {
            if (airbase == null || airbase.center == null || !CommanderGameAccess.IsLandBase(airbase))
            {
                continue;
            }

            championsBuilderBases.Add(airbase);
            GlobalPosition anchor = airbase.center.GlobalPosition();
            int have = CountBuildingsNear(enemyHq, BuildingType.DEP, anchor, Mathf.Max(airbase.GetRadius(), BaseSiteMaxMeters));
            for (int i = have; depot != null && i < EnemyStartingDepotsPerBase; i++)
            {
                if (TryPlaceKitBuilding(enemyHq, depot, anchor, out _))
                {
                    built++;
                }
            }

            if (bunker != null
                && !HasBuildingNear(enemyHq, BuildingType.AMMO, anchor, SupplyCoverageMeters)
                && TryPlaceKitBuilding(enemyHq, bunker, anchor, out _))
            {
                bunkers++;
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Champions: {enemyHq.faction.factionName} given {built} depots and {bunkers} munitions bunkers "
                + $"across {championsBuilderBases.Count} bases.");
    }

    /// <summary>
    /// A depot goes beside a road with its door facing it, so what it builds rolls straight out onto
    /// the road; anything else, or a depot with no road near the base, takes a dart.
    /// </summary>
    private bool TryPlaceKitBuilding(FactionHQ hq, BuildingDefinition definition, GlobalPosition anchor, out bool byRoad)
    {
        return PlaceKitBuilding(hq, definition, anchor, out byRoad) != null;
    }

    private Unit? PlaceKitBuilding(FactionHQ hq, BuildingDefinition definition, GlobalPosition anchor, out bool byRoad, string? label = null)
    {
        GlobalPosition site = default;
        Quaternion facing = Quaternion.identity;
        byRoad = CommanderBuildPreview.TryGetDoor(definition, out _, out _)
            && preview.TryFindRoadsideSite(
                definition, hq, anchor, BaseSiteMaxMeters, SiteDice, KitSiteAttempts, out site, out facing);
        return byRoad || TryFindSiteNear(hq, definition, anchor, out site, KitSiteAttempts)
            ? SpawnBuilding(hq, site, definition, label ?? GetStructureLabel(definition), facing: facing)
            : null;
    }

    /// <summary>
    /// Two free builders outside each player faction's first depot, once per mission, on the duel
    /// and Champions: the host's own faction, and in multiplayer every other team a guest commands
    /// (only the host can spawn, so the guest's copy of the mod never does this itself). Spawned straight onto the ground rather than bought through the depot, so they cost
    /// no money and draw no fuel or ammunition off the opening shelves. In Champions this waits for
    /// the deal, because the player's depots are the kit it puts down. The repair service's standby
    /// sweep then keeps them where they stand.
    /// </summary>
    private void TickStartingBuilders()
    {
        // A draft hands out nothing it did not pay for: repair vehicles are drafted like the rest.
        if (!CommanderEnemyCommanderService.IsDuelMission || CommanderDraftService.IsDraftMission || CommanderMapSettings.StartingBuilders == 0)
        {
            return;
        }

        // On a dedicated server this runs as the first team with a player (RunAsHostTeam), so a null
        // HQ there means nobody has joined yet: wait, or the window below would close on an empty map.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null || !localHq.IsServer)
        {
            return;
        }

        bool champions = CommanderEnemyCommanderService.IsChampionsMission;
        if (champions && !championsDealt)
        {
            return;
        }

        if (startingBuildersDeadline < 0f)
        {
            startingBuildersDeadline = Time.timeSinceLevelLoad + StartingBuilderWindowSeconds;
        }
        else if (Time.timeSinceLevelLoad > startingBuildersDeadline)
        {
            championsBuilderBases.Clear();
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || hq.faction == null || !hq.IsServer || startingBuildersGiven.Contains(hq))
            {
                continue;
            }

            bool ai = CommanderEnemyCommanderService.IsAiFaction(hq, localHq);

            // In Champions the enemy's builders come per base, off the list the deal fills.
            if (champions && ai)
            {
                continue;
            }

            // The host, a guest's team, and the enemy commander, which builds with them too.
            if ((ReferenceEquals(hq, localHq) || CommanderEnemyCommanderService.HasHumanPlayers(hq) || ai)
                && hq.TryGetNearestDepot(Vector3.zero, float.MaxValue, out VehicleDepot depot)
                && TryGiveStartingBuilders(hq, depot, CommanderMapSettings.StartingBuilders))
            {
                startingBuildersGiven.Add(hq);
            }
        }

        for (int i = championsBuilderBases.Count - 1; i >= 0; i--)
        {
            Airbase airbase = championsBuilderBases[i];
            FactionHQ? owner = airbase != null ? airbase.CurrentHQ : null;
            if (owner == null || airbase!.center == null || !CommanderEnemyCommanderService.IsAiFaction(owner, localHq))
            {
                championsBuilderBases.RemoveAt(i);
                continue;
            }

            // Under a unit cap every builder takes a slot, so each base gets its share of BuilderCap over the
            // eight bases a side can be dealt (one at 60, two at 150 and 300) until it is spent.
            championsBuildersDealt.TryGetValue(owner, out int dealt);
            int count = CommanderUnitCap.Enabled
                ? Mathf.Min(
                    Mathf.Max(1, Mathf.CeilToInt(CommanderUnitCap.BuilderCap / (float)ChampionsMaxDealtBases)),
                    CommanderMapSettings.StartingBuilders,
                    CommanderUnitCap.BuilderCap - dealt)
                : CommanderMapSettings.StartingBuilders;
            if (count <= 0)
            {
                championsBuilderBases.RemoveAt(i);
                continue;
            }

            if (owner.TryGetNearestDepot(airbase.center.position, Mathf.Max(airbase.GetRadius(), BaseSiteMaxMeters), out VehicleDepot depot)
                && TryGiveStartingBuilders(owner, depot, count))
            {
                championsBuildersDealt[owner] = dealt + count;
                championsBuilderBases.RemoveAt(i);
            }
        }
    }

    /// <summary><paramref name="builders"/> builders beside <paramref name="depot"/>; false means try again next tick.</summary>
    private bool TryGiveStartingBuilders(FactionHQ hq, VehicleDepot depot, int builders)
    {
        if (depot == null)
        {
            return false;
        }

        Transform? door = CommanderGameAccess.GetDepotSpawnTransform(depot);
        VehicleDefinition? builder = CommanderGameAccess.FindRepairVehicleDefinition(hq);
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (door == null || builder?.unitPrefab == null || spawner == null)
        {
            // Tried again next tick rather than given up on: the Champions kit spawns its depots this same
            // frame. Warned once, by name, after ten seconds of trying.
            if (!warnedNoStartingBuilders && Time.timeSinceLevelLoad > NoBuilderWarnSeconds)
            {
                warnedNoStartingBuilders = true;
                CommanderPlugin.Log.LogWarning(
                    $"Starting builders: none given to {hq.faction.factionName} - missing "
                        + (door == null ? "the depot door, " : string.Empty)
                        + (builder?.unitPrefab == null ? "a repair vehicle on the faction, " : string.Empty)
                        + (spawner == null ? "the spawner, " : string.Empty)
                        + "so the player has to buy its own.");
            }

            return false;
        }

        // A depot the Champions kit put down this frame is not in the physics scene yet, so the
        // clear-spot search below would see straight through it (or its twin beside it). One
        // second is plenty for it to register.
        if (!startingBuildersDepotSeenAt.TryGetValue(depot, out float seenAt))
        {
            seenAt = Time.time;
            startingBuildersDepotSeenAt[depot] = seenAt;
        }

        if (Time.time - seenAt < 1f
            || !CommanderSpawnService.TryFindClearVerge(depot, StartingBuilderMaxMeters, out GlobalPosition laneOrigin, out Vector3 verge, out float clear))
        {
            return false;
        }

        // Every base now has two depots side by side, and a verge clear of one door can be the
        // other's doorway: the clear-spot search sees buildings, not the empty lane in front of one.
        List<Transform> doors = new();
        foreach (VehicleDepot other in Object.FindObjectsOfType<VehicleDepot>())
        {
            Transform? otherDoor = other != null && ReferenceEquals(other.NetworkHQ, hq)
                ? CommanderGameAccess.GetDepotSpawnTransform(other)
                : null;
            if (otherDoor != null && FastMath.InRange(otherDoor.position, door.position, 500f))
            {
                doors.Add(otherDoor);
            }
        }

        Vector3 lane = door.forward;
        lane.y = 0f;
        lane.Normalize();
        Vector3 mirrored = (2f * Vector3.Dot(verge, lane) * lane) - verge;

        for (int i = 0; i < builders; i++)
        {
            // In a column down the clearest drive off to the side of the exit lane, measured from a point
            // already past the doorway, so builders do not end up inside a building or in the doorway.
            // No floor under this: a floor can walk a builder into a wall when nothing to either side is
            // clear. Both on the lane point, which is clear, is recoverable.
            float meters = Mathf.Max(0f, Mathf.Min(clear, 35f) - (i * 12f));
            Vector3 heading = verge;
            GlobalPosition spot = CommanderGameAccess.SnapToTerrain(laneOrigin + (verge * meters));
            if (IsInDepotLane(spot, doors))
            {
                GlobalPosition other = CommanderGameAccess.SnapToTerrain(laneOrigin + (mirrored * meters));
                if (!IsInDepotLane(other, doors))
                {
                    spot = other;
                    heading = mirrored;
                }
            }

            spawner.SpawnVehicle(
                builder.unitPrefab,
                spot + (Vector3.up * builder.spawnOffset.y),
                Quaternion.LookRotation(heading),
                Vector3.zero,
                hq,
                $"gc_starting_builder_{hq.faction.factionName}_{depot.GetInstanceID()}_{i}",
                1f,
                holdPosition: false,
                null);
        }

        CommanderPlugin.Log.LogInfo(
            $"{hq.faction.factionName} starts with {builders} {CommanderGameAccess.GetVehicleLabel(builder)} builders at {depot.name}.");
        return true;
    }
}
