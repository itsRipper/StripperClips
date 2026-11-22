using System;
using System.Collections.Generic;

namespace StripperClips.Pure;

internal struct CoopCommitGate
{
    private bool _begun;
    public bool Finished { get; private set; }
    public bool Interrupted { get; private set; }

    public bool TryBegin()
    {
        if (_begun || Finished) return false;
        _begun = true;
        return true;
    }

    public void Finish(bool interrupted)
    {
        Finished = true;
        Interrupted = interrupted;
    }
}

internal static class CoopReloadPolicy
{
    internal static bool AllPeersEnrolled<T>(IReadOnlyList<T> connected, IReadOnlyDictionary<int, T> enrolled,
        Func<T, int> getId) where T : class
    {
        foreach (var peer in connected)
            if (!enrolled.TryGetValue(getId(peer), out var member) || !ReferenceEquals(peer, member)) return false;
        return true;
    }
}
