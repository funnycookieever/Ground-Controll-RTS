using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The air half of the enemy's expansion: an occasional airframe landed on a neutral base to take
/// it, and a helicopter that flies a builder to a base it has taken so that base can put up a depot.
/// </summary>
/// <remarks>
/// Both are deliberately small next to the attack on the player. One neutral capture landing is in
/// the air at a time and a new one is sent at most every <see cref="NeutralAirCaptureSeconds"/>;
/// one airlift runs at a time. The airlift is the sling lift the player uses
/// (<see cref="CommanderSlingLiftService.TryStartAiLift"/>), because nothing in the base game's AI
/// ever uses the hook. A captured base has no builder and no depot, and a builder driving there from
/// home down the convoy roads takes most of a match.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Least time between two neutral capture landings.</summary>
    private const float NeutralAirCaptureSeconds = 240f;

    /// <summary>Least time between two airlift helicopter launches, so one still taxiing out is not
    /// joined by another every review.</summary>
    private const float AirliftLaunchSeconds = 180f;

    /// <summary>Scaled seconds between two airlift helicopters launched for bases with no builder, so
    /// a builderless base does not wait on a helicopter busy elsewhere.</summary>
    private const float BaseAirliftLaunchSeconds = 60f;

    /// <summary>Scaled seconds a base waits on a builder landing run before another is sent: launch,
    /// flight and touchdown, with room for a run that was shot down.</summary>
    private const float BuilderRunSeconds = 360f;

    /// <summary>Tries on one stranded builder before it is left alone for
    /// <see cref="StrandedGiveUpSeconds"/>, so a builder that cannot be hooked does not tie up a
    /// helicopter.</summary>
    private const int StrandedTries = 3;
    private const float StrandedGiveUpSeconds = 600f;

    private static readonly List<Airbase> AirliftScratch = new();

    /// <summary>
    /// Once a review: when the timer is up and no capture landing of this commander's is already on
    /// a neutral base, land one idle airframe on the nearest neutral base it can use. A helicopter
    /// lands anywhere; a fixed-wing only where its takeoff roll fits, or it cannot leave again.
    /// </summary>
    private static void TrySendNeutralCaptureLanding(
        CommanderAirCommandService airCommand, FactionHQ hq, CommanderState state)
    {
        if (Time.time < state.NextNeutralAirCaptureAt)
        {
            return;
        }

        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            if (IsNeutralTarget(entry.Value) && airCommand.CountAiCaptureLandings(hq, entry.Value) > 0)
            {
                return;
            }
        }

        GlobalPosition home = CommanderCaptureService.GetTerritoryCenter(hq);
        Airbase? target = null;
        float best = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (!IsNeutralTarget(airbase))
            {
                continue;
            }

            float distance = FastMath.Distance(home, airbase.center.GlobalPosition());
            if (distance < best)
            {
                best = distance;
                target = airbase;
            }
        }

        if (target == null)
        {
            return;
        }

        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            // Never the picket, a transport, a fighter or the airlift helicopter: a capture lander is
            // whatever strike airframe can best be spared.
            bool rotary = CommanderAirCommandService.HasRotaryPilot(aircraft);
            if (IsCargoConfigured(aircraft)
                || CommanderSlingLiftService.GetHook(aircraft) != null
                || IsRadarPicket(aircraft.definition, hq)
                || GetAirRole(aircraft) == AirRole.Fighter
                || (!rotary && (aircraft.definition is not AircraftDefinition definition
                    || !CommanderAirCommandService.HasTakeoffRunway(target, definition))))
            {
                continue;
            }

            if (airCommand.TrySetAiCaptureLanding(aircraft, target))
            {
                state.NextNeutralAirCaptureAt = Time.time
                    + (IsChallenging(hq) ? ChallengingNeutralAirCaptureSeconds : NeutralAirCaptureSeconds);
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) sends {CommanderGameAccess.GetUnitLabel(aircraft)} "
                        + $"to land on the neutral {CommanderCaptureService.GetAirbaseLabel(target)} and capture it.");
                return;
            }
        }
    }

    private static bool IsNeutralTarget(Airbase? airbase)
    {
        return airbase != null
            && !CommanderSettings.DisableCapture
            && !airbase.disabled
            && airbase.center != null
            && airbase.CurrentHQ == null
            && airbase.SavedAirbase != null
            && airbase.SavedAirbase.Capturable
            && CommanderGameAccess.IsLandBase(airbase);
    }

    private static Loadout? BuildSlingLoadout(AircraftDefinition definition)
    {
        return CommanderAirCommandService.BuildSlingLoadout(definition);
    }

    /// <summary>Drops the base airlifts whose helicopter is gone or has finished its job.</summary>
    private static void PruneBaseAirlifts(CommanderState state, CommanderSlingLiftService sling)
    {
        if (state.BaseAirlifts.Count == 0)
        {
            return;
        }

        AirliftScratch.Clear();
        foreach (KeyValuePair<Airbase, Aircraft> entry in state.BaseAirlifts)
        {
            if (entry.Value == null || entry.Value.disabled || !sling.IsLifting(entry.Value))
            {
                AirliftScratch.Add(entry.Key);
            }
        }

        for (int i = 0; i < AirliftScratch.Count; i++)
        {
            state.BaseAirlifts.Remove(AirliftScratch[i]);
        }

        AirliftScratch.Clear();
    }

    /// <summary>A helicopter with a hook that is not on a lift.</summary>
    private static Aircraft? FindFreeHookHelo(FactionHQ hq, CommanderSlingLiftService sling)
    {
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (CommanderSlingLiftService.GetHook(aircraft) != null && !sling.IsLifting(aircraft))
            {
                return aircraft;
            }
        }

        return null;
    }

    /// <summary>A builder at the base, or one whose standby post is there and is out on a job, so a
    /// base whose builder is away is not sent another.</summary>
    private static bool HasBuilderNear(FactionHQ hq, GlobalPosition centre, float radius)
    {
        CommanderRepairService? repair = CommanderRepairService.Instance;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && CommanderRepairService.IsRepairUnit(unit)
                && (FastMath.InRange(unit.GlobalPosition(), centre, radius)
                    || (repair?.TryGetStandbyPost(unit, out GlobalPosition post) == true && FastMath.InRange(post, centre, radius))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDepotNear(FactionHQ hq, GlobalPosition centre, float radius)
    {
        // The faction's own depot list, not a scene-wide object search once per base per review.
        return hq.TryGetNearestDepot(centre.ToLocalPosition(), radius, out VehicleDepot depot)
            && depot != null
            && !depot.disabled;
    }

    /// <summary>A builder the hook can take: not slung, not on a construction job, nearest first.</summary>
    private static Unit? FindNearestFreeBuilder(FactionHQ hq, GlobalPosition from)
    {
        Unit? best = null;
        float bestDistance = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle
                || unit.disabled
                || unit.IsSlung()
                || !CommanderRepairService.IsRepairUnit(unit)
                || !unit.definition.CanSlingLoad
                || CommanderEconomyService.IsBuilder(unit)
                || IsUnderCover(unit)
                || CommanderSlingLiftService.Instance?.IsLoadOfAnyLift(unit) == true)
            {
                continue;
            }

            float distance = FastMath.Distance(unit.GlobalPosition(), from);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = unit;
            }
        }

        return best;
    }

    /// <summary>
    /// Anything standing more than 10 m over the vehicle within 10 m of it. An Ibis came down on
    /// a 30 m building beside a Jackknife, sat on the roof with the rope out of reach, and the next
    /// review sent it back to the same nearest builder three times running.
    /// </summary>
    private static bool IsUnderCover(Unit unit)
    {
        Vector3 at = unit.transform.position;
        return Physics.SphereCast(
                at + (Vector3.up * 150f),
                10f,
                Vector3.down,
                out RaycastHit hit,
                150f,
                PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore)
            && hit.point.y - at.y > 10f;
    }

    /// <summary>
    /// No builder on the ground anywhere: the helicopter brings one with it. Carrier Duel's commander
    /// starts on ship decks, so the builders it bought waited in a reserve no depot could roll out,
    /// its airlift Ibis had nothing to carry, and a captured base never built a thing. A builder
    /// already bought is taken out of the reserve; otherwise one is paid for here.
    /// </summary>
    private static void TryAirliftNewBuilder(
        FactionHQ hq, CommanderState state, CommanderSlingLiftService sling, Aircraft helo, Airbase destination, GlobalPosition drop)
    {
        VehicleDefinition? definition = CommanderGameAccess.FindRepairVehicleDefinition(hq);
        if (definition == null || !definition.CanSlingLoad)
        {
            return;
        }

        // One try per AirliftLaunchSeconds: a lift that keeps failing must not spend a builder a review.
        if (Time.time < state.NextSpawnLiftAt)
        {
            return;
        }

        bool fromReserve = hq.GetUnitSupply(definition) > 0;
        float cost = Mathf.Max(0f, definition.value);
        if (!fromReserve && hq.factionFunds < cost)
        {
            return;
        }

        // Paid when the helicopter takes the job: if it is lost before it settles and spawns the
        // builder, the builder's price goes with it.
        if (!sling.TryBeginAiSpawnLift(helo, definition, drop))
        {
            return;
        }

        state.NextSpawnLiftAt = Time.time + BaseAirliftLaunchSeconds;

        if (fromReserve)
        {
            hq.ModifyUnitSupply(definition, -1);
        }
        else
        {
            CommanderGameAccess.TrySpend(hq, cost);
        }

        state.BaseAirlifts[destination] = helo;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) airlifts a new {CommanderGameAccess.GetVehicleLabel(definition)} "
                + $"({(fromReserve ? "out of its reserve" : $"bought for {cost:0}")}) to "
                + $"{CommanderCaptureService.GetAirbaseLabel(destination)} with a {CommanderGameAccess.GetUnitLabel(helo)}.");
    }

    /// <summary>
    /// Once a review: a base this commander holds with neither a builder nor a vehicle depot inside
    /// the build radius gets a builder flown in. A helicopter with a hook that is not busy takes the
    /// nearest free builder; with none, one is launched from a field that has a hook-capable airframe.
    /// The builder is set down on the base's hold point, the lift makes it the base's standby builder
    /// (<c>SetHome</c>), and the economy ladder's per-base depot blueprint does the rest.
    /// </summary>
    private void ReviewAirlift(FactionHQ hq, CommanderState state)
    {
        CommanderSlingLiftService? sling = CommanderSlingLiftService.Instance;
        if (sling == null || hq.factionUnits == null)
        {
            return;
        }

        bool strandedBusy = state.AirliftHelo != null && !state.AirliftHelo.disabled && sling.IsLifting(state.AirliftHelo);
        if (!strandedBusy)
        {
            state.AirliftHelo = null;
        }

        PruneBaseAirlifts(state, sling);

        // Both jobs each review, each with its own helicopter. The base goes first, so a stranded
        // builder cannot take the helicopter launched for it; the stranded builder still has its own.
        ReviewBaseAirlift(hq, state, sling);

        // A paid blueprint whose builder cannot drive there, such as one on another island.
        if (strandedBusy
            || CommanderEconomyService.Instance?.TryGetStrandedBuilder(hq, out GlobalPosition drop, out Unit builder) != true
            || sling.IsLoadOfAnyLift(builder))
        {
            return;
        }

        state.StrandedTries.TryGetValue(builder, out (int Count, float Until) tries);
        if (tries.Count >= StrandedTries && Time.time < tries.Until)
        {
            return;
        }

        if (tries.Count >= StrandedTries)
        {
            tries = (0, 0f);
        }

        Aircraft? helo = FindFreeHookHelo(hq, sling);
        if (helo == null)
        {
            TryLaunchAirliftHelo(hq, state, null);
            return;
        }

        if (sling.TryStartAiLift(helo, builder, drop))
        {
            state.StrandedTries[builder] = (tries.Count + 1, Time.time + StrandedGiveUpSeconds);
            state.AirliftHelo = helo;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) airlifts a {CommanderGameAccess.GetUnitLabel(builder)} "
                    + $"to its blueprint with a {CommanderGameAccess.GetUnitLabel(helo)}.");
        }
    }

    /// <summary>A held land base with no builder and no depot in its build radius, and no lift on the
    /// way, gets the nearest free builder flown in, or a new one spawned under the helicopter.</summary>
    private void ReviewBaseAirlift(FactionHQ hq, CommanderState state, CommanderSlingLiftService sling)
    {
        Airbase? destination = null;
        float radius = CommanderSettings.BuildRadiusKm * 1000f;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null
                && airbase.center != null
                && CommanderGameAccess.IsLandBase(airbase)
                && !state.BaseAirlifts.ContainsKey(airbase)
                && !(state.BuilderRunUntil.TryGetValue(airbase, out float runUntil) && Time.time < runUntil)
                && !HasBuilderNear(hq, airbase.center.GlobalPosition(), radius)
                && !HasDepotNear(hq, airbase.center.GlobalPosition(), radius))
            {
                destination = airbase;
                break;
            }
        }

        if (destination == null)
        {
            return;
        }

        // A landing run first: a helicopter with a Jackknife in its bay touches down on the base's LZ
        // and unloads it. The sling lift is the fallback for a faction
        // with no such airframe, and the stranded builder's job.
        GlobalPosition drop = CommanderCaptureService.GetHoldPoint(destination);
        if (Time.time >= state.NextBuilderRunAt && CommanderSupplyHeliService.Instance is CommanderSupplyHeliService supply)
        {
            state.NextBuilderRunAt = Time.time + BaseAirliftLaunchSeconds;
            string? run = supply.LaunchAiBuilderRun(hq, destination, drop);
            if (run != null && run.StartsWith("launched", System.StringComparison.Ordinal))
            {
                state.BuilderRunUntil[destination] = Time.time + BuilderRunSeconds;
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) sends a builder to {CommanderCaptureService.GetAirbaseLabel(destination)} "
                        + $"on a landing run, {run}.");
                return;
            }

            if (run != null)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) could not send a builder run to {CommanderCaptureService.GetAirbaseLabel(destination)}: {run}");
            }
        }

        Aircraft? helo = FindFreeHookHelo(hq, sling);
        if (helo == null)
        {
            TryLaunchAirliftHelo(hq, state, destination);
            return;
        }

        Unit? builder = FindNearestFreeBuilder(hq, helo.GlobalPosition());
        if (builder == null)
        {
            TryAirliftNewBuilder(hq, state, sling, helo, destination, drop);
            return;
        }

        if (sling.TryStartAiLift(helo, builder, drop))
        {
            state.BaseAirlifts[destination] = helo;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) airlifts a {CommanderGameAccess.GetUnitLabel(builder)} "
                    + $"to {CommanderCaptureService.GetAirbaseLabel(destination)} with a {CommanderGameAccess.GetUnitLabel(helo)}.");
        }
    }

    /// <summary>A hook helicopter, off the builderless base itself when that field can fly one, so its
    /// builder drop there is near instant; else the field the roster picks.</summary>
    private void TryLaunchAirliftHelo(FactionHQ hq, CommanderState state, Airbase? destination)
    {
        if (Time.time < state.NextAirliftLaunchAt)
        {
            return;
        }

        foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
        {
            AircraftDefinition? definition = rosterEntry;
            if (definition == null
                || CommanderAirCommandService.HasPlanePilot(definition)
                || !CommanderAirCommandService.CanAiFly(definition)
                || BuildSlingLoadout(definition) is not Loadout loadout)
            {
                continue;
            }

            Airbase? field = destination != null && CanLaunchFrom(destination, hq, definition)
                ? destination
                : PickLaunchField(hq, definition);
            if (field != null && TryLaunchAirframe(hq, state, field, definition, loadout, "AIRLIFT, sling hook", out _))
            {
                state.NextAirliftLaunchAt = Time.time + (destination != null ? BaseAirliftLaunchSeconds : AirliftLaunchSeconds);
                return;
            }
        }
    }
}
