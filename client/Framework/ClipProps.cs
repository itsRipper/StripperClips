using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StripperClips.Framework;

// Copy only meshes and materials from the rig to avoid running gameplay components.
public sealed class ClipProps : IDisposable
{
    public sealed class Spec
    {
        public string Id = "", Slot = "", Source = "";
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public Vector3 Scale = Vector3.one;
        public bool Visible;
    }

    private readonly Dictionary<string, GameObject> _props = new Dictionary<string, GameObject>();
    private readonly List<Material> _materials = new List<Material>();

    public int Count => _props.Count;

    public GameObject? Find(string id) => _props.TryGetValue(id, out var prop) ? prop : null;

    public string Describe()
    {
        var parts = new List<string>();
        foreach (var pair in _props)
        {
            var renderers = pair.Value.GetComponentsInChildren<Renderer>(true);
            var vertices = 0;
            foreach (var filter in pair.Value.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh != null) vertices += filter.sharedMesh.vertexCount;
            var shader = renderers.Length > 0 && renderers[0].sharedMaterial != null ? renderers[0].sharedMaterial.shader.name : "none";
            var t = pair.Value.transform;
            var camera = Camera.main;
            var distance = camera != null ? Vector3.Distance(camera.transform.position, t.position) : -1f;
            var inView = camera != null && renderers.Length > 0 && GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), renderers[0].bounds);
            var culled = camera != null && (camera.cullingMask & (1 << pair.Value.layer)) == 0;
            parts.Add(FormattableString.Invariant($"{pair.Key}:renderers={renderers.Length},vertices={vertices},layer={pair.Value.layer},shader={shader},active={pair.Value.activeInHierarchy},scale={t.lossyScale.x:0.###},camera_m={distance:0.###},in_view={inView},layer_culled={culled}"));
        }
        return string.Join("|", parts);
    }

    public static ClipProps Spawn(Transform rigRoot, IReadOnlyList<Spec> specs, Func<string, GameObject?> loadAsset)
    {
        var props = new ClipProps();
        var reference = VisibleRenderer(rigRoot);
        try
        {
            foreach (var spec in specs)
            {
                var slot = rigRoot.Find(spec.Slot) ?? throw new InvalidOperationException("prop slot not in rig: " + spec.Slot);
                var root = new GameObject("fnd_prop_" + spec.Id);
                props._props[spec.Id] = root;
                root.transform.SetParent(slot, false);
                root.transform.localPosition = spec.Position;
                root.transform.localRotation = spec.Rotation;
                root.transform.localScale = spec.Scale;
                if (spec.Source.StartsWith("asset:", StringComparison.Ordinal))
                {
                    var prefab = loadAsset(spec.Source.Substring(6)) ?? throw new InvalidOperationException("prop asset not in bundle: " + spec.Source);
                    UnityEngine.Object.Instantiate(prefab, root.transform, false);
                }
                else if (spec.Source.StartsWith("rig:", StringComparison.Ordinal))
                    CopyRenderers(rigRoot.Find(spec.Source.Substring(4)) ?? throw new InvalidOperationException("prop source not in rig: " + spec.Source), root.transform);
                else throw new InvalidOperationException("unknown prop source " + spec.Source);
                // Unity's Standard shader does not render in EFT's first-person pass.
                var layer = reference != null ? reference.gameObject.layer : slot.gameObject.layer;
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    if (reference != null) renderer.sharedMaterials = props.GameMaterials(renderer.sharedMaterials, reference.sharedMaterial);
                }
                root.SetActive(spec.Visible);
            }
        }
        catch
        {
            props.Dispose();
            throw;
        }
        return props;
    }

    public bool Handle(ClipEvent clipEvent)
    {
        var show = clipEvent.Name == "prop.show";
        if (!show && clipEvent.Name != "prop.hide") return false;
        if (clipEvent.Value == null || !_props.TryGetValue(clipEvent.Value, out var prop)) return false;
        prop.SetActive(show);
        return true;
    }

    public bool ShowParts(string id, int from, int to)
    {
        if (!_props.TryGetValue(id, out var prop) || prop == null) return false;
        var found = false;
        foreach (var t in prop.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("part_", StringComparison.Ordinal) || !int.TryParse(t.name.Substring(5), out var index)) continue;
            t.gameObject.SetActive(index >= from && index < to);
            found = true;
        }
        return found;
    }

    public void Dispose()
    {
        foreach (var prop in _props.Values) if (prop != null) UnityEngine.Object.Destroy(prop);
        _props.Clear();
        foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear();
    }

    private static Renderer? VisibleRenderer(Transform rigRoot)
    {
        foreach (var renderer in rigRoot.GetComponentsInChildren<MeshRenderer>(false))
            if (renderer.enabled && renderer.sharedMaterial != null && renderer.sharedMaterial.shader.name != "Standard") return renderer;
        return null;
    }

    private Material[] GameMaterials(Material[] materials, Material weapon)
    {
        var result = new Material[materials.Length];
        for (var i = 0; i < materials.Length; i++)
        {
            var source = materials[i];
            if (source == null || source.shader == weapon.shader || source.shader.name.StartsWith("p0/", StringComparison.Ordinal)) { result[i] = source!; continue; }
            var material = new Material(weapon) { name = source.name + "_game" };
            material.mainTexture = source.mainTexture != null ? source.mainTexture : Texture2D.whiteTexture;
            material.color = source.HasProperty("_Color") ? source.color : Color.white;
            foreach (var map in new[] { "_BumpMap", "_SpecMap", "_DetailAlbedoMap" }) if (material.HasProperty(map)) material.SetTexture(map, null);
            _materials.Add(material);
            result[i] = material;
        }
        return result;
    }

    private static void CopyRenderers(Transform source, Transform parent)
    {
        foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>(false))
        {
            var filter = renderer.GetComponent<MeshFilter>();
            if (!renderer.enabled || filter == null || filter.sharedMesh == null) continue;
            var name = filter.sharedMesh.name;
            var lod = name.LastIndexOf("_LOD", StringComparison.Ordinal);
            if (lod >= 0 && name.Substring(lod) != "_LOD0") continue;
            var relative = source.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            var copy = new GameObject(renderer.name);
            copy.transform.SetParent(parent, false);
            copy.transform.localPosition = relative.GetColumn(3);
            copy.transform.localRotation = relative.rotation;
            copy.transform.localScale = relative.lossyScale;
            copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            copy.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
        }
    }
}
