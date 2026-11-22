using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StripperClips.Pure;

internal static class StanagTransfer
{
    internal static Task<TransferOutcome> Run<T>(IReadOnlyList<T> stacks, Func<bool> canContinue,
        Func<bool> canRestore, Func<int, bool> inventoryMatches, Func<T, bool> compatible,
        Func<T, Task<StackMoveOutcome>> move, Func<T, Task<StackMoveOutcome>> restore)
    {
        var moved = 0;
        if (!canContinue() || !inventoryMatches(moved)) return Task.FromResult(TransferOutcome.Refused);

        return WholeClipTransfer.Run(stacks, compatible, async stack =>
        {
            if (!canContinue() || !inventoryMatches(moved)) return StackMoveOutcome.Rejected;
            var result = await move(stack);
            if (result == StackMoveOutcome.Rejected) return result;
            moved++;
            return canContinue() && inventoryMatches(moved) ? result : StackMoveOutcome.MovedWithoutConfirmation;
        }, async stack =>
        {
            if (!canRestore() || !inventoryMatches(moved)) return StackMoveOutcome.Rejected;
            var result = await restore(stack);
            if (result != StackMoveOutcome.Moved) return result;
            moved--;
            return inventoryMatches(moved) ? result : StackMoveOutcome.MovedWithoutConfirmation;
        });
    }
}
