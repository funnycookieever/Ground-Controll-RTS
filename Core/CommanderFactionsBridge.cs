using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace GroundControlRts;

/// <summary>
/// Ground Control's side of Ground Control Factions, the separate up-to-ten-factions plugin (its own repo). Optional at build
/// and at run time: no reference to its DLL, its <c>FactionsApi</c> is found by reflection when it has loaded, and without it
/// every call here is a no-op.
/// </summary>
/// <remarks>
/// With both installed, Ground Control's defeats knock a faction out (<see cref="CommanderVictoryService"/>
/// hands Ground Control Factions the knockout) beside Ground Control Factions's own no-base rule, holds its base deal until the Champions deal is done, and kits
/// out each faction it gives a base with a depot and a munitions bunker.
/// </remarks>
internal static class CommanderFactionsBridge
{
    internal const string PluginId = "com.groundcontrol.factions";

    private static Func<FactionHQ, bool>? isEliminated;
    private static Func<FactionHQ, string, bool>? eliminate;

    internal static bool Loaded { get; private set; }

    /// <summary>Plugin load. Ground Control Factions is a soft dependency, so it has loaded first if present.</summary>
    internal static void Hook()
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginId, out BepInEx.PluginInfo info) || info.Instance == null)
        {
            return;
        }

        Type? api = info.Instance.GetType().Assembly.GetType("GroundControlFactions.FactionsApi");
        if (api == null)
        {
            CommanderPlugin.Log.LogWarning("Ground Control Factions found but it has no FactionsApi: running without it.");
            return;
        }

        try
        {
            // ExternalVictory stays off: Ground Control Factions's own "no base for 30 s" rule runs beside Ground
            // Control's, which knocks a duel's faction out sooner (its starting base). In the first
            // 8-faction soak a faction sat at no bases for 8 game minutes and Ground Control's check
            // never reached it.
            SetHook(api, "HoldDeal", new Func<bool>(() => CommanderEnemyCommanderService.IsChampionsMission
                && CommanderEconomyService.Instance != null
                && !CommanderEconomyService.Instance.ChampionsDealt));
            SetHook(api, "GiveStartingKit", new Action<FactionHQ, Airbase>((hq, airbase) => CommanderEconomyService.Instance?.GiveBaseDepots(hq, airbase)));
            SetHook(api, "IsBystander", new Func<FactionHQ, bool>(CommanderLeviathanDeepService.IsDeep));
            SetHook(api, "Announce", new Action<string>(text => CommanderAlertService.Instance?.NotifyCapture(text)));
            isEliminated = (Func<FactionHQ, bool>)Delegate.CreateDelegate(typeof(Func<FactionHQ, bool>), api.GetMethod("IsEliminated", new[] { typeof(FactionHQ) })!);
            eliminate = (Func<FactionHQ, string, bool>)Delegate.CreateDelegate(typeof(Func<FactionHQ, string, bool>), api.GetMethod("Eliminate", new[] { typeof(FactionHQ), typeof(string) })!);
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or TargetException)
        {
            CommanderPlugin.Log.LogWarning($"Ground Control Factions found but its FactionsApi does not match this version: running without it. {ex.Message}");
            return;
        }

        Loaded = true;
        CommanderPlugin.Log.LogInfo("Ground Control Factions found: Ground Control knocks out its beaten factions and kits out its bases.");
    }

    internal static bool IsEliminated(FactionHQ hq)
    {
        return isEliminated != null && isEliminated(hq);
    }

    /// <summary>Knocks a beaten faction out of a free-for-all. True once the match is decided.</summary>
    internal static bool Eliminate(FactionHQ hq, string reason)
    {
        return eliminate != null && eliminate(hq, reason);
    }

    private static void SetHook(Type api, string name, Delegate hook)
    {
        api.GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, hook);
    }
}
