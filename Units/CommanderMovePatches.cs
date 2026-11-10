using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.Networking;

namespace GroundControlRts;

internal static class CommanderMovePatches
{
    /// <summary>
    /// A towing tractor must not steer around the trailer hitched to its own tail, or it drives in
    /// circles trying to get clear of it.
    /// </summary>
    [HarmonyPatch(typeof(GroundVehicle), "UpdateObstacles")]
    internal static class CommanderTowObstaclePatch
    {
        internal static void Postfix(GroundVehicle __instance, List<Obstacle> ___obstacles)
        {
            ___obstacles.RemoveAll(obstacle => CommanderMobileEmplacementService.IsTowedBy(__instance, obstacle.Transform));
        }
    }

    [HarmonyPatch(typeof(UnitCommand), "ServerSetDestination")]
    internal static class CommanderMoveDestinationPatch
    {
        internal static void Postfix(UnitCommand __instance, GlobalPosition waypoint, Player player)
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive == true)
            {
                CommanderMoveService.NotifyPlayerDestination(__instance, waypoint, player);
            }
        }
    }

    /// <summary>
    /// Focus fire: once a unit has been given an explicit attack order, its turrets keep the
    /// commanded target as long as the Basegame assessment considers it engageable at all.
    /// </summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class CommanderFocusFirePatch
    {
        private const float ForcedPriority = 1e9f;

        private static readonly AccessTools.FieldRef<Turret, Unit> TurretTarget =
            AccessTools.FieldRefAccess<Turret, Unit>("target");

        private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit =
            AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

        private static readonly AccessTools.FieldRef<Turret, WeaponStation> TurretStation =
            AccessTools.FieldRefAccess<Turret, WeaponStation>("currentWeaponStation");

        /// <summary>
        /// Every ordered candidate is asked about from zero and the better of the two picks kept. A
        /// higher-scoring unordered contact seen earlier in the scan (a hangar, a helipad) would
        /// otherwise hold the threshold out of reach and the ordered target would never be taken;
        /// likewise a ship's first ordered target would block the rest of its list.
        /// </summary>
        internal static void Prefix(Turret __instance, Unit targetCandidate, ref float priorityThreshold, out Held __state)
        {
            __state = default;
            Unit shooter = TurretAttachedUnit(__instance);
            if (CommanderMoveService.Instance?.HasAttackOrders != true
                || (CommanderMoveService.GetShipTargetRank(shooter, targetCandidate) < 0
                    && !CommanderMoveService.IsOrderedTarget(shooter, targetCandidate)))
            {
                return;
            }

            __state = new Held
            {
                Active = true,
                Target = TurretTarget(__instance),
                Station = TurretStation(__instance),
                Threshold = priorityThreshold,
            };
            priorityThreshold = 0f;
        }

        internal static void Postfix(Turret __instance, Unit targetCandidate, ref float priorityThreshold, Held __state)
        {
            Unit shooter = TurretAttachedUnit(__instance);
            int rank = CommanderMoveService.Instance?.HasAttackOrders == true
                ? CommanderMoveService.GetShipTargetRank(shooter, targetCandidate)
                : -1;
            if (rank >= 0)
            {
                // Fewest turrets already on it first, then the order the player clicked them in, so
                // a ship's (and a flotilla's) turrets spread over the list instead of piling on one.
                float score = -1f;
                if (ReferenceEquals(TurretTarget(__instance), targetCandidate))
                {
                    TrackingInfo? info = shooter.NetworkHQ.GetTrackingData(targetCandidate.persistentID);
                    score = ForcedPriority - ((info?.attackers ?? 0) * 1e6f) - (rank * 1e3f);
                }

                if (__state.Active && score <= __state.Threshold)
                {
                    TurretTarget(__instance) = __state.Target;
                    TurretStation(__instance) = __state.Station;
                    priorityThreshold = __state.Threshold;
                }
                else if (score > 0f)
                {
                    priorityThreshold = score;
                }

                return;
            }

            if (CommanderMoveService.Instance?.HasAttackOrders != true
                || !CommanderMoveService.IsOrderedTarget(shooter, targetCandidate))
            {
                return;
            }

            if (!ReferenceEquals(TurretTarget(__instance), targetCandidate))
            {
                // Not engageable even from zero: the pick from before this candidate stands.
                if (__state.Active)
                {
                    TurretTarget(__instance) = __state.Target;
                    TurretStation(__instance) = __state.Station;
                    priorityThreshold = __state.Threshold;
                }

                return;
            }

            // The candidate was just accepted by the Basegame scoring; lock it in so no later
            // candidate in this scan outbids the commanded target.
            priorityThreshold = ForcedPriority;
        }

        /// <summary>The pick a ship's turret held before this candidate, put back if it stays better.</summary>
        internal struct Held
        {
            internal bool Active;
            internal Unit Target;
            internal WeaponStation Station;
            internal float Threshold;
        }
    }

    /// <summary>
    /// Hold Fire: a unit told to stay dark rejects every target candidate, so its turrets acquire
    /// nothing and never open up. Radar-guided batteries fed by a FireControl still need the radar
    /// switched off as well, which the unit systems window does.
    /// </summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class CommanderHoldFirePatch
    {
        private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit =
            AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

        private static readonly HashSet<string> Logged = new();

        internal static bool Prefix(Turret __instance)
        {
            if (CommanderMoveService.Instance?.HasHoldFireUnits != true
                || !CommanderMoveService.IsHoldingFire(TurretAttachedUnit(__instance)))
            {
                return true;
            }

            LogOnce(TurretAttachedUnit(__instance), "turret");
            return false;
        }

        /// <summary>One line per unit and path, so a log shows which way a held unit was stopped.</summary>
        internal static void LogOnce(Unit unit, string path)
        {
            string key = unit.persistentID + path;
            if (Logged.Add(key))
            {
                CommanderPlugin.Log.LogInfo($"HOLD FIRE: {unit.unitName} kept its {path} quiet.");
            }
        }
    }

    /// <summary>Missiles only: an IR SAM set to it refuses every candidate that is not a missile.</summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class CommanderMissileOnlyPatch
    {
        private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit =
            AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

        internal static bool Prefix(Turret __instance, Unit targetCandidate)
        {
            return targetCandidate is Missile
                || !CommanderMoveService.IsMissileOnly(TurretAttachedUnit(__instance));
        }
    }

    /// <summary>
    /// Hold Fire, radar batteries: a <c>FireControl</c> with a radar hands its turrets targets
    /// through <c>SetTargetFromController</c>, never through <c>AssessTargetPriority</c>. A null
    /// target also stands down a battery that was already shooting.
    /// </summary>
    [HarmonyPatch(typeof(Turret), nameof(Turret.SetTargetFromController))]
    internal static class CommanderHoldFireControllerPatch
    {
        private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit =
            AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

        internal static void Prefix(Turret __instance, ref Unit target)
        {
            if (CommanderMoveService.Instance?.HasHoldFireUnits == true
                && CommanderMoveService.IsHoldingFire(TurretAttachedUnit(__instance)))
            {
                if (target != null)
                {
                    CommanderHoldFirePatch.LogOnce(TurretAttachedUnit(__instance), "radar battery");
                }

                target = null!;
            }
        }
    }

    /// <summary>
    /// Hold Fire, datalink salvos: a ship's missile cells are fired by <c>FireControl</c>'s own
    /// salvo planner (<c>QueuedAttack.Fire</c> calls <c>WeaponStation.Fire</c>), with no turret in
    /// the way, which is how a destroyer on HOLD FIRE still launched. Skipping each queued shot also
    /// catches a salvo already under way; the attacker count is released the way <c>Cancel</c> does.
    /// </summary>
    [HarmonyPatch]
    internal static class CommanderHoldFireSalvoPatch
    {
        internal static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.Inner(typeof(FireControl), "QueuedAttack"), "Fire");
        }

        internal static bool Prefix(Unit attachedUnit, TrackingInfo ___trackingInfo)
        {
            if (CommanderMoveService.Instance?.HasHoldFireUnits != true
                || !CommanderMoveService.IsHoldingFire(attachedUnit))
            {
                return true;
            }

            CommanderHoldFirePatch.LogOnce(attachedUnit, "missile salvo");
            ___trackingInfo.attackers--;
            return false;
        }
    }
}
