using System;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqTransportOptionsTests
{
    private static readonly string _longest = new string('a', RabbitMqTransportOptions.MaxNameLength);

    public static TheoryData<Action<RabbitMqTransportOptions>, Type?> Options => new TheoryData<Action<RabbitMqTransportOptions>, Type?>
    {
        { static o => { }, null },
        { static o => o.PrefetchCount = 0, typeof(ArgumentOutOfRangeException) },
        { static o => o.PrefetchCount = 1, null },
        { static o => o.PrefetchCount = ushort.MaxValue, null },
        { static o => o.PrefetchCount = ushort.MaxValue + 1, typeof(ArgumentOutOfRangeException) },
        { static o => o.Exchange = null!, typeof(ArgumentNullException) },
        { static o => o.Exchange = "", typeof(ArgumentException) },
        { static o => o.Exchange = "has space", typeof(ArgumentException) },
        { static o => o.Exchange = "orders.#", typeof(ArgumentException) },
        { static o => o.Exchange = "amq.topic", typeof(ArgumentException) },
        { static o => o.Exchange = new string('a', RabbitMqTransportOptions.MaxNameLength - ".dead-letter".Length), null },
        { static o => o.Exchange = new string('a', RabbitMqTransportOptions.MaxNameLength - ".dead-letter".Length + 1), typeof(ArgumentException) },
        { static o => o.ServiceName = null, null },
        { static o => o.ServiceName = "orders-service", null },
        { static o => o.ServiceName = "", typeof(ArgumentException) },
        { static o => o.ServiceName = "amq.gen-1", typeof(ArgumentException) },
        { static o => o.ServiceName = "café", typeof(ArgumentException) },
        { static o => o.ServiceName = new string('a', RabbitMqTransportOptions.MaxNameLength - ".dead-letter".Length + 1), typeof(ArgumentException) },
        { static o => o.ConnectionUri = null!, typeof(ArgumentNullException) },
        { static o => o.ConnectionUri = new Uri("amqps://broker:5671/vhost"), null },
        { static o => o.ConnectionUri = new Uri("http://broker"), typeof(ArgumentException) },
        { static o => o.ConnectionUri = new Uri("/relative", UriKind.Relative), typeof(ArgumentException) },
        { static o => o.MapRoutingKey("legacy", "legacy.order", "orders.placed"), null },
        { static o => o.MapRoutingKey("rogue", "legacy.order", "orders.placed"), typeof(ArgumentException) },
        { static o => o.MapRoutingKey("shop", "legacy.order", "orders.placed").Exchange = "shop", typeof(ArgumentException) },
        { static o => o.MapRoutingKey("rogue", "legacy.order", "orders.placed").Exchange = "shop", null },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public void Options_AreCheckedWhenTheTransportIsRegistered(Action<RabbitMqTransportOptions> configure, Type? refused)
    {
        Exception? thrown = Record.Exception(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddRogueRabbitMqTransport(o =>
        {
            o.DeliveryStampKey = RabbitMqHost.DeliveryStampKey;
            configure(o);
        }));

        Assert.Equal(refused, thrown?.GetType());
    }

    [Theory]
    [InlineData("legacy", "legacy.order", "orders.placed", null)]
    [InlineData("legacy", "legacy.*", "orders.placed", typeof(ArgumentException))]
    [InlineData("legacy", "legacy.#", "orders.placed", typeof(ArgumentException))]
    [InlineData("legacy", "has space", "orders.placed", typeof(ArgumentException))]
    [InlineData("legacy", "", "orders.placed", typeof(ArgumentException))]
    [InlineData("legacy", "legacy.order", "not a name", typeof(ArgumentException))]
    [InlineData("amq.topic", "legacy.order", "orders.placed", typeof(ArgumentException))]
    [InlineData(null, "legacy.order", "orders.placed", typeof(ArgumentNullException))]
    [InlineData("legacy", null, "orders.placed", typeof(ArgumentNullException))]
    [InlineData("legacy", "legacy.order", null, typeof(ArgumentNullException))]
    public void MapRoutingKey_RefusesWildcardsAndInvalidNames(string? exchange, string? routingKey, string? messageName, Type? refused)
    {
        Exception? thrown = Record.Exception(() => new RabbitMqTransportOptions().MapRoutingKey(exchange!, routingKey!, messageName!));

        Assert.Equal(refused, thrown?.GetType());
    }

    [Theory]
    [InlineData("legacy.#", null)]
    [InlineData("legacy.*.v1", null)]
    [InlineData("legacy.order", null)]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("has space", typeof(ArgumentException))]
    [InlineData(null, typeof(ArgumentNullException))]
    public void Bind_AllowsWildcardKeys_AndRefusesInvalidOnes(string? bindingKey, Type? refused)
    {
        Exception? thrown = Record.Exception(() => new RabbitMqTransportOptions().Bind("legacy", bindingKey!));

        Assert.Equal(refused, thrown?.GetType());
    }

    [Theory]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("amq.direct", typeof(ArgumentException))]
    [InlineData("has space", typeof(ArgumentException))]
    [InlineData(null, typeof(ArgumentNullException))]
    public void Bind_RefusesAnInvalidOrReservedExchange(string? exchange, Type? refused)
    {
        Exception? thrown = Record.Exception(() => new RabbitMqTransportOptions().Bind(exchange!, "legacy.#"));

        Assert.Equal(refused, thrown?.GetType());
    }

    [Theory]
    [InlineData("event-type", "OrderPlaced", "orders.placed", null)]
    [InlineData("", "OrderPlaced", "orders.placed", typeof(ArgumentException))]
    [InlineData("event type", "OrderPlaced", "orders.placed", typeof(ArgumentException))]
    [InlineData("event-type", "", "orders.placed", typeof(ArgumentException))]
    [InlineData("event-type", "line\nbreak", "orders.placed", typeof(ArgumentException))]
    [InlineData("event-type", "OrderPlaced", "orders.#", typeof(ArgumentException))]
    [InlineData(null, "OrderPlaced", "orders.placed", typeof(ArgumentNullException))]
    [InlineData("event-type", null, "orders.placed", typeof(ArgumentNullException))]
    [InlineData("event-type", "OrderPlaced", null, typeof(ArgumentNullException))]
    public void MapHeader_RefusesInvalidHeadersValuesAndNames(string? header, string? value, string? messageName, Type? refused)
    {
        Exception? thrown = Record.Exception(() => new RabbitMqTransportOptions().MapHeader(header!, value!, messageName!));

        Assert.Equal(refused, thrown?.GetType());
    }

    [Fact]
    public void MapLengths_AreBoundedByTheAmqpShortString()
    {
        var options = new RabbitMqTransportOptions();

        options.MapHeader(_longest, _longest, "orders.placed").Bind("legacy", _longest);
        Assert.Throws<ArgumentException>(() => options.Bind("legacy", _longest + "a"));
        Assert.Throws<ArgumentException>(() => options.MapHeader(_longest + "a", "v", "orders.placed"));
        Assert.Throws<ArgumentException>(() => options.MapHeader("h", _longest + "a", "orders.placed"));
        Assert.Throws<ArgumentException>(() => options.MapRoutingKey("legacy", _longest + "a", "orders.placed"));
    }

    [Fact]
    public void DeadLetterNames_FollowTheExchangeAndService()
    {
        var options = new RabbitMqTransportOptions { Exchange = "shop", ServiceName = "billing" };

        Assert.Equal("shop.dead-letter", options.DeadLetterExchange);
        Assert.Equal("billing.dead-letter", options.DeadLetterQueue);
        Assert.Null(new RabbitMqTransportOptions().DeadLetterQueue);
    }

    [Fact]
    public void TheTransport_KeepsACopy_SoLaterChangesHaveNoEffect()
    {
        var options = new RabbitMqTransportOptions { ServiceName = "billing", DeliveryStampKey = RabbitMqHost.DeliveryStampKey };
        var transport = new RabbitMqTransport(options, Array.Empty<string>());

        options.ServiceName = "other";
        options.MapRoutingKey("legacy", "legacy.order", "orders.placed").MapHeader("event-type", "OrderPlaced", "orders.placed").Bind("legacy", "legacy.#");

        Assert.Equal("billing", transport.Options.ServiceName);
        Assert.Empty(transport.Options.RoutingKeyMaps);
        Assert.Empty(transport.Options.HeaderMaps);
        Assert.Empty(transport.Options.Bindings);
    }

    [Fact]
    public void TheTransport_ChecksItsOptionsAndSubscriptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMqTransport(new RabbitMqTransportOptions { PrefetchCount = 0, DeliveryStampKey = RabbitMqHost.DeliveryStampKey }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey }, new[] { "orders.#" }));
        Assert.Throws<ArgumentNullException>(() => new RabbitMqTransport(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => new RabbitMqTransport(new RabbitMqTransportOptions(), null!));
    }
}
