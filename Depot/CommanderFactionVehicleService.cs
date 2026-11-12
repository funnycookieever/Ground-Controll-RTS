using System;
using System.Collections.Generic;

namespace GroundControlRts;

internal sealed class CommanderFactionVehicleService : ICommanderResetSession
{
    /// <summary>VehicleDefinition.jsonKey to factionTag, or SHARED. Vehicle faction definitions for depots and factories.</summary>
    private static readonly Dictionary<string, string> VehicleFactions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AFV8_APC"] = "BDF",
        ["AFV8_IFV"] = "BDF",
        ["AFV8_SAM"] = "BDF",
        ["CRAMTrailer1"] = "BDF",
        ["HLT-FC"] = "BDF",
        ["HLT-FT"] = "BDF",
        ["HLT-L"] = "BDF",
        ["HLT-M"] = "BDF",
        ["HLT-MART"] = "BDF",
        ["HLT-R"] = "BDF",
        ["HLT-T"] = "BDF",
        ["LCV45"] = "BDF",
        ["LaserTrailer1"] = "BDF",
        ["Linebreaker_APC"] = "PALA",
        ["Linebreaker_IFV"] = "PALA",
        ["Linebreaker_SAM"] = "PALA",
        ["MBT"] = "BDF",
        ["MBT1"] = "PALA",
        ["RadarSAM1"] = "PALA",
        ["SAMTrailer1"] = "BDF",
        ["SPAAG1"] = "BDF",
        ["SPAAG2"] = "PALA",
        ["Truck2-CRAM"] = "PALA",
        ["Truck2-FC"] = "PALA",
        ["Truck2-FT"] = "PALA",
        ["Truck2-L"] = "PALA",
        ["Truck2-LADS"] = "PALA",
        ["Truck2-M"] = "PALA",
        ["Truck2-MLRS"] = "PALA",
        ["Truck2-MRAP"] = "PALA",
        ["Truck2-R"] = "PALA",
        ["Truck2-RSAM"] = "PALA",
        ["Truck2-T"] = "PALA",
        ["Truck2-TBM-N"] = "PALA",
        ["Truck2-TBM"] = "PALA",
        ["LightTruck1_AA"] = "BDF",
        ["LightTruck1_AT"] = "BDF",
        ["6x6_1_AA"] = "BDF",
        ["6x6_1_APC"] = "BDF",
        ["6x6_1_AT"] = "BDF",
        ["6x6_1_IFV"] = "BDF",
        ["SAMTurret1"] = "BDF",
        ["RadarContainer1"] = "BDF",
        ["UGV1_grenade"] = "PALA",
        ["UGV1_SAM"] = "PALA",
        ["UGVDozer1"] = "SHARED",
    };

    /// <summary>
    /// How many enriched warheads a definition's prefab needs to roll out, 0 for everything that
    /// is not nuclear. Prefab data, so it never changes inside a session and is read once per
    /// definition.
    /// </summary>
    private static readonly Dictionary<UnitDefinition, int> NuclearWarheadCosts = new();

    /// <summary>Scratch for the roster log.</summary>
    private static readonly List<VehicleDefinition> RosterProbe = new();

    private static bool loggedNuclearRoster;

    private readonly HashSet<string> heldCategories = new(StringComparer.Ordinal);
    private readonly HashSet<VehicleDefinition> heldDefinitions = new();

    internal CommanderFactionVehicleService()
    {
        Instance = this;
    }

    internal static CommanderFactionVehicleService? Instance { get; private set; }

    internal static FactionHQ? AutomaticDeploymentHq { get; set; }

    internal static float FactionFunds => CommanderGameAccess.GetLocalHq()?.factionFunds ?? 0f;

    public void ResetSession()
    {
        heldCategories.Clear();
        heldDefinitions.Clear();
        AutomaticDeploymentHq = null;
        loggedNuclearRoster = false;
        CommanderDepotLane.Reset();
    }

    /// <summary>
    /// True when the player turned friendly AI off and this is their side. The reserve is left
    /// untouched, so those units are still there for the depot queue and Air Command to spend.
    /// </summary>
    internal static bool IsFriendlyAiOff(FactionHQ? hq)
    {
        return CommanderSettings.DisableFriendlyAi
            && hq != null
            && CommanderFeatureGate.AdvancedFeaturesEnabled
            && hq == CommanderGameAccess.GetLocalHq();
    }

    internal static int GetReserveCount(VehicleDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        return hq != null ? hq.GetUnitSupply(definition) : 0;
    }

    internal static float GetPurchaseCost(VehicleDefinition definition)
    {
        return Math.Max(0f, definition.value);
    }

    /// <summary>Purchase eligibility only. Existing reserves may deploy regardless of allocation.</summary>
    internal static bool CanPurchase(VehicleDefinition definition, FactionHQ? hq)
    {
        if (!CommanderSettings.LimitToFactoryVehicles)
        {
            return true;
        }

        if (hq?.faction == null)
        {
            return false;
        }

        if (VehicleFactions.TryGetValue(definition.jsonKey, out string allocation))
        {
            return allocation == "SHARED"
                || string.Equals(allocation, hq.faction.factionTag, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    internal static bool CanAcquire(VehicleDefinition definition, FactionHQ? hq, out string reason)
    {
        if (hq != null && hq.GetUnitSupply(definition) > 0)
        {
            reason = string.Empty;
            return true;
        }

        // A draft sells nothing: the reserve is the whole army.
        if (CommanderDraftService.IsDraftMission)
        {
            reason = $"No {CommanderGameAccess.GetVehicleLabel(definition)} left in your drafted reserve.";
            return false;
        }

        if (!CanPurchase(definition, hq))
        {
            reason = $"{CommanderGameAccess.GetVehicleLabel(definition)} is not available to this faction.";
            return false;
        }

        float cost = GetPurchaseCost(definition);
        if ((hq?.factionFunds ?? 0f) >= cost)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Insufficient faction funds for {CommanderGameAccess.GetVehicleLabel(definition)}.";
        return false;
    }

    internal static void CommitAcquisition(VehicleDefinition definition, FactionHQ? hq)
    {
        if (hq == null)
        {
            return;
        }

        if (hq.GetUnitSupply(definition) > 0)
        {
            hq.ModifyUnitSupply(definition, -1);
            return;
        }

        hq.AddFunds(-GetPurchaseCost(definition));
    }

    /// <summary>
    /// The munitions bunker a nuclear ground unit's warhead comes out of, or null with a reason
    /// when it may not be fielded. Returns null with no reason for everything else, which is nearly
    /// every unit: an ordinary spawn is not charged.
    /// </summary>
    /// <remarks>
    /// The base game only spends a warhead through <c>Rearmer</c>, and
    /// <c>VehicleDepot.TrySpawnVehicle</c> spawns the prefab already loaded, so a depot-bought nuclear
    /// launcher would be a free warhead. The restriction list, <c>WeaponChecker.MountAllowedNuclear</c>
    /// and escalation thresholds only cover aircraft loadouts. This is the ground half of that gate, and
    /// it draws on <c>Rearmer</c>'s own choice of airbase, so the base that rearms a launcher is the base
    /// that may field it.
    /// <para>
    /// Uranium is the only gate; DEFCON escalation (<c>MissionManager.AllowTactical</c> /
    /// <c>AllowStrategic</c>) does not apply. A warhead already costs ten minutes of an enrichment plant.
    /// </para>
    /// </remarks>
    internal static Airbase? FindSpawnWarheadStore(
        VehicleDepot? depot, VehicleDefinition definition, out string reason)
    {
        reason = string.Empty;
        FactionHQ? hq = depot == null ? null : depot.NetworkHQ;
        LogNuclearRosterOnce(hq);

        int cost = GetWarheadCost(definition);

        // GOD DIFFICULTY's launchers are the god's gift, not the economy's.
        if (cost == 0 || hq == null || !hq.IsServer || CommanderEnemyCommanderService.IsNuclearArmed(hq))
        {
            return null;
        }

        // Any base will do: uranium is one faction-wide pool (CommanderEconomyPatches.GetWarheadsPostfix).
        if (!hq.TryGetNearestAirbase(depot!.transform.position, out Airbase airbase) || airbase == null)
        {
            reason = "no base to draw enriched uranium from";
            return null;
        }

        // Checked rather than assumed: RemoveWarheads walks an empty store happily, so an unchecked
        // call would field the launcher and report warheads spent that never existed.
        int held = airbase.GetWarheads();
        if (held < cost)
        {
            reason = cost == 1
                ? "no enriched uranium in store, build a nuclear enrichment plant"
                : $"this needs {cost} enriched warheads and the bunker holds {held}";
            return null;
        }

        return airbase;
    }

    /// <summary>
    /// The enriched warheads this unit costs to field, 0 when it carries no nuclear store.
    /// <c>Unit.weaponStations</c> is built at runtime and is empty on a prefab, but every store is
    /// a <c>Weapon</c> component with its <c>WeaponInfo</c> and its magazine serialized on it, so
    /// the prefab does answer this.
    /// </summary>
    /// <remarks>
    /// Counted in rounds, not stations, because rounds are what the base game charges:
    /// <c>Rearmer.ProcessRearmRequest</c> sums the nuclear rounds it loads and hands that total to
    /// <c>RemoveWarheads</c>, and <c>WeaponChecker.MountAllowedNuclear</c> tests
    /// <c>hardpoints.Count * mount.ammo</c>. The MSV nuclear launcher carries two rounds and so
    /// costs two warheads. <c>Weapon.ammo</c> is the serialized magazine, which is what
    /// <c>MissileLauncher.GetFullAmmo</c> returns; the base <c>Weapon</c> leaves it 0 and is a
    /// single shot, hence the floor of 1.
    /// </remarks>
    internal static int GetWarheadCost(UnitDefinition? definition)
    {
        if (definition == null || definition.unitPrefab == null)
        {
            return 0;
        }

        if (NuclearWarheadCosts.TryGetValue(definition, out int cached))
        {
            return cached;
        }

        int cost = 0;
        Weapon[] weapons = definition.unitPrefab.GetComponentsInChildren<Weapon>(true);
        for (int i = 0; i < weapons.Length; i++)
        {
            WeaponInfo info = weapons[i] == null ? null! : weapons[i].info;
            if (info != null && info.nuclear)
            {
                cost += Math.Max(1, weapons[i].ammo);
            }
        }

        NuclearWarheadCosts[definition] = cost;
        return cost;
    }

    internal bool IsCategoryHeld(string category)
    {
        return heldCategories.Contains(category);
    }

    internal void ToggleCategory(string category)
    {
        if (string.Equals(category, "All", StringComparison.Ordinal))
        {
            return;
        }

        if (!heldCategories.Add(category))
        {
            heldCategories.Remove(category);
        }
    }

    internal bool IsDefinitionHeld(VehicleDefinition definition)
    {
        return heldDefinitions.Contains(definition);
    }

    internal void ToggleDefinition(VehicleDefinition definition)
    {
        if (!heldDefinitions.Add(definition))
        {
            heldDefinitions.Remove(definition);
        }
    }

    internal bool ShouldBlockAutomaticDeployment(VehicleDepot depot, VehicleDefinition definition)
    {
        FactionHQ? deploymentHq = AutomaticDeploymentHq;
        if (deploymentHq == null || depot.NetworkHQ != deploymentHq)
        {
            return false;
        }

        // A type the player has queued is the queue's to roll out (see CommanderSpawnService.QueueReserve).
        return IsFriendlyAiOff(deploymentHq)
            || IsPlayedByPlayers(deploymentHq)
            || (ReferenceEquals(deploymentHq, CommanderGameAccess.GetLocalHq())
                && CommanderSpawnService.Instance?.CountQueued(definition, unpaidOnly: true) > 0)
            || heldDefinitions.Contains(definition)
            || heldCategories.Contains(CommanderGameAccess.GetVehicleCategoryLabel(definition));
    }

    /// <summary>
    /// A side with a player on it keeps its factory output in reserve until the player rolls it
    /// out from a depot (a click, SPAWN RESERVE, or the depot's AUTO button).
    /// Left to <c>FactionHQ.DeployVehicles</c> the same units would leave the depot with no player
    /// order and drive at the enemy on their own. A side nobody plays keeps the base game's roll-out,
    /// and so does a both-sides match, where the "local" side is each AI in turn.
    /// </summary>
    private static bool IsPlayedByPlayers(FactionHQ hq)
    {
        return (!CommanderEnemyCommanderService.LocalSideRotates
                && ReferenceEquals(hq, GameManager.GetLocalHQ(out FactionHQ localHq) ? localHq : null))
            || CommanderEnemyCommanderService.HasHumanPlayers(hq);
    }

    /// <summary>
    /// Logs, once per mission, every vehicle in the faction roster that spends enriched uranium.
    /// Which prefab carries a nuclear store is asset data, not code.
    /// </summary>
    private static void LogNuclearRosterOnce(FactionHQ? hq)
    {
        if (loggedNuclearRoster || hq?.faction == null)
        {
            return;
        }

        loggedNuclearRoster = true;
        CommanderGameAccess.CollectFactionVehicleDefinitions(RosterProbe, hq);
        string names = string.Empty;
        for (int i = 0; i < RosterProbe.Count; i++)
        {
            int cost = GetWarheadCost(RosterProbe[i]);
            if (cost > 0)
            {
                names += (names.Length == 0 ? string.Empty : ", ")
                    + $"{RosterProbe[i].unitName} ({cost} warhead{(cost == 1 ? string.Empty : "s")})";
            }
        }

        RosterProbe.Clear();
        CommanderPlugin.Log.LogInfo(names.Length == 0
            ? $"No vehicle in the {hq.faction.name} roster carries a nuclear store, so enriched "
                + "uranium gates aircraft only on this mission."
            : $"Ground units that spend enriched uranium ({hq.faction.name}): {names}.");
    }
}
