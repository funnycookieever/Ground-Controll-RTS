using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderEconomyPatches
{
    private static readonly AccessTools.FieldRef<FactionHQ, float> BonusIncome =
        AccessTools.FieldRefAccess<FactionHQ, float>("bonusIncome");

    /// <summary>Set while a per-base count is wanted: the faction's own sums and splits.</summary>
    private static int rawWarheadCount;

    /// <summary>
    /// A factory's production cadence is a <c>SlowUpdate</c> registered once with the interval it
    /// was authored with, so raising <c>productionInterval</c> later reschedules nothing. The only
    /// lever that moves is the batch size, so an upgraded factory tops up the reserve here, right
    /// after the Basegame added its single unit.
    /// </summary>
    /// <summary>
    /// Ground Control Draft has no production: what a side drafted is everything it gets. A mission
    /// factory (or one a map update adds) keeps its schedule and simply produces nothing.
    /// </summary>
    /// <remarks>
    /// A ship plant's hull goes to the NAVAL window's reserve instead: <c>FactionHQ.AddSupplyUnit</c>
    /// throws for a <c>ShipDefinition</c>, so the original would fail every cycle.
    /// </remarks>
    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    [HarmonyPrefix]
    internal static bool ProduceUnitPrefix(Factory __instance)
    {
        if (CommanderDraftService.IsDraftMission)
        {
            return false;
        }

        if (__instance.ProductionUnit is not ShipDefinition ship)
        {
            return true;
        }

        FactionHQ? hq = __instance.attachedUnit != null ? __instance.attachedUnit.NetworkHQ : null;
        MissionManager? mission = NetworkSceneSingleton<MissionManager>.i;
        if (hq != null && hq.IsServer && mission != null)
        {
            CommanderNavalPurchaseService.AddReserve(hq, ship, CommanderEconomyService.GetFactoryOutput(__instance.attachedUnit));
            __instance.NetworklastProductionTime = mission.MissionTime;
        }

        return false;
    }

    /// <summary>
    /// Keeps a pilot's donation in the treasury the commander spends from.
    /// </summary>
    /// <remarks>
    /// <c>Player.CmdDonateFactionFunds</c> pays through <c>FactionHQ.AddBonusFunds</c>, which adds to
    /// <c>factionFunds</c> and to the private <c>bonusIncome</c>, and the next <c>DistributeFunds</c>
    /// (every 30 s) hands the whole of <c>bonusIncome</c> back out to the faction's players. A solo
    /// commander got their donation straight back in their own wallet: "it was there, then gone".
    /// A donation is the only thing that ever feeds <c>bonusIncome</c>, so clearing it touches nothing else.
    /// </remarks>
    [HarmonyPatch(typeof(FactionHQ), "DistributeFunds")]
    [HarmonyPrefix]
    internal static void DistributeFundsPrefix(FactionHQ __instance)
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            BonusIncome(__instance) = 0f;
        }
    }

    [HarmonyPatch(typeof(Factory), "ProduceWarhead")]
    [HarmonyPrefix]
    internal static bool ProduceWarheadPrefix()
    {
        return !CommanderDraftService.IsDraftMission;
    }

    /// <summary>
    /// Enriched uranium is one faction-wide pool: every depot, airbase, carrier and rearm point sees
    /// all of it. The base game keeps it per base (a launcher or a jet could only use the bunker it
    /// stood beside), so a base's count reads as the faction's, except inside the faction's own sum
    /// and its split of new warheads between bunkers, which need the real per-base figure.
    /// </summary>
    [HarmonyPatch(typeof(Airbase), nameof(Airbase.GetWarheads))]
    [HarmonyPostfix]
    internal static void GetWarheadsPostfix(Airbase __instance, ref int __result)
    {
        if (rawWarheadCount > 0 || __instance.CurrentHQ == null)
        {
            return;
        }

        __result = __instance.CurrentHQ.GetWarheadStockpile();
    }

    /// <summary>
    /// A destroyed munitions bunker never leaves <c>Airbase.stores</c> (nothing calls
    /// <c>RemoveStorage</c>) and never comes back (<c>WarheadStorage.selfDisabled</c> is sticky, even
    /// through <c>Repair</c>). <c>AddWarheadStockpile</c> still counts it as storage, so a base whose
    /// bunkers are all dead scores lowest and wins every split, and <c>Airbase.AddWarheads</c> then
    /// falls back to <c>stores[0]</c> - the dead bunker - where <c>GetWarheads</c> never counts it.
    /// Every warhead after the first bunker kill vanished. Dropping dead stores first fixes both.
    /// </summary>
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AddWarheadStockpile))]
    [HarmonyPrefix]
    internal static void PruneDeadWarheadStoresPrefix(FactionHQ __instance)
    {
        foreach (Airbase airbase in __instance.GetAirbases())
        {
            if (airbase != null)
            {
                airbase.stores.RemoveAll(store => store == null || store.Disabled);
            }
        }
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.GetWarheadStockpile))]
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AddWarheadStockpile))]
    [HarmonyPrefix]
    internal static void RawWarheadCountPrefix()
    {
        rawWarheadCount++;
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.GetWarheadStockpile))]
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AddWarheadStockpile))]
    [HarmonyFinalizer]
    internal static void RawWarheadCountFinalizer()
    {
        rawWarheadCount--;
    }

    /// <summary>
    /// The rearm half of the shared pool: a nuclear round is loaded from any rearmer, a munitions
    /// truck in the field included. <c>Rearmer.ProcessRearmRequest</c> only counted warheads when the
    /// rearmer stood inside its nearest base, so a ballistic launcher that had fired never reloaded.
    /// That range test is the method's only <c>FastMath.InRange</c>.
    /// </summary>
    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.ProcessRearmRequest))]
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> AnyRearmerLoadsWarheads(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo inRange = AccessTools.Method(typeof(FastMath), nameof(FastMath.InRange), new[] { typeof(GlobalPosition), typeof(GlobalPosition), typeof(float) });
        MethodInfo always = AccessTools.Method(typeof(CommanderEconomyPatches), nameof(WarheadsInReach));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(inRange))
            {
                instruction.operand = always;
            }

            yield return instruction;
        }
    }

    /// <summary>
    /// The spending half of the shared pool: what this base's bunkers cannot cover is taken from
    /// the faction's other bases.
    /// </summary>
    [HarmonyPatch(typeof(Airbase), nameof(Airbase.RemoveWarheads))]
    [HarmonyPrefix]
    internal static bool RemoveWarheadsPrefix(Airbase __instance, int number)
    {
        FactionHQ? hq = __instance.CurrentHQ;
        if (rawWarheadCount > 0 || hq == null || number <= 0)
        {
            return true;
        }

        rawWarheadCount++;
        try
        {
            number -= TakeWarheads(__instance, number);
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (number <= 0)
                {
                    break;
                }

                if (airbase != null && !ReferenceEquals(airbase, __instance))
                {
                    number -= TakeWarheads(airbase, number);
                }
            }
        }
        finally
        {
            rawWarheadCount--;
        }

        return false;
    }

    /// <summary>
    /// The naval dock is built from the <c>VehicleDepot1</c> prefab, so its depot registers itself
    /// on spawn and again on repair or capture. On the faction's depot list it rolled ground reserve
    /// out onto the pier and answered every "is there a depot at this base" with yes.
    /// <c>CommanderEconomyService.SpawnNavalDock</c> takes back the first registration.
    /// </summary>
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AddDepot))]
    [HarmonyPrefix]
    internal static bool AddDepotPrefix(VehicleDepot depot)
    {
        return CommanderEconomyService.Instance?.IsBuiltNavalDock(CommanderGameAccess.GetDepotOwner(depot)) != true;
    }

    /// <summary>
    /// <c>Unit.unitName</c> is an initialOnly SyncVar: it travels in the spawn message and never
    /// again, so a rename after <c>Spawn</c> stays on the host and a guest's map hover reads the
    /// prefab. This is the last point before that message, and a guest needs no mod code for it.
    /// </summary>
    [HarmonyPatch(typeof(Building), "OnStartServer")]
    [HarmonyPrefix]
    internal static void BuildingStartServerPrefix(Building __instance)
    {
        string? name = CommanderEconomyService.GetSpawnName(__instance);
        if (name != null)
        {
            __instance.NetworkunitName = name;
        }
    }

    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    [HarmonyPostfix]
    internal static void ProduceUnitPostfix(Factory __instance)
    {
        // Harmony runs a postfix even when a prefix skipped the original.
        Unit attached = __instance.attachedUnit;
        if (attached == null
            || __instance.ProductionUnit == null
            || __instance.ProductionUnit is ShipDefinition
            || CommanderDraftService.IsDraftMission)
        {
            return;
        }

        int bonus = CommanderEconomyService.GetFactoryOutput(attached) - 1;
        FactionHQ hq = attached.NetworkHQ;

        // AddSupplyUnit is [Server]: it throws rather than no-ops on a pure client.
        if (bonus > 0 && hq != null && hq.IsServer)
        {
            hq.AddSupplyUnit(__instance.ProductionUnit, bonus);
        }
    }

    /// <summary>
    /// Only mission buildings can be repaired; a commander-built one is lost when it is destroyed
    /// and its wreck is cleared (<c>RemoveBuiltWrecks</c>). <c>Repairer</c> asks
    /// <c>NeedsRepair</c> before every <c>Repair</c> call and the mod's own target searches read
    /// it too; <c>FactionHQ.TryGetUnitNeedingRepair</c> reads <c>GetRepairPriority</c>, which
    /// checks the private field, so both are answered or a crew still drives there.
    /// </summary>
    [HarmonyPatch(typeof(Building), nameof(Building.NeedsRepair))]
    [HarmonyPostfix]
    internal static void NeedsRepairPostfix(Building __instance, ref bool __result)
    {
        if (__result && CommanderEconomyService.IsCommanderBuilt(__instance))
        {
            __result = false;
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.GetRepairPriority))]
    [HarmonyPostfix]
    internal static void GetRepairPriorityPostfix(Building __instance, ref float __result)
    {
        if (__result > 0f && CommanderEconomyService.IsCommanderBuilt(__instance))
        {
            __result = 0f;
        }
    }

    private static int TakeWarheads(Airbase airbase, int wanted)
    {
        int taken = UnityEngine.Mathf.Min(airbase.GetWarheads(), wanted);
        if (taken > 0)
        {
            airbase.RemoveWarheads(taken);
        }

        return taken;
    }

    private static bool WarheadsInReach(GlobalPosition _, GlobalPosition __, float ___)
    {
        return true;
    }
}
