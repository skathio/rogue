using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

public sealed class MessagingConfigurationTests
{
    public static TheoryData<Action<MessagingOptions>, bool> Options => new TheoryData<Action<MessagingOptions>, bool>
    {
        { static o => o.BatchSize = 0, true },
        { static o => o.BatchSize = 1, false },
        { static o => o.BatchSize = MessagingOptions.MaxBatchSize, false },
        { static o => o.BatchSize = MessagingOptions.MaxBatchSize + 1, true },
        { static o => o.RetryDelay = TimeSpan.FromTicks(-1), true },
        { static o => o.RetryDelay = TimeSpan.Zero, false },
        { static o => o.MaxRetryDelay = o.RetryDelay - TimeSpan.FromTicks(1), true },
        { static o => o.MaxRetryDelay = o.RetryDelay, false },
        { static o => o.PollInterval = TimeSpan.Zero, true },
        { static o => o.PollInterval = TimeSpan.FromTicks(1), false },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public void OutOfRangeOptions_AreRefusedByAddRogueMessaging(Action<MessagingOptions> configure, bool refused)
    {
        Exception? thrown = Record.Exception(() => new ServiceCollection().AddRogueMessaging(configure));

        Assert.Equal(refused, thrown is ArgumentOutOfRangeException);
    }

    [Fact]
    public void AddRogueMessageConsumer_TwiceAddsOneHostedConsumer()
    {
        var services = new ServiceCollection();

        services.AddRogueMessageConsumer();
        services.AddRogueMessageConsumer();

        Assert.Single(services, static d => d.ServiceType == typeof(IHostedService));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("orders.*")]
    [InlineData("orders.#")]
    [InlineData("café")]
    [InlineData("line\nbreak")]
    public void InvalidName_IsRefused(string name)
    {
        Assert.False(MessageLimits.IsValidName(name));
        Assert.Throws<ArgumentException>(() => new MessageBinding<StockReport>(name, null));
    }

    [Fact]
    public void NameLength_IsBounded()
    {
        Assert.True(MessageLimits.IsValidName(new string('a', MessageLimits.MaxNameLength)));
        Assert.False(MessageLimits.IsValidName(new string('a', MessageLimits.MaxNameLength + 1)));
        Assert.True(MessageLimits.IsValidName("Orders.Created_v2-eu:1"));
        Assert.Throws<ArgumentNullException>(() => new MessageBinding<StockReport>(null!, null));
    }

    [Fact]
    public void NameTakenByTwoTypes_FailsTheRegistry_NamingBoth()
    {
        var bindings = new MessageBinding[] { new MessageBinding<StockReport>("test.same", null), new MessageBinding<OrderAudited>("test.same", null) };

        var thrown = Assert.Throws<InvalidOperationException>(() => new MessageRegistry(bindings, Covered()));

        Assert.Contains(typeof(StockReport).ToString(), thrown.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(OrderAudited).ToString(), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameBindingTwice_IsKeptOnce()
    {
        var bindings = new MessageBinding[] { new MessageBinding<StockReport>("test.same", null), new MessageBinding<StockReport>("test.same", null) };

        var registry = new MessageRegistry(bindings, Covered());

        Assert.True(registry.TryGet("test.same", out _));
    }

    [Fact]
    public void TypeTheSerializerContextDoesNotCover_FailsTheRegistry_NamingIt()
    {
        var options = new MessagingOptions { SerializerContext = EmptyJson.Default };

        var thrown = Assert.Throws<InvalidOperationException>(() => new MessageRegistry(new[] { new MessageBinding<StockReport>("test.report", null) }, options));

        Assert.Contains(typeof(StockReport).ToString(), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagesWithoutASerializerContext_FailTheRegistry()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => new MessageRegistry(new[] { new MessageBinding<StockReport>("test.report", null) }, new MessagingOptions()));

        Assert.Contains("SerializerContext", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMessages_NeedNoSerializerContext()
    {
        var registry = new MessageRegistry(Enumerable.Empty<MessageBinding>(), new MessagingOptions());

        Assert.False(registry.TryGet("test.report", out _));
    }

    private static MessagingOptions Covered() => new MessagingOptions { SerializerContext = TestMessagesJson.Default };
}

/// <summary>A context that covers no type.</summary>
internal sealed class EmptyJson : JsonSerializerContext
{
    private EmptyJson()
        : base(null)
    {
    }

    public static EmptyJson Default { get; } = new EmptyJson();

    protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null;

    public override JsonTypeInfo? GetTypeInfo(Type type) => null;
}
