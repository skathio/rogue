# Behaviors guide

Pipeline behaviors wrap request dispatch like middleware: each behavior runs code before and after
the next stage, and ultimately around the handler. SkathIO.Rogue weaves them at compile time, so the
pipeline for a given request type is fixed in generated code — there is no per-call reflection to
build the chain.

## Pipeline behaviors

A behavior implements `IPipelineBehavior<TRequest, TResponse>`:

```csharp
public sealed class TimingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async ValueTask<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var sw = ValueStopwatch.StartNew();
        try { return await next(); }
        finally { /* sw.ElapsedMilliseconds */ }
    }
}
```

Register it open-generic so it applies to every request:

```csharp
builder.Services.AddRogue(o => o.AddOpenBehavior(typeof(TimingBehavior<,>)));
```

Or register a closed/typed behavior with `o.AddBehavior<TBehavior>()`.

### Auto-discovery from referenced assemblies

The generator also scans **referenced assemblies** for open-generic `IPipelineBehavior<,>` and applies
them automatically (PD-17). This is why merely referencing `SkathIO.Rogue.Logging` wires
`LoggingBehavior` onto every request, and referencing the FluentValidation package wires
`ValidationBehavior`. It is the intended contract — be aware that it also introduces the package's DI
dependencies (e.g. `LoggingBehavior` requires an `ILogger` in the container).

## Ordering

Behaviors execute in ascending `Order`. Set the order at registration
(`AddOpenBehavior(typeof(X<,>), order: 10)`) or declaratively on the behavior type with
`[BehaviorOrder(n)]`. Lower runs first (outermost). Inspect the resolved pipeline for a request type
at runtime via `IRoguePipelineInspector.GetPipeline<TRequest>()`, which returns the ordered
`BehaviorInfo` list (type, order, source) — useful for asserting wiring in tests.

## Stream pipeline behaviors

Streaming requests (`IStreamRequest<T>`) have their own behavior interface,
`IStreamPipelineBehavior<TRequest, TResponse>`, which wraps an `IAsyncEnumerable<T>` via
`StreamHandlerDelegate<T>`. These are woven around the stream handler in generated code the same way
the request behaviors are (net8.0+).

## Pre/post processors and exception handlers

Beyond behaviors, four processor interfaces give finer-grained hooks (FR-25/26/27). The generator
wraps the behavior engine with a processor stage when any are registered, and preserves a fast path
for processor-free requests:

| Interface | Runs |
|-----------|------|
| `IRequestPreProcessor<TRequest>` | Before the handler. |
| `IRequestPostProcessor<TRequest, TResponse>` | After the handler succeeds. |
| `IRequestExceptionAction<TRequest, TException>` | On a matching exception (side effect; does not recover). |
| `IRequestExceptionHandler<TRequest, TResponse, TException>` | On a matching exception; can mark it handled and supply a response via `RequestExceptionHandlerState<TResponse>`. |

Exception matching is reflection-free `is TException` type matching in generated code (NFR-SEC-1).

## Logging behavior

`SkathIO.Rogue.Logging` provides `LoggingBehavior<TRequest, TResponse>`. It logs request start/finish
and timing through `ILogger`. **Payload logging is off by default** (NFR-SEC-2) to avoid leaking
request contents. Opt in per-request type with `[LogPayload]` on the request, or globally via
`LoggingOptions.LogPayload`.

```bash
dotnet add package SkathIO.Rogue.Logging
```

```csharp
[LogPayload]                                  // opt this request's payload into the log
public sealed record GreetRequest(string Name) : IRequest<GreetResponse>;
```

## Validation behavior

`SkathIO.Rogue.Validation.FluentValidation` provides `ValidationBehavior<TRequest, TResponse>`. It
resolves every `IValidator<TRequest>` from DI, aggregates all failures, and throws
`FluentValidation.ValidationException` **before** calling the handler if validation fails.

```bash
dotnet add package SkathIO.Rogue.Validation.FluentValidation
```

```csharp
public sealed class GreetRequestValidator : AbstractValidator<GreetRequest>
{
    public GreetRequestValidator() => RuleFor(x => x.Name).NotEmpty();
}
```

Map the thrown `ValidationException` to your transport's error shape (e.g. HTTP 400) in your own
middleware — the behavior does not own transport concerns.

## Notification publishers

Notification fan-out strategy is configurable via `RogueOptions.NotificationPublisher`:

- `ForeachAwaitPublisher` (default) — awaits each handler sequentially; the first throw propagates.
- `WhenAllPublisher` — runs handlers concurrently with `Task.WhenAll`.

## Observability

`RogueTelemetry` is a zero-overhead-when-off shim exposing an `ActivitySource` and `Meter` named
`"SkathIO.Rogue"`. It is gated by `RogueOptions.EnableTelemetry`; when disabled there is no per-dispatch
cost. Subscribe with the standard `System.Diagnostics` / OpenTelemetry tooling against the
`"SkathIO.Rogue"` source/meter name.
