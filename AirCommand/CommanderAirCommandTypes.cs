using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    internal enum AirCommandMode
    {
        AwacsJammer,
        Cas,
        AirGuard,
        Arad,
        StrategicStrike,

        /// <summary>The default. Works its box against anything it can hit, air or ground, with
        /// whatever it is carrying — no weapon-class filter and no target-class filter.</summary>
        Hybrid,

        /// <summary>No mission record at all: the airframe is handed to the base-game AI and flies
        /// itself. Every restraint this service applies is a mission, so having none is the
        /// feature.</summary>
        Autopilot,

        /// <summary>Spawns and holds on the deck it came out of until it is given something to
        /// do. Carried as a <see cref="LandingIntent.Hold"/> landing order.</summary>
        Standby,
    }

    internal enum LoadoutBalance
    {
        Primary,
        Mixed,
    }

    /// <summary>Why a commanded aircraft is being put on the deck at a named airbase.</summary>
    internal enum LandingIntent
    {
        /// <summary>Not landing anywhere in particular; the mission is flying.</summary>
        None,

        /// <summary>Land at a field the faction holds. The Basegame recovers the airframe.</summary>
        Resupply,

        /// <summary>Land inside a takeable base's ring and hold it until the base falls.</summary>
        Capture,

        /// <summary>Sit on the deck doing nothing until the player gives an order. Not serviced,
        /// grants no capture strength — <c>PilotParkedState</c> and the brakes, and that is all.</summary>
        Hold,

        /// <summary>Land at a field the faction holds and eject: the airframe goes back into stock.</summary>
        Reserve,
    }

    internal sealed class AirMissionOption
    {
        internal AirMissionOption(
            AircraftDefinition definition,
            AirCommandMode mode,
            HardpointSet[] hardpointSets,
            List<AirHardpointGroup> hardpointGroups)
        {
            Definition = definition;
            Mode = mode;
            HardpointSets = hardpointSets;
            HardpointGroups = hardpointGroups;
        }

        internal AircraftDefinition Definition { get; }

        internal HardpointSet[] HardpointSets { get; }

        internal List<AirHardpointGroup> HardpointGroups { get; }

        internal string LoadoutName => Preset?.Name ?? "Custom hardpoints";

        internal float Score => ScoreLoadout(BuildLoadout(), Mode, Definition);

        internal AirCommandMode Mode { get; }

        /// <summary>The preset the player clicked, if any. A preset is a finished set of pylons, so
        /// it bypasses the hardpoint groups entirely rather than trying to drive them.</summary>
        internal LoadoutPreset? Preset { get; set; }

        internal Loadout BuildLoadout()
        {
            if (Preset != null)
            {
                return Preset.Build(HardpointSets);
            }

            Loadout loadout = new();
            for (int i = 0; i < HardpointSets.Length; i++)
            {
                loadout.weapons.Add(null!);
            }

            for (int i = 0; i < HardpointGroups.Count; i++)
            {
                AirHardpointGroup group = HardpointGroups[i];
                WeaponMount? mount = group.SelectedMount;
                for (int index = 0; index < group.HardpointIndices.Count; index++)
                {
                    loadout.weapons[group.HardpointIndices[index]] = mount!;
                }
            }

            return loadout;
        }
    }

    internal sealed class AirHardpointGroup
    {
        private int selectedIndex = -1;

        internal AirHardpointGroup(string label, List<int> hardpointIndices, List<WeaponMount> mounts, int physicalMountCount)
        {
            Label = label;
            HardpointIndices = hardpointIndices;
            Mounts = mounts;
            PhysicalMountCount = physicalMountCount;
        }

        internal string Label { get; }

        internal List<int> HardpointIndices { get; }

        internal List<WeaponMount> Mounts { get; }

        internal int PhysicalMountCount { get; }

        internal WeaponMount? SelectedMount => selectedIndex >= 0 && selectedIndex < Mounts.Count ? Mounts[selectedIndex] : null;

        internal void Select(int index)
        {
            selectedIndex = index >= 0 && index < Mounts.Count ? index : -1;
        }

        internal void Clear()
        {
            selectedIndex = -1;
        }
    }

    internal sealed class AirbaseOption
    {
        internal AirbaseOption(Airbase airbase, string label, float distance, bool ready)
        {
            Airbase = airbase;
            Label = label;
            Distance = distance;
            Ready = ready;
        }

        internal Airbase Airbase { get; }

        internal string Label { get; }

        internal float Distance { get; }

        internal bool Ready { get; }
    }

    private sealed class PendingAreaSelection
    {
        internal PendingAreaSelection(AirMissionOption option, Airbase airbase)
        {
            Option = option;
            Airbase = airbase;
        }

        internal AirMissionOption Option { get; }

        internal Airbase Airbase { get; }
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(FactionHQ hq, AirMissionOption option, Airbase airbase, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack, bool purchasedWithFunds, float purchaseCost, float expiresAt, Loadout? loadout)
        {
            Loadout = loadout;
            Hq = hq;
            Airbase = airbase;
            Option = option;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpiresAt = expiresAt;
        }

        internal FactionHQ Hq { get; }

        internal AirMissionOption Option { get; }

        /// <summary>The field it launched from. A STANDBY spawn is put back down on it.</summary>
        internal Airbase Airbase { get; }

        internal GlobalPosition AreaCenter { get; }

        internal float Radius { get; }

        internal float TargetAltitude { get; }

        internal bool TargetOrdnance { get; }

        internal bool SaturationAttack { get; }

        internal bool PurchasedWithFunds { get; }

        internal float PurchaseCost { get; }

        internal float ExpiresAt { get; }

        /// <summary>Recovery duty to hand the airframe when it registers, or Off.</summary>
        internal CommanderRecoveryService.RecoveryMode Recovery { get; set; }

        /// <summary>The hardpoints the player picked, kept so a resupply can put them back.</summary>
        internal Loadout? Loadout { get; }
    }

    private sealed class AirMission
    {
        private GlobalPosition areaCenter;
        private Unit? forcedTarget;

        internal AirMission(FactionHQ hq, AirCommandMode mode, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack, bool purchasedWithFunds, float purchaseCost)
        {
            Hq = hq;
            Mode = mode;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
        }

        internal FactionHQ Hq { get; }

        internal AirCommandMode Mode { get; set; }

        /// <summary>
        /// The friendly unit this aircraft is escorting, if any. An escort's area is not a place
        /// on the map, it is wherever the escorted unit has got to, so <see cref="AreaCenter"/>
        /// reads it live rather than a sweep pushing a new centre in every few seconds — at three
        /// hundred metres a second a bomber outruns any tick rate cheap enough to run.
        /// </summary>
        internal Unit? EscortTarget { get; set; }

        internal GlobalPosition AreaCenter
        {
            get
            {
                if (EscortTarget != null && !EscortTarget.disabled)
                {
                    // Cached on the way past, so a shot-down escortee leaves the station where it
                    // fell instead of at wherever the order happened to be given.
                    areaCenter = EscortTarget.GlobalPosition();
                }

                return areaCenter;
            }
            set => areaCenter = value;
        }

        internal float Radius { get; set; }

        /// <summary>Cruise floor in metres, 0 for the base game's own choice. Settable in flight.</summary>
        internal float TargetAltitude { get; set; }

        /// <summary>Shoot incoming missiles as well as aircraft. Always on for an escort.</summary>
        internal bool TargetOrdnance { get; set; }

        internal bool SaturationAttack { get; }

        internal bool PurchasedWithFunds { get; }

        internal float PurchaseCost { get; }

        internal GameObject? MapVisual { get; set; }

        internal bool Returning { get; set; }

        internal bool RtbIssued { get; set; }

        /// <summary>The airbase this aircraft has been told to put itself down on, if any.</summary>
        internal Airbase? LandingBase { get; set; }

        internal LandingIntent Intent { get; set; }

        /// <summary>The pilot has been switched into the landing state for <see cref="LandingBase"/>.</summary>
        internal bool LandingIssued { get; set; }

        /// <summary>Stopped on the deck and held there, rather than left to the Basegame taxi state.</summary>
        internal bool Parked { get; set; }

        /// <summary>Capture strength this mission added to the airframe and still owes back.</summary>
        internal float GrantedCaptureStrength { get; set; }

        /// <summary>Travel points the aircraft flies through before settling on the mission area.</summary>
        internal List<GlobalPosition> Route { get; } = new();

        internal int RouteIndex { get; set; }

        /// <summary>PATROL: the route starts over at its first point instead of ending. Survives a
        /// resupply break-off, which swaps <see cref="Route"/> for the trip to the field.</summary>
        internal bool RouteLoop { get; set; }

        /// <summary>
        /// Explicitly commanded target; preferred over the automatic mission target search.
        /// Assigning one replaces the whole queue behind it, so every existing path that drops the
        /// attack (a landing order, an escort, bombs gone) drops the queued targets with it and
        /// none of them had to learn about the queue.
        /// </summary>
        internal Unit? ForcedTarget
        {
            get => forcedTarget;

            set
            {
                forcedTarget = value;
                QueuedTargets.Clear();
            }
        }

        /// <summary>Targets queued behind <see cref="ForcedTarget"/> by shift-clicking more of them,
        /// flown one after another and fired at together by the selection bar's FIRE button.</summary>
        internal List<Unit> QueuedTargets { get; } = new();

        /// <summary>Flies into known ground fire instead of around it: an enemy strike wave that
        /// has been committed to the attack, or a player aircraft given an attack order on
        /// something on the ground (it then also rearms once its bombs are gone).</summary>
        internal bool IgnoreGroundThreats { get; set; }

        /// <summary>Set when the aircraft is out of ammunition or short of fuel and wants a field.</summary>
        internal bool WantsResupply { get; set; }

        /// <summary>The player gave an order while the tank was low: the fuel check waits until that
        /// order is flown (see <c>HoldsLowFuelOrder</c>).</summary>
        internal bool IgnoreLowFuel { get; set; }

        /// <summary>An enemy airframe that has been seen carrying bombs; once they are gone it goes
        /// home to rearm.</summary>
        internal bool DropsBombs { get; set; }

        /// <summary>Time the aircraft on the deck finishes being serviced, 0 while it is not.</summary>
        internal float ResupplyReadyAt { get; set; }

        /// <summary>Why the depots could not service it this stay, reported once; empty while they can.</summary>
        internal string ShortfallReason { get; set; } = string.Empty;

        internal bool ReportedShortfall => ShortfallReason.Length > 0;

        /// <summary>
        /// The route, index and station the mission was flying before it broke off to resupply, put
        /// back the moment the airframe is armed again. Without this a resupplied aircraft launches
        /// with nothing to do and the base game's idle timer flies it straight home.
        /// </summary>
        internal List<GlobalPosition> ResumeRoute { get; } = new();

        internal int ResumeRouteIndex { get; set; }

        internal GlobalPosition ResumeAreaCenter { get; set; }

        internal bool HasResumeState { get; set; }

        /// <summary>
        /// How many times this airframe has been lifted clear of a takeoff or taxi the base-game AI
        /// gave up on. Capped, so an aeroplane that is genuinely wrecked is allowed to be lost
        /// rather than rescued forever.
        /// </summary>
        internal int GroundRescues { get; set; }

        internal bool NeedsGroundRescue { get; set; }

        /// <summary>When the base-game taxi to a service stand began after a resupply touchdown, 0 if not yet.</summary>
        internal float TaxiToStandSince { get; set; }

        /// <summary>When the taxi to a stand last stopped behind traffic (0: moving).</summary>
        internal float TaxiStoppedSince { get; set; }

        /// <summary>Where and when this airframe last moved on the ground, for <c>EjectStuckOnGround</c>.</summary>
        internal Vector3 GroundStillAt { get; set; }

        internal float GroundStillSince { get; set; }

        /// <summary>When servicing was first refused for want of stock (0: not grounded).</summary>
        internal float GroundedSince { get; set; }

        /// <summary>Distance to the current travel point at the last 2 s sweep, for the flying-away watch.</summary>
        internal float RouteDistance { get; set; }

        /// <summary>When the distance to the travel point started growing, -1 while it is not.</summary>
        internal float RecedingSince { get; set; } = -1f;

        internal bool RecedeLogged { get; set; }

        /// <summary>Steps to the next queued target, keeping the rest of the queue. False when the
        /// queue is empty, which is when the attack order is finished.</summary>
        internal bool TakeNextTarget()
        {
            while (QueuedTargets.Count > 0)
            {
                Unit next = QueuedTargets[0];
                QueuedTargets.RemoveAt(0);
                if (next != null && !next.disabled)
                {
                    forcedTarget = next;
                    return true;
                }
            }

            forcedTarget = null;
            return false;
        }

        /// <summary>Back to the first travel point once the last is passed, when patrolling a route
        /// of two or more and not on a landing order.</summary>
        internal bool TryWrapRoute()
        {
            if (!RouteLoop || LandingBase != null || Route.Count < 2 || RouteIndex < Route.Count)
            {
                return false;
            }

            RouteIndex = 0;
            return true;
        }
    }
}
