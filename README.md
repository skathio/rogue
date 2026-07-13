# SkathIO.Rogue

[![NuGet](https://img.shields.io/nuget/v/SkathIO.Rogue.svg)](https://www.nuget.org/packages/SkathIO.Rogue)
[![CI](https://github.com/skathio/rogue/actions/workflows/ci.yml/badge.svg)](https://github.com/skathio/rogue/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

**AOT-safe, source-generated CQRS/mediator for .NET. Free and MIT-licensed forever.**

Rogue is a drop-in alternative to MediatR built for the modern .NET ecosystem: reflection-free dispatch via Roslyn source generation, Native AOT support, zero-allocation on the hot path, and a `ValueTask`-based pipeline — with no commercial license required, now or ever.

---

## Why Rogue?

| | MediatR | martinothamar/Mediator | **SkathIO.Rogue** |
|---|---|---|---|
| License | Commercial (v13+) | MIT | **MIT** |
| Dispatch | Reflection | Source-gen (monomorphized) | **Source-gen (DI-resolved)** |
| Native AOT | No | Yes | **Yes** |
| Pipeline behaviors | Open generic | Open generic | **Open generic + `[BehaviorOrder]`** |
| Streaming | No | No | **`IAsyncEnumerable<T>`** |
| Migration path from MediatR | — | Manual | **One-line + compat shim** |

Benchmarks: Rogue meets or beats MediatR across the measured warm-path scenarios (single-handler `Send`, notification fan-out at N = 2 / 5 / 20, and validated `Send` with a real FluentValidation behavior) and leads cold-start by ~18.5×. See [docs/benchmarks.md](docs/benchmarks.md) and [bench/RESULTS.md](bench/RESULTS.md) for the full measured tables and methodology.

---

## Installation

```
dotnet add package SkathIO.Rogue
```

For library authors who only need the interfaces (no source generator or DI wiring):

```
dotnet add package SkathIO.Rogue.Abstractions
```

Optional integration packages (v1.0):

```
dotnet add package SkathIO.Rogue.Logging
dotnet add package SkathIO.Rogue.Validation.FluentValidation
```

---

## Quick start

### 1. Register

```csharp
// Program.cs
builder.Services.AddRogue();
```

The source generator discovers all `IQueryHandler<,>`, `ICommandHandler<>` / `ICommandHandler<,>`,
`IEventHandler<>`, and `IStreamQueryHandler<,>` types in your project at compile time — no assembly
scanning, no reflection.

### 2. Define requests and handlers

```csharp
// Queries return data
public record GetUserQuery(int Id) : IQuery<User>;

public class GetUserQueryHandler : IQueryHandler<GetUserQuery, User>
{
    public ValueTask<User> Handle(GetUserQuery query, CancellationToken ct)
        => ValueTask.FromResult(new User(query.Id, "Alice"));
}

// Commands signal intent
public record CreateOrderCommand(string ProductId, int Qty) : ICommand<OrderId>;

public class CreateOrderCommandHandler : ICommandHandler<CreateOrderCommand, OrderId>
{
    public async ValueTask<OrderId> Handle(CreateOrderCommand cmd, CancellationToken ct)
    {
        // ... create the order
        return new OrderId(Guid.NewGuid());
    }
}

// Events fan out to all handlers
public record OrderPlacedEvent(OrderId OrderId) : IEvent;

public class SendOrderConfirmationHandler : IEventHandler<OrderPlacedEvent>
{
    public ValueTask Handle(OrderPlacedEvent @event, CancellationToken ct)
    {
        // ... send email
        return ValueTask.CompletedTask;
    }
}
```

### 3. Send

```csharp
// Inject ISender / IPublisher via DI
public class OrderController(ISender sender, IPublisher publisher)
{
    public async Task<IActionResult> PlaceOrder(PlaceOrderRequest req)
    {
        var orderId = await sender.Send(new CreateOrderCommand(req.ProductId, req.Qty));
        await publisher.Publish(new OrderPlacedEvent(orderId));
        return Ok(orderId);
    }
}
```

### 4. Stream

```csharp
public record PagedProductsQuery(int PageSize) : IStreamQuery<Product>;

public class PagedProductsHandler : IStreamQueryHandler<PagedProductsQuery, Product>
{
    public async IAsyncEnumerable<Product> Handle(
        PagedProductsQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var product in FetchPagedAsync(query.PageSize, ct))
            yield return product;
    }
}

// Usage
await foreach (var product in sender.CreateStream(new PagedProductsQuery(50)))
    Console.WriteLine(product.Name);
```

---

## Scoped dispatch

`AddRogue()` registers `ISender` / `IMediator` (and the underlying dispatcher) as **Scoped** — one
mediator instance per request/scope, matching the standard mediator-pattern lifetime. That means
`ISender`/`IMediator` must be resolved from **within a scope**, not from the root `IServiceProvider`.

For ASP.NET Core controllers/minimal-API handlers, constructor/parameter injection already resolves
from the current request's scope — no extra work needed (see the [Quick start](#3-send) example
above). The failure case is code that runs **outside** a request scope: hosted services, background
workers, and startup/seeding code.

```csharp
// Wrong — throws at resolution time
public class OutboxProcessor(IServiceProvider provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sender = provider.GetRequiredService<ISender>(); // throws
        ...
    }
}

// Right — create a scope first
public class OutboxProcessor(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new ProcessOutboxCommand(), ct);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
```

Resolving `ISender`/`IMediator` from the root provider throws:

```
System.InvalidOperationException: Cannot resolve scoped service 'SkathIO.Rogue.ISender' from root provider.
```

This is standard `Microsoft.Extensions.DependencyInjection` scope-validation behavior for any
Scoped service resolved from the root container — it applies equally with or without any optional
Rogue integration package (e.g. `SkathIO.Rogue.Validation.FluentValidation`) installed, since it's
a property of the mediator's own Scoped registration, not of any particular pipeline behavior.

**Note:** the .NET Generic Host only enables this scope validation (`ValidateScopes`) by default in
the `Development` environment. Outside Development — the default in most deployed configurations —
resolving from the root provider does **not** throw; it silently succeeds with an instance captured
against the root container, effectively a singleton mediator that never sees per-request scoped
state. That's a worse failure mode than the exception above precisely because it's silent. Create a
scope regardless of environment; don't rely on the exception as your only signal.

---

## Pipeline behaviors

Behaviors wrap every request in the pipeline — use them for logging, validation, caching, or any
cross-cutting concern.

```csharp
public class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async ValueTask<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        logger.LogInformation("Handling {Request}", typeof(TRequest).Name);
        var result = await next();
        logger.LogInformation("Handled {Request}", typeof(TRequest).Name);
        return result;
    }
}
```

Register open behaviors — they apply to all request types automatically:

```csharp
builder.Services.AddRogue(opts =>
    opts.AddOpenBehavior(typeof(LoggingBehavior<,>)));
```

Control pipeline order with `[BehaviorOrder]` (lower = outermost, executes first):

```csharp
[BehaviorOrder(-10)]  // outermost — runs before everything else
public class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> { ... }

[BehaviorOrder(10)]   // inner — runs just before the handler
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> { ... }
```

---

## Performance

Rogue dispatches via source-generated, DI-resolved methods — no reflection on the hot path.

- **0 bytes allocated** on the generated concrete dispatch path (`RogueDispatcher.Send{Request}`) for a behavior-free, synchronously-completing handler; the portable `ISender.Send<T>` path boxes one `ValueTask<T>` by design
- **Faster than MediatR** on the measured warm paths (single-handler `Send`, notification fan-out at N = 2 / 5 / 20) and ~19× faster cold-start — no runtime assembly scan
- **Native AOT compatible** — no dynamic code generation, no `Expression.Compile()`, no `MakeGenericMethod`
- **`ValueTask`-based** throughout — avoids unnecessary heap allocations vs `Task`

Full benchmark results and methodology: [docs/benchmarks.md](docs/benchmarks.md) and [bench/RESULTS.md](bench/RESULTS.md).

---

## Migration from MediatR

Most MediatR code migrates in one step:

```diff
- services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
+ services.AddRogue();
```

Rogue's native core is CQS-explicit (`IQuery<T>`, `ICommand` / `ICommand<T>`, `IEvent`,
`IStreamQuery<T>`). For drop-in migration, the `SkathIO.Rogue.MediatR` package provides the
MediatR-shaped surface (`IRequest<T>`, `IRequestHandler<,>`, `INotification`, `INotificationHandler<>`,
`IStreamRequest<T>`, …) under the `SkathIO.Rogue.Compatibility` namespace, plus an `AddMediatR(...)`
shim so existing `IMediator` injection points keep working. A bundled Roslyn analyzer (ROGM001–006)
ships code-fixes — including rewrites to the native CQS contracts.

See the [migration guide](docs/migration-guide.md) and the [before/after sample](samples/mediatr-migration).

---

## Packages

| Package | Purpose |
|---------|---------|
| `SkathIO.Rogue` | Core library — source generator + DI wiring + dispatcher |
| `SkathIO.Rogue.Abstractions` | Interfaces only — for library authors |
| `SkathIO.Rogue.Logging` | `LoggingBehavior<,>` — structured request/response logging *(v1.0)* |
| `SkathIO.Rogue.Validation.FluentValidation` | `ValidationBehavior<,>` — FluentValidation pipeline integration *(v1.0)* |
| `SkathIO.Rogue.MediatR` | MediatR compatibility shim — `IMediator` adapter *(v1.0)* |

---

## Requirements

- **.NET 8.0+** (or .NET Standard 2.0 for library authors)
- **C# 10+** (source generator produces modern C#)
- Supports **Native AOT** on .NET 8+

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributions are welcome — open an issue before starting large changes.

---

## License

[MIT](LICENSE) — free forever, no exceptions.
