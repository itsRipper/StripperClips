using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace StripperClips.Framework;

public sealed class ClipPackage : IDisposable
{
    private sealed class Sidecar
    {
        public string format = "", name = "", bundle = "", asset = "", source = "", sha256 = "";
        public float fps { get; set; }
        public float length { get; set; }
        public int frames { get; set; }
        public string[] paths = Array.Empty<string>();
        public SidecarEvent[] events = Array.Empty<SidecarEvent>();
        public SidecarProp[] props = Array.Empty<SidecarProp>();
    }

    private sealed class SidecarProp
    {
        public string id = "", slot = "", source = "";
        public float[] lp = { 0, 0, 0 }, lr = { 0, 0, 0, 1 }, ls = { 1, 1, 1 };
        public bool visible { get; set; }
    }

    private sealed class SidecarEvent
    {
        public float time { get; set; }
        public string name = "", type = "", value = "";
        public bool native { get; set; }
    }

    private AssetBundle? _bundle;

    private ClipPackage(string name, AnimationClip clip, AssetBundle bundle, string[] paths, List<ClipEvent> events, List<ClipProps.Spec> props, string source)
    {
        Name = name; Clip = clip; _bundle = bundle; Paths = paths; Events = events; Props = props; Source = source;
    }

    public string Name { get; }
    public AnimationClip Clip { get; }
    public string[] Paths { get; }
    public List<ClipEvent> Events { get; }
    public List<ClipProps.Spec> Props { get; }
    public string Source { get; }

    public static ClipPackage Load(string sidecarPath)
    {
        var sidecar = JsonConvert.DeserializeObject<Sidecar>(File.ReadAllText(sidecarPath))
            ?? throw new InvalidDataException("empty sidecar " + sidecarPath);
        if (sidecar.format != "foundation-clip-sidecar/1") throw new InvalidDataException("not a foundation clip sidecar: " + sidecarPath);
        var bundlePath = Path.Combine(Path.GetDirectoryName(sidecarPath)!, sidecar.bundle);
        var bundle = AssetBundle.LoadFromFile(bundlePath) ?? throw new IOException("cannot load bundle " + bundlePath);
        var clip = bundle.LoadAsset<AnimationClip>(sidecar.asset);
        if (clip == null) { bundle.Unload(true); throw new InvalidDataException($"bundle {bundlePath} has no clip {sidecar.asset}"); }
        var events = new List<ClipEvent>();
        foreach (var e in sidecar.events)
            events.Add(new ClipEvent { Time = e.time, Name = e.name, Value = e.value.Length > 0 ? e.value : null, Native = e.native });
        var props = new List<ClipProps.Spec>();
        foreach (var p in sidecar.props)
            props.Add(new ClipProps.Spec
            {
                Id = p.id, Slot = p.slot, Source = p.source, Visible = p.visible,
                Position = new Vector3(p.lp[0], p.lp[1], p.lp[2]), Rotation = new Quaternion(p.lr[0], p.lr[1], p.lr[2], p.lr[3]), Scale = new Vector3(p.ls[0], p.ls[1], p.ls[2]),
            });
        return new ClipPackage(sidecar.name, clip, bundle, sidecar.paths, events, props, sidecar.source);
    }

    public GameObject? LoadAsset(string asset) => _bundle != null ? _bundle.LoadAsset<GameObject>(asset) : null;

    public string? FirstMissingPath(Transform rigRoot)
    {
        foreach (var path in Paths)
            if (rigRoot.Find(path) == null) return path;
        return null;
    }

    public void Dispose()
    {
        if (_bundle == null) return;
        _bundle.Unload(true);
        _bundle = null;
    }
}
