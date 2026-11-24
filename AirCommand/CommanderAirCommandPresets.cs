using System;
using System.Collections.Generic;
using System.Text;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Loadout presets: one click to arm an airframe instead of hunting through a weapon catalogue.
/// </summary>
/// <remarks>
/// Three sources, in this order, all reduced to the same thing (one weapon <c>jsonKey</c> per
/// hardpoint set) so applying one is a single code path:
/// <list type="number">
/// <item><b>Role presets</b>, generated here from the mounts the airframe actually offers. The
/// discriminator is asset data the mod can read at runtime and must not hardcode:
/// <c>WeaponInfo.targetRequirements.minRadar &gt; 0</c> is a radar-guided store,
/// <c>minIR &gt; 0</c> is a heat seeker, and <c>maxRange</c> orders them. So "LONG-RANGE FIGHTER (RADAR)"
/// resolves to whatever the longest-reaching radar missile on that aeroplane happens to be rather
/// than to a weapon name a game patch could rename.</item>
/// <item><b>The game's own standard loadouts</b> (<c>AircraftParameters.StandardLoadouts</c>), which
/// are authored per airframe and already named.</item>
/// <item><b>Saved presets</b>, whatever the player built in the advanced tab and pressed SAVE on,
/// kept in the BepInEx config so they outlive the session.</item>
/// </list>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    private static readonly char[] PresetLineSeparator = { '\n' };
    private static readonly char[] PresetNameBreaks = { '\t', '\n', '\r' };

    private readonly List<LoadoutPreset> presets = new();
    private readonly List<LoadoutPreset> customPresets = new();

    /// <summary>Per preset: 1 when the chosen mission can fire something on it, -1 when it cannot,
    /// 0 for a mode with no weapon rule. Rebuilt with the list.</summary>
    private readonly List<int> presetFit = new();
    private bool customPresetsLoaded;
    private int bestPresetIndex = -1;

    /// <summary>Set once the player clicks a preset. Until then the mission picks it: choosing CAS
    /// arms the aircraft for CAS, which is the answer a new player expects.</summary>
    private bool presetChosenByPlayer;

    internal IReadOnlyList<LoadoutPreset> Presets => presets;

    internal int SelectedPresetIndex { get; private set; } = -1;

    /// <summary>False is the preset tab, which is the default: presets are the easy path.</summary>
    internal bool AdvancedLoadout { get; private set; }

    internal static bool IsAirToAir(WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        return info != null && !info.jammer && !info.nuclear && !info.gun && info.effectiveness.antiAir > 0.05f;
    }

    /// <summary>
    /// One preset per line, tab separated: aircraft, name, then a key per hardpoint. An empty key is
    /// an empty pylon and has to survive the trip, which is why this is a plain split and not
    /// <c>RemoveEmptyEntries</c> — losing one empty field shifts every weapon behind it onto the
    /// wrong pylon.
    /// </summary>
    internal static string SerializePresets(List<LoadoutPreset> saved)
    {
        StringBuilder builder = new();
        for (int i = 0; i < saved.Count; i++)
        {
            LoadoutPreset preset = saved[i];
            builder.Append(preset.DefinitionKey).Append('\t').Append(preset.Name);
            for (int key = 0; key < preset.MountKeys.Count; key++)
            {
                builder.Append('\t').Append(preset.MountKeys[key]);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    internal static List<LoadoutPreset> ParsePresets(string? stored)
    {
        List<LoadoutPreset> parsed = new();
        string[] lines = (stored ?? string.Empty).Split(PresetLineSeparator, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            List<string> keys = new();
            for (int part = 2; part < parts.Length; part++)
            {
                keys.Add(parts[part]);
            }

            parsed.Add(new LoadoutPreset(parts[1], keys, custom: true) { DefinitionKey = parts[0] });
        }

        return parsed;
    }

    /// <summary>
    /// Round-trips a saved preset through the config string, run once at load beside the other
    /// Air Command self-checks. Empty pylons in the middle of a rack are the case that breaks, and
    /// a preset that silently shifts its weapons one pylon left is invisible until an aeroplane
    /// takes off wrong.
    /// </summary>
    internal static void SelfCheckPresets()
    {
        List<LoadoutPreset> saved = new()
        {
            new LoadoutPreset("LONG RANGE", new List<string> { "scimitar", string.Empty, "scimitar" }, custom: true)
            {
                DefinitionKey = "Compass",
            },
            new LoadoutPreset("EMPTY MIDDLE", new List<string> { string.Empty, "ms3", string.Empty }, custom: true)
            {
                DefinitionKey = "Ifrit",
            },
            new LoadoutPreset(CleanPresetName(" PASTED\tNAME\r\n"), new List<string> { "ms3" }, custom: true)
            {
                DefinitionKey = "Ifrit",
            },
        };

        List<LoadoutPreset> parsed = ParsePresets(SerializePresets(saved));
        bool failed = parsed.Count != saved.Count || saved[2].Name != "PASTED NAME";
        for (int i = 0; !failed && i < saved.Count; i++)
        {
            failed = parsed[i].DefinitionKey != saved[i].DefinitionKey
                || parsed[i].Name != saved[i].Name
                || !parsed[i].SameStores(saved[i]);
        }

        if (failed)
        {
            CommanderPlugin.Log.LogError(
                "Air Command self-check FAILED: saved loadout presets do not survive being stored.");
            return;
        }

        CommanderPlugin.Log.LogInfo("Air Command self-check passed: saved loadout presets round-trip.");
    }

    internal void SetAdvancedLoadout(bool advanced)
    {
        if (AdvancedLoadout == advanced)
        {
            return;
        }

        AdvancedLoadout = advanced;
        if (advanced)
        {
            // The editor takes over the pylons; a preset that stayed set would silently win at
            // launch. It starts from the preset the player was looking at, not from whatever it last
            // held, so the expert path begins where the easy one left off.
            AirMissionOption? option = SelectedOption;
            if (option?.Preset != null)
            {
                SeedEditorFromPreset(option, option.Preset);
            }
            else
            {
                ApplySelectedWeapons();
            }
        }
        else
        {
            SelectPreset(Mathf.Max(SelectedPresetIndex, 0));
        }
    }

    /// <summary>A preset the player clicked, which from then on the mission type leaves alone.</summary>
    internal void ChoosePreset(int index)
    {
        presetChosenByPlayer = true;
        SelectPreset(index);
    }

    internal void SelectPreset(int index)
    {
        AirMissionOption? option = SelectedOption;
        if (option == null || index < 0 || index >= presets.Count)
        {
            SelectedPresetIndex = -1;
            return;
        }

        SelectedPresetIndex = index;
        AdvancedLoadout = false;
        option.Preset = presets[index];
    }

    /// <summary>Stores what the editor is currently showing, under a name, for good, and says on
    /// the status line whether it did.</summary>
    internal bool TrySaveCurrentAsPreset(string name, out string message)
    {
        bool saved = SaveCurrentAsPreset(name, out message);
        presetChosenByPlayer |= saved;
        SetStatus(message);
        return saved;
    }

    internal bool HasPreset(LoadoutPreset preset)
    {
        return presets.Contains(preset);
    }

    /// <summary>Renames a saved preset for good, and says on the status line whether it did.</summary>
    internal bool TryRenamePreset(LoadoutPreset preset, string name)
    {
        name = CleanPresetName(name);
        if (name.Length == 0)
        {
            SetStatus("Give the preset a name first.");
            return false;
        }

        // AddPreset skips a repeated name, so a duplicate here would drop this preset off the list.
        for (int i = 0; i < presets.Count; i++)
        {
            if (!ReferenceEquals(presets[i], preset)
                && string.Equals(presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"Already a preset: {presets[i].Name}.");
                return false;
            }
        }

        int at = presets.IndexOf(preset);
        preset.Name = name;
        SaveCustomPresets();
        if (at >= 0)
        {
            MovePreset(at, at, save: true);
        }

        SetStatus($"Preset renamed to \"{name}\".");
        return true;
    }

    internal void DeletePreset(int index)
    {
        if (index < 0 || index >= presets.Count || !presets[index].Custom)
        {
            return;
        }

        string name = presets[index].Name;
        LoadCustomPresets();
        customPresets.Remove(presets[index]);
        SaveCustomPresets();
        RefreshPresets();
        SetStatus($"Preset \"{name}\" deleted.");
    }

    /// <summary>Moves one row of the PRESETS list; <paramref name="save"/> keeps the whole order
    /// for this aircraft, so it is still there next match.</summary>
    internal void MovePreset(int from, int to, bool save)
    {
        AirMissionOption? option = SelectedOption;
        if (option == null || from < 0 || from >= presets.Count || to < 0 || to >= presets.Count)
        {
            return;
        }

        LoadoutPreset? selected = SelectedPresetIndex >= 0 ? presets[SelectedPresetIndex] : null;
        LoadoutPreset moved = presets[from];
        presets.RemoveAt(from);
        presets.Insert(to, moved);
        SelectedPresetIndex = selected != null ? presets.IndexOf(selected) : -1;
        if (from != to)
        {
            RatePresets(option);
        }

        if (!save)
        {
            return;
        }

        string key = GetDefinitionKey(option.Definition);
        StringBuilder builder = new();
        string[] lines = CommanderSettings.AirLoadoutPresetOrder.Split(PresetLineSeparator, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Split('\t')[0] != key)
            {
                builder.Append(lines[i]).Append('\n');
            }
        }

        builder.Append(key);
        for (int i = 0; i < presets.Count; i++)
        {
            builder.Append('\t').Append(presets[i].Name);
        }

        CommanderSettings.AirLoadoutPresetOrder = builder.Append('\n').ToString();
    }

    /// <summary>What the advanced tab is currently hanging on the pylons, in words.</summary>
    internal string DescribeCurrentLoadout()
    {
        AirMissionOption? option = SelectedOption;
        if (option == null)
        {
            return "empty";
        }

        LoadoutPreset? previousPreset = option.Preset;
        option.Preset = null;
        Loadout loadout = option.BuildLoadout();
        option.Preset = previousPreset;
        return LoadoutPreset
            .FromLoadout(string.Empty, loadout, option.HardpointSets, custom: false)
            .Describe(option.HardpointSets);
    }

    /// <summary>The name, a BEST FOR / CAN'T DO tag against the chosen mission, and what it hangs on
    /// the pylons.</summary>
    internal string GetPresetLabel(int index)
    {
        LoadoutPreset preset = presets[index];
        string mode = GetModeShortLabel(SelectedMode);
        string tag = index == bestPresetIndex
            ? $"   [BEST FOR {mode}]"
            : index < presetFit.Count && presetFit[index] < 0
                ? $"   [CAN'T DO {mode}]"
                : string.Empty;
        string saved = preset.Custom ? "SAVED  |  " : string.Empty;
        return $"{preset.Name}{tag}\n{saved}{preset.Describe(SelectedOption?.HardpointSets)}";
    }

    /// <summary>
    /// Puts a preset's exact pylons on the hardpoint groups, where the editor works, instead of
    /// re-running the allocator from its two main weapons, which could lay them out differently.
    /// </summary>
    private static void WearPylons(AirMissionOption option, LoadoutPreset preset)
    {
        option.Preset = null;
        for (int g = 0; g < option.HardpointGroups.Count; g++)
        {
            AirHardpointGroup group = option.HardpointGroups[g];
            int set = group.HardpointIndices[0];
            string key = set < preset.MountKeys.Count ? preset.MountKeys[set] : string.Empty;
            group.Select(group.Mounts.FindIndex(mount => string.Equals(mount.jsonKey, key, StringComparison.Ordinal)));
        }
    }

    /// <summary>The mounts this airframe can carry, pooled across its hardpoint groups.</summary>
    private static List<WeaponMount> CollectAirframeMounts(AirMissionOption option)
    {
        List<WeaponMount> mounts = new();
        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            List<WeaponMount> groupMounts = option.HardpointGroups[groupIndex].Mounts;
            for (int i = 0; i < groupMounts.Count; i++)
            {
                if (!mounts.Contains(groupMounts[i]))
                {
                    mounts.Add(groupMounts[i]);
                }
            }
        }

        return mounts;
    }

    private static WeaponManager? GetPrefabWeaponManager(AircraftDefinition definition)
    {
        return definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager
            : null;
    }

    private static bool ContainsCargo(Loadout loadout)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount != null && (mount.Cargo || mount.Troops || mount.info?.cargo == true || mount.info?.troops == true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The best air-to-air mount whose weapon is called <paramref name="name"/>.</summary>
    private static WeaponMount? PickNamedMount(List<WeaponMount> mounts, string name)
    {
        return PickBestMount(
            mounts,
            mount => (mount.info?.weaponName ?? string.Empty).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0,
            mount => ScoreMount(mount, AirCommandMode.AirGuard));
    }

    private static WeaponMount? PickBestMount(
        List<WeaponMount> mounts, Func<WeaponMount, bool> accept, Func<WeaponMount, float> score)
    {
        WeaponMount? best = null;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < mounts.Count; i++)
        {
            WeaponMount mount = mounts[i];
            if (!accept(mount))
            {
                continue;
            }

            float value = score(mount);
            if (value > bestScore)
            {
                bestScore = value;
                best = mount;
            }
        }

        return best;
    }

    private static float WeaponRange(WeaponMount mount)
    {
        return mount.info?.targetRequirements.maxRange ?? 0f;
    }

    private static bool IsRadarAirToAir(WeaponMount mount)
    {
        return IsAirToAir(mount) && mount.info!.targetRequirements.minRadar > 0f;
    }

    private static bool IsHeatAirToAir(WeaponMount mount)
    {
        return IsAirToAir(mount) && mount.info!.targetRequirements.minIR > 0f;
    }

    private static bool IsAntiSurface(WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        return info != null
            && !info.jammer
            && !info.nuclear
            && !info.gun
            && !IsStrategicStrikeWeapon(info, mount)
            && info.effectiveness.antiSurface > 0.05f;
    }

    private static int PresetRank(LoadoutPreset preset, List<string> order)
    {
        int at = order.FindIndex(name => string.Equals(name, preset.Name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? at : preset.Custom ? -1 : int.MaxValue;
    }

    private static List<string> ReadPresetOrder(string? stored, string definitionKey)
    {
        string[] lines = (stored ?? string.Empty).Split(PresetLineSeparator, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split('\t');
            if (parts[0] == definitionKey)
            {
                return new List<string>(parts).GetRange(1, parts.Length - 1);
            }
        }

        return new List<string>();
    }

    /// <summary>A pasted name can hold the tab and newline the config line is split on.</summary>
    private static string CleanPresetName(string name)
    {
        return string.Join(" ", name.Split(PresetNameBreaks, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string GetDefinitionKey(AircraftDefinition definition)
    {
        return definition.name;
    }

    /// <summary>
    /// Opens the editor on a preset: its most-carried store becomes the primary pick, the next the
    /// secondary, any cargo the cargo pick, and the pylons are the preset's own.
    /// </summary>
    private void SeedEditorFromPreset(AirMissionOption option, LoadoutPreset preset)
    {
        Loadout loadout = preset.Build(option.HardpointSets);
        List<string> keys = new();
        List<int> stores = new();
        string? cargoKey = null;
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];

            // Guns come from the cannons toggle, not from a pick.
            if (mount == null || mount.info?.gun == true)
            {
                continue;
            }

            string key = GetWeaponTypeKey(mount);
            if (IsCargoMount(mount))
            {
                cargoKey ??= key;
                continue;
            }

            int at = keys.IndexOf(key);
            if (at < 0)
            {
                keys.Add(key);
                stores.Add(GetRackStoreCount(mount));
            }
            else
            {
                stores[at] += GetRackStoreCount(mount);
            }
        }

        string? primaryKey = null;
        string? secondaryKey = null;
        int primaryStores = -1;
        int secondaryStores = -1;
        for (int i = 0; i < keys.Count; i++)
        {
            if (stores[i] > primaryStores)
            {
                secondaryKey = primaryKey;
                secondaryStores = primaryStores;
                primaryKey = keys[i];
                primaryStores = stores[i];
            }
            else if (stores[i] > secondaryStores)
            {
                secondaryKey = keys[i];
                secondaryStores = stores[i];
            }
        }

        SelectedPrimaryWeaponIndex = FindWeaponIndexByKey(primaryKey);
        SelectedSecondaryWeaponIndex = FindWeaponIndexByKey(secondaryKey);
        SelectedCargoWeaponIndex = FindCargoIndexByKey(cargoKey);
        WearPylons(option, preset);
    }

    private bool SaveCurrentAsPreset(string name, out string message)
    {
        AirMissionOption? option = SelectedOption;
        if (option == null)
        {
            message = "No aircraft is selected.";
            return false;
        }

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            message = "Give the preset a name first.";
            return false;
        }

        LoadoutPreset? previousPreset = option.Preset;
        option.Preset = null;
        Loadout loadout = option.BuildLoadout();
        option.Preset = previousPreset;

        LoadoutPreset saved = LoadoutPreset.FromLoadout(name, loadout, option.HardpointSets, custom: true);
        if (saved.IsEmpty)
        {
            message = "That loadout has nothing on it.";
            return false;
        }

        for (int i = 0; i < presets.Count; i++)
        {
            // Only another saved one counts: a player may keep a built-in loadout under their own
            // name, and refusing left them on PYLONS with no name field, typing into the hotkeys.
            if (presets[i].Custom && presets[i].SameStores(saved))
            {
                ChoosePreset(i);
                message = $"Already a preset: {presets[i].Name}.";
                return false;
            }
        }

        LoadCustomPresets();
        customPresets.RemoveAll(entry =>
            entry.DefinitionKey == GetDefinitionKey(option.Definition)
            && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        saved.DefinitionKey = GetDefinitionKey(option.Definition);
        customPresets.Add(saved);
        SaveCustomPresets();
        RefreshPresets();
        SelectedPresetIndex = presets.IndexOf(saved);
        if (SelectedPresetIndex >= 0)
        {
            SelectPreset(SelectedPresetIndex);
        }

        message = $"Preset \"{name}\" saved.";
        return true;
    }

    /// <summary>Tags every preset against the chosen mission and finds the one that does it best.</summary>
    private void RatePresets(AirMissionOption option)
    {
        presetFit.Clear();
        bestPresetIndex = -1;
        float bestScore = 0f;
        bool judged = HasWeaponRule(SelectedMode);
        for (int i = 0; i < presets.Count; i++)
        {
            Loadout loadout = presets[i].Build(option.HardpointSets);
            int fit = !judged ? 0 : CarriesWeaponFor(loadout, SelectedMode) ? 1 : -1;
            presetFit.Add(fit);
            float score = ScoreLoadout(loadout, SelectedMode, option.Definition);
            if (fit >= 0 && score > bestScore)
            {
                bestScore = score;
                bestPresetIndex = i;
            }
        }
    }

    /// <summary>
    /// Rebuilds the preset list for the selected airframe and re-applies the chosen one. Runs on
    /// every aircraft change, because a preset is a set of pylons and pylons are per airframe.
    /// </summary>
    private void RefreshPresets()
    {
        string wantedName = SelectedPresetIndex >= 0 && SelectedPresetIndex < presets.Count
            ? presets[SelectedPresetIndex].Name
            : SelectedOption?.Preset?.Name ?? string.Empty;
        presets.Clear();
        AirMissionOption? option = SelectedOption;
        if (option == null)
        {
            SelectedPresetIndex = -1;
            return;
        }

        if (!CommanderSettings.AirHideBuiltInPresets)
        {
            AddRolePresets(option);
            AddStandardLoadoutPresets(option);
        }

        AddCustomPresets(option);
        SortPresets(GetDefinitionKey(option.Definition));
        RatePresets(option);

        SelectedPresetIndex = presets.FindIndex(entry =>
            string.Equals(entry.Name, wantedName, StringComparison.OrdinalIgnoreCase));
        if (!presetChosenByPlayer && bestPresetIndex >= 0)
        {
            SelectedPresetIndex = bestPresetIndex;
        }

        if (SelectedPresetIndex < 0)
        {
            SelectedPresetIndex = presets.Count > 0 ? 0 : -1;
        }

        if (AdvancedLoadout)
        {
            // Generating the role presets ran the pylon allocator, so the airframe is currently
            // wearing the last role it produced. Put the player's own choice back on it.
            ApplySelectedWeapons();
            return;
        }

        option.Preset = SelectedPresetIndex >= 0 ? presets[SelectedPresetIndex] : null;
        if (option.Preset == null)
        {
            // No preset exists for this airframe at all: fall back to the manual editor rather than
            // offering an empty tab and a launch button that refuses.
            AdvancedLoadout = true;
            ApplySelectedWeapons();
        }
    }

    private void AddRolePresets(AirMissionOption option)
    {
        List<WeaponMount> mounts = CollectAirframeMounts(option);
        WeaponMount? radarLong = PickBestMount(mounts, IsRadarAirToAir, static mount => WeaponRange(mount));
        WeaponMount? radarShort = PickBestMount(mounts, IsRadarAirToAir, static mount => -WeaponRange(mount));
        WeaponMount? heat = PickBestMount(mounts, IsHeatAirToAir, static mount => WeaponRange(mount));
        WeaponMount? air = PickBestMount(mounts, IsAirToAir, mount => ScoreMount(mount, AirCommandMode.AirGuard));
        WeaponMount? surface = PickBestMount(mounts, IsAntiSurface, mount => ScoreMount(mount, AirCommandMode.Cas));
        WeaponMount? arad = PickBestMount(
            mounts,
            static mount => mount.info != null && IsAradWeapon(mount.info),
            mount => ScoreMount(mount, AirCommandMode.Arad));

        WeaponMount? scimitar = PickNamedMount(mounts, "Scimitar");
        WeaponMount? scythe = PickNamedMount(mounts, "Scythe");
        WeaponMount? dome = PickBestMount(mounts, static mount => mount.radar, static mount => 0f);

        // Named for the job, with the guidance in brackets for anyone who wants it.
        AddRolePreset(option, "LONG-RANGE FIGHTER (RADAR)", radarLong, null, LoadoutBalance.Primary);

        // Named weapons rather than "best radar missile": the pick above is by range and does not
        // always land on the Scimitar. Only on airframes that carry them (the Ifrit among others).
        AddRolePreset(option, "LONG RANGE (SCIMITARS ONLY)", scimitar, null, LoadoutBalance.Primary, primaryOnly: true);
        AddRolePreset(option, "ALL-ROUND AIR (SCIMITAR + SCYTHE)", scimitar, scythe, LoadoutBalance.Mixed);

        // The Medusa's radar dome with ARADs on the rest; FinishLoadout puts its laser on as fixed
        // equipment. Keyed on the dome itself, so it appears wherever a radar mount is offered.
        if (dome != null && arad != null)
        {
            AddRolePreset(option, "AEW (RADAR DOME + LASER + ARAD)", dome, arad, LoadoutBalance.Primary);
        }

        AddRolePreset(
            option,
            "FIGHTER (RADAR + HEAT)",
            ReferenceEquals(radarShort, radarLong) ? radarLong : radarShort,
            heat,
            LoadoutBalance.Mixed);
        AddRolePreset(option, "DOGFIGHT (HEAT)", heat, null, LoadoutBalance.Primary);
        AddRolePreset(option, "GROUND ATTACK", surface, null, LoadoutBalance.Primary);
        AddRolePreset(option, "SEAD (ANTI-RADAR)", arad, null, LoadoutBalance.Primary);
        AddRolePreset(option, "MULTIROLE", air, surface, LoadoutBalance.Mixed);

        // The hook and nothing else, for SLING LIFT. Found by WeaponInfo.sling, so it appears on
        // whichever airframes offer the hook (the Ibis and Tarantula) without naming them.
        AddRolePreset(
            option,
            "TRANSPORT - SLING HOOK",
            mounts.Find(static mount => mount.info?.sling == true),
            null,
            LoadoutBalance.Primary,
            primaryOnly: true);
    }

    /// <summary>
    /// Turns "this weapon in front, that one behind" into a finished set of pylons by running the
    /// same allocator the advanced tab uses and snapshotting the result. No second layout engine.
    /// </summary>
    private void AddRolePreset(
        AirMissionOption option,
        string name,
        WeaponMount? primary,
        WeaponMount? secondary,
        LoadoutBalance balance,
        bool primaryOnly = false)
    {
        if (primary == null)
        {
            return;
        }

        if (secondary != null && SameMountType(primary, secondary))
        {
            secondary = null;
        }

        LoadoutPreset? previousPreset = option.Preset;
        option.Preset = null;
        ConfigureOptionLoadout(option, primary, secondary, null, balance);
        Loadout loadout = option.BuildLoadout();
        option.Preset = previousPreset;
        for (int i = 0; primaryOnly && i < loadout.weapons.Count; i++)
        {
            // Drops the fixed equipment FinishLoadout adds (internal cannons).
            if (!SameMountType(loadout.weapons[i], primary))
            {
                loadout.weapons[i] = null!;
            }
        }

        LoadoutPreset preset = LoadoutPreset.FromLoadout(name, loadout, option.HardpointSets, custom: false);
        preset.Generated = true;
        if (!preset.IsEmpty)
        {
            AddPreset(preset);
        }
    }

    private void AddStandardLoadoutPresets(AirMissionOption option)
    {
        StandardLoadout[]? standardLoadouts = option.Definition.aircraftParameters?.StandardLoadouts;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (standardLoadouts == null)
        {
            return;
        }

        for (int i = 0; i < standardLoadouts.Length; i++)
        {
            StandardLoadout standard = standardLoadouts[i];
            if (standard == null || standard.disabled || standard.loadout == null)
            {
                continue;
            }

            // A cargo or troop loadout is not a combat preset; the escort turns into a transport the
            // moment one pylon carries a load, which is a base-game behaviour, not a mod choice.
            if (ContainsCargo(standard.loadout))
            {
                continue;
            }

            LoadoutPreset preset = LoadoutPreset.FromLoadout(
                string.IsNullOrWhiteSpace(standard.Name) ? $"STANDARD {i + 1}" : standard.Name.ToUpperInvariant(),
                standard.loadout,
                option.HardpointSets,
                custom: false);
            if (!preset.IsEmpty && (hq == null || standard.AllowedByHQ(GetPrefabWeaponManager(option.Definition)!, hq)))
            {
                AddPreset(preset);
            }
        }
    }

    private void AddCustomPresets(AirMissionOption option)
    {
        LoadCustomPresets();
        string key = GetDefinitionKey(option.Definition);
        for (int i = 0; i < customPresets.Count; i++)
        {
            if (customPresets[i].DefinitionKey == key)
            {
                AddPreset(customPresets[i]);
            }
        }
    }

    /// <summary>Skips a generated preset that arms the aeroplane exactly like one already listed,
    /// and any preset whose name is taken.</summary>
    private void AddPreset(LoadoutPreset preset)
    {
        for (int i = 0; i < presets.Count; i++)
        {
            if ((!preset.Custom && presets[i].SameStores(preset))
                || string.Equals(presets[i].Name, preset.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        presets.Add(preset);
    }

    /// <summary>The player's dragged order wins. Anything not in it yet: a saved preset goes on top,
    /// a built-in one to the bottom, each keeping the order it was added in.</summary>
    private void SortPresets(string definitionKey)
    {
        List<string> order = ReadPresetOrder(CommanderSettings.AirLoadoutPresetOrder, definitionKey);
        List<LoadoutPreset> added = new(presets);
        presets.Sort((a, b) =>
        {
            int byRank = PresetRank(a, order).CompareTo(PresetRank(b, order));
            return byRank != 0 ? byRank : added.IndexOf(a).CompareTo(added.IndexOf(b));
        });
    }

    private void LoadCustomPresets()
    {
        if (customPresetsLoaded)
        {
            return;
        }

        customPresetsLoaded = true;
        customPresets.Clear();
        customPresets.AddRange(ParsePresets(CommanderSettings.AirLoadoutPresets));
    }

    private void SaveCustomPresets()
    {
        CommanderSettings.AirLoadoutPresets = SerializePresets(customPresets);
    }

    /// <summary>
    /// A named set of pylons, stored as one weapon <c>jsonKey</c> per hardpoint set. Keys rather
    /// than <c>WeaponMount</c> references because a saved preset has to survive a session, and
    /// because the same key resolves on every airframe that offers that store.
    /// </summary>
    internal sealed class LoadoutPreset
    {
        internal LoadoutPreset(string name, List<string> mountKeys, bool custom)
        {
            Name = name;
            MountKeys = mountKeys;
            Custom = custom;
        }

        internal string Name { get; set; }

        internal List<string> MountKeys { get; }

        internal bool Custom { get; }

        /// <summary>Built by the mod from what the airframe carries, rather than authored.</summary>
        internal bool Generated { get; set; }

        /// <summary>Which aircraft a saved preset belongs to; empty for a built-in one.</summary>
        internal string DefinitionKey { get; set; } = string.Empty;

        internal bool IsEmpty
        {
            get
            {
                for (int i = 0; i < MountKeys.Count; i++)
                {
                    if (!string.IsNullOrEmpty(MountKeys[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        internal static LoadoutPreset FromLoadout(
            string name, Loadout loadout, HardpointSet[] sets, bool custom)
        {
            List<string> keys = new();
            for (int i = 0; i < sets.Length; i++)
            {
                WeaponMount? mount = i < loadout.weapons.Count ? loadout.weapons[i] : null;
                keys.Add(mount != null ? mount.jsonKey : string.Empty);
            }

            return new LoadoutPreset(name, keys, custom);
        }

        internal Loadout Build(HardpointSet[] sets)
        {
            Loadout loadout = new();
            for (int i = 0; i < sets.Length; i++)
            {
                loadout.weapons.Add(FindMount(sets, i)!);
            }

            return loadout;
        }

        internal bool SameStores(LoadoutPreset other)
        {
            if (other.MountKeys.Count != MountKeys.Count)
            {
                return false;
            }

            for (int i = 0; i < MountKeys.Count; i++)
            {
                if (!string.Equals(MountKeys[i], other.MountKeys[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>"4x Scythe + 2x MS-3", the line under the preset name.</summary>
        internal string Describe(HardpointSet[]? sets)
        {
            List<string> names = new();
            List<int> counts = new();
            for (int i = 0; i < MountKeys.Count; i++)
            {
                WeaponMount? mount = sets != null ? FindMount(sets, i) : null;
                string label = mount != null ? GetWeaponTypeName(mount) : MountKeys[i];
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                int stores = mount != null ? GetRackStoreCount(mount) : 1;
                int existing = names.IndexOf(label);
                if (existing >= 0)
                {
                    counts[existing] += stores;
                }
                else
                {
                    names.Add(label);
                    counts.Add(stores);
                }
            }

            if (names.Count == 0)
            {
                return "empty";
            }

            StringBuilder builder = new();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" + ");
                }

                builder.Append(counts[i]).Append("x ").Append(names[i]);
            }

            return builder.ToString();
        }

        private WeaponMount? FindMount(HardpointSet[] sets, int index)
        {
            if (index >= sets.Length || index >= MountKeys.Count || string.IsNullOrEmpty(MountKeys[index]))
            {
                return null;
            }

            List<WeaponMount>? candidates = sets[index].weaponOptions;
            for (int i = 0; candidates != null && i < candidates.Count; i++)
            {
                if (candidates[i] != null
                    && string.Equals(candidates[i].jsonKey, MountKeys[index], StringComparison.Ordinal))
                {
                    return candidates[i];
                }
            }

            return null;
        }
    }
}
