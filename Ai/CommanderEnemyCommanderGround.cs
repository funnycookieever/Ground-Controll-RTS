using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's two standing postures that are not "buy another tank": a radar screen
/// pushed out to ground it can see from, and a squadron bought through the naval dock.
/// </summary>
/// <remarks>
/// The radar screen is the answer to a real asymmetry. <c>RevealPlayerBase</c> hands the enemy every
/// building the player owns, because a commander that cannot see a base has nothing to attack — but
/// it deliberately reveals nothing that moves. So the enemy knows where the base is and nothing at
/// all about the army in front of it, and its own sensors are whatever happens to be parked at home.
/// Driving a radar vehicle out to high ground on the approach is how a commander earns that
/// information instead of being given it, and it is the same trade the player makes when they push a
/// radar forward: everything it sees, it sees because it is exposed.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Radar vehicles the commander wants standing watch before it stops buying them.</summary>
    private const int ReconPostTarget = 2;

    /// <summary>
    /// How far from the player's territory the radar screen sits. Far enough to be outside short
    /// range air defence, close enough that a ground radar can still see the approaches.
    /// </summary>
    private const float ReconStandoffMeters = 12000f;

    /// <summary>Fan the posts sit on, either side of the axis between the two territories.</summary>
    private const float ReconFanDegrees = 55f;

    /// <summary>A truck this close to its post is on station; re-ordering it only makes it shuffle.</summary>
    private const float ReconArrivedMeters = 400f;

    /// <summary>Local samples taken around a post looking for the highest ground near it.</summary>
    private const int ReconHeightSamples = 5;
    private const float ReconHeightSpreadMeters = 900f;

    /// <summary>Most hulls the commander keeps at sea, however rich it is.</summary>
    private const int NavalHullMax = 10;

    /// <summary>Hulls bought with whatever the fund holds, so a new dock puts something to sea at
    /// once. After these it saves up for the hull it actually wants.</summary>
    private const int NavalOpeningHulls = 2;

    /// <summary>A hull is only worth saving for if this many reviews of the naval share pay for it.
    /// This is what ties the fleet to the economy: a poor commander's share never reaches a frigate,
    /// so it keeps its patrol boats and lets the money go to the army; a rich one reaches a carrier.
    /// Reviews are 30 s apart, so 10 is five minutes of saving.</summary>
    private const int NavalReachReviews = 10;

    /// <summary>Warships (corvette and up) afloat before a carrier or an LFD is bought: a carrier
    /// alone is the most expensive target on the map with only point defence of its own.</summary>
    private const int NavalEscortsBeforeCarrier = 2;

    /// <summary>Hulls that gather at the rally point before they sail at the player together.</summary>
    private const int NavalWaveSize = 3;

    /// <summary>A hull this close to the rally point has arrived and counts toward the wave.</summary>
    private const float NavalRallyRadiusMeters = 2500f;

    /// <summary>How long a short wave waits for the rest before it sails anyway.</summary>
    private const float NavalRallyGiveUpSeconds = 300f;

    /// <summary>Share of a review's pot set aside for ships, accrued the same way airframes are.</summary>
    private const float NavalBudgetShare = 0.2f;

    /// <summary>Share of income saved toward a hull once the dock is at its top level.</summary>
    private const float NavalFundIncomeShare = 0.25f;

    /// <summary>Share of one review's budget kept for restocking fuel and ammunition depots. Small,
    /// because a top-up is cheap and only ever happens on a depot that is under half full.</summary>
    private const float SupplyBudgetShare = 0.15f;

    /// <summary>An artillery post is at least this far out from the gun's own base, clear of the
    /// depots and the road out of them.</summary>
    private const float BatteryClearOfBaseMeters = 1500f;

    /// <summary>Share of the gun's reach it stands off the target at: inside it with room to spare,
    /// since <c>TryPlacePost</c> may push the post out by a third.</summary>
    private const float BatteryRangeShare = 0.7f;

    /// <summary>Sideways gap between guns on the same line.</summary>
    private const float BatterySpacingMeters = 200f;

    /// <summary>Slots along the battery line tried before giving up on it.</summary>
    private const int BatterySlotTries = 8;

    /// <summary>The fallback post's distance from where a gun stands when its whole line is unusable.</summary>
    private const float BatteryFallbackMeters = 300f;

    /// <summary>Each artillery type's reach, learned off the first one fielded: a definition carries
    /// no weapon stations, so before one has rolled out its reach is unknown and it may be bought.</summary>
    private static readonly Dictionary<VehicleDefinition, float> ArtilleryReach = new();

    private readonly List<Airbase> attackedBases = new();

    private readonly List<Unit> reconUnits = new();
    private readonly List<Ship> staleShips = new();
    private readonly List<ShipDefinition> shipCatalog = new();
    private readonly int[] hullsByType = new int[(int)ShipType.PB + 1];

    /// <summary>
    /// The hulls an enemy ship plant may make: what its dock's level unlocks, so a plant waits for
    /// the dock rather than turning out a frigate nothing can put to sea. Only a hull in
    /// <c>Encyclopedia.Lookup</c>, the table <c>Factory.SetFactory</c> resolves its product in.
    /// </summary>
    internal static void CollectFactoryShips(FactionHQ hq, List<UnitDefinition> into)
    {
        CommanderEnemyCommanderService? service = Instance;
        int dockLevel = CommanderEconomyService.GetNavalDockLevel(hq);
        if (service == null || dockLevel <= 0 || Encyclopedia.Lookup == null)
        {
            return;
        }

        service.RefreshShipCatalog();
        for (int i = 0; i < service.shipCatalog.Count; i++)
        {
            ShipDefinition definition = service.shipCatalog[i];
            if (CommanderNavalPurchaseService.IsUnlocked(definition, dockLevel)
                && !CommanderUnitBanService.IsBanned(definition)
                && !string.IsNullOrEmpty(definition.jsonKey)
                && Encyclopedia.Lookup.TryGetValue(definition.jsonKey, out UnitDefinition listed)
                && ReferenceEquals(listed, definition))
            {
                into.Add(definition);
            }
        }
    }

    /// <summary>
    /// Picks the posts once per commander: a fan of points at standoff from the player's territory,
    /// spread either side of the axis between the two sides, each pulled to the highest ground in
    /// its neighbourhood. Height is the whole point — a radar in a valley is a radar that sees the
    /// valley. Posts are fixed for the mission, because a post that moves is a truck that never
    /// arrives.
    /// </summary>
    private static void EnsureReconPosts(CommanderState state, FactionHQ hq, FactionHQ localHq)
    {
        if (state.ReconPosts.Count > 0)
        {
            return;
        }

        GlobalPosition playerCenter = CommanderCaptureService.GetTerritoryCenter(localHq);
        GlobalPosition ownCenter = CommanderCaptureService.GetTerritoryCenter(hq);
        Vector3 axis = ownCenter.AsVector3() - playerCenter.AsVector3();
        axis.y = 0f;
        if (axis.sqrMagnitude < 1f)
        {
            return;
        }

        axis.Normalize();
        for (int i = 0; i < ReconPostTarget; i++)
        {
            // Evenly spread across the fan: two posts land on its edges, more fill in between.
            float t = ReconPostTarget == 1 ? 0.5f : i / (float)(ReconPostTarget - 1);
            float degrees = Mathf.Lerp(-ReconFanDegrees, ReconFanDegrees, t);
            Vector3 direction = Quaternion.Euler(0f, degrees, 0f) * axis;
            Vector3 nominal = playerCenter.AsVector3() + (direction * ReconStandoffMeters);
            state.ReconPosts.Add(FindHighGround(new GlobalPosition(nominal.x, nominal.y, nominal.z)));
        }
    }

    /// <summary>The highest terrain within a short walk of a point, sampled rather than searched.</summary>
    private static GlobalPosition FindHighGround(GlobalPosition around)
    {
        GlobalPosition best = CommanderGameAccess.SnapToTerrain(around);
        for (int i = 1; i < ReconHeightSamples; i++)
        {
            float angle = i * (Mathf.PI * 2f / ReconHeightSamples);
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                around.x + (Mathf.Cos(angle) * ReconHeightSpreadMeters),
                around.y,
                around.z + (Mathf.Sin(angle) * ReconHeightSpreadMeters)));
            if (candidate.y > best.y)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// How many of each class the fleet holds at most. The cheap classes are capped low so a
    /// commander never fills its ten slots with patrol boats; the slots left over are only reached
    /// by saving for the classes above. One carrier: it is a mobile airfield, a second adds little.
    /// </summary>
    private static int GetClassLimit(ShipType type)
    {
        return type switch
        {
            ShipType.PB => 3,
            ShipType.FFL => 3,
            ShipType.FFG => 2,
            ShipType.DDG => 2,
            ShipType.LFD => 1,
            ShipType.CV or ShipType.LHA => 1,
            _ => 0,
        };
    }

    private static Airbase? NearestBase(List<Airbase> bases, GlobalPosition from)
    {
        Airbase? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < bases.Count; i++)
        {
            float distance = FastMath.Distance(bases[i].center.GlobalPosition(), from);
            if (distance < bestDistance)
            {
                best = bases[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool TryPlaceBattery(Unit unit, GlobalPosition target, int index, out GlobalPosition post)
    {
        Vector3 away = unit.transform.GlobalPosition().AsVector3() - target.AsVector3();
        away.y = 0f;
        float distance = away.magnitude;
        float reach = unit.GetMaxRange() * BatteryRangeShare;
        float radius = Mathf.Min(
            reach > 0f ? reach : distance,
            distance - BatteryClearOfBaseMeters);
        radius = Mathf.Max(radius, BatteryClearOfBaseMeters);

        post = default;

        // 0, +1, -1, +2, -2 ... slots either side of the line, a fixed spacing apart. The next few slots
        // too: with no post the gun gets no order at all and stands on the road outside the depot it
        // rolled out of, and its own slot can be all sea, road or depot.
        for (int k = 0; k < BatterySlotTries; k++)
        {
            int slot = (index + k + 1) / 2 * ((index + k) % 2 == 0 ? -1 : 1);
            float angle = Mathf.Atan2(away.z, away.x) + (slot * BatterySpacingMeters / radius);
            if (TryPlacePost(target, radius, angle, 0, out post))
            {
                return true;
            }
        }

        // Nowhere on its line: off the road where it stands is better than in it.
        GlobalPosition at = unit.transform.GlobalPosition();
        for (int k = 0; k < 4; k++)
        {
            if (TryPlacePost(at, BatteryFallbackMeters, k * Mathf.PI / 2f, 0, out post))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a new hull waits for its wave: off the commander's own dock, or its own
    /// territory if the dock is gone.</summary>
    private static GlobalPosition GetNavalRally(FactionHQ hq)
    {
        return CommanderEconomyService.TryGetNavalDockPosition(hq, out GlobalPosition dock)
            ? dock
            : CommanderCaptureService.GetTerritoryCenter(hq);
    }

    private static void NoteRallying(CommanderState state, Ship? ship)
    {
        if (ship != null)
        {
            state.NavalRallying.Add(ship);
        }
    }

    /// <summary>
    /// Drives the faction's radar vehicles out to standing overwatch posts on the approach from the
    /// player's territory, and reports whether it is short of one so the buy loop can order it.
    /// </summary>
    private void ReviewRecon(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        reconUnits.Clear();
        if (hq.factionUnits != null)
        {
            // A radar the home guard has pinned to a base ring belongs to that base. Two
            // reviews issuing opposite destinations to one truck is a truck that shuttles
            // between them and stands watch nowhere.
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && unit != null
                    && !unit.disabled
                    && unit.definition is VehicleDefinition definition
                    && definition.vehicleType == VehicleType.RDR
                    && !state.Support.ContainsKey(unit))
                {
                    reconUnits.Add(unit);
                }
            }
        }

        // The reserve too: a bought radar waits out a depot cooldown before it is on the field, and
        // counting only the field bought another one every review until the first arrived.
        state.WantsReconUnit = reconUnits.Count + CountRadarReserve(hq) < ReconPostTarget;
        if (reconUnits.Count == 0)
        {
            return;
        }

        EnsureReconPosts(state, hq, localHq);
        if (state.ReconPosts.Count == 0)
        {
            return;
        }

        for (int i = 0; i < reconUnits.Count; i++)
        {
            GlobalPosition post = state.ReconPosts[i % state.ReconPosts.Count];
            Unit truck = reconUnits[i];

            // Already on station. Every issue is a networked RPC, so a truck that has arrived is
            // left alone rather than re-ordered to the spot it is standing on.
            if (FastMath.InRange(truck.transform.GlobalPosition(), post, ReconArrivedMeters))
            {
                continue;
            }

            CommanderGameAccess.GetUnitCommand(truck)?.SetDestination(post, false);
        }
    }

    /// <summary>
    /// Gives every artillery vehicle its own firing position and pins it there, so guns do not deploy
    /// on the depot's doorstep and block everything behind them. The post is on the line from the
    /// player's base back to the gun, inside its reach but at least
    /// <see cref="BatteryClearOfBaseMeters"/> out, fanned sideways so the guns do not stack.
    /// </summary>
    private void ReviewArtillery(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        staleDefenders.Clear();
        foreach (Unit unit in state.Batteries.Keys)
        {
            if (unit == null || unit.disabled || unit.NetworkHQ != hq)
            {
                staleDefenders.Add(unit!);
            }
        }

        for (int i = 0; i < staleDefenders.Count; i++)
        {
            state.Batteries.Remove(staleDefenders[i]);
            state.BatteryTargets.Remove(staleDefenders[i]);
        }

        if (hq.factionUnits == null)
        {
            return;
        }

        // Ballistic launchers go for the player's opening base. Guns back the pushes: each shells the
        // player base under attack nearest to it, else the player base nearest a depot. The buy gate
        // (ArtilleryDistance) is that nearest base.
        GlobalPosition target = GetStrikeTarget(localHq);
        Airbase? frontBase = null;
        CommanderCaptureService.Instance?.CollectAttackedBases(hq, localHq, attackedBases);
        state.ArtilleryDepot = null;
        state.ArtilleryDistance = 0f;
        List<(VehicleDepot, float)> depots = CommanderCaptureSpoils.DepotsRef(hq);
        for (int i = 0; i < depots.Count; i++)
        {
            VehicleDepot depot = depots[i].Item1;
            if (depot == null || depot.disabled)
            {
                continue;
            }

            foreach (Airbase airbase in localHq.GetAirbases())
            {
                if (airbase == null || airbase.disabled || airbase.center == null)
                {
                    continue;
                }

                GlobalPosition at = airbase.center.GlobalPosition();
                float distance = FastMath.Distance(depot.GlobalPosition(), at);
                if (state.ArtilleryDepot == null || distance < state.ArtilleryDistance)
                {
                    state.ArtilleryDepot = depot;
                    state.ArtilleryDistance = distance;
                    frontBase = airbase;
                }
            }
        }

        int launchers = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit is not GroundVehicle
                || unit.disabled
                || unit.definition is not VehicleDefinition { vehicleType: VehicleType.ART }
                || state.Defenders.ContainsKey(unit)
                || state.Support.ContainsKey(unit)
                || state.NuclearLaunchers.Contains(unit))
            {
                continue;
            }

            float reach = unit.GetMaxRange();
            if (reach > 0f)
            {
                ArtilleryReach[(VehicleDefinition)unit.definition] = reach;
            }

            // Ballistic launchers go to the shared strike site and fire from there; see that partial.
            bool ballistic = CommanderAreaFire.IsBallisticLauncher(unit);
            if (ballistic)
            {
                launchers++;
            }

            Airbase? aim = ballistic ? null : NearestBase(attackedBases, unit.GlobalPosition()) ?? frontBase;
            if (aim != null
                && state.BatteryTargets.TryGetValue(unit, out Airbase had)
                && !ReferenceEquals(had, aim))
            {
                state.Batteries.Remove(unit);
            }

            if (!state.Batteries.TryGetValue(unit, out GlobalPosition post))
            {
                if (ballistic
                    ? !TryPlaceStrikeSlot(state, unit, target, out post)
                    : !TryPlaceBattery(unit, aim != null ? aim.center.GlobalPosition() : target, state.Batteries.Count, out post))
                {
                    continue;
                }

                state.Batteries[unit] = post;
                if (aim != null)
                {
                    state.BatteryTargets[unit] = aim;
                }
            }

            if (ballistic && ReviewStrikeLauncher(localHq, state, unit, post))
            {
                continue;
            }

            HoldAt(unit, post);
        }

        ReviewStrikeSupply(hq, state, launchers);
    }

    private int CountRadarReserve(FactionHQ hq)
    {
        int reserve = 0;
        for (int i = 0; i < catalog.Count; i++)
        {
            if (catalog[i].vehicleType == VehicleType.RDR)
            {
                reserve += hq.GetUnitSupply(catalog[i]);
            }
        }

        return reserve;
    }

    /// <summary>The dearest radar vehicle the budget allows: a better set sees further, which is
    /// the only thing this purchase is for.</summary>
    private VehicleDefinition? ChooseReconUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.vehicleType == VehicleType.RDR
                && definition.value <= budget
                && (best == null || definition.value > best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>Counts the faction's live hulls into <see cref="hullsByType"/> and returns the total.</summary>
    private int CountFleet(FactionHQ hq)
    {
        System.Array.Clear(hullsByType, 0, hullsByType.Length);
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Ship && !unit.disabled
                && unit.definition is ShipDefinition definition)
            {
                hullsByType[(int)definition.shipType]++;
                count++;
            }
        }

        return count;
    }

    private int CountClass(ShipType type)
    {
        // The two big-deck classes share one slot.
        return type is ShipType.CV or ShipType.LHA
            ? hullsByType[(int)ShipType.CV] + hullsByType[(int)ShipType.LHA]
            : hullsByType[(int)type];
    }

    private int CountEscorts()
    {
        return hullsByType[(int)ShipType.FFL] + hullsByType[(int)ShipType.FFG] + hullsByType[(int)ShipType.DDG];
    }

    /// <summary>A class the fleet still has room for, and for a carrier, escorts to sail with.</summary>
    private bool FleetWants(ShipDefinition definition)
    {
        ShipType type = definition.shipType;
        if (CountClass(type) >= GetClassLimit(type))
        {
            return false;
        }

        return type is not (ShipType.CV or ShipType.LHA or ShipType.LFD) || CountEscorts() >= NavalEscortsBeforeCarrier;
    }

    /// <summary>
    /// Buys a hull through the faction's own naval dock, under the same level gate the player is on:
    /// no dock, no ships, and each dock level opens the next class. The dock itself is built by the
    /// economy spender, the same split as mines and factories — one buys units, the other buys the
    /// ability to buy them.
    /// <para>
    /// Returns what it took out of this review's pot, which is the saved share and not the purchase:
    /// a commander with no dock takes nothing, so a faction that will never go to sea does not
    /// quietly withhold a fifth of every review from its convoys.
    /// </para>
    /// </summary>
    private float ReviewNaval(FactionHQ hq, CommanderState state, float share)
    {
        CommanderNavalPurchaseService? naval = CommanderNavalPurchaseService.Instance;
        int dockLevel = CommanderEconomyService.GetNavalDockLevel(hq);
        if (naval == null || dockLevel <= 0)
        {
            return 0f;
        }

        RefreshShipCatalog();

        // A factory's hull goes to sea first and free, whatever the hull target says: the target
        // is about what to spend, and a ship plant's output left in the reserve is the plant wasted.
        for (int i = 0; i < shipCatalog.Count; i++)
        {
            ShipDefinition held = shipCatalog[i];
            if (CommanderNavalPurchaseService.GetReserve(hq, held) > 0
                && CommanderNavalPurchaseService.IsUnlocked(held, dockLevel)
                && naval.TryPurchaseForHq(hq, held, GetNavalRally(hq), out Ship? fromReserve) >= 0f)
            {
                NoteRallying(state, fromReserve);
                TotalPurchases++;
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) put a {held.unitName} to sea out of its reserve.");
                return 0f;
            }
        }

        // Destroyers and capital ships are bought past the hull limit, or the free small hulls from ship
        // plants would shut the paid buy for good. GetClassLimit still bounds each class.
        int ships = CountFleet(hq);
        bool fleetFull = ships >= NavalHullMax;

        // The goal is the dearest hull the fleet has room for that this commander's share could pay
        // for in NavalReachReviews. Nothing in reach (a poor commander, or every class it can reach
        // already full) means it stops saving and the money goes back to the army until the economy
        // grows.
        ShipDefinition? goal = null;
        ShipDefinition? affordable = null;

        // A top dock has no reach limit, or the 1600 destroyer would never become a goal.
        bool topDock = dockLevel >= CommanderEconomyService.MaxLevel;
        float reach = topDock ? float.MaxValue : share * NavalReachReviews;
        for (int i = 0; i < shipCatalog.Count; i++)
        {
            ShipDefinition definition = shipCatalog[i];
            if (!CommanderNavalPurchaseService.IsUnlocked(definition, dockLevel)
                || CommanderUnitBanService.IsBanned(definition)
                || !FleetWants(definition)
                || (fleetFull && definition.shipType is not (ShipType.DDG or ShipType.CV or ShipType.LHA or ShipType.LFD)))
            {
                continue;
            }

            if (definition.value <= reach && (goal == null || definition.value > goal.value))
            {
                goal = definition;
            }

            if (definition.value <= state.NavalFund + share && (affordable == null || definition.value > affordable.value))
            {
                affordable = definition;
            }
        }

        if (fleetFull && goal == null && affordable == null)
        {
            return 0f;
        }

        // Saved up to the goal, like the air fund. The first NavalOpeningHulls take whatever the fund
        // affords so a new dock is used at once; after that it waits for the goal, or it would only ever
        // buy the cheapest class.
        float navalFund = state.NavalFund;
        float taken = AccrueFund(ref navalFund, share, goal?.value ?? 0f);

        // The share alone is tens a review, so a capital hull is saved for out of income as well, up
        // to its price. Time.time, so a review that returned early still counts.
        if (topDock && goal != null && state.NavalFundAt >= 0f)
        {
            float income = CommanderEconomyService.GetEnemyIncomePerMinute(hq) * NavalFundIncomeShare * (Time.time - state.NavalFundAt) / 60f;
            navalFund = Mathf.Max(navalFund, Mathf.Min(navalFund + income, goal.value));
        }

        state.NavalFundAt = Time.time;
        state.NavalFund = navalFund;
        ShipDefinition? choice = ships < NavalOpeningHulls ? affordable ?? goal : goal;
        if (choice != null && choice.value > state.NavalFund)
        {
            choice = null;
        }

        if (choice == null)
        {
            return taken;
        }

        float spent = naval.TryPurchaseForHq(hq, choice, GetNavalRally(hq), out Ship? bought);
        NoteRallying(state, bought);
        if (spent > 0f)
        {
            state.NavalFund -= spent;
            TotalPurchases++;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) put a {choice.unitName} to sea for {spent:0} "
                    + $"(hull {ships + 1} of {NavalHullMax}, saving for {goal?.unitName ?? "nothing"}{(topDock ? string.Empty : $", reach {reach:0}")}).");
        }

        return taken;
    }

    /// <summary>
    /// Sends the hulls waiting at the rally point at the player together. Sent one by one as they
    /// were bought, every patrol boat and frigate met the player's fleet alone. A wave sails once
    /// <see cref="NavalWaveSize"/> hulls have gathered, or, after <see cref="NavalRallyGiveUpSeconds"/>,
    /// whatever has gathered if that is at least two; a lone hull stays home as a picket.
    /// Runs on the 10 s defence clock, not the buy review, which returns early when the commander is broke.
    /// </summary>
    private void ReleaseNavalWave(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        state.NavalRallying.RemoveWhere(ship => ship == null || ship.disabled);
        if (state.NavalRallying.Count == 0)
        {
            state.NavalWaitingSince = 0f;
            return;
        }

        if (state.NavalWaitingSince <= 0f)
        {
            state.NavalWaitingSince = Time.time;
        }

        if (!CommanderNavalPurchaseService.TryGetSeaLanePoint(GetNavalRally(hq), out GlobalPosition rally)
            || !CommanderNavalPurchaseService.TryGetSeaLanePoint(CommanderCaptureService.GetTerritoryCenter(localHq), out GlobalPosition target))
        {
            return;
        }

        int gathered = 0;
        foreach (Ship ship in state.NavalRallying)
        {
            if (FastMath.InRange(ship.GlobalPosition(), rally, NavalRallyRadiusMeters))
            {
                gathered++;
            }
        }

        bool tired = Time.time - state.NavalWaitingSince > NavalRallyGiveUpSeconds;
        if (gathered < NavalWaveSize && !(tired && gathered >= 2))
        {
            return;
        }

        staleShips.Clear();
        foreach (Ship ship in state.NavalRallying)
        {
            if (FastMath.InRange(ship.GlobalPosition(), rally, NavalRallyRadiusMeters))
            {
                CommanderGameAccess.TrySetDestination(ship, target);
                staleShips.Add(ship);
            }
        }

        for (int i = 0; i < staleShips.Count; i++)
        {
            state.NavalRallying.Remove(staleShips[i]);
        }

        state.NavalWaitingSince = state.NavalRallying.Count > 0 ? Time.time : 0f;
        CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) sent a naval wave of {staleShips.Count} hulls at the player.");
    }

    /// <summary>
    /// Every hull the game ships, collected here rather than read off the naval window: that list is
    /// only built once the player opens RTS mode, and this commander is spending from minute zero.
    /// Landing craft are left out: the AI never lands troops with one, so it was a wasted hull
    /// that took a slot in its fleet. Filtering here keeps them off
    /// the paid buy, the reserve launch and the enemy ship plants alike.
    /// </summary>
    private void RefreshShipCatalog()
    {
        if (shipCatalog.Count > 0)
        {
            return;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.ships == null)
        {
            return;
        }

        for (int i = 0; i < encyclopedia.ships.Count; i++)
        {
            ShipDefinition definition = encyclopedia.ships[i];
            if (definition != null
                && definition.unitPrefab != null
                && definition.shipType != ShipType.LC
                && definition.IsAllowed(includeEventContent: false)
                && definition.unitPrefab.GetComponent<Ship>() != null)
            {
                shipCatalog.Add(definition);
            }
        }
    }
}
