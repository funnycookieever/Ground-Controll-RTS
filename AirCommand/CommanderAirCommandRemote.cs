using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A guest's Air Command, flown on the host. Every mission lives in the host's dictionary because
/// the host is the only machine whose AI pilots the flight patches can steer, so a guest's order
/// is sent here and applied through the same method the host's own click calls. What the guest's
/// windows show is the host's missions for that guest's faction, copied back every two seconds.
/// See <see cref="CommanderNetwork"/>.
/// </summary>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// The guest's launch settings while the host applies one of its orders, so an aircraft a guest
    /// adopts uses the guest's choices rather than whatever the host has selected. Altitude still
    /// travels for older peers but is unused: an adopted aircraft starts at STANDARD.
    /// </summary>
    private (float Altitude, bool Ordnance, bool Saturation)? remoteDefaults;

    private bool OrderTargetOrdnance => remoteDefaults?.Ordnance ?? TargetOrdnance;

    private bool OrderSaturationAttack => remoteDefaults?.Saturation ?? SaturationAttack;

    private static AirCommandMode ReadMode(string[] args, int index)
    {
        AirCommandMode mode = (AirCommandMode)CommanderNetwork.Int(args, index);
        return Enum.IsDefined(typeof(AirCommandMode), mode) ? mode : AirCommandMode.Hybrid;
    }

    /// <summary>By jsonKey; falls back to every loaded airframe, which is where modded aircraft are.</summary>
    private static AircraftDefinition? FindAircraftDefinition(string key)
    {
        AircraftDefinition? definition = CommanderNetwork.Definition<AircraftDefinition>(key);
        if (definition != null || key.Length == 0)
        {
            return definition;
        }

        AircraftDefinition[] loaded = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        for (int i = 0; i < loaded.Length; i++)
        {
            if (loaded[i] != null && loaded[i].jsonKey == key)
            {
                return loaded[i];
            }
        }

        return null;
    }

    private static WeaponMount? FindMount(HardpointSet set, string key)
    {
        if (key.Length == 0 || set.weaponOptions == null)
        {
            return null;
        }

        for (int i = 0; i < set.weaponOptions.Count; i++)
        {
            WeaponMount? mount = set.weaponOptions[i];
            if (mount != null && mount.jsonKey == key)
            {
                return mount;
            }
        }

        return null;
    }

    private static bool OwnsAirbase(FactionHQ hq, Airbase airbase)
    {
        foreach (Airbase owned in hq.GetAirbases())
        {
            if (ReferenceEquals(owned, airbase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A hangar as "building id, index among its doors", the same on every machine.</summary>
    private static string HangarReference(Hangar? hangar)
    {
        Unit? building = hangar == null ? null : hangar.attachedUnit;
        if (hangar == null || building == null)
        {
            return string.Empty;
        }

        int index = Array.IndexOf(building.GetComponentsInChildren<Hangar>(true), hangar);
        return index < 0 ? string.Empty : $"{CommanderNetwork.Id(building)} {CommanderNetwork.Num(index)}";
    }

    private static Hangar? ResolveHangarReference(string reference, FactionHQ hq)
    {
        string[] fields = reference.Split(' ');
        if (fields.Length != 2
            || !CommanderNetwork.TryUnit(fields[0], out Unit building)
            || !ReferenceEquals(building.NetworkHQ, hq))
        {
            return null;
        }

        Hangar[] hangars = building.GetComponentsInChildren<Hangar>(true);
        int index = CommanderNetwork.Int(fields, 1);
        return index >= 0 && index < hangars.Length ? hangars[index] : null;
    }

    private void RegisterRemote()
    {
        CommanderNetwork.OnHost("air.launch", HostLaunch);
        CommanderNetwork.OnHost("air.order", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            if (CommanderNetwork.TryPosition(args, 5, out GlobalPosition point))
            {
                Unit? target = CommanderNetwork.TryUnit(args, 6, out Unit found) ? found : null;

                // The guest's click asked its own cargo list, which is always empty (runs live here), so
                // a travel point for a loaded transport arrives as a plain order. Read it the way the
                // host's own click does: a leg on the way to the LZ, or the cargo run overrides it.
                if (target == null
                    && CommanderSupplyHeliService.Instance?.TryAddCargoRunWaypoint(aircraft, point, CommanderNetwork.Bool(args, 7)) == true)
                {
                    return;
                }

                SetAircraftOrder(aircraft, point, target, CommanderNetwork.Bool(args, 7), ReadMode(args, 8));
            }
        }));
        CommanderNetwork.OnHost("air.escort", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            if (CommanderNetwork.TryUnit(args, 5, out Unit escortee))
            {
                SetAircraftEscortOrder(aircraft, escortee);
            }
        }));
        CommanderNetwork.OnHost("air.patrol", (hq, args) => HostOrder(
            hq,
            args,
            aircraft => SetAircraftPatrol(aircraft, CommanderNetwork.Bool(args, 5))));
        CommanderNetwork.OnHost("air.clear", (hq, args) => HostOrder(hq, args, aircraft => ClearAircraftRoute(aircraft)));
        CommanderNetwork.OnHost("air.land", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            LandingIntent intent = (LandingIntent)CommanderNetwork.Int(args, 6);
            if (CommanderNetwork.TryAirbase(args, 5, out Airbase field) && Enum.IsDefined(typeof(LandingIntent), intent))
            {
                SetAircraftLandingOrder(aircraft, field, intent, CommanderNetwork.Bool(args, 7));
            }
        }));
        CommanderNetwork.OnHost("air.resupply", (hq, args) => HostOrder(hq, args, aircraft => ResupplyAircraft(aircraft)));
        CommanderNetwork.OnHost("air.reserve", (hq, args) => HostOrder(hq, args, aircraft => ReserveAircraft(aircraft)));
        CommanderNetwork.OnHost("air.rtb", (hq, args) => HostOrder(hq, args, RequestReturnToBase));
        CommanderNetwork.OnHost("air.radius", (hq, args) => HostOrder(
            hq,
            args,
            aircraft => StepMissionRadius(aircraft, CommanderNetwork.Float(args, 5))));
        CommanderNetwork.OnHost("air.alt", (hq, args) => HostOrder(
            hq,
            args,
            aircraft => StepMissionAltitude(aircraft, CommanderNetwork.Float(args, 5))));
        CommanderNetwork.OnHost("air.autopilot", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            RemoveMission(aircraft);
            ReturnToCombatState(aircraft);
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} released to autopilot.");
        }));
        CommanderNetwork.OnHost("air.standby", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            Airbase? field = FindNearestOwnedAirbase(aircraft);
            if (field == null || !SetAircraftLandingOrder(aircraft, field, LandingIntent.Hold, append: false))
            {
                SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no friendly field to stand by on.");
            }
        }));
        CommanderNetwork.OnHost("air.area", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            if (missions.TryGetValue(aircraft, out AirMission mission)
                && CommanderNetwork.TryPosition(args, 5, out GlobalPosition center))
            {
                mission.AreaCenter = center;
                DestroyMissionMapVisual(mission);
                SetStatus($"{GetModeLabel(mission.Mode)} mission area relocated.");
            }
        }));
        CommanderNetwork.OnHost("air.adopt", (hq, args) => HostOrder(hq, args, aircraft =>
        {
            AirCommandMode mode = ReadMode(args, 5);
            if (!CommanderNetwork.TryPosition(args, 6, out GlobalPosition center))
            {
                return;
            }

            float radius = Mathf.Clamp(CommanderNetwork.Float(args, 7), 5000f, 150000f);
            SetStatus(TryAdoptAircraft(aircraft, mode, center, radius)
                ? $"{CommanderGameAccess.GetUnitLabel(aircraft)} tasked with a {GetModeLabel(mode)} mission."
                : "That aircraft can no longer be tasked.");
        }));

        CommanderNetwork.OnSync(SyncMissions);
        CommanderNetwork.OnGuest("air.m", ApplyMission);
        CommanderNetwork.OnGuest("air.ids", ApplyMissionIds);
        CommanderNetwork.OnGuest("st.air", args => SetStatus(CommanderNetwork.Arg(args, 1)));
    }

    /// <summary>
    /// On a guest, sends an order for one of its aircraft to the host, which holds every mission,
    /// and returns true so the caller stops. False on the host, where the caller applies it. The
    /// guest's launch settings ride along: see <see cref="remoteDefaults"/>.
    /// </summary>
    private bool ForwardAir(string verb, Aircraft aircraft, params string[] args)
    {
        if (!CommanderNetwork.IsGuest)
        {
            return false;
        }

        string[] request = new string[args.Length + 4];
        request[0] = CommanderNetwork.Id(aircraft);
        request[1] = CommanderNetwork.Num(SelectedTargetAltitude);
        request[2] = CommanderNetwork.Flag(TargetOrdnance);
        request[3] = CommanderNetwork.Flag(SaturationAttack);
        Array.Copy(args, 0, request, 4, args.Length);
        return CommanderNetwork.Forward(verb, request);
    }

    /// <summary>The guest half of LAUNCH: check the loadout the window built, then send it.</summary>
    private void ForwardLaunch(AirMissionOption option, Airbase airbase, GlobalPosition target)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        Loadout loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, option.Definition);
        if (hq == null)
        {
            RefuseLaunch("No faction HQ.");
            return;
        }

        if (!ValidateSelectedLoadout(option, loadout, airbase, hq, out string error))
        {
            RefuseLaunch(error);
            return;
        }

        SendLaunch(option, loadout, airbase, HangarFilter, target, CommanderRecoveryService.RecoveryMode.Off);
    }

    /// <summary>The <c>air.launch</c> request itself, shared by LAUNCH and the TRANSPORT window's rescue.</summary>
    private void SendLaunch(
        AirMissionOption option,
        Loadout loadout,
        Airbase airbase,
        Hangar? hangar,
        GlobalPosition target,
        CommanderRecoveryService.RecoveryMode recovery)
    {
        string[] keys = new string[loadout.weapons.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = loadout.weapons[i]?.jsonKey ?? string.Empty;
        }

        if (CommanderNetwork.Forward(
                "air.launch",
                option.Definition.jsonKey,
                CommanderNetwork.Num((int)option.Mode),
                CommanderNetwork.Key(airbase),
                HangarReference(hangar),
                CommanderNetwork.Position(target),
                CommanderNetwork.Num(GetMissionRadius(option.Mode)),
                CommanderNetwork.Num(SupportsTargetAltitude(option.Mode) ? SelectedTargetAltitude : 0f),
                CommanderNetwork.Flag(option.Mode == AirCommandMode.AirGuard && TargetOrdnance),
                CommanderNetwork.Flag(UsesSaturationAttack(option.Mode) && SaturationAttack),
                string.Join(",", keys),
                CommanderNetwork.Num((int)RecoveryFor(option.Mode, recovery, loadout)))
            && CommanderNetwork.HostHasMod)
        {
            SetStatus($"{GetModeLabel(option.Mode)}: {GetAircraftLabel(option.Definition)} launching.");
        }
    }

    /// <summary>
    /// A guest's LAUNCH. The loadout arrives as one jsonKey per pylon and is rebuilt against the
    /// airframe's own weapon options, so a guest cannot hang anything the pylon does not offer;
    /// field, faction and nuclear rules are then checked exactly as for the host's own launch.
    /// </summary>
    private void HostLaunch(FactionHQ hq, string[] args)
    {
        RunForGuest(() =>
        {
            AircraftDefinition? definition = FindAircraftDefinition(CommanderNetwork.Arg(args, 1));
            if (definition == null || definition.unitPrefab == null || !CanAiFly(definition))
            {
                RefuseLaunch("The host has no such aircraft.");
                return;
            }

            if (!CommanderNetwork.TryAirbase(args, 3, out Airbase airbase) || !OwnsAirbase(hq, airbase))
            {
                RefuseLaunch("That airbase is not yours any more.");
                return;
            }

            if (!CommanderNetwork.TryPosition(args, 5, out GlobalPosition target))
            {
                return;
            }

            AirCommandMode mode = ReadMode(args, 2);
            HardpointSet[] sets = definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets
                ?? Array.Empty<HardpointSet>();
            string[] keys = CommanderNetwork.Arg(args, 10).Split(',');
            Loadout loadout = new();
            for (int i = 0; i < sets.Length; i++)
            {
                loadout.weapons.Add(FindMount(sets[i], i < keys.Length ? keys[i] : string.Empty)!);
            }

            if (!ValidateMounts(loadout, sets, airbase, hq, out string error))
            {
                RefuseLaunch(error);
                return;
            }

            LaunchMission(
                hq,
                new AirMissionOption(definition, mode, sets, new List<AirHardpointGroup>()),
                loadout,
                airbase,
                ResolveHangarReference(CommanderNetwork.Arg(args, 4), hq),
                target,
                Mathf.Clamp(CommanderNetwork.Float(args, 6), 5000f, 150000f),
                SupportsTargetAltitude(mode) ? ClampTargetAltitude(CommanderNetwork.Float(args, 7)) : 0f,
                CommanderNetwork.Bool(args, 8),
                CommanderNetwork.Bool(args, 9),
                RecoveryFor(mode, (CommanderRecoveryService.RecoveryMode)CommanderNetwork.Int(args, 11), loadout));
        });
    }

    /// <summary>Resolves the aircraft, checks it is the guest's, and runs one order for it.</summary>
    private void HostOrder(FactionHQ hq, string[] args, Action<Aircraft> order)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit)
            || unit is not Aircraft aircraft
            || !ReferenceEquals(aircraft.NetworkHQ, hq))
        {
            return;
        }

        remoteDefaults = (
            ClampTargetAltitude(CommanderNetwork.Float(args, 2)),
            CommanderNetwork.Bool(args, 3),
            CommanderNetwork.Bool(args, 4));
        try
        {
            RunForGuest(() => order(aircraft));
        }
        finally
        {
            remoteDefaults = null;
        }
    }

    /// <summary>
    /// Runs a guest's request and sends that guest whatever the Air Command status line then said,
    /// leaving the host's own status line as it was.
    /// </summary>
    private void RunForGuest(Action request)
    {
        string hostText = statusText;
        float hostUntil = statusUntil;
        statusText = string.Empty;
        try
        {
            request();
            if (statusText.Length > 0)
            {
                CommanderNetwork.Reply("st.air", statusText);
            }
        }
        finally
        {
            statusText = hostText;
            statusUntil = hostUntil;
        }
    }

    /// <summary>
    /// The host's missions for this guest's faction: one "air.m" per aircraft, then "air.ids" with
    /// every id so the guest can drop the ones that ended.
    /// </summary>
    private void SyncMissions(FactionHQ hq, List<string[]> messages)
    {
        List<string> ids = new() { "air.ids" };
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            if (aircraft == null || aircraft.disabled || !ReferenceEquals(aircraft.NetworkHQ, hq))
            {
                continue;
            }

            AirMission mission = entry.Value;
            string id = CommanderNetwork.Id(aircraft);
            ids.Add(id);
            string[] route = new string[mission.Route.Count];
            for (int i = 0; i < route.Length; i++)
            {
                route[i] = CommanderNetwork.Position(mission.Route[i]);
            }

            string[] queued = new string[mission.QueuedTargets.Count];
            for (int i = 0; i < queued.Length; i++)
            {
                queued[i] = CommanderNetwork.Id(mission.QueuedTargets[i]);
            }

            messages.Add(new[]
            {
                "air.m",
                id,
                CommanderNetwork.Num((int)mission.Mode),
                CommanderNetwork.Position(mission.AreaCenter),
                CommanderNetwork.Num(mission.Radius),
                CommanderNetwork.Num(mission.TargetAltitude),
                CommanderNetwork.Num(mission.RouteIndex),
                string.Join(";", route),
                CommanderNetwork.Id(mission.ForcedTarget),
                CommanderNetwork.Key(mission.LandingBase),
                CommanderNetwork.Num((int)mission.Intent),
                CommanderNetwork.Flag(mission.Parked)
                    + CommanderNetwork.Flag(mission.Returning)
                    + CommanderNetwork.Flag(mission.RouteLoop),
                CommanderNetwork.Id(mission.EscortTarget),
                mission.ShortfallReason,
                string.Join(";", queued),
            });
        }

        messages.Add(ids.ToArray());
    }

    /// <summary>The guest side of one "air.m": create or update the mission it is drawn from.</summary>
    private void ApplyMission(string[] args)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null
            || !CommanderNetwork.TryUnit(args, 1, out Unit unit)
            || unit is not Aircraft aircraft
            || !CommanderNetwork.TryPosition(args, 3, out GlobalPosition center))
        {
            return;
        }

        AirCommandMode mode = ReadMode(args, 2);
        float radius = CommanderNetwork.Float(args, 4);
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            mission = new AirMission(hq, mode, center, radius, 0f, false, false, false, 0f);
            missions[aircraft] = mission;
            CommanderSelectionService.PinMissionUnit(aircraft, "AIR COMMAND", GetModeLabel(mode));
        }
        else if (mission.Mode != mode
            || !Mathf.Approximately(mission.Radius, radius)
            || FastMath.Distance(mission.AreaCenter, center) > 1f)
        {
            // The circle is drawn once and left; a moved or resized area needs a new one.
            DestroyMissionMapVisual(mission);
        }

        mission.Mode = mode;
        mission.AreaCenter = center;
        mission.Radius = radius;
        mission.TargetAltitude = CommanderNetwork.Float(args, 5);
        mission.Route.Clear();
        foreach (string point in CommanderNetwork.Arg(args, 7).Split(';'))
        {
            if (CommanderNetwork.TryPosition(point, out GlobalPosition routePoint))
            {
                mission.Route.Add(routePoint);
            }
        }

        mission.RouteIndex = Mathf.Clamp(CommanderNetwork.Int(args, 6), 0, mission.Route.Count);
        mission.ForcedTarget = CommanderNetwork.TryUnit(args, 8, out Unit forced) ? forced : null;

        // After ForcedTarget, whose setter empties the queue. The guest's route line, brackets and
        // cockpit markers read this copy.
        foreach (string id in CommanderNetwork.Arg(args, 14).Split(';'))
        {
            if (CommanderNetwork.TryUnit(id, out Unit queued))
            {
                mission.QueuedTargets.Add(queued);
            }
        }

        mission.LandingBase = CommanderNetwork.TryAirbase(args, 9, out Airbase field) ? field : null;
        LandingIntent intent = (LandingIntent)CommanderNetwork.Int(args, 10);
        mission.Intent = Enum.IsDefined(typeof(LandingIntent), intent) ? intent : LandingIntent.None;
        string flags = CommanderNetwork.Arg(args, 11);
        mission.Parked = flags.Length > 0 && flags[0] == '1';
        mission.Returning = flags.Length > 1 && flags[1] == '1';
        mission.RouteLoop = flags.Length > 2 && flags[2] == '1';
        mission.EscortTarget = CommanderNetwork.TryUnit(args, 12, out Unit escortee) ? escortee : null;
        mission.ShortfallReason = CommanderNetwork.Arg(args, 13);
    }

    private void ApplyMissionIds(string[] args)
    {
        HashSet<string> live = new(args, StringComparer.Ordinal);
        staleAircraft.Clear();
        foreach (Aircraft aircraft in missions.Keys)
        {
            if (aircraft == null || !live.Contains(CommanderNetwork.Id(aircraft)))
            {
                staleAircraft.Add(aircraft!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            RemoveMission(staleAircraft[i]);
        }

        staleAircraft.Clear();
        RefreshMissionMapVisuals();
    }
}
