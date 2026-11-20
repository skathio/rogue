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

    /// <summary>Cuts <paramref name="error"/> to <see cref="MaxErrorLength"/>.</summary>
    public static string TrimError(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Length <= MaxErrorLength ? error : error.Substring(0, MaxErrorLength);
    }

    internal static void CheckKind(string kind, string parameter) => CheckName(kind, MaxKindLength, parameter);

    internal static void CheckQueue(string queue, string parameter) => CheckName(queue, MaxQueueLength, parameter);

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
