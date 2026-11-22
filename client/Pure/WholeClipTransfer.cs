using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StripperClips.Pure;

internal enum TransferOutcome { Committed, Refused, Restored, RecoveryFailed, Pending }
internal enum StackMoveOutcome { Rejected, Moved, MovedWithoutConfirmation, Pending }

internal static class WholeClipTransfer
{
    // Fika acknowledges each stack move separately; an unacknowledged move may still land, so nothing follows it.
    public static async Task<TransferOutcome> Run<T>(IReadOnlyList<T> stacks, Func<T, bool> canMove,
        Func<T, Task<StackMoveOutcome>> move, Func<T, Task<StackMoveOutcome>> restore)
    {
        if (stacks.Count == 0) return TransferOutcome.Refused;
        foreach (var stack in stacks)
            if (!canMove(stack)) return TransferOutcome.Refused;

        var confirmed = 0;
        while (confirmed < stacks.Count)
        {
            var result = await move(stacks[confirmed]);
            if (result == StackMoveOutcome.Pending) return TransferOutcome.Pending;
            if (result == StackMoveOutcome.Rejected) break;
            confirmed++;
            if (result == StackMoveOutcome.MovedWithoutConfirmation) break;
            if (confirmed == stacks.Count) return TransferOutcome.Committed;
        }
        if (confirmed == 0) return TransferOutcome.Refused;

        while (confirmed > 0)
        {
            var result = await restore(stacks[--confirmed]);
            if (result == StackMoveOutcome.Pending) return TransferOutcome.Pending;
            if (result != StackMoveOutcome.Moved) return TransferOutcome.RecoveryFailed;
        }
        return TransferOutcome.Restored;
    }
}
