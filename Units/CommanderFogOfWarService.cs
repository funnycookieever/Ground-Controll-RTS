using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Fog of war in the 3D world: an enemy unit is neither drawn nor heard unless it is on the local
/// faction's radar picture. That picture is the game's own spotting, the faction's
/// <c>trackingDatabase</c>, which every detector writes and the host syncs to every client, so each
/// machine hides by its own faction's knowledge with no message of ours. A building stays drawn
/// once it has been seen (it does not move); anything mobile only while its contact is live, the
/// game's own 4 s window (<c>TrackingInfo.Observed</c>), the same one its map and HUD use.
/// A hidden unit loses its meshes, lights, sounds and wheel dust; enemy blueprints are not drawn.
/// </summary>
/// <remarks>
/// <c>Renderer.forceRenderingOff</c>, <c>AudioSource.mute</c> and <c>Light.cullingMask</c>, not
/// <c>enabled</c>/<c>volume</c>/<c>intensity</c>: the game drives those itself (damage states, LODs,
/// engine revs) and the two would fight. Colliders stay, so a hidden building still stops a bomb.
/// Core tier: it has to hide whether or not RTS mode is open.
/// </remarks>
internal sealed class CommanderFogOfWarService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float IntervalSeconds = 0.25f;
    private const float MobileMemorySeconds = 4f;
    private const float FreshSeconds = 2f;

    private static readonly HashSet<Unit> Hidden = new();

    // A hidden light's own mask, put back on reveal.
    private static readonly Dictionary<Light, int> LightMasks = new();
    private static readonly List<Renderer> Renderers = new();
    private static readonly List<AudioSource> Sounds = new();
    private static readonly List<Light> Lights = new();

    /// <summary>Units hidden at spawn, re-hidden every frame until then: their stores and parts arrive a few frames late.</summary>
    private static readonly Dictionary<Unit, float> FreshUntil = new();
    private static readonly List<Unit> FreshScratch = new();

    private float nextAt;

    public void ResetSession()
    {
        // The scene is gone with its units; nothing to restore.
        Hidden.Clear();
        LightMasks.Clear();
        FreshUntil.Clear();
    }

    public void TickPersistent()
    {
        TickFresh();
        if (!CommanderScheduler.IsDue(ref nextAt, IntervalSeconds))
        {
            return;
        }

        bool on = CommanderSettings.FogOfWar && CommanderMapSettings.InMatch;
        FactionHQ? local = GameManager.GetLocalHQ(out FactionHQ hq) ? hq : null;
        if (!on || local == null)
        {
            // Off, or a spectator with no side: everything is drawn.
            RevealAll();
            return;
        }

        List<Unit> units = UnitRegistry.allUnits;
        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            if (unit == null)
            {
                continue;
            }

            if (ShouldHide(unit, local))
            {
                // Every tick, not just on the change: stores, pylons and effects are added to a
                // unit after it spawns, and would show on a jet hidden before they arrived.
                Hidden.Add(unit);
                SetHidden(unit, true);
            }
            else if (Hidden.Remove(unit))
            {
                SetHidden(unit, false);
            }
        }

        // A unit that left the registry (despawned) is dropped without touching it.
        // ponytail: a destroyed light's mask entry stays until ResetSession; prune here if matches run very long.
        Hidden.RemoveWhere(unit => unit == null);
    }

    /// <summary>A unit just spawned: hidden before its first frame is drawn, not at the next tick.</summary>
    internal static void OnRegistered(Unit unit)
    {
        if (unit != null
            && CommanderSettings.FogOfWar
            && CommanderMapSettings.InMatch
            && GameManager.GetLocalHQ(out FactionHQ local)
            && ShouldHide(unit, local))
        {
            Hidden.Add(unit);
            SetHidden(unit, true);
            FreshUntil[unit] = Time.time + FreshSeconds;
        }
    }

    /// <summary>Whether fog hides what <paramref name="hq"/> owns from this machine (its blueprints).</summary>
    internal static bool HidesFaction(FactionHQ? hq)
    {
        return CommanderSettings.FogOfWar
            && CommanderMapSettings.InMatch
            && hq != null
            && GameManager.GetLocalHQ(out FactionHQ local)
            && hq != local;
    }

    /// <summary>Whether the local faction has this enemy unit on its radar, for the hostile pickers too.</summary>
    internal static bool HasSeen(Unit unit, FactionHQ local)
    {
        TrackingInfo? tracking = local.GetTrackingData(unit.persistentID);
        return tracking != null
            && (unit is Building || Time.timeSinceLevelLoad - tracking.lastSpottedTime <= MobileMemorySeconds);
    }

    internal static bool IsHidden(Unit unit)
    {
        return Hidden.Contains(unit);
    }

    private static void TickFresh()
    {
        if (FreshUntil.Count == 0)
        {
            return;
        }

        FreshScratch.Clear();
        FreshScratch.AddRange(FreshUntil.Keys);
        for (int i = 0; i < FreshScratch.Count; i++)
        {
            Unit unit = FreshScratch[i];
            if (unit == null || Time.time > FreshUntil[unit] || !Hidden.Contains(unit))
            {
                FreshUntil.Remove(unit!);
                continue;
            }

            SetHidden(unit, true);
        }
    }

    private static bool ShouldHide(Unit unit, FactionHQ local)
    {
        // Neutral, friendly, dead (a wreck is a wreck) and missiles (their smoke trail is the warning) are drawn.
        return !unit.disabled
            && unit is not Missile
            && unit.NetworkHQ != null
            && unit.NetworkHQ != local
            && !HasSeen(unit, local);
    }

    private static void SetHidden(Unit unit, bool hide)
    {
        SetTreeHidden(unit.transform, hide);

        // A jointed part (an aircraft's wings, tail, gear) is moved out from under the unit to get
        // its own Rigidbody (AeroPart.CreateRB), so the unit's own tree only holds the fuselage.
        List<UnitPart>? parts = unit.partLookup;
        for (int i = 0; parts != null && i < parts.Count; i++)
        {
            UnitPart part = parts[i];
            if (part != null && !part.transform.IsChildOf(unit.transform))
            {
                // ponytail: a part under another detached part is walked twice; harmless, the writes repeat.
                SetTreeHidden(part.transform, hide);
            }
        }
    }

    private static void SetTreeHidden(Transform root, bool hide)
    {
        root.GetComponentsInChildren(true, Renderers);
        for (int i = 0; i < Renderers.Count; i++)
        {
            Renderers[i].forceRenderingOff = hide;
        }

        root.GetComponentsInChildren(true, Sounds);
        for (int i = 0; i < Sounds.Count; i++)
        {
            Sounds[i].mute = hide;
        }

        root.GetComponentsInChildren(true, Lights);
        for (int i = 0; i < Lights.Count; i++)
        {
            Light light = Lights[i];
            if (hide && !LightMasks.ContainsKey(light))
            {
                LightMasks[light] = light.cullingMask;
                light.cullingMask = 0;
            }
            else if (!hide && LightMasks.TryGetValue(light, out int mask))
            {
                light.cullingMask = mask;
                LightMasks.Remove(light);
            }
        }
    }

    private static void RevealAll()
    {
        foreach (Unit unit in Hidden)
        {
            if (unit != null)
            {
                SetHidden(unit, false);
            }
        }

        Hidden.Clear();
    }
}
