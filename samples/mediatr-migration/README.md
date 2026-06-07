# MediatR migration sample

Before/after sample for the MediatR → SkathIO.Rogue migration path.

- `before/Program.cs` — MediatR-based code (`using MediatR;`, `AddMediatR(...)`, handler returns `Task<T>`).
- `after/Program.cs` — the same program after the `SkathIO.Rogue.MediatR` migration code-fixes run
  (`using SkathIO.Rogue;`, `AddRogue()`, handler returns `ValueTask<T>`).

These are documentation samples illustrating the analyzer code-fix output (ROGM001/ROGM002); they are
not standalone buildable projects. See `docs/migration-guide.md` for the full walkthrough and the
feature-mapping table.
