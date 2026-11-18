using System.Collections.Generic;

namespace StripperClips.Pure;

public static class ReloadRules
{
    public static bool FitsWhole(int clipCount, int magazineCount, int magazineCapacity) =>
        clipCount > 0 && clipCount <= magazineCapacity - magazineCount;

    public static bool CanStart(bool singleRoundLoading, bool malfunction, int clipCount, int magazineCount, int magazineCapacity) =>
        singleRoundLoading && !malfunction && FitsWhole(clipCount, magazineCount, magazineCapacity);

    public static bool ChamberOnClose(int chamberCount, int magazineCount) => chamberCount == 0 && magazineCount > 0;

    public static int PickClip(IReadOnlyList<int> clipCounts, int magazineCount, int magazineCapacity)
    {
        var best = -1;
        var bestCount = 0;
        for (var i = 0; i < clipCounts.Count; i++)
        {
            var count = clipCounts[i];
            if (count > bestCount && FitsWhole(count, magazineCount, magazineCapacity))
            {
                best = i;
                bestCount = count;
            }
        }
        return best;
    }
}
