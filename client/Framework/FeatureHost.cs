using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Comfort.Common;
using EFT;

namespace StripperClips.Framework;

public interface IRaidFeature
{
    void OnRaidStart(RaidScope raid);
}

public interface IRaidTick
{
    void Tick(float deltaTime);
}

public sealed class FeatureHost
{
    private readonly ManualLogSource _log;
    private readonly List<IRaidFeature> _features = new List<IRaidFeature>();
    private readonly List<IRaidTick> _ticking = new List<IRaidTick>();
    private readonly RaidPhaseTracker _tracker = new RaidPhaseTracker();
    private GameWorld? _world;
    private RaidScope? _scope;
    private bool _gameStarted;

    public FeatureHost(ManualLogSource log) => _log = log;

    public RaidScope? Current => _scope;

    // The host cannot observe the start event for a world already in progress.
    public event Action<RaidScope>? GameStarted;

    public void Add(IRaidFeature feature) => _features.Add(feature);

    public void Update(float deltaTime)
    {
        var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
        var worldId = world != null ? world.GetInstanceID() : 0;
        var transition = _tracker.Observe(worldId, world != null && world.MainPlayer != null, _gameStarted);
        switch (transition)
        {
            case RaidTransition.WorldCreated:
                _world = world;
                _gameStarted = false;
                world!.AfterGameStarted += MarkStarted;
                _log.LogInfo(TraceLine.Format("FND_WORLD_CREATED", ("type", world.GetType().Name)));
                break;
            case RaidTransition.PlayerReady:
                StartFeatures(world!);
                break;
            case RaidTransition.Started:
                if (_scope != null) GameStarted?.Invoke(_scope);
                break;
            case RaidTransition.Ended:
                EndFeatures();
                break;
        }
        for (var index = 0; index < _ticking.Count; index++)
        {
            try { _ticking[index].Tick(deltaTime); }
            catch (Exception exception)
            {
                _log.LogError($"FND_FEATURE_TICK_FAILED feature={_ticking[index].GetType().Name} {exception}");
                _ticking.RemoveAt(index--);
            }
        }
    }

    public void Shutdown() => EndFeatures();

    private void MarkStarted() => _gameStarted = true;

    private void StartFeatures(GameWorld world)
    {
        _scope = new RaidScope(world, world.MainPlayer, _log);
        _log.LogInfo(TraceLine.Format("FND_RAID_START", ("hideout", _scope.IsHideout), ("location", world.LocationId)));
        foreach (var feature in _features)
        {
            try
            {
                feature.OnRaidStart(_scope);
                if (feature is IRaidTick ticking) _ticking.Add(ticking);
            }
            catch (Exception exception)
            {
                _log.LogError($"FND_FEATURE_START_FAILED feature={feature.GetType().Name} {exception}");
            }
        }
    }

    private void EndFeatures()
    {
        _ticking.Clear();
        _scope?.End();
        _scope = null;
        // Unsubscribing is safe after Unity destroys the world object.
        if (!ReferenceEquals(_world, null)) _world.AfterGameStarted -= MarkStarted;
        _world = null;
        _gameStarted = false;
        _log.LogInfo("FND_RAID_END");
    }
}
