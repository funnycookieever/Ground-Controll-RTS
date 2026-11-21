using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Whoever takes an airbase takes what is standing on it. <c>Airbase.CaptureFaction</c> already
/// re-owns the buildings in <c>Airbase.buildings</c>, but that list only holds mission buildings
/// authored against that airbase: everything a commander built, and every mission building that
/// does not name the field, stayed with the loser. So a captured base kept the enemy's munitions
/// bunker, fuel depot and vehicle depots, still stocked, still theirs.
/// </summary>
internal static class CommanderCaptureSpoils
{
    /// <summary>
    /// <c>FactionHQ.AddDepot</c> is only called on spawn and on repair, never on an owner change,
    /// so a captured depot stays on the loser's spawn list: <c>DeployVehicles</c> would keep rolling
    /// the loser's reserve out of it, spawned with the depot's new faction, i.e. free units for the
    /// winner, and the winner's own reserve could never use it.
    /// </summary>
    internal static readonly AccessTools.FieldRef<FactionHQ, List<(VehicleDepot, float)>> DepotsRef =
        AccessTools.FieldRefAccess<FactionHQ, List<(VehicleDepot, float)>>("depotSorted");

    private static readonly List<Building> Taken = new();
    private static readonly HashSet<FactionHQ> Losers = new();

    /// <summary>
    /// Host only. Hands every building of another faction within the build radius to the new
    /// owner, unless that faction still holds an airbase nearer to it (the building belongs to that
    /// base, not this one). Neutral buildings are left alone, they are scenery.
    /// </summary>
    internal static void HandOver(Airbase airbase, FactionHQ? previous)
    {
        if (airbase.IsServer && airbase.CurrentHQ == null)
        {
            ReleaseToNeutral(airbase);
            return;
        }

        // Unity's overloaded == does not narrow nullability, hence the patterns.
        if (!airbase.IsServer || airbase.CurrentHQ is not FactionHQ winner || airbase.center is not Transform centerTransform)
        {
            return;
        }

        Vector3 center = centerTransform.position;
        float radius = Mathf.Max(CommanderSettings.BuildRadiusKm, 0.1f) * 1000f;
        float radiusSq = radius * radius;
        CommanderEconomyService.Instance?.DropBlueprintsOnCapture(airbase, center, radiusSq, winner);
        Taken.Clear();
        Losers.Clear();
        if (previous != null)
        {
            Losers.Add(previous);
        }

        for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
        {
            if (UnitRegistry.allUnits[i] is not Building building || building == null)
            {
                continue;
            }

            float distanceSq = (building.transform.position - center).sqrMagnitude;
            if (distanceSq > radiusSq)
            {
                continue;
            }

            FactionHQ? owner = building.NetworkHQ;
            if (owner == null)
            {
                continue;
            }

            if (owner == winner)
            {
                // Already flipped by the base game (Airbase.buildings); only its depot list is left.
                if (building is VehicleDepot)
                {
                    Taken.Add(building);
                }

                continue;
            }

            if (OwnerHasNearerBase(owner, airbase, building.transform.position, distanceSq))
            {
                continue;
            }

            Losers.Add(owner);
            building.NetworkHQ = winner;
            Taken.Add(building);
        }

        foreach (Building building in Taken)
        {
            if (building is VehicleDepot depot)
            {
                MoveDepot(depot, winner);
            }
        }

        if (Taken.Count > 0)
        {
            CommanderPlugin.Log.LogInfo(
                $"Capture: {CommanderCaptureService.GetAirbaseLabel(airbase)} handed {Taken.Count} building(s) to {winner.faction.name}.");
        }

        Taken.Clear();
        Losers.Clear();
    }

    internal static bool OwnerHasNearerBase(FactionHQ owner, Airbase captured, Vector3 position, float distanceSq)
    {
        foreach (Airbase other in owner.GetAirbases())
        {
            if (other != null && other != captured && other.center != null
                && (other.center.position - position).sqrMagnitude < distanceSq)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>Airbase.CaptureFaction(null)</c> returns before it re-owns the buildings, so a base
    /// knocked down to neutral would keep its hangars on the old side. A neutral base's buildings
    /// are unowned at mission start, so that is what they go back to.
    /// </summary>
    private static void ReleaseToNeutral(Airbase airbase)
    {
        foreach (Building building in airbase.buildings)
        {
            if (building != null)
            {
                building.NetworkHQ = null;
            }
        }
    }

    private static void MoveDepot(VehicleDepot depot, FactionHQ winner)
    {
        foreach (FactionHQ loser in Losers)
        {
            List<(VehicleDepot, float)> list = DepotsRef(loser);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Item1 == depot)
                {
                    list.RemoveAt(i);
                }
            }
        }

        List<(VehicleDepot, float)> own = DepotsRef(winner);
        for (int i = 0; i < own.Count; i++)
        {
            if (own[i].Item1 == depot)
            {
                return;
            }
        }

        if (!depot.disabled)
        {
            winner.AddDepot(depot);
        }
    }
}
