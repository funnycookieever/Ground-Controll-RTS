using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderTacticalMapPatches
{
    /// <summary>
    /// While RTS mode is active the mod owns map input: the left button pans the map and clicks
    /// icons, the box-select modifier turns a left drag into a selection box, the middle button
    /// also pans, and the Basegame zoom and keyboard-pan bindings keep working.
    /// </summary>
    [HarmonyPatch(typeof(DynamicMap), "MapControls")]
    internal static class CommanderTacticalMapControlsPatch
    {
        private const float ClickSlopPixels = 6f;

        private static readonly AccessTools.FieldRef<DynamicMap, bool> FollowingCamera =
            AccessTools.FieldRefAccess<DynamicMap, bool>("followingCamera");

        private static readonly AccessTools.FieldRef<DynamicMap, Vector2> PositionOffset =
            AccessTools.FieldRefAccess<DynamicMap, Vector2>("positionOffset");

        private static readonly AccessTools.FieldRef<DynamicMap, Vector2> StationaryOffset =
            AccessTools.FieldRefAccess<DynamicMap, Vector2>("stationaryOffset");

        private static readonly AccessTools.FieldRef<DynamicMap, float> MapMoveMaxJumpSpeed =
            AccessTools.FieldRefAccess<DynamicMap, float>("mapMoveMaxJumpSpeed");

        private static bool leftButtonHeld;
        private static bool pressDuringPlacement;
        private static Vector2 leftButtonDownPosition;
        private static bool leftButtonMoved;
        private static Vector2 lastMousePosition;
        private static bool dragTracking;
        private static bool pressBeganOffMap;

        /// <summary>Basegame icon picking, reimplemented because the original lives inside MapControls.</summary>
        internal static MapIcon? FindIconUnderCursor(DynamicMap map)
        {
            Vector3 mousePosition = Input.mousePosition;
            float bestDistance = float.PositiveInfinity;
            MapIcon? best = null;
            System.Collections.Generic.List<MapIcon> icons = map.mapIcons;
            for (int i = 0; i < icons.Count; i++)
            {
                MapIcon icon = icons[i];
                if (icon == null
                    || !icon.gameObject.activeInHierarchy
                    || icon.iconImage == null
                    || !icon.iconImage.raycastTarget)
                {
                    continue;
                }

                if (icon is UnitMapIcon unitIcon
                    && (!CommanderGameAccess.IsKnownToFaction(unitIcon.unit, CommanderGameAccess.GetLocalHq(), includeLostContacts: true)
                        || SceneSingleton<TargetListSelector>.i?.CheckExclusions(unitIcon.unit) == true))
                {
                    continue;
                }

                float distance = CommanderTacticalMapService.IconPickDistanceSq(icon, mousePosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = icon;
                }
            }

            return best;
        }

        internal static bool Prefix(DynamicMap __instance)
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            Vector2 mouse = Input.mousePosition;
            bool overChrome = CommanderOverlayUi.Instance?.ContainsScreenPoint(mouse) == true
                || CommanderTacticalMapService.Instance?.OwnsPointer(mouse) == true;

            // A press belongs to whatever it began on until it is released, so dragging off a window onto the
            // map does not pan or select.
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
            {
                pressBeganOffMap = overChrome || !__instance.IsCursorInMapRectangle();
            }

            bool held = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)
                || Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2);
            pressBeganOffMap &= held;

            if (!overChrome && !pressBeganOffMap && __instance.IsCursorInMapRectangle())
            {
                CommanderMapControls(__instance);
            }
            else
            {
                // Dragging off the map and back must not arrive as one huge jump.
                dragTracking = false;
                leftButtonHeld = false;
            }

            UpdateCameraTracking(__instance);
            return false;
        }

        private static void CommanderMapControls(DynamicMap map)
        {
            // The wheel is read raw first, like the RTS camera's own zoom. "Zoom View" is a Rewired
            // axis, and the game switches every Rewired mouse map off while its chat box is open and
            // back on after a 100 ms scaled-time delay (ChatBox), so a chat closed while paused
            // leaves it at zero: the map stops zooming while panning, which is raw input, still works.
            // The binding stays as the fallback for controllers.
            float wheel = Mathf.Clamp(Input.mouseScrollDelta.y, -3f, 3f);
            float zoomAxis = (wheel != 0f ? wheel : CommanderGameInput.GetAxis("Zoom View")) * 0.05f * CommanderSettings.MapZoomSpeed;
            if (zoomAxis != 0f)
            {
                map.SetZoomLevel(Mathf.Clamp(map.mapScaleCenter.transform.localScale.x * (zoomAxis + 1f), 1f, 40f));
            }

            // Screen pixels per map unit, which is what the Basegame uses for its own cursor maths.
            // The old code divided only by the zoom level and so ignored the fact that the compact
            // Tactical Map is a scaled-down copy of the fullscreen one - which is exactly why
            // dragging the modded map crawled while the fullscreen map felt normal.
            float pixelsPerMapUnit = Mathf.Max(map.mapImage.transform.lossyScale.x, 0.0001f);
            float keyboardHorizontal = CommanderGameInput.GetAxis("Move Map Horizontal");
            float keyboardVertical = CommanderGameInput.GetAxis("Move Map Vertical");
            if (keyboardHorizontal != 0f || keyboardVertical != 0f)
            {
                float speed = 600f * Time.unscaledDeltaTime / pixelsPerMapUnit;
                PositionOffset(map) += new Vector2(keyboardHorizontal * speed, keyboardVertical * speed);
            }

            // A plain left drag pans, exactly like the Basegame map. The middle button keeps
            // panning too, and a left drag with the box-select modifier belongs to the box.
            bool boxDragging = CommanderBoxSelectService.Instance?.DraggingOnMap == true;
            Vector2 mousePosition = Input.mousePosition;
            if (Input.GetMouseButton(2) || (Input.GetMouseButton(0) && !boxDragging))
            {
                // Real cursor pixels, not Unity's smoothed mouse axis, so the map sticks to the
                // cursor at a speed of 1 no matter the zoom, the window size or the framerate.
                if (dragTracking)
                {
                    PositionOffset(map) -= (mousePosition - lastMousePosition)
                        * (CommanderSettings.MapDragSpeed / pixelsPerMapUnit);
                }

                lastMousePosition = mousePosition;
                dragTracking = true;
            }
            else
            {
                dragTracking = false;
            }

            FollowingCamera(map) = PositionOffset(map) == Vector2.zero;

            if (CommanderGameInput.JumpMapDown && map.TryGetCursorCoordinates(out GlobalPosition jumpTarget))
            {
                CommanderTacticalMapService.Instance?.JumpCameraToPosition(jumpTarget);
            }

            TrackIconClick(map);
        }

        /// <summary>
        /// Icon selection resolves on release, and only when the cursor barely moved: the same
        /// button now pans the map, so clicking on press would select an icon every time the
        /// player grabbed the map to drag it.
        /// </summary>
        private static void TrackIconClick(DynamicMap map)
        {
            if (Input.GetMouseButtonDown(0))
            {
                leftButtonHeld = true;
                leftButtonMoved = false;
                leftButtonDownPosition = Input.mousePosition;

                // Latched on the press: the click that dropped a mission area on the map would
                // otherwise also select what was under it or move the camera there on release.
                pressDuringPlacement = CommanderInputController.PlacementActive;
            }

            if (!leftButtonHeld)
            {
                return;
            }

            if (Vector2.Distance(leftButtonDownPosition, Input.mousePosition) > ClickSlopPixels)
            {
                leftButtonMoved = true;
            }

            if (!Input.GetMouseButtonUp(0))
            {
                return;
            }

            leftButtonHeld = false;
            if (leftButtonMoved
                || pressDuringPlacement
                || CommanderBoxSelectService.Instance?.Dragging == true
                || CommanderSpawnService.Instance?.AwaitingRallyPointSelection == true)
            {
                return;
            }

            MapIcon? icon = FindIconUnderCursor(map);
            if (icon == null)
            {
                // Not on any icon: a click on the map itself, which puts the 3D view there.
                if (map.TryGetCursorCoordinates(out GlobalPosition position))
                {
                    CommanderTacticalMapService.Instance?.JumpCameraToPosition(position);
                }

                return;
            }

            CommanderSelectionService? selection = CommanderSelectionService.Instance;
            if (icon is UnitMapIcon unitIcon && selection != null)
            {
                // Not the base game's ClickIcon: with nobody in a cockpit that one also teleports the
                // camera onto the unit and calls SetFollowingUnit, which is the "click a unit on the
                // map and the 3D view flies to it" report. The mod's own selection is all a click means.
                bool additive = CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection);
                if (additive && selection.IsSelected(unitIcon.unit))
                {
                    CommanderSelectionService.DeselectUnit(unitIcon.unit);
                }
                else
                {
                    selection.SelectUnit(unitIcon.unit, additive);
                }

                return;
            }

            icon.ClickIcon(MapIcon.ClickSource.Controller);
        }

        private static void UpdateCameraTracking(DynamicMap map)
        {
            CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
            if (camera == null)
            {
                return;
            }

            Vector3 cameraPosition = camera.transform.position.ToGlobalPosition().AsVector3() * map.mapDisplayFactor;
            ref Vector2 positionOffset = ref PositionOffset(map);
            ref Vector2 stationaryOffset = ref StationaryOffset(map);
            if (FollowingCamera(map))
            {
                Vector2 target = new(cameraPosition.x, cameraPosition.z);
                Aircraft? aircraft = SceneSingleton<CombatHUD>.i?.aircraft;
                if (aircraft != null && !aircraft.disabled)
                {
                    Vector3 forward = aircraft.transform.forward;
                    stationaryOffset = target + (5000f * map.mapDisplayFactor * new Vector2(forward.x, forward.z));
                }
                else
                {
                    stationaryOffset = Vector2.MoveTowards(stationaryOffset, target, MapMoveMaxJumpSpeed(map));
                }
            }

            map.mapImage.transform.localEulerAngles = Vector3.zero;
            map.mapScaleCenter.transform.localEulerAngles = Vector3.zero;
            Vector2 mapPosition = -stationaryOffset - positionOffset;
            ((RectTransform)map.mapBackground.transform).rect.ClampPos(ref mapPosition, 2f);
            positionOffset = -stationaryOffset - mapPosition;
            map.mapImage.transform.localPosition = mapPosition * map.mapImage.transform.localScale.x;
            map.viewIndicator.transform.localPosition = new Vector3(cameraPosition.x, cameraPosition.z, 0f);
            map.viewIndicator.transform.eulerAngles = new Vector3(
                0f,
                0f,
                map.mapImage.transform.eulerAngles.z - camera.transform.eulerAngles.y);
        }
    }

    /// <summary>
    /// <c>MapIcon</c> is an <c>IPointerClickHandler</c>: Unity calls <c>ClickIcon</c> on every left
    /// click on a unit icon, no matter that <c>MapControls</c> is replaced, and with nobody in a cockpit
    /// that call moves the camera onto the unit. In RTS mode <c>TrackIconClick</c> owns unit clicks, so
    /// the base game's is skipped.
    /// </summary>
    [HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.ClickIcon))]
    internal static class CommanderUnitMapClickPatch
    {
        private static bool Prefix()
        {
            return CommanderPlugin.Instance?.IsCommanderModeActive != true;
        }
    }

    [HarmonyPatch(typeof(DynamicMap), "JumpCameraTo")]
    internal static class CommanderDisableBaseMapJumpPatch
    {
        internal static bool Prefix()
        {
            return CommanderPlugin.Instance?.IsCommanderModeActive != true
                || CommanderTacticalMapService.AllowCommanderMapJump;
        }
    }

    [HarmonyPatch(typeof(ExtraUiInput), "Update")]
    internal static class CommanderKeepTacticalMapOpenPatch
    {
        internal static bool Prefix()
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            if (CommanderTacticalMapService.Instance?.SuppressExtraUiThisFrame == true)
            {
                return false;
            }

            if (!CommanderGameInput.MapDown)
            {
                return true;
            }

            // The base method's own InsideInputField check is too late and unreliable: see IsTyping.
            if (CommanderShortcutInput.IsTyping)
            {
                return false;
            }

            if (CommanderAirCommandUi.Instance?.HandleMapKey() == true)
            {
                return false;
            }

            return CommanderTacticalMapService.Instance?.HandleMapKey() != true;
        }
    }

    [HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.UpdateIcon))]
    internal static class CommanderTacticalMapIconScalePatch
    {
        private static void Postfix(UnitMapIcon __instance)
        {
            if (__instance.unit is Aircraft aircraft)
            {
                CommanderRadarExposure.NoteMapIcon(aircraft, __instance);
            }

            if (CommanderPlugin.Instance?.IsCommanderModeActive == true
                && CommanderTacticalMapService.Instance?.IsOpen == true
                && __instance.iconImage != null)
            {
                __instance.iconImage.transform.localScale *= 1.4f;
            }

            // The Leviathan's body and arms are kilometres across; a building-sized dot was too
            // small to find and click on the map.
            if (__instance.iconImage != null && __instance.unit != null && CommanderLeviathanWorld.IsBigMapTarget(__instance.unit))
            {
                __instance.iconImage.transform.localScale *= CommanderLeviathanWorld.MapTargetIconScale;
            }
        }
    }

    [HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.ClickIcon))]
    internal static class CommanderAirbaseMapClickPatch
    {
        private static bool Prefix(AirbaseMapIcon __instance)
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            // Unity's EventSystem knows nothing of IMGUI, so a click on a window over the map
            // reaches the airbase icon under it too.
            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(Input.mousePosition) == true)
            {
                return false;
            }

            CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
            if (airCommand?.IsUiVisible == true)
            {
                airCommand.TrySelectAirbaseFromMap(__instance.airbase);
                return false;
            }

            // The compact Tactical Map is for command interaction and should never open
            // the Basegame aircraft-selection panel.
            return CommanderTacticalMapService.Instance?.IsOpen != true;
        }
    }

    [HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.UpdateIcon))]
    internal static class CommanderAirCommandAirbaseIconPatch
    {
        private static void Postfix(AirbaseMapIcon __instance)
        {
            CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true
                || airCommand?.IsUiVisible != true)
            {
                return;
            }

            bool selectable = airCommand.IsSelectableAirbase(__instance.airbase);
            __instance.gameObject.SetActive(DynamicMap.mapMaximized && selectable);
            if (selectable && __instance.iconImage != null && airCommand.IsSelectedAirbase(__instance.airbase))
            {
                __instance.iconImage.color = GameAssets.i.HUDFriendlySelected;
            }
        }
    }
}
