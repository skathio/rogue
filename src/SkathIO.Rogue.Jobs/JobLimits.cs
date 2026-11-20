using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Length limits on the names a job carries. Stores keep kind and queue in fixed-size columns, so the
/// limits are part of the store contract.
/// </summary>
public static class JobLimits
{
    /// <summary>The longest job kind, in characters.</summary>
    public const int MaxKindLength = 200;

    /// <summary>The longest queue name, in characters.</summary>
    public const int MaxQueueLength = 100;

    /// <summary>The longest recorded error, in characters; a store keeps only this much of a longer one.</summary>
    public const int MaxErrorLength = 4000;

    /// <summary>The longest key, in characters: a job's unique key or task key, a leader lock's name or owner.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>
    /// Removes every NUL character (U+0000) from <paramref name="error"/>, then cuts it to
    /// <see cref="MaxErrorLength"/>. Postgres text cannot hold NUL, so storing an exception message
    /// that has one would fail the very write that records the failure.
    /// </summary>
    public static string TrimError(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Contains('\0', StringComparison.Ordinal))
        {
            error = error.Replace("\0", string.Empty, StringComparison.Ordinal);
        }
        return error.Length <= MaxErrorLength ? error : error.Substring(0, MaxErrorLength);
    }

    internal static void CheckKind(string kind, string parameter) => CheckName(kind, MaxKindLength, parameter);

    /// <summary>
    /// Checks a queue name against the store contract: 1 to <see cref="MaxQueueLength"/> characters,
    /// with no white space or control characters. Stores call it on the queue a claim names.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="queue"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="queue"/> is empty, too long or has white space.</exception>
    public static void CheckQueue(string queue, string parameter) => CheckName(queue, MaxQueueLength, parameter);

    /// <summary>
    /// Checks a key against the store contract: 1 to <see cref="MaxKeyLength"/> characters, with no white
    /// space or control characters. Stores call it on the leader lock's name and owner.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty, too long or has white space.</exception>
    public static void CheckKey(string key, string parameter) => CheckName(key, MaxKeyLength, parameter);

    private static void CheckName(string value, int maxLength, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length == 0 || value.Length > maxLength)
        {
            throw new ArgumentException("Must be 1 to " + maxLength + " characters long.", parameter);
        }
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException("Must not contain white space or control characters.", parameter);
            }
        }
    }
}
