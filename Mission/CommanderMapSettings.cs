using System;
using System.Collections.Generic;
using System.Globalization;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The Ground Control rules a map maker sets in the mission editor, per map. They are mission tags,
/// "GC Objectives Win" or "GC Builders 3", because a tag is the one field of ours a mission file keeps
/// through a save (every other field round-trips through fixed game classes), travels with the map
/// and reaches every guest inside <c>MissionSettings</c>. A number is the tag's first word after the
/// prefix. Only <see cref="CommanderFeatureGate.MissionTag"/> is shown in the mission picker; the rest
/// are hidden there (<c>CommanderMissionPickerPatches</c>), or every different number would be a
/// filter chip of its own.
/// </summary>
/// <remarks>
/// The GC RTS Settings page of Customize Mission writes the same tags, onto the mission copy it is
/// customising, exactly as the game's own tabs write their fields: the editor sets a map's values and
/// Customize Mission changes them for one match. <c>CommanderSettings</c> asks <see cref="Override"/>
/// first; a Ground Control map with no tag for an option plays the config's value, any other
/// mission the stock game's (<see cref="StockValue"/>).
/// </remarks>
internal static class CommanderMapSettings
{
    internal const string ObjectivesWinTag = "GC Objectives Win";
    internal const string BuildersPrefix = "GC Builders ";
    internal const string HeadStartPrefix = "GC AI Head Start ";
    internal const string AiIncomePrefix = "GC AI Income ";
    internal const string BaseIncomePrefix = "GC Base Income ";

    internal const string AiCommandersPrefix = "GC AI Commanders ";
    internal const string EnemyModePrefix = "GC Enemy Mode ";
    internal const string EnemyDifficultyPrefix = "GC Enemy Difficulty ";
    internal const string HumanTeamsPrefix = "GC AI Human Teams ";
    internal const string FactoryVehiclesPrefix = "GC Factory Vehicles Only ";
    internal const string FriendlyAiOffPrefix = "GC Friendly AI Off ";
    internal const string BanVotePrefix = "GC Ban Vote ";
    internal const string BansPerSidePrefix = "GC Bans Per Side ";
    internal const string UnitCapPrefix = "GC Unit Cap ";
    internal const string ShipCapPrefix = "GC Ship Cap ";
    internal const string ArtilleryCapPrefix = "GC Artillery Cap ";
    internal const string AircraftCapPrefix = "GC Aircraft Cap ";
    internal const string BuildRadiusPrefix = "GC Build Radius ";
    internal const string DockRadiusPrefix = "GC Naval Dock Radius ";
    internal const string AircraftCapturePrefix = "GC Aircraft Capture ";

    internal const int DefaultBuilders = 2;
    internal const int MaxBuilders = 4;
    internal const int DefaultHeadStartPercent = 150;

    /// <summary>Every numbered prefix, parsed once per mission into <see cref="Cached"/>. None is the
    /// start of another, or a tag would read as both.</summary>
    private static readonly string[] NumberPrefixes =
    {
        BuildersPrefix, HeadStartPrefix, AiIncomePrefix, BaseIncomePrefix, AiCommandersPrefix, EnemyModePrefix,
        EnemyDifficultyPrefix, HumanTeamsPrefix, FactoryVehiclesPrefix, FriendlyAiOffPrefix,
        BanVotePrefix, BansPerSidePrefix, UnitCapPrefix, ShipCapPrefix, ArtilleryCapPrefix, AircraftCapPrefix,
        BuildRadiusPrefix, DockRadiusPrefix, AircraftCapturePrefix,
    };

    // Settings are read every frame and a tag's number is a substring parse. Every write of ours goes
    // through SetTag, which bumps the Revision, so a cache is never stale.
    private static readonly Dictionary<string, float> Cached = new();
    private static Mission? cachedFor;
    private static int cachedRevision = -1;

    /// <summary>Moves on every tag write of ours, so a cache of a mission's tags knows to re-read.</summary>
    internal static int Revision { get; private set; }

    /// <summary>A match is being played: the map's tags are obeyed, not edited.</summary>
    internal static bool InMatch => GameManager.gameState is GameState.SinglePlayer or GameState.Multiplayer or GameState.ServerWaiting;

    /// <summary>Only the map's own objectives end the match; losing the starting base is no defeat.</summary>
    internal static bool ObjectivesWinOnly =>
        CommanderFeatureGate.IsTaggedMission && HasTag(MissionManager.CurrentMission, ObjectivesWinTag);

    /// <summary>Free builders each side starts with.</summary>
    internal static int StartingBuilders =>
        Mathf.Clamp(OverrideInt(BuildersPrefix) ?? DefaultBuilders, 0, MaxBuilders);

    /// <summary>AI Commanders set to Off for this mission: no AI commander at all, on the mod's own
    /// maps too.</summary>
    internal static bool AiCommandersOff => OverrideInt(AiCommandersPrefix) == 0;

    /// <summary>The AI's opening balance as a multiple of its authored one, or null for the mode's own.</summary>
    internal static float? AiHeadStart => Override(HeadStartPrefix) / 100f;

    /// <summary>Flat AI income per minute, or null for the mode's own.</summary>
    internal static float? AiIncomePerMinute => Override(AiIncomePrefix);

    /// <summary>Income a minute every side gets as if from an income building it does not have; 0 for none.</summary>
    internal static int BaseIncomePerMinute => Mathf.Max(0, OverrideInt(BaseIncomePrefix) ?? 0);

    internal static void Touch()
    {
        Revision++;
    }

    /// <summary>The number the mission being played sets for <paramref name="prefix"/>. Only in a
    /// match: in the menu and the editor the config and the tags are edited, not obeyed. A stock
    /// mission carries no "GC " tags unless Customize Mission put them there.</summary>
    internal static float? Override(string prefix)
    {
        if (!InMatch)
        {
            return null;
        }

        Mission? mission = MissionManager.CurrentMission;
        if (!ReferenceEquals(mission, cachedFor) || cachedRevision != Revision)
        {
            cachedFor = mission;
            cachedRevision = Revision;
            Cached.Clear();
            for (int i = 0; i < NumberPrefixes.Length; i++)
            {
                if ((GetValue(mission, NumberPrefixes[i]) ?? StockValue(mission, NumberPrefixes[i])) is float value)
                {
                    Cached[NumberPrefixes[i]] = value;
                }
            }
        }

        return Cached.TryGetValue(prefix, out float found) ? found : null;
    }

    /// <summary>
    /// What an option with no tag means on a mission that is not a Ground Control map: the value
    /// that leaves the base game as it is, rather than the config's. Null
    /// on a Ground Control map, or for an option that changes nothing until RTS mode is used.
    /// </summary>
    internal static float? StockValue(Mission? mission, string prefix)
    {
        if (mission == null || IsGroundControlMap(mission))
        {
            return null;
        }

        return prefix is AiCommandersPrefix or HumanTeamsPrefix or FriendlyAiOffPrefix or BanVotePrefix
            or UnitCapPrefix or AircraftCapturePrefix
            ? 0f
            : null;
    }

    /// <summary>One of the mod's own missions or a map tagged GC RTS in the editor.</summary>
    internal static bool IsGroundControlMap(Mission mission)
    {
        return HasTag(mission, CommanderFeatureGate.MissionTag) || IsGroundControlName(mission.Name);
    }

    internal static bool IsGroundControlName(string? name)
    {
        return (name ?? string.Empty).IndexOf("Ground Control", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A change made in the match (the in-match settings' difficulty, enemy mode and human teams)
    /// also goes to the mission's own value when it has one, or the mission would keep overriding it.
    /// Returns the value so a setter can pass it on to the config.
    /// </summary>
    internal static int FollowInMatch(string prefix, int value)
    {
        if (Override(prefix) != null && MissionManager.CurrentMission != null)
        {
            SetValue(MissionManager.CurrentMission, prefix, value);
        }

        return value;
    }

    /// <summary>A yes/no override: 0 is off, anything else on.</summary>
    internal static bool? OverrideBool(string prefix)
    {
        return Override(prefix) is float value ? value != 0f : null;
    }

    /// <summary>A whole-number override.</summary>
    internal static int? OverrideInt(string prefix)
    {
        return Override(prefix) is float value ? Mathf.RoundToInt(value) : null;
    }

    internal static bool HasTag(Mission? mission, string tag)
    {
        List<MissionTag>? tags = mission?.missionSettings?.Tags;
        if (tags == null)
        {
            return false;
        }

        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i].Tag == tag)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds or removes the tag. The colour follows the game's own tags (35 % saturation,
    /// 80 % brightness), so the chip sits among them in the mission picker.</summary>
    internal static void SetTag(Mission mission, string tag, bool on)
    {
        List<MissionTag> tags = mission.missionSettings.Tags ??= new List<MissionTag>();
        tags.RemoveAll(existing => existing.Tag == tag);
        Revision++;
        if (on)
        {
            // Fields, not the constructor: a game patch added parameters to it, and a DLL built
            // against the old one threw MissingMethodException in plugin Awake (no CMD button).
            tags.Add(new MissionTag { Tag = tag, Color = Color.HSVToRGB(0.1f, 0.35f, 0.8f), SortOrder = 9 });
        }
    }

    /// <summary>The number after <paramref name="prefix"/> (its unit, "%" or " km", ignored), or null
    /// when the map does not set it. Not gated on the map being a GC RTS map: the editor reads it to
    /// fill its rows.</summary>
    internal static float? GetValue(Mission? mission, string prefix)
    {
        List<MissionTag>? tags = mission?.missionSettings?.Tags;
        if (tags == null)
        {
            return null;
        }

        for (int i = 0; i < tags.Count; i++)
        {
            string tag = tags[i].Tag;
            if (tag == null || !tag.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string number = tag.Substring(prefix.Length);
            int space = number.IndexOf(' ');
            number = (space >= 0 ? number.Substring(0, space) : number).TrimEnd('%');
            if (float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Writes the number, or removes it for null, so a map left at a default carries no tag.</summary>
    internal static void SetValue(Mission mission, string prefix, float? value, string suffix = "")
    {
        List<MissionTag> tags = mission.missionSettings.Tags ??= new List<MissionTag>();
        tags.RemoveAll(existing => existing.Tag != null && existing.Tag.StartsWith(prefix, StringComparison.Ordinal));
        if (value != null)
        {
            SetTag(mission, prefix + value.Value.ToString("0.##", CultureInfo.InvariantCulture) + suffix, true);
        }
    }

    /// <summary>Settings tags the mission picker leaves out of its filters and its rows.</summary>
    internal static bool IsHiddenTag(string? tag)
    {
        return tag != null
            && tag.StartsWith(CommanderEconomyService.MissionNamePrefix, StringComparison.Ordinal)
            && tag != CommanderFeatureGate.MissionTag;
    }

    /// <summary>Run at plugin load: a number written is the number read back, no prefix swallows
    /// another's tag, and the picker keeps the tag players filter on.</summary>
    internal static void SelfCheck()
    {
        Mission mission = new() { missionSettings = new MissionSettings() };
        bool ok = GetValue(mission, HeadStartPrefix) == null;
        SetValue(mission, HeadStartPrefix, 250, "%");
        SetValue(mission, HeadStartPrefix, 300, "%");
        SetValue(mission, BuildRadiusPrefix, 2.5f, " km");
        ok &= mission.missionSettings.Tags.Count == 2 && mission.missionSettings.Tags[0].Tag == "GC AI Head Start 300%" && GetValue(mission, HeadStartPrefix) == 300f;
        ok &= mission.missionSettings.Tags[1].Tag == "GC Build Radius 2.5 km" && GetValue(mission, BuildRadiusPrefix) == 2.5f;
        SetValue(mission, HeadStartPrefix, null);
        SetValue(mission, BuildRadiusPrefix, null);
        ok &= mission.missionSettings.Tags.Count == 0;
        for (int i = 0; i < NumberPrefixes.Length; i++)
        {
            for (int j = 0; j < NumberPrefixes.Length; j++)
            {
                ok &= i == j || !NumberPrefixes[i].StartsWith(NumberPrefixes[j], StringComparison.Ordinal);
            }
        }

        ok &= IsHiddenTag("GC Builders 3") && IsHiddenTag("GC Champions") && !IsHiddenTag(CommanderFeatureGate.MissionTag) && !IsHiddenTag("PvP");
        if (!ok)
        {
            CommanderPlugin.Log.LogError("CommanderMapSettings self-check failed: map settings tags do not round-trip.");
        }
    }
}
