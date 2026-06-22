# Getting started

SkathIO.Rogue is an AOT-safe, source-generated CQRS/mediator for .NET. Handlers are discovered and
wired at **compile time** — there is no runtime reflection or assembly scanning, so the whole thing
works under Native AOT and IL trimming.

Target frameworks: `netstandard2.0`, `net8.0`, `net10.0`.

## Install

```bash
dotnet add package SkathIO.Rogue
```

The source generator ships **inside** the `SkathIO.Rogue` package as an analyzer asset
(`analyzers/dotnet/cs`). Referencing the package is enough — the generator runs in your
compilation automatically. No separate generator package to install.

## Register

```csharp
using SkathIO.Rogue;

builder.Services.AddRogue();
```

`AddRogue()` registers the generated dispatcher plus everything the generator discovered in your
compilation. An optional configuration callback exposes `RogueOptions`:

```csharp
builder.Services.AddRogue(o =>
{
    o.EnableObjectDispatch = true;                 // opt into the untyped Send(object)/Publish(object) path
    o.EnableTelemetry = true;                      // turn on the ActivitySource/Meter shim
    o.Lifetime = ServiceLifetime.Transient;        // DI lifetime for discovered handlers + behaviors (default: Transient)
    o.AddOpenBehavior(typeof(MyBehavior<,>));       // register an open-generic pipeline behavior
});
```

## Define a message and handler

Rogue's core is CQS-explicit: a read implements `IQuery<TResponse>`, a write implements `ICommand` or
`ICommand<TResponse>`, and each is served by the matching `*Handler` returning a `ValueTask`.

```csharp
public sealed record GreetQuery(string Name) : IQuery<GreetResponse>;
public sealed record GreetResponse(string Greeting);

public sealed class GreetHandler : IQueryHandler<GreetQuery, GreetResponse>
{
    public ValueTask<GreetResponse> Handle(GreetQuery query, CancellationToken ct)
        => new(new GreetResponse($"Hello, {query.Name}!"));
}
```

## Dispatch

Inject `ISender` (queries/commands/streams) or `IPublisher` (events). `IMediator` combines both.

```csharp
app.MapGet("/greet/{name}", async (string name, ISender sender) =>
    Results.Ok(await sender.Send(new GreetQuery(name))));
```

A full runnable version of this is in [`samples/minimal-api`](../samples/minimal-api).

## Message shapes

The core is CQS-explicit — each contract is independent (a type implementing two of them raises the
`ROGUE011` diagnostic).

| Interface | Handler | Dispatch | Notes |
|-----------|---------|----------|-------|
| `IQuery<T>` | `IQueryHandler<TQuery, T>` | `ISender.Send` | A read; one handler per query. |
| `ICommand<T>` | `ICommandHandler<TCommand, T>` | `ISender.Send` | A write with a response; one handler. |
| `ICommand` | `ICommandHandler<TCommand>` | `ISender.Send` | A write, void; returns `ValueTask` (`ValueTask<Unit>` on netstandard2.0). |
| `IEvent` | `IEventHandler<TEvent>` | `IPublisher.Publish` | Zero-to-many handlers, fan-out. |
| `IStreamQuery<T>` | `IStreamQueryHandler<TQuery, T>` | `ISender.CreateStream` | Returns `IAsyncEnumerable<T>` (net8.0+). |

The untyped `Send(object)` / `Publish(object)` overloads exist for dynamic dispatch but are **opt-in**
via `RogueOptions.EnableObjectDispatch`; they route through a generated type `switch`.

Migrating from MediatR? The `IRequest` / `INotification` / `IStreamRequest`-shaped surface is available
in the `SkathIO.Rogue.MediatR` package (`SkathIO.Rogue.Compatibility` namespace) — see the
[migration guide](migration-guide.md).

## Lifetimes

The generated **dispatcher** is registered **scoped** — per-scope, so it can resolve handlers that
themselves depend on scoped services. **Discovered handlers and behaviors** default to **transient**
(`RogueOptions.Lifetime`, overridable per your needs).
`IRoguePipelineInspector` and the notification publishers are stateless singletons.

## Troubleshooting

The generator emits compile-time diagnostics so wiring mistakes surface as build errors/warnings, not
runtime exceptions:

| ID | Meaning |
|----|---------|
| `ROGUE001` | No handler registered for a request type used in source. |
| `ROGUE002` | More than one handler registered for a request type. |
| `ROGUE003` | Handler response type does not match the request's declared response. |
| `ROGUE004` | Handler may not be constructable from DI. |
| `ROGUE005` | Handler or behavior is abstract or has no public constructor. |
| `ROGUE006` | Open-generic request type is not supported by the generator. |
| `ROGUE010` | Suggestion: inject `ISender`/`IPublisher` instead of `IMediator`. |
| `ROGUE011` | A type implements multiple CQS contracts (ambiguous under the CQS-explicit core). |
| `ROGUE012` | A MediatR-adapter request has a command-vs-query mapping conflict. |

(`ROGUE007` is intentionally unused — a removed-from-scope id, never reissued.)

If a dispatch throws `RogueUnregisteredRequestException` at runtime, the generator did not run in the
dispatching project's compilation. With the NuGet package this is automatic; with a **project
reference** (as the samples use) you must add an explicit analyzer reference:

```xml
<ProjectReference Include="path/to/SkathIO.Rogue.SourceGenerator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

## Where next

- [Behaviors guide](behaviors.md) — pipeline behaviors, ordering, pre/post processors, logging, validation.
- [API reference](api-reference.md) — the full public surface.
- [Migration guide](migration-guide.md) — moving from MediatR.
- [Benchmarks](benchmarks.md) — performance characteristics and the honesty scenario.
