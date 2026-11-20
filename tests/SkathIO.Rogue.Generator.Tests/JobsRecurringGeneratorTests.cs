using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// How the generator handles a <c>[Recurring]</c> command it cannot run: the consumer still builds, and
/// the host fails at startup naming what is missing, instead of the build failing inside generated code.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class JobsRecurringGeneratorTests
{
    private const string Handlers = @"
public sealed class NeedsIdHandler : ICommandHandler<NeedsId>
{
    public ValueTask Handle(NeedsId command, CancellationToken cancellationToken) => default;
}
public sealed class NotAJobHandler : ICommandHandler<NotAJob>
{
    public ValueTask Handle(NotAJob command, CancellationToken cancellationToken) => default;
}";

    [Fact]
    public void RecurringCommand_WithoutAParameterlessConstructor_BuildsWithNoFactory_AndFailsStartupNamingIt()
    {
        const string source = @"
[Job]
[Recurring(""0 * * * *"")]
public sealed record NeedsId(int Id) : ICommand;
public sealed record NotAJob : ICommand;" + Handlers;

        string generated = Generated(source);
        SchedulingConsumer consumer = SchedulingConsumer.Compile(source);
        var clock = new HardeningClock();
        using ServiceProvider host = consumer.Host(new SchedulingLog(clock), new InMemoryJobStore(clock), worker: false);

        Assert.Contains("RecurringJob<global::Scheduling.NeedsId>(\"0 * * * *\", null)", generated, StringComparison.Ordinal);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => host.GetRequiredService<IJobScheduler>());
        Assert.Contains("Scheduling.NeedsId", error.Message, StringComparison.Ordinal);
        Assert.Contains("parameterless", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringCommand_WithoutJob_IsNotAJob_AndFailsStartupNamingTheAttribute()
    {
        const string source = @"
[Job]
public sealed record NeedsId(int Id) : ICommand;
[Recurring(""*/5 * * * *"")]
public sealed record NotAJob : ICommand;" + Handlers;

        string generated = Generated(source);
        SchedulingConsumer consumer = SchedulingConsumer.Compile(source);
        var clock = new HardeningClock();
        using ServiceProvider host = consumer.Host(new SchedulingLog(clock), new InMemoryJobStore(clock), worker: false);

        Assert.Contains("RecurringJob<global::Scheduling.NotAJob>(\"*/5 * * * *\", static () => new global::Scheduling.NotAJob())", generated, StringComparison.Ordinal);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => host.GetRequiredService<IJobScheduler>());
        Assert.Contains("[Job]", error.Message, StringComparison.Ordinal);
    }

    private static string Generated(string source)
        => SchedulingConsumer.Generate(source).Single(static s => s.Name.EndsWith("RogueJobs.g.cs", StringComparison.Ordinal)).Text;
}
