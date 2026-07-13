# API reference

The authoritative public surface is tracked per package in `PublicAPI.Shipped.txt` (enforced by the
`public-api` CI gate). This page is a human-readable map of it. Types whose shape differs by target
framework (the void path returns `ValueTask<Unit>` on `netstandard2.0` vs `ValueTask` on net8.0+, and
the streaming types are net8.0+ only) are noted inline.

## Packages

| Package | Contents |
|---------|----------|
| `SkathIO.Rogue` | Runtime: `AddRogue`, dispatcher, options, telemetry, publishers. Bundles the source generator as an analyzer asset. |
| `SkathIO.Rogue.Abstractions` | Contracts only: the message/handler/behavior interfaces, `Unit`, marker types. Depend on this from layers that define messages but should not pull the runtime. |
| `SkathIO.Rogue.Logging` | `LoggingBehavior`, `LoggingOptions`, `[LogPayload]`. |
| `SkathIO.Rogue.Validation.FluentValidation` | `ValidationBehavior` over FluentValidation. |
| `SkathIO.Rogue.MediatR` | MediatR compatibility shim + migration analyzer (ROGM001–006) bundled as an analyzer asset. |

## Abstractions (`SkathIO.Rogue.Abstractions`)

**Messages** (CQS-explicit core — each an independent contract)

- `IQuery<TResponse>` — a read. Dispatched by `ISender.Send`.
- `ICommand` / `ICommand<TResponse>` — a write, void or with a response. Dispatched by `ISender.Send`.
- `IEvent` — a fan-out message. Dispatched by `IPublisher.Publish`.
- `IStreamQuery<TItem>` — a streaming read returning `IAsyncEnumerable<TItem>` (net8.0+).
- `Unit` — the void response type (value type; `Unit.Value`, `Unit.Task`).

The MediatR-shaped surface (`IRequest`, `INotification`, `IStreamRequest`, …) is **not** in this
package; it lives in `SkathIO.Rogue.MediatR` for migration — see
[Compatibility](#compatibility-skathioroguemediatr) below.

**Handlers**

- `IQueryHandler<TQuery, TResponse>` — one handler per query.
- `ICommandHandler<TCommand, TResponse>` / `ICommandHandler<TCommand>` — one handler per command.
- `IEventHandler<TEvent>` — zero-to-many handlers per event (fan-out).
- `IStreamQueryHandler<TQuery, TItem>` (net8.0+)

**Pipeline**

- `IPipelineBehavior<TRequest, TResponse>` + `RequestHandlerDelegate<TResponse>`
- `IStreamPipelineBehavior<TRequest, TResponse>` + `StreamHandlerDelegate<TResponse>` (net8.0+)
- `IRequestPreProcessor<TRequest>`, `IRequestPostProcessor<TRequest, TResponse>`
- `IRequestExceptionAction<TRequest, TException>`
- `IRequestExceptionHandler<TRequest, TResponse, TException>` + `RequestExceptionHandlerState<TResponse>`
- `BehaviorOrderAttribute(int order)` — declarative behavior ordering.

**Dispatch entry points**

- `ISender` — `Send<TResponse>(IQuery<TResponse>)`, `Send<TResponse>(ICommand<TResponse>)`,
  `Send(ICommand)`, `Send(object)`, `CreateStream<TItem>(IStreamQuery<TItem>)`.
- `IPublisher` — `Publish(IEvent)`, `Publish(object)`.
- `IMediator` — combines `ISender` + `IPublisher`.

**Event publishing**

- `IEventPublisher` — fan-out strategy contract:
  `Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>>, TEvent, CancellationToken)`. Implementations
  receive the resolved, strongly-typed handlers directly — there is no wrapper/executor type.

**Inspection**

- `IRoguePipelineInspector` — `GetPipeline<TRequest>()` / `GetPipeline(Type)` returns the ordered `BehaviorInfo` list.
- `BehaviorInfo(Type BehaviorType, int Order, string Source)` — a resolved pipeline entry.

## Runtime (`SkathIO.Rogue`)

**Registration**

- `RogueServiceCollectionExtensions.AddRogue(this IServiceCollection, Action<RogueOptions>? configure = null)`
- `RogueOptions`:
  - `EnableObjectDispatch` (bool) — opt into untyped `Send(object)`/`Publish(object)`.
  - `EnableTelemetry` (bool) — turn on the telemetry shim.
  - `Lifetime` (`ServiceLifetime`) — DI lifetime applied to discovered handlers (default `Transient`). Note: this does not control pipeline behaviors, which are **always** registered `Transient` regardless of this setting (avoids a captive-dependency trap for behaviors with a Scoped dependency — see the [behaviors guide](behaviors.md#validation-behavior)), nor the `RogueDispatcher` registration, which is `Scoped`.
  - `NotificationPublisher` (`IEventPublisher`) — fan-out strategy.
  - `AddBehavior<TBehavior>(int order = 0)`, `AddOpenBehavior(Type, int order = 0)`, `BehaviorRegistrations`.
- `BehaviorRegistration(Type BehaviorType, int Order, bool IsOpen)` — a registered behavior.
- `RogueRegistrationBridge.GeneratedRegistrar` — the static seam the generated module initializer
  wires `AddRogue` to. Public for the generator's emitted code; not a consumer API.

**Dispatcher**

- `RogueDispatcher` — base type the generator subclasses (`RogueDispatcherImpl`). Implements
  `Send`/`Publish`/`CreateStream`/`SendObject`.
- `Mediator` — `IMediator` implementation.

**Concrete fast path (0-alloc) — `RogueExtensions`**

For the hottest send paths the generator emits `SkathIO.Rogue.Generated.RogueExtensions`, a static
class with one typed extension method **per command/query handler** on `RogueDispatcher`. It bypasses
the `ISender` interface dispatch (which boxes one `ValueTask<T>` by design) and is **0 B allocated**
for a synchronously-completing, behavior-free handler.

- **Inject `RogueDispatcher`**, not `ISender` — the fast path downcasts to the generated impl, and
  `ISender` resolves to `Mediator` (the cast would fail). `RogueDispatcher` is registered `Scoped`.
- **Naming**: `Send{RequestSimpleName}` — e.g. a `PingRequest` handler yields `SendPingRequest`. Two
  request types sharing a simple name across namespaces produce valid overloads (resolved by the
  request parameter type).
- **`using SkathIO.Rogue.Generated;`** must be in scope to call the extension methods.
- **Void commands** return `ValueTask<Unit>` (discard the `Unit`) to keep the path allocation-free.
- The method is emitted `internal` (still callable in-assembly) when the request or response type is
  not `public`, to respect accessibility.

```csharp
using SkathIO.Rogue.Generated;

public sealed class Endpoint(RogueDispatcher dispatcher)   // inject the dispatcher, not ISender
{
    public ValueTask<string> Ping() => dispatcher.SendPingRequest(new PingRequest("ping"));
}
```

**Trade-off**: `ISender.Send(...)` stays the portable, decoupled default (one box). Use the
`RogueDispatcher` fast path only where the allocation matters and a direct dependency on the concrete
dispatcher is acceptable.

**Publishers**

- `ForeachAwaitPublisher` (default), `WhenAllPublisher`.

**Pipeline execution**

- `PipelineExecutor.Execute<TRequest, TResponse>(...)` — the fallback fold used by the runtime path.

**Telemetry**

- `RogueTelemetry` — `Name = "SkathIO.Rogue"`, `Enabled`, `StartDispatch<TRequest>()`, `StopDispatch(...)`.
- `DispatchScope`, `ValueStopwatch` — allocation-free timing helpers.

**Errors**

- `RogueUnregisteredRequestException(Type requestType)` — thrown when a request has no generated route
  (usually means the generator did not run in the dispatching compilation — see
  [getting-started](getting-started.md#troubleshooting)).

## Compatibility (`SkathIO.Rogue.MediatR`)

For drop-in MediatR migration. Types live in the `SkathIO.Rogue.Compatibility` namespace unless noted.

- **MediatR-shaped messages/handlers** — `IRequest` / `IRequest<TResponse>`,
  `IRequestHandler<TRequest, TResponse>` / `IRequestHandler<TRequest>`, `INotification`,
  `INotificationHandler<TNotification>`, `IStreamRequest<TResponse>`,
  `IStreamRequestHandler<TRequest, TItem>` (net8.0+), `IPipelineBehavior<TRequest, TResponse>`,
  `IMediator` / `ISender` / `IPublisher`, `Unit`, `ServiceFactory`.
- **Registration shim** — `MediatRCompatExtensions.AddMediatR(this IServiceCollection, …)` +
  `MediatRCompatOptions` (`RegisterServicesFromAssembly…`), forwarding to `AddRogue`.
- **Reflection escape hatch** — `ReflectionMediator` (obsolete; not AOT-safe) for open-generic request
  types the generator cannot close at compile time.
- **`[MapAsQuery]`** (`SkathIO.Rogue.MediatR.MapAsQueryAttribute`) — maps an otherwise-ambiguous MediatR
  request onto the CQS `IQuery` side during migration.

Migration analyzer diagnostics `ROGM001`–`ROGM006` — see [migration-guide](migration-guide.md).

## Diagnostics

Compile-time generator diagnostics: `ROGUE001`–`ROGUE006` + `ROGUE010`/`ROGUE011`/`ROGUE012`
(`ROGUE007` is intentionally unused) — see
[getting-started](getting-started.md#troubleshooting). Migration analyzer diagnostics:
`ROGM001`–`ROGM006` (see [migration-guide](migration-guide.md)).
