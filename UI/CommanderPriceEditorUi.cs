using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GroundControlRts;

/// <summary>
/// UNIT PRICES, the economy profile editor, opened from the Edit Unit Prices button on the GC RTS
/// Settings page before a match: the profile picker, the income multiplier and one row per unit and
/// building with its default, the player's price and, for ground vehicles, the depot tier.
/// Everything it changes goes through <see cref="CommanderPriceProfile"/>, which saves as it goes.
/// </summary>
/// <remarks>
/// It only exists in the menu, so a match's prices are fixed from the moment it starts: nothing in a
/// match can change the profile. It is IMGUI over the game's uGUI menu, and IMGUI clicks also reach
/// the canvas underneath, so a clear full-screen uGUI plate takes every uGUI click while it is open.
/// </remarks>
internal static class CommanderPriceEditorUi
{
    private const int WindowId = 0x47435052;
    private const float RowHeight = 30f;
    private const string PriceFieldPrefix = "gc.price.";
    private const string ProfileNameField = "gc.profile";
    private const string SearchField = "gc.search";
    private static readonly string[] FilterLabels = { "ALL", "GROUND", "AIR", "NAVAL", "CARGO", "BUILDINGS", "CHANGED" };
    private static readonly float[] FilterWidths = { 52f, 78f, 52f, 66f, 66f, 96f, 86f };
    private static readonly List<PriceRow> Rows = new();
    private static readonly HashSet<string> RowNames = new(StringComparer.Ordinal);

    private static Rect windowRect;
    private static GameObject? blocker;
    private static Action? onClosed;
    private static Vector2 scroll;
    private static int filter;
    private static string search = string.Empty;
    private static int rowsRevision = -1;
    private static string? editKey;
    private static string editText = string.Empty;
    private static string? profileNameEdit;
    private static float deleteArmedUntil;
    private static float resetArmedUntil;

    private enum PriceKind
    {
        Ground,
        Air,
        Naval,
        Cargo,
        Building,
    }

    internal static bool IsOpen { get; private set; }

    /// <summary>The menu button. <paramref name="closed"/> runs when the window closes, to refresh the button's label.</summary>
    internal static void Open(Action? closed)
    {
        if (IsOpen || GameManager.gameState is GameState.SinglePlayer or GameState.Multiplayer)
        {
            return;
        }

        // The defaults column is the mod's own price pass, which otherwise only runs in a match.
        CommanderEconomyService.InvalidatePrices();
        CommanderEconomyService.EnsureVehiclePrices();
        onClosed = closed;
        IsOpen = true;
        rowsRevision = -1;
        float width = Mathf.Min(780f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(900f, CommanderUiScale.Height - 48f);
        windowRect = new Rect((CommanderUiScale.Width - width) * 0.5f, (CommanderUiScale.Height - height) * 0.5f, width, height);
        EventSystem.current?.SetSelectedGameObject(null);
        blocker = CreateBlocker();
    }

    internal static void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        editKey = null;
        CommitProfileName();
        if (blocker != null)
        {
            Object.Destroy(blocker);
            blocker = null;
        }

        onClosed?.Invoke();
        onClosed = null;
    }

    /// <summary>From the mode controller's OnGUI, every frame. Closes itself if a match has started.</summary>
    internal static void Draw()
    {
        if (!IsOpen)
        {
            return;
        }

        if (GameManager.gameState is GameState.SinglePlayer or GameState.Multiplayer)
        {
            Close();
            return;
        }

        // Dims the menu behind, so it reads as a dialog over it.
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0f, 0f, CommanderUiScale.Width, CommanderUiScale.Height), Texture2D.whiteTexture);
        GUI.color = previous;
        windowRect.size = CommanderWindowScale.Size(
            WindowId,
            new Vector2(Mathf.Min(780f, CommanderUiScale.Width - 24f), Mathf.Min(900f, CommanderUiScale.Height - 48f)),
            new Vector2(480f, 360f));
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "UNIT PRICES");
    }

    /// <summary>A clear, screen-sized plate on top of every canvas that takes the menu's clicks.</summary>
    private static GameObject CreateBlocker()
    {
        GameObject root = new("GroundControl Price Editor Blocker", typeof(Canvas), typeof(GraphicRaycaster));
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        GameObject plate = new("Plate", typeof(RectTransform), typeof(Image));
        plate.transform.SetParent(root.transform, false);
        RectTransform rect = (RectTransform)plate.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        plate.GetComponent<Image>().color = Color.clear;
        return root;
    }

    private static void DrawWindow(int id)
    {
        float width = windowRect.width - 24f;
        HandleKeys();
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            Close();
            return;
        }

        float y = 38f;
        float panelHeight = 146f;
        GUI.Box(new Rect(12f, y, width, panelHeight), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, width - 24f, 22f), "ECONOMY PROFILE", CommanderUiTheme.Header);
        DrawProfileRow(y + 38f, width);

        float income = CommanderPriceProfile.IncomeMultiplier;
        GUI.Label(
            new Rect(24f, y + 78f, 250f, 24f),
            new GUIContent($"Income buildings pay   x{income:0.00}", "Multiplies what every gold mine, fuel exchange and gas terminal pays, for every side."),
            CommanderUiTheme.Label);
        float slid = Mathf.Round(GUI.HorizontalSlider(new Rect(280f, y + 84f, width - 280f, 20f), income, 0.25f, 3f) * 20f) / 20f;
        if (!Mathf.Approximately(slid, income))
        {
            CommanderPriceProfile.SetIncome(slid);
        }

        GUI.Label(new Rect(24f, y + 112f, width - 24f, 24f), GetProfileStatus(), CommanderUiTheme.MutedLabel);

        float listY = y + panelHeight + 12f;
        float footerY = windowRect.height - 50f;
        DrawList(listY, width, footerY - listY - 10f);

        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && CommanderPriceProfile.ChangeCount(CommanderPriceProfile.Active) > 0;
        bool armed = Time.realtimeSinceStartup < resetArmedUntil;
        if (GUI.Button(
            new Rect(12f, footerY, 220f, 34f),
            new GUIContent(armed ? "CLICK AGAIN TO RESET" : "RESET ALL PRICES", "Puts every unit, building and the income back to the mod's own numbers in this profile."),
            armed ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            if (armed)
            {
                CommanderPriceProfile.ResetAll();
                resetArmedUntil = 0f;
                ClearEdits();
            }
            else
            {
                resetArmedUntil = Time.realtimeSinceStartup + 3f;
            }
        }

        GUI.enabled = wasEnabled;
        GUI.Label(
            new Rect(244f, footerY, width - 244f - 120f, 34f),
            "Prices are in millions and are fixed once the match starts. Units you leave alone follow the mod's updates.",
            CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(12f + width - 110f, footerY, 110f, 34f), "DONE", CommanderUiTheme.PrimaryButton))
        {
            Close();
            return;
        }

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, windowRect.width, windowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 40f, 28f));
    }

    /// <summary>Enter ends an edit, Escape too, or closes the window when nothing is being typed.</summary>
    private static void HandleKeys()
    {
        string focused = GUI.GetNameOfFocusedControl() ?? string.Empty;
        bool typing = focused.StartsWith("gc.", StringComparison.Ordinal);

        Event current = Event.current;
        if (current.type == EventType.KeyDown && current.keyCode is KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Escape)
        {
            if (!typing && current.keyCode == KeyCode.Escape)
            {
                current.Use();
                Close();
                return;
            }

            if (typing)
            {
                if (focused == ProfileNameField && current.keyCode != KeyCode.Escape)
                {
                    CommitProfileName();
                }

                GUI.FocusControl(null);
                current.Use();
            }
        }

        if (editKey != null && focused != PriceFieldPrefix + editKey)
        {
            editKey = null;
        }

        if (profileNameEdit != null && focused != ProfileNameField)
        {
            CommitProfileName();
        }
    }

    private static void DrawProfileRow(float y, float width)
    {
        List<string> names = CommanderPriceProfile.ListProfiles();
        int index = Mathf.Max(0, names.IndexOf(CommanderPriceProfile.ProfileName));
        if (GUI.Button(new Rect(24f, y, 30f, 30f), new GUIContent("<", "Previous profile"), CommanderUiTheme.Button))
        {
            SwitchProfile(names[(index + names.Count - 1) % names.Count]);
            return;
        }

        Rect nameRect = new(58f, y, 230f, 30f);
        if (CommanderPriceProfile.IsStandard)
        {
            GUI.Box(nameRect, string.Empty, CommanderUiTheme.Inset);
            TextAnchor anchor = CommanderUiTheme.Header.alignment;
            CommanderUiTheme.Header.alignment = TextAnchor.MiddleCenter;
            GUI.Label(nameRect, new GUIContent(CommanderPriceProfile.StandardName, "The mod's own prices. Change one and a profile of your own is started."), CommanderUiTheme.Header);
            CommanderUiTheme.Header.alignment = anchor;
        }
        else
        {
            GUI.SetNextControlName(ProfileNameField);
            string shown = profileNameEdit ?? CommanderPriceProfile.ProfileName;
            string typed = GUI.TextField(nameRect, shown, 40);
            if (typed != shown)
            {
                profileNameEdit = typed;
            }
        }

        if (GUI.Button(new Rect(292f, y, 30f, 30f), new GUIContent(">", "Next profile"), CommanderUiTheme.Button))
        {
            SwitchProfile(names[(index + 1) % names.Count]);
            return;
        }

        float right = 24f + width - 12f;
        if (GUI.Button(new Rect(right - 118f, y, 118f, 30f), new GUIContent("OPEN FOLDER", "Where profiles are saved. Share one by copying its file into a friend's folder."), CommanderUiTheme.Button))
        {
            CommanderPriceProfile.OpenFolder();
        }

        bool armed = Time.realtimeSinceStartup < deleteArmedUntil;
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && !CommanderPriceProfile.IsStandard;
        if (GUI.Button(
            new Rect(right - 222f, y, 100f, 30f),
            armed ? "SURE?" : "DELETE",
            armed ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            if (armed)
            {
                CommanderPriceProfile.DeleteCurrent();
                deleteArmedUntil = 0f;
                ClearEdits();
            }
            else
            {
                deleteArmedUntil = Time.realtimeSinceStartup + 3f;
            }
        }

        GUI.enabled = wasEnabled;
        if (GUI.Button(new Rect(right - 326f, y, 100f, 30f), new GUIContent("COPY", "A new profile starting from this one."), CommanderUiTheme.Button))
        {
            CommanderPriceProfile.Duplicate();
            ClearEdits();
        }
    }

    private static string GetProfileStatus()
    {
        int changes = CommanderPriceProfile.ChangeCount(CommanderPriceProfile.Active);
        return CommanderPriceProfile.IsStandard
            ? "The mod's own prices. Type a new price below and it is saved as a profile of your own."
            : $"{changes} change{(changes == 1 ? string.Empty : "s")}, saved as you type. When you host, everyone plays with these.";
    }

    private static void SwitchProfile(string name)
    {
        CommitProfileName();
        CommanderPriceProfile.Load(name);
        ClearEdits();
    }

    private static void CommitProfileName()
    {
        string? typed = profileNameEdit;
        profileNameEdit = null;
        if (typed != null && typed.Trim() != CommanderPriceProfile.ProfileName && !CommanderPriceProfile.Rename(typed))
        {
            CommanderPlugin.Log.LogWarning($"Economy profile not renamed to '{typed}': the name is empty, taken or not allowed.");
        }
    }

    private static void ClearEdits()
    {
        editKey = null;
        profileNameEdit = null;
        GUI.FocusControl(null);
    }

    private static void DrawList(float y, float width, float height)
    {
        GUI.Box(new Rect(12f, y, width, height), string.Empty, CommanderUiTheme.Panel);

        // Filters on the left, search on the right.
        float x = 20f;
        for (int i = 0; i < FilterLabels.Length; i++)
        {
            float w = FilterWidths[i];
            if (GUI.Button(new Rect(x, y + 8f, w, 28f), FilterLabels[i], filter == i ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                filter = i;
                scroll = Vector2.zero;
                rowsRevision = -1;
            }

            x += w + 2f;
        }

        Rect searchRect = new(x + 8f, y + 8f, 12f + width - x - 16f, 28f);
        GUI.SetNextControlName(SearchField);
        string typedSearch = GUI.TextField(searchRect, search, 32);
        if (typedSearch.Length == 0 && GUI.GetNameOfFocusedControl() != SearchField)
        {
            GUI.Label(new Rect(searchRect.x + 8f, searchRect.y, searchRect.width - 8f, searchRect.height), "Search...", CommanderUiTheme.MutedLabel);
        }

        if (typedSearch != search)
        {
            search = typedSearch;
            scroll = Vector2.zero;
            rowsRevision = -1;
        }

        if (Event.current.type == EventType.Layout)
        {
            RefreshRows();
        }

        // Column header. The rows below read the same numbers.
        Rect view = new(16f, y + 66f, width - 8f, height - 74f);
        float inner = view.width - 14f;
        PriceColumns columns = new(inner);
        float headerY = y + 44f;
        GUI.Label(new Rect(view.x + columns.Name, headerY, columns.TypeX - columns.Name, 18f), $"NAME ({Rows.Count})", CommanderUiTheme.Caption);
        GUI.Label(new Rect(view.x + columns.TypeX, headerY, 90f, 18f), "TYPE", CommanderUiTheme.Caption);
        GUI.Label(new Rect(view.x + columns.DefaultX, headerY, columns.DefaultWidth, 18f), "DEFAULT", CommanderUiTheme.Caption);
        GUI.Label(new Rect(view.x + columns.PriceX, headerY, columns.PriceWidth + 20f, 18f), "YOUR PRICE (M)", CommanderUiTheme.Caption);
        GUI.Label(new Rect(view.x + columns.TierX, headerY, 90f, 18f), new GUIContent("DEPOT TIER", "The depot level a ground vehicle needs before it can be bought."), CommanderUiTheme.Caption);
        CommanderUiTheme.DrawRule(new Rect(view.x, y + 63f, view.width, 1f));

        if (Rows.Count == 0)
        {
            GUI.Label(
                new Rect(view.x + 8f, view.y + 8f, view.width - 16f, 48f),
                CommanderEconomyService.PricedUnits.Count == 0 ? "The game has not loaded its units yet." : "Nothing matches.",
                CommanderUiTheme.MutedLabel);
            return;
        }

        Rect content = new(0f, 0f, inner, Rows.Count * RowHeight);
        scroll = GUI.BeginScrollView(view, scroll, content);
        int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight));
        int last = Mathf.Min(Rows.Count - 1, Mathf.CeilToInt((scroll.y + view.height) / RowHeight));
        for (int i = first; i <= last; i++)
        {
            DrawRow(Rows[i], i * RowHeight, columns);
        }

        GUI.EndScrollView();
    }

    private static void DrawRow(PriceRow row, float y, PriceColumns columns)
    {
        float price = row.Kind == PriceKind.Building
            ? CommanderPriceProfile.TryGetBuildingPrice(row.Name, out float building) ? building : row.DefaultPrice
            : CommanderPriceProfile.TryGetUnitPrice(row.Name, out float unit) ? unit : row.DefaultPrice;
        int tier = row.DefaultTier > 0 && CommanderPriceProfile.TryGetTier(row.Name, out int set) ? set : row.DefaultTier;
        bool changed = !Mathf.Approximately(price, row.DefaultPrice) || tier != row.DefaultTier;

        CommanderUiTheme.DrawRule(new Rect(0f, y + RowHeight - 1f, columns.ResetX + columns.ResetWidth, 1f), 0.12f);
        if (changed)
        {
            Color previous = GUI.color;
            GUI.color = CommanderUiTheme.Accent;
            GUI.DrawTexture(new Rect(0f, y + 4f, 3f, RowHeight - 8f), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        CommanderUiTheme.FitLabel(new Rect(columns.Name, y, columns.TypeX - columns.Name - 6f, RowHeight), row.Name, CommanderUiTheme.Label);
        GUI.Label(new Rect(columns.TypeX, y + 6f, columns.DefaultX - columns.TypeX - 4f, 20f), row.Type, CommanderUiTheme.Caption);
        GUI.Label(new Rect(columns.DefaultX, y, columns.DefaultWidth, RowHeight), row.DefaultLabel, CommanderUiTheme.MutedLabel);

        bool editing = editKey == row.Key;
        string shown = editing ? editText : FormatPrice(price);
        GUI.SetNextControlName(PriceFieldPrefix + row.Key);
        string typed = GUI.TextField(new Rect(columns.PriceX, y + 2f, columns.PriceWidth, RowHeight - 4f), shown, 9);
        if (typed != shown)
        {
            editKey = row.Key;
            editText = typed;
            if (TryParsePrice(typed, out float value))
            {
                if (row.Kind == PriceKind.Building)
                {
                    CommanderPriceProfile.SetBuildingPrice(row.Name, value, row.DefaultPrice);
                }
                else
                {
                    CommanderPriceProfile.SetUnitPrice(row.Name, value, row.DefaultPrice);
                }
            }
        }

        if (row.DefaultTier > 0)
        {
            for (int level = 1; level <= CommanderEconomyService.DepotMaxLevel; level++)
            {
                if (GUI.Button(
                    new Rect(columns.TierX + ((level - 1) * 30f), y + 2f, 28f, RowHeight - 4f),
                    level.ToString(CultureInfo.InvariantCulture),
                    tier == level ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
                {
                    CommanderPriceProfile.SetTier(row.Name, level, row.DefaultTier);
                }
            }
        }
        else
        {
            GUI.Label(new Rect(columns.TierX + 36f, y, 30f, RowHeight), "-", CommanderUiTheme.MutedLabel);
        }

        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && changed;
        if (GUI.Button(new Rect(columns.ResetX, y + 2f, columns.ResetWidth, RowHeight - 4f), new GUIContent("RESET", "Back to the default price and tier."), CommanderUiTheme.Button))
        {
            CommanderPriceProfile.ResetUnit(row.Name);
            editKey = null;
            GUI.FocusControl(null);
        }

        GUI.enabled = wasEnabled;
    }

    /// <summary>Rebuilt only when the filter, the search or a price changed, never per frame.</summary>
    private static void RefreshRows()
    {
        if (CommanderPriceProfile.Revision == rowsRevision)
        {
            return;
        }

        rowsRevision = CommanderPriceProfile.Revision;
        Rows.Clear();
        RowNames.Clear();

        // First, so a radar trailer shows what it costs as cargo rather than its vehicle value.
        foreach (UnitDefinition cargo in CommanderSupplyHeliService.FindCargoOnlyUnits())
        {
            if (RowNames.Add(cargo.unitName))
            {
                AddRow(new PriceRow(PriceKind.Cargo, cargo.unitName, "CARGO", CommanderSupplyHeliService.GetDefaultCargoUnitCost(cargo), 0));
            }
        }

        List<UnitDefinition> units = CommanderEconomyService.PricedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            UnitDefinition definition = units[i];
            if (definition == null || !RowNames.Add(definition.unitName))
            {
                continue;
            }

            PriceKind kind = definition switch
            {
                AircraftDefinition => PriceKind.Air,
                ShipDefinition => PriceKind.Naval,
                _ => PriceKind.Ground,
            };
            string type = definition switch
            {
                VehicleDefinition vehicle => CommanderGameAccess.GetVehicleCategoryLabel(vehicle),
                AircraftDefinition => "AIRCRAFT",
                _ => "SHIP",
            };
            float defaultPrice = CommanderEconomyService.ModPrices.TryGetValue(definition.unitName, out float mod) ? mod : definition.value;
            int defaultTier = CommanderEconomyService.ModTiers.TryGetValue(definition.unitName, out int tier) ? tier : 0;
            AddRow(new PriceRow(kind, definition.unitName, type, defaultPrice, defaultTier));
        }

        IReadOnlyList<BuildingDefinition>? buildings = CommanderEconomyService.Instance?.Catalog;
        for (int i = 0; buildings != null && i < buildings.Count; i++)
        {
            BuildingDefinition definition = buildings[i];
            string name = CommanderEconomyService.GetStructureLabel(definition);
            if (RowNames.Add("building:" + name))
            {
                AddRow(new PriceRow(
                    PriceKind.Building,
                    name,
                    CommanderEconomyService.GetCategoryLabel(definition.buildingType),
                    CommanderEconomyService.GetModStructureCost(definition),
                    0));
            }
        }

        // One plant per unit a factory can make: Factory.SetFactory resolves its product in
        // Encyclopedia.Lookup, so anything outside it would never produce.
        for (int i = 0; Encyclopedia.Lookup != null && i < units.Count; i++)
        {
            UnitDefinition definition = units[i];
            if (definition != null
                && !string.IsNullOrEmpty(definition.jsonKey)
                && Encyclopedia.Lookup.TryGetValue(definition.jsonKey, out UnitDefinition listed)
                && ReferenceEquals(listed, definition)
                && !CommanderUnitBanService.IsBanned(definition)
                && RowNames.Add("building:" + CommanderEconomyService.GetFactoryLabel(definition)))
            {
                AddRow(new PriceRow(
                    PriceKind.Building,
                    CommanderEconomyService.GetFactoryLabel(definition),
                    "FACTORY",
                    CommanderEconomyService.GetModFactoryBuildCost(definition),
                    0));
            }
        }

        Rows.Sort(static (a, b) => a.Kind != b.Kind
            ? a.Kind.CompareTo(b.Kind)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddRow(PriceRow row)
    {
        bool passes = filter switch
        {
            1 => row.Kind == PriceKind.Ground,
            2 => row.Kind == PriceKind.Air,
            3 => row.Kind == PriceKind.Naval,
            4 => row.Kind == PriceKind.Cargo,
            5 => row.Kind == PriceKind.Building,
            6 => IsChanged(row),
            _ => true,
        };
        if (passes && (search.Length == 0
            || row.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || row.Type.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            Rows.Add(row);
        }
    }

    private static bool IsChanged(PriceRow row)
    {
        return row.Kind == PriceKind.Building
            ? CommanderPriceProfile.TryGetBuildingPrice(row.Name, out _)
            : CommanderPriceProfile.TryGetUnitPrice(row.Name, out _) || (row.DefaultTier > 0 && CommanderPriceProfile.TryGetTier(row.Name, out _));
    }

    private static string FormatPrice(float price)
    {
        return price.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>A comma works as the decimal point too, whatever the Windows locale.</summary>
    private static bool TryParsePrice(string text, out float value)
    {
        return float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && value >= CommanderPriceProfile.MinPrice
            && value <= CommanderPriceProfile.MaxPrice;
    }

    private readonly struct PriceRow
    {
        internal PriceRow(PriceKind kind, string name, string type, float defaultPrice, int defaultTier)
        {
            Kind = kind;
            Name = name;
            Type = type;
            DefaultPrice = defaultPrice;
            DefaultTier = defaultTier;
            DefaultLabel = CommanderEconomyService.FundsLabel(defaultPrice);
            Key = (kind == PriceKind.Building ? "b:" : "u:") + name;
        }

        internal PriceKind Kind { get; }

        internal string Name { get; }

        internal string Type { get; }

        internal float DefaultPrice { get; }

        internal int DefaultTier { get; }

        internal string DefaultLabel { get; }

        internal string Key { get; }
    }

    /// <summary>Column x positions, laid out from the right so the name takes whatever is left.</summary>
    private readonly struct PriceColumns
    {
        internal PriceColumns(float width)
        {
            ResetWidth = 58f;
            ResetX = width - ResetWidth;
            TierX = ResetX - 100f;
            PriceWidth = 86f;
            PriceX = TierX - PriceWidth - 14f;
            DefaultWidth = 76f;
            DefaultX = PriceX - DefaultWidth - 6f;
            TypeX = DefaultX - 92f;
            Name = 10f;
        }

        internal float Name { get; }

        internal float TypeX { get; }

        internal float DefaultX { get; }

        internal float DefaultWidth { get; }

        internal float PriceX { get; }

        internal float PriceWidth { get; }

        internal float TierX { get; }

        internal float ResetX { get; }

        internal float ResetWidth { get; }
    }
}
