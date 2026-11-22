namespace StripperClips.Pure;

internal enum ReloadVisualPhase : byte { Start = 1, Loaded, Closed, Cancelled, Settled }

internal readonly struct ReloadVisual
{
    public ReloadVisual(long action, ReloadVisualPhase phase, string weaponId, string clipId = "", int count = 0,
        bool open = false, bool chambered = false)
    {
        Action = action;
        Phase = phase;
        WeaponId = weaponId;
        ClipId = clipId;
        Count = count;
        Open = open;
        Chambered = chambered;
    }

    public long Action { get; }
    public ReloadVisualPhase Phase { get; }
    public string WeaponId { get; }
    public string ClipId { get; }
    public int Count { get; }
    public bool Open { get; }
    public bool Chambered { get; }
}

internal sealed class ReloadVisualOrder
{
    private long _action;
    private ReloadVisualPhase _phase;

    public static bool CanReplay(ReloadVisualPhase phase) =>
        phase == ReloadVisualPhase.Start || phase == ReloadVisualPhase.Loaded;

    public bool CanReplayStart => CanReplay(_phase);

    // Cancelling the pose can precede the last native move or its compensation.
    public bool HasTransferContext { get; private set; }

    public bool Accept(long action, ReloadVisualPhase phase)
    {
        if (action <= 0 || phase < ReloadVisualPhase.Start || phase > ReloadVisualPhase.Settled) return false;
        if (phase == ReloadVisualPhase.Settled && (action != _action || _phase != ReloadVisualPhase.Cancelled)) return false;
        if (action < _action || (action == _action && phase <= _phase)) return false;
        if (action > _action && phase != ReloadVisualPhase.Start && phase != ReloadVisualPhase.Cancelled) return false;
        if (phase == ReloadVisualPhase.Start) HasTransferContext = true;
        else if (action != _action || phase == ReloadVisualPhase.Closed || phase == ReloadVisualPhase.Settled)
            HasTransferContext = false;
        _action = action;
        _phase = phase;
        return true;
    }
}
