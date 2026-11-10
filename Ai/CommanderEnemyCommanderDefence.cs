using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's home guard: a share of its ground force pinned on a ring around each base
/// it holds, and a threat posture that thickens that ring while something hostile is inside radar
/// range of it.
/// </summary>
/// <remarks>
/// This exists because of an engine behaviour, not a design preference.
/// <c>GroundVehicle.CheckObstacles</c> re-targets any vehicle with no <b>player</b> command onto the
/// nearest objective or tracked enemy, so every vehicle the commander bought walked at the player
/// the moment it left the depot ramp — the whole force was always an attack and the base was always
/// empty behind it. Pinning a defender is therefore not a move order but a
/// <c>SetDestination(post, playerCommand: true)</c>, the only call that sets
/// <c>commandedDestination</c> and stops the auto-walk; releasing one means clearing that private
/// field again, because with <c>holdAtPlayerCommandedDestination</c> set nothing in the base game
/// ever clears it.
/// <para>
/// The ring is fixed points around the airbase centre, not a coverage solve. The units
/// standing on it aim and shoot for themselves — <c>Turret.AssessTargetPriority</c> is the whole
/// air- and ground-defence behaviour and it is already better than anything issued from here.
/// Upgrade to placed sectors only if the ring reads as obviously wrong in play.
/// </para>
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// How often the home guard is reviewed. Deliberately faster than the 30 s buy review: a raid
    /// that is visible on radar is a raid that lands inside a minute, so a posture that only changed
    /// with the shopping list would change after the attack was over.
    /// </summary>
    private const float DefenceReviewIntervalSeconds = 10f;

    // Road pathfinds (enemy orders) sent per frame; see PinTo.
    private const int PinsPerFrame = 3;

    /// <summary>Tanks and other non-air-defence vehicles held at home per base, on top of the air
    /// defence. Fixed whatever the posture, so the push is not starved.</summary>
    private const int MaxGroundDefendersPerBase = 10;

    /// <summary>Vehicles that stand together on one post. A cluster covers itself; a lone vehicle per
    /// post is easy to pick apart. Four so the whole guard fits on a base's posts.</summary>
    private const int DefendersPerPost = 4;

    /// <summary>Air-defence vehicles in every base's ring, whatever else is in it: enough for a few on
    /// every post round the base, not two clusters on
    /// the forward posts. A ring of troop carriers does nothing at all to an air raid.</summary>
    private const int AirDefendersPerBase = 20;

    /// <summary>The most of its combat vehicles a commander keeps at home as ground guard, in percent,
    /// every difficulty. Air defence is on top.</summary>
    private const int MaxHomeGuardPercent = 20;

    /// <summary>Stations on each base's ring, on top of the forward posts.</summary>
    private const int DefencePostsPerBase = 6;

    /// <summary>Spacing of the parking spots inside one post's cluster: six on the first circle,
    /// twelve on the second. Close enough to cover each other and to keep a SAM site's launcher,
    /// fire control and radar inside the game's 300 m link, far enough not to collide.</summary>
    private const float SlotSpacingMeters = 45f;

    /// <summary>Road clearance for a single parking spot. Smaller than the post's own, because the
    /// spot is a vehicle and not a whole cluster.</summary>
    private const float SlotRoadClearanceMeters = 20f;

    /// <summary>Posts pushed out toward the player on the approach to each base, ahead of the ring.
    /// They are built first and air defence is recruited first, so these are the stations the
    /// launchers take: a raid is met on its way in rather than over the apron.</summary>
    private const int ForwardPostsPerBase = 2;

    /// <summary>How far out those sit, as a multiple of the base's own ring.</summary>
    private const float ForwardPostRingScale = 2.5f;

    /// <summary>Fan they spread over, either side of the bearing to the player's territory.</summary>
    private const float ForwardPostFanDegrees = 40f;

    /// <summary>The ring follows the base's own capture radius, clamped so a tiny highway strip still
    /// gets a usable ring and a huge airfield does not scatter its guard.</summary>
    private const float DefenceRingMinMeters = 675f;
    private const float DefenceRingMaxMeters = 2100f;

    /// <summary>Clear ground demanded around a post. A ring drawn round an airbase crosses the roads
    /// that serve it, and a post on the tarmac is a guard parked nose to tail along it, which is the
    /// complaint this file's ring rebuild answers.</summary>
    private const float PostRoadClearanceMeters = 70f;

    /// <summary>
    /// The stations the SAM sites stand on: a radar-guided launcher, the fire-control truck that aims
    /// it and a radar for that truck, all within the game's 300 m link range. They are the first
    /// <see cref="MaxSamSites"/> posts and are fixed, so trucks never chase a moving site.
    /// <para>
    /// <c>EnsureDefencePosts</c> lays one forward post per base before a second anywhere, so site
    /// <c>n</c> is on base <c>n</c>'s approach.
    /// </para>
    /// </summary>
    private const int SamSitePost = 0;

    /// <summary>
    /// SAM sites the commander stands up across all its bases, at most <see cref="SamSitesPerBase"/>
    /// per base. Each site with a launcher also gets its own radar truck, on top of
    /// <see cref="BaseRadarTarget"/>, since the launcher does not fire without one inside 300 m.
    /// </summary>
    private const int MaxSamSites = 10;
    private const int SamSitesPerBase = 3;

    /// <summary>StratoLances on one site. They share the site's fire control and radar, and do not count
    /// toward the ring's size or its air-defence quota.</summary>
    private const int LaunchersPerSamSite = 4;

    /// <summary>A site takes this many StratoLances before the next site opens; the fourth comes once
    /// every site has this many. One per site across many sites reads as no SAM site at all.</summary>
    private const int SamSiteFillLaunchers = 3;

    /// <summary>Radar vehicles the guard spreads over its bases, so a bombed radar station does not
    /// leave the commander blind. Capped at three; more are better used on the approaches
    /// (<c>ReconPostTarget</c>). SAM sites raise this to one per manned site.</summary>
    private const int BaseRadarTarget = 3;

    /// <summary>Munitions trucks parked with the guard, so a defender can top up without driving
    /// back to a bunker. The rest stay with the convoys.</summary>
    private const int BaseSupplyTruckTarget = 2;

    /// <summary>A vehicle already pinned to a destination this close to its spot is not ordered
    /// again: every issue is a networked RPC. The game nudges a player point a little, hence not 0.</summary>
    private const float SlotOrderToleranceMeters = 30f;

    /// <summary>How close to the convoy a surplus munitions truck is kept. Loose on purpose: the
    /// truck is following something that is driving away, and every re-issue is a networked RPC.
    /// </summary>
    private const float ConvoyArrivedMeters = 250f;

    /// <summary>A defender with less than this much of its ordnance left leaves the ring to restock.
    /// Ammunition is the whole of a ground vehicle's logistics: only <c>Aircraft</c> burns fuel in
    /// this game.</summary>
    private const float DefenceRearmBelow = 0.25f;

    /// <summary>How close to the store a restocking defender is driven. The ring's own arrival slack
    /// is far too loose for this — a rearmer serves what is parked beside it.</summary>
    private const float RearmArrivedMeters = 60f;

    /// <summary>
    /// How far out a tracked hostile counts as an attack on the base. This is the "it can see it on
    /// the radar" range, so it is deliberately far wider than weapon range: the point is to be
    /// standing on the ring before the strike arrives, not after.
    /// </summary>
    private const float ThreatRadiusMeters = 15000f;

    /// <summary>A contact older than this is a memory, not a threat.</summary>
    private const float ThreatMemorySeconds = 45f;

    /// <summary>The posture holds this long past the last contact, so an attacker who drops out of
    /// radar cover for a few seconds does not stand the guard down mid-attack.</summary>
    private const float ThreatHoldSeconds = 300f;

    /// <summary>Only vehicles this close to one of the commander's own bases are recruited into its
    /// guard, so convoy vehicles are not pulled back from the front.</summary>
    private const float RecruitRadiusMeters = 5000f;

    /// <summary>Posts stay this far from any vehicle depot, where every new vehicle rolls out and
    /// the base's traffic is. A guard parked there is a guard in the way.</summary>
    private const float PostDepotClearanceMeters = 500f;

    /// <summary>How far round a nominal post the high-ground search looks. Height is the whole
    /// point of a static guard: a launcher on a rise sees further than one in a dip.</summary>
    private const float PostHighGroundSpreadMeters = 250f;

    /// <summary>A guard vehicle further than this from its spot is still on its way.</summary>
    private const float GuardArrivedMeters = 25f;

    /// <summary>Wide enough to hold the stall-and-reverse cycle on a slope, which is movement but
    /// not progress; anything really driving leaves it in seconds.</summary>
    private const float GuardStuckMeters = 60f;

    /// <summary>How long a guard vehicle may stay inside <see cref="GuardStuckMeters"/> short of its
    /// spot before it sets up where it is. Scaled time.</summary>
    private const float GuardStuckSeconds = 120f;

    /// <summary>
    /// <c>GroundVehicle.commandedDestination</c>. Private, and the only way back out of a pinned
    /// position — see the remarks on this class.
    /// </summary>
    private static readonly AccessTools.FieldRef<GroundVehicle, bool> CommandedDestinationRef =
        AccessTools.FieldRefAccess<GroundVehicle, bool>("commandedDestination");

    /// <summary>Radii a post is tried at, as multiples of the ring. Each post starts at a different
    /// one, which breaks the ring up into scattered ground instead of a circle, and the rest are the
    /// fallbacks for a post that lands on a road or in the sea.</summary>
    private static readonly float[] PostRingScales = { 1f, 0.72f, 1.3f, 0.5f };

    /// <summary>The land bases a ring is being rebuilt for, which happens a handful of times a
    /// match.</summary>
    private static readonly List<Airbase> BaseScratch = new();
    private static readonly List<GlobalPosition> DepotScratch = new();
    private static readonly Dictionary<UnitDefinition, bool> SamLauncherTypes = new();
    private static readonly Dictionary<VehicleDefinition, bool> SupplyTypes = new();
    private static readonly Dictionary<VehicleDefinition, bool> FireControlTypes = new();
    private static readonly Dictionary<Unit, GlobalPosition> PendingPins = new();
    private static readonly List<Unit> PinBatch = new();
    private static int pinFrame = -1;
    private static int pinsThisFrame;

    private readonly List<Unit> defenceCandidates = new();
    private readonly List<Unit> staleDefenders = new();
    private readonly List<Unit> relinkScratch = new();
    private readonly List<GlobalPosition> baseCentres = new();
    private readonly List<int> baseCounts = new();
    private readonly List<int> depotKeys = new();
    private readonly System.Text.StringBuilder guardReport = new();
    private int[] postOccupancy = new int[1];
    private int[] siteLaunchers = new int[1];
    private int[] siteFireControls = new int[1];
    private int[] siteRadars = new int[1];
    private int[] siteTrucks = new int[1];

    /// <summary>
    /// True while this unit is standing on an enemy commander's base ring. Read by
    /// <see cref="CommanderCaptureService"/> so an expansion squad and the home guard never fight
    /// over the same vehicle — one issues an ordinary order, the other a pinning one.
    /// </summary>
    /// <summary>
    /// Un-pins a vehicle that was ordered somewhere with a player command. Nothing in the base game
    /// ever clears <c>commandedDestination</c> on a vehicle that has arrived, so this write is the
    /// only way back out; the capture squad needs it for the same reason the home guard does.
    /// </summary>
    internal static void ReleaseCommandedDestination(Unit? unit)
    {
        // A capture squad lets go of a unit the home guard has just taken (a SAM launcher is
        // recruited wherever it stands). Un-pinning it then undid the guard's pin, and CheckObstacles
        // drove it at the player until the next defence review.
        if (unit is GroundVehicle vehicle && !IsDefendingUnit(unit))
        {
            PendingPins.Remove(unit);
            CommandedDestinationRef(vehicle) = false;
        }
    }

    /// <summary>
    /// <c>SetDestination(spot, playerCommand: true)</c>, unless the vehicle is already pinned there.
    /// Every issue is a networked RPC and a fresh path search on the host, and the reviews that call
    /// this walk hundreds of vehicles in one frame: re-sending an order the vehicle is already
    /// following was the AI-vs-AI server stalling every review and catching up after.
    /// </summary>
    internal static void PinTo(Unit unit, GlobalPosition spot)
    {
        if (unit is GroundVehicle vehicle
            && CommandedDestinationRef(vehicle)
            && CommanderGameAccess.TryGetCurrentCommandPosition(unit, out GlobalPosition current)
            && CommanderGameAccess.ApproximatelyEqual(current, spot, SlotOrderToleranceMeters))
        {
            return;
        }

        // Each issue runs the base game's road pathfinder synchronously on the host, a few ms
        // apiece: a capture wave of twenty cost 60-140 ms in one frame. Past the frame's budget
        // the order waits in PendingPins and FlushPendingPins sends it on a later frame.
        if (Time.frameCount != pinFrame)
        {
            pinFrame = Time.frameCount;
            pinsThisFrame = 0;
        }

        if (pinsThisFrame >= PinsPerFrame)
        {
            PendingPins[unit] = spot;
            return;
        }

        pinsThisFrame++;
        PendingPins.Remove(unit);
        CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(spot, true);
    }

    /// <summary>Sends orders <see cref="PinTo"/> deferred, a frame's budget at a time.</summary>
    internal static void FlushPendingPins()
    {
        if (PendingPins.Count == 0)
        {
            return;
        }

        PinBatch.Clear();
        foreach (KeyValuePair<Unit, GlobalPosition> entry in PendingPins)
        {
            PinBatch.Add(entry.Key);
            if (PinBatch.Count >= PinsPerFrame)
            {
                break;
            }
        }

        for (int i = 0; i < PinBatch.Count; i++)
        {
            Unit unit = PinBatch[i];
            GlobalPosition spot = PendingPins[unit];
            PendingPins.Remove(unit);
            if (unit != null && !unit.disabled)
            {
                PinTo(unit, spot);
            }
        }
    }

    /// <summary>Air defence that folds its weapon away to move (the LADS): ring-only, because out with
    /// a push it never fires. The CRAM by name: a stationary mount with no folding parts.</summary>
    internal static bool IsStaticAirDefence(Unit unit)
    {
        return DefencePriority(unit) > 0
            && !IsConvoyAirDefence(unit)
            && (CommanderFireNow.MustStopToFire(unit) || unit.definition.unitName == CramName);
    }

    internal static bool IsDefendingUnit(Unit? unit)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || unit == null)
        {
            return false;
        }

        foreach (KeyValuePair<FactionHQ, CommanderState> entry in service.states)
        {
            if (entry.Value.Defenders.ContainsKey(unit)
                || entry.Value.Support.ContainsKey(unit)
                || entry.Value.Batteries.ContainsKey(unit)
                || entry.Value.AaScouts.Contains(unit)
                || entry.Value.NuclearLaunchers.Contains(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Off the <c>VehicleDepot.TrySpawnVehicle</c> prefix: an AI commander's artillery rolls out only
    /// at its depot nearest the strike target, so the guns start as close to the enemy as the faction
    /// reaches. <c>DeployVehicles</c> offers each type to every depot in turn, so refusing the others
    /// is all it takes; the gun waits in the reserve while that depot is busy.
    /// </summary>
    internal static bool HoldsArtilleryAt(VehicleDepot depot, VehicleDefinition definition)
    {
        return definition.vehicleType == VehicleType.ART
            && Instance != null
            && depot != null
            && depot.NetworkHQ != null
            && Instance.states.TryGetValue(depot.NetworkHQ, out CommanderState state)
            && state.ArtilleryDepot != null
            && !state.ArtilleryDepot.disabled
            && state.ArtilleryDepot.NetworkHQ == depot.NetworkHQ
            && !ReferenceEquals(state.ArtilleryDepot, depot);
    }

    /// <summary>
    /// The same door for a StratoLance: it rolls out only at the depot nearest the site that wants the
    /// next launcher, and waits in the reserve while that depot is busy.
    /// </summary>
    internal static bool HoldsSamLauncherAt(VehicleDepot depot, VehicleDefinition definition)
    {
        return definition.vehicleType == VehicleType.R_SAM
            && Instance != null
            && depot != null
            && depot.NetworkHQ != null
            && Instance.states.TryGetValue(depot.NetworkHQ, out CommanderState state)
            && state.SamDepot != null
            && !state.SamDepot.disabled
            && state.SamDepot.NetworkHQ == depot.NetworkHQ
            && !ReferenceEquals(state.SamDepot, depot)
            && IsSamSiteLauncher(definition);
    }

    /// <summary>
    /// The same door for a site's radar and fire control: a truck joins a site at the base it rolled
    /// out at, so it has to roll out at the base whose site is short of one. Waits in the reserve
    /// while that depot is busy.
    /// </summary>
    internal static bool HoldsSiteTruckAt(VehicleDepot depot, VehicleDefinition definition)
    {
        if (Instance == null
            || depot == null
            || depot.NetworkHQ == null
            || !Instance.states.TryGetValue(depot.NetworkHQ, out CommanderState state))
        {
            return false;
        }

        VehicleDepot? wanted = definition.vehicleType == VehicleType.RDR ? state.SiteRadarDepot
            : IsFireControlVehicle(definition) ? state.SiteFireControlDepot
            : null;
        return wanted != null
            && !wanted.disabled
            && wanted.NetworkHQ == depot.NetworkHQ
            && !ReferenceEquals(wanted, depot);
    }

    /// <summary>
    /// Air defence, radar and fire control: what a base's guard and SAM sites are made of, and never
    /// an assault vehicle. A capture push on the player's last base takes every free capture-capable
    /// unit every 20 s, so it grabbed each new launcher before the guard's review did, and a unit on a
    /// drive is never recruited: the defensive setups rolled away with the convoy.
    /// </summary>
    internal static bool IsHomeGuardType(Unit unit)
    {
        return DefencePriority(unit) > 0 || IsRadarVehicle(unit) || IsFireControlVehicle(unit);
    }

    /// <summary>A Team Duel SPAAG the mission stood beside a radar station or base: authored
    /// <c>holdPosition</c>, which nothing bought has, so no order of the commander's moves it.</summary>
    internal static bool IsPlacedGuard(Unit unit)
    {
        return IsTeamDuelMission && unit is GroundVehicle vehicle && vehicle.GetHoldPosition();
    }

    /// <summary>A munitions truck, and not a builder: the builder count owns those.</summary>
    internal static bool IsRearmVehicle(Unit unit)
    {
        // The per-type answer for a vehicle: the capture pushes ask this of every unit, every review.
        bool store = unit.definition is VehicleDefinition definition
            ? !unit.disabled && IsSupplyVehicle(definition)
            : CommanderSupplyStoreService.GetRearmer(unit) != null;
        return store && !CommanderRepairService.IsRepairUnit(unit);
    }

    /// <summary>
    /// A hit on anything a hostile commander owns puts it on the defensive, whether or not its
    /// sensors ever saw what did it: the radar half of the trigger cannot see a low pass that has
    /// already dropped. Called from the shared <c>Unit.RecordDamage</c> postfix, so it stays a
    /// dictionary lookup and a float write — <c>states</c> never holds the local HQ, which is what
    /// keeps the player's own losses out of it.
    /// </summary>
    internal void NotifyUnitDamaged(Unit? unit, PersistentID by)
    {
        FactionHQ? hq = unit == null ? null : unit.NetworkHQ;
        if (hq != null && states.TryGetValue(hq, out CommanderState state))
        {
            state.ThreatUntil = Time.time + ThreatHoldSeconds;
            if (state.Challenging)
            {
                LocateBattery(hq, by, anyAttacker: false);
            }

            if (state.Tactics && CommanderEconomyService.IsIncomeBuildingUnit(unit))
            {
                RaiseEcoAlarm(hq, state, unit!, by);
            }
        }
    }

    /// <summary>
    /// While the guard is short, puts the depots at the least-guarded bases first in the faction's
    /// spawn list, so a new vehicle rolls out at the base that needs it instead of being bought at
    /// one base and driven across the map to another. Every base has a depot (Champions deals two to
    /// each), so this is only a question of which door. Called from the <c>DeployVehicles</c> prefix,
    /// which runs right after the game's own 30 s <c>SortDepots</c>, so the game's order (nearest the
    /// objective first) is back for the convoys the moment the guard is full, rotated one depot
    /// per tick so every base's doors take a turn.
    /// </summary>
    internal void OrderDepotsForGuard(FactionHQ hq)
    {
        if (!hq.IsServer || !states.TryGetValue(hq, out CommanderState state))
        {
            return;
        }

        List<(VehicleDepot, float)> depots = CommanderCaptureSpoils.DepotsRef(hq);
        if (!state.WantsDefenceUnit || state.DefencePosts.Count == 0)
        {
            // DeployVehicles gives each type to the first depot off cooldown, and without objective positions
            // (Champions) SortDepots never reorders. Rotate so every base fields units.
            if (depots.Count > 1)
            {
                (VehicleDepot, float) first = depots[0];
                depots.RemoveAt(0);
                depots.Add(first);
            }

            return;
        }

        CountPostOccupancy(state, includeSupport: false);
        CountBaseOccupancy(state);
        depotKeys.Clear();
        for (int i = 0; i < depots.Count; i++)
        {
            int key = int.MaxValue;
            VehicleDepot depot = depots[i].Item1;
            if (depot != null)
            {
                GlobalPosition at = depot.transform.GlobalPosition();
                float nearest = RecruitRadiusMeters;
                for (int b = 0; b < baseCentres.Count; b++)
                {
                    float distance = FastMath.Distance(at, baseCentres[b]);
                    if (distance < nearest)
                    {
                        nearest = distance;
                        key = baseCounts[b];
                    }
                }
            }

            depotKeys.Add(key);
        }

        // Insertion sort, because it is stable: depots at equally guarded bases keep the game's order.
        for (int i = 1; i < depots.Count; i++)
        {
            for (int j = i; j > 0 && depotKeys[j - 1] > depotKeys[j]; j--)
            {
                (depots[j - 1], depots[j]) = (depots[j], depots[j - 1]);
                (depotKeys[j - 1], depotKeys[j]) = (depotKeys[j], depotKeys[j - 1]);
            }
        }
    }

    /// <summary>
    /// Anything hostile, seen recently, inside <see cref="ThreatRadiusMeters"/> of a base this
    /// faction holds. It reads the commander's own <c>trackingDatabase</c> and nothing else, so the
    /// enemy reacts to what its sensors actually hold — a player who comes in under the radar
    /// arrives against the resting ring, which is what flying low is for.
    /// </summary>
    private static bool IsUnderThreat(FactionHQ hq)
    {
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            // Buildings are in that database because RevealPlayerBase put them there, permanently.
            // A building is not an attack, and without this filter a base the player captured next
            // door would hold the commander in defence posture for the rest of the match.
            if (unit is Building)
            {
                continue;
            }

            if (IsNearOwnBase(hq, info.lastKnownPosition, ThreatRadiusMeters))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNearOwnBase(FactionHQ hq, GlobalPosition position, float radius)
    {
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null
                && !airbase.disabled
                && airbase.center != null
                && FastMath.InRange(position, airbase.center.GlobalPosition(), radius))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ring, rebuilt only when the set of bases changes — a base that changes hands moves the
    /// whole guard, but a post that drifts every review is a truck that never arrives. Every base
    /// gets its forward posts first, then a scattered ring of stations around it.
    /// </summary>
    private static void EnsureDefencePosts(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        BaseScratch.Clear();
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (CommanderGameAccess.IsLandBase(airbase))
            {
                BaseScratch.Add(airbase);
            }
        }

        // Same count is not same bases: the Champions deal swaps the starting base for another one, and
        // Boscali's whole ring stayed at K92, a base it no longer held, while its own stood empty and
        // all its air defence went out with the convoys.
        if (BaseScratch.Count == state.DefenceBaseCount
            && (BaseScratch.Count == 0 || (state.DefencePosts.Count > 0 && SameBases(state.DefenceBases, BaseScratch))))
        {
            return;
        }

        DepotScratch.Clear();
        foreach (VehicleDepot depot in Object.FindObjectsOfType<VehicleDepot>())
        {
            if (depot != null)
            {
                DepotScratch.Add(depot.transform.GlobalPosition());
            }
        }

        GlobalPosition playerCentre = CommanderCaptureService.GetTerritoryCenter(localHq);
        state.DefenceBaseCount = BaseScratch.Count;
        state.DefenceBases.Clear();
        state.DefenceBases.AddRange(BaseScratch);
        state.DefencePosts.Clear();
        state.DefencePostBases.Clear();
        state.Slots.Clear();
        state.GuardProgress.Clear();

        // Post and site numbers mean other bases from here on, so whatever must stay where it stands
        // gives its number up and RecruitAirDefence picks it again at the base it is at. Keeping the
        // number sent StratoLances across the map every time a base changed hands.
        foreach (Unit unit in new List<Unit>(state.Defenders.Keys))
        {
            if (unit != null && (IsSamSiteLauncher(unit) || IsStaticAirDefence(unit)))
            {
                state.Defenders.Remove(unit);
            }
        }

        // A round of one post at every base before the second round anywhere, rather than one base's
        // posts and then the next's. ChooseEmptiestPost fills this list in order, and the guard is
        // capped at half the army, so a flat run per base handed the first two or three bases a full
        // ring and every base captured after that nothing at all.
        for (int i = 0; i < ForwardPostsPerBase; i++)
        {
            for (int b = 0; b < BaseScratch.Count; b++)
            {
                GetBaseRing(BaseScratch[b], out GlobalPosition centre, out float ring);

                // Toward the player, so the launchers that fill these first stand on the approach.
                Vector3 toPlayer = playerCentre.AsVector3() - centre.AsVector3();
                toPlayer.y = 0f;
                float bearing = toPlayer.sqrMagnitude < 1f ? 0f : Mathf.Atan2(toPlayer.z, toPlayer.x);
                float t = ForwardPostsPerBase == 1 ? 0.5f : i / (float)(ForwardPostsPerBase - 1);
                float fan = Mathf.Lerp(-ForwardPostFanDegrees, ForwardPostFanDegrees, t) * Mathf.Deg2Rad;
                if (TryPlacePost(centre, ring * ForwardPostRingScale, bearing + fan, 0, out GlobalPosition forward))
                {
                    state.DefencePosts.Add(forward);
                    state.DefencePostBases.Add(centre);
                }
            }
        }

        for (int i = 0; i < DefencePostsPerBase; i++)
        {
            float angle = i * (Mathf.PI * 2f / DefencePostsPerBase);
            for (int b = 0; b < BaseScratch.Count; b++)
            {
                GetBaseRing(BaseScratch[b], out GlobalPosition centre, out float ring);
                if (TryPlacePost(centre, ring, angle, i, out GlobalPosition post))
                {
                    state.DefencePosts.Add(post);
                    state.DefencePostBases.Add(centre);
                }
            }
        }

        // Site n is post n, so this many sites reach every base's first post. A base whose forward
        // post found no ground has its first post further down the list.
        state.SamSitesToCoverBases = 0;
        for (int b = 0; b < BaseScratch.Count; b++)
        {
            GetBaseRing(BaseScratch[b], out GlobalPosition centre, out _);
            for (int i = 0; i < state.DefencePosts.Count; i++)
            {
                if (IsPostAt(state, i, centre))
                {
                    state.SamSitesToCoverBases = Mathf.Max(state.SamSitesToCoverBases, i + 1);
                    break;
                }
            }
        }
    }

    /// <summary>The same airbases, in any order; both lists hold a handful.</summary>
    private static bool SameBases(List<Airbase> laid, List<Airbase> held)
    {
        if (laid.Count != held.Count)
        {
            return false;
        }

        foreach (Airbase airbase in held)
        {
            if (!laid.Contains(airbase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The ring a base's guard stands on: its own capture radius, clamped so a highway
    /// strip still gets something worth standing on and an airfield does not scatter its guard.</summary>
    private static void GetBaseRing(Airbase airbase, out GlobalPosition centre, out float ring)
    {
        centre = airbase.center.GlobalPosition();
        ring = Mathf.Clamp(
            airbase.SavedAirbase?.CaptureRange ?? 0f,
            DefenceRingMinMeters,
            DefenceRingMaxMeters);
    }

    /// <summary>
    /// A post on this bearing, at the first radius in <see cref="PostRingScales"/> (starting from
    /// <paramref name="firstScale"/>) that is on dry land, off the road network and drivable from the
    /// base. A mountain base can have no drivable ground on a bearing, so the first dry nominal point
    /// stands in rather than losing the post; <see cref="SettleIfStuck"/> covers that vehicle.
    /// </summary>
    /// <remarks>
    /// A ring at a single radius runs along the base's roads and the guard parks on the tarmac.
    /// Varying the radius per post scatters them, and the road test keeps them off carriageways.
    /// </remarks>
    private static bool TryPlacePost(
        GlobalPosition centre,
        float ring,
        float angle,
        int firstScale,
        out GlobalPosition post)
    {
        GlobalPosition steep = default;
        bool hasSteep = false;
        for (int i = 0; i < PostRingScales.Length; i++)
        {
            float radius = ring * PostRingScales[(firstScale + i) % PostRingScales.Length];
            float x = centre.x + (Mathf.Cos(angle) * radius);
            float z = centre.z + (Mathf.Sin(angle) * radius);

            // The nominal point and four round it; the highest one a vehicle can reach wins.
            bool found = false;
            post = default;
            for (int s = 0; s < 5; s++)
            {
                float around = s * (Mathf.PI / 2f);
                float spread = s == 0 ? 0f : PostHighGroundSpreadMeters;
                GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                    x + (Mathf.Cos(around) * spread),
                    centre.y,
                    z + (Mathf.Sin(around) * spread)));
                if (!IsPostGround(candidate))
                {
                    continue;
                }

                if (!hasSteep)
                {
                    steep = candidate;
                    hasSteep = true;
                }

                if ((!found || candidate.y > post.y) && CommanderTerrainRouteService.IsDrivable(centre, candidate))
                {
                    post = candidate;
                    found = true;
                }
            }

            if (found)
            {
                return true;
            }
        }

        post = steep;
        return hasSteep;
    }

    /// <summary>
    /// The spot to hold <paramref name="unit"/> at: its own, unless it has spent
    /// <see cref="GuardStuckSeconds"/> failing to get there, when where it stands becomes the spot. A
    /// mount that never arrives never parks, and one that never parks never sets up to fire.
    /// </summary>
    private static GlobalPosition SettleIfStuck(CommanderState state, Unit unit, GlobalPosition spot)
    {
        GlobalPosition at = unit.transform.GlobalPosition();
        if (FastMath.InRange(at, spot, GuardArrivedMeters))
        {
            state.GuardProgress.Remove(unit);
            return spot;
        }

        if (!state.GuardProgress.TryGetValue(unit, out (GlobalPosition Position, float Since) last)
            || FastMath.OutOfRange(at, last.Position, GuardStuckMeters))
        {
            state.GuardProgress[unit] = (at, Time.time);
            return spot;
        }

        if (Time.time - last.Since < GuardStuckSeconds || !state.Slots.TryGetValue(unit, out DefenceSlot slot))
        {
            return spot;
        }

        state.GuardProgress.Remove(unit);
        state.Slots[unit] = new DefenceSlot(slot.Post, slot.Index, at);
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({unit.NetworkHQ?.faction.name}) sets up a stuck {CommanderGameAccess.GetUnitLabel(unit)} where it stands, "
                + $"{FastMath.Distance(at, spot):0} m short of its post at ({spot.x:0}, {spot.z:0}).");
        return at;
    }

    private static bool IsPostGround(GlobalPosition point)
    {
        // SnapToTerrain returns the seabed over water, so this is also what keeps a coastal
        // base's ring off the sea floor.
        if (CommanderGameAccess.IsBelowSeaLevel(point)
            || CommanderBuildPreview.IsOnLevelRoad(point, PostRoadClearanceMeters))
        {
            return false;
        }

        for (int i = 0; i < DepotScratch.Count; i++)
        {
            if (FastMath.InRange(point, DepotScratch[i], PostDepotClearanceMeters))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Free to join the guard: at home, and not already sent at a base by a capture push.</summary>
    private static bool IsRecruitable(FactionHQ hq, Unit unit)
    {
        // Air defence belongs to the guard wherever it is: one already out with a push is called home
        // rather than left for CheckObstacles to drive at the player.
        return !IsPlacedGuard(unit)
            && !IsAaScout(unit)
            && (IsHomeGuardType(unit) || IsNearOwnBase(hq, unit.transform.GlobalPosition(), RecruitRadiusMeters))
            && !(CommanderCaptureService.Instance?.IsOnEnemyDrive(unit) ?? false)
            && !(IsNuclearArmed(hq) && CommanderFactionVehicleService.GetWarheadCost(unit.definition) > 0);
    }

    /// <summary>The centre of the own base nearest this unit, as recorded against the posts.</summary>
    private static GlobalPosition GetHomeBase(CommanderState state, Unit unit)
    {
        GlobalPosition at = unit.transform.GlobalPosition();
        GlobalPosition home = default;
        float best = float.MaxValue;
        for (int i = 0; i < state.DefencePostBases.Count; i++)
        {
            float distance = FastMath.Distance(at, state.DefencePostBases[i]);
            if (distance < best)
            {
                best = distance;
                home = state.DefencePostBases[i];
            }
        }

        return home;
    }

    /// <summary>The depot nearest a site, within <see cref="RecruitRadiusMeters"/>.</summary>
    private static VehicleDepot? FindSiteDepot(FactionHQ hq, GlobalPosition site)
    {
        List<(VehicleDepot, float)> depots = CommanderCaptureSpoils.DepotsRef(hq);
        VehicleDepot? nearest = null;
        float distance = RecruitRadiusMeters;
        for (int d = 0; d < depots.Count; d++)
        {
            VehicleDepot depot = depots[d].Item1;
            if (depot != null && !depot.disabled)
            {
                float to = FastMath.Distance(depot.GlobalPosition(), site);
                if (to < distance)
                {
                    distance = to;
                    nearest = depot;
                }
            }
        }

        return nearest;
    }

    private static bool IsPostAt(CommanderState state, int post, GlobalPosition baseCentre)
    {
        return post < state.DefencePostBases.Count
            && CommanderGameAccess.ApproximatelyEqual(state.DefencePostBases[post], baseCentre, 1f);
    }

    /// <summary>Whether <paramref name="a"/> beats <paramref name="b"/> for a unit at <paramref name="at"/>:
    /// the unit's own base first, then the nearer one.</summary>
    private static bool IsCloserBase(GlobalPosition a, GlobalPosition b, GlobalPosition home, GlobalPosition at)
    {
        return IsCloserBase(a, b, home, at, a, b);
    }

    private static bool IsCloserBase(
        GlobalPosition a,
        GlobalPosition b,
        GlobalPosition home,
        GlobalPosition at,
        GlobalPosition baseOfA,
        GlobalPosition baseOfB)
    {
        bool aHome = CommanderGameAccess.ApproximatelyEqual(baseOfA, home, 1f);
        bool bHome = CommanderGameAccess.ApproximatelyEqual(baseOfB, home, 1f);
        if (aHome != bHome)
        {
            return aHome;
        }

        return FastMath.Distance(at, a) < FastMath.Distance(at, b);
    }

    /// <summary>
    /// Where the <paramref name="index"/>-th vehicle of a cluster parks, relative to its post: six
    /// round the first circle, twelve round the next, and so on, each circle turned half a step so
    /// the rings do not line up into spokes.
    /// </summary>
    private static Vector2 SlotOffset(int index)
    {
        int circle = 1;
        int first = 0;
        while (index >= first + (6 * circle))
        {
            first += 6 * circle;
            circle++;
        }

        int count = 6 * circle;
        float angle = ((index - first) + (circle % 2 == 0 ? 0.5f : 0f)) * (Mathf.PI * 2f / count);
        float radius = SlotSpacingMeters * circle;
        return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
    }

    /// <summary>
    /// The unit's own parking spot at <paramref name="post"/>, picked the first time and kept, so
    /// vehicles do not jostle for one shared point.
    /// </summary>
    private static GlobalPosition GetSlot(CommanderState state, Unit unit, int post)
    {
        if (state.Slots.TryGetValue(unit, out DefenceSlot slot) && slot.Post == post)
        {
            return slot.Position;
        }

        int index = 0;
        bool taken = true;
        while (taken)
        {
            taken = false;
            foreach (KeyValuePair<Unit, DefenceSlot> entry in state.Slots)
            {
                if (entry.Value.Post == post && entry.Value.Index == index && !ReferenceEquals(entry.Key, unit))
                {
                    taken = true;
                    index++;
                    break;
                }
            }
        }

        GlobalPosition centre = state.DefencePosts[post % state.DefencePosts.Count];
        GlobalPosition position = centre;

        // Turn the spot round its post until it is off the road and out of the sea; the post itself
        // already passed both tests, so it is the fallback.
        for (int turn = 0; turn < 6; turn++)
        {
            Vector2 offset = SlotOffset(index);
            float angle = turn * (Mathf.PI / 3f);
            float x = (offset.x * Mathf.Cos(angle)) - (offset.y * Mathf.Sin(angle));
            float z = (offset.x * Mathf.Sin(angle)) + (offset.y * Mathf.Cos(angle));
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(
                new GlobalPosition(centre.x + x, centre.y, centre.z + z));
            if (!CommanderGameAccess.IsBelowSeaLevel(candidate)
                && !CommanderBuildPreview.IsOnLevelRoad(candidate, SlotRoadClearanceMeters)
                && CommanderTerrainRouteService.IsDrivable(centre, candidate))
            {
                position = candidate;
                break;
            }
        }

        state.Slots[unit] = new DefenceSlot(post, index, position);
        return position;
    }

    /// <summary>
    /// Pins the vehicle to its spot, and only sends the order when it is not already pinned there.
    /// An unpinned vehicle gets driven at the player by <c>CheckObstacles</c>.
    /// </summary>
    private static void HoldAt(Unit unit, GlobalPosition spot)
    {
        if (unit is not GroundVehicle vehicle)
        {
            return;
        }

        CommanderRepairService.KeepAtCommandedDestination(vehicle);

        // playerCommand: true is the whole mechanism. See the remarks on this class.
        PinTo(unit, spot);
    }

    /// <summary>A radar vehicle: what a base needs so one bombed radar station does not blind it,
    /// and what a fire-control truck needs within 300 m of itself before a launcher will fire.</summary>
    private static bool IsRadarVehicle(Unit unit)
    {
        return unit.definition is VehicleDefinition definition && definition.vehicleType == VehicleType.RDR;
    }

    /// <summary>
    /// A launcher that is aimed by a fire-control truck rather than by itself: the StratoLance and
    /// its kin, the only vehicles the SAM site exists for. The cheap type test comes first because
    /// the real one walks the unit's turrets.
    /// </summary>
    private static bool IsSamSiteLauncher(Unit unit)
    {
        if (DefencePriority(unit) == 0)
        {
            return false;
        }

        // Cached per type: the turret walk allocates, and the ring asks this of every defender
        // several times a review now that launchers are kept out of the ring's counts.
        // Off the prefab, never the live unit: the walk answers false for a wreck, and the first
        // unit asked after a base changed hands is often one (EnsureDefencePosts runs before the
        // prune). Cached, that false turned every StratoLance into a roaming ring spare for the
        // rest of the match, and the unpinned ones drove off at the player like a convoy.
        if (!SamLauncherTypes.TryGetValue(unit.definition, out bool launcher))
        {
            launcher = unit.definition is VehicleDefinition definition
                ? CommanderRadarService.IsRadarGuidedLauncherDefinition(definition)
                : CommanderRadarService.IsRadarGuidedLauncherUncached(unit);
            if (!unit.disabled)
            {
                SamLauncherTypes[unit.definition] = launcher;
            }
        }

        return launcher;
    }

    /// <summary>SAM launchers already standing in the ring.</summary>
    private static int CountRingLaunchers(CommanderState state)
    {
        int count = 0;
        foreach (Unit unit in state.Defenders.Keys)
        {
            if (IsSamSiteLauncher(unit))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The same question about something the commander is thinking of buying.</summary>
    private static bool IsSamSiteLauncher(VehicleDefinition definition)
    {
        // Only what the depot files under its Radar SAM tab (the StratoLance). A towed launcher is
        // R_SAM too but lands in the Trailer tab: no wheels of its own, and dearer, so it won the pick.
        return CommanderGameAccess.GetVehicleCategoryLabel(definition) == "Radar SAM"
            && CommanderRadarService.IsRadarGuidedLauncherDefinition(definition);
    }

    /// <summary>Any radar-guided air defence, towed or not. Kept out of the convoy roll: nothing
    /// pins one that is not in the ring, so it would drive the dearest thing owned at the player.</summary>
    private static bool IsRadarGuidedAirDefence(VehicleDefinition definition)
    {
        return IsAirDefence(definition) && CommanderRadarService.IsRadarGuidedLauncherDefinition(definition);
    }

    /// <summary>
    /// Gun or laser air defence built to shoot munitions rather than aircraft: the commander's answer
    /// to massed rocket artillery.
    /// </summary>
    /// <remarks>
    /// Read from <c>RoleIdentity.antiMissile</c> (asset data), so new or renamed vehicles classify
    /// themselves. Radar-guided launchers are excluded; they belong to SAM sites and have their own
    /// fund. <c>DescribeRosterParts</c> logs every air-defence vehicle's role numbers once per mission.
    /// </remarks>
    private static bool IsCounterBatteryDefence(VehicleDefinition definition)
    {
        return IsAirDefence(definition)
            && !IsRadarGuidedAirDefence(definition)
            && !IsConvoyAirDefence(definition)
            && definition.roleIdentity.antiMissile > 0f;
    }

    private static bool IsCounterBatteryDefence(Unit unit)
    {
        return unit.definition is VehicleDefinition definition && IsCounterBatteryDefence(definition);
    }

    /// <summary>Sites this commander runs: up to three per base, ten in all, and never more than it
    /// has posts for. At least one, because it is a divisor. Site n stands on post n, and the posts
    /// are laid a round per base at a time, so site n is at base n modulo the base count: capping
    /// the total at three times the bases is what keeps any one base to three. Never so few that a
    /// base goes without (<see cref="CommanderState.SamSitesToCoverBases"/>): a launcher starts its
    /// site at the base it rolled out at, it does not cross the map to one.</summary>
    private static int SamSiteCount(CommanderState state)
    {
        int wanted = Mathf.Max(
            Mathf.Min(state.DefenceBaseCount * SamSitesPerBase, MaxSamSites),
            state.SamSitesToCoverBases);
        return Mathf.Clamp(wanted, 1, Mathf.Max(state.DefencePosts.Count, 1));
    }

    /// <summary>Radar trucks the guard keeps: the resting spread over its bases, or one per site
    /// with a launcher on it, whichever is more. Tied to launchers rather than sites so the commander
    /// does not buy ten radars for sites it cannot afford to arm yet.</summary>
    private static int SamRadarTarget(int bases, int launchers, int sites)
    {
        return Mathf.Max(Mathf.Min(bases, BaseRadarTarget), Mathf.Min(launchers, sites));
    }

    /// <summary>A site with a launcher on it and no radar of its own, so a launcher that cannot fire.</summary>
    private static bool AnySiteLacksRadar(int[] launchers, int[] radars, int sites)
    {
        for (int i = 0; i < sites; i++)
        {
            if (launchers[i] > 0 && radars[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Which sites a new launcher looks at first: a started one short of
    /// <see cref="SamSiteFillLaunchers"/>, then an empty one, then a filled one with room, then a
    /// full one.</summary>
    private static int SiteFillRank(int launchers)
    {
        return launchers > 0 && launchers < SamSiteFillLaunchers ? 0
            : launchers == 0 ? 1
            : launchers < LaunchersPerSamSite ? 2
            : 3;
    }

    /// <summary>
    /// The nearest thing the faction owns that can put ordnance back into a vehicle: a munitions
    /// bunker, or one of its trucks. An empty shelf is skipped, because driving a dry launcher to a
    /// dry bunker takes it out of the ring for nothing.
    /// </summary>
    private static bool TryFindRearmSource(FactionHQ hq, CommanderState state, Unit defender, out GlobalPosition position)
    {
        position = default;
        if (hq.factionUnits == null)
        {
            return false;
        }

        float best = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            // A store that stands still: a building or the guard's own parked truck. A convoy's
            // truck drives off to the front, and a defender chasing it left its post with it.
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || ReferenceEquals(unit, defender)
                || CommanderSupplyStoreService.GetAmmoFraction(unit) <= 0f
                || (unit is not Building && !state.Support.ContainsKey(unit)))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                defender.transform.position, unit.transform.position);
            if (distance < best)
            {
                best = distance;
                position = unit.transform.GlobalPosition();
            }
        }

        return best < float.MaxValue;
    }

    /// <summary>
    /// Hands the surplus back to the attack. Without this a commander that was raided once keeps a
    /// wartime guard for the rest of the match and stops pushing.
    /// </summary>
    private static void ReleaseSurplusDefenders(CommanderState state, int target)
    {
        while (state.Defenders.Count > target)
        {
            Unit? release = null;
            int highestRank = -1;
            foreach (KeyValuePair<Unit, int> entry in state.Defenders)
            {
                // Ground vehicles go before launchers. Among each, the highest post index is the
                // most recently filled one, so the ring thins back the way it was built up rather
                // than opening a hole in the middle of it.
                int rank = entry.Value + (DefencePriority(entry.Key) > 0 ? 0 : 100000);
                if (rank > highestRank)
                {
                    highestRank = rank;
                    release = entry.Key;
                }
            }

            if (release == null)
            {
                return;
            }

            state.Defenders.Remove(release);
            if (release is GroundVehicle vehicle)
            {
                CommandedDestinationRef(vehicle) = false;
            }
        }
    }

    /// <summary>Every combat ground vehicle this faction owns, home guard included.</summary>
    private static int CountCombatVehicles(FactionHQ hq)
    {
        int count = 0;
        if (hq.factionUnits == null)
        {
            return count;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsCombatVehicle(definition))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Fighting vehicles on the field plus those bought and waiting in the reserve.</summary>
    private static int CountArmy(FactionHQ hq)
    {
        int count = CountCombatVehicles(hq);
        foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
        {
            if (entry.Key != null && IsCombatVehicle(entry.Key))
            {
                count += entry.Value.Count;
            }
        }

        return count;
    }

    /// <summary>10 a base, and under a unit cap 2, 5, 10 for 60, 150, 300: a fifth of a capped army
    /// spread over eight Champions bases is already thin.</summary>
    private static int GroundDefendersPerBase()
    {
        return CommanderUnitCap.Enabled
            ? Mathf.Clamp(CommanderUnitCap.Cap / 30, 2, MaxGroundDefendersPerBase)
            : MaxGroundDefendersPerBase;
    }

    private static int DefencePriority(Unit unit)
    {
        return unit.definition is VehicleDefinition definition && IsAirDefence(definition) ? 1 : 0;
    }

    private static int CountAirDefenders(CommanderState state)
    {
        int count = 0;
        foreach (Unit unit in state.Defenders.Keys)
        {
            count += DefencePriority(unit);
        }

        return count;
    }

    /// <summary>
    /// One runnable check on the posture arithmetic, run beside the plan self-check at plugin load.
    /// These are tuning constants, and a retune that inverts them turns "under attack" into "send
    /// the guard away" without changing a line of logic.
    /// </summary>
    private static void CheckDefencePosture()
    {
        // Read into locals first: compared as constants the compiler folds the whole check away
        // and warns that the failure branch is unreachable, which is exactly the branch that has
        // to survive a retune.
        int ground = MaxGroundDefendersPerBase;
        int posts = DefencePostsPerBase;
        int forwardPosts = ForwardPostsPerBase;
        int launchers = AirDefendersPerBase;
        float innerRing = DefenceRingMinMeters;
        float outerRing = DefenceRingMaxMeters;
        float rearmBelow = DefenceRearmBelow;
        int perPost = DefendersPerPost;
        if (posts < 1 || outerRing < innerRing)
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: the base ring has no usable posts.");
        }

        // The forward posts are the first ones built and air defence is recruited first, which is
        // the whole of "meet the raid on the way in": a ring with no forward posts puts the
        // launchers on the apron.
        if (forwardPosts < 1)
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: the guard has no forward air-defence posts.");
        }

        if (rearmBelow is <= 0f or >= 1f)
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: a dry defender would never restock.");
        }

        // Clusters, not lone vehicles, and a full guard that fits on the posts it has rather than
        // stacking the surplus onto the emptiest one.
        if (perPost < 2 || ground + launchers > (posts + forwardPosts) * perPost)
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: a post holds a lone vehicle, or the full guard "
                    + "does not fit on the posts.");
        }

        CheckSlotLayout();

        // Every manned site wants a radar of its own parked on its post (the game's 300 m link), so
        // the radar target must follow the sites, not stop at the resting BaseRadarTarget.
        int[] oneRadar = { 1, 0 };
        int[] bothManned = { 1, 2 };
        if (SamRadarTarget(7, 3, 3) < 3 || SamRadarTarget(7, 10, 10) < 10
            || !AnySiteLacksRadar(bothManned, oneRadar, 2)
            || AnySiteLacksRadar(oneRadar, oneRadar, 2))
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: there are more SAM sites than there are radar "
                    + "trucks to link them up with.");
        }

        // SamSiteCount is a divisor (a launcher's station is its post modulo it), so a commander
        // with no bases or no posts yet must still come out at one, and a rich one must never ask
        // for more sites than it has posts to stand them on, or more than three on one base.
        CheckSamSites("no bases yet", 0, 0, 1);
        CheckSamSites("one base", 1, 10, SamSitesPerBase);
        CheckSamSites("two bases", 2, 20, 2 * SamSitesPerBase);
        CheckSamSites("champions", 7, 70, MaxSamSites);
        CheckSamSites("posts not laid yet", 7, 2, 2);
    }

    /// <summary>Every parking spot in a cluster is its own, a vehicle's width clear of the others,
    /// and a SAM site's first six (launcher, fire control, radar and company) stay well inside the
    /// game's 300 m link of each other.</summary>
    private static void CheckSlotLayout()
    {
        const int Spots = 18;
        for (int i = 0; i < Spots; i++)
        {
            Vector2 a = SlotOffset(i);
            if (i < 6 && a.magnitude > 140f)
            {
                CommanderPlugin.Log.LogError(
                    $"Enemy defence self-check FAILED: cluster spot {i} is {a.magnitude:0} m from its post.");
            }

            for (int j = i + 1; j < Spots; j++)
            {
                if ((a - SlotOffset(j)).magnitude < 15f)
                {
                    CommanderPlugin.Log.LogError(
                        $"Enemy defence self-check FAILED: cluster spots {i} and {j} overlap.");
                }
            }
        }
    }

    private static void CheckSamSites(string name, int bases, int posts, int expected)
    {
        CommanderState state = new() { DefenceBaseCount = bases };
        for (int i = 0; i < posts; i++)
        {
            state.DefencePosts.Add(default);
        }

        int actual = SamSiteCount(state);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy SAM site self-check FAILED ({name}): expected {expected} site(s), got {actual}.");
        }
    }

    /// <summary>
    /// Now and then one mobile air-defence vehicle leaves the ring for the nearest enemy base, as eyes up
    /// the road. Taken from the guard, which buys its replacement and never takes one back.
    /// </summary>
    private static void ReviewAaScouts(FactionHQ hq, CommanderState state)
    {
        state.AaScouts.RemoveWhere(scout => scout == null || scout.disabled);
        if (Time.time < state.NextAaScoutAt || state.AaScouts.Count >= MaxAaScouts)
        {
            return;
        }

        foreach (Unit unit in state.Defenders.Keys)
        {
            if (unit is GroundVehicle
                && unit != null
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsAirDefence(definition)
                && !IsStaticAirDefence(unit)
                && !IsSamSiteLauncher(unit)
                && CommanderCaptureService.TryGetNearestEnemyBase(hq, unit.transform.GlobalPosition(), out GlobalPosition hold))
            {
                state.Defenders.Remove(unit);
                state.AaScouts.Add(unit);
                state.NextAaScoutAt = Time.time + AaScoutSeconds;
                PinTo(unit, hold);
                CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) sends a {unit.definition.unitName} up the road to scout.");
                return;
            }
        }
    }

    private static bool IsAaScout(Unit unit)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service != null)
        {
            foreach (KeyValuePair<FactionHQ, CommanderState> entry in service.states)
            {
                if (entry.Value.AaScouts.Contains(unit))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ReviewDefences(FactionHQ hq, FactionHQ localHq)
    {
        if (!states.TryGetValue(hq, out CommanderState state))
        {
            state = new CommanderState();
            states[hq] = state;
        }

        ReviewDefence(hq, localHq, state);
        ReviewAaScouts(hq, state);
        ReviewArtillery(hq, localHq, state);
        ReleaseNavalWave(hq, localHq, state);
        ReviewGodNukes(hq, localHq, state);
        SteerPickets(hq, localHq);
        UpdateCruiseHeights(hq);
    }

    private void ReviewDefence(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        if (IsUnderThreat(hq))
        {
            state.ThreatUntil = Time.time + ThreatHoldSeconds;
        }

        bool defending = Time.time < state.ThreatUntil;
        if (defending != state.Defending)
        {
            state.Defending = defending;
            CommanderPlugin.Log.LogInfo(defending
                ? $"Enemy commander ({hq.faction.name}) goes to DEFENCE posture: hostiles inside "
                    + $"{ThreatRadiusMeters / 1000f:0.#} km of its bases."
                : $"Enemy commander ({hq.faction.name}) stands down from DEFENCE posture.");
        }

        EnsureDefencePosts(hq, localHq, state);
        PruneDefenders(hq, state);
        PruneSlots(state);

        // Every ring holds real air defence, not just whatever combat vehicles were free. Launchers are
        // taken first, and surplus handed back is ground vehicles first. Every counter-battery mount is
        // added on top, so it is pinned over a base instead of driven at the player; the cap below never
        // releases air defence, so once in, they stay.
        // CHALLENGING keeps only the per-base ring; the rest of its air defence goes with the push.
        int airTarget = state.DefencePosts.Count == 0
            ? 0
            : IsChallenging(hq) ? ChallengingAirDefenceTarget(state.DefenceBaseCount)
            : (state.DefenceBaseCount * AirDefendersPerBase) + CountCounterBatteryVehicles(hq);
        RecruitAirDefence(hq, state, airTarget);

        // The ground half: GroundDefendersPerBase a base, never more than a fifth of the army. Air
        // defence (launchers included) rides on top and is never released here, or the two
        // recruiters would trade it back and forth every review.
        int groundTarget = state.DefencePosts.Count == 0
            ? 0
            : Mathf.Min(
                state.DefenceBaseCount * GroundDefendersPerBase(),
                CountCombatVehicles(hq) * MaxHomeGuardPercent / 100);
        int target = CountAirDefenders(state) + groundTarget;
        if (state.Defenders.Count > target)
        {
            ReleaseSurplusDefenders(state, target);
        }
        else if (state.Defenders.Count < target)
        {
            RecruitDefenders(hq, state, target);
        }

        // Short of a full ring, or of its launchers, out of what it already owns, so the buy loop
        // puts an air-defence vehicle ahead of its plan — the same precedence the first launcher and
        // a capture unit get.
        state.GuardShort = state.Defenders.Count < target;
        state.AirGuardShort = CountAirDefenders(state) - CountRingLaunchers(state) < airTarget;
        state.WantsDefenceUnit = state.Defenders.Count < target
            || CountAirDefenders(state) - CountRingLaunchers(state) < airTarget;

        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            // A radar-guided launcher stands on the site post whatever it was assigned, because that
            // is where the fire control and the radar that link it up are parked.
            int station = IsSamSiteLauncher(entry.Key) ? entry.Value % SamSiteCount(state) : entry.Value;
            GlobalPosition spot = GetSlot(state, entry.Key, station % state.DefencePosts.Count);

            // A defender that has shot itself dry is a vehicle-shaped hole in the ring, and nothing
            // in the game drives it off to restock: it holds the post it was pinned to until
            // something kills it. Below the threshold it goes to the nearest shelf instead and comes
            // back on its own, because the next review reads a full rack and sends it to its post.
            float ammo = CommanderGameAccess.GetUnitAmmo(entry.Key);
            if (ammo >= 0f && ammo < DefenceRearmBelow && TryFindRearmSource(hq, state, entry.Key, out GlobalPosition store))
            {
                if (!FastMath.InRange(entry.Key.transform.GlobalPosition(), store, RearmArrivedMeters))
                {
                    PinTo(entry.Key, store);
                }

                continue;
            }

            HoldAt(entry.Key, SettleIfStuck(state, entry.Key, spot));
        }

        ReviewSupport(hq, localHq, state);
        ReportGuard(hq, state);
    }

    /// <summary>
    /// The ring's support vehicles: radar trucks spread over the bases so one bombed radar station
    /// does not blind the commander, the fire-control truck a radar-guided launcher needs before it
    /// will shoot, and a munitions truck parked with the guard so a defender can top up without
    /// leaving the base.
    /// </summary>
    /// <remarks>
    /// Deliberately not in <see cref="CommanderState.Defenders"/>: none of these is a combat
    /// vehicle, so counting them in the ring's targets or in its half-the-army cap would thin the
    /// fighting part of it. They are pinned the same way, because they auto-walk at the player
    /// otherwise for the same engine reason everything else here does.
    /// <para>
    /// A launcher, its fire control and a radar all go to <b>one</b> post. That is the game's own
    /// rule, not a preference: <c>Turret.SearchForRadar</c> wants a fire control within 300 m of the
    /// launcher and <c>FireControl.SearchForRadar</c> a radar within 300 m of the truck, so a
    /// StratoLance standing anywhere else on the ring is a launcher that never fires.
    /// </para>
    /// </remarks>
    private void ReviewSupport(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        staleDefenders.Clear();
        foreach (KeyValuePair<Unit, int> entry in state.Support)
        {
            if (entry.Key == null || entry.Key.disabled || entry.Key.NetworkHQ != hq)
            {
                staleDefenders.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleDefenders.Count; i++)
        {
            state.Support.Remove(staleDefenders[i]);
        }

        if (state.DefencePosts.Count == 0)
        {
            return;
        }

        int sites = SamSiteCount(state);
        CountSiteParts(state, sites);
        RelinkSiteParts(state, sites);
        ChooseSamDepot(hq, state, sites);

        // What the guard should be building toward, and what it has: a launcher on each site post,
        // with a fire control and a radar parked beside it. Short of one, the buy review stands up
        // another (TryBuySamLauncher) — nothing in the convoy roll ever reaches for these.
        int launchers = CountSamLaunchers(hq);
        state.WantsSamSite = launchers < sites * LaunchersPerSamSite;

        int radars = 0;
        int fireControls = 0;
        int trucks = 0;
        foreach (Unit unit in state.Support.Keys)
        {
            if (IsRadarVehicle(unit))
            {
                radars++;
            }
            else if (IsRearmVehicle(unit))
            {
                trucks++;
            }
            else
            {
                fireControls++;
            }
        }

        // One truck per site that actually has a launcher on it. A spare truck aims nothing. Sites
        // fill three deep, so that is the manned sites, not the launchers.
        int manned = 0;
        for (int i = 0; i < sites; i++)
        {
            if (siteLaunchers[i] > 0)
            {
                manned++;
            }
        }

        // The first launcher is still in the reserve or on the road: its site wants its truck now.
        manned = Mathf.Max(manned, Mathf.Min(launchers, 1));
        int fireControlTarget = manned;
        int radarTarget = SamRadarTarget(state.DefenceBaseCount, manned, sites);
        int truckTarget = Mathf.Max(Mathf.Min(state.DefenceBaseCount, BaseSupplyTruckTarget), manned);
        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit)
                    || unit == null
                    || unit.disabled
                    || unit is not GroundVehicle
                    || state.Support.ContainsKey(unit)
                    || state.Defenders.ContainsKey(unit)
                    || unit == state.StrikeTruck)
                {
                    continue;
                }

                if (IsRadarVehicle(unit))
                {
                    // A site with a launcher and no radar at its base first, target or not. The rest spread over the ring.
                    int site = ChooseSiteNeeding(state, sites, siteRadars, unit, requireLauncher: true);
                    if (site >= 0 || radars < radarTarget)
                    {
                        state.Support[unit] = site >= 0 ? site : ChooseEmptiestPost(state, unit);
                        if (site >= 0)
                        {
                            siteRadars[site]++;
                        }

                        radars++;
                    }
                }
                else if (IsRearmVehicle(unit))
                {
                    if (trucks < truckTarget)
                    {
                        // A manned site's launchers restock from it without leaving their post.
                        int site = ChooseSiteNeeding(state, sites, siteTrucks, unit, requireLauncher: true);
                        state.Support[unit] = site >= 0 ? site : ChooseEmptiestPost(state, unit);
                        if (site >= 0)
                        {
                            siteTrucks[site]++;
                        }

                        trucks++;
                    }
                }
                else if (IsFireControlVehicle(unit))
                {
                    // Every one is claimed: it does nothing but link a site, and an unclaimed one
                    // drives at the enemy on its own. A manned site at its base first, then a site
                    // waiting for its launcher, else it parks on the base's ring until one comes.
                    int site = ChooseSiteNeeding(state, sites, siteFireControls, unit, requireLauncher: true);
                    if (site < 0)
                    {
                        site = ChooseSiteNeeding(state, sites, siteFireControls, unit, requireLauncher: false);
                    }

                    state.Support[unit] = site >= 0 ? site : ChooseEmptiestPost(state, unit);
                    if (site >= 0)
                    {
                        siteFireControls[site]++;
                    }

                    fireControls++;
                }
            }
        }

        // Read after the loop above, so a free radar already on the field is used before one is bought.
        state.WantsSiteRadar = AnySiteLacksRadar(siteLaunchers, siteRadars, sites);
        state.WantsFireControl = fireControls < fireControlTarget || AnySiteLacksRadar(siteLaunchers, siteFireControls, sites);
        state.SiteRadarDepot = ChooseSiteTruckDepot(hq, state, sites, siteRadars);
        state.SiteFireControlDepot = ChooseSiteTruckDepot(hq, state, sites, siteFireControls);

        foreach (KeyValuePair<Unit, int> entry in state.Support)
        {
            // A site's radar and fire control never settle short of it: the link is 300 m, so a truck
            // set up a kilometre out leaves the launcher silent however long it stands there.
            GlobalPosition spot = GetSlot(state, entry.Key, entry.Value % state.DefencePosts.Count);
            HoldAt(entry.Key, entry.Value < sites && !IsRearmVehicle(entry.Key) ? spot : SettleIfStuck(state, entry.Key, spot));
        }

        ReviewConvoySupport(hq, localHq, state);
    }

    /// <summary>
    /// Every munitions truck the ring did not take rides with the convoy, which is what it was
    /// bought for.
    /// </summary>
    /// <remarks>
    /// Without this they are the only thing the commander owns that nobody ever orders. The MSV
    /// Munitions is authored <c>navigateToObjectives=false</c>, so unlike a fighting vehicle it does
    /// not drive itself at the player either — it takes the depot's roll-off nudge, stops twenty
    /// metres out and stands on the spawn point for the rest of the match, with the next one parked
    /// behind it. The convoy's leading vehicle is the aim point, re-read every review because the
    /// thing being followed drives away, and the truck trails it by however far behind it started.
    /// </remarks>
    private void ReviewConvoySupport(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        if (hq.factionUnits == null)
        {
            return;
        }

        // The convoy's leading vehicle, deliberately not TryGetSpearhead: that one counts every
        // ground vehicle the faction owns, trucks included, so with no convoy out the aim point
        // would be whichever truck was parked nearest the player and the rest would drive into the
        // back of it — which is the depot the whole method exists to clear.
        GlobalPosition playerBase = GetStrikeTarget(localHq);
        GlobalPosition convoy = default;
        bool hasConvoy = false;
        float closest = float.MaxValue;
        defenceCandidates.Clear();
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || state.Support.ContainsKey(unit)
                || state.Defenders.ContainsKey(unit)
                || unit == state.StrikeTruck)
            {
                continue;
            }

            if (IsRearmVehicle(unit))
            {
                defenceCandidates.Add(unit);
                continue;
            }

            float distance = FastMath.Distance(unit.transform.GlobalPosition(), playerBase);
            if (distance < closest)
            {
                closest = distance;
                convoy = unit.transform.GlobalPosition();
                hasConvoy = true;
            }
        }

        // Nothing on the road means nothing to follow, so they wait with the guard instead, a post
        // apart so they do not stack up. Off the spawn point either way, which is the point.
        int spare = hasConvoy ? 0 : ChooseEmptiestPost(state, null);
        for (int i = 0; i < defenceCandidates.Count; i++)
        {
            Unit truck = defenceCandidates[i];
            GlobalPosition destination = hasConvoy
                ? convoy
                : state.DefencePosts[(spare + i) % state.DefencePosts.Count];
            if (!FastMath.InRange(truck.transform.GlobalPosition(), destination, ConvoyArrivedMeters))
            {
                PinTo(truck, destination);
            }
        }

        defenceCandidates.Clear();
    }

    private void PruneDefenders(FactionHQ hq, CommanderState state)
    {
        staleDefenders.Clear();
        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            if (entry.Key == null || entry.Key.disabled || entry.Key.NetworkHQ != hq || IsPostAcrossWater(state, entry.Key, entry.Value))
            {
                staleDefenders.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleDefenders.Count; i++)
        {
            state.Defenders.Remove(staleDefenders[i]);
        }
    }

    /// <summary>Drops the parking spots of vehicles that are no longer in the guard, so their place
    /// in the cluster goes to the next one.</summary>
    private void PruneSlots(CommanderState state)
    {
        staleDefenders.Clear();
        foreach (Unit unit in state.Slots.Keys)
        {
            if (unit == null || (!state.Defenders.ContainsKey(unit) && !state.Support.ContainsKey(unit)))
            {
                staleDefenders.Add(unit!);
            }
        }

        for (int i = 0; i < staleDefenders.Count; i++)
        {
            state.Slots.Remove(staleDefenders[i]);
            state.GuardProgress.Remove(staleDefenders[i]);
        }
    }

    private void RecruitDefenders(FactionHQ hq, CommanderState state, int target)
    {
        defenceCandidates.Clear();
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsCombatVehicle(definition)
                && definition.vehicleType != VehicleType.ART
                && DefencePriority(unit) == 0
                && !state.Defenders.ContainsKey(unit)
                && IsRecruitable(hq, unit))
            {
                // Ground only: RecruitAirDefence owns air defence and its own count.
                defenceCandidates.Add(unit);
            }
        }

        for (int i = 0; i < defenceCandidates.Count && state.Defenders.Count < target; i++)
        {
            state.Defenders[defenceCandidates[i]] = ChooseClusterPost(state, defenceCandidates[i]);
        }
    }

    /// <summary>
    /// Radar-guided launchers this faction has, deployed and still sitting in the reserve. The
    /// reserve half matters: a purchase takes a depot cooldown to reach the field, and counting only
    /// what is standing there buys another launcher on every review until the first one arrives.
    /// </summary>
    private int CountSamLaunchers(FactionHQ hq)
    {
        int count = 0;
        for (int i = 0; i < rosterParts.Count; i++)
        {
            if (IsSamSiteLauncher(rosterParts[i]))
            {
                count += hq.GetUnitSupply(rosterParts[i]);
            }
        }

        if (hq.factionUnits == null)
        {
            return count;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle && !unit.disabled && IsSamSiteLauncher(unit))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Counter-battery vehicles this faction has, deployed and still sitting in the reserve. Counts
    /// the reserve for the same reason <see cref="CountSamLaunchers"/> does: a purchase takes a
    /// depot cooldown to reach the field, and counting only what is standing there buys another on
    /// every review until the first one arrives.
    /// </summary>
    private int CountCounterBatteryVehicles(FactionHQ hq)
    {
        int count = 0;
        for (int i = 0; i < rosterParts.Count; i++)
        {
            if (IsCounterBatteryDefence(rosterParts[i]))
            {
                count += hq.GetUnitSupply(rosterParts[i]);
            }
        }

        if (hq.factionUnits == null)
        {
            return count;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle && !unit.disabled && IsCounterBatteryDefence(unit))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Puts free air-defence vehicles into the ring until it holds <paramref name="airTarget"/>,
    /// even past the ring's size: <see cref="ReleaseSurplusDefenders"/> then hands back ground vehicles.
    /// Radar-guided launchers come first and are exempt from that target, see below.</summary>
    private void RecruitAirDefence(FactionHQ hq, CommanderState state, int airTarget)
    {
        if (hq.factionUnits == null)
        {
            return;
        }

        // Launchers first, and over the target if it comes to that. A radar-guided launcher that is
        // not in the guard is not merely idle: nothing pins it, so CheckObstacles drives the most
        // expensive vehicle the commander owns at the player on its own. The ring's own cap in
        // ReviewDefence already refuses to release below CountAirDefenders, so this holds.
        // Launchers do not count toward airTarget: they are extra, so the site keeps its other air
        // defence however many StratoLances stand on it. Each goes straight to a site with room.
        int sites = SamSiteCount(state);
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && IsSamSiteLauncher(unit)
                && !state.Defenders.ContainsKey(unit))
            {
                int site = ChooseSamSite(state, sites, unit);
                if (site < 0)
                {
                    break;
                }

                state.Defenders[unit] = SamSitePost + site;
            }
        }

        int held = CountAirDefenders(state) - CountRingLaunchers(state);

        // The AFV8 Mobile Air Defense and Linebreaker SAM serve both jobs, and the ring has room for
        // every one, so it leaves the convoys the escorts they want first. The rest always join it:
        // a capture push never takes one, so out of the ring nothing pins it.
        // A Spaag is ordinary ring air defence until a push wants one, so it is held back the same way:
        // it rolls out beside the ring, which would otherwise take it before the push ever saw it.
        int convoys = CommanderCaptureService.Instance?.CountConvoys(hq) ?? 0;
        int spareEscorts = -convoys * ConvoyAirDefencePerDrive;
        int spareSpaags = -convoys * ConvoySpaagPerDrive;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && !state.Defenders.ContainsKey(unit))
            {
                spareEscorts += IsConvoyAirDefence(unit) ? 1 : 0;
                spareSpaags += IsConvoySpaag(unit) ? 1 : 0;
            }
        }

        // Counter-battery mounts first, then the rest of the air defence. The target above is raised
        // by exactly the number of those the commander owns, so taking them last would let ordinary
        // launchers fill the room made for them and leave the anti-rocket mounts roaming.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                // Over the target only for air defence on an island: it cannot go anywhere else, and
                // left out of the guard it stood at the depot door it rolled out of.
                if (id.TryGetUnit(out Unit unit)
                    && unit is GroundVehicle
                    && !unit.disabled
                    && DefencePriority(unit) > 0
                    && (pass > 0 || IsCounterBatteryDefence(unit) || IsStaticAirDefence(unit))
                    && !state.Defenders.ContainsKey(unit))
                {
                    bool escort = IsConvoyAirDefence(unit);
                    bool spaag = IsConvoySpaag(unit);
                    if ((escort ? spareEscorts > 0 : held < airTarget || IsIslandAt(GetHomeBase(state, unit)) || IsStaticAirDefence(unit))
                        && (!spaag || spareSpaags > 0)
                        && IsRecruitable(hq, unit))
                    {
                        state.Defenders[unit] = ChooseClusterPost(state, unit);
                        held++;
                        spareEscorts -= escort ? 1 : 0;
                        spareSpaags -= spaag ? 1 : 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The depot the next StratoLance rolls out of: the one nearest the site <see cref="SiteFillRank"/>
    /// wants filled, among sites with a depot within <see cref="RecruitRadiusMeters"/>. A site with none
    /// is passed over, because a launcher joins a site at its own base and would never reach it.
    /// </summary>
    private void ChooseSamDepot(FactionHQ hq, CommanderState state, int sites)
    {
        state.SamDepot = null;
        int best = -1;
        for (int i = 0; i < sites; i++)
        {
            VehicleDepot? nearest = FindSiteDepot(hq, state.DefencePosts[i]);
            if (nearest != null
                && (best < 0
                    || SiteFillRank(siteLaunchers[i]) < SiteFillRank(siteLaunchers[best])
                    || (SiteFillRank(siteLaunchers[i]) == SiteFillRank(siteLaunchers[best])
                        && siteLaunchers[i] > 0
                        && siteLaunchers[i] < siteLaunchers[best])))
            {
                best = i;
                state.SamDepot = nearest;
            }
        }
    }

    /// <summary>The depot a truck for the first manned site lacking <paramref name="parts"/> rolls out
    /// of. With every manned site whole, the one the next launcher is coming to.</summary>
    private VehicleDepot? ChooseSiteTruckDepot(FactionHQ hq, CommanderState state, int sites, int[] parts)
    {
        for (int i = 0; i < sites; i++)
        {
            VehicleDepot? depot = siteLaunchers[i] > 0 && parts[i] == 0 ? FindSiteDepot(hq, state.DefencePosts[i]) : null;
            if (depot != null)
            {
                return depot;
            }
        }

        return state.SamDepot;
    }

    /// <summary>
    /// The site a new launcher joins, always at the base it rolled out at: a started site short of
    /// <see cref="SamSiteFillLaunchers"/>, then a new one, then a fourth on the emptiest filled one, then
    /// crowding a full one. Crowding beats refusing: an unassigned launcher is unpinned and drives at the
    /// player. -1 only with no posts laid.
    /// </summary>
    private int ChooseSamSite(CommanderState state, int sites, Unit unit)
    {
        if (state.DefencePosts.Count == 0)
        {
            return -1;
        }

        CountSiteParts(state, sites);
        GlobalPosition home = GetHomeBase(state, unit);
        GlobalPosition at = unit.transform.GlobalPosition();
        int best = -1;
        bool bestAtHome = false;
        for (int i = 0; i < sites; i++)
        {
            bool atHome = IsPostAt(state, i, home);
            if (best >= 0 && bestAtHome && !atHome)
            {
                continue;
            }

            if (best < 0
                || (atHome && !bestAtHome)
                || SiteFillRank(siteLaunchers[i]) < SiteFillRank(siteLaunchers[best])
                || (SiteFillRank(siteLaunchers[i]) == SiteFillRank(siteLaunchers[best])
                    && siteLaunchers[i] > 0
                    && siteLaunchers[i] < siteLaunchers[best])
                || (siteLaunchers[i] == siteLaunchers[best]
                    && IsCloserBase(state.DefencePosts[i], state.DefencePosts[best], home, at, state.DefencePostBases[i], state.DefencePostBases[best])))
            {
                best = i;
                bestAtHome = atHome;
            }
        }

        return best;
    }

    /// <summary>
    /// The post a new ring defender joins: the first short of <see cref="DefendersPerPost"/>, in the
    /// order the posts were laid (forward posts first), at the base with the fewest defenders, else
    /// the nearest post with room anywhere, else the emptiest at home. Preferring the home base keeps
    /// guards from crossing the map.
    /// </summary>
    private int ChooseClusterPost(CommanderState state, Unit unit)
    {
        int posts = state.DefencePosts.Count;
        if (posts == 0)
        {
            return 0;
        }

        CountPostOccupancy(state, includeSupport: false);
        CountBaseOccupancy(state);
        GlobalPosition home = GetHomeBase(state, unit);
        GlobalPosition at = unit.transform.GlobalPosition();

        // Stationary air defence stays at the base it rolled out at, crowding a full ring if it has to.
        if (IsStaticAirDefence(unit))
        {
            int own = -1;
            for (int i = 0; i < posts; i++)
            {
                if (IsPostAt(state, i, home) && (own < 0 || postOccupancy[i] < postOccupancy[own]))
                {
                    own = i;
                }
            }

            if (own >= 0)
            {
                return own;
            }
        }

        // The base with the fewest defenders, its own base on a tie, then the nearest. Its own base
        // outright left every base without a working depot unguarded: the enemy's vehicles roll out
        // of one or two bases, and those filled their rings and the half-the-army cap between them.
        int target = -1;
        for (int b = 0; b < baseCentres.Count; b++)
        {
            if (!CanDriveBetween(home, baseCentres[b]))
            {
                continue;
            }

            if (target < 0
                || baseCounts[b] < baseCounts[target]
                || (baseCounts[b] == baseCounts[target] && IsCloserBase(baseCentres[b], baseCentres[target], home, at)))
            {
                target = b;
            }
        }

        // The emptiest post at that base, the lowest (forward) one on a tie, so the guard goes round
        // the whole base. The first post with room filled the two forward posts and one ring post
        // and left the rest of the ring empty.
        int emptiest = -1;
        for (int i = 0; i < posts && target >= 0; i++)
        {
            if (postOccupancy[i] < DefendersPerPost
                && IsPostAt(state, i, baseCentres[target])
                && (emptiest < 0 || postOccupancy[i] < postOccupancy[emptiest]))
            {
                emptiest = i;
            }
        }

        if (emptiest >= 0)
        {
            return emptiest;
        }

        int nearest = -1;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < posts; i++)
        {
            float distance = FastMath.Distance(at, state.DefencePosts[i]);
            if (postOccupancy[i] < DefendersPerPost && distance < nearestDistance
                && i < state.DefencePostBases.Count && CanDriveBetween(home, state.DefencePostBases[i]))
            {
                nearestDistance = distance;
                nearest = i;
            }
        }

        if (nearest < 0 && IsIslandAt(home))
        {
            // Every post on its island is full: crowd the island's own posts rather than pick one
            // across the water it can never reach.
            for (int i = 0; i < posts && i < state.DefencePostBases.Count; i++)
            {
                float distance = FastMath.Distance(at, state.DefencePosts[i]);
                if (distance < nearestDistance && CanDriveBetween(home, state.DefencePostBases[i]))
                {
                    nearestDistance = distance;
                    nearest = i;
                }
            }
        }

        return nearest >= 0 ? nearest : ChooseEmptiestPost(state, unit);
    }

    /// <summary>Ring defenders at each base (<see cref="baseCentres"/>), from <see cref="postOccupancy"/>.</summary>
    private void CountBaseOccupancy(CommanderState state)
    {
        baseCentres.Clear();
        baseCounts.Clear();
        for (int i = 0; i < state.DefencePosts.Count && i < state.DefencePostBases.Count; i++)
        {
            int b = baseCentres.Count - 1;
            while (b >= 0 && !CommanderGameAccess.ApproximatelyEqual(baseCentres[b], state.DefencePostBases[i], 1f))
            {
                b--;
            }

            if (b < 0)
            {
                baseCentres.Add(state.DefencePostBases[i]);
                baseCounts.Add(0);
                b = baseCentres.Count - 1;
            }

            baseCounts[b] += postOccupancy[i];
        }
    }

    /// <summary>
    /// Logs the guard at each base when it changes, at most once a minute.
    /// </summary>
    private void ReportGuard(FactionHQ hq, CommanderState state)
    {
        if (Time.time < state.NextGuardReportAt)
        {
            return;
        }

        CountPostOccupancy(state, includeSupport: false);
        CountBaseOccupancy(state);
        guardReport.Clear();
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.center == null || !CommanderGameAccess.IsLandBase(airbase))
            {
                continue;
            }

            int count = 0;
            for (int b = 0; b < baseCentres.Count; b++)
            {
                if (CommanderGameAccess.ApproximatelyEqual(baseCentres[b], airbase.center.GlobalPosition(), 1f))
                {
                    count = baseCounts[b];
                }
            }

            guardReport.Append(guardReport.Length == 0 ? string.Empty : ", ")
                .Append(CommanderCaptureService.GetAirbaseLabel(airbase)).Append(' ').Append(count);
        }

        string report = guardReport.ToString();
        if (report != state.LastGuardReport)
        {
            state.LastGuardReport = report;
            state.NextGuardReportAt = Time.time + 60f;
            CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) home guard by base: {report}.");
        }
    }

    /// <summary>The emptiest post, at the base <paramref name="unit"/> stands at when one is given.</summary>
    private int ChooseEmptiestPost(CommanderState state, Unit? unit)
    {
        int posts = state.DefencePosts.Count;
        if (posts == 0)
        {
            return 0;
        }

        CountPostOccupancy(state, includeSupport: true);
        GlobalPosition home = unit != null ? GetHomeBase(state, unit) : default;
        int best = -1;
        for (int i = 0; i < posts; i++)
        {
            if ((unit == null || IsPostAt(state, i, home)) && (best < 0 || postOccupancy[i] < postOccupancy[best]))
            {
                best = i;
            }
        }

        return Mathf.Max(best, 0);
    }

    private void CountPostOccupancy(CommanderState state, bool includeSupport)
    {
        int posts = Mathf.Max(state.DefencePosts.Count, 1);
        if (postOccupancy.Length < posts)
        {
            postOccupancy = new int[posts];
        }

        for (int i = 0; i < posts; i++)
        {
            postOccupancy[i] = 0;
        }

        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            // A site's launchers are on top of its cluster, not part of it.
            if (!IsSamSiteLauncher(entry.Key))
            {
                postOccupancy[entry.Value % posts]++;
            }
        }

        if (includeSupport)
        {
            foreach (KeyValuePair<Unit, int> entry in state.Support)
            {
                postOccupancy[entry.Value % posts]++;
            }
        }
    }

    /// <summary>Launchers, fire controls and radars standing on each SAM site's post.</summary>
    private void CountSiteParts(CommanderState state, int sites)
    {
        if (siteLaunchers.Length < sites)
        {
            siteLaunchers = new int[sites];
            siteFireControls = new int[sites];
            siteRadars = new int[sites];
            siteTrucks = new int[sites];
        }

        for (int i = 0; i < sites; i++)
        {
            siteLaunchers[i] = siteFireControls[i] = siteRadars[i] = siteTrucks[i] = 0;
        }

        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            if (IsSamSiteLauncher(entry.Key))
            {
                siteLaunchers[entry.Value % sites]++;
            }
        }

        foreach (KeyValuePair<Unit, int> entry in state.Support)
        {
            if (entry.Value >= sites || entry.Key == null)
            {
                continue;
            }

            if (IsRadarVehicle(entry.Key))
            {
                siteRadars[entry.Value]++;
            }
            else if (IsFireControlVehicle(entry.Key))
            {
                siteFireControls[entry.Value]++;
            }
            else if (IsRearmVehicle(entry.Key))
            {
                siteTrucks[entry.Value]++;
            }
        }
    }

    /// <summary>The nearest site at the unit's own base lacking this part (<paramref name="parts"/>
    /// counts it per site), among those with a launcher on them when <paramref name="requireLauncher"/>.
    /// -1 if none. Never another base: that base's site buys its own.</summary>
    private int ChooseSiteNeeding(CommanderState state, int sites, int[] parts, Unit unit, bool requireLauncher)
    {
        GlobalPosition at = unit.transform.GlobalPosition();
        GlobalPosition home = GetHomeBase(state, unit);
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < sites && i < state.DefencePosts.Count; i++)
        {
            if (parts[i] > 0 || (requireLauncher && siteLaunchers[i] == 0) || !IsPostAt(state, i, home))
            {
                continue;
            }

            float distance = FastMath.Distance(at, state.DefencePosts[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Moves a fire control or radar that is not linking anything up (its site has no launcher, or
    /// already has one of it) to a site whose launcher is waiting for it. A launcher without both
    /// inside 300 m never fires.
    /// </summary>
    private void RelinkSiteParts(CommanderState state, int sites)
    {
        relinkScratch.Clear();
        relinkScratch.AddRange(state.Support.Keys);
        for (int i = 0; i < relinkScratch.Count; i++)
        {
            Unit unit = relinkScratch[i];

            // A munitions truck too: a StratoLance that shot itself dry drove off to the nearest store
            // and back, which read as launchers wandering about instead of standing on their site.
            int[]? parts = IsRadarVehicle(unit) ? siteRadars
                : IsFireControlVehicle(unit) ? siteFireControls
                : IsRearmVehicle(unit) ? siteTrucks
                : null;
            if (parts == null)
            {
                continue;
            }

            int current = state.Support[unit];
            if (current < sites && siteLaunchers[current] > 0 && parts[current] <= 1)
            {
                continue;
            }

            int site = ChooseSiteNeeding(state, sites, parts, unit, requireLauncher: true);
            if (site < 0)
            {
                continue;
            }

            if (current < sites)
            {
                parts[current]--;
            }

            parts[site]++;
            state.Support[unit] = site;
        }

        relinkScratch.Clear();
    }
}
