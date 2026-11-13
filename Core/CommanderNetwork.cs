using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The link between two copies of this mod in one multiplayer match, which is what lets a guest
/// command. Nearly everything a commander does is server-side in the base game: faction money is a
/// SyncVar, every spawn goes through the server object manager, and the AI pilots that fly Air
/// Command missions only run on the host. So a guest's click is sent here as a request, and the
/// host carries it out through the very same service code it runs for its own clicks, with
/// <see cref="ActingHq"/> standing in for "my faction". Refusals come back as the guest's red
/// banner, and the few host-side readings a guest's windows need (building levels, depot fuel, the
/// repair list, air missions) are sent back every couple of seconds.
/// </summary>
/// <remarks>
/// <para>
/// The host speaks first. Mirage charges a player error points for every message the server has
/// no handler for, and enough of them disconnects that player, so a guest that sent anything to a
/// host without this mod could get itself kicked. A client keeps no such score, so the host
/// announcing itself to a guest that lacks the mod costs one line in that guest's log. Hence the
/// host says hello every few seconds, and a guest sends nothing until it has heard one carrying
/// its own version. The guest answers "hi", and only guests that did get the sync traffic.
/// </para>
/// <para>
///  the message is plain text split on one separator, not a typed struct per request,
/// because a typed message needs a Mirage weaver pass this project does not have. The arguments
/// are ids, jsonKeys, numbers and positions, a few dozen bytes each.
/// </para>
/// </remarks>
internal sealed class CommanderNetwork : ICommanderTickPersistent
{
    private const char Separator = '\u001f';
    private const float HelloIntervalSeconds = 5f;
    private const float SyncIntervalSeconds = 2f;
    private const float LocalSideSwapSeconds = 30f;

    /// <summary>
    /// How long a guest waits without hearing anything from the host before it logs a warning. The
    /// host sends <c>hello</c> every 5 s and sync every 2 s, so past this the traffic has stopped.
    /// Tells apart a transport drop from the host's game going quiet.
    /// </summary>
    private const float HostQuietWarnSeconds = 8f;

    /// <summary>
    /// Most a single <see cref="CommanderNetMessage"/> may carry, in UTF-8 bytes, with room to
    /// spare under Mirage's own ceiling.
    /// </summary>
    /// <remarks>
    /// <c>NetworkWriter.WriteString</c> encodes into a shared <c>byte[NetworkWriter.MaxStringLength]</c>
    /// and that limit is <b>1300</b>: over it, <c>Encoding.GetBytes</c> throws before Mirage's own
    /// length check even runs. This message is one string and several of its payloads have no bound
    /// at all - <c>levels</c> carries a line per building the faction has built, <c>blueprints</c> a
    /// line per construction site with a full position on it, <c>air.m</c> a line per mission - so a
    /// long enough match walks into that ceiling and every sync from then on throws inside the host's
    /// tick. Raising <c>MaxStringLength</c> was the other option and is rejected on purpose: it is a
    /// static on the engine's own writer, shared with every string the game sends, and resizing that
    /// buffer under the game is not the mod's business. Pieces are put back together in
    /// <see cref="TryReassemble"/> before any handler sees them, so no verb knows this happens.
    /// </remarks>
    private const int MaxMessageBytes = 1100;

    /// <summary>Wrapper verb for one piece of a split payload. Never handled by a service.</summary>
    private const string PieceVerb = "part";

    private const float PieceTimeoutSeconds = 30f;

    /// <summary>
    /// About a megabyte at <see cref="MaxMessageBytes"/>, far past any real sync. The count comes off
    /// the wire before the sender is known to be on a faction, and it sizes an array.
    /// </summary>
    private const int MaxPiecesPerPayload = 1024;

    /// <summary>Unfinished payloads held per sender. One at a time is normal; a few cover a late sync.</summary>
    private const int MaxOpenPayloadsPerSender = 8;

    private static readonly string SeparatorText = Separator.ToString();

    private static readonly Dictionary<string, Action<FactionHQ, string[]>> HostHandlers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Action<string[]>> GuestHandlers = new(StringComparer.Ordinal);
    private static readonly List<Action<FactionHQ, List<string[]>>> SyncSources = new();
    private static readonly List<string[]> SyncScratch = new();
    private static readonly HashSet<INetworkPlayer> ModdedGuests = new();
    private static readonly List<INetworkPlayer> LeftGuests = new();
    private static readonly Dictionary<string, string?[]> PieceBuffers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> PieceStartedAt = new(StringComparer.Ordinal);
    private static readonly List<string> PendingDestinations = new();
    private static readonly List<string> PieceScratch = new();
    private static readonly List<string> StalePieceScratch = new();
    private static readonly HashSet<string> FailedSendVerbs = new(StringComparer.Ordinal);
    private static readonly string[] SelfCheckArgs = { "0.0.0.0" };

    private static bool pieceRefusalLogged;
    private static bool registered;
    private static string hostVersion = string.Empty;
    private static string? wireVersion;
    private static string lastLoggedRequest = string.Empty;
    private static float lastLoggedRequestAt;
    private static float lastHostMessageAt;
    private static bool hostQuietLogged;
    private static int nextPieceId;

    private MessageHandler? boundServer;
    private MessageHandler? boundClient;
    private float nextHelloAt;
    private float nextSyncAt;

    /// <summary>
    /// The guest's faction while the host is carrying out one of that guest's requests, null the
    /// rest of the time. <see cref="CommanderGameAccess.GetLocalHq"/> returns it, which is how every
    /// service's own checks and spending apply to the guest's faction without a second code path.
    /// </summary>
    internal static FactionHQ? ActingHq { get; private set; }

    /// <summary>True on the host while it is carrying out a guest's request.</summary>
    internal static bool IsHandlingGuest => Sender != null;

    /// <summary>A client in someone else's match: its commander clicks have to go to the host.</summary>
    internal static bool IsGuest
    {
        get
        {
            NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
            return manager != null
                && manager.Client != null
                && manager.Client.Active
                && (manager.Server == null || !manager.Server.Active);
        }
    }

    /// <summary>The host has said hello with this exact version, so it will understand a request.</summary>
    /// <summary>
    /// The player whose request the host is carrying out, or null outside one. A destination
    /// issued for a guest is filed under this player, the same as the game's own RPC would.
    /// </summary>
    internal static Player? SenderPlayer =>
        Sender?.Identity != null && Sender.Identity.TryGetComponent(out Player player) ? player : null;

    internal static bool HostHasMod => IsGuest && hostVersion == WireVersion;

    /// <summary>
    /// What the two ends compare: the version plus a hash of every request and reply this build
    /// understands. A rebuild that adds a verb without a version bump would otherwise pass the
    /// handshake and then wait for ever on a host that ignores it.
    /// </summary>
    internal static string WireVersion => wireVersion ??= BuildWireVersion();

    /// <summary>The host has unlocked every feature. A guest cannot do it alone: the host's
    /// advanced services are what carry out its orders.</summary>
    internal static bool HostFeaturesUnlocked { get; set; }

    /// <summary>The connection whose request the host is carrying out, or null outside one.</summary>
    internal static INetworkPlayer? Sender { get; private set; }

    /// <summary>
    /// The host has someone else in its match. Game speed is then held at 1x: the game stamps
    /// position updates with the host's scaled clock, and a guest's clock runs in real time and
    /// catches up at 8% at most, so a sped-up host leaves every unit and weapon lagging and
    /// snapping on the guest's screen.
    /// </summary>
    internal static bool HostHasGuests
    {
        get
        {
            NetworkServer? server = NetworkManagerNuclearOption.i?.Server;
            if (server == null || !server.Active)
            {
                return false;
            }

            IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
            for (int i = 0; i < players.Count; i++)
            {
                if (!players[i].IsHost)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The host's game speed, for a guest's speed buttons: <c>Time.timeScale</c> is not networked.</summary>
    internal static float HostTimeScale { get; private set; } = 1f;

    internal static string HostMissingReason => hostVersion.Length == 0
        ? "Only the host can do that: the host is not running Ground Control."
        : $"Only the host can do that: the host runs a different build of Ground Control ({hostVersion}, you run {WireVersion}).";

    public void TickPersistent()
    {
        FlushDestinations();
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (!registered || manager == null)
        {
            return;
        }

        NetworkServer? server = manager.Server;
        if (server != null && server.Active)
        {
            if (!ReferenceEquals(boundServer, server.MessageHandler))
            {
                boundServer = server.MessageHandler;
                boundServer.RegisterHandler<CommanderNetMessage>(OnHostMessage, allowUnauthenticated: false);
            }

            // Someone joined a sped-up match: back to 1x. Pause (0) is the game's own and synced.
            if (Time.timeScale > 1f && HostHasGuests)
            {
                TimeScaleManager.Scale = 1f;
            }

            if (CommanderScheduler.IsDueRealtime(ref nextHelloAt, HelloIntervalSeconds))
            {
                SendHello(server);
                NoteLeftGuests(server);
            }

            if (CommanderScheduler.IsDueRealtime(ref nextSyncAt, SyncIntervalSeconds))
            {
                SendSync(server);
            }

            return;
        }

        ModdedGuests.Clear();
        NetworkClient? client = manager.Client;
        if (client == null || !client.Active)
        {
            // Out of the match: whoever hosts the next one has to say hello again.
            hostVersion = string.Empty;
            HostFeaturesUnlocked = false;
            lastHostMessageAt = 0f;
            hostQuietLogged = false;
            return;
        }

        if (!ReferenceEquals(boundClient, client.MessageHandler))
        {
            boundClient = client.MessageHandler;
            boundClient.RegisterHandler<CommanderNetMessage>(OnGuestMessage, allowUnauthenticated: false);
        }

        WatchHostTraffic();
    }

    /// <summary>Teaches Mirage the message. Once, at plugin load.</summary>
    internal static void Initialize()
    {
        Writer<CommanderNetMessage>.Write = static (writer, message) => writer.WriteString(message.Text);
        Reader<CommanderNetMessage>.Read = static reader => new CommanderNetMessage { Text = reader.ReadString() ?? string.Empty };
        int id = MessagePacker.GetId<CommanderNetMessage>();
        if (MessagePacker.MessageTypes.TryGetValue(id, out Type existing) && existing != typeof(CommanderNetMessage))
        {
            CommanderPlugin.Log.LogError(
                $"Multiplayer commanding is off: message id {id} already belongs to {existing.FullName}.");
            return;
        }

        MessagePacker.RegisterMessage<CommanderNetMessage>();
        OnGuest("notice", args => CommanderNotice.Fail(Arg(args, 1)));
        OnGuest("speed", args => HostTimeScale = Mathf.Max(0f, Float(args, 1)));
        OnGuest("cap", args => CommanderUnitCap.HostMode = Int(args, 1));
        OnGuest("aic", args => CommanderEnemyCommanderService.HostAiCommanders = Int(args, 1));
        OnGuest("adv", args => HostFeaturesUnlocked = Bool(args, 1));
        CommanderCallSigns.Initialize();
        registered = true;
    }

    /// <summary>
    /// Runs host-side work that belongs to a guest's faction but happens later than the request (a
    /// queued cargo run spawning, say): every check that reads <see cref="ActingHq"/> then sees the
    /// guest's faction. Nothing is replied, because nobody is waiting for an answer.
    /// </summary>
    internal static void RunAs(FactionHQ hq, Action action)
    {
        FactionHQ? previous = ActingHq;
        ActingHq = hq;
        try
        {
            action();
        }
        finally
        {
            ActingHq = previous;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> as the host's own team. On a dedicated server there is no such
    /// team (no local player, so <see cref="CommanderGameAccess.GetLocalHq"/> is null) and every rule
    /// written as "the host's team against the AI" had nobody to play for: no Champions deal, no enemy
    /// commander, no opening shelves. The first team with a player in it stands in. Anywhere else,
    /// and on a dedicated server before anyone has joined, it just runs.
    /// </summary>
    internal static void RunAsHostTeam(Action action)
    {
        FactionHQ? standIn = GetDedicatedStandIn();
        if (standIn == null)
        {
            action();
            return;
        }

        RunAs(standIn, action);
    }

    /// <summary>Host: this guest runs the mod and can be sent requests and syncs.</summary>
    internal static bool IsModdedGuest(INetworkPlayer player)
    {
        return ModdedGuests.Contains(player);
    }

    /// <summary>What the host does with a guest's request. Runs with <see cref="ActingHq"/> set.</summary>
    internal static void OnHost(string verb, Action<FactionHQ, string[]> handler)
    {
        HostHandlers[verb] = handler;
    }

    /// <summary>What a guest does with something the host sent it.</summary>
    internal static void OnGuest(string verb, Action<string[]> handler)
    {
        GuestHandlers[verb] = handler;
    }

    /// <summary>A reading the host sends each modded guest every couple of seconds, for that guest's faction.</summary>
    internal static void OnSync(Action<FactionHQ, List<string[]>> source)
    {
        SyncSources.Add(source);
    }

    /// <summary>
    /// Asserts that an over-long payload survives the round trip through <see cref="SendPieces"/>
    /// and <see cref="TryReassemble"/>, that no piece breaks Mirage's string ceiling, and that a
    /// short one is left alone. Run at plugin load, because the only other place this is exercised
    /// is a multiplayer match late enough for a sync payload to have grown past the limit, and the
    /// failure there is silent.
    /// </summary>
    internal static void SelfCheck()
    {
        StringBuilder builder = new();
        for (int i = 0; i < 400; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(" f 3").Append(Separator);
        }

        // A non-ASCII name is the case a byte-slice would cut in half.
        string payload = Join("levels", new[] { builder.ToString(), "Ödemark Flygbas", "🛩 wing" });
        PieceScratch.Clear();
        List<string> wire = new();
        SendPieces(payload, wire.Add);
        if (wire.Count < 2)
        {
            CommanderPlugin.Log.LogError("Network self-check FAILED: an over-long payload was not split.");
            return;
        }

        for (int i = 0; i < wire.Count; i++)
        {
            if (Encoding.UTF8.GetByteCount(wire[i]) > MaxMessageBytes)
            {
                CommanderPlugin.Log.LogError(
                    $"Network self-check FAILED: piece {i} is {Encoding.UTF8.GetByteCount(wire[i])} bytes, over the {MaxMessageBytes} limit.");
                return;
            }
        }

        string? rebuilt = null;
        for (int i = 0; i < wire.Count; i++)
        {
            rebuilt = TryReassemble("selfcheck", wire[i]);
            if (rebuilt != null && i < wire.Count - 1)
            {
                CommanderPlugin.Log.LogError($"Network self-check FAILED: payload completed early, at piece {i + 1} of {wire.Count}.");
                return;
            }
        }

        if (rebuilt != payload)
        {
            CommanderPlugin.Log.LogError("Network self-check FAILED: the reassembled payload does not match what was sent.");
            return;
        }

        string plain = Join("hello", SelfCheckArgs);
        if (TryReassemble("selfcheck", plain) != plain)
        {
            CommanderPlugin.Log.LogError("Network self-check FAILED: a short payload was not passed through untouched.");
            return;
        }

        if (TryReassemble("selfcheck", Join(PieceVerb, new[] { "huge", "0", int.MaxValue.ToString(CultureInfo.InvariantCulture), "x" })) != null
            || PieceBuffers.Count != 0)
        {
            CommanderPlugin.Log.LogError("Network self-check FAILED: a split message claiming too many pieces was buffered.");
            return;
        }

        for (int i = 0; i <= MaxOpenPayloadsPerSender; i++)
        {
            TryReassemble("selfcheck", Join(PieceVerb, new[] { i.ToString(CultureInfo.InvariantCulture), "0", "2", "x" }));
        }

        int held = PieceBuffers.Count;
        PieceBuffers.Clear();
        PieceStartedAt.Clear();
        if (held != MaxOpenPayloadsPerSender)
        {
            CommanderPlugin.Log.LogError($"Network self-check FAILED: {held} unfinished payloads held from one sender.");
            return;
        }

        pieceRefusalLogged = false;
        CommanderPlugin.Log.LogInfo($"Multiplayer self-check passed: a {Encoding.UTF8.GetByteCount(payload)} byte payload round-trips in {wire.Count} pieces.");
    }

    /// <summary>
    /// The one gate every guest-side action goes through. On a guest it sends the request to the
    /// host (or refuses out loud, when the host cannot take it) and returns true, so the caller
    /// stops there. On the host and in singleplayer it returns false and the caller does the work
    /// itself, exactly as before.
    /// </summary>
    internal static bool Forward(string verb, params string[] args)
    {
        if (!IsGuest)
        {
            return false;
        }

        if (!HostHasMod)
        {
            CommanderNotice.Fail(HostMissingReason);
            return true;
        }

        SendToHost(Join(verb, args));
        return true;
    }

    /// <summary>
    /// A guest's move order, batched to the host in one message with every other order this frame.
    /// <c>UnitCommand.CmdSetDestination</c> is rate limited to 5 a second per player (burst 20), which
    /// drops orders for large armies; the host applies these locally with no limit. False when this
    /// seat must send the RPC itself: not a guest, or a host without the mod.
    /// </summary>
    internal static bool TryQueueDestination(Unit unit, GlobalPosition destination, bool playerCommand)
    {
        if (!HostHasMod)
        {
            return false;
        }

        PendingDestinations.Add(Id(unit));
        PendingDestinations.Add(Position(destination));
        PendingDestinations.Add(Flag(playerCommand));
        return true;
    }

    /// <summary>
    /// Host only: sends to every modded guest on <paramref name="hq"/>'s team at once, rather than
    /// waiting up to two seconds for the next sync. For news a teammate should see the moment it
    /// happens, such as a ping.
    /// </summary>
    internal static void SendToFaction(FactionHQ hq, string verb, params string[] args)
    {
        NetworkServer? server = NetworkManagerNuclearOption.i?.Server;
        if (server == null || !server.Active)
        {
            return;
        }

        string text = Join(verb, args);
        IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
        for (int i = 0; i < players.Count; i++)
        {
            INetworkPlayer player = players[i];
            if (!player.IsHost && ModdedGuests.Contains(player) && ReferenceEquals(ResolveHq(player), hq))
            {
                SendTo(player, text);
            }
        }
    }

    /// <summary>Sends a sync message to every modded guest, including observers on other factions.</summary>
    internal static void SendToGuests(string[] message)
    {
        NetworkServer? server = NetworkManagerNuclearOption.i?.Server;
        if (server == null || !server.Active)
        {
            return;
        }

        string text = string.Join(SeparatorText, message);
        foreach (INetworkPlayer player in server.AuthenticatedPlayers)
        {
            if (!player.IsHost && ModdedGuests.Contains(player))
            {
                SendTo(player, text);
            }
        }
    }

    /// <summary>Answers the guest whose request the host is carrying out. Does nothing otherwise.</summary>
    internal static void Reply(string verb, params string[] args)
    {
        if (Sender != null)
        {
            SendTo(Sender, Join(verb, args));
        }
    }

    // Argument helpers. Numbers are invariant-culture text so a guest on a comma-decimal Windows
    // locale and a host on a dot-decimal one read the same position.
    internal static string Arg(string[] args, int index)
    {
        return index < args.Length ? args[index] : string.Empty;
    }

    internal static float Float(string[] args, int index)
    {
        return float.TryParse(Arg(args, index), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            && !float.IsNaN(value)
            && !float.IsInfinity(value)
                ? value
                : 0f;
    }

    internal static int Int(string[] args, int index)
    {
        return int.TryParse(Arg(args, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    internal static bool Bool(string[] args, int index)
    {
        return Arg(args, index) == "1";
    }

    internal static string Num(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static string Num(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    internal static string Flag(bool value)
    {
        return value ? "1" : "0";
    }

    internal static string Id(Unit? unit)
    {
        return unit == null ? "0" : unit.persistentID.Id.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool TryUnit(string id, out Unit unit)
    {
        unit = null!;
        return uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            && value != 0
            && new PersistentID { Id = value }.TryGetUnit(out unit)
            && unit != null;
    }

    internal static bool TryUnit(string[] args, int index, out Unit unit)
    {
        return TryUnit(Arg(args, index), out unit);
    }

    internal static string Position(GlobalPosition position)
    {
        return $"{Num(position.x)} {Num(position.y)} {Num(position.z)}";
    }

    internal static bool TryPosition(string text, out GlobalPosition position)
    {
        position = default;
        string[] parts = text.Split(' ');
        if (parts.Length != 3)
        {
            return false;
        }

        float x = Float(parts, 0);
        float y = Float(parts, 1);
        float z = Float(parts, 2);
        position = new GlobalPosition(x, y, z);
        return true;
    }

    internal static bool TryPosition(string[] args, int index, out GlobalPosition position)
    {
        return TryPosition(Arg(args, index), out position);
    }

    /// <summary>The name the game files an airbase under, the same on every machine.</summary>
    internal static string Key(Airbase? airbase)
    {
        if (airbase != null && FactionRegistry.airbaseLookup != null)
        {
            foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
            {
                if (ReferenceEquals(entry.Value, airbase))
                {
                    return entry.Key;
                }
            }
        }

        return string.Empty;
    }

    internal static bool TryAirbase(string[] args, int index, out Airbase airbase)
    {
        airbase = null!;
        string key = Arg(args, index);
        return key.Length > 0
            && FactionRegistry.airbaseLookup != null
            && FactionRegistry.airbaseLookup.TryGetValue(key, out airbase)
            && airbase != null;
    }

    /// <summary>A unit definition by jsonKey, the id both machines agree on.</summary>
    internal static T? Definition<T>(string key)
        where T : UnitDefinition
    {
        if (key.Length == 0)
        {
            return null;
        }

        if (Encyclopedia.Lookup != null && Encyclopedia.Lookup.TryGetValue(key, out UnitDefinition found) && found is T typed)
        {
            return typed;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        return encyclopedia == null
            ? null
            : Find<T>(encyclopedia.aircraft, key)
                ?? Find<T>(encyclopedia.vehicles, key)
                ?? Find<T>(encyclopedia.ships, key)
                ?? Find<T>(encyclopedia.buildings, key);
    }

    private static void SendHello(NetworkServer server)
    {
        string hello = Join("hello", new[] { WireVersion });
        string advanced = Join("adv", new[] { Flag(CommanderFeatureGate.AdvancedFeaturesEnabled) });
        IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].IsHost)
            {
                SendTo(players[i], hello);
                SendTo(players[i], advanced);
            }
        }
    }

    private static void SendSync(NetworkServer server)
    {
        IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
        for (int i = 0; i < players.Count; i++)
        {
            INetworkPlayer player = players[i];
            FactionHQ? hq = player.IsHost || !ModdedGuests.Contains(player) ? null : ResolveHq(player);
            if (hq == null)
            {
                continue;
            }

            SyncScratch.Clear();
            SyncScratch.Add(new[] { "speed", Num(Time.timeScale) });
            SyncScratch.Add(new[] { "cap", Num(CommanderSettings.UnitCapEnabled ? CommanderSettings.UnitCapMode : -1) });
            SyncScratch.Add(new[] { "aic", Num(CommanderSettings.AiCommanders) });
            for (int source = 0; source < SyncSources.Count; source++)
            {
                SyncSources[source](hq, SyncScratch);
            }

            for (int message = 0; message < SyncScratch.Count; message++)
            {
                SendTo(player, string.Join(SeparatorText, SyncScratch[message]));
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A guest's request runs base-game code that can throw anything; one bad message must not kill the host's network handler.")]
    private static void OnHostMessage(INetworkPlayer sender, CommanderNetMessage message)
    {
        string? payload = TryReassemble(sender.ToString() ?? "guest", message.Text ?? string.Empty);
        if (payload == null)
        {
            return;
        }

        string[] args = payload.Split(Separator);
        if (args[0] == "hi")
        {
            if (ModdedGuests.Add(sender))
            {
                CommanderPlugin.Log.LogInfo($"{sender} runs Ground Control {WireVersion} and can command from their seat.");
            }

            return;
        }

        FactionHQ? hq = ResolveHq(sender);
        if (hq == null || !HostHandlers.TryGetValue(args[0], out Action<FactionHQ, string[]> handler))
        {
            CommanderPlugin.Log.LogWarning(
                $"Ignored guest request '{args[0]}' from {sender}: "
                + (hq == null ? "the sender is on no faction." : "unknown request."));
            return;
        }

        // A refused depot request is asked again every few seconds; one line per change is enough.
        // Move orders arrive many times a second in a battle and say nothing a log reader needs, and
        // a queue report is a clock that changes every time.
        string request = args[0] is "dest" or "team.report" ? string.Empty : string.Join(" ", args);
        if (request.Length > 0
            && (request != lastLoggedRequest || Time.realtimeSinceStartup - lastLoggedRequestAt > 60f))
        {
            lastLoggedRequest = request;
            lastLoggedRequestAt = Time.realtimeSinceStartup;
            CommanderPlugin.Log.LogInfo($"Guest request ({hq.faction?.name}): {request}");
        }

        ActingHq = hq;
        Sender = sender;
        try
        {
            handler(hq, args);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Guest request '{args[0]}' failed: {exception}");
        }
        finally
        {
            ActingHq = null;
            Sender = null;
        }
    }

    private static void FlushDestinations()
    {
        if (PendingDestinations.Count == 0)
        {
            return;
        }

        if (HostHasMod)
        {
            SendToHost(Join("dest", PendingDestinations.ToArray()));
        }

        PendingDestinations.Clear();
    }

    /// <summary>Sends one payload to one guest, split if it is over the wire limit.</summary>
    private static void SendTo(INetworkPlayer player, string text)
    {
        SendPieces(text, piece => player.Send(new CommanderNetMessage { Text = piece }));
    }

    /// <summary>Sends one payload from a guest to the host, split if it is over the wire limit.</summary>
    private static void SendToHost(string text)
    {
        NetworkClient? client = NetworkManagerNuclearOption.i?.Client;
        if (client != null)
        {
            SendPieces(text, piece => client.Send(new CommanderNetMessage { Text = piece }));
        }
    }

    /// <summary>
    /// One payload, one call to <paramref name="send"/>, unless it is over <see cref="MaxMessageBytes"/>,
    /// in which case it goes as several <see cref="PieceVerb"/> messages the far end puts back together.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Handing bytes to the engine's serializer is a trust boundary; whatever it throws must not take the mod's whole tick down with it.")]
    private static void SendPieces(string text, Action<string> send)
    {
        // Nothing in the tick chain catches, and this service is the first one registered, so an
        // exception escaping here stops every other service running that frame - for as long as the
        // payload that caused it keeps being rebuilt, which for a sync source is forever. Logged
        // once per verb rather than per frame, because the fault is in what is being sent, not in
        // how often.
        try
        {
            SendPiecesUnguarded(text, send);
        }
        catch (Exception error)
        {
            string verb = text.Split(Separator)[0];
            if (FailedSendVerbs.Add(verb))
            {
                CommanderPlugin.Log.LogError($"Could not send '{verb}' ({Encoding.UTF8.GetByteCount(text)} bytes): {error}");
            }
        }
    }

    private static void SendPiecesUnguarded(string text, Action<string> send)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxMessageBytes)
        {
            send(text);
            return;
        }

        // The header is five short fields; 80 bytes of headroom covers it with the payload budget.
        SplitByBytes(text, MaxMessageBytes - 80, PieceScratch);
        string id = nextPieceId++.ToString(CultureInfo.InvariantCulture);
        string total = PieceScratch.Count.ToString(CultureInfo.InvariantCulture);
        CommanderPlugin.Log.LogDebug(
            $"Sync payload of {Encoding.UTF8.GetByteCount(text)} bytes sent as {PieceScratch.Count} pieces.");
        for (int i = 0; i < PieceScratch.Count; i++)
        {
            send(Join(PieceVerb, new[] { id, i.ToString(CultureInfo.InvariantCulture), total, PieceScratch[i] }));
        }

        PieceScratch.Clear();
    }

    /// <summary>
    /// Cuts <paramref name="text"/> into runs of at most <paramref name="budget"/> UTF-8 bytes,
    /// never inside a character. Measured per character rather than by slicing the byte array,
    /// because a unit or mission name may be non-ASCII and half a code point is not a string.
    /// </summary>
    private static void SplitByBytes(string text, int budget, List<string> pieces)
    {
        pieces.Clear();
        StringBuilder current = new();
        int bytes = 0;
        for (int i = 0; i < text.Length; i++)
        {
            // Surrogate pairs are one character and must not be cut between the halves.
            bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length;
            int length = pair ? 2 : 1;
            int size = pair ? 4 : text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3;
            if (bytes + size > budget && current.Length > 0)
            {
                pieces.Add(current.ToString());
                current.Length = 0;
                bytes = 0;
            }

            current.Append(text, i, length);
            bytes += size;
            i += length - 1;
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString());
        }
    }

    /// <summary>
    /// Undoes <see cref="SendPieces"/>. Returns the payload unchanged when it was never split, the
    /// whole payload once the last piece lands, and null while pieces are still missing.
    /// <paramref name="sender"/> keys the buffers so two guests cannot interleave, and a part set
    /// that never completes (the sender left mid-payload) is dropped after
    /// <see cref="PieceTimeoutSeconds"/> rather than held forever.
    /// </summary>
    private static string? TryReassemble(string sender, string text)
    {
        if (!text.StartsWith(PieceVerb + SeparatorText, StringComparison.Ordinal))
        {
            return text;
        }

        string[] header = text.Split(new[] { Separator }, 5);
        if (header.Length < 5
            || !int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            || !int.TryParse(header[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total)
            || total <= 0
            || total > MaxPiecesPerPayload
            || index < 0
            || index >= total)
        {
            return null;
        }

        DropStalePieces();
        string key = sender + SeparatorText + header[1];
        if (!PieceBuffers.TryGetValue(key, out string?[] parts) || parts.Length != total)
        {
            if (parts == null && CountOpenPayloads(sender) >= MaxOpenPayloadsPerSender)
            {
                if (!pieceRefusalLogged)
                {
                    pieceRefusalLogged = true;
                    CommanderPlugin.Log.LogWarning($"Refused a split message from {sender}: too many unfinished payloads.");
                }

                return null;
            }

            parts = new string?[total];
            PieceBuffers[key] = parts;
            PieceStartedAt[key] = Time.realtimeSinceStartup;
        }

        parts[index] = header[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == null)
            {
                return null;
            }
        }

        PieceBuffers.Remove(key);
        PieceStartedAt.Remove(key);
        return string.Concat(parts);
    }

    private static int CountOpenPayloads(string sender)
    {
        string prefix = sender + SeparatorText;
        int open = 0;
        foreach (string key in PieceBuffers.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                open++;
            }
        }

        return open;
    }

    private static void DropStalePieces()
    {
        if (PieceStartedAt.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        StalePieceScratch.Clear();
        foreach (KeyValuePair<string, float> entry in PieceStartedAt)
        {
            if (now - entry.Value > PieceTimeoutSeconds)
            {
                StalePieceScratch.Add(entry.Key);
            }
        }

        for (int i = 0; i < StalePieceScratch.Count; i++)
        {
            PieceBuffers.Remove(StalePieceScratch[i]);
            PieceStartedAt.Remove(StalePieceScratch[i]);
        }

        StalePieceScratch.Clear();
    }

    /// <summary>Says once when the host's traffic stops, and once when it comes back. See <see cref="HostQuietWarnSeconds"/>.</summary>
    private static void WatchHostTraffic()
    {
        if (!HostHasMod || lastHostMessageAt <= 0f)
        {
            return;
        }

        float quiet = Time.realtimeSinceStartup - lastHostMessageAt;
        if (quiet > HostQuietWarnSeconds && !hostQuietLogged)
        {
            hostQuietLogged = true;
            CommanderPlugin.Log.LogWarning(
                $"Nothing has arrived from the host for {quiet:0.0} s. The host sends every 2 s, so the connection is stalled.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A host message runs base-game code that can throw anything; one bad message must not kill the guest's network handler.")]
    private static void OnGuestMessage(INetworkPlayer host, CommanderNetMessage message)
    {
        if (hostQuietLogged)
        {
            hostQuietLogged = false;
            CommanderPlugin.Log.LogWarning(
                $"The host is through again after {Time.realtimeSinceStartup - lastHostMessageAt:0.0} s of silence.");
        }

        lastHostMessageAt = Time.realtimeSinceStartup;
        string? payload = TryReassemble("host", message.Text ?? string.Empty);
        if (payload == null)
        {
            return;
        }

        string[] args = payload.Split(Separator);
        if (args[0] == "hello")
        {
            HearHello(Arg(args, 1));
            return;
        }

        if (!HostHasMod || !GuestHandlers.TryGetValue(args[0], out Action<string[]> handler))
        {
            return;
        }

        try
        {
            handler(args);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Host message '{args[0]}' failed: {exception}");
        }
    }

    private static void HearHello(string version)
    {
        if (version == hostVersion)
        {
            return;
        }

        hostVersion = version;
        if (version == WireVersion)
        {
            Forward("hi");
            CommanderPlugin.Log.LogInfo($"The host runs Ground Control {version}: commander orders from this seat go through the host.");
            return;
        }

        CommanderPlugin.Log.LogWarning($"The host runs Ground Control {version}, this game runs {WireVersion}.");
        CommanderNotice.Fail($"The host runs a different build of Ground Control ({version}, you run {WireVersion}). Install the same build to command.");
    }

    private static string BuildWireVersion()
    {
        List<string> verbs = new(HostHandlers.Keys);
        verbs.AddRange(GuestHandlers.Keys);
        verbs.Sort(StringComparer.Ordinal);

        // FNV-1a: string.GetHashCode is not promised to agree between two machines.
        uint hash = 2166136261;
        for (int i = 0; i < verbs.Count; i++)
        {
            foreach (char letter in verbs[i] + "\n")
            {
                hash = (hash ^ letter) * 16777619;
            }
        }

        return $"{PluginInfo.Version}+{hash.ToString("x8", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Forgets the guests that have gone.</summary>
    private static void NoteLeftGuests(NetworkServer server)
    {
        LeftGuests.Clear();
        IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
        foreach (INetworkPlayer guest in ModdedGuests)
        {
            bool present = false;
            for (int i = 0; i < players.Count && !present; i++)
            {
                present = ReferenceEquals(players[i], guest);
            }

            if (!present)
            {
                LeftGuests.Add(guest);
            }
        }

        for (int i = 0; i < LeftGuests.Count; i++)
        {
            INetworkPlayer guest = LeftGuests[i];
            CommanderQueuePayment.NoteGuestLeft(guest);
            ModdedGuests.Remove(guest);
        }
    }

    /// <summary>The faction a connection is playing for, or null while it has not picked one.</summary>
    private static FactionHQ? ResolveHq(INetworkPlayer sender)
    {
        NetworkIdentity? identity = sender.Identity;
        if (identity != null && identity.TryGetComponent(out Player player) && player.HQ != null)
        {
            return player.HQ;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            List<Player> players = hq.GetPlayers(sortByScore: false);
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] != null && ReferenceEquals(players[i].Owner, sender))
                {
                    return hq;
                }
            }
        }

        return null;
    }

    private static FactionHQ? GetDedicatedStandIn()
    {
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (ActingHq != null || manager?.Server == null || !manager.Server.Active)
        {
            return null;
        }

        // AI COMMANDERS on both sides: the host's own team is played too, by handing "the local
        // team" to each faction in turn.
        if (CommanderEnemyCommanderService.BothSides)
        {
            return RotateLocalSide();
        }

        if (GameManager.GetLocalHQ(out FactionHQ localHq) && localHq != null)
        {
            return null;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.faction != null && CommanderEnemyCommanderService.HasHumanPlayers(hq))
            {
                return hq;
            }
        }

        return null;
    }

    /// <summary>Each faction in turn, <see cref="LocalSideSwapSeconds"/> apiece.</summary>
    private static FactionHQ? RotateLocalSide()
    {
        Dictionary<Faction, FactionHQ>.ValueCollection hqs = FactionRegistry.GetAllHQs();
        int count = 0;
        foreach (FactionHQ hq in hqs)
        {
            if (hq != null && hq.faction != null)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        int pick = (int)(Time.timeSinceLevelLoad / LocalSideSwapSeconds) % count;
        foreach (FactionHQ hq in hqs)
        {
            if (hq != null && hq.faction != null && pick-- == 0)
            {
                return hq;
            }
        }

        return null;
    }

    private static string Join(string verb, string[] args)
    {
        return args.Length == 0 ? verb : verb + SeparatorText + string.Join(SeparatorText, args);
    }

    private static T? Find<T>(IEnumerable<UnitDefinition>? definitions, string key)
        where T : UnitDefinition
    {
        if (definitions == null)
        {
            return null;
        }

        foreach (UnitDefinition definition in definitions)
        {
            if (definition is T typed && definition.jsonKey == key)
            {
                return typed;
            }
        }

        return null;
    }
}
