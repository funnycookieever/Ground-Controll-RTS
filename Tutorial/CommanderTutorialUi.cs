using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The tutorial's step card, on the left under the command bar. Drawn in command mode and in the
/// cockpit alike (the cockpit steps need it), and registered in <c>ContainsScreenPoint</c> through
/// <see cref="Instance"/> like the alerts window.
/// </summary>
internal sealed class CommanderTutorialUi
{
    private const int WindowId = 0x54555452;
    private const float WindowWidth = 400f;
    private const float TitleHeight = 28f;
    private const float Pad = 12f;

    private static readonly Color DoneColor = new(0.45f, 1f, 0.55f, 1f);

    private readonly System.Func<bool> commanderActive;
    private readonly GUI.WindowFunction drawWindow;
    private float bodyHeight;
    private Rect windowRect = new(12f, 110f, WindowWidth, 200f);
    private Rect reopenRect;

    internal CommanderTutorialUi(System.Func<bool> commanderActive)
    {
        this.commanderActive = commanderActive;
        drawWindow = DrawWindow;
        Instance = this;
    }

    internal static CommanderTutorialUi? Instance { get; private set; }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        CommanderTutorialService? tutorial = CommanderTutorialService.Instance;
        if (tutorial == null || !CommanderTutorialService.IsTutorialMission)
        {
            return false;
        }

        Vector2 gui = CommanderUiScale.ScreenToGui(screenPoint);
        return tutorial.Active
            ? CommanderWindowScale.Visual(WindowId, windowRect).Contains(gui)
            : reopenRect.Contains(gui);
    }

    internal void Draw()
    {
        CommanderTutorialService? tutorial = CommanderTutorialService.Instance;
        if (tutorial == null || !CommanderTutorialService.IsTutorialMission)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        if (!tutorial.Active)
        {
            DrawReopen(tutorial);
            return;
        }

        Vector2 min = new(300f, 150f);
        float width = CommanderWindowScale.Size(WindowId, new Vector2(WindowWidth, 0f), min).x;
        bodyHeight = CommanderUiTheme.RowHeight(CommanderUiTheme.Label, width - (Pad * 2f), tutorial.Body, 40f);
        windowRect.size = CommanderWindowScale.Size(WindowId, new Vector2(width, TitleHeight + 8f + 26f + bodyHeight + 10f + 32f + Pad), min);
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        string title = tutorial.Finished
            ? "TUTORIAL"
            : $"TUTORIAL  {tutorial.StepIndex + 1} / {tutorial.StepCount}";
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, drawWindow, title);

        // Every window a step asks for can open over this corner, and a newer IMGUI window draws on
        // top. The card is what the player is following.
        GUI.BringWindowToFront(WindowId);
    }

    /// <summary>A small tab to bring a hidden card back, only where the mouse is free to click it.</summary>
    private void DrawReopen(CommanderTutorialService tutorial)
    {
        reopenRect = Rect.zero;
        if (!commanderActive() || tutorial.Finished)
        {
            return;
        }

        reopenRect = new Rect(12f, 110f, 120f, 30f);
        if (GUI.Button(reopenRect, "TUTORIAL", CommanderUiTheme.Button))
        {
            tutorial.Dismissed = false;
        }
    }

    private void DrawWindow(int windowId)
    {
        CommanderTutorialService? tutorial = CommanderTutorialService.Instance;
        if (tutorial == null)
        {
            return;
        }

        float width = windowRect.width - (Pad * 2f);
        float y = TitleHeight + 8f;
        Color previous = GUI.color;
        if (tutorial.ShowingDone)
        {
            GUI.color = DoneColor;
        }

        GUI.Label(new Rect(Pad, y, width, 24f), tutorial.ShowingDone ? $"{tutorial.Title}   DONE" : tutorial.Title, CommanderUiTheme.Header);
        GUI.color = previous;
        y += 26f;
        GUI.Label(new Rect(Pad, y, width, bodyHeight), tutorial.Body, CommanderUiTheme.Label);
        y += bodyHeight + 10f;

        if (tutorial.Finished)
        {
            if (GUI.Button(new Rect(Pad, y, width, 30f), "CLOSE", CommanderUiTheme.PrimaryButton))
            {
                tutorial.Dismissed = true;
            }
        }
        else
        {
            GUI.Label(new Rect(Pad, y, width - 120f, 30f), "Do it and the next step follows by itself.", CommanderUiTheme.MutedLabel);
            if (GUI.Button(new Rect(windowRect.width - Pad - 110f, y, 110f, 30f), "SKIP STEP", CommanderUiTheme.Button))
            {
                tutorial.Skip();
            }
        }

        // Hides the card, not the tutorial: steps still pass, and the TUTORIAL tab brings it back.
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            tutorial.Dismissed = true;
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 40f, TitleHeight));
    }
}
