# Migrating from MediatR to SkathIO.Rogue

SkathIO.Rogue is a drop-in alternative to MediatR. Most codebases migrate in 15 minutes.

## Step 1 — Install packages

Replace `MediatR` with `SkathIO.Rogue`:
```
dotnet remove package MediatR
dotnet add package SkathIO.Rogue
dotnet add package SkathIO.Rogue.MediatR  # compat shim + migration analyzer
```

## Step 2 — Run the migration analyzer

The `SkathIO.Rogue.MediatR` package bundles a Roslyn analyzer with code-fixes (`ROGM001`–`ROGM006`). In
Visual Studio or Rider, run **Analyze → Fix all in Solution** to apply them.

What the code-fixes do automatically:
- `ROGM001`: replaces `using MediatR;` → `using SkathIO.Rogue;`
- `ROGM002`: replaces handler `Task` / `Task<T>` return types → `ValueTask` / `ValueTask<T>`
- `ROGM006`: rewrites the MediatR-shaped marker/handler interfaces to Rogue's native CQS contracts —
  `IRequest<T>` → `IQuery<T>` or `ICommand<T>`, `IRequestHandler<,>` → `IQueryHandler<,>` / `ICommandHandler<,>`,
  `INotification` → `IEvent`, `INotificationHandler<>` → `IEventHandler<>`,
  `IStreamRequest<T>` → `IStreamQuery<T>`
- `ROGM005` (review, not auto-applied): a response-bearing request whose command-vs-query intent can't be
  inferred from its name is migrated to the safe default `ICommand<T>` and flagged — change it to `IQuery<T>`
  (and its handler to `IQueryHandler<,>`) if it only reads state

> Prefer to keep the MediatR-shaped names instead of migrating to CQS? Reference `SkathIO.Rogue.MediatR`
> and change `using MediatR;` → `using SkathIO.Rogue.Compatibility;` — the `IRequest` / `INotification` /
> `IStreamRequest` surface lives there. (Skip the ROGM006 fix in that case.)

## Step 3 — Build and verify

```
dotnet build
dotnet test
```

## Feature mapping

The default migration rewrites MediatR types to Rogue's native CQS core (via the ROGM006 code-fix):

| MediatR | SkathIO.Rogue (CQS core) | Notes |
|---------|--------------------------|-------|
| `IRequest<T>` (read) | `IQuery<T>` + `IQueryHandler<T,R>` | `Task<R>` → `ValueTask<R>` |
| `IRequest<T>` (write) | `ICommand<T>` + `ICommandHandler<T,R>` | Ambiguous intent → `ICommand<T>` + ROGM005 review |
| `IRequest` (void) | `ICommand` + `ICommandHandler<T>` | Void path returns `ValueTask` |
| `INotification` / `INotificationHandler<>` | `IEvent` / `IEventHandler<>` | Fan-out preserved |
| `IStreamRequest<T>` / `IStreamRequestHandler<,>` | `IStreamQuery<T>` / `IStreamQueryHandler<,>` | net8.0+ |
| `IPipelineBehavior<T,R>` | `IPipelineBehavior<T,R>` | Shape preserved; `Task` → `ValueTask` |
| `IMediator` / `ISender` / `IPublisher` | same names | Prefer `ISender` / `IPublisher` |
| `AddMediatR(cfg => ...)` | `AddRogue()` | Assembly scan → compile-time discovery (the compat shim forwards `AddMediatR`) |
| Open-generic request handlers | `ReflectionMediator` escape hatch | Not AOT-safe; see ROGM003 |

If you instead keep the MediatR-shaped surface (the `SkathIO.Rogue.Compatibility` namespace in the
`SkathIO.Rogue.MediatR` package), the interface names stay identical — only the `using` and the handler
return types (`Task` → `ValueTask`) change.

## Handling open-generic requests (ROGM003)

If you have open-generic request types (e.g. `class MyRequest<T> : IRequest<T>`), the Rogue source generator cannot handle them at compile time. Options:

1. **Restructure to closed generics** (recommended for AOT/performance).
2. **Use the reflection escape hatch** — register `ReflectionMediator` in DI:
   ```csharp
   #pragma warning disable CS0618
   services.AddTransient<SkathIO.Rogue.Compatibility.ReflectionMediator>();
   #pragma warning restore CS0618
   ```
   This is not AOT-safe. Use only as a temporary bridge during migration.

## Removing the compat shim (optional cleanup)

The migration code-fix does not add `using SkathIO.Rogue.Compatibility;` (doing so would make the marker-interface references ambiguous). You add it by hand only where you call the DI-only compat helpers (`AddMediatR`, `Unit.Value`, `ReflectionMediator`); once those call sites move to `AddRogue`/`SkathIO.Rogue` directly, drop the compat using and remove the `SkathIO.Rogue.MediatR` package.
