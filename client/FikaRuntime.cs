using System;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using EFT;
using StripperClips.Pure;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("StripperClips.Fika")]

namespace StripperClips;

internal static class FikaRuntime
{
    internal const string PluginId = "com.fika.core";
    internal const string IntegrationName = "StripperClips.Fika.dll";
    internal static Func<Player, bool>? Ready;
    internal static Func<Player, bool>? Connected;
    internal static Func<Player, bool>? StanagReady;
    internal static event Action<Player, StanagVisual>? StanagChanged;
    internal static event Action<Player, ReloadVisual>? VisualChanged;
    internal static event Action? RaidEnded;
    private static long _lastAction;

    internal static bool IsCoop(Player player) => player.GetType().Assembly.GetName().Name == "Fika.Core";
    internal static bool CanReload(Player player) => !IsCoop(player) || Ready?.Invoke(player) == true;
    internal static bool CanLoadStanag(Player player) => !IsCoop(player) || StanagReady?.Invoke(player) == true;

    internal static void NotifyStanag(Player owner, StanagVisual visual)
    {
        try { StanagChanged?.Invoke(owner, visual); }
        catch (Exception error) { StripperReload.Log.LogWarning("Fika magazine loading notification failed: " + error.Message); }
    }

    internal static long NextAction()
    {
        _lastAction = Math.Max(_lastAction + 1, DateTime.UtcNow.Ticks);
        return _lastAction;
    }

    internal static void Notify(Player owner, ReloadVisual visual)
    {
        try { VisualChanged?.Invoke(owner, visual); }
        catch (Exception error) { StripperReload.Log.LogWarning("Fika reload notification failed: " + error.Message); }
    }

    internal static void EndRaid() => RaidEnded?.Invoke();

    internal static void TryStart(GameObject host, ManualLogSource log)
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(PluginId, out var fika) || fika.Instance == null) return;
            var version = fika.Instance.GetType().Assembly.GetName().Version;
            if (version != new System.Version(2, 4, 6, 0))
            {
                log.LogWarning("StripperClips co-op reloads require Fika.Core 2.4.6.0; found " + version);
                return;
            }
            var path = Path.Combine(Path.GetDirectoryName(typeof(FikaRuntime).Assembly.Location)!, IntegrationName);
            if (!File.Exists(path))
            {
                log.LogWarning("StripperClips.Fika.dll is missing; co-op clip reloads are unavailable.");
                return;
            }
            var entry = Assembly.LoadFile(path).GetType("StripperClips.Fika.FikaIntegration", throwOnError: true);
            host.AddComponent(entry);
        }
        catch (Exception error) { log.LogError("StripperClips Fika integration could not start: " + error); }
    }
}
