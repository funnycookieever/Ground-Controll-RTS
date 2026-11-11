using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal static class CommanderGameAccess
{
    private static readonly FieldInfo? UnitMarkerField = AccessTools.Field(typeof(CombatHUD), "unitMarker");
    private static readonly FieldInfo? OnFollowingUnitSetField = AccessTools.Field(typeof(CameraStateManager), "onFollowingUnitSet");
    private static readonly FieldInfo? VehicleDepotSpawnTransformField = AccessTools.Field(typeof(VehicleDepot), "spawnTransform");
    private static readonly AccessTools.FieldRef<UnitPart, bool> UnitPartCriticalRef = AccessTools.FieldRefAccess<UnitPart, bool>("criticalPart");
    private static readonly MethodInfo? ServerSetDestinationMethod = AccessTools.Method(typeof(UnitCommand), "ServerSetDestination");
    private static readonly List<VehicleDefinition> RepairVehicleScratch = new();
    private static readonly Dictionary<Faction, VehicleDefinition> RepairVehicleByFaction = new();
    private static bool loggedUntaggedGround;

    /// <summary>
    /// "My faction". While the host is carrying out a guest's request it is the guest's faction,
    /// which is how every service's own rules apply to that guest. See <see cref="CommanderNetwork"/>.
    /// </summary>
    internal static FactionHQ? GetLocalHq()
    {
        return CommanderNetwork.ActingHq ?? (GameManager.GetLocalHQ(out FactionHQ localHq) ? localHq : null);
    }

    internal static FactionHQ? GetPlayerHq()
    {
        return GameManager.GetLocalPlayer(out Player player) && player != null ? player.HQ : null;
    }

    internal static FactionHQ? GetDynamicMapHq()
    {
        return SceneSingleton<DynamicMap>.i?.HQ;
    }

    internal static FactionHQ? GetSupplyHq()
    {
        FactionHQ? localHq = GetLocalHq();
        if (localHq?.VehicleSupply != null && localHq.VehicleSupply.Count > 0)
        {
            return localHq;
        }

        FactionHQ? dynamicMapHq = GetDynamicMapHq();
        if (dynamicMapHq?.VehicleSupply != null && dynamicMapHq.VehicleSupply.Count > 0)
        {
            return dynamicMapHq;
        }

        FactionHQ? playerHq = GetPlayerHq();
        if (playerHq?.VehicleSupply != null && playerHq.VehicleSupply.Count > 0)
        {
            return playerHq;
        }

        return localHq ?? dynamicMapHq ?? playerHq;
    }

    internal static bool IsFriendlyUnit(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || localHq == null || unit.disabled)
        {
            return false;
        }

        FactionHQ? unitHq = unit.NetworkHQ;
        return ReferenceEquals(unitHq, localHq) || ReferenceEquals(unit.MapHQ, localHq);
    }

    internal static bool ShouldTrackUnit(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        // Mission-authored factories are everywhere and would bury the marker layer, but one this
        // commander paid for is a thing you need to click on to upgrade.
        if (unit is Building
            && unit.GetComponent<Factory>() != null
            && !CommanderEconomyService.IsCommanderBuilt(unit))
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return IsCommanderMarkerUnit(unit, localHq);
        }

        if (!IsCommanderMarkerUnit(unit, localHq))
        {
            return false;
        }

        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f;
    }

    /// <summary>
    /// Takes <paramref name="cost"/> from the faction only when it has that much, and says whether
    /// it did. Every enemy-commander purchase goes through this: several of its buy paths priced
    /// against a saved-up fund or a reserve rather than the live balance, and a multiplayer guest
    /// came back from a disconnect to find the team at -90 M.
    /// </summary>
    internal static bool TrySpend(FactionHQ hq, float cost)
    {
        if (cost <= 0f)
        {
            return true;
        }

        if (hq.factionFunds < cost)
        {
            return false;
        }

        hq.AddFunds(-cost);
        return true;
    }

    /// <summary>
    /// Whether this faction can know the unit is there: its own, any building (it stands on the
    /// terrain in plain view), or a mobile contact while it is still being seen. Every hostile
    /// picker (world raycast, map icon) asks this, because a ray or a pick radius also reaches
    /// vehicles nobody has discovered. Buildings were gated on the tracking database too at first,
    /// and a right-click on an enemy base's buildings then fell through to a capture order.
    /// </summary>
    internal static bool IsKnownToFaction(Unit? unit, FactionHQ? localHq, bool includeLostContacts = false)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return true;
        }

        if (unit is Building)
        {
            // Under fog of war a building nobody has seen is not drawn, so it is not pickable either.
            return !CommanderSettings.FogOfWar || CommanderFogOfWarService.HasSeen(unit, localHq);
        }

        // A contact radar has lost is still on the map at its last known position, and it is still
        // a target: an attack order on it closes on where it was seen and fires once it is found.
        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && (includeLostContacts || Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f);
    }

    internal static bool ShouldAllowCommanderSelection(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return IsCommanderMarkerUnit(unit, localHq);
        }

        if (!IsCommanderMarkerUnit(unit, localHq))
        {
            return false;
        }

        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f;
    }

    internal static bool ShouldRetainCommanderMarker(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return true;
        }

        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f;
    }

    internal static bool HasFriendlyDepot(Unit? unit, FactionHQ? localHq)
    {
        return GetFriendlyDepotFromUnit(unit, localHq) != null;
    }

    /// <summary>
    /// The hangar or landing pad component on a building, if it is one. A pad and a hangar are the
    /// same <c>Hangar</c> component in this game; what separates them is the airframes each one
    /// lists, which is why "helicopters only at the pad" needs no aircraft-type test anywhere.
    /// </summary>
    internal static Hangar? GetHangar(Unit? unit)
    {
        if (unit == null || unit is not Building)
        {
            return null;
        }

        Hangar[] hangars = unit.GetComponentsInChildren<Hangar>(true);
        Hangar? first = null;
        for (int i = 0; i < hangars.Length; i++)
        {
            if (hangars[i] == null || hangars[i].attachedUnit == null)
            {
                continue;
            }

            if (hangars[i].Available)
            {
                return hangars[i];
            }

            first ??= hangars[i];
        }

        return first;
    }

    /// <summary>True when every airframe this set of doors holds is a helicopter or tiltwing,
    /// i.e. it is a landing pad rather than a hangar. Purely a label; the aircraft list itself is
    /// the hangar's own either way.</summary>
    internal static bool IsRotaryHangar(Hangar? hangar)
    {
        if (hangar == null)
        {
            return false;
        }

        AircraftDefinition[] available = hangar.GetAvailableAircraft();
        bool any = false;
        for (int i = 0; i < available.Length; i++)
        {
            if (available[i] == null)
            {
                continue;
            }

            if (!CommanderAirCommandService.IsRotaryAirframe(available[i]))
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    internal static Transform? GetMarkerParent()
    {
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        if (gameplayUi?.gameplayCanvas != null)
        {
            return gameplayUi.gameplayCanvas.transform;
        }

        return gameplayUi != null ? gameplayUi.transform : null;
    }

    internal static Image? CreateMarkerImage(Transform parent)
    {
        CombatHUD? combatHud = SceneSingleton<CombatHUD>.i;
        GameObject? prefab = combatHud != null ? UnitMarkerField?.GetValue(combatHud) as GameObject : null;
        GameObject markerObject;

        if (prefab != null)
        {
            markerObject = Object.Instantiate(prefab, parent);
        }
        else
        {
            markerObject = new GameObject("CommanderMarker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            markerObject.transform.SetParent(parent, false);
        }

        Image? image = markerObject.GetComponent<Image>();
        RectTransform? rectTransform = markerObject.transform as RectTransform;
        if (image == null || rectTransform == null)
        {
            Object.Destroy(markerObject);
            return null;
        }

        rectTransform.SetParent(parent, false);
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.zero;
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        image.raycastTarget = false;
        image.enabled = false;
        return image;
    }

    internal static Sprite? GetFriendlySprite(Unit unit)
    {
        return unit.definition != null && unit.definition.friendlyIcon != null
            ? unit.definition.friendlyIcon
            : GameAssets.i != null
                ? GameAssets.i.targetUnitSpriteFriendly
                : null;
    }

    internal static Sprite? GetHostileSprite(Unit unit)
    {
        return unit.definition != null && unit.definition.hostileIcon != null
            ? unit.definition.hostileIcon
            : GameAssets.i != null
                ? GameAssets.i.targetUnitSprite
                : null;
    }

    internal static Color GetFriendlyColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDFriendly : Color.green;
    }

    internal static Color GetSelectedFriendlyColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDFriendlySelected : Color.cyan;
    }

    internal static Color GetHostileColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDHostile : Color.red;
    }

    internal static Color GetSelectedHostileColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDHostileSelected : new Color(1f, 0.45f, 0.25f);
    }

    internal static float GetBaseScale(Unit unit)
    {
        float iconSize = unit.definition != null && unit.definition.iconSize > 0f ? unit.definition.iconSize : 1f;
        float userScale = PlayerSettings.hmdIconSize > 0f ? PlayerSettings.hmdIconSize : 1f;
        return iconSize * userScale;
    }

    internal static bool TryGetWorldMarkerState(Unit unit, Camera camera, out Vector3 screenPosition, out float scale)
    {
        return TryGetWorldMarkerState(unit.GlobalPosition(), camera, out screenPosition, out scale);
    }

    internal static bool TryGetWorldMarkerState(GlobalPosition unitPosition, Camera camera, out Vector3 screenPosition, out float scale)
    {
        screenPosition = default;
        scale = 1f;
        Vector3 worldPosition = GlobalPositionExtensions.ToLocalPosition(unitPosition);
        Vector3 cameraPosition = camera.transform.position;
        Vector3 direction = worldPosition - cameraPosition;
        if (Vector3.Dot(direction, camera.transform.forward) <= 0f)
        {
            return false;
        }

        Vector3 worldToScreen = camera.WorldToScreenPoint(worldPosition);
        if (worldToScreen.z <= 0f)
        {
            return false;
        }

        if (worldToScreen.x < 0f || worldToScreen.x > Screen.width || worldToScreen.y < 0f || worldToScreen.y > Screen.height)
        {
            return false;
        }

        float distance = Vector3.Distance(cameraPosition, worldPosition);
        float distanceFactor = Mathf.Clamp01((distance * 0.00004f) - 0.5f);
        scale = Mathf.Lerp(1f, 0.45f, distanceFactor);
        screenPosition = new Vector3(worldToScreen.x, worldToScreen.y, 0f);
        return true;
    }

    internal static bool TryRaycastSelectableUnit(Vector2 screenPosition, out Unit unit)
    {
        unit = null!;

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        RaycastHit[] hits = Physics.RaycastAll(ray, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0)
        {
            return false;
        }

        System.Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
        FactionHQ? localHq = GetLocalHq();
        for (int i = 0; i < hits.Length; i++)
        {
            VehicleDepot? hitDepot = hits[i].collider.GetComponentInParent<VehicleDepot>();
            if (hitDepot != null)
            {
                Unit? depotOwner = GetDepotOwner(hitDepot);
                if (ShouldAllowCommanderSelection(depotOwner, localHq))
                {
                    unit = depotOwner!;
                    return true;
                }
            }

            Unit? hitUnit = hits[i].collider.GetComponentInParent<Unit>();
            if (hitUnit == null)
            {
                hitUnit = hits[i].collider.GetComponentInParent<UnitPart>()?.parentUnit;
            }

            hitUnit = CommanderSamSiteCoreRegistry.ResolveSelection(hitUnit);
            if (ShouldAllowCommanderSelection(hitUnit, localHq))
            {
                unit = hitUnit!;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Any hostile unit under the cursor, including structures and objectives that are not
    /// eligible for Commander selection. Used to turn a right click into an attack order.
    /// </summary>
    internal static bool TryRaycastHostileUnit(Vector2 screenPosition, out Unit unit)
    {
        unit = null!;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        FactionHQ? localHq = GetLocalHq();
        if (camera == null || localHq == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        RaycastHit[] hits = Physics.RaycastAll(ray, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0)
        {
            return false;
        }

        System.Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            Unit? hitUnit = hits[i].collider.GetComponentInParent<Unit>()
                ?? hits[i].collider.GetComponentInParent<UnitPart>()?.parentUnit;
            if (hitUnit == null || hitUnit.disabled || hitUnit.NetworkHQ == null)
            {
                continue;
            }

            if (!IsFriendlyUnit(hitUnit, localHq) && IsKnownToFaction(hitUnit, localHq))
            {
                unit = hitUnit;
                return true;
            }
        }

        return false;
    }

    internal static bool TryRaycastWorldPosition(Vector2 screenPosition, out GlobalPosition position)
    {
        position = default;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        position = GlobalPositionExtensions.ToGlobalPosition(hit.point);
        return true;
    }

    /// <summary>
    /// Drops a position onto the terrain surface, probing a small ring of offsets so a point that
    /// lands inside a building or on water still finds ground nearby. Only hits carrying the
    /// terrain material count: a roof is not ground.
    /// </summary>
    internal static GlobalPosition SnapToTerrain(GlobalPosition target)
    {
        Vector2[] offsets =
        {
            Vector2.zero,
            new(3f, 0f),
            new(-3f, 0f),
            new(0f, 3f),
            new(0f, -3f),
            new(6f, 0f),
            new(-6f, 0f),
            new(0f, 6f),
            new(0f, -6f),
            new(8f, 8f),
            new(-8f, 8f),
            new(8f, -8f),
            new(-8f, -8f),
        };
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 local = new GlobalPosition(
                target.x + offsets[i].x,
                target.y,
                target.z + offsets[i].y).ToLocalPosition();
            Vector3 origin = new(local.x, Datum.LocalSeaY + 10000f, local.z);
            if (GameAssets.i == null)
            {
                continue;
            }

            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                Vector3.down,
                20000f,
                PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore);
            float highestTerrainY = float.MinValue;
            Vector3 terrainPoint = default;
            for (int hitIndex = 0; hitIndex < hits.Length; hitIndex++)
            {
                RaycastHit hit = hits[hitIndex];
                if (hit.collider != null
                    && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial
                    && hit.point.y > highestTerrainY)
                {
                    highestTerrainY = hit.point.y;
                    terrainPoint = hit.point;
                }
            }

            if (highestTerrainY > float.MinValue)
            {
                return terrainPoint.ToGlobalPosition();
            }
        }

        if (TryFindUntaggedGround(target, out GlobalPosition ground))
        {
            return ground;
        }

        // No terrain under any probe means open sea with no seabed collider (custom maps). Handing
        // back the caller's own height made every IsBelowSeaLevel test read that as dry land at the
        // anchor's altitude, and the enemy commander built income buildings and radars out in the
        // bay. Below the sea plane, the point reads as water everywhere.
        return new GlobalPosition(target.x, Mathf.Min(target.y, -1f), target.z);
    }

    internal static bool TryRaycastWaterPosition(Vector2 screenPosition, out GlobalPosition position)
    {
        position = default;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        if (!Datum.WaterPlane().Raycast(ray, out float distance) || distance < 0f || distance > 500000f)
        {
            return false;
        }

        position = GlobalPositionExtensions.ToGlobalPosition(ray.GetPoint(distance));
        return true;
    }

    /// <summary>
    /// Puts an aircraft somewhere else in one piece: the airframe and every jointed part with it.
    /// </summary>
    /// <remarks>
    /// An aircraft is not one body. <c>AeroPart.CreateRB</c> detaches each jointed part (wings, tail,
    /// control surfaces) with <c>SetParent(null)</c> and gives it its own rigidbody on a breakable
    /// joint. Moving only <c>aircraft.transform</c> snaps the joints and destroys the airframe, so every
    /// body moves by the same rigid transform and gets the same velocity.
    /// <paramref name="velocity"/> is relative to the ship deck under it, if any, so aircraft on a
    /// moving carrier keep up with the deck.
    /// </remarks>
    internal static void MoveAircraft(Aircraft aircraft, Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        if (Physics.Raycast(position + Vector3.up, Vector3.down, out RaycastHit deck, 20f, PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore)
            && deck.rigidbody != null)
        {
            velocity += deck.rigidbody.GetPointVelocity(deck.point);
        }

        Transform root = aircraft.transform;
        Vector3 oldPosition = root.position;
        Quaternion turn = rotation * Quaternion.Inverse(root.rotation);
        List<UnitPart> parts = aircraft.partLookup;
        for (int i = 0; parts != null && i < parts.Count; i++)
        {
            UnitPart part = parts[i];
            Rigidbody? body = part == null ? null : part.rb;
            if (body == null || body == aircraft.rb || part!.IsDetached())
            {
                continue;
            }

            Vector3 partPosition = position + (turn * (body.position - oldPosition));
            Quaternion partRotation = turn * body.rotation;
            part.transform.SetPositionAndRotation(partPosition, partRotation);
            body.position = partPosition;
            body.rotation = partRotation;
            body.velocity = velocity;
            body.angularVelocity = Vector3.zero;
        }

        root.SetPositionAndRotation(position, rotation);
        if (aircraft.rb != null)
        {
            aircraft.rb.position = position;
            aircraft.rb.rotation = rotation;
            aircraft.rb.velocity = velocity;
            aircraft.rb.angularVelocity = Vector3.zero;
        }
    }

    /// <summary>
    /// A land base: not disabled and not a carrier or frigate deck (an airbase attached to a unit).
    /// Anything the commanders build or park "at a base" needs this, because a ship sails away from
    /// what was put beside it and the base reads as uncovered again: 14 depots and 14 radars in one
    /// Carrier Duel.
    /// </summary>
    internal static bool IsLandBase(Airbase? airbase)
    {
        return airbase != null && !airbase.disabled && airbase.center != null && !airbase.TryGetAttachedUnit(out _);
    }

    /// <summary>
    /// True when a global position is under water. Sea level in the <see cref="GlobalPosition"/>
    /// frame is a flat <c>y = 0</c> (<c>Datum.SeaLevel</c>); <c>Datum.LocalSeaY</c> is the same
    /// plane expressed in the *local* frame, and the floating origin shifts that on every axis
    /// including y once the camera climbs past 1024 m. Comparing a global y against
    /// <c>LocalSeaY</c> therefore reads "underwater" as "below the camera", which is how the naval
    /// dock's shoreline probes stopped finding water the moment the RTS camera gained any altitude.
    /// </summary>
    internal static bool IsBelowSeaLevel(GlobalPosition position)
    {
        return position.y < 0f;
    }

    /// <summary>
    /// True when the first static surface under <paramref name="localPoint"/> (a local-frame point
    /// within a few metres of the ground) is bare terrain, which is how <c>LandingGear</c> tells
    /// dirt from tarmac. Pavement, taxiways, runways and hangar floors are their own meshes.
    /// </summary>
    internal static bool IsDirtUnder(Vector3 localPoint)
    {
        return GameAssets.i != null
            && Physics.Raycast(localPoint + (Vector3.up * 3f), Vector3.down, out RaycastHit hit, 15f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
            && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial;
    }

    internal static bool ShouldAllowCommanderMove(Unit? unit)
    {
        FactionHQ? localHq = GetLocalHq();
        return IsFriendlyUnit(unit, localHq)
            && (unit is GroundVehicle || unit is Ship)
            && !CommanderMobileEmplacementService.IsReservedHauler(unit)
            && !CommanderSamSiteService.IsReservedConstructionJacknife(unit);
    }

    internal static bool IsFriendlyDepot(VehicleDepot? depot)
    {
        return IsFriendlyDepot(depot, GetLocalHq());
    }

    internal static bool IsFriendlyDepot(VehicleDepot? depot, FactionHQ? localHq)
    {
        return depot != null && IsFriendlyUnit(GetDepotOwner(depot), localHq);
    }

    internal static Unit? GetDepotOwner(VehicleDepot depot)
    {
        return depot.GetComponentInParent<Unit>();
    }

    internal static VehicleDepot? GetFriendlyDepotFromUnit(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null)
        {
            return null;
        }

        if (unit is VehicleDepot directDepot && IsFriendlyDepot(directDepot, localHq))
        {
            return directDepot;
        }

        VehicleDepot? childDepot = unit.GetComponentInChildren<VehicleDepot>();
        return IsFriendlyDepot(childDepot, localHq) ? childDepot : null;
    }

    internal static UnitCommand? GetUnitCommand(Unit unit)
    {
        return unit switch
        {
            GroundVehicle groundVehicle => groundVehicle.UnitCommand,
            Ship ship => ship.UnitCommand,
            _ => null,
        };
    }

    /// <summary>
    /// <c>UnitCommand.SetDestination</c>, except that a guest's order goes to the host in the
    /// frame's batch instead of as its own rate-limited RPC. See
    /// <see cref="CommanderNetwork.TryQueueDestination"/>.
    /// </summary>
    internal static bool SendDestination(Unit unit, GlobalPosition destination, bool playerCommand)
    {
        UnitCommand? unitCommand = GetUnitCommand(unit);
        if (unitCommand == null)
        {
            return false;
        }

        if (!CommanderNetwork.TryQueueDestination(unit, destination, playerCommand))
        {
            unitCommand.SetDestination(destination, playerCommand);
        }

        return true;
    }

    /// <summary>
    /// The host's half of <see cref="SendDestination"/>: the server-side call the game's own RPC
    /// ends in, filed under the guest who gave the order (null for a non-player command).
    /// </summary>
    internal static void SetDestinationOnServer(Unit unit, GlobalPosition destination, Player? player)
    {
        UnitCommand? unitCommand = GetUnitCommand(unit);
        if (unitCommand == null || unit.disabled || ServerSetDestinationMethod == null)
        {
            return;
        }

        ServerSetDestinationMethod.Invoke(unitCommand, new object?[] { destination, player });
        if (player != null && unit is GroundVehicle vehicle)
        {
            CommanderRepairService.KeepAtCommandedDestination(vehicle);
        }
        else if (player != null && unit is Ship ship)
        {
            // See TrySetDestination.
            ship.SetHoldPosition(true);
        }
    }

    internal static bool SetUnitHoldPosition(Unit unit, bool hold)
    {
        switch (unit)
        {
            case GroundVehicle groundVehicle:
                groundVehicle.SetHoldPosition(hold);
                return true;
            case Ship ship:
                ship.SetHoldPosition(hold);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryGetCurrentCommandPosition(Unit unit, out GlobalPosition position)
    {
        position = default;
        UnitCommand? unitCommand = GetUnitCommand(unit);
        if (unitCommand == null)
        {
            return false;
        }

        UnitCommand.Command command = unitCommand.GetCommandCached();
        position = command.position;
        return command.time > 0f || command.player != null || !command.position.Equals(default);
    }

    internal static bool TrySetDestination(Unit unit, GlobalPosition destination)
    {
        if (!SendDestination(unit, destination, playerCommand: true))
        {
            return false;
        }

        // Some prefabs (the Jacknife, supply trucks) are authored to drop a player order on arrival,
        // after which CheckObstacles drives them at the enemy: a munitions truck left its rally point.
        if (unit is GroundVehicle vehicle)
        {
            CommanderRepairService.KeepAtCommandedDestination(vehicle);
        }

        // ShipAI forgets a player order 120 s after arriving and sails at the nearest enemy.
        // holdPosition only gates its target choice, steering still follows orders; RESUME AI
        // clears it. Local side only: an AI naval wave is meant to go hunting at its lane.
        if (unit is Ship ship && ship.NetworkHQ != null && ship.NetworkHQ == GetLocalHq())
        {
            ship.SetHoldPosition(true);
        }

        return true;
    }

    internal static void CollectFriendlySurfaceUnits(List<Unit> units)
    {
        units.Clear();
        FactionHQ? localHq = GetLocalHq();
        if (localHq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID unitId in localHq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit) || !ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            units.Add(unit);
        }
    }

    internal static void TeleportGroundVehicle(GroundVehicle vehicle, GlobalPosition target)
    {
        GlobalPosition snappedTarget = SnapToTerrain(target);
        Vector3 local = snappedTarget.ToLocalPosition();
        Quaternion rotation = vehicle.transform.rotation;
        Vector3 origin = local + (Vector3.up * 100f);
        if (Physics.Raycast(
                origin,
                Vector3.down,
                out RaycastHit hit,
                200f,
                PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore)
            && hit.collider != null
            && GameAssets.i != null
            && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial)
        {
            Vector3 forward = Vector3.ProjectOnPlane(vehicle.transform.forward, hit.normal);
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.ProjectOnPlane(Vector3.forward, hit.normal);
            }

            rotation = Quaternion.LookRotation(forward.normalized, hit.normal);
            local = hit.point + (rotation * vehicle.definition.spawnOffset);
        }
        else
        {
            local += rotation * vehicle.definition.spawnOffset;
        }

        vehicle.transform.SetPositionAndRotation(local, rotation);
        if (vehicle.rb != null)
        {
            vehicle.rb.position = local;
            vehicle.rb.rotation = rotation;
            vehicle.rb.velocity = Vector3.zero;
            vehicle.rb.angularVelocity = Vector3.zero;
        }
    }

    internal static Transform? GetDepotSpawnTransform(VehicleDepot depot)
    {
        return VehicleDepotSpawnTransformField?.GetValue(depot) as Transform;
    }

    internal static Vector3 GetDepotSpawnPosition(VehicleDepot depot)
    {
        Transform? spawnTransform = GetDepotSpawnTransform(depot);
        return spawnTransform != null ? spawnTransform.position : depot.transform.position;
    }

    /// <summary>
    /// Condition 0-1, taken from the worst-off critical part on the unit. Parts start at 100 hit
    /// points, and Basegame only disables a unit when a critical part reaches 0 — non-critical
    /// parts (control surfaces, pylons, ship turrets) can be shot off and the unit carries on.
    /// </summary>
    internal static float GetUnitCondition(Unit? unit)
    {
        if (unit == null || unit.disabled)
        {
            return 0f;
        }

        float worst = 1f;
        List<DamageablePart> parts = unit.damageables;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Damageable is not UnitPart part || !UnitPartCriticalRef(part))
            {
                continue;
            }

            if (parts[i].Removed)
            {
                return 0f;
            }

            worst = Mathf.Min(worst, Mathf.Clamp01(part.hitPoints * 0.01f));
        }

        return worst;
    }

    /// <summary>Ammo 0-1 across every weapon station, or -1 when the unit carries no weapons.</summary>
    internal static float GetUnitAmmo(Unit? unit)
    {
        if (unit == null || unit.disabled || unit.weaponStations == null || unit.weaponStations.Count == 0)
        {
            return -1f;
        }

        return Mathf.Clamp01(unit.GetAmmoLevel());
    }

    /// <summary>
    /// Fuel 0-1, or -1 for anything that carries no fuel tanks (every ground vehicle and ship —
    /// only <see cref="Aircraft"/> burns fuel in this game). Tanks only drain on the client that
    /// simulates the airframe, so on a pure multiplayer client a remote aircraft reads its spawn
    /// level rather than a live one.
    /// </summary>
    internal static float GetUnitFuel(Unit? unit)
    {
        if (unit is not Aircraft aircraft || aircraft.disabled)
        {
            return -1f;
        }

        return Mathf.Clamp01(aircraft.GetFuelLevel());
    }

    internal static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    internal static bool ApproximatelyEqual(GlobalPosition a, GlobalPosition b, float toleranceMeters)
    {
        return HorizontalDistance(a.AsVector3(), b.AsVector3()) <= toleranceMeters;
    }

    internal static bool IsSpawnableVehicleDefinition(VehicleDefinition? definition)
    {
        if (definition?.unitPrefab == null)
        {
            return false;
        }

        return definition.unitPrefab.GetComponent<GroundVehicle>() != null;
    }

    internal static bool TryGetLocalVehicleDefinitions(List<VehicleDefinition> buffer)
    {
        if (buffer == null)
        {
            return false;
        }

        buffer.Clear();

        VehicleDefinition[] allDefinitions = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
        for (int i = 0; i < allDefinitions.Length; i++)
        {
            VehicleDefinition definition = allDefinitions[i];
            if (IsSpawnableVehicleDefinition(definition) && !buffer.Contains(definition))
            {
                buffer.Add(definition);
            }
        }

        return buffer.Count > 0;
    }

    internal static bool TryGetLocalProductionVehicleDefinitions(List<VehicleDefinition> buffer)
    {
        if (buffer == null)
        {
            return false;
        }

        buffer.Clear();
        FactionHQ? hq = GetSupplyHq();
        if (hq == null)
        {
            return false;
        }

        Factory[] factories = Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            if (factory == null
                || factory.attachedUnit == null
                || !IsFriendlyUnit(factory.attachedUnit, hq)
                || factory.ProductionUnit is not VehicleDefinition definition
                || !IsSpawnableVehicleDefinition(definition)
                || buffer.Contains(definition))
            {
                continue;
            }

            buffer.Add(definition);
        }

        return buffer.Count > 0;
    }

    internal static void CollectFactionVehicleDefinitions(List<VehicleDefinition> buffer)
    {
        CollectFactionVehicleDefinitions(buffer, GetSupplyHq());
    }

    /// <summary>The ground units <paramref name="hq"/>'s faction fields, from its convoy groups.</summary>
    internal static void CollectFactionVehicleDefinitions(List<VehicleDefinition> buffer, FactionHQ? hq)
    {
        buffer.Clear();
        if (hq?.faction == null)
        {
            return;
        }

        List<Faction.ConvoyGroup> convoyGroups = hq.faction.GetConvoyGroups();
        for (int groupIndex = 0; groupIndex < convoyGroups.Count; groupIndex++)
        {
            List<Faction.ConvoyUnit> constituents = convoyGroups[groupIndex].Constituents;
            for (int unitIndex = 0; unitIndex < constituents.Count; unitIndex++)
            {
                if (constituents[unitIndex].Type is VehicleDefinition definition
                    && IsSpawnableVehicleDefinition(definition)
                    && !buffer.Contains(definition))
                {
                    buffer.Add(definition);
                }
            }
        }
    }

    /// <summary>
    /// The faction's repair vehicle — the Jacknife on every stock faction. Matched by name first
    /// and by an actual <see cref="Repairer"/> component second, because a faction that renames
    /// the truck still has to carry the component for the Basegame repair loop to work at all.
    /// </summary>
    internal static VehicleDefinition? FindRepairVehicleDefinition(FactionHQ? hq = null)
    {
        // Cached per faction: the fallback below scans every vehicle asset in the game and walks
        // prefabs, and the enemy's builder buy and the starting builders ask this every review.
        // Definitions outlive the scene, so the answer never goes stale.
        hq ??= GetSupplyHq();
        Faction? faction = hq?.faction;
        if (faction != null && RepairVehicleByFaction.TryGetValue(faction, out VehicleDefinition? cached))
        {
            return cached;
        }

        VehicleDefinition? found = FindRepairVehicleDefinitionUncached(hq);
        if (faction != null && found != null)
        {
            RepairVehicleByFaction[faction] = found;
        }

        return found;
    }

    internal static string GetUnitLabel(Unit? unit)
    {
        if (unit == null)
        {
            return "Unknown unit";
        }

        // A mission's own name for a unit is shown only when it reads as a name: authoring IDs such
        // as "pala_VehicleDepot1_1" or "<MAP_UNIT>++revetment1" say less than the unit's type.
        if (!string.IsNullOrWhiteSpace(unit.UniqueName)
            && unit.UniqueName.IndexOf('_') < 0
            && unit.UniqueName.IndexOf('<') < 0)
        {
            return unit.UniqueName;
        }

        if (!string.IsNullOrWhiteSpace(unit.unitName))
        {
            // A human-flown aircraft keeps its plain name: call signs are for the AI's airframes.
            bool callSign = unit is Ship || (unit is Aircraft aircraft && aircraft.Player == null);
            string sign = callSign ? CommanderCallSigns.Get(unit) : string.Empty;
            return sign.Length > 0 ? $"{unit.unitName} {sign}" : unit.unitName;
        }

        return unit.name;
    }

    /// <summary>The unit that dealt the most of a <c>Unit.damageCredit</c> table.</summary>
    internal static PersistentID GetTopDealer(Dictionary<PersistentID, float>? credit)
    {
        PersistentID worst = PersistentID.None;
        float worstAmount = 0.01f;
        if (credit != null)
        {
            foreach (KeyValuePair<PersistentID, float> entry in credit)
            {
                if (entry.Value > worstAmount)
                {
                    worst = entry.Key;
                    worstAmount = entry.Value;
                }
            }
        }

        return worst;
    }

    internal static string GetVehicleLabel(UnitDefinition? definition)
    {
        if (definition == null)
        {
            return "Unknown vehicle";
        }

        if (!string.IsNullOrWhiteSpace(definition.unitName))
        {
            return definition.unitName;
        }

        if (!string.IsNullOrWhiteSpace(definition.code))
        {
            return definition.code;
        }

        return definition.name;
    }

    internal static string GetVehicleCategoryLabel(VehicleDefinition? definition)
    {
        if (definition == null)
        {
            return "Other";
        }

        if (IsUncrewedGroundVehicleDefinition(definition))
        {
            return "UGV";
        }

        if (IsTrailerVehicleDefinition(definition))
        {
            return "Trailer";
        }

        return definition.vehicleType switch
        {
            VehicleType.TRUCK => "Truck",
            VehicleType.UGV => "UGV",
            VehicleType.LCV => "Light Vehicle",
            VehicleType.AFV => "AFV",
            VehicleType.MBT => "MBT",
            VehicleType.ART => "Artillery",
            VehicleType.AAA => "AAA",
            VehicleType.IR_SAM => "IR SAM",
            VehicleType.R_SAM => "Radar SAM",
            VehicleType.RDR => "Radar",
            _ => "Other",
        };
    }

    internal static bool IsTrailerVehicleDefinition(VehicleDefinition? definition)
    {
        return definition != null
            && definition.manpower == 0
            && !IsUncrewedGroundVehicleDefinition(definition);
    }

    internal static void RaiseFollowingUnitSet(Unit? unit)
    {
        if (OnFollowingUnitSetField == null)
        {
            return;
        }

        object? eventOwner = OnFollowingUnitSetField.IsStatic ? null : SceneSingleton<CameraStateManager>.i;
        System.Action<Unit>? callback = OnFollowingUnitSetField.GetValue(eventOwner) as System.Action<Unit>;
        callback?.Invoke(unit!);
    }

    private static bool IsCommanderMarkerUnit(Unit unit, FactionHQ localHq)
    {
        if (unit is GroundVehicle or Ship or Aircraft or Missile)
        {
            return true;
        }

        if (HasFriendlyDepot(unit, localHq)
            || CommanderSamSiteCoreRegistry.IsTrackedSiteUnit(unit)
            || CommanderEconomyService.IsCommanderBuilt(unit)
            || GetHangar(unit) != null)
        {
            return true;
        }

        if (unit is not Building || unit.GetComponent<Factory>() != null)
        {
            return false;
        }

        return unit.weaponStations.Count > 0
            || unit.GetComponentInChildren<Rearmer>(true) != null;
    }

    private static VehicleDefinition? FindRepairVehicleDefinitionUncached(FactionHQ? hq)
    {
        CollectFactionVehicleDefinitions(RepairVehicleScratch, hq);
        VehicleDefinition? repairer = FindRepairer(RepairVehicleScratch);

        // A faction's convoy groups are the formations it sends out, and the stock factions put no
        // repair truck in any of them, so this came back empty on Boscali and Champions handed out
        // no starting builders at all. The depot's BUY list is every vehicle in the game, so fall
        // back to that rather than leave a faction with no builder.
        if (repairer == null && TryGetLocalVehicleDefinitions(RepairVehicleScratch))
        {
            repairer = FindRepairer(RepairVehicleScratch);
        }

        return repairer;
    }

    private static VehicleDefinition? FindRepairer(List<VehicleDefinition> definitions)
    {
        for (int i = 0; i < definitions.Count; i++)
        {
            VehicleDefinition definition = definitions[i];
            if (definition?.unitPrefab == null)
            {
                continue;
            }

            string identity = $"{definition.unitName} {definition.code} {definition.jsonKey}";
            if (identity.IndexOf("jacknife", System.StringComparison.OrdinalIgnoreCase) >= 0
                || identity.IndexOf("jackknife", System.StringComparison.OrdinalIgnoreCase) >= 0
                || definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null)
            {
                return definition;
            }
        }

        return null;
    }

    private static bool IsUncrewedGroundVehicleDefinition(VehicleDefinition definition)
    {
        return definition.vehicleType == VehicleType.UGV
            || (!string.IsNullOrWhiteSpace(definition.code)
                && definition.code.StartsWith("UGV", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ground a custom map built without the game's terrain material or off the Statics layer. Such a
    /// landing gear still rolls on it, because the gear's linecast takes any layer, but the sea-level
    /// answer put a built hangar's floor under the land and its aircraft came out buried. The lowest
    /// surface above the sea that is not a unit, so a tree or a roof is not taken for the ground.
    /// </summary>
    private static bool TryFindUntaggedGround(GlobalPosition target, out GlobalPosition ground)
    {
        ground = default;
        Vector3 local = target.ToLocalPosition();
        RaycastHit[] hits = Physics.RaycastAll(
            new Vector3(local.x, Datum.LocalSeaY + 10000f, local.z),
            Vector3.down,
            20000f,
            ~((int)PhysicsLayers.ExclusionZonesMask | (int)PhysicsLayers.IgnoreCollisionsMask | (int)PhysicsLayers.WaterMask),
            QueryTriggerInteraction.Ignore);
        float lowest = float.MaxValue;
        Collider? found = null;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.point.y > Datum.LocalSeaY
                && hit.point.y < lowest
                && hit.collider.GetComponentInParent<Unit>() == null)
            {
                lowest = hit.point.y;
                found = hit.collider;
            }
        }

        if (found == null)
        {
            return false;
        }

        if (!loggedUntaggedGround)
        {
            loggedUntaggedGround = true;
            CommanderPlugin.Log.LogInfo(
                $"Ground without the game's terrain material: {found.name}, layer {found.gameObject.layer}, material {(found.sharedMaterial != null ? found.sharedMaterial.name : "none")}.");
        }

        ground = new Vector3(local.x, lowest, local.z).ToGlobalPosition();
        return true;
    }
}
