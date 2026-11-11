using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Construction: the player's build click no longer spawns the building. It pays for a blueprint,
/// every free repair vehicle the faction owns drives out and parks round it, and the building rises
/// out of the ground while they work. More builders on site is a faster build.
/// </summary>
/// <remarks>
/// What rises is the preview's <c>Graphics.DrawMesh</c> of the prefab, and the real unit is
/// only spawned at 100 %. Spawning it early and sinking it would mean a half-built SAM site that can
/// already fire and a half-built mine that already pays, i.e. suspending every building behaviour
/// there is; a picture needs none of that and no networking. The enemy commander builds the same
/// way (<see cref="QueueEnemyBlueprint"/>): one blueprint at a time, its own repair vehicles drive
/// out to it from any distance, and it buys more of them when it is short.
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    private const float ConstructionReviewSeconds = 0.5f;

    /// <summary>Gap between the footprint's corner and the ring the builders park on, so a parked
    /// truck is never standing inside the building it is putting up.</summary>
    private const float BuilderStandoffMeters = 12f;

    /// <summary>How far off its parking spot a builder still counts as working. Ground vehicles stop
    /// short of a destination, so an exact test would never see one arrive.</summary>
    private const float BuilderReachMeters = 25f;

    /// <summary>The golden angle: spreads any number of builders round the ring with no slot
    /// bookkeeping.</summary>
    private const float BuilderSpacingRadians = 2.39996f;

    /// <summary>How far from its standby post a free repair vehicle joins a blueprint by itself. A
    /// blueprint further out needs a build order (select repair vehicles, right-click it): without
    /// this limit a Jacknife on standby drove across the map to a dock blueprint by the uncaptured
    /// base, which read as wandering off with no order.</summary>
    private const float AutoBuildRangeMeters = 3000f;

    /// <summary>An enemy blueprint no builder has reached in this long is refunded, so a commander
    /// that lost every builder and cannot buy one is not stuck saving for a building forever.</summary>
    private const float EnemyBlueprintTimeoutSeconds = 300f;

    // Cross-country pace a builder is given to reach a far blueprint before the timeout counts.
    private const float BuilderTravelMetersPerSecond = 5f;

    /// <summary>Furthest an AI builder drives off road to a blueprint; further, it takes the roads.
    /// Every AI site is within <c>BaseSiteMaxMeters</c> (900 m) of a base, so this is its own base.</summary>
    private const float AiBuilderOffRoadMeters = 1500f;

    /// <summary>Scaled seconds between two builders spawned for one AI blueprint.</summary>
    private const float LocalBuilderSpawnSeconds = 60f;

    /// <summary>An AI builder further than this from its dock blueprint is flown there.</summary>
    private const float DockAirliftMeters = 1500f;

    /// <summary>How long a builder may drive through the building it just finished.</summary>
    private const float BuilderGhostSeconds = 5f;

    /// <summary>Scaled seconds an AI builder short of its blueprint may go without moving
    /// <see cref="BuilderStuckMeters"/> before it is let go of the job.</summary>
    private const float BuilderStuckSeconds = 90f;

    private const float BuilderStuckMeters = 15f;

    /// <summary>How long a builder let go for being stuck is left out of the job list, so the same
    /// one is not sent straight back into the same hole.</summary>
    private const float BuilderRestSeconds = 300f;

    private readonly List<ConstructionSite> constructionSites = new();
    private readonly List<Unit> idleBuilderScratch = new();
    private readonly List<GhostedBuilder> ghostedBuilders = new();

    /// <summary>Where each AI builder short of its blueprint last moved, and when.</summary>
    private readonly Dictionary<Unit, (GlobalPosition Position, float Since)> builderProgress = new();

    /// <summary>AI builders let go for being stuck, and until when nothing sends them anywhere.</summary>
    private readonly Dictionary<Unit, float> builderResting = new();

    private readonly List<Unit> builderPruneScratch = new();

    /// <summary>Build orders queued behind travel points: the blueprint, and the order they wait on.</summary>
    private readonly Dictionary<Unit, (GlobalPosition Site, object Order)> queuedBuildOrders = new();
    private readonly List<Unit> queuedBuildScratch = new();
    private readonly List<Unit> queuedBuildOne = new(1);
    private float nextQueuedBuildCheckAt;
    private float nextConstructionReviewAt;

    /// <summary>The site being spawned right now. It has already left the list, and its builders
    /// must still not count as blocking it.</summary>
    private ConstructionSite? finishingSite;

    /// <summary>Seconds one builder takes to put up any building.</summary>
    internal static float ConstructionSeconds => Mathf.Max(0.5f, CommanderSettings.ConstructionSeconds);

    /// <summary>Blueprints still waiting to be finished. The site check reads these, because a
    /// blueprint has no collider and a second building could otherwise be placed on top of one.</summary>
    internal static IReadOnlyList<ConstructionSite>? Blueprints => Instance?.constructionSites;

    /// <summary>How far an AI builder is trusted to drive to any other blueprint, and past which it is
    /// flown. Its post is at a held base and every AI blueprint is inside the build radius of one,
    /// so twice that radius is its own base; further is another island or across a range.</summary>
    private static float AiBuilderDriveMeters => CommanderSettings.BuildRadiusKm * 2000f;

    /// <summary>
    /// <paramref name="hq"/>'s blueprints of <paramref name="kind"/> (any kind when null). The enemy
    /// ladder counts a blueprint as already built, which is what lets it have one going up at every
    /// base at once without queueing the same rung twice.
    /// </summary>
    internal static int CountBlueprints(FactionHQ hq, CommanderBuildKind? kind = null)
    {
        CommanderEconomyService? service = Instance;
        int count = 0;
        for (int i = 0; service != null && i < service.constructionSites.Count; i++)
        {
            ConstructionSite site = service.constructionSites[i];
            if (ReferenceEquals(site.Hq, hq) && (kind == null || site.Kind == kind))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary><paramref name="hq"/>'s structure blueprints of that category within
    /// <paramref name="radius"/> of <paramref name="position"/>, anywhere at <c>float.MaxValue</c>.</summary>
    internal static int CountBlueprintsNear(FactionHQ hq, BuildingType type, GlobalPosition position, float radius)
    {
        CommanderEconomyService? service = Instance;
        int count = 0;
        for (int i = 0; service != null && i < service.constructionSites.Count; i++)
        {
            ConstructionSite site = service.constructionSites[i];
            if (!ReferenceEquals(site.Hq, hq)
                || IsIncomeKind(site.Kind)
                || site.Kind == CommanderBuildKind.Factory
                || site.Definition.buildingType != type)
            {
                continue;
            }

            Vector3 offset = site.Site - position;
            offset.y = 0f;
            if (radius >= float.MaxValue || offset.sqrMagnitude <= radius * radius)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Is <paramref name="unit"/> on a construction job? A builder ignores repair work and
    /// never counts as blocking a building site.</summary>
    internal static bool IsBuilder(Unit? unit)
    {
        CommanderEconomyService? service = Instance;
        if (unit == null || service == null)
        {
            return false;
        }

        for (int i = 0; i < service.constructionSites.Count; i++)
        {
            if (service.constructionSites[i].Builders.Contains(unit))
            {
                return true;
            }
        }

        return service.finishingSite?.Builders.Contains(unit) == true;
    }

    /// <summary>
    /// Sends a builder that something else stopped back to its blueprint. A cargo run hands its
    /// vehicle over stopped and on hold, and <see cref="ReviewBuilders"/> never re-sends one already
    /// on a site's list: a Jackknife flown in beside its blueprint stood still on the LZ.
    /// </summary>
    internal static void ResumeBuilder(Unit unit)
    {
        CommanderEconomyService? service = Instance;
        for (int i = 0; service != null && i < service.constructionSites.Count; i++)
        {
            if (service.constructionSites[i].Builders.Contains(unit))
            {
                SendBuilder(service.constructionSites[i], unit);
                return;
            }
        }
    }

    /// <summary>
    /// The faction has blueprints and not one builder on any of them. A second blueprint queued
    /// behind one that is going up has no builders either, but that one is waiting its turn, not
    /// stuck — which is why this asks about the faction, not about each site.
    /// </summary>
    internal bool HasBlueprintWithoutBuilders(FactionHQ hq)
    {
        bool anyBlueprint = false;
        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite site = constructionSites[i];
            if (!ReferenceEquals(site.Hq, hq))
            {
                continue;
            }

            if (site.Builders.Count > 0)
            {
                return false;
            }

            anyBlueprint = true;
        }

        return anyBlueprint;
    }

    /// <summary><paramref name="hq"/>'s blueprints nobody is on yet: waiting their turn or stuck.</summary>
    internal int CountBlueprintsWithoutBuilders(FactionHQ hq)
    {
        int count = 0;
        for (int i = 0; i < constructionSites.Count; i++)
        {
            if (ReferenceEquals(constructionSites[i].Hq, hq) && constructionSites[i].Builders.Count == 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// A right-click on a blueprint with nothing but repair vehicles selected: those build it. It is
    /// the only way to send builders beyond <see cref="AutoBuildRangeMeters"/>, and it replaces a
    /// plain move order that parked the vehicle on the slab, stopped it being a builder, and so made
    /// it the thing blocking the building from spawning.
    /// </summary>
    internal bool TryAssignBuilders(IReadOnlyList<Unit> units, GlobalPosition point, bool append = false)
    {
        if (units.Count == 0 || constructionSites.Count == 0)
        {
            return false;
        }

        CommanderRepairService? repair = CommanderRepairService.Instance;
        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            if (unit is not GroundVehicle || unit.disabled || repair == null || !CommanderRepairService.IsRepairUnit(unit))
            {
                return false;
            }
        }

        // Only this commander's own blueprints: a multiplayer guest's sit in the same list.
        ConstructionSite? site = FindSiteAt(point, CommanderGameAccess.GetLocalHq());
        if (site == null)
        {
            return false;
        }

        CommanderMoveService? moves = CommanderMoveService.Instance;

        // Queued onto travel points: the route is driven first and the build order handed over at
        // its end (TickQueuedBuildOrders), so the slot is picked from where the route finishes.
        if (append && moves != null)
        {
            List<Unit> now = new();
            for (int i = 0; i < units.Count; i++)
            {
                object? order = moves.GetOrderHandle(units[i]);
                if (order != null)
                {
                    queuedBuildOrders[units[i]] = (site.Site, order);
                }
                else
                {
                    now.Add(units[i]);
                }
            }

            if (now.Count < units.Count)
            {
                CommanderOrderPing.Show(site.Site, attack: false);
                StatusText = $"{units.Count} repair vehicle(s) will build the {site.Label} at the end of their route.";
                if (now.Count == 0)
                {
                    return true;
                }

                units = now;
            }
        }

        string[] request = new string[units.Count + 1];
        request[0] = CommanderNetwork.Position(site.Site);
        for (int i = 0; i < units.Count; i++)
        {
            // A move order is how a builder is let go (ReviewBuilders), so any it carries has to go.
            // On a guest that is the guest's own order, which would drive it straight off the job.
            moves?.ClearOrder(units[i]);
            queuedBuildOrders.Remove(units[i]);
            request[i + 1] = CommanderNetwork.Id(units[i]);
        }

        if (!CommanderNetwork.IsHandlingGuest)
        {
            CommanderOrderPing.Show(site.Site, attack: false);
        }

        StatusText = $"{units.Count} repair vehicle(s) sent to build the {site.Label}.";

        // A guest's blueprint is only the host's picture of it (ApplyBlueprints): the host sends the builders.
        if (CommanderNetwork.Forward("build.assign", request))
        {
            return true;
        }

        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            for (int s = 0; s < constructionSites.Count; s++)
            {
                if (!ReferenceEquals(constructionSites[s], site))
                {
                    constructionSites[s].Builders.Remove(unit);
                }
            }

            // Always re-sent: a builder already on this site's list that stopped short (set down off
            // a hook, say) ignored the build order and only a plain move order shifted it.
            SendBuilder(site, unit);
        }

        return true;
    }

    /// <summary>The cancel key over the world or the map: this commander's blueprint under the cursor.</summary>
    internal bool TryCancelBlueprintAt(GlobalPosition point)
    {
        ConstructionSite? site = FindSiteAt(point, CommanderGameAccess.GetLocalHq());
        if (site == null)
        {
            return false;
        }

        CancelBlueprint(site);
        return true;
    }

    /// <summary>
    /// Takes a mis-placed blueprint back at its full price, however far along it is: nothing spawns
    /// before 100 %, so an unfinished blueprint has paid out nothing and cancelling it gains nothing.
    /// </summary>
    internal void CancelBlueprint(ConstructionSite site)
    {
        // A guest's list is the host's picture of it (ApplyBlueprints), and only the host can refund.
        if (CommanderNetwork.Forward("build.cancel", CommanderNetwork.Position(site.Site)))
        {
            StatusText = CommanderNetwork.HostHasMod ? $"Cancelling the {site.Label} blueprint." : CommanderNetwork.HostMissingReason;
            return;
        }

        if (!constructionSites.Remove(site))
        {
            return;
        }

        CommanderRepairService? repair = CommanderRepairService.Instance;
        for (int i = 0; i < site.Builders.Count; i++)
        {
            repair?.SendHome(site.Builders[i]);
        }

        site.Hq.AddFunds(site.Cost);
        StatusText = $"Blueprint cancelled: {site.Label}. Refunded {FundsLabel(site.Cost)}.";
    }

    /// <summary>
    /// Host, off <see cref="CommanderCaptureSpoils.HandOver"/>. A blueprint belongs to the base it was
    /// sited from, and nothing else ever cancels one: when that base changes hands the loser's
    /// builders kept driving into the winner's base to work on it, and it kept blocking the winner's
    /// own placements there. Same rule as the building hand-over: within the build radius, unless its
    /// owner still holds a nearer base.
    /// </summary>
    internal void DropBlueprintsOnCapture(Airbase airbase, Vector3 center, float radiusSq, FactionHQ winner)
    {
        CommanderRepairService? repair = CommanderRepairService.Instance;
        for (int i = constructionSites.Count - 1; i >= 0; i--)
        {
            ConstructionSite site = constructionSites[i];
            if (site.Hq == null || site.Hq == winner)
            {
                continue;
            }

            Vector3 position = site.Site.ToLocalPosition();
            float distanceSq = (position - center).sqrMagnitude;
            if (distanceSq > radiusSq || CommanderCaptureSpoils.OwnerHasNearerBase(site.Hq, airbase, position, distanceSq))
            {
                continue;
            }

            constructionSites.RemoveAt(i);
            for (int b = 0; b < site.Builders.Count; b++)
            {
                repair?.SendHome(site.Builders[b]);
            }

            bool local = ReferenceEquals(site.Hq, CommanderGameAccess.GetLocalHq());
            string hostStatus = StatusText;
            RefundConstruction(site, $"The {site.Label} blueprint was lost with its base.", local);
            if (!local)
            {
                StatusText = hostStatus;
            }
        }
    }

    /// <summary>
    /// An AI blueprint whose builder is further off than it can drive: <see cref="DockAirliftMeters"/>
    /// for a dock, <see cref="AiBuilderDriveMeters"/> for anything else (shores and other islands may
    /// have no road). The enemy's airlift flies the builder there instead.
    /// </summary>
    internal bool TryGetStrandedBuilder(FactionHQ hq, out GlobalPosition site, out Unit builder)
    {
        for (int s = 0; s < constructionSites.Count; s++)
        {
            ConstructionSite candidate = constructionSites[s];
            if (!ReferenceEquals(candidate.Hq, hq) || candidate.Progress > 0f)
            {
                continue;
            }

            float limit = candidate.Kind == CommanderBuildKind.NavalDock ? DockAirliftMeters : AiBuilderDriveMeters;
            for (int b = 0; b < candidate.Builders.Count; b++)
            {
                Unit unit = candidate.Builders[b];
                if (unit == null || unit.disabled || unit.IsSlung())
                {
                    continue;
                }

                Vector3 offset = unit.GlobalPosition() - candidate.Site;
                offset.y = 0f;
                if (offset.sqrMagnitude > limit * limit)
                {
                    site = candidate.Site;
                    builder = unit;
                    return true;
                }
            }
        }

        site = default;
        builder = null!;
        return false;
    }

    /// <summary>
    /// Repair vehicles with nothing better to do. A paid crew is on its way to the building it was
    /// hired for, a SAM-site Jacknife belongs to its site, and anything under a player order or the
    /// Hold Position stance is where the player wants it.
    /// </summary>
    private static void CollectIdleBuilders(FactionHQ hq, List<Unit> into)
    {
        into.Clear();
        CommanderRepairService? repair = CommanderRepairService.Instance;
        CommanderMoveService? moves = CommanderMoveService.Instance;
        if (hq == null || repair == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle
                || unit.disabled
                || !CommanderRepairService.IsRepairUnit(unit)
                || IsBuilder(unit)
                || repair.TryGetAssignedTarget(unit, out _)
                || CommanderSamSiteService.IsReservedConstructionJacknife(unit)
                || moves?.TryGetOrder(unit, out _, out _, out _) == true
                || moves?.GetStance(unit) == CommanderStance.HoldPosition)
            {
                continue;
            }

            into.Add(unit);
        }
    }

    private static GlobalPosition GetRingSlot(ConstructionSite site, float angle)
    {
        return CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            site.Site.x + (Mathf.Cos(angle) * site.RingRadius),
            site.Site.y,
            site.Site.z + (Mathf.Sin(angle) * site.RingRadius)));
    }

    private static void SendBuilder(ConstructionSite site, Unit builder)
    {
        // The first slot faces the builder: a blueprint has no collider, so the pathfinder drives
        // straight across it to a slot on the far side, and the building then rises round the truck.
        Vector3 from = builder.GlobalPosition() - site.Site;
        float angle = Mathf.Atan2(from.z, from.x) + (site.BuildersSent++ * BuilderSpacingRadians);

        // A blueprint beside a depot has ring slots in the door lane, and a truck parked there for the
        // whole build is what the next spawn is pushed into: walk round the ring to a slot clear of it.
        List<Transform> doors = DoorsNear(site.Site.ToLocalPosition(), site.RingRadius + DoorSearchMeters);
        GlobalPosition post = GetRingSlot(site, angle);
        for (int step = 1; step < 12 && IsInDepotLane(post, doors); step++)
        {
            post = GetRingSlot(site, angle + (step * Mathf.PI / 6f));
        }

        // A stopped vehicle is held in place whatever it is told next.
        CommanderGameAccess.SetUnitHoldPosition(builder, false);

        // And one whose order lapses on arrival drives off at the enemy once the building is up.
        // AI builders drive with ROADS: OFF, since sites sit around the base and roads go the long way.
        // Only round the base, though: off road is a straight line over whatever is in the way.
        if (builder is GroundVehicle vehicle)
        {
            CommanderRepairService.KeepAtCommandedDestination(vehicle);
            if (CommanderEnemyCommanderService.IsAiFaction(site.Hq, CommanderGameAccess.GetLocalHq()))
            {
                if (from.sqrMagnitude <= AiBuilderOffRoadMeters * AiBuilderOffRoadMeters)
                {
                    CommanderDirectPathService.ForceEnabled(vehicle);
                }
                else
                {
                    CommanderDirectPathService.Forget(vehicle);
                }
            }
        }

        if (CommanderGameAccess.TrySetDestination(builder, post))
        {
            if (!site.Builders.Contains(builder))
            {
                site.Builders.Add(builder);
            }

            // The timeout is "nobody came", not "nobody came within 300 s of queueing": a builder
            // sent from the home base to a blueprint at a captured field 10-45 km away never made
            // it, so every build away from home was refunded and queued again for the whole match.
            site.QueuedAt = Mathf.Max(site.QueuedAt, Time.time + (from.magnitude / BuilderTravelMetersPerSecond));
        }
    }

    private static void SetIgnored(Collider[] walls, Collider[] hull, bool ignore)
    {
        for (int w = 0; w < walls.Length; w++)
        {
            for (int h = 0; h < hull.Length; h++)
            {
                // Either side may have been destroyed in the meantime.
                if (walls[w] != null && hull[h] != null)
                {
                    Physics.IgnoreCollision(walls[w], hull[h], ignore);
                }
            }
        }
    }

    /// <summary>The blueprint of <paramref name="hq"/>'s whose builder ring contains <paramref name="point"/>, nearest first.</summary>
    private ConstructionSite? FindSiteAt(GlobalPosition point, FactionHQ? hq)
    {
        ConstructionSite? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite site = constructionSites[i];
            if (!ReferenceEquals(site.Hq, hq))
            {
                continue;
            }

            Vector3 offset = point - site.Site;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance <= site.RingRadius && distance < bestDistance)
            {
                bestDistance = distance;
                best = site;
            }
        }

        return best;
    }

    /// <summary>The paid click: queues the blueprint and sends builders straight away.</summary>
    private void PlaceBlueprint(
        CommanderBuildKind kind,
        BuildingDefinition definition,
        UnitDefinition? production,
        FactionHQ hq,
        GlobalPosition position,
        float cost,
        string label,
        Quaternion rotation)
    {
        ConstructionSite site = new(
            kind,
            definition,
            production,
            hq,
            CommanderGameAccess.SnapToTerrain(position),
            cost,
            label,
            GetRingRadius(definition))
        {
            Rotation = rotation,
            QueuedAt = Time.time,
        };
        constructionSites.Add(site);
        ReviewBuilders();

        int builders = 0;
        for (int i = 0; i < constructionSites.Count; i++)
        {
            builders += constructionSites[i].Builders.Count;
        }

        // Loud, because a blueprint that silently never starts reads as a broken build button.
        if (builders == 0)
        {
            StatusText = $"Blueprint placed: {label}. It needs a repair vehicle to build it.";
            CommanderNotice.Fail("No free repair vehicle nearby. Select repair vehicles and right-click the blueprint.");
            return;
        }

        StatusText = site.Builders.Count > 0
            ? $"Blueprint placed: {label}. {site.Builders.Count} builder(s) on the way."
            : $"Blueprint placed: {label}. Queued behind the current build.";
    }

    /// <summary>
    /// The enemy commander's build: paid for here, put up by its own repair vehicles, finished by
    /// <see cref="FinishSite(ConstructionSite)"/> like the player's. The same site rule runs first,
    /// with the facing it will be built at, so a blueprint that can never be finished is never paid for.
    /// </summary>
    private bool QueueEnemyBlueprint(
        FactionHQ hq,
        CommanderBuildKind kind,
        BuildingDefinition? definition,
        UnitDefinition? production,
        GlobalPosition position,
        float cost,
        string label,
        Quaternion? facing = null)
    {
        Quaternion rotation = facing ?? Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        if (definition == null
            || !preview.IsSiteAllowed(definition, position, hq, out _, kind == CommanderBuildKind.NavalDock, rotation)
            || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return false;
        }

        constructionSites.Add(new ConstructionSite(
            kind,
            definition,
            production,
            hq,
            CommanderGameAccess.SnapToTerrain(position),
            cost,
            label,
            GetRingRadius(definition))
        {
            Rotation = rotation,
            QueuedAt = Time.time,
        });
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) placed a blueprint for a {label}; its builders put it up.");
        return true;
    }

    /// <summary>Where the builders park, measured from the centre.</summary>
    private float GetRingRadius(BuildingDefinition definition)
    {
        return preview.GetReach(definition) + BuilderStandoffMeters;
    }

    /// <summary>Per frame in both modes, so a build carries on while the player is flying.</summary>
    private void TickConstruction()
    {
        RestoreGhostedBuilders();
        if (constructionSites.Count == 0)
        {
            queuedBuildOrders.Clear();
            return;
        }

        if (queuedBuildOrders.Count > 0 && CommanderScheduler.IsDue(ref nextQueuedBuildCheckAt, ConstructionReviewSeconds))
        {
            TickQueuedBuildOrders();
        }

        // A guest's list is the host's picture of its blueprints (ApplyBlueprints), so it is only
        // drawn: the host runs the builders and the spawn. It rises at the host's last rate between
        // syncs.
        if (CommanderNetwork.IsGuest)
        {
            for (int i = 0; i < constructionSites.Count; i++)
            {
                ConstructionSite copy = constructionSites[i];
                copy.Progress = Mathf.Min(1f, copy.Progress + (copy.RemoteRate * Time.deltaTime));
                preview.DrawBlueprint(copy.Definition, copy.Site, copy.Progress, copy.Rotation);
            }

            return;
        }

        if (CommanderScheduler.IsDue(ref nextConstructionReviewAt, ConstructionReviewSeconds))
        {
            ReviewBuilders();
        }

        // Scaled time, so a pause stops the build and 2x/4x speeds it up like everything else.
        float step = Time.deltaTime / ConstructionSeconds;
        for (int i = constructionSites.Count - 1; i >= 0; i--)
        {
            ConstructionSite site = constructionSites[i];
            site.Progress += site.Working * step;
            if (site.Progress < 1f)
            {
                if (site.Progress <= 0f
                    && Time.time - site.QueuedAt > EnemyBlueprintTimeoutSeconds
                    && CommanderEnemyCommanderService.IsAiFaction(site.Hq, CommanderGameAccess.GetLocalHq()))
                {
                    constructionSites.RemoveAt(i);
                    site.Hq.AddFunds(site.Cost);
                    CommanderPlugin.Log.LogInfo(
                        $"Enemy commander ({site.Hq.faction.name}) gave up on its {site.Label} blueprint: "
                            + "no builder reached it. Refunded.");
                    continue;
                }

                // Fog: the host holds every faction's blueprints; it only draws its own side's.
                if (!CommanderFogOfWarService.HidesFaction(site.Hq))
                {
                    preview.DrawBlueprint(site.Definition, site.Site, site.Progress, site.Rotation);
                }

                continue;
            }

            // Off the list first: a finished building's own blueprint must not block its site.
            constructionSites.RemoveAt(i);
            CompleteConstruction(site);
        }
    }

    /// <summary>
    /// Hands each queued build order over once its route is done (finished orders are removed on
    /// arrival). A new order in the meantime is a different order object, and drops the build.
    /// Runs on a guest too: there the assignment is forwarded to the host as a normal build order.
    /// </summary>
    private void TickQueuedBuildOrders()
    {
        CommanderMoveService? moves = CommanderMoveService.Instance;
        queuedBuildScratch.Clear();
        queuedBuildScratch.AddRange(queuedBuildOrders.Keys);
        for (int i = 0; i < queuedBuildScratch.Count; i++)
        {
            Unit unit = queuedBuildScratch[i];
            (GlobalPosition site, object order) = queuedBuildOrders[unit];
            object? current = unit != null && !unit.disabled ? moves?.GetOrderHandle(unit) : null;
            if (ReferenceEquals(current, order))
            {
                continue;
            }

            queuedBuildOrders.Remove(unit!);
            if (current == null && unit != null && !unit.disabled)
            {
                queuedBuildOne.Clear();
                queuedBuildOne.Add(unit);
                TryAssignBuilders(queuedBuildOne, site);
            }
        }
    }

    /// <summary>A builder of <paramref name="hq"/>'s already on a job within driving range of
    /// <paramref name="site"/>: it will be free there soon, so the blueprint waits for it.</summary>
    private bool HasBusyBuilderNear(FactionHQ hq, GlobalPosition site)
    {
        float range = AiBuilderDriveMeters;
        for (int s = 0; s < constructionSites.Count; s++)
        {
            ConstructionSite other = constructionSites[s];
            if (!ReferenceEquals(other.Hq, hq))
            {
                continue;
            }

            for (int b = 0; b < other.Builders.Count; b++)
            {
                Unit unit = other.Builders[b];
                if (unit != null && !unit.disabled && !unit.IsSlung() && FastMath.InRange(unit.GlobalPosition(), site, range))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Counts who is actually on site, lets go of builders that died or were given an order, and
    /// sends each free repair vehicle to the oldest blueprint within its reach, so a queue of
    /// blueprints at one base goes up one after another at full speed rather than all at once at a
    /// crawl.
    /// </summary>
    private void ReviewBuilders()
    {
        CommanderMoveService? moves = CommanderMoveService.Instance;
        for (int s = 0; s < constructionSites.Count; s++)
        {
            ConstructionSite site = constructionSites[s];
            float reach = site.RingRadius + BuilderReachMeters;
            site.Working = 0;
            for (int b = site.Builders.Count - 1; b >= 0; b--)
            {
                Unit builder = site.Builders[b];

                // A move order from the player is how a builder is pulled off the job.
                if (builder == null || builder.disabled || moves?.TryGetOrder(builder, out _, out _, out _) == true)
                {
                    site.Builders.RemoveAt(b);
                    continue;
                }

                // A builder dangling off a sling lift over the site is not building it.
                Vector3 offset = builder.GlobalPosition() - site.Site;
                offset.y = 0f;
                if (!builder.IsSlung() && offset.sqrMagnitude <= reach * reach)
                {
                    site.Working++;
                    builderProgress.Remove(builder);
                }
                else if (!builder.IsSlung() && IsStuckAiBuilder(site, builder))
                {
                    site.Builders.RemoveAt(b);
                }
            }
        }

        // A multiplayer guest's blueprints sit in the same list, so each faction's builders go to
        // that faction's sites only.
        CommanderRepairService? repair = CommanderRepairService.Instance;
        float rangeSquared = AutoBuildRangeMeters * AutoBuildRangeMeters;
        for (int h = 0; h < constructionSites.Count; h++)
        {
            FactionHQ hq = constructionSites[h].Hq;
            bool seen = false;
            for (int j = 0; j < h && !seen; j++)
            {
                seen = ReferenceEquals(constructionSites[j].Hq, hq);
            }

            if (seen)
            {
                continue;
            }

            // One builder per blueprint; each unmanned blueprint (oldest first) takes the nearest free
            // builder. AI builders have no range limit; the player's only take sites near their standby post.
            bool ai = CommanderEnemyCommanderService.IsAiFaction(hq, CommanderGameAccess.GetLocalHq());
            CollectIdleBuilders(hq, idleBuilderScratch);
            if (ai)
            {
                for (int i = idleBuilderScratch.Count - 1; i >= 0; i--)
                {
                    if (builderResting.TryGetValue(idleBuilderScratch[i], out float until) && Time.time < until)
                    {
                        idleBuilderScratch.RemoveAt(i);
                    }
                }
            }

            for (int s = 0; s < constructionSites.Count && idleBuilderScratch.Count > 0; s++)
            {
                ConstructionSite site = constructionSites[s];
                if (!ReferenceEquals(site.Hq, hq) || site.Builders.Count > 0)
                {
                    continue;
                }

                // An AI builder stays at its own base, so it never drives across country. A base with
                // none of its own gets one spawned there. Only the dock still takes a builder from
                // anywhere, because the airlift flies it out to the shore (TryGetStrandedBuilder).
                bool local = ai && site.Kind != CommanderBuildKind.NavalDock;
                float driveSquared = AiBuilderDriveMeters * AiBuilderDriveMeters;
                int nearest = -1;
                float best = local ? driveSquared : ai ? float.MaxValue : rangeSquared;
                for (int i = 0; i < idleBuilderScratch.Count; i++)
                {
                    Unit builder = idleBuilderScratch[i];
                    GlobalPosition home = repair?.TryGetStandbyPost(builder, out GlobalPosition post) == true
                        ? post
                        : builder.GlobalPosition();
                    Vector3 offset = home - site.Site;
                    offset.y = 0f;
                    if (offset.sqrMagnitude <= best)
                    {
                        best = offset.sqrMagnitude;
                        nearest = i;
                    }
                }

                // An AI builder further than it can drive is flown (TryGetStrandedBuilder), so one
                // waits for a free builder of its own base rather than taking another base's: on an
                // archipelago the nearest free builder was on the next island, it stood on its shore
                // for the rest of the match, and its own base bought another to replace it.
                if (ai && (nearest < 0 || best > driveSquared) && HasBusyBuilderNear(hq, site.Site))
                {
                    // Waiting on purpose is not "nobody came": the timeout refunded these and the
                    // ladder queued them again all match, the dock among them.
                    site.QueuedAt = Time.time;
                    continue;
                }

                if (local && nearest < 0)
                {
                    if (TrySpawnLocalBuilder(site))
                    {
                        site.QueuedAt = Time.time;
                    }

                    continue;
                }

                if (nearest >= 0)
                {
                    SendBuilder(site, idleBuilderScratch[nearest]);
                    idleBuilderScratch.RemoveAt(nearest);
                }
            }
        }

        idleBuilderScratch.Clear();
    }

    /// <summary>
    /// A paid builder rolled out beside the depot of the base <paramref name="site"/> is at, for an AI
    /// blueprint with no builder of that base's own. A base with no depot gets none here: the airlift
    /// lands one there (<c>ReviewBaseAirlift</c>).
    /// </summary>
    private bool TrySpawnLocalBuilder(ConstructionSite site)
    {
        FactionHQ hq = site.Hq;
        VehicleDefinition? builder = CommanderGameAccess.FindRepairVehicleDefinition(hq);
        float cost = builder != null ? Mathf.Max(0f, builder.value) : 0f;
        if (Time.time < site.NextBuilderSpawnAt
            || builder == null
            || hq.factionFunds < cost
            || CommanderUnitCap.IsGroundFull(hq)
            || !hq.TryGetNearestDepot(site.Site.ToLocalPosition(), AiBuilderDriveMeters, out VehicleDepot depot)
            || !TryGiveStartingBuilders(hq, depot, 1)
            || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return false;
        }

        site.NextBuilderSpawnAt = Time.time + LocalBuilderSpawnSeconds;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) rolled out a builder at {depot.name} for its {site.Label} blueprint for {cost:0}.");
        return true;
    }

    /// <summary>
    /// An AI builder that has not moved for <see cref="BuilderStuckSeconds"/> on its way to a blueprint,
    /// so the job can be handed to a free builder instead.
    /// </summary>
    private bool IsStuckAiBuilder(ConstructionSite site, Unit builder)
    {
        if (!CommanderEnemyCommanderService.IsAiFaction(site.Hq, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        // One further than it can drive is waiting for the airlift (TryGetStrandedBuilder), which
        // only takes a builder that is still on the job: a first version let 45 km ones go.
        GlobalPosition at = builder.GlobalPosition();
        if (FastMath.OutOfRange(at, site.Site, site.Kind == CommanderBuildKind.NavalDock ? DockAirliftMeters : AiBuilderDriveMeters))
        {
            builderProgress.Remove(builder);
            return false;
        }

        if (!builderProgress.TryGetValue(builder, out (GlobalPosition Position, float Since) last)
            || FastMath.OutOfRange(at, last.Position, BuilderStuckMeters))
        {
            builderProgress[builder] = (at, Time.time);
            return false;
        }

        if (Time.time - last.Since < BuilderStuckSeconds)
        {
            return false;
        }

        builderProgress.Remove(builder);
        builderResting[builder] = Time.time + BuilderRestSeconds;
        PruneBuilderRecords();
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({site.Hq.faction.name}) let a stuck builder go from its {site.Label} blueprint "
                + $"({(at - site.Site).magnitude:0} m short); another takes the job.");
        return true;
    }

    private void PruneBuilderRecords()
    {
        builderPruneScratch.Clear();
        foreach (Unit unit in builderProgress.Keys)
        {
            if (unit == null || unit.disabled)
            {
                builderPruneScratch.Add(unit!);
            }
        }

        foreach (KeyValuePair<Unit, float> entry in builderResting)
        {
            if (entry.Key == null || entry.Key.disabled || Time.time >= entry.Value)
            {
                builderPruneScratch.Add(entry.Key!);
            }
        }

        foreach (Unit unit in builderPruneScratch)
        {
            builderProgress.Remove(unit);
            builderResting.Remove(unit);
        }
    }

    /// <summary>
    /// 100 %: the real building, through the same spawn every build ends in. The site is checked
    /// again because time has passed since the click (a base lost, a tank parked on the slab), and a
    /// build that cannot finish hands the money back rather than keeping it.
    /// </summary>
    private void CompleteConstruction(ConstructionSite site)
    {
        // Its own builders park round it, and one that ended up on the slab must not refuse it.
        finishingSite = site;
        try
        {
            FinishSite(site);
        }
        finally
        {
            finishingSite = null;
        }
    }

    private void FinishSite(ConstructionSite site)
    {
        // Another faction's building (a guest's, the enemy commander's) must not write the host's
        // BUILD status line or put up the host's red banner.
        bool local = ReferenceEquals(site.Hq, CommanderGameAccess.GetLocalHq());
        string hostStatus = StatusText;
        try
        {
            FinishSite(site, local);
        }
        finally
        {
            if (!local)
            {
                StatusText = hostStatus;
            }
        }
    }

    private void FinishSite(ConstructionSite site, bool local)
    {
        // The builders' job is over either way: back to standby at their own base.
        CommanderRepairService? repair = CommanderRepairService.Instance;
        for (int i = 0; i < site.Builders.Count; i++)
        {
            repair?.SendHome(site.Builders[i]);
        }

        FactionHQ hq = site.Hq;
        if (hq == null)
        {
            return;
        }

        if (!preview.IsSiteAllowed(
                site.Definition, site.Site, hq, out string blocked, site.Kind == CommanderBuildKind.NavalDock, site.Rotation))
        {
            RefundConstruction(site, $"The {site.Label} could not be finished. {blocked}", local);
            return;
        }

        Unit? built;
        switch (site.Kind)
        {
            case CommanderBuildKind.Mine:
            case CommanderBuildKind.FuelExchange:
            case CommanderBuildKind.GasTerminal:
                built = SpawnIncome(hq, site.Kind, site.Site, facing: site.Rotation);
                StatusText = $"{GetIncomeName(site.Kind)} built. +{FundsLabel(GetIncomePerMinute(site.Kind, 1))}/min.";
                break;
            case CommanderBuildKind.NavalDock:
                built = SpawnNavalDock(hq, site.Site, facing: site.Rotation);
                StatusText = $"Naval dock built. Level 1 unlocks {CommanderNavalPurchaseService.GetLevelUnlockLabel(1)}.";
                break;
            case CommanderBuildKind.Factory:
                // Checked non-null at the click, which is also where the product was chosen.
                built = SpawnFactory(hq, site.Site, site.Production!, facing: site.Rotation);
                StatusText = $"{CommanderGameAccess.GetVehicleLabel(site.Production!)} factory built. "
                    + $"One unit every {FactoryProductionSeconds:0}s.";
                break;
            default:
                built = PlaceStructure(hq, site.Site, site.Definition, site.Production, site.Rotation);
                break;
        }

        if (built == null)
        {
            RefundConstruction(site, $"The {site.Label} could not be built.", local);
            return;
        }

        if (site.Kind == CommanderBuildKind.Mine && site.Cost <= 0f)
        {
            EnemyFreeMinesBuilt[hq] = (EnemyFreeMinesBuilt.TryGetValue(hq, out int free) ? free : 0) + 1;
        }

        if (!local)
        {
            CommanderPlugin.Log.LogInfo($"{hq.faction?.name} finished building a {site.Label}.");
        }

        GhostBuilders(built, site.Builders);
        RefreshFriendlyLists();
    }

    /// <summary>
    /// The finished building's colliders ignore its own builders for <see cref="BuilderGhostSeconds"/>.
    /// A builder that ended up on the slab, or wedged against a wall that rose round it, would
    /// otherwise be stuck there for good; this lets it drive out on its way home.
    /// </summary>
    private void GhostBuilders(Unit building, List<Unit> builders)
    {
        Collider[] walls = building.GetComponentsInChildren<Collider>(true);
        for (int b = 0; b < builders.Count; b++)
        {
            if (builders[b] == null)
            {
                continue;
            }

            Collider[] hull = builders[b].GetComponentsInChildren<Collider>(true);
            SetIgnored(walls, hull, true);
            ghostedBuilders.Add(new GhostedBuilder(Time.time + BuilderGhostSeconds, walls, hull));
        }
    }

    private void RestoreGhostedBuilders()
    {
        for (int i = ghostedBuilders.Count - 1; i >= 0; i--)
        {
            GhostedBuilder ghost = ghostedBuilders[i];
            if (Time.time >= ghost.Until)
            {
                SetIgnored(ghost.Walls, ghost.Hull, false);
                ghostedBuilders.RemoveAt(i);
            }
        }
    }

    private void RefundConstruction(ConstructionSite site, string reason, bool local)
    {
        site.Hq.AddFunds(site.Cost);
        StatusText = $"{reason} Refunded.";
        if (local)
        {
            CommanderNotice.Fail(StatusText);
        }
        else
        {
            CommanderPlugin.Log.LogInfo($"{site.Hq.faction?.name}: {StatusText}");
        }
    }

    /// <summary>One paid blueprint, waiting for builders or under construction.</summary>
    internal sealed class ConstructionSite
    {
        internal ConstructionSite(
            CommanderBuildKind kind,
            BuildingDefinition definition,
            UnitDefinition? production,
            FactionHQ hq,
            GlobalPosition site,
            float cost,
            string label,
            float ringRadius)
        {
            Kind = kind;
            Definition = definition;
            Production = production;
            Hq = hq;
            Site = site;
            Cost = cost;
            Label = label;
            RingRadius = ringRadius;
        }

        internal CommanderBuildKind Kind { get; }

        internal BuildingDefinition Definition { get; }

        /// <summary>What a factory will produce, fixed at the click as it always was.</summary>
        internal UnitDefinition? Production { get; }

        internal FactionHQ Hq { get; }

        internal GlobalPosition Site { get; }

        internal float Cost { get; }

        internal string Label { get; }

        /// <summary>Where the builders park, measured from the centre.</summary>
        internal float RingRadius { get; }

        /// <summary>0 to 1.</summary>
        internal float Progress { get; set; }

        /// <summary>Builders within reach at the last review.</summary>
        internal int Working { get; set; }

        internal int BuildersSent { get; set; }

        /// <summary><c>Time.time</c> before which no builder is spawned for it again: the last one
        /// takes a moment to register and be seen as free.</summary>
        internal float NextBuilderSpawnAt { get; set; }

        /// <summary>Progress per second at the host's last sync. A guest's copies only.</summary>
        internal float RemoteRate { get; set; }

        /// <summary>The facing it will be built at: the player's rotate key, or the enemy's pick.</summary>
        internal Quaternion Rotation { get; set; } = Quaternion.identity;

        /// <summary><c>Time.time</c> when it was paid for.</summary>
        internal float QueuedAt { get; set; }

        internal List<Unit> Builders { get; } = new();
    }

    /// <summary>A builder let through the building it finished, until <see cref="Until"/>.</summary>
    private sealed class GhostedBuilder
    {
        internal GhostedBuilder(float until, Collider[] walls, Collider[] hull)
        {
            Until = until;
            Walls = walls;
            Hull = hull;
        }

        internal float Until { get; }

        internal Collider[] Walls { get; }

        internal Collider[] Hull { get; }
    }
}
