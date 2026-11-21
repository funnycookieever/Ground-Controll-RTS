using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The answer to an opponent firing Piledrivers: Boltstrikes at every base, bought ahead of the army
/// and the air force. Only once the home income stands, so the economy is
/// never traded for it, and not while StratoLance sites already cover the bases.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    private const string BoltstrikeName = "T9K41 Boltstrike";
    private const string LadsName = "MSV LADS";
    private const string CramName = "MSV CRAM";

    /// <summary>Scaled seconds the alarm holds after the last Piledriver launch seen.</summary>
    private const float PiledriverAlarmSeconds = 600f;

    private const int BoltstrikesPerBase = 2;

    private const int LadsPerBase = 4;

    private const int MaxPiledriverBoltstrikes = 12;

    /// <summary>Two depots field about three vehicles a minute, so more a review only fills the reserve.</summary>
    private const int PiledriverBoltstrikesPerReview = 4;

    /// <summary>Scaled seconds the army waits on a Boltstrike that never comes, so a poor commander's
    /// army is not paused for good.</summary>
    private const float PiledriverPauseGiveUpSeconds = 180f;

    private static readonly HarmonyLib.AccessTools.FieldRef<MissileSeeker, Missile> LaunchedMissile =
        HarmonyLib.AccessTools.FieldRefAccess<MissileSeeker, Missile>("missile");

    /// <summary>A LADS outside the ring's air quota. Under a unit cap it counts, so the cap holds.</summary>
    internal static bool IsExtraLads(Unit unit)
    {
        return !CommanderUnitCap.Enabled && unit.definition.unitName == LadsName;
    }

    /// <summary>
    /// Off the <c>BallisticMissileGuidance.Initialize</c> postfix, host side. Every ballistic round
    /// on this roster is a Piledriver (the nuclear launchers fly the same guidance and earn the same
    /// answer); the base game only initializes a seeker on the simulating end, the host.
    /// </summary>
    internal void NotePiledriverLaunch(MissileSeeker seeker)
    {
        if (states.Count == 0)
        {
            return;
        }

        Missile missile = LaunchedMissile(seeker);
        if (missile == null)
        {
            return;
        }

        FactionHQ? from = missile.owner != null ? missile.owner.NetworkHQ : missile.NetworkHQ;
        if (from == null)
        {
            return;
        }

        foreach (System.Collections.Generic.KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            if (entry.Key == null || ReferenceEquals(entry.Key, from))
            {
                continue;
            }

            if (entry.Value.PiledriverAlarmUntil < Time.time)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Enemy commander ({entry.Key.faction.name}) saw a Piledriver launch from {from.faction.name}: "
                        + "Boltstrikes come first until its bases are covered.");
            }

            entry.Value.PiledriverAlarmUntil = Time.time + PiledriverAlarmSeconds;
        }
    }

    private static bool IsLandless(FactionHQ hq)
    {
        bool landless = true;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            landless &= !CommanderGameAccess.IsLandBase(airbase);
        }

        return landless;
    }

    private static int WantedBoltstrikes(FactionHQ hq)
    {
        return Mathf.Min(CommanderVictoryService.CountBases(hq) * BoltstrikesPerBase, MaxPiledriverBoltstrikes);
    }

    /// <summary>On the field or waiting in the reserve.</summary>
    private static int CountOwned(FactionHQ hq, VehicleDefinition definition)
    {
        int owned = hq.GetUnitSupply(definition);
        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit) && unit != null && !unit.disabled && unit.definition == definition)
                {
                    owned++;
                }
            }
        }

        return owned;
    }

    /// <summary>
    /// Buys Boltstrikes out of everything above the build reserve while the alarm is up. True while
    /// the bases are still short after this review's buys: the caller then skips the air, naval and
    /// convoy buys, so the money waits for the next Boltstrike instead.
    /// </summary>
    private bool TryBuyPiledriverDefence(FactionHQ hq, CommanderState state, float budget, ref float spendable)
    {
        int purchasesBefore = TotalPurchases;
        bool shortNow = IsShortOfPiledriverDefence(hq, state, budget, ref spendable, out int owned, out int wanted);
        if (!shortNow || TotalPurchases != purchasesBefore)
        {
            state.PiledriverPauseSince = Time.time;
            state.PiledriverGaveUp = false;
        }
        else if (!state.PiledriverPause && !state.PiledriverGaveUp)
        {
            state.PiledriverPauseSince = Time.time;
        }
        else if (Time.time - state.PiledriverPauseSince > PiledriverPauseGiveUpSeconds)
        {
            // Still short and nothing bought for a while: the army goes back to buying until a
            // Boltstrike is bought or the alarm ends, and one is still bought first on any review
            // that can afford it.
            state.PiledriverGaveUp = true;
        }

        if (state.PiledriverGaveUp)
        {
            shortNow = false;
        }

        if (shortNow != state.PiledriverPause)
        {
            state.PiledriverPause = shortNow;
            CommanderPlugin.Log.LogInfo(shortNow
                ? $"Enemy commander ({hq.faction.name}) is pausing its army and air buys for Boltstrikes ({owned} of {wanted})."
                : $"Enemy commander ({hq.faction.name}) is back to its plan ({owned} Boltstrikes).");
        }

        return shortNow;
    }

    private bool IsShortOfPiledriverDefence(
        FactionHQ hq, CommanderState state, float budget, ref float spendable, out int owned, out int wanted)
    {
        owned = 0;
        wanted = 0;
        if (state.PiledriverAlarmUntil < Time.time || !CommanderEconomyService.HasEnemyHomeIncome(hq))
        {
            return false;
        }

        // One StratoLance a base is the "good defence" that makes this unnecessary.
        int bases = CommanderVictoryService.CountBases(hq);
        if (bases == 0 || CountSamLaunchers(hq) >= bases)
        {
            return false;
        }

        // Not in the catalogue is not on this faction's roster, banned, or above its depot level:
        // pausing the army for a vehicle it cannot buy would pause it for nothing.
        VehicleDefinition? boltstrike = FindInCatalog(BoltstrikeName);

        // Uncapped, the air-defence share ceiling is exactly what this overrides; capped, the slots are real.
        if (boltstrike == null || IsAirDefenceAtMax() || (CommanderUnitCap.Enabled && IsAirDefenceFull(boltstrike)))
        {
            return false;
        }

        wanted = WantedBoltstrikes(hq);
        owned = CountOwned(hq, boltstrike);

        float cost = Mathf.Max(0f, boltstrike.value);
        for (int bought = 0; bought < PiledriverBoltstrikesPerReview && owned < wanted; bought++)
        {
            if (cost > budget || !CommanderGameAccess.TrySpend(hq, cost))
            {
                break;
            }

            hq.ModifyUnitSupply(boltstrike, 1);
            NoteOwned(boltstrike);
            TotalPurchases++;
            owned++;
            budget -= cost;
            spendable = Mathf.Max(0f, spendable - cost);
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(boltstrike)} "
                    + $"for {cost:0} against Piledrivers ({owned} of {wanted}).");
        }

        return owned < wanted;
    }

    private VehicleDefinition? FindInCatalog(string unitName)
    {
        for (int i = 0; i < catalog.Count; i++)
        {
            if (catalog[i].unitName == unitName)
            {
                return catalog[i];
            }
        }

        return null;
    }
}
