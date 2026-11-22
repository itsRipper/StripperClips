using System;

namespace StripperClips.Framework;

internal struct ClipPlaybackBarrier
{
    private float? _limit;

    internal bool Holding => _limit.HasValue;
    internal void Hold(float time) => _limit = time;
    internal void Resume() => _limit = null;
    internal float Advance(float time, float delta, float length) => Math.Min(time + delta, Math.Min(length, _limit ?? length));
}
