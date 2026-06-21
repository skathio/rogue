using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Tests;

public sealed class ForeachAwaitPublisherTests
{
    private sealed class TestNotification : IEvent { }

    /// <summary>A test handler whose <see cref="Handle"/> behavior is supplied by the caller, replacing
    /// the now-removed <c>EventHandlerExecutor</c> wrapper (publish-fanout-perf D2).</summary>
    private sealed class FakeHandler : IEventHandler<TestNotification>
    {
        private readonly Func<TestNotification, CancellationToken, ValueTask> _handle;

        public FakeHandler(Func<TestNotification, CancellationToken, ValueTask> handle) => _handle = handle;

#if NETSTANDARD2_0
        public async ValueTask<Unit> Handle(TestNotification @event, CancellationToken cancellationToken)
        {
            await _handle(@event, cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }
#else
        public ValueTask Handle(TestNotification @event, CancellationToken cancellationToken)
            => _handle(@event, cancellationToken);
#endif
    }

    [Fact]
    public async Task Publish_CallsHandlersInOrder()
    {
        var order = new List<int>();
        var handlers = new IEventHandler<TestNotification>[]
        {
            new FakeHandler((_, _) => { order.Add(1); return ValueTask.CompletedTask; }),
            new FakeHandler((_, _) => { order.Add(2); return ValueTask.CompletedTask; }),
        };

        await new ForeachAwaitPublisher().Publish(handlers, new TestNotification(), CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task Publish_FirstExceptionWins_LaterHandlersNotCalled()
    {
        var called = false;
        var handlers = new IEventHandler<TestNotification>[]
        {
            new FakeHandler((_, _) => throw new InvalidOperationException("first")),
            new FakeHandler((_, _) => { called = true; return ValueTask.CompletedTask; }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ForeachAwaitPublisher().Publish(handlers, new TestNotification(), CancellationToken.None).AsTask());

        Assert.False(called);
    }

    [Fact]
    public async Task Publish_ZeroHandlers_Completes()
    {
        await new ForeachAwaitPublisher().Publish(Array.Empty<IEventHandler<TestNotification>>(), new TestNotification(), CancellationToken.None);
    }
}
