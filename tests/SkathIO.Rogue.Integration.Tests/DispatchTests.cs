using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using Xunit;

namespace SkathIO.Rogue.Integration.Tests;

// ── Test message types ───────────────────────────────────────────────────────

public sealed class Ping : ICommand<string> { }
public sealed class PingHandler : ICommandHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping request, CancellationToken ct)
        => ValueTask.FromResult("pong");
}

public sealed class FireAndForget : ICommand { }
public sealed class FireAndForgetHandler : ICommandHandler<FireAndForget>
{
    public static bool WasCalled;
    public ValueTask Handle(FireAndForget request, CancellationToken ct)
    {
        WasCalled = true;
        return ValueTask.CompletedTask;
    }
}

// A request whose only behavior short-circuits (never calls next) so the handler must not run.
public sealed class ShortCircuited : ICommand<string> { }
public sealed class ShortCircuitedHandler : ICommandHandler<ShortCircuited, string>
{
    public static bool WasCalled;
    public ValueTask<string> Handle(ShortCircuited request, CancellationToken ct)
    {
        WasCalled = true;
        return ValueTask.FromResult("handler-ran");
    }
}
public sealed class ShortCircuitBehavior : IPipelineBehavior<ShortCircuited, string>
{
    public ValueTask<string> Handle(ShortCircuited request, RequestHandlerDelegate<string> next, CancellationToken ct)
        => ValueTask.FromResult("short-circuit"); // never calls next
}

// A notification whose single handler always throws — used to assert first-exception propagation.
public sealed class FailingNotification : IEvent { }
public sealed class FailingNotificationHandler : IEventHandler<FailingNotification>
{
    public ValueTask Handle(FailingNotification notification, CancellationToken ct)
        => throw new System.InvalidOperationException("handler failed");
}

public sealed class CountStream : IStreamQuery<int> { }
public sealed class CountStreamHandler : IStreamQueryHandler<CountStream, int>
{
    public async System.Collections.Generic.IAsyncEnumerable<int> Handle(
        CountStream request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

// A stream request plus an open-generic stream behavior that records whether it was woven into the
// CreateStream dispatch (Phase 4.2.1 / FR-23 acceptance (c)).
public sealed class TickStream : IStreamQuery<int> { }
public sealed class TickStreamHandler : IStreamQueryHandler<TickStream, int>
{
    public async System.Collections.Generic.IAsyncEnumerable<int> Handle(
        TickStream request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}
public sealed class RecordingStreamBehavior<TReq, TRes> : IStreamPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public static int CallCount;
    public System.Collections.Generic.IAsyncEnumerable<TRes> Handle(
        TReq request, StreamHandlerDelegate<TRes> next, CancellationToken ct)
    {
        CallCount++;
        return next();
    }
}

public sealed class OrderPlaced : IEvent { }
public sealed class OrderPlacedHandler1 : IEventHandler<OrderPlaced>
{
    public static int CallCount;
    public ValueTask Handle(OrderPlaced notification, CancellationToken ct)
    {
        CallCount++;
        return ValueTask.CompletedTask;
    }
}
public sealed class OrderPlacedHandler2 : IEventHandler<OrderPlaced>
{
    public static int CallCount;
    public ValueTask Handle(OrderPlaced notification, CancellationToken ct)
    {
        CallCount++;
        return ValueTask.CompletedTask;
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

public sealed class DispatchTests
{
    private static ServiceProvider Build(System.Action<RogueOptions>? configure = null)
    {
        var svc = new ServiceCollection();
        svc.AddRogue(configure);
        return svc.BuildServiceProvider();
    }

    // ── Basic dispatch ────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_InvokesHandler_ReturnsResponse()
    {
        await using var sp = Build();
        var sender = sp.GetRequiredService<ISender>();
        var result = await sender.Send(new Ping());
        Assert.Equal("pong", result);
    }

    [Fact]
    public async Task Send_VoidRequest_CompletesWithoutUnit()
    {
        FireAndForgetHandler.WasCalled = false;
        await using var sp = Build();
        var sender = sp.GetRequiredService<ISender>();
        // No return value forced on caller
        await sender.Send(new FireAndForget());
        Assert.True(FireAndForgetHandler.WasCalled);
    }

    [Fact]
    public async Task IMediator_IsSender_AndPublisher()
    {
        await using var sp = Build();
        var mediator = sp.GetRequiredService<IMediator>();
        // IMediator extends ISender and IPublisher
        Assert.IsAssignableFrom<ISender>(mediator);
        Assert.IsAssignableFrom<IPublisher>(mediator);
        var result = await mediator.Send(new Ping());
        Assert.Equal("pong", result);
    }

    // ── Behavior pipeline ─────────────────────────────────────────────────────

    [Fact]
    public async Task Behavior_ShortCircuit_HandlerNotCalled()
    {
        // ShortCircuitBehavior returns without calling next, so the handler must never run.
        //
        // D5 double-duty (rogue-perf pass 2): ShortCircuited has a CLOSED behavior and this compilation's
        // only open behavior is RecordingStreamBehavior<,> (a STREAM behavior). After the stream-filtered
        // HasUsableOpenBehavior fix, ShortCircuited now gets a static chain (Send_..._ShortCircuited_Chain_1)
        // instead of falling back to PipelineExecutor — so this test exercises the generated chain's
        // short-circuit path (b0 returns without invoking next), through the real DI ISender.Send route.
        ShortCircuitedHandler.WasCalled = false;
        await using var sp = Build();
        var sender = sp.GetRequiredService<ISender>();
        var result = await sender.Send(new ShortCircuited());
        Assert.Equal("short-circuit", result);
        Assert.False(ShortCircuitedHandler.WasCalled);
    }

    // ── Streaming dispatch ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateStream_InvokesHandler_YieldsAllElements()
    {
        await using var sp = Build();
        var mediator = sp.GetRequiredService<IMediator>();
        var collected = new System.Collections.Generic.List<int>();
        await foreach (var item in mediator.CreateStream(new CountStream()))
        {
            collected.Add(item);
        }
        Assert.Equal(new[] { 0, 1, 2 }, collected);
    }

    [Fact]
    public async Task CreateStream_WithStreamBehavior_WeavesBehaviorAroundHandler()
    {
        // FR-23 acceptance (c): a registered IStreamPipelineBehavior<,> is woven into the stream
        // dispatch, its Handle runs, and all handler elements still flow through unchanged.
        RecordingStreamBehavior<TickStream, int>.CallCount = 0;
        await using var sp = Build();
        var mediator = sp.GetRequiredService<IMediator>();

        var collected = new System.Collections.Generic.List<int>();
        await foreach (var item in mediator.CreateStream(new TickStream()))
        {
            collected.Add(item);
        }

        Assert.Equal(new[] { 0, 1 }, collected);
        Assert.Equal(1, RecordingStreamBehavior<TickStream, int>.CallCount);
    }

    // ── Notification dispatch ─────────────────────────────────────────────────

    [Fact]
    public async Task Publish_SequentialStrategy_FirstExceptionPropagates()
    {
        await using var sp = Build();
        var publisher = sp.GetRequiredService<IPublisher>();
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => publisher.Publish(new FailingNotification()).AsTask());
    }

    [Fact]
    public async Task Publish_CallsAllHandlersInOrder()
    {
        OrderPlacedHandler1.CallCount = 0;
        OrderPlacedHandler2.CallCount = 0;
        await using var sp = Build();
        var publisher = sp.GetRequiredService<IPublisher>();
        await publisher.Publish(new OrderPlaced());
        Assert.Equal(1, OrderPlacedHandler1.CallCount);
        Assert.Equal(1, OrderPlacedHandler2.CallCount);
    }

    // ── IRoguePipelineInspector ───────────────────────────────────────────────

    [Fact]
    public void PipelineInspector_IsRegistered()
    {
        using var sp = Build();
        var inspector = sp.GetRequiredService<IRoguePipelineInspector>();
        Assert.NotNull(inspector);
    }

    [Fact]
    public void PipelineInspector_GetPipeline_ReturnsEmptyWhenNoBehaviors()
    {
        using var sp = Build();
        var inspector = sp.GetRequiredService<IRoguePipelineInspector>();
        var pipeline = inspector.GetPipeline<Ping>();
        Assert.NotNull(pipeline);
        Assert.Empty(pipeline);
    }

    [Fact]
    public void PipelineInspector_GetPipeline_WithBehavior_ReturnsBehaviorInfo()
    {
        using var sp = Build();
        var inspector = sp.GetRequiredService<IRoguePipelineInspector>();
        var pipeline = inspector.GetPipeline<ShortCircuited>();
        Assert.NotNull(pipeline);
        var info = Assert.Single(pipeline);
        Assert.Equal(typeof(ShortCircuitBehavior), info.BehaviorType);
    }
}
