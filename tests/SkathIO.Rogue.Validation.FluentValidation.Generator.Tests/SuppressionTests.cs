using System.Linq;
using Xunit;

namespace SkathIO.Rogue.Validation.FluentValidation.Generator.Tests;

/// <summary>
/// Proves a zero-validator compilation emits no module initializer at all. Mirrors
/// <c>SkathIO.Rogue.Generator.Tests/MultiProjectBehaviorSuppressionTests</c>'s intent, adapted per
/// D2: this generator has no metadata-scan discovery path, so there is no
/// <c>IsMetadata</c>-tie-break case to reproduce (the exact shape that caused GitHub issue #21 for
/// the core generator). This suite proves the strictly simpler claim instead: "zero source-declared
/// validators ⇒ no module init," full stop — structurally guaranteed by D2's source-only design, but
/// proven here rather than only argued (spec.md §11 risk row).
/// </summary>
public sealed class SuppressionTests
{
    private static string ModuleInitText(string source) =>
        GeneratorTestHelper.RunGeneratorAndAssertClean(source)
            .Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueFluentValidationModuleInit.g.cs")
            .SourceText.ToString();

    [Fact]
    public void ZeroValidatorCompilation_EmitsNoModuleInitializerAtAll()
    {
        const string source = "// no validators here at all";
        var text = ModuleInitText(source);

        Assert.DoesNotContain("[global::System.Runtime.CompilerServices.ModuleInitializer]", text);
        Assert.DoesNotContain("RogueRegistrationBridge", text);
        Assert.DoesNotContain("internal static class RogueFluentValidationModuleInit", text);

        // The #if/#endif shell is still present (stable hint name, empty body when suppressed).
        Assert.Contains("#if !NETSTANDARD2_0", text);
        Assert.Contains("#endif", text);
    }

    [Fact]
    public void CompilationWithOnlyAbstractAndNoCtorCandidates_StillSuppressesModuleInit()
    {
        // Candidates exist syntactically, but every one of them is filtered out downstream
        // (abstract, no public ctor) — the DiscoveredValidators set is still empty, so suppression
        // must still hold. This is the closest FV-generator analogue of GH #21's "something looked
        // discoverable but shouldn't count" shape, exercised without any metadata scan involved.
        const string source = @"
using FluentValidation;

public class Ping { }

public abstract class AbstractOnly : AbstractValidator<Ping>
{
}

public class NoPublicCtor : AbstractValidator<Ping>
{
    private NoPublicCtor() { }
}
";
        var text = ModuleInitText(source);

        Assert.DoesNotContain("[global::System.Runtime.CompilerServices.ModuleInitializer]", text);
        Assert.DoesNotContain("RogueRegistrationBridge", text);
    }

    [Fact]
    public void CompilationWithAtLeastOneRealValidator_IsNotSuppressed()
    {
        // Control case (mirrors MultiProjectBehaviorSuppressionTests'
        // HostCompilation_WithSourceDeclaredBehaviorOnly_StillRegisters): a populated compilation
        // must NOT be suppressed — pins the suppression check isn't accidentally over-broad.
        const string source = @"
using FluentValidation;

public class Ping { }

public class PingValidator : AbstractValidator<Ping>
{
    public PingValidator() { }
}
";
        var text = ModuleInitText(source);

        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]", text);
        Assert.Contains("global::SkathIO.Rogue.RogueRegistrationBridge.Register(", text);
    }
}
