using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Expansion: taking airbases the faction does not hold. Owns the capture-target list both
/// commanders read, the player's capture order, and the enemy commander's decision to go and take
/// a base.
/// </summary>
/// <remarks>
/// The game's capture rule is simply "have a unit with <c>CaptureStrength</c> inside the airbase's
/// <c>CaptureRange</c>" (<c>Capture.GetInRangeUnits</c>), so a capture order is a move order into
/// the middle of the ring and nothing more — no new networking and no new state on the units. What
/// was missing was that nobody ever issued it: the player could not see which bases were takeable,
/// and the enemy commander bought units but never told them to go anywhere.
/// <para>
/// This is the one place in the mod that issues an order for a faction that is not the player's,
/// and it passes <c>playerCommand: false</c> so the Basegame treats it as the AI moving itself.
/// </para>
/// <para>
/// Capture is carried by ground vehicles, which cross the map slowly. The fast version is
/// an air assault — landing a troop-carrying helicopter inside the ring — and the machinery for it
/// already exists in <see cref="CommanderSupplyHeliService"/>, which flies a helicopter to a chosen
/// point and lands it there. It is deliberately not wired up here because it stacks three engine
/// behaviours that cannot be verified without running the game: whether a troop mount reads as a
/// cargo station, whether <c>AIHeloTransportState</c> can be forced on an aircraft that did not
/// pick it, and whether troops landed inside a ring register as capture strength. Upgrade once
/// ground capture is confirmed in play.
/// </para>
/// </remarks>
internal sealed class CommanderCaptureService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>How often the takeable-base list is rebuilt. Bases change hands slowly.</summary>
    private const float TargetRefreshSeconds = 5f;

    /// <summary>How often each enemy commander reconsiders its expansion.</summary>
    private const float EnemyReviewSeconds = 20f;

    /// <summary>How far from its own territory the enemy will look for a base worth taking.</summary>
    private const float EnemyReachMeters = 40000f;

    /// <summary>Units the enemy commits to one capture, per base it stages from. Enough to survive
    /// a picket, not an army - but a commander holding seven bases runs seven of them.</summary>
    private const int EnemySquadSize = 3;

    /// <summary>Capture squad size per staging base when the target is a base somebody holds.</summary>
    private const int EnemyAssaultSquadSize = 6;

    /// <summary>
    /// Fewest pushes a commander runs at once, however few bases it holds. One push per base held
    /// meant the duel's enemy, with one base, sent three vehicles at one neutral field for the whole
    /// match and never took anything; the spare pushes stage from the same base and, because a base
    /// another push is taking is passed over, each goes for a different field.
    /// </summary>
    private const int MinEnemyDrives = 3;

    /// <summary>How far short of a defended base a squad forms up before it goes in.</summary>
    private const float FormUpMeters = 5000f;

    /// <summary>How close to the form-up point counts as having arrived there.</summary>
    private const float FormUpRadiusMeters = 700f;

    /// <summary>How far to either side of the main form-up point the flanking lanes form up. One
    /// form-up point on the straight line home put the whole army on one road, nose to tail.</summary>
    private const float FlankMeters = 3000f;

    /// <summary>Of every this many vehicles, one forms up on the left flank and one on the right.</summary>
    private const int ApproachLanes = 4;

    /// <summary>Smallest wave sent at a defended base, unless that is the whole squad.</summary>
    private const int AssaultWaveSize = 6;

    /// <summary>A wave that never fills (a straggler stuck on a hill) still goes after this long.</summary>
    private const float FormUpTimeoutSeconds = 240f;

    /// <summary>The CHALLENGING commander's one massed push: its key in <see cref="drives"/>, the
    /// fewest vehicles it goes in with, how many it wants per defender standing on the objective,
    /// and how long it waits for that before going in with what it has.</summary>
    private const int MainEffortSlot = -1;
    private const int MainEffortMinWave = 12;
    private const float MainEffortOdds = 1.5f;

    // Short enough that a push still goes when the army can never reach the full wave size.
    private const float MainEffortTimeoutSeconds = 300f;

    /// <summary>After this long the massed push goes whatever the odds: a form-up point nothing can
    /// reach, or a wave size the army never grows to.</summary>
    private const float MainEffortGiveUpSeconds = 600f;

    /// <summary>The massed push gathers this far out of its own base, toward the objective: under its
    /// own air defence and out of range of the guns covering the objective.</summary>
    private const float RallyMeters = 2500f;
    private const float RallyRadiusMeters = 900f;

    /// <summary>
    /// A squad waits as several knots rather than one: up to <see cref="MaxSpreadGroups"/> groups of
    /// about <see cref="VehiclesPerSpreadGroup"/>, one on the point and the rest on a ring
    /// <see cref="SpreadGroupMeters"/> out, each vehicle on its own spot within
    /// <see cref="SpreadGroupRadiusMeters"/> of its group's centre. Every vehicle pinned to the one
    /// shared point piled a Champions push of 100 into a single knot that jammed the pathfinder and
    /// fed artillery, and one 250 m disc of them was still one target. The farthest spot (550 m)
    /// stays inside both arrival radii, so a spread vehicle still counts as gathered.
    /// </summary>
    private const int MaxSpreadGroups = 7;
    private const int VehiclesPerSpreadGroup = 8;
    private const float SpreadGroupMeters = 450f;
    private const float SpreadGroupRadiusMeters = 100f;
    private const int SpreadSlots = 16;

    /// <summary>Share of a marching wave that has to reach the form-up point before it goes in, and
    /// how long the first arrivals wait there for the rest.</summary>
    private const float RegroupShare = 0.7f;
    private const float RegroupTimeoutSeconds = 900f;

    /// <summary>Slack around a capture ring within which an order counts as a capture order.</summary>
    private const float OrderSlackMeters = 500f;

    /// <summary>How close to a runway edge, hangar or pad of your own an aircraft order has to land to
    /// mean "land here and resupply" rather than "fly here".</summary>
    private const float OnFieldMeters = 80f;

    /// <summary>
    /// How close to a runway edge, hangar or pad an order has to land to mean "go and take this
    /// base" when only aircraft are selected. Wider than <see cref="OnFieldMeters"/> because the
    /// base is aimed at rather than a single landing spot, and far tighter than the capture ring.
    /// </summary>
    private const float AirCaptureFieldMeters = 400f;

    /// <summary>
    /// Clear space a capture squad's hold point needs. Roughly a couple of vehicle lengths, which
    /// is enough to keep the point off a terminal wall without demanding a parade ground.
    /// </summary>
    private const float HoldClearanceMeters = 25f;

    /// <summary>
    /// How close one of your units has to get before a base counts as found. An airfield is a big
    /// thing seen from the air and a small thing seen from a road, hence two ranges rather than one.
    /// </summary>
    private const float AirDiscoveryMeters = 12000f;
    private const float GroundDiscoveryMeters = 4000f;

    private const float LosingWarnGrip = 0.95f;

    /// <summary>How long after its grip last fell a base still counts as being captured.</summary>
    private const float ContestedSeconds = 30f;

    /// <summary>A base this far from an enemy base by road can stage attack convoys. Team Duel's
    /// forward bases are 40-61 km from the enemy home and the two homes 99 km apart.</summary>
    private const float AttackRoadMeters = 70000f;

    /// <summary>Road distance over straight line, about right on Team Duel's map, for a pair not searched yet.</summary>
    private const float RoadWindingEstimate = 1.4f;

    /// <summary>Compass points probed for a hold point clear of the airbase's own buildings.</summary>
    private static readonly Vector2[] HoldProbeDirections =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        new(0.7071f, 0.7071f), new(-0.7071f, 0.7071f), new(0.7071f, -0.7071f), new(-0.7071f, -0.7071f),
    };

    /// <summary>Hold points already worked out, one per airbase. An airfield does not move.</summary>
    private static readonly Dictionary<Airbase, GlobalPosition> HoldPoints = new();

    /// <summary>Last grip seen on each held base and when it last fell, for <see cref="IsBeingCaptured"/>.</summary>
    private static readonly Dictionary<Airbase, (float Grip, float FellAt)> GripHistory = new();

    /// <summary>Metres of road between two bases, see <see cref="GetRoadMeters"/>. Roads do not
    /// change during a match and one search walks and re-sorts every node on the map.</summary>
    private static readonly Dictionary<(Airbase From, Airbase To), float> RoadMeters = new();

    private static readonly List<RoadPathfinding.Node> RoadNodes = new();

    /// <summary>Generous on purpose: a full buffer reads as clear ground, and an airbase centre
    /// is exactly where colliders are dense.</summary>
    private static readonly Collider[] HoldProbeHits = new Collider[64];

    private static int roadSearchFrame = -1;

    private readonly CommanderSelectionService selectionService;

    private readonly List<CaptureTarget> targets = new();

    /// <summary>Drives keyed by the base they stage from and a slot number, so a commander with
    /// few bases can still run <see cref="MinEnemyDrives"/> of them.</summary>
    private readonly Dictionary<(Airbase Staging, int Slot), EnemyDrive> drives = new();

    private readonly List<(Airbase Staging, int Slot)> staleDrives = new();
    private readonly List<Airbase> stagingBases = new();

    /// <summary>Captures already logged this review; several drives can be aimed at one base.</summary>
    private readonly HashSet<Airbase> announcedCaptures = new();
    private readonly List<Unit> squad = new();
    private readonly List<float> squadDistances = new();

    /// <summary>Units already taken by a drive this review, so two fronts do not send the same
    /// three vehicles in opposite directions.</summary>
    private readonly HashSet<Unit> claimed = new();

    private readonly List<VehicleDefinition> rosterProbe = new();

    /// <summary>
    /// Which base each unit was last sent to take. This is what makes a capture order
    /// redirectable: the capture snap reaches a whole ring plus slack, so once a squad is standing
    /// on a base every order the player gives it nearby snapped straight back to the same hold
    /// point and the units looked stuck. A unit already sent to that base is not re-snapped, so
    /// the second order is taken literally and drives it off.
    /// </summary>
    private readonly Dictionary<Unit, Airbase> assignments = new();

    private readonly List<Unit> staleAssignments = new();

    /// <summary>
    /// Bases the player has found, and the reason capture targets are not simply "every capturable
    /// airbase on the map". Sticky for the whole mission: an airfield does not move, so once you
    /// have seen one it stays on your map, the same way a spotted neutral contact does.
    /// </summary>
    private readonly HashSet<Airbase> discovered = new();

    /// <summary>The player's bases a "being captured" warning has been raised for, until their
    /// grip is back to full.</summary>
    private readonly HashSet<Airbase> warnedLosing = new();

    private float nextTargetRefreshAt;
    private float nextEnemyReviewAt;
    private float nextGripSampleAt;
    private bool loggedRoster;

    internal CommanderCaptureService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal static CommanderCaptureService? Instance { get; private set; }

    /// <summary>Bases the local faction could take, empty ones first then nearest. </summary>
    internal IReadOnlyList<CaptureTarget> Targets => targets;

    internal string StatusText { get; private set; } = string.Empty;

    public void ResetSession()
    {
        RoadMeters.Clear();
        targets.Clear();
        drives.Clear();
        staleDrives.Clear();
        squad.Clear();
        squadDistances.Clear();
        claimed.Clear();
        discovered.Clear();
        warnedLosing.Clear();
        assignments.Clear();
        staleAssignments.Clear();
        HoldPoints.Clear();
        GripHistory.Clear();
        nextGripSampleAt = 0f;
        loggedRoster = false;
        StatusText = string.Empty;
        nextTargetRefreshAt = 0f;
        nextEnemyReviewAt = 0f;
    }

    public void TickPersistent()
    {
        // Every frame, not on the 5 s refresh: a large squad drains a base in a couple of seconds.
        WarnLosingBases();
        if (CommanderScheduler.IsDue(ref nextGripSampleAt, 1f))
        {
            SampleGrips();
        }

        if (CommanderScheduler.IsDue(ref nextTargetRefreshAt, TargetRefreshSeconds))
        {
            RefreshTargets();
        }

        if (CommanderEnemyCommanderService.EffectiveMode != CommanderEnemyCommanderService.ModeOff
            && CommanderScheduler.IsDueHeavy(ref nextEnemyReviewAt, EnemyReviewSeconds))
        {
            if (CommanderEnemyCommanderService.LocalSideRotates)
            {
                // Both sides every time. The stand-in swaps every 30 s and this runs every 20 s, so
                // one side was getting two reviews in three.
                foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
                {
                    if (hq != null && hq.faction != null)
                    {
                        CommanderNetwork.RunAs(hq, ReviewEnemies);
                    }
                }
            }
            else
            {
                CommanderNetwork.RunAsHostTeam(ReviewEnemies);
            }
        }
    }

    /// <summary>
    /// True while somebody is draining the owner's grip on this base, or did within the last
    /// <see cref="ContestedSeconds"/>. Nothing may be built there meanwhile.
    /// </summary>
    internal static bool IsBeingCaptured(Airbase? airbase)
    {
        return airbase != null
            && GripHistory.TryGetValue(airbase, out (float Grip, float FellAt) entry)
            && entry.FellAt > 0f
            && Time.time - entry.FellAt < ContestedSeconds;
    }

    /// <summary>
    /// The airbase an order point lands on that this faction already holds. Ground units have no use
    /// for one, but for an aircraft an order dropped on a field you own is a request to land and rearm.
    /// </summary>
    /// <remarks>
    /// "On the field" means on a runway or beside a hangar or landing pad, not anywhere in the capture
    /// ring, so helicopters can still be moved around near home.
    /// </remarks>
    internal static bool TryResolveOwnedAirfield(GlobalPosition point, FactionHQ? hq, out Airbase airbase)
    {
        airbase = null!;
        if (hq == null)
        {
            return false;
        }

        float bestDistance = OnFieldMeters;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (candidate == null || candidate.disabled)
            {
                continue;
            }

            float distance = DistanceToField(candidate, point);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                airbase = candidate;
            }
        }

        return airbase != null;
    }

    /// <summary>A unit that contributes to a capture: it has capture strength and can be ordered.</summary>
    internal static bool CanCapture(Unit? unit)
    {
        return unit != null
            && !unit.disabled
            && unit.CaptureStrength > 0f
            && CommanderGameAccess.GetUnitCommand(unit) != null;
    }

    /// <summary>
    /// Where a faction "is": the mean of the airbases it holds. Capture distances are measured from
    /// there, so "nearest" means nearest to its territory rather than to whichever unit happens to
    /// have wandered furthest from home.
    /// </summary>
    internal static GlobalPosition GetTerritoryCenter(FactionHQ hq)
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                sum += airbase.center.GlobalPosition().AsVector3();
                count++;
            }
        }

        return count == 0 ? default : new GlobalPosition(sum.x / count, sum.y / count, sum.z / count);
    }

    /// <summary>
    /// Where a unit <paramref name="distance"/> away belongs in a nearest-first list already
    /// holding <paramref name="distances"/>, or -1 when the squad is full of nearer ones. Split out
    /// of <see cref="CollectCaptureSquad"/> so <see cref="SelfCheck"/> can run it: units cannot be
    /// made up in a test, distances can.
    /// </summary>
    internal static int NearestInsertIndex(List<float> distances, float distance, int limit)
    {
        int at = distances.Count;
        while (at > 0 && distances[at - 1] > distance)
        {
            at--;
        }

        return at >= limit ? -1 : at;
    }

    /// <summary>
    /// One runnable check for the expansion priority, run from <see cref="CommanderPlugin"/> at
    /// load next to the other self-checks, because a Unity plugin has nowhere else to run one.
    /// The rule that matters is that an empty base anywhere in reach beats a defended one next
    /// door — invert it and the enemy commander throws its capture squad at the player's airbase
    /// instead of walking onto a free one.
    /// </summary>
    internal static void SelfCheck()
    {
        Expect("far empty beats near held", ScoreTarget(false, EnemyReachMeters) < ScoreTarget(true, 0f));
        Expect("nearest empty wins", ScoreTarget(false, 1000f) < ScoreTarget(false, 2000f));
        Expect("nearest held wins", ScoreTarget(true, 1000f) < ScoreTarget(true, 2000f));

        // A squad is the units nearest the base it stages from, so the order it is built in is what
        // decides which vehicles join which front.
        List<float> probe = new() { 100f, 300f };
        Expect("nearer unit goes first", NearestInsertIndex(probe, 50f, EnemySquadSize) == 0);
        Expect("further unit goes last", NearestInsertIndex(probe, 500f, EnemySquadSize) == 2);
        Expect("a full squad turns a distant unit away", NearestInsertIndex(new List<float> { 1f, 2f, 3f }, 4f, 3) < 0);
        Expect("a full squad takes a nearer unit", NearestInsertIndex(new List<float> { 1f, 2f, 3f }, 2.5f, 3) == 2);
    }

    /// <summary>The owner's hold on a base it holds, 1 = secure, 0 = lost.</summary>
    internal static float GetOwnerGrip(Airbase? airbase)
    {
        Capture? capture = airbase == null || airbase.disabled ? null : airbase.capture;
        return capture == null ? 1f : capture.controlBalance;
    }

    internal static string GetAirbaseLabel(Airbase? airbase)
    {
        string? name = airbase?.SavedAirbase?.DisplayName;
        return string.IsNullOrEmpty(name) ? airbase?.name ?? "airbase" : name!;
    }

    /// <summary>
    /// Whether this commander holds a base close enough by road to attack from. A convoy from a distant
    /// home base is a long drive into the enemy's air force, so until then it takes neutral bases and
    /// builds its economy. With no neutral base left it attacks from where it stands.
    /// </summary>
    internal static bool HasForwardBase(FactionHQ hq)
    {
        bool neutralLeft = false;
        foreach (KeyValuePair<string, Airbase> own in FactionRegistry.airbaseLookup)
        {
            neutralLeft |= own.Value != null && IsTakeable(own.Value, hq) && own.Value.CurrentHQ == null;
            if (own.Value == null || !ReferenceEquals(own.Value.CurrentHQ, hq) || own.Value.center == null)
            {
                continue;
            }

            foreach (KeyValuePair<string, Airbase> other in FactionRegistry.airbaseLookup)
            {
                if (IsTakeable(other.Value, hq)
                    && other.Value.CurrentHQ != null
                    && GetRoadMeters(own.Value, other.Value) <= AttackRoadMeters
                    && CommanderEnemyCommanderService.CanDriveBetween(own.Value.center.GlobalPosition(), other.Value.center.GlobalPosition()))
                {
                    return true;
                }
            }
        }

        return !neutralLeft;
    }

    /// <summary>The hold point of the enemy base nearest <paramref name="from"/> that a vehicle can drive to.</summary>
    internal static bool TryGetNearestEnemyBase(FactionHQ hq, GlobalPosition from, out GlobalPosition hold)
    {
        Airbase? best = null;
        float nearest = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (IsTakeable(airbase, hq) && airbase.CurrentHQ != null)
            {
                GlobalPosition at = airbase.center.GlobalPosition();
                float distance = FastMath.Distance(from, at);
                if (distance < nearest && CommanderEnemyCommanderService.CanDriveBetween(from, at))
                {
                    nearest = distance;
                    best = airbase;
                }
            }
        }

        hold = best != null ? GetHoldPoint(best) : default;
        return best != null;
    }

    /// <summary>
    /// Where a capture squad is sent to stand. Deliberately not the airbase centre transform: on a
    /// real airfield that sits on the terminal or the tower, so a squad ordered there drove into
    /// the wall, reversed off it and drove in again forever. Any point inside <c>CaptureRange</c>
    /// captures — <c>Capture.GetInRangeUnits</c> is a plain radius test — so the squad goes to the
    /// first spot on a ring at half that radius which no building occupies. Cached per airbase:
    /// the airfield does not move, and open ground stays open.
    /// </summary>
    internal static GlobalPosition GetHoldPoint(Airbase airbase)
    {
        if (HoldPoints.TryGetValue(airbase, out GlobalPosition cached))
        {
            return cached;
        }

        GlobalPosition center = airbase.center.GlobalPosition();
        GlobalPosition point = center;
        if (IsBuiltOver(center))
        {
            float reach = Mathf.Max(airbase.SavedAirbase?.CaptureRange ?? 0f, 200f) * 0.5f;
            for (int i = 0; i < HoldProbeDirections.Length; i++)
            {
                GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                    center.x + (HoldProbeDirections[i].x * reach),
                    center.y,
                    center.z + (HoldProbeDirections[i].y * reach)));

                // SnapToTerrain returns the seabed over water, so this also keeps the squad
                // from being sent for a swim at a coastal airfield.
                if (!CommanderGameAccess.IsBelowSeaLevel(candidate) && !IsBuiltOver(candidate))
                {
                    point = candidate;
                    break;
                }
            }
        }

        HoldPoints[airbase] = point;
        return point;
    }

    /// <summary>
    /// True when an order point lands on a base this faction could take, rewriting the point to the
    /// middle of its capture ring. This is what makes a capture order just an order: right-click a
    /// base in the 3D view or on the map — on its own or as the last of a string of travel points —
    /// and the units go and stand in the ring instead of stopping wherever the cursor happened to
    /// be. The slack is deliberate: a base on a zoomed-out map is a few pixels wide.
    /// </summary>
    internal bool TryResolveCaptureOrder(ref GlobalPosition point, out CaptureTarget target)
    {
        target = default;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        Airbase? best = null;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (!IsTakeable(airbase, localHq))
            {
                continue;
            }

            // Horizontal only: a map coordinate carries no useful height.
            GlobalPosition center = airbase.center.GlobalPosition();
            float dx = point.x - center.x;
            float dz = point.z - center.z;
            float distance = Mathf.Sqrt((dx * dx) + (dz * dz));
            if (!discovered.Contains(airbase))
            {
                continue;
            }

            if (distance <= airbase.SavedAirbase.CaptureRange + OrderSlackMeters && distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        if (best == null)
        {
            ForgetSelectionAssignments();
            return false;
        }

        // A ground squad captures by standing anywhere in the ring, so the whole ring plus slack is
        // the right snap for it. An aircraft's half of a capture is a land-and-hold order, and the
        // ring is about two kilometres wide, so with only aircraft selected every order near a base
        // became a landing there — including an attack order on the hostile units parked in it,
        // which is what the player is usually clicking on. Same rule the resupply landing already
        // uses: the click has to be on the field itself.
        if (IsSelectionAircraftOnly() && DistanceToField(best, point) > AirCaptureFieldMeters)
        {
            ForgetSelectionAssignments();
            return false;
        }

        // Already sent there: this click is the player moving the squad off the base, not another
        // capture order. Snapping it back to the ring is what made captured units unorderable.
        if (IsSelectionAlreadySentTo(best))
        {
            ForgetSelectionAssignments();
            CommanderAlertService.Instance?.NotifyCaptureOrder(
                $"LEAVING {GetAirbaseLabel(best).ToUpperInvariant()}", selectionService.SelectedUnits[0]);
            return false;
        }

        target = new CaptureTarget(best, bestDistance);
        point = target.HoldPoint;
        return true;
    }

    /// <summary>
    /// Announces a capture order and warns when the selection cannot actually move the bar. It
    /// warns rather than refuses on purpose: which units carry troops is not obvious, and an order
    /// that silently does nothing is exactly how the first version of this looked broken.
    /// </summary>
    internal void AnnounceCaptureOrder(CaptureTarget target, Unit? anchor)
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] != null && target.Airbase != null)
            {
                assignments[selection[i]] = target.Airbase;
            }
        }

        int capable = CountCapableSelected();
        int selected = selection.Count;
        string label = target.Label.ToUpperInvariant();
        string text = capable > 0
            ? $"CAPTURING {label} ({capable} OF {selected} CAN TAKE GROUND)"
            : $"{label}: NOTHING SELECTED CARRIES TROOPS";
        StatusText = capable > 0
            ? $"{capable} of {selected} selected unit(s) can capture {target.Label}."
            : $"Ordered to {target.Label}, but nothing selected has capture strength — "
                + "send a troop carrier to actually take it.";
        CommanderAlertService.Instance?.NotifyCaptureOrder(text, anchor);
    }

    /// <summary>True while any unit carries the player's capture order on this base.</summary>
    internal bool HasCaptureOrder(Airbase airbase)
    {
        foreach (Airbase assigned in assignments.Values)
        {
            if (ReferenceEquals(assigned, airbase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How many of the selected units would actually move the capture bar.</summary>
    internal int CountCapableSelected()
    {
        int count = 0;
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (CanCapture(selection[i]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Every found base the local faction could take, for the map and the 3D view.</summary>
    internal void CopyDiscoveredTargets(List<CaptureTarget> buffer)
    {
        buffer.Clear();
        for (int i = 0; i < targets.Count; i++)
        {
            buffer.Add(targets[i]);
        }
    }

    /// <summary>
    /// True when this commander wants to expand but owns nothing that can take ground, which is
    /// what tells <see cref="CommanderEnemyCommanderService"/> to buy a capture unit ahead of its
    /// plan. Without this the enemy can sit on a full pot next to an empty airbase forever.
    /// </summary>
    /// <summary>
    /// True only when none of this commander's pushes has a squad. With one push per base held, "any
    /// push is short" was true every review: a bought unit rolls out of whichever depot the base game
    /// picks and is claimed by a nearer push, so the short one stayed short and the plan was
    /// overridden for the whole match.
    /// </summary>
    internal bool WantsCaptureUnit(FactionHQ hq)
    {
        bool any = false;
        foreach (KeyValuePair<(Airbase Staging, int Slot), EnemyDrive> entry in drives)
        {
            if (entry.Key.Staging == null || !ReferenceEquals(entry.Key.Staging.CurrentHQ, hq))
            {
                continue;
            }

            if (!entry.Value.WantsCaptureUnit)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>True when an enemy push has already sent this unit at a base. The home guard asks,
    /// so it does not drag an attacker back down the road it just drove up.</summary>
    internal bool IsOnEnemyDrive(Unit unit)
    {
        foreach (EnemyDrive drive in drives.Values)
        {
            if (drive.Launched.Contains(unit) || drive.Committed.Contains(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Pushes this commander has on the road, i.e. with a target and a squad.</summary>
    internal int CountConvoys(FactionHQ hq)
    {
        int count = 0;
        foreach (EnemyDrive drive in drives.Values)
        {
            if (ReferenceEquals(drive.Owner, hq) && drive.Target != null && drive.Committed.Count > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Horizontal distance from a point to the nearest runway edge, hangar or pad.</summary>
    private static float DistanceToField(Airbase airbase, GlobalPosition point)
    {
        float best = float.MaxValue;
        Airbase.Runway[]? runways = airbase.runways;
        for (int i = 0; runways != null && i < runways.Length; i++)
        {
            Airbase.Runway runway = runways[i];
            if (runway?.Start == null || runway.End == null)
            {
                continue;
            }

            float toCentreline = Mathf.Sqrt(CommanderBuildPreview.SegmentDistanceSquared(
                point,
                runway.Start.position.ToGlobalPosition(),
                runway.End.position.ToGlobalPosition()));
            best = Mathf.Min(best, toCentreline - (runway.GetWidth() * 0.5f));
        }

        foreach (Hangar hangar in airbase.hangars)
        {
            if (hangar == null)
            {
                continue;
            }

            GlobalPosition at = hangar.transform.position.ToGlobalPosition();
            float dx = point.x - at.x;
            float dz = point.z - at.z;
            best = Mathf.Min(best, Mathf.Sqrt((dx * dx) + (dz * dz)));
        }

        return best;
    }

    private static bool IsTakeable(Airbase? airbase, FactionHQ hq)
    {
        return airbase != null
            && !airbase.disabled
            && airbase.center != null
            && airbase.SavedAirbase != null
            && airbase.SavedAirbase.Capturable
            && !ReferenceEquals(airbase.CurrentHQ, hq);
    }

    /// <summary>Lifts the hold-point pin off every unit a drive had committed.</summary>
    private static void ReleaseDrive(EnemyDrive drive)
    {
        for (int i = 0; i < drive.Committed.Count; i++)
        {
            CommanderEnemyCommanderService.ReleaseCommandedDestination(drive.Committed[i]);
        }

        drive.Committed.Clear();
        drive.Launched.Clear();
        drive.Marching.Clear();
        drive.FormingSince = 0f;
    }

    /// <summary>
    /// A point <see cref="FormUpMeters"/> short of the hold point, on the line back to the staging
    /// base. A staging base already that close needs no form-up: the squad gathers there anyway.
    /// </summary>
    private static bool TryGetFormUpPoint(Airbase staging, GlobalPosition hold, out GlobalPosition formUp)
    {
        formUp = hold;
        GlobalPosition from = staging.center.GlobalPosition();
        float distance = FastMath.Distance(from, hold);
        float back = Mathf.Min(FormUpMeters, distance * 0.6f);
        if (back < FormUpRadiusMeters * 2f)
        {
            return false;
        }

        // Back toward the staging base until it is dry. A form-up point in a bay used to mean no
        // gathering at all, and every vehicle drove in alone as it was bought.
        for (; back < distance - FormUpRadiusMeters; back += FormUpRadiusMeters)
        {
            float t = back / distance;
            GlobalPosition point = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                hold.x + ((from.x - hold.x) * t),
                hold.y,
                hold.z + ((from.z - hold.z) * t)));
            if (!CommanderGameAccess.IsBelowSeaLevel(point))
            {
                formUp = point;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// This vehicle's own form-up point: the main one for most of the squad, or one
    /// <see cref="FlankMeters"/> to either side, so the pathfinder sends the flanks down other roads.
    /// Keyed on the persistent id so a vehicle keeps its lane and is not re-ordered every review.
    /// </summary>
    private static GlobalPosition GetLaneFormUp(GlobalPosition formUp, GlobalPosition hold, Unit unit)
    {
        int lane = (int)((unit.persistentID.Id / 7u) % ApproachLanes);
        if (lane < 2)
        {
            return formUp;
        }

        float dx = formUp.x - hold.x;
        float dz = formUp.z - hold.z;
        float length = Mathf.Sqrt((dx * dx) + (dz * dz));
        if (length < 1f)
        {
            return formUp;
        }

        // Never wider than the approach is long, or a short approach turns into a sideways drive.
        float side = (lane == 2 ? 1f : -1f) * Mathf.Min(FlankMeters, length) / length;
        GlobalPosition flank = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            formUp.x - (dz * side),
            formUp.y,
            formUp.z + (dx * side)));
        return CommanderGameAccess.IsBelowSeaLevel(flank) ? formUp : flank;
    }

    /// <summary>The game keeps no "under attack since" for a ring, so this notes when each grip last fell.</summary>
    private static void SampleGrips()
    {
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.CurrentHQ == null)
            {
                continue;
            }

            float grip = GetOwnerGrip(airbase);
            float fellAt = 0f;
            if (GripHistory.TryGetValue(airbase, out (float Grip, float FellAt) last))
            {
                fellAt = grip < last.Grip - 0.0001f ? Time.time : last.FellAt;
            }

            GripHistory[airbase] = (grip, fellAt);
        }
    }

    /// <summary>
    /// Sends the marching wave in from the form-up point once <see cref="RegroupShare"/> of it has
    /// closed up there, so fast carriers do not arrive minutes ahead of the tanks. What has closed up
    /// goes on its own once it is <paramref name="required"/> strong, without waiting for later groups.
    /// </summary>
    private static int LaunchMarchers(EnemyDrive drive, GlobalPosition formUp, GlobalPosition hold, int required)
    {
        if (drive.Marching.Count == 0)
        {
            drive.MarchSince = 0f;
            return 0;
        }

        int arrived = 0;
        foreach (Unit unit in drive.Marching)
        {
            if (unit != null && FastMath.InRange(unit.transform.GlobalPosition(), GetLaneFormUp(formUp, hold, unit), FormUpRadiusMeters))
            {
                arrived++;
            }
        }

        bool closedUp = arrived >= Mathf.CeilToInt(drive.Marching.Count * RegroupShare)
            || Time.time - drive.MarchSince >= RegroupTimeoutSeconds;
        if (!closedUp && arrived < required)
        {
            return 0;
        }

        if (closedUp)
        {
            int all = drive.Marching.Count;
            drive.Launched.UnionWith(drive.Marching);
            drive.Marching.Clear();
            drive.MarchSince = 0f;
            return all;
        }

        int sent = drive.Marching.RemoveWhere(unit =>
        {
            bool there = unit != null && FastMath.InRange(unit.transform.GlobalPosition(), GetLaneFormUp(formUp, hold, unit), FormUpRadiusMeters);
            if (there)
            {
                drive.Launched.Add(unit!);
            }

            return there;
        });
        drive.MarchSince = Time.time;
        return sent;
    }

    /// <summary>Where the massed push gathers: <see cref="RallyMeters"/> out of its staging base on
    /// the line to the objective, or the base itself where that spot is under water.</summary>
    private static GlobalPosition GetRallyPoint(Airbase staging, GlobalPosition hold)
    {
        GlobalPosition from = staging.center.GlobalPosition();
        float distance = Mathf.Max(1f, FastMath.Distance(from, hold));
        float t = Mathf.Min(RallyMeters, distance * 0.3f) / distance;
        GlobalPosition rally = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            from.x + ((hold.x - from.x) * t),
            from.y,
            from.z + ((hold.z - from.z) * t)));
        return CommanderGameAccess.IsBelowSeaLevel(rally) ? GetHoldPoint(staging) : rally;
    }

    /// <summary>
    /// This vehicle's own spot round <paramref name="point"/>: its group (see <see cref="MaxSpreadGroups"/>),
    /// then a sunflower spiral inside the group so the spots fill it evenly. Keyed on the persistent
    /// id, not the squad index, so the spot only moves when the squad crosses a group boundary and
    /// <c>PinTo</c>'s already-ordered check holds the rest of the time. <paramref name="scale"/>
    /// shrinks the whole pattern to fit a capture ring.
    /// </summary>
    private static GlobalPosition SpreadAround(GlobalPosition point, Unit unit, int squadSize, float scale = 1f)
    {
        uint id = unit.persistentID.Id;
        int groups = Mathf.Clamp(Mathf.CeilToInt(squadSize / (float)VehiclesPerSpreadGroup), 1, MaxSpreadGroups);
        int group = (int)(id % (uint)groups);
        int slot = (int)((id / (uint)groups) % SpreadSlots);

        // Group 0 sits on the point; the others share the ring, turned by the group count so a
        // two-group squad is not lopsided.
        float centreAngle = group == 0 ? 0f : (group - 1) * (2f * Mathf.PI / Mathf.Max(1, groups - 1));
        float centreDistance = group == 0 ? 0f : SpreadGroupMeters * scale;
        float distance = SpreadGroupRadiusMeters * scale * Mathf.Sqrt((slot + 0.5f) / SpreadSlots);
        float angle = slot * 2.39996f;
        GlobalPosition spot = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            point.x + (Mathf.Cos(centreAngle) * centreDistance) + (Mathf.Cos(angle) * distance),
            point.y,
            point.z + (Mathf.Sin(centreAngle) * centreDistance) + (Mathf.Sin(angle) * distance)));
        return CommanderGameAccess.IsBelowSeaLevel(spot) ? point : spot;
    }

    /// <summary>Every fighting ground vehicle the faction has on the field.</summary>
    private static int CountFightingVehicles(FactionHQ? hq)
    {
        int count = 0;
        if (hq != null && hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit) && CommanderEnemyCommanderService.IsFightingVehicle(unit))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Fighting ground vehicles of the base's owner standing on or beside its ring.</summary>
    private static int CountDefenders(Airbase target, GlobalPosition hold, float ring)
    {
        FactionHQ? owner = target.CurrentHQ;
        if (owner == null || owner.factionUnits == null)
        {
            return 0;
        }

        float reach = Mathf.Max(ring * 1.5f, 2500f);
        int count = 0;
        foreach (PersistentID id in owner.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && CommanderEnemyCommanderService.IsFightingVehicle(unit)
                && FastMath.InRange(unit.transform.GlobalPosition(), hold, reach))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Lower is better. A base somebody already holds is still worth taking, but only once the
    /// empty ones are gone — hence a whole reach added to its score rather than a separate pass.
    /// </summary>
    private static float ScoreTarget(bool held, float distance)
    {
        // The penalty has to be strictly larger than the reach, or an empty base at the very edge
        // of reach only ties with a held base underfoot instead of beating it.
        return held ? distance + (EnemyReachMeters * 2f) : distance;
    }

    /// <summary>
    /// How far a convoy drives between two bases: the road route, base to road included. With no route,
    /// the straight line, because off a road the game drives straight at the destination.
    /// </summary>
    private static float GetRoadMeters(Airbase from, Airbase to)
    {
        if (RoadMeters.TryGetValue((from, to), out float meters))
        {
            return meters;
        }

        GlobalPosition start = from.center.GlobalPosition();
        GlobalPosition end = to.center.GlobalPosition();
        meters = FastMath.Distance(start, end);

        // One search a frame, about 40 ms each. The rest answer with an estimate until their turn.
        if (roadSearchFrame == Time.frameCount)
        {
            return meters * RoadWindingEstimate;
        }

        roadSearchFrame = Time.frameCount;
        RoadPathfinding.RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        RoadPathfinder.PathfindResult result = RoadPathfinder.PathfindResult.NoNetwork;
        if (network != null)
        {
            RoadPathfinder.TryPathfind(network, start, end, RoadNodes, out result);
        }

        if (result == RoadPathfinder.PathfindResult.Success && RoadNodes.Count > 0)
        {
            meters = FastMath.Distance(start, RoadNodes[0].position)
                + FastMath.Distance(RoadNodes[RoadNodes.Count - 1].position, end);
            for (int i = 0; i < RoadNodes.Count - 1; i++)
            {
                foreach (KeyValuePair<RoadPathfinding.Road, RoadPathfinding.Node> link in RoadNodes[i].connectionsLookup)
                {
                    if (ReferenceEquals(link.Value, RoadNodes[i + 1]))
                    {
                        meters += link.Key.length;
                        break;
                    }
                }
            }
        }

        RoadMeters[(from, to)] = meters;
        CommanderPlugin.Log.LogInfo(
            $"Road distance {GetAirbaseLabel(from)} to {GetAirbaseLabel(to)}: {meters / 1000f:0.0} km ({result}), "
                + $"{FastMath.Distance(start, end) / 1000f:0.0} km direct.");
        return meters;
    }

    private static void Expect(string name, bool condition)
    {
        if (!condition)
        {
            CommanderPlugin.Log.LogError($"Capture priority self-check FAILED: {name}.");
        }
    }

    /// <summary>
    /// A building stands here. Only buildings count: vehicles parked on the spot move away on
    /// their own, and treating them as blockers would make the hold point depend on whoever
    /// happened to be standing there the first time it was asked for.
    /// </summary>
    private static bool IsBuiltOver(GlobalPosition point)
    {
        int hits = Physics.OverlapSphereNonAlloc(
            point.ToLocalPosition() + (Vector3.up * HoldClearanceMeters),
            HoldClearanceMeters,
            HoldProbeHits,
            ~0,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider hit = HoldProbeHits[i];
            Unit? occupant = hit == null ? null : hit.GetComponentInParent<Unit>();
            if (occupant is Building && !occupant.disabled)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when every orderable unit in the selection is already committed to this base. One unit
    /// in the selection that has not been sent there means the order is reinforcement, not a
    /// redirect, so it still snaps.
    /// </summary>
    private bool IsSelectionAlreadySentTo(Airbase airbase)
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        int considered = 0;
        for (int i = 0; i < selection.Count; i++)
        {
            Unit unit = selection[i];
            if (unit == null || unit.disabled || CommanderGameAccess.GetUnitCommand(unit) == null)
            {
                continue;
            }

            considered++;
            if (!assignments.TryGetValue(unit, out Airbase assigned) || !ReferenceEquals(assigned, airbase))
            {
                return false;
            }
        }

        return considered > 0;
    }

    /// <summary>True when nothing selected captures by driving into the ring.</summary>
    private bool IsSelectionAircraftOnly()
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] != null && selection[i] is not Aircraft)
            {
                return false;
            }
        }

        return selection.Count > 0;
    }

    /// <summary>Drops the selection's capture assignments, because it has just been given a
    /// plain order somewhere else.</summary>
    private void ForgetSelectionAssignments()
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            assignments.Remove(selection[i]);
        }
    }

    /// <summary>
    /// Every capturable airbase the local faction does not already hold. Empty bases sort first: an
    /// unheld base costs a drive, a held one costs a battle.
    /// </summary>
    private void RefreshTargets()
    {
        targets.Clear();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        LogCaptureRosterOnce(localHq);
        UpdateDiscovery(localHq);
        PruneAssignments();
        GlobalPosition from = GetTerritoryCenter(localHq);
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (IsTakeable(airbase, localHq) && discovered.Contains(airbase))
            {
                targets.Add(new CaptureTarget(airbase, FastMath.Distance(from, airbase.center.GlobalPosition())));
            }
        }

        targets.Sort(static (left, right) =>
        {
            int held = left.HeldByOther.CompareTo(right.HeldByOther);
            return held != 0 ? held : left.Distance.CompareTo(right.Distance);
        });
    }

    /// <summary>
    /// Names, once per mission, every vehicle in the faction roster that carries capture strength.
    /// <c>UnitDefinition.captureStrength</c> lives in the game's asset files, not in its code, so
    /// which units can take ground cannot be read outside a running game — this line in the BepInEx
    /// console is the answer, and an empty one says capture needs troops flown in instead.
    /// </summary>
    private void LogCaptureRosterOnce(FactionHQ hq)
    {
        if (loggedRoster)
        {
            return;
        }

        loggedRoster = true;
        CommanderGameAccess.CollectFactionVehicleDefinitions(rosterProbe, hq);
        string names = string.Empty;
        for (int i = 0; i < rosterProbe.Count; i++)
        {
            if (rosterProbe[i].captureStrength > 0f)
            {
                names += (names.Length == 0 ? string.Empty : ", ")
                    + $"{rosterProbe[i].unitName} ({rosterProbe[i].captureStrength:0.#})";
            }
        }

        rosterProbe.Clear();
        if (names.Length == 0)
        {
            CommanderPlugin.Log.LogWarning(
                $"No vehicle in the {hq.faction.name} roster carries capture strength, so no ground "
                    + "unit can take an airbase on this mission. Capture needs troops landed from "
                    + "the air here.");
            return;
        }

        CommanderPlugin.Log.LogInfo($"Vehicles that can capture ({hq.faction.name}): {names}.");
    }

    /// <summary>
    /// Adds any capturable base one of the faction's units has come near to the found set. Runs on
    /// the target refresh rather than per frame, and only ever grows: a base drops off the list
    /// when it is taken, never because you flew away from it.
    /// </summary>
    private void UpdateDiscovery(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || airbase.SavedAirbase == null
                || !airbase.SavedAirbase.Capturable
                || discovered.Contains(airbase))
            {
                continue;
            }

            // A base the faction already holds is found by definition.
            if (ReferenceEquals(airbase.CurrentHQ, hq))
            {
                discovered.Add(airbase);
                continue;
            }

            GlobalPosition center = airbase.center.GlobalPosition();
            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled)
                {
                    continue;
                }

                float range = unit is Aircraft ? AirDiscoveryMeters : GroundDiscoveryMeters;
                if (FastMath.InRange(unit.transform.GlobalPosition(), center, range))
                {
                    discovered.Add(airbase);
                    CommanderAlertService.Instance?.NotifyCapture(
                        $"FOUND {GetAirbaseLabel(airbase).ToUpperInvariant()} — CAPTURABLE");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Sends everything waiting at the form-up point once enough has gathered: half the squad still
    /// waiting, at least <see cref="AssaultWaveSize"/> (or all of it, if it is smaller). Returns how
    /// many went in.
    /// </summary>
    private int LaunchWaveIfFormed(EnemyDrive drive, GlobalPosition formUp, GlobalPosition hold, int required = 0, int defenders = 0)
    {
        int waiting = 0;
        int gathered = 0;
        for (int i = 0; i < squad.Count; i++)
        {
            if (drive.Launched.Contains(squad[i]))
            {
                continue;
            }

            waiting++;
            if (FastMath.InRange(squad[i].transform.GlobalPosition(), GetLaneFormUp(formUp, hold, squad[i]), FormUpRadiusMeters))
            {
                gathered++;
            }
        }

        if (waiting == 0)
        {
            drive.FormingSince = 0f;
            return 0;
        }

        // The clock runs from the first vehicle waiting, not the first one gathered, and a timeout sends
        // everything waiting wherever it stands, so an unreachable form-up point cannot hold the army.
        if (drive.FormingSince <= 0f)
        {
            drive.FormingSince = Time.time;
        }

        // A massed push names its own size and waits longer for it: going in short is the mistake
        // it exists to avoid.
        int wave = required > 0 ? required : Mathf.Min(waiting, Mathf.Max(AssaultWaveSize, waiting / 2));
        float waited = Time.time - drive.FormingSince;
        bool timedOut = waited > (required > 0 ? MainEffortTimeoutSeconds : FormUpTimeoutSeconds);

        // Out of patience, a massed push still does not go in outnumbered: the first match sent 20
        // against 26 and 26 against 37 that way and lost both waves for nothing.
        if (required > 0 && timedOut && gathered < defenders && waited < MainEffortGiveUpSeconds)
        {
            return 0;
        }

        // And what goes on a timeout is what has gathered, not stragglers from wherever they stand.
        bool everything = timedOut && (required <= 0 || waited >= MainEffortGiveUpSeconds);
        if (gathered < wave && !timedOut)
        {
            return 0;
        }

        int sent = 0;
        for (int i = 0; i < squad.Count; i++)
        {
            if (!drive.Launched.Contains(squad[i])
                && (everything || FastMath.InRange(squad[i].transform.GlobalPosition(), GetLaneFormUp(formUp, hold, squad[i]), FormUpRadiusMeters)))
            {
                drive.Launched.Add(squad[i]);
                sent++;
            }
        }

        drive.FormingSince = 0f;
        return sent;
    }

    private void ReviewEnemies()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        // One drive per base the commander holds, not one per commander: seven bases means seven
        // pushes, each staged from its own field, so the attacks come from every direction the
        // enemy holds ground in instead of always down the same road.
        claimed.Clear();
        announcedCaptures.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (!CommanderEnemyCommanderService.IsAiFaction(hq, localHq))
            {
                continue;
            }

            stagingBases.Clear();
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (CommanderGameAccess.IsLandBase(airbase))
                {
                    stagingBases.Add(airbase);
                }
            }

            int slots = stagingBases.Count == 0 ? 0 : (MinEnemyDrives + stagingBases.Count - 1) / stagingBases.Count;
            for (int i = 0; i < stagingBases.Count; i++)
            {
                for (int slot = 0; slot < slots; slot++)
                {
                    ReviewEnemyBase(hq, stagingBases[i], slot, localHq);
                }
            }

            // After the walk-ins, so they keep their three vehicles each and this takes the rest.
            if (CommanderEnemyCommanderService.IsChallenging(hq))
            {
                ReviewMainEffort(hq, localHq);
            }
        }

        PruneDrives(localHq);
    }

    /// <summary>
    /// One expansion decision per base the commander holds: pick a base worth taking from there,
    /// commit the capture-capable units nearest that field, and keep pointing them at the ring
    /// until the base changes hands or the squad is gone. Re-issuing every review is cheap — three
    /// destinations every twenty seconds — and it is what recovers the drive when a unit is killed
    /// on the way.
    /// </summary>
    private void ReviewEnemyBase(FactionHQ hq, Airbase staging, int slot, FactionHQ localHq, Airbase? objective = null)
    {
        if (!drives.TryGetValue((staging, slot), out EnemyDrive drive))
        {
            drive = new EnemyDrive();
            drives[(staging, slot)] = drive;
        }

        drive.Owner = hq;

        // CHALLENGING splits the work: these per-base drives only walk onto empty bases, and every
        // held one is left to the single massed push (objective != null), so the army is never
        // spent six vehicles at a time against a guarded ring.
        bool mainEffort = objective != null;
        bool neutralOnly = !mainEffort && (CommanderEnemyCommanderService.IsChallenging(hq) || !HasForwardBase(hq));
        if (mainEffort)
        {
            if (!ReferenceEquals(drive.Target, objective))
            {
                ReleaseDrive(drive);
                drive.Target = objective;
                drive.Announced = 0;
            }
        }
        else if (!IsTakeable(drive.Target, hq) || (neutralOnly && drive.Target!.CurrentHQ != null))
        {
            if (drive.Target != null && ReferenceEquals(drive.Target.CurrentHQ, hq) && announcedCaptures.Add(drive.Target))
            {
                // The toast is raised by CommanderAlertPatches off Airbase.CaptureFaction, which
                // catches every base that changes hands rather than only the one this drive was
                // aimed at. Console line only here.
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) captured {GetAirbaseLabel(drive.Target)}.");
            }

            ReleaseDrive(drive);
            drive.Target = ChooseEnemyTarget(hq, staging, neutralOnly);
            drive.Announced = 0;
        }

        if (drive.Target == null)
        {
            drive.WantsCaptureUnit = false;
            return;
        }

        // A held base is an assault, not a walk-in: three vehicles never outlast the defenders in the
        // ring, and a base the enemy has already bombed flat is only lost once something stands on it.
        // The player's last base is the whole war, so everything that can take ground goes for it.
        // Six per staging base still let most of the army drive past it after other targets.
        bool lastBase = ReferenceEquals(drive.Target.CurrentHQ, localHq)
            && CommanderVictoryService.CountBases(localHq) <= 1;
        int squadSize = lastBase || mainEffort
            ? int.MaxValue
            : drive.Target.CurrentHQ != null ? EnemyAssaultSquadSize : EnemySquadSize;
        CollectCaptureSquad(hq, staging.center.GlobalPosition(), squadSize, drive.Committed, anyFighter: mainEffort);

        // No unit that can take ground is the one thing that stops an expansion dead, so it is the
        // one thing the unit spender is told about.
        drive.WantsCaptureUnit = squad.Count == 0;
        if (squad.Count == 0)
        {
            return;
        }

        GlobalPosition hold = GetHoldPoint(drive.Target);
        float ring = Mathf.Max(drive.Target.SavedAirbase.CaptureRange, 100f);

        // Anything that dropped out of the squad since the last review is let go, or it stands on
        // the old hold point for the rest of the match.
        for (int i = drive.Committed.Count - 1; i >= 0; i--)
        {
            Unit committed = drive.Committed[i];
            if (committed == null || committed.disabled || !squad.Contains(committed))
            {
                CommanderEnemyCommanderService.ReleaseCommandedDestination(committed);
                drive.Committed.RemoveAt(i);
                drive.Launched.Remove(committed!);
                drive.Marching.Remove(committed!);
            }
        }

        // A defended base is attacked in waves. Sent straight at the ring, every vehicle arrived
        // alone the moment it was bought and died alone: one Champions push grew to 156 units over
        // fifty minutes and never took the base. Now the squad gathers out of range and goes in
        // together once a wave has formed.
        GlobalPosition formUp = hold;
        bool staged = drive.Target.CurrentHQ != null && TryGetFormUpPoint(staging, hold, out formUp);
        int defenders = mainEffort ? CountDefenders(drive.Target, hold, ring) : 0;
        int required = mainEffort ? Mathf.Max(MainEffortMinWave, Mathf.CeilToInt(defenders * MainEffortOdds)) : 0;
        if (mainEffort && CommanderEnemyCommanderService.HasChallengingTactics(hq))
        {
            // The defenders on the ring are not the army that meets the wave: sized on them, the
            // push marched 13 at a time into the whole of the player's force.
            required = Mathf.Max(
                required,
                Mathf.Max(
                    CommanderEnemyCommanderService.ChallengingMinWave,
                    Mathf.CeilToInt(CountFightingVehicles(drive.Target.CurrentHQ) * CommanderEnemyCommanderService.ChallengingArmyShare)));
        }

        GlobalPosition rally = mainEffort && staged ? GetRallyPoint(staging, hold) : formUp;
        if (mainEffort && staged)
        {
            int marching = MarchIfGathered(drive, rally, required, defenders);
            if (marching > 0)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({hq.faction.name}) marches {marching} on {GetAirbaseLabel(drive.Target)} "
                        + $"({defenders} defender(s) on the base, {squad.Count} in the push).");
            }
        }

        int sent = !staged ? 0
            : mainEffort ? LaunchMarchers(drive, formUp, hold, required)
            : LaunchWaveIfFormed(drive, formUp, hold, required, defenders);
        if (sent > 0)
        {
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) sends a wave of {sent} from its form-up point at "
                    + $"{GetAirbaseLabel(drive.Target)} ({drive.Launched.Count} of {squad.Count} committed"
                    + (mainEffort ? $", {defenders} defender(s) on the base)." : ")."));
        }

        // Holding a base, the whole pattern has to fit well inside the capture ring.
        float holdScale = Mathf.Min(1f, ring * 0.6f / (SpreadGroupMeters + SpreadGroupRadiusMeters));

        // Groups are counted off the largest the squad has been, so losses never reshuffle them and
        // re-send an order to every vehicle (each is an RPC and a path search on the host).
        int peak = Mathf.Max(drive.Announced, squad.Count);
        for (int i = 0; i < squad.Count; i++)
        {
            if (!drive.Committed.Contains(squad[i]))
            {
                drive.Committed.Add(squad[i]);

                // Some vehicles are authored to drop a player order on arrival, after which the base
                // game drives them at the nearest enemy: the squad reached the player's base and
                // carried on past it. Holding the order is what keeps them standing in the ring.
                if (squad[i] is GroundVehicle vehicle)
                {
                    CommanderRepairService.KeepAtCommandedDestination(vehicle);
                }
            }

            // A unit already standing in the ring is already capturing. Re-ordering it there every
            // review only makes it shuffle, and every issue is a networked RPC.
            GlobalPosition position = squad[i].transform.GlobalPosition();
            if (FastMath.InRange(position, hold, ring * 0.8f))
            {
                continue;
            }

            if (staged && !drive.Launched.Contains(squad[i]))
            {
                // A massed push waits at home and only its marching wave stands at the form-up point.
                bool gathering = mainEffort && !drive.Marching.Contains(squad[i]);
                GlobalPosition wait = gathering ? rally : GetLaneFormUp(formUp, hold, squad[i]);
                if (!FastMath.InRange(position, wait, (gathering ? RallyRadiusMeters : FormUpRadiusMeters) * 0.5f))
                {
                    CommanderEnemyCommanderService.PinTo(squad[i], SpreadAround(wait, squad[i], peak));
                }

                continue;
            }

            // playerCommand: true, and that is the whole reason the enemy never took a base.
            // GroundVehicle.CheckObstacles re-steers any vehicle with no commandedDestination at the
            // nearest thing it can see, and only a player command sets that flag — so a squad sent
            // to Sandrift turned round on the next obstacle check and walked at the player instead.
            // UnitCommand only asks whether GameManager.GetLocalPlayer came back non-null, so the
            // flag is available to a faction that is not the player's; the home guard already relies
            // on exactly this. ReleaseDrive is the matching un-pin.
            CommanderEnemyCommanderService.PinTo(squad[i], SpreadAround(hold, squad[i], peak, holdScale));
        }

        // Logged again whenever the push grows, so the log shows the assault building up.
        if (squad.Count > drive.Announced)
        {
            drive.Announced = squad.Count;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) is moving on {GetAirbaseLabel(drive.Target)} "
                    + $"from {GetAirbaseLabel(staging)} with {squad.Count} unit(s).");
        }
    }

    /// <summary>
    /// What this base goes after: measured from the base itself, not from the commander's territory
    /// centre, which on a map whose bases surround the player averages out to a point in the middle
    /// of the war. A base another of this commander's drives is already taking is passed over while
    /// anything else is in reach, so the pushes spread out instead of stacking on one field.
    /// </summary>
    private Airbase? ChooseEnemyTarget(FactionHQ hq, Airbase staging, bool neutralOnly = false)
    {
        // Two walk-ins on one empty base is a squad wasted, so a neutral-only drive never doubles up.
        return ChooseEnemyTarget(hq, staging, skipTaken: true, neutralOnly)
            ?? (neutralOnly ? null : ChooseEnemyTarget(hq, staging, skipTaken: false, neutralOnly));
    }

    /// <summary>
    /// Starts the massed push's next wave on its way once enough of it has gathered at the rally
    /// point: <paramref name="required"/> vehicles, or after <see cref="MainEffortTimeoutSeconds"/>
    /// at least as many as the objective has defenders. Returns how many set off.
    /// </summary>
    private int MarchIfGathered(EnemyDrive drive, GlobalPosition rally, int required, int defenders)
    {
        int waiting = 0;
        int gathered = 0;
        for (int i = 0; i < squad.Count; i++)
        {
            if (drive.Launched.Contains(squad[i]) || drive.Marching.Contains(squad[i]))
            {
                continue;
            }

            waiting++;
            if (FastMath.InRange(squad[i].transform.GlobalPosition(), rally, RallyRadiusMeters))
            {
                gathered++;
            }
        }

        if (waiting == 0)
        {
            drive.FormingSince = 0f;
            return 0;
        }

        if (drive.FormingSince <= 0f)
        {
            drive.FormingSince = Time.time;
        }

        float waited = Time.time - drive.FormingSince;
        bool giveUp = waited > MainEffortGiveUpSeconds;
        if (!giveUp && gathered < required && !(waited > MainEffortTimeoutSeconds && gathered >= Mathf.Max(defenders, MainEffortMinWave)))
        {
            return 0;
        }

        int sent = 0;
        for (int i = 0; i < squad.Count; i++)
        {
            if (!drive.Launched.Contains(squad[i])
                && !drive.Marching.Contains(squad[i])
                && (giveUp || FastMath.InRange(squad[i].transform.GlobalPosition(), rally, RallyRadiusMeters)))
            {
                drive.Marching.Add(squad[i]);
                sent++;
            }
        }

        drive.FormingSince = 0f;
        if (sent > 0 && drive.MarchSince <= 0f)
        {
            drive.MarchSince = Time.time;
        }

        return sent;
    }

    /// <summary>
    /// The CHALLENGING commander's one massed push at a base somebody holds: the nearest it can drive
    /// to, then the next nearest from there. Everything that fights and is not in the home guard
    /// gathers short of it and goes in once there are <see cref="MainEffortOdds"/> for every defender.
    /// </summary>
    private void ReviewMainEffort(FactionHQ hq, FactionHQ localHq)
    {
        // One push per commander: if a nearer base has been taken since, the same squad moves to it.
        EnemyDrive? existing = null;
        (Airbase Staging, int Slot) existingKey = default;
        foreach (KeyValuePair<(Airbase Staging, int Slot), EnemyDrive> entry in drives)
        {
            if (entry.Key.Slot == MainEffortSlot && ReferenceEquals(entry.Value.Owner, hq))
            {
                existing = entry.Value;
                existingKey = entry.Key;
                break;
            }
        }

        // A column on the road keeps its objective until it is taken, or re-picking turns it round into
        // the vehicles behind it. A push still gathering at home re-picks every review.
        bool underWay = existing != null && (existing.Launched.Count > 0 || existing.Marching.Count > 0);
        Airbase? objective = underWay && IsTakeable(existing!.Target, hq)
            ? existing.Target
            : HasForwardBase(hq) ? FindNearestHeldBase(hq) : null;

        // Staged from the held base nearest the objective, so the form-up point is on the shortest road.
        Airbase? staging = null;
        if (objective != null)
        {
            GlobalPosition at = objective.center.GlobalPosition();
            float nearest = float.MaxValue;
            for (int i = 0; i < stagingBases.Count; i++)
            {
                float distance = GetRoadMeters(stagingBases[i], objective);
                if (distance < nearest
                    && CommanderEnemyCommanderService.CanDriveBetween(stagingBases[i].center.GlobalPosition(), at))
                {
                    nearest = distance;
                    staging = stagingBases[i];
                }
            }
        }

        if (existing != null && !ReferenceEquals(existingKey.Staging, staging))
        {
            drives.Remove(existingKey);
            if (staging == null)
            {
                ReleaseDrive(existing);
            }
            else
            {
                drives[(staging, MainEffortSlot)] = existing;
            }
        }

        if (objective != null && staging != null)
        {
            ReviewEnemyBase(hq, staging, MainEffortSlot, localHq, objective);
        }
    }

    /// <summary>The base somebody else holds nearest any of <see cref="stagingBases"/> by land.</summary>
    private Airbase? FindNearestHeldBase(FactionHQ hq)
    {
        Airbase? best = null;
        float nearest = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (!IsTakeable(airbase, hq) || airbase.CurrentHQ == null)
            {
                continue;
            }

            GlobalPosition at = airbase.center.GlobalPosition();
            for (int i = 0; i < stagingBases.Count; i++)
            {
                GlobalPosition from = stagingBases[i].center.GlobalPosition();
                float distance = GetRoadMeters(stagingBases[i], airbase);
                if (distance < nearest && CommanderEnemyCommanderService.CanDriveBetween(from, at))
                {
                    nearest = distance;
                    best = airbase;
                }
            }
        }

        return best;
    }

    private Airbase? ChooseEnemyTarget(FactionHQ hq, Airbase staging, bool skipTaken, bool neutralOnly)
    {
        GlobalPosition from = staging.center.GlobalPosition();
        Airbase? best = null;
        float bestScore = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;

            // Only empty bases are spread over. Every drive at its nearest held base is one base at a
            // time; passing over a taken one sent the spare drives at the next enemy base along.
            if (!IsTakeable(airbase, hq)
                || (skipTaken && airbase.CurrentHQ == null && IsDriveTarget(airbase))
                || (neutralOnly && airbase.CurrentHQ != null))
            {
                continue;
            }

            float distance = FastMath.Distance(from, airbase.center.GlobalPosition());
            if (distance > EnemyReachMeters)
            {
                continue;
            }

            float score = ScoreTarget(airbase.CurrentHQ != null, GetRoadMeters(staging, airbase));
            if (score < bestScore)
            {
                bestScore = score;
                best = airbase;
            }
        }

        return best;
    }

    /// <summary>True when one of this commander's other drives is already going for that base.</summary>
    private bool IsDriveTarget(Airbase airbase)
    {
        foreach (KeyValuePair<(Airbase Staging, int Slot), EnemyDrive> entry in drives)
        {
            if (ReferenceEquals(entry.Value.Target, airbase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <paramref name="limit"/> capture-capable units for a drive: the ones it already committed
    /// first, then the nearest to <paramref name="from"/> that no other drive has taken this review.
    /// Committed units come first so the squad is not re-picked (and released mid-march) every review.
    /// </summary>
    private void CollectCaptureSquad(FactionHQ hq, GlobalPosition from, int limit, List<Unit> committed, bool anyFighter = false)
    {
        squad.Clear();
        squadDistances.Clear();
        if (hq.factionUnits == null)
        {
            return;
        }

        for (int i = 0; i < committed.Count && squad.Count < limit; i++)
        {
            Unit unit = committed[i];
            if (unit != null && !unit.disabled && IsSquadCandidate(unit, anyFighter))
            {
                squad.Add(unit);
                claimed.Add(unit);
            }
        }

        int kept = squad.Count;
        int room = limit - kept;
        foreach (PersistentID id in hq.factionUnits)
        {
            // A vehicle standing on the commander's own base ring is spoken for: the home guard
            // pins it with a player command and this would order it away again every review.
            // Builders stay home and build; the commander buys more of them rather than sending
            // one to war. A munitions truck is capture-capable and is still not an assault vehicle:
            // it belongs with the convoy or the ring, and a squad slot spent on one is a slot that
            // shoots nothing.
            // A massed push takes everything else, so it leaves the guard its recruits: a vehicle
            // the home ring is still short of is the ring's, not the push's.
            if (room <= 0
                || !id.TryGetUnit(out Unit unit)
                || !IsSquadCandidate(unit, anyFighter)
                || (anyFighter && CommanderEnemyCommanderService.IsWantedByGuard(hq, unit)))
            {
                continue;
            }

            float distance = FastMath.Distance(unit.transform.GlobalPosition(), from);
            int at = NearestInsertIndex(squadDistances, distance, room);
            if (at < 0)
            {
                continue;
            }

            squad.Insert(kept + at, unit);
            squadDistances.Insert(at, distance);
            if (squadDistances.Count > room)
            {
                squad.RemoveAt(kept + room);
                squadDistances.RemoveAt(room);
            }
        }

        for (int i = kept; i < squad.Count; i++)
        {
            claimed.Add(squad[i]);
        }

        AddConvoyAirDefence(hq, from, committed);
    }

    /// <summary>
    /// Up to <see cref="CommanderEnemyCommanderService.ConvoyAirDefencePerDrive"/> air defence escorts
    /// and <see cref="CommanderEnemyCommanderService.ConvoySpaagPerDrive"/> Spaags on top of the squad.
    /// Only a squad that has something in it gets any, so an escort never drives out alone.
    /// </summary>
    private void AddConvoyAirDefence(FactionHQ hq, GlobalPosition from, List<Unit> committed)
    {
        if (squad.Count == 0)
        {
            return;
        }

        AddEscorts(hq, from, committed, CommanderEnemyCommanderService.ConvoyAirDefencePerDrive, spaag: false);
        AddEscorts(hq, from, committed, CommanderEnemyCommanderService.ConvoySpaagPerDrive, spaag: true);
    }

    /// <summary>The ones already riding with the drive first, then the nearest free ones.</summary>
    private void AddEscorts(FactionHQ hq, GlobalPosition from, List<Unit> committed, int limit, bool spaag)
    {
        int escorts = 0;
        for (int i = 0; i < committed.Count && escorts < limit; i++)
        {
            if (IsEscortCandidate(committed[i], spaag))
            {
                squad.Add(committed[i]);
                claimed.Add(committed[i]);
                escorts++;
            }
        }

        while (escorts < limit)
        {
            Unit? nearest = null;
            float best = float.MaxValue;
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit) && IsEscortCandidate(unit, spaag))
                {
                    float distance = FastMath.Distance(unit.transform.GlobalPosition(), from);
                    if (distance < best)
                    {
                        best = distance;
                        nearest = unit;
                    }
                }
            }

            if (nearest == null)
            {
                return;
            }

            squad.Add(nearest);
            claimed.Add(nearest);
            escorts++;
        }
    }

    private bool IsEscortCandidate(Unit? unit, bool spaag)
    {
        return unit is GroundVehicle
            && !unit.disabled
            && (spaag ? CommanderEnemyCommanderService.IsConvoySpaag(unit) : CommanderEnemyCommanderService.IsConvoyAirDefence(unit))
            && CommanderGameAccess.GetUnitCommand(unit) != null
            && !CommanderEnemyCommanderService.IsDefendingUnit(unit)
            && !claimed.Contains(unit);
    }

    /// <summary><paramref name="anyFighter"/> is the massed push: every head in the ring counts, so it
    /// takes anything that fights and moves, not only what carries capture strength.</summary>
    private bool IsSquadCandidate(Unit unit, bool anyFighter = false)
    {
        // The massed push takes air defence too, whatever the home ring has not claimed: nothing
        // else in a column can answer an attack aircraft. Never artillery, posted or not: it belongs
        // to ReviewArtillery's batteries, including a launcher fresh out of the depot or with no post
        // in reach.
        return (anyFighter
                ? CommanderEnemyCommanderService.IsFightingVehicle(unit)
                : CanCapture(unit) && !CommanderEnemyCommanderService.IsHomeGuardType(unit))
            && !CommanderRepairService.IsRepairUnit(unit)
            && !CommanderEnemyCommanderService.IsRearmVehicle(unit)
            && !CommanderEnemyCommanderService.IsConvoyAirDefence(unit)
            && !CommanderEnemyCommanderService.IsStaticAirDefence(unit)
            && !CommanderEnemyCommanderService.IsPlacedGuard(unit)
            && unit.definition is not VehicleDefinition { vehicleType: VehicleType.ART }
            && !CommanderEnemyCommanderService.IsDefendingUnit(unit)
            && !claimed.Contains(unit);
    }

    /// <summary>Drops capture assignments for units that died or whose base changed hands.</summary>
    private void PruneAssignments()
    {
        staleAssignments.Clear();
        foreach (KeyValuePair<Unit, Airbase> entry in assignments)
        {
            if (entry.Key == null || entry.Key.disabled || entry.Value == null || entry.Value.disabled)
            {
                staleAssignments.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleAssignments.Count; i++)
        {
            assignments.Remove(staleAssignments[i]);
        }
    }

    /// <summary>Drops the drive of any base the commander no longer holds, and lets its squad go
    /// rather than leaving it pinned to a hold point nobody reviews any more.</summary>
    private void PruneDrives(FactionHQ localHq)
    {
        staleDrives.Clear();
        foreach (KeyValuePair<(Airbase Staging, int Slot), EnemyDrive> entry in drives)
        {
            Airbase airbase = entry.Key.Staging;
            if (airbase == null
                || airbase.disabled
                || airbase.CurrentHQ == null
                || (entry.Value.Owner != null && !ReferenceEquals(airbase.CurrentHQ, entry.Value.Owner))
                || (!CommanderEnemyCommanderService.LocalSideRotates && ReferenceEquals(airbase.CurrentHQ, localHq)))
            {
                staleDrives.Add(entry.Key);
            }
        }

        for (int i = 0; i < staleDrives.Count; i++)
        {
            if (drives.TryGetValue(staleDrives[i], out EnemyDrive drive))
            {
                ReleaseDrive(drive);
            }

            drives.Remove(staleDrives[i]);
        }
    }

    /// <summary>
    /// A toast the moment one of the player's bases starts slipping. The base game shows capture
    /// progress nowhere, and a big enough squad drains a base in seconds, so the player's report
    /// was losing their last base "instantly" with nothing on screen.
    /// </summary>
    private void WarnLosingBases()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        foreach (Airbase airbase in localHq.GetAirbases())
        {
            float grip = GetOwnerGrip(airbase);
            if (grip >= 0.999f)
            {
                warnedLosing.Remove(airbase);
            }
            else if (grip < LosingWarnGrip && warnedLosing.Add(airbase))
            {
                CommanderAlertService.Instance?.NotifyCapture(
                    $"{GetAirbaseLabel(airbase).ToUpperInvariant()} IS BEING CAPTURED");
            }
        }
    }

    internal readonly struct CaptureTarget
    {
        internal CaptureTarget(Airbase airbase, float distance)
        {
            Airbase = airbase;
            Distance = distance;
        }

        internal Airbase Airbase { get; }

        internal float Distance { get; }

        /// <summary>Somebody else already holds it, so taking it means fighting for it.</summary>
        internal bool HeldByOther => Airbase.CurrentHQ != null;

        /// <summary>
        /// How far the base is from being yours, 0 to 1, across both of the game's capture steps.
        /// <c>Capture</c> first drains the owner's grip (<c>controlBalance</c> 1 to 0, then neutral) and
        /// then fills the challenger's (0 to 1). The drain is the first half of one bar and the fill the
        /// second, so it never looks like the capture restarts; an empty base starts at half way.
        /// </summary>
        internal float CaptureProgress
        {
            get
            {
                Airbase? airbase = Airbase;
                Capture? capture = airbase == null ? null : airbase.capture;
                if (airbase == null || capture == null)
                {
                    return 0f;
                }

                if (airbase.CurrentHQ != null)
                {
                    return (1f - capture.controlBalance) * 0.5f;
                }

                // capturingHQ is server-only (not a SyncVar), so a guest always reads it null and lost
                // this half of the bar. A neutral base's balance is only above 0 while somebody is
                // taking it (the server zeroes it when the capture stops), so the balance alone says it.
                return capture.capturingHQ != null || capture.controlBalance > 0f
                    ? 0.5f + (capture.controlBalance * 0.5f)
                    : 0f;
            }
        }

        /// <summary>True while the base still has an owner to be pushed off it.</summary>
        internal bool Neutralising => Airbase != null && Airbase.CurrentHQ != null;

        /// <summary>Who is taking an unheld base, or null when nobody is or it is already held.</summary>
        internal FactionHQ? CapturingHq
        {
            get
            {
                Airbase? airbase = Airbase;
                return airbase == null ? null : airbase.capture?.capturingHQ;
            }
        }

        internal string Label => GetAirbaseLabel(Airbase);

        internal GlobalPosition HoldPoint => GetHoldPoint(Airbase);

        /// <summary>
        /// Whether <paramref name="hq"/> is the side taking this base. A held base is being taken off
        /// its owner, which is us (the target list never holds our own bases). A guest cannot see
        /// <c>capturingHQ</c>, so on a neutral base it goes by whether our units were sent there.
        /// </summary>
        internal bool IsCapturedBy(FactionHQ? hq)
        {
            if (Airbase == null || Airbase.CurrentHQ != null)
            {
                return true;
            }

            FactionHQ? capturing = CapturingHq;
            return capturing != null
                ? ReferenceEquals(capturing, hq)
                : Instance?.HasCaptureOrder(Airbase) != false;
        }
    }

    private sealed class EnemyDrive
    {
        /// <summary>Units currently pinned to this drive's hold point, so the pin can be lifted when
        /// the drive moves on. See <see cref="ReviewEnemyBase"/>.</summary>
        internal List<Unit> Committed { get; } = new();

        /// <summary>Units sent in from the form-up point; the rest are still gathering.</summary>
        internal HashSet<Unit> Launched { get; } = new();

        /// <summary>A massed push's wave on its way from the rally point to the form-up point, and
        /// when it set off.</summary>
        internal HashSet<Unit> Marching { get; } = new();

        internal float MarchSince { get; set; }

        /// <summary>When the wave now gathering started to arrive, 0 while nothing has.</summary>
        internal float FormingSince { get; set; }

        internal Airbase? Target { get; set; }

        /// <summary>The commander running this drive. A staging base that changes hands takes its
        /// drive with it, and the squad is let go rather than left pinned for the new owner.</summary>
        internal FactionHQ? Owner { get; set; }

        internal int Announced { get; set; }

        internal bool WantsCaptureUnit { get; set; }
    }
}
