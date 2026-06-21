namespace SkathIO.Rogue;

/// <summary>
/// Internal extension helpers for <see cref="System.Threading.Tasks.ValueTask"/> conversions.
/// Used by the generated dispatcher to handle void-path handlers on net8+.
/// </summary>
internal static class ValueTaskExtensions
{
#if !NETSTANDARD2_0
    /// <summary>
    /// Converts a bare <see cref="System.Threading.Tasks.ValueTask"/> to a
    /// <see cref="System.Threading.Tasks.ValueTask{Unit}"/>.
    /// If the task is already completed, returns <see cref="Unit.Task"/> with no allocation.
    /// </summary>
    internal static System.Threading.Tasks.ValueTask<Unit> AsUnit(
        this System.Threading.Tasks.ValueTask vt)
    {
        if (vt.IsCompletedSuccessfully)
        {
            return Unit.Task;
        }
        return Slow(vt);
    }

    private static async System.Threading.Tasks.ValueTask<Unit> Slow(System.Threading.Tasks.ValueTask vt)
    {
        await vt.ConfigureAwait(false);
        return Unit.Value;
    }
#endif
}
