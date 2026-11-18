using System.IO;
using BepInEx;
using StripperClips.Framework;

namespace StripperClips;

[BepInPlugin("itsripper.stripperclips", "StripperClips", "1.0.0")]
public sealed class Plugin : ModPlugin
{
    protected override void Configure()
    {
        var prefer = Config.Bind("Reload", "PreferClips", true, "Prefer stripper clips when reloading. Disable to retain native reload priority.");
        ReloadPatches.PreferClips = () => prefer.Value;
        var trace = Config.Bind("Debug", "TransitionTrace", false, "Log FND_CLIP_TRANSITION lines (rendered weapon pose around the start and end of a reload).");
        WeaponClipAction.Trace = line => { if (trace.Value) Logger.LogInfo(line); };
        StripperReload.Log = Logger;
        StripperReload.ClipsFolder = Path.Combine(Path.GetDirectoryName(Info.Location)!, "clips");
        WeaponClipAction.Install(Patches);
        ReloadPatches.Install(Patches);
        Features.Add(new Session());
    }

    private void OnDestroy() => StripperReload.Unload();

    private sealed class Session : IRaidFeature
    {
        public void OnRaidStart(RaidScope raid)
        {
            StripperReload.LocalPlayer = raid.MainPlayer;
            raid.OnEnd(() =>
            {
                WeaponClipAction.Active?.Stop("raid_end");
                StripperReload.Reset();
                StripperReload.LocalPlayer = null;
            });
        }
    }
}
