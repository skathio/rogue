using System.Linq;
using Microsoft.CodeAnalysis;
using SkathIO.Rogue.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

public sealed class DiagnosticsTests
{
    // ── ROGUE001 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE001_Fires_WhenRequestHasNoHandler()
    {
        const string source = @"
using SkathIO.Rogue;

// A request type with no handler registered
public class GetUser : IRequest<string> { }
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, static d =>
            d.Id == "ROGUE001" &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ROGUE001_DoesNotFire_WhenHandlerPresent()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }

public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE001");
    }

    [Fact]
    public void ROGUE001_DoesNotFire_ForNotification_WithNoHandlers()
    {
        // FR-13: notifications MAY have zero handlers — not an error
        const string source = @"
using SkathIO.Rogue;

public class OrderPlaced : INotification { }
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE001");
    }

    // ── ROGUE002 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE002_Fires_WhenTwoHandlersPresentForSameRequest()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }

public class GetUserHandler1 : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}

public class GetUserHandler2 : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, static d =>
            d.Id == "ROGUE002" &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ROGUE002_DoesNotFire_WithExactlyOneHandler()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }
public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);

        Assert.DoesNotContain(
            result.Results.SelectMany(static r => r.Diagnostics),
            static d => d.Id == "ROGUE002");
    }

    // ── ROGUE005 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE005_Fires_ForAbstractHandler()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }

// Abstract handler — cannot be registered by DI
public abstract class GetUserHandlerBase : IRequestHandler<GetUser, string>
{
    public abstract ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, static d =>
            d.Id == "ROGUE005" &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ROGUE005_DoesNotFire_ForConcreteHandlerWithPublicCtor()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }

public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE005");
    }

    // ── ROGUE006 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE006_Fires_ForOpenGenericRequest()
    {
        const string source = @"
using SkathIO.Rogue;

// Open-generic request type — generator cannot emit a static dispatch path
public class GetItem<T> : IRequest<T> { }
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, static d =>
            d.Id == "ROGUE006" &&
            d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ROGUE006_DoesNotFire_ForClosedRequest()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }
public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE006");
    }

    // ── ROGUE003 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE003_Fires_WhenHandlerResponseTypeMismatch()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

// Request declares string response
public class GetUser : IRequest<string> { }

// Handler declares int response — MISMATCH
public class GetUserHandler : IRequestHandler<GetUser, int>
{
    public ValueTask<int> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<int>(42);
}
";
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        var diagnostics = result.Results.SelectMany(r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, d =>
            d.Id == "ROGUE003" &&
            d.Severity == DiagnosticSeverity.Error);
        // Ensure the mismatch doesn't spuriously also fire ROGUE001
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGUE001");
    }

    [Fact]
    public void ROGUE003_DoesNotFire_WhenRequestTypeAbsentFromCompilation()
    {
        // Handler-only compilation (request type in another project per PD-10) — no ROGUE003
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUserHandler : IRequestHandler<SomeExternalRequest, string>
{
    public ValueTask<string> Handle(SomeExternalRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(string.Empty);
}
";
        // SomeExternalRequest is not defined in this compilation — ROGUE003 must NOT fire
        // (the cross-compilation skip per PD-10). Note: compilation will have CS0246 errors,
        // but the generator should still run and not fire ROGUE003.
        var result = GeneratorTestHelper.RunGenerator(source);
        Assert.DoesNotContain(
            result.Results.SelectMany(r => r.Diagnostics),
            d => d.Id == "ROGUE003");
    }

    [Fact]
    public void ROGUE003_DoesNotFire_WhenResponseTypesMatch()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }
public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        Assert.DoesNotContain(
            result.Results.SelectMany(r => r.Diagnostics),
            d => d.Id == "ROGUE003");
    }

    // ── ROGUE004 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE004_Fires_WhenHandlerHasUnregisteredApplicationDependency()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

// A custom application service — not registered by the generator
public interface IUserRepository { }

public class GetUser : IRequest<string> { }

// Handler depends on IUserRepository, which isn't a Rogue-registered type
public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public GetUserHandler(IUserRepository repo) { }
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        var diagnostics = result.Results.SelectMany(r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, d =>
            d.Id == "ROGUE004" &&
            d.Severity == DiagnosticSeverity.Warning);
        // Concrete handler with public ctor — no ROGUE005
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGUE005");
    }

    [Fact]
    public void ROGUE004_DoesNotFire_ForFrameworkDependencies()
    {
        // Uses System.IO.TextWriter — a System.* type that should not trigger ROGUE004.
        // This exercises the same IsWellKnownFrameworkType "System." prefix branch as
        // ILogger<T> (Microsoft.*) would, without requiring an additional assembly reference
        // in the test harness.
        const string source = @"
using SkathIO.Rogue;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }

// TextWriter is a System.* type — should not trigger ROGUE004
public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public GetUserHandler(TextWriter writer) { }
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        Assert.DoesNotContain(
            result.Results.SelectMany(r => r.Diagnostics),
            d => d.Id == "ROGUE004");
    }

    // ── ROGUE010 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROGUE010_IsEnabledByDefault()
    {
        // ROGUE010 is Info severity (non-intrusive); enabled by default, suppressible via editorconfig
        Assert.True(DiagnosticDescriptors.IMediatorNudge.IsEnabledByDefault);
    }

    [Fact]
    public void ROGUE010_DefaultSeverity_IsInfo()
    {
        Assert.Equal(DiagnosticSeverity.Info, DiagnosticDescriptors.IMediatorNudge.DefaultSeverity);
    }

    // ── Descriptor invariants ─────────────────────────────────────────────────────

    [Fact]
    public void AllDescriptors_HaveHelpLink()
    {
        Assert.StartsWith("https://", DiagnosticDescriptors.NoHandler.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.DuplicateHandler.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.ResponseTypeMismatch.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.UnconstructableType.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.AbstractOrNoUsableCtor.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.OpenGenericRequest.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.IMediatorNudge.HelpLinkUri);
    }

    [Fact]
    public void AllErrorDescriptors_AreEnabledByDefault()
    {
        Assert.True(DiagnosticDescriptors.NoHandler.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.DuplicateHandler.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.ResponseTypeMismatch.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.AbstractOrNoUsableCtor.IsEnabledByDefault);
    }
}
