using HarmonyLib;

namespace GroundControlRts;

/// <summary>Fog of war hooks into the game's own spawn and effects.</summary>
internal static class CommanderFogOfWarPatches
{
    /// <summary>A hidden vehicle's wheels kick up no dust: it is a global particle system, not one of its children.</summary>
    [HarmonyPatch(typeof(GroundVehicle), nameof(GroundVehicle.ContactDustParticles))]
    private static class Dust
    {
        private static bool Prefix(GroundVehicle __instance)
        {
            return !CommanderFogOfWarService.IsHidden(__instance);
        }
    }

    /// <summary>A unit is hidden the moment it is registered, so a fresh spawn never shows for a frame.</summary>
    [HarmonyPatch(typeof(UnitRegistry), nameof(UnitRegistry.RegisterUnit))]
    private static class Spawn
    {
        private static void Postfix(Unit unit)
        {
            CommanderFogOfWarService.OnRegistered(unit);
        }
    }
}
