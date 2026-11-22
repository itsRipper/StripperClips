using BepInEx;
using UnityEngine;

namespace StripperClips.Framework;

public abstract class ModPlugin : BaseUnityPlugin
{
    protected FeatureHost Features { get; private set; } = null!;
    protected PatchSet Patches { get; private set; } = null!;

    protected virtual string SupportedBuild => SupportedGame.VerifiedBuild;

    protected abstract void Configure();

    private void Awake()
    {
        if (!SupportedGame.Check(Logger, SupportedBuild))
        {
            enabled = false;
            return;
        }
        Patches = new PatchSet(Info.Metadata.GUID);
        Features = new FeatureHost(Logger);
        Configure();
        Logger.LogInfo(TraceLine.Format("FND_PLUGIN_READY", ("guid", Info.Metadata.GUID), ("version", Info.Metadata.Version)));
    }

    protected virtual void Update() => Features.Update(Time.deltaTime);

    // Unity calls only the most derived OnDestroy, so a derived plugin must override and call this.
    protected virtual void OnDestroy()
    {
        if (Features == null) return;
        Features.Shutdown();
        Patches.Dispose();
    }
}
