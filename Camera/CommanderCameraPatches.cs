using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GroundControlRts;

internal static class CommanderCameraPatches
{
    [HarmonyPatch(typeof(CameraStateManager), nameof(CameraStateManager.SetFollowingUnit))]
    internal static class CommanderCameraFollowingPatch
    {
        private static bool Prefix(Unit unit)
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true || !DynamicMap.mapMaximized)
            {
                return true;
            }

            if (CommanderTacticalMapService.Instance?.SuppressMapFollow == true)
            {
                return false;
            }

            return !CommanderGameAccess.ShouldAllowCommanderSelection(unit, CommanderGameAccess.GetLocalHq());
        }
    }

    /// <summary>
    /// <c>NightVision.BlockToggle</c> refuses whenever the cursor is free, which in RTS mode is always,
    /// and <c>NightVision.Update</c> returns on it before it switches the effect on or off. So in RTS mode
    /// only a text field blocks it: the toggle key, the game's own binding and the auto-NVG at night all
    /// work from the commander camera.
    /// </summary>
    [HarmonyPatch(typeof(NightVision), "BlockToggle")]
    internal static class CommanderNightVisionPatch
    {
        internal static bool Prefix(ref bool __result)
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            __result = CommanderShortcutInput.IsTyping;
            return false;
        }
    }

    /// <summary>
    /// <para>
    /// Base-game night vision is one post-exposure gain and one bloom threshold, both slid along
    /// the mission's ambient light by <c>NightVision.UpdateGain</c>. At night that lands dark, and
    /// the low bloom threshold it pairs with the gain means every fire, lamp and muzzle flash
    /// smears over the half of the screen the gain was meant to reveal.
    /// </para>
    /// <para>
    /// This replaces that one method with the same curve plus two knobs:
    /// <see cref="CommanderSettings.NightVisionGain"/> stops of extra exposure and
    /// <see cref="CommanderSettings.NightVisionGlareCutoff"/> on the bloom threshold. Those two
    /// parameters are the only two the game itself writes, so nothing else in the volume profile
    /// is touched and nothing needs restoring: switch the setting off and the original runs again.
    /// </para>
    /// <para>
    /// It replaces rather than follows the original because <c>Update</c> calls this every frame
    /// while the original only recomputes once a second, so a postfix adding a boost on top of
    /// what it found would compound to white within a few frames.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(NightVision), "UpdateGain")]
    internal static class CommanderNightVisionGainPatch
    {
        // Contrast and saturation are not parameters the game writes, so the profile's own state is
        // kept here and put back when the setting goes off. A parameter without its override set
        // renders at URP's default of 0, which is why that is its base and not its stale value.
        private static ColorAdjustments? captured;
        private static bool contrastOverride;
        private static bool saturationOverride;
        private static bool colorFilterOverride;
        private static float contrastValue;
        private static float saturationValue;
        private static Color colorFilterValue;
        private static bool applied;

        internal static bool Prefix(
            ColorAdjustments ___colorAdjustments,
            Bloom ___bloom,
            float ___gainMin,
            float ___gainMax,
            float ___bloomThresholdMin,
            float ___bloomThresholdMax)
        {
            LevelInfo? level = NetworkSceneSingleton<LevelInfo>.i;
            if (___colorAdjustments == null)
            {
                return true;
            }

            if (captured != ___colorAdjustments)
            {
                captured = ___colorAdjustments;
                contrastOverride = ___colorAdjustments.contrast.overrideState;
                contrastValue = ___colorAdjustments.contrast.value;
                saturationOverride = ___colorAdjustments.saturation.overrideState;
                saturationValue = ___colorAdjustments.saturation.value;
                colorFilterOverride = ___colorAdjustments.colorFilter.overrideState;
                colorFilterValue = ___colorAdjustments.colorFilter.value;
            }

            if (!CommanderSettings.EnhanceNightVision || level == null || ___bloom == null)
            {
                // Only undo our own writes, once; otherwise leave the profile to the game and other mods.
                if (applied)
                {
                    RestoreProfile(___colorAdjustments);
                    CommanderNightVisionFillPatch.Clear();
                    applied = false;
                }

                return true;
            }

            applied = true;
            float ambient = level.GetAmbientLight();
            float exposure = CommanderCameraTuning.NightVisionExposure(
                ___gainMin, ___gainMax, ambient, CommanderSettings.NightVisionGain);
            ___colorAdjustments.postExposure.value = exposure;
            CommanderNightVisionFillPatch.SetFloor(level, CommanderCameraTuning.NightVisionFillFloor(
                CommanderSettings.NightVisionFill, exposure, ambient));

            ___bloom.threshold.value = CommanderCameraTuning.NightVisionBloomThreshold(
                ___bloomThresholdMin, ___bloomThresholdMax, ambient, CommanderSettings.NightVisionGlareCutoff);

            ___colorAdjustments.contrast.overrideState = true;
            ___colorAdjustments.contrast.value = CommanderCameraTuning.NightVisionContrast(
                contrastOverride ? contrastValue : 0f, ambient, CommanderSettings.NightVisionContrast);

            // 0 green: the profile's own tint. 1 white: drained to grey. 2 full colour: the green
            // filter swapped for white and a little saturation back, like NVG-Config's FullColor.
            int tint = CommanderSettings.NightVisionTint;
            ___colorAdjustments.saturation.overrideState = tint != 0 || saturationOverride;
            ___colorAdjustments.saturation.value = tint switch { 1 => -100f, 2 => 20f, _ => saturationValue };
            ___colorAdjustments.colorFilter.overrideState = tint == 2 || colorFilterOverride;
            ___colorAdjustments.colorFilter.value = tint == 2 ? Color.white : colorFilterValue;
            return false;
        }

        private static void RestoreProfile(ColorAdjustments adjustments)
        {
            adjustments.contrast.overrideState = contrastOverride;
            adjustments.contrast.value = contrastValue;
            adjustments.saturation.overrideState = saturationOverride;
            adjustments.saturation.value = saturationValue;
            adjustments.colorFilter.overrideState = colorFilterOverride;
            adjustments.colorFilter.value = colorFilterValue;
        }
    }

    /// <summary>
    /// At night <c>LevelInfo.UpdateTimeOfDayLighting</c> sets the ambient and fog colours near
    /// black, so the goggles' exposure has nothing to amplify: unlit ground stays black and the
    /// fog fades everything distant into it. While the goggles are on this puts a grey floor under
    /// those colours (see <see cref="CommanderCameraTuning.NightVisionFillFloor"/>), which lights
    /// the whole scene evenly like an overcast day. It is a per-channel max, so running on the
    /// method's throttled early returns cannot compound, and the game's next real update (within
    /// a second) puts its own values back once the goggles come off.
    /// </summary>
    [HarmonyPatch(typeof(LevelInfo), "UpdateTimeOfDayLighting")]
    internal static class CommanderNightVisionFillPatch
    {
        private static readonly AccessTools.FieldRef<NightVision, bool> Active =
            AccessTools.FieldRefAccess<NightVision, bool>("nightVisActive");

        private static readonly System.Action<LevelInfo, bool> Refresh =
            AccessTools.MethodDelegate<System.Action<LevelInfo, bool>>(
                AccessTools.Method(typeof(LevelInfo), "UpdateTimeOfDayLighting"));

        private static float floor;

        /// <summary>Called every frame the goggles are on; relights at once when they go on.</summary>
        internal static void SetFloor(LevelInfo level, float value)
        {
            bool wasOff = floor <= 0f;
            floor = value;
            if (wasOff && value > 0f)
            {
                Refresh(level, true);
            }
        }

        internal static void Clear()
        {
            floor = 0f;
        }

        internal static void Postfix()
        {
            if (NightVision.i == null || !Active(NightVision.i))
            {
                floor = 0f;
                return;
            }

            if (floor <= 0f)
            {
                return;
            }

            RenderSettings.ambientSkyColor = Lift(RenderSettings.ambientSkyColor, floor);
            RenderSettings.ambientEquatorColor = Lift(RenderSettings.ambientEquatorColor, floor);
            RenderSettings.ambientGroundColor = Lift(RenderSettings.ambientGroundColor, floor * 0.6f);
            RenderSettings.fogColor = Lift(RenderSettings.fogColor, floor * 1.2f);
        }

        private static Color Lift(Color colour, float min)
        {
            return new Color(
                Mathf.Max(colour.r, min),
                Mathf.Max(colour.g, min),
                Mathf.Max(colour.b, min),
                colour.a);
        }
    }

    [HarmonyPatch(typeof(SonicBoomManager), nameof(SonicBoomManager.ManageSonicBooms))]
    internal static class CommanderFollowSonicBoomPatch
    {
        private static void Prefix(ref Vector3 __state)
        {
            CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
            __state = camera != null ? camera.cameraVelocity : Vector3.zero;
            Aircraft? followed = CommanderCameraFollowService.Instance?.FollowedAircraft;
            if (camera != null && followed?.rb != null)
            {
                // SonicBoomManager only needs the listener velocity during this call.
                // Restoring it immediately avoids feeding aircraft speed into FreeCam.
                camera.cameraVelocity = followed.rb.velocity;
            }
        }

        private static void Postfix(Vector3 __state)
        {
            CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
            if (camera != null)
            {
                camera.cameraVelocity = __state;
            }
        }
    }

    [HarmonyPatch(typeof(CameraFreeState), nameof(CameraFreeState.UpdateState))]
    internal static class CommanderFreeCameraInputPatch
    {
        private const float PovMovementSpeed = 2f;
        private const float PovBoostedMovementSpeed = 10f;
        private const float BoostMultiplier = 3f;
        private const float MouseLookScale = 2f;
        private const float GroundProbeHeight = 5000f;
        private static readonly FieldInfo? PanViewField = AccessTools.Field(typeof(CameraFreeState), "panView");
        private static readonly FieldInfo? TiltViewField = AccessTools.Field(typeof(CameraFreeState), "tiltView");
        private static readonly FieldInfo? FovAdjustmentField = AccessTools.Field(typeof(CameraFreeState), "FOVAdjustment");
        private static Vector3 customVelocity;
        private static float lastGroundHeight;
        private static bool groundHeightValid;

        /// <summary>
        /// True when the player drove the camera this frame. The follow service uses it to abandon
        /// an auto-frame glide the moment the player takes the camera back.
        /// </summary>
        internal static bool PlayerMovedCameraThisFrame { get; private set; }

        internal static void ResetCustomMotion()
        {
            customVelocity = Vector3.zero;
            groundHeightValid = false;
            PlayerMovedCameraThisFrame = false;
        }

        /// <summary>Terrain height under the camera, the same probe the Basegame uses for its own clamp.</summary>
        internal static float GroundHeightBelow(Vector3 position)
        {
            if (Physics.Linecast(
                position + (Vector3.up * GroundProbeHeight),
                position - (Vector3.up * GroundProbeHeight),
                out RaycastHit hit,
                PhysicsLayers.StaticsMask))
            {
                return Mathf.Max(hit.point.y, Datum.LocalSeaY);
            }

            return Datum.LocalSeaY;
        }

        private static void Prefix(CameraFreeState __instance, CameraStateManager cam, out CameraInputState __state)
        {
            __state = default;
            PlayerMovedCameraThisFrame = false;
            if (!CommanderCameraController.CustomInputActive)
            {
                return;
            }

            __state.Active = true;
            __state.AllowInputs = cam.allowInputs;
            __state.Rotation = cam.transform.rotation;
            __state.Pan = GetAngle(PanViewField, __instance, cam.transform.eulerAngles.y);
            __state.Tilt = GetAngle(TiltViewField, __instance, cam.transform.eulerAngles.x);

            // The RTS camera keeps a fixed lens. Left alone the Basegame maps the wheel to an FOV
            // axis, which warps the view and silently rescales look sensitivity along with it.
            __state.FieldOfView = cam.mainCamera.fieldOfView;
            __state.FovAdjustment = FovAdjustmentField?.GetValue(__instance);

            // Keep the Basegame FreeCam update for terrain collision and state handling, but prevent
            // Rewired's aircraft bindings from moving it.
            cam.allowInputs = false;
            cam.cameraVelocity = Vector3.zero;
            if (CommanderTacticalMapService.Instance?.IsFullscreenOpen == true
                || CommanderShortcutInput.IsTyping)
            {
                customVelocity = Vector3.zero;
                return;
            }

            if (CommanderCameraFollowService.IsPovActive)
            {
                PovMotion(cam, ref __state);
                return;
            }

            // Speed and zoom size themselves off last frame's ground height, which is invisibly
            // stale; the clamp re-probes after the move, because at boost speed the camera can cross
            // a whole hillside in one frame and a stale floor would let it through the terrain.
            if (!groundHeightValid)
            {
                lastGroundHeight = GroundHeightBelow(cam.transform.position);
                groundHeightValid = true;
            }

            float aboveGround = cam.transform.position.y - lastGroundHeight;
            float speedScale = CommanderSettings.CameraHeightScaledSpeed
                ? CommanderCameraTuning.SpeedScale(aboveGround)
                : 1f;

            GroundMotion(cam, speedScale);
            WheelZoom(cam, aboveGround);
            LookInput(cam, ref __state);
            lastGroundHeight = GroundHeightBelow(cam.transform.position);
            ClampToGround(cam, lastGroundHeight);

            __state.ReportedVelocity = customVelocity;
        }

        /// <summary>
        /// Pan across the ground, not along the view axis. A pitched-down camera moving on its own
        /// forward vector dives into the terrain and is then shoved back up by the ground clamp,
        /// which is most of what the old camera felt like. Up/down stay the only way to change height.
        /// </summary>
        private static void GroundMotion(CameraStateManager cam, float speedScale)
        {
            float longitudinal = Axis(CommanderSettings.CameraForward, CommanderSettings.CameraBackward);
            float lateral = Axis(CommanderSettings.CameraRight, CommanderSettings.CameraLeft);
            float vertical = Axis(CommanderSettings.CameraUp, CommanderSettings.CameraDown);
            ApplyEdgeScroll(ref longitudinal, ref lateral);

            Vector3 flatForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 0.0001f)
            {
                // Looking straight down: the top of the screen is the direction that reads as forward.
                flatForward = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
            }

            flatForward = flatForward.sqrMagnitude < 0.0001f ? Vector3.forward : flatForward.normalized;
            Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);

            Vector3 direction = (flatForward * longitudinal) + (flatRight * lateral) + (Vector3.up * vertical);
            bool hasMovementInput = direction.sqrMagnitude > 0.001f;
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            bool boost = CommanderShortcutInput.IsPressed(CommanderSettings.CameraBoost);
            float speed = CommanderSettings.CameraPanSpeed * speedScale * (boost ? BoostMultiplier : 1f);
            Vector3 targetVelocity = direction * cam.desiredTransSpeed * speed;
            float blend = CommanderCameraTuning.SmoothBlend(CommanderSettings.CameraSmoothing, Time.unscaledDeltaTime);
            customVelocity = Vector3.Lerp(customVelocity, targetVelocity, blend);
            if (!hasMovementInput && customVelocity.sqrMagnitude < 0.01f)
            {
                customVelocity = Vector3.zero;
            }

            cam.transform.position += customVelocity * Time.unscaledDeltaTime;
            PlayerMovedCameraThisFrame |= hasMovementInput;
        }

        /// <summary>Classic RTS screen-edge push, off by default because the mod's windows crowd the edges.</summary>
        private static void ApplyEdgeScroll(ref float longitudinal, ref float lateral)
        {
            if (!CommanderSettings.CameraEdgeScroll || !Application.isFocused)
            {
                return;
            }

            Vector3 mouse = Input.mousePosition;
            if (mouse.x < 0f || mouse.y < 0f || mouse.x > Screen.width || mouse.y > Screen.height)
            {
                return;
            }

            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(mouse) == true
                || CommanderTacticalMapService.Instance?.ContainsScreenPoint(mouse) == true)
            {
                return;
            }

            float margin = CommanderCameraTuning.EdgeScrollMargin;
            if (mouse.x <= margin)
            {
                lateral -= 1f;
            }
            else if (mouse.x >= Screen.width - margin)
            {
                lateral += 1f;
            }

            if (mouse.y <= margin)
            {
                longitudinal -= 1f;
            }
            else if (mouse.y >= Screen.height - margin)
            {
                longitudinal += 1f;
            }
        }

        /// <summary>
        /// The wheel dollies toward whatever the cursor is over, so zooming in also recentres on the
        /// thing you were pointing at. Each notch covers a quarter of the height above the ground,
        /// which keeps the step readable from the deck to the stratosphere.
        /// </summary>
        private static void WheelZoom(CameraStateManager cam, float aboveGround)
        {
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(wheel, 0f))
            {
                return;
            }

            Vector3 mouse = Input.mousePosition;
            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(mouse) == true
                || CommanderTacticalMapService.Instance?.ContainsScreenPoint(mouse) == true)
            {
                return;
            }

            Vector3 position = cam.transform.position;
            Vector3 direction = cam.transform.forward;
            float available = float.MaxValue;
            if (TryGetGroundPoint(cam, Input.mousePosition, out Vector3 groundPoint))
            {
                Vector3 toPoint = groundPoint - position;
                if (toPoint.sqrMagnitude > 1f)
                {
                    available = toPoint.magnitude;
                    direction = toPoint / available;
                }
            }

            float step = wheel * Mathf.Max(aboveGround, 20f) * CommanderCameraTuning.ZoomStepFraction
                * Mathf.Max(CommanderSettings.CameraZoomSpeed, 0.05f);

            // Never let a zoom-in punch through the point it is aiming at.
            step = Mathf.Min(step, Mathf.Max(available - CommanderCameraTuning.GroundClearance, 0f));
            Vector3 moved = position + (direction * step);
            moved.y = Mathf.Min(moved.y, CommanderCameraTuning.MaxAltitude);
            cam.transform.position = moved;
            PlayerMovedCameraThisFrame = true;
        }

        /// <summary>
        /// Hold the look key to swing the view. The camera turns about itself rather than orbiting the
        /// ground point at screen centre, which can be kilometres away on a shallow view.
        /// </summary>
        private static void LookInput(CameraStateManager cam, ref CameraInputState state)
        {
            if (!CommanderShortcutInput.IsPressed(CommanderSettings.CameraFreeLook))
            {
                return;
            }

            float fovScale = Mathf.Min(cam.mainCamera.fieldOfView / 20f, 1f);
            float sensitivity = MouseLookScale * Mathf.Max(CommanderSettings.CameraLookSensitivity, 0.05f);
            float pitchDirection = PlayerSettings.viewInvertPitch ? 1f : -1f;
            float oldPan = state.Pan;
            float oldTilt = CommanderCameraTuning.ClampPitch(state.Tilt);
            state.Pan += fovScale * Input.GetAxisRaw("Mouse X") * sensitivity;
            state.Tilt = CommanderCameraTuning.ClampPitch(
                oldTilt + (pitchDirection * fovScale * Input.GetAxisRaw("Mouse Y") * sensitivity));

            PlayerMovedCameraThisFrame |= !Mathf.Approximately(state.Pan, oldPan)
                || !Mathf.Approximately(state.Tilt, oldTilt);
        }

        /// <summary>
        /// Keeps clearance over the terrain without the Basegame's hard snap, which jolts every time
        /// the camera crosses a ridge. It eases toward the comfortable clearance and only stops dead
        /// at the much lower hard limit, which also keeps the Basegame's own clamp from ever firing.
        /// </summary>
        private static void ClampToGround(CameraStateManager cam, float groundHeight)
        {
            Vector3 position = cam.transform.position;
            float soft = groundHeight + CommanderCameraTuning.GroundClearance;
            if (position.y < soft)
            {
                float blend = CommanderCameraTuning.SmoothBlend(0.08f, Time.unscaledDeltaTime);
                position.y = Mathf.Lerp(position.y, soft, blend);
            }

            position.y = Mathf.Clamp(
                position.y,
                groundHeight + CommanderCameraTuning.HardGroundClearance,
                CommanderCameraTuning.MaxAltitude);
            cam.transform.position = position;
        }

        /// <summary>POV rides inside a unit, so it keeps the old view-relative nudge and no RTS aids.</summary>
        private static void PovMotion(CameraStateManager cam, ref CameraInputState state)
        {
            float longitudinal = Axis(CommanderSettings.CameraForward, CommanderSettings.CameraBackward);
            float lateral = Axis(CommanderSettings.CameraRight, CommanderSettings.CameraLeft);
            float vertical = Axis(CommanderSettings.CameraUp, CommanderSettings.CameraDown);
            Vector3 direction = (cam.transform.forward * longitudinal)
                + (cam.transform.right * lateral)
                + (Vector3.up * vertical);
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            bool boost = CommanderShortcutInput.IsPressed(CommanderSettings.CameraBoost);
            customVelocity = direction * (boost ? PovBoostedMovementSpeed : PovMovementSpeed);
            cam.transform.position += customVelocity * Time.unscaledDeltaTime;
            state.ReportedVelocity = Vector3.zero;
        }

        private static void Postfix(CameraFreeState __instance, CameraStateManager cam, CameraInputState __state)
        {
            if (!__state.Active)
            {
                return;
            }

            cam.allowInputs = __state.AllowInputs;
            cam.cameraVelocity = __state.ReportedVelocity;
            PanViewField?.SetValue(__instance, __state.Pan);
            TiltViewField?.SetValue(__instance, __state.Tilt);
            cam.mainCamera.fieldOfView = __state.FieldOfView;
            if (__state.FovAdjustment != null)
            {
                FovAdjustmentField?.SetValue(__instance, __state.FovAdjustment);
            }

            // Erase any Basegame Free Look input applied by the original method. POV rebuilds its
            // complete attached rotation in the camera LateUpdate postfix.
            if (!CommanderCameraFollowService.IsPovActive)
            {
                float blend = CommanderCameraTuning.SmoothBlend(
                    CommanderSettings.CameraSmoothing, Time.unscaledDeltaTime);
                cam.transform.rotation = Quaternion.Lerp(
                    __state.Rotation,
                    Quaternion.Euler(__state.Tilt, __state.Pan, 0f),
                    blend);
            }
        }

        /// <summary>World point under a screen position: the zoom target under the cursor.</summary>
        private static bool TryGetGroundPoint(CameraStateManager cam, Vector3 screenPoint, out Vector3 point)
        {
            point = Vector3.zero;
            if (cam.mainCamera == null)
            {
                return false;
            }

            Ray ray = cam.mainCamera.ScreenPointToRay(screenPoint);
            if (Physics.Raycast(ray, out RaycastHit hit, 60000f, PhysicsLayers.StaticsMask))
            {
                point = hit.point;
                return true;
            }

            // No terrain under the cursor (open water, or off the map edge): use the sea plane.
            if (ray.direction.y >= -0.01f)
            {
                return false;
            }

            point = ray.origin + (ray.direction * ((Datum.LocalSeaY - ray.origin.y) / ray.direction.y));
            return true;
        }

        private static float Axis(BepInEx.Configuration.KeyboardShortcut positive, BepInEx.Configuration.KeyboardShortcut negative)
        {
            return (CommanderShortcutInput.IsPressed(positive) ? 1f : 0f)
                - (CommanderShortcutInput.IsPressed(negative) ? 1f : 0f);
        }

        private static float GetAngle(FieldInfo? field, CameraFreeState state, float fallback)
        {
            return field?.GetValue(state) is float value ? value : fallback;
        }

        private struct CameraInputState
        {
            internal bool Active;
            internal bool AllowInputs;
            internal float Pan;
            internal float Tilt;
            internal float FieldOfView;
            internal object? FovAdjustment;
            internal Quaternion Rotation;
            internal Vector3 ReportedVelocity;
        }
    }
}
