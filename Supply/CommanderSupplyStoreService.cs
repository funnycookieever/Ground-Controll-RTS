using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The fuel and ammunition a faction has on the shelf, where it is kept, and what it costs to put
/// more there. Every resupply the commander orders is paid for out of one of these, and a unit sent
/// to a store that has run dry stays parked instead of being quietly restocked out of nothing.
/// </summary>
/// <remarks>
/// <para>
/// Ammunition is <em>not</em> new state. Every ammo building and rearm truck in the game
/// already carries a <c>Rearmer</c> whose public <c>Capacity</c> is the kilograms of ordnance left
/// on the shelf, drawn down by <c>Rearmer.ProcessRearmRequest</c> every time it fills a magazine and
/// written back over the network by <c>Unit.RpcUpdateRearmerCapacity</c>. So "how full is this
/// depot" is a base-game number the mod only has to read, and a ground vehicle already spends it
/// without the mod being involved at all. What the mod adds is the readout, the BUY button, and the
/// one pool the game genuinely does not have — fuel — kept on the same buildings so the player has
/// one thing to find rather than two.
/// </para>
/// <para>
/// A rearm is deliberately <em>not</em> run through <c>ProcessRearmRequest</c>. That method walks
/// <c>weaponStations[i].WeaponInfo.massPerRound</c> with no null check, which is safe for the ground
/// vehicles the base game points it at and is not safe for an aircraft that has shot a pylon empty.
/// The draw is computed here over the stations that still have a weapon, and the restock itself is a
/// fresh <c>Loadout</c> assignment — the same path a hangar spawn uses.
/// </para>
/// <para>
/// Fail-soft on purpose: a faction that owns no rearmer anywhere gets its resupplies free. A mission
/// that ships no ammo depot would otherwise lose its air force to a rule the player cannot see, and
/// the moment one depot exists the rule starts biting.
/// </para>
/// </remarks>
internal sealed class CommanderSupplyStoreService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>A munitions bunker is the base's main store and holds five standard shelves of fuel.</summary>
    internal const float MunitionsBunkerFuelKg = 100000f;

    /// <summary>What a munitions truck carries. The ordnance figure is forced onto its <c>Rearmer</c> in <c>CommanderSupplyStorePatches</c>.
    /// 5 t is one full reload of the MSV Ballistic Missile Launcher (two 2.5 t Piledrivers); at 4 t a
    /// truck could only ever hand it one of the two.</summary>
    internal const float MunitionsTruckFuelKg = 2000f;
    internal const float MunitionsTruckAmmoKg = 5000f;

    /// <summary>What a fuel truck carries.</summary>
    internal const float FuelTruckFuelKg = 8000f;

    /// <summary>
    /// Artillery ordnance costs this many times its authored mass, to roll the gun out and to refill
    /// it. A salvo is a lot of projectiles for a cheap hull, so the shells are where it is paid for.
    /// </summary>
    internal const float ArtilleryAmmoMultiplier = 3f;

    /// <summary>A round at least this heavy is a strategic missile, not an artillery shell.</summary>
    internal const float StrategicRoundKg = 1000f;

    /// <summary>How close a truck has to be to a dump to load from it, the same reach a resupply order uses.</summary>
    internal const float TruckDumpReachMeters = 300f;

    /// <summary>
    /// Every building rearmer the prefab authors as <c>singleUse</c>, i.e. the game's ammo dumps,
    /// recorded in <c>Rearmer.Awake</c> because the flag is private asset data.
    /// </summary>
    internal static readonly HashSet<Rearmer> DumpRearmers = new();

    private const float ScanIntervalSeconds = 5f;

    /// <summary>The answer to every BUY in a draft: the shelves hold what was drafted, and no more.</summary>
    private const string DraftNothingForSale = "Draft: nothing is for sale. Your shelves hold what you drafted; munitions and fuel trucks can still carry it forward.";

    private const float WarnIntervalSeconds = 90f;

    /// <summary>Below this fraction the player is told the depots are running out.</summary>
    private const float LowFraction = 0.25f;

    /// <summary>The BUY size until the player drags it: a quarter of a shelf.</summary>
    private const float DefaultPurchasePercent = 25f;

    /// <summary>One AI supply purchase: a tenth of a shelf.</summary>
    private const float AiPurchaseFraction = 0.1f;

    /// <summary>Words in a building's name that mean "this holds fuel".</summary>
    private static readonly string[] FuelStoreNameHints = { "fuel", "storage tank", "tank farm", "depot" };

    private static readonly Dictionary<UnitDefinition, bool> HeavyRoundDefinitions = new();

    private static float? chosenPurchasePercent;

    private static float bunkerAmmoKg;

    private readonly Dictionary<Unit, float> fuel = new();
    private readonly List<Unit> storeSweep = new();
    private readonly List<Unit> scratch = new();
    private readonly List<Unit> nearby = new();
    private readonly HashSet<Unit> assigned = new();

    private readonly HashSet<FactionHQ> seededStartingStock = new();

    /// <summary>Depots this player keeps topped up to the order size (AUTO FUEL / AUTO AMMO).</summary>
    private readonly HashSet<Unit> autoFuel = new();
    private readonly HashSet<Unit> autoAmmo = new();

    private float nextScanAt;
    private float nextAutoBuyAt;
    private float nextWarnAt;
    private bool warnedLow;
    private bool warnedEmpty;
    private bool loggedRoster;

    internal CommanderSupplyStoreService()
    {
        Instance = this;
        CommanderNetwork.OnHost("supply.buy", HostBuy);
        CommanderNetwork.OnHost("supply.base", HostBuyBase);
        CommanderNetwork.OnHost("supply.load", HostLoadFromDump);
        CommanderNetwork.OnGuest("supply.done", GuestBought);
        CommanderNetwork.OnSync(SyncFuel);
        CommanderNetwork.OnGuest("supply.fuel", ApplyFuel);
    }

    internal static CommanderSupplyStoreService? Instance { get; private set; }

    /// <summary>The standard fuel shelf, and the basis every fuel price is quoted per kilogram off.</summary>
    internal static float FuelCapacityKg => Mathf.Max(1f, CommanderSettings.SupplyFuelCapacityKg);

    /// <summary>
    /// How much of a shelf one BUY delivers, 1 to 100 percent, picked freely beside the button.
    /// Until the player touches it, it is a quarter of a shelf. Every price on the screen
    /// prorates off that unit, so a bigger order costs proportionally more. It is the
    /// <em>amount</em> that varies, not the rate, which is why this is not
    /// <c>SupplyPurchaseFraction</c> — that setting is the price basis and moving it would be a
    /// discount slider.
    /// </summary>
    internal static float PurchasePercent
    {
        get => chosenPurchasePercent ?? DefaultPurchasePercent;
        set => chosenPurchasePercent = ClampPercent(value);
    }

    /// <summary>"50%" — how much of a shelf the current BUY size delivers, for a button label.</summary>
    internal static string PurchaseSizeLabel => $"{GetPurchaseFraction() * 100f:0}%";

    public void ResetSession()
    {
        fuel.Clear();
        storeSweep.Clear();
        scratch.Clear();
        nearby.Clear();
        assigned.Clear();
        warnedLow = false;
        warnedEmpty = false;
        loggedRoster = false;
        DumpRearmers.RemoveWhere(rearmer => rearmer == null);
        seededStartingStock.Clear();
        autoFuel.Clear();
        autoAmmo.Clear();
    }

    public void TickPersistent()
    {
        // Each player's own toggles, on their own machine: a guest's buys forward to the host like a click.
        if ((autoFuel.Count > 0 || autoAmmo.Count > 0) && CommanderScheduler.IsDue(ref nextAutoBuyAt, ScanIntervalSeconds))
        {
            AutoBuy(autoFuel, fuel: true);
            AutoBuy(autoAmmo, fuel: false);
        }

        // A dedicated server has no team of its own; the team with players stands in for it.
        CommanderNetwork.RunAsHostTeam(TickPersistentAsHost);
    }

    internal static float GetFuelCapacityKg(Unit? store)
    {
        if (store is GroundVehicle)
        {
            return GetRearmer(store) != null ? MunitionsTruckFuelKg : FuelTruckFuelKg;
        }

        return store != null
            && store.definition is BuildingDefinition building
            && building.buildingType == BuildingType.AMMO
            ? MunitionsBunkerFuelKg
            : FuelCapacityKg;
    }

    /// <summary>Whole percent, 1 to 100: a BUY of nothing, or of more than a shelf, is not an order.</summary>
    internal static float ClampPercent(float percent)
    {
        return Mathf.Clamp(Mathf.Round(percent), 1f, 100f);
    }

    /// <summary>
    /// The fraction of a shelf one BUY delivers. <paramref name="percent"/> of 0 means "whatever the
    /// local picker says"; a guest sends its own percentage over the wire, because the buy itself
    /// runs on the host and the host's picker is not the one that was clicked.
    /// </summary>
    internal static float GetPurchaseFraction(float percent = 0f)
    {
        return (percent > 0f ? ClampPercent(percent) : PurchasePercent) / 100f;
    }

    /// <summary>The <c>Rearmer</c> that makes this unit a supply store, if it is one.</summary>
    /// <remarks>
    /// <c>Unit.RpcUpdateRearmerCapacity</c> reads the component off the unit's own object, so this
    /// looks in exactly the same place — a rearmer found deeper in the hierarchy could be read here
    /// and then never written back.
    /// </remarks>
    internal static Rearmer? GetRearmer(Unit? unit)
    {
        // In children too: a Rearmer names its Unit in a serialized field, so it need not sit on the
        // unit's own object, and a floating supply pallet is exactly the kind of prefab that nests it.
        return unit == null || unit.disabled || CommanderEconomyService.IsIncomeBuildingUnit(unit)
            ? null
            : unit.GetComponentInChildren<Rearmer>();
    }

    /// <summary>
    /// A building that holds fuel but carries no <c>Rearmer</c> — the game's fuel depots and tank
    /// farms. They are stores too: the mod's fuel pool is its own number and does not need the
    /// base game's ammunition component to hang off.
    /// </summary>
    /// <remarks>
    /// <c>BuildingType</c> is the game's own filing, so DEP and AMMO come for free. The
    /// name check behind it is the fallback for the tank farms the encyclopedia files under CIV or
    /// FAC — add a word to <see cref="FuelStoreNameHints"/> if a depot is still missed. The roster
    /// logged once per mission names every building and its type, which is how you find out.
    /// </remarks>
    internal static bool IsFuelStore(Unit? unit)
    {
        // The Fuel Exchange and the Gas Export Terminal wear fuel prefabs but are income only: no
        // shelf, no BUY, the same as a gold mine.
        if (unit == null || unit.disabled || CommanderEconomyService.IsIncomeBuildingUnit(unit))
        {
            return false;
        }

        // A fuel truck carries no Rearmer, so only its name says it holds fuel.
        if (unit is GroundVehicle)
        {
            return IsFuelTruckName(unit.definition != null ? unit.definition.unitName : null);
        }

        if (unit is not Building)
        {
            return false;
        }

        if (unit.definition is BuildingDefinition building
            && (building.buildingType == BuildingType.DEP || building.buildingType == BuildingType.AMMO))
        {
            return true;
        }

        return MatchesFuelStoreName(unit.definition != null ? unit.definition.unitName : null);
    }

    internal static bool MatchesFuelStoreName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        for (int i = 0; i < FuelStoreNameHints.Length; i++)
        {
            if (name!.IndexOf(FuelStoreNameHints[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsFuelTruckName(string? name)
    {
        return name != null && name.IndexOf("fuel", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static bool IsSupplyStore(Unit? unit)
    {
        return GetRearmer(unit) != null || IsFuelStore(unit);
    }

    /// <summary>
    /// A munitions or fuel truck: a store that drives. It cannot be bought into, it fills up from a
    /// bunker (<see cref="RefillTruck"/>), or the BUY button would be a free depot on wheels.
    /// </summary>
    internal static bool IsMobileStore(Unit? unit)
    {
        return unit is not Building && IsSupplyStore(unit);
    }

    /// <summary>An ammo dump: a forward store that trucks fill and empty, never the treasury.</summary>
    internal static bool IsDump(Unit? unit)
    {
        Rearmer? rearmer = unit is Building ? GetRearmer(unit) : null;
        return rearmer != null && DumpRearmers.Contains(rearmer);
    }

    /// <summary>A store with no BUY: a truck, or a dump that trucks deliver to.</summary>
    internal static bool IsTruckFed(Unit? unit)
    {
        return IsMobileStore(unit) || IsDump(unit);
    }

    /// <summary>Ammunition left in this store, 0-1. Read straight off the base game's own counter.</summary>
    internal static float GetAmmoFraction(Unit? unit)
    {
        Rearmer? rearmer = GetRearmer(unit);
        if (rearmer == null)
        {
            return 0f;
        }

        float max = rearmer.GetMaxCapacity();
        return max <= 0f ? 0f : Mathf.Clamp01(rearmer.Capacity / max);
    }

    /// <summary>Every supply store the faction currently holds.</summary>
    internal static void CollectStores(FactionHQ? hq, List<Unit> into)
    {
        into.Clear();
        if (hq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit) && IsSupplyStore(unit))
            {
                into.Add(unit);
            }
        }
    }

    internal static float GetTotalAmmoKg(List<Unit> stores)
    {
        float total = 0f;
        for (int i = 0; i < stores.Count; i++)
        {
            total += GetRearmer(stores[i])?.Capacity ?? 0f;
        }

        return total;
    }

    /// <summary>Kilograms of ordnance a unit is missing, in the units a rearmer holds.</summary>
    internal static float GetMissingAmmoKg(Unit? unit)
    {
        List<WeaponStation>? stations = unit?.weaponStations;
        if (stations == null)
        {
            return 0f;
        }

        float mass = 0f;
        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (station == null || info == null || info.cargo || info.massPerRound <= 0f)
            {
                continue;
            }

            mass += Mathf.Max(0, station.FullAmmo - station.GetAmmoTotal()) * info.massPerRound;
        }

        return mass;
    }

    internal static float GetMissingFuelKg(Aircraft? aircraft)
    {
        List<FuelTank>? tanks = aircraft == null || aircraft.disabled ? null : aircraft.GetFuelTanks();
        if (tanks == null)
        {
            return 0f;
        }

        float missing = 0f;
        for (int i = 0; i < tanks.Count; i++)
        {
            missing += Mathf.Max(0f, tanks[i].GetCapacity() - tanks[i].GetLevel());
        }

        return missing;
    }

    /// <summary>What <paramref name="kg"/> costs at a depot's price of one purchase per quarter of <paramref name="depotKg"/>.</summary>
    internal static float GetBaseCost(float pricePerPurchase, float kg, float depotKg)
    {
        float unit = Mathf.Max(1f, depotKg * CommanderSettings.SupplyPurchaseFraction);
        return pricePerPurchase * Mathf.Max(0f, kg) / unit;
    }

    /// <summary>
    /// A munitions bunker's ammunition shelf, read off the loaded bunker prefabs. A base BUY is priced
    /// against its largest shelf, so anything priced against this costs the same per kilogram.
    /// </summary>
    internal static float GetBunkerAmmoKg()
    {
        if (bunkerAmmoKg <= 0f)
        {
            foreach (BuildingDefinition building in Resources.FindObjectsOfTypeAll<BuildingDefinition>())
            {
                Rearmer? rearmer = building != null && building.buildingType == BuildingType.AMMO && building.unitPrefab != null
                    ? building.unitPrefab.GetComponentInChildren<Rearmer>(true)
                    : null;
                if (rearmer != null)
                {
                    // A prefab never ran Awake, which is what copies Capacity into maxCapacity.
                    bunkerAmmoKg = Mathf.Max(bunkerAmmoKg, rearmer.GetMaxCapacity(), rearmer.Capacity);
                }
            }
        }

        return bunkerAmmoKg > 0f ? bunkerAmmoKg : MunitionsTruckAmmoKg;
    }

    internal static float GetBaseWanted(float have, float max, float percent = 0f)
    {
        return Mathf.Max(0f, Mathf.Min(max * GetPurchaseFraction(percent), max - have));
    }

    /// <summary>
    /// What a unit costs to put on the field, in kilograms off the nearest shelves. Scaled off the
    /// unit's own <c>value</c> so a patched-in vehicle prices itself and a heavy jet costs more to
    /// launch than a jeep costs to roll out.
    /// </summary>
    internal static float GetSpawnSupplyKg(UnitDefinition? definition)
    {
        // Capped at 100 so an expensive unit drawing tonnes off one bunker is dear, not unbuyable.
        float perValue = Mathf.Max(0f, CommanderSettings.SpawnSupplyKgPerValue);
        return definition == null ? 0f : Mathf.Clamp(definition.value, 0f, 100f) * perValue;
    }

    /// <summary>Fuel a spawn draws: the usual share plus, for a supply truck, the load it rolls out with.</summary>
    internal static float GetSpawnFuelKg(UnitDefinition? definition)
    {
        return GetSpawnSupplyKg(definition) + GetTruckLoadKg(definition, fuel: true);
    }

    internal static float GetSpawnAmmoKg(UnitDefinition? definition)
    {
        float kg = GetSpawnSupplyKg(definition) + GetTruckLoadKg(definition, fuel: false);
        return IsArtillery(definition) ? kg * ArtilleryAmmoMultiplier : kg;
    }

    /// <summary>
    /// Artillery that pays the <see cref="ArtilleryAmmoMultiplier"/> surcharge. The MSV ballistic
    /// launchers are filed as <c>ART</c> too, but a 2.5 t Piledriver is already priced by its weight:
    /// at 3x one launcher's two-round reload took 15 t and emptied a dump while its rounds still sat
    /// out the 300 s reload. A launcher whose rounds weigh a tonne or more is left out.
    /// </summary>
    internal static bool IsArtillery(UnitDefinition? definition)
    {
        if (definition is not VehicleDefinition { vehicleType: VehicleType.ART })
        {
            return false;
        }

        if (!HeavyRoundDefinitions.TryGetValue(definition, out bool heavy))
        {
            heavy = false;
            Weapon[] weapons = definition.unitPrefab == null ? Array.Empty<Weapon>() : definition.unitPrefab.GetComponentsInChildren<Weapon>(true);
            for (int i = 0; i < weapons.Length && !heavy; i++)
            {
                heavy = weapons[i] != null && weapons[i].info != null && weapons[i].info.massPerRound >= StrategicRoundKg;
            }

            HeavyRoundDefinitions[definition] = heavy;
        }

        return !heavy;
    }

    /// <summary>
    /// Charges an artillery refill the <see cref="ArtilleryAmmoMultiplier"/> surcharge on top of what
    /// <c>Rearmer.ProcessRearmRequest</c> just drew. A shelf that cannot cover it is emptied, not refused:
    /// the rounds are already in the tubes.
    /// </summary>
    internal static void ChargeArtilleryRefill(Rearmer rearmer, Unit rearmed, float capacityBefore)
    {
        float drawn = capacityBefore - rearmer.Capacity;
        if (drawn > 0f && rearmer.Unit != null && IsArtillery(rearmed.definition))
        {
            SetAmmoKg(rearmer.Unit, rearmer, rearmer.Capacity - (drawn * (ArtilleryAmmoMultiplier - 1f)));
        }
    }

    /// <summary>
    /// Loads what <c>Rearmer.ProcessRearmRequest</c> left empty straight into the launchers, paid for
    /// by weight off the same shelf, so every ground vehicle and ship can rearm at any store, ammo
    /// dumps included. The base game skips weapons it does not author as rearmable, which left mobile
    /// air defence unable to reload anywhere. A weapon still sitting out a reload is left to the base
    /// game, and so are warheads and strategic rounds, which have their own gates. Host only.
    /// </summary>
    internal static int TopUpWeapons(Rearmer rearmer, Unit unit)
    {
        Unit? store = rearmer.Unit;
        if (store == null || unit is Aircraft || unit.disabled || !store.IsServer || unit.weaponStations == null)
        {
            return 0;
        }

        float multiplier = IsArtillery(unit.definition) ? ArtilleryAmmoMultiplier : 1f;
        int loaded = 0;
        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            WeaponStation station = unit.weaponStations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (station?.Weapons == null
                || info == null
                || info.nuclear
                || info.massPerRound >= StrategicRoundKg
                || station.GetAmmoTotal() > station.GetAmmoLoaded())
            {
                continue;
            }

            bool changed = false;
            for (int w = 0; w < station.Weapons.Count; w++)
            {
                Weapon weapon = station.Weapons[w];
                if (weapon == null || !weapon.IsAttached())
                {
                    continue;
                }

                int missing = weapon.GetFullAmmo() - weapon.ammo;
                WeaponInfo roundInfo = weapon.info != null ? weapon.info : info;
                float perRound = Mathf.Max(0f, roundInfo.massPerRound) * multiplier;
                int rounds = perRound <= 0f ? missing : Mathf.Min(missing, Mathf.FloorToInt(rearmer.Capacity / perRound));
                if (rounds <= 0)
                {
                    continue;
                }

                weapon.ammo += rounds;
                SetAmmoKg(store, rearmer, rearmer.Capacity - (rounds * perRound));
                loaded += rounds;
                changed = true;
            }

            if (changed)
            {
                station.AccountAmmo();
                station.Updated();
            }
        }

        return loaded;
    }

    /// <summary>
    /// A truck spawns full, so its load comes off the shelves it leaves from, or a depot queue would
    /// be free stock. Same tests as the live unit: a <c>Rearmer</c> is a munitions truck, "fuel" in
    /// the name a fuel truck.
    /// </summary>
    internal static float GetTruckLoadKg(UnitDefinition? definition, bool fuel)
    {
        if (definition is not VehicleDefinition vehicle
            || vehicle.unitPrefab == null
            || CommanderGameAccess.IsTrailerVehicleDefinition(vehicle))
        {
            return 0f;
        }

        if (vehicle.unitPrefab.GetComponentInChildren<Rearmer>(true) != null)
        {
            return fuel ? MunitionsTruckFuelKg : MunitionsTruckAmmoKg;
        }

        return fuel && IsFuelTruckName(vehicle.unitName) ? FuelTruckFuelKg : 0f;
    }

    /// <summary>
    /// The pure arithmetic behind a BUY button, run once at load. A depot with an unreachable top
    /// or a purchase that charges full price for a sliver is the kind of thing that only shows up
    /// after a config retune, and the repo has nowhere else to run a test.
    /// </summary>
    internal static void SelfCheck()
    {
        float fraction = Mathf.Clamp(CommanderSettings.SupplyPurchaseFraction, 0.01f, 1f);
        float capacity = FuelCapacityKg;
        float unit = capacity * fraction;
        if (unit <= 0f || capacity <= 0f)
        {
            CommanderPlugin.Log.LogError(
                "Supply self-check FAILED: a purchase delivers nothing. Check the Supply section of the config.");
            return;
        }

        // Filling an empty depot must terminate, and the last (part) purchase must cost less than
        // a whole one — that proration is the only arithmetic on the button.
        float level = 0f;
        int buys = 0;
        float charged = 0f;
        while (level < capacity - 1f && buys < 1000)
        {
            float wanted = Mathf.Min(unit, capacity - level);
            charged += CommanderSettings.SupplyFuelPurchaseCost * (wanted / unit);
            level += wanted;
            buys++;
        }

        if (buys >= 1000 || level < capacity - 1f)
        {
            CommanderPlugin.Log.LogError("Supply self-check FAILED: a depot cannot be filled.");
            return;
        }

        float expected = CommanderSettings.SupplyFuelPurchaseCost / fraction;
        if (Mathf.Abs(charged - expected) > expected * 0.01f)
        {
            CommanderPlugin.Log.LogError(
                $"Supply self-check FAILED: filling a depot charged {charged:0.0}, expected {expected:0.0}.");
            return;
        }

        // A base BUY is a quarter of every shelf there, so it must cost one depot price per depot
        // and nothing once the shelf is full.
        float baseQuote = GetBaseCost(CommanderSettings.SupplyFuelPurchaseCost, GetBaseWanted(0f, capacity * 3f), capacity);
        if (Mathf.Abs(baseQuote - (CommanderSettings.SupplyFuelPurchaseCost * 3f)) > 0.01f
            || GetBaseWanted(capacity * 3f, capacity * 3f) > 0f)
        {
            CommanderPlugin.Log.LogError($"Supply self-check FAILED: a three-depot base BUY quoted {baseQuote:0.0}.");
            return;
        }

        // The BUY size must move the amount and the bill together: it is a bigger order, not a
        // cheaper one. Restored afterwards because this runs at load with the player's setting.
        float? savedPercent = chosenPurchasePercent;
        try
        {
            PurchasePercent = 10f;
            float oneWanted = GetBaseWanted(0f, capacity * 8f);
            float oneCost = GetBaseCost(CommanderSettings.SupplyFuelPurchaseCost, oneWanted, capacity);
            PurchasePercent = 20f;
            float twoWanted = GetBaseWanted(0f, capacity * 8f);
            float twoCost = GetBaseCost(CommanderSettings.SupplyFuelPurchaseCost, twoWanted, capacity);

            // Twice the order, or the whole shelf if two units overrun it, and — the part that
            // matters — at the same price per kilogram. A BUY size that moved the rate would be a
            // discount slider, which is exactly what SupplyPurchaseFraction must not be turned into.
            float expectedWanted = Mathf.Min(oneWanted * 2f, capacity * 8f);
            if (oneWanted <= 0f
                || Mathf.Abs(twoWanted - expectedWanted) > expectedWanted * 0.01f
                || Mathf.Abs((twoCost / twoWanted) - (oneCost / oneWanted)) > (oneCost / oneWanted) * 0.01f)
            {
                CommanderPlugin.Log.LogError(
                    $"Supply self-check FAILED: a 20% BUY delivered {twoWanted:0} kg for {twoCost:0.0} "
                        + $"where 10% delivered {oneWanted:0} kg for {oneCost:0.0}.");
                return;
            }
        }
        finally
        {
            chosenPurchasePercent = savedPercent;
        }

        // The name fallback is the only part of "is this a fuel store" that is not the game's own
        // filing, and the cost of getting it wrong is a depot that silently holds nothing.
        string[] shouldMatch = { "Fuel Depot", "storage tank", "Fuel Storage Tank", "Supply Depot" };
        string[] shouldNotMatch = { "Ifrit", "Radar Station", "Hangar", "Gold Mine", string.Empty };
        for (int i = 0; i < shouldMatch.Length; i++)
        {
            if (!MatchesFuelStoreName(shouldMatch[i]))
            {
                CommanderPlugin.Log.LogError($"Supply self-check FAILED: \"{shouldMatch[i]}\" is not read as a fuel store.");
                return;
            }
        }

        for (int i = 0; i < shouldNotMatch.Length; i++)
        {
            if (MatchesFuelStoreName(shouldNotMatch[i]))
            {
                CommanderPlugin.Log.LogError($"Supply self-check FAILED: \"{shouldNotMatch[i]}\" is read as a fuel store.");
                return;
            }
        }

        CommanderPlugin.Log.LogDebug($"Supply self-check passed ({buys} purchases fill a depot).");
    }

    internal float GetFuelKg(Unit? unit)
    {
        if (unit == null)
        {
            return 0f;
        }

        // A store the mod has not seen before starts full: the mission author stocked that depot,
        // the commander did not, and an airbase that begins the match dry reads as a bug.
        if (!fuel.TryGetValue(unit, out float value))
        {
            value = GetFuelCapacityKg(unit);
            fuel[unit] = value;
        }

        return value;
    }

    internal float GetFuelFraction(Unit? unit)
    {
        return Mathf.Clamp01(GetFuelKg(unit) / GetFuelCapacityKg(unit));
    }

    internal bool FactionHasStores(FactionHQ? hq)
    {
        CollectStores(hq, scratch);
        return scratch.Count > 0;
    }

    /// <summary>
    /// Every store the faction holds within <paramref name="rangeMeters"/> of a point, nearest
    /// first. A base's supply is the sum of what stands around it, so a depot dropped anywhere
    /// inside the build radius feeds the field — it does not have to sit beside the strip.
    /// </summary>
    internal void CollectNearbyStores(FactionHQ? hq, GlobalPosition near, float rangeMeters, List<Unit> into)
    {
        into.Clear();
        CollectStores(hq, scratch);
        for (int i = 0; i < scratch.Count; i++)
        {
            if (FastMath.Distance(scratch[i].GlobalPosition(), near) <= rangeMeters)
            {
                into.Add(scratch[i]);
            }
        }

        into.Sort((Unit a, Unit b) =>
            FastMath.Distance(a.GlobalPosition(), near).CompareTo(FastMath.Distance(b.GlobalPosition(), near)));
    }

    internal float GetTotalFuelKg(List<Unit> stores)
    {
        float total = 0f;
        for (int i = 0; i < stores.Count; i++)
        {
            total += GetFuelKg(stores[i]);
        }

        return total;
    }

    /// <summary>
    /// Fuel and ammunition around every airbase the faction holds, nearest to the camera first, plus
    /// one trailing row for depots that stand away from any field. This is the whole point of the
    /// readout: the resupply that emptied a shelf may not have touched the building you are looking
    /// at, and per-building bars can never say that.
    /// </summary>
    internal void CollectBaseSupply(List<BaseSupply> into)
    {
        into.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        CollectStores(hq, storeSweep);
        GlobalPosition eye = GetCameraPosition();
        float range = CommanderSettings.SupplyStoreRangeKm * 1000f;
        assigned.Clear();
        foreach (Airbase airbase in hq.GetAirbases())
        {
            // A ship's deck is listed below as the ship itself.
            if (airbase == null || airbase.center == null || airbase.TryGetAttachedUnit(out _))
            {
                continue;
            }

            GlobalPosition center = airbase.center.GlobalPosition();
            List<Unit> units = new();
            for (int i = 0; i < storeSweep.Count; i++)
            {
                Unit store = storeSweep[i];

                // Ships sail together, so by range every deck's row held the whole fleet and one
                // BUY filled every ship. Each ship is its own row.
                if (store is Ship || FastMath.Distance(store.GlobalPosition(), center) > range)
                {
                    continue;
                }

                units.Add(store);
            }

            into.Add(MakeBaseSupply(CommanderCaptureService.GetAirbaseLabel(airbase), center, eye, units));
        }

        for (int i = 0; i < storeSweep.Count; i++)
        {
            if (storeSweep[i] is Ship ship)
            {
                into.Add(MakeBaseSupply(
                    CommanderGameAccess.GetUnitLabel(ship), ship.GlobalPosition(), eye, new List<Unit> { ship }));
            }
        }

        into.Sort((BaseSupply a, BaseSupply b) => a.DistanceKm.CompareTo(b.DistanceKm));

        int looseCount = 0;
        float looseFuel = 0f;
        float looseFuelMax = 0f;
        float looseAmmo = 0f;
        float looseAmmoMax = 0f;
        for (int i = 0; i < storeSweep.Count; i++)
        {
            Unit store = storeSweep[i];
            if (assigned.Contains(store))
            {
                continue;
            }

            looseCount++;
            looseFuel += GetFuelKg(store);
            looseFuelMax += GetFuelCapacityKg(store);
            Rearmer? rearmer = GetRearmer(store);
            if (rearmer != null)
            {
                looseAmmo += rearmer.Capacity;
                looseAmmoMax += rearmer.GetMaxCapacity();
            }
        }

        if (looseCount > 0)
        {
            into.Add(new BaseSupply(
                "FIELD DEPOTS", float.MaxValue, looseCount, looseFuel, looseFuelMax, looseAmmo, looseAmmoMax));
        }
    }

    /// <summary>
    /// Fills an aircraft's tanks and racks out of the nearest store. Returns false — and says why —
    /// when no store can cover it, which is the signal to leave the aircraft parked where it is.
    /// </summary>
    internal bool TryResupplyAircraft(Aircraft? aircraft, Airbase? airbase, Loadout? preferredLoadout, out string message)
    {
        message = string.Empty;
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null || hq == null || aircraft.disabled)
        {
            return false;
        }

        float wantFuel = GetMissingFuelKg(aircraft);

        // Full tanks are what ran it off the end of this runway before: a refuelled A-19 Brawler
        // left Dustbowl's 889 m strip on its wheels at 59 m/s. Fill to the type's standard load.
        float safeLevel = CommanderAirCommandService.GetSafeFuelLevel(airbase, aircraft.definition);
        if (safeLevel < 1f)
        {
            float capacity = 0f;
            List<FuelTank> tanks = aircraft.GetFuelTanks();
            for (int i = 0; i < tanks.Count; i++)
            {
                capacity += tanks[i].GetCapacity();
            }

            wantFuel = Mathf.Max(0f, wantFuel - (capacity * (1f - safeLevel)));
        }

        float wantAmmo = GetMissingAmmoKg(aircraft);
        if (wantFuel <= 1f && wantAmmo <= 1f)
        {
            message = "already fuelled and armed";
            return true;
        }

        // No depot anywhere: the mission never shipped one, so resupply is free rather than a
        // silent dead end. The first depot the faction owns switches the whole rule on.
        if (!FactionHasStores(hq))
        {
            Refuel(aircraft, wantFuel);
            Rearm(aircraft, airbase, preferredLoadout);
            message = "resupplied (this map has no supply depot)";
            return true;
        }

        // Every depot within range is one shelf. Fuel and ammunition are drawn separately because
        // they live on different buildings: a fuel depot carries no Rearmer and a munitions bunker
        // is where the ordnance is, and an aircraft parked between them should get both.
        CollectNearbyStores(
            hq, aircraft.GlobalPosition(), CommanderSettings.SupplyStoreRangeKm * 1000f, nearby);
        if (nearby.Count == 0)
        {
            message = $"no supply depot within {CommanderSettings.SupplyStoreRangeKm:0} km";
            return false;
        }

        string baseName = airbase != null
            ? CommanderCaptureService.GetAirbaseLabel(airbase)
            : CommanderGameAccess.GetUnitLabel(nearby[0]);
        float availableFuel = GetTotalFuelKg(nearby);
        float availableAmmo = GetTotalAmmoKg(nearby);
        if (wantFuel > availableFuel || wantAmmo > availableAmmo)
        {
            message = wantFuel > availableFuel && wantAmmo > availableAmmo
                ? $"{baseName} is out of fuel and ammunition"
                : wantFuel > availableFuel
                    ? $"{baseName} is out of fuel"
                    : $"{baseName} is out of ammunition";
            return false;
        }

        float drawnFuel = 0f;
        float drawnAmmo = 0f;
        if (wantFuel > 0f)
        {
            // Charged for what actually went in. The base game will not refill a holed tank
            // (FuelTank.Refuel returns false while it leaks), and charging for the want instead let
            // one shot-up Chicane "take" a full tank every fifteen seconds until Agrapol's fuel was
            // gone, with nothing in its tanks to show for it.
            Refuel(aircraft, wantFuel);
            float delivered = wantFuel - GetMissingFuelKg(aircraft);
            drawnFuel = delivered > 0f ? DrawFuel(nearby, delivered) : 0f;
        }

        if (wantAmmo > 0f)
        {
            drawnAmmo = DrawAmmo(nearby, wantAmmo);
            Rearm(aircraft, airbase, preferredLoadout);
        }

        if (wantFuel > 1f && drawnFuel < 1f)
        {
            message = "a fuel tank is holed and cannot be refilled";
            return false;
        }

        // Logged because "the ammunition bar never moved" is not a thing the UI can answer on its
        // own: an aircraft that landed for fuel with a full rack is supposed to cost no ordnance,
        // and that reads identically to a draw that failed.
        CommanderPlugin.Log.LogInfo(
            $"Resupply at {baseName}: {CommanderGameAccess.GetUnitLabel(aircraft)} took "
                + $"{drawnFuel:0} kg fuel and {drawnAmmo:0} kg ordnance from {nearby.Count} depot(s); "
                + $"{availableFuel - drawnFuel:0} kg fuel and {availableAmmo - drawnAmmo:0} kg ordnance left.");

        message = $"refuelled and rearmed at {baseName}";
        return true;
    }

    /// <summary>
    /// A quarter of the base's whole fuel shelf, then every depot at the base levelled to the same
    /// fill, so the bunker and the fuel farm read alike. Priced per kilogram like a depot's own BUY.
    /// </summary>
    internal bool TryBuyBaseFuel(string baseName, List<Unit>? stores, out string message, float percent = 0f)
    {
        if (CommanderDraftService.IsDraftMission)
        {
            message = DraftNothingForSale;
            return false;
        }

        stores = WithoutDumps(stores);
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || stores == null || stores.Count == 0)
        {
            message = "That base has no supply depot.";
            return false;
        }

        float capacity = FuelCapacityKg;
        float max = 0f;
        for (int i = 0; i < stores.Count; i++)
        {
            max += GetFuelCapacityKg(stores[i]);
        }

        float have = GetTotalFuelKg(stores);
        float wanted = GetBaseWanted(have, max, percent);
        float cost = GetBaseCost(CommanderSettings.SupplyFuelPurchaseCost, wanted, capacity);
        if (!CanPayForBase(hq, baseName, "fuel", wanted, cost, out message))
        {
            return false;
        }

        if (ForwardBaseBuy(baseName, "fuel", stores))
        {
            message = string.Empty;
            return false;
        }

        hq.AddFunds(-cost);
        float fill = Mathf.Clamp01((have + wanted) / max);
        for (int i = 0; i < stores.Count; i++)
        {
            SetFuelKg(stores[i], GetFuelCapacityKg(stores[i]) * fill);
        }

        warnedLow = false;
        warnedEmpty = false;
        message = $"Fuel delivered to {baseName} for {CommanderEconomyService.FundsLabel(cost)}; every depot there at {fill * 100f:0}%.";
        return true;
    }

    /// <summary>The ammunition twin of <see cref="TryBuyBaseFuel"/>; only stores with a rearmer count.</summary>
    internal bool TryBuyBaseAmmo(string baseName, List<Unit>? stores, out string message, float percent = 0f)
    {
        if (CommanderDraftService.IsDraftMission)
        {
            message = DraftNothingForSale;
            return false;
        }

        stores = WithoutDumps(stores);
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float max = 0f;
        float have = 0f;
        float largest = 0f;
        for (int i = 0; stores != null && i < stores.Count; i++)
        {
            Rearmer? rearmer = GetRearmer(stores[i]);
            if (rearmer != null)
            {
                max += rearmer.GetMaxCapacity();
                have += rearmer.Capacity;
                largest = Mathf.Max(largest, rearmer.GetMaxCapacity());
            }
        }

        if (hq == null || max <= 0f)
        {
            message = "That base has no munitions store.";
            return false;
        }

        float wanted = GetBaseWanted(have, max, percent);
        float cost = GetBaseCost(CommanderSettings.SupplyAmmoPurchaseCost, wanted, largest);
        if (!CanPayForBase(hq, baseName, "ammunition", wanted, cost, out message))
        {
            return false;
        }

        if (ForwardBaseBuy(baseName, "ammo", stores!))
        {
            message = string.Empty;
            return false;
        }

        hq.AddFunds(-cost);
        float fill = Mathf.Clamp01((have + wanted) / max);
        for (int i = 0; i < stores!.Count; i++)
        {
            Rearmer? rearmer = GetRearmer(stores[i]);
            if (rearmer != null)
            {
                SetAmmoKg(stores[i], rearmer, rearmer.GetMaxCapacity() * fill);
            }
        }

        warnedLow = false;
        warnedEmpty = false;
        message = $"Ammunition delivered to {baseName} for {CommanderEconomyService.FundsLabel(cost)}; every store there at {fill * 100f:0}%.";
        return true;
    }

    internal bool IsAutoBuying(Unit depot, bool fuel)
    {
        return (fuel ? autoFuel : autoAmmo).Contains(depot);
    }

    internal void SetAutoBuy(Unit depot, bool fuel, bool on)
    {
        HashSet<Unit> set = fuel ? autoFuel : autoAmmo;
        if (on)
        {
            set.Add(depot);
        }
        else
        {
            set.Remove(depot);
        }
    }

    internal bool TryBuyFuel(Unit? store, out string message, float percent = 0f)
    {
        if (CommanderDraftService.IsDraftMission)
        {
            message = DraftNothingForSale;
            return false;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float capacity = FuelCapacityKg;
        if (store == null || hq == null || !IsSupplyStore(store))
        {
            message = "That is not a supply depot.";
            return false;
        }

        if (IsTruckFed(store))
        {
            message = IsDump(store)
                ? "An ammo dump is stocked by munitions and fuel trucks, not from the treasury."
                : "A supply truck fills up at a bunker, not from the treasury.";
            return false;
        }

        // A BUY is a share of this store's own shelf, the bar the player is looking at: a share of
        // the standard shelf put 5 % into a bunker's five-times-bigger tank on a 25 % BUY. The price
        // per kilogram stays the standard shelf's, so a bunker's bigger tank is not a discount.
        float shelf = GetFuelCapacityKg(store);
        float wanted = Mathf.Min(shelf * GetPurchaseFraction(percent), shelf - GetFuelKg(store));
        if (wanted <= 1f)
        {
            message = "That depot's fuel is already full.";
            return false;
        }

        float cost = CommanderSettings.SupplyFuelPurchaseCost
            * (wanted / (capacity * CommanderSettings.SupplyPurchaseFraction));
        if (hq.factionFunds < cost)
        {
            message = $"Fuel costs {CommanderEconomyService.FundsLabel(cost)}; the faction has "
                + $"{CommanderEconomyService.FundsLabel(hq.factionFunds)}.";
            return false;
        }

        // The host holds the fuel pool and the treasury; it answers with supply.done.
        if (CommanderNetwork.Forward(
            "supply.buy", CommanderNetwork.Id(store), "fuel", CommanderNetwork.Num(GetPurchaseFraction(percent) * 100f)))
        {
            message = string.Empty;
            return false;
        }

        hq.AddFunds(-cost);
        SetFuelKg(store, GetFuelKg(store) + wanted);
        warnedLow = false;
        warnedEmpty = false;
        message = $"Fuel delivered to {CommanderGameAccess.GetUnitLabel(store)} for "
            + $"{CommanderEconomyService.FundsLabel(cost)}.";
        return true;
    }

    internal bool TryBuyAmmo(Unit? store, out string message, float percent = 0f)
    {
        if (CommanderDraftService.IsDraftMission)
        {
            message = DraftNothingForSale;
            return false;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        Rearmer? rearmer = GetRearmer(store);
        if (store == null || hq == null || rearmer == null)
        {
            message = "That is not a supply depot.";
            return false;
        }

        if (IsTruckFed(store))
        {
            message = IsDump(store)
                ? "An ammo dump is stocked by munitions and fuel trucks, not from the treasury."
                : "A supply truck fills up at a bunker, not from the treasury.";
            return false;
        }

        float capacity = rearmer.GetMaxCapacity();
        float wanted = Mathf.Min(capacity * GetPurchaseFraction(percent), capacity - rearmer.Capacity);
        if (wanted <= 1f)
        {
            message = "That depot's ammunition is already full.";
            return false;
        }

        float cost = CommanderSettings.SupplyAmmoPurchaseCost
            * (wanted / (capacity * CommanderSettings.SupplyPurchaseFraction));
        if (hq.factionFunds < cost)
        {
            message = $"Ammunition costs {CommanderEconomyService.FundsLabel(cost)}; the faction has "
                + $"{CommanderEconomyService.FundsLabel(hq.factionFunds)}.";
            return false;
        }

        if (CommanderNetwork.Forward(
            "supply.buy", CommanderNetwork.Id(store), "ammo", CommanderNetwork.Num(GetPurchaseFraction(percent) * 100f)))
        {
            message = string.Empty;
            return false;
        }

        hq.AddFunds(-cost);
        SetAmmoKg(store, rearmer, rearmer.Capacity + wanted);
        warnedLow = false;
        warnedEmpty = false;
        message = $"Ammunition delivered to {CommanderGameAccess.GetUnitLabel(store)} for "
            + $"{CommanderEconomyService.FundsLabel(cost)}.";
        return true;
    }

    /// <summary>
    /// A truck that drove to a bunker takes what it has room for, out of that bunker's own shelves.
    /// Host only: both numbers are server state. Returns a line for the player, empty if nothing moved.
    /// </summary>
    internal string RefillTruck(Unit truck, Unit bunker)
    {
        float movedFuel = 0f;
        if (IsSupplyStore(bunker))
        {
            movedFuel = Mathf.Min(GetFuelCapacityKg(truck) - GetFuelKg(truck), GetFuelKg(bunker));
            if (movedFuel > 0f)
            {
                SetFuelKg(bunker, GetFuelKg(bunker) - movedFuel);
                SetFuelKg(truck, GetFuelKg(truck) + movedFuel);
            }
        }

        float movedAmmo = 0f;
        Rearmer? from = GetRearmer(bunker);
        Rearmer? into = GetRearmer(truck);
        if (from != null && into != null)
        {
            movedAmmo = Mathf.Min(into.GetMaxCapacity() - into.Capacity, from.Capacity);
            if (movedAmmo > 0f)
            {
                SetAmmoKg(bunker, from, from.Capacity - movedAmmo);
                SetAmmoKg(truck, into, into.Capacity + movedAmmo);
            }
        }

        return movedFuel < 1f && movedAmmo < 1f
            ? string.Empty
            : $"{CommanderGameAccess.GetUnitLabel(truck).ToUpperInvariant()} LOADED {movedFuel:0} KG FUEL, {movedAmmo:0} KG AMMO";
    }

    /// <summary>
    /// The nearest friendly bunker or fuel depot (not a dump) within <paramref name="reach"/> of a
    /// supply truck, for a truck whose plain travel point ended beside one: players park a truck on
    /// the bunker rather than right-clicking it, and a mission bunker is often not clickable at all.
    /// <paramref name="includeDumps"/> lets a dump answer too, for a truck passing one on a route.
    /// </summary>
    internal Unit? FindRefillStore(Unit truck, float reach, bool includeDumps = false)
    {
        CollectStores(truck.NetworkHQ, scratch);
        Unit? found = null;
        float best = reach;
        for (int i = 0; i < scratch.Count; i++)
        {
            float distance = FastMath.Distance(scratch[i].GlobalPosition(), truck.GlobalPosition());
            if (distance <= best && scratch[i] is Building && (includeDumps || !IsDump(scratch[i])))
            {
                best = distance;
                found = scratch[i];
            }
        }

        return found;
    }

    /// <summary>
    /// A truck that drove to an ammo dump hands over what the dump has room for: the dump is a
    /// forward store and trucks are the only way anything reaches it. Host only.
    /// </summary>
    internal string UnloadTruck(Unit truck, Unit dump)
    {
        float movedFuel = Mathf.Min(GetFuelCapacityKg(dump) - GetFuelKg(dump), GetFuelKg(truck));
        if (movedFuel > 0f)
        {
            SetFuelKg(truck, GetFuelKg(truck) - movedFuel);
            SetFuelKg(dump, GetFuelKg(dump) + movedFuel);
        }

        float movedAmmo = 0f;
        Rearmer? from = GetRearmer(truck);
        Rearmer? into = GetRearmer(dump);
        if (from != null && into != null)
        {
            movedAmmo = Mathf.Min(into.GetMaxCapacity() - into.Capacity, from.Capacity);
            if (movedAmmo > 0f)
            {
                SetAmmoKg(truck, from, from.Capacity - movedAmmo);
                SetAmmoKg(dump, into, into.Capacity + movedAmmo);
            }
        }

        return movedFuel < 1f && movedAmmo < 1f
            ? string.Empty
            : $"{CommanderGameAccess.GetUnitLabel(truck).ToUpperInvariant()} UNLOADED {movedFuel:0} KG FUEL, {movedAmmo:0} KG AMMO";
    }

    /// <summary>
    /// The truck's LOAD FROM DUMP button: takes stock back out of the nearest friendly dump within
    /// <see cref="TruckDumpReachMeters"/>, the reverse of the automatic unload.
    /// </summary>
    internal bool TryLoadFromDump(Unit truck, out string message)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !IsMobileStore(truck))
        {
            message = "Only a supply truck can load from a dump.";
            return false;
        }

        if (CommanderNetwork.Forward("supply.load", CommanderNetwork.Id(truck)))
        {
            message = string.Empty;
            return false;
        }

        CollectStores(hq, scratch);
        Unit? dump = null;
        float best = TruckDumpReachMeters;
        for (int i = 0; i < scratch.Count; i++)
        {
            float distance = FastMath.Distance(scratch[i].GlobalPosition(), truck.GlobalPosition());
            if (distance <= best && IsDump(scratch[i]))
            {
                best = distance;
                dump = scratch[i];
            }
        }

        if (dump == null)
        {
            message = "No friendly ammo dump within 300 m of this truck.";
            return false;
        }

        message = RefillTruck(truck, dump);
        if (message.Length == 0)
        {
            message = "The truck is full, or the dump is empty.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The enemy commander restocking its own depots, at the prices the player pays and only where a
    /// store is actually short — a faction that is not fighting should not burn its balance on
    /// shelves that are already full. Returns what it spent.
    /// </summary>
    internal float TopUpForAi(FactionHQ? hq, float budget)
    {
        if (hq == null || budget <= 0f)
        {
            return 0f;
        }

        CollectStores(hq, scratch);
        return TopUp(hq, scratch, budget);
    }

    /// <summary>
    /// Buys a tenth of a shelf at a time into the stores around <paramref name="at"/> until they cover
    /// this spawn, the shelves are full, or the budget is gone, so an AI's stock follows what it
    /// fields. The air launch calls it with the air fund's budget; other AI spawns get it from
    /// <see cref="TrySpendSpawnSupply"/>.
    /// </summary>
    internal float TopUpForSpawn(FactionHQ? hq, GlobalPosition at, UnitDefinition? definition, float budget)
    {
        if (hq == null || budget <= 0f)
        {
            return 0f;
        }

        CollectNearbyStores(hq, at, CommanderSettings.SupplyStoreRangeKm * 1000f, nearby);
        float fuelKg = GetSpawnFuelKg(definition);
        float ammoKg = GetSpawnAmmoKg(definition);
        float spent = 0f;
        while (spent < budget)
        {
            bool needFuel = GetTotalFuelKg(nearby) < fuelKg;
            bool needAmmo = GetTotalAmmoKg(nearby) < ammoKg;
            if (!needFuel && !needAmmo)
            {
                break;
            }

            float before = spent;
            for (int i = 0; i < nearby.Count && (needFuel || needAmmo); i++)
            {
                if (needFuel)
                {
                    float cost = BuyAiFuel(hq, nearby[i], budget - spent);
                    spent += cost;
                    needFuel = cost <= 0f;
                }

                Rearmer? rearmer = GetRearmer(nearby[i]);
                if (needAmmo && rearmer != null)
                {
                    float cost = BuyAiAmmo(hq, nearby[i], rearmer, budget - spent);
                    spent += cost;
                    needAmmo = cost <= 0f;
                }
            }

            // Every shelf full, or nothing more it can afford.
            if (spent <= before)
            {
                break;
            }
        }

        return spent;
    }

    /// <summary>
    /// Only a shelf down to its last tenth gets a tenth more. Keeping every shelf over half full put
    /// the AI's money into fuel and ordnance nothing ever drew; what a spawn needs beyond that comes
    /// from <see cref="TopUpForSpawn"/>.
    /// </summary>
    internal float TopUp(FactionHQ hq, List<Unit> stores, float budget)
    {
        float spent = 0f;
        for (int i = 0; i < stores.Count && spent < budget; i++)
        {
            Unit store = stores[i];
            if (GetFuelKg(store) < GetFuelCapacityKg(store) * AiPurchaseFraction)
            {
                spent += BuyAiFuel(hq, store, budget - spent);
            }

            Rearmer? rearmer = GetRearmer(store);
            if (rearmer != null && rearmer.Capacity < rearmer.GetMaxCapacity() * AiPurchaseFraction)
            {
                spent += BuyAiAmmo(hq, store, rearmer, budget - spent);
            }
        }

        return spent;
    }

    /// <summary>
    /// Charges a spawn to the depots around it. Returns false — and says why — when they cannot
    /// cover it, which is the signal to refuse the spawn rather than field a dry unit.
    /// </summary>
    /// <remarks>
    /// This is the one place every unit either commander puts on the field pays for its fuel and
    /// ordnance, so it hangs off the two choke points rather than off each caller:
    /// <c>VehicleDepot.TrySpawnVehicle</c> (the player's depot queue, the factory reserve the game
    /// deploys on its own, and the enemy commander's convoys alike) and
    /// <c>CommanderAirCommandService.LaunchAiAircraft</c> (both commanders' airframes).
    /// Fail-soft on a faction with no depot at all, the same rule <see cref="TryResupplyAircraft"/>
    /// uses.
    /// </remarks>
    internal bool TrySpendSpawnSupply(
        FactionHQ? hq, GlobalPosition at, UnitDefinition? definition, out string message)
    {
        if (definition is AircraftDefinition aircraft && CommanderDraftService.HasPrepaidLaunch(hq, aircraft))
        {
            message = string.Empty;
            CommanderDraftService.UsePrepaidLaunch(hq!, aircraft);
            return true;
        }

        // An AI commander buys what the spawn is short, a tenth of a shelf at a time. Not for an
        // airframe: its launch already did that out of the air fund, and the balance past that is
        // what pays for the airframe itself.
        if (!CanCoverSpawnSupply(hq, at, definition, out message)
            && (definition is AircraftDefinition
                || !CommanderEnemyCommanderService.IsAiFaction(hq, CommanderGameAccess.GetLocalHq())
                || TopUpForSpawn(hq, at, definition, hq!.factionFunds) <= 0f
                || !CanCoverSpawnSupply(hq, at, definition, out message)))
        {
            return false;
        }

        if (nearby.Count > 0)
        {
            DrawFuel(nearby, GetSpawnFuelKg(definition));
            DrawAmmo(nearby, GetSpawnAmmoKg(definition));
        }

        return true;
    }

    /// <summary>
    /// The test <see cref="TrySpendSpawnSupply"/> makes, without spending anything, so a window can
    /// say a purchase will be refused before the player makes it. Leaves the stores it would draw
    /// on in <c>nearby</c>, empty when the spawn is free.
    /// </summary>
    internal bool CanCoverSpawnSupply(
        FactionHQ? hq, GlobalPosition at, UnitDefinition? definition, out string message)
    {
        message = string.Empty;
        nearby.Clear();

        // Advanced tier only, the same gate this whole service ticks under: on a mission where the
        // stores are never scanned or stocked, charging a spawn against them would be a rule with
        // no way to answer it.
        // A drafted unit's first trip out was paid for in draft points, full tanks and racks
        // included. Only an airframe ever comes back into stock, and its next launch is a resupply.
        float wantedFuel = GetSpawnFuelKg(definition);
        float wantedAmmo = GetSpawnAmmoKg(definition);
        if (hq == null
            || (CommanderDraftService.IsDraftMission
                && (definition is not AircraftDefinition || CommanderDraftService.HasPrepaidLaunch(hq, definition)))
            || (wantedFuel <= 0f && wantedAmmo <= 0f)
            || !CommanderFeatureGate.AdvancedFeaturesEnabled
            || !FactionHasStores(hq))
        {
            return true;
        }

        CollectNearbyStores(hq, at, CommanderSettings.SupplyStoreRangeKm * 1000f, nearby);
        if (nearby.Count == 0)
        {
            message = $"no supply depot within {CommanderSettings.SupplyStoreRangeKm:0} km of the spawn";
            return false;
        }

        if (GetTotalFuelKg(nearby) < wantedFuel || GetTotalAmmoKg(nearby) < wantedAmmo)
        {
            message = $"the depots here hold less than the {wantedFuel:0} kg of fuel and {wantedAmmo:0} kg of ordnance this unit needs";
            nearby.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// A shelf a commander just built holds nothing: stock is bought, not built. Only the build
    /// paths call this (player catalogue, enemy supply rung), so mission-authored and Champions-kit
    /// shelves keep their opening stock.
    /// </summary>
    internal void EmptyNewStore(Unit store)
    {
        if (!IsSupplyStore(store))
        {
            return;
        }

        SetFuelKg(store, 0f);
        Rearmer? rearmer = GetRearmer(store);
        if (rearmer != null)
        {
            SetAmmoKg(store, rearmer, 0f);
        }
    }

    /// <summary>A ship comes out of the dock with full fuel and ammunition shelves.</summary>
    internal void FillNewStore(Unit store)
    {
        if (!IsSupplyStore(store))
        {
            return;
        }

        SetFuelKg(store, GetFuelCapacityKg(store));
        Rearmer? rearmer = GetRearmer(store);
        if (rearmer != null)
        {
            SetAmmoKg(store, rearmer, rearmer.GetMaxCapacity());
        }
    }

    /// <summary>The ammunition twin of <see cref="BuyAiFuel"/>.</summary>
    private static float BuyAiAmmo(FactionHQ hq, Unit store, Rearmer rearmer, float budget)
    {
        float shelf = rearmer.GetMaxCapacity();
        float wanted = Mathf.Min(shelf * AiPurchaseFraction, shelf - rearmer.Capacity);
        float cost = CommanderSettings.SupplyAmmoPurchaseCost
            * (wanted / (shelf * CommanderSettings.SupplyPurchaseFraction));
        if (wanted <= 1f || cost > budget || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        SetAmmoKg(store, rearmer, rearmer.Capacity + wanted);
        return cost;
    }

    private static float DrawAmmo(List<Unit> stores, float wantedKg)
    {
        float drawn = 0f;
        for (int i = 0; i < stores.Count && drawn < wantedKg; i++)
        {
            Rearmer? rearmer = GetRearmer(stores[i]);
            float take = rearmer == null ? 0f : Mathf.Min(wantedKg - drawn, rearmer.Capacity);
            if (take <= 0f)
            {
                continue;
            }

            SetAmmoKg(stores[i], rearmer!, rearmer!.Capacity - take);
            drawn += take;
        }

        return drawn;
    }

    private static List<Unit>? WithoutDumps(List<Unit>? stores)
    {
        return stores?.FindAll(store => !IsDump(store));
    }

    /// <summary>
    /// A guest's base-wide BUY. Run locally it only moved the guest's own copy of the treasury and
    /// the shelves, and the host's next sync put both back: a buy that "refunded itself".
    /// </summary>
    private static bool ForwardBaseBuy(string baseName, string kind, List<Unit> stores)
    {
        string[] args = new string[stores.Count + 3];
        args[0] = baseName;
        args[1] = kind;
        args[2] = CommanderNetwork.Num(GetPurchaseFraction() * 100f);
        for (int i = 0; i < stores.Count; i++)
        {
            args[i + 3] = CommanderNetwork.Id(stores[i]);
        }

        return CommanderNetwork.Forward("supply.base", args);
    }

    private static void GuestBought(string[] args)
    {
        string message = CommanderNetwork.Arg(args, 3);
        if (!CommanderNetwork.Bool(args, 2))
        {
            CommanderNotice.Fail(message);
            return;
        }

        CommanderNetwork.TryUnit(args, 1, out Unit store);
        CommanderAlertService.Instance?.NotifySupply(message, store);
    }

    private static GlobalPosition GetCameraPosition()
    {
        CameraStateManager? cameras = SceneSingleton<CameraStateManager>.i;
        return cameras?.mainCamera == null
            ? default
            : cameras.mainCamera.transform.position.ToGlobalPosition();
    }

    private static bool CanPayForBase(FactionHQ hq, string baseName, string what, float wanted, float cost, out string message)
    {
        if (wanted <= 1f)
        {
            message = $"{baseName}'s {what} is already full.";
            return false;
        }

        if (hq.factionFunds < cost)
        {
            message = $"{char.ToUpperInvariant(what[0])}{what.Substring(1)} for {baseName} costs "
                + $"{CommanderEconomyService.FundsLabel(cost)}; the faction has "
                + $"{CommanderEconomyService.FundsLabel(hq.factionFunds)}.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Writes a rearmer's remaining stock. <c>Unit.RpcUpdateRearmerCapacity</c> is the base game's
    /// own path — it calls <c>SetCapacity</c> on the host and mirrors it to every client — so the
    /// number a player reads on a depot stays the number the game is spending.
    /// </summary>
    private static void SetAmmoKg(Unit store, Rearmer rearmer, float kg)
    {
        float clamped = Mathf.Clamp(kg, 0f, rearmer.GetMaxCapacity());

        // Written locally first: the RPC's handler only looks on the unit's own object
        // (TryGetComponent), and GetRearmer also finds one on a child, where the RPC alone
        // charged the BUY and left the shelf where it was.
        rearmer.SetCapacity(clamped);
        if (store.IsServer && rearmer.gameObject == store.gameObject)
        {
            store.RpcUpdateRearmerCapacity(clamped);
        }
    }

    private static void Refuel(Aircraft aircraft, float wantedKg)
    {
        // Aircraft.Refuel is a ClientRpc: only the host may send it.
        if (!aircraft.IsServer)
        {
            return;
        }

        float capacity = 0f;
        List<FuelTank> tanks = aircraft.GetFuelTanks();
        for (int i = 0; i < tanks.Count; i++)
        {
            capacity += tanks[i].GetCapacity();
        }

        if (capacity <= 0f)
        {
            return;
        }

        // Aircraft.Refuel fills every tank to the fuelLevel SyncVar's ratio, so the ratio is what
        // gets written here and the fill follows from it.
        aircraft.NetworkfuelLevel = Mathf.Clamp01(aircraft.GetFuelLevel() + (wantedKg / capacity));
        aircraft.Refuel(aircraft);
    }

    /// <summary>
    /// Puts a full rack back on the aircraft. A fresh <c>Loadout</c> object is required rather than
    /// the one it already carries: <c>Networkloadout</c> is a SyncVar and only fires
    /// <c>onLoadoutChanged</c> — which is what respawns the weapons — when the value differs.
    /// </summary>
    private static void Rearm(Aircraft aircraft, Airbase? airbase, Loadout? preferred)
    {
        Loadout replacement = new();
        if (preferred != null && preferred.weapons.Count > 0)
        {
            replacement.weapons.AddRange(preferred.weapons);
        }
        else
        {
            // SelectAIAircraftWeapons dereferences the airbase it is handed after its own null
            // check, so it must never be given one this side could not resolve.
            Airbase? field = airbase ?? aircraft.NetworkHQ?.GetNearestAirbase(aircraft.transform.position);
            if (field == null)
            {
                return;
            }

            Loadout? chosen = aircraft.weaponManager.SelectAIAircraftWeapons(field);
            if (chosen == null)
            {
                return;
            }

            replacement.weapons.AddRange(chosen.weapons);
        }

        aircraft.Networkloadout = replacement;
    }

    /// <summary>
    /// Spends fuel out of the stores in the list, nearest first, and returns what it got. Spilling
    /// over is the point: one depot half full and another beside it should fill an aircraft between
    /// them rather than leave it grounded next to a shelf that is not empty.
    /// </summary>
    private BaseSupply MakeBaseSupply(string name, GlobalPosition center, GlobalPosition eye, List<Unit> units)
    {
        float fuelKg = 0f;
        float fuelMax = 0f;
        float ammoKg = 0f;
        float ammoMax = 0f;
        float largestAmmo = 0f;
        for (int i = 0; i < units.Count; i++)
        {
            Unit store = units[i];
            assigned.Add(store);
            fuelKg += GetFuelKg(store);
            fuelMax += GetFuelCapacityKg(store);
            Rearmer? rearmer = GetRearmer(store);
            if (rearmer != null)
            {
                ammoKg += rearmer.Capacity;
                ammoMax += rearmer.GetMaxCapacity();
                largestAmmo = Mathf.Max(largestAmmo, rearmer.GetMaxCapacity());
            }
        }

        return new BaseSupply(
            name, FastMath.Distance(center, eye) / 1000f, units.Count, fuelKg, fuelMax, ammoKg, ammoMax, units, largestAmmo);
    }

    private float DrawFuel(List<Unit> stores, float wantedKg)
    {
        float drawn = 0f;
        for (int i = 0; i < stores.Count && drawn < wantedKg; i++)
        {
            float take = Mathf.Min(wantedKg - drawn, GetFuelKg(stores[i]));
            if (take <= 0f)
            {
                continue;
            }

            SetFuelKg(stores[i], GetFuelKg(stores[i]) - take);
            drawn += take;
        }

        return drawn;
    }

    /// <summary>
    /// Empties the player's opening shelves down to <see cref="CommanderSettings.StartingFuelKg"/>
    /// and <see cref="CommanderSettings.StartingAmmoKg"/>, once per mission, on the duel and Champions only.
    /// </summary>
    /// <remarks>
    /// A mission author stocks every depot full, which handed the commander most of a match's
    /// supply before the first order was given — the shelves simply never ran down. Only the local
    /// faction is touched: a hostile commander keeps its authored munitions bunker, because it has
    /// no BUY button to dig itself out with on the opening review.
    /// </remarks>
    private void ApplyStartingStock(FactionHQ hq, List<Unit> stores)
    {
        // Host only: a guest's shelves come from the supply.fuel sync, and this overwrote them.
        if (!hq.IsServer || stores.Count == 0 || !seededStartingStock.Add(hq))
        {
            return;
        }

        // Only the duel and Champions start dry. On the stock missions every base ships full, so a
        // commander is not handed a BUY bill for each of Escalation's airbases before the first order.
        // A draft's buildings are bottomless (RefillDraftBuildings).
        if (!CommanderEnemyCommanderService.IsDuelMission || CommanderDraftService.IsDraftMission)
        {
            return;
        }

        float startFuel = Mathf.Max(0f, CommanderSettings.StartingFuelKg);
        float startAmmo = Mathf.Max(0f, CommanderSettings.StartingAmmoKg);
        for (int i = 0; i < stores.Count; i++)
        {
            Unit store = stores[i];
            if (store is Ship)
            {
                // A faction's first ship can be its first store; it sails out full, not on the opening shoestring.
                continue;
            }

            SetFuelKg(store, Mathf.Min(GetFuelKg(store), startFuel));
            Rearmer? rearmer = GetRearmer(store);
            if (rearmer != null && rearmer.Capacity > startAmmo)
            {
                SetAmmoKg(store, rearmer, startAmmo);
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Opening supply for {hq.faction?.name} set to {startFuel:0} kg fuel and {startAmmo:0} kg ordnance "
                + $"across {stores.Count} depot(s).");
    }

    /// <summary>
    /// A human commander who joined the host's match starts on the same shoestring the host does,
    /// or a two-player duel opens with one side on full shelves. The AI keeps its authored stock.
    /// </summary>
    private void SeedGuestCommanders(FactionHQ localHq)
    {
        if (!localHq.IsServer)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || ReferenceEquals(hq, localHq)
                || !hq.IsServer
                || seededStartingStock.Contains(hq)
                || !CommanderEnemyCommanderService.HasHumanPlayers(hq)
                || CommanderEnemyCommanderService.IsAiFaction(hq, localHq))
            {
                continue;
            }

            CollectStores(hq, scratch);
            ApplyStartingStock(hq, scratch);
        }
    }

    /// <summary>A tenth of this store's fuel shelf at the player's price per kilogram. Returns the cost, 0 when nothing was bought.</summary>
    private float BuyAiFuel(FactionHQ hq, Unit store, float budget)
    {
        float shelf = GetFuelCapacityKg(store);
        float wanted = Mathf.Min(shelf * AiPurchaseFraction, shelf - GetFuelKg(store));
        float cost = CommanderSettings.SupplyFuelPurchaseCost
            * (wanted / (FuelCapacityKg * CommanderSettings.SupplyPurchaseFraction));
        if (wanted <= 1f || cost > budget || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        SetFuelKg(store, GetFuelKg(store) + wanted);
        return cost;
    }

    private void SetFuelKg(Unit store, float kg)
    {
        fuel[store] = Mathf.Clamp(kg, 0f, GetFuelCapacityKg(store));
    }

    private void HostLoadFromDump(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit truck) || !CommanderGameAccess.IsFriendlyUnit(truck, hq))
        {
            return;
        }

        bool loaded = TryLoadFromDump(truck, out string message);
        CommanderNetwork.Reply("supply.done", CommanderNetwork.Id(truck), CommanderNetwork.Flag(loaded), message);
    }

    /// <summary>A guest's BUY button, on the host.</summary>
    private void HostBuy(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit store) || !CommanderGameAccess.IsFriendlyUnit(store, hq))
        {
            return;
        }

        float percent = ClampPercent(CommanderNetwork.Float(args, 3));
        bool bought = CommanderNetwork.Arg(args, 2) == "fuel"
            ? TryBuyFuel(store, out string message, percent)
            : TryBuyAmmo(store, out message, percent);
        CommanderNetwork.Reply("supply.done", CommanderNetwork.Id(store), CommanderNetwork.Flag(bought), message);
    }

    /// <summary>A guest's base-wide BUY, on the host. Every store must be the guest's own.</summary>
    private void HostBuyBase(FactionHQ hq, string[] args)
    {
        List<Unit> stores = new();
        for (int i = 4; i < args.Length; i++)
        {
            if (!CommanderNetwork.TryUnit(args, i, out Unit store) || !CommanderGameAccess.IsFriendlyUnit(store, hq))
            {
                return;
            }

            stores.Add(store);
        }

        string baseName = CommanderNetwork.Arg(args, 1);
        float percent = ClampPercent(CommanderNetwork.Float(args, 3));
        bool bought = CommanderNetwork.Arg(args, 2) == "fuel"
            ? TryBuyBaseFuel(baseName, stores, out string message, percent)
            : TryBuyBaseAmmo(baseName, stores, out message, percent);
        CommanderNetwork.Reply(
            "supply.done", stores.Count > 0 ? CommanderNetwork.Id(stores[0]) : string.Empty, CommanderNetwork.Flag(bought), message);
    }

    /// <summary>
    /// The fuel pool is the mod's own number and lives on the host, where it is spent, so a guest's
    /// FUEL bars are copied from there. Ammunition needs nothing: the base game syncs a rearmer.
    /// </summary>
    private void SyncFuel(FactionHQ hq, List<string[]> messages)
    {
        CollectStores(hq, scratch);
        string[] args = new string[scratch.Count + 1];
        args[0] = "supply.fuel";
        for (int i = 0; i < scratch.Count; i++)
        {
            // Ammunition too: the game only sends a Rearmer's capacity when it changes, so a guest
            // who joined after that still read the prefab's full shelf.
            Rearmer? rearmer = GetRearmer(scratch[i]);
            float ammo = rearmer != null ? rearmer.Capacity : -1f;
            args[i + 1] = $"{CommanderNetwork.Id(scratch[i])} {CommanderNetwork.Num(GetFuelKg(scratch[i]))} {CommanderNetwork.Num(ammo)}";
        }

        messages.Add(args);
    }

    private void ApplyFuel(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            string[] fields = args[i].Split(' ');
            if (fields.Length == 3 && CommanderNetwork.TryUnit(fields[0], out Unit store))
            {
                fuel[store] = CommanderNetwork.Float(fields, 1);
                float ammo = CommanderNetwork.Float(fields, 2);
                if (ammo >= 0f)
                {
                    GetRearmer(store)?.SetCapacity(ammo);
                }
            }
        }
    }

    private void PruneFuel()
    {
        if (fuel.Count == 0)
        {
            return;
        }

        scratch.Clear();
        foreach (KeyValuePair<Unit, float> entry in fuel)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                scratch.Add(entry.Key!);
            }
        }

        for (int i = 0; i < scratch.Count; i++)
        {
            fuel.Remove(scratch[i]);
        }
    }

    /// <summary>
    /// Names the faction's depots once per mission. Which prefabs carry a <c>Rearmer</c> is asset
    /// data a decompile cannot answer and this whole feature hangs off that component, so the
    /// BepInEx console gets the list rather than the reader getting a guess.
    /// </summary>
    private void LogRosterOnce()
    {
        if (loggedRoster)
        {
            return;
        }

        loggedRoster = true;
        if (storeSweep.Count == 0)
        {
            CommanderPlugin.Log.LogInfo(
                "No supply depot on this mission: resupply is free until the faction owns a building "
                    + "or vehicle carrying a Rearmer.");
            return;
        }

        for (int i = 0; i < storeSweep.Count; i++)
        {
            Unit store = storeSweep[i];
            Rearmer? rearmer = GetRearmer(store);
            string type = store.definition is BuildingDefinition building
                ? building.buildingType.ToString()
                : "vehicle";
            if (IsDump(store))
            {
                type += ", dump: trucks only";
            }

            CommanderPlugin.Log.LogInfo(
                $"Supply depot: {CommanderGameAccess.GetUnitLabel(store)} [{type}] holds "
                    + $"{rearmer?.GetMaxCapacity() ?? 0f:0} kg of ammunition and {GetFuelCapacityKg(store):0} kg of fuel.");
        }
    }

    private void WarnOnLowStock()
    {
        if (storeSweep.Count == 0 || !CommanderScheduler.IsDue(ref nextWarnAt, WarnIntervalSeconds))
        {
            return;
        }

        float total = 0f;
        for (int i = 0; i < storeSweep.Count; i++)
        {
            total += GetFuelFraction(storeSweep[i]) + GetAmmoFraction(storeSweep[i]);
        }

        float average = total / (storeSweep.Count * 2f);

        if (average <= 0.01f)
        {
            if (!warnedEmpty)
            {
                warnedEmpty = true;
                warnedLow = true;
                CommanderAlertService.Instance?.NotifySupply("SUPPLY DEPOTS EMPTY - BUY FUEL AND AMMO", null);
            }

            return;
        }

        warnedEmpty = false;
        if (average <= LowFraction)
        {
            if (!warnedLow)
            {
                warnedLow = true;
                CommanderAlertService.Instance?.NotifySupply($"SUPPLY LOW - {average * 100f:0}% ACROSS DEPOTS", null);
            }

            return;
        }

        warnedLow = false;
    }

    /// <summary>
    /// Buys whatever a toggled depot is short of the order size: at 20 % with the shelf on 19 %, 1 %.
    /// A lost or captured depot drops off the list; a refusal (short of funds) waits for the next pass.
    /// </summary>
    private void AutoBuy(HashSet<Unit> depots, bool fuel)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        depots.RemoveWhere(depot => depot == null || depot.disabled || !CommanderGameAccess.IsFriendlyUnit(depot, hq));
        foreach (Unit depot in depots)
        {
            Rearmer? rearmer = fuel ? null : GetRearmer(depot);
            float fill = fuel
                ? GetFuelKg(depot) / Mathf.Max(1f, GetFuelCapacityKg(depot))
                : rearmer == null ? 1f : rearmer.Capacity / Mathf.Max(1f, rearmer.GetMaxCapacity());
            float shortPercent = PurchasePercent - (fill * 100f);
            if (shortPercent < 0.5f)
            {
                continue;
            }

            _ = fuel
                ? TryBuyFuel(depot, out _, Mathf.Ceil(shortPercent))
                : TryBuyAmmo(depot, out _, Mathf.Ceil(shortPercent));
        }
    }

    private void TickPersistentAsHost()
    {
        if (!CommanderScheduler.IsDue(ref nextScanAt, ScanIntervalSeconds))
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        CollectStores(hq, storeSweep);
        ApplyStartingStock(hq, storeSweep);
        SeedGuestCommanders(hq);
        PruneFuel();
        RefillDraftBuildings();
        LogRosterOnce();
        WarnOnLowStock();
    }

    /// <summary>
    /// Ground Control Draft: every side's buildings are bottomless once the battle starts, topped
    /// back up on every scan. Trucks still carry only what they loaded.
    /// </summary>
    private void RefillDraftBuildings()
    {
        if (!CommanderDraftService.IsLive)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer)
            {
                continue;
            }

            CollectStores(hq, scratch);
            for (int i = 0; i < scratch.Count; i++)
            {
                Unit store = scratch[i];
                if (store is not Building)
                {
                    continue;
                }

                if (IsFuelStore(store))
                {
                    SetFuelKg(store, GetFuelCapacityKg(store));
                }

                Rearmer? rearmer = GetRearmer(store);
                if (rearmer != null && rearmer.Capacity < rearmer.GetMaxCapacity())
                {
                    SetAmmoKg(store, rearmer, rearmer.GetMaxCapacity());
                }
            }

            scratch.Clear();
        }
    }

    /// <summary>What one base has on the shelf, for the BASE SUPPLY readout.</summary>
    internal readonly struct BaseSupply
    {
        internal BaseSupply(string name, float distanceKm, int stores, float fuelKg, float fuelMaxKg, float ammoKg, float ammoMaxKg, List<Unit>? units = null, float largestAmmoKg = 0f)
        {
            Units = units;
            LargestAmmoKg = largestAmmoKg;
            Name = name;
            DistanceKm = distanceKm;
            Stores = stores;
            FuelKg = fuelKg;
            FuelMaxKg = fuelMaxKg;
            AmmoKg = ammoKg;
            AmmoMaxKg = ammoMaxKg;
        }

        internal string Name { get; }

        /// <summary>The base's own depots, for its BUY buttons; null on the FIELD DEPOTS row.</summary>
        internal List<Unit>? Units { get; }

        internal float DistanceKm { get; }

        internal int Stores { get; }

        internal float FuelKg { get; }

        internal float FuelMaxKg { get; }

        internal float AmmoKg { get; }

        internal float AmmoMaxKg { get; }

        /// <summary>What the base's next fuel / ammunition BUY costs, 0 when that shelf is full.</summary>
        internal float FuelPrice => GetBaseCost(
            CommanderSettings.SupplyFuelPurchaseCost, GetBaseWanted(FuelKg, FuelMaxKg), FuelCapacityKg);

        internal float AmmoPrice => GetBaseCost(
            CommanderSettings.SupplyAmmoPurchaseCost, GetBaseWanted(AmmoKg, AmmoMaxKg), LargestAmmoKg);

        internal float LargestAmmoKg { get; }

        internal float FuelFraction => FuelMaxKg <= 0f ? 0f : Mathf.Clamp01(FuelKg / FuelMaxKg);

        internal float AmmoFraction => AmmoMaxKg <= 0f ? 0f : Mathf.Clamp01(AmmoKg / AmmoMaxKg);
    }
}
