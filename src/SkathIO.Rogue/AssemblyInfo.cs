using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SkathIO.Rogue.UnitTests")]
// SkathIO.Rogue.Generator.Tests needs the internal SnapshotRegistrars/RestoreRegistrars to isolate the
// process-global RogueRegistrationBridge registry between cases that each load a distinct generated
// assembly (PD-33's append-only registry accumulates every loaded assembly's registrar; without a reset
// a later case's AddRogue would invoke earlier cases' registrars too — and TryAdd's first-wins could let
// an earlier assembly's RogueDispatcherImpl shadow the current one). Granting IVT here is safe because
// Generator.Tests references the generator as a plain ProjectReference (NOT OutputItemType="Analyzer"),
// so the generator does not run over its own compilation — no generated RogueDispatcherImpl exists in
// the test compilation to cause the CS0436 conflict the note below warns about.
[assembly: InternalsVisibleTo("SkathIO.Rogue.Generator.Tests")]
// Note: SkathIO.Rogue.Integration.Tests does NOT get InternalsVisibleTo — that would expose the
// empty generated types (RogueDispatcherImpl etc.) from this DLL into the test compilation,
// causing CS0436 conflicts when the generator re-emits those types with handlers populated.
