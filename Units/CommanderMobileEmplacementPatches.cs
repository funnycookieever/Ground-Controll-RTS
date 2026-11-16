using HarmonyLib;

namespace GroundControlRts;

internal static class CommanderMobileEmplacementPatches
{
    [HarmonyPatch(typeof(Turret), "StowForward")]
    internal static class CommanderTrailerStowPatch
    {
        internal static bool Prefix(Turret __instance, float ___minElevation, float ___maxElevation)
        {
            // A fixed-elevation launcher cannot aim back up after StowForward zeros its lift.
            // Keep its authored pose while SetStowed still blocks firing during transport.
            return ___minElevation != ___maxElevation
                || CommanderMobileEmplacementService.Instance?.IsInTowJob(__instance.GetAttachedUnit(), out _) != true;
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
