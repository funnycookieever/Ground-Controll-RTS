using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Movable toast window for combat alerts, parked in the top-right corner so it never covers
/// the faction funds readout. Drawn whether or not RTS mode is active; clicking a toast
/// selects the unit and jumps the RTS camera to it.
/// </summary>
internal sealed class CommanderAlertUi
{
    private const int WindowId = 0x434F4D41;
    private const float WindowWidth = 380f;
    private const float TitleHeight = 28f;
    private const float ToastHeight = 30f;

    /// <summary>Row height per alert, measured in <see cref="Draw"/> and drawn in <see cref="DrawWindow"/>.</summary>
    private readonly List<float> toastHeights = new();

    private readonly CommanderAlertService alertService;
    private readonly CommanderSelectionService selectionService;
    private readonly System.Func<bool> commanderActive;
    private Rect windowRect;
    private bool positionInitialized;
    private CommanderAlertService.Alert? pendingDismiss;

    internal CommanderAlertUi(
        CommanderAlertService alertService,
        CommanderSelectionService selectionService,
        System.Func<bool> commanderActive)
    {
        this.alertService = alertService;
        this.selectionService = selectionService;
        this.commanderActive = commanderActive;
        Instance = this;
    }

    internal static CommanderAlertUi? Instance { get; private set; }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return CommanderSettings.AlertWindow
            && alertService.Alerts.Count > 0
            && CommanderWindowScale.Visual(WindowId, windowRect).Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    internal void Draw()
    {
        int count = alertService.Alerts.Count;
        if (count == 0 || !CommanderSettings.AlertWindow)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        Vector2 min = new(260f, 80f);
        float width = CommanderWindowScale.Size(WindowId, new Vector2(Mathf.Min(WindowWidth, CommanderUiScale.Width - 24f), 0f), min).x;
        float height = CommanderWindowScale.Size(WindowId, new Vector2(width, TitleHeight + 6f + MeasureToasts(width)), min).y;
        if (!positionInitialized)
        {
            // Centred under the command bar: the tactical map owns the top-right corner.
            windowRect = new Rect(Mathf.Round((CommanderUiScale.Width - width) * 0.5f), 88f, width, height);
            positionInitialized = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
        }

        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "ALERTS");
    }

    private static Color GetColor(CommanderAlertService.LogKind kind)
    {
        return kind switch
        {
            CommanderAlertService.LogKind.Kill => new Color(0.55f, 1f, 0.6f, 1f),
            CommanderAlertService.LogKind.Arrival => new Color(0.6f, 0.85f, 1f, 1f),
            _ => new Color(1f, 0.65f, 0.55f, 1f),
        };
    }

    /// <summary>
    /// A toast is as tall as its own text. A supply or capture message is a sentence, and at a
    /// flat 30 px row the sentence wrapped and lost half of both its lines.
    /// </summary>
    private float MeasureToasts(float width)
    {
        IReadOnlyList<CommanderAlertService.Alert> alerts = alertService.Alerts;
        toastHeights.Clear();
        float total = 0f;
        for (int i = 0; i < alerts.Count; i++)
        {
            float height = CommanderUiTheme.RowHeight(CommanderUiTheme.Toast, width - 12f, alerts[i].Text, ToastHeight);
            toastHeights.Add(height);
            total += height + 4f;
        }

        return total;
    }

    private void DrawWindow(int windowId)
    {
        IReadOnlyList<CommanderAlertService.Alert> alerts = alertService.Alerts;
        bool active = commanderActive();
        float y = TitleHeight + 2f;
        for (int i = alerts.Count - 1; i >= 0; i--)
        {
            CommanderAlertService.Alert alert = alerts[i];
            float toastHeight = i < toastHeights.Count ? toastHeights[i] : ToastHeight;
            Rect rect = new(6f, y, windowRect.width - 12f, toastHeight);
            y += toastHeight + 4f;

            Color previous = GUI.color;
            GUI.color = GetColor(alert.Kind);
            bool clicked = GUI.Button(rect, alert.Text, CommanderUiTheme.Toast);
            GUI.color = previous;
            if (!clicked)
            {
                continue;
            }

            if (active && alert.Unit != null && !alert.Unit.disabled)
            {
                selectionService.SelectUnit(alert.Unit, false);
                CommanderCameraFollowService.Instance?.FocusSelection();
            }

            pendingDismiss = alert;
            break;
        }

        // Hides the window, not the alerts: they keep collecting, and the settings toggle brings it back.
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            CommanderSettings.AlertWindow = false;
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width, TitleHeight));

        if (pendingDismiss != null)
        {
            alertService.DismissAlert(pendingDismiss);
            pendingDismiss = null;
        }
    }
}
