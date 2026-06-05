using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using Xunit;

namespace SkathIO.Rogue.Integration.Tests;

/// <summary>
/// Smoke tests verifying the generator-produced dispatch compiles and executes correctly.
/// The 0-alloc guarantee (NFR-PERF-1, AC-C) is tested via PipelineExecutor unit tests in
/// SkathIO.Rogue.UnitTests, which call PipelineExecutor.Execute directly. The ISender path
/// boxes ValueTask per PD-12; the concrete dispatcher methods are 0-alloc but internal.
/// </summary>
public sealed class SmokePerfTests
{
    [Fact]
    public async System.Threading.Tasks.Task AddRogue_WiresDispatch_NoBehaviors()
    {
        var services = new ServiceCollection();
        services.AddRogue();
        await using var sp = services.BuildServiceProvider();
        var sender = sp.GetRequiredService<ISender>();
        var result = await sender.Send(new Ping(), CancellationToken.None);
        Assert.Equal("pong", result);
    }
}
