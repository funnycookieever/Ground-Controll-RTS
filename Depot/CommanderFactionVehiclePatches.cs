using System;
using System.Collections.Generic;
using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderFactionVehiclePatches
{
    /// <summary>Another faction's depots already logged as refusing a spawn for supply. Its refusals
    /// put nothing on screen, and a base that never fields anything reads as the AI ignoring it.</summary>
    private static readonly HashSet<VehicleDepot> LoggedRefusals = new();

    /// <summary>The depot inside <c>TrySpawnVehicle</c>, for the <c>MoveFromDepot</c> it calls.</summary>
    private static VehicleDepot? spawningDepot;

    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyPrefix]
    private static void DeployVehiclesPrefix(FactionHQ __instance)
    {
        CommanderFactionVehicleService.AutomaticDeploymentHq = __instance;
        CommanderEnemyCommanderService.Instance?.OrderDepotsForGuard(__instance);
    }

    /// <summary>
    /// The mission's free air force: a random airframe out of the reserve every tick, up to
    /// <c>AIAircraftLimit</c>. With friendly AI off the local side launches only what the commander does.
    /// On GC missions <see cref="CommanderFreeAiPilots"/> flies them instead, free of stock, and
    /// friendly AI off does not stop them: the lobby slider is the switch for those.
    /// </summary>
    [HarmonyPatch(typeof(FactionHQ), "DeployAIAircraft")]
    [HarmonyPrefix]
    private static bool DeployAIAircraftPrefix(FactionHQ __instance)
    {
        if ((!CommanderEnemyCommanderService.IsDuelMission && CommanderFactionVehicleService.IsFriendlyAiOff(__instance))
            || CommanderUnitCap.IsFull(__instance, tellPlayer: false)
            || CommanderUnitCap.IsAircraftFull(__instance))
        {
            CommanderFreeAiPilots.LogBlocked(__instance);
            return false;
        }

        if (!CommanderEnemyCommanderService.IsDuelMission)
        {
            return true;
        }

        CommanderFreeAiPilots.Deploy(__instance);
        return false;
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.ReportKilled))]
    [HarmonyPostfix]
    private static void ReportKilledPostfix(Unit __instance, Dictionary<PersistentID, float>? ___damageCredit)
    {
        CommanderFreeAiPilots.NotifyKilled(__instance, ___damageCredit);
    }

    [HarmonyPatch(typeof(FactionHQ), "DistributeFunds")]
    [HarmonyPostfix]
    private static void DistributeFundsPostfix(FactionHQ __instance)
    {
        CommanderFreeAiPilots.NotifyFundsDistributed(__instance);
    }

    [HarmonyPatch(typeof(Unit), "OnStartServer")]
    [HarmonyPrefix]
    private static void UnitStartServerPrefix(Unit __instance)
    {
        CommanderFreeAiPilots.NotifyFactionUnitRegistered(__instance.NetworkHQ, __instance);
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RegisterFactionUnit))]
    [HarmonyPostfix]
    private static void RegisterFactionUnitPostfix(FactionHQ __instance, Unit unit)
    {
        CommanderFreeAiPilots.NotifyFactionUnitRegistered(__instance, unit);
    }

    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyPostfix]
    private static void DeployVehiclesPostfix()
    {
        CommanderFactionVehicleService.AutomaticDeploymentHq = null;
    }

    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyFinalizer]
    private static Exception? DeployVehiclesFinalizer(Exception? __exception)
    {
        CommanderFactionVehicleService.AutomaticDeploymentHq = null;
        return __exception;
    }

    /// <summary>
    /// The one door every ground unit walks out of, for both commanders: the player's depot queue,
    /// the factory reserve the game deploys on its own, and the enemy commander's convoys. So it is
    /// where a spawn is held back, and where it pays for its fuel and ordnance.
    /// </summary>
    [HarmonyPatch(typeof(VehicleDepot), nameof(VehicleDepot.TrySpawnVehicle))]
    [HarmonyPrefix]
    private static bool TrySpawnVehiclePrefix(VehicleDepot __instance, VehicleDefinition vehicleDefinition, ref bool __result)
    {
        // First, before anything below spends fuel or a warhead on a unit that may not come out.
        if (CommanderUnitBanService.Holds(vehicleDefinition))
        {
            __result = false;
            return false;
        }

        // The AI commander's routing (artillery, SAM and site trucks to its chosen depot, assault off
        // islands) is for its own reserve roll-out. A player's queue on an AI-commanded side (AI
        // COMMANDERS: BOTH) calls straight in, and held there it waited at the head for ever.
        bool automatic = CommanderFactionVehicleService.AutomaticDeploymentHq != null;
        if (automatic
            && (CommanderEnemyCommanderService.HoldsArtilleryAt(__instance!, vehicleDefinition)
                || CommanderEnemyCommanderService.HoldsSamLauncherAt(__instance!, vehicleDefinition)
                || CommanderEnemyCommanderService.HoldsSiteTruckAt(__instance!, vehicleDefinition)
                || CommanderEnemyCommanderService.HoldsAssaultAtIsland(__instance!, vehicleDefinition)))
        {
            __result = false;
            return false;
        }

        // The queue checks the tier, but the game deploys reserve units through here on its own.
        // The AI buys by its best depot, so a low depot refusing here lets DeployVehicles offer the
        // unit to the next depot in turn, until the one at the right tier takes it.
        if (CommanderEconomyService.Instance != null
            && !CommanderDraftService.IsDraftMission
            && CommanderEconomyService.GetDepotLevel(CommanderGameAccess.GetDepotOwner(__instance))
                < CommanderEconomyService.GetRequiredDepotLevel(vehicleDefinition))
        {
            __result = false;
            return false;
        }

        CommanderDepotLane.Clear(__instance!);

        CommanderFactionVehicleService? service = CommanderFactionVehicleService.Instance;
        if (service != null && service.ShouldBlockAutomaticDeployment(__instance, vehicleDefinition))
        {
            __result = false;
            return false;
        }

        FactionHQ? hq = __instance == null ? null : __instance.NetworkHQ;
        if (CommanderUnitCap.IsFull(hq, tellPlayer: CommanderFactionVehicleService.AutomaticDeploymentHq == null)
            || CommanderUnitCap.IsKindFull(hq, vehicleDefinition, tellPlayer: CommanderFactionVehicleService.AutomaticDeploymentHq == null))
        {
            __result = false;
            return false;
        }

        // An AI side leaves a third of the cap for its aircraft and ships, and fields no more than
        // 60% of it in one vehicle. Either way the vehicle waits in the reserve.
        if (automatic
            && ((CommanderUnitCap.IsAiSide(hq) && CommanderUnitCap.IsGroundFull(hq))
                || CommanderUnitCap.IsAiTypeFull(hq, vehicleDefinition)))
        {
            __result = false;
            return false;
        }

        // Found before anything is spent, taken after everything else is: a nuclear launcher that
        // failed the fuel check after its warhead had already left the bunker would burn the
        // faction's uranium on a unit that never rolled out.
        Airbase? warheadStore = CommanderFactionVehicleService.FindSpawnWarheadStore(
            __instance, vehicleDefinition, out string nuclearShortfall);
        if (nuclearShortfall.Length > 0)
        {
            CommanderSpawnService.Instance?.ReportSpawnSupplyShortfall(hq, vehicleDefinition, nuclearShortfall);
            __result = false;
            return false;
        }

        if (CommanderSupplyStoreService.Instance?.TrySpendSpawnSupply(
            hq, __instance!.GlobalPosition(), vehicleDefinition, out string reason) == false)
        {
            CommanderSpawnService.Instance?.ReportSpawnSupplyShortfall(hq, vehicleDefinition, reason);
            if (hq != null && !ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()) && LoggedRefusals.Add(__instance!))
            {
                CommanderPlugin.Log.LogInfo(
                    $"{hq.faction.factionName}'s {__instance!.name} cannot deploy: {reason}.");
            }

            __result = false;
            return false;
        }

        warheadStore?.RemoveWarheads(CommanderFactionVehicleService.GetWarheadCost(vehicleDefinition));
        spawningDepot = __instance;
        return true;
    }

    /// <summary>
    /// A trailer cannot drive out of the door, so it sat in the lane until the stuck sweep moved it
    /// to the side. It goes straight to the verge instead.
    /// </summary>
    [HarmonyPatch(typeof(GroundVehicle), nameof(GroundVehicle.MoveFromDepot))]
    [HarmonyPostfix]
    private static void MoveFromDepotPostfix(GroundVehicle __instance)
    {
        if (spawningDepot != null && CommanderGameAccess.IsTrailerVehicleDefinition(__instance.definition as VehicleDefinition))
        {
            CommanderDepotLane.ParkTrailer(spawningDepot, __instance);
        }
    }

    /// <summary>
    /// The player's factory output, rolled out by <c>DeployVehicles</c>, joins its depot's staging
    /// block, rally point or AUTO ATTACK like a vehicle bought there. Left to the base game it
    /// chased the nearest tracked enemy (see <c>CommanderSpawnService.AdoptReserveSpawn</c>).
    /// </summary>
    [HarmonyPatch(typeof(VehicleDepot), nameof(VehicleDepot.TrySpawnVehicle))]
    [HarmonyPostfix]
    private static void TrySpawnVehiclePostfix(VehicleDepot __instance, VehicleDefinition vehicleDefinition, bool __result)
    {
        spawningDepot = null;
        FactionHQ? deploying = CommanderFactionVehicleService.AutomaticDeploymentHq;

        // Not in a both-sides match: there "the local team" is each AI in turn.
        if (__result && deploying != null && !CommanderEnemyCommanderService.LocalSideRotates && ReferenceEquals(deploying, CommanderGameAccess.GetLocalHq()))
        {
            CommanderSpawnService.Instance?.AdoptReserveSpawn(__instance, vehicleDefinition);
        }
    }

    [HarmonyPatch(typeof(Factory), "set_NetworkproductionUnit")]
    [HarmonyPostfix]
    private static void ProductionUnitChangedPostfix(Factory __instance)
    {
        CommanderSpawnService.NotifyFactoryChanged(__instance);
    }
}
