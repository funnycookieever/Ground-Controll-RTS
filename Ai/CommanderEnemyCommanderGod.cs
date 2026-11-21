using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// GOD DIFFICULTY's two powers, the only part of it that changes how the AI plays. Both are
/// switched on by <see cref="CommanderGodService"/>, on the host, and neither exists otherwise.
/// </summary>
/// <remarks>
/// <b>Divine wrath</b>: losing a base forces the next review at once and, for
/// <see cref="WrathSeconds"/>, sends the strike wing at the base it lost as a wave of whatever size
/// it has, instead of holding for <see cref="StrikeWaveSize"/> over its staging base.
/// <para>
/// <b>Nuclear threat</b>: late in the match the god buys a nuclear ground launcher every
/// <see cref="GodLauncherBuySeconds"/> (at most <see cref="MaxGodLaunchers"/>), drives each one to
/// within reach of the player's nearest base and hands it to area fire, which knows how to raise a
/// ballistic launcher and steer a targetless round onto a point. Air Command keeps nuclear stores
/// out of every aircraft mission, which is why this is the ground launcher and not a bomber. The
/// god's launchers skip the depot's uranium gate
/// (<see cref="CommanderFactionVehicleService.FindSpawnWarheadStore"/>): its bases may hold no
/// warhead storage at all, and the warheads are the god's gift, not the economy's.
/// </para>
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    private const float WrathSeconds = 240f;
    private const int MaxGodLaunchers = 2;
    private const float GodLauncherBuySeconds = 300f;
    private const float NuclearAreaRadiusMeters = 400f;
    private const float LauncherApproachShare = 0.75f;
    private const int GodWarheadGift = 6;

    private static readonly List<VehicleDefinition> NuclearCandidates = new();
    private readonly List<Unit> godLaunchers = new();

    /// <summary>The god lost a base: send everything it has at it, now.</summary>
    internal static void UnleashWrath(FactionHQ hq, GlobalPosition target)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || hq == null || !hq.IsServer)
        {
            return;
        }

        CommanderState state = service.GetGodState(hq);
        state.WrathUntil = Time.time + WrathSeconds;
        state.WrathTarget = target;

        // The review clock is what tasks the wing; zeroing it runs the review on the next frame.
        service.nextReviewAt = 0f;
        CommanderPlugin.Log.LogInfo($"GOD DIFFICULTY: {hq.faction.name} unleashes its wrath on {CommanderNetwork.Position(target)}.");
    }

    /// <summary>The god's nuclear weapons are unlocked: warheads in store and launchers on order.</summary>
    internal static void ArmNuclear(FactionHQ hq)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || hq == null || !hq.IsServer)
        {
            return;
        }

        CommanderState state = service.GetGodState(hq);
        state.NuclearArmed = true;
        state.NextLauncherBuyAt = 0f;

        // Only lands where the god holds warhead storage; the launchers do not depend on it.
        hq.AddWarheadStockpile(GodWarheadGift);
        CommanderPlugin.Log.LogInfo($"GOD DIFFICULTY: {hq.faction.name} is armed with nuclear weapons.");
    }

    /// <summary>Whether this faction's nuclear launchers roll out with no uranium in store.</summary>
    internal static bool IsNuclearArmed(FactionHQ? hq)
    {
        return hq != null
            && Instance != null
            && Instance.states.TryGetValue(hq, out CommanderState state)
            && state.NuclearArmed;
    }

    private static float GetNuclearRange(Unit unit, bool loadedOnly)
    {
        float range = 0f;
        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            WeaponStation station = unit.weaponStations[i];
            if (station?.WeaponInfo != null && station.WeaponInfo.nuclear && (!loadedOnly || station.Ammo > 0))
            {
                range = Mathf.Max(range, station.WeaponInfo.targetRequirements.maxRange);
            }
        }

        return range;
    }

    /// <summary>The cheapest nuclear ground launcher the game has, trailers aside.</summary>
    private static VehicleDefinition? ChooseNuclearLauncher()
    {
        CommanderGameAccess.TryGetLocalVehicleDefinitions(NuclearCandidates);
        VehicleDefinition? best = null;
        for (int i = 0; i < NuclearCandidates.Count; i++)
        {
            VehicleDefinition candidate = NuclearCandidates[i];
            if (CommanderFactionVehicleService.GetWarheadCost(candidate) > 0
                && !CommanderGameAccess.IsTrailerVehicleDefinition(candidate)
                && (best == null || candidate.value < best.value))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>The player's base nearest the launcher: the one it reaches soonest.</summary>
    private static bool TryGetNuclearTarget(FactionHQ localHq, Unit launcher, out Airbase target)
    {
        target = null!;
        float best = float.MaxValue;
        GlobalPosition from = launcher.GlobalPosition();
        foreach (Airbase airbase in localHq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = FastMath.Distance(from, airbase.center.GlobalPosition());
            if (distance < best)
            {
                best = distance;
                target = airbase;
            }
        }

        return target != null;
    }

    private CommanderState GetGodState(FactionHQ hq)
    {
        if (!states.TryGetValue(hq, out CommanderState state))
        {
            state = new CommanderState();
            states[hq] = state;
        }

        return state;
    }

    /// <summary>10 s defence clock: order more launchers, and aim the ones on the field. The god's
    /// are free; any other commander buys one only when its uranium stockpile covers the warheads,
    /// and the depot's uranium gate charges them as for the player.</summary>
    private void ReviewGodNukes(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        if (hq.factionUnits == null)
        {
            return;
        }

        state.NuclearLaunchers.RemoveWhere(unit => unit == null || unit.disabled || unit.NetworkHQ != hq);
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && CommanderFactionVehicleService.GetWarheadCost(unit.definition) > 0)
            {
                state.NuclearLaunchers.Add(unit);
            }
        }

        godLaunchers.Clear();
        godLaunchers.AddRange(state.NuclearLaunchers);
        int loaded = 0;
        for (int i = 0; i < godLaunchers.Count; i++)
        {
            Unit launcher = godLaunchers[i];
            float range = GetNuclearRange(launcher, loadedOnly: true);
            if (range <= 0f)
            {
                // Fired its magazine: it stays where it is, pinned, a spent launcher.
                continue;
            }

            loaded++;
            if (CommanderAreaFire.IsAreaFiring(launcher) || !TryGetNuclearTarget(localHq, launcher, out Airbase target))
            {
                continue;
            }

            GlobalPosition aim = target.center.GlobalPosition();
            if (CommanderAreaFire.TryBeginForAi(launcher, aim, NuclearAreaRadiusMeters))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}): {CommanderGameAccess.GetUnitLabel(launcher)} raises its nuclear launcher against {CommanderCaptureService.GetAirbaseLabel(target)}.");
                if (state.NuclearArmed)
                {
                    CommanderGodService.NotifyNuclearLaunch(CommanderCaptureService.GetAirbaseLabel(target));
                }

                continue;
            }

            // Out of reach: drive to a spot three quarters of its range out from the target, on
            // the line back to where it stands.
            Vector3 back = launcher.GlobalPosition() - aim;
            back.y = 0f;
            if (back.sqrMagnitude < 1f)
            {
                continue;
            }

            Vector3 offset = back.normalized * (range * LauncherApproachShare);
            GlobalPosition spot = CommanderGameAccess.SnapToTerrain(
                new GlobalPosition(aim.x + offset.x, aim.y, aim.z + offset.z));
            HoldAt(launcher, spot);
        }

        // Cheap before the prefab walk: a commander without the god's gift needs uranium in store.
        if (Time.time < state.NextLauncherBuyAt || (!state.NuclearArmed && hq.GetWarheadStockpile() <= 0))
        {
            return;
        }

        state.NextLauncherBuyAt = Time.time + GodLauncherBuySeconds;
        VehicleDefinition? launcherType = ChooseNuclearLauncher();
        if (launcherType == null)
        {
            CommanderPlugin.Log.LogWarning("No nuclear ground launcher exists in this game, so no AI commander fields one.");
            return;
        }

        // Reserve launchers waiting at the depot draw on the same stockpile when they roll out.
        int waiting = hq.GetUnitSupply(launcherType);
        if (loaded + waiting >= MaxGodLaunchers
            || (!state.NuclearArmed
                && hq.GetWarheadStockpile() < CommanderFactionVehicleService.GetWarheadCost(launcherType) * (waiting + 1))
            || !CommanderGameAccess.TrySpend(hq, launcherType.value))
        {
            return;
        }

        hq.ModifyUnitSupply(launcherType, 1);
        CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) orders a {CommanderGameAccess.GetVehicleLabel(launcherType)}.");
    }
}
