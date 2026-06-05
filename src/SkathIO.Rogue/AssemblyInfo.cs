using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SkathIO.Rogue.UnitTests")]
// Note: SkathIO.Rogue.Integration.Tests does NOT get InternalsVisibleTo — that would expose the
// empty generated types (RogueDispatcherImpl etc.) from this DLL into the test compilation,
// causing CS0436 conflicts when the generator re-emits those types with handlers populated.
