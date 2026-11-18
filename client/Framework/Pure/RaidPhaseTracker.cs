namespace StripperClips.Framework;

public enum RaidTransition
{
    None,
    WorldCreated,
    PlayerReady,
    Started,
    Ended,
}

// When EFT replaces a world, emit Ended before WorldCreated.
public sealed class RaidPhaseTracker
{
    private int _worldId;
    private bool _playerReady;
    private bool _started;

    public bool HasWorld => _worldId != 0;
    public bool PlayerReady => _playerReady;

    public RaidTransition Observe(int worldId, bool playerExists, bool gameStarted)
    {
        if (_worldId != 0 && worldId != _worldId)
        {
            _worldId = 0;
            _playerReady = false;
            _started = false;
            return RaidTransition.Ended;
        }
        if (worldId == 0) return RaidTransition.None;
        if (_worldId == 0)
        {
            _worldId = worldId;
            return RaidTransition.WorldCreated;
        }
        if (!_playerReady && playerExists)
        {
            _playerReady = true;
            return RaidTransition.PlayerReady;
        }
        if (_playerReady && !_started && gameStarted)
        {
            _started = true;
            return RaidTransition.Started;
        }
        return RaidTransition.None;
    }
}
