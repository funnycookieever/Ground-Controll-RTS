using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.MissionEditorScripts.Buttons;
using NuclearOption.SavedMission;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GroundControlRts;

/// <summary>
/// Ground Control in the game's own mission editor, so players can make their own GC RTS maps:
/// a GC RTS tab in the unit placement panel (gold mine, gas export terminal, fuel exchange, naval
/// dock, enrichment plant and a factory for every ground unit, aircraft and ship) beside Aircraft, Vehicles and
/// Buildings, and a Ground Control RTS section at the bottom of Mission Settings: the toggle that
/// turns the full mod on for the map, the map's own rules, and every GC RTS Settings option of
/// Customize Mission, saved with the map, and the map's own unit prices.
/// </summary>
/// <remarks>
/// Both are copies of the panel's own controls, so they look like the base game's. Nothing is stored
/// outside the mission's own fields, because the mission JSON round-trips through fixed game
/// classes: the map flag and rules are mission tags (<see cref="CommanderMapSettings"/>), a
/// building's GC role is its unique name ("GC Gold Mine_1"), which
/// <c>CommanderEconomyService.AdoptMissionBuildings</c> reads back when the map is played, and a
/// factory is the game's own <c>factoryOptions</c>.
/// </remarks>
[HarmonyPatch]
internal static class CommanderMissionEditorPatches
{
    /// <summary>The key in <c>NewUnitPanel.unitProviders</c>, and what its sticky tab remembers.</summary>
    private const string TabKey = "groundControlRts";

    /// <summary>The GC RTS tab's list, in dropdown order. A prefab can be several entries (one hall
    /// for many factories), so a placement is matched by the dropdown's index, not its definition.</summary>
    private static readonly List<TabEntry> TabEntries = new();

    /// <summary>Redraws each GC rule row from a mission.</summary>
    private static readonly List<Action<Mission>> MapRows = new();

    private static Toggle? mapToggle;

    [HarmonyPatch(typeof(NewUnitPanel), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void NewUnitPanelAwakePostfix(NewUnitPanel __instance, Button ___otherUnits, Button ___airbase)
    {
        if (___otherUnits == null)
        {
            CommanderPlugin.Log.LogWarning("The editor's unit panel has changed shape; the GC RTS tab was not added.");
            return;
        }

        // After Airbase, the last of the game's tabs, so none of theirs move.
        Button tab = Object.Instantiate(___otherUnits.gameObject, ___otherUnits.transform.parent).GetComponent<Button>();
        tab.name = "GroundControlRtsTab";
        tab.transform.SetSiblingIndex((___airbase != null ? ___airbase.transform : ___otherUnits.transform).GetSiblingIndex() + 1);
        tab.onClick = new Button.ButtonClickedEvent();
        tab.onClick.AddListener(() => OpenTab(__instance, tab));
        tab.GetComponent<ShowHoverText>()?.SetText("Ground Control RTS");
        DrawTabIcon(tab, __instance.GetComponentInChildren<TextMeshProUGUI>(true));

        if (Traverse.Create(typeof(NewUnitPanel)).Field("stickyTab").GetValue<string>() == TabKey)
        {
            OpenTab(__instance, tab);
        }
    }

    /// <summary>The game's tabs are white silhouettes and it has none for a gold mine, so ours is
    /// "GC" in the panel's own font, in the silhouette's colour and place.</summary>
    private static void DrawTabIcon(Button tab, TextMeshProUGUI? fontSource)
    {
        Image? icon = null;
        foreach (Image image in tab.GetComponentsInChildren<Image>(true))
        {
            if (image.gameObject != tab.gameObject)
            {
                icon = image;
            }
        }

        if (icon == null || fontSource == null)
        {
            return;
        }

        icon.enabled = false;
        GameObject textObject = new("GroundControlRtsIcon", typeof(RectTransform));
        textObject.transform.SetParent(icon.transform, false);
        RectTransform rect = (RectTransform)textObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = fontSource.font;
        text.text = "GC";
        text.fontSize = 26f;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.fontStyle = FontStyles.Bold;
        text.color = icon.color;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
    }

    /// <summary>The game's own tab click with our list, then the dropdown relabelled: a gold mine
    /// is a refinery to the game, and "Refinery Structure" is not what a map maker is looking for.</summary>
    private static void OpenTab(NewUnitPanel panel, Button tab)
    {
        if (!EnsureProvider())
        {
            CommanderPlugin.Log.LogWarning("No GC RTS building prefabs were found; the GC RTS tab is empty.");
            return;
        }

        AccessTools.Method(typeof(NewUnitPanel), "TabClickedUnit").Invoke(panel, new object[] { tab, TabKey });
        TMP_Dropdown? dropdown = Traverse.Create(panel).Field("typeDropdown").GetValue<TMP_Dropdown>();
        if (dropdown == null)
        {
            return;
        }

        for (int i = 0; i < dropdown.options.Count && i < TabEntries.Count; i++)
        {
            dropdown.options[i].text = TabEntries[i].Label;
        }

        dropdown.RefreshShownValue();
    }

    /// <summary>
    /// Adds our list to the panel's static table of tabs, in our order rather than the game's A to Z:
    /// the GC buildings, the enrichment plant, then a factory per ground unit, aircraft and ship. Built again while it
    /// has no factories, since the factions it reads them from may not have been up the first time.
    /// </summary>
    private static bool EnsureProvider()
    {
        IDictionary providers = (IDictionary)AccessTools.Field(typeof(NewUnitPanel), "unitProviders").GetValue(null);
        if (providers.Contains(TabKey) && TabEntries.Exists(entry => entry.Kind == CommanderBuildKind.Factory))
        {
            return true;
        }

        TabEntries.Clear();
        foreach (CommanderBuildKind kind in CommanderEconomyService.MissionKinds)
        {
            BuildingDefinition? definition = CommanderEconomyService.GetMissionDefinition(kind);
            if (definition != null && !TabEntries.Exists(entry => entry.Definition == definition))
            {
                TabEntries.Add(new TabEntry(definition, kind, CommanderEconomyService.GetMissionLabel(kind), null, 0f));
            }
        }

        BuildingDefinition? plant = CommanderEconomyService.GetMissionEnrichmentDefinition();
        if (plant != null)
        {
            TabEntries.Add(new TabEntry(
                plant, CommanderBuildKind.Structure, plant.unitName, CommanderEconomyService.MissionWarheadProduction, CommanderEconomyService.WarheadProductionSeconds));
        }

        List<UnitDefinition> products = new();
        CommanderEconomyService.CollectMissionFactoryProducts(products);
        foreach (UnitDefinition product in products)
        {
            BuildingDefinition? hall = CommanderEconomyService.GetMissionFactoryDefinition(product);
            if (hall != null)
            {
                TabEntries.Add(new TabEntry(
                    hall, CommanderBuildKind.Factory, CommanderEconomyService.GetFactoryLabel(product), product.jsonKey, CommanderEconomyService.FactoryProductionSeconds));
            }
        }

        if (TabEntries.Count == 0)
        {
            return false;
        }

        List<BuildingDefinition> definitions = new();
        foreach (TabEntry entry in TabEntries)
        {
            definitions.Add(entry.Definition);
        }

        Type providerType = AccessTools.Inner(typeof(NewUnitPanel), "UnitOptionProvider");
        Type sortType = AccessTools.Inner(providerType, "SortMode");
        object provider = AccessTools.Method(providerType, "Create")
            .MakeGenericMethod(typeof(BuildingDefinition))
            .Invoke(null, new object[] { definitions, Enum.ToObject(sortType, 0) });

        // Both arrays are simply ours: the game's copies are sorted by name, which would put a
        // factory's label on the wrong line, and none of these is event content to filter out.
        UnitDefinition[] ordered = definitions.ToArray();
        foreach (string field in new[] { "noEvent", "allowEvent" })
        {
            AccessTools.Field(providerType, field).SetValue(provider, ordered);
        }

        providers[TabKey] = provider;
        return true;
    }

    /// <summary>
    /// Names what the GC RTS tab just placed after its role, the same way the panel's own name field
    /// renames a unit, and tags the map: a gold mine on a map that is not a GC RTS map would do nothing.
    /// A factory is set to make its unit through the game's own <c>factoryOptions</c>, the same field
    /// the editor's Factory panel writes, so it can be changed there afterwards.
    /// </summary>
    [HarmonyPatch(typeof(NewUnitPanel), "PlaceUnit")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void PlaceUnitPostfix(NewUnitPanel __instance, (bool PlaceMore, IEditorSelectable PlacedObject) __result)
    {
        int index = Traverse.Create(__instance).Field("typeDropdown").GetValue<TMP_Dropdown>()?.value ?? -1;
        if (Traverse.Create(typeof(NewUnitPanel)).Field("stickyTab").GetValue<string>() != TabKey
            || __result.PlacedObject is not Unit unit
            || unit.SavedUnit == null
            || index < 0
            || index >= TabEntries.Count
            || TabEntries[index].Definition != unit.definition)
        {
            return;
        }

        TabEntry entry = TabEntries[index];
        string name = entry.Production != null ? entry.Label : CommanderEconomyService.MissionNamePrefix + entry.Label;
        if (entry.Production != null && unit.SavedUnit is SavedBuilding saved)
        {
            saved.factoryOptions = new SavedBuilding.FactoryOptions { productionType = entry.Production, productionTime = entry.ProductionSeconds };
        }

        List<SavedUnit> all = new();
        MissionManager.GetAllSavedUnitsNonAlloc(all, includeBuiltIn: true);
        SaveHelper.MakeUnique(ref name, unit.SavedUnit, all, warn: false);
        unit.SavedUnit.Rename(name);
        unit.NetworkUniqueName = name;
        if (MissionManager.CurrentMission != null && !CommanderFeatureGate.HasTag(MissionManager.CurrentMission))
        {
            CommanderFeatureGate.SetTag(MissionManager.CurrentMission, true);
            if (mapToggle != null)
            {
                mapToggle.SetIsOnWithoutNotify(true);
            }
        }
    }

    /// <summary>
    /// The Ground Control RTS section, below the game's own rows in the editor's Mission Settings:
    /// copies of the Allow Respawn row (toggles) and the Starting Rank row (sliders). Only in the
    /// editor: the same tab is the Mission Parameters column of Customize Mission, where the mod has
    /// its own page. Every row is a tag (<see cref="CommanderMapSettings"/>): the map's value, which
    /// Customize Mission's GC RTS Settings page can change for one match, as the game's own tabs do.
    /// </summary>
    [HarmonyPatch(typeof(MissionSettingsTab), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SettingsAwakePostfix(Toggle ___respawnToggle, Slider ___startingRankSlider, TextMeshProUGUI ___startingRankLabel)
    {
        if (GameManager.gameState != GameState.Editor || ___respawnToggle == null)
        {
            return;
        }

        MapRows.Clear();
        Transform column = RowOf(___respawnToggle.transform).parent;
        Transform last = column.GetChild(column.childCount - 1);
        AddHeader(ref last, ___startingRankLabel, "Ground Control RTS");
        mapToggle = AddToggleRow(___respawnToggle, ref last, "Ground Control RTS", on =>
        {
            if (MissionManager.CurrentMission != null)
            {
                CommanderFeatureGate.SetTag(MissionManager.CurrentMission, on);
            }
        });
        last.name = "GroundControlRtsMapToggle";

        Toggle objectives = AddToggleRow(___respawnToggle, ref last, "Win by objectives only", on => Write(m => CommanderMapSettings.SetTag(m, CommanderMapSettings.ObjectivesWinTag, on)));
        MapRows.Add(m => objectives.SetIsOnWithoutNotify(CommanderMapSettings.HasTag(m, CommanderMapSettings.ObjectivesWinTag)));

        // Ticked when the map carries its own prices; a click opens UNIT PRICES on them.
        Toggle prices = AddToggleRow(___respawnToggle, ref last, "Unit prices", _ =>
        {
            Mission? mission = MissionManager.CurrentMission;
            if (mission != null)
            {
                RefreshRows(mission);
                CommanderPriceProfile.EditMission(mission, () => RefreshRows(mission));
            }
        });
        TextMeshProUGUI[] priceLabels = last.GetComponentsInChildren<TextMeshProUGUI>(true);
        MapRows.Add(m =>
        {
            string? map = CommanderPriceProfile.DescribeMission(m);
            prices.SetIsOnWithoutNotify(map != null);
            foreach (TextMeshProUGUI label in priceLabels)
            {
                label.text = map != null ? "Unit prices: " + map : "Unit prices: Standard (click to edit)";
            }
        });
        if (___startingRankSlider == null)
        {
            CommanderPlugin.Log.LogWarning("The editor's Mission Settings have changed shape; the GC RTS sliders were not added.");
            return;
        }

        void Row(string title, string prefix, string suffix, int count, Func<int, float> valueOf, Func<int, string> nameOf, int defaultIndex)
        {
            AddSliderRow(___startingRankSlider, ___startingRankLabel, ref last, title, prefix, suffix, count, valueOf, nameOf, defaultIndex);
        }

        // The map's own rules.
        Row("Starting builders", CommanderMapSettings.BuildersPrefix, string.Empty, CommanderMapSettings.MaxBuilders + 1, i => i, i => Number(i), CommanderMapSettings.DefaultBuilders);
        Row("AI head start", CommanderMapSettings.HeadStartPrefix, "%", 11, i => i * 50, i => Number(i * 50) + "%", CommanderMapSettings.DefaultHeadStartPercent / 50);
        Row("AI income per minute", CommanderMapSettings.AiIncomePrefix, string.Empty, 21, i => i * 25, i => Number(i * 25), 0);
        Row("Base income per minute", CommanderMapSettings.BaseIncomePrefix, string.Empty, 41, i => i * 25, i => Number(i * 25), 0);

        // Customize Mission's GC RTS Settings page, in its order, at the mod's defaults.
        Row("AI Commanders", CommanderMapSettings.AiCommandersPrefix, string.Empty, CommanderEnemyCommanderService.AiCommandersBoth + 1, i => i, CommanderMissionSettingsPatches.AiCommandersLabel, CommanderEnemyCommanderService.AiCommandersEnemy);
        Row("Enemy Commander", CommanderMapSettings.EnemyModePrefix, string.Empty, 2, i => i + CommanderEnemyCommanderService.ModeMatched, i => CommanderMissionSettingsPatches.EnemyModeLabel(i + CommanderEnemyCommanderService.ModeMatched), 0);
        Row("Enemy Difficulty", CommanderMapSettings.EnemyDifficultyPrefix, string.Empty, CommanderEnemyCommanderService.DifficultyChallenging + 1, i => i, CommanderEnemyCommanderService.GetDifficultyLabel, CommanderEnemyCommanderService.DifficultyNormal);
        Row("Enemy AI also runs teams with players", CommanderMapSettings.HumanTeamsPrefix, string.Empty, 2, i => i, OffOn, 0);
        Row(CommanderMissionSettingsPatches.FactionVehiclesOnlyLabel, CommanderMapSettings.FactoryVehiclesPrefix, string.Empty, 2, i => i, OffOn, 0);
        Row("Friendly AI off: only units you spawn", CommanderMapSettings.FriendlyAiOffPrefix, string.Empty, 2, i => i, OffOn, 0);
        Row("Fog of war: enemies unseen are not drawn", CommanderMapSettings.FogOfWarPrefix, string.Empty, 2, i => i, OffOn, 0);
        Row("Unit ban vote before the match", CommanderMapSettings.BanVotePrefix, string.Empty, 2, i => i, OffOn, 0);
        Row("Voted bans per side", CommanderMapSettings.BansPerSidePrefix, string.Empty, 21, i => i, i => i == 0 ? "None" : Number(i), 2);
        Row("Unit Cap", CommanderMapSettings.UnitCapPrefix, string.Empty, CommanderUnitCap.ModeCount + 1, i => i, CommanderMissionSettingsPatches.UnitCapLabel, 0);
        Row("Ship Cap", CommanderMapSettings.ShipCapPrefix, string.Empty, CommanderUnitCap.MaxKindCap + 1, i => i, CommanderMissionSettingsPatches.KindCapLabel, 0);
        Row("Artillery Cap", CommanderMapSettings.ArtilleryCapPrefix, string.Empty, CommanderUnitCap.MaxKindCap + 1, i => i, CommanderMissionSettingsPatches.KindCapLabel, 0);
        Row("Aircraft Cap", CommanderMapSettings.AircraftCapPrefix, string.Empty, CommanderUnitCap.MaxKindCap + 1, i => i, CommanderMissionSettingsPatches.KindCapLabel, 0);
        Row("Build Radius", CommanderMapSettings.BuildRadiusPrefix, " km", 29, i => (i + 2) * 0.5f, i => Number((i + 2) * 0.5f) + " km", 3);
        Row("Naval Dock Radius", CommanderMapSettings.DockRadiusPrefix, " km", 49, i => (i + 2) * 0.5f, i => Number((i + 2) * 0.5f) + " km", 22);
        Row("Aircraft Capture Strength", CommanderMapSettings.AircraftCapturePrefix, string.Empty, 33, i => i * 0.5f, i => Number(i * 0.5f), 16);

        // The game fills the tab (SetMission) before it first wakes, so the rows are filled here too.
        if (MissionManager.CurrentMission != null)
        {
            mapToggle.SetIsOnWithoutNotify(CommanderFeatureGate.HasTag(MissionManager.CurrentMission));
            RefreshRows(MissionManager.CurrentMission);
        }
    }

    private static string OffOn(int index)
    {
        return index == 0 ? "Off" : "On";
    }

    private static string Number(float value)
    {
        return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The section's title: the column's own label font, larger and bold, so it reads as a
    /// heading over the rows below it rather than as one of them.</summary>
    private static void AddHeader(ref Transform last, TextMeshProUGUI? fontSource, string title)
    {
        GameObject header = new("GroundControlRtsHeader", typeof(RectTransform));
        header.transform.SetParent(last.parent, false);
        header.transform.SetSiblingIndex(last.GetSiblingIndex() + 1);
        last = header.transform;
        LayoutElement size = header.AddComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = 44f;
        TextMeshProUGUI text = header.AddComponent<TextMeshProUGUI>();
        if (fontSource != null)
        {
            text.font = fontSource.font;
            text.color = fontSource.color;
        }

        text.text = title.ToUpperInvariant();
        text.fontSize = 24f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Bottom;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
    }

    [HarmonyPatch(typeof(MissionSettingsTab), "SetMission")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SetMissionPostfix(Mission mission)
    {
        if (GameManager.gameState == GameState.Editor && mapToggle != null)
        {
            mapToggle.SetIsOnWithoutNotify(CommanderFeatureGate.HasTag(mission));
            RefreshRows(mission);
        }
    }

    private static void RefreshRows(Mission mission)
    {
        for (int i = 0; i < MapRows.Count; i++)
        {
            MapRows[i](mission);
        }
    }

    /// <summary>Changes a map rule, ticks Ground Control RTS (a rule on a map that is not a GC RTS
    /// map does nothing) and redraws the rows.</summary>
    private static void Write(Action<Mission> change)
    {
        Mission? mission = MissionManager.CurrentMission;
        if (mission == null)
        {
            return;
        }

        change(mission);
        if (!CommanderFeatureGate.HasTag(mission))
        {
            CommanderFeatureGate.SetTag(mission, true);
            mapToggle?.SetIsOnWithoutNotify(true);
        }

        RefreshRows(mission);
    }

    /// <summary>A copy of the Allow Respawn row below <paramref name="last"/>.</summary>
    private static Toggle AddToggleRow(Toggle template, ref Transform last, string title, UnityEngine.Events.UnityAction<bool> changed)
    {
        Transform source = RowOf(template.transform);
        GameObject row = Object.Instantiate(source.gameObject, source.parent);
        row.name = "GroundControlRts" + title.Replace(" ", string.Empty).Replace(":", string.Empty);
        row.transform.SetSiblingIndex(last.GetSiblingIndex() + 1);
        last = row.transform;
        foreach (TextMeshProUGUI label in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            label.text = title;
        }

        Toggle toggle = row.GetComponentInChildren<Toggle>(true);
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.SetIsOnWithoutNotify(false);
        toggle.onValueChanged.AddListener(changed);
        return toggle;
    }

    /// <summary>
    /// A copy of the Starting Rank slider row below <paramref name="last"/>, one position per option
    /// (<paramref name="valueOf"/> is what it writes, <paramref name="nameOf"/> what it shows), at
    /// <paramref name="defaultIndex"/> until the map sets it. Every move writes the value, the default
    /// included, so the map keeps what its maker saw. The value label is found in the copy at the
    /// original label's path; a row shaped some other way gets the value in its title instead.
    /// </summary>
    private static void AddSliderRow(
        Slider template,
        TextMeshProUGUI? valueTemplate,
        ref Transform last,
        string title,
        string prefix,
        string suffix,
        int count,
        Func<int, float> valueOf,
        Func<int, string> nameOf,
        int defaultIndex)
    {
        Transform source = RowOf(template.transform);
        string? sliderPath = PathFrom(source, template.transform);
        string? labelPath = valueTemplate == null ? null : PathFrom(source, valueTemplate.transform);
        GameObject row = Object.Instantiate(source.gameObject, source.parent);
        row.name = "GroundControlRts" + prefix.Replace(" ", string.Empty);
        row.transform.SetSiblingIndex(last.GetSiblingIndex() + 1);
        last = row.transform;

        // Starting Rank shares its row with Score Rank Multiplier: keep the slider and its label
        // above it, and shrink the row by the share of children dropped.
        Transform? sliderTop = sliderPath == null ? null : row.transform.Find(sliderPath.Split('/')[0]);
        if (sliderTop != null && row.transform.childCount > 2)
        {
            int keep = sliderTop.GetSiblingIndex();
            int children = row.transform.childCount;
            for (int i = children - 1; i >= 0; i--)
            {
                if (i != keep && i != keep - 1)
                {
                    Object.DestroyImmediate(row.transform.GetChild(i).gameObject);
                }
            }

            RectTransform rect = (RectTransform)row.transform;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, rect.sizeDelta.y * row.transform.childCount / children);
        }

        Slider slider = (sliderPath == null ? null : row.transform.Find(sliderPath)?.GetComponent<Slider>()) ?? row.GetComponentInChildren<Slider>(true);
        TextMeshProUGUI? value = labelPath == null ? null : row.transform.Find(labelPath)?.GetComponent<TextMeshProUGUI>();
        List<TextMeshProUGUI> titles = new();
        foreach (TextMeshProUGUI label in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (label != value)
            {
                titles.Add(label);
            }
        }

        slider.onValueChanged = new Slider.SliderEvent();
        slider.minValue = 0f;
        slider.maxValue = count - 1;
        slider.wholeNumbers = true;

        // A tag written by hand may sit between two options: the nearest is shown.
        int IndexOf(float? written)
        {
            if (written == null)
            {
                return defaultIndex;
            }

            int best = 0;
            for (int i = 1; i < count; i++)
            {
                if (Mathf.Abs(valueOf(i) - written.Value) < Mathf.Abs(valueOf(best) - written.Value))
                {
                    best = i;
                }
            }

            return best;
        }

        void Show(Mission mission)
        {
            int index = IndexOf(CommanderMapSettings.GetValue(mission, prefix));
            slider.SetValueWithoutNotify(index);
            string text = nameOf(index) + (index == defaultIndex ? " (default)" : string.Empty);
            if (value != null)
            {
                value.text = text;
            }

            for (int i = 0; i < titles.Count; i++)
            {
                titles[i].text = value != null ? title : $"{title}: {text}";
            }
        }

        MapRows.Add(Show);
        slider.onValueChanged.AddListener(raw => Write(mission =>
        {
            CommanderMapSettings.SetValue(mission, prefix, valueOf(Mathf.Clamp(Mathf.RoundToInt(raw), 0, count - 1)), suffix);
        }));
    }

    /// <summary>The child path from <paramref name="root"/> down to <paramref name="node"/>, or null
    /// when it is not under it.</summary>
    private static string? PathFrom(Transform root, Transform node)
    {
        string path = string.Empty;
        for (Transform? at = node; at != null; at = at.parent)
        {
            if (at == root)
            {
                return path;
            }

            path = path.Length == 0 ? at.name : at.name + "/" + path;
        }

        return null;
    }

    /// <summary>The toggle's row in the column: the Respawn toggle sits in a plain panel with no
    /// layout of its own, and a copy inside that panel was drawn over the original.</summary>
    private static Transform RowOf(Transform toggle)
    {
        Transform node = toggle;
        while (node.parent != null && node.parent.GetComponent<LayoutGroup>() == null)
        {
            node = node.parent;
        }

        return node;
    }

    /// <summary>One line of the GC RTS tab. <c>Production</c> is a factory's product (a jsonKey, or
    /// the warhead line), null for a building the economy adopts by its GC name.</summary>
    private sealed class TabEntry
    {
        internal TabEntry(BuildingDefinition definition, CommanderBuildKind kind, string label, string? production, float productionSeconds)
        {
            Definition = definition;
            Kind = kind;
            Label = label;
            Production = production;
            ProductionSeconds = productionSeconds;
        }

        internal BuildingDefinition Definition { get; }

        internal CommanderBuildKind Kind { get; }

        internal string Label { get; }

        internal string? Production { get; }

        internal float ProductionSeconds { get; }
    }
}
