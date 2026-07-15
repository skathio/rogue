namespace SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;

internal static class WellKnownTypeNames
{
    // FQN with backtick-arity notation used in Roslyn metadata names. This project takes no
    // PackageReference on FluentValidation itself — Roslyn semantic symbols carry the fully-qualified
    // metadata name regardless of what the generator project itself references, the same trick
    // RogueGenerator.cs uses for IPipelineBehavior`2.
    public const string IValidator1 = "FluentValidation.IValidator`1";
}
