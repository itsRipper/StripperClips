using System;
using System.Collections.Generic;

namespace StripperClips.Framework;

// Use (previous, current] to fire each event once. Start before zero to catch events at zero.
public sealed class ClipEventTrack
{
    public const float Start = -1f;

    private readonly float[] _times;
    private readonly int[] _order;

    public ClipEventTrack(IReadOnlyList<float> times)
    {
        _order = new int[times.Count];
        for (var i = 0; i < _order.Length; i++) _order[i] = i;
        // Keep authored order when timestamps match.
        Array.Sort(_order, (a, b) => times[a] != times[b] ? times[a].CompareTo(times[b]) : a.CompareTo(b));
        _times = new float[_order.Length];
        for (var i = 0; i < _order.Length; i++) _times[i] = times[_order[i]];
    }

    public int Count => _times.Length;

    public void Collect(float previous, float current, List<int> fired)
    {
        if (current <= previous) return;
        for (var i = 0; i < _times.Length; i++)
        {
            var time = _times[i];
            if (time > current) break;
            if (time > previous) fired.Add(_order[i]);
        }
    }
}
