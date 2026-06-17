using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Tests;

public sealed class PipelineExecutorTests
{
    private sealed class PingRequest : ICommand<string> { }

    // Static to avoid lambda allocation inside the measurement window (NFR-PERF-1 gate)
    private static readonly RequestHandlerDelegate<string> PongHandler = () => ValueTask.FromResult("pong");

    [Fact]
    public async Task Execute_NoBehaviors_CallsHandlerDirectly()
    {
        var result = await PipelineExecutor.Execute<PingRequest, string>(
            new PingRequest(),
            [],
            () => ValueTask.FromResult("pong"),
            CancellationToken.None);

        Assert.Equal("pong", result);
    }

    [Fact]
    public async Task Execute_SingleBehavior_WrapsHandler()
    {
        var log = new List<string>();

        ValueTask<string> BehaviorImpl(PingRequest _, RequestHandlerDelegate<string> next, CancellationToken __)
        {
            log.Add("before");
            var r = next();
            log.Add("after");
            return r;
        }

        var behaviors = new[] { new DelegateBehavior<PingRequest, string>(BehaviorImpl) };
        var result = await PipelineExecutor.Execute<PingRequest, string>(
            new PingRequest(),
            behaviors,
            () => { log.Add("handler"); return ValueTask.FromResult("pong"); },
            CancellationToken.None);

        Assert.Equal("pong", result);
        Assert.Equal(new[] { "before", "handler", "after" }, log);
    }

    [Fact]
    public async Task Execute_BehaviorShortCircuits_HandlerNotCalled()
    {
        var handlerCalled = false;

        ValueTask<string> ShortCircuit(PingRequest _, RequestHandlerDelegate<string> next, CancellationToken __)
            => ValueTask.FromResult("short-circuit");

        var behaviors = new[] { new DelegateBehavior<PingRequest, string>(ShortCircuit) };
        var result = await PipelineExecutor.Execute<PingRequest, string>(
            new PingRequest(),
            behaviors,
            () => { handlerCalled = true; return ValueTask.FromResult("pong"); },
            CancellationToken.None);

        Assert.Equal("short-circuit", result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public void Execute_NoBehaviors_ZeroAllocations()
    {
        var request = new PingRequest();
        // Warm up JIT — use the same static delegate to avoid spurious first-run allocs
        for (var i = 0; i < 100; i++)
        {
            PipelineExecutor.Execute<PingRequest, string>(request, [], PongHandler, CancellationToken.None);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            // Force completion without allocating a Task
            var vt = PipelineExecutor.Execute<PingRequest, string>(request, [], PongHandler, CancellationToken.None);
            Assert.True(vt.IsCompleted);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        // Per-operation delta of zero over 1000 iterations — robust against tiered-JIT noise.
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public async Task Execute_TwoBehaviors_OrderIsOuterInner()
    {
        var log = new List<string>();

        var behaviors = new IPipelineBehavior<PingRequest, string>[]
        {
            new DelegateBehavior<PingRequest, string>(async (_, next, _) =>
            {
                log.Add("b1-before");
                var r = await next();
                log.Add("b1-after");
                return r;
            }),
            new DelegateBehavior<PingRequest, string>(async (_, next, _) =>
            {
                log.Add("b2-before");
                var r = await next();
                log.Add("b2-after");
                return r;
            }),
        };

        await PipelineExecutor.Execute<PingRequest, string>(
            new PingRequest(), behaviors,
            () => { log.Add("handler"); return ValueTask.FromResult("pong"); },
            CancellationToken.None);

        Assert.Equal(new[] { "b1-before", "b2-before", "handler", "b2-after", "b1-after" }, log);
    }

    private sealed class DelegateBehavior<TReq, TRes> : IPipelineBehavior<TReq, TRes>
        where TReq : notnull
    {
        private readonly Func<TReq, RequestHandlerDelegate<TRes>, CancellationToken, ValueTask<TRes>> _impl;

        public DelegateBehavior(Func<TReq, RequestHandlerDelegate<TRes>, CancellationToken, ValueTask<TRes>> impl)
        {
            _impl = impl;
        }

        public ValueTask<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken cancellationToken)
            => _impl(request, next, cancellationToken);
    }
}
