using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Tests;

public sealed class WhenAllPublisherTests
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
    public async Task Publish_RunsAllHandlers()
    {
        var count = 0;
        var handlers = new IEventHandler<TestNotification>[]
        {
            new FakeHandler((_, _) => { count++; return ValueTask.CompletedTask; }),
            new FakeHandler((_, _) => { count++; return ValueTask.CompletedTask; }),
        };

        await new WhenAllPublisher().Publish(handlers, new TestNotification(), CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Publish_MultipleExceptions_AggregatesIntoAggregateException()
    {
        var handlers = new IEventHandler<TestNotification>[]
        {
            new FakeHandler((_, _) => throw new InvalidOperationException("one")),
            new FakeHandler((_, _) => throw new InvalidOperationException("two")),
        };

        // `await` unwraps AggregateException; inspect the Task directly to verify aggregation.
        var task = new WhenAllPublisher()
            .Publish(handlers, new TestNotification(), CancellationToken.None)
            .AsTask();
        await task.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);

        Assert.True(task.IsFaulted);
        // task.Exception is always an AggregateException wrapper; .Flatten() unwraps nesting.
        var flattened = task.Exception!.Flatten();
        Assert.Equal(2, flattened.InnerExceptions.Count);
    }
}
