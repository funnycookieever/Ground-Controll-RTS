using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal sealed class CommanderMarkerView
{
    private static bool statusSpritesLoaded;
    private static Sprite? ammoStatusSprite;
    private static Sprite? reconStatusSprite;
    private static Material? ammoStatusMaterial;
    private static Material? reconStatusMaterial;
    private static Color ammoStatusColor = Color.white;
    private static Color reconStatusColor = Color.white;

    private readonly Unit unit;
    private readonly Image image;
    private readonly RectTransform rectTransform;
    private readonly Sprite? sprite;
    private readonly Color color;
    private readonly Color selectedColor;
    private readonly float baseScale;
    private readonly bool depotMarker;
    private readonly bool friendly;
    private readonly TrackingInfo? trackingInfo;
    private Image? ammoStatusImage;
    private Image? reconStatusImage;
    private bool visible;

    internal CommanderMarkerView(Unit unit, Image image, FactionHQ localHq)
    {
        this.unit = unit;
        this.image = image;
        rectTransform = (RectTransform)image.transform;
        friendly = CommanderGameAccess.IsFriendlyUnit(unit, localHq);
        trackingInfo = friendly ? null : localHq.GetTrackingData(unit.persistentID);
        sprite = friendly ? CommanderGameAccess.GetFriendlySprite(unit) : CommanderGameAccess.GetHostileSprite(unit);
        color = friendly ? CommanderGameAccess.GetFriendlyColor() : CommanderGameAccess.GetHostileColor(unit);
        selectedColor = friendly ? CommanderGameAccess.GetSelectedFriendlyColor() : CommanderGameAccess.GetSelectedHostileColor(unit);
        depotMarker = CommanderGameAccess.HasFriendlyDepot(unit, unit.NetworkHQ);
        baseScale = CommanderGameAccess.GetBaseScale(unit) * (depotMarker ? 1.25f : 1f);

        this.image.sprite = sprite;
        this.image.color = depotMarker ? Color.Lerp(color, Color.white, 0.25f) : color;
        this.image.raycastTarget = false;
        this.image.enabled = false;
    }

    internal void Update(Camera camera, bool selected)
    {
        float alpha = GetDatalinkAlpha();
        GlobalPosition markerPosition = friendly || trackingInfo == null ? unit.GlobalPosition() : trackingInfo.GetPosition();
        if (sprite == null || alpha <= 0f || !CommanderGameAccess.TryGetWorldMarkerState(markerPosition, camera, out Vector3 screenPosition, out float distanceScale))
        {
            image.enabled = false;
            SetStatusImagesEnabled(false);
            visible = false;
            return;
        }

        rectTransform.position = screenPosition;
        rectTransform.localScale = Vector3.one * (baseScale * distanceScale);
        image.sprite = sprite;
        Color markerColor = selected ? selectedColor : (depotMarker ? Color.Lerp(color, Color.white, 0.25f) : color);
        markerColor.a *= alpha;
        image.color = markerColor;
        image.enabled = true;
        if (friendly)
        {
            UpdateFireControlStatus();
        }
        else
        {
            SetStatusImagesEnabled(false);
        }

        visible = true;
    }

    internal bool TryHit(Vector2 screenPosition, out float hitDistance)
    {
        hitDistance = float.MaxValue;
        if (!visible || !image.enabled)
        {
            return false;
        }

        if (!RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPosition, null))
        {
            return false;
        }

        hitDistance = Vector2.Distance(screenPosition, rectTransform.position);
        return true;
    }

    internal bool TryGetScreenPosition(out Vector2 screenPosition)
    {
        screenPosition = default;
        if (!visible || !image.enabled)
        {
            return false;
        }

        screenPosition = rectTransform.position;
        return true;
    }

    internal bool IsFriendlyMarker()
    {
        return friendly;
    }

    internal void Dispose()
    {
        if (image != null)
        {
            Object.Destroy(image.gameObject);
        }
    }

    private static void EnsureStatusSprites()
    {
        if (statusSpritesLoaded)
        {
            return;
        }

        statusSpritesLoaded = true;
        Image[] images = Resources.FindObjectsOfTypeAll<Image>();
        for (int i = 0; i < images.Length; i++)
        {
            Image candidateImage = images[i];
            Sprite candidateSprite = candidateImage.sprite;
            if (candidateSprite == null)
            {
                continue;
            }

            if (ammoStatusSprite == null && candidateSprite.name == "hudIcon_ammo_friendly")
            {
                ammoStatusSprite = candidateSprite;
                ammoStatusMaterial = candidateImage.material;
                ammoStatusColor = candidateImage.color;
            }
            else if (reconStatusSprite == null && candidateSprite.name == "hudIcon_recon_friendly")
            {
                reconStatusSprite = candidateSprite;
                reconStatusMaterial = candidateImage.material;
                reconStatusColor = candidateImage.color;
            }

            if (ammoStatusSprite != null && reconStatusSprite != null)
            {
                return;
            }
        }

        Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
        for (int i = 0; i < sprites.Length; i++)
        {
            Sprite candidate = sprites[i];
            if (ammoStatusSprite == null && candidate.name == "hudIcon_ammo_friendly")
            {
                ammoStatusSprite = candidate;
            }
            else if (reconStatusSprite == null && candidate.name == "hudIcon_recon_friendly")
            {
                reconStatusSprite = candidate;
            }

            if (ammoStatusSprite != null && reconStatusSprite != null)
            {
                break;
            }
        }
    }

    private float GetDatalinkAlpha()
    {
        if (friendly)
        {
            return 1f;
        }

        if (trackingInfo == null)
        {
            return 0f;
        }

        float timeAfterBaseTracking = Time.timeSinceLevelLoad - trackingInfo.lastSpottedTime - 4f;
        if (timeAfterBaseTracking <= 1f)
        {
            return 1f;
        }

        return 1f - Mathf.Clamp01((timeAfterBaseTracking - 1f) / 3f);
    }

    private void UpdateFireControlStatus()
    {
        bool disabled = CommanderRadarService.IsUnitRadarOffline(unit);
        if (disabled && ammoStatusImage == null)
        {
            EnsureStatusSprites();
            float markerSize = Mathf.Min(rectTransform.rect.width, rectTransform.rect.height);
            if (markerSize <= 0f)
            {
                markerSize = Mathf.Min(rectTransform.sizeDelta.x, rectTransform.sizeDelta.y);
            }

            float statusSize = Mathf.Clamp(markerSize * 0.045f, 1.5f, 3f);
            Vector2 statusPosition = Vector2.zero;
            ammoStatusImage = CreateStatusImage(
                "RadarOfflineAmmoStatus",
                ammoStatusSprite,
                ammoStatusMaterial,
                ammoStatusColor,
                statusPosition,
                statusSize);
            reconStatusImage = CreateStatusImage(
                "RadarOfflineReconStatus",
                reconStatusSprite,
                reconStatusMaterial,
                reconStatusColor,
                statusPosition,
                statusSize);
        }

        SetStatusImagesEnabled(disabled);
    }

    private void SetStatusImagesEnabled(bool enabled)
    {
        if (ammoStatusImage != null)
        {
            ammoStatusImage.enabled = enabled && ammoStatusImage.sprite != null;
        }

        if (reconStatusImage != null)
        {
            reconStatusImage.enabled = enabled && reconStatusImage.sprite != null;
        }
    }

    private Image CreateStatusImage(
        string name,
        Sprite? statusSprite,
        Material? statusMaterial,
        Color statusColor,
        Vector2 anchoredPosition,
        float size)
    {
        GameObject statusObject = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform statusTransform = statusObject.GetComponent<RectTransform>();
        statusTransform.SetParent(rectTransform, false);
        statusTransform.anchorMin = Vector2.one;
        statusTransform.anchorMax = Vector2.one;
        statusTransform.pivot = new Vector2(0.5f, 0.5f);
        statusTransform.anchoredPosition = anchoredPosition;
        statusTransform.sizeDelta = new Vector2(size, size);

        Image statusImage = statusObject.GetComponent<Image>();
        statusImage.sprite = statusSprite;
        statusImage.material = statusMaterial != null ? statusMaterial : image.material;
        statusImage.color = new Color(statusColor.r, statusColor.g, statusColor.b, statusColor.a * 0.55f);
        statusImage.preserveAspect = true;
        statusImage.maskable = image.maskable;
        statusImage.raycastTarget = false;
        return statusImage;
    }
}
