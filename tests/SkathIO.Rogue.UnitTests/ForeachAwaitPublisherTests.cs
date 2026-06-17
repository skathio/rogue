using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Tests;

public sealed class ForeachAwaitPublisherTests
{
    private sealed class TestNotification : IEvent { }

    [Fact]
    public async Task Publish_CallsHandlersInOrder()
    {
        var order = new List<int>();
        var executors = new[]
        {
            new EventHandlerExecutor(new object(), (_, _) => { order.Add(1); return ValueTask.CompletedTask; }),
            new EventHandlerExecutor(new object(), (_, _) => { order.Add(2); return ValueTask.CompletedTask; }),
        };

        await new ForeachAwaitPublisher().Publish(executors, new TestNotification(), CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task Publish_FirstExceptionWins_LaterHandlersNotCalled()
    {
        var called = false;
        var executors = new[]
        {
            new EventHandlerExecutor(new object(), (_, _) => throw new InvalidOperationException("first")),
            new EventHandlerExecutor(new object(), (_, _) => { called = true; return ValueTask.CompletedTask; }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ForeachAwaitPublisher().Publish(executors, new TestNotification(), CancellationToken.None).AsTask());

        Assert.False(called);
    }

    [Fact]
    public async Task Publish_ZeroHandlers_Completes()
    {
        await new ForeachAwaitPublisher().Publish([], new TestNotification(), CancellationToken.None);
    }
}
