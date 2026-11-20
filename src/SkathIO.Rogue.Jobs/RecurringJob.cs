using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// A command that runs at each occurrence of a cron expression. The source generator registers one per
/// command marked <see cref="RecurringAttribute"/>; one can also be registered in code with
/// <c>AddRecurringJob</c>. Its occurrences run in hosts that call <c>AddRogueJobWorker</c>.
/// </summary>
public abstract class RecurringJob
{
    private protected RecurringJob(string cron, Type commandType)
    {
        ArgumentNullException.ThrowIfNull(cron);
        try
        {
            Schedule = CronSchedule.Parse(cron);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The cron expression of the recurring job " + commandType + " is not valid: " + ex.Message, nameof(cron), ex);
        }
    }

    /// <summary>The cron expression, in UTC.</summary>
    public string Cron => Schedule.Expression;

    /// <summary>The command type.</summary>
    public abstract Type CommandType { get; }

    internal CronSchedule Schedule { get; }

    // Binds the job to its command's job binding; throws when the command is not a job or cannot be created.
    internal abstract RecurringBinding Bind(JobRegistry registry);
}

/// <summary>The recurring job that runs the command <typeparamref name="TCommand"/>.</summary>
/// <typeparam name="TCommand">The command type; a <c>[Job]</c> command.</typeparam>
public sealed class RecurringJob<TCommand> : RecurringJob
    where TCommand : notnull
{
    private readonly Func<TCommand>? _create;

    /// <summary>Creates the recurring job; generated code calls this once per marked command.</summary>
    /// <param name="cron">A five-field cron expression, in UTC; see <see cref="RecurringAttribute"/>.</param>
    /// <param name="create">
    /// Creates the command for each occurrence, or <see langword="null"/> when the command has no
    /// parameterless constructor; such a job fails the host at startup.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="cron"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="cron"/> is not a valid expression, or never occurs.</exception>
    public RecurringJob(string cron, Func<TCommand>? create)
        : base(cron, typeof(TCommand))
    {
        _create = create;
    }

    /// <inheritdoc />
    public override Type CommandType => typeof(TCommand);

    internal override RecurringBinding Bind(JobRegistry registry)
    {
        JobBinding<TCommand> binding;
        try
        {
            binding = registry.Get<TCommand>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("The recurring job " + typeof(TCommand) + " is not a job: mark it [Job] as well as [Recurring].", ex);
        }
        Func<TCommand> create = _create
            ?? throw new InvalidOperationException("The recurring job " + typeof(TCommand) + " has no parameterless constructor to create each occurrence with. Add one.");
        int limit = registry.Options.MaxPayloadBytes;
        return new RecurringBinding(binding, Schedule, at => JobScheduler.Create(binding, create(), limit, new JobPlacement(at, binding.Kind)));
    }
}

/// <summary>A recurring job bound to its command's job binding: what the recurring scheduler runs.</summary>
internal sealed class RecurringBinding
{
    private readonly Func<DateTimeOffset, NewJob> _create;

    public RecurringBinding(JobBinding binding, CronSchedule schedule, Func<DateTimeOffset, NewJob> create)
    {
        Binding = binding;
        Schedule = schedule;
        _create = create;
    }

    public JobBinding Binding { get; }

    public CronSchedule Schedule { get; }

    /// <summary>The job of the occurrence at <paramref name="at"/>, keyed by the job's kind and that time.</summary>
    public NewJob Create(DateTimeOffset at) => _create(at);
}
