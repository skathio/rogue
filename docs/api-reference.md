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
| `SkathIO.Rogue.MediatR` | MediatR compatibility shim + migration analyzer (ROGM001–004) bundled as an analyzer asset. |

## Abstractions (`SkathIO.Rogue.Abstractions`)

**Messages**

- `IRequest` / `IRequest<TResponse>` — request, void or with a response.
- `IQuery<TResponse>`, `ICommand`, `ICommand<TResponse>` — CQRS-named aliases of `IRequest`.
- `INotification` — fan-out message.
- `IStreamRequest<TResponse>`, `IStreamQuery<TResponse>` — streaming request (net8.0+).
- `IBaseRequest`, `IBaseStreamRequest` — non-generic markers.
- `Unit` — the void response type (value type; `Unit.Value`, `Unit.Task`).

**Handlers**

- `IRequestHandler<TRequest, TResponse>` / `IRequestHandler<TRequest>`
- `IQueryHandler<TQuery, TResponse>`, `ICommandHandler<TCommand, TResponse>`, `ICommandHandler<TCommand>`
- `INotificationHandler<TNotification>`
- `IStreamRequestHandler<TRequest, TResponse>` (net8.0+)

**Pipeline**

- `IPipelineBehavior<TRequest, TResponse>` + `RequestHandlerDelegate<TResponse>`
- `IStreamPipelineBehavior<TRequest, TResponse>` + `StreamHandlerDelegate<TResponse>` (net8.0+)
- `IRequestPreProcessor<TRequest>`, `IRequestPostProcessor<TRequest, TResponse>`
- `IRequestExceptionAction<TRequest, TException>`
- `IRequestExceptionHandler<TRequest, TResponse, TException>` + `RequestExceptionHandlerState<TResponse>`
- `BehaviorOrderAttribute(int order)` — declarative behavior ordering.

**Dispatch entry points**

- `ISender` — `Send<TResponse>(IRequest<TResponse>)`, `Send(object)`, `CreateStream<TResponse>(IStreamRequest<TResponse>)`.
- `IPublisher` — `Publish(INotification)`, `Publish(object)`.
- `IMediator` — combines `ISender` + `IPublisher`.

**Notification publishing**

- `INotificationPublisher` — strategy contract.
- `NotificationHandlerExecutor` — a resolved handler + its invoker.

**Inspection**

- `IRoguePipelineInspector` — `GetPipeline<TRequest>()` / `GetPipeline(Type)` returns the ordered `BehaviorInfo` list.
- `BehaviorInfo(Type BehaviorType, int Order, string Source)` — a resolved pipeline entry.

## Runtime (`SkathIO.Rogue`)

**Registration**

- `RogueServiceCollectionExtensions.AddRogue(this IServiceCollection, Action<RogueOptions>? configure = null)`
- `RogueOptions`:
  - `EnableObjectDispatch` (bool) — opt into untyped `Send(object)`/`Publish(object)`.
  - `EnableTelemetry` (bool) — turn on the telemetry shim.
  - `Lifetime` (`ServiceLifetime`) — DI lifetime applied to discovered handlers and behaviors (default `Transient`). Note: this does not control the `RogueDispatcher` registration, which is `Scoped`.
  - `NotificationPublisher` (`INotificationPublisher`) — fan-out strategy.
  - `AddBehavior<TBehavior>(int order = 0)`, `AddOpenBehavior(Type, int order = 0)`, `BehaviorRegistrations`.
- `BehaviorRegistration(Type BehaviorType, int Order, bool IsOpen)` — a registered behavior.
- `RogueRegistrationBridge.GeneratedRegistrar` — the static seam the generated module initializer
  wires `AddRogue` to (PD-15/PD-16). Public for the generator's emitted code; not a consumer API.

**Dispatcher**

- `RogueDispatcher` — base type the generator subclasses (`RogueDispatcherImpl`). Implements
  `Send`/`Publish`/`CreateStream`/`SendObject`.
- `Mediator` — `IMediator` implementation.

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

## Diagnostics

Compile-time generator diagnostics: `ROGUE001`–`ROGUE007` (see
[getting-started](getting-started.md#troubleshooting)). Migration analyzer diagnostics:
`ROGM001`–`ROGM004` (see [migration-guide](migration-guide.md)).
