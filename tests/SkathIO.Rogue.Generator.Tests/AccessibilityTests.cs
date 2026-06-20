using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// NFR-12 / AC-I generated-code-safety assertions against the D5-reshaped emitter
/// (`ICommand&lt;T&gt;`/`IQuery&lt;T&gt;`/`IEvent`/`ICommandHandler&lt;,&gt;` — not the old
/// `IRequest`/`INotification` surface). Three properties:
/// <list type="number">
///   <item>(a) No accessibility widening — an <c>internal</c> message/handler stays internally
///   visible; the generated dispatcher/registration types are emitted <c>internal</c>, never
///   promoted to <c>public</c>, and a peer assembly without <c>[InternalsVisibleTo]</c> cannot bind
///   the generated <c>RogueDispatcherImpl</c>.</item>
///   <item>(b) No internals exposure — the generator does not emit any
///   <c>[InternalsVisibleTo]</c> attribute of its own; the only internals exposure is whatever the
///   user declared.</item>
///   <item>(c) No reflection-emit / runtime codegen — the emitted source contains no
///   <c>System.Reflection.Emit</c>, <c>Expression.Compile()</c>, <c>DynamicMethod</c>,
///   <c>ILGenerator</c>, <c>MakeGenericMethod</c>, or <c>Activator.CreateInstance</c> on the core
///   dispatch path. This is a static source inspection of the emitted <c>.cs</c> string.</item>
/// </list>
/// </summary>
[Collection(RealDiDispatchCollection.Name)] // EmitAndLoadAssembly touches the process-global bridge
public sealed class AccessibilityTests
{
    // An internal command + internal handler exercising the D5-reshaped CQS contracts. The
    // [InternalsVisibleTo] is the USER's own declaration — the generator must neither widen these
    // types' accessibility nor add an InternalsVisibleTo of its own.
    private const string InternalCqsSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(""FriendAsm"")]
internal sealed class InternalPing : ICommand<string> { }
internal sealed class InternalPingHandler : ICommandHandler<InternalPing, string>
{
    public ValueTask<string> Handle(InternalPing request, CancellationToken ct) => new ValueTask<string>(""pong"");
}";

    private static IReadOnlyList<string> EmittedSources(string source)
    {
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        return result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .ToList();
    }

    // ── (a) No accessibility widening ─────────────────────────────────────────────────

    [Fact]
    public void GeneratedTypes_AreInternal_NotPublic_ForInternalMessageAndHandler()
    {
        var sources = EmittedSources(InternalCqsSource);
        var dispatcher = sources.Single(s => s.Contains("class RogueDispatcherImpl"));
        var registration = sources.Single(s => s.Contains("class RogueGeneratedRegistration"));

        // The generated container types are internal — the same accessibility a consumer's own
        // generated code carries. They are NOT promoted to public.
        Assert.Contains("internal sealed class RogueDispatcherImpl", dispatcher);
        Assert.DoesNotContain("public sealed class RogueDispatcherImpl", dispatcher);
        Assert.DoesNotContain("public class RogueDispatcherImpl", dispatcher);

        Assert.Contains("internal static class RogueGeneratedRegistration", registration);
        Assert.Contains("internal static void Register(", registration);
        // The registration entry point is internal — never a public-widened registrar.
        Assert.DoesNotContain("public static void Register(", registration);
        Assert.DoesNotContain("public static class RogueGeneratedRegistration", registration);
    }

    [Fact]
    public void Generator_DoesNotRedeclareUserTypes_NorChangeTheirModifiers()
    {
        // The generator references the user's types only via global::-qualified type usages; it never
        // re-declares `InternalPing`/`InternalPingHandler` (which is where a modifier change could
        // sneak in). Assert no generated file declares these types with any access modifier.
        var sources = EmittedSources(InternalCqsSource);
        foreach (var text in sources)
        {
            Assert.DoesNotContain("class InternalPing ", text);
            Assert.DoesNotContain("class InternalPing\r", text);
            Assert.DoesNotContain("class InternalPing\n", text);
            Assert.DoesNotContain("class InternalPing:", text);
            Assert.DoesNotContain("class InternalPingHandler", text);
        }
    }

    [Fact]
    public void GeneratedDispatcherType_IsNotPublic_WhenLoadedFromAssembly()
    {
        // Runtime confirmation: compile user source + generated sources to a real assembly, load it,
        // and assert the generated dispatcher type is internal (NotPublic) — i.e. not visible from a
        // peer assembly that lacks [InternalsVisibleTo].
        Assembly asm = GeneratorTestHelper.EmitAndLoadAssembly(InternalCqsSource);

        Type dispatcherType = asm.GetType("SkathIO.Rogue.Generated.RogueDispatcherImpl", throwOnError: true)!;
        Assert.True(dispatcherType.IsNotPublic, "Generated RogueDispatcherImpl must be internal (NotPublic).");
        Assert.False(dispatcherType.IsPublic, "Generated RogueDispatcherImpl must not be public.");

        Type registrationType = asm.GetType("SkathIO.Rogue.Generated.RogueGeneratedRegistration", throwOnError: true)!;
        Assert.True(registrationType.IsNotPublic, "Generated RogueGeneratedRegistration must be internal (NotPublic).");

        // The user's own internal message/handler stay internal — not widened to public.
        Type userMessage = asm.GetType("InternalPing", throwOnError: true)!;
        Assert.True(userMessage.IsNotPublic, "User's internal InternalPing must stay internal in the emitted assembly.");
        Type userHandler = asm.GetType("InternalPingHandler", throwOnError: true)!;
        Assert.True(userHandler.IsNotPublic, "User's internal InternalPingHandler must stay internal in the emitted assembly.");
    }

    [Fact]
    public void PeerAssemblyWithoutInternalsVisibleTo_CannotBindGeneratedDispatcher()
    {
        // Emit the consumer assembly (user source + generated sources) to disk-equivalent metadata,
        // then compile a SECOND, peer assembly that references it and tries to name the generated
        // internal dispatcher type. Because the consumer's [InternalsVisibleTo] names "FriendAsm"
        // (not this peer), the reference must fail to bind — proving no accessibility leak.
        MetadataReference consumerRef = EmitConsumerToMetadataReference(InternalCqsSource);

        const string peerSource = @"
class Peer
{
    static object M() => new global::SkathIO.Rogue.Generated.RogueDispatcherImpl(null!);
}";

        CSharpCompilation peer = CSharpCompilation.Create(
            assemblyName: "PeerAssembly_NoFriend",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(peerSource) },
            references: RuntimeReferencesPlus(consumerRef),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = peer.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        // The peer cannot see the internal type — Roslyn reports it as inaccessible/undefined
        // (CS0122 inaccessible, or CS0234/CS0246 not-found because the internal type is invisible).
        Assert.NotEmpty(errors);
        Assert.Contains(
            errors,
            e => e.Id is "CS0122" or "CS0234" or "CS0246" or "CS0103");
    }

    // ── (b) No internals exposure ─────────────────────────────────────────────────────

    [Fact]
    public void GeneratedCode_DoesNotEmit_InternalsVisibleTo()
    {
        // The only InternalsVisibleTo in the picture is the user's own declaration in source. The
        // generator must not emit one — that would expose internals beyond the user's intent.
        var sources = EmittedSources(InternalCqsSource);
        foreach (var text in sources)
        {
            Assert.DoesNotContain("InternalsVisibleTo", text);
        }
    }

    // ── (c) No reflection-emit / runtime codegen on the core path ──────────────────────

    [Theory]
    [InlineData("System.Reflection.Emit")]
    [InlineData("DynamicMethod")]
    [InlineData("ILGenerator")]
    [InlineData(".Compile()")]      // Expression<T>.Compile()
    [InlineData("Expression.Lambda")]
    [InlineData("MakeGenericMethod")]
    [InlineData("MakeGenericType")]
    [InlineData("Activator.CreateInstance")]
    [InlineData("RuntimeHelpers.GetUninitializedObject")]
    [InlineData("Assembly.GetTypes")]
    [InlineData("AppDomain.GetAssemblies")]
    public void GeneratedCode_ContainsNoRuntimeCodegenOrReflectionEmit(string forbiddenToken)
    {
        // Static source inspection of the emitted .cs strings: the core dispatch/registration path
        // must be pure compile-time codegen — no reflection-emit, no runtime IL, no expression
        // compilation, no late-bound reflection over types (NFR-12 / NFR-SEC-1 / AOT safety).
        var sources = EmittedSources(InternalCqsSource);
        foreach (var text in sources)
        {
            Assert.DoesNotContain(forbiddenToken, text);
        }
    }

    [Fact]
    public void GeneratedCode_UsesStaticSwitchDispatch_NotReflection()
    {
        // Positive confirmation that dispatch is a static type switch (the AOT-safe shape), not a
        // reflective lookup. The reshaped emitter routes ICommand<TResponse> through a `case ... r:`
        // switch on the concrete request type.
        var sources = EmittedSources(InternalCqsSource);
        var dispatcher = sources.Single(s => s.Contains("class RogueDispatcherImpl"));

        Assert.Contains("switch (command)", dispatcher);
        Assert.Contains("case global::InternalPing r:", dispatcher);
        // Dispatch is a static type switch, not a reflective lookup. (PipelineExecutor is no longer a
        // proxy for "static, not reflection": a behavior-free request takes the D4 bypass and never
        // references PipelineExecutor. The no-reflection claim is asserted directly here.)
        Assert.DoesNotContain("MakeGenericMethod", dispatcher);
        Assert.DoesNotContain("Activator.CreateInstance", dispatcher);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the generator over <paramref name="source"/> and emits the consumer (user source +
    /// generated sources) to an in-memory PE image, returned as a <see cref="MetadataReference"/> so
    /// a peer compilation can reference it.
    /// </summary>
    private static MetadataReference EmitConsumerToMetadataReference(string source)
    {
        var runResult = GeneratorTestHelper.RunGeneratorAndAssertClean(source);

        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        foreach (var genSource in runResult.Results.SelectMany(r => r.GeneratedSources))
        {
            trees.Add(CSharpSyntaxTree.ParseText(genSource.SourceText));
        }

        CSharpCompilation consumer = CSharpCompilation.Create(
            assemblyName: "ConsumerAssembly_WithInternalDispatcher",
            syntaxTrees: trees,
            references: RuntimeReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = consumer.Emit(ms);
        Assert.True(
            emit.Success,
            "Consumer emit failed:\n" + string.Join(
                "\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));

        ms.Seek(0, SeekOrigin.Begin);
        return MetadataReference.CreateFromImage(ms.ToArray());
    }

    private static MetadataReference[] RuntimeReferences()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.ComponentModel.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Diagnostics.DiagnosticSource.dll")),
            MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.ICommand<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.PipelineExecutor).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions).Assembly.Location),
        };
    }

    private static MetadataReference[] RuntimeReferencesPlus(MetadataReference extra)
        => RuntimeReferences().Append(extra).ToArray();
}
