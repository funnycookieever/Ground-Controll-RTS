using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's air force: buying airframes, launching them, and telling them what to bomb.
/// </summary>
/// <remarks>
/// <c>AIPilotCombatModes.NoTarget</c> switches to landing after fifteen ticks without a target,
/// which a fresh launch hits long before it finds the player. Enemy airframes therefore get Air
/// Command missions like the player's; <c>NoTargetPrefix</c> zeroes the counter for anything
/// carrying one.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// Radius of the strike box the enemy works over. Wide enough to cover a base and the ground
    /// around it, because the mission area is also the target filter for a strike sortie.
    /// </summary>
    private const float StrikeRadiusMeters = 25000f;

    /// <summary>Added to an income building's price when a bomber picks its target, more than any
    /// building costs, so a mine always comes first.</summary>
    private const float IncomeTargetBonus = 100000f;

    /// <summary>A glide-bomb carrier aims at the densest knot of the player's known ground targets
    /// within this of itself or of the strike box, convoys included, and its pass spreads over every
    /// one within <see cref="SalvoClusterMeters"/>, so a swarm is not spent on one target.</summary>
    private const float SalvoSearchMeters = 40000f;

    private const float SalvoClusterMeters = 1500f;

    /// <summary>Candidates scored per pick; the cluster score is pairwise, so this bounds it.</summary>
    private const int MaxSalvoCandidates = 200;

    /// <summary>Radius of the combat air patrol the commander holds over its own ground.</summary>
    private const float HomeGuardRadiusMeters = 15000f;

    /// <summary>
    /// The radar picket (the Medusa, see <see cref="IsRadarPicket"/>) flies AWACS on the far side of
    /// the commander's own ground, between these distances from the player's base, and its station is
    /// pushed out whenever anything of the player's comes within <see cref="PicketSafeMeters"/> of it.
    /// Its radar feeds the tracking database the whole enemy fights from. One is kept up once the
    /// wing has <see cref="PicketAfterAirframes"/> airframes.
    /// </summary>
    private const float PicketMinStandoffMeters = 35000f;
    private const float PicketMaxStandoffMeters = 60000f;
    private const float PicketSafeMeters = 25000f;
    private const float PicketStationRadiusMeters = 8000f;

    /// <summary>Strike airframes that have to be up before they go for the player's base together,
    /// and how few may be left before the survivors pull back to wait for the next wave.</summary>
    private const int StrikeWaveSize = 3;

    /// <summary>Reach at which an anti-ship store scores double: outside a warship's air defence.</summary>
    private const float AntiShipStandoffMeters = 20000f;

    /// <summary>A CHALLENGING anti-ship buy keeps the airframes whose reach is at least this share of
    /// the longest on offer.</summary>
    private const float AntiShipReachShare = 0.6f;
    private const int StrikeWaveBreakSize = 2;

    /// <summary>The smallest wave <see cref="ReachableWaveSize"/> cuts to: one strike airframe and
    /// one fighter escorting it is enough.</summary>
    private const int MinStrikeWaveSize = 2;

    /// <summary>Scaled seconds a striker waits at staging before the wave goes with what is up,
    /// rolled per wave between these.</summary>
    private const float MinStrikeWaveWaitSeconds = 30f;
    private const float MaxStrikeWaveWaitSeconds = 120f;

    /// <summary>Share of waves that go straight in; the rest swing out to one side first.</summary>
    private const float StraightWaveShare = 0.35f;

    /// <summary>How far out to the side a flank swings, as a share of the run in, and its bounds.</summary>
    private const float MinFlankShare = 0.3f;
    private const float MaxFlankShare = 0.55f;
    private const float MinFlankMeters = 6000f;
    private const float MaxFlankMeters = 20000f;

    /// <summary>How much a guided missile outscores a rocket or gun of the same punch on close support.</summary>
    private const float CasGuidedMissileWeight = 2f;
    private const int PicketAfterAirframes = 2;

    /// <summary>Radar pickets the commander keeps up at most. One more is allowed per
    /// <see cref="PicketAfterAirframes"/> airframes in the wing, up to this.</summary>
    private const int MaxRadarPickets = 2;

    /// <summary>The picket's own radar must outrange every other airframe's on the roster by this.</summary>
    private const float PicketRadarMargin = 1.5f;

    /// <summary>Highest cruise height (radar altitude) the commander asks for. The game's airframe
    /// data has no ceiling to read, and its own evasion code only clamps at 8 km.</summary>
    private const float CruiseCeilingMeters = 12000f;

    /// <summary>A standard loadout is rolled among those scoring at least this share of the best.</summary>
    private const float LoadoutRollShare = 0.6f;

    /// <summary>Where a bomb carrier cruises and runs in from (radar altitude).</summary>
    private const float BomberCruiseMeters = 10500f;

    /// <summary>How far outside a known air-defence unit's reach an aircraft starts climbing over it.</summary>
    private const float AirDefenceMarginMeters = 10000f;

    /// <summary>Reviews' worth of saving a fund may hold before the surplus goes back to the
    /// ground spender. Without a ceiling, a commander that can never buy an aircraft — no compatible
    /// strip, no hull it can afford — would quietly withhold its air share from the convoys forever.</summary>
    private const int FundSaveReviews = 6;

    /// <summary>Airframes of one role the commander runs before it starts buying the dear ones.</summary>
    private const int CheapAirframesPerRole = 2;

    /// <summary>Most Crickets one enemy commander has alive at once. See <c>DropCrickets</c>.</summary>
    private const int MaxLiveCrickets = 6;

    /// <summary>Tenfolds over the authored balance past which the wing skips the cheap opening
    /// altogether. The price band exists to stop a poor commander buying jets it cannot replace, and
    /// a commander holding a hundred times what the mission authored is not that commander.</summary>
    private const float DearAirframeDecades = 2f;

    /// <summary>
    /// How far either side of the cheapest (or dearest) airframe a buy will look, so the same
    /// airframe is not picked every time.
    /// </summary>
    private const float AirPriceBand = 1.7f;

    /// <summary>Rotary airframes the commander keeps in the air at once: one gunship riding the
    /// convoy, one running deliveries. A third adds nothing the first two are not already doing.</summary>
    private const int RotaryLimit = 2;

    /// <summary>How wide the convoy escort looks for targets. It is a target filter, not a patrol
    /// area — the helicopter flies itself — so it is the gunship's engagement bubble around the
    /// vehicles it is riding with, not the size of the battle.</summary>
    private const float EscortRadiusMeters = 8000f;

    /// <summary>Fighters the commander keeps up whatever the player flies.</summary>
    private const int MinFighters = 2;

    /// <summary>Fighters the commander will match the player's aircraft with, one for one.</summary>
    private const int MaxFightersForPlayerAir = 10;

    /// <summary>Summed best anti-air effectiveness over an airframe's hardpoints for a Strike-role
    /// hull to be bought as an interceptor: roughly two good missile pylons. The roster log prints
    /// each airframe's figure ("interceptor N"), which is where to retune this.</summary>
    private const float InterceptorMinScore = 1.5f;

    /// <summary>How hard a fighter buy leans on price, see <see cref="InterceptorWeight"/>.</summary>
    private const float InterceptorPricePower = 0.75f;

    private const int MaxCaptureLanders = 2;

    /// <summary>A loss this soon after launch means the airframe never did its job.</summary>
    private const float QuickLossSeconds = 240f;

    /// <summary>How far back quick losses are counted, and how long a type sits out.</summary>
    private const float QuickLossWindowSeconds = 600f;

    /// <summary>Quick losses inside the window that bench a type.</summary>
    private const int QuickLossesToBench = 3;

    private static readonly Dictionary<AircraftDefinition, float> RadarRanges = new();

    private static readonly List<WeaponMount> LegalMounts = new();

    private static readonly List<GlobalPosition> PicketBases = new();

    /// <summary>Per airframe, what <see cref="GetInterceptorScore"/> read off its prefab.</summary>
    private static readonly Dictionary<AircraftDefinition, float> InterceptorScores = new();

    private static readonly AccessTools.FieldRef<Airbase.Runway, Queue<Aircraft>> DiagTakeoffQueue =
        AccessTools.FieldRefAccess<Airbase.Runway, Queue<Aircraft>>("takeoffQueue");

    /// <summary>The aircraft a hangar spawned last; it stays shut until that one is clear of it.</summary>
    private static readonly AccessTools.FieldRef<Hangar, GameObject> HangarSpawned =
        AccessTools.FieldRefAccess<Hangar, GameObject>("spawnedObject");

    /// <summary>Each airframe's surface stores heavy enough to matter, see <see cref="GetAntiShipReach"/>.</summary>
    private static readonly Dictionary<AircraftDefinition, List<WeaponInfo>> AntiShipStores = new();

    /// <summary>Per faction, what <see cref="GetAirRoster"/> built and on which frame.</summary>
    private static readonly Dictionary<FactionHQ, (int Frame, List<AircraftDefinition> List)> AirRosters = new();

    private static readonly List<(Unit Unit, GlobalPosition At, float Weight)> SalvoCandidates = new();

    /// <summary>When each field last put an aircraft up, so sorties are shared out across the
    /// fields that can fly the chosen airframe instead of all coming off the same strip.</summary>
    private readonly Dictionary<Airbase, float> lastLaunchTimes = new();

    /// <summary>What an airframe is for, read off its own role data rather than a name table.</summary>
    /// <remarks>
    /// <c>UnitDefinition.roleIdentity</c> is the game's own answer to "what does this thing kill",
    /// and <c>captureCapacity</c> is its answer to "does it carry troops". Both are asset data that
    /// survives a patch; a list of aircraft names does not.
    /// </remarks>
    private enum AirRole
    {
        /// <summary>Ground attack. Expensive per airframe, which is why it cannot be the only buy.</summary>
        Strike,

        /// <summary>Air-to-air. Bought as a counter to the player being in the sky, not by default.</summary>
        Fighter,

        /// <summary>Carries troops or cargo. Rotary, and the Basegame flies it without any help.</summary>
        Transport,
    }

    /// <summary>
    /// The airframes an enemy factory may make: this faction's roster that some field it holds
    /// could launch, less the radar picket (one or two a match). Only a type in
    /// <c>Encyclopedia.Lookup</c>, the table <c>Factory.SetFactory</c> resolves its product in.
    /// </summary>
    internal static void CollectFactoryAirframes(FactionHQ hq, List<UnitDefinition> into)
    {
        if (Encyclopedia.Lookup == null)
        {
            return;
        }

        foreach (AircraftDefinition definition in GetAirRoster(hq))
        {
            if (definition != null
                && !string.IsNullOrEmpty(definition.jsonKey)
                && Encyclopedia.Lookup.TryGetValue(definition.jsonKey, out UnitDefinition listed)
                && ReferenceEquals(listed, definition)
                && !CommanderUnitBanService.IsBanned(definition)
                && !IsRadarPicket(definition, hq)
                && CanAnyFieldLaunch(hq, definition))
            {
                into.Add(definition);
            }
        }
    }

    /// <summary>
    /// Sets a slice of this review's pot aside, and returns what it actually took so the caller can
    /// deduct exactly that. Airframes are dear and ground vehicles are cheap, so a flat per-review
    /// slice never added up to an aircraft once the commander was also buying convoys — it kept the
    /// balance too low for its own air share to ever clear a purchase price. Saving is the whole fix:
    /// it is the difference between an enemy that flew for the first few minutes and then never
    /// again, and one that keeps a wing up all match.
    /// </summary>
    private static float AccrueFund(ref float fund, float share, float floor = 0f)
    {
        // The ceiling is six reviews' share *or* the price of the dearest airframe this commander
        // could actually put in the air, whichever is higher. Without the floor a commander whose
        // per-review share is small can never save up to a top-tier jet no matter how long the match
        // runs, which is the "the AI only ever flew the starter aeroplanes" report: it was not that
        // it preferred them, it was that its fund was capped below their price.
        float ceiling = Mathf.Max(share * FundSaveReviews, floor);
        float taken = Mathf.Clamp(ceiling - fund, 0f, Mathf.Max(0f, share));
        fund += taken;
        return taken;
    }

    private static AirRole GetAirRole(AircraftDefinition definition)
    {
        if (definition.captureCapacity > 0 && !CommanderAirCommandService.HasPlanePilot(definition))
        {
            return AirRole.Transport;
        }

        return definition.roleIdentity.antiAir > definition.roleIdentity.antiSurface
            ? AirRole.Fighter
            : AirRole.Strike;
    }

    /// <summary>
    /// What a live aircraft is doing, which for a multirole hull is what it was armed with. The
    /// Compass and the Vagrant are Strike by their role data, so a Compass sent up with nothing but
    /// air-to-air missiles must still count (and be tasked) as a fighter.
    /// </summary>
    private static AirRole GetAirRole(Aircraft aircraft)
    {
        AirRole role = GetAirRole(aircraft.definition);
        return role == AirRole.Strike && CarriesOnlyAirToAir(aircraft) ? AirRole.Fighter : role;
    }

    /// <summary>At least one air-to-air store aboard and nothing that attacks the ground (guns and
    /// jammers aside), i.e. exactly what <see cref="StripSurfaceStores"/> leaves.</summary>
    private static bool CarriesOnlyAirToAir(Aircraft aircraft)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        bool air = false;
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponInfo? info = stations[i]?.WeaponInfo;
            if (info == null || info.gun || info.jammer)
            {
                continue;
            }

            if (IsAirOnlyInfo(info))
            {
                air = true;
            }
            else if (info.effectiveness.antiSurface > 0.05f)
            {
                return false;
            }
        }

        return air;
    }

    /// <summary>
    /// How well an airframe can be armed to kill aircraft: the best air-to-air store each hardpoint
    /// set offers, summed. Asset data off the prefab, so it needs no name table; the roster log
    /// prints it. This is what lets the Compass, Vagrant and Ifrit (Scythes, heat seekers,
    /// Scimitars) be bought as interceptors on a strip that takes no dedicated fighter.
    /// </summary>
    private static float GetInterceptorScore(AircraftDefinition definition)
    {
        if (InterceptorScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = 0f;
        HardpointSet[]? sets = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets
            : null;
        for (int i = 0; sets != null && i < sets.Length; i++)
        {
            float best = 0f;
            List<WeaponMount>? options = sets[i].weaponOptions;
            for (int m = 0; options != null && m < options.Count; m++)
            {
                WeaponInfo? info = options[m]?.info;
                if (info != null && IsAirOnlyInfo(info))
                {
                    best = Mathf.Max(best, info.effectiveness.antiAir);
                }
            }

            score += best;
        }

        InterceptorScores[definition] = score;
        return score;
    }

    /// <summary>
    /// A fighter buy's odds for one airframe: air-to-air squared, times price to
    /// <see cref="InterceptorPricePower"/>. Score alone made the 8-credit Cricket (3.68) an even
    /// roll with the 30-credit Compass (3.74), and an AI-against-AI log launched 60 Cricket
    /// fighters, the most of any type. With price the Cricket still beats the Brawler (1.84) but
    /// sits well under the Compass and Vagrant; <see cref="CheckInterceptorWeights"/> pins that.
    /// </summary>
    private static float InterceptorWeight(float score, float value)
    {
        score = Mathf.Max(0.1f, score);
        return score * score * Mathf.Pow(Mathf.Max(1f, value), InterceptorPricePower);
    }

    /// <summary>Interceptor weights: Cricket must beat Brawler and stay under half the Compass and Vagrant.</summary>
    private static void CheckInterceptorWeights()
    {
        float cricket = InterceptorWeight(3.68f, 8f);
        float brawler = InterceptorWeight(1.84f, 32f);
        float compass = InterceptorWeight(3.74f, 30f);
        float vagrant = InterceptorWeight(4.68f, 40f);
        if (!(cricket > brawler && cricket * 2f < compass && cricket * 2f < vagrant))
        {
            CommanderPlugin.Log.LogError(
                $"Enemy interceptor weight self-check FAILED: Cricket {cricket:0}, Brawler {brawler:0}, "
                + $"Compass {compass:0}, Vagrant {vagrant:0}.");
        }
    }

    /// <summary>A fighter buy takes a dedicated fighter, or any fixed-wing airframe that can carry
    /// <see cref="InterceptorMinScore"/> worth of air-to-air missiles.</summary>
    private static bool FitsRole(AircraftDefinition definition, AirRole role)
    {
        AirRole own = GetAirRole(definition);
        return own == role
            || (role == AirRole.Fighter
                && own == AirRole.Strike
                && CommanderAirCommandService.HasPlanePilot(definition)
                && GetInterceptorScore(definition) >= InterceptorMinScore);
    }

    /// <summary>
    /// Every airframe this faction may buy: what its held fields' hangars list (the same list the
    /// player's AIR window offers), plus anything already in stock. <c>hq.AircraftSupply</c> alone is
    /// only the mission's authored supplies. Built once per frame and shared within a review.
    /// </summary>
    private static List<AircraftDefinition> GetAirRoster(FactionHQ hq)
    {
        if (!AirRosters.TryGetValue(hq, out (int Frame, List<AircraftDefinition> List) cached))
        {
            cached = (-1, new List<AircraftDefinition>());
        }

        if (cached.Frame == Time.frameCount)
        {
            return cached.List;
        }

        List<AircraftDefinition> roster = cached.List;
        roster.Clear();
        foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> entry in hq.AircraftSupply)
        {
            AddToRoster(roster, entry.Key);
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled)
            {
                foreach (AircraftDefinition definition in airbase.GetAvailableAircraft())
                {
                    AddToRoster(roster, definition);
                }
            }
        }

        AirRosters[hq] = (Time.frameCount, roster);
        return roster;
    }

    private static void AddToRoster(List<AircraftDefinition> roster, AircraftDefinition? definition)
    {
        if (definition != null
            && definition.unitPrefab != null
            && definition.aircraftParameters != null
            && !CommanderUnitBanService.IsBanned(definition)
            && !roster.Contains(definition))
        {
            roster.Add(definition);
        }
    }

    private static AirRole ChooseAirRole(FactionHQ hq, in ForceRead playerForce)
    {
        // CHALLENGING takes the empty bases from the air first: a helicopter landed on one has it in
        // two minutes, where a squad driving there takes twenty and may not arrive.
        if (IsChallenging(hq) && AnyNeutralBase() && !HasCaptureLander(hq) && CountRotary(hq) < RotaryLimit + 1)
        {
            return AirRole.Transport;
        }

        // Match the player's air force one for one, except when the player's home is a ship: then the
        // match is won by sinking it and fighters only escort the strike wave.
        int fighters = CountRole(hq, AirRole.Fighter);
        int strike = CountRole(hq, AirRole.Strike);
        int wantFighters = Mathf.Max(
            MinFighters,
            (strike + 1) / 2,
            playerForce.HomeAfloat ? 0 : Mathf.Min(playerForce.Aircraft, MaxFightersForPlayerAir));

        // Past the first two fighters, one strike airframe for every two fighters comes ahead of any
        // more of them, and a gunship only while there are no more gunships than strikers, so a poor
        // air fund replacing lost fighters still buys the strike airframes a wave needs.
        if (fighters >= MinFighters && strike * 2 < fighters)
        {
            return AirRole.Strike;
        }

        if (fighters < wantFighters)
        {
            return AirRole.Fighter;
        }

        return CountRotary(hq) < (IsChallenging(hq) ? ChallengingGunships : RotaryLimit)
            && CountRotary(hq) <= strike
            && CountGroundVehicles(hq) > 0
            ? AirRole.Transport
            : AirRole.Strike;
    }

    /// <summary>Rotary airframes this faction has in the world, whatever they are carrying.</summary>
    private static int CountRotary(FactionHQ hq)
    {
        int count = 0;
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (!CommanderAirCommandService.HasPlanePilot(aircraft))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether the commander already has a gunship riding with its ground units. The first
    /// rotary buy is that escort; only once it exists does the commander start running cargo.</summary>
    private static bool HasConvoyEscort(FactionHQ hq)
    {
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (!CommanderAirCommandService.HasPlanePilot(aircraft) && !IsCargoConfigured(aircraft))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ground vehicles this faction has on the road. No convoy means nothing to escort and
    /// nothing to reinforce, so no reason to own a helicopter at all.</summary>
    private static int CountGroundVehicles(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle && !unit.disabled)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Whether an aircraft was fitted out to carry things — a cargo pylon, loaded or already
    /// emptied.
    /// </summary>
    /// <remarks>
    /// Deliberately not the base game's own test, which is <c>station.Cargo &amp;&amp; Ammo &gt; 0</c>:
    /// that one is right for <c>AssessHQTargets</c>, which asks "is there something to deliver right
    /// now", and wrong here, where the question is "which of the two aircraft did the commander
    /// buy". A transport that has dropped its load answers no to the game's test, and would then be
    /// counted as the convoy's gunship and handed a CAS mission it has no weapons for. The fitting
    /// does not change for the airframe's whole life, so it is the stable answer. A transport,
    /// emptied or not, is left entirely to <c>AIHeloTransportState</c>, which flies the delivery and
    /// then takes itself home to be recovered.
    /// </remarks>
    private static bool IsCargoConfigured(Aircraft aircraft)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        if (stations == null)
        {
            return false;
        }

        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            if (station != null && station.Cargo)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every live aircraft on this faction's books.</summary>
    private static IEnumerable<Aircraft> EnumerateAircraft(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            yield break;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft aircraft && !unit.disabled && !CommanderFreeAiPilots.IsFree(aircraft))
            {
                yield return aircraft;
            }
        }
    }

    /// <summary>
    /// Picks what an airframe leaves the strip carrying, instead of taking the dice.
    /// </summary>
    /// <remarks>
    /// <paramref name="wantCargo"/> is only meaningful for rotary airframes, and it decides which of
    /// two completely different aircraft the commander is buying, because the base game branches on
    /// the loadout and on nothing else: a loaded cargo station sends the pilot to
    /// <c>AIHeloTransportState</c> and a real delivery, no cargo station leaves it in
    /// <c>AIHeloCombatState</c> as a gunship. So a gunship buy must refuse *any* loadout carrying
    /// cargo, not merely prefer one without — one loaded pylon and the escort turns itself into a
    /// transport mid-mission. Fixed-wing keeps the random pick; its loadouts are all fighting ones.
    /// </remarks>
    private static StandardLoadout? PickStandardLoadout(AircraftDefinition definition, FactionHQ hq, bool? wantCargo)
    {
        StandardLoadout[]? loadouts = definition.aircraftParameters.StandardLoadouts;
        if (wantCargo == null || loadouts == null || loadouts.Length == 0)
        {
            return definition.aircraftParameters.GetRandomStandardLoadout(definition, hq);
        }

        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        StandardLoadout? best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < loadouts.Length; i++)
        {
            StandardLoadout? candidate = loadouts[i];
            List<WeaponMount>? mounts = candidate?.loadout?.weapons;
            if (candidate == null || candidate.disabled || mounts == null)
            {
                continue;
            }

            if (prefab?.weaponManager != null && !candidate.AllowedByHQ(prefab.weaponManager, hq))
            {
                continue;
            }

            CountLoadoutMounts(mounts, out int cargo, out int guns);

            // Neither preference tolerates the other's pylons: a gunship with one cargo mount is a
            // transport, and a transport with no cargo mount has nothing to deliver.
            if (wantCargo.Value ? cargo == 0 : cargo > 0)
            {
                continue;
            }

            int score = wantCargo.Value ? (cargo * 10) + guns : guns;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        // An airframe with no loadout of the wanted kind takes the dice rather than the
        // commander picking a different airframe. Only worth building if the roster log starts
        // reporting "WRONG KIND" — with two rotary hulls on the list, that line is the cheaper test.
        return best ?? definition.aircraftParameters.GetRandomStandardLoadout(definition, hq);
    }

    /// <summary>
    /// An allowed standard loadout that scores well for the job: rolled, weighted by score, among
    /// those within <see cref="LoadoutRollShare"/> of the best, for variety.
    /// </summary>
    private static StandardLoadout? PickRoleLoadout(
        AircraftDefinition definition, FactionHQ hq, System.Func<WeaponMount, float> scoreStore)
    {
        StandardLoadout[]? loadouts = definition.aircraftParameters.StandardLoadouts;
        WeaponManager? manager = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager
            : null;
        int count = loadouts?.Length ?? 0;
        float[] scores = new float[count];
        float bestScore = 0f;
        for (int i = 0; i < count; i++)
        {
            StandardLoadout? candidate = loadouts![i];
            List<WeaponMount>? mounts = candidate?.loadout?.weapons;
            if (candidate == null
                || candidate.disabled
                || mounts == null
                || (manager != null && !candidate.AllowedByHQ(manager, hq)))
            {
                continue;
            }

            for (int m = 0; m < mounts.Count; m++)
            {
                WeaponMount? mount = mounts[m];
                scores[i] += mount?.info != null ? ScoreMount(mount, scoreStore) : 0f;
            }

            bestScore = Mathf.Max(bestScore, scores[i]);
        }

        if (bestScore <= 0f)
        {
            return null;
        }

        float total = 0f;
        for (int i = 0; i < count; i++)
        {
            if (scores[i] < bestScore * LoadoutRollShare)
            {
                scores[i] = 0f;
            }

            total += scores[i];
        }

        float roll = Random.value * total;
        StandardLoadout? picked = null;
        for (int i = 0; i < count; i++)
        {
            if (scores[i] > 0f)
            {
                picked = loadouts![i];
                roll -= scores[i];
                if (roll <= 0f)
                {
                    break;
                }
            }
        }

        return picked;
    }

    /// <summary>A bombing sortie: bombs are the point, anything else rides along.</summary>
    private static float ScoreBombingStore(WeaponMount mount)
    {
        float score = ScoreStrikeStore(mount);
        return CommanderAirCommandService.IsBombStore(mount.info, glideOnly: false) ? score : score * 0.3f;
    }

    /// <summary>A role's score for a store, except that a scouting store (the Eyeball Mk.II, which
    /// reads like an AGM-48 and does no damage) is worth next to nothing, so it only goes on a pylon
    /// nothing else fits and a strike airframe always carries a full load of weapons.</summary>
    private static float ScoreMount(WeaponMount mount, System.Func<WeaponMount, float> scoreStore)
    {
        float score = scoreStore(mount);
        return CommanderFireNow.IsSensorStore(mount.info) ? Mathf.Min(score, 0.01f) : score;
    }

    /// <summary>A close-support sortie against vehicles: guided missiles first, then rockets and
    /// guns, not bombs. A free-fall bomb has to be dropped from overhead, through the air defence the
    /// convoy brought.</summary>
    private static float ScoreCasStore(WeaponMount mount)
    {
        WeaponInfo info = mount.info;
        float score = ScoreStrikeStore(mount);
        if (CommanderAirCommandService.IsBombStore(info, glideOnly: false))
        {
            return score * 0.3f;
        }

        return info.missile && !info.boresight && !info.gun ? score * CasGuidedMissileWeight : score;
    }

    /// <summary>
    /// A close-support loadout built pylon by pylon (<see cref="ScoreCasStore"/>, so AGMs where a
    /// pylon takes them), with the one hardpoint set that gives up the least ground punch carrying the
    /// best air-to-air store instead, a pair of IRM-S1/S2 on a Cricket. Null when the airframe offers
    /// nothing for the job here.
    /// </summary>
    private static Loadout? BuildCasLoadout(AircraftDefinition definition, FactionHQ hq, Airbase airbase)
    {
        Loadout? loadout = BuildRoleLoadout(definition, hq, airbase, ScoreCasStore);
        HardpointSet[]? sets = definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        if (loadout == null || sets == null || !HasAnyStore(loadout))
        {
            return null;
        }

        int swapAt = -1;
        WeaponMount? swapIn = null;
        float leastLost = float.MaxValue;
        for (int i = 0; i < sets.Length; i++)
        {
            WeaponMount? current = loadout.weapons[i];
            if (current != null && IsAirOnlyStore(current))
            {
                return loadout;
            }

            WeaponChecker.GetAvailableWeaponsNonAlloc(null!, sets[i], airbase, hq, allowEmpty: false, LegalMounts);
            WeaponMount? best = null;
            float bestScore = 0f;
            for (int m = 0; m < LegalMounts.Count; m++)
            {
                WeaponMount mount = LegalMounts[m];
                float score = mount?.info == null ? 0f : ScoreFighterStore(mount);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = mount;
                }
            }

            float lost = current?.info != null ? ScoreCasStore(current) : 0f;
            if (best != null && lost < leastLost)
            {
                leastLost = lost;
                swapAt = i;
                swapIn = best;
            }
        }

        if (swapAt >= 0)
        {
            WeaponMount previous = loadout.weapons[swapAt];
            loadout.weapons[swapAt] = swapIn!;
            if (sets[swapAt].BlockedByOtherHardpoint(loadout))
            {
                loadout.weapons[swapAt] = previous;
            }
        }

        return loadout;
    }

    /// <summary>
    /// A loadout built pylon by pylon for an airframe with no usable standard loadout: on each
    /// hardpoint set, the store the airbase may issue that scores best for the job, else empty.
    /// Without it the launch passed no loadout at all, and <c>Hangar.SpawnAircraft</c> falls back on
    /// <c>SelectAIAircraftWeapons</c>, a random store per pylon: that is every Ifrit that went up on
    /// AIR SUPERIORITY with PAB bombs, a GBM and an AShM-300 aboard, whatever the strip said.
    /// </summary>
    private static Loadout? BuildRoleLoadout(
        AircraftDefinition definition, FactionHQ hq, Airbase airbase, System.Func<WeaponMount, float> scoreStore)
    {
        HardpointSet[]? sets = definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        if (sets == null)
        {
            return null;
        }

        Loadout loadout = new();
        for (int i = 0; i < sets.Length; i++)
        {
            loadout.weapons.Add(null!);
        }

        for (int i = 0; i < sets.Length; i++)
        {
            WeaponChecker.GetAvailableWeaponsNonAlloc(null!, sets[i], airbase, hq, allowEmpty: false, LegalMounts);
            WeaponMount? best = null;
            float bestScore = 0f;
            for (int m = 0; m < LegalMounts.Count; m++)
            {
                WeaponMount mount = LegalMounts[m];
                float score = mount?.info == null || mount.info.nuclear ? 0f : ScoreMount(mount, scoreStore);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = mount;
                }
            }

            if (best == null)
            {
                continue;
            }

            loadout.weapons[i] = best;
            if (sets[i].BlockedByOtherHardpoint(loadout))
            {
                loadout.weapons[i] = null!;
            }
        }

        return loadout;
    }

    private static bool HasAnyStore(Loadout? loadout)
    {
        for (int i = 0; loadout?.weapons != null && i < loadout.weapons.Count; i++)
        {
            if (loadout.weapons[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A copy of the loadout with every pylon that cannot fight aircraft left empty.</summary>
    private static Loadout? StripSurfaceStores(Loadout? source)
    {
        if (source?.weapons == null)
        {
            return source;
        }

        Loadout stripped = new();
        for (int i = 0; i < source.weapons.Count; i++)
        {
            WeaponMount? mount = source.weapons[i];
            bool keep = mount == null
                || IsAirOnlyStore(mount)
                || mount.radar
                || IsJammerMount(mount)
                || mount.info?.gun == true;
            stripped.weapons.Add(keep ? mount! : null!);
        }

        return stripped;
    }

    /// <summary>
    /// An air-to-air store and nothing else. Stricter than the AIR window's test on purpose: the
    /// first strip kept PAB, GPO-500 and Tusko-B on Revokers, so a bomb never counts here whatever
    /// its authored anti-air figure says, and a store has to be better against aircraft than ground.
    /// </summary>
    private static bool IsAirOnlyStore(WeaponMount mount)
    {
        return mount.info != null && IsAirOnlyInfo(mount.info);
    }

    /// <summary><see cref="IsAirOnlyStore"/> for a store already on a pylon.</summary>
    private static bool IsAirOnlyInfo(WeaponInfo info)
    {
        return !info.jammer
            && !info.nuclear
            && !info.gun
            && info.effectiveness.antiAir > 0.05f
            && !info.bomb
            && !info.glideBomb
            && info.effectiveness.antiAir >= info.effectiveness.antiSurface;
    }

    private static float ScoreFighterStore(WeaponMount mount)
    {
        return IsAirOnlyStore(mount) ? mount.info.effectiveness.antiAir : 0f;
    }

    /// <summary>What a store adds to a sortie against a base: ground punch a CAS mission fires (bombs
    /// count double, they are what kills buildings), a little for a jammer that keeps it alive, and a
    /// little for self-defence missiles. An ARAD only hits radars, so it scores nothing.</summary>
    private static float ScoreStrikeStore(WeaponMount mount)
    {
        WeaponInfo info = mount.info;
        float score = 0f;
        if (!CommanderAirCommandService.IsAradWeapon(info)
            && CommanderAirCommandService.IsWeaponEligible(info, CommanderAirCommandService.AirCommandMode.Cas))
        {
            score += info.effectiveness.antiSurface * (info.bomb || info.glideBomb ? 2f : 1f);
        }

        if (IsJammerMount(mount))
        {
            score += 0.5f;
        }

        if (IsAirOnlyStore(mount))
        {
            score += info.effectiveness.antiAir * 0.25f;
        }

        return score;
    }

    /// <summary>
    /// What a store adds to a sortie against a fleet: nothing unless it pierces <paramref name="shipArmour"/>,
    /// the base game's own gate (<c>CombatAI.AnalyzeTarget</c> never fires a store below the target's
    /// armour tier, so rockets, cannon and light bombs are dead weight against a warship). Reach
    /// counts double at 20 km: a ship is a moving SAM site, and a store fired from outside its
    /// umbrella arrives with its carrier still flying.
    /// </summary>
    private static float ScoreAntiShipStore(WeaponMount mount, float shipArmour)
    {
        WeaponInfo info = mount.info;
        return IsAntiShipInfo(info, shipArmour)
            ? info.effectiveness.antiSurface * (1f + Mathf.Clamp01(info.targetRequirements.maxRange / AntiShipStandoffMeters))
            : 0f;
    }

    private static bool IsAntiShipInfo(WeaponInfo? info, float shipArmour)
    {
        return info != null
            && !info.nuclear
            && !info.jammer
            && !info.cargo
            && !info.troops
            && info.effectiveness.antiSurface > 0.05f
            && info.armorTierEffectiveness >= shipArmour
            && !CommanderAirCommandService.IsAradWeapon(info);
    }

    /// <summary>
    /// The longest reach of any store this airframe can carry that pierces <paramref name="shipArmour"/>,
    /// from the prefab's hardpoint options; below zero when none does. Reach keeps strike aircraft
    /// outside a warship's missile envelope.
    /// </summary>
    private static float GetAntiShipReach(AircraftDefinition definition, float shipArmour)
    {
        if (!AntiShipStores.TryGetValue(definition, out List<WeaponInfo> stores))
        {
            stores = new List<WeaponInfo>();
            HardpointSet[]? sets = definition.unitPrefab != null
                ? definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets
                : null;
            for (int i = 0; sets != null && i < sets.Length; i++)
            {
                List<WeaponMount>? options = sets[i].weaponOptions;
                for (int m = 0; options != null && m < options.Count; m++)
                {
                    WeaponInfo? info = options[m]?.info;
                    if (IsAntiShipInfo(info, 0f) && !stores.Contains(info!))
                    {
                        stores.Add(info!);
                    }
                }
            }

            AntiShipStores[definition] = stores;
        }

        float reach = -1f;
        for (int i = 0; i < stores.Count; i++)
        {
            if (stores[i].armorTierEffectiveness >= shipArmour)
            {
                reach = Mathf.Max(reach, stores[i].targetRequirements.maxRange);
            }
        }

        return reach;
    }

    /// <summary>
    /// Narrows this buy's candidates to the airframes that can carry something that hurts the
    /// fleet, when any can; <paramref name="standoff"/> (CHALLENGING) narrows them again to the ones
    /// that hurt it from furthest away. Returns the price reference the band is measured from,
    /// recomputed over what is left.
    /// </summary>
    private static float KeepAntiShipCandidates(
        List<(AircraftDefinition Definition, Airbase Field)> airCandidates, float shipArmour, bool standoff, bool escalate, float reference)
    {
        float best = -1f;
        for (int i = 0; i < airCandidates.Count; i++)
        {
            best = Mathf.Max(best, GetAntiShipReach(airCandidates[i].Definition, shipArmour));
        }

        if (best < 0f)
        {
            return reference;
        }

        float floor = standoff ? best * AntiShipReachShare : 0f;
        airCandidates.RemoveAll(candidate => GetAntiShipReach(candidate.Definition, shipArmour) < floor);
        reference = escalate ? 0f : float.MaxValue;
        for (int i = 0; i < airCandidates.Count; i++)
        {
            float value = CommanderEconomyService.PriceOf(airCandidates[i].Definition);
            reference = escalate ? Mathf.Max(reference, value) : Mathf.Min(reference, value);
        }

        return reference;
    }

    /// <summary>
    /// The player's ship this airframe should go after, or null when it carries nothing that pierces
    /// any of them. CHALLENGING goes for the dearest hull it can hurt, which on Carrier Duel is the
    /// carrier that decides the match; NORMAL takes the nearest one. Positions are the last known
    /// ones (<c>TryGetKnownPosition</c>), never the live hull.
    /// </summary>
    private static Unit? PickShipTarget(FactionHQ hq, FactionHQ localHq, Aircraft aircraft, out GlobalPosition at)
    {
        at = default;
        if (localHq.factionUnits == null)
        {
            return null;
        }

        bool dearest = IsChallenging(hq);
        GlobalPosition from = aircraft.GlobalPosition();
        Unit? best = null;
        float bestScore = float.MinValue;
        foreach (PersistentID id in localHq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not Ship
                || unit.disabled
                || !hq.TryGetKnownPosition(unit, out GlobalPosition known)
                || !CanAnyStoreDefeat(aircraft, unit))
            {
                continue;
            }

            float score = dearest ? unit.definition.value : -FastMath.Distance(from, known);
            if (score > bestScore)
            {
                best = unit;
                bestScore = score;
                at = known;
            }
        }

        return best;
    }

    /// <summary>Whether a CAS mission will fire something aboard at this ship. A store CAS refuses
    /// (a cruise missile counts as strategic) is only fired on HYBRID, which has no weapon filter.</summary>
    private static bool HasCasStoreAgainst(Aircraft aircraft, Unit target)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station != null
                && !station.Cargo
                && station.Ammo > 0
                && CommanderAirCommandService.CanDefeatArmor(station.WeaponInfo, target)
                && CommanderAirCommandService.IsWeaponEligible(station.WeaponInfo, CommanderAirCommandService.AirCommandMode.Cas))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits a loadout into the two things that matter here: pylons that carry units, and
    /// pylons that shoot at the ground.</summary>
    private static void CountLoadoutMounts(List<WeaponMount> mounts, out int cargo, out int guns)
    {
        cargo = 0;
        guns = 0;
        for (int i = 0; i < mounts.Count; i++)
        {
            WeaponMount? mount = mounts[i];
            if (mount == null)
            {
                continue;
            }

            if (mount.Cargo || mount.Troops || mount.info?.cargo == true)
            {
                cargo++;
            }
            else if (mount.info != null && mount.info.effectiveness.antiSurface > 0.05f)
            {
                guns++;
            }
        }
    }

    /// <summary>How many airframes of one role this faction already has in the world.</summary>
    private static int CountRole(FactionHQ hq, AirRole role)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is Aircraft aircraft
                && !unit.disabled
                && !CommanderFreeAiPilots.IsFree(aircraft)
                && !IsRadarPicket(aircraft.definition, hq)
                && GetAirRole(aircraft) == role)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The radar picket is the airframe whose own radar outranges every other one on the roster by
    /// half again — the Medusa's dome. Read off the prefab's <c>Radar</c> components, not its name.
    /// The first attempt keyed on jamming pods in a standard loadout and picked the Alkyon AB-4,
    /// whose "Tuskos" fit carries one: the enemy bought three 390 bombers to circle and jam with eight
    /// bombs each still aboard, while the Medusa (no pod in any standard loadout) flew a fighter's
    /// mission straight at the player's base.
    /// </summary>
    private static bool IsRadarPicket(AircraftDefinition definition, FactionHQ hq)
    {
        float own = GetRadarRange(definition);
        if (own <= 0f)
        {
            return false;
        }

        // Four of Primeva's airframes report the same 50 km, so "outranges every other one by half
        // again" was true of none of them and the picket was never once bought in a whole match.
        // A faction that fields an EW airframe pickets with that; the radar rule is the fallback
        // for one that does not.
        bool rosterHasEw = false;
        foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
        {
            if (rosterEntry != null && IsElectronicWarfare(rosterEntry))
            {
                rosterHasEw = true;
                break;
            }
        }

        if (rosterHasEw)
        {
            return IsElectronicWarfare(definition);
        }

        foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
        {
            if (rosterEntry != null && rosterEntry != definition && GetRadarRange(rosterEntry) * PicketRadarMargin > own)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The roster's electronic warfare airframe, by the <c>jsonKey</c> the game files use
    /// for it (<c>EW1</c> on the stock factions). Role and loadout both say "fighter" for it.</summary>
    private static bool IsElectronicWarfare(AircraftDefinition definition)
    {
        string key = definition.jsonKey ?? string.Empty;
        return key.StartsWith("EW", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The longest radar on an airframe's prefab, in metres; 0 when it has none.</summary>
    private static float GetRadarRange(AircraftDefinition definition)
    {
        if (RadarRanges.TryGetValue(definition, out float range))
        {
            return range;
        }

        range = 0f;
        if (definition.unitPrefab != null)
        {
            Radar[] radars = definition.unitPrefab.GetComponentsInChildren<Radar>(true);
            for (int i = 0; i < radars.Length; i++)
            {
                range = Mathf.Max(range, radars[i].RadarParameters.maxRange);
            }
        }

        RadarRanges[definition] = range;
        return range;
    }

    /// <summary>
    /// The picket's fit: its standard loadout with the dome, then ARADs, then a jamming pod on every
    /// hardpoint that offers one, so the dome watches and its RADAR mission shoots back at the
    /// emitters it finds. The Medusa's standard loadouts carry neither.
    /// </summary>
    private static Loadout? BuildPicketLoadout(AircraftDefinition definition, FactionHQ hq, out int pods)
    {
        StandardLoadout? standard = PickStandardLoadout(definition, hq, null);
        pods = 0;
        HardpointSet[]? sets = definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        List<WeaponMount>? standardMounts = standard?.loadout?.weapons;
        if (sets == null)
        {
            return standard?.loadout;
        }

        Loadout loadout = new();
        for (int i = 0; i < sets.Length; i++)
        {
            loadout.weapons.Add(standardMounts != null && i < standardMounts.Count ? standardMounts[i] : null!);
        }

        for (int i = 0; i < sets.Length; i++)
        {
            // The dome where the airframe offers one: a radar picket should be carrying its radar.
            // ARADs on the rest, so its RADAR mission can fire at the emitters the dome finds, and
            // a jammer only where no ARAD fits.
            WeaponMount? jammer = sets[i].weaponOptions?.Find(static mount => mount != null && mount.radar)
                ?? sets[i].weaponOptions?.Find(static mount => mount?.info != null && CommanderAirCommandService.IsAradWeapon(mount.info))
                ?? sets[i].weaponOptions?.Find(IsJammerMount);
            if (jammer == null)
            {
                continue;
            }

            // A pylon that another loaded pylon rules out stays as it was.
            WeaponMount previous = loadout.weapons[i];
            loadout.weapons[i] = jammer;
            if (sets[i].BlockedByOtherHardpoint(loadout))
            {
                loadout.weapons[i] = previous;
                continue;
            }

            pods++;
        }

        return loadout;
    }

    private static bool IsJammerMount(WeaponMount? mount)
    {
        return mount != null
            && (mount.info?.jammer == true || mount.prefab?.GetComponentInChildren<JammingPod>(true) != null);
    }

    private static int CountPickets(FactionHQ hq)
    {
        int count = 0;
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (IsRadarPicket(aircraft.definition, hq))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Fields this faction holds whose hangars list the airframe. Hangar lists are asset
    /// data, so this is the only way to learn why an airframe (the Darkreach) is never launched.</summary>
    private static int CountLaunchFields(FactionHQ hq, AircraftDefinition definition)
    {
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The pilot types on an airframe's prefab, which is what decides its AI flight model.</summary>
    private static string DescribePilotTypes(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null || prefab.pilots.Length == 0)
        {
            return "none";
        }

        string types = string.Empty;
        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            Pilot? pilot = prefab.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            string name = pilot.pilotType.ToString();
            if (types.IndexOf(name, System.StringComparison.Ordinal) < 0)
            {
                types = types.Length == 0 ? name : types + "+" + name;
            }
        }

        return types.Length == 0 ? "none" : types;
    }

    /// <summary>
    /// Price of the dearest airframe any field this commander holds would actually accept. Used as
    /// the air fund's save-up target: a strip that takes nothing heavy keeps the target low, and
    /// capturing a real airfield raises it on its own. With <paramref name="dearest"/> false, the
    /// cheapest instead (0 if none), which is what sizes a strike wave the fund can reach.
    /// </summary>
    private static float GetSpawnableAirframeValue(FactionHQ hq, bool dearest)
    {
        float best = dearest ? 0f : float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
            {
                AircraftDefinition? definition = rosterEntry;

                // The Cricket is only a fallback buy (DropCrickets), so it is not what the fund buys.
                if (definition == null || (!dearest && CommanderEconomyService.IsCricket(definition)))
                {
                    continue;
                }

                float price = CommanderEconomyService.PriceOf(definition);
                if ((dearest ? price > best : price < best) && CanLaunchFrom(airbase, hq, definition))
                {
                    best = price;
                }
            }
        }

        return best == float.MaxValue ? 0f : best;
    }

    /// <summary><see cref="CanRollOffFrom"/> without the busy deck, which is only this minute.</summary>
    private static bool CanAnyFieldLaunch(FactionHQ hq, AircraftDefinition definition)
    {
        if (!CommanderAirCommandService.CanAiFly(definition))
        {
            return false;
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null
                && !airbase.disabled
                && CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition)
                && CommanderAirCommandService.HasTakeoffRunway(airbase, definition)
                && airbase.CanSpawnAircraft(definition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a launch of this type takes out of the air fund: nothing for the airframe when
    /// one is in stock (a factory's, or one that came home), its price otherwise.</summary>
    private static float GetLaunchPrice(FactionHQ hq, AircraftDefinition definition)
    {
        return hq.GetUnitSupply(definition) > 0 ? 0f : CommanderEconomyService.PriceOf(definition);
    }

    /// <summary>Any airframe in this faction's stock, which launches without an air fund.</summary>
    private static bool HasAirStock(FactionHQ hq)
    {
        return CountAirStock(hq) > 0;
    }

    /// <summary>Airframes waiting in this faction's stock, from its factories or flown home.</summary>
    private static int CountAirStock(FactionHQ hq)
    {
        int count = 0;
        foreach (AircraftDefinition definition in GetAirRoster(hq))
        {
            if (definition != null)
            {
                count += Mathf.Max(0, hq.GetUnitSupply(definition));
            }
        }

        return count;
    }

    /// <summary>In a draft only what was drafted flies, and it flies free: anything else is not for sale.</summary>
    private static bool IsInDraftStock(FactionHQ hq, AircraftDefinition definition)
    {
        return !CommanderDraftService.IsDraftMission || hq.GetUnitSupply(definition) > 0;
    }

    /// <summary>
    /// The one rule every enemy airframe pick goes through: the player's AIR window rule
    /// (<c>IsCompatibleAirbase</c>), a runway long enough and a free hangar. Nothing launches
    /// airborne any more, so a field where this type has already failed its takeoff roll (Ifrits at
    /// the naval base: new, full of fuel, stuck on the ground) is passed over while another field
    /// can launch it. It is not banned outright: a duel has one field per side, and a ban there
    /// emptied the roster down to helicopters.
    /// </summary>
    private static bool CanLaunchFrom(Airbase airbase, FactionHQ hq, AircraftDefinition definition)
    {
        return CanRollOffFrom(airbase, hq, definition)
            && (!CommanderAirCommandService.HasFailedTakeoff(airbase, definition)
                || !HasCleanLaunchField(hq, definition));
    }

    private static bool CanRollOffFrom(Airbase airbase, FactionHQ hq, AircraftDefinition definition)
    {
        return CommanderAirCommandService.CanAiFly(definition)
            && !CommanderAirCommandService.IsDeckBusy(airbase)
            && CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition)
            && CommanderAirCommandService.HasTakeoffRunway(airbase, definition)
            && airbase.CanSpawnAircraft(definition);
    }

    /// <summary>Some field this faction holds launches the type and has never seen it fail there.</summary>
    private static bool HasCleanLaunchField(FactionHQ hq, AircraftDefinition definition)
    {
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null
                && !CommanderAirCommandService.HasFailedTakeoff(airbase, definition)
                && CanRollOffFrom(airbase, hq, definition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asserts the role rule still reads the way the buy loop assumes. It is two comparisons on
    /// asset data, so a game patch that retunes an airframe's role identity, or an edit that flips
    /// the comparison, silently turns the mixed wing back into a monoculture with nothing to notice
    /// it. Run once at plugin load beside the plan self-check.
    /// </summary>
    /// <summary>
    /// Asserts the cargo/gunship split still reads the way the buy loop assumes. This is the pivot
    /// of the whole rotary wing — a loadout counted as cargo becomes a delivery flight and one
    /// counted as clean becomes the convoy escort — and it is two field reads on asset data, so a
    /// game patch that moves a troop bay onto a different flag, or an edit that flips the
    /// comparison, would silently buy two of the same aircraft with nothing to notice it.
    /// </summary>
    private static void CheckLoadoutCounting()
    {
        WeaponMount troopBay = ScriptableObject.CreateInstance<WeaponMount>();
        troopBay.Troops = true;

        WeaponMount rocketPod = ScriptableObject.CreateInstance<WeaponMount>();
        rocketPod.info = ScriptableObject.CreateInstance<WeaponInfo>();
        rocketPod.info.effectiveness.antiSurface = 0.8f;

        WeaponMount empty = ScriptableObject.CreateInstance<WeaponMount>();

        CheckLoadoutCount("gunship", new List<WeaponMount> { rocketPod, rocketPod, empty }, 0, 2);
        CheckLoadoutCount("transport", new List<WeaponMount> { troopBay, rocketPod }, 1, 1);
        CheckLoadoutCount("empty", new List<WeaponMount> { empty, null! }, 0, 0);

        Object.Destroy(rocketPod.info);
        Object.Destroy(troopBay);
        Object.Destroy(rocketPod);
        Object.Destroy(empty);
    }

    private static void CheckLoadoutCount(string name, List<WeaponMount> mounts, int expectedCargo, int expectedGuns)
    {
        CountLoadoutMounts(mounts, out int cargo, out int guns);
        if (cargo != expectedCargo || guns != expectedGuns)
        {
            CommanderPlugin.Log.LogError(
                $"Rotary loadout self-check FAILED ({name}): expected {expectedCargo} cargo / "
                    + $"{expectedGuns} gun, got {cargo} / {guns}.");
        }
    }

    private static void CheckAirRoles()
    {
        CheckLoadoutCounting();
        CheckInterceptorWeights();
        CheckAirRole("interceptor", antiAir: 0.9f, antiSurface: 0.1f, captureCapacity: 0, AirRole.Fighter);
        CheckAirRole("attack jet", antiAir: 0.1f, antiSurface: 0.9f, captureCapacity: 0, AirRole.Strike);
        CheckAirRole("transport", antiAir: 0f, antiSurface: 0f, captureCapacity: 8, AirRole.Transport);
    }

    private static void CheckAirRole(
        string name, float antiAir, float antiSurface, int captureCapacity, AirRole expected)
    {
        // No prefab, so HasPlanePilot is false and the transport branch is reachable, which is the
        // branch worth checking: a plane that carries troops must still count as what it shoots at.
        AircraftDefinition definition = ScriptableObject.CreateInstance<AircraftDefinition>();
        definition.roleIdentity.antiAir = antiAir;
        definition.roleIdentity.antiSurface = antiSurface;
        definition.captureCapacity = captureCapacity;

        AirRole actual = GetAirRole(definition);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy air role self-check FAILED ({name}): expected {expected}, got {actual}.");
        }

        Object.Destroy(definition);
    }

    /// <summary>Says why no aircraft was bought, but only when the reason changes — a review runs
    /// every 30 s and the same line every time is noise nobody reads.</summary>
    private static void ReportAirDenial(FactionHQ hq, CommanderState state, string reason)
    {
        if (state.LastAirDenial == reason)
        {
            return;
        }

        state.LastAirDenial = reason;
        CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) bought no aircraft: {reason}.{DescribeClosedHangars(hq)}");
    }

    /// <summary>
    /// Every closed hangar the faction holds and what is keeping it shut. A hangar reopens only once
    /// the last aircraft it spawned is clear of it (<c>Hangar.WaitForUnitToLeave</c>), so one that
    /// stalls on a carrier's elevator ends that carrier's launches for good.
    /// </summary>
    private static string DescribeClosedHangars(FactionHQ hq)
    {
        StringBuilder text = new();
        foreach (Airbase airbase in hq.GetAirbases())
        {
            for (int i = 0; airbase != null && i < airbase.hangars.Count; i++)
            {
                Hangar hangar = airbase.hangars[i];
                if (hangar == null || hangar.Available)
                {
                    continue;
                }

                GameObject spawned = HangarSpawned(hangar);
                Unit? unit = spawned != null ? spawned.GetComponent<Unit>() : null;
                text.Append($" {airbase.name}/{hangar.name} closed{(hangar.Disabled ? " (disabled)" : string.Empty)}, last spawn ")
                    .Append(spawned == null
                        ? "gone"
                        : $"{(unit != null ? CommanderGameAccess.GetUnitLabel(unit) : spawned.name)}{(unit != null && unit.disabled ? " (wreck)" : string.Empty)} "
                            + $"{Vector3.Distance(spawned.transform.position, hangar.transform.position):0} m away, {(unit != null ? unit.speed : 0f):0} m/s"
                            + (unit is Aircraft { pilots.Length: > 0 } parked
                                ? $", {parked.pilots[0].currentState?.GetType().Name ?? "no pilot state"}{(parked.Player != null ? ", player" : string.Empty)}"
                                : string.Empty))
                    .Append(';');
            }

            for (int r = 0; airbase?.runways != null && r < airbase.runways.Length; r++)
            {
                Airbase.Runway runway = airbase.runways[r];
                Queue<Aircraft>? queue = runway != null ? DiagTakeoffQueue(runway) : null;
                Aircraft? head = queue != null && queue.Count > 0 ? queue.Peek() : null;
                if (runway != null && (runway.GetLandingList().Count > 0 || head != null))
                {
                    text.Append($" {airbase!.name} runway {r}: {runway.GetLandingList().Count} landing")
                        .Append(head != null
                            ? $", takeoff queue {queue!.Count} headed by {(head == null ? "null" : CommanderGameAccess.GetUnitLabel(head))}"
                                + $" ({(head != null && head.pilots?.Length > 0 ? head.pilots[0].currentState?.GetType().Name : "?")}, {(head != null && head.disabled ? "wreck" : "live")})"
                            : string.Empty)
                        .Append(';');
                }
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Cruise height from the situation, never a fixed number per type: above any known air defence
    /// near the aircraft (and, for bomb carriers, near the target); a radar picket as high as its radar
    /// needs; otherwise 0, which leaves it to the base game. <c>GovernClimb</c> decides how much of it
    /// a given airframe reaches.
    /// </summary>
    private static float GetCruiseHeight(FactionHQ hq, Aircraft aircraft)
    {
        // Bombers go in at 10 km and up to stay above guns; glide bombers as high as they can, since
        // their bombs reach further from higher up.
        bool glide = CommanderAirCommandService.HasBombAmmo(aircraft, glideOnly: true);
        bool bomber = glide || CommanderAirCommandService.HasBombAmmo(aircraft, glideOnly: false);
        float height = glide ? CommanderAirCommandService.HighestAiHeightMeters : bomber ? BomberCruiseMeters : 0f;
        GlobalPosition areaCentre = default;
        bool watchArea = bomber
            && CommanderAirCommandService.Instance?.TryGetMissionAreaCenter(aircraft, out areaCentre) == true;
        if (IsRadarPicket(aircraft.definition, hq))
        {
            // Radar horizon: d (km) = 3.57 * sqrt(h (m)), so seeing a surface contact at full range
            // takes h = (d / 3.57)^2.
            float horizon = GetRadarRange(aircraft.definition) / 1000f / 3.57f;
            height = horizon * horizon;
        }

        GlobalPosition position = aircraft.GlobalPosition();
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (!info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || !IsAirDefenceThreat(unit)
                || (unit is not Building && now - info.lastSpottedTime > ThreatMemorySeconds))
            {
                continue;
            }

            float reach = unit.GetMaxRange();
            if (FastMath.InRange(info.lastKnownPosition, position, reach + AirDefenceMarginMeters)
                || (watchArea && FastMath.InRange(info.lastKnownPosition, areaCentre, reach + AirDefenceMarginMeters)))
            {
                height = Mathf.Max(height, reach);
            }
        }

        return Mathf.Min(height, CruiseCeilingMeters);
    }

    /// <summary>A launcher or gun vehicle, or a building or ship that carries anti-air weapons.</summary>
    private static bool IsAirDefenceThreat(Unit unit)
    {
        if (unit.definition is VehicleDefinition vehicle)
        {
            return IsAirDefence(vehicle);
        }

        return (unit is Building || unit is Ship)
            && unit.weaponStations.Count > 0
            && unit.definition != null
            && unit.definition.roleIdentity.antiAir > 0f;
    }

    /// <summary>
    /// Where the picket orbits: out from the player's base toward the base this commander holds
    /// furthest from it, <see cref="PicketMinStandoffMeters"/> to <see cref="PicketMaxStandoffMeters"/>
    /// out, then pushed to <see cref="PicketSafeMeters"/> from the nearest thing of the player's that
    /// can hurt it. Not the average of the commander's bases: in Champions those surround the player,
    /// so their average sat beside the player's own field.
    /// </summary>
    /// <remarks>Backs away from the single nearest threat, so it can back toward a second
    /// one. Weigh every threat in reach if a picket is seen doing that.</remarks>
    private static GlobalPosition GetPicketStation(FactionHQ hq, GlobalPosition playerBase, int index)
    {
        // The index-th furthest base, so each picket watches from its own bearing.
        PicketBases.Clear();
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                PicketBases.Add(airbase.center.GlobalPosition());
            }
        }

        PicketBases.Sort((a, b) => FastMath.Distance(b, playerBase).CompareTo(FastMath.Distance(a, playerBase)));
        GlobalPosition rear = PicketBases.Count > 0
            ? PicketBases[index % PicketBases.Count]
            : CommanderCaptureService.GetTerritoryCenter(hq);

        Vector3 away = rear - playerBase;
        away.y = 0f;
        if (away.sqrMagnitude < 1f)
        {
            away = Vector3.forward;
        }

        GlobalPosition station = playerBase
            + (away.normalized * Mathf.Clamp(away.magnitude, PicketMinStandoffMeters, PicketMaxStandoffMeters));
        if (TryGetNearestThreat(hq, station, PicketSafeMeters, out GlobalPosition threat))
        {
            Vector3 push = station - threat;
            push.y = 0f;
            station = threat + ((push.sqrMagnitude < 1f ? away.normalized : push.normalized) * PicketSafeMeters);
        }

        return station;
    }

    /// <summary>Plane-flown strike airframes this faction has up: not fighters, not pickets.</summary>
    private static int CountStrikeWing(FactionHQ hq, out int fighters)
    {
        int count = 0;
        fighters = 0;
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            // Radar hunters fly their own sorties and never wait for the wave.
            if (!CommanderAirCommandService.HasPlanePilot(aircraft)
                || IsRadarPicket(aircraft.definition, hq)
                || IsRadarHunter(aircraft, hq))
            {
                continue;
            }

            if (GetAirRole(aircraft) == AirRole.Fighter)
            {
                fighters++;
            }
            else
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Aircraft a strike wave waits for: the difficulty's own size, cut to what the wing can field.
    /// <paramref name="saveCap"/> over <paramref name="cheapest"/> is how many airframes the air fund
    /// buys in the reviews it saves over, and <paramref name="room"/> is what the airborne ceiling
    /// leaves the wing, so a poor fund does not wait for a wave it can never field. Never below two,
    /// so a wave is still more than one aircraft.
    /// </summary>
    private static int ReachableWaveSize(int wanted, float saveCap, float cheapest, int room)
    {
        int reach = Mathf.Min(wanted, room);
        if (saveCap > 0f && cheapest > 0f)
        {
            reach = Mathf.Min(reach, Mathf.FloorToInt(saveCap / cheapest));
        }

        return Mathf.Clamp(reach, MinStrikeWaveSize, wanted);
    }

    /// <summary>
    /// Where a wave turns in from, or null for straight in: a point part way along the run from the
    /// staging base, swung out to a random side. Every wave flying the same line from the same base
    /// met the player's air defence where it was the previous time.
    /// </summary>
    private static GlobalPosition? PickWaveFlank(GlobalPosition staging, GlobalPosition target, out string approach)
    {
        approach = "straight in";
        Vector3 run = target - staging;
        run.y = 0f;
        float length = run.magnitude;
        if (length < 1000f || Random.value < StraightWaveShare)
        {
            return null;
        }

        bool left = Random.value < 0.5f;
        approach = left ? "round the left flank" : "round the right flank";
        Vector3 along = run / length;
        Vector3 side = Vector3.Cross(Vector3.up, along) * (left ? -1f : 1f);
        float swing = Mathf.Clamp(length * Random.Range(MinFlankShare, MaxFlankShare), MinFlankMeters, MaxFlankMeters);
        GlobalPosition point = staging + (along * (length * Random.Range(0.45f, 0.7f))) + (side * swing);
        point.y = target.y;
        return point;
    }

    /// <summary>The base this faction holds nearest the player's, where a wave forms up.</summary>
    private static GlobalPosition? GetStagingBase(FactionHQ hq, GlobalPosition playerBase)
    {
        GlobalPosition? best = null;
        float nearest = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = FastMath.Distance(airbase.center.GlobalPosition(), playerBase);
            if (distance < nearest)
            {
                nearest = distance;
                best = airbase.center.GlobalPosition();
            }
        }

        return best;
    }

    /// <summary>
    /// The nearest thing of the player's inside <paramref name="radius"/> that can hurt an aircraft:
    /// anything mobile seen lately, or a building with weapons on it (an air-defence post).
    /// </summary>
    private static bool TryGetNearestThreat(FactionHQ hq, GlobalPosition point, float radius, out GlobalPosition threat)
    {
        threat = point;
        float nearest = radius;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (!info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            bool building = unit is Building;
            if ((building && unit.weaponStations.Count == 0)
                || (!building && now - info.lastSpottedTime > ThreatMemorySeconds))
            {
                continue;
            }

            float distance = FastMath.Distance(info.lastKnownPosition, point);
            if (distance < nearest)
            {
                nearest = distance;
                threat = info.lastKnownPosition;
            }
        }

        return nearest < radius;
    }

    /// <summary>
    /// The player's building this commander most wants bombed, of those it knows of inside the strike
    /// box that something aboard can actually hurt: an income building first, then the dearest. A
    /// munitions bunker is the dearest building on a base and armoured past every missile, so without
    /// the armour test every bomber was sent at it.
    /// </summary>
    private static Unit? PickPriorityTarget(FactionHQ hq, FactionHQ localHq, GlobalPosition playerBase, Aircraft aircraft)
    {
        // Same priority table every AI weapon uses (CommanderTargetPriorityPatches): income first.
        Unit? best = null;
        float bestScore = 0f;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            if (!entry.Value.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not Building
                || !ReferenceEquals(unit.NetworkHQ, localHq)
                || unit.definition == null)
            {
                continue;
            }

            float score = unit.definition.value
                + (CommanderEconomyService.IsIncomeBuildingUnit(unit) ? IncomeTargetBonus : 0f);
            if (score <= bestScore
                || !CanAnyStoreDefeat(aircraft, unit)
                || !FastMath.InRange(entry.Value.lastKnownPosition, playerBase, StrikeRadiusMeters))
            {
                continue;
            }

            best = unit;
            bestScore = score;
        }

        return best;
    }

    /// <summary>
    /// The known player building or ground vehicle with the most of the same within
    /// <see cref="SalvoClusterMeters"/>, each weighed 1 + sqrt(price)/5 so a column of tanks can
    /// outweigh a hangar, an income building +5. Only targets a store aboard can defeat count.
    /// </summary>
    private static Unit? PickSalvoTarget(FactionHQ hq, FactionHQ localHq, GlobalPosition playerBase, Aircraft aircraft)
    {
        SalvoCandidates.Clear();
        GlobalPosition from = aircraft.GlobalPosition();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            if (SalvoCandidates.Count >= MaxSalvoCandidates)
            {
                break;
            }

            GlobalPosition at = entry.Value.lastKnownPosition;
            if (!entry.Value.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not (Building or GroundVehicle)
                || !ReferenceEquals(unit.NetworkHQ, localHq)
                || unit.definition == null
                || !(FastMath.InRange(at, from, SalvoSearchMeters) || FastMath.InRange(at, playerBase, StrikeRadiusMeters))
                || !CanAnyStoreDefeat(aircraft, unit))
            {
                continue;
            }

            float weight = 1f + (Mathf.Sqrt(Mathf.Max(0f, unit.definition.value)) / 5f)
                + (CommanderEconomyService.IsIncomeBuildingUnit(unit) ? 5f : 0f);
            SalvoCandidates.Add((unit, at, weight));
        }

        Unit? best = null;
        float bestScore = 0f;
        for (int i = 0; i < SalvoCandidates.Count; i++)
        {
            float score = 0f;
            for (int j = 0; j < SalvoCandidates.Count; j++)
            {
                if (FastMath.InRange(SalvoCandidates[i].At, SalvoCandidates[j].At, SalvoClusterMeters))
                {
                    score += SalvoCandidates[j].Weight;
                }
            }

            if (score > bestScore)
            {
                best = SalvoCandidates[i].Unit;
                bestScore = score;
            }
        }

        SalvoCandidates.Clear();
        return best;
    }

    /// <summary>Something loaded aboard can hurt that target, by <see cref="CommanderAirCommandService.CanDefeatArmor"/>.</summary>
    private static bool CanAnyStoreDefeat(Aircraft aircraft, Unit target)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station != null
                && !station.Cargo
                && station.Ammo > 0
                && CommanderAirCommandService.CanDefeatArmor(station.WeaponInfo, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The highest armour tier any loaded, non-cargo store aboard can defeat.</summary>
    private static float GetBestArmorEffectiveness(Aircraft aircraft)
    {
        float best = -1f;
        List<WeaponStation>? stations = aircraft.weaponStations;
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station != null && !station.Cargo && station.Ammo > 0 && station.WeaponInfo != null)
            {
                best = Mathf.Max(best, station.WeaponInfo.armorTierEffectiveness);
            }
        }

        return best;
    }

    /// <summary>
    /// Whether this airframe is actually carrying bombs right now. Read off the live weapon stations
    /// rather than off the airframe's role, because what a bought aeroplane came out of the door
    /// with is a standard loadout the mod does not choose for a fixed-wing buy.
    /// </summary>
    private static bool HasBombLoadout(Aircraft aircraft)
    {
        // Glide and laser-guided bombs count too: the Alkyon's glide bombs were not a "bomb" here.
        return CommanderAirCommandService.HasBombAmmo(aircraft, glideOnly: false);
    }

    /// <summary>
    /// CAS, unless something aboard is a strategic store (CAS never fires one), or nothing is a CAS
    /// weapon and something is a strategic one. STRATEGIC STRIKE fires every store that hurts the
    /// surface. CAS refuses standoff and strategic stores, so an airframe carrying only those would
    /// never attack. The tasking line logs what each airframe carries.
    /// </summary>
    private static CommanderAirCommandService.AirCommandMode PickStrikeMode(Aircraft aircraft)
    {
        return CommanderAirCommandService.CarriesStrategicStore(aircraft)
            || (!CommanderAirCommandService.CanUseMode(aircraft, CommanderAirCommandService.AirCommandMode.Cas)
                && CommanderAirCommandService.CanUseMode(aircraft, CommanderAirCommandService.AirCommandMode.StrategicStrike))
            ? CommanderAirCommandService.AirCommandMode.StrategicStrike
            : CommanderAirCommandService.AirCommandMode.Cas;
    }

    /// <summary>What an airframe has aboard, by weapon name, for the log.</summary>
    private static string DescribeStores(Aircraft aircraft)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        List<string> names = new();
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            string? name = station?.WeaponInfo?.weaponName;
            if (station != null && station.Ammo > 0 && !string.IsNullOrEmpty(name) && !names.Contains(name!))
            {
                names.Add(name!);
            }
        }

        return names.Count == 0 ? "nothing" : string.Join(", ", names);
    }

    /// <summary>
    /// The leading edge of the commander's own ground push: the vehicle it owns that has got
    /// nearest to <paramref name="target"/>. <paramref name="pastHalfway"/> says whether that
    /// vehicle is more than half the way there, which is the difference between a convoy that has
    /// actually set off and one still milling around its own base.
    /// </summary>
    /// <remarks>
    /// This is the whole of the ground/air coordination, and it is deliberately one position rather
    /// than a schedule. The ground half of the enemy needs no steering — the base game drives every
    /// unpinned vehicle at the nearest tracked enemy on its own — so "attack together" means
    /// pointing the air half at wherever the convoys have actually got to. The CAS box centred here
    /// puts the strike package over the fight the ground units are already in, and at 25 km wide it
    /// still covers the player's base for the last third of the approach. The gunship escort uses
    /// the same position without the halfway test, because escorting a convoy that has not left yet
    /// is still escorting it.
    /// </remarks>
    private static bool TryGetSpearhead(
        FactionHQ hq, GlobalPosition target, out GlobalPosition spearhead, out bool pastHalfway)
    {
        spearhead = target;
        pastHalfway = false;
        if (hq.factionUnits == null)
        {
            return false;
        }

        GlobalPosition home = CommanderCaptureService.GetTerritoryCenter(hq);
        float halfway = FastMath.Distance(home, target) * 0.5f;
        float closest = float.MaxValue;
        bool found = false;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle vehicle)
            {
                continue;
            }

            GlobalPosition position = vehicle.transform.GlobalPosition();
            float toTarget = FastMath.Distance(position, target);
            if (toTarget >= closest)
            {
                continue;
            }

            closest = toTarget;
            spearhead = position;
            pastHalfway = FastMath.Distance(position, home) > halfway;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// A helicopter or tiltwing. There are exactly two of these and the loadout already decided
    /// which: one carrying cargo is left entirely alone, because the base game's
    /// <c>AIHeloTransportState</c> flies a complete delivery on its own and a mission would only
    /// keep it in the combat state with the cargo still aboard. One carrying no cargo is the
    /// convoy's gunship, and it gets a CAS mission on the convoy — as a target *filter*, not as
    /// steering, so it shoots at what is near the vehicles it is riding with instead of whatever
    /// <c>ChooseHQTarget</c> finds across the map. The area is re-pointed every review because the
    /// convoy drives away, and <c>CommanderAirCommandPatches.HeloNoTargetPostfix</c> is what makes
    /// it follow rather than land itself out at home.
    /// </summary>
    /// <summary>
    /// When the player is down to one base, gunships and strike aircraft land on it to take it, up
    /// to <see cref="MaxCaptureLanders"/> at a time; a fixed-wing only where its takeoff roll fits.
    /// Buildings still standing there only slow a capture (they add to the base's capture
    /// defence); they never block it. Returns whether this aircraft was sent.
    /// </summary>
    private static bool TrySendCaptureLanding(
        CommanderAirCommandService airCommand, FactionHQ hq, FactionHQ localHq, Aircraft aircraft)
    {
        bool rotary = !CommanderAirCommandService.HasPlanePilot(aircraft);

        // A bomber is not a lander: it bombs the player's last base instead of landing on it.
        if (IsCargoConfigured(aircraft)
            || CommanderVictoryService.CountBases(localHq) != 1
            || (!rotary && (GetAirRole(aircraft) == AirRole.Fighter || IsRadarPicket(aircraft.definition, hq) || HasBombLoadout(aircraft))))
        {
            return false;
        }

        foreach (Airbase airbase in localHq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            // CHALLENGING keeps its aircraft for empty bases while the last base is still guarded; landing
            // among defenders throws the aircraft away. An empty ring is fair game.
            if ((IsChallenging(hq) && AnyNeutralBase() && CommanderCapturePatches.CountHeads(airbase, localHq) > 0)
                || (!rotary && (aircraft.definition is not AircraftDefinition definition
                    || !CommanderAirCommandService.HasTakeoffRunway(airbase, definition))))
            {
                return false;
            }

            if (airCommand.CountAiCaptureLandings(hq, airbase) < MaxCaptureLanders
                && airCommand.TrySetAiCaptureLanding(aircraft, airbase))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) sends {CommanderGameAccess.GetUnitLabel(aircraft)} "
                        + $"to land on {CommanderCaptureService.GetAirbaseLabel(airbase)} and capture it.");
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Names the cargo and gunship loadouts a rotary airframe actually has, once per mission.
    /// </summary>
    /// <remarks>
    /// The escort/delivery split is only real if the airframe owns a standard loadout of each kind,
    /// and standard loadouts are asset data a decompile cannot read — so this is the only way to
    /// tell "the Tarantula flew as a transport because that is what the commander wanted" from
    /// "because it has no cargo-free loadout and <c>PickStandardLoadout</c> fell back to the dice".
    /// </remarks>
    private static string DescribeRotaryLoadouts(AircraftDefinition definition, FactionHQ hq)
    {
        if (CommanderAirCommandService.HasPlanePilot(definition))
        {
            return string.Empty;
        }

        StandardLoadout? cargo = PickStandardLoadout(definition, hq, true);
        StandardLoadout? gunship = PickStandardLoadout(definition, hq, false);
        return $", cargo loadout '{DescribeLoadoutKind(cargo, wantCargo: true)}'"
            + $", gunship loadout '{DescribeLoadoutKind(gunship, wantCargo: false)}'";
    }

    /// <summary>The loadout's name, or why it is not the kind that was asked for.</summary>
    private static string DescribeLoadoutKind(StandardLoadout? loadout, bool wantCargo)
    {
        List<WeaponMount>? mounts = loadout?.loadout?.weapons;
        if (loadout == null || mounts == null)
        {
            return "none";
        }

        CountLoadoutMounts(mounts, out int cargo, out int guns);
        string name = string.IsNullOrWhiteSpace(loadout.Name) ? "unnamed" : loadout.Name;
        bool matches = wantCargo ? cargo > 0 : cargo == 0;
        return matches ? name : $"{name} — WRONG KIND, fell back to a random pick ({cargo} cargo, {guns} gun)";
    }

    /// <summary>What a launch line calls the airframe: role alone is not enough now that the same
    /// hull is bought as two different aircraft.</summary>
    private static string DescribeAirframeJob(
        AircraftDefinition definition, bool? wantCargo, StandardLoadout? loadout, bool interceptor = false)
    {
        AirRole role = interceptor ? AirRole.Fighter : GetAirRole(definition);
        if (wantCargo == null)
        {
            return role.ToString();
        }

        string? name = loadout?.Name;
        string suffix = string.IsNullOrWhiteSpace(name) ? string.Empty : $": {name}";
        return (wantCargo.Value ? "Transport, loaded" : "Gunship escort") + suffix;
    }

    /// <summary>Aircraft this faction currently has in the world, pilots and AI alike.</summary>
    /// <summary>
    /// Aircraft this commander may have in the world at once: 30 on the duel, 100 in Champions.
    /// Flat on purpose. It is there to keep the frame rate survivable, and wealth is felt in how fast
    /// the wing is bought and how dear the airframes are, which is the buy loop's business, not this
    /// one's. See <see cref="DuelAirborneLimit"/>.
    /// </summary>
    private static int GetAirborneLimit()
    {
        return IsChampionsMission ? ChampionsAirborneLimit : DuelAirborneLimit;
    }

    private static int CountAirborne(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft aircraft && !unit.disabled && !CommanderFreeAiPilots.IsFree(aircraft))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// How high each of this commander's aeroplanes cruises between attacks, re-read every ten
    /// seconds from the situation rather than fixed per airframe or per price. Only a cruise height:
    /// the moment a pilot has a target, Air Command stops writing altitude and the base game flies
    /// the attack, multi-kilometre dives for speed included (ApplyMissionTargetAltitude).
    /// </summary>
    private static void UpdateCruiseHeights(FactionHQ hq)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || !IsDuelMission)
        {
            return;
        }

        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (CommanderAirCommandService.HasPlanePilot(aircraft))
            {
                airCommand.SetAiCruiseAltitude(aircraft, GetCruiseHeight(hq, aircraft));
            }

            // Same ten-second clock: an Eyeball aboard goes out ahead near the target.
            airCommand.TryLaunchAiScout(aircraft);
        }
    }

    private static void TaskRotaryAircraft(
        CommanderAirCommandService airCommand, FactionHQ hq, Aircraft aircraft, GlobalPosition convoyPoint)
    {
        if (IsCargoConfigured(aircraft))
        {
            return;
        }

        if (airCommand.TryTaskAiAircraft(
            aircraft,
            CommanderAirCommandService.AirCommandMode.Cas,
            convoyPoint,
            EscortRadiusMeters,
            allowRotary: true))
        {
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) put {CommanderGameAccess.GetUnitLabel(aircraft)} "
                    + "on the convoy as a gunship escort.");
            return;
        }

        airCommand.RetargetAiMission(
            aircraft,
            CommanderAirCommandService.AirCommandMode.Cas,
            convoyPoint,
            EscortRadiusMeters);
    }

    /// <summary>
    /// Buys one aircraft and launches it directly, instead of topping up stock and relying on
    /// <c>FactionHQ.DeployAIAircraft</c> (free pilots only in the duel, see <c>CommanderFreeAiPilots</c>), which picks a random
    /// airframe and a random field. Here the field is chosen first and only aircraft it accepts are
    /// considered. Returns what it spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wing is composed by role. Within a role it buys the cheapest airframe until it runs
    /// <see cref="CheapAirframesPerRole"/> of them, then starts spending up.
    /// </para>
    /// <para>
    /// Helicopters are allowed: <c>TryTaskAiAircraft</c> gives them no mission and
    /// <c>AIHeloCombatState</c> flies them on its own. <c>PilotType.VTOL</c> is still excluded since
    /// <c>Pilot.SetStartingAiState</c> gives it no AI state (see <c>CommanderAirCommandService.CanAiFly</c>).
    /// </para>
    /// <para>
    /// Every path that buys nothing logs why, once.
    /// </para>
    /// </remarks>
    private float BuyAirframe(
        FactionHQ hq, CommanderState state, float budget, in ForceRead playerForce, float wealthSteps)
    {
        LogAirRosterOnce(hq);
        int airborneLimit = GetAirborneLimit();
        if (CountAirborne(hq) >= airborneLimit)
        {
            ReportAirDenial(hq, state, $"it is at the {airborneLimit}-aircraft ceiling");
            return 0f;
        }

        // One radar picket once the wing has something for it to watch over. Saved for rather than
        // skipped when the fund is short: it is dear, and "buy something cheaper instead" every
        // review is how it would never happen.
        // The second picket is bought when the money is there, never saved for: a
        // wing that stops buying fighters to afford a second radar is not a harder opponent.
        int pickets = CountPickets(hq);
        if (pickets < (IsChallenging(hq) ? ChallengingRadarPickets : MaxRadarPickets) && CountAirborne(hq) - pickets >= PicketAfterAirframes * (pickets + 1))
        {
            float picketSpent = TryBuyPicket(hq, state, budget, out bool picketAvailable);
            if (picketSpent > 0f)
            {
                return picketSpent;
            }

            if (picketAvailable && pickets == 0 && !HasAirStock(hq))
            {
                ReportAirDenial(hq, state, "it is saving its air fund for a radar picket");
                return 0f;
            }
        }

        // Stock first, whatever the role: it is already paid for. The any-role pass below only runs when
        // the wanted role cannot be bought, and a commander that can always buy it new (GOD, any rich
        // one) left its plants' output and every recovered airframe in the hangar all match. A budget
        // of 0 lets only stocked airframes through. Not in a draft, where everything is stock and the
        // role pick below is what composes the wing.
        if (!CommanderDraftService.IsDraftMission && HasAirStock(hq))
        {
            float launched = TryBuyRole(hq, state, 0f, null, state.NextStrikeBombs, false, playerForce);
            if (launched > 0f)
            {
                return launched;
            }
        }

        // A commander several tenfolds past the authored balance never serves the cheap opening: it
        // can replace anything it loses, so the first airframe it buys is already the dear one.
        bool rich = wealthSteps >= DearAirframeDecades;

        AirRole wanted = ChooseAirRole(hq, playerForce);

        // Strike buys alternate between close support and bombing, decided before the pick so the
        // airframe is chosen for the job (a Brawler over the convoys, a Darkreach for the factories)
        // instead of rolling the job onto whatever was bought.
        bool bombing = state.NextStrikeBombs;
        float spent = TryBuyRole(
            hq, state, budget, wanted, bombing, rich || CountRole(hq, wanted) >= CheapAirframesPerRole, playerForce);
        if (spent > 0f && wanted == AirRole.Strike)
        {
            state.NextStrikeBombs = !bombing;
        }

        // Saving for the wanted role never holds back stock of another role: the wrong aircraft in
        // the air beats the right one still being saved for.
        if (spent > 0f || (savingFor != null && !HasAirStock(hq)))
        {
            return spent;
        }

        // Nothing in the wanted role fits the budget or the strip — or this faction's roster has no
        // such airframe at all, which is the usual reason. Buying the wrong kind of aircraft beats
        // buying none, so the second pass takes anything that will fly, and it measures "am I past
        // the cheap opening" against the whole wing rather than against a role it cannot field.
        return TryBuyRole(hq, state, budget, null, bombing, rich || CountAirborne(hq) >= CheapAirframesPerRole, playerForce);
    }

    /// <summary>
    /// The Cricket is an early-game airframe: it is only bought when nothing else fits the budget and
    /// the strips, and never past <see cref="MaxLiveCrickets"/> alive. A Cricket line's stock, free to
    /// launch, sent six up in one review and kept the wing all Crickets after better airframes were
    /// affordable. Stocked Crickets still fly once the fund is empty, since nothing else fits then.
    /// </summary>
    private float DropCrickets(FactionHQ hq, bool escalate, float reference)
    {
        bool cricket = false;
        bool other = false;
        for (int i = 0; i < airCandidates.Count; i++)
        {
            if (CommanderEconomyService.IsCricket(airCandidates[i].Definition))
            {
                cricket = true;
            }
            else
            {
                other = true;
            }
        }

        if (!cricket)
        {
            return reference;
        }

        if (!other)
        {
            CountAirframesOwned(hq);
            int live = 0;
            foreach (KeyValuePair<AircraftDefinition, int> owned in airframesOwned)
            {
                if (CommanderEconomyService.IsCricket(owned.Key))
                {
                    live += owned.Value;
                }
            }

            if (live < MaxLiveCrickets)
            {
                return reference;
            }
        }

        airCandidates.RemoveAll(candidate => CommanderEconomyService.IsCricket(candidate.Definition));
        reference = escalate ? 0f : float.MaxValue;
        for (int i = 0; i < airCandidates.Count; i++)
        {
            float price = CommanderEconomyService.PriceOf(airCandidates[i].Definition);
            reference = escalate ? Mathf.Max(reference, price) : Mathf.Min(reference, price);
        }

        return reference;
    }

    /// <summary>
    /// Narrows the candidates to the ones in stock when there are any, so a factory's output flies
    /// before the commander pays for another airframe, and gives back the price band's new reference.
    /// </summary>
    private float KeepStockedCandidates(FactionHQ hq, bool escalate, float reference)
    {
        bool anyStocked = false;
        for (int i = 0; i < airCandidates.Count && !anyStocked; i++)
        {
            anyStocked = hq.GetUnitSupply(airCandidates[i].Definition) > 0;
        }

        if (!anyStocked)
        {
            return reference;
        }

        airCandidates.RemoveAll(candidate => hq.GetUnitSupply(candidate.Definition) <= 0);
        reference = escalate ? 0f : float.MaxValue;
        for (int i = 0; i < airCandidates.Count; i++)
        {
            float price = CommanderEconomyService.PriceOf(airCandidates[i].Definition);
            reference = escalate ? Mathf.Max(reference, price) : Mathf.Min(reference, price);
        }

        return reference;
    }

    /// <summary>
    /// The airframe a buy settles on: a weighted roll over everything inside
    /// <see cref="AirPriceBand"/> of the cheapest (or, once the wing is spending up, the dearest) one
    /// it can afford, each candidate divided by how many of that type are already flying. Taking the
    /// single cheapest airframe in the role is what made the wing a monoculture, and it is the same
    /// fault the ground convoys had.
    /// </summary>
    private AircraftDefinition? PickFromPriceBand(
        FactionHQ hq, float reference, bool escalate, bool interceptor, bool byJob, out Airbase? field)
    {
        field = null;
        if (airCandidates.Count == 0)
        {
            return null;
        }

        float floor = escalate ? reference / AirPriceBand : reference;
        float ceiling = escalate ? reference : reference * AirPriceBand;
        CountAirframesOwned(hq);
        AircraftDefinition? pick = null;
        float total = 0f;
        for (int i = 0; i < airCandidates.Count; i++)
        {
            (AircraftDefinition definition, Airbase candidateField) = airCandidates[i];

            // A fighter buy is not held to the price band: it weighs every affordable interceptor
            // by InterceptorWeight, so the Compass and Vagrant beat a Cricket on the opening strip
            // and an Ifrit wins once a field that takes it is held.
            // Nor is a buy past the opening: the candidates are already the best for the job, and
            // among those a dearer one is not a better one.
            if (!interceptor && !byJob && (CommanderEconomyService.PriceOf(definition) < floor || CommanderEconomyService.PriceOf(definition) > ceiling))
            {
                continue;
            }

            airframesOwned.TryGetValue(definition, out int owned);
            float weight = 1f / (1 + owned);
            if (interceptor)
            {
                weight *= InterceptorWeight(GetInterceptorScore(definition), CommanderEconomyService.PriceOf(definition));
            }

            total += weight;
            if (Random.value * total <= weight)
            {
                pick = definition;
                field = candidateField;
            }
        }

        return pick;
    }

    private bool IsAirCandidate(AircraftDefinition definition)
    {
        for (int i = 0; i < airCandidates.Count; i++)
        {
            if (ReferenceEquals(airCandidates[i].Definition, definition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How many of each airframe type the faction is flying, for the buy weighting.</summary>
    private void CountAirframesOwned(FactionHQ hq)
    {
        airframesOwned.Clear();
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is Aircraft airframe
                && !unit.disabled
                && !CommanderFreeAiPilots.IsFree(airframe)
                && unit.definition is AircraftDefinition definition)
            {
                airframesOwned.TryGetValue(definition, out int owned);
                airframesOwned[definition] = owned + 1;
            }
        }
    }

    private float TryBuyRole(
        FactionHQ hq, CommanderState state, float budget, AirRole? role, bool bombing, bool escalate, in ForceRead playerForce)
    {
        float cheapestSeen = float.MaxValue;
        bool sawAnyAirbase = false;
        savingFor = null;
        int savingForFit = 0;

        // Scan the whole air estate before buying, rather than settling for the first field's best, or a
        // highway strip early in the list limits every purchase.
        airCandidates.Clear();
        float reference = escalate ? 0f : float.MaxValue;

        // The any-airframe pass must not fall back on a helicopter while there is no convoy for it
        // to ride with (ChooseAirRole's own rule). On Carrier Duel CHALLENGING bought Chicanes three
        // a review that way, and with nothing to escort they hovered over open sea between its own
        // ships and wrote themselves off: eight lost in two minutes, none to enemy fire.
        bool noRotary = role == null && CountGroundVehicles(hq) == 0;

        // Against a fleet the strike wing is an anti-ship wing: CHALLENGING arms every strike
        // airframe for it, NORMAL half of them (all of them when the player's home is a ship), and
        // either buys a hull that can carry such a store whenever its strips offer one. A trainer
        // with rockets and light bombs cannot scratch a warship, and on Carrier Duel the warships
        // are the bases.
        bool antiShip = role != AirRole.Fighter
            && playerForce.Ships > 0
            && (IsChallenging(hq) || playerForce.HomeAfloat || Random.value < 0.5f);
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            sawAnyAirbase = true;
            foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
            {
                AircraftDefinition? definition = rosterEntry;

                // The picket is only ever bought by TryBuyPicket. By role it is a fighter, and it
                // is the dearest one, so once the wing was spending up every fighter buy was a
                // Medusa - which SteerPickets then turned into a radar picket that CountRole does
                // not count, so the fighter was still missing and the next buy was another Medusa.
                if (definition == null
                    || (noRotary && !CommanderAirCommandService.HasPlanePilot(definition))
                    || IsRadarPicket(definition, hq)
                    || IsBenched(definition)
                    || !IsInDraftStock(hq, definition)
                    || !CanLaunchFrom(airbase, hq, definition))
                {
                    continue;
                }

                if (CommanderEconomyService.PriceOf(definition) < cheapestSeen)
                {
                    cheapestSeen = CommanderEconomyService.PriceOf(definition);
                }

                if (role.HasValue && !FitsRole(definition, role.Value))
                {
                    continue;
                }

                // Not its job at all (a Darkreach for close support, a Brawler as a fighter). An
                // anti-ship buy has its own filter, KeepAntiShipCandidates, and is not rated here.
                int fit = antiShip ? 1 : GetJobFit(definition, role, bombing);
                if (fit <= 0)
                {
                    continue;
                }

                // The goal is the airframe best suited to the job, the cheapest of those, not the
                // dearest: an Alkyon is not a better close-support aircraft than a Brawler.
                float price = CommanderEconomyService.PriceOf(definition);
                if (escalate
                    && !CommanderEconomyService.IsCricket(definition)
                    && price <= state.AirSaveCap
                    && (savingFor == null
                        || fit > savingForFit
                        || (fit == savingForFit && price < CommanderEconomyService.PriceOf(savingFor))))
                {
                    savingFor = definition;
                    savingForFit = fit;
                }

                if (GetLaunchPrice(hq, definition) > budget)
                {
                    continue;
                }

                // Once per airframe, not once per field that will take it. PickLaunchField chooses
                // the field afterwards anyway, and a second entry would weight the roll toward
                // whatever every strip happens to accept, which is the trainers.
                if (IsAirCandidate(definition))
                {
                    continue;
                }

                airCandidates.Add((definition, airbase));
                reference = escalate
                    ? Mathf.Max(reference, CommanderEconomyService.PriceOf(definition))
                    : Mathf.Min(reference, CommanderEconomyService.PriceOf(definition));
            }
        }

        reference = DropCrickets(hq, escalate, reference);

        if (antiShip)
        {
            reference = KeepAntiShipCandidates(airCandidates, playerForce.ShipArmour, IsChallenging(hq), escalate, reference);
        }

        reference = KeepStockedCandidates(hq, escalate, reference);

        // Past the opening only the best-suited airframes the fund can pay for stay in the roll.
        bool byJob = escalate && !antiShip;
        int bestFit = 0;
        for (int i = 0; byJob && i < airCandidates.Count; i++)
        {
            bestFit = System.Math.Max(bestFit, GetJobFit(airCandidates[i].Definition, role, bombing));
        }

        if (byJob)
        {
            airCandidates.RemoveAll(candidate => GetJobFit(candidate.Definition, role, bombing) < bestFit);
        }

        // Past the cheap opening, save for the best airframe for the job instead of buying whatever is
        // affordable this review. Free stock still flies, and AccrueFund's ceiling is at least this
        // price, so this never holds forever.
        bool stocked = airCandidates.Count > 0 && GetLaunchPrice(hq, airCandidates[0].Definition) <= 0f;
        if (savingFor != null
            && !stocked
            && budget < CommanderEconomyService.PriceOf(savingFor)
            && bestFit < savingForFit)
        {
            ReportAirDenial(
                hq,
                state,
                $"it is saving its air fund ({budget:0}) for a {savingFor.unitName} ({CommanderEconomyService.PriceOf(savingFor):0})");
            return 0f;
        }

        savingFor = null;
        AircraftDefinition? choice = PickFromPriceBand(
            hq, reference, escalate, role == AirRole.Fighter, byJob, out Airbase? bestAirbase);

        // Every field that can take the airframe is a candidate, and the one quiet longest gets the
        // sortie, so attacks come from different bearings.
        if (choice != null)
        {
            bestAirbase = PickLaunchField(hq, choice) ?? bestAirbase;
        }

        if (choice != null && bestAirbase != null)
        {
            // What a rotary airframe carries decides what it *is*, so it is chosen rather than
            // rolled: the convoy's gunship escort first, cargo runs once that escort exists. A
            // fixed-wing airframe passes null and keeps the base game's random pick.
            // CHALLENGING flies every helicopter as a gunship: it escorts the push, and an empty
            // one is what gets landed on a base nobody holds.
            bool? wantCargo = CommanderAirCommandService.HasPlanePilot(choice)
                ? null
                : !IsChallenging(hq) && HasConvoyEscort(hq);
            StandardLoadout? standard = PickStandardLoadout(choice, hq, wantCargo);
            Loadout? loadout = standard?.loadout;
            bool antiShipArmed = false;
            bool radarHunter = false;
            string theme = string.Empty;
            bool interceptor = wantCargo == null
                && (role == AirRole.Fighter || GetAirRole(choice) == AirRole.Fighter);
            if (interceptor)
            {
                // A fighter flies AIR SUPERIORITY, which fires nothing at the ground. The random
                // standard loadout put bombs and an anti-ship missile on the Ifrit, dead weight the
                // whole sortie. Every pylon gets the best air-to-air store the strip may issue
                // (Scythes, heat seekers, Scimitars); a standard loadout stripped of its ground
                // stores is only the fallback, because a multirole hull's standard loadouts carry
                // one or two missiles among the bombs.
                standard = PickRoleLoadout(choice, hq, ScoreFighterStore) ?? standard;
                Loadout? built = BuildRoleLoadout(choice, hq, bestAirbase, ScoreFighterStore);
                loadout = HasAnyStore(built) ? built : StripSurfaceStores(standard?.loadout);
            }
            else if (wantCargo == null)
            {
                // A strike airframe takes the loadout with the most it can drop on a base. The dice
                // sent an Alkyon out with an ARAD, a jammer and one heat seeker: nothing a CAS
                // sortie fires at a building, so it flew the whole match and hit nothing.
                // The job was settled before the pick (BuyAirframe alternates them): bombers go at
                // the player's base, high (TaskAirWing), close support stays with the convoys.
                float shipArmour = playerForce.ShipArmour;
                float AntiShipJob(WeaponMount mount)
                {
                    return ScoreAntiShipStore(mount, shipArmour);
                }

                StandardLoadout? antiShipStandard = antiShip ? PickRoleLoadout(choice, hq, AntiShipJob) : null;
                Loadout? antiShipBuilt = antiShip && antiShipStandard == null
                    ? BuildRoleLoadout(choice, hq, bestAirbase, AntiShipJob)
                    : null;
                if (antiShipStandard != null || HasAnyStore(antiShipBuilt))
                {
                    antiShipArmed = true;
                    standard = antiShipStandard;
                    loadout = antiShipStandard?.loadout ?? antiShipBuilt;
                }
                else if (!bombing
                    && WantsRadarHunter(hq)
                    && BuildRoleLoadout(choice, hq, bestAirbase, ScoreRadarHunterStore) is Loadout hunter
                    && CarriesArad(hunter))
                {
                    // The airframe can carry ARADs and the player has a radar up: it goes hunting.
                    radarHunter = true;
                    standard = null;
                    loadout = hunter;
                }
                else if (TryBuildThemedLoadout(choice, hq, bestAirbase, bombing, out Loadout? themed, out theme))
                {
                    // Half the time a listed airframe goes up full of one store instead.
                    standard = null;
                    loadout = themed;
                }
                else if (!bombing && BuildCasLoadout(choice, hq, bestAirbase) is Loadout cas)
                {
                    standard = null;
                    loadout = cas;
                }
                else
                {
                    System.Func<WeaponMount, float> job = bombing ? ScoreBombingStore : ScoreCasStore;
                    standard = PickRoleLoadout(choice, hq, job) ?? PickRoleLoadout(choice, hq, ScoreStrikeStore) ?? standard;
                    loadout = standard?.loadout ?? BuildRoleLoadout(choice, hq, bestAirbase, ScoreStrikeStore);
                }
            }

            if (TryLaunchAirframe(
                hq,
                state,
                bestAirbase,
                choice,
                loadout,
                DescribeAirframeJob(choice, wantCargo, standard, interceptor) + (antiShipArmed ? ", anti-ship" : radarHunter ? ", radar hunter" : theme.Length > 0 ? $", full of {theme}" : string.Empty),
                out float cost))
            {
                return cost;
            }
        }

        if (role == null)
        {
            string denial = !sawAnyAirbase
                ? "it holds no airbase to launch from"
                : choice != null && CommanderAirCommandService.LastLaunchRefusal.Length > 0
                    ? $"the launch of a {choice.unitName} was refused: {CommanderAirCommandService.LastLaunchRefusal}"
                : cheapestSeen == float.MaxValue
                    ? "no airframe its strips accept has an AI flight model (VTOLs have none)"
                    : $"its air fund is {budget:0} and the cheapest airframe its strips accept costs {cheapestSeen:0}";
            ReportAirDenial(
                hq,
                state,
                denial);
        }

        return 0f;
    }

    /// <summary>The field that can fly this airframe and has gone longest without launching one.</summary>
    private Airbase? PickLaunchField(FactionHQ hq, AircraftDefinition definition)
    {
        Airbase? best = null;
        float bestTime = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null || !CanLaunchFrom(airbase, hq, definition))
            {
                continue;
            }

            float last = lastLaunchTimes.TryGetValue(airbase, out float at) ? at : 0f;
            if (best == null || last < bestTime)
            {
                best = airbase;
                bestTime = last;
            }
        }

        return best;
    }

    /// <summary>Puts a bought airframe in the air and pays for it. Every buy path ends here.</summary>
    private bool TryLaunchAirframe(
        FactionHQ hq,
        CommanderState state,
        Airbase airbase,
        AircraftDefinition choice,
        Loadout? loadout,
        string job,
        out float cost)
    {
        cost = 0f;
        LiveryKey livery = new(choice.aircraftParameters.GetRandomLiveryForFaction(hq.faction));

        // The air fund is money set aside earlier, and the balance may have been spent since. The
        // stores are paid for on every launch, as the player's are. A null loadout is the hangar's
        // random pick, which cannot be priced before it spawns.
        float munitions = CommanderAirCommandService.GetMunitionsCost(choice, loadout);
        bool fromStock = hq.GetUnitSupply(choice) > 0;

        // An airframe in stock is already paid for and goes up on whatever the balance can pay of its stores.
        if (!fromStock && hq.factionFunds < munitions + CommanderEconomyService.PriceOf(choice))
        {
            return false;
        }

        // Restock this field's shelves out of the air fund when they cannot cover the launch. Left
        // to the review's general top-up, a strip it never reached refused every launch all match.
        float restock = 0f;
        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        if (stores != null
            && !stores.CanCoverSpawnSupply(hq, airbase.center.GlobalPosition(), choice, out _))
        {
            restock = stores.TopUpForSpawn(
                hq, airbase.center.GlobalPosition(), choice, Mathf.Max(0f, hq.factionFunds - munitions - (fromStock ? 0f : CommanderEconomyService.PriceOf(choice))));
        }

        // Full tanks, as the player's launches get. A standard loadout's fuel ratio is part-filled,
        // and the Darkreach flew eight minutes, hit the game's own 20 % return-to-base and landed
        // at home without ever reaching the player: "it only defends its own base".
        if (!CommanderAirCommandService.LaunchAiAircraft(hq, airbase, choice, livery, loadout!, 1f))
        {
            return false;
        }

        // An airframe that flew home and was recovered went back into this faction's stock, and
        // nothing ever took it out again: every Medusa that came home on fuel was paid for twice.
        // Stock goes first and is free.
        if (fromStock)
        {
            hq.ModifyUnitSupply(choice, -1);
        }

        cost = (fromStock ? 0f : Mathf.Max(0f, CommanderEconomyService.PriceOf(choice))) + munitions;
        lastLaunchTimes[airbase] = Time.timeSinceLevelLoad;
        cost = Mathf.Min(cost, Mathf.Max(0f, hq.factionFunds));
        CommanderGameAccess.TrySpend(hq, cost);
        cost += restock; // already paid by the top-up; counted so the air fund carries it

        // A drafted launch, or a stock one with an empty loadout, is free, but every caller reads
        // "spent nothing" as "launched nothing" and would go on to launch a second airframe in the
        // same buy.
        if (CommanderDraftService.IsDraftMission || fromStock)
        {
            cost = Mathf.Max(cost, DraftLaunchMarker);
        }

        TotalPurchases++;
        state.LastAirDenial = string.Empty;
        CommanderPlugin.Log.LogInfo(fromStock
            ? $"Enemy commander ({hq.faction.name}) launched a {choice.unitName} ({job}) from {airbase.name} out of stock, munitions {cost:0.#}."
            : $"Enemy commander ({hq.faction.name}) launched a {choice.unitName} ({job}) from {airbase.name} for {cost:0}.");
        return true;
    }

    /// <summary>
    /// Buys the radar picket and launches it with a jamming pod on every hardpoint that offers one.
    /// <paramref name="available"/> says whether any field could launch one at all, which decides
    /// between saving up for it and buying something else.
    /// </summary>
    private float TryBuyPicket(FactionHQ hq, CommanderState state, float budget, out bool available)
    {
        available = false;
        foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
        {
            AircraftDefinition? definition = rosterEntry;
            if (definition == null || !IsRadarPicket(definition, hq) || !IsInDraftStock(hq, definition))
            {
                continue;
            }

            // The field that has gone longest without a launch, like every other buy. Taking the
            // first field in the list put every Medusa up from North Boscali.
            Airbase? airbase = PickLaunchField(hq, definition);
            if (airbase == null)
            {
                continue;
            }

            available = true;
            if (GetLaunchPrice(hq, definition) > budget)
            {
                continue;
            }

            Loadout? loadout = BuildPicketLoadout(definition, hq, out int pods);
            return TryLaunchAirframe(
                hq, state, airbase, definition, loadout, $"RADAR PICKET, {pods} dome/ARAD/jammer pylon(s)", out float cost)
                ? cost
                : 0f;
        }

        return 0f;
    }

    /// <summary>
    /// Names, once per mission, every airframe on this faction's list with the three things the buy
    /// loop decides on: the pilot type the Basegame AI will fly it with, the role this reads off its
    /// role identity, and its price.
    /// </summary>
    /// <remarks>
    /// All three live in the game's asset files, not in its code, so a decompile cannot answer any
    /// of them and neither can reasoning about aircraft names. This line in the BepInEx console is
    /// the answer. It is also the only way to tell a genuinely unflyable airframe from one this
    /// filter is wrongly excluding: anything logged as VTOL is one the mod refuses to buy, and if
    /// something you have watched the Basegame AI fly appears there, the filter is wrong, not the
    /// aeroplane.
    /// </remarks>
    private void LogAirRosterOnce(FactionHQ hq)
    {
        if (!loggedAirRoster.Add(hq))
        {
            return;
        }

        foreach (AircraftDefinition rosterEntry in GetAirRoster(hq))
        {
            AircraftDefinition? definition = rosterEntry;
            if (definition == null)
            {
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"Air roster ({hq.faction.name}): {definition.unitName} [{definition.jsonKey}] "
                    + $"pilot {DescribePilotTypes(definition)}, role {GetAirRole(definition)}, "
                    + $"interceptor {GetInterceptorScore(definition):0.00}"
                    + (FitsRole(definition, AirRole.Fighter) ? " (fighter buy)" : string.Empty) + ", "
                    + $"value {definition.value:0}{DescribeRotaryLoadouts(definition, hq)}"
                    + $", radar {GetRadarRange(definition) / 1000f:0} km, takeoff {definition.aircraftParameters.takeoffDistance:0} m, "
                    + $"climbs above {CommanderAirCommandService.GetSafeClimbSpeed(definition):0} m/s"
                    + $", {CountLaunchFields(hq, definition)} held field(s) list it"
                    + (IsRadarPicket(definition, hq) ? " - RADAR PICKET" : string.Empty)
                    + (CommanderAirCommandService.CanAiFly(definition) ? string.Empty : "  — NOT AI-FLYABLE, never bought"));
        }
    }

    /// <summary>
    /// Gives every airframe this faction owns that is not already on a mission something to do.
    /// A strike box over the player's territory by default; air superiority over the same box for
    /// part of the wing once the player is actually flying, because a strike package with nothing
    /// escorting it is a free kill.
    /// </summary>
    private void TaskAirWing(FactionHQ hq, FactionHQ localHq)
    {
        ReportLostAircraft(hq);
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || hq.factionUnits == null)
        {
            return;
        }

        GlobalPosition homeCentre = CommanderCaptureService.GetTerritoryCenter(hq);
        GlobalPosition playerBase = GetStrikeTarget(localHq);
        bool hasSpearhead = TryGetSpearhead(hq, playerBase, out GlobalPosition spearhead, out bool pastHalfway);

        // The convoy is where the escort lives, wherever it has got to. The strike box only follows
        // it once it is past the halfway mark; before that the box would sit on the enemy's own
        // start line and the whole wing would circle home.
        GlobalPosition convoyPoint = hasSpearhead ? spearhead : homeCentre;
        GlobalPosition strikeTarget = hasSpearhead && pastHalfway ? spearhead : playerBase;
        int fighters = 0;
        int pickets = 0;

        // Strike airframes go in waves. One at a time they were each turned back by the same air
        // defence, circled at its edge and never hit the base; the player saw one attacker at a
        // time. So they hold over the base nearest the player until StrikeWaveSize are up, then all
        // go at once and fly into the fire rather than around it, until attrition breaks the wave.
        CommanderState? wingState = states.TryGetValue(hq, out CommanderState found) ? found : null;
        bool defending = wingState?.Defending == true;
        int strikeWing = CountStrikeWing(hq, out int fighterWing);

        // GOD DIFFICULTY's divine wrath: the wave goes now, whatever its size, at the base the god
        // just lost, with every fighter escorting it. The review after it ends recalls the fighters.
        bool wrath = wingState != null && Time.time < wingState.WrathUntil;
        bool recall = wingState != null && !wrath && wingState.WrathRecall;
        if (wingState != null)
        {
            wingState.WrathRecall = wrath;
        }

        // CHALLENGING's income alarm (RaiseEcoAlarm): the home CAP moves over the building being hit,
        // and is called back the review after the raid goes quiet, as wrath's fighters are.
        bool tactics = wingState?.Tactics == true;
        bool ecoAlarm = tactics && Time.time < wingState!.EcoAlarmUntil;
        bool ecoRecall = wingState != null && !ecoAlarm && wingState.EcoRecall;
        if (wingState != null)
        {
            wingState.EcoRecall = ecoAlarm;
        }

        // CHALLENGING keeps a wave to the aircraft that left together: the wave breaks on its own
        // losses, not on the wing's, and anything launched meanwhile waits for the next one.
        if (tactics)
        {
            wingState!.WaveMembers.RemoveWhere(member => member == null || member.disabled);
        }

        // Fighters that are not on home CAP (the same split as the loop below) escort the wave, so
        // they count toward it: a CAS airframe and an air superiority one make a wave of two. It
        // still needs one strike airframe, or it is only a fighter sweep.
        int guardEvery = defending || ecoAlarm ? 2 : 3;
        int escorts = fighterWing - (fighterWing / guardEvery);
        int waveSize = ReachableWaveSize(
            tactics ? ChallengingStrikeWaveSize : StrikeWaveSize,
            wingState?.AirSaveCap ?? 0f,
            GetSpawnableAirframeValue(hq, dearest: false),
            GetAirborneLimit() - (CountAirborne(hq) - strikeWing - fighterWing));
        int waveStrike = wingState?.WaveOut == true && tactics ? wingState.WaveMembers.Count : strikeWing;

        // Each wave is rolled its own size, 2 up to what the wing can reach, and its own longest
        // wait, so the player cannot learn "three, every two minutes".
        // A striker that has waited that long goes with whatever is up: the fund sizes the wave by
        // what it could buy, not what survives. A wave that left
        // smaller than the break size breaks only when it is gone.
        if (wingState != null && wingState.WaveGoal <= 0)
        {
            wingState.WaveGoal = Random.Range(MinStrikeWaveSize, waveSize + 1);
            wingState.WaveWaitLimit = Random.Range(MinStrikeWaveWaitSeconds, MaxStrikeWaveWaitSeconds);
        }

        int waveGoal = wingState != null ? Mathf.Min(wingState.WaveGoal, waveSize) : waveSize;
        bool waveDue = waveStrike + escorts >= waveGoal;
        if (wingState != null && !wingState.WaveOut && strikeWing >= 1 && !waveDue)
        {
            if (wingState.WaveWaitSince <= 0f)
            {
                wingState.WaveWaitSince = Time.time;
            }

            waveDue = Time.time - wingState.WaveWaitSince >= wingState.WaveWaitLimit;
        }

        GlobalPosition waveTarget = wrath ? wingState!.WrathTarget : playerBase;
        bool waveOut = wrath
            ? strikeWing >= 1
            : waveStrike >= 1 && (wingState?.WaveOut == true
                ? waveStrike + escorts >= Mathf.Min(StrikeWaveBreakSize, wingState.WaveLeftWith)
                : waveDue);
        if (wingState != null && (waveOut || strikeWing == 0))
        {
            wingState.WaveWaitSince = 0f;
        }

        if (wingState != null && waveOut && !wingState.WaveOut)
        {
            wingState.WaveLeftWith = waveStrike + escorts;
        }

        bool newWave = tactics && waveOut && !wingState!.WaveOut;
        if (tactics && (newWave || !waveOut))
        {
            wingState!.WaveMembers.Clear();
        }

        GlobalPosition staging = GetStagingBase(hq, playerBase) ?? homeCentre;
        bool launched = wingState != null && waveOut && !wingState.WaveOut;
        bool pulledBack = wingState != null && !waveOut && wingState.WaveOut;
        string approach = "straight in";
        GlobalPosition? flank = null;
        if (launched && !wrath)
        {
            flank = PickWaveFlank(staging, waveTarget, out approach);
        }

        if (wingState != null && wingState.WaveOut != waveOut)
        {
            wingState.WaveOut = waveOut;
            wingState.WaveGoal = 0;
            CommanderPlugin.Log.LogInfo(waveOut
                ? $"Enemy commander ({hq.faction.name}) sends a strike wave of {strikeWing} and {escorts} escort(s) at the player's base "
                    + $"(rolled {waveGoal} of up to {waveSize}, {approach})."
                : $"Enemy commander ({hq.faction.name}) pulls its strike wing back ({strikeWing} left) to wait for the next wave.");
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft aircraft || unit.disabled || CommanderFreeAiPilots.IsFree(aircraft))
            {
                continue;
            }

            TrackAircraft(aircraft);

            // The airlift helicopter carries a hook and nothing to fight with; ReviewAirlift flies it.
            if (CommanderSlingLiftService.GetHook(aircraft) != null)
            {
                continue;
            }

            if (!CommanderAirCommandService.HasPlanePilot(aircraft))
            {
                TaskRotaryAircraft(airCommand, hq, aircraft, convoyPoint);
                TrySendCaptureLanding(airCommand, hq, localHq, aircraft);
                continue;
            }

            // A strike aircraft already on a mission can be the one that lands on the last base.
            if (TrySendCaptureLanding(airCommand, hq, localHq, aircraft))
            {
                continue;
            }

            // Counted over every airframe the faction owns, not over the ones newly tasked in this
            // review: a review buys at most one aircraft, so a per-review counter was always 0.
            CommanderAirCommandService.AirCommandMode mode;
            GlobalPosition centre;
            float radius;
            string where;
            bool bomber = false;
            bool inWave = false;
            bool withWave = false;
            Unit? shipTarget = null;
            Unit? salvoTarget = null;
            bool isFighterOrPicket = true;
            if (IsRadarPicket(aircraft.definition, hq))
            {
                // AWACS holds it inside its circle with the radar on; SteerPickets moves that circle
                // away from the player's units, and UpdateCruiseHeights decides how high it flies.
                mode = CommanderAirCommandService.AirCommandMode.AwacsJammer;
                centre = GetPicketStation(hq, playerBase, pickets++);
                radius = PicketStationRadiusMeters;
                where = " on radar watch behind its own lines.";
            }
            else if (IsRadarHunter(aircraft, hq))
            {
                // RADAR at the nearest player radar it knows of, committed so threat avoidance does
                // not turn it back at the edge of the very SAM it was sent for. Re-picked every
                // review, so it moves on once the radar is dead or switched off.
                isFighterOrPicket = false;
                mode = CommanderAirCommandService.AirCommandMode.AwacsJammer;
                Unit? radar = FindRadarToHunt(hq, aircraft.GlobalPosition(), out GlobalPosition radarAt);
                inWave = radar != null;
                centre = radar != null ? radarAt : staging;
                radius = radar != null ? StrikeRadiusMeters : HomeGuardRadiusMeters;
                where = radar != null
                    ? $" hunting the player's radar {CommanderGameAccess.GetUnitLabel(radar)}."
                    : " over its staging base, with no player radar to hunt.";
            }
            else if (GetAirRole(aircraft) == AirRole.Fighter)
            {
                // One fighter in three holds a CAP over home, one in two while the commander is
                // under attack; the rest escort the strike wave, or the front while no wave is out.
                // Only fighters stand guard: a bomber on CAP has nothing to shoot, and an early log
                // had the Alkyon AB-4 on AIR SUPERIORITY over its own ground.
                fighters++;
                bool guard = !wrath && fighters % guardEvery == 0;
                mode = CommanderAirCommandService.AirCommandMode.AirGuard;
                centre = guard ? (ecoAlarm ? wingState!.EcoAlarmAt : homeCentre) : waveOut ? waveTarget : strikeTarget;
                withWave = !guard && waveOut;
                radius = guard ? HomeGuardRadiusMeters : StrikeRadiusMeters;
                where = guard ? (ecoAlarm ? " over its income buildings, which are under attack." : " over its own ground.") : " on the front.";
            }
            else
            {
                isFighterOrPicket = false;

                // The whole strike wing works as one wave (see above). A bomb carrier in the wave is
                // also pointed at the player's most valuable building it can hurt (below).
                if (newWave)
                {
                    wingState!.WaveMembers.Add(aircraft);
                }

                inWave = waveOut && (!tactics || wrath || wingState!.WaveMembers.Contains(aircraft));
                withWave = inWave;
                bomber = HasBombLoadout(aircraft) && inWave;
                mode = PickStrikeMode(aircraft);
                centre = inWave ? waveTarget : staging;

                // Something aboard sinks ships: the wave goes at the fleet, at the hull it was armed
                // for. The box alone would leave it working whatever it found first, and a CAS
                // mission never fires a store it counts as strategic, which some anti-ship missiles are.
                if (inWave && !wrath && PickShipTarget(hq, localHq, aircraft, out GlobalPosition shipAt) is Unit ship)
                {
                    shipTarget = ship;
                    bomber = false;
                    centre = shipAt;
                    if (!HasCasStoreAgainst(aircraft, ship))
                    {
                        mode = CommanderAirCommandService.AirCommandMode.Hybrid;
                    }
                }

                // Glide bombs go where most of the player's ground is, a convoy as readily as a base,
                // and the box follows the target the wing is already on until it is destroyed.
                else if (inWave
                    && !wrath
                    && CommanderAirCommandService.HasBombAmmo(aircraft, glideOnly: true)
                    && (airCommand.GetForcedTarget(aircraft) ?? PickSalvoTarget(hq, localHq, waveTarget, aircraft)) is Unit salvo
                    && hq.TryGetKnownPosition(salvo, out GlobalPosition salvoAt))
                {
                    salvoTarget = salvo;
                    centre = salvoAt;
                }

                radius = inWave ? StrikeRadiusMeters : HomeGuardRadiusMeters;
                where = wrath
                    ? $" in the god's wrath, at the base it lost, in a wave of {strikeWing}."
                    : inWave
                    ? $" against the player's base, in a wave of {(tactics ? wingState!.WaveMembers.Count : strikeWing)}."
                    : " over its staging base, waiting for the wave.";
            }

            if (airCommand.TryTaskAiAircraft(aircraft, mode, centre, radius))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) tasked {CommanderGameAccess.GetUnitLabel(aircraft)} "
                        + $"with {CommanderAirCommandService.GetModeLabel(mode)}{where} Carrying: {DescribeStores(aircraft)}.");
            }

            // Already carrying a mission, so TryTaskAiAircraft refused. That is the state a wing
            // parked over its own base gets stuck in: a sortie that ran dry sets Returning, the
            // aeroplane comes home, and if the Basegame recovery never completes — a highway strip
            // has no taxi network to reach a hangar over — the mission stays Returning for the rest
            // of the match and nothing ever sends it out again. An airframe that is fuelled and
            // armed is sent back out instead.
            else if (airCommand.TryRelaunchIdleAiAircraft(aircraft, mode, centre, radius))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) sent {CommanderGameAccess.GetUnitLabel(aircraft)} "
                        + $"back out on {CommanderAirCommandService.GetModeLabel(mode)}{where}");
            }

            // A strike airframe follows the wave every review: out when it forms, back when it breaks.
            // Fighters keep the post they were given, or they would swap between home and front
            // whenever the unit list reorders.
            else if (!isFighterOrPicket
                || ((wrath || recall || ecoAlarm || ecoRecall) && mode == CommanderAirCommandService.AirCommandMode.AirGuard))
            {
                airCommand.RetargetAiMission(aircraft, mode, centre, radius);
            }

            if (!isFighterOrPicket)
            {
                airCommand.SetAiCommitted(aircraft, inWave);
            }

            // The wave goes round its flank once, as it leaves; a wing pulled back drops whatever
            // of that route it had not flown, or it would fly the flank on its way home.
            if (flank.HasValue && withWave)
            {
                airCommand.SetAiRoute(aircraft, flank.Value);
            }
            else if (pulledBack && !isFighterOrPicket)
            {
                airCommand.ClearAiRoute(aircraft);
            }

            if (shipTarget != null && airCommand.TrySetAiForcedTarget(aircraft, shipTarget))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) sends {CommanderGameAccess.GetUnitLabel(aircraft)} "
                        + $"at the ship {CommanderGameAccess.GetUnitLabel(shipTarget)} (armour {shipTarget.definition.armorTier:0.#}, "
                        + $"its best weapon {GetBestArmorEffectiveness(aircraft):0.#}, {CommanderAirCommandService.GetModeLabel(mode)}).");
            }

            // One target at a time, re-picked each review once the last is destroyed. An Alkyon
            // left to the box search circled the player's base with eight bombs and dropped none.
            if (bomber
                && (salvoTarget ?? PickPriorityTarget(hq, localHq, waveTarget, aircraft)) is Unit priority
                && airCommand.TrySetAiForcedTarget(aircraft, priority))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) sends {CommanderGameAccess.GetUnitLabel(aircraft)} "
                        + $"at {CommanderGameAccess.GetUnitLabel(priority)} (armour {priority.definition.armorTier:0.#}, "
                        + $"its best weapon {GetBestArmorEffectiveness(aircraft):0.#}).");
            }
        }
    }

    /// <summary>
    /// Re-points each radar picket's station on the ten-second defence clock, so it backs away from
    /// anything of the player's that comes near instead of waiting half a minute for the next review.
    /// </summary>
    private void SteerPickets(FactionHQ hq, FactionHQ localHq)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || !IsDuelMission)
        {
            return;
        }

        GlobalPosition playerBase = GetStrikeTarget(localHq);
        int index = 0;
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (IsRadarPicket(aircraft.definition, hq))
            {
                airCommand.RetargetAiMission(
                    aircraft,
                    CommanderAirCommandService.AirCommandMode.AwacsJammer,
                    GetPicketStation(hq, playerBase, index++),
                    PicketStationRadiusMeters);
            }
        }
    }

    /// <summary>
    /// Where the enemy sends its strikes: the airbase the player started the mission holding.
    /// </summary>
    /// <remarks>
    /// The centre of the player's territory drifts into empty ground between bases. The opening base
    /// does not move, the enemy learns of it at the first review (see <c>RevealPlayerBase</c>), so it is
    /// remembered once and only re-picked if the player loses it.
    /// </remarks>
    private GlobalPosition GetStrikeTarget(FactionHQ localHq)
    {
        if (playerHomeBase == null
            || playerHomeBase.disabled
            || playerHomeBase.center == null
            || !localHq.ContainsAirbase(playerHomeBase))
        {
            playerHomeBase = null;
            foreach (Airbase airbase in localHq.GetAirbases())
            {
                if (airbase != null && !airbase.disabled && airbase.center != null)
                {
                    playerHomeBase = airbase;
                    break;
                }
            }
        }

        return playerHomeBase != null && playerHomeBase.center != null
            ? playerHomeBase.center.GlobalPosition()
            : CommanderCaptureService.GetTerritoryCenter(localHq);
    }

    private void TrackAircraft(Aircraft aircraft)
    {
        if (!airborneSince.ContainsKey(aircraft))
        {
            // The label is taken now: a destroyed airframe can no longer be asked for its name.
            airborneSince[aircraft] = (Time.time, $"{CommanderGameAccess.GetUnitLabel(aircraft)} #{aircraft.persistentID.Id}");
        }
    }

    /// <summary>
    /// Logs how long each enemy airframe lasted once it leaves the world, and whether it was lost
    /// or recovered.
    /// </summary>
    private void ReportLostAircraft(FactionHQ hq)
    {
        lostAircraft.Clear();
        foreach (KeyValuePair<Aircraft, (float Since, string Label)> entry in airborneSince)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                lostAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < lostAircraft.Count; i++)
        {
            Aircraft aircraft = lostAircraft[i];
            (float since, string label) = airborneSince[aircraft];

            // Named, so two identical losses are not read as one logged twice. A recovery into stock also
            // removes the airframe; unitState is a plain field, so it is still readable after destruction.
            bool recovered = aircraft.unitState == Unit.UnitState.Returned;
            if (!recovered && Time.time - since < QuickLossSeconds && aircraft.definition is AircraftDefinition type)
            {
                NoteQuickLoss(hq, type);
            }

            CommanderPlugin.Log.LogInfo(recovered
                ? $"Enemy commander ({hq.faction.name}) recovered {label} into stock after {Time.time - since:0} s in the air."
                : $"Enemy commander ({hq.faction.name}) lost {label} after {Time.time - since:0} s in the air.");
            airborneSince.Remove(aircraft);
        }

        lostAircraft.Clear();
    }

    /// <summary>
    /// True while an airframe type keeps dying within minutes of launch, so money is not wasted on it.
    /// A benched type is skipped until its losses age out.
    /// </summary>
    private bool IsBenched(AircraftDefinition definition)
    {
        if (!quickLosses.TryGetValue(definition, out List<float> times))
        {
            return false;
        }

        times.RemoveAll(time => Time.time - time > QuickLossWindowSeconds);
        return times.Count >= QuickLossesToBench;
    }

    private void NoteQuickLoss(FactionHQ hq, AircraftDefinition definition)
    {
        if (!quickLosses.TryGetValue(definition, out List<float> times))
        {
            times = new List<float>();
            quickLosses[definition] = times;
        }

        times.Add(Time.time);
        if (IsBenched(definition) && times.Count == QuickLossesToBench)
        {
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) stops buying {definition.unitName} for a while: "
                    + $"{QuickLossesToBench} lost within {QuickLossSeconds:0} s of launch.");
        }
    }
}
