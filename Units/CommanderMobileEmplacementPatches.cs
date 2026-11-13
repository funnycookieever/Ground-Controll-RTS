using HarmonyLib;
using NuclearOption.NetworkTransforms;

namespace GroundControlRts;

internal static class CommanderMobileEmplacementPatches
{
    [HarmonyPatch(typeof(GroundVehicleNetworkTransform), nameof(GroundVehicleNetworkTransform.ShouldSend))]
    internal static class CommanderTrailerTransformPatch
    {
        private static void Postfix(GroundVehicleNetworkTransform __instance, ref bool __result)
        {
            // The normal sender skips kinematic bodies, including trailers moved by Tow.
            __result = __result || CommanderMobileEmplacementService.IsTowedTrailer(__instance.GroundVehicle);
        }
    }

    [HarmonyPatch(typeof(Turret), "FixedUpdate")]
    internal static class CommanderLauncherLiftPatch
    {
        internal static bool Prefix(Turret __instance)
        {
            return !CommanderMobileEmplacementService.IsLiftingLauncher(__instance);
        }
    }

    [HarmonyPatch(typeof(UnitCommand), nameof(UnitCommand.SetDestination))]
    internal static class CommanderMobileEmplacementDestinationPatch
    {
        private static bool Prefix(UnitCommand __instance)
        {
            return !CommanderMobileEmplacementService.ShouldBlockDestination(__instance)
                && !CommanderSamSiteService.ShouldBlockConstructionDestination(__instance);
        }
    }
}
