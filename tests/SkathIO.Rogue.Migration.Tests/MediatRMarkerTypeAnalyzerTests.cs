using System.Threading.Tasks;
using SkathIO.Rogue.Migration.Analyzer;
using Xunit;

namespace SkathIO.Rogue.Migration.Tests;

/// <summary>
/// Unit coverage for the ROGM006 marker-rewrite analyzer + code-fix and the ROGM005 ambiguity
/// diagnostic. The end-to-end AC-F gate exercises the whole ~50-handler sample; these tests pin the
/// per-shape mapping (query/command/event, void command, ambiguity → safe default) so a regression in
/// the F8 convention surfaces at the unit level, not only via the recompile gate.
/// </summary>
public sealed class MediatRMarkerTypeAnalyzerTests
{
    private const string Stubs = @"
namespace MediatR {
  public interface IRequest<TResponse> { }
  public interface IRequest { }
  public interface INotification { }
  public interface IRequestHandler<TRequest, TResponse> { }
  public interface IRequestHandler<TRequest> { }
  public interface INotificationHandler<TNotification> { }
}
";

    private static string WithStubs(string code) => "using MediatR;\n" + code + Stubs;

    [Fact]
    public async Task ROGM006_Fires_OnRequestMarker()
    {
        var code = WithStubs("public sealed record GetThingQuery(string Id) : IRequest<string>;");
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new MediatRMarkerTypeAnalyzer());
        Assert.Contains(diagnostics, d => d.Id == "ROGM006");
    }

    [Fact]
    public async Task ROGM006_DoesNotFire_OnCqsContract()
    {
        var code = "public interface IQuery<T> { }\npublic sealed record GetThingQuery(string Id) : IQuery<string>;";
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new MediatRMarkerTypeAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM006");
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsQueryNamedRequest_ToIQuery()
    {
        var code = WithStubs("public sealed record GetThingQuery(string Id) : IRequest<string>;");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains(": IQuery<string>", fixedCode);
        Assert.DoesNotContain(": IRequest<string>", fixedCode);
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsCommandNamedRequest_ToICommand()
    {
        var code = WithStubs("public sealed record CreateThingCommand(string Name) : IRequest<bool>;");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains(": ICommand<bool>", fixedCode);
        Assert.DoesNotContain("IQuery", fixedCode);
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsNoResponseRequest_ToVoidICommand()
    {
        var code = WithStubs("public sealed record CancelThingCommand(string Id) : IRequest;");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        // The void IRequest maps to the parameterless ICommand. Guard against accidentally producing
        // ICommand<...> (the response-bearing shape) — the rewritten base must be the bare `ICommand`.
        Assert.Contains(": ICommand", fixedCode);
        Assert.DoesNotContain("ICommand<", fixedCode);
        Assert.DoesNotContain(": IRequest", fixedCode);
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsNotification_ToIEvent()
    {
        var code = WithStubs("public sealed record ThingHappened(string Id) : INotification;");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains(": IEvent", fixedCode);
        // The record's base list no longer names INotification (the stub *declaration*
        // `interface INotification { }` legitimately remains in this single-file fixture).
        Assert.DoesNotContain(": INotification", fixedCode);
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsQueryHandler_ToIQueryHandler()
    {
        // A response-bearing handler classifies by its request type's (first type argument) name.
        var code = WithStubs("public sealed class H : IRequestHandler<GetThingQuery, string> { }");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains("IQueryHandler<GetThingQuery, string>", fixedCode);
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsCommandHandler_ToICommandHandler()
    {
        var code = WithStubs("public sealed class H : IRequestHandler<CreateThingCommand, bool> { }");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains("ICommandHandler<CreateThingCommand, bool>", fixedCode);
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsVoidHandler_ToVoidICommandHandler()
    {
        var code = WithStubs("public sealed class H : IRequestHandler<CancelThingCommand> { }");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains("ICommandHandler<CancelThingCommand>", fixedCode);
    }

    [Fact]
    public async Task ROGM005_Fires_OnAmbiguousResponseBearingRequest()
    {
        // "ProcessThing" signals neither read (Query) nor write (Command) → ambiguous.
        var code = WithStubs("public sealed record ProcessThing(string Id) : IRequest<string>;");
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new MediatRMarkerTypeAnalyzer());
        Assert.Contains(diagnostics, d => d.Id == "ROGM005");
    }

    [Fact]
    public async Task ROGM005_DoesNotFire_OnQueryNamedRequest()
    {
        var code = WithStubs("public sealed record GetThingQuery(string Id) : IRequest<string>;");
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new MediatRMarkerTypeAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM005");
    }

    [Fact]
    public async Task ROGM005_DoesNotFire_OnCommandNamedRequest()
    {
        var code = WithStubs("public sealed record CreateThingCommand(string Name) : IRequest<bool>;");
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new MediatRMarkerTypeAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM005");
    }

    [Fact]
    public async Task ROGM005_DoesNotFire_OnNoResponseRequest()
    {
        // A no-response request is always a void command — no command-vs-query ambiguity.
        var code = WithStubs("public sealed record DoThing(string Id) : IRequest;");
        var diagnostics = await AnalyzerTestHelper.RunAsync(code, new MediatRMarkerTypeAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "ROGM005");
    }

    [Fact]
    public async Task ROGM006_CodeFix_MapsAmbiguousRequest_ToSafeDefaultICommand()
    {
        // Ambiguous intent is migrated to the safe default ICommand<T> — never silently a query.
        var code = WithStubs("public sealed record ProcessThing(string Id) : IRequest<string>;");
        var fixedCode = await AnalyzerTestHelper.ApplyCodeFixAsync(
            code, new MediatRMarkerTypeAnalyzer(), new MigrateMediatRMarkerTypeCodeFix());
        Assert.Contains(": ICommand<string>", fixedCode);
        Assert.DoesNotContain("IQuery", fixedCode);
    }
}
