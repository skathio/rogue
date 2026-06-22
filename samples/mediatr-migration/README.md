# MediatR migration sample

Before/after sample for the MediatR → SkathIO.Rogue migration path.

## `before/` — a real ~50-marker, multi-domain MediatR codebase

`before/` is a domain-grouped feature-folder sample (not a single-handler stub) shaped to exercise
the corner cases the migration must survive:

| Domain          | Shapes covered                                                                    |
|-----------------|-----------------------------------------------------------------------------------|
| `Catalog/`      | request/response query, list query, void `IRequest` command, explicit `IRequest<Unit>` command |
| `Orders/`       | command returning a result, an **already-`ValueTask`** query (ROGM002 must NOT fire), void command |
| `Customers/`    | query, command returning a value, **partial-class handler split across two files** (overlapping edits) |
| `Inventory/`    | query, command returning `bool`, an **open-generic** request (ROGM003 fires, no auto-fix)  |
| `Shipping/`     | two request/response queries returning record-struct results                      |
| `Notifications/`| an `INotification` with **two** handlers (fan-out) + a second notification with one handler |
| `Reporting/`    | report query, void export command                                                 |

`MediatRStubs.cs` declares the minimal `MediatR` namespace the sample compiles against (there is no
real MediatR package on the migration-gate's in-memory compile path). `Program.cs` invokes a
representative subset of handlers directly (no DI container) so the migrated sample can be compiled,
loaded, and run end-to-end.

## `after/Program.cs` — illustrative migrated output

A representative excerpt (query / void command / fan-out notification) showing the post-migration
shape: `using SkathIO.Rogue;` and `ValueTask`-returning handlers. The migration code-fixes are
ROGM001 (`using` rewrite) and ROGM002 (`Task` → `ValueTask`). The compat
`using SkathIO.Rogue.Compatibility;` is added by hand only where DI-only helpers (e.g. `AddMediatR`)
are called — see `docs/migration-guide.md`.

## Automated migration gate

`tests/SkathIO.Rogue.Migration.Tests/MigrationGateTests.cs` runs the whole `before/` sample through
the analyzer code-fixes to a fixed point, recompiles the migrated source against the real
`SkathIO.Rogue` assemblies, runs its entry point, and asserts the end-to-end migration completes
within the 15-minute ceiling — an executable gate, not a documentation illustration.
