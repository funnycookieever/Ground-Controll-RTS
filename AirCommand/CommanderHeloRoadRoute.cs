using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Sends AI helicopters along the roads on a long leg. <c>AutopilotHelo.AutoAim</c> flies a straight
/// line and only follows the ground's height, so the tallest mountain on that line got climbed.
/// Roads go round mountains, at low level. The last <see cref="DirectMeters"/> stay a straight line.
/// </summary>
internal static class CommanderHeloRoadRoute
{
    private const float DirectMeters = 4000f;

    /// <summary>A road this much longer than the straight line is not worth it.</summary>
    private const float MaxDetour = 2.5f;

    /// <summary>The destination has to move this far before the route is worth planning again:
    /// each plan is a synchronous search of the whole road network.</summary>
    private const float ReplanMeters = 1500f;

    private const float ReplanSeconds = 20f;

    /// <summary>Route points closer together than this add nothing.</summary>
    private const float PointSpacingMeters = 400f;

    /// <summary>Close enough to a route point to fly on to the next one.</summary>
    private const float ReachedMeters = 600f;

    private static readonly Dictionary<Aircraft, Route> Routes = new();
    private static readonly List<Node> Nodes = new();

    /// <summary>Prefix on <c>AutopilotHelo.AutoAim</c>; swaps the destination for the next road point.</summary>
    internal static void Apply(Autopilot autopilot, ref GlobalPosition destination)
    {
        Aircraft? aircraft = autopilot.aircraft;
        if (aircraft == null
            || aircraft.Player != null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || aircraft.pilots[0].currentState is not (AIHeloCombatState or AIHeloTransportState)
            || !CommanderSupplyHeliService.FollowsRoads(aircraft))
        {
            return;
        }

        GlobalPosition here = aircraft.GlobalPosition();
        float direct = Horizontal(here, destination);
        if (direct < DirectMeters)
        {
            Routes.Remove(aircraft);
            return;
        }

        if (!Routes.TryGetValue(aircraft, out Route route))
        {
            route = new Route();
            Routes[aircraft] = route;
        }

        if ((route.Points.Count == 0 || Horizontal(route.Destination, destination) > ReplanMeters)
            && Time.time >= route.NextPlanAt
            && CommanderScheduler.TryClaimHeavyFrame())
        {
            route.NextPlanAt = Time.time + ReplanSeconds;
            route.Destination = destination;
            Plan(route, here, destination, direct);
        }

        // A stale route for an old destination flies direct rather than the wrong way.
        if (route.Points.Count == 0 || Horizontal(route.Destination, destination) > ReplanMeters)
        {
            return;
        }

        while (route.Next < route.Points.Count - 1 && Horizontal(here, route.Points[route.Next]) < ReachedMeters)
        {
            route.Next++;
        }

        // Past the end of the road, or nearer the destination than the road is: the last leg is direct.
        GlobalPosition point = route.Points[route.Next];
        if (Horizontal(point, destination) < direct)
        {
            destination = point;
        }
    }

    internal static void Forget(Aircraft aircraft)
    {
        Routes.Remove(aircraft);
    }

    internal static void ResetSession()
    {
        Routes.Clear();
    }

    private static void Plan(Route route, GlobalPosition here, GlobalPosition destination, float direct)
    {
        route.Points.Clear();
        route.Next = 0;
        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network == null)
        {
            return;
        }

        RoadPathfinder.TryPathfind(network, here, destination, Nodes, out RoadPathfinder.PathfindResult result);
        if (result != RoadPathfinder.PathfindResult.Success)
        {
            return;
        }

        float length = 0f;
        for (int i = 0; i < Nodes.Count - 1; i++)
        {
            foreach (KeyValuePair<Road, Node> link in Nodes[i].connectionsLookup)
            {
                if (ReferenceEquals(link.Value, Nodes[i + 1]))
                {
                    length += link.Key.length;
                    AddRoad(route, link.Key, ReferenceEquals(link.Key.startNode, Nodes[i]));
                    break;
                }
            }
        }

        if (route.Points.Count == 0 || length > direct * MaxDetour)
        {
            route.Points.Clear();
            return;
        }

        // Join the road at the point nearest the aircraft, not back at the first node.
        float nearest = float.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            float distance = Horizontal(here, route.Points[i]);
            if (distance < nearest)
            {
                nearest = distance;
                route.Next = i;
            }
        }
    }

    private static void AddRoad(Route route, Road road, bool forward)
    {
        for (int i = 0; i < road.points.Count; i++)
        {
            GlobalPosition point = road.points[forward ? i : road.points.Count - 1 - i];
            if (route.Points.Count == 0 || Horizontal(route.Points[route.Points.Count - 1], point) >= PointSpacingMeters)
            {
                route.Points.Add(point);
            }
        }
    }

    private static float Horizontal(GlobalPosition a, GlobalPosition b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return Mathf.Sqrt((x * x) + (z * z));
    }

    private sealed class Route
    {
        internal List<GlobalPosition> Points { get; } = new();

        internal GlobalPosition Destination { get; set; }

        internal int Next { get; set; }

        internal float NextPlanAt { get; set; }
    }
}
