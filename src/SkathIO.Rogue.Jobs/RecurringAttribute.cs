using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Runs a <c>[Job]</c> command at each occurrence of a cron expression, in UTC, in every host that calls
/// <c>AddRogueJobWorker</c>. One host at a time, the leader, stores the next occurrence as a delayed job,
/// and the store keeps one job per occurrence, so each runs once however many hosts there are.
/// Occurrences that fall while no host runs are skipped, not caught up.
/// </summary>
/// <remarks>
/// The command needs a parameterless constructor, which each occurrence is created with. The
/// expression has five fields, minute (0-59), hour (0-23), day of month (1-31), month (1-12 or
/// JAN-DEC) and day of week (0-7 or SUN-SAT, where 0 and 7 are Sunday), each <c>*</c>, a value, a
/// range <c>a-b</c>, a step <c>*/n</c> or <c>a-b/n</c>, or a comma list of these. When both day fields
/// are restricted, a day that matches either one matches. A command whose expression is not valid
/// fails the host at startup.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RecurringAttribute : Attribute
{
    /// <summary>Marks the command to run at each occurrence of <paramref name="cron"/>.</summary>
    /// <param name="cron">A five-field cron expression, such as <c>"30 * * * *"</c> for minute 30 of every hour.</param>
    public RecurringAttribute(string cron)
    {
        Cron = cron;
    }

    /// <summary>The cron expression.</summary>
    public string Cron { get; }
}
