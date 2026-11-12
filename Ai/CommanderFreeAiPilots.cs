using System.Collections.Generic;
using System.Linq;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The lobby's "Active AI aircraft" on GC missions: AI players that live like human ones. Each one
/// joins once (the faction pays its join allowance, as for a new player), keeps a static call sign,
/// earns a pilot's share of the regular income, scores and ranks up on the base game's thresholds,
/// and is paid for kills and interceptions at <see cref="RewardMultiplier"/> times a player's rate.
/// An airframe is bought from its own funds; when it cannot afford one it takes a reserve airframe
/// out of faction stock, as the base game's reserve request does. Rank gates both, as for a player.
/// The commanders leave their aircraft alone (see <see cref="IsFree"/>).
/// </summary>
internal static class CommanderFreeAiPilots
{
    internal const string NameTag = "[AI Player]";

    /// <summary>XP and money from kills and interceptions, against a human player's.</summary>
    private const float RewardMultiplier = 1.5f;

    private const float PendingSeconds = 30f;

    // ponytail: a fixed respawn wait; a player's is however long they sit in the spawn menu.
    private const float RespawnSeconds = 20f;

    /// <summary><c>Player.rankThresholds</c>, scaled by the mission's rank multiplier as the base game does.</summary>
    private static readonly float[] RankThresholds = { 0f, 5f, 15f, 30f, 60f, 120f };

    private static readonly string[] CallSigns =
    {
        "Alfa", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike",
        "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
    };

    private static readonly List<AiPlayer> Players = new();
    private static readonly Dictionary<Aircraft, AiPlayer> Owners = new();
    private static readonly Dictionary<PersistentID, AiPlayer> Credit = new();
    private static readonly List<AircraftDefinition> Candidates = new();
    private static readonly Dictionary<FactionHQ, float> NextLogAt = new();

    internal static bool IsFree(Aircraft aircraft)
    {
        return Owners.ContainsKey(aircraft);
    }

    /// <summary>Stands in for <c>FactionHQ.DeployAIAircraft</c>: at most one join and one launch per call.</summary>
    internal static void Deploy(FactionHQ hq)
    {
        Players.RemoveAll(player => player.Hq == null);
        int limit = Mathf.Max(0, Mathf.FloorToInt(GetLimit(hq)));
        List<AiPlayer> side = Players.Where(player => player.Hq == hq).ToList();
        foreach (AiPlayer player in side)
        {
            Refresh(player);
        }

        if (side.Count < limit)
        {
            side.Add(Join(hq, side));
        }

        // A side over its limit (a human left) keeps its extra AI players benched, not gone: their
        // aircraft fly on, and they come back without paying to join again.
        AiPlayer? next = side.Take(limit).FirstOrDefault(player => player.Aircraft == null
            && player.PendingDefinition == null
            && Time.time >= player.NextSpawnAt);
        if (next != null && !TryLaunch(next) && (!NextLogAt.TryGetValue(hq, out float logAt) || Time.time >= logAt))
        {
            NextLogAt[hq] = Time.time + 30f;
            CommanderPlugin.Log.LogInfo(
                $"{next.Name} ({hq.faction?.name}) cannot launch: rank {next.Rank}, {next.Funds:0} funds, {Candidates.Count} airframe type(s) it may fly.");
        }
    }

    /// <summary>Says why the deploy tick was skipped before it got here, at most twice a minute per side.</summary>
    internal static void LogBlocked(FactionHQ hq)
    {
        if (!CommanderEnemyCommanderService.IsDuelMission || (NextLogAt.TryGetValue(hq, out float logAt) && Time.time < logAt))
        {
            return;
        }

        NextLogAt[hq] = Time.time + 30f;
        CommanderPlugin.Log.LogInfo(
            $"AI players ({hq.faction?.name}): blocked. Unit cap full {CommanderUnitCap.IsFull(hq, tellPlayer: false)}, aircraft cap full {CommanderUnitCap.IsAircraftFull(hq)}.");
    }

    /// <summary>Ties a spawned airframe to the AI player that launched it, from <c>RegisterFactionUnit</c>.
    /// Also called from <c>Unit.OnStartServer</c>, before the spawn message, so the name reaches
    /// guests: <c>unitName</c> is an initialOnly SyncVar.</summary>
    internal static void NotifyFactionUnitRegistered(FactionHQ? hq, Unit unit)
    {
        if (hq == null || unit is not Aircraft aircraft || aircraft.Player != null)
        {
            return;
        }

        if (!Owners.TryGetValue(aircraft, out AiPlayer? owner))
        {
            owner = Players.FirstOrDefault(player => player.Hq == hq
                && player.Aircraft == null
                && player.PendingDefinition == aircraft.definition);
            if (owner == null)
            {
                return;
            }

            owner.PendingDefinition = null;
            owner.Aircraft = aircraft;
            Owners[aircraft] = owner;
            aircraft.NetworkunitName = owner.Name;
        }

        // The second call, from RegisterFactionUnit, is the one with a persistent ID to credit kills to.
        if (aircraft.persistentID != PersistentID.None)
        {
            Credit[aircraft.persistentID] = owner;
        }
    }

    /// <summary>
    /// <c>Unit.ReportKilled</c> pays only units with a human <c>Player</c>. The same split for AI players:
    /// <c>ReportKillAction</c>'s score and allocation by share of the damage, after tax, times
    /// <see cref="RewardMultiplier"/>. A missile shot down is a kill like any other, so this pays interceptions too.
    /// </summary>
    internal static void NotifyKilled(Unit unit, Dictionary<PersistentID, float>? damageCredit)
    {
        if (damageCredit == null || Credit.Count == 0 || unit.definition == null)
        {
            return;
        }

        float total = damageCredit.Values.Sum();
        FactionHQ? victimHq = unit.NetworkHQ;
        if (total <= 0f || victimHq == null)
        {
            return;
        }

        float worth = Mathf.Sqrt(unit.definition.value + unit.GetAmmoValue().Current);
        foreach (KeyValuePair<PersistentID, float> entry in damageCredit)
        {
            float share = entry.Value / total;
            if (share < 0.01f || !Credit.TryGetValue(entry.Key, out AiPlayer player) || player.Hq == null || player.Hq == victimHq)
            {
                continue;
            }

            player.Funds += player.Hq.killReward * share * worth * (1f - player.Hq.playerTaxRate) * RewardMultiplier;
            AddScore(player, share * worth * RewardMultiplier);
        }
    }

    /// <summary>
    /// <c>FactionHQ.DistributeFunds</c> pays each human its share every 30 s; each AI player gets the
    /// same per-pilot share out of the same treasury.
    /// </summary>
    internal static void NotifyFundsDistributed(FactionHQ hq)
    {
        foreach (AiPlayer player in Players)
        {
            if (player.Hq != hq || hq.factionFunds <= 0f)
            {
                continue;
            }

            float excess = Mathf.Max((hq.factionFunds - hq.excessFundsThreshold) * Mathf.Clamp01(hq.excessFundsDistributePercent), 0f);
            float share = Mathf.Min((hq.regularIncome + excess) * 0.5f, hq.factionFunds);
            hq.AddFunds(-share);
            player.Funds += share;
        }
    }

    private static float GetLimit(FactionHQ hq)
    {
        int friendlyPlayers = hq.GetPlayers(sortByScore: false).Count;
        int enemyPlayers = 0;
        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            if (other != hq)
            {
                enemyPlayers += other.GetPlayers(sortByScore: false).Count;
            }
        }

        return hq.AIAircraftLimit + (enemyPlayers * hq.addAIPerEnemyPlayer) - (friendlyPlayers * hq.reduceAIPerFriendlyPlayer);
    }

    /// <summary>Drops a lost airframe, and gives back what a launch that never appeared was charged.</summary>
    private static void Refresh(AiPlayer player)
    {
        if (player.Aircraft is { } lost && (lost == null || lost.disabled))
        {
            // A destroyed Unity object compares equal to null but is still the dictionary key.
            Owners.Remove(lost!);
            player.Aircraft = null;
            player.NextSpawnAt = Time.time + RespawnSeconds;
        }

        if (player.PendingDefinition != null && Time.time > player.PendingUntil)
        {
            player.Funds += player.PendingCost;
            if (player.PendingFromStock)
            {
                player.Hq.ModifyUnitSupply(player.PendingDefinition, 1);
            }

            player.PendingDefinition = null;
        }
    }

    /// <summary>A new player's join: the faction pays its join allowance into the newcomer's account.</summary>
    private static AiPlayer Join(FactionHQ hq, List<AiPlayer> side)
    {
        string callSign = CallSigns.FirstOrDefault(sign => side.All(player => player.CallSign != sign)) ?? $"{side.Count + 1}";
        AiPlayer player = new(hq, callSign);
        float allowance = Mathf.Min(hq.playerJoinAllowance, Mathf.Max(hq.factionFunds, 0f));
        hq.AddFunds(-allowance);
        player.Funds = allowance;
        Players.Add(player);
        CommanderPlugin.Log.LogInfo($"{player.Name} joined {hq.faction?.name} with {allowance:0} from the treasury.");
        return player;
    }

    /// <summary>
    /// What a player in the spawn menu would do: the best airframe its rank allows that it can buy,
    /// or else a reserve airframe from stock. Random among the three most valuable, so a side's AI
    /// players do not all fly the same type.
    /// </summary>
    private static bool TryLaunch(AiPlayer player)
    {
        FactionHQ hq = player.Hq;
        Candidates.Clear();
        foreach (AircraftDefinition definition in Encyclopedia.i.aircraft)
        {
            if (definition.aircraftParameters.rankRequired <= player.Rank
                && CommanderAirCommandService.CanAiFly(definition)
                && (player.Funds >= definition.value || InStock(hq, definition))
                && FindAirbase(hq, definition) != null)
            {
                Candidates.Add(definition);
            }
        }

        if (Candidates.Count == 0)
        {
            return false;
        }

        List<AircraftDefinition> best = Candidates.OrderByDescending(definition => definition.value).Take(3).ToList();
        AircraftDefinition choice = best[Random.Range(0, best.Count)];
        Airbase airbase = FindAirbase(hq, choice)!;

        Loadout loadout = null!;
        float fuel = choice.aircraftParameters.DefaultFuelLevel;
        StandardLoadout? standard = choice.aircraftParameters.GetRandomStandardLoadout(choice, hq);
        if (standard != null)
        {
            loadout = standard.loadout;
            fuel = standard.FuelRatio;
        }

        bool bought = player.Funds >= choice.value;
        float munitions = Mathf.Min(CommanderAirCommandService.GetMunitionsCost(choice, loadout), Mathf.Max(player.Funds - (bought ? choice.value : 0f), 0f));
        float cost = (bought ? choice.value : 0f) + munitions;

        player.PendingDefinition = choice;
        player.PendingUntil = Time.time + PendingSeconds;
        LiveryKey livery = new(choice.aircraftParameters.GetRandomLiveryForFaction(hq.faction));
        if (!CommanderAirCommandService.LaunchAiAircraft(hq, airbase, choice, livery, loadout, fuel))
        {
            player.PendingDefinition = null;
            return false;
        }

        // LaunchAiAircraft leaves stock as it found it; a reserve airframe comes out of it here.
        player.PendingFromStock = !bought;
        if (!bought)
        {
            hq.ModifyUnitSupply(choice, -1);
        }

        player.PendingCost = cost;
        player.Funds -= cost;
        CommanderPlugin.Log.LogInfo(
            $"{player.Name} ({hq.faction?.name}, rank {player.Rank}) takes a {choice.unitName} from {airbase.name}, "
                + (bought ? $"bought for {cost:0}" : $"from reserve, {cost:0} on munitions") + $"; {player.Funds:0} left.");
        return true;
    }

    private static bool InStock(FactionHQ hq, AircraftDefinition definition)
    {
        return hq.AircraftSupply.TryGetValue(definition, out FactionHQ.RuntimeSupply supply) && supply.Count > 0;
    }

    private static Airbase? FindAirbase(FactionHQ hq, AircraftDefinition definition)
    {
        return hq.GetAirbases().FirstOrDefault(airbase => airbase != null
            && airbase.CanSpawnAircraft(definition)
            && CommanderAirCommandService.HasTakeoffRunway(airbase, definition, countOverruns: false));
    }

    /// <summary><c>Player.SetScore</c>: rank only ever goes up.</summary>
    private static void AddScore(AiPlayer player, float score)
    {
        player.Score += score;
        float rankScore = player.Score * MissionHelper.RankMultiplier;
        int rank = 0;
        for (int i = 0; i < RankThresholds.Length && RankThresholds[i] <= rankScore; i++)
        {
            rank = i;
        }

        if (rank > player.Rank)
        {
            player.Rank = rank;
            CommanderPlugin.Log.LogInfo($"{player.Name} ({player.Hq.faction?.name}) ranks up to {rank} at {player.Score:0.#} score.");
        }
    }

    private sealed class AiPlayer
    {
        internal AiPlayer(FactionHQ hq, string callSign)
        {
            Hq = hq;
            CallSign = callSign;
        }

        internal FactionHQ Hq { get; }

        internal string CallSign { get; }

        internal string Name => $"{NameTag} {CallSign}";

        internal float Score { get; set; }

        internal int Rank { get; set; }

        internal float Funds { get; set; }

        internal Aircraft? Aircraft { get; set; }

        internal float NextSpawnAt { get; set; }

        internal AircraftDefinition? PendingDefinition { get; set; }

        internal float PendingUntil { get; set; }

        internal float PendingCost { get; set; }

        internal bool PendingFromStock { get; set; }
    }
}
