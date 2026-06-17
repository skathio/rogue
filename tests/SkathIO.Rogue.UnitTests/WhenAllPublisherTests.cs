using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Tests;

public sealed class WhenAllPublisherTests
{
    private sealed class TestNotification : IEvent { }

    [Fact]
    public async Task Publish_RunsAllHandlers()
    {
        var count = 0;
        var executors = new[]
        {
            new EventHandlerExecutor(new object(), (_, _) => { count++; return ValueTask.CompletedTask; }),
            new EventHandlerExecutor(new object(), (_, _) => { count++; return ValueTask.CompletedTask; }),
        };

        await new WhenAllPublisher().Publish(executors, new TestNotification(), CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Publish_MultipleExceptions_AggregatesIntoAggregateException()
    {
        var executors = new[]
        {
            new EventHandlerExecutor(new object(), (_, _) => throw new InvalidOperationException("one")),
            new EventHandlerExecutor(new object(), (_, _) => throw new InvalidOperationException("two")),
        };

        // `await` unwraps AggregateException; inspect the Task directly to verify aggregation.
        var task = new WhenAllPublisher()
            .Publish(executors, new TestNotification(), CancellationToken.None)
            .AsTask();
        await task.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);

        Assert.True(task.IsFaulted);
        // task.Exception is always an AggregateException wrapper; .Flatten() unwraps nesting.
        var flattened = task.Exception!.Flatten();
        Assert.Equal(2, flattened.InnerExceptions.Count);
    }
}
