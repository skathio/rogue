using System.Linq;
using System.Reflection;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// D5 (PD-2 / rogue-perf) RUNTIME tests for the statically-typed behavior chain. The sibling
/// emission tests assert the generated chain's <em>text</em>; these compile + load the generated
/// assembly and dispatch through the production <c>ISender.Send</c> path so they prove the chain
/// actually <em>runs</em> correctly — ordering, exception propagation, and the depth-&gt;8 fallback —
/// which a textual assertion cannot (wrong <c>next</c> wiring or a swallowed exception would still
/// compile). Spec deliverable 4 (phases/04, AC lines 86–88).
///
/// <para>
/// Each compilation deliberately contains NO open behavior of any family. An open non-stream
/// behavior would veto the chain (<c>HasUsableOpenBehavior(stream: false)</c>) and silently route
/// through <c>PipelineExecutor.Execute</c> instead — so every chain test also positively asserts the
/// generated source contains the request's <c>_Chain_N</c> method, proving the chain path (not the
/// fold fallback, which would produce the same observable result) is the one under test.
/// </para>
/// </summary>
[Collection(RealDiDispatchCollection.Name)] // shares the process-global registration bridge — see RealDiDispatchCollection
public sealed class ChainRuntimeTests
{
    // ── Ordering ───────────────────────────────────────────────────────────────────────

    private const string OrderingSource = @"
using SkathIO.Rogue;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Recorder
{
    public static readonly List<string> Steps = new List<string>();
}

public class PingRequest : ICommand<string> { }

public class PingHandler : ICommandHandler<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, CancellationToken ct)
    {
        Recorder.Steps.Add(""handler"");
        return ValueTask.FromResult(""pong"");
    }
}

// Two DISTINCT closed behaviors. Order is deterministic (PD-13a sort by Order then source then FQN);
// with no [PipelineOrder] both fall back to source order then ordinal FQN — B0 sorts before B1, so the
// generated Chain_2 is b0(B0) outermost, b1(B1) inner.
public sealed class B0 : IPipelineBehavior<PingRequest, string>
{
    public async ValueTask<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct)
    {
        Recorder.Steps.Add(""pre-B0"");
        var r = await next();
        Recorder.Steps.Add(""post-B0"");
        return r;
    }
}

public sealed class B1 : IPipelineBehavior<PingRequest, string>
{
    public async ValueTask<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct)
    {
        Recorder.Steps.Add(""pre-B1"");
        var r = await next();
        Recorder.Steps.Add(""post-B1"");
        return r;
    }
}";

    [Fact]
    public async System.Threading.Tasks.Task Chain_PreservesBehaviorOrder_OuterToInnerToHandlerAndBack()
    {
        // Positive proof the chain path is the one exercised (not the PipelineExecutor fallback): the
        // generated dispatcher must contain PingRequest's Chain_2 method.
        AssertChainEmitted(OrderingSource, "_Chain_2");

        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(OrderingSource);
        var provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var result = await DispatchCommand(assembly, provider, "PingRequest");

        Assert.Equal("pong", result);

        var steps = ReadRecorderSteps(assembly);
        Assert.Equal(
            new[] { "pre-B0", "pre-B1", "handler", "post-B1", "post-B0" },
            steps);
    }

    // ── Exception propagation ────────────────────────────────────────────────────────────

    private const string ThrowingBeforeNextSource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class BoomMarker
{
    public static bool HandlerCalled;
}

public class PingRequest : ICommand<string> { }

public class PingHandler : ICommandHandler<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, CancellationToken ct)
    {
        BoomMarker.HandlerCalled = true;
        return ValueTask.FromResult(""pong"");
    }
}

// Throws BEFORE calling next() — the handler must never run and the exception must propagate out of Send.
public sealed class ThrowingBehavior : IPipelineBehavior<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct)
        => throw new System.InvalidOperationException(""behavior boom"");
}";

    [Fact]
    public async System.Threading.Tasks.Task Chain_ExceptionFromBehaviorBeforeNext_Propagates_HandlerNotCalled()
    {
        // The throwing behavior is closed, so this request gets a single-link chain (Chain_1). The
        // behavior throws before invoking next(), so the exception must surface from Send and the handler
        // must never run.
        AssertChainEmitted(ThrowingBeforeNextSource, "_Chain_1");

        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(ThrowingBeforeNextSource);
        var provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => DispatchCommand(assembly, provider, "PingRequest"));
        Assert.Equal("behavior boom", ex.Message);

        var markerType = assembly.GetType("BoomMarker", throwOnError: true)!;
        var handlerCalled = (bool)markerType.GetField("HandlerCalled", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.False(handlerCalled);
    }

    // ── Stream-filtered open-behavior veto (Minor / pass 2) ──────────────────────────────

    private const string ClosedChainWithOpenStreamSource = @"
using SkathIO.Rogue;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class PingRequest : ICommand<string> { }

public class PingHandler : ICommandHandler<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, CancellationToken ct) => ValueTask.FromResult(""pong"");
}

// A CLOSED non-stream behavior — qualifies PingRequest for the static chain.
public sealed class ClosedPing : IPipelineBehavior<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}

// An OPEN STREAM behavior. Before the stream filter this vetoed the non-stream chain compilation-wide;
// it can never apply to PingRequest (a non-stream command), so it must NOT disable PingRequest's chain.
public sealed class OpenStreamBehavior<TReq, TRes> : IStreamPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public IAsyncEnumerable<TRes> Handle(TReq request, StreamHandlerDelegate<TRes> next, CancellationToken ct) => next();
}";

    [Fact]
    public async System.Threading.Tasks.Task Chain_SurvivesOpenStreamBehavior_NonStreamChainStillEmittedAndRuns()
    {
        // Minor fix (pass 2): HasUsableOpenBehavior is now stream-filtered. An open STREAM behavior in the
        // compilation no longer vetoes the non-stream D5 chain — PingRequest (closed behavior, no open
        // NON-stream behavior) must still get Chain_1 and dispatch through it correctly.
        AssertChainEmitted(ClosedChainWithOpenStreamSource, "_Chain_1");

        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(ClosedChainWithOpenStreamSource);
        var provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var result = await DispatchCommand(assembly, provider, "PingRequest");
        Assert.Equal("pong", result);
    }

    // ── Depth > MAX_STATIC_CHAIN_DEPTH fallback ──────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task Chain_DepthNine_FallsBackToPipelineExecutor_StillCorrect()
    {
        // Nine DISTINCT closed behaviors exceed MAX_STATIC_CHAIN_DEPTH = 8: the generator emits Chain_1..8
        // but NOT Chain_9, and the _WithBehaviors switch default routes count==9 through
        // PipelineExecutor.Execute. This asserts the runtime fallback path produces the correct result
        // (the no-Chain_9 emission is already covered by the textual max-depth test).
        string source = BuildNBehaviorSource(9);

        var dispatcherText = GeneratorTestHelper.RunGeneratorAndAssertClean(source)
            .Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueDispatcher.g.cs")
            .SourceText.ToString();
        Assert.Contains("_Chain_8(", dispatcherText);
        Assert.DoesNotContain("_Chain_9", dispatcherText);

        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(source);
        var provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var result = await DispatchCommand(assembly, provider, "PingRequest");
        Assert.Equal("pong", result);
    }

    /// <summary>Builds a source with PingRequest + <paramref name="n"/> distinct closed pass-through behaviors.</summary>
    private static string BuildNBehaviorSource(int n)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using SkathIO.Rogue;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("public class PingRequest : ICommand<string> { }");
        sb.AppendLine("public class PingHandler : ICommandHandler<PingRequest, string>");
        sb.AppendLine("{");
        sb.AppendLine("    public ValueTask<string> Handle(PingRequest request, CancellationToken ct) => ValueTask.FromResult(\"pong\");");
        sb.AppendLine("}");
        for (int i = 0; i < n; i++)
        {
            sb.AppendLine("public sealed class Pass" + i + " : IPipelineBehavior<PingRequest, string>");
            sb.AppendLine("{");
            sb.AppendLine("    public ValueTask<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the generated dispatcher contains <paramref name="chainFragment"/> (e.g. <c>_Chain_2</c>)
    /// for the request, proving the static chain is emitted (so the runtime assertions exercise the chain,
    /// not the PipelineExecutor fold fallback).
    /// </summary>
    private static void AssertChainEmitted(string source, string chainFragment)
    {
        var dispatcherText = GeneratorTestHelper.RunGeneratorAndAssertClean(source)
            .Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueDispatcher.g.cs")
            .SourceText.ToString();
        Assert.Contains(chainFragment, dispatcherText);
    }

    /// <summary>
    /// Resolves <c>ISender</c> from the built provider and dispatches a fresh instance of
    /// <paramref name="requestTypeName"/> (an <c>ICommand&lt;string&gt;</c>) through the typed
    /// <c>Send&lt;TResponse&gt;(ICommand&lt;TResponse&gt;, CancellationToken)</c> overload — the exact
    /// dispatch entry real consumers use for the behavior path, which routes ISender.Send → the typed
    /// override switch → Send_X → _WithBehaviors → the chain. Returns the unwrapped string result.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> DispatchCommand(
        Assembly assembly, System.IServiceProvider provider, string requestTypeName)
    {
        var sender = (SkathIO.Rogue.ISender)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService(provider, typeof(SkathIO.Rogue.ISender));

        var requestType = assembly.GetType(requestTypeName, throwOnError: true)!;
        var request = (SkathIO.Rogue.ICommand<string>)System.Activator.CreateInstance(requestType)!;

        // ISender.Send<TResponse>(ICommand<TResponse>, CancellationToken) — pick the generic command overload.
        var sendGeneric = typeof(SkathIO.Rogue.ISender).GetMethods()
            .First(m => m.Name == "Send"
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType.IsGenericType
                && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(SkathIO.Rogue.ICommand<>))
            .MakeGenericMethod(typeof(string));

        object valueTask;
        try
        {
            valueTask = sendGeneric.Invoke(
                sender, new object?[] { request, System.Threading.CancellationToken.None })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Send_X and its chain methods are synchronous; a behavior that throws BEFORE next() throws
            // synchronously out of the chain (not as a faulted ValueTask), so Invoke surfaces it wrapped.
            // Unwrap so callers see the real exception regardless of the sync/async throw point.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }

        // Unwrap the returned ValueTask<string> via AsTask(), surfacing any async-thrown exception to the awaiter.
        var asTask = (System.Threading.Tasks.Task<string>)valueTask.GetType()
            .GetMethod("AsTask")!.Invoke(valueTask, null)!;
        return await asTask;
    }

    /// <summary>Reads back the <c>Recorder.Steps</c> static list from the loaded assembly.</summary>
    private static string[] ReadRecorderSteps(Assembly assembly)
    {
        var recorderType = assembly.GetType("Recorder", throwOnError: true)!;
        var steps = (System.Collections.Generic.List<string>)recorderType
            .GetField("Steps", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        return steps.ToArray();
    }
}
