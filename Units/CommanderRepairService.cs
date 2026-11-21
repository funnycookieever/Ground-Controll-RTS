using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Repair: which friendly buildings are damaged, and the paid crews a commander sends to fix them.
/// <para>
/// A crew is the faction's own repair truck, bought out of faction funds and dropped beside the
/// building it was hired for. From there the Basegame <see cref="Repairer"/> loop does the work —
/// this service only pins which building the crew should drive to, through
/// <see cref="CommanderRepairPatches"/>. Both commanders hire crews at the same price.
/// </para>
/// </summary>
/// <remarks>
/// The crew is a real vehicle rather than a timer that heals the building, so it can be
/// shelled on the way in and it shows up on the map like everything else. It also means repair
/// needs no new networking — spawning a vehicle is something this mod already does.
/// </remarks>
internal sealed class CommanderRepairService : ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>
    /// A human side's munitions trucks, held off the base game's <c>RearmVehicleAI</c> missions.
    /// Left on them, a truck the player parked drove off to whoever its faction's rearm controller
    /// picked, and back to a depot after every rearm, so a move or a STOP never held it.
    /// </summary>
    internal static readonly HashSet<RearmVehicleAI> TamedTrucks = new();

    private const float DamageRefreshIntervalSeconds = 3f;

    private const float StandbySweepSeconds = 2f;

    /// <summary>How far from its post a standby repair vehicle goes to fix something: the Repairer's
    /// own reach, so "its base" and "what it can repair" are the same ground.</summary>
    private const float StandbyRepairRangeMeters = 1000f;

    /// <summary>How far out of the building the crew is dropped, on top of the building radius.</summary>
    private const float CrewDropMargin = 80f;

    /// <summary>How long after a crew's last tick the building still counts as being worked on.
    /// <c>Repairer</c> ticks every five seconds, so this is one missed tick.</summary>
    private const float RepairTickGraceSeconds = 8f;

    private static readonly AccessTools.FieldRef<GroundVehicle, bool> CommandedDestinationRef =
        AccessTools.FieldRefAccess<GroundVehicle, bool>("commandedDestination");

    /// <summary>The game's own "these want a crew" list. Private, and the only place a
    /// destroyed building is still on the books after <c>RemoveFactionUnit</c> drops it.</summary>
    private static readonly FieldInfo? UnitsNeedingRepairField =
        AccessTools.Field(typeof(FactionHQ), "unitsNeedingRepair");

    private static readonly AccessTools.FieldRef<GroundVehicle, bool> HoldAtCommandedDestinationRef =
        AccessTools.FieldRefAccess<GroundVehicle, bool>("holdAtPlayerCommandedDestination");

    private static readonly AccessTools.FieldRef<GroundVehicle, bool> NavigateToObjectivesRef =
        AccessTools.FieldRefAccess<GroundVehicle, bool>("navigateToObjectives");

    private static readonly HashSet<UnitDefinition> ReportedHoldFlags = new();

    /// <summary>Whether a type carries a <see cref="Repairer"/>: prefab data, asked of every unit a
    /// faction owns several times per AI review, and a component search each time.</summary>
    private static readonly Dictionary<UnitDefinition, bool> RepairTypes = new();

    /// <summary>Where each of our repair vehicles waits between jobs: the base it arrived at.</summary>
    private readonly Dictionary<Unit, GlobalPosition> standbyPosts = new();

    /// <summary>Ground vehicles the standby sweep has already looked at, repair vehicles or not, so
    /// the component search is paid once per vehicle rather than every sweep.</summary>
    private readonly HashSet<Unit> sweptVehicles = new();
    private readonly HashSet<GroundVehicle> tamedArtillery = new();

    /// <summary>Factions a multiplayer guest has played this match.</summary>
    private readonly HashSet<FactionHQ> guestFactions = new();

    private readonly List<Unit> staleVehicles = new();
    private readonly HashSet<Unit> nearestTargetUnits = new();
    private readonly HashSet<Unit> repairOffUnits = new();

    // Builders the player told to wait where their last build finished instead of driving home.
    private readonly HashSet<Unit> stayAfterBuildUnits = new();
    private readonly Dictionary<Unit, Unit> crewAssignments = new();
    private readonly List<Unit> damagedBuildings = new();
    private readonly List<Unit> staleCrews = new();
    private readonly List<Unit> damagedScratch = new();
    private readonly HashSet<Unit> hostCrewed = new();

    /// <summary>When a crew last put work into each building, so the countdown only shows while
    /// somebody is actually on the job. Host side; a guest gets the numbers in the repair list.</summary>
    private readonly Dictionary<Unit, float> repairTicks = new();

    /// <summary>The host's countdowns, and when they arrived, so a guest's clock runs between syncs
    /// instead of stepping every two seconds.</summary>
    private readonly Dictionary<Unit, (float Seconds, float ReceivedAt)> hostCountdowns = new();

    private readonly List<Unit> staleTicks = new();

    private float nextStandbySweepAt;

    private float nextDamageRefreshAt;
    private float statusUntil;
    private string statusText = string.Empty;

    internal CommanderRepairService()
    {
        Instance = this;
        nextDamageRefreshAt = CommanderScheduler.Stagger("repair.damage", DamageRefreshIntervalSeconds);
        nextStandbySweepAt = CommanderScheduler.Stagger("repair.standby", StandbySweepSeconds);
        CommanderNetwork.OnHost("repair", HostRepair);
        CommanderNetwork.OnHost("repair.nearest", HostNearestTarget);
        CommanderNetwork.OnHost("repair.stay", HostStayAfterBuild);
        CommanderNetwork.OnGuest("st.repair", args => SetStatus(CommanderNetwork.Arg(args, 1)));
        CommanderNetwork.OnSync(SyncRepairList);
        CommanderNetwork.OnGuest("repair.list", ApplyRepairList);
    }

    internal static CommanderRepairService? Instance { get; private set; }

    internal static float CrewCost => Mathf.Max(0f, CommanderSettings.RepairCrewCost);

    /// <summary>Seconds one crew takes to bring a building from a wreck back to whole.
    /// <see cref="CommanderRepairRatePatches"/> is what makes that true of any building.</summary>
    internal static float RepairSeconds => Mathf.Max(5f, CommanderSettings.RepairSeconds);

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    /// <summary>Friendly buildings currently in need of repair, refreshed while RTS mode is up.</summary>
    internal IReadOnlyList<Unit> DamagedBuildings => damagedBuildings;

    public void TickActive()
    {
        if (CommanderScheduler.IsDue(ref nextDamageRefreshAt, DamageRefreshIntervalSeconds))
        {
            RefreshDamagedBuildings();
            PruneCrews();
        }
    }

    /// <summary>
    /// Keeps our repair vehicles at home. The Jacknife has <c>holdAtPlayerCommandedDestination</c> off
    /// and <c>navigateToObjectives</c> on, so on reaching any ordered spot the game drops the order and
    /// drives it at the mission's objectives. Persistent, because it wanders off while flying too.
    /// </summary>
    public void TickPersistent()
    {
        if (!CommanderScheduler.IsDue(ref nextStandbySweepAt, StandbySweepSeconds))
        {
            return;
        }

        // Every faction the host runs, not only its own: a multiplayer guest's builders are driven by the
        // host's game and would head for the objective too. The enemy commander's builders likewise.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.factionUnits != null && hq.IsServer)
            {
                // Players on it, not "not the AI": IsAiFaction is false for every faction when the
                // enemy commander is off, which would strip a stock mission's AI guns of their driver.
                bool human = ReferenceEquals(hq, localHq) || CommanderEnemyCommanderService.HasHumanPlayers(hq);
                SweepStandby(hq, human);
                HoldLeftBehindUnits(hq, localHq);
            }
        }

        StowMovingArtillery();

        staleVehicles.Clear();
        foreach (Unit swept in sweptVehicles)
        {
            if (swept == null || swept.disabled)
            {
                staleVehicles.Add(swept!);
            }
        }

        for (int i = 0; i < staleVehicles.Count; i++)
        {
            sweptVehicles.Remove(staleVehicles[i]);
            standbyPosts.Remove(staleVehicles[i]);
        }

        staleVehicles.Clear();
    }

    public void ResetSession()
    {
        nearestTargetUnits.Clear();
        repairOffUnits.Clear();
        stayAfterBuildUnits.Clear();
        crewAssignments.Clear();
        damagedBuildings.Clear();
        staleCrews.Clear();
        damagedScratch.Clear();
        standbyPosts.Clear();
        sweptVehicles.Clear();
        tamedArtillery.Clear();
        TamedTrucks.Clear();
        staleVehicles.Clear();
        guestFactions.Clear();
        repairTicks.Clear();
        hostCountdowns.Clear();
        staleTicks.Clear();
        statusText = string.Empty;
    }

    /// <summary>
    /// Every building of one faction that wants a repair crew, flattened or merely dented.
    /// </summary>
    /// <remarks>
    /// This reads <c>FactionHQ.unitsNeedingRepair</c>, the game's own list, and not
    /// <c>factionUnits</c> — which is the whole fix. <c>Unit.UnitDisabled</c> calls
    /// <c>RemoveFactionUnit</c> the moment a building is flattened, so a walk of <c>factionUnits</c>
    /// can only ever see damaged buildings and never destroyed ones: both commanders could patch up
    /// a dented refinery and neither could ever rebuild a bombed one. The game's own list keeps the
    /// wreck (only a completed <c>Building.Repair</c> or an owner change removes an entry), and
    /// <c>Building.Repair</c> works on a disabled building and un-disables it when the hit points
    /// come back — so a destroyed launch pad really is repairable, it just could not be found.
    /// </remarks>
    internal static void CollectRepairable(FactionHQ? hq, List<Unit> into)
    {
        into.Clear();
        List<Unit>? needingRepair = hq == null ? null : UnitsNeedingRepairField?.GetValue(hq) as List<Unit>;
        if (needingRepair == null)
        {
            return;
        }

        for (int i = 0; i < needingRepair.Count; i++)
        {
            Unit unit = needingRepair[i];
            if (unit != null && unit is IRepairable repairable && repairable.NeedsRepair())
            {
                into.Add(unit);
            }
        }
    }

    /// <summary>
    /// Makes a player order stick once the vehicle gets there. <c>GroundVehicle.CheckObstacles</c>
    /// drops <c>commandedDestination</c> on arrival when <c>holdAtPlayerCommandedDestination</c> is off,
    /// then drives at the nearest objective or enemy. The authored values are logged once per type.
    /// </summary>
    internal static void KeepAtCommandedDestination(GroundVehicle vehicle)
    {
        if (ReportedHoldFlags.Add(vehicle.definition))
        {
            CommanderPlugin.Log.LogInfo(
                $"{vehicle.definition.unitName} as authored: holdAtPlayerCommandedDestination="
                    + $"{HoldAtCommandedDestinationRef(vehicle)}, navigateToObjectives={NavigateToObjectivesRef(vehicle)}.");
        }

        HoldAtCommandedDestinationRef(vehicle) = true;
    }

    /// <summary>A repair vehicle never drives off at the objectives by itself, and a player order
    /// sticks once it arrives. It still leaves to repair something within its own 1 km reach.</summary>
    internal static void KeepOnStandby(GroundVehicle vehicle)
    {
        KeepAtCommandedDestination(vehicle);
        NavigateToObjectivesRef(vehicle) = false;
    }

    internal static bool IsRepairUnit(Unit? unit)
    {
        if (unit == null || unit.definition == null)
        {
            return GetRepairer(unit) != null;
        }

        if (!RepairTypes.TryGetValue(unit.definition, out bool repair))
        {
            repair = GetRepairer(unit) != null;
            RepairTypes[unit.definition] = repair;
        }

        return repair;
    }

    internal bool UsesNearestTarget(Unit? unit)
    {
        return unit != null && nearestTargetUnits.Contains(unit);
    }

    /// <summary>Repair switched off: the vehicle only ever builds blueprints.</summary>
    internal bool IsRepairOff(Unit? unit)
    {
        return unit != null && repairOffUnits.Contains(unit);
    }

    /// <summary>Cycles PRIORITY → NEAREST → OFF → PRIORITY.</summary>
    internal void CycleRepairMode(Unit unit)
    {
        Repairer? repairer = GetRepairer(unit);
        if (repairer == null)
        {
            return;
        }

        bool nearest = false;
        bool off = false;
        if (nearestTargetUnits.Remove(unit))
        {
            off = repairOffUnits.Add(unit);
        }
        else if (!repairOffUnits.Remove(unit))
        {
            nearest = nearestTargetUnits.Add(unit);
        }

        // The repair search runs on the host; a guest's toggle is only a label without this.
        CommanderNetwork.Forward("repair.nearest", CommanderNetwork.Id(unit), CommanderNetwork.Flag(nearest), CommanderNetwork.Flag(off));

        CommanderRepairPatches.RequestImmediateSearch(repairer);
        SetStatus(off
            ? "Repair off: this vehicle only builds blueprints."
            : nearest
                ? "Repair targeting set to nearest damaged structure."
                : "Repair targeting restored to Basegame priority.");
    }

    internal bool StaysAfterBuild(Unit? unit)
    {
        return unit != null && stayAfterBuildUnits.Contains(unit);
    }

    internal void ToggleStayAfterBuild(Unit unit)
    {
        bool enabled = !stayAfterBuildUnits.Remove(unit);
        if (enabled)
        {
            stayAfterBuildUnits.Add(unit);
        }

        // Construction finishes on the host, so a guest's toggle is only a label without this.
        CommanderNetwork.Forward("repair.stay", CommanderNetwork.Id(unit), CommanderNetwork.Flag(enabled));
        SetStatus(enabled
            ? "Builder will wait where its build finishes."
            : "Builder will return to its post after building.");
    }

    internal bool ShouldUseNearestTarget(Unit? unit)
    {
        return unit != null && !unit.disabled && nearestTargetUnits.Contains(unit);
    }

    /// <summary>The building a hired crew was paid to fix, while that job is still outstanding.</summary>
    internal bool TryGetAssignedTarget(Unit? crew, out Unit target)
    {
        target = null!;
        if (crew == null || !crewAssignments.TryGetValue(crew, out Unit assigned))
        {
            return false;
        }

        if (assigned == null || assigned is not IRepairable repairable || !repairable.NeedsRepair())
        {
            crewAssignments.Remove(crew);
            return false;
        }

        target = assigned;
        return true;
    }

    /// <summary>Whether a paid crew is already on its way to <paramref name="building"/>.</summary>
    internal bool HasCrewAssigned(Unit building)
    {
        if (CommanderNetwork.HostHasMod)
        {
            return hostCrewed.Contains(building);
        }

        foreach (KeyValuePair<Unit, Unit> entry in crewAssignments)
        {
            if (entry.Key != null && !entry.Key.disabled && ReferenceEquals(entry.Value, building))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A crew just put work into <paramref name="building"/>. Called from the repair-rate
    /// prefix, which is the one place that sees every repair anybody does.</summary>
    internal void NoteRepairTick(Unit building)
    {
        repairTicks[building] = Time.time;
    }

    /// <summary>
    /// Seconds until <paramref name="building"/> is whole again, while a crew is on it. Nothing
    /// otherwise: the countdown is a job in progress, not a damage report on a building nobody is
    /// working on.
    /// </summary>
    /// <remarks>
    /// The estimate is the worst part's hit points against the one-crew rate, so two crews on the
    /// same building finish ahead of it rather than behind it. Repair runs on the host, so a guest
    /// reads the host's number and runs it down locally between syncs.
    /// </remarks>
    internal bool TryGetRepairCountdown(Unit? building, out float seconds)
    {
        seconds = 0f;
        if (building == null)
        {
            return false;
        }

        if (CommanderNetwork.HostHasMod)
        {
            if (!hostCountdowns.TryGetValue(building, out (float Seconds, float ReceivedAt) remote))
            {
                return false;
            }

            seconds = remote.Seconds - (Time.time - remote.ReceivedAt);
            return seconds > 0f;
        }

        if (!repairTicks.TryGetValue(building, out float lastTick)
            || Time.time - lastTick > RepairTickGraceSeconds)
        {
            return false;
        }

        seconds = GetRemainingSeconds(building);
        return seconds > 0f;
    }

    /// <summary>Hires a crew for the local faction. The BUILD window REPAIR rows call this.</summary>
    internal bool SendRepairCrew(Unit building)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return Refuse("No faction HQ.");
        }

        // Crews are spawned through the server object manager, so a guest asks the host for one.
        if (CommanderNetwork.Forward("repair", CommanderNetwork.Id(building)))
        {
            if (CommanderNetwork.HostHasMod)
            {
                SetStatus($"Repair crew for {CommanderGameAccess.GetUnitLabel(building)} on its way.");
            }

            return CommanderNetwork.HostHasMod;
        }

        if (hq.factionFunds < CrewCost)
        {
            return Refuse("Insufficient faction funds for a repair crew.");
        }

        if (!TryHireCrew(hq, building))
        {
            return Refuse("This faction fields no repair vehicle.");
        }

        SetStatus($"Repair crew dispatched to {CommanderGameAccess.GetUnitLabel(building)} for {CrewCost:0}.");
        return true;
    }

    /// <summary>
    /// The enemy commander half. Same price, same crew, out of its own funds — the duel stays
    /// symmetric, so a bombed enemy refinery gets patched up just like yours does.
    /// </summary>
    internal bool TrySendEnemyRepairCrew(FactionHQ hq)
    {
        if (!hq.IsServer)
        {
            return false;
        }

        // One crew on the road at a time. Every crew stays on as a repair vehicle once it is done, so
        // one hire per review at a flattened base was a steady stream of Jacknifes (public server).
        foreach (KeyValuePair<Unit, Unit> entry in crewAssignments)
        {
            if (entry.Key != null && !entry.Key.disabled && ReferenceEquals(entry.Key.NetworkHQ, hq))
            {
                return false;
            }
        }

        Unit? worst = null;
        CollectRepairable(hq, damagedScratch);
        for (int i = 0; i < damagedScratch.Count; i++)
        {
            Unit candidate = damagedScratch[i];
            if (HasCrewAssigned(candidate))
            {
                continue;
            }

            if (worst == null || candidate.definition.value > worst.definition.value)
            {
                worst = candidate;
            }
        }

        // The crew drives out of a standing depot rather than appearing beside the wreck, so a base
        // with its depots down cannot conjure the vehicles that rebuild it, and the player can
        // intercept the ones that come in from elsewhere.
        if (worst == null
            || !hq.TryGetNearestDepot(worst.transform.position, float.MaxValue, out VehicleDepot depot)
            || depot == null
            || depot.disabled
            || !TryHireCrew(hq, worst, depot))
        {
            return false;
        }

        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction?.name}) sent a repair crew to {CommanderGameAccess.GetUnitLabel(worst)}.");
        return true;
    }

    internal bool IsOnStandby(Unit? unit)
    {
        return unit != null && standbyPosts.ContainsKey(unit);
    }

    internal bool TryGetStandbyPost(Unit unit, out GlobalPosition post)
    {
        return standbyPosts.TryGetValue(unit, out post);
    }

    /// <summary>
    /// What one of our standby repair vehicles should fix: the nearest damaged building within
    /// <see cref="StandbyRepairRangeMeters"/> of its post, or nothing. The base game's own pick,
    /// <c>FactionHQ.TryGetUnitNeedingRepair</c>, is not range-limited: <c>Building.GetRepairPriority</c>
    /// only multiplies a building beyond the repairer's 1 km by 0.001, so with nothing damaged
    /// nearby the Jacknife drove across the map to the one damaged building left.
    /// </summary>
    internal Unit? FindStandbyRepairTarget(Unit repairer)
    {
        if (!standbyPosts.TryGetValue(repairer, out GlobalPosition post))
        {
            return null;
        }

        CollectRepairable(repairer.NetworkHQ, damagedScratch);
        Unit? best = null;
        float bestDistance = StandbyRepairRangeMeters * StandbyRepairRangeMeters;
        for (int i = 0; i < damagedScratch.Count; i++)
        {
            float distance = FastMath.SquareDistance(post, damagedScratch[i].startPosition);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = damagedScratch[i];
            }
        }

        return best;
    }

    /// <summary>Sends a repair vehicle back to the post it waits at, after a job away from it. One set to
    /// stay waits where it is instead, and that spot becomes its post, so the standby sweep and the
    /// next queued blueprint's reach both measure from there.</summary>
    internal void SendHome(Unit unit)
    {
        if (unit != null && !unit.disabled && stayAfterBuildUnits.Contains(unit) && standbyPosts.ContainsKey(unit))
        {
            GlobalPosition here = CommanderEconomyService.MoveOutOfDepotLanes(unit.GlobalPosition());
            standbyPosts[unit] = here;
            CommanderGameAccess.TrySetDestination(unit, here);
            return;
        }

        // Asked again here: the post is older than any depot built or rally point set since.
        if (unit != null && !unit.disabled && standbyPosts.TryGetValue(unit, out GlobalPosition post))
        {
            post = CommanderEconomyService.MoveOutOfDepotLanes(post);
            standbyPosts[unit] = post;
            CommanderGameAccess.TrySetDestination(unit, post);
        }
    }

    /// <summary>Moves a repair vehicle's post: one flown somewhere by sling lift is based there now,
    /// or it would try to drive back from a place it was flown to because it could not drive.</summary>
    internal void SetHome(Unit unit, GlobalPosition post)
    {
        if (unit != null && standbyPosts.ContainsKey(unit))
        {
            standbyPosts[unit] = post;
        }
    }

    /// <summary>What is left of the job, from the worst-off part: <c>Building.Repair</c> lifts every
    /// part by the same step, so the lowest one finishes last.</summary>
    private static float GetRemainingSeconds(Unit building)
    {
        List<UnitPart> parts = building.partLookup;
        float worst = 100f;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] != null)
            {
                worst = Mathf.Min(worst, Mathf.Max(0f, parts[i].hitPoints));
            }
        }

        return (100f - worst) * 0.01f * RepairSeconds;
    }

    private static Repairer? GetRepairer(Unit? unit)
    {
        return unit?.GetComponentInChildren<Repairer>(true);
    }

    /// <summary>
    /// A multiplayer guest who leaves stops sending orders: that side's ground units finish the
    /// order they have and then wait, rather than drive at the host's base.
    /// </summary>
    /// <remarks>
    /// Without orders, <c>GroundVehicle.CheckObstacles</c> drives idle vehicles at the nearest objective
    /// or enemy. Turning <c>navigateToObjectives</c> off is the whole rule: a vehicle under an order
    /// finishes it and stops. It is not turned back on when the guest returns; a depot on AUTO ATTACK
    /// gives its units real orders anyway.
    /// </remarks>
    private void HoldLeftBehindUnits(FactionHQ hq, FactionHQ? localHq)
    {
        if (!CommanderEnemyCommanderService.IsMultiplayerMatch || ReferenceEquals(hq, localHq))
        {
            return;
        }

        if (CommanderEnemyCommanderService.HasHumanPlayers(hq))
        {
            guestFactions.Add(hq);
            return;
        }

        if (!guestFactions.Contains(hq))
        {
            return;
        }

        int held = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle vehicle
                && !unit.disabled
                && NavigateToObjectivesRef(vehicle))
            {
                NavigateToObjectivesRef(vehicle) = false;
                KeepAtCommandedDestination(vehicle);
                held++;
            }
        }

        if (held > 0)
        {
            CommanderPlugin.Log.LogInfo(
                $"{hq.faction.name} has no player connected: {held} of its ground unit(s) will finish their order and wait instead of driving at the enemy.");
        }
    }

    /// <summary>
    /// Removes the base game's <c>MobileArtilleryAI</c> from a human side's gun. It calls
    /// <c>StopImmediately</c> every second while firing and <c>ReturnToRoad</c> otherwise, ignoring
    /// player commands. Destroying it cancels its UniTask loops (they use its destroy token). It also
    /// raised the gun when stopped, so <see cref="StowMovingArtillery"/> does that instead.
    /// </summary>
    private void TameArtillery(GroundVehicle vehicle)
    {
        if (vehicle.TryGetComponent(out RearmVehicleAI truck))
        {
            TamedTrucks.Add(truck);
        }

        MobileArtilleryAI? ai = vehicle.GetComponentInChildren<MobileArtilleryAI>(true);
        if (ai != null)
        {
            Object.Destroy(ai);
            tamedArtillery.Add(vehicle);
        }
    }

    /// <summary>A gun that has stopped is raised, one that is moving is stowed, like the base game's
    /// own artillery AI did between firing spots. Raised only once its setup animation is done: an
    /// MLRS unstowed the moment it stopped fired its rockets with the launcher still folded.</summary>
    private void StowMovingArtillery()
    {
        tamedArtillery.RemoveWhere(vehicle => vehicle == null || vehicle.disabled);
        foreach (GroundVehicle vehicle in tamedArtillery)
        {
            vehicle.StowTurrets(Mathf.Abs(vehicle.speed) >= 1f || CommanderFireNow.ArePartsDeploying(vehicle));
        }
    }

    private void SweepStandby(FactionHQ hq, bool human)
    {
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle vehicle
                || unit.disabled
                || !sweptVehicles.Add(unit))
            {
                continue;
            }

            if (human)
            {
                TameArtillery(vehicle);
            }

            if (GetRepairer(unit) == null)
            {
                continue;
            }

            KeepOnStandby(vehicle);

            // One already under a player order (a depot's rally point) waits where that order ends;
            // anything else waits where it stands, unless that is a depot's doorway.
            bool ordered = CommandedDestinationRef(vehicle);
            GlobalPosition post = ordered && CommanderGameAccess.TryGetCurrentCommandPosition(unit, out GlobalPosition going)
                ? going
                : CommanderEconomyService.MoveOutOfDepotLanes(unit.GlobalPosition());
            standbyPosts[unit] = post;
            if (!ordered)
            {
                CommanderGameAccess.TrySetDestination(unit, post);
            }
        }
    }

    private bool TryHireCrew(FactionHQ hq, Unit building, Unit? spawnBeside = null)
    {
        Unit anchor = spawnBeside ?? building;
        VehicleDefinition? definition = CommanderGameAccess.FindRepairVehicleDefinition(hq);
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || spawner == null || hq.factionFunds < CrewCost)
        {
            return false;
        }

        // Dropped clear of the building footprint, or the crew spawns inside the wall it came to
        // fix and the Basegame pathing has to shove it out first.
        float angle = Random.Range(0f, Mathf.PI * 2f);
        float distance = (anchor.maxRadius * 2f) + CrewDropMargin;
        GlobalPosition site = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            anchor.startPosition.x + (Mathf.Cos(angle) * distance),
            anchor.startPosition.y,
            anchor.startPosition.z + (Mathf.Sin(angle) * distance)));

        Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        Unit? crew = spawner.SpawnFromUnitDefinitionInEditor(
            definition,
            (site.ToLocalPosition() + (rotation * definition.spawnOffset)).ToGlobalPosition(),
            rotation,
            hq,
            $"NOC_CREW_{hq.GetInstanceID()}_{Time.frameCount}");
        if (crew == null)
        {
            return false;
        }

        CommanderGameAccess.TrySpend(hq, CrewCost);
        crewAssignments[crew] = building;

        Repairer? repairer = GetRepairer(crew);
        if (repairer != null)
        {
            CommanderRepairPatches.RequestImmediateSearch(repairer);
        }

        return true;
    }

    private void RefreshDamagedBuildings()
    {
        // A guest's list comes from the host (ApplyRepairList): the game's own repair register,
        // the only place a flattened building is still listed, is server-side.
        if (CommanderNetwork.HostHasMod)
        {
            return;
        }

        CollectRepairable(CommanderGameAccess.GetLocalHq(), damagedBuildings);
    }

    private void HostStayAfterBuild(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit) || !ReferenceEquals(unit.NetworkHQ, hq))
        {
            return;
        }

        if (CommanderNetwork.Bool(args, 2))
        {
            stayAfterBuildUnits.Add(unit);
        }
        else
        {
            stayAfterBuildUnits.Remove(unit);
        }
    }

    private void HostNearestTarget(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit) || !ReferenceEquals(unit.NetworkHQ, hq))
        {
            return;
        }

        if (CommanderNetwork.Bool(args, 2))
        {
            nearestTargetUnits.Add(unit);
        }
        else
        {
            nearestTargetUnits.Remove(unit);
        }

        if (CommanderNetwork.Bool(args, 3))
        {
            repairOffUnits.Add(unit);
        }
        else
        {
            repairOffUnits.Remove(unit);
        }

        Repairer? repairer = GetRepairer(unit);
        if (repairer != null)
        {
            CommanderRepairPatches.RequestImmediateSearch(repairer);
        }
    }

    private void HostRepair(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit building) || !ReferenceEquals(building.NetworkHQ, hq))
        {
            return;
        }

        string hostStatus = statusText;
        float hostUntil = statusUntil;
        statusText = string.Empty;
        SendRepairCrew(building);
        if (statusText.Length > 0)
        {
            CommanderNetwork.Reply("st.repair", statusText);
        }

        statusText = hostStatus;
        statusUntil = hostUntil;
    }

    /// <summary>What needs a crew, and whether one is already on its way, as "id crewed".</summary>
    private void SyncRepairList(FactionHQ hq, List<string[]> messages)
    {
        CollectRepairable(hq, damagedScratch);
        string[] args = new string[damagedScratch.Count + 1];
        args[0] = "repair.list";
        for (int i = 0; i < damagedScratch.Count; i++)
        {
            Unit building = damagedScratch[i];
            float countdown = TryGetRepairCountdown(building, out float seconds) ? seconds : 0f;
            args[i + 1] =
                $"{CommanderNetwork.Id(building)} {CommanderNetwork.Flag(HasCrewAssigned(building))} {CommanderNetwork.Num(countdown)}";
        }

        messages.Add(args);
    }

    private void ApplyRepairList(string[] args)
    {
        damagedBuildings.Clear();
        hostCrewed.Clear();
        hostCountdowns.Clear();
        for (int i = 1; i < args.Length; i++)
        {
            string[] fields = args[i].Split(' ');
            if (fields.Length < 2 || !CommanderNetwork.TryUnit(fields[0], out Unit building))
            {
                continue;
            }

            damagedBuildings.Add(building);
            if (CommanderNetwork.Bool(fields, 1))
            {
                hostCrewed.Add(building);
            }

            float countdown = CommanderNetwork.Float(fields, 2);
            if (countdown > 0f)
            {
                hostCountdowns[building] = (countdown, Time.time);
            }
        }
    }

    private void PruneCrews()
    {
        staleCrews.Clear();
        foreach (KeyValuePair<Unit, Unit> entry in crewAssignments)
        {
            if (entry.Key == null
                || entry.Key.disabled
                || entry.Value == null
                || entry.Value is not IRepairable repairable
                || !repairable.NeedsRepair())
            {
                staleCrews.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleCrews.Count; i++)
        {
            crewAssignments.Remove(staleCrews[i]);
        }

        staleCrews.Clear();

        staleTicks.Clear();
        foreach (KeyValuePair<Unit, float> tick in repairTicks)
        {
            if (tick.Key == null || Time.time - tick.Value > RepairTickGraceSeconds)
            {
                staleTicks.Add(tick.Key!);
            }
        }

        for (int i = 0; i < staleTicks.Count; i++)
        {
            repairTicks.Remove(staleTicks[i]);
        }

        staleTicks.Clear();
    }

    /// <summary>A repair that did not happen. Loud, because the grey status line read as nothing
    /// having been clicked at all.</summary>
    private bool Refuse(string text)
    {
        SetStatus(text);
        CommanderNotice.Fail(text);
        return false;
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + 6f;
    }
}
