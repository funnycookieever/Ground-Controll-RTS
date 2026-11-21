using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The one refusal banner: red, centred and large, so a refused spawn or repair never looks like a
/// dead button.
/// </summary>
internal static class CommanderNotice
{
    private const float DurationSeconds = 6f;

    private static string text = string.Empty;
    private static float until;

    /// <summary>Something the player asked for did not happen, and this is why.</summary>
    internal static void Fail(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // A refusal the host hits while carrying out a guest's click belongs on the guest's screen.
        if (CommanderNetwork.IsHandlingGuest)
        {
            CommanderNetwork.Reply("notice", message);
            return;
        }

        text = message.ToUpperInvariant();
        until = Time.unscaledTime + DurationSeconds;
    }

    internal static void Clear()
    {
        text = string.Empty;
        until = 0f;
    }

    internal static void Draw()
    {
        if (text.Length == 0 || Time.unscaledTime > until)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        string message = "! " + text;
        float width = Mathf.Min(760f, CommanderUiScale.Width - 40f);

        // The plate grows to however many lines the message takes.
        float height = CommanderUiTheme.RowHeight(CommanderUiTheme.Alert, width, message, 54f);

        // Below the funds box, which sits at the top centre, and below the Leviathan's health bar.
        Rect rect = new((CommanderUiScale.Width - width) * 0.5f, Mathf.Max(96f, CommanderLeviathanUi.BottomY + 8f), width, height);
        GUI.Box(rect, message, CommanderUiTheme.Alert);
    }
}
