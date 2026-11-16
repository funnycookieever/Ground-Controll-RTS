using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Moves a shared radar to its new owner's list when its unit changes hands. A <c>Radar</c> calls
/// <c>FactionHQ.RegisterRadar</c> once, on initialize, and nothing moves it afterwards, so a
/// captured radar station went on serving the faction that lost it: the new owner's StratoLances
/// (whose <c>Turret.SearchForRadar</c> asks its own HQ's list for a radar within 300 m) stood
/// beside it and never fired, while the old owner's launchers could still use it.
/// </summary>
[HarmonyPatch(typeof(Unit), nameof(Unit.HQChanged))]
internal static class CommanderRadarOwnerPatches
{
    private static readonly AccessTools.FieldRef<TargetDetector, bool> Shared = AccessTools.FieldRefAccess<TargetDetector, bool>("shared");
    private static readonly AccessTools.FieldRef<TargetDetector, bool> Disabled = AccessTools.FieldRefAccess<TargetDetector, bool>("disabled");
    private static readonly FieldInfo FireControlMode = AccessTools.Field(typeof(FireControl), "targetAcquisitionMode");
    private static readonly FieldInfo TurretMode = AccessTools.Field(typeof(Turret), "targetAcquisitionMode");
    private static readonly Action<FireControl, Radar> RegisterRadar =
        AccessTools.MethodDelegate<Action<FireControl, Radar>>(AccessTools.Method(typeof(FireControl), "RegisterRadar"));

    private static readonly MethodInfo RadarDisabled = AccessTools.Method(typeof(FireControl), "FireControl_OnRadarDisabled");
    private static readonly Action<Turret, TargetDetector> DeregisterDetector =
        AccessTools.MethodDelegate<Action<Turret, TargetDetector>>(AccessTools.Method(typeof(Turret), "DeregisterTargetDetector"));

    [HarmonyPostfix]
    internal static void Postfix(Unit __instance, FactionHQ oldHQ, FactionHQ newHQ)
    {
        if (__instance == null || __instance.disabled || ReferenceEquals(oldHQ, newHQ))
        {
            return;
        }

        Radar[] radars = __instance.GetComponentsInChildren<Radar>(true);
        for (int i = 0; i < radars.Length; i++)
        {
            Radar radar = radars[i];
            if (radar == null || !Shared(radar) || Disabled(radar))
            {
                continue;
            }

            oldHQ?.DeregisterRadar(radar);

            // Deregister first so a second hook call on the same owner never lists it twice.
            newHQ?.DeregisterRadar(radar);
            newHQ?.RegisterRadar(radar);
        }
    }

    [HarmonyPatch(typeof(UnitRegistry), nameof(UnitRegistry.RegisterUnit))]
    internal static class RadarUnitRegisteredPatch
    {
        [HarmonyPostfix]
        internal static void Postfix(Unit unit)
        {
            // FactionHQ.onRegisterUnit is server-only; UnitRegistry also registers guest spawns.
            CommanderRadarService.Instance?.OnRegisterUnit(unit);
        }
    }

    [HarmonyPatch(typeof(Turret), "Turret_OnCompleteScan")]
    internal static class TurretRadarRangePatch
    {
        [HarmonyPrefix]
        internal static bool Prefix(Turret __instance, Unit ___attachedUnit, List<TargetDetector> ___targetDetectors)
        {
            if (!___attachedUnit.IsServer || TurretMode.GetValue(__instance)?.ToString() != "searchForRadar")
            {
                return true;
            }

            foreach (TargetDetector detector in ___targetDetectors)
            {
                if (detector is Radar radar
                    && (radar.GetAttachedUnit().NetworkHQ != ___attachedUnit.NetworkHQ
                        || !FastMath.InRange(___attachedUnit.GlobalPosition(), radar.transform.GlobalPosition(), 300f)))
                {
                    foreach (TargetDetector linked in ___targetDetectors)
                    {
                        DeregisterDetector(__instance, linked);
                    }

                    __instance.SetTargetFromController(null);
                    __instance.FireControlDisabled();
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(FireControl), nameof(FireControl.TryGetRadar))]
    internal static class FireControlRadarPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(FireControl __instance, Unit ___attachedUnit, ref Radar? ___radar, List<Turret> ___turrets)
        {
            if (!___attachedUnit.IsServer || ___attachedUnit.NetworkHQ == null
                || FireControlMode.GetValue(__instance)?.ToString() != "searchForRadar")
            {
                return;
            }

            // Native acquisition stops searching after its first link; moving either unit must
            // invalidate that cached link and use the same native 300 m lookup again.
            ___attachedUnit.NetworkHQ.TryGetRadar(___attachedUnit.GlobalPosition(), 300f, out Radar nearest);
            if (___radar == nearest)
            {
                return;
            }

            if (___radar != null)
            {
                ___radar.GetAttachedUnit().onDisableUnit -= AccessTools.MethodDelegate<Action<Unit>>(RadarDisabled, __instance);
            }

            ___radar = null;
            if (nearest != null)
            {
                RegisterRadar(__instance, nearest);
            }
            else
            {
                foreach (Turret turret in ___turrets)
                {
                    turret.GetAttachedUnit().radar = null;
                    turret.SetTargetFromController(null);
                }
            }
        }
    }

    [HarmonyPatch(typeof(FireControl), "DistributeTurretTargets")]
    internal static class FireControlTargetsPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(FireControl __instance, Unit ___attachedUnit, List<Turret> ___turrets)
        {
            if (!___attachedUnit.IsServer)
            {
                return;
            }

            __instance.TryGetRadar(out _);
            for (int i = ___turrets.Count - 1; i >= 0; i--)
            {
                Turret turret = ___turrets[i];
                Unit launcher = turret.GetAttachedUnit();
                if (launcher.NetworkHQ != ___attachedUnit.NetworkHQ
                    || !FastMath.InRange(launcher.GlobalPosition(), ___attachedUnit.GlobalPosition(), 300f))
                {
                    __instance.DeregisterTurret(turret);
                    turret.SetTargetFromController(null);
                    turret.FireControlDisabled();
                }
            }
        }
    }
}
