using HarmonyLib;

namespace GroundControlRts;

internal static class CommanderAreaFirePatches
{
    [HarmonyPatch(typeof(Turret), "FixedUpdate")]
    internal static class FixedUpdatePatch
    {
        internal static bool Prefix(Turret __instance)
        {
            return CommanderAreaFire.FixedUpdatePrefix(__instance);
        }
    }

    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class FilterPatch
    {
        internal static bool Prefix(Turret __instance, Unit targetCandidate)
        {
            return CommanderAreaFire.FilterPrefix(__instance, targetCandidate);
        }
    }

    [HarmonyPatch(typeof(InertialSeekerShell), nameof(InertialSeekerShell.Initialize))]
    internal static class InertialInitializePatch
    {
        internal static void Postfix(InertialSeekerShell __instance, Unit target)
        {
            CommanderAreaFire.InertialInitializePostfix(__instance, target);
        }
    }

    [HarmonyPatch(typeof(BallisticMissileGuidance), nameof(BallisticMissileGuidance.Initialize))]
    internal static class BallisticInitializePatch
    {
        internal static void Postfix(BallisticMissileGuidance __instance)
        {
            CommanderEnemyCommanderService.Instance?.NotePiledriverLaunch(__instance);
            CommanderAreaFire.BallisticInitializePostfix(__instance);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.InterceptPriority))]
    internal static class InterceptPriorityPatch
    {
        internal static void Postfix(Missile __instance, Unit toUnit, float minRange, ref float __result)
        {
            CommanderAreaFire.InterceptPriorityPostfix(__instance, toUnit, minRange, ref __result);
        }
    }

    [HarmonyPatch(typeof(FireControl), nameof(FireControl.DeployOrStowLaunchers))]
    internal static class DeployOrStowPatch
    {
        internal static bool Prefix(FireControl __instance, bool deploying)
        {
            return CommanderAreaFire.DeployOrStowPrefix(__instance, deploying);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.Detonate), new[] { typeof(UnityEngine.Vector3), typeof(bool), typeof(bool) })]
    internal static class MissileDetonatePatch
    {
        internal static void Postfix(Missile __instance)
        {
            CommanderAreaFire.MissileDetonatePostfix(__instance);
        }
    }

    [HarmonyPatch(typeof(InertialSeekerShell), "UpdateTargetPosition")]
    internal static class InertialUpdateTargetPatch
    {
        internal static bool Prefix(InertialSeekerShell __instance)
        {
            return CommanderAreaFire.InertialUpdateTargetPrefix(__instance);
        }
    }

    [HarmonyPatch(typeof(InertialSeekerShell), "SlowChecks")]
    internal static class InertialSlowChecksPatch
    {
        internal static System.Exception? Finalizer(InertialSeekerShell __instance, System.Exception? __exception)
        {
            return CommanderAreaFire.InertialSlowChecksFinalizer(__instance, __exception);
        }
    }
}
