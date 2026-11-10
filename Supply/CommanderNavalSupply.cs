using System;
using System.Globalization;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSupplyHeliService
{
    internal string GetNavalSupplyButtonLabel()
    {
        CargoAircraftOption? option = FindNavalSupplyAircraft();
        if (option == null)
        {
            return "NAVAL SUPPLY UNAVAILABLE";
        }

        string price = UnitConverter.ValueReading(CommanderEconomyService.PriceOf(option.Definition)) ?? CommanderEconomyService.PriceOf(option.Definition).ToString("F1", CultureInfo.CurrentCulture);
        return $"REQUEST NAVAL SUPPLY  |  {price}";
    }

    internal void RequestNavalSupply(Ship ship)
    {
        if (ship == null || ship.disabled)
        {
            SetStatus("The selected ship is unavailable.");
            return;
        }

        if (CommanderNetwork.Forward("naval.supply", CommanderNetwork.Id(ship)))
        {
            if (CommanderNetwork.HostHasMod)
            {
                SetStatus($"Naval supply requested for {CommanderGameAccess.GetUnitLabel(ship)}.");
            }

            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            NavalSupplyFailed(error);
            return;
        }

        if (!CommanderGameAccess.IsFriendlyUnit(ship, hq))
        {
            NavalSupplyFailed("Naval supply can only be requested for a friendly ship.");
            return;
        }

        CargoAircraftOption? aircraft = FindNavalSupplyAircraft();
        if (aircraft == null || !TryFindNavalCargo(aircraft, out CargoSlotOption? slot, out WeaponMount? mount))
        {
            NavalSupplyFailed("No UH-90K naval-supply loadout is available.");
            return;
        }

        Airbase? airbase = FindNearestNavalSupplyAirbase(hq!, aircraft.Definition, mount!, ship.transform.position);
        if (airbase == null)
        {
            NavalSupplyFailed("No friendly airfield can currently spawn a UH-90K naval-supply run.");
            return;
        }

        bool purchased = false;
        float cargoCost = Mathf.Max(aircraft.HardpointSets[slot!.HardpointIndex].hardpoints?.Count ?? 0, 1) * GetMountCargoCost(mount!);
        bool fromStock = hq!.GetUnitSupply(aircraft.Definition) > 0;
        if (hq.factionFunds < cargoCost + (fromStock ? 0f : CommanderEconomyService.PriceOf(aircraft.Definition)))
        {
            NavalSupplyFailed("The faction cannot afford the naval-supply aircraft and its cargo.");
            return;
        }

        hq.AddFunds(-cargoCost);
        if (!fromStock)
        {
            float cost = CommanderEconomyService.PriceOf(aircraft.Definition);
            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(aircraft.Definition, 1);
            purchased = true;
        }

        Loadout loadout = CreateEmptyLoadout(aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(loadout, aircraft.HardpointSets, slot.HardpointIndex, mount!);
        PendingAircraftSpawn pendingSpawn = new(
            hq,
            aircraft.Definition,
            airbase,
            "Naval supplies",
            ship.GlobalPosition(),
            false,
            0f,
            true,
            "Basegame naval supply",
            purchased,
            purchased ? CommanderEconomyService.PriceOf(aircraft.Definition) : 0f,
            new[] { ship.GlobalPosition() },
            Time.unscaledTime + PendingSpawnTimeoutSeconds,
            ship);
        pendingAircraftSpawns.Add(pendingSpawn);

        int liveryIndex = aircraft.Definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(
            null,
            aircraft.Definition,
            new LiveryKey(liveryIndex),
            loadout,
            aircraft.Definition.aircraftParameters.DefaultFuelLevel);
        if (!result.Allowed)
        {
            pendingAircraftSpawns.Remove(pendingSpawn);
            if (purchased)
            {
                hq.ModifyUnitSupply(aircraft.Definition, -1);
                hq.AddFunds(CommanderEconomyService.PriceOf(aircraft.Definition));
            }

            hq.AddFunds(cargoCost);
            NavalSupplyFailed("The nearest compatible airfield rejected the naval-supply spawn.");
            return;
        }

        ship.RequestRearm();
        if (!CommanderNetwork.IsHandlingGuest)
        {
            SetStatus($"UH-90K naval supply dispatched to {CommanderGameAccess.GetUnitLabel(ship)}.");
        }
    }

    private static bool TryFindNavalCargo(CargoAircraftOption option, out CargoSlotOption? selectedSlot, out WeaponMount? selectedMount)
    {
        for (int slotIndex = 0; slotIndex < option.CargoSlots.Count; slotIndex++)
        {
            CargoSlotOption slot = option.CargoSlots[slotIndex];
            for (int mountIndex = 0; mountIndex < slot.Mounts.Count; mountIndex++)
            {
                WeaponMount mount = slot.Mounts[mountIndex];
                if (mount?.info?.rearmShip == true)
                {
                    selectedSlot = slot;
                    selectedMount = mount;
                    return true;
                }
            }
        }

        selectedSlot = null;
        selectedMount = null;
        return false;
    }

    private static bool TrySelectNavalSupplyStation(Aircraft aircraft)
    {
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station?.WeaponInfo?.rearmShip == true && station.Ammo > 0)
            {
                aircraft.weaponManager.currentWeaponStation = station;
                return true;
            }
        }

        return false;
    }

    private static bool OverrideNavalSupplyTarget(AIHeloTransportState state, Aircraft aircraft, CargoMission mission)
    {
        Ship? ship = mission.NavalTarget;
        if (ship == null || ship.disabled || ship.NetworkHQ != mission.Hq || !TrySelectNavalSupplyStation(aircraft))
        {
            mission.TargetOverrideActive = false;
            return false;
        }

        LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
        TimeWithoutMissionField?.SetValue(state, 0f);
        AirdropField?.SetValue(state, true);
        TransportModeField?.SetValue(state, AIHeloTransportState.TransportMode.NavalSupply);
        state.stateDisplayName = $"Supplying {CommanderGameAccess.GetUnitLabel(ship)}";
        if (PilotField?.GetValue(state) is Pilot pilot)
        {
            pilot.flightInfo.EnemyContact = true;
        }

        object? destination = TransportDestinationField?.GetValue(state);
        if (destination == null || UpdateLzForUnitMethod == null)
        {
            return false;
        }

        DestinationValidMissionField?.SetValue(destination, true);
        DestinationDropConditionsField?.SetValue(destination, false);
        UpdateLzForUnitMethod.Invoke(destination, new object[] { aircraft, ship });
        TransportDestinationField?.SetValue(state, destination);
        return true;
    }

    private CargoAircraftOption? FindNavalSupplyAircraft()
    {
        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            CargoAircraftOption option = aircraftOptions[i];
            string identity = $"{option.Definition.unitName} {option.Definition.code} {option.Definition.jsonKey}";
            if ((identity.IndexOf("UH-90K", StringComparison.OrdinalIgnoreCase) >= 0
                    || identity.IndexOf("UtilityHelo1", StringComparison.OrdinalIgnoreCase) >= 0)
                && TryFindNavalCargo(option, out _, out _))
            {
                return option;
            }
        }

        return null;
    }

    /// <summary>The host's half of a guest's REQUEST NAVAL SUPPLY, run with the guest's faction acting.</summary>
    private void HostNavalSupply(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit) && unit is Ship ship && ReferenceEquals(ship.NetworkHQ, hq))
        {
            RequestNavalSupply(ship);
        }
    }

    // A guest's refusal goes back to the guest; the host's own status line is not what they see.
    private void NavalSupplyFailed(string text)
    {
        if (CommanderNetwork.IsHandlingGuest)
        {
            CommanderNotice.Fail(text);
        }
        else
        {
            SetStatus(text);
        }
    }

    private Airbase? FindNearestNavalSupplyAirbase(
        FactionHQ hq,
        AircraftDefinition definition,
        WeaponMount mount,
        Vector3 targetPosition)
    {
        Airbase? nearest = null;
        float nearestSqrDistance = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (!IsFreeAirbase(candidate, hq, definition)
                || !WeaponChecker.MountAllowedHQ(mount, hq)
                || !WeaponChecker.MountAllowedAirbase(mount, candidate))
            {
                continue;
            }

            Transform position = candidate.center != null ? candidate.center : candidate.transform;
            float sqrDistance = (position.position - targetPosition).sqrMagnitude;
            if (sqrDistance < nearestSqrDistance)
            {
                nearestSqrDistance = sqrDistance;
                nearest = candidate;
            }
        }

        return nearest;
    }
}
