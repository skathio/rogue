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
them automatically. This is why merely referencing `SkathIO.Rogue.Logging` wires
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

Streaming queries (`IStreamQuery<T>`) have their own behavior interface,
`IStreamPipelineBehavior<TRequest, TResponse>`, which wraps an `IAsyncEnumerable<T>` via
`StreamHandlerDelegate<T>`. These are woven around the stream handler in generated code the same way
the request behaviors are (net8.0+).

## Pre/post processors and exception handlers

Beyond behaviors, four processor interfaces give finer-grained hooks. The generator
wraps the behavior engine with a processor stage when any are registered, and preserves a fast path
for processor-free requests:

| Interface | Runs |
|-----------|------|
| `IRequestPreProcessor<TRequest>` | Before the handler. |
| `IRequestPostProcessor<TRequest, TResponse>` | After the handler succeeds. |
| `IRequestExceptionAction<TRequest, TException>` | On a matching exception (side effect; does not recover). |
| `IRequestExceptionHandler<TRequest, TResponse, TException>` | On a matching exception; can mark it handled and supply a response via `RequestExceptionHandlerState<TResponse>`. |

Exception matching is reflection-free `is TException` type matching in generated code.

## Logging behavior

`SkathIO.Rogue.Logging` provides `LoggingBehavior<TRequest, TResponse>`. It logs request start/finish
and timing through `ILogger`. **Payload logging is off by default** to avoid leaking
request contents. Opt in per-request type with `[LogPayload]` on the request, or globally via
`LoggingOptions.LogPayload`.

```bash
dotnet add package SkathIO.Rogue.Logging
```

```csharp
[LogPayload]                                  // opt this request's payload into the log
public sealed record GreetQuery(string Name) : IQuery<GreetResponse>;
```

## Validation behavior

`SkathIO.Rogue.Validation.FluentValidation` provides `ValidationBehavior<TRequest, TResponse>`. It
resolves every `IValidator<TRequest>` from DI, aggregates all failures, and throws
`FluentValidation.ValidationException` **before** calling the handler if validation fails.

```bash
dotnet add package SkathIO.Rogue.Validation.FluentValidation
```

Referencing the package and writing a validator is the entire contract — **there is no wiring call
of any kind**, unlike `AddOpenBehavior` for a custom behavior. Every concrete, public-constructible
`AbstractValidator<T>` in a project that references the package is discovered at compile time and
registered into DI automatically:

```csharp
public sealed class GreetQueryValidator : AbstractValidator<GreetQuery>
{
    public GreetQueryValidator() => RuleFor(x => x.Name).NotEmpty();
}
```

```csharp
// Program.cs — unchanged. No AddRogue(o => ...) edit accompanies the validator above, and none is
// needed or offered.
builder.Services.AddRogue();
```

That's the whole thing — the same zero-fuss contract commands, queries, and handlers already get.
`ValidationBehavior<,>` itself is still auto-woven the same way any other pipeline behavior is (see
[Auto-discovery from referenced assemblies](#auto-discovery-from-referenced-assemblies) above); what's
new is that the *validators* it resolves no longer need a manual registration or a reflection-based
startup scan (e.g. `AddValidatorsFromAssemblyContaining`) either.

Map the thrown `ValidationException` to your transport's error shape (e.g. HTTP 400) in your own
middleware — the behavior does not own transport concerns.

**Lifetime note (since 1.1.0):** `ValidationBehavior<,>` — like every pipeline behavior — is always
registered `Transient`, regardless of `RogueOptions.Lifetime`. Earlier, setting `Lifetime =
ServiceLifetime.Singleton` (e.g. for handler-performance reasons) also made behaviors Singleton, so a
Singleton `ValidationBehavior` consuming a Scoped `IValidator<T>` (the default lifetime
`AddValidatorsFromAssembly` registers validators at) would throw at container-build time — a
captive-dependency trap. This is fixed: behaviors are now unconditionally `Transient`, so validators
of any lifetime resolve correctly no matter what `Lifetime` is set to. See the
[Lifetimes section](getting-started.md#lifetimes) for the full rationale. Discovered validator
registrations are separately hard-pinned to `Scoped`, independent of `RogueOptions.Lifetime` — see the
next note.

**Behavior change on upgrade — read this if you already reference this package.** Validator discovery
is fully automatic and, deliberately, has no opt-in call and no gate — there is no
`AddFluentValidation()`-style method, and none is planned; referencing the package and writing a
validator is the entire contract, as described above. If you already reference
`SkathIO.Rogue.Validation.FluentValidation` and kept a validator around that you deliberately never
wired into DI (e.g. for ad-hoc/manual invocation only), upgrading sweeps it into the pipeline
automatically — it starts running for every matching request with no code change of your own to cause
it. Two things to check when you upgrade:

- If you already have a manual `services.AddScoped<IValidator<T>, TValidator>()` (or
  `AddValidatorsFromAssemblyContaining<T>()`) registration for a validator that's also
  compile-time-discovered, remove it — otherwise the validator is registered twice, which can
  duplicate its failure message in the aggregated `ValidationException` (`TryAddEnumerable` dedups by
  implementation type, so whether this actually happens depends on registration order). Removing the
  old registration is exactly what this library's own smoke test does when migrating off
  `AddValidatorsFromAssemblyContaining` — see
  `tests/SkathIO.Rogue.Smoke.Application/ApplicationServiceCollectionExtensions.cs`.
- To suppress *validator* discovery for a specific project entirely while keeping `ValidationBehavior<,>`
  itself available at runtime, exclude this package's analyzer asset on that project's
  `PackageReference`:

  ```xml
  <PackageReference Include="SkathIO.Rogue.Validation.FluentValidation" Version="x.y.z"
                    ExcludeAssets="analyzers" />
  ```

  `analyzers` here is a standard NuGet asset-type name (alongside `compile`, `runtime`,
  `contentfiles`, `build`, `native`) — it refers to *this package's* `analyzers/dotnet/cs` payload,
  which is exactly where this generator ships. Excluding it drops only this generator (so no
  *validators* in that project are source-discovered) — it has no effect on the separate `SkathIO.Rogue`
  package's own generator, so your commands, queries, and handlers keep being discovered exactly as
  before. `ValidationBehavior<,>` also remains usable if another project's discovery (or a manual
  registration) supplies validators for it to resolve.

## Notification publishers

Notification fan-out strategy is configurable via `RogueOptions.NotificationPublisher`:

- `ForeachAwaitPublisher` (default) — awaits each handler sequentially; the first throw propagates.
- `WhenAllPublisher` — runs handlers concurrently with `Task.WhenAll`.

## Observability

`RogueTelemetry` is a zero-overhead-when-off shim exposing an `ActivitySource` and `Meter` named
`"SkathIO.Rogue"`. It is gated by `RogueOptions.EnableTelemetry`; when disabled there is no per-dispatch
cost. Subscribe with the standard `System.Diagnostics` / OpenTelemetry tooling against the
`"SkathIO.Rogue"` source/meter name.
