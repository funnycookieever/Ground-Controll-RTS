using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The RTS camera's feel, in one place: how fast panning scales with altitude, how far the
/// camera may pitch, how quickly it settles, and whether a unit is already framed well enough
/// that moving the camera to it would be an unwanted yank.
/// <para>
/// It is deliberately pure maths on plain numbers so <see cref="SelfCheck"/> can run it at load
/// without a scene, the same way the other services self-check.
/// </para>
/// </summary>
internal static class CommanderCameraTuning
{
    /// <summary>Pitch limits, in degrees down-positive. Past these the camera goes upside down.</summary>
    internal const float MinPitch = -80f;
    internal const float MaxPitch = 85f;

    /// <summary>Height above ground at which pan speed is exactly the configured speed.</summary>
    internal const float ReferenceHeight = 400f;
    internal const float MinSpeedScale = 0.15f;
    internal const float MaxSpeedScale = 4f;

    /// <summary>The camera eases toward this clearance, and is hard-stopped at the smaller one.</summary>
    internal const float GroundClearance = 6f;
    internal const float HardGroundClearance = 3f;
    internal const float MaxAltitude = 30000f;

    /// <summary>The ambient-light range the game's night vision reads, from NightVision.UpdateGain.</summary>
    internal const float AmbientDark = 0.01f;
    internal const float AmbientLit = 0.4f;

    /// <summary>A unit inside this much of the screen edge counts as comfortably visible.</summary>
    internal const float FrameMargin = 0.14f;

    /// <summary>Beyond this a unit is on screen but too far away to read, so it still gets framed.</summary>
    internal const float FrameMaxDistance = 15000f;

    /// <summary>
    /// How many unit radii away a unit is still big enough to make out. A fighter is about ten metres
    /// across, so it stops being readable a couple of kilometres out, well before the 15 km ceiling.
    /// </summary>
    internal const float ReadableRadiusMultiple = 200f;

    /// <summary>Fraction of the height above ground one mouse-wheel notch travels.</summary>
    internal const float ZoomStepFraction = 0.25f;

    /// <summary>Screen border, in pixels, that triggers edge scrolling.</summary>
    internal const float EdgeScrollMargin = 6f;

    /// <summary>
    /// Pan speed multiplier for the camera's height above the ground. Near the deck you nudge;
    /// high up you cross the map. Without this a single speed is either useless at altitude or
    /// uncontrollable on the deck, which is the usual reason a free camera feels wrong in an RTS.
    /// </summary>
    internal static float SpeedScale(float heightAboveGround)
    {
        return Mathf.Clamp(heightAboveGround / ReferenceHeight, MinSpeedScale, MaxSpeedScale);
    }

    /// <summary>
    /// Normalises a pan/tilt angle that may have arrived as 0-360 (Unity euler angles) and clamps
    /// it to the usable pitch band. 350 means -10, not "past the top".
    /// </summary>
    internal static float ClampPitch(float tilt)
    {
        return Mathf.Clamp(Mathf.DeltaAngle(0f, tilt), MinPitch, MaxPitch);
    }

    /// <summary>
    /// Blend factor for an exponential approach with a time-constant in seconds, frame-rate
    /// independent. A smoothing of zero means "arrive this frame".
    /// </summary>
    internal static float SmoothBlend(float smoothingSeconds, float deltaTime)
    {
        if (smoothingSeconds <= 0.0001f || deltaTime <= 0f)
        {
            return 1f;
        }

        return 1f - Mathf.Exp(-deltaTime / smoothingSeconds);
    }

    /// <summary>
    /// True when a unit is already on screen, away from the edges and close enough to read, so
    /// selecting it should leave the camera exactly where the player put it.
    /// </summary>
    internal static float ReadableDistance(float unitRadius)
    {
        return Mathf.Clamp(unitRadius * ReadableRadiusMultiple, 400f, FrameMaxDistance);
    }

    internal static bool IsFramed(Vector3 viewportPoint, float distance, float maxDistance = FrameMaxDistance)
    {
        return viewportPoint.z > 0f
            && distance <= maxDistance
            && viewportPoint.x >= FrameMargin
            && viewportPoint.x <= 1f - FrameMargin
            && viewportPoint.y >= FrameMargin
            && viewportPoint.y <= 1f - FrameMargin;
    }

    /// <summary>
    /// Where the mission sits on the game's own night-vision curve: 0 is a moonless night, 1 is
    /// broad daylight. <c>NightVision.UpdateGain</c> reads <c>LevelInfo.GetAmbientLight()</c>
    /// against these two ends, and every night-vision number below rides on the same curve so a
    /// dusk that never needed goggles is left alone.
    /// </summary>
    internal static float NightVisionDaylight(float ambientLight)
    {
        return Mathf.InverseLerp(AmbientDark, AmbientLit, ambientLight);
    }

    /// <summary>
    /// Post-exposure for the night-vision volume, in stops: the game's own gain curve plus the
    /// mod's boost, and the boost fades out as the sun comes up, so goggles left on into the
    /// morning are exactly as bright as they always were. The curve's ends are the game's
    /// serialized <c>gainMin</c> / <c>gainMax</c>, which is why they are parameters here.
    /// </summary>
    internal static float NightVisionExposure(float gainMin, float gainMax, float ambientLight, float boostStops)
    {
        float daylight = NightVisionDaylight(ambientLight);
        return Mathf.Lerp(gainMax, gainMin, daylight) + (boostStops * (1f - daylight));
    }

    /// <summary>
    /// Bloom threshold for the night-vision volume. Raising it is what stops a burning hulk or a
    /// street lamp blooming over the half of the screen the gain was meant to reveal: only the
    /// genuinely bright sources glare. Same curve, same fade-out in daylight.
    /// </summary>
    internal static float NightVisionBloomThreshold(float thresholdMin, float thresholdMax, float ambientLight, float glareCutoff)
    {
        float daylight = NightVisionDaylight(ambientLight);
        float scale = Mathf.Lerp(Mathf.Max(0.05f, glareCutoff), 1f, daylight);
        return Mathf.Lerp(thresholdMin, thresholdMax, daylight) * scale;
    }

    /// <summary>
    /// Contrast for the night-vision volume: the profile's own plus the mod's boost, faded out
    /// in daylight like the rest, and kept inside URP's -100..100.
    /// </summary>
    internal static float NightVisionContrast(float baseContrast, float ambientLight, float boost)
    {
        float daylight = NightVisionDaylight(ambientLight);
        return Mathf.Clamp(baseContrast + (boost * (1f - daylight)), -100f, 100f);
    }

    /// <summary>
    /// Ambient light floor while the goggles are on: picked so that after the volume's
    /// <paramref name="exposureStops"/> it lands at <paramref name="fill"/>, roughly how a shaded
    /// surface reads in daylight. Dividing out the exposure is what keeps it from whiting out
    /// whatever the game's serialized gain is. Faded out in daylight like the rest.
    /// </summary>
    internal static float NightVisionFillFloor(float fill, float exposureStops, float ambientLight)
    {
        float daylight = NightVisionDaylight(ambientLight);
        return Mathf.Max(0f, fill) * Mathf.Pow(2f, -exposureStops) * (1f - daylight);
    }

    /// <summary>
    /// One runnable check, run once from <see cref="CommanderPlugin"/> at load and logged to the
    /// BepInEx console. It guards the two pieces of camera maths that are silently wrong rather
    /// than obviously broken: a pitch clamp fed a 0-360 angle, and the framing test.
    /// </summary>
    internal static void SelfCheck()
    {
        System.Collections.Generic.List<string> failures = new();

        // 350 degrees is -10 degrees, and must survive the clamp unchanged.
        Expect(failures, "pitch 350 normalises", ClampPitch(350f), -10f);
        Expect(failures, "pitch clamps down", ClampPitch(120f), MaxPitch);
        Expect(failures, "pitch clamps up", ClampPitch(-120f), MinPitch);
        Expect(failures, "pitch passthrough", ClampPitch(35f), 35f);

        Expect(failures, "speed at reference", SpeedScale(ReferenceHeight), 1f);
        Expect(failures, "speed floor", SpeedScale(0f), MinSpeedScale);
        Expect(failures, "speed ceiling", SpeedScale(1e6f), MaxSpeedScale);

        Expect(failures, "smoothing off is instant", SmoothBlend(0f, 0.016f), 1f);
        if (SmoothBlend(0.05f, 0.016f) is var blend && (blend <= 0f || blend >= 1f))
        {
            failures.Add($"smoothing blend out of range: {blend}");
        }

        ExpectTrue(failures, "centred unit is framed", IsFramed(new Vector3(0.5f, 0.5f, 100f), 1000f));
        ExpectTrue(failures, "behind camera is not framed", !IsFramed(new Vector3(0.5f, 0.5f, -1f), 1000f));
        ExpectTrue(failures, "edge is not framed", !IsFramed(new Vector3(0.02f, 0.5f, 100f), 1000f));
        ExpectTrue(failures, "far away is not framed", !IsFramed(new Vector3(0.5f, 0.5f, 100f), FrameMaxDistance + 1f));

        // A ten-metre aeroplane four kilometres off is a speck, however centred it is.
        ExpectTrue(
            failures,
            "unreadable speck is not framed",
            !IsFramed(new Vector3(0.5f, 0.5f, 100f), 4000f, ReadableDistance(10f)));
        ExpectTrue(failures, "readable close up is framed", IsFramed(new Vector3(0.5f, 0.5f, 100f), 500f, ReadableDistance(10f)));
        Expect(failures, "readable distance has a floor", ReadableDistance(0f), 400f);
        Expect(failures, "readable distance has a ceiling", ReadableDistance(1e6f), FrameMaxDistance);

        // Night vision: the boost is all of itself in the dark and none of itself in daylight.
        Expect(failures, "night gain dark", NightVisionExposure(1f, 5f, 0f, 2f), 7f);
        Expect(failures, "night gain daylight", NightVisionExposure(1f, 5f, AmbientLit, 2f), 1f);
        Expect(failures, "night gain unboosted", NightVisionExposure(1f, 5f, 0f, 0f), 5f);
        Expect(failures, "night glare dark", NightVisionBloomThreshold(0.4f, 1f, 0f, 2f), 0.8f);
        Expect(failures, "night glare daylight", NightVisionBloomThreshold(0.4f, 1f, AmbientLit, 2f), 1f);
        Expect(failures, "night contrast dark", NightVisionContrast(10f, 0f, 30f), 40f);
        Expect(failures, "night contrast daylight", NightVisionContrast(10f, AmbientLit, 30f), 10f);
        Expect(failures, "night contrast clamps", NightVisionContrast(90f, 0f, 30f), 100f);
        Expect(failures, "night fill dark", NightVisionFillFloor(0.5f, 2f, 0f), 0.125f);
        Expect(failures, "night fill daylight", NightVisionFillFloor(0.5f, 2f, AmbientLit), 0f);

        if (failures.Count == 0)
        {
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Camera tuning self-check FAILED: {failures[i]}");
        }
    }

    private static void Expect(System.Collections.Generic.List<string> failures, string name, float actual, float expected)
    {
        if (!Mathf.Approximately(actual, expected))
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void ExpectTrue(System.Collections.Generic.List<string> failures, string name, bool condition)
    {
        if (!condition)
        {
            failures.Add(name);
        }
    }
}
