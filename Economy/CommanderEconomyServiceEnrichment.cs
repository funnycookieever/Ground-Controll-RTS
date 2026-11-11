using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Enriched uranium, which is the game's own warhead stockpile under the name the request used. The
/// mod adds no resource of its own here: a nuclear enrichment plant is a catalogue building whose
/// prefab already says it makes warheads, and the base game does the rest -
/// <c>Factory.SetFactory("Nuclear Warhead", interval)</c> registers <c>ProduceWarhead</c>, which pays
/// <c>FactionHQ.AddWarheadStockpile</c>, which fills the munitions bunkers, which is what
/// <c>WeaponChecker.MountAllowedNuclear</c> charges for a nuclear store on an aircraft and what
/// <c>Rearmer</c> charges for one on a ground vehicle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which kind of plant a building is comes off its own prefab.</b> <c>Factory.SetFactory</c>
/// branches on the production key alone: anything the encyclopedia knows is a unit line, and only
/// the literal string "Nuclear Warhead" is the warhead line. Handing an enrichment plant the FACTORY
/// button's currently selected vehicle is what made it turn out main battle tanks.
/// <c>Factory.factoryType</c> reads the prefab's own <c>weapons</c> flag, so a patch that adds
/// another warhead plant files itself and a name table cannot go stale.
/// </para>
/// <para>
/// The other half of the gate is the mission, not this file: the mod's missions un-restrict the
/// BallisticMissile1 family and set the game's own nuclear escalation threshold to a score neither
/// side reaches early, so the bomb is late game because the war has to have been fought first.
/// </para>
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>The production key <c>Factory.SetFactory</c> reads as "make warheads, not units".</summary>
    private const string NuclearWarheadProduction = "Nuclear Warhead";

    /// <summary>Income an AI faction needs before it builds an enrichment plant: four maxed Gas Export
    /// Terminals' worth, so only a commander that has already won its economy gets nuclear bombs.</summary>
    private const float EnemyEnrichmentIncomePerMinute = 300f;

    private readonly Dictionary<FactionHQ, string> enrichmentGate = new();

    private bool loggedWarheadStores;

    /// <summary>Seconds a nuclear enrichment plant takes to turn out one warhead.</summary>
    internal static float WarheadProductionSeconds => Mathf.Max(60f, CommanderSettings.WarheadProductionSeconds);

    /// <summary>Warheads the local faction has in store, which is what a nuclear store is paid from.</summary>
    internal static int GetFriendlyWarheads()
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        return hq == null ? 0 : hq.GetWarheadStockpile();
    }

    /// <summary>
    /// Where an enrichment plant's output actually goes. <c>FactionHQ.AddWarheadStockpile</c> hands
    /// each warhead to a held airbase that has a <c>WarheadStorage</c> building - the munitions
    /// bunker - and <b>throws the batch away</b> when the faction holds none, which is a silent loss
    /// the player would read as a dead building. Which prefabs carry that component is asset data a
    /// decompile cannot answer, so the log line names them rather than guessing.
    /// </summary>
    private void ReportWarheadStorage(FactionHQ hq)
    {
        LogWarheadStoresOnce();
        int stores = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled)
            {
                stores += airbase.GetStorage();
            }
        }

        if (stores > 0)
        {
            return;
        }

        StatusText = "Enrichment plant built, but this faction has nowhere to keep a warhead: "
            + "build a structure that stores them, or take a base that has one.";
        CommanderAlertService.Instance?.NotifySupply(
            "NO WARHEAD STORE - ENRICHED URANIUM WILL BE LOST", null);
        CommanderPlugin.Log.LogWarning(
            $"{hq.faction.factionName} has an enrichment plant and no warhead storage, so every "
                + "warhead it makes is discarded by AddWarheadStockpile.");
    }

    /// <summary>
    /// The enemy's one enrichment plant, late and rich only: an income of
    /// <see cref="EnemyEnrichmentIncomePerMinute"/>, warhead storage at a held base (or every batch is
    /// thrown away), a land home base to put it beside, and none built or queued yet. Its warheads are
    /// what let a strike buy roll GPO-N. The price, 0 when the rung is closed, so the reserve and the
    /// build ask the same question.
    /// </summary>
    private float GetEnemyEnrichmentCost(FactionHQ hq, out BuildingDefinition? plant, out GlobalPosition anchor)
    {
        plant = null;
        anchor = default;

        // Or a balance that covers the plant several times over: a rich start need not wait for income.
        float income = GetIncomePerMinute(hq);
        BuildingDefinition? mission = GetMissionEnrichmentDefinition();
        bool rich = mission != null && hq.factionFunds >= GetStructureCost(mission) * EnemyReserveMultiple;
        if (income < EnemyEnrichmentIncomePerMinute && !rich)
        {
            return NoteEnrichmentGate(hq, $"income {income:0}/min, needs {EnemyEnrichmentIncomePerMinute:0}");
        }

        if (!CommanderVictoryService.TryGetHomeBase(hq, out Airbase home) || !CommanderGameAccess.IsLandBase(home))
        {
            return NoteEnrichmentGate(hq, "no land home base");
        }

        if (CountEnrichmentPlants(hq) > 0)
        {
            return NoteEnrichmentGate(hq, "has one");
        }

        int stores = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            stores += airbase != null && !airbase.disabled ? airbase.GetStorage() : 0;
        }

        plant = stores > 0 ? GetMissionEnrichmentDefinition() : null;
        if (plant == null)
        {
            return NoteEnrichmentGate(hq, stores > 0 ? "no enrichment plant on this mission" : "no warhead storage at any held base");
        }

        anchor = home.center.GlobalPosition();
        NoteEnrichmentGate(hq, "open");
        return GetStructureCost(plant);
    }

    /// <summary>Logs why the enrichment rung is shut, once per change: nothing else says why a rich
    /// enemy never builds one.</summary>
    private float NoteEnrichmentGate(FactionHQ hq, string reason)
    {
        if (!enrichmentGate.TryGetValue(hq, out string last) || last != reason)
        {
            enrichmentGate[hq] = reason;
            CommanderPlugin.Log.LogInfo($"Enemy enrichment plant ({hq.faction.name}): {reason}.");
        }

        return 0f;
    }

    private bool TryBuildEnemyEnrichment(FactionHQ hq)
    {
        float cost = GetEnemyEnrichmentCost(hq, out BuildingDefinition? plant, out GlobalPosition anchor);
        return cost > 0f
            && TryFindSiteNear(hq, plant!, anchor, out GlobalPosition site)
            && QueueEnemyBlueprint(hq, CommanderBuildKind.Structure, plant!, null, site, cost, GetStructureLabel(plant!));
    }

    private int CountEnrichmentPlants(FactionHQ hq)
    {
        int count = 0;
        Factory[] factories = GetFactories();
        for (int i = 0; i < factories.Length; i++)
        {
            Unit? attached = factories[i] == null ? null : factories[i].attachedUnit;
            if (attached != null && !attached.disabled && attached.NetworkHQ == hq && factories[i].factoryType == Factory.FactoryType.Nukes)
            {
                count++;
            }
        }

        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite site = constructionSites[i];
            Factory? factory = site.Definition?.unitPrefab == null ? null : site.Definition.unitPrefab.GetComponentInChildren<Factory>(true);
            if (ReferenceEquals(site.Hq, hq) && factory != null && factory.factoryType == Factory.FactoryType.Nukes)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Names the catalogue buildings that can hold a warhead, once per mission, for the same reason
    /// the capture roster is logged: the component lives in the game's asset files, so nothing but a
    /// runtime look can say which building the player has to put up.
    /// </summary>
    private void LogWarheadStoresOnce()
    {
        if (loggedWarheadStores)
        {
            return;
        }

        IReadOnlyList<BuildingDefinition> buildings = Catalog;
        if (buildings.Count == 0)
        {
            return;
        }

        loggedWarheadStores = true;
        System.Text.StringBuilder names = new();
        for (int i = 0; i < buildings.Count; i++)
        {
            if (buildings[i].unitPrefab != null
                && buildings[i].unitPrefab.GetComponentInChildren<WarheadStorage>(true) != null)
            {
                names.Append(names.Length == 0 ? string.Empty : ", ").Append(GetStructureLabel(buildings[i]));
            }
        }

        CommanderPlugin.Log.LogInfo(names.Length == 0
            ? "No buildable structure stores warheads, so enriched uranium can only be kept at a base "
                + "the mission gave storage to."
            : $"Structures that store enriched uranium: {names}.");
    }
}
