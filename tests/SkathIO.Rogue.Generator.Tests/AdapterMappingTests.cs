using System.Linq;
using Microsoft.CodeAnalysis;
using SkathIO.Rogue.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// PD-43 amendment / PD-48 — the MediatR-adapter mapping rule. A class implementing the adapter's
/// self-contained <c>SkathIO.Rogue.Compatibility.IRequestHandler&lt;,&gt;</c> / <c>IRequestHandler&lt;&gt;</c>
/// is discovered and mapped onto the CQS dispatch path by the F8 command-vs-query convention:
/// default → <c>ICommand&lt;T&gt;</c>, <c>[MapAsQuery]</c> → <c>IQuery&lt;T&gt;</c>, no-response → void
/// <c>ICommand</c>, and <c>[MapAsQuery]</c> + no-response → ROGUE012. Adapter messages dispatch through
/// the object-dispatch path (they do not implement the core CQS markers).
/// <para>
/// The adapter surface (<c>SkathIO.Rogue.Compatibility.*</c> and <c>SkathIO.Rogue.MediatR.MapAsQuery</c>)
/// lives in the <c>SkathIO.Rogue.MediatR</c> package, which the generator test harness does NOT reference
/// (it references <c>SkathIO.Rogue.Abstractions</c> only). The generator keys on the metadata FQN strings,
/// so the adapter types are declared inline in each test's source in the exact namespaces/names the
/// generator matches — the same technique core CQS tests use against the real Abstractions assembly.
/// </para>
/// </summary>
[Collection(RealDiDispatchCollection.Name)] // shares the process-global registration bridge — see RealDiDispatchCollection
public sealed class AdapterMappingTests
{
    // Inline adapter surface — namespaces/names/shapes must match WellKnownTypeNames.Adapter* and
    // MapAsQueryAttribute exactly (this is what the generator discovers). ValueTask-based, mirroring
    // the real Compatibility/IRequest.cs / IRequestHandler.cs / MapAsQueryAttribute.cs. Declared as
    // namespace blocks with NO leading usings so the combined test source keeps all top-level `using`
    // directives ahead of every namespace declaration (C# CS1529 otherwise) when fed to the
    // compile-verification compilation.
    private const string AdapterSurface = @"
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

    // Common usings hoisted to the very top of every test source (must precede the adapter namespace
    // blocks). The adapter types are referenced fully-qualified-via-using.
    private const string Usings = @"
using SkathIO.Rogue;
using SkathIO.Rogue.Compatibility;
using SkathIO.Rogue.MediatR;
using System.Threading;
using System.Threading.Tasks;
";

    /// <summary>usings (top) + adapter surface namespaces + the test's user types.</summary>
    private static string Source(string userTypes) => Usings + AdapterSurface + userTypes;

    private static HandlerModel SingleHandler(string source)
    {
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);
        return Assert.Single(models.Handlers);
    }

    // ── Discovery + F8 mapping ──────────────────────────────────────────────────────

    [Fact]
    public void AdapterRequestHandler_WithResponse_Unmarked_MapsToCommand()
    {
        // F8 default: an adapter IRequest<T> handler (no [MapAsQuery]) maps to ICommand<T> (safe — assumes
        // possible side effects).
        string source = Source(@"
public class CreateOrder : IRequest<string> { }
public class CreateOrderHandler : IRequestHandler<CreateOrder, string>
{
    public ValueTask<string> Handle(CreateOrder request, CancellationToken ct) => new ValueTask<string>(""ok"");
}
");
        HandlerModel handler = SingleHandler(source);

        Assert.True(handler.IsAdapterMapped);
        Assert.Equal(HandlerKind.Command, handler.Kind);
        Assert.Contains("CreateOrder", handler.RequestFqn);
        Assert.Contains("string", handler.ResponseFqn);
        Assert.False(handler.MapAsQueryConflict);
    }

    [Fact]
    public void AdapterRequestHandler_WithResponse_MapAsQuery_MapsToQuery()
    {
        // F8 override: [MapAsQuery] on the request maps the handler to IQuery<T>.
        string source = Source(@"
[MapAsQuery]
public class GetOrder : IRequest<string> { }
public class GetOrderHandler : IRequestHandler<GetOrder, string>
{
    public ValueTask<string> Handle(GetOrder request, CancellationToken ct) => new ValueTask<string>(""order"");
}
");
        HandlerModel handler = SingleHandler(source);

        Assert.True(handler.IsAdapterMapped);
        Assert.Equal(HandlerKind.Query, handler.Kind);
        Assert.Contains("GetOrder", handler.RequestFqn);
        Assert.False(handler.MapAsQueryConflict);
    }

    [Fact]
    public void AdapterRequestHandler_NoResponse_MapsToVoidCommand()
    {
        // F8: a no-response adapter IRequest handler maps to a void ICommand.
        string source = Source(@"
public class Notify : IRequest { }
public class NotifyHandler : IRequestHandler<Notify>
{
    public ValueTask Handle(Notify request, CancellationToken ct) => default;
}
");
        HandlerModel handler = SingleHandler(source);

        Assert.True(handler.IsAdapterMapped);
        Assert.Equal(HandlerKind.Command, handler.Kind);
        Assert.Null(handler.ResponseFqn);
        Assert.False(handler.MapAsQueryConflict);
    }

    [Fact]
    public void AdapterRequestHandler_NoResponse_MapAsQuery_RecordsConflict()
    {
        // ROGUE012 conflict source: [MapAsQuery] on a no-response IRequest. Still mapped to a void command
        // (not dropped); the conflict flag drives ROGUE012 (asserted in DiagnosticsTests).
        string source = Source(@"
[MapAsQuery]
public class BadQuery : IRequest { }
public class BadQueryHandler : IRequestHandler<BadQuery>
{
    public ValueTask Handle(BadQuery request, CancellationToken ct) => default;
}
");
        HandlerModel handler = SingleHandler(source);

        Assert.True(handler.IsAdapterMapped);
        Assert.Equal(HandlerKind.Command, handler.Kind);   // still a void command — not silently dropped
        Assert.Null(handler.ResponseFqn);
        Assert.True(handler.MapAsQueryConflict);
    }

    // ── Emission: registration + dispatch shape ─────────────────────────────────────

    [Fact]
    public void AdapterRequestHandler_RegistersUnderAdapterInterface_NotCoreCqs()
    {
        // PD-48 register/resolve lockstep: an adapter handler registers under the adapter's own
        // Compatibility.IRequestHandler<TReq,TResp>, NOT the core ICommandHandler/IQueryHandler (which it
        // does not implement).
        string source = Source(@"
public class CreateOrder : IRequest<string> { }
public class CreateOrderHandler : IRequestHandler<CreateOrder, string>
{
    public ValueTask<string> Handle(CreateOrder request, CancellationToken ct) => new ValueTask<string>(""ok"");
}
");
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        string reg = result.Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueServiceCollectionExtensions.g.cs").SourceText.ToString();

        Assert.Contains("global::SkathIO.Rogue.Compatibility.IRequestHandler<global::CreateOrder, global::System.String>", reg);
        // It must NOT be registered under the core CQS handler interface.
        Assert.DoesNotContain("global::SkathIO.Rogue.ICommandHandler<global::CreateOrder", reg);
        Assert.DoesNotContain("global::SkathIO.Rogue.IQueryHandler<global::CreateOrder", reg);
    }

    [Fact]
    public void AdapterRequestHandler_DispatchesViaObjectSwitch_NotTypedSendSwitch()
    {
        // PD-48: adapter messages are excluded from the typed-Send switches (CS8121 otherwise) and
        // dispatch only through SendObject. The SendObject switch must have a case for the adapter request.
        string source = Source(@"
public class CreateOrder : IRequest<string> { }
public class CreateOrderHandler : IRequestHandler<CreateOrder, string>
{
    public ValueTask<string> Handle(CreateOrder request, CancellationToken ct) => new ValueTask<string>(""ok"");
}
");
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        string dispatcher = result.Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueDispatcher.g.cs").SourceText.ToString();

        // SendObject switch arm for the adapter message
        Assert.Contains("public override global::System.Threading.Tasks.ValueTask<object?> SendObject(", dispatcher);
        Assert.Contains("case global::CreateOrder r:", dispatcher);
        // The per-request Send method resolves the ADAPTER handler interface.
        Assert.Contains("GetRequiredService<global::SkathIO.Rogue.Compatibility.IRequestHandler<global::CreateOrder, global::System.String>>", dispatcher);
    }

    [Fact]
    public void AdapterRequestHandler_GeneratedDispatcher_Compiles()
    {
        // The generated dispatcher + registration must compile against the adapter surface (declared inline
        // here so the compile-verification compilation sees the same types the generated code references).
        string source = Source(@"
public class CreateOrder : IRequest<string> { }
public class CreateOrderHandler : IRequestHandler<CreateOrder, string>
{
    public ValueTask<string> Handle(CreateOrder request, CancellationToken ct) => new ValueTask<string>(""ok"");
}

[MapAsQuery]
public class GetOrder : IRequest<string> { }
public class GetOrderHandler : IRequestHandler<GetOrder, string>
{
    public ValueTask<string> Handle(GetOrder request, CancellationToken ct) => new ValueTask<string>(""order"");
}

public class Notify : IRequest { }
public class NotifyHandler : IRequestHandler<Notify>
{
    public ValueTask Handle(Notify request, CancellationToken ct) => default;
}
");
        // Throws / fails the assertion if any generated code does not compile.
        GeneratorTestHelper.RunGeneratorAndAssertCompiles(source);
    }

    // ── Real-DI dispatch (end-to-end through IMediator.Send(object)) ─────────────────

    [Fact]
    public async System.Threading.Tasks.Task AdapterCommand_DispatchesThroughObjectSend_RealDI()
    {
        // End-to-end: an adapter IRequest<T> handler resolved via a real IServiceCollection/AddRogue and
        // dispatched through IMediator.Send(object) returns the handler's response.
        string source = Source(@"
public class CreateOrder : IRequest<string> { }
public class CreateOrderHandler : IRequestHandler<CreateOrder, string>
{
    public ValueTask<string> Handle(CreateOrder request, CancellationToken ct) => new ValueTask<string>(""created"");
}
");
        object result = await DispatchObjectViaRealDI(source, "CreateOrder");
        Assert.Equal("created", result);
    }

    [Fact]
    public async System.Threading.Tasks.Task AdapterQuery_MapAsQuery_DispatchesThroughObjectSend_RealDI()
    {
        // End-to-end: a [MapAsQuery]-marked adapter request resolves its handler via the adapter interface
        // (the F8 IQuery mapping does not change the resolved handler interface) and dispatches.
        string source = Source(@"
[MapAsQuery]
public class GetOrder : IRequest<string> { }
public class GetOrderHandler : IRequestHandler<GetOrder, string>
{
    public ValueTask<string> Handle(GetOrder request, CancellationToken ct) => new ValueTask<string>(""fetched"");
}
");
        object result = await DispatchObjectViaRealDI(source, "GetOrder");
        Assert.Equal("fetched", result);
    }

    [Fact]
    public async System.Threading.Tasks.Task AdapterVoidCommand_DispatchesThroughObjectSend_RealDI()
    {
        // End-to-end: a no-response adapter IRequest handler maps to a void command. The adapter's void
        // Handle returns bare ValueTask on every TFM (PD-48 unconditional Unit-wrap); object dispatch
        // returns the boxed Unit.
        string source = Source(@"
public class Notify : IRequest { }
public class NotifyHandler : IRequestHandler<Notify>
{
    public ValueTask Handle(Notify request, CancellationToken ct) => default;
}
");
        object result = await DispatchObjectViaRealDI(source, "Notify");
        Assert.Equal(SkathIO.Rogue.Unit.Value, result);
    }

    /// <summary>
    /// Compiles + loads the generated assembly, builds a real DI provider, resolves <c>IMediator</c>, and
    /// dispatches a fresh instance of <paramref name="requestTypeName"/> through <c>Send(object, ct)</c>.
    /// Returns the unwrapped result.
    /// </summary>
    private static async System.Threading.Tasks.Task<object> DispatchObjectViaRealDI(string source, string requestTypeName)
    {
        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(source);
        var provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var mediator = (SkathIO.Rogue.IMediator)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService(provider, typeof(SkathIO.Rogue.IMediator));

        var requestType = assembly.GetType(requestTypeName, throwOnError: true)!;
        var request = System.Activator.CreateInstance(requestType)!;

        // IMediator.Send(object request, CancellationToken) → ValueTask<object?>
        var sendObject = typeof(SkathIO.Rogue.ISender).GetMethods()
            .First(m => m.Name == "Send" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(object));

        object valueTask = sendObject.Invoke(mediator, new object?[] { request, System.Threading.CancellationToken.None })!;
        var asTask = (System.Threading.Tasks.Task<object?>)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        return (await asTask)!;
    }
}
