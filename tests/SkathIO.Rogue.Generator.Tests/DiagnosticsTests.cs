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
public class GetUser : ICommand<string> { }
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

public class GetUser : ICommand<string> { }

public class GetUserHandler : ICommandHandler<GetUser, string>
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

public class OrderPlaced : IEvent { }
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

public class GetUser : ICommand<string> { }

public class GetUserHandler1 : ICommandHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}

public class GetUserHandler2 : ICommandHandler<GetUser, string>
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

public class GetUser : ICommand<string> { }
public class GetUserHandler : ICommandHandler<GetUser, string>
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

public class GetUser : ICommand<string> { }

// Abstract handler — cannot be registered by DI
public abstract class GetUserHandlerBase : ICommandHandler<GetUser, string>
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

public class GetUser : ICommand<string> { }

public class GetUserHandler : ICommandHandler<GetUser, string>
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
public class GetItem<T> : ICommand<T> { }
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

public class GetUser : ICommand<string> { }
public class GetUserHandler : ICommandHandler<GetUser, string>
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
public class GetUser : ICommand<string> { }

// Handler declares int response — MISMATCH
public class GetUserHandler : ICommandHandler<GetUser, int>
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

public class GetUserHandler : ICommandHandler<SomeExternalRequest, string>
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

public class GetUser : ICommand<string> { }
public class GetUserHandler : ICommandHandler<GetUser, string>
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

public class GetUser : ICommand<string> { }

// Handler depends on IUserRepository, which isn't a Rogue-registered type
public class GetUserHandler : ICommandHandler<GetUser, string>
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

public class GetUser : ICommand<string> { }

// TextWriter is a System.* type — should not trigger ROGUE004
public class GetUserHandler : ICommandHandler<GetUser, string>
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

    // ── ROGUE011 (multiple CQS contracts — F5, PD-40 clean break) ─────────────────

    [Fact]
    public void ROGUE011_Fires_WhenTypeIsBothCommandAndQuery()
    {
        // Under the clean break there is no shared marker to disambiguate, so a type that is both a
        // command and a query is ambiguous — the generator cannot pick a dispatch path.
        const string source = @"
using SkathIO.Rogue;

public class Ambiguous : ICommand<string>, IQuery<string> { }
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, static d =>
            d.Id == "ROGUE011" &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ROGUE011_DoesNotFire_ForSingleContractType()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IQuery<string> { }
public class GetUserHandler : IQueryHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser query, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE011");
    }

    [Fact]
    public void ROGUE011_DoesNotFire_ForVoidCommand_WhichReportsICommandAndICommandOfUnit()
    {
        // ICommand : ICommand<Unit>, so a void command surfaces BOTH ICommand and ICommand<Unit>.
        // Those are the SAME family — ROGUE011 must NOT treat that as multiple contracts.
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class DoThing : ICommand { }
public class DoThingHandler : ICommandHandler<DoThing>
{
    public ValueTask Handle(DoThing command, CancellationToken cancellationToken)
        => default;
}
";
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE011");
    }

    // ── ROGUE012 (adapter command-vs-query mapping conflict — PD-43 amendment / PD-48) ──

    // Inline MediatR-adapter surface (the SkathIO.Rogue.MediatR package is not referenced by this
    // harness; the generator keys on metadata FQN strings, so declaring the types inline in the right
    // namespaces is sufficient — see AdapterMappingTests for the full rationale). All top-level usings
    // are hoisted to the very top of the source (ahead of these namespace blocks) so the source compiles
    // cleanly — a CS1529 (usings after namespaces) leaves [MapAsQuery] as an unresolved error type, and
    // the conflict (which depends on the attribute's semantic class) would silently not fire.
    private const string AdapterSurfaceForRogue012 = @"
namespace SkathIO.Rogue.Compatibility
{
    public interface IRequest<out TResponse> { }
    public interface IRequest : IRequest<global::SkathIO.Rogue.Unit> { }
    public interface IRequestHandler<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        System.Threading.Tasks.ValueTask<TResponse> Handle(TRequest request, System.Threading.CancellationToken cancellationToken);
    }
    public interface IRequestHandler<in TRequest>
        where TRequest : IRequest<global::SkathIO.Rogue.Unit>
    {
        System.Threading.Tasks.ValueTask Handle(TRequest request, System.Threading.CancellationToken cancellationToken);
    }
}
namespace SkathIO.Rogue.MediatR
{
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)]
    public sealed class MapAsQueryAttribute : System.Attribute { }
}
";

    private const string AdapterUsings = @"
using SkathIO.Rogue;
using SkathIO.Rogue.Compatibility;
using SkathIO.Rogue.MediatR;
using System.Threading;
using System.Threading.Tasks;
";

    private static string AdapterSource(string userTypes) => AdapterUsings + AdapterSurfaceForRogue012 + userTypes;

    [Fact]
    public void ROGUE012_Fires_WhenMapAsQueryOnNoResponseRequest()
    {
        // [MapAsQuery] on a no-response IRequest is a conflict — a query must return a value.
        string source = AdapterSource(@"
[MapAsQuery]
public class BadQuery : IRequest { }
public class BadQueryHandler : IRequestHandler<BadQuery>
{
    public ValueTask Handle(BadQuery request, CancellationToken ct) => default;
}
");
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.Contains(diagnostics, static d =>
            d.Id == "ROGUE012" &&
            d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ROGUE012_DoesNotFire_WhenMapAsQueryOnResponseBearingRequest()
    {
        // [MapAsQuery] on a request WITH a response is the normal IQuery mapping — no conflict.
        string source = AdapterSource(@"
[MapAsQuery]
public class GetOrder : IRequest<string> { }
public class GetOrderHandler : IRequestHandler<GetOrder, string>
{
    public ValueTask<string> Handle(GetOrder request, CancellationToken ct) => new ValueTask<string>(""order"");
}
");
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE012");
    }

    [Fact]
    public void ROGUE012_DoesNotFire_WhenMapAsQueryAbsent()
    {
        // A no-response adapter request WITHOUT [MapAsQuery] is a plain void command — no conflict.
        string source = AdapterSource(@"
public class Notify : IRequest { }
public class NotifyHandler : IRequestHandler<Notify>
{
    public ValueTask Handle(Notify request, CancellationToken ct) => default;
}
");
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        System.Collections.Generic.List<Diagnostic> diagnostics =
            result.Results.SelectMany(static r => r.Diagnostics).ToList();

        Assert.DoesNotContain(diagnostics, static d => d.Id == "ROGUE012");
    }

    [Fact]
    public void ROGUE012_DefaultSeverity_IsWarning()
    {
        Assert.Equal("ROGUE012", DiagnosticDescriptors.AdapterMappingConflict.Id);
        Assert.Equal(DiagnosticSeverity.Warning, DiagnosticDescriptors.AdapterMappingConflict.DefaultSeverity);
    }

    // ── ROGUE007 must NOT exist (removed-from-scope id; gate per phases.md) ────────

    [Fact]
    public void ROGUE007_Descriptor_DoesNotExist()
    {
        // ROGUE007 is the removed-from-scope behavior-order id; its descriptor must not be reintroduced
        // (including by the ROGUE011 addition, which deliberately skips 007).
        bool any007 = typeof(DiagnosticDescriptors)
            .GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                     | System.Reflection.BindingFlags.Public)
            .Where(static f => f.FieldType == typeof(DiagnosticDescriptor))
            .Select(static f => (DiagnosticDescriptor)f.GetValue(null)!)
            .Any(static d => d.Id == "ROGUE007");
        Assert.False(any007, "ROGUE007 descriptor must not exist (removed-from-scope id).");
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
        Assert.StartsWith("https://", DiagnosticDescriptors.MultipleCqsContracts.HelpLinkUri);
        Assert.StartsWith("https://", DiagnosticDescriptors.AdapterMappingConflict.HelpLinkUri);
    }

    [Fact]
    public void AllErrorDescriptors_AreEnabledByDefault()
    {
        Assert.True(DiagnosticDescriptors.NoHandler.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.DuplicateHandler.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.ResponseTypeMismatch.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.AbstractOrNoUsableCtor.IsEnabledByDefault);
        Assert.True(DiagnosticDescriptors.MultipleCqsContracts.IsEnabledByDefault);
    }

    [Fact]
    public void ROGUE011_DefaultSeverity_IsError()
    {
        Assert.Equal("ROGUE011", DiagnosticDescriptors.MultipleCqsContracts.Id);
        Assert.Equal(DiagnosticSeverity.Error, DiagnosticDescriptors.MultipleCqsContracts.DefaultSeverity);
    }
}
