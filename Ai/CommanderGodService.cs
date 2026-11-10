using System.Collections.Generic;
using System.Globalization;
using NuclearOption.Chat;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// GOD DIFFICULTY: on Champions with a budget of 10 B or more (or the ForceGodDifficulty setting),
/// the enemy commander speaks. The host broadcasts its lines through the game's own server chat
/// message (<c>ChatManager.RpcServerMessage</c>), so every player sees them, modded or not. The
/// tag in each line drives <see cref="CommanderGodUi"/> on every client that runs the mod, off the
/// <c>GameplayUI.GameMessage</c> postfix in <see cref="CommanderGodPatches"/>.
/// </summary>
/// <remarks>
/// Voice only: nothing here changes what the AI does.
/// </remarks>
internal sealed class CommanderGodService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>When the god first speaks. The UI counts the survival timer back from it.</summary>
    internal const float FirstWordsSeconds = 120f;

    /// <summary>Faction money is in millions: 10 000 is 10 B.</summary>
    private const float GodBudgetMillions = 10000f;
    private const float AwakeningGapSeconds = 9f;
    private const float IntroLineGapSeconds = 7f;
    private const float TauntMinSeconds = 100f;
    private const float TauntMaxSeconds = 170f;
    private const float EventCooldownSeconds = 25f;
    private const float NuclearAfterSeconds = 1800f;
    private const float NuclearAfterWoundSeconds = 120f;

    private const string Awakening = "I am awake.";
    private const string PlayerLastBase = "One base left. Savour its walls, mortals. They are the last you will ever see.";
    private const string GodWounded = "Impossible... you wound a god? Then I will stop holding back.";
    private const string GodWins = "As foretold. All of you have perished. Kneel in the ashes.";
    private const string GodLoses = "This... is not the end. Gods do not die. They return.";
    private const string NuclearUnlocked = "Enough. The atom answers to me now. Your bases will glow for a thousand years.";
    private const string NuclearLaunch = "Look up, mortals. A second sun is rising over {0}.";

    private static readonly string[] Intro =
    {
        "I have watched you for two minutes. That is all the mercy you will ever receive.",
        "Ten billion. Every coin of it will be spent on your graves.",
        "Kneel, or burn. Either way, all of you will perish.",
    };

    private static readonly string[] Taunts =
    {
        "All of you will perish.",
        "Your bases are borrowed. I am coming to collect.",
        "I do not lose. I merely wait for you to finish failing.",
        "Every aircraft you launch is a gift to my air defence.",
        "The sky is mine. The ground is mine. Soon, you will be mine.",
        "I have counted your units. It did not take long.",
        "Resistance is noted. And ignored.",
        "Pray, if you like. I am the one who answers.",
        "My factories do not sleep. Do yours?",
        "You fight for territory. I AM the territory.",
        "Your commander is a mortal with a mouse. I am eternal.",
        "Run to your hangars. I know where they are.",
        "Another wave is coming. There is always another wave.",
        "I have seen how this ends. You have not.",
        "Do not mistake my silence for weakness. It was patience.",
    };

    private static readonly string[] Treasury =
    {
        "My treasury holds {0}. Yours holds dust.",
        "{0} in my coffers. How much is your life worth?",
    };

    private static readonly string[] LostBase =
    {
        "You DARE take {0}? Feel my wrath.",
        "{0} is a scratch. You will pay for it a thousandfold.",
        "Enjoy {0}. My wrath is already on its way.",
        "You have my attention now. Every wing I own is turning toward {0}.",
    };

    private static readonly string[] TookBase =
    {
        "Another base returns to its rightful owner.",
        "Your flag falls. Mine rises. As it was written.",
        "One less place for you to hide.",
        "Did you think those walls would hold?",
    };

    private readonly System.Random random = new();
    private readonly List<int> tauntBag = new();
    private readonly HashSet<Airbase> heldBases = new();
    private readonly List<Airbase> lostBases = new();

    private bool? active;
    private float clockStart = -1f;
    private int introIndex = -1;
    private float nextIntroAt;
    private float nextTauntAt;
    private float eventReadyAt;
    private int lastGodBases = -1;
    private int peakGodBases;
    private bool woundedSaid;
    private bool lastBaseSaid;
    private bool endSaid;
    private bool nuclearSaid;
    private float woundedAt = -1f;
    private string lastLostBase = string.Empty;

    internal CommanderGodService()
    {
        CommanderGodUi.Install();
    }

    public void ResetSession()
    {
        active = null;
        clockStart = -1f;
        introIndex = -1;
        nextIntroAt = 0f;
        lastGodBases = -1;
        peakGodBases = 0;
        woundedSaid = lastBaseSaid = endSaid = nuclearSaid = false;
        woundedAt = -1f;
        tauntBag.Clear();
        heldBases.Clear();
        CommanderGodUi.Reset();
    }

    public void TickPersistent()
    {
        // Every client, host or guest: the effects the last line started.
        CommanderGodUi.Tick();

        FactionHQ? god = FindGod();
        if (god == null || !CommanderEnemyCommanderService.IsChampionsMission)
        {
            return;
        }

        active ??= IsGodBudget();
        if (active != true)
        {
            return;
        }

        if (clockStart < 0f)
        {
            clockStart = Time.time;
            CommanderPlugin.Log.LogInfo("GOD DIFFICULTY is on: the enemy commander will speak.");
        }

        int godBases = CommanderVictoryService.CountBases(god);
        Airbase? lost = TrackLostBase(god);
        if (GameManager.gameResolution != GameResolution.Ongoing)
        {
            if (!endSaid && introIndex >= 0)
            {
                endSaid = true;
                bool godWins = godBases > 0;
                Say(godWins ? GodWins : GodLoses, godWins ? CommanderGodUi.TagTriumphant : CommanderGodUi.TagFallen);
            }

            return;
        }

        // Divine wrath waits for nothing, not a line's cooldown, the intro or the first words: the
        // wave goes the moment a base falls, aimed at the latest one lost. Only the show waits two
        // minutes, never the game.
        if (lost != null)
        {
            lastLostBase = CommanderCaptureService.GetAirbaseLabel(lost);
            CommanderEnemyCommanderService.UnleashWrath(god, lost.center.GlobalPosition());
        }

        if (Time.time - clockStart < FirstWordsSeconds)
        {
            lastGodBases = godBases;
            peakGodBases = Mathf.Max(peakGodBases, godBases);
            return;
        }

        if (introIndex < Intro.Length)
        {
            if (Time.time >= nextIntroAt)
            {
                if (introIndex < 0)
                {
                    Say(Awakening, CommanderGodUi.TagAwakens);
                    nextIntroAt = Time.time + AwakeningGapSeconds;
                }
                else
                {
                    Say(Intro[introIndex], introIndex == Intro.Length - 1 ? CommanderGodUi.TagEnraged : CommanderGodUi.TagNone);
                    nextIntroAt = Time.time + IntroLineGapSeconds;
                }

                introIndex++;
                ScheduleTaunt();
            }

            return;
        }

        if (!nuclearSaid
            && (Time.time - clockStart >= NuclearAfterSeconds
                || (woundedAt >= 0f && Time.time - woundedAt >= NuclearAfterWoundSeconds)))
        {
            nuclearSaid = true;
            CommanderEnemyCommanderService.ArmNuclear(god);
            SayEvent(NuclearUnlocked, CommanderGodUi.TagArmed);
            return;
        }

        if (TryEventLine(god, godBases))
        {
            return;
        }

        if (Time.time >= nextTauntAt)
        {
            Say(NextTaunt(god), CommanderGodUi.TagNone);
            ScheduleTaunt();
        }
    }

    /// <summary>One of the god's nuclear launchers has been aimed at a player base.</summary>
    internal static void NotifyNuclearLaunch(string baseName)
    {
        Say(string.Format(CultureInfo.InvariantCulture, NuclearLaunch, baseName), CommanderGodUi.TagLaunching);
    }

    /// <summary>This side is the god: GOD DIFFICULTY is on and it is the side players cannot join. Not
    /// just any AI side, or an AI playing the player's team (a soak, or AI commanders on both sides)
    /// got the god's economy too.</summary>
    internal static bool IsGod(FactionHQ? hq)
    {
        return hq != null && hq.preventJoin && IsGodBudget();
    }

    internal static bool IsGodBudget()
    {
        if (CommanderSettings.ForceGodDifficulty
            || CommanderFeatureGate.MissionName.IndexOf("Ground Control Champions GOD", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null
                && CommanderEconomyService.TryGetAuthoredBalance(hq, out float balance)
                && balance >= GodBudgetMillions)
            {
                return true;
            }
        }

        return false;
    }

    private static void Say(string line, string tag)
    {
        string message = CommanderGodUi.Marker + tag + "]</b></color> <color=#FFB347>" + line + "</color>";
        ChatManager? chat = NetworkSceneSingleton<ChatManager>.i;
        if (chat == null)
        {
            // No chat object: at least play the effect locally.
            CommanderGodUi.Receive(message, FirstWordsSeconds);
            return;
        }

        chat.RpcServerMessage(message, false);
    }

    /// <summary>The AI side: the faction this host runs that no human is on.</summary>
    private static FactionHQ? FindGod()
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.faction != null && hq.IsServer && !CommanderEnemyCommanderService.HasHumanPlayers(hq))
            {
                return hq;
            }
        }

        return null;
    }

    private static int CountPlayerBases(FactionHQ god)
    {
        int count = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq != god)
            {
                count += CommanderVictoryService.CountBases(hq);
            }
        }

        return count;
    }

    /// <summary>The base the god held last tick and does not hold now, if any.</summary>
    private Airbase? TrackLostBase(FactionHQ god)
    {
        lostBases.Clear();
        foreach (Airbase airbase in heldBases)
        {
            if (airbase == null || airbase.disabled || airbase.CurrentHQ != god)
            {
                lostBases.Add(airbase!);
            }
        }

        for (int i = 0; i < lostBases.Count; i++)
        {
            heldBases.Remove(lostBases[i]);
        }

        foreach (Airbase airbase in god.GetAirbases())
        {
            if (airbase != null && !airbase.disabled)
            {
                heldBases.Add(airbase);
            }
        }

        for (int i = 0; i < lostBases.Count; i++)
        {
            if (lostBases[i] != null && !lostBases[i].disabled)
            {
                return lostBases[i];
            }
        }

        return null;
    }

    private bool TryEventLine(FactionHQ god, int godBases)
    {
        int previous = lastGodBases;
        lastGodBases = godBases;
        peakGodBases = Mathf.Max(peakGodBases, godBases);
        if (Time.time < eventReadyAt || previous < 0)
        {
            return false;
        }

        if (!woundedSaid && peakGodBases >= 2 && godBases * 2 <= peakGodBases)
        {
            woundedSaid = true;
            woundedAt = Time.time;
            return SayEvent(GodWounded, CommanderGodUi.TagWounded);
        }

        if (godBases < previous)
        {
            return SayEvent(string.Format(CultureInfo.InvariantCulture, Pick(LostBase), lastLostBase), CommanderGodUi.TagEnraged);
        }

        if (godBases > previous)
        {
            if (!lastBaseSaid && CountPlayerBases(god) == 1)
            {
                lastBaseSaid = true;
                return SayEvent(PlayerLastBase, CommanderGodUi.TagNone);
            }

            return SayEvent(Pick(TookBase), CommanderGodUi.TagNone);
        }

        return false;
    }

    private bool SayEvent(string line, string tag)
    {
        Say(line, tag);
        eventReadyAt = Time.time + EventCooldownSeconds;
        ScheduleTaunt();
        return true;
    }

    private string NextTaunt(FactionHQ god)
    {
        if (random.Next(5) == 0)
        {
            return string.Format(CultureInfo.InvariantCulture, Pick(Treasury), CommanderEconomyService.FundsLabel(god.factionFunds));
        }

        // A shuffled bag, so no line repeats until every one has been said.
        if (tauntBag.Count == 0)
        {
            for (int i = 0; i < Taunts.Length; i++)
            {
                tauntBag.Add(i);
            }
        }

        int slot = random.Next(tauntBag.Count);
        string line = Taunts[tauntBag[slot]];
        tauntBag.RemoveAt(slot);
        return line;
    }

    private string Pick(string[] lines)
    {
        return lines[random.Next(lines.Length)];
    }

    private void ScheduleTaunt()
    {
        nextTauntAt = Time.time + TauntMinSeconds + ((float)random.NextDouble() * (TauntMaxSeconds - TauntMinSeconds));
    }
}
