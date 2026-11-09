namespace GroundControlRts;

/// <summary>The door the enemy commander's builder deliveries need into the SUPPLY RUN window.</summary>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>
    /// An AI faction's builder delivery: a rotary airframe with a Jackknife in its bay, launched off <paramref name="destination"/> when
    /// that field can fly it, lands at <paramref name="lz"/> and unloads it, the same landing run the
    /// SUPPLY RUN window sends, more reliable than slinging a builder. Run as that faction, so the options, the price and
    /// the spawn are its own; the player's own selection in the window is put back afterwards.
    /// Null when no airframe on its fields carries a Jackknife, else what the window would have said.
    /// </summary>
    internal string? LaunchAiBuilderRun(FactionHQ hq, Airbase destination, GlobalPosition lz)
    {
        string? result = null;
        int keptAircraft = SelectedAircraftIndex;
        Airbase? keptAirbase = SelectedAirbase;
        string keptStatus = statusText;
        bool keptAirdrop = airdropDelivery;
        CommanderNetwork.RunAs(hq, () =>
        {
            RefreshOptions();
            for (int i = 0; i < aircraftOptions.Count && result == null; i++)
            {
                CargoAircraftOption option = aircraftOptions[i];
                if (!option.Rotary || !SelectJackknifeCargo(option))
                {
                    continue;
                }

                SelectAircraft(i);
                Airbase? field = null;
                foreach (AirbaseOption airbase in airbaseOptions)
                {
                    if (airbase.Ready && (field == null || ReferenceEquals(airbase.Airbase, destination)))
                    {
                        field = airbase.Airbase;
                    }
                }

                if (field == null)
                {
                    ClearCargoSelection(option);
                    continue;
                }

                SelectedAirbase = field;
                result = LaunchSelectedLandingRun(lz);
                ClearCargoSelection(option);
            }
        });

        RefreshOptions();
        SelectAircraft(keptAircraft);
        SelectedAirbase = keptAirbase;
        statusText = keptStatus;
        airdropDelivery = keptAirdrop;
        return result;
    }

    /// <summary>One Jackknife in the first bay that offers one, every other bay empty.</summary>
    private static bool SelectJackknifeCargo(CargoAircraftOption option)
    {
        bool picked = false;
        foreach (CargoSlotOption slot in option.CargoSlots)
        {
            int index = picked ? -1 : slot.Mounts.FindIndex(IsJacknifeCargo);
            slot.Select(index);
            picked |= index >= 0;
        }

        return picked;
    }

    private static void ClearCargoSelection(CargoAircraftOption option)
    {
        foreach (CargoSlotOption slot in option.CargoSlots)
        {
            slot.Clear();
        }
    }

    /// <summary>The selected airframe, cargo and field flown as a landing run onto one point, the
    /// click on the map the window waits for answered at once.</summary>
    private string LaunchSelectedLandingRun(GlobalPosition target)
    {
        airdropDelivery = false;
        BeginSelectedCargoRun();
        PendingTargetSelection? selection = pendingTargetSelection;
        if (selection == null)
        {
            return statusText;
        }

        pendingTargetSelection = null;
        selection.Targets.Add(target);
        SpawnCargoRun(
            selection.Aircraft,
            selection.Loadout,
            selection.CargoLabel,
            selection.Airbase,
            selection.HighTerrainClearance,
            selection.TerrainClearanceMeters,
            selection.Airdrop,
            selection.SupportSummary,
            selection.UseOtherAirfields,
            selection.Targets,
            selection.FollowRoads);
        return "launched with " + selection.CargoLabel;
    }
}
