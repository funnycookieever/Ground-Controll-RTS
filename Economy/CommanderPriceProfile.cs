using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The player's own economy: a price per unit or building, a depot tier per ground vehicle and an
/// income multiplier, saved as a named profile in <c>BepInEx/config/GroundControlRts/Economy</c>.
/// A profile only lists what the player changed, never a copy of the defaults, so a later balance
/// pass in the mod still reaches every unit the player left alone.
/// <para>
/// In multiplayer only the host's profile counts. A guest ignores its own and asks the host for its
/// table whenever the revision in the sync changes; a host's purchase is charged at the host's
/// <c>definition.value</c> anyway, so this only keeps the guest's prices honest.
/// </para>
/// </summary>
internal static class CommanderPriceProfile
{
    /// <summary>The built-in profile: the mod's own prices, no file.</summary>
    internal const string StandardName = "STANDARD";
    internal const float MinPrice = 0.1f;
    internal const float MaxPrice = 100000f;
    internal const float MinIncome = 0.1f;
    internal const float MaxIncome = 5f;
    private const string Extension = ".txt";
    private const float RequestRetrySeconds = 5f;

    /// <summary>A map's own prices: one hidden mission tag, "GC Prices name; income = 1; unit X = 50".</summary>
    private const string MissionTagPrefix = "GC Prices ";

    private static readonly Table Own = new();
    private static readonly Table Empty = new();
    private static readonly List<string> NameScratch = new();
    private static readonly Table MapTable = new();
    private static readonly Table Scratch = new();
    private static Mission? mapFor;
    private static int mapRevision = -1;
    private static bool hasMap;
    private static string mapName = string.Empty;
    private static Table? host;
    private static string hostRevision = string.Empty;
    private static float lastRequestAt = -100f;

    internal static string ProfileName { get; private set; } = StandardName;

    /// <summary>Moves on every change to either table, so a cached list knows to rebuild.</summary>
    internal static int Revision { get; private set; } = 1;

    /// <summary>The host's profile name, as a guest last heard it; empty until the table arrives.</summary>
    internal static string HostProfileName { get; private set; } = string.Empty;

    internal static bool IsStandard => ProfileName == StandardName;

    /// <summary>A guest reads the host's table, or the mod's prices until it arrives. Everyone else
    /// reads the map's own prices in a match on a map that has them, else their own profile.</summary>
    internal static Table Active => CommanderNetwork.IsGuest ? host ?? Empty : Map ?? Own;

    internal static float IncomeMultiplier => Active.Income;

    internal static string Folder => Path.Combine(Path.Combine(Paths.ConfigPath, "GroundControlRts"), "Economy");

    /// <summary>The prices of the mission being played, re-read whenever its tags change.</summary>
    private static Table? Map
    {
        get
        {
            Mission? mission = CommanderMapSettings.InMatch ? MissionManager.CurrentMission : null;
            if (!ReferenceEquals(mission, mapFor) || mapRevision != CommanderMapSettings.Revision)
            {
                mapFor = mission;
                mapRevision = CommanderMapSettings.Revision;
                bool had = hasMap;
                hasMap = TryReadMission(mission, MapTable, out mapName);
                if (had || hasMap)
                {
                    Revision++;
                    CommanderEconomyService.InvalidatePrices();
                }
            }

            return hasMap ? MapTable : null;
        }
    }

    /// <summary>Once at plugin load: reads the saved profile and teaches the network the three verbs.</summary>
    internal static void Initialize()
    {
        string saved = CommanderSettings.EconomyProfile;
        Load(string.IsNullOrWhiteSpace(saved) ? StandardName : saved);
        CommanderNetwork.OnSync(static (_, messages) => messages.Add(new[] { "eco.rev", CommanderNetwork.Num(Revision) }));
        CommanderNetwork.OnGuest("eco.rev", OnHostRevision);
        CommanderNetwork.OnHost("eco.get", static (_, _) => CommanderNetwork.Reply("eco.table", Pack()));
        CommanderNetwork.OnGuest("eco.table", OnHostTable);
    }

    /// <summary>Scene change: a guest forgets the last host's table and asks the next one.</summary>
    internal static void ResetSession()
    {
        host = null;
        HostProfileName = string.Empty;
        hostRevision = string.Empty;
        lastRequestAt = -100f;
    }

    internal static bool TryGetUnitPrice(string name, out float price)
    {
        return Active.Prices.TryGetValue(name, out price);
    }

    internal static bool TryGetTier(string name, out int tier)
    {
        return Active.Tiers.TryGetValue(name, out tier);
    }

    internal static bool TryGetBuildingPrice(string name, out float price)
    {
        return Active.Buildings.TryGetValue(name, out price);
    }

    internal static int ChangeCount(Table table)
    {
        return table.Prices.Count + table.Tiers.Count + table.Buildings.Count + (Mathf.Approximately(table.Income, 1f) ? 0 : 1);
    }

    /// <summary>A price equal to the default clears the override, so the file never holds a copy of a default.</summary>
    internal static void SetUnitPrice(string name, float price, float defaultPrice)
    {
        Edit(Own.Prices, name, Mathf.Clamp(price, MinPrice, MaxPrice), Mathf.Approximately(price, defaultPrice));
    }

    internal static void SetTier(string name, int tier, int defaultTier)
    {
        Edit(Own.Tiers, name, Mathf.Clamp(tier, 1, CommanderEconomyService.DepotMaxLevel), tier == defaultTier);
    }

    internal static void SetBuildingPrice(string name, float price, float defaultPrice)
    {
        Edit(Own.Buildings, name, Mathf.Clamp(price, MinPrice, MaxPrice), Mathf.Approximately(price, defaultPrice));
    }

    internal static void SetIncome(float multiplier)
    {
        float clamped = Mathf.Clamp(multiplier, MinIncome, MaxIncome);
        if (Mathf.Approximately(clamped, Own.Income))
        {
            return;
        }

        EnsureEditable();
        Own.Income = clamped;
        Changed();
    }

    internal static void ResetUnit(string name)
    {
        if (Own.Prices.Remove(name) | Own.Tiers.Remove(name) | Own.Buildings.Remove(name))
        {
            Changed();
        }
    }

    internal static void ResetAll()
    {
        Own.Clear();
        Changed();
    }

    /// <summary>STANDARD first, then every saved profile by name.</summary>
    internal static List<string> ListProfiles()
    {
        NameScratch.Clear();
        NameScratch.Add(StandardName);
        if (Directory.Exists(Folder))
        {
            string[] files = Directory.GetFiles(Folder, "*" + Extension);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                NameScratch.Add(Path.GetFileNameWithoutExtension(files[i]));
            }
        }

        return NameScratch;
    }

    internal static void Load(string name)
    {
        Own.Clear();
        ProfileName = StandardName;
        string path = PathOf(name);
        if (name != StandardName && File.Exists(path))
        {
            try
            {
                Parse(File.ReadAllText(path), Own);
                ProfileName = name;
            }
            catch (IOException exception)
            {
                CommanderPlugin.Log.LogWarning($"Economy profile '{name}' could not be read ({exception.Message}); using the standard prices.");
            }
        }

        CommanderSettings.EconomyProfile = ProfileName;
        Changed(save: false);
    }

    /// <summary>A copy of the current profile under a new name, which becomes the active one.</summary>
    internal static void Duplicate()
    {
        ProfileName = UniqueName(IsStandard ? "My prices" : ProfileName);
        CommanderSettings.EconomyProfile = ProfileName;
        Save();
        Revision++;
    }

    /// <summary>False when the name is empty, taken or not a file name.</summary>
    internal static bool Rename(string newName)
    {
        newName = newName.Trim();
        if (IsStandard || newName.Length == 0 || newName == ProfileName || !IsValidName(newName) || File.Exists(PathOf(newName)))
        {
            return false;
        }

        string old = PathOf(ProfileName);
        ProfileName = newName;
        CommanderSettings.EconomyProfile = newName;
        Save();
        if (File.Exists(old))
        {
            File.Delete(old);
        }

        // Guests show the host's profile name, so a rename is news to them too.
        Revision++;
        return true;
    }

    internal static void DeleteCurrent()
    {
        if (!IsStandard && File.Exists(PathOf(ProfileName)))
        {
            File.Delete(PathOf(ProfileName));
        }

        Load(StandardName);
    }

    internal static void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Application.OpenURL("file:///" + Folder.Replace('\\', '/'));
    }

    internal static bool IsValidName(string name)
    {
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != StandardName;
    }

    /// <summary>"name (changes)" for a mission that carries its own prices, else null.</summary>
    internal static string? DescribeMission(Mission? mission)
    {
        return TryReadMission(mission, Scratch, out string name) ? $"{name} ({ChangeCount(Scratch)})" : null;
    }

    /// <summary>
    /// Opens UNIT PRICES on a mission's own prices: they are loaded as a profile (an existing file
    /// with the same prices, else a new one), and on close the profile shown is written into the
    /// mission (STANDARD removes it) and the player's own profile is put back.
    /// </summary>
    internal static void EditMission(Mission mission, Action? closed)
    {
        if (CommanderPriceEditorUi.IsOpen)
        {
            return;
        }

        string previous = ProfileName;
        Table table = new();
        Load(TryReadMission(mission, table, out string name) ? Import(name, table) : StandardName);
        CommanderPriceEditorUi.Open(() =>
        {
            WriteMission(mission, IsStandard ? null : ProfileName, Own);
            Load(previous);
            closed?.Invoke();
        });
    }

    /// <summary>Asserts a profile survives the file and the network round trip.</summary>
    internal static void SelfCheck()
    {
        Table table = new() { Income = 1.25f };
        table.Prices["VT-7 Vagrant"] = 55.5f;
        table.Tiers["Spearhead MBT"] = 1;
        table.Buildings["Helipad"] = 80f;
        Table fromFile = new();
        Parse(Serialize(table), fromFile);
        Table fromWire = new();
        string[] wire = new string[PackSize(table) + 1];
        wire[0] = "eco.table";
        Pack(table, "x", 7).CopyTo(wire, 1);
        Unpack(wire, fromWire);
        bool ok = Same(table, fromFile) && Same(table, fromWire);
        Mission mission = new() { missionSettings = new MissionSettings() };
        WriteMission(mission, "Map; test", table);
        WriteMission(mission, "Map; test", table);
        Table fromMission = new();
        ok &= mission.missionSettings.Tags.Count == 1 && TryReadMission(mission, fromMission, out string readName) && readName == "Map  test" && Same(table, fromMission);
        if (ok)
        {
            CommanderPlugin.Log.LogInfo("Economy profile self-check passed.");
        }
        else
        {
            CommanderPlugin.Log.LogError("Economy profile self-check FAILED: a profile does not survive the file or the network round trip.");
        }
    }

    /// <summary>Replaces the mission's prices with <paramref name="table"/>, or removes them for a null name or an empty table.</summary>
    private static void WriteMission(Mission mission, string? name, Table table)
    {
        List<MissionTag> tags = mission.missionSettings.Tags ??= new List<MissionTag>();
        tags.RemoveAll(static tag => tag.Tag != null && tag.Tag.StartsWith(MissionTagPrefix, StringComparison.Ordinal));
        CommanderMapSettings.Touch();
        if (name == null || ChangeCount(table) == 0)
        {
            return;
        }

        StringBuilder text = new(MissionTagPrefix);
        text.Append(name.Replace(';', ' ').Replace('=', ' '));
        foreach (string line in Serialize(table).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#')
            {
                text.Append("; ").Append(trimmed);
            }
        }

        CommanderMapSettings.SetTag(mission, text.ToString(), true);
    }

    private static bool TryReadMission(Mission? mission, Table into, out string name)
    {
        into.Clear();
        name = string.Empty;
        List<MissionTag>? tags = mission?.missionSettings?.Tags;
        for (int i = 0; tags != null && i < tags.Count; i++)
        {
            string tag = tags[i].Tag;
            if (tag != null && tag.StartsWith(MissionTagPrefix, StringComparison.Ordinal))
            {
                string body = tag.Substring(MissionTagPrefix.Length);
                int end = body.IndexOf(';');
                name = (end < 0 ? body : body.Substring(0, end)).Trim();
                Parse(body.Replace(';', '\n'), into);
                return true;
            }
        }

        return false;
    }

    /// <summary>The name of a profile holding exactly <paramref name="table"/>: an existing one, or a new file.</summary>
    private static string Import(string name, Table table)
    {
        string stem = name.Length > 0 && IsValidName(name) ? name : "Map prices";
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? stem : $"{stem} {i}";
            string path = PathOf(candidate);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, Serialize(table));
                return candidate;
            }

            Table existing = new();
            Parse(File.ReadAllText(path), existing);
            if (Same(existing, table))
            {
                return candidate;
            }
        }
    }

    private static void Edit<T>(Dictionary<string, T> into, string name, T value, bool isDefault)
    {
        if (isDefault)
        {
            if (into.Remove(name))
            {
                Changed();
            }

            return;
        }

        if (into.TryGetValue(name, out T existing) && EqualityComparer<T>.Default.Equals(existing, value))
        {
            return;
        }

        EnsureEditable();
        into[name] = value;
        Changed();
    }

    /// <summary>STANDARD is read-only: the first edit on it starts a new profile rather than being lost.</summary>
    private static void EnsureEditable()
    {
        if (IsStandard)
        {
            ProfileName = UniqueName("My prices");
            CommanderSettings.EconomyProfile = ProfileName;
        }
    }

    private static void Changed(bool save = true)
    {
        if (save && !IsStandard)
        {
            Save();
        }

        Revision++;
        CommanderEconomyService.InvalidatePrices();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathOf(ProfileName), Serialize(Own));
        }
        catch (IOException exception)
        {
            CommanderPlugin.Log.LogWarning($"Economy profile '{ProfileName}' could not be saved: {exception.Message}");
        }
    }

    private static string UniqueName(string stem)
    {
        string name = stem;
        for (int i = 2; File.Exists(PathOf(name)); i++)
        {
            name = $"{stem} {i}";
        }

        return name;
    }

    private static string PathOf(string name)
    {
        return Path.Combine(Folder, name + Extension);
    }

    private static string Serialize(Table table)
    {
        StringBuilder text = new();
        text.AppendLine("# Ground Control economy profile. Prices are in millions (1000 = 1 B).");
        text.AppendLine("# Only changed values are listed: anything missing costs what the mod says.");
        text.AppendLine("# Edit it in game under COMMANDER SETTINGS > PRICES, or here by hand.");
        text.Append("income = ").AppendLine(Number(table.Income));
        AppendSection(text, "unit", table.Prices);
        foreach (KeyValuePair<string, int> tier in Sorted(table.Tiers))
        {
            text.Append("tier ").Append(tier.Key).Append(" = ").AppendLine(tier.Value.ToString(CultureInfo.InvariantCulture));
        }

        AppendSection(text, "building", table.Buildings);
        return text.ToString();
    }

    private static void AppendSection(StringBuilder text, string keyword, Dictionary<string, float> entries)
    {
        foreach (KeyValuePair<string, float> entry in Sorted(entries))
        {
            text.Append(keyword).Append(' ').Append(entry.Key).Append(" = ").AppendLine(Number(entry.Value));
        }
    }

    private static List<KeyValuePair<string, T>> Sorted<T>(Dictionary<string, T> entries)
    {
        List<KeyValuePair<string, T>> list = new(entries);
        list.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Lines are "keyword name = value"; anything else, comments included, is skipped.</summary>
    private static void Parse(string text, Table into)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int equals = line.LastIndexOf('=');
            if (line.Length == 0 || line[0] == '#' || equals < 0)
            {
                continue;
            }

            string head = line.Substring(0, equals).Trim();
            string value = line.Substring(equals + 1).Trim();
            int space = head.IndexOf(' ');
            string keyword = space < 0 ? head : head.Substring(0, space);
            string name = space < 0 ? string.Empty : head.Substring(space + 1).Trim();
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
            {
                continue;
            }

            switch (keyword)
            {
                case "income":
                    into.Income = Mathf.Clamp(number, MinIncome, MaxIncome);
                    break;
                case "unit" when name.Length > 0:
                    into.Prices[name] = Mathf.Clamp(number, MinPrice, MaxPrice);
                    break;
                case "tier" when name.Length > 0:
                    into.Tiers[name] = Mathf.Clamp(Mathf.RoundToInt(number), 1, CommanderEconomyService.DepotMaxLevel);
                    break;
                case "building" when name.Length > 0:
                    into.Buildings[name] = Mathf.Clamp(number, MinPrice, MaxPrice);
                    break;
            }
        }
    }

    private static string Number(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string[] Pack()
    {
        Table active = Active;
        return Pack(active, ReferenceEquals(active, MapTable) ? mapName : ProfileName, Revision);
    }

    private static int PackSize(Table table)
    {
        return 3 + ((table.Prices.Count + table.Tiers.Count + table.Buildings.Count) * 3);
    }

    /// <summary>revision, profile name, income, then (kind, name, value) triples.</summary>
    private static string[] Pack(Table table, string name, int rev)
    {
        string[] args = new string[PackSize(table)];
        args[0] = CommanderNetwork.Num(rev);
        args[1] = name;
        args[2] = CommanderNetwork.Num(table.Income);
        int at = 3;
        foreach (KeyValuePair<string, float> entry in table.Prices)
        {
            args[at++] = "u";
            args[at++] = entry.Key;
            args[at++] = CommanderNetwork.Num(entry.Value);
        }

        foreach (KeyValuePair<string, int> entry in table.Tiers)
        {
            args[at++] = "t";
            args[at++] = entry.Key;
            args[at++] = CommanderNetwork.Num(entry.Value);
        }

        foreach (KeyValuePair<string, float> entry in table.Buildings)
        {
            args[at++] = "b";
            args[at++] = entry.Key;
            args[at++] = CommanderNetwork.Num(entry.Value);
        }

        return args;
    }

    /// <summary><paramref name="args"/>[0] is the verb, as a handler receives it.</summary>
    private static void Unpack(string[] args, Table into)
    {
        into.Clear();
        into.Income = Mathf.Clamp(CommanderNetwork.Float(args, 3), MinIncome, MaxIncome);
        for (int i = 4; i + 2 < args.Length; i += 3)
        {
            string name = args[i + 1];
            switch (args[i])
            {
                case "u":
                    into.Prices[name] = Mathf.Clamp(CommanderNetwork.Float(args, i + 2), MinPrice, MaxPrice);
                    break;
                case "t":
                    into.Tiers[name] = Mathf.Clamp(CommanderNetwork.Int(args, i + 2), 1, CommanderEconomyService.DepotMaxLevel);
                    break;
                case "b":
                    into.Buildings[name] = Mathf.Clamp(CommanderNetwork.Float(args, i + 2), MinPrice, MaxPrice);
                    break;
            }
        }
    }

    /// <summary>Asks for the host's table when the revision moved, at most once per retry window.</summary>
    private static void OnHostRevision(string[] args)
    {
        string rev = CommanderNetwork.Arg(args, 1);
        if ((host != null && rev == hostRevision) || Time.realtimeSinceStartup - lastRequestAt < RequestRetrySeconds)
        {
            return;
        }

        lastRequestAt = Time.realtimeSinceStartup;
        CommanderNetwork.Forward("eco.get");
    }

    private static void OnHostTable(string[] args)
    {
        Table table = new();
        Unpack(args, table);
        host = table;
        hostRevision = CommanderNetwork.Arg(args, 1);
        HostProfileName = CommanderNetwork.Arg(args, 2);
        CommanderPlugin.Log.LogInfo($"Using the host's economy profile '{HostProfileName}' ({ChangeCount(table)} changes).");
        CommanderEconomyService.InvalidatePrices();
    }

    private static bool Same(Table a, Table b)
    {
        return Mathf.Approximately(a.Income, b.Income)
            && SameEntries(a.Prices, b.Prices)
            && SameEntries(a.Tiers, b.Tiers)
            && SameEntries(a.Buildings, b.Buildings);
    }

    private static bool SameEntries<T>(Dictionary<string, T> a, Dictionary<string, T> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, T> entry in a)
        {
            if (!b.TryGetValue(entry.Key, out T other) || !EqualityComparer<T>.Default.Equals(entry.Value, other))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One profile's overrides, keyed on the game's unit and building names.</summary>
    internal sealed class Table
    {
        internal Dictionary<string, float> Prices { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, int> Tiers { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, float> Buildings { get; } = new(StringComparer.Ordinal);

        internal float Income { get; set; } = 1f;

        internal void Clear()
        {
            Prices.Clear();
            Tiers.Clear();
            Buildings.Clear();
            Income = 1f;
        }
    }
}
