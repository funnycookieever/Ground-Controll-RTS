using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderSettings
{
    // Visuals. Defaults are the theme's own colours, so an untouched config looks as it always did.
    internal static readonly Color DefaultAccentColor = new(0.40f, 0.78f, 0.60f, 1f);
    internal static readonly Color DefaultBackgroundColor = new(0.085f, 0.098f, 0.110f, 1f);

    private static readonly Dictionary<string, ConfigEntryBase> Entries = new();
    private static ConfigFile? config;

    // The player's multiplier on top of the resolution scale in CommanderUiScale.
    internal static float UiScale { get => Get("UI", "UiScale", 1f); set => Set("UI", "UiScale", value); }

    // How many sounds Unity mixes at once (the game's own is 64, Unity's ceiling 255). Read at launch.
    internal static int RealAudioVoices { get => Get("General", "RealAudioVoices", 255); set => Set("General", "RealAudioVoices", value); }

    internal static bool ModEnabled { get => Get("General", "Enabled", true); set => Set("General", "Enabled", value); }

    internal static bool LimitToFactoryVehicles { get => CommanderMapSettings.OverrideBool(CommanderMapSettings.FactoryVehiclesPrefix) ?? Get("Gameplay", "LimitToFactoryVehicles", false); set => Set("Gameplay", "LimitToFactoryVehicles", value); }

    // Off by default: the mission's own AI keeps launching aircraft and rolling vehicles out of
    // the reserve. On, only what the commander spawns takes the field on the local side.
    // The AREA FIRE slider: the diameter the next area is placed at.
    internal static float AreaFireDiameterMeters { get => Get("Gameplay", "AreaFireDiameterMeters", 1000f); set => Set("Gameplay", "AreaFireDiameterMeters", value); }

    internal static bool DisableFriendlyAi { get => CommanderMapSettings.OverrideBool(CommanderMapSettings.FriendlyAiOffPrefix) ?? Get("Gameplay", "DisableFriendlyAi", false); set => Set("Gameplay", "DisableFriendlyAi", value); }

    // Off by default. One cap per side, both sides: see CommanderUnitCap.
    internal static bool UnitCapEnabled { get => CommanderMapSettings.OverrideInt(CommanderMapSettings.UnitCapPrefix) is int cap ? cap > 0 : Get("Gameplay", "UnitCapEnabled", false); set => Set("Gameplay", "UnitCapEnabled", value); }

    internal static int UnitCapMode { get => CommanderMapSettings.OverrideInt(CommanderMapSettings.UnitCapPrefix) is int cap && cap > 0 ? cap - 1 : Get("Gameplay", "UnitCapMode", CommanderUnitCap.ModeNormal); set => Set("Gameplay", "UnitCapMode", value); }

    // Off by default. The host's choice decides the match: see CommanderUnitBanService.
    internal static bool UnitBansEnabled { get => CommanderMapSettings.OverrideBool(CommanderMapSettings.BanVotePrefix) ?? Get("Gameplay", "UnitBansEnabled", false); set => Set("Gameplay", "UnitBansEnabled", value); }

    // How many units each side bans in the vote, of any kind. Clamped to the roster when read.
    internal static int UnitBanVoteCount { get => CommanderMapSettings.OverrideInt(CommanderMapSettings.BansPerSidePrefix) ?? Get("Gameplay", "UnitBanVoteCount", 2); set => Set("Gameplay", "UnitBanVoteCount", value); }

    // The host's own bans, jsonKeys separated by commas, set on the GC RTS Settings page. Any
    // kind and any number; they apply whether or not the vote is on.
    internal static string HostBannedUnits { get => Get("Gameplay", "HostBannedUnits", string.Empty); set => Set("Gameplay", "HostBannedUnits", value); }

    internal static bool ShowCommandButton { get => Get("UI", "ShowCommandButton", true); set => Set("UI", "ShowCommandButton", value); }

    internal static bool ShowFactionMoney { get => Get("UI", "ShowFactionMoney", true); set => Set("UI", "ShowFactionMoney", value); }

    internal static bool ShowTacticalMap { get => Get("UI", "ShowTacticalMap", true); set => Set("UI", "ShowTacticalMap", value); }

    internal static bool ShowSelectionBar { get => Get("UI", "ShowSelectionBar", true); set => Set("UI", "ShowSelectionBar", value); }

    internal static bool ShowPinnedUnits { get => Get("UI", "ShowPinnedUnits", true); set => Set("UI", "ShowPinnedUnits", value); }

    internal static bool ShowUnitSystems { get => Get("UI", "ShowUnitSystems", true); set => Set("UI", "ShowUnitSystems", value); }

    internal static bool ShowDepotUi { get => Get("UI", "ShowDepotUi", true); set => Set("UI", "ShowDepotUi", value); }

    internal static bool ShowSupplyUi { get => Get("UI", "ShowSupplyUi", true); set => Set("UI", "ShowSupplyUi", value); }

    internal static bool ShowAirCommandUi { get => Get("UI", "ShowAirCommandUi", true); set => Set("UI", "ShowAirCommandUi", value); }

    internal static bool ShowNavalUi { get => Get("UI", "ShowNavalUi", true); set => Set("UI", "ShowNavalUi", value); }

    internal static bool ShowSamAnalyzerUi { get => Get("UI", "ShowSamAnalyzerUi", true); set => Set("UI", "ShowSamAnalyzerUi", value); }

    internal static bool ShowWorldMarkers { get => Get("UI", "ShowWorldMarkers", true); set => Set("UI", "ShowWorldMarkers", value); }

    internal static bool ShowUnitListUi { get => Get("UI", "ShowUnitListUi", true); set => Set("UI", "ShowUnitListUi", value); }

    internal static bool ShowBuildUi { get => Get("UI", "ShowBuildUi", true); set => Set("UI", "ShowBuildUi", value); }

    // Plain UI is the accessibility mode: opaque plates, solid edges, no fades or gradients.
    internal static bool PlainUi { get => Get("Visuals", "PlainUi", false); set => Set("Visuals", "PlainUi", value); }

    // Theme colours live under new keys (was Visuals/AccentColor): BepInEx keeps an old value, and
    // the 0.9 neon defaults would otherwise survive the move to the slate look.
    internal static Color AccentColor { get => Get("Theme", "Accent", DefaultAccentColor); set => Set("Theme", "Accent", value); }

    internal static Color BackgroundColor { get => Get("Theme", "Background", DefaultBackgroundColor); set => Set("Theme", "Background", value); }

    // The base game's night vision is one post-exposure gain driven by the mission's ambient light,
    // and at night it lands dark with every fire and lamp blooming over what little it revealed.
    // See CommanderNightVisionGainPatch. Both numbers fade out as the sun comes up.
    internal static bool EnhanceNightVision { get => Get("Visuals", "EnhanceNightVision", true); set => Set("Visuals", "EnhanceNightVision", value); }

    // Extra post-exposure in stops, so +1 is twice as bright.
    internal static float NightVisionGain { get => Get("Visuals", "NightVisionGain", 2.5f); set => Set("Visuals", "NightVisionGain", value); }

    // Multiplies the game's bloom threshold: above 1, only real light sources glare.
    internal static float NightVisionGlareCutoff { get => Get("Visuals", "NightVisionGlareCutoff", 1.8f); set => Set("Visuals", "NightVisionGlareCutoff", value); }

    // Added to the volume's contrast (URP's -100..100). Brightness alone lifts the picture into a flat
    // green haze where nothing stands out; contrast is what separates a vehicle from the ground.
    internal static float NightVisionContrast { get => Get("Visuals", "NightVisionContrast", 30f); set => Set("Visuals", "NightVisionContrast", value); }

    // 0 green phosphor (the game's), 1 white phosphor, 2 full colour. Full colour plus the fill below
    // reads like an overcast day. A new key, so the old white-phosphor bool doesn't carry over.
    internal static int NightVisionTint { get => Get("Visuals", "NightVisionTint", 2); set => Set("Visuals", "NightVisionTint", value); }

    // How bright unlit ground looks through the goggles, as a fraction of a shaded daytime surface.
    // At night the scene has almost no ambient light, so exposure alone amplifies black; this puts
    // a floor under the ambient and fog light. See CommanderNightVisionFillPatch.
    internal static float NightVisionFill { get => Get("Visuals", "NightVisionFill", 0.5f); set => Set("Visuals", "NightVisionFill", value); }

    // Per-window zoom from the corner grips, "id=scale;id=scale". See CommanderWindowScale.
    // A new key since the UI went to native scale: zooms saved against the old 2/3 base were too big.
    internal static string WindowScales { get => Get("UI", "WindowZoom", string.Empty); set => Set("UI", "WindowZoom", value); }

    // One-time reset of AIR COMMAND's saved zoom. See CommanderWindowScale.Load.
    internal static bool AirWindowZoomCleared { get => Get("UI", "AirWindowZoomCleared", false); set => Set("UI", "AirWindowZoomCleared", value); }

    // Dragged window edges and section bars, "id:slot=size;...". See CommanderWindowScale.
    internal static string WindowSizes { get => Get("UI", "WindowSizes", string.Empty); set => Set("UI", "WindowSizes", value); }

    // A new key since the UI went to native scale (was UI/TacticalMapSize, in 2/3-scale units).
    internal static float TacticalMapSize { get => Get("UI", "TacticalMapPixels", 540f); set => Set("UI", "TacticalMapPixels", value); }

    // Renamed from MapDragSensitivity because the meaning changed: the pan is now driven by real
    // cursor pixels against the map's on-screen scale, so 1.0 means the map sticks to the cursor.
    // A rename is the only way to reissue a default, since BepInEx keeps whatever is in the file.
    internal static float MapDragSpeed { get => Get("UI", "MapDragSpeed", 1f); set => Set("UI", "MapDragSpeed", value); }

    internal static float MapZoomSpeed { get => Get("UI", "MapZoomSpeed", 1f); set => Set("UI", "MapZoomSpeed", value); }

    internal static bool AutoFollowSelection { get => Get("Camera", "AutoFollowSelection", false); set => Set("Camera", "AutoFollowSelection", value); }

    // Selecting a unit attaches the follow but must not yank a camera the player just aimed, so
    // the camera only travels when the unit is off screen, near an edge, or too far to read.
    internal static bool AutoFrameSelection { get => Get("Camera", "AutoFrameOffscreenSelection", true); set => Set("Camera", "AutoFrameOffscreenSelection", value); }

    internal static float CameraPanSpeed { get => Get("Camera", "PanSpeed", 300f); set => Set("Camera", "PanSpeed", value); }

    internal static float CameraZoomSpeed { get => Get("Camera", "ZoomSpeed", 1f); set => Set("Camera", "ZoomSpeed", value); }

    // The RTS camera keeps its own look feel instead of borrowing PlayerSettings.viewSensitivity
    // and viewSmoothing, which are tuned for a pilot's head in a cockpit and read as lag here.
    internal static float CameraLookSensitivity { get => Get("Camera", "LookSensitivity", 1f); set => Set("Camera", "LookSensitivity", value); }

    internal static float CameraSmoothing { get => Get("Camera", "Smoothing", 0.05f); set => Set("Camera", "Smoothing", value); }

    internal static bool CameraHeightScaledSpeed { get => Get("Camera", "HeightScaledSpeed", true); set => Set("Camera", "HeightScaledSpeed", value); }

    // The base game's map jump always lands about 450 m up, so every jump from a high overview
    // meant zooming back out by hand.
    internal static float MapJumpHeight { get => Get("Camera", "MapJumpHeight", 450f); set => Set("Camera", "MapJumpHeight", value); }

    internal static bool MapJumpKeepHeight { get => Get("Camera", "MapJumpKeepHeight", true); set => Set("Camera", "MapJumpKeepHeight", value); }

    internal static bool CameraEdgeScroll { get => Get("Camera", "EdgeScroll", false); set => Set("Camera", "EdgeScroll", value); }

    // Follow copies a smoothed anchor rather than the unit's exact per-frame movement, so an
    // aircraft's jitter does not become camera shake. Lead is off by default: it is a taste knob.
    internal static float FollowSmoothing { get => Get("Camera", "FollowSmoothing", 0.1f); set => Set("Camera", "FollowSmoothing", value); }

    internal static float FollowLeadSeconds { get => Get("Camera", "FollowLeadSeconds", 0f); set => Set("Camera", "FollowLeadSeconds", value); }

    internal static bool GroupHotkeys { get => Get("Gameplay", "GroupHotkeys", true); set => Set("Gameplay", "GroupHotkeys", value); }

    internal static bool CameraBookmarks { get => Get("Gameplay", "CameraBookmarks", true); set => Set("Gameplay", "CameraBookmarks", value); }

    internal static bool OrderFeedback { get => Get("Gameplay", "OrderFeedback", true); set => Set("Gameplay", "OrderFeedback", value); }

    /// <summary>Radar warning ring round the player's AI aircraft in the RTS view.</summary>
    internal static bool RadarExposureRings { get => Get("Gameplay", "RadarExposureRings", true); set => Set("Gameplay", "RadarExposureRings", value); }

    /// <summary>Tactical map draws the route of every unit you own, not only the selected ones.</summary>
    internal static bool MapAllRoutes { get => Get("Gameplay", "MapAllRoutes", false); set => Set("Gameplay", "MapAllRoutes", value); }

    /// <summary>Pings, attack targets and contested bases drawn over the cockpit view while flying.</summary>
    internal static bool CockpitMarkers { get => Get("Gameplay", "CockpitMarkers", true); set => Set("Gameplay", "CockpitMarkers", value); }

    /// <summary>How long a temporary ping stays up. Permanent markers stay until someone removes them.</summary>
    internal static float PingSeconds { get => Get("Gameplay", "PingSeconds", 90f); set => Set("Gameplay", "PingSeconds", value); }

    internal static bool RetargetAfterKill { get => Get("Gameplay", "RetargetAfterKill", true); set => Set("Gameplay", "RetargetAfterKill", value); }

    internal static bool AttackMoveIntoRange { get => Get("Gameplay", "AttackMoveIntoRange", true); set => Set("Gameplay", "AttackMoveIntoRange", value); }

    internal static bool AttackMoveRoutes { get => Get("Gameplay", "AttackMoveRoutes", true); set => Set("Gameplay", "AttackMoveRoutes", value); }

    internal static bool GuardOrders { get => Get("Gameplay", "GuardOrders", true); set => Set("Gameplay", "GuardOrders", value); }

    internal static bool AutoRetreatDamaged { get => Get("Gameplay", "AutoRetreatDamaged", false); set => Set("Gameplay", "AutoRetreatDamaged", value); }

    internal static float RetreatConditionPercent { get => Get("Gameplay", "RetreatConditionPercent", 40f); set => Set("Gameplay", "RetreatConditionPercent", value); }

    internal static float WaypointHoldSeconds { get => Get("Gameplay", "WaypointHoldSeconds", 60f); set => Set("Gameplay", "WaypointHoldSeconds", value); }

    internal static int FormationShape { get => Get("Gameplay", "FormationShape", 0); set => Set("Gameplay", "FormationShape", value); }

    internal static float FormationCohesionMeters { get => Get("Gameplay", "FormationCohesionMeters", 300f); set => Set("Gameplay", "FormationCohesionMeters", value); }

    internal static bool CombatAlerts { get => Get("Gameplay", "CombatAlerts", true); set => Set("Gameplay", "CombatAlerts", value); }

    internal static bool AlertWindow { get => Get("UI", "AlertWindow", true); set => Set("UI", "AlertWindow", value); }

    // 1 MATCHED, 2 MISSION FUNDS (0, the retired AUTO, plays as MATCHED). OFF is never saved:
    // it lasts one match (CommanderEnemyCommanderService.OffThisMatch), so the duel always opens
    // with an opponent.
    internal static int EnemyCommanderMode { get => CommanderMapSettings.OverrideInt(CommanderMapSettings.EnemyModePrefix) ?? Get("Gameplay", "EnemyCommanderMode", 0); set => Set("Gameplay", "EnemyCommanderMode", CommanderMapSettings.FollowInMatch(CommanderMapSettings.EnemyModePrefix, value)); }

    /// <summary>Which missions get AI commanders: 0 the Ground Control missions (and Carrier Duel)
    /// only, 1 the other side on every mission, base game and custom alike, 2 every side, the host's
    /// own included. See <c>CommanderEnemyCommanderService.AiCommanders</c>.</summary>
    internal static int AiCommanders { get => CommanderMapSettings.OverrideInt(CommanderMapSettings.AiCommandersPrefix) ?? Get("Gameplay", "AiCommanders", 0); set => Set("Gameplay", "AiCommanders", value); }

    /// <summary>Whether the enemy commander also runs a faction that has human players on it. Off:
    /// a team with a player is that player's. On: players there fly for the AI commander.</summary>
    internal static bool EnemyCommanderHumanTeams { get => CommanderMapSettings.OverrideBool(CommanderMapSettings.HumanTeamsPrefix) ?? Get("Gameplay", "EnemyCommanderHumanTeams", false); set => Set("Gameplay", "EnemyCommanderHumanTeams", CommanderMapSettings.FollowInMatch(CommanderMapSettings.HumanTeamsPrefix, value ? 1 : 0) != 0); }

    /// <summary>How the enemy commander plays: 0 NORMAL, 1 CHALLENGING (counter-picks what it buys
    /// and attacks in one massed push). Same money either way.</summary>
    internal static int EnemyDifficulty { get => CommanderMapSettings.OverrideInt(CommanderMapSettings.EnemyDifficultyPrefix) ?? Get("Gameplay", "EnemyDifficulty", 0); set => Set("Gameplay", "EnemyDifficulty", CommanderMapSettings.FollowInMatch(CommanderMapSettings.EnemyDifficultyPrefix, value)); }

    /// <summary>GOD DIFFICULTY on every Champions match, not only one with a 10 B budget.</summary>
    internal static bool ForceGodDifficulty { get => Get("Gameplay", "ForceGodDifficulty", false); set => Set("Gameplay", "ForceGodDifficulty", value); }

    // Economy prices are mission-relative: faction balances are authored per mission (about 1000
    // at the start of Escalation), so these are knobs, not constants.

    /// <summary>What a hostile commander's mine earns, as a multiple of the player's. The AI has no
    /// second income (it never flies a sortie for a kill reward the way a player does), so a flat
    /// parity mine leaves it poorer than the player over a long match. CHALLENGING only.</summary>
    internal static float EnemyMineIncomeMultiplier { get => Get("Economy", "EnemyMineIncomeMultiplier", 1.5f); set => Set("Economy", "EnemyMineIncomeMultiplier", value); }

    // An aircraft plant's price is FactoryBuildCost scaled by its product's value against this reference.
    // Ground plants ignore it: they cost a fixed number of their product. Roughly the middle of a roster.
    internal static float FactoryProductReferenceValue { get => Get("Economy", "FactoryProductReferenceValue", 8f); set => Set("Economy", "FactoryProductReferenceValue", value); }

    internal static float FactoryProductionSeconds { get => Get("Economy", "FactoryProductionSeconds", 240f); set => Set("Economy", "FactoryProductionSeconds", value); }

    // Seconds between warheads at a nuclear enrichment plant, i.e. how long one lot of enriched
    // uranium takes. Its output is one tactical nuclear munition, so it is deliberately far slower
    // than a vehicle line rather than priced as one.
    // Renamed from WarheadIntervalSeconds (300, itself renamed from WarheadProductionSeconds 600) so
    // an existing config picks up the new default.
    internal static float WarheadProductionSeconds { get => Get("Economy", "WarheadCycleSeconds", 120f); set => Set("Economy", "WarheadCycleSeconds", value); }

    // The nuclear enrichment plant is priced here rather than off its encyclopedia value.
    internal static float EnrichmentPlantCost { get => Get("Economy", "EnrichmentPlantCost", 2000f); set => Set("Economy", "EnrichmentPlantCost", value); }

    // A catalogue building is priced off its own encyclopedia value, so a radar costs what a
    // radar is worth without the mod carrying a price table that a game patch would invalidate.
    // The active economy profile's name (a file in config/GroundControlRts/Economy), or STANDARD.
    internal static string EconomyProfile { get => Get("Economy", "Profile", "STANDARD"); set => Set("Economy", "Profile", value); }

    internal static float BuildingCostMultiplier { get => Get("Economy", "BuildingCostMultiplier", 1f); set => Set("Economy", "BuildingCostMultiplier", value); }

    // Share of a destroyed vehicle, aircraft or ship's price paid into the faction funds of whoever
    // killed it, split by damage, every faction and every mission. Replaces the old sqrt-based
    // KillFundsBonus (new key so existing configs pick up the new default).
    internal static float KillValueShare { get => Get("Economy", "KillValueShare", 0.25f); set => Set("Economy", "KillValueShare", value); }

    // Share of an armed building's or air-defence vehicle's price its owner pays every minute to
    // hold it, on the mod's own missions. Defence costs money to hold, attack does not.
    internal static float DefenceUpkeepPercentPerMinute { get => Get("Economy", "DefenceUpkeepPercentPerMinute", 1f); set => Set("Economy", "DefenceUpkeepPercentPerMinute", value); }

    internal static float RepairCrewCost { get => Get("Economy", "RepairCrewCost", 150f); set => Set("Economy", "RepairCrewCost", value); }

    // Seconds one repair crew takes to put a flattened building back up; lighter damage is
    // proportionally quicker and a second crew on the same building halves it. The prefab's own
    // repairTime makes this tens of minutes, which reads as a repair that never finishes.
    internal static float RepairSeconds { get => Get("Economy", "RepairSeconds", 240f); set => Set("Economy", "RepairSeconds", value); }

    // Seconds one repair vehicle takes to put up a building; two on site take half as long.
    internal static float ConstructionSeconds { get => Get("Economy", "ConstructionSeconds", 5f); set => Set("Economy", "ConstructionSeconds", value); }

    // Both commanders may only build within this distance of an airbase their faction holds.
    internal static float BuildRadiusKm { get => CommanderMapSettings.Override(CommanderMapSettings.BuildRadiusPrefix) ?? Get("Economy", "BuildRadiusKm", 2.5f); set => Set("Economy", "BuildRadiusKm", value); }

    // A naval dock has to reach the coast, which is usually further out than the base perimeter,
    // so it gets its own (larger) radius instead of loosening the rule for every building. 12 km
    // because 7 was not enough on the duel map: the nearest usable shoreline to a duel base is
    // further out than the stock missions' sea-level objects suggested, so the dock could not be
    // placed at all while standing on the beach.
    internal static float NavalDockRadiusKm { get => CommanderMapSettings.Override(CommanderMapSettings.DockRadiusPrefix) ?? Get("Economy", "NavalDockRadiusKm", 12f); set => Set("Economy", "NavalDockRadiusKm", value); }

    // How far from the water's edge a dock may sit. A shoreline is a band, not a line.
    internal static float NavalDockShoreMeters { get => Get("Economy", "NavalDockShoreMeters", 90f); set => Set("Economy", "NavalDockShoreMeters", value); }

    // Renamed from NavalDockBuildCost to reissue the default at 75 M: BepInEx keeps whatever is
    // already in the config file, so a rename is the only way to change one.
    internal static float NavalDockCost { get => Get("Economy", "NavalDockPrice", 75f); set => Set("Economy", "NavalDockPrice", value); }

    // A vehicle depot starts at level 1; level 2 sells all artillery and anything priced at
    // DepotLevel2MinValue or more (MBTs, the bigger SAMs), for both commanders.
    internal static float DepotUpgradeCost { get => Get("Economy", "DepotUpgradeCost", 300f); set => Set("Economy", "DepotUpgradeCost", value); }

    // Level 3: the ballistic launchers, the StratoLance, the fire-control truck and the SPAAG.
    internal static float DepotLevel3UpgradeCost { get => Get("Economy", "DepotLevel3UpgradeCost", 1000f); set => Set("Economy", "DepotLevel3UpgradeCost", value); }

    // A naval dock has three levels too: level 2, then level 3.
    internal static float NavalDockUpgradeCost { get => Get("Economy", "NavalDockUpgradeCost", 450f); set => Set("Economy", "NavalDockUpgradeCost", value); }

    internal static float NavalDockLevel3UpgradeCost { get => Get("Economy", "NavalDockLevel3UpgradeCost", 900f); set => Set("Economy", "NavalDockLevel3UpgradeCost", value); }

    internal static float DepotLevel2MinValue { get => Get("Economy", "DepotLevel2MinValue", 10f); set => Set("Economy", "DepotLevel2MinValue", value); }

    // Written over the unit's encyclopedia value at load, so every price in the mod follows.
    internal static float MobileArtilleryCost { get => Get("Economy", "MobileArtilleryCost", 100f); set => Set("Economy", "MobileArtilleryCost", value); }

    // What one aircraft parked inside a capture ring is worth. Aircraft carry no capture strength
    // of their own unless they are holding a troop pod, so this is the mod granting it - roughly a
    // light vehicle's worth, so a base still wants a few airframes or a ground squad.
    // Writes every AI bombing run to the BepInEx log: where it released, and where the game's own
    // release rule would have let it release from that height. On by default because it is the only
    // way to see why a bomber closed on a target it could have hit from far out.

    // Renamed from AircraftCaptureStrength to reset the default to 8: BepInEx keeps whatever is in the
    // config file, so a rename is the only way to change one. For scale, one AFV8 APC carries 12.
    internal static float AircraftCaptureStrength { get => CommanderMapSettings.Override(CommanderMapSettings.AircraftCapturePrefix) ?? Get("Gameplay", "LandedAircraftCaptureStrength", 8f); set => Set("Gameplay", "LandedAircraftCaptureStrength", value); }

    // How fast a capture bar nobody is pushing drifts back, per second: 0.01 undoes 25% in 25 s.
    // 0 turns the drift off (the stock game's bars stay where they were left).
    internal static float CaptureRecoveryPerSecond { get => Get("Gameplay", "CaptureRecoveryPerSecond", 0.01f); set => Set("Gameplay", "CaptureRecoveryPerSecond", value); }

    // Supply. Ammunition is the base game's own Rearmer.Capacity, in kilograms; fuel is the mod's
    // own pool on the same buildings and is given a capacity in the same units so one BUY price
    // reads sensibly against the other. A stock rearm building holds 10000 kg of ammunition.
    internal static float SupplyFuelCapacityKg { get => Get("Supply", "FuelCapacityKg", 20000f); set => Set("Supply", "FuelCapacityKg", value); }

    /// <summary>What the player's depots hold when the mission starts. The mission author stocks
    /// every shelf full, which is most of a match's supply handed over at t=0; the commander starts
    /// on a shoestring and has to buy instead. Hostile commanders keep the authored stock.</summary>
    internal static float StartingFuelKg { get => Get("Supply", "StartingFuelKg", 100f); set => Set("Supply", "StartingFuelKg", value); }

    internal static float StartingAmmoKg { get => Get("Supply", "StartingAmmoKg", 100f); set => Set("Supply", "StartingAmmoKg", value); }

    /// <summary>Kilograms of fuel — and, separately, of ordnance — a newly spawned unit draws off
    /// the nearest depots, per point of its own value. Set to 0 to make spawning free again.</summary>
    internal static float SpawnSupplyKgPerValue { get => Get("Supply", "SpawnSupplyKgPerValue", 5f); set => Set("Supply", "SpawnSupplyKgPerValue", value); }

    // One purchase is a quarter of a depot, so filling an empty one is four buys, not one click.
    internal static float SupplyPurchaseFraction { get => Get("Supply", "PurchaseFraction", 0.25f); set => Set("Supply", "PurchaseFraction", value); }

    // Key renamed from FuelPurchaseCost (40) when the price was halved: BepInEx keeps an existing
    // value, so the old key would have kept every player on the old price.
    internal static float SupplyFuelPurchaseCost { get => Get("Supply", "FuelPurchasePrice", 20f); set => Set("Supply", "FuelPurchasePrice", value); }

    internal static float SupplyAmmoPurchaseCost { get => Get("Supply", "AmmoPurchaseCost", 60f); set => Set("Supply", "AmmoPurchaseCost", value); }

    // How far a landed aircraft will reach for a depot. Ammo buildings sit on the field, so this
    // only has to cover an airbase, not the map.
    internal static float SupplyStoreRangeKm { get => Get("Supply", "StoreRangeKm", 5f); set => Set("Supply", "StoreRangeKm", value); }

    // How long an aircraft sits on the deck being serviced before it launches again.
    internal static float AircraftResupplySeconds { get => Get("Supply", "AircraftResupplySeconds", 25f); set => Set("Supply", "AircraftResupplySeconds", value); }

    // Fuel percentage at which a commanded aircraft breaks off and goes for a top-up. The base game
    // sends an AI pilot home at 20 %, which is too late to reach a field that is not next door.
    internal static float AircraftResupplyFuelPercent { get => Get("Supply", "AircraftResupplyFuelPercent", 25f); set => Set("Supply", "AircraftResupplyFuelPercent", value); }

    internal static int SamScanQueriesPerFrame { get => Get("SAM Analyzer", "RaycastsPerFrame", 64); set => Set("SAM Analyzer", "RaycastsPerFrame", value); }

    internal static KeyboardShortcut PrimaryAction { get => GetShortcut("PrimaryAction", KeyCode.Mouse0, "Select units and place world targets."); set => Set("Keybinds", "PrimaryAction", value); }

    internal static KeyboardShortcut SecondaryAction { get => GetShortcut("SecondaryAction", KeyCode.Mouse1, "Issue move orders."); set => Set("Keybinds", "SecondaryAction", value); }

    internal static KeyboardShortcut AddToSelection { get => GetShortcut("AddToSelection", KeyCode.LeftShift, "Hold while selecting to add units."); set => Set("Keybinds", "AddToSelection", value); }

    internal static KeyboardShortcut RepeatDeployment { get => GetShortcut("RepeatDeployment", KeyCode.LeftShift, "Hold while placing a supply target, a building or an air mission area to repeat the deployment."); set => Set("Keybinds", "RepeatDeployment", value); }

    internal static KeyboardShortcut OpenBuild { get => GetShortcut("OpenBuild", KeyCode.B, "Open or close the BUILD window."); set => Set("Keybinds", "OpenBuild", value); }

    internal static KeyboardShortcut OpenGroundUnits { get => GetShortcut("OpenGroundUnits", KeyCode.G, "Select the nearest depot to buy ground units."); set => Set("Keybinds", "OpenGroundUnits", value); }

    internal static KeyboardShortcut OpenAirCommand { get => GetShortcut("OpenAirCommandKey", KeyCode.F, "Open or close AIR COMMAND."); set => Set("Keybinds", "OpenAirCommandKey", value); }

    internal static KeyboardShortcut OpenNaval { get => GetShortcut("OpenNaval", KeyCode.V, "Open or close the ship purchase window."); set => Set("Keybinds", "OpenNaval", value); }

    internal static KeyboardShortcut OpenOrderOfBattle { get => GetShortcut("OpenOrderOfBattle", KeyCode.O, "Open or close the ORDER OF BATTLE window."); set => Set("Keybinds", "OpenOrderOfBattle", value); }

    internal static KeyboardShortcut ToggleTacticalMap { get => GetShortcut("ToggleTacticalMap", KeyCode.J, "Open or close the tactical map."); set => Set("Keybinds", "ToggleTacticalMap", value); }

    internal static KeyboardShortcut GameSpeed1 { get => GetShortcut("GameSpeed1", KeyCode.F5, "Set the game speed to 1x (host only)."); set => Set("Keybinds", "GameSpeed1", value); }

    internal static KeyboardShortcut GameSpeed2 { get => GetShortcut("GameSpeed2", KeyCode.F6, "Set the game speed to 2x (host only)."); set => Set("Keybinds", "GameSpeed2", value); }

    internal static KeyboardShortcut GameSpeed4 { get => GetShortcut("GameSpeed4", KeyCode.F7, "Set the game speed to 4x (host only)."); set => Set("Keybinds", "GameSpeed4", value); }

    internal static KeyboardShortcut CameraCenterFollow { get => GetShortcut("CameraCenterFollow", KeyCode.Space, "Tap to center; hold to center and follow."); set => Set("Keybinds", "CameraCenterFollow", value); }

    internal static KeyboardShortcut ToggleCommander { get => GetShortcut("ToggleCommander", KeyCode.F8, "Enter or leave the Commander view, also while flying; your aircraft holds its course meanwhile."); set => Set("Keybinds", "ToggleCommander", value); }

    internal static KeyboardShortcut ToggleUi { get => GetShortcut("ToggleUi", KeyCode.H, "Cycle visible, RTS UI hidden, and all UI hidden."); set => Set("Keybinds", "ToggleUi", value); }

    internal static KeyboardShortcut CameraForward { get => GetShortcut("CameraForward", KeyCode.W, "Move the RTS camera forward."); set => Set("Keybinds", "CameraForward", value); }

    internal static KeyboardShortcut CameraBackward { get => GetShortcut("CameraBackward", KeyCode.S, "Move the RTS camera backward."); set => Set("Keybinds", "CameraBackward", value); }

    internal static KeyboardShortcut CameraLeft { get => GetShortcut("CameraLeft", KeyCode.A, "Move the RTS camera left."); set => Set("Keybinds", "CameraLeft", value); }

    internal static KeyboardShortcut CameraRight { get => GetShortcut("CameraRight", KeyCode.D, "Move the RTS camera right."); set => Set("Keybinds", "CameraRight", value); }

    internal static KeyboardShortcut CameraUp { get => GetShortcut("CameraUp", KeyCode.Q, "Move the RTS camera upward."); set => Set("Keybinds", "CameraUp", value); }

    internal static KeyboardShortcut CameraDown { get => GetShortcut("CameraDown", KeyCode.E, "Move the RTS camera downward."); set => Set("Keybinds", "CameraDown", value); }

    internal static KeyboardShortcut QueueWaypoint { get => GetShortcut("QueueWaypoint", KeyCode.LeftShift, "Hold while ordering to append a travel point instead of replacing the route."); set => Set("Keybinds", "QueueWaypoint", value); }

    internal static KeyboardShortcut AssignGroupModifier { get => GetShortcut("AssignGroupModifier", KeyCode.LeftControl, "Hold with 1-9 to store the selection as that group."); set => Set("Keybinds", "AssignGroupModifier", value); }

    internal static KeyboardShortcut StopOrder { get => GetShortcut("StopOrder", KeyCode.X, "Cancel RTS orders and hold the selection where it stands."); set => Set("Keybinds", "StopOrder", value); }

    internal static KeyboardShortcut SelectSameType { get => GetShortcut("SelectSameType", KeyCode.LeftControl, "Hold while clicking a unit to select every unit of that type the faction owns."); set => Set("Keybinds", "SelectSameType", value); }

    internal static KeyboardShortcut ClearSelection { get => GetShortcut("ClearSelection", KeyCode.C, "Deselect everything."); set => Set("Keybinds", "ClearSelection", value); }

    internal static KeyboardShortcut RotateBuilding { get => GetShortcut("RotateBuilding", KeyCode.R, "While placing a building, turn it 45 degrees."); set => Set("Keybinds", "RotateBuilding", value); }

    internal static KeyboardShortcut CancelBlueprint { get => GetShortcut("CancelBlueprint", KeyCode.Delete, "Over one of your blueprints, cancel it for a full refund."); set => Set("Keybinds", "CancelBlueprint", value); }

    internal static KeyboardShortcut ToggleNightVision { get => GetShortcut("ToggleNightVision", KeyCode.N, "Night vision on or off in RTS mode (the game's own NVG)."); set => Set("Keybinds", "ToggleNightVision", value); }

    internal static KeyboardShortcut PingKey { get => GetShortcut("Ping", KeyCode.P, "Ping for your team. Tap for a quick ping, hold for the wheel of six kinds, Shift for a permanent marker. On an existing marker it removes it."); set => Set("Keybinds", "Ping", value); }

    internal static KeyboardShortcut CycleIdleUnit { get => GetShortcut("CycleIdleUnit", KeyCode.Period, "Select and jump to the next friendly ground/naval unit with no RTS order."); set => Set("Keybinds", "CycleIdleUnit", value); }

    internal static KeyboardShortcut CameraFreeLook { get => GetShortcut("CameraFreeLook", KeyCode.Mouse2, "Hold while moving the mouse to look around in RTS mode."); set => Set("Keybinds", "CameraFreeLook", value); }

    internal static KeyboardShortcut CameraBoost { get => GetShortcut("CameraBoost", KeyCode.LeftShift, "Hold for faster RTS camera movement."); set => Set("Keybinds", "CameraBoost", value); }

    internal static KeyboardShortcut MapBoxSelect { get => GetShortcut("MapBoxSelect", KeyCode.LeftControl, "Hold while dragging on the map to draw a selection box; a plain drag pans the map."); set => Set("Keybinds", "MapBoxSelect", value); }

    internal static string AirCommandMode { get => Get("Air Command", "MissionMode", "Standby"); set => Set("Air Command", "MissionMode", value); }

    internal static string AirLoadoutBalance { get => Get("Air Command", "LoadoutBalance", "Primary"); set => Set("Air Command", "LoadoutBalance", value); }

    internal static float AirTargetAltitude { get => Get("Air Command", "TargetAltitude", 0f); set => Set("Air Command", "TargetAltitude", value); }

    internal static bool AirGuardTargetOrdnance { get => Get("Air Command", "AirGuardTargetOrdnance", false); set => Set("Air Command", "AirGuardTargetOrdnance", value); }

    internal static bool AirIncludeInternalCannons { get => Get("Air Command", "IncludeInternalCannons", true); set => Set("Air Command", "IncludeInternalCannons", value); }

    internal static float AwacsRadiusKm { get => Get("Air Command", "AwacsRadiusKm", 60f); set => Set("Air Command", "AwacsRadiusKm", value); }

    internal static float CasRadiusKm { get => Get("Air Command", "CasRadiusKm", 20f); set => Set("Air Command", "CasRadiusKm", value); }

    internal static float AirGuardRadiusKm { get => Get("Air Command", "AirGuardRadiusKm", 30f); set => Set("Air Command", "AirGuardRadiusKm", value); }

    internal static float AradRadiusKm { get => Get("Air Command", "AradRadiusKm", 50f); set => Set("Air Command", "AradRadiusKm", value); }

    internal static float StrikeRadiusKm { get => Get("Air Command", "StrikeRadiusKm", 80f); set => Set("Air Command", "StrikeRadiusKm", value); }

    internal static float HybridRadiusKm { get => Get("Air Command", "HybridRadiusKm", 40f); set => Set("Air Command", "HybridRadiusKm", value); }

    /// <summary>Player-saved loadout presets, one per line: aircraft prefab name, preset name, then
    /// one weapon jsonKey per hardpoint, all tab separated. A flat string because BepInEx config has
    /// no list type and a preset list is not worth a save file of its own.</summary>
    internal static string AirLoadoutPresets { get => Get("Air Command", "SavedLoadoutPresets", string.Empty); set => Set("Air Command", "SavedLoadoutPresets", value); }

    // The last army this player took into a Ground Control Draft battle, for the draft screen's LOAD LAST.
    internal static string DraftLastPicks { get => Get("Draft", "LastPicks", string.Empty); set => Set("Draft", "LastPicks", value); }

    internal static void Initialize(ConfigFile configFile)
    {
        config = configFile;
        _ = ModEnabled;
        _ = LimitToFactoryVehicles;
        _ = DisableFriendlyAi;
        _ = UnitBansEnabled;
        _ = UnitBanVoteCount;
        _ = HostBannedUnits;
        _ = KillValueShare;
        _ = ShowCommandButton;
        _ = PrimaryAction;
        _ = SecondaryAction;
        _ = AddToSelection;
        _ = RepeatDeployment;
        _ = OpenBuild;
        _ = OpenGroundUnits;
        _ = OpenAirCommand;
        _ = OpenNaval;
        _ = OpenOrderOfBattle;
        _ = ToggleTacticalMap;
        _ = GameSpeed1;
        _ = GameSpeed2;
        _ = GameSpeed4;
        _ = CameraCenterFollow;
        _ = ToggleCommander;
        _ = ToggleUi;
        _ = CameraForward;
        _ = CameraBackward;
        _ = CameraLeft;
        _ = CameraRight;
        _ = CameraUp;
        _ = CameraDown;
        _ = CameraFreeLook;
        _ = CameraBoost;
        _ = MapBoxSelect;
        _ = QueueWaypoint;
        _ = AssignGroupModifier;
        _ = StopOrder;
        _ = SelectSameType;
        _ = CycleIdleUnit;
        _ = PingKey;
        _ = AutoFollowSelection;
        _ = GroupHotkeys;
        _ = CameraBookmarks;
        _ = OrderFeedback;
        _ = CockpitMarkers;
        _ = RadarExposureRings;
        _ = PingSeconds;
        _ = RetargetAfterKill;
        _ = AttackMoveIntoRange;
        _ = AttackMoveRoutes;
        _ = GuardOrders;
        _ = AutoRetreatDamaged;
        _ = RetreatConditionPercent;
        _ = WaypointHoldSeconds;
        _ = FormationShape;
        _ = FormationCohesionMeters;
        _ = CombatAlerts;
        _ = AlertWindow;
        _ = EnemyCommanderMode;
        _ = EnemyCommanderHumanTeams;
        _ = AiCommanders;
        _ = EnemyDifficulty;
        _ = ForceGodDifficulty;
        _ = TacticalMapSize;
        _ = MapDragSpeed;
        _ = MapZoomSpeed;
        _ = AutoFrameSelection;
        _ = CameraPanSpeed;
        _ = CameraZoomSpeed;
        _ = CameraLookSensitivity;
        _ = CameraSmoothing;
        _ = CameraHeightScaledSpeed;
        _ = CameraEdgeScroll;
        _ = MapJumpHeight;
        _ = MapJumpKeepHeight;
        _ = FollowSmoothing;
        _ = FollowLeadSeconds;
        _ = ShowBuildUi;
        _ = PlainUi;
        _ = AccentColor;
        _ = BackgroundColor;
        _ = EnemyMineIncomeMultiplier;
        _ = FactoryProductReferenceValue;
        _ = FactoryProductionSeconds;
        _ = WarheadProductionSeconds;
        _ = BuildingCostMultiplier;
        _ = DefenceUpkeepPercentPerMinute;
        _ = RepairCrewCost;
        _ = RepairSeconds;
        _ = ConstructionSeconds;
        _ = BuildRadiusKm;
        _ = NavalDockRadiusKm;
        _ = AreaFireDiameterMeters;
        _ = NavalDockShoreMeters;
        _ = NavalDockCost;
        _ = DepotUpgradeCost;
        _ = DepotLevel2MinValue;
        _ = DepotLevel3UpgradeCost;
        _ = NavalDockUpgradeCost;
        _ = NavalDockLevel3UpgradeCost;
        _ = EnrichmentPlantCost;
        _ = MobileArtilleryCost;
        _ = AircraftCaptureStrength;
        _ = CaptureRecoveryPerSecond;
        _ = SupplyFuelCapacityKg;
        _ = StartingFuelKg;
        _ = StartingAmmoKg;
        _ = SpawnSupplyKgPerValue;
        _ = SupplyPurchaseFraction;
        _ = SupplyFuelPurchaseCost;
        _ = SupplyAmmoPurchaseCost;
        _ = SupplyStoreRangeKm;
        _ = AircraftResupplySeconds;
        _ = AircraftResupplyFuelPercent;
        _ = SamScanQueriesPerFrame;
        _ = AirCommandMode;
        _ = AwacsRadiusKm;
        _ = CasRadiusKm;
        _ = AirGuardRadiusKm;
        _ = AradRadiusKm;
        _ = StrikeRadiusKm;
        _ = HybridRadiusKm;
        _ = AirLoadoutPresets;
        _ = DraftLastPicks;
    }

    private static KeyboardShortcut GetShortcut(string key, KeyCode defaultKey, string description)
    {
        if (config == null)
        {
            return new KeyboardShortcut(defaultKey);
        }

        string lookup = "Keybinds/" + key;
        if (Entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            return ((ConfigEntry<KeyboardShortcut>)existing).Value;
        }

        ConfigEntry<KeyboardShortcut> created = config.Bind(
            "Keybinds",
            key,
            new KeyboardShortcut(defaultKey),
            new ConfigDescription(description + " Set the main key to None to disable it."));
        Entries.Add(lookup, created);
        return created.Value;
    }

    private static T Get<T>(string section, string key, T defaultValue)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, defaultValue);
        return entry == null ? defaultValue : entry.Value;
    }

    private static void Set<T>(string section, string key, T value)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, value);
        if (entry != null)
        {
            entry.Value = value;
        }
    }

    private static ConfigEntry<T>? GetEntry<T>(string section, string key, T defaultValue)
    {
        if (config == null)
        {
            return null;
        }

        string lookup = section + "/" + key;
        if (Entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            return (ConfigEntry<T>)existing;
        }

        ConfigEntry<T> created = config.Bind(section, key, defaultValue);
        Entries.Add(lookup, created);
        return created;
    }
}
