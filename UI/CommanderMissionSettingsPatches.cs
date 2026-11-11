using System;
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
/// A GC RTS SETTINGS button beside Close in the game's own Customize Mission screen (the button
/// beside the mission picker, for singleplayer and CREATE LOBBY alike) that swaps the mission
/// columns for a Ground Control RTS page, so the host sets the match rules before the match.
/// </summary>
/// <remarks>
/// The button, columns, boxes, sliders and toggles are copies of the screen's own Close button,
/// Mission Parameters column, Funds box, slider rows and Prevent Players Joining toggle, so the page
/// is the game's tab with the mod's settings in it. It is one more entry in the row of tabs, shown
/// in their place at their size. Like the game's own tabs it edits the mission being customised:
/// each value is a mission tag (<see cref="CommanderMapSettings"/>, the same ones the mission editor
/// writes), so it starts at what the map's maker set and rides in the copy the match is played from.
/// A mission with no tag for an option shows the config's value. The ban list is the host's own and
/// stays in the config; so are the unit prices, unless the map carries its own (set in the editor),
/// which the match then plays and this page edits on the copy.
/// The enemy commander's OFF is not offered: it lasts one match and the scene change into the
/// mission resets it, so it stays in Settings > Gameplay.
/// </remarks>
[HarmonyPatch]
internal static class CommanderMissionSettingsPatches
{
    /// <summary>What "LimitToFactoryVehicles" does: the depot offers the faction's own roster (its
    /// convoy groups) instead of every ground vehicle in the game.</summary>
    internal const string FactionVehiclesOnlyLabel = "Depots sell only the faction's own vehicles";

    /// <summary>Shared with the mission editor's Ground Control RTS rows. As a mission value 0 is Off:
    /// no AI commander on this mission, the mod's own maps included.</summary>
    internal static string AiCommandersLabel(int value)
    {
        return value switch
        {
            CommanderEnemyCommanderService.AiCommandersEnemy => "Enemy Side",
            CommanderEnemyCommanderService.AiCommandersBoth => "Both Sides",
            _ => "Off",
        };
    }

    internal static string EnemyModeLabel(int value)
    {
        return CommanderEnemyCommanderService.GetModeLabel(value == CommanderEnemyCommanderService.ModeMission
            ? CommanderEnemyCommanderService.ModeMission
            : CommanderEnemyCommanderService.ModeMatched);
    }

    /// <summary>0 is off, then each cap mode one up.</summary>
    internal static string UnitCapLabel(int value)
    {
        return value == 0 ? "Off" : $"{CommanderUnitCap.GetLabel(value - 1)}: {CommanderUnitCap.GetCap(value - 1)} per side";
    }

    /// <summary>The ship and artillery caps: 0 is off, else that many per side.</summary>
    internal static string KindCapLabel(int value)
    {
        return value == 0 ? "Off" : $"{value} per side";
    }

    /// <summary>The mission Customize Mission is editing; the page reads and writes its tags.</summary>
    private static Mission? Edited(CustomizeMissionMenu menu)
    {
        return Traverse.Create(menu).Field("mission").GetValue<Mission>();
    }

    // A base-game mission shows the stock value where it has no tag, the same one it plays.
    private static float Read(CustomizeMissionMenu menu, string prefix, float config)
    {
        Mission? mission = Edited(menu);
        return CommanderMapSettings.GetValue(mission, prefix) ?? CommanderMapSettings.StockValue(mission, prefix) ?? config;
    }

    private static void Write(CustomizeMissionMenu menu, string prefix, float value, string suffix = "")
    {
        Mission? mission = Edited(menu);
        if (mission != null)
        {
            CommanderMapSettings.SetValue(mission, prefix, value, suffix);
        }
    }

    /// <summary>
    /// With no tag, a Ground Control map plays the config's AI Commanders, whose 0 means "the mod's
    /// own missions only", i.e. an AI there. Shown as what it does. Other missions never get here:
    /// their untagged value is Off (<see cref="CommanderMapSettings.StockValue"/>).
    /// </summary>
    private static int UntaggedAiCommanders()
    {
        int config = CommanderSettings.AiCommanders;
        return config != CommanderEnemyCommanderService.AiCommandersListed ? config : CommanderEnemyCommanderService.AiCommandersEnemy;
    }

    /// <summary>
    /// Closing swaps the picker to a saved copy of the mission without telling this menu, so a second
    /// visit edited the old object: the page showed the change and the match never got it.
    /// </summary>
    [HarmonyPatch(typeof(CustomizeMissionMenu), "CloseCustomizeMenu")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void ClosePostfix(ref Mission ___mission, MissionPicker ___missionsPicker)
    {
        ___mission = ___missionsPicker.Mission;
    }

    [HarmonyPatch(typeof(CustomizeMissionMenu), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void AwakePostfix(CustomizeMissionMenu __instance)
    {
        Transform root = __instance.transform;
        Button? closeButton = Traverse.Create(__instance).Field("closeButton").GetValue<Button>();
        Transform? columnTemplate = Find(root, "MissionSettingsMenu");
        Transform? boxTemplate = Find(root, "FundsPanel");
        Transform? toggleTemplate = Find(root, "PreventPlayerJoinToggle");
        Slider? sliderTemplate = boxTemplate == null ? null : boxTemplate.GetComponentInChildren<Slider>(true);
        if (closeButton == null || columnTemplate == null || columnTemplate.Find("Title") == null || columnTemplate.parent == null
            || boxTemplate == null || toggleTemplate == null || sliderTemplate == null)
        {
            CommanderPlugin.Log.LogWarning("Customize Mission has changed shape; the Ground Control RTS page was not added.");
            return;
        }

        // Panels, the row holding the three tabs; the page is a fourth entry shown in their place.
        Transform row = columnTemplate.parent;
        List<GameObject> tabs = new();
        for (int i = 0; i < row.childCount; i++)
        {
            if (row.GetChild(i).gameObject.activeSelf)
            {
                tabs.Add(row.GetChild(i).gameObject);
            }
        }

        // Built inactive, so the copied tab's own scripts never wake before they are removed.
        GameObject page = new("GroundControlRts", typeof(RectTransform));
        page.SetActive(false);
        page.transform.SetParent(row, false);
        HorizontalLayoutGroup? rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        HorizontalLayoutGroup pageLayout = page.AddComponent<HorizontalLayoutGroup>();
        pageLayout.spacing = rowLayout != null ? rowLayout.spacing : 10f;
        pageLayout.childAlignment = TextAnchor.UpperLeft;
        pageLayout.childControlWidth = false;
        pageLayout.childControlHeight = true;
        pageLayout.childForceExpandWidth = false;
        pageLayout.childForceExpandHeight = false;
        LayoutElement pageSize = page.AddComponent<LayoutElement>();

        // The row does not size its entries; each tab fits itself from its top-left corner.
        RectTransform pageRect = (RectTransform)page.transform;
        pageRect.anchorMin = pageRect.anchorMax = pageRect.pivot = new Vector2(0f, 1f);
        ContentSizeFitter pageFit = page.AddComponent<ContentSizeFitter>();
        pageFit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        pageFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // One list per screen: the main menu holds two (singleplayer and CREATE LOBBY), and one
        // shared list was cleared by the second, so the first screen's rows never showed a value.
        List<Action> refreshers = page.AddComponent<RefreshWhenShown>().Refreshers;

        // The game's own "pick a unit, Add" pair from its Faction tab: the ban list, and the
        // dropdown every pick-one setting uses.
        object? factionTab = Traverse.Create(__instance).Field("factionTab").GetValue();
        TMP_Dropdown? unitSelect = factionTab == null ? null : Traverse.Create(factionTab).Field("unitSelect").GetValue<TMP_Dropdown>();
        Button? addUnitButton = factionTab == null ? null : Traverse.Create(factionTab).Field("addUnitButton").GetValue<Button>();

        Transform enemy = AddColumn(page.transform, columnTemplate, "Enemy Commander");
        AddToggle(
            refreshers,
            enemy,
            toggleTemplate,
            "Enemy AI also runs teams with players",
            () => Read(__instance, CommanderMapSettings.HumanTeamsPrefix, CommanderSettings.EnemyCommanderHumanTeams ? 1 : 0) != 0,
            value => Write(__instance, CommanderMapSettings.HumanTeamsPrefix, value ? 1 : 0));
        Transform enemyBox = AddBox(enemy, boxTemplate);
        AddChoice(
            refreshers,
            enemyBox,
            sliderTemplate,
            unitSelect,
            "AI Commanders",
            "Off, an AI commander for the side nobody plays, or one for every side. Works on every mission, base game and custom, and turns on the full mod there.",
            CommanderEnemyCommanderService.AiCommandersListed,
            CommanderEnemyCommanderService.AiCommandersBoth,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.AiCommandersPrefix, UntaggedAiCommanders())),
            value => Write(__instance, CommanderMapSettings.AiCommandersPrefix, value),
            AiCommandersLabel);
        AddChoice(
            refreshers,
            enemyBox,
            sliderTemplate,
            unitSelect,
            "Enemy Commander",
            "How the AI commanders are funded: MATCHED gives the AI your income, MISSION FUNDS the mission's.",
            CommanderEnemyCommanderService.ModeMatched,
            CommanderEnemyCommanderService.ModeMission,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.EnemyModePrefix, CommanderSettings.EnemyCommanderMode)),
            value => Write(__instance, CommanderMapSettings.EnemyModePrefix, value),
            EnemyModeLabel);
        AddChoice(
            refreshers,
            enemyBox,
            sliderTemplate,
            unitSelect,
            "Enemy Difficulty",
            "NORMAL buys a mixed army and attacks in small squads. CHALLENGING buys what counters your army and attacks in one massed push.",
            CommanderEnemyCommanderService.DifficultyNormal,
            CommanderEnemyCommanderService.DifficultyChallenging,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.EnemyDifficultyPrefix, CommanderSettings.EnemyDifficulty)),
            value => Write(__instance, CommanderMapSettings.EnemyDifficultyPrefix, value),
            CommanderEnemyCommanderService.GetDifficultyLabel);

        Transform forces = AddColumn(page.transform, columnTemplate, "Forces");
        AddToggle(
            refreshers,
            forces,
            toggleTemplate,
            FactionVehiclesOnlyLabel,
            () => Read(__instance, CommanderMapSettings.FactoryVehiclesPrefix, CommanderSettings.LimitToFactoryVehicles ? 1 : 0) != 0,
            value => Write(__instance, CommanderMapSettings.FactoryVehiclesPrefix, value ? 1 : 0));
        AddToggle(
            refreshers,
            forces,
            toggleTemplate,
            "Friendly AI off: only units you spawn",
            () => Read(__instance, CommanderMapSettings.FriendlyAiOffPrefix, CommanderSettings.DisableFriendlyAi ? 1 : 0) != 0,
            value => Write(__instance, CommanderMapSettings.FriendlyAiOffPrefix, value ? 1 : 0));
        AddToggle(
            refreshers,
            forces,
            toggleTemplate,
            "Fog of war: enemies unseen are not drawn",
            () => Read(__instance, CommanderMapSettings.FogOfWarPrefix, CommanderSettings.FogOfWar ? 1 : 0) != 0,
            value => Write(__instance, CommanderMapSettings.FogOfWarPrefix, value ? 1 : 0));
        AddToggle(
            refreshers,
            forces,
            toggleTemplate,
            "Unit ban vote before the match",
            () => Read(__instance, CommanderMapSettings.BanVotePrefix, CommanderSettings.UnitBansEnabled ? 1 : 0) != 0,
            value => Write(__instance, CommanderMapSettings.BanVotePrefix, value ? 1 : 0));
        AddSlider(
            refreshers,
            AddBox(forces, boxTemplate),
            sliderTemplate,
            "Voted Bans Per Side",
            "How many units each side bans in the vote, ground, air and naval alike. Each player has this many votes.",
            0,
            Mathf.Max(1, CountBannableUnits()),
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.BansPerSidePrefix, CommanderSettings.UnitBanVoteCount)),
            value => Write(__instance, CommanderMapSettings.BansPerSidePrefix, value),
            value => value == 0 ? "None" : value.ToString(System.Globalization.CultureInfo.CurrentCulture));
        AddChoice(
            refreshers,
            AddBox(forces, boxTemplate),
            sliderTemplate,
            unitSelect,
            "Unit Cap",
            "Most vehicles, ships and aircraft each side may have on the map at once, for players and the AI alike.",
            0,
            CommanderUnitCap.ModeCount,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.UnitCapPrefix, CommanderSettings.UnitCapEnabled ? CommanderSettings.UnitCapMode + 1 : 0)),
            value => Write(__instance, CommanderMapSettings.UnitCapPrefix, value),
            UnitCapLabel);
        Transform kindCaps = AddBox(forces, boxTemplate);
        AddSlider(
            refreshers,
            kindCaps,
            sliderTemplate,
            "Ship Cap",
            "Most ships each side may have at sea at once, for players and the AI alike. 0 turns it off.",
            0,
            CommanderUnitCap.MaxKindCap,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.ShipCapPrefix, 0)),
            value => Write(__instance, CommanderMapSettings.ShipCapPrefix, value),
            KindCapLabel);
        AddSlider(
            refreshers,
            kindCaps,
            sliderTemplate,
            "Artillery Cap",
            "Most artillery vehicles each side may field at once, for players and the AI alike. 0 turns it off.",
            0,
            CommanderUnitCap.MaxKindCap,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.ArtilleryCapPrefix, 0)),
            value => Write(__instance, CommanderMapSettings.ArtilleryCapPrefix, value),
            KindCapLabel);
        AddSlider(
            refreshers,
            kindCaps,
            sliderTemplate,
            "Aircraft Cap",
            "Most aircraft each side may have up at once, for players and the AI alike. 0 turns it off.",
            0,
            CommanderUnitCap.MaxKindCap,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.AircraftCapPrefix, 0)),
            value => Write(__instance, CommanderMapSettings.AircraftCapPrefix, value),
            KindCapLabel);

        if (unitSelect != null && addUnitButton != null)
        {
            AddBanList(refreshers, AddColumn(page.transform, columnTemplate, "Banned Units"), boxTemplate, toggleTemplate, unitSelect, addUnitButton);
        }
        else
        {
            CommanderPlugin.Log.LogWarning("Customize Mission's Faction tab has changed shape; the Banned Units list was not added.");
        }

        // Half-kilometre and half-point steps, as in Settings > Gameplay.
        Transform bases = AddColumn(page.transform, columnTemplate, "Bases");
        Transform basesBox = AddBox(bases, boxTemplate);
        AddSlider(
            refreshers,
            basesBox,
            sliderTemplate,
            "Build Radius",
            "How far from a base its buildings may be placed.",
            2,
            30,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.BuildRadiusPrefix, CommanderSettings.BuildRadiusKm) * 2f),
            value => Write(__instance, CommanderMapSettings.BuildRadiusPrefix, value * 0.5f, " km"),
            value => $"{value * 0.5f:0.#} km");
        AddSlider(
            refreshers,
            basesBox,
            sliderTemplate,
            "Naval Dock Radius",
            "How far from a base a naval dock may be placed, which decides whether a base can reach the sea.",
            2,
            50,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.DockRadiusPrefix, CommanderSettings.NavalDockRadiusKm) * 2f),
            value => Write(__instance, CommanderMapSettings.DockRadiusPrefix, value * 0.5f, " km"),
            value => $"{value * 0.5f:0.#} km");
        AddSlider(
            refreshers,
            basesBox,
            sliderTemplate,
            "Aircraft Capture Strength",
            "How much an aircraft parked in a capture ring counts towards taking the base. 0 turns it off.",
            0,
            32,
            () => Mathf.RoundToInt(Read(__instance, CommanderMapSettings.AircraftCapturePrefix, CommanderSettings.AircraftCaptureStrength) * 2f),
            value => Write(__instance, CommanderMapSettings.AircraftCapturePrefix, value * 0.5f),
            value => $"{value * 0.5f:0.#}");

        if (addUnitButton != null)
        {
            AddPricesButton(__instance, refreshers, bases, boxTemplate, addUnitButton);
        }

        AddPageButton(closeButton, page, pageSize, rowLayout, tabs);
    }

    /// <summary>
    /// Opens UNIT PRICES, the economy profile editor. It lives here, before the match, because a
    /// match's prices are fixed once it starts. The label names the profile the next match will use.
    /// </summary>
    private static void AddPricesButton(CustomizeMissionMenu menu, List<Action> refreshers, Transform column, Transform boxTemplate, Button buttonTemplate)
    {
        Transform box = AddBox(column, boxTemplate);
        box.name = "GroundControl Prices";
        PadEvenly(box);
        Button button = Object.Instantiate(buttonTemplate.gameObject, box).GetComponent<Button>();
        button.name = "GroundControl Prices Button";
        button.onClick = new Button.ButtonClickedEvent();
        TextMeshProUGUI? label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        void Refresh()
        {
            if (label != null)
            {
                int changes = CommanderPriceProfile.ChangeCount(CommanderPriceProfile.Active);
                string? map = CommanderPriceProfile.DescribeMission(Edited(menu));
                label.text = map != null ? $"Unit Prices: Map's {map}"
                    : CommanderPriceProfile.IsStandard
                    ? "Unit Prices: Standard"
                    : $"Unit Prices: {CommanderPriceProfile.ProfileName} ({changes})";
            }
        }

        button.onClick.AddListener(() =>
        {
            // A map with its own prices plays them, so those are what this match's copy edits.
            Mission? mission = Edited(menu);
            if (mission != null && CommanderPriceProfile.DescribeMission(mission) != null)
            {
                CommanderPriceProfile.EditMission(mission, Refresh);
            }
            else
            {
                CommanderPriceEditorUi.Open(Refresh);
            }
        });
        refreshers.Add(Refresh);
    }

    /// <summary>A copy of the Mission Parameters column: its plate, layout and big title, emptied.</summary>
    private static Transform AddColumn(Transform page, Transform template, string title)
    {
        Transform column = Object.Instantiate(template.gameObject, page).transform;
        column.name = "GroundControl " + title;
        Object.DestroyImmediate(column.GetComponent<MissionSettingsTab>());
        Object.DestroyImmediate(column.GetComponent<ContentSizeFitter>());
        for (int i = column.childCount - 1; i >= 0; i--)
        {
            Transform child = column.GetChild(i);
            if (child.name == "Title")
            {
                child.GetComponent<TextMeshProUGUI>().text = title;
            }
            else
            {
                Object.DestroyImmediate(child.gameObject);
            }
        }

        return column;
    }

    /// <summary>A copy of the Funds box, emptied: the inset plate the game groups its sliders in.</summary>
    private static Transform AddBox(Transform column, Transform template)
    {
        Transform box = Object.Instantiate(template.gameObject, column).transform;
        box.name = "GroundControl Box";
        for (int i = box.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(box.GetChild(i).gameObject);
        }

        return box;
    }

    /// <summary>
    /// The Funds box pads its top for a slider's label, which hangs above the slider's rect, and
    /// barely pads its bottom. A box of dropdowns, buttons or ticks needs even padding, or its last
    /// row runs through the bottom edge.
    /// </summary>
    private static void PadEvenly(Transform box)
    {
        LayoutGroup? layout = box.GetComponent<LayoutGroup>();
        if (layout != null)
        {
            layout.padding = new RectOffset(layout.padding.left, layout.padding.right, 10, 10);
        }
    }

    /// <summary>A copy of Close beside it that swaps the mission tabs for the RTS page and back.
    /// The page takes the tabs' size, so the window does not change. Close always returns to the
    /// tabs, so the screen reopens on the game's own page.</summary>
    private static void AddPageButton(Button closeButton, GameObject page, LayoutElement pageSize, HorizontalLayoutGroup? rowLayout, List<GameObject> tabs)
    {
        GameObject copy = Object.Instantiate(closeButton.gameObject, closeButton.transform.parent);
        copy.name = "GroundControlRtsButton";
        RectTransform rect = (RectTransform)copy.transform;
        float closeWidth = rect.rect.width;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x + (200f - closeWidth), rect.sizeDelta.y);
        rect.anchoredPosition += new Vector2((closeWidth / 2f) + 120f, 0f);

        TextMeshProUGUI? label = copy.GetComponentInChildren<TextMeshProUGUI>(true);
        Button button = copy.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        void Show(bool rts)
        {
            if (rts && !page.activeSelf && rowLayout != null)
            {
                Rect tabsRect = ((RectTransform)rowLayout.transform).rect;
                pageSize.minWidth = tabsRect.width - rowLayout.padding.horizontal;
                pageSize.minHeight = tabsRect.height - rowLayout.padding.vertical;
            }

            page.SetActive(rts);
            for (int i = 0; i < tabs.Count; i++)
            {
                tabs[i].SetActive(!rts);
            }

            if (label != null)
            {
                label.text = rts ? "Mission Settings" : "GC RTS Settings";
            }
        }

        button.onClick.AddListener(() => Show(!page.activeSelf));
        closeButton.onClick.AddListener(() => Show(false));
        Show(false);
    }

    private static void AddSlider(
        List<Action> refreshers,
        Transform panel,
        Slider template,
        string title,
        string hover,
        int min,
        int max,
        Func<int> read,
        Action<int> write,
        Func<int, string> format)
    {
        GameObject row = Object.Instantiate(template.gameObject, panel);
        row.name = "GroundControl " + title;
        ShowHoverText? hoverText = row.GetComponent<ShowHoverText>();
        if (hoverText != null)
        {
            hoverText.SetText(hover);
        }

        row.transform.Find("Label").GetComponent<TextMeshProUGUI>().text = title;

        // The value readout sits beside the handle, not under it; it is the row's other text.
        TextMeshProUGUI? valueText = null;
        foreach (TextMeshProUGUI text in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text.name != "Label")
            {
                valueText = text;
            }
        }

        Slider slider = row.GetComponent<Slider>();
        slider.onValueChanged = new Slider.SliderEvent();

        // Free-moving like the game's own sliders; only the stored value is rounded. Whole-number
        // snapping made the handle jump step to step across the short bar.
        slider.wholeNumbers = false;
        slider.minValue = min;
        slider.maxValue = max;
        void Label(int value)
        {
            if (valueText != null)
            {
                valueText.text = format(value);
            }
        }

        void Show()
        {
            int value = Mathf.Clamp(read(), min, max);
            slider.SetValueWithoutNotify(value);
            Label(value);
        }

        slider.onValueChanged.AddListener(value =>
        {
            int rounded = Mathf.Clamp(Mathf.RoundToInt(value), min, max);
            write(rounded);
            Label(rounded);
        });
        refreshers.Add(Show);
    }

    /// <summary>
    /// A setting with a few named values: its title over a copy of the game's dropdown. Falls back
    /// to a slider when the Faction tab's dropdown was not found.
    /// </summary>
    private static void AddChoice(
        List<Action> refreshers,
        Transform panel,
        Slider sliderTemplate,
        TMP_Dropdown? dropdownTemplate,
        string title,
        string hover,
        int min,
        int max,
        Func<int> read,
        Action<int> write,
        Func<int, string> format)
    {
        if (dropdownTemplate == null)
        {
            AddSlider(refreshers, panel, sliderTemplate, title, hover, min, max, read, write, format);
            return;
        }

        PadEvenly(panel);
        Transform? labelTemplate = sliderTemplate.transform.Find("Label");
        if (labelTemplate != null)
        {
            GameObject label = Object.Instantiate(labelTemplate.gameObject, panel);
            label.name = "GroundControl " + title + " Label";
            label.GetComponent<TextMeshProUGUI>().text = title;

            // The slider's label is 30 tall to clear its bar; above a dropdown that reads as a gap.
            RectTransform labelRect = (RectTransform)label.transform;
            labelRect.sizeDelta = new Vector2(labelRect.sizeDelta.x, 18f);
        }

        TMP_Dropdown dropdown = Object.Instantiate(dropdownTemplate.gameObject, panel).GetComponent<TMP_Dropdown>();
        dropdown.name = "GroundControl " + title;
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        List<string> labels = new();
        for (int value = min; value <= max; value++)
        {
            labels.Add(format(value));
        }

        dropdown.ClearOptions();
        dropdown.AddOptions(labels);

        // The slider's hover text, moved onto the dropdown; ShowHoverText throws without a HoverText.
        ShowHoverText? source = sliderTemplate.GetComponent<ShowHoverText>();
        HoverText? hoverBox = source == null ? null : Traverse.Create(source).Field("hover").GetValue<HoverText>();
        if (hoverBox != null)
        {
            if (!dropdown.TryGetComponent(out ShowHoverText hoverText))
            {
                hoverText = dropdown.gameObject.AddComponent<ShowHoverText>();
            }

            hoverText.SetHover(hoverBox);
            hoverText.SetText(hover);
        }

        dropdown.onValueChanged.AddListener(index => write(min + index));
        refreshers.Add(() =>
        {
            dropdown.SetValueWithoutNotify(Mathf.Clamp(read(), min, max) - min);
            dropdown.RefreshShownValue();
        });
    }

    /// <summary>
    /// The host's ban list: every aircraft, vehicle and ship in a copy of the game's unit dropdown,
    /// BAN UNIT under it, and each banned unit as a ticked row below; untick it to lift the ban.
    /// Saved straight to the config, as jsonKeys, like every other value on the page.
    /// </summary>
    private static void AddBanList(List<Action> refreshers, Transform column, Transform boxTemplate, Transform toggleTemplate, TMP_Dropdown dropdownTemplate, Button buttonTemplate)
    {
        Transform pickBox = AddBox(column, boxTemplate);
        PadEvenly(pickBox);
        TMP_Dropdown dropdown = Object.Instantiate(dropdownTemplate.gameObject, pickBox).GetComponent<TMP_Dropdown>();
        dropdown.name = "GroundControl Ban Picker";
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();

        // A new event drops the persistent listener the scene gave the original (add a supply unit).
        Button add = Object.Instantiate(buttonTemplate.gameObject, pickBox).GetComponent<Button>();
        add.name = "GroundControl Ban Button";
        add.onClick = new Button.ButtonClickedEvent();
        TextMeshProUGUI? addLabel = add.GetComponentInChildren<TextMeshProUGUI>(true);
        if (addLabel != null)
        {
            addLabel.text = "Ban unit";
        }

        Transform listBox = AddBox(column, boxTemplate);
        PadEvenly(listBox);
        List<UnitDefinition> choices = new();
        List<GameObject> rows = new();

        void Save(List<UnitDefinition> banned)
        {
            List<string> keys = new();
            for (int i = 0; i < banned.Count; i++)
            {
                keys.Add(banned[i].jsonKey);
            }

            CommanderSettings.HostBannedUnits = string.Join(",", keys);
        }

        void Rebuild()
        {
            List<UnitDefinition> banned = CommanderUnitBanService.ReadHostBans();
            choices.Clear();
            List<string> labels = new();
            Encyclopedia? encyclopedia = Encyclopedia.i;
            if (encyclopedia != null)
            {
                AddChoices(encyclopedia.aircraft, banned, choices);
                AddChoices(encyclopedia.vehicles, banned, choices);
                AddChoices(encyclopedia.ships, banned, choices);
            }

            for (int i = 0; i < choices.Count; i++)
            {
                labels.Add(DescribeUnit(choices[i]));
            }

            dropdown.ClearOptions();
            dropdown.AddOptions(labels);
            dropdown.SetValueWithoutNotify(0);
            dropdown.RefreshShownValue();
            add.interactable = choices.Count > 0;

            // Hidden at once so the layout drops them this frame; destroyed at the end of it,
            // since the click that asked for this may have come from one of them.
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].SetActive(false);
                Object.Destroy(rows[i]);
            }

            rows.Clear();
            for (int i = 0; i < banned.Count; i++)
            {
                UnitDefinition unit = banned[i];
                GameObject row = Object.Instantiate(toggleTemplate.gameObject, listBox);
                row.name = "GroundControl Banned " + unit.jsonKey;
                row.transform.Find("Text (TMP)").GetComponent<TextMeshProUGUI>().text = DescribeUnit(unit);
                Transform? differs = Find(row.transform, "ToggleDifferentValue");
                if (differs != null)
                {
                    differs.gameObject.SetActive(false);
                }

                Toggle toggle = row.GetComponentInChildren<Toggle>(true);
                toggle.onValueChanged = new Toggle.ToggleEvent();
                toggle.SetIsOnWithoutNotify(true);
                toggle.onValueChanged.AddListener(on =>
                {
                    if (!on)
                    {
                        List<UnitDefinition> now = CommanderUnitBanService.ReadHostBans();
                        now.Remove(unit);
                        Save(now);
                        Rebuild();
                    }
                });
                rows.Add(row);
            }

            listBox.gameObject.SetActive(rows.Count > 0);
        }

        add.onClick.AddListener(() =>
        {
            int index = dropdown.value;
            if (index >= 0 && index < choices.Count)
            {
                List<UnitDefinition> banned = CommanderUnitBanService.ReadHostBans();
                banned.Add(choices[index]);
                Save(banned);
                Rebuild();
            }
        });
        refreshers.Add(Rebuild);
    }

    private static void AddChoices<T>(List<T>? units, List<UnitDefinition> banned, List<UnitDefinition> into)
        where T : UnitDefinition
    {
        if (units == null)
        {
            return;
        }

        int first = into.Count;
        for (int i = 0; i < units.Count; i++)
        {
            T unit = units[i];
            if (unit != null && unit.unitPrefab != null && unit.IsAllowed(includeEventContent: false) && !banned.Contains(unit) && !into.Contains(unit))
            {
                into.Add(unit);
            }
        }

        into.Sort(first, into.Count - first, Comparer<UnitDefinition>.Create(static (a, b) => string.Compare(a.unitName, b.unitName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Every aircraft, vehicle and ship the game has: no side can ban more than exist.</summary>
    private static int CountBannableUnits()
    {
        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia == null)
        {
            return 0;
        }

        List<UnitDefinition> all = new();
        List<UnitDefinition> none = new();
        AddChoices(encyclopedia.aircraft, none, all);
        AddChoices(encyclopedia.vehicles, none, all);
        AddChoices(encyclopedia.ships, none, all);
        return all.Count;
    }

    private static string DescribeUnit(UnitDefinition unit)
    {
        string kind = unit switch
        {
            AircraftDefinition => "Air",
            ShipDefinition => "Naval",
            _ => "Ground",
        };
        return $"{kind}: {unit.unitName}";
    }

    private static void AddToggle(List<Action> refreshers, Transform panel, Transform template, string label, Func<bool> read, Action<bool> write)
    {
        GameObject row = Object.Instantiate(template.gameObject, panel);
        row.name = "GroundControl " + label;
        row.transform.Find("Text (TMP)").GetComponent<TextMeshProUGUI>().text = label;

        // The game's "factions differ" dash; there is only one value here.
        Transform? differs = Find(row.transform, "ToggleDifferentValue");
        if (differs != null)
        {
            differs.gameObject.SetActive(false);
        }

        Toggle toggle = row.GetComponentInChildren<Toggle>(true);
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.onValueChanged.AddListener(value => write(value));
        refreshers.Add(() => toggle.SetIsOnWithoutNotify(read()));
    }

    private static Transform? Find(Transform root, string name)
    {
        foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
        {
            if (node.name == name)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Shows the saved values once the screen is up. Values written while it is still hidden do not
    /// stick: the copied labels kept their placeholder and the toggles all read on. Done a frame after it opens, when every row has woken, and again on every
    /// opening, since the config may have changed in a match.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Unity creates it through AddComponent.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Unity calls OnEnable and Update.")]
    private sealed class RefreshWhenShown : MonoBehaviour
    {
        private bool pending;

        internal List<Action> Refreshers { get; } = new();

        private void OnEnable()
        {
            pending = true;
        }

        private void Update()
        {
            if (!pending)
            {
                return;
            }

            pending = false;
            foreach (Action refresh in Refreshers)
            {
                refresh();
            }
        }
    }
}
