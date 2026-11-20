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
  32 bytes in UTF-8 (`RabbitMqTransportOptions.MinDeliveryStampKeyBytes`), or is not well-formed
  text (it holds half of a surrogate pair).
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
