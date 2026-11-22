using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.JobsBenchmarks;

/// <summary>Counts down the jobs one benchmark operation enqueued; its task completes once all have run.</summary>
internal static class JobCounter
{
    private static TaskCompletionSource _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _left;

    /// <summary>Starts a count of <paramref name="jobs"/>; the task completes when the last of them has run.</summary>
    public static Task Expect(int jobs)
    {
        // The source is replaced before the count is set, so the job that takes the count to zero
        // always completes this operation's task.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _done, done);
        Volatile.Write(ref _left, jobs);
        return done.Task;
    }

    public static void Hit()
    {
        if (Interlocked.Decrement(ref _left) == 0)
        {
            Volatile.Read(ref _done).TrySetResult();
        }
    }
}
