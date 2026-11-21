using System.Diagnostics.CodeAnalysis;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The BepInEx entry point: installs the Harmony patches, runs the load-time self-checks and adds
/// the <see cref="CommanderModeController"/> that is the RTS mode itself.
/// </summary>
[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[SuppressMessage("Usage", "CA2243:Attribute string literals should parse correctly", Justification = "A BepInEx plugin GUID is a reverse-DNS name, not a System.Guid; changing it renames the player's config file.")]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Unity owns a plugin's lifetime; OnDestroy unpatches Harmony.")]
public sealed class CommanderPlugin : BaseUnityPlugin
{
    private Harmony? harmony;
    private CommanderModeController? modeController;
    private CommanderLog? log;

    internal static CommanderPlugin? Instance { get; private set; }

    internal static CommanderLog Log => Instance!.log ??= new CommanderLog(Instance.Logger);

    internal bool IsCommanderModeActive => modeController != null && modeController.IsActive;

    // Unity mixes at most numRealVoices sources (the game ships 64) and silences the rest; big battles
    // exceed that and all sound cuts out. Reset restarts the audio system and stops every source, so it
    // runs here, before any scene plays. The virtual cap (512) is raised too, since Unity logs a
    // warning for every sound past it.
    private static void RaiseAudioVoiceLimit()
    {
        const int WantedVirtualVoices = 2048;
        AudioConfiguration audio = AudioSettings.GetConfiguration();
        int wanted = Mathf.Clamp(CommanderSettings.RealAudioVoices, 1, 255);
        if (audio.numRealVoices >= wanted && audio.numVirtualVoices >= WantedVirtualVoices)
        {
            return;
        }

        int was = audio.numRealVoices;
        int wasVirtual = audio.numVirtualVoices;
        audio.numRealVoices = Mathf.Max(audio.numRealVoices, wanted);
        audio.numVirtualVoices = Mathf.Max(audio.numVirtualVoices, WantedVirtualVoices);
        bool reset = AudioSettings.Reset(audio);
        AudioConfiguration now = AudioSettings.GetConfiguration();
        Log.LogInfo($"Audio voices: {now.numRealVoices} real (was {was}), {now.numVirtualVoices} virtual (was {wasVirtual}){(reset ? string.Empty : ", the audio reset failed")}.");
    }

    private void Awake()
    {
        Instance = this;
        CommanderSettings.Initialize(Config);
        if (!CommanderSettings.ModEnabled)
        {
            Log.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled in configuration. No patches were installed.");
            return;
        }

        RaiseAudioVoiceLimit();
        harmony = new Harmony(PluginInfo.Guid);
        harmony.PatchAll();

        CommanderNetwork.Initialize();
        CommanderPriceProfile.Initialize();
        CommanderMissionInstaller.InstallShippedMissions();
        CommanderServiceRegistryCheck.Run();
        CommanderScheduler.SelfCheck();
        CommanderNetwork.SelfCheck();
        CommanderEnemyCommanderService.SelfCheck();
        CommanderEconomyService.SelfCheck();
        CommanderPriceProfile.SelfCheck();
        CommanderCaptureService.SelfCheck();
        CommanderBuildPreview.SelfCheck();
        CommanderCameraTuning.SelfCheck();
        CommanderSupplyStoreService.SelfCheck();
        CommanderAirCommandService.SelfCheck();
        CommanderLeviathanDeepService.SelfCheck();
        CommanderTerrainRouteService.SelfCheck();
        CommanderRepairRatePatches.SelfCheck();
        CommanderDraftService.SelfCheck();
        CommanderUnitBanService.SelfCheck();
        CommanderPingService.SelfCheck();
        CommanderMapSettings.SelfCheck();
        modeController = gameObject.AddComponent<CommanderModeController>();
        Log.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded.");
    }

    private void OnDestroy()
    {
        if (harmony != null)
        {
            harmony.UnpatchSelf();
            harmony = null;
        }

        Instance = null;
    }
}
