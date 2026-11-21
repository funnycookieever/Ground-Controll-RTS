using System.Collections.Generic;
using NuclearOption.SavedMission.Outcomes;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Ends the match when a faction is left holding no airbase. Capturing the last base an enemy
/// owns is the win condition of an RTS round, and the mod needs one that does not depend on the
/// mission author having written a matching <c>CaptureAirbase</c> objective for every base on the
/// map — a base lost to a capture the mission never named would otherwise leave the round running
/// with a faction that can neither launch nor build.
/// </summary>
/// <remarks>
/// Core tier and a persistent tick: it must run whether or not the player has RTS mode open, and
/// on every mission, not just the mod's own. Host only — <c>DeclareEndGame</c> is a server RPC,
/// and it is the host that owns the result.
/// <para>
/// A faction is only judged once it has held a base, so the seconds before the mission finishes
/// registering airbases cannot declare everybody the loser at t=0.
/// </para>
/// </remarks>
internal sealed class CommanderVictoryService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float CheckIntervalSeconds = 5f;

    /// <summary>After the draft's battle starts, how long before a side can be judged out of units:
    /// the reserve is in stock, not on the field, for the first moments.</summary>
    private const float DraftGraceSeconds = 30f;

    /// <summary>Consecutive checks a side must be out of fighting units before it loses, so a unit
    /// between the depot door and its registration is not the end of the match.</summary>
    private const int DraftExhaustedChecks = 2;

    /// <summary>Whether a unit definition can fight or take ground, read off the prefab once.</summary>
    private static readonly Dictionary<UnitDefinition, bool> FightingDefinitions = new();

    /// <summary>
    /// On the duel, the base each faction started on. Losing it loses the match, however many
    /// neutral bases it has taken since, so nobody has to hunt down every last expansion.
    /// </summary>
    private static readonly Dictionary<FactionHQ, Airbase> HomeBases = new();

    private readonly HashSet<FactionHQ> everHeldBase = new();

    /// <summary>Draft: consecutive checks each side has been found with nothing left to fight with.</summary>
    private readonly Dictionary<FactionHQ, int> exhaustedChecks = new();

    private float draftLiveAt = -1f;

    private float nextCheckAt;
    private bool declared;

    public void ResetSession()
    {
        everHeldBase.Clear();
        HomeBases.Clear();
        exhaustedChecks.Clear();
        draftLiveAt = -1f;
        declared = false;
        nextCheckAt = 0f;
    }

    public void TickPersistent()
    {
        if (declared
            || GameManager.gameResolution != GameResolution.Ongoing
            || !CommanderScheduler.IsDue(ref nextCheckAt, CheckIntervalSeconds))
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            // The Leviathan's army holds bases only while it is winning; losing them all is no defeat.
            if (hq == null || !hq.IsServer || hq.faction == null || CommanderLeviathanDeepService.IsDeep(hq))
            {
                continue;
            }

            // The Leviathan is co-op (user, 2026-11-06): the Deep wins only by taking every base and
            // carrier the players hold, never because they are out of units for a moment.
            if (!CommanderLeviathanService.IsLeviathanMission && RanOutOfUnits(hq))
            {
                declared = true;
                hq.DeclareEndGame(EndType.Defeat);
                CommanderPlugin.Log.LogInfo(
                    $"{hq.faction.factionName} has no fighting units left on the field or in reserve - declaring defeat.");
                return;
            }

            if (LostHomeBase(hq))
            {
                declared = true;
                hq.DeclareEndGame(EndType.Defeat);
                CommanderPlugin.Log.LogInfo(
                    $"{hq.faction.factionName} has lost its starting base — declaring defeat.");
                return;
            }

            if (CountBases(hq) > 0)
            {
                everHeldBase.Add(hq);
                continue;
            }

            if (!everHeldBase.Contains(hq))
            {
                continue;
            }

            // One call settles both sides: RpcDeclareEndGame gives this faction the defeat and
            // everybody else the mirror result, so there is no second declaration to make.
            declared = true;
            hq.DeclareEndGame(EndType.Defeat);
            CommanderPlugin.Log.LogInfo(
                $"{hq.faction.factionName} has lost its last base — declaring defeat.");
            return;
        }
    }

    /// <summary>The base a faction loses the duel with, once the first check has seen it.</summary>
    internal static bool TryGetHomeBase(FactionHQ hq, out Airbase home)
    {
        return HomeBases.TryGetValue(hq, out home) && home != null;
    }

    internal static int CountBases(FactionHQ hq)
    {
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled)
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasFightingUnits(FactionHQ hq)
    {
        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && unit != null
                    && !unit.disabled
                    && (unit is GroundVehicle || unit is Aircraft || unit is Ship)
                    && ((unit.weaponStations != null && unit.weaponStations.Count > 0)
                        || (unit.definition != null && unit.definition.captureStrength > 0f)))
                {
                    return true;
                }
            }
        }

        foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
        {
            if (entry.Value.Count > 0 && IsFighting(entry.Key))
            {
                return true;
            }
        }

        foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> entry in hq.AircraftSupply)
        {
            if (entry.Value.Count > 0 && IsFighting(entry.Key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFighting(UnitDefinition? definition)
    {
        if (definition == null || definition.unitPrefab == null)
        {
            return false;
        }

        if (!FightingDefinitions.TryGetValue(definition, out bool fighting))
        {
            fighting = definition.captureStrength > 0f
                || definition.unitPrefab.GetComponentInChildren<Weapon>(true) != null
                || (definition is AircraftDefinition
                    && definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets?.Length > 0);
            FightingDefinitions[definition] = fighting;
        }

        return fighting;
    }

    /// <summary>
    /// Duel only (not Champions, where the enemy holding the rest is the point): the first base a
    /// faction is seen holding is its home, and it has lost the match once anyone else holds it.
    /// </summary>
    private static bool LostHomeBase(FactionHQ hq)
    {
        // Not a draft either: there the match is fought until one army is gone.
        // The listed duels only: a base-game mission played with AI COMMANDERS keeps its own
        // way of being won, and losing the first base seen is no defeat there.
        // A map maker can leave the match to the map's own objectives in the editor.
        if (!CommanderEnemyCommanderService.IsListedDuelMission
            || CommanderEnemyCommanderService.IsChampionsMission
            || CommanderMapSettings.ObjectivesWinOnly
            || CommanderDraftService.IsDraftMission)
        {
            return false;
        }

        if (HomeBases.TryGetValue(hq, out Airbase home))
        {
            return home == null || home.disabled || home.CurrentHQ != hq;
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled)
            {
                HomeBases[hq] = airbase;
                CommanderPlugin.Log.LogInfo(
                    $"{hq.faction.factionName} starts on {airbase.name} — losing it loses the duel.");
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Ground Control Draft's win condition: nothing is ever produced, so a side with no fighting
    /// unit on the field and none left in its drafted reserve has lost. "Fighting" is anything with
    /// a weapon or capture strength; a lone repair truck cannot win the match and must not hold it
    /// open. Aircraft recovered into stock and airframes a rescued pilot brought back count, because
    /// they are in the reserve.
    /// </summary>
    private bool RanOutOfUnits(FactionHQ hq)
    {
        if (!CommanderDraftService.IsLive)
        {
            return false;
        }

        if (draftLiveAt < 0f)
        {
            draftLiveAt = Time.timeSinceLevelLoad;
        }

        if (Time.timeSinceLevelLoad - draftLiveAt < DraftGraceSeconds || HasFightingUnits(hq))
        {
            exhaustedChecks.Remove(hq);
            return false;
        }

        int checks = (exhaustedChecks.TryGetValue(hq, out int seen) ? seen : 0) + 1;
        exhaustedChecks[hq] = checks;
        return checks >= DraftExhaustedChecks;
    }
}
