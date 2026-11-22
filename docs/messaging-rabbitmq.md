# RabbitMQ transport

`SkathIO.Rogue.Messaging.RabbitMQ` carries `SkathIO.Rogue.Messaging` messages over RabbitMQ.

Read the [messaging guide](messaging.md) first for messages, the bus, the consumer and retries, and
the [outbox and inbox guide](outbox.md) to publish in a database transaction and handle a duplicate
delivery once.

## Register

```csharp
using SkathIO.Rogue.Messaging.RabbitMQ;

builder.Services.AddRogue();
builder.Services.AddRogueMessaging(o => o.SerializerContext = AppJson.Default);
builder.Services.AddRogueMessageConsumer();
builder.Services.AddRogueRabbitMqTransport(o =>
{
    o.ConnectionUri = new Uri(builder.Configuration["RabbitMq:ConnectionUri"]!);
    o.ServiceName = "billing";
    o.DeliveryStampKey = builder.Configuration["RabbitMq:DeliveryStampKey"];
});
```

## The delivery stamp key

When a handler fails, the transport puts a copy of the message back on the service's queue with its
delivery count, signed with HMAC-SHA256 under `DeliveryStampKey`. The count is read only from that
signature, so a sender cannot shorten a message's retries by setting a header.

- **Required.** There is no default. `AddRogueRabbitMqTransport` (and the `RabbitMqTransport`
  constructor) throws an `ArgumentException` at startup when the key is missing or shorter than
  32 bytes in UTF-8 (`RabbitMqTransportOptions.MinDeliveryStampKeyBytes`), is not well-formed
  text (it holds half of a surrogate pair), or starts or ends with white space. A trailing newline
  or space is easily added or dropped by one instance's configuration file or secret store and not
  another's, and the instances would then hold different keys; check the value you store has none.
- **The same on every instance of a service.** Any instance can take the copy another one put back.
  With the same key it reads the count, so a message that always fails is dead-lettered after
  `MaxAttempts` deliveries. With a different key the count starts again from 1, and the message can
  be retried for much longer than `MaxAttempts`.
- **A secret.** Keep it out of source control, as you would a password: user-secrets in
  development, and a secret store or protected environment variable in production. The package
  never logs the key and never puts it in an exception message.

Generate a key (64 characters, so 64 bytes in UTF-8):

```bash
openssl rand -hex 32
```

In development, keep it in user-secrets:

```bash
dotnet user-secrets init
dotnet user-secrets set "RabbitMq:DeliveryStampKey" "<the generated key>"
```

In production, give each instance the same value from your secret store, for example as the
environment variable `RabbitMq__DeliveryStampKey`, which `builder.Configuration` reads as
`RabbitMq:DeliveryStampKey`.

To rotate the key, deploy the new key to every instance at once. Copies already in retry under the
old key count again from 1.

## Retries and the prefetch window

RabbitMQ has no native delay for a single message, so the consumer waits out a failed message's
retry delay itself, with the delivery still unsettled. The broker sends at most `PrefetchCount`
unsettled deliveries, and the transport reports that number as its unsettled window
(`IMessageTransport.UnsettledLimit`). At most half of the window waits out a retry delay at once. A
message that fails while that half is full is nacked at once instead: it goes back on the queue with
no delay and its delivery count one higher, so it is still dead-lettered after `MaxAttempts`
deliveries. Messages that keep failing therefore never take the whole window from messages that
would succeed, but past that half they may be retried sooner than `RetryDelay`. With
`PrefetchCount` 1 no failed message waits.

Failed retries stay in the consumer by design and do not use the wait queue below; only busy
deferrals do. A retry's delay doubles on each attempt, and the wait queue expires messages only from
its head, once those ahead of them have expired, so a long backoff there would hold back every busy
copy queued behind it.

## Busy deliveries and the wait queue

With an inbox, a delivery whose id another delivery holds is deferred: put back unhandled, its
delivery count unchanged. It stays away `RetryDelay`, or `PollInterval` when `RetryDelay` is zero,
and it waits on the broker, not in the consumer. The transport reports
`TransportCapabilities.NativeDeferDelay`, so the consumer hands it the delay instead of waiting: a
busy delivery holds no prefetch place and no concurrency slot, at any `PrefetchCount`, however many
duplicates of one id arrive, and comes round again once per delay for as long as the id stays busy.

Each consuming service gets this topology, declared when the transport connects:

| Name | Kind | Arguments | Role |
| --- | --- | --- | --- |
| `Exchange` (`rogue`) | topic exchange, durable | | every message, routed by its name |
| `ServiceName` | quorum queue, durable | `x-dead-letter-exchange` = `<Exchange>.dead-letter`, `x-dead-letter-routing-key` = `ServiceName` | the service's messages |
| `<Exchange>.dead-letter` | direct exchange, durable | | dead letters, routed by service name |
| `<ServiceName>.dead-letter` | quorum queue, durable | | the messages the service gave up on |
| `<ServiceName>.wait` | quorum queue, durable | `x-dead-letter-exchange` = `""` (the default exchange), `x-dead-letter-routing-key` = `ServiceName`, `x-dead-letter-strategy` = `at-least-once`, `x-overflow` = `reject-publish`, `x-message-ttl` = `86400000` (24 hours) | deferred copies waiting out their delay |

`RabbitMqTransportOptions.WaitQueue` gives the wait queue's name. No consumer reads it. A deferred
copy goes there through the default exchange with its delay, in whole milliseconds rounded up, as its
per-message expiration (`expiration`), and with its signed delivery count. When it expires, the
broker dead-letters it to the default exchange with the service's name as routing key, so it lands
back on the service's queue with an `x-death` header whose reason is `expired`. It arrives through
the default exchange, where the transport reads its signed count, so a deferral never costs an
attempt. The wait queue's dead-letter target is the service's queue, not the dead-letter queue:
expiring there is the end of a wait, not a failure. At-least-once dead-lettering keeps each copy in
the wait queue until the service's queue has confirmed it, so a full or unavailable service queue
delays a copy rather than loses it; quorum queues allow that strategy only with `reject-publish`
overflow. The wait queue sets no length limit of its own. Message TTL and at-least-once dead-lettering on quorum
queues need RabbitMQ 3.10 or later; on an older broker the wait queue cannot be declared and the
transport does not connect.

A queue expires a message only from its head. Every busy delivery of one service is deferred for the
same delay, so the copies leave in order; a caller of `DeferAsync` that mixes delays makes a shorter
one wait behind a longer one ahead of it. A delay is held to at most 24 hours, the wait queue's own
message TTL. `AddRogueMessaging` refuses a `RetryDelay` over 24 hours, and a `PollInterval` over 24
hours when `RetryDelay` is zero, since those set how long a busy delivery stays away.

A delivery deferred with no delay, as one whose handlers were stopped by shutdown is, skips the wait
queue: its copy goes straight back to the service's queue with its signed delivery count unchanged,
so it comes back at once and never waits behind busy copies with a longer delay. Any delay above
zero, however short, goes through the wait queue.

The wait queue's own message TTL (`x-message-ttl`) is 24 hours, the longest delay a copy is given,
so it never cuts a deferral short. A message put on the wait queue by anything but the transport,
with no expiration or a longer one, therefore leaves it at most 24 hours after it arrived there
(the TTL runs from its arrival, and nothing ahead of it stays longer), and goes back to the
service's queue like any copy. Until then it holds back every deferred copy behind it. Keep others from publishing to it (see "Who may publish
to the default exchange"), and purge it if that happens.

The wait queue is new in this version. A service that upgrades gets it on its next connection; the
service's queue and the dead-letter queue keep their arguments, so nothing needs to be deleted.
RabbitMQ cannot change a queue's arguments after its declaration: a redeclare with other arguments
is refused and the transport does not connect. A later change to the wait queue's arguments, its
message TTL included, therefore means deleting the wait queue (once it is empty) so the transport
declares it again.

## When the broker refuses a copy

When the broker refuses the copy of a failed message, the original is requeued, and the broker's
redelivery counts as the attempt that failed. When the wait queue refuses the copy of a deferred
delivery (it was deleted or limited by a policy, or the broker refused the publish), the original is
dead-lettered to the service's dead-letter queue with its delivery count and the reason
`Deferred, but the wait queue refused its copy; replay it once its id is free.`, and the consumer
logs one warning that the settle failed. A delivery deferred with no delay whose copy the service's
queue refuses (it is full, for example) is handled the same way, with the reason
`Deferred with no delay, but the service's queue refused its copy; replay it once the queue accepts messages.`
Neither is requeued, which would bring it straight back, nor handed to the consumer again, so a
refused deferral cannot go round between the broker and the consumer at their speed. If that
dead-letter copy is refused too, the original is nacked to the queue's dead-letter exchange without
a reason. Replay it from the dead-letter queue once the queue that refused it accepts copies again.

## Who may publish to the default exchange

The transport puts its retry copies back on the service's queue, and its deferred copies on the wait
queue, through the default exchange, and names each by its AMQP type property. Anyone allowed to write to the default exchange can therefore
put a message on the queue. The type property names it only when it is a name the service receives
(one of its subscriptions, or a name a `MapRoutingKey` or `MapHeader` gives); any other is
dead-lettered unread. Within those names, though, such a sender can still make the service handle a
message of its choosing. Limit write permission on the default exchange (the empty exchange name,
`amq.default`) in the virtual host to the service itself, with RabbitMQ user permissions.

## Delivery count on a redelivery

When a consumer's channel or process stops before it settles a delivery, the broker delivers it
again with the redelivered flag set. The quorum queue counts how many times it took the delivery back
in its `x-delivery-count` header. The transport adds that count to the signed count, held to
1 to 1,000,000, so a message that crashes the process each time still reaches `MaxAttempts`. A
redelivery without the header, or with a value that is not an integer, counts one more.
