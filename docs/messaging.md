# Messaging (`SkathIO.Rogue.Messaging`)

`SkathIO.Rogue.Messaging` sends Rogue events and commands between services. A message type is marked
`[Message]` with a stable name; it travels under that name in a JSON envelope, and on the receiving
side the consumer hands it to its Rogue handlers through code the SkathIO.Rogue source generator
writes. There is no reflection and no type name on the wire, so it works under Native AOT, and a
service in another language can send or receive the same messages.

```bash
dotnet add package SkathIO.Rogue.Messaging
dotnet add package SkathIO.Rogue.Messaging.RabbitMQ   # a transport between processes
```

As with jobs, the core `SkathIO.Rogue` generator emits the message code, so keep both packages on the
same major.minor version (`ROGUE013` warns when they differ). With project references instead of
packages, add the analyzer reference described in
[getting-started](getting-started.md#troubleshooting).

## Declare a message

```csharp
using System.Text.Json.Serialization;
using SkathIO.Rogue;
using SkathIO.Rogue.Messaging;

[Message("orders.placed")]
public sealed record OrderPlaced(int OrderId, decimal Total) : IEvent;

[Message("billing.charge")]
public sealed record ChargeCustomer(int OrderId, decimal Amount) : ICommand;

public sealed class ChargeCustomerHandler : ICommandHandler<ChargeCustomer>
{
    public ValueTask Handle(ChargeCustomer command, CancellationToken cancellationToken)
    {
        // Charge the customer.
        return default;
    }
}

[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(ChargeCustomer))]
internal sealed partial class AppJson : JsonSerializerContext
{
}
```

- The name is 1 to 200 characters: ASCII letters and digits, and `.`, `_`, `-` and `:`. Messages are
  looked up by this name on the way in, never by their type name, so the type can be renamed or moved
  without breaking senders. An invalid name, or one two types share, fails the host at startup.
- A marked `IEvent` is published to its handlers through `IPublisher` when it arrives. A marked
  `ICommand` or `ICommand<TResponse>` is sent to its handler through `ISender` (its response is
  dropped). Any other marked type can be sent but not handled here.
- Every message type must be covered by the `JsonSerializerContext` in
  `MessagingOptions.SerializerContext`.
- The generator binds the `[Message]` types declared in each project that references
  `SkathIO.Rogue`, and `AddRogue` registers them. As for any command, a `[Message]` command needs a
  handler in the project that declares it, or the build fails with `ROGUE001`.

## Register

```csharp
using SkathIO.Rogue;
using SkathIO.Rogue.Messaging;

builder.Services.AddRogue();
builder.Services.AddRogueMessaging(o => o.SerializerContext = AppJson.Default);
builder.Services.AddRogueMessageConsumer();   // only in hosts that should consume
// plus one transport, see below
```

- `AddRogueMessaging` registers `IMessageBus` and the `MessageConsumer`, and turns on forwarding of
  `[Message]` events published through `IPublisher`. `AddRogue` must also be called: it registers the
  message types the generator found.
- `AddRogueMessageConsumer` is the opt-in that consumes. It adds the hosted service that runs the
  consumer. A host that only sends leaves it out.
- An `IMessageTransport` must also be registered.

Only the first call's options apply. An option out of range throws at registration.

## Send

There are two ways to send:

```csharp
using SkathIO.Rogue;
using SkathIO.Rogue.Messaging;

public sealed class PlaceOrderHandler(IPublisher publisher, IMessageBus bus) : ICommandHandler<PlaceOrder>
{
    public async ValueTask Handle(PlaceOrder command, CancellationToken cancellationToken)
    {
        // Runs this service's OrderPlaced handlers, then sends the event to the bus.
        await publisher.Publish(new OrderPlaced(command.OrderId, command.Total), cancellationToken);

        // Sends only: no local handler runs.
        await bus.Publish(new ChargeCustomer(command.OrderId, command.Total), cancellationToken);
    }
}
```

- **`IPublisher.Publish`** of a `[Message]` event runs its local handlers as usual and, once they have
  all succeeded, sends it to the bus. If a local handler throws, nothing is sent.
- **`IMessageBus.Publish`** serializes the message and sends it, without running any local handler.

Each envelope gets a new id, the correlation and causation ids of the message being handled (if
any), and the current W3C trace context. A message whose body is over
`MessagingOptions.MaxMessageBytes` (256 KiB) is not sent; `Publish` throws
`InvalidOperationException`. To send only once your database transaction commits, use the
[outbox](outbox.md).

## Transports

The bus and the consumer talk to the broker through `IMessageTransport`. The transport sends
envelopes and settles each delivery exactly once: ack, nack with a delay, or dead-letter. It reports
what it does natively in `TransportCapabilities` (native delay, native dead-lettering, batched sends,
native deferral), and the consumer fills in the rest.

| Transport | Package | Use |
|-----------|---------|-----|
| `InMemoryTransport` | `SkathIO.Rogue.Messaging` | In one process: tests, and a modular monolith. Holds dead-lettered messages in `DeadLettered`. |
| RabbitMQ | `SkathIO.Rogue.Messaging.RabbitMQ` | Between services: a topic exchange and one quorum queue per consuming service, with dead-lettering, publisher confirms and recovery. See the [RabbitMQ guide](messaging-rabbitmq.md). |

The in-memory transport, as the jobs-and-messaging AOT sample uses it:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Messaging;

var services = new ServiceCollection();
services.AddSingleton<IMessageTransport>(new InMemoryTransport());
services.AddRogue();
services.AddRogueMessaging(o => o.SerializerContext = AppJson.Default);
await using ServiceProvider provider = services.BuildServiceProvider();

await provider.GetRequiredService<IMessageBus>().Publish(new OrderPlaced(42, 9.99m));
int handled = await provider.GetRequiredService<MessageConsumer>().RunOnceAsync();
```

`MessageConsumer.RunOnceAsync` drains what is waiting, so a test needs no hosted service.

RabbitMQ:

```csharp
using SkathIO.Rogue.Messaging.RabbitMQ;

builder.Services.AddRogueRabbitMqTransport(o =>
{
    o.ConnectionUri = new Uri(builder.Configuration["RabbitMq:ConnectionUri"]!);
    o.ServiceName = "billing";
    o.DeliveryStampKey = builder.Configuration["RabbitMq:DeliveryStampKey"];
});
```

The RabbitMQ transport needs a `DeliveryStampKey` secret shared by every instance of the service;
the [RabbitMQ guide](messaging-rabbitmq.md#the-delivery-stamp-key) explains why and how to keep it.

## The consumer

The consumer receives up to `BatchSize` (100) deliveries at a time and handles at most
`MaxConcurrency` at once, each message in a service scope of its own. When it is idle it receives
again after `PollInterval` (1 second).

- A message with an unknown name, a body over `MaxMessageBytes`, or a body that is not JSON of its
  type within `MaxJsonDepth` (32) is dead-lettered at once, without running any handler.
- When an `IMessageInbox` is registered (the [outbox package](outbox.md#the-inbox) adds one), a
  message the inbox has already seen handled is acked without running its handlers again.
- On shutdown, running handlers are cancelled and their messages put back with no delay, so being
  stopped never uses up an attempt.
- Logs carry a message's id and name, never its body. Traces and metrics are emitted under
  `RogueMessagingTelemetry.Name` (`"SkathIO.Rogue.Messaging"`).

### Own-origin events

An event published through `IPublisher` has already run its handlers in the sending service. The bus
stamps the transport's `ServiceName` on its envelope as its `Origin`, and the consumer acks a received
event whose origin is its own service name without running its handlers, so they do not run a second
time when the service also consumes the event. Instances of the same service share the name, so the
event is handled once per service, by the instance that published it. A message sent through
`IMessageBus.Publish` carries no origin and is always handled. (The origin is the sender's claim, but
a forged one only drops events the forger itself sent.)

### Retries and dead-lettering

When a handler throws, the message is delivered again after a delay:

- The first redelivery waits `RetryDelay` (5 seconds), and each later one waits twice as long, up to
  `MaxRetryDelay` (5 minutes).
- After `MaxAttempts` (5) deliveries have failed, the message is dead-lettered with the reason. The
  RabbitMQ transport moves it to the service's `<ServiceName>.dead-letter` queue.
- A transport with native delay redelivers it later on the broker. On one without (RabbitMQ), the
  consumer waits out the delay itself, within limits described in the
  [RabbitMQ guide](messaging-rabbitmq.md#retries-and-the-prefetch-window).

```csharp
builder.Services.AddRogueMessaging(o =>
{
    o.SerializerContext = AppJson.Default;
    o.MaxAttempts = 5;
    o.RetryDelay = TimeSpan.FromSeconds(5);
    o.MaxRetryDelay = TimeSpan.FromMinutes(5);
    o.MaxConcurrency = 16;
    o.MaxMessageBytes = 256 * 1024;
});
```

`RetryDelay` is at most 24 hours, and so is `PollInterval` when `RetryDelay` is zero, because they
also set how long a delivery the inbox turns away as busy stays away.

## See also

- [RabbitMQ transport](messaging-rabbitmq.md) — topology, the delivery stamp key, retries and the wait queue.
- [Outbox and inbox](outbox.md) — publish in your database transaction; handle a duplicate delivery once.
- [Background jobs](jobs.md) — a job handler can publish messages.
