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

The `SkathIO.Rogue.MediatR` package includes a Roslyn analyzer (`ROGM001`/`ROGM002`). In Visual Studio or Rider, run **Analyze → Fix all in Solution** for both diagnostics.

What the code-fix does automatically:
- `ROGM001`: Replaces `using MediatR;` → `using SkathIO.Rogue; using SkathIO.Rogue.Compatibility;`
- `ROGM002`: Replaces handler `Task<T>` return types → `ValueTask<T>`

## Step 3 — Build and verify

```
dotnet build
dotnet test
```

## Feature mapping

| MediatR | SkathIO.Rogue | Notes |
|---------|---------------|-------|
| `IRequest<T>` | `IRequest<T>` | Identical shape |
| `IRequestHandler<T,R>` → `Task<R>` | `IRequestHandler<T,R>` → `ValueTask<R>` | Code-fix rewrites return type |
| `INotification` / `INotificationHandler` | identical names | Direct map |
| `IPipelineBehavior<T,R>` | `IPipelineBehavior<T,R>` | Shape preserved; `Task` → `ValueTask` |
| `IMediator` / `ISender` / `IPublisher` | same | Use `ISender`/`IPublisher` where possible |
| `AddMediatR(cfg => ...)` | `AddRogue()` | Assembly scan replaced by compile-time discovery |
| Open-generic request handlers | `ReflectionMediator` escape hatch | Not AOT-safe; see ROGM003 |

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

After migration, the compat `using SkathIO.Rogue.Compatibility;` lines can be removed once all handler implementations use `SkathIO.Rogue` directly. The `SkathIO.Rogue.MediatR` package can also be removed once migration is complete.
