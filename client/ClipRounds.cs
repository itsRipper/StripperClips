using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using StripperClips.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace StripperClips;

internal sealed class ClipRounds
{
    // SpringMagazineVisual uses a 90-degree round rotation under a 90-degree mounting point.
    private static readonly Quaternion Seat = Quaternion.Euler(90f, 0f, 0f) * Quaternion.Euler(90f, 0f, 0f);

    private readonly List<Placed> _placed = new List<Placed>();

    private struct Placed
    {
        public GameObject Round;
        public Renderer Baked;
        public Transform[] Transforms;
        public int[] Layers;
        public Renderer[] Renderers;
        public ShadowCastingMode[] Shadows;
    }

    public int Count => _placed.Count;

    public static ClipRounds Bind(WeaponClipAction action, Magazine clip)
    {
        var rounds = new ClipRounds();
        var capacity = clip.MaxCount;
        var held = clip.Count;
        // Prop slots count from the bottom. A partially filled clip occupies the top slots.
        var byPosition = new Ammo?[capacity];
        for (var j = capacity - held; j < capacity; j++) byPosition[j] = clip.GetBulletAtPosition(j - (capacity - held));
        if (!Singleton<ObjectsFactory>.Instantiated) return rounds;
        var factory = Singleton<ObjectsFactory>.Instance;
        var skipped = 0;
        foreach (var (prop, offset) in new[] { ("rounds", 0), ("clip", 1) })
        {
            var root = action.Prop(prop);
            if (root == null) continue;
            foreach (var part in root.GetComponentsInChildren<Transform>(true))
            {
                if (!part.name.StartsWith("part_") || !int.TryParse(part.name.Substring(5), out var index)) continue;
                var position = index - offset;
                if (position < 0 || position >= capacity || byPosition[position] == null) continue;
                if (!rounds.Place(factory, part, byPosition[position]!)) skipped++;
            }
        }
        StripperReload.Log.LogInfo(TraceLine.Format("STRIPPER_ROUNDS", ("placed", rounds.Count), ("kept_baked", skipped), ("clip", Describe(byPosition))));
        action.Finishing += _ => rounds.Release();
        return rounds;
    }

    private bool Place(ObjectsFactory factory, Transform part, Ammo ammo)
    {
        var baked = part.GetComponent<MeshRenderer>();
        var filter = part.GetComponent<MeshFilter>();
        if (baked == null || filter == null || filter.sharedMesh == null) return false;
        var bounds = filter.sharedMesh.bounds;
        if (bounds.size.z < 0.03f || bounds.size.z < 3f * bounds.size.x) return false; // Cartridge base is at maximum local Z.
        var key = ammo.Template.Prefab;
        if (key == null || !EasyAssetsExtensions.IsAssetLoaded(factory.EasyAssets, key.path)) return false;
        var round = factory.CreateItem(ammo, false);
        if (round == null) return false;
        round.GetComponent<AmmoPoolObject>()?.SetUsed(false);
        var transforms = round.GetComponentsInChildren<Transform>(true);
        var renderers = round.GetComponentsInChildren<Renderer>(true);
        var placed = new Placed { Round = round, Baked = baked, Transforms = transforms, Layers = new int[transforms.Length], Renderers = renderers, Shadows = new ShadowCastingMode[renderers.Length] };
        for (var i = 0; i < transforms.Length; i++) { placed.Layers[i] = transforms[i].gameObject.layer; transforms[i].gameObject.layer = part.gameObject.layer; }
        for (var i = 0; i < renderers.Length; i++) { placed.Shadows[i] = renderers[i].shadowCastingMode; renderers[i].shadowCastingMode = ShadowCastingMode.Off; }
        var t = round.transform;
        t.SetParent(part, false);
        t.localPosition = new Vector3(bounds.center.x, bounds.center.y, bounds.max.z);
        t.localRotation = Seat;
        t.localScale = Vector3.one;
        round.SetActive(true);
        baked.enabled = false;
        _placed.Add(placed);
        return true;
    }

    public void Release()
    {
        foreach (var p in _placed)
        {
            if (p.Baked != null) p.Baked.enabled = true;
            if (p.Round == null) continue;
            for (var i = 0; i < p.Transforms.Length; i++) if (p.Transforms[i] != null) p.Transforms[i].gameObject.layer = p.Layers[i];
            for (var i = 0; i < p.Renderers.Length; i++) if (p.Renderers[i] != null) p.Renderers[i].shadowCastingMode = p.Shadows[i];
            p.Round.transform.SetParent(null, false);
            AssetPoolObject.ReturnToPool(p.Round, true);
        }
        _placed.Clear();
    }

    private static string Describe(Ammo?[] byPosition)
    {
        var names = new List<string>();
        for (var j = byPosition.Length - 1; j >= 0; j--) names.Add(byPosition[j] != null ? Tail(byPosition[j]!.TemplateId.ToString()) : "-");
        return string.Join(",", names);
    }

    private static string Tail(string id) => id.Length > 6 ? id.Substring(id.Length - 6) : id;
}
