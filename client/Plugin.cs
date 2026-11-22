using System.IO;
using BepInEx;
using StripperClips.Framework;

namespace StripperClips;

[BepInPlugin("itsripper.stripperclips", "StripperClips", "1.1.0")]
public sealed class Plugin : ModPlugin
{
    protected override void Configure()
    {
        var prefer = Config.Bind("Reload", "PreferClips", true, "Prefer stripper clips when reloading. Disable to retain native reload priority.");
        ReloadPatches.PreferClips = () => prefer.Value;
        var trace = Config.Bind("Debug", "TransitionTrace", false, "Log FND_CLIP_TRANSITION lines (rendered weapon pose around the start and end of a reload).");
        WeaponClipAction.Trace = line => { if (trace.Value) Logger.LogInfo(line); };
        var stanagTrace = Config.Bind("Debug", "StanagTrace", false, "Log STANAG magazine-loading phases.");
        Stanag.MagazinePresentation.Trace = () => stanagTrace.Value;
        StripperReload.Log = Logger;
        StripperReload.ClipsFolder = Path.Combine(Path.GetDirectoryName(Info.Location)!, "clips");
        WeaponClipAction.Install(Patches);
        ReloadPatches.Install(Patches);
        Stanag.MagazineLoad.Install(Patches);
        Features.Add(new Session());
    }

    // End the raid scope and remove the patches before unloading the clip packages.
    protected override void OnDestroy()
    {
        base.OnDestroy();
        Stanag.MagazineLoad.EndRaid();
        StripperReload.Unload();
    }

    private void Start()
    {
        if (enabled) FikaRuntime.TryStart(gameObject, Logger);
    }

    private sealed class Session : IRaidFeature
    {
        public void OnRaidStart(RaidScope raid)
        {
            StripperReload.LocalPlayer = raid.MainPlayer;
            raid.OnEnd(() =>
            {
                WeaponClipAction.Active?.Stop("raid_end");
                Stanag.MagazineLoad.EndRaid();
                StripperReload.Reset();
                StripperReload.LocalPlayer = null;
                Stanag.MagazineTransfer.EndRaid();
                FikaRuntime.EndRaid();
            });
        }
    }
}
