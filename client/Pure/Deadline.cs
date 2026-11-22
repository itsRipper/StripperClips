using System;
using System.Threading;
using System.Threading.Tasks;

namespace StripperClips.Pure;

internal static class Deadline
{
    // A task that outlives the timeout keeps running and may still succeed.
    public static async Task<(bool Completed, T Result)> Await<T>(Task<T> task, TimeSpan timeout)
    {
        if (task.IsCompleted) return (true, await task);
        using (var cancel = new CancellationTokenSource())
        {
            if (await Task.WhenAny(task, Task.Delay(timeout, cancel.Token)) != task) return (false, default!);
            cancel.Cancel();
        }
        return (true, await task);
    }
}
