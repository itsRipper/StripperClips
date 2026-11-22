namespace StripperClips.Pure;

internal enum StanagPhase : byte { Start = 1, Committed, Finished, Cancelled }

internal readonly struct StanagVisual
{
    internal StanagVisual(long action, StanagPhase phase, string magazineId = "", string clipId = "", string[]? ammunition = null)
    {
        Action = action;
        Phase = phase;
        MagazineId = magazineId;
        ClipId = clipId;
        Ammunition = ammunition;
    }

    internal long Action { get; }
    internal StanagPhase Phase { get; }
    internal string MagazineId { get; }
    internal string ClipId { get; }
    internal string[]? Ammunition { get; }
}

internal sealed class StanagVisualOrder
{
    private long _action;
    internal StanagPhase Phase { get; private set; }
    internal bool Active => Phase == StanagPhase.Start || Phase == StanagPhase.Committed;

    internal bool Accept(StanagVisual visual)
    {
        if (visual.Action <= 0 || visual.Action < _action || visual.Phase < StanagPhase.Start || visual.Phase > StanagPhase.Cancelled)
            return false;
        if (visual.Action != _action)
        {
            if (visual.Phase != StanagPhase.Start && visual.Phase != StanagPhase.Cancelled) return false;
        }
        else if (!Active || visual.Phase <= Phase || visual.Phase == StanagPhase.Finished && Phase != StanagPhase.Committed) return false;
        _action = visual.Action;
        Phase = visual.Phase;
        return true;
    }
}
