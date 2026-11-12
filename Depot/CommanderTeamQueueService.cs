using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// What teammates have queued at their depots. Queues live on the machine that clicked, so nobody
/// else could see them: each guest reports its own to the host, and the host hands the team's list
/// out with the sync (its own queues included, on a listen server). Display only.
/// </summary>
/// <remarks>
/// A report is one entry per queue, three args each: depot id, seconds left on the unit being built
/// (negative when its clock has not started) and the waiting units as <c>key:count</c> runs joined
/// with commas. The sync puts the owner's net id and name in front of each entry: the id tells
/// apart players the game names "Player". Entries drop off <see cref="ReportStaleSeconds"/> after
/// the last report.
/// </remarks>
internal sealed class CommanderTeamQueueService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float ReportIntervalSeconds = 3f;
    private const float ReportStaleSeconds = 10f;
    private const float LineRefreshSeconds = 1f;
    private const int EntryArgs = 3;
    private const string AiOwner = "AI Commander";

    /// <summary>A queue's worth of runs, and queues per player: a guest's report has to stay small.</summary>
    private const int MaxRunsPerQueue = 12;
    private const int MaxQueuesPerPlayer = 12;

    private readonly CommanderSpawnService spawnService;
    private readonly Dictionary<FactionHQ, Dictionary<uint, Report>> reports = new();
    private readonly List<Entry> entries = new();
    private readonly List<string> scratch = new();
    private float nextReportAt;
    private float nextLineRefreshAt;
    private bool reportedQueues;
    private bool loggedTeamQueues;

    internal CommanderTeamQueueService(CommanderSpawnService spawnService)
    {
        this.spawnService = spawnService;
        Instance = this;
        CommanderNetwork.OnHost("team.report", HostReport);
        CommanderNetwork.OnGuest("team.queues", ApplyTeamQueues);
        CommanderNetwork.OnSync(SyncTeamQueues);
    }

    internal static CommanderTeamQueueService? Instance { get; private set; }

    /// <summary>One line per teammate's queue, for the depot window.</summary>
    internal List<string> Lines { get; } = new();

    /// <summary>Teammates' queued units as pictures, for the depot window's QUEUE row.</summary>
    internal List<TeamRun> Runs { get; } = new();

    public void ResetSession()
    {
        reports.Clear();
        entries.Clear();
        Lines.Clear();
        Runs.Clear();
        reportedQueues = false;
        loggedTeamQueues = false;
    }

    public void TickPersistent()
    {
        if (CommanderScheduler.IsDueRealtime(ref nextLineRefreshAt, LineRefreshSeconds))
        {
            RefreshHostEntries();
            RefreshLines();
        }

        if (CommanderNetwork.HostHasMod && CommanderScheduler.IsDueRealtime(ref nextReportAt, ReportIntervalSeconds))
        {
            SendReport();
        }
    }

    private static Player? LocalPlayer()
    {
        return GameManager.GetLocalPlayer(out Player player) ? player : null;
    }

    private static string OwnerName(Player? player)
    {
        string name = player != null ? player.GetDisplayName(PlayerNameContext.Other) : string.Empty;
        return name.Length == 0 || name == "Player" ? "Teammate" : name;
    }

    private static void AddEntries(List<Entry> into, Report report, float elapsed)
    {
        string[] args = report.Args;
        for (int i = 0; i + EntryArgs <= args.Length; i += EntryArgs)
        {
            into.Add(new Entry(
                report.Owner,
                args[i],
                AgeSeconds(CommanderNetwork.Float(args, i + 1), elapsed),
                args[i + 2]));
        }
    }

    /// <summary>A clock read <paramref name="elapsed"/> seconds ago, as of now. One that has not
    /// started stays negative; one that ran out stays at zero, so the two do not read alike.</summary>
    private static float AgeSeconds(float left, float elapsed)
    {
        return left < 0f ? left : Mathf.Max(0f, left - elapsed);
    }

    private string DescribeRuns(Entry entry, VehicleDepot? depot)
    {
        StringBuilder text = new();
        string[] parts = entry.Runs.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string[] run = parts[i].Split(':');
            VehicleDefinition? definition = CommanderNetwork.Definition<VehicleDefinition>(CommanderNetwork.Arg(run, 0));
            if (definition == null)
            {
                continue;
            }

            int count = CommanderNetwork.Int(run, 1);
            if (depot != null)
            {
                float doneAt = text.Length == 0 && entry.BuildLeft >= 0f ? entry.ReadAt + entry.BuildLeft : -1f;
                Runs.Add(new TeamRun(depot, definition, count, entry.Owner, doneAt));
            }

            text.Append(text.Length > 0 ? ", " : string.Empty)
                .Append(CommanderGameAccess.GetVehicleLabel(definition))
                .Append(count > 1 ? $" x{count}" : string.Empty);
        }

        return text.ToString();
    }

    private void SendReport()
    {
        scratch.Clear();
        spawnService.CollectOwnQueues(scratch, MaxQueuesPerPlayer, MaxRunsPerQueue);
        if (scratch.Count == 0 && !reportedQueues)
        {
            return;
        }

        // The last, empty one is what clears this player from the list at once.
        reportedQueues = scratch.Count > 0;
        CommanderNetwork.Forward("team.report", scratch.ToArray());
    }

    private void HostReport(FactionHQ hq, string[] args)
    {
        Player? sender = CommanderNetwork.SenderPlayer;
        if (sender == null)
        {
            return;
        }

        if (!reports.TryGetValue(hq, out Dictionary<uint, Report> team))
        {
            team = new Dictionary<uint, Report>();
            reports[hq] = team;
        }

        // Only this faction's depots, at most what an honest guest sends: everyone on the team reads it.
        scratch.Clear();
        for (int i = 1; i + EntryArgs <= args.Length && scratch.Count < MaxQueuesPerPlayer * EntryArgs; i += EntryArgs)
        {
            if (CommanderNetwork.TryUnit(args, i, out Unit depot) && ReferenceEquals(depot.NetworkHQ, hq))
            {
                scratch.Add(args[i]);
                scratch.Add(args[i + 1]);
                scratch.Add(args[i + 2]);
            }
        }

        if (scratch.Count == 0)
        {
            team.Remove(sender.NetId);
            return;
        }

        team[sender.NetId] = new Report(OwnerName(sender), scratch.ToArray(), Time.time);
    }

    /// <summary>The host's own view: every report on its team, read straight from the table.</summary>
    private void RefreshHostEntries()
    {
        if (CommanderNetwork.IsGuest)
        {
            return;
        }

        entries.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq != null && reports.TryGetValue(hq, out Dictionary<uint, Report> team))
        {
            foreach (Report report in team.Values)
            {
                if (Time.time - report.At <= ReportStaleSeconds)
                {
                    AddEntries(entries, report, Time.time - report.At);
                }
            }
        }

        scratch.Clear();
        if (hq != null && CollectAiReserve(hq, scratch))
        {
            AddEntries(entries, new Report(AiOwner, scratch.ToArray(), Time.time), 0f);
        }
    }

    /// <summary>
    /// What an AI commander playing <paramref name="hq"/> has bought and not yet rolled out, as one
    /// queue entry at the depot it deploys from first. The AI buys into the faction reserve, not a
    /// depot queue, so without this a player on its team never saw what was coming.
    /// </summary>
    private bool CollectAiReserve(FactionHQ hq, List<string> args)
    {
        if (CommanderEnemyCommanderService.EffectiveMode == CommanderEnemyCommanderService.ModeOff
            || !CommanderUnitCap.IsAiSide(hq))
        {
            return false;
        }

        VehicleDepot? depot = null;
        List<(VehicleDepot, float)>? depots = CommanderCaptureSpoils.DepotsRef(hq);
        for (int i = 0; depots != null && i < depots.Count && depot == null; i++)
        {
            depot = depots[i].Item1 != null && !depots[i].Item1.disabled ? depots[i].Item1 : null;
        }

        if (depot == null)
        {
            return false;
        }

        // A player's own queued units take reserve units at the door: those are already in their QUEUE box.
        bool local = ReferenceEquals(hq, CommanderGameAccess.GetLocalHq());
        StringBuilder runs = new();
        int count = 0;
        foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
        {
            int waiting = entry.Key == null ? 0 : entry.Value.Count - (local ? spawnService.CountQueued(entry.Key, unpaidOnly: true) : 0);
            if (waiting <= 0 || count >= MaxRunsPerQueue)
            {
                continue;
            }

            runs.Append(count++ > 0 ? "," : string.Empty)
                .Append(entry.Key!.jsonKey)
                .Append(':')
                .Append(CommanderNetwork.Num(waiting));
        }

        if (count == 0)
        {
            return false;
        }

        args.Add(CommanderNetwork.Id(depot));
        args.Add(CommanderNetwork.Num(-1f));
        args.Add(runs.ToString());
        return true;
    }

    private void SyncTeamQueues(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "team.queues" };
        if (reports.TryGetValue(hq, out Dictionary<uint, Report> team))
        {
            foreach (KeyValuePair<uint, Report> report in team)
            {
                float elapsed = Time.time - report.Value.At;
                if (elapsed > ReportStaleSeconds)
                {
                    continue;
                }

                for (int i = 0; i + EntryArgs <= report.Value.Args.Length; i += EntryArgs)
                {
                    args.Add(report.Key.ToString(CultureInfo.InvariantCulture));
                    args.Add(report.Value.Owner);
                    args.Add(report.Value.Args[i]);
                    args.Add(CommanderNetwork.Num(AgeSeconds(CommanderNetwork.Float(report.Value.Args, i + 1), elapsed)));
                    args.Add(report.Value.Args[i + 2]);
                }
            }
        }

        if (ReferenceEquals(CommanderGameAccess.GetLocalHq(), hq))
        {
            scratch.Clear();
            spawnService.CollectOwnQueues(scratch, MaxQueuesPerPlayer, MaxRunsPerQueue);
            Player? local = LocalPlayer();
            string id = local != null ? local.NetId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            string owner = OwnerName(local);
            for (int i = 0; i < scratch.Count; i += EntryArgs)
            {
                args.Add(id);
                args.Add(owner);
                args.Add(scratch[i]);
                args.Add(scratch[i + 1]);
                args.Add(scratch[i + 2]);
            }
        }

        scratch.Clear();
        if (CollectAiReserve(hq, scratch))
        {
            args.Add("ai");
            args.Add(AiOwner);
            args.Add(scratch[0]);
            args.Add(scratch[1]);
            args.Add(scratch[2]);
        }

        messages.Add(args.ToArray());
    }

    /// <summary>The guest's side of the sync. Its own queues come back in the list and are skipped:
    /// it has them in the QUEUE box already.</summary>
    private void ApplyTeamQueues(string[] args)
    {
        entries.Clear();
        Player? local = LocalPlayer();
        string self = local != null ? local.NetId.ToString(CultureInfo.InvariantCulture) : string.Empty;
        for (int i = 1; i + EntryArgs + 1 < args.Length; i += EntryArgs + 2)
        {
            if (!string.Equals(args[i], self, StringComparison.Ordinal))
            {
                entries.Add(new Entry(args[i + 1], args[i + 2], CommanderNetwork.Float(args, i + 3), args[i + 4]));
            }
        }

        if (entries.Count > 0 && !loggedTeamQueues)
        {
            loggedTeamQueues = true;
            CommanderPlugin.Log.LogInfo("The first teammate's depot queue arrived from the host.");
        }

        RefreshLines();
    }

    private void RefreshLines()
    {
        Lines.Clear();
        Runs.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            bool known = CommanderNetwork.TryUnit(entry.DepotId, out Unit unit);
            string units = DescribeRuns(entry, known ? unit as VehicleDepot : null);
            if (units.Length == 0)
            {
                continue;
            }

            string depot = known ? CommanderGameAccess.GetUnitLabel(unit) : "depot";
            string clock = entry.BuildLeft >= 0f
                ? $"  ({CommanderBuildTimes.Format(entry.BuildLeft - (Time.time - entry.ReadAt))})"
                : string.Empty;
            Lines.Add($"{entry.Owner} at {depot}: {units}{clock}");
        }
    }

    internal sealed class TeamRun
    {
        internal TeamRun(VehicleDepot depot, VehicleDefinition definition, int count, string owner, float buildDoneAt)
        {
            Depot = depot;
            Definition = definition;
            Count = count;
            Owner = owner;
            BuildDoneAt = buildDoneAt;
        }

        internal VehicleDepot Depot { get; }

        internal VehicleDefinition Definition { get; }

        internal int Count { get; }

        internal string Owner { get; }

        /// <summary><c>Time.time</c> the unit being built is done, negative on any other picture.</summary>
        internal float BuildDoneAt { get; }
    }

    private sealed class Report
    {
        internal Report(string owner, string[] args, float at)
        {
            Owner = owner;
            Args = args;
            At = at;
        }

        internal string Owner { get; }

        internal string[] Args { get; }

        /// <summary><c>Time.time</c> it arrived.</summary>
        internal float At { get; }
    }

    private sealed class Entry
    {
        internal Entry(string owner, string depotId, float buildLeft, string runs)
        {
            Owner = owner;
            DepotId = depotId;
            BuildLeft = buildLeft;
            Runs = runs;
            ReadAt = Time.time;
        }

        internal string Owner { get; }

        internal string DepotId { get; }

        internal float BuildLeft { get; }

        internal string Runs { get; }

        internal float ReadAt { get; }
    }
}
