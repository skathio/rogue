# RabbitMQ transport

`SkathIO.Rogue.Messaging.RabbitMQ` carries `SkathIO.Rogue.Messaging` messages over RabbitMQ.

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

RabbitMQ has no native delay, so the consumer waits out a failed message's retry delay itself, with
the delivery still unsettled. The broker sends at most `PrefetchCount` unsettled deliveries, and the
transport reports that number as its unsettled window (`IMessageTransport.UnsettledLimit`). At most
half of the window waits out a retry delay at once. A message that fails while that half is full is
nacked at once instead: it goes back on the queue with no delay and its delivery count one higher,
so it is still dead-lettered after `MaxAttempts` deliveries. Messages that keep failing therefore
never take the whole window from messages that would succeed, but past that half they may be retried
sooner than `RetryDelay`. With `PrefetchCount` 1 no failed message waits.

With an inbox, a delivery whose id another delivery holds is put back unhandled, its delivery count
unchanged. It waits `RetryDelay` first, or `PollInterval` when `RetryDelay` is zero, and it waits
within the same half of the window as the failed messages. While that half is full, such a delivery
is put back at once, so duplicates of one id cannot take the whole window either; until the id is
free, those past the half go round between the queue and the consumer with no delay. With
`PrefetchCount` 1 none of them waits.

When the broker refuses the copy of a failed message, the original is requeued, and the broker's
redelivery counts as the attempt that failed. When it refuses the copy of a deferred delivery, the
original is not requeued: the same consumer receives it again, still unsettled, with the count it
had. It goes back to the broker only when the channel closes or the transport is disposed, which
counts one more delivery like any other redelivery (see below).

## Who may publish to the default exchange

The transport puts its retry copies back on the service's queue through the default exchange, and
names each by its AMQP type property. Anyone allowed to write to the default exchange can therefore
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
