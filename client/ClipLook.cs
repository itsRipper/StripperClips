using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using StripperClips.Framework;
using UnityEngine;

namespace StripperClips;

// The animation frame and inventory LOD0 share a UV layout.
internal static class ClipLook
{
    public static void Apply(GameObject? prop, Item clip)
    {
        if (prop == null) return;
        var frame = prop.transform.Find("part_000") ?? prop.transform;
        var renderer = frame.GetComponentInChildren<MeshRenderer>(true);
        var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
        if (renderer == null || filter == null || filter.sharedMesh == null) return;
        var item = ItemFrame(clip);
        if (item == null) return;
        var (mesh, material) = item.Value;
        if (mesh.GetIndexCount(0) != filter.sharedMesh.GetIndexCount(0))
        {
            StripperReload.Log.LogInfo(TraceLine.Format("STRIPPER_CLIP_LOOK", ("applied", false), ("reason", "other_frame_mesh"), ("prop_tris", filter.sharedMesh.GetIndexCount(0) / 3), ("item_tris", mesh.GetIndexCount(0) / 3)));
            return;
        }
        renderer.sharedMaterial = material; // Owned by the item bundle.
        StripperReload.Log.LogInfo(TraceLine.Format("STRIPPER_CLIP_LOOK", ("applied", true), ("material", material.name), ("shader", material.shader.name)));
    }

    private static (Mesh mesh, Material material)? ItemFrame(Item clip)
    {
        if (!Singleton<ObjectsFactory>.Instantiated) return null;
        var assets = Singleton<ObjectsFactory>.Instance.EasyAssets;
        var key = clip.Template.Prefab;
        if (assets == null || key == null || string.IsNullOrEmpty(key.path) || !EasyAssetsExtensions.IsAssetLoaded(assets, key.path))
        {
            StripperReload.Log.LogInfo(TraceLine.Format("STRIPPER_CLIP_LOOK", ("applied", false), ("reason", "item_prefab_not_loaded"), ("key", key?.path ?? "")));
            return null;
        }
        var prefab = EasyAssetsExtensions.GetAsset<GameObject>(assets, key);
        if (prefab == null) return null;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter.name.EndsWith("_LOD0") || filter.sharedMesh == null) continue;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != null) return (filter.sharedMesh, renderer.sharedMaterial);
        }
        return null;
    }
}
