using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace StripperClips.Framework;

// Dispose in reverse registration order so cleanup can still use earlier resources.
public sealed class RaidScope
{
    private readonly List<Action> _cleanups = new List<Action>();
    private readonly ManualLogSource _log;

    public RaidScope(GameWorld world, Player mainPlayer, ManualLogSource log)
    {
        World = world;
        MainPlayer = mainPlayer;
        _log = log;
    }

    public GameWorld World { get; }
    public Player MainPlayer { get; }
    public bool IsHideout => World is HideoutGameWorld;

    public void OnEnd(Action cleanup) => _cleanups.Add(cleanup);

    public T Own<T>(T owned) where T : UnityEngine.Object
    {
        // Scene unload may already have destroyed it; Unity's null check covers that.
        _cleanups.Add(() => { if (owned != null) UnityEngine.Object.Destroy(owned is Component component ? component.gameObject : owned); });
        return owned;
    }

    public T OwnDisposable<T>(T owned) where T : class, IDisposable
    {
        _cleanups.Add(owned.Dispose);
        return owned;
    }

    internal void End()
    {
        for (var index = _cleanups.Count - 1; index >= 0; index--)
        {
            try { _cleanups[index](); }
            catch (Exception exception) { _log.LogError($"FND_SCOPE_CLEANUP_FAILED {exception}"); }
        }
        _cleanups.Clear();
    }
}
