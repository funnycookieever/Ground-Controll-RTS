using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The AIR MISSIONS window: what is already flying, and what is sitting idle waiting to be given
/// something to do. It is a window of its own on purpose - it outlives the launch panel, so
/// re-tasking a package does not mean reopening the whole thing.
/// </summary>
internal sealed partial class CommanderAirCommandUi
{
    /// <summary>The order TASK AS cycles through: the AIR window's buttons, AREA's focuses in turn.</summary>
    private static readonly CommanderAirCommandService.AirCommandMode[] TaskModes =
    {
        CommanderAirCommandService.AirCommandMode.Hybrid,
        CommanderAirCommandService.AirCommandMode.AirGuard,
        CommanderAirCommandService.AirCommandMode.Cas,
        CommanderAirCommandService.AirCommandMode.StrategicStrike,
        CommanderAirCommandService.AirCommandMode.AwacsJammer,
        CommanderAirCommandService.AirCommandMode.Autopilot,
        CommanderAirCommandService.AirCommandMode.Standby,
    };

    /// <summary>
    /// The altitude slider, in the stepper's own notches (LOW LEVEL, STANDARD, then 250 m each).
    /// Returns the change as a step delta, because that is what the networked order carries.
    /// </summary>
    internal static float AltitudeSlider(Rect rect, float altitude)
    {
        int notch = CommanderAirCommandService.AltitudeNotch(altitude);
        int picked = CommanderAirCommandService.AltitudeNotchAt(
            GUI.HorizontalSlider(rect, CommanderAirCommandService.AltitudeSliderPosition(notch), 0f, 1f));
        return (picked - notch) * CommanderAirCommandService.TargetAltitudeStepMeters;
    }

    /// <summary>The mission radius slider, whole kilometres; returns the change in km.</summary>
    private static float RadiusSlider(Rect rect, float radiusKm)
    {
        float current = Mathf.Round(radiusKm);
        return Mathf.Round(GUI.HorizontalSlider(rect, current, 5f, 150f)) - current;
    }

    private void DrawMissionWindow(int windowId)
    {
        if (GUI.Button(new Rect(missionWindowRect.width - 30f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            MissionsVisible = false;
            return;
        }

        CommanderUiTheme.DrawZoomButtons(missionWindowRect.width - 30f);
        float y = 34f;

        // The window is as tall as its list, and says so when there is nothing in it.
        if (missionAircraft.Count == 0 && idleAircraft.Count == 0)
        {
            GUI.Label(
                new Rect(12f, y + 4f, missionWindowRect.width - 24f, 44f),
                "Nothing flying. Launch a sortie from AIR.",
                CommanderUiTheme.MutedLabel);
            missionContentHeight = y + 60f;
            GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
            return;
        }

        if (idleAircraft.Count > 0)
        {
            DrawTaskAsButton(y);
            y += 38f;
        }

        float listHeight = (missionAircraft.Count * 58f) + (idleAircraft.Count * 42f);
        missionContentHeight = y + listHeight + 16f + MissionFooterHeight();

        Aircraft? selectedAircraft = null;
        for (int i = 0; i < missionAircraft.Count; i++)
        {
            if (service.IsMissionAircraftSelected(missionAircraft[i]))
            {
                selectedAircraft = missionAircraft[i];
                break;
            }
        }

        float footerHeight = selectedAircraft != null ? MissionFooterHeight() : 0f;
        Rect view = new(10f, y, missionWindowRect.width - 20f, missionWindowRect.height - y - 12f - footerHeight);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, listHeight + 4f));
        missionScroll = GUI.BeginScrollView(view, missionScroll, inner);
        bool oldEnabled = GUI.enabled;
        for (int i = 0; i < missionAircraft.Count; i++)
        {
            Aircraft aircraft = missionAircraft[i];
            float rowY = 2f + (i * 58f);
            if (GUI.Button(
                new Rect(2f, rowY, inner.width - 204f, 52f),
                service.GetMissionAircraftLabel(aircraft),
                service.IsMissionAircraftSelected(aircraft) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.ToggleMissionAircraft(aircraft);
            }

            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(
                new Rect(inner.width - 198f, rowY, 50f, 52f),
                new GUIContent("AREA", "Click a new centre for this aircraft's mission area."),
                CommanderUiTheme.Button))
            {
                service.BeginMissionAreaEdit(aircraft);
            }

            // Same name as the selection bar's button for the same order: land, refuel, rearm, go back out.
            if (GUI.Button(
                new Rect(inner.width - 144f, rowY, 72f, 52f),
                new GUIContent("RESUPPLY", "Lands at the nearest field you hold, refuels and rearms from its depots, then flies back to this mission."),
                CommanderUiTheme.Button))
            {
                service.RequestReturnToBase(aircraft);
            }

            if (GUI.Button(
                new Rect(inner.width - 68f, rowY, 66f, 52f),
                new GUIContent("RESERVE", "Lands at the nearest field you hold, where the crew ejects and the aircraft goes back into reserve, ready to launch again."),
                CommanderUiTheme.Button))
            {
                service.ReserveAircraft(aircraft);
            }

            GUI.enabled = oldEnabled;
        }

        // Idle aircraft follow the flying ones in the same list, each with its own TASK.
        float idleTop = 2f + (missionAircraft.Count * 58f);
        for (int i = 0; i < idleAircraft.Count; i++)
        {
            Aircraft aircraft = idleAircraft[i];
            float rowY = idleTop + (i * 42f);
            if (GUI.Button(
                new Rect(2f, rowY, inner.width - 70f, 36f),
                CommanderGameAccess.GetUnitLabel(aircraft),
                CommanderUiTheme.Button))
            {
                CommanderSelectionService.Instance?.SelectUnit(aircraft, false);
                CommanderCameraFollowService.Instance?.FocusSelection();
            }

            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(new Rect(inner.width - 66f, rowY, 64f, 36f), "TASK", CommanderUiTheme.PrimaryButton))
            {
                service.BeginAdoption(aircraft);
            }

            GUI.enabled = oldEnabled;
        }

        GUI.EndScrollView();

        if (selectedAircraft != null)
        {
            DrawMissionFooter(selectedAircraft, view.yMax + 6f);
        }

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, missionWindowRect.width, missionWindowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
    }

    /// <summary>The CHANGE MISSION grid wraps to as many columns as the window is wide.</summary>
    private int MissionModeColumns()
    {
        return Mathf.Clamp(Mathf.FloorToInt((missionWindowRect.width - 40f) / 100f), 2, TaskModes.Length);
    }

    private float MissionFooterHeight()
    {
        int rows = (TaskModes.Length + MissionModeColumns() - 1) / MissionModeColumns();
        return 150f + (rows * 30f);
    }

    /// <summary>
    /// The selected package's settings: what it is flying, how wide and how high. Every row is
    /// laid out off the window's width, so dragging the window wider gives the sliders the room.
    /// </summary>
    private void DrawMissionFooter(Aircraft aircraft, float footerY)
    {
        float width = missionWindowRect.width;
        GUI.Box(new Rect(10f, footerY, width - 20f, MissionFooterHeight() - 10f), string.Empty, CommanderUiTheme.Panel);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;

        float y = footerY + 6f;
        GUI.Label(new Rect(20f, y, width - 40f, 22f), "CHANGE MISSION", CommanderUiTheme.MutedLabel);
        y += 24f;

        // Keeps the area it already has; AUTOPILOT and STANDBY behave as TASK gives them.
        CommanderAirCommandService.AirCommandMode? current = service.GetMissionMode(aircraft);
        int columns = MissionModeColumns();
        float cellWidth = (width - 40f - ((columns - 1) * 4f)) / columns;
        for (int i = 0; i < TaskModes.Length; i++)
        {
            CommanderAirCommandService.AirCommandMode mode = TaskModes[i];
            Rect cell = new(20f + ((i % columns) * (cellWidth + 4f)), y + ((i / columns) * 30f), cellWidth, 26f);
            if (GUI.Button(
                cell,
                CommanderAirCommandService.GetModeShortLabel(mode),
                mode == current ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button)
                && mode != current)
            {
                service.ChangeMissionMode(aircraft, mode);
            }
        }

        y += ((TaskModes.Length + columns - 1) / columns * 30f) + 6f;

        float sliderWidth = Mathf.Max(60f, width - 126f - 100f);
        float valueX = 126f + sliderWidth + 6f;
        GUI.Label(new Rect(20f, y, 104f, 24f), "AREA RADIUS", CommanderUiTheme.MutedLabel);
        float radiusKm = service.GetMissionRadiusKm(aircraft);
        float radiusDelta = RadiusSlider(new Rect(126f, y + 7f, sliderWidth, 12f), radiusKm);
        if (radiusDelta != 0f)
        {
            service.StepMissionRadius(aircraft, radiusDelta);
        }

        CommanderUiTheme.FitLabel(new Rect(valueX, y + 1f, 74f, 24f), $"{radiusKm:0} km", CommanderUiTheme.Header);
        y += 34f;

        // Altitude is changed on a package that is already up, not only at launch: "the AI
        // flies too low" is something a player notices while watching it, not while buying it.
        GUI.Label(new Rect(20f, y, 104f, 24f), "ALTITUDE", CommanderUiTheme.MutedLabel);
        float missionAltitude = service.GetMissionAltitude(aircraft);
        float altitudeDelta = AltitudeSlider(new Rect(126f, y + 7f, sliderWidth, 12f), missionAltitude);
        if (altitudeDelta != 0f)
        {
            service.StepMissionAltitude(aircraft, altitudeDelta);
        }

        // 74 px is narrower than STANDARD at the header size, which is how the D ended up on a
        // second line with both lines cut in half.
        CommanderUiTheme.FitLabel(
            new Rect(valueX, y + 1f, 74f, 24f),
            CommanderAirCommandService.FormatTargetAltitude(missionAltitude),
            CommanderUiTheme.Header);
        y += 36f;

        if (GUI.Button(new Rect(20f, y, width - 40f, 32f), "MOVE AREA CENTER", CommanderUiTheme.PrimaryButton))
        {
            service.BeginMissionAreaEdit(aircraft);
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>
    /// What TASK gives an idle aircraft: an area mode asks for a circle, AUTOPILOT releases it to the
    /// game's AI, and STANDBY sends it to its nearest field to sit down. The picker is the same
    /// choice as AIR COMMAND's mission row, shown here because that window may well be closed.
    /// </summary>
    private void DrawTaskAsButton(float y)
    {
        CommanderAirCommandService.AirCommandMode mode = service.SelectedMode;
        int current = System.Array.IndexOf(TaskModes, mode);
        const string taskTooltip = "Click to change what TASK gives an aircraft. AREA and RADAR then ask you to place a circle; "
            + "AUTOPILOT hands it to the game's AI; STANDBY lands it at its nearest field.";
        if (GUI.Button(
            new Rect(10f, y, missionWindowRect.width - 20f, 32f),
            new GUIContent($"TASK AS:  {CommanderAirCommandService.GetModeShortLabel(mode)}   >", taskTooltip),
            CommanderUiTheme.Button))
        {
            service.SelectMode(TaskModes[(current + 1) % TaskModes.Length]);
        }
    }
}
