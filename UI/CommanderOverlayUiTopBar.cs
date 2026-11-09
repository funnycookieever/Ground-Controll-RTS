using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The command bar docked at the top of the screen: the treasury on the left, one tab per window
/// with its hotkey, settings on the right (game speed is in the SETTINGS title bar). It replaces a
/// floating CMD button that opened a panel of buttons that opened windows: every window is now one
/// click, or one key, away, and the balance being spent is always in view.
/// </summary>
internal sealed partial class CommanderOverlayUi
{
    private const float TopBarHeight = 40f;
    private const float TopBarTabWidth = 92f;
    private const float TopBarFundsWidth = 244f;
    private const float TopBarSpeedWidth = 40f;

    private static readonly AccessTools.FieldRef<MessageUI, TextMeshProUGUI> KillFeedText =
        AccessTools.FieldRefAccess<MessageUI, TextMeshProUGUI>("killFeedText");

    private static readonly Vector3[] KillFeedCorners = new Vector3[4];
    private static GUIStyle? killFeedStyle;

    /// <summary>The income figure on one line: wrapped, "-$2.75m/min" broke before its last letter.</summary>
    private static GUIStyle? incomeStyle;

    private Rect topBarRect;
    private Rect topBarHintRect;
    private string topBarIncome = string.Empty;
    private float nextTopBarIncomeAt;
    private string topBarReserve = string.Empty;
    private float nextTopBarReserveAt;

    /// <summary>
    /// The game does not network its clock, so speed is single-player only: never a guest's, and
    /// not the host's once anyone has joined (see <see cref="CommanderNetwork.HostHasGuests"/>).
    /// </summary>
    private static bool CanSetSpeed => !CommanderNetwork.IsGuest && !CommanderNetwork.HostHasGuests;

    /// <summary>The placement or map instruction under the bar, empty when nothing is armed.</summary>
    private string PlacementHint => economyService.AwaitingPlacement
        ? economyService.PlacementStatus
        : supplyHeliService.AwaitingTargetSelection
            ? "Click the cargo destination in the world or on the map. Right-click cancels."
        : airCommandService.AwaitingAreaSelection
            ? "Click the mission area on the tactical map or in the world. Right-click cancels."
        : spawnService.AwaitingRallyPointSelection
            ? "Click where the depot's new units should gather, in the world or on the map. Right-click cancels."
        : CommanderAreaFire.AwaitingTarget
            ? "Click the ground or the map to place the fire area. Right-click cancels."
            : string.Empty;

    private static string KeyName(KeyCode key)
    {
        return key >= KeyCode.A && key <= KeyCode.Z ? key.ToString() : string.Empty;
    }

    /// <summary>One window tab: its name, with the hotkey small and to the right; lit while open.</summary>
    private static bool DrawTopBarTab(ref float x, float y, float height, string label, KeyCode key, string tooltip, bool open)
    {
        Rect rect = new(x, y, TopBarTabWidth - 4f, height);
        x += TopBarTabWidth;
        bool clicked = GUI.Button(rect, new GUIContent(label, tooltip), open ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button);
        if (key != KeyCode.None)
        {
            Color previous = GUI.contentColor;
            GUI.contentColor = new Color(1f, 1f, 1f, GUI.enabled ? 0.6f : 0.25f);
            GUI.Label(new Rect(rect.xMax - 15f, rect.y, 12f, rect.height), KeyName(key), CommanderUiTheme.Caption);
            GUI.contentColor = previous;
        }

        return clicked;
    }

    /// <summary>
    /// Game speed, 1x / 2x / 4x, from <paramref name="x"/> rightward. Host only.
    /// </summary>
    private static void DrawSpeedButtons(float x, float y, float height)
    {
        bool host = CanSetSpeed;
        float live = CommanderNetwork.IsGuest ? CommanderNetwork.HostTimeScale : Time.timeScale;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && host;
        for (int i = 0; i < TimeScaleSteps.Length; i++)
        {
            float scale = TimeScaleSteps[i];
            if (GUI.Button(
                new Rect(x, y, TopBarSpeedWidth, height),
                new GUIContent($"{scale:0}x", host ? "Game speed." : "Game speed: 1x in multiplayer."),
                Mathf.Approximately(live, scale) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                TimeScaleManager.Scale = scale;
            }

            x += TopBarSpeedWidth + 2f;
        }

        GUI.enabled = oldEnabled;
    }

    private void LayoutTopBar()
    {
        float width = TopBarFundsWidth + 12f + (TopBarTabWidth * 7f) + 12f + 96f + 32f + 20f;
        width = Mathf.Min(width, CommanderUiScale.Width - 24f);
        topBarRect = new Rect(Mathf.Round((CommanderUiScale.Width - width) * 0.5f), 0f, width, TopBarHeight);
    }

    private void DrawTopBar()
    {
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        GUI.Box(topBarRect, string.Empty, CommanderUiTheme.Panel);

        // The accent line under the bar, the same line that tops every window.
        CommanderUiTheme.DrawAccentLine(new Rect(topBarRect.x, topBarRect.yMax - 2f, topBarRect.width, 2f));

        float x = topBarRect.x + 12f;
        float y = topBarRect.y + 5f;
        float height = TopBarHeight - 12f;
        bool draft = advanced && CommanderDraftService.IsDraftMission;
        if (draft && showFactionMoney)
        {
            // A draft has no money: the bar counts the drafted reserve instead.
            GUI.Label(new Rect(x, topBarRect.y, 70f, TopBarHeight - 2f), CommanderDraftService.IsDrafting ? "POINTS" : "RESERVE", CommanderUiTheme.Caption);
            GUI.Label(new Rect(x + 70f, topBarRect.y, TopBarFundsWidth - 70f, TopBarHeight - 2f), GetDraftReserveLabel(), CommanderUiTheme.Figure);
            x += TopBarFundsWidth;
            DrawTopBarDivider(x);
            x += 12f;
        }
        else if (showFactionMoney)
        {
            GUI.Label(new Rect(x, topBarRect.y, 60f, TopBarHeight - 2f), "FUNDS", CommanderUiTheme.Caption);
            GUI.Label(new Rect(x + 50f, topBarRect.y, 100f, TopBarHeight - 2f), CommanderSpawnService.GetFactionFundsLabel(), CommanderUiTheme.Figure);
            string income = GetIncomeLabel();
            incomeStyle ??= new GUIStyle(CommanderUiTheme.MutedLabel) { wordWrap = false, clipping = TextClipping.Clip };
            GUI.Label(new Rect(x + 146f, topBarRect.y, TopBarFundsWidth - 146f, TopBarHeight - 2f), income, incomeStyle);
            DrawIncomeTick(new Rect(x + 146f, topBarRect.yMax - 8f, Mathf.Min(incomeStyle.CalcSize(new GUIContent(income)).x, TopBarFundsWidth - 146f), 2f));
            x += TopBarFundsWidth;
            DrawTopBarDivider(x);
            x += 12f;
        }

        bool oldEnabled = GUI.enabled;
        if (showCommandButton)
        {
            GUI.enabled = oldEnabled && advanced;
            if (draft)
            {
                DrawDraftTabs(ref x, y, height);
            }
            else if (DrawTopBarTab(ref x, y, height, "BUILD", CommanderSettings.OpenBuild.MainKey, "Income buildings, factories, defences and every other structure, plus repairs.", economyUi.Visible))
            {
                ToggleBuild();
            }

            if (!draft && DrawTopBarTab(ref x, y, height, "GROUND", CommanderSettings.OpenGroundUnits.MainKey, "Selects your nearest vehicle depot and opens its purchase list.", depotUi.Visible))
            {
                if (depotUi.Visible)
                {
                    selectionService.DeselectAll();
                    depotUi.Reset();
                }
                else
                {
                    spawnService.SelectNearestDepot();
                }
            }

            GUI.enabled = oldEnabled && advanced && !draft;
            if (DrawTopBarTab(ref x, y, height, "NAVAL", CommanderSettings.OpenNaval.MainKey, "Ships. Needs a naval dock, built from BUILD on the shore.", navalPurchaseUi.Visible))
            {
                navalPurchaseUi.Toggle();
            }

            GUI.enabled = oldEnabled && advanced;
            if (DrawTopBarTab(ref x, y, height, "AIR", CommanderSettings.OpenAirCommand.MainKey, "Buy, arm and launch aircraft. TRANSPORT in there plans cargo runs.", airCommandUi.Visible))
            {
                ToggleAirCommand();
            }

            if (DrawTopBarTab(ref x, y, height, "MISSIONS", KeyCode.None, "What is flying, and aircraft sitting idle waiting for a task.", airCommandUi.MissionsVisible))
            {
                airCommandUi.ToggleMissions();
            }

            GUI.enabled = oldEnabled;
            if (DrawTopBarTab(ref x, y, height, "FORCES", CommanderSettings.OpenOrderOfBattle.MainKey, "Every unit you own, tracked hostiles, control groups and the battle log.", unitListUi.Visible))
            {
                ToggleOrderOfBattle();
            }

            GUI.enabled = oldEnabled && advanced;
            if (DrawTopBarTab(ref x, y, height, "SUPPLY", KeyCode.None, "Fuel and ammunition held around each airbase you own.", baseSupplyWindowVisible))
            {
                baseSupplyWindowVisible = !baseSupplyWindowVisible;
            }

            GUI.enabled = oldEnabled;
            DrawTopBarDivider(x + 4f);
            x += 14f;
        }

        if (GUI.Button(new Rect(x, y, 96f, height), "SETTINGS", settingsVisible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            settingsVisible = !settingsVisible;
            bindingCapture = null;
        }

        x += 100f;

        // In core mode the old COMMANDER panel is where ALL FEATURES is unlocked, so "?" opens it.
        if (GUI.Button(
            new Rect(x, y, 28f, height),
            new GUIContent("?", advanced ? "How to command." : "How to command, and ALL FEATURES for this mission."),
            panelVisible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            panelVisible = !panelVisible;
        }

        string hint = PlacementHint;
        topBarHintRect = Rect.zero;
        if (hint.Length > 0)
        {
            float hintWidth = Mathf.Min(640f, CommanderUiTheme.Label.CalcSize(new GUIContent(hint)).x + 40f);
            topBarHintRect = new Rect(Mathf.Round((CommanderUiScale.Width - hintWidth) * 0.5f), topBarRect.yMax + 8f, hintWidth, 30f);
            GUI.Box(topBarHintRect, string.Empty, CommanderUiTheme.Toast);
            GUI.Label(new Rect(topBarHintRect.x + 14f, topBarHintRect.y, topBarHintRect.width - 28f, topBarHintRect.height), hint, CommanderUiTheme.Label);
        }

        DrawKillFeedOverBar();
        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, CommanderUiScale.Width, CommanderUiScale.Height));
    }

    /// <summary>
    /// The game's kill feed is a uGUI text, and IMGUI always draws above every canvas, so the bar hid
    /// the lines that ran under it. The same text is drawn again here, clipped to the bar, so it sits
    /// on top of the bar and nowhere else: outside the bar the game's own text is still what shows.
    /// </summary>
    private void DrawKillFeedOverBar()
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        MessageUI? messages = SceneSingleton<MessageUI>.i;
        TextMeshProUGUI? feed = messages != null ? KillFeedText(messages) : null;
        if (feed == null || !feed.isActiveAndEnabled || string.IsNullOrEmpty(feed.text)
            || feed.canvas == null || feed.canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            return;
        }

        // An overlay canvas's world space is screen pixels.
        feed.rectTransform.GetWorldCorners(KillFeedCorners);
        Vector2 topLeft = CommanderUiScale.ScreenToGui(KillFeedCorners[1]);
        Vector2 bottomRight = CommanderUiScale.ScreenToGui(KillFeedCorners[3]);
        Rect rect = Rect.MinMaxRect(topLeft.x, topLeft.y, bottomRight.x, bottomRight.y);
        if (!rect.Overlaps(topBarRect))
        {
            return;
        }

        killFeedStyle ??= new GUIStyle(CommanderUiTheme.Label) { richText = true, wordWrap = false, clipping = TextClipping.Overflow };
        killFeedStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(feed.fontSize * feed.canvas.scaleFactor / CommanderUiScale.Scale));
        killFeedStyle.normal.textColor = feed.color;
        HorizontalAlignmentOptions horizontal = feed.horizontalAlignment;
        VerticalAlignmentOptions vertical = feed.verticalAlignment;
        int column = horizontal == HorizontalAlignmentOptions.Right ? 2 : horizontal == HorizontalAlignmentOptions.Center ? 1 : 0;
        int row = vertical == VerticalAlignmentOptions.Bottom ? 2 : vertical == VerticalAlignmentOptions.Middle ? 1 : 0;
        killFeedStyle.alignment = (TextAnchor)((row * 3) + column);

        GUI.BeginGroup(topBarRect);
        GUI.Label(new Rect(rect.x - topBarRect.x, rect.y - topBarRect.y, rect.width, rect.height), feed.text, killFeedStyle);
        GUI.EndGroup();
    }

    /// <summary>
    /// A draft's BUILD and GROUND tabs: nothing is built or bought, so they open the draft and the
    /// drafted reserve instead. Each is live only in its own phase.
    /// </summary>
    private void DrawDraftTabs(ref float x, float y, float height)
    {
        bool drafting = CommanderDraftService.IsDrafting;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && drafting;
        if (DrawTopBarTab(ref x, y, height, "DRAFT", KeyCode.None, drafting ? "Spend the points on your army, air wing and supply before the battle starts." : "Locked in: the battle has started.", draftUi.Visible))
        {
            if (draftUi.Visible)
            {
                draftUi.Hide();
            }
            else
            {
                draftUi.Show();
            }
        }

        GUI.enabled = oldEnabled && !drafting;
        if (DrawTopBarTab(ref x, y, height, "RESERVE", CommanderSettings.OpenGroundUnits.MainKey, "Selects your nearest depot and lists what is left of your drafted ground units.", depotUi.Visible))
        {
            if (depotUi.Visible)
            {
                selectionService.DeselectAll();
                depotUi.Reset();
            }
            else
            {
                spawnService.SelectNearestDepot();
            }
        }

        GUI.enabled = oldEnabled;
    }

    private void DrawTopBarDivider(float x)
    {
        Color previous = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.12f);
        GUI.DrawTexture(new Rect(x, topBarRect.y + 8f, 1f, TopBarHeight - 18f), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private string GetDraftReserveLabel()
    {
        if (CommanderScheduler.IsDueRealtime(ref nextTopBarReserveAt, 1f))
        {
            FactionHQ? hq = CommanderGameAccess.GetLocalHq();
            int ground = 0;
            int air = 0;
            if (hq != null)
            {
                foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
                {
                    ground += Mathf.Max(0, entry.Value.Count);
                }

                foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> entry in hq.AircraftSupply)
                {
                    air += Mathf.Max(0, entry.Value.Count);
                }
            }

            CommanderDraftService? service = CommanderDraftService.Instance;
            topBarReserve = CommanderDraftService.IsDrafting && service != null
                ? $"{CommanderDraftUi.Points(service.GetSpent())} / {CommanderDraftUi.Points(service.Budget)}"
                : $"{ground} GND  {air} AIR";
        }

        return topBarReserve;
    }

    /// <summary>A hairline under the income figure that fills toward the next payout, then flashes
    /// as the money lands. Nothing is drawn where this machine does not run the income clock.</summary>
    private void DrawIncomeTick(Rect rect)
    {
        float progress = economyService.IncomeTickProgress;
        if (progress < 0f || topBarIncome == "no income")
        {
            return;
        }

        Color previous = GUI.color;
        Color accent = CommanderUiTheme.Accent;
        GUI.color = new Color(accent.r, accent.g, accent.b, 0.18f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);

        // The first half second after a payout the whole line flashes bright and fades.
        float flash = progress < 0.035f ? 1f - (progress / 0.035f) : 0f;
        GUI.color = new Color(accent.r, accent.g, accent.b, 0.75f);
        GUI.DrawTexture(new Rect(rect.x, rect.y, Mathf.Round(rect.width * progress), rect.height), Texture2D.whiteTexture);
        if (flash > 0f)
        {
            GUI.color = new Color(accent.r, accent.g, accent.b, flash);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }

        GUI.color = previous;
    }

    private string GetIncomeLabel()
    {
        if (CommanderScheduler.IsDueRealtime(ref nextTopBarIncomeAt, 1f))
        {
            // Net of defence upkeep: the gross figure promised money the guns were already spending.
            float net = economyService.FriendlyNetIncomePerMinute;
            topBarIncome = net == 0f && economyService.FriendlyGrossIncomePerMinute == 0f
                ? "no income"
                : $"{CommanderEconomyService.SignedFundsLabel(net)}/min";
        }

        return topBarIncome;
    }
}
