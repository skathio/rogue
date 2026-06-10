using System.Collections.Generic;
using System.Text;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Emits the generated sealed subclass <c>RogueDispatcherImpl</c> in the
/// <c>SkathIO.Rogue.Generated</c> namespace. It derives from
/// <c>global::SkathIO.Rogue.RogueDispatcher</c> (defined in the runtime DLL) and overrides the
/// virtual dispatch methods. A subclass — rather than a <c>partial</c> — is required because a
/// <c>partial class</c> cannot span the assembly boundary between the runtime DLL and the
/// consumer's compilation (PD-14). Output: <c>RogueDispatcher.g.cs</c>.
/// </summary>
internal static class DispatcherEmitter
{
    // DI shorthand constants
    private const string SP  = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";
    private const string SVC = "_serviceProvider";

    // FR-45 / PD-30: telemetry shim FQN. StartDispatch returns DispatchScope? (null when telemetry
    // is disabled or unsubscribed — the zero-overhead path that preserves PD-31's 0-alloc guarantee).
    private const string RT = "global::SkathIO.Rogue.RogueTelemetry";

    internal static string Emit(DiscoveredModels models, RogueEmitOptions opts)
    {
        var w = new CodeWriter();

        // File header is written once by the caller (RogueGenerator), not here.
        // We write the source body only.
        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();

        w.Open("internal sealed class RogueDispatcherImpl : global::SkathIO.Rogue.RogueDispatcher");

        // ── Constructor ───────────────────────────────────────────────────────────────
        // Passes the service provider to the base, which stores it in the protected
        // _serviceProvider field that the override methods below read.
        w.Line("public RogueDispatcherImpl(global::System.IServiceProvider serviceProvider)");
        w.Line("    : base(serviceProvider) { }");
        w.Line();

        // ── void-path async helper ────────────────────────────────────────────────────
        // Used when IRequestHandler<TReq> returns bare ValueTask on net8+ and we need ValueTask<Unit>.
        w.Line("#if !NETSTANDARD2_0");
        w.Open("private static async global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> AwaitVoidThenUnit(global::System.Threading.Tasks.ValueTask vt)");
        w.Line("await vt.ConfigureAwait(false);");
        w.Line("return global::SkathIO.Rogue.Unit.Value;");
        w.Close(); // AwaitVoidThenUnit
        w.Line("#endif");
        w.Line();

        // ── BoxAsync helper for SendObject ────────────────────────────────────────────
        w.Open("private static async global::System.Threading.Tasks.ValueTask<object?> BoxAsync<T>(global::System.Threading.Tasks.ValueTask<T> vt)");
        w.Line("return await vt.ConfigureAwait(false);");
        w.Close(); // BoxAsync
        w.Line();

        // ── Per-request Send methods ──────────────────────────────────────────────────
        foreach (var handler in models.Handlers)
        {
            EmitSendMethod(w, handler, models.Processors);
            w.Line();
        }

        // ── Send<TResponse> override (ISender dispatch switch) ────────────────────────
        EmitSendOverride(w, models.Handlers);
        w.Line();

        // ── SendObject override ───────────────────────────────────────────────────────
        EmitSendObjectOverride(w, models.Handlers);
        w.Line();

        // ── Streaming dispatch (net8+ only) ───────────────────────────────────────────
        w.Line("#if !NETSTANDARD2_0");
        foreach (var sh in models.StreamHandlers)
        {
            EmitCreateStreamMethod(w, sh);
            w.Line();
        }
        EmitCreateStreamOverride(w, models.StreamHandlers);
        w.Line("#endif");
        w.Line();

        // ── Per-notification Publish helper methods ───────────────────────────────────
        // Build: notifFqn → list of handler FQNs
        var handlersByNotif = new Dictionary<string, List<string>>();
        foreach (var nh in models.NotificationHandlers)
        {
            if (!handlersByNotif.TryGetValue(nh.NotificationFqn, out var list))
            {
                list = new List<string>();
                handlersByNotif[nh.NotificationFqn] = list;
            }
            list.Add(nh.TypeFqn);
        }

        foreach (var kvp in handlersByNotif)
        {
            EmitPublishNotifMethod(w, kvp.Key, kvp.Value);
            w.Line();
        }

        // ── Publish override ──────────────────────────────────────────────────────────
        EmitPublishOverride(w, handlersByNotif);

        w.Close(); // class RogueDispatcherImpl

        w.Dedent();
        w.Line("}"); // namespace SkathIO.Rogue.Generated

        return w.ToString();
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Per-request Send_XXX method
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitSendMethod(CodeWriter w, HandlerModel handler, EquatableArray<ProcessorModel> processors)
    {
        bool isVoid = handler.ResponseFqn is null;
        string responseFqn = isVoid ? "global::SkathIO.Rogue.Unit" : ToGlobalFqn(handler.ResponseFqn!);
        string requestFqn  = ToGlobalFqn(handler.RequestFqn);

        string handlerIface = isVoid
            ? "global::SkathIO.Rogue.IRequestHandler<" + requestFqn + ">"
            : "global::SkathIO.Rogue.IRequestHandler<" + requestFqn + ", " + responseFqn + ">";

        string behaviorIface    = "global::SkathIO.Rogue.IPipelineBehavior<" + requestFqn + ", " + responseFqn + ">";
        string behaviorListType = "global::System.Collections.Generic.IReadOnlyList<" + behaviorIface + ">";
        string methodName       = "Send_" + MakeSafeName(handler.RequestFqn);

        // FR-25/26/27: collect the pre/post processors and exception handlers/actions discovered
        // for THIS request (matched by request FQN, and response FQN for the response-typed kinds).
        // The match uses the model FQNs (no global:: prefix) — the same form RogueGenerator records.
        var pre        = CollectProcessors(processors, handler.RequestFqn, handler.ResponseFqn, ProcessorKind.Pre,              matchResponse: false);
        var post       = CollectProcessors(processors, handler.RequestFqn, handler.ResponseFqn, ProcessorKind.Post,             matchResponse: true);
        var exHandlers = CollectProcessors(processors, handler.RequestFqn, handler.ResponseFqn, ProcessorKind.ExceptionHandler, matchResponse: true);
        var exActions  = CollectProcessors(processors, handler.RequestFqn, handler.ResponseFqn, ProcessorKind.ExceptionAction,  matchResponse: false);

        bool hasProcessors = pre.Count > 0 || post.Count > 0 || exHandlers.Count > 0 || exActions.Count > 0;

        w.Open(
            "private global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "(" + requestFqn + " request, global::System.Threading.CancellationToken cancellationToken)");

        // Resolve handler
        w.Line("var handler = " + SP + ".GetRequiredService<" + handlerIface + ">(" + SVC + ");");

        // Resolve behavior list (IReadOnlyList registered by RegistrationEmitter, or Array.Empty)
        w.Line("var behaviors = " + SP + ".GetService<" + behaviorListType + ">(" + SVC + ")");
        w.Line("    ?? ((" + behaviorListType + ")global::System.Array.Empty<" + behaviorIface + ">());");

        if (!hasProcessors)
        {
            // FAST PATH — no pre/post processors, no exception handlers/actions for this request type.
            //
            // PD-31 (AC-C / NFR-PERF-1 closure elimination): when this request *also* has no behaviors
            // (the common case, and the path the "0 bytes" claim is actually about), bypass
            // RequestHandlerDelegate construction and PipelineExecutor.Execute entirely — call the
            // handler directly.
            //
            // Critically, the `() => handler.Handle(...)` lambda must NOT appear inline in this method
            // at all — not even in an `else`/has-behaviors branch. `handler`, `request`, and
            // `cancellationToken` are used by BOTH the fast-path direct return AND the lambda, so if the
            // lambda were declared here, Roslyn would hoist the display-class allocation to the point
            // those locals are first assigned (i.e. to the top of THIS method, before the
            // `behaviors.Count == 0` check) — allocating the closure on every dispatch regardless of
            // which branch runs. Delegating the has-behaviors case to a separate static method
            // (`{methodName}_WithBehaviors`, taking `handler`/`request`/`behaviors`/`cancellationToken`
            // as parameters) confines the lambda's captured-variable scope to that method, so the
            // closure allocates only when behaviors are actually present — never on the fast path.
            //
            // Behavior presence is a RUNTIME fact (open behaviors discovered by the PD-17 metadata scan
            // apply to all requests and are resolved from DI), so the bypass is a runtime branch on
            // `behaviors.Count == 0`.
            // FR-45 / PD-30: begin a dispatch scope. When telemetry is disabled or unsubscribed,
            // StartDispatch returns null and we take the existing fast path verbatim — no Activity,
            // no allocation, PD-31's 0-byte guarantee fully preserved.
            w.Line("var __scope = " + RT + ".StartDispatch<" + requestFqn + ">();");
            w.Open("if (__scope is null)");

            w.Open("if (behaviors.Count == 0)");
            EmitDirectHandlerReturn(w, isVoid);
            w.Close(); // if (behaviors.Count == 0)
            w.Line();

            w.Line("return " + methodName + "_WithBehaviors(handler, request, behaviors, cancellationToken);");
            w.Close(); // if (__scope is null)
            w.Line();

            // Telemetry-on path: delegate to the async companion, which owns the try/finally that
            // observes the dispatch outcome and stops the scope.
            w.Line("return " + methodName + "_WithTelemetry(handler, request, behaviors, cancellationToken, __scope.Value);");
        }
        else
        {
            // PROCESSOR PATH (FR-25/26/27): pre-processors → behavior pipeline (the SAME engine) →
            // post-processors, all inside a try/catch that runs exception actions (observe-only) and
            // exception handlers (may supply a fallback response). The matching of a thrown exception
            // to a registered handler/action is emitted as a statically-typed `is TEx` chain — no
            // reflection (NFR-SEC-1), AOT-safe, and the exact TEx types are known at generate time.
            //
            // The processor wrap calls the handler from inside a try/fold the call site can't inline
            // away, so it genuinely needs the deferred RequestHandlerDelegate (built here, unaffected
            // by the PD-31 fast-path bypass above). Every dispatch to a processor-bearing request goes
            // through this wrap regardless of behavior count, so there is no fast path here to protect
            // from the closure — the allocation is an intrinsic, by-design cost of FR-25/26/27.
            EmitHandlerCallDelegate(w, isVoid, responseFqn);
            EmitProcessorPath(w, requestFqn, responseFqn, "handlerCall", pre, post, exHandlers, exActions);
        }

        w.Close(); // method

        if (!hasProcessors)
        {
            w.Line();
            EmitSendWithBehaviorsMethod(w, methodName, handlerIface, requestFqn, responseFqn, behaviorListType, isVoid);
            w.Line();
            EmitSendWithTelemetryMethod(w, methodName, handlerIface, requestFqn, responseFqn, behaviorListType);
        }
    }

    /// <summary>
    /// FR-45 / PD-30: the telemetry-on continuation of the no-processor path. Reached only when
    /// <c>StartDispatch</c> returned a non-null scope (telemetry enabled AND subscribed), so this
    /// path is already allocating an async state machine — the <c>RequestHandlerDelegate</c> closure
    /// inside <c>_WithBehaviors</c> (which itself handles the empty-behavior case via
    /// <c>PipelineExecutor.Execute</c>) is no additional concern here. The <c>try/catch/finally</c>
    /// observes the outcome and stops the scope, capturing any exception for the span status.
    /// </summary>
    private static void EmitSendWithTelemetryMethod(
        CodeWriter w, string methodName, string handlerIface, string requestFqn, string responseFqn,
        string behaviorListType)
    {
        w.Open(
            "private static async global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "_WithTelemetry(" +
            handlerIface + " handler, " + requestFqn + " request, " + behaviorListType + " behaviors, " +
            "global::System.Threading.CancellationToken cancellationToken, " +
            "global::SkathIO.Rogue.DispatchScope scope)");

        w.Line("global::System.Exception? __exc = null;");
        w.Open("try");
        w.Line("return await " + methodName + "_WithBehaviors(handler, request, behaviors, cancellationToken).ConfigureAwait(false);");
        w.Close(); // try
        w.Open("catch (global::System.Exception __ex)");
        w.Line("__exc = __ex;");
        w.Line("throw;");
        w.Close(); // catch
        w.Open("finally");
        w.Line(RT + ".StopDispatch(scope, __exc);");
        w.Close(); // finally

        w.Close(); // method
    }

    /// <summary>
    /// PD-31 (AC-C / NFR-PERF-1 closure elimination): the has-behaviors continuation of the
    /// no-processor fast path, factored into its own <c>private static</c> method so that the
    /// <c>() =&gt; handler.Handle(...)</c> closure's captured variables (<c>handler</c>,
    /// <c>request</c>, <c>cancellationToken</c>) are PARAMETERS here — not locals shared with
    /// {methodName}'s fast-path branch. See the comment in <see cref="EmitSendMethod"/> for why
    /// sharing those locals would hoist the display-class allocation onto every dispatch.
    /// </summary>
    private static void EmitSendWithBehaviorsMethod(
        CodeWriter w, string methodName, string handlerIface, string requestFqn, string responseFqn,
        string behaviorListType, bool isVoid)
    {
        w.Open(
            "private static global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "_WithBehaviors(" +
            handlerIface + " handler, " + requestFqn + " request, " + behaviorListType + " behaviors, " +
            "global::System.Threading.CancellationToken cancellationToken)");

        EmitHandlerCallDelegate(w, isVoid, responseFqn);
        w.Line("return global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", " + responseFqn + ">(");
        w.Line("    request, behaviors, handlerCall, cancellationToken);");

        w.Close(); // method
    }

    /// <summary>
    /// PD-31 (AC-C / NFR-PERF-1 closure elimination): emits a direct, non-deferred
    /// <c>return handler.Handle(...)</c> for the no-behavior fast path — no
    /// <see cref="global::SkathIO.Rogue.RequestHandlerDelegate{TResponse}"/> is constructed, so no
    /// per-dispatch display-class closure is allocated. Mirrors the void-wrapping shape of
    /// <see cref="EmitHandlerCallDelegate"/> exactly (same TFM split, same <c>AwaitVoidThenUnit</c>
    /// fast-completion check) — only the *shape* differs (an immediate <c>return</c> vs. a deferred
    /// delegate assignment).
    /// </summary>
    private static void EmitDirectHandlerReturn(CodeWriter w, bool isVoid)
    {
        if (isVoid)
        {
            // IRequestHandler<TReq>.Handle returns ValueTask<Unit> on netstandard2.0 (no bare
            // ValueTask-returning handler interface there) and bare ValueTask on net8+.
            w.Line("#if NETSTANDARD2_0");
            w.Line("return handler.Handle(request, cancellationToken);");
            w.Line("#else");
            w.Line("var voidVt = handler.Handle(request, cancellationToken);");
            w.Line("if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
            w.Line("return AwaitVoidThenUnit(voidVt);");
            w.Line("#endif");
        }
        else
        {
            w.Line("return handler.Handle(request, cancellationToken);");
        }
    }

    /// <summary>
    /// Emits a <c>global::SkathIO.Rogue.RequestHandlerDelegate&lt;TResponse&gt; handlerCall = ...</c>
    /// declaration that defers the handler invocation — required wherever the call must be threaded
    /// through <see cref="global::SkathIO.Rogue.PipelineExecutor"/> (the has-behaviors path) or the
    /// FR-25/26/27 processor wrap (<see cref="EmitProcessorPath"/>), both of which need an invokable,
    /// deferred reference rather than an immediate result. This is the allocating shape PD-31
    /// deliberately keeps — and keeps confined to — the paths that genuinely need deferral; the
    /// no-behavior, no-processor fast path bypasses it entirely via
    /// <see cref="EmitDirectHandlerReturn"/>.
    /// </summary>
    private static void EmitHandlerCallDelegate(CodeWriter w, bool isVoid, string responseFqn)
    {
        if (isVoid)
        {
            w.Line("#if NETSTANDARD2_0");
            w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> handlerCall = () => handler.Handle(request, cancellationToken);");
            w.Line("#else");
            w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> handlerCall = () =>");
            w.Line("{");
            w.Line("    var voidVt = handler.Handle(request, cancellationToken);");
            w.Line("    if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
            w.Line("    return AwaitVoidThenUnit(voidVt);");
            w.Line("};");
            w.Line("#endif");
        }
        else
        {
            w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> handlerCall = () => handler.Handle(request, cancellationToken);");
        }
    }

    /// <summary>
    /// Emits the FR-25/26/27 processor-aware dispatch body for a single request type. The method
    /// is declared as a returning expression by the caller; this emits an inlined async local
    /// function so the whole body can <c>await</c> processors without changing the method signature.
    /// </summary>
    private static void EmitProcessorPath(
        CodeWriter w,
        string requestFqn,
        string responseFqn,
        string handlerCall,
        List<ProcessorModel> pre,
        List<ProcessorModel> post,
        List<ProcessorModel> exHandlers,
        List<ProcessorModel> exActions)
    {
        string preIface  = "global::SkathIO.Rogue.IRequestPreProcessor<" + requestFqn + ">";
        string postIface = "global::SkathIO.Rogue.IRequestPostProcessor<" + requestFqn + ", " + responseFqn + ">";

        // Resolve the processor sets via DI (registered by RegistrationEmitter). GetServices returns
        // every registered instance in registration order — FR-25 deterministic order.
        if (pre.Count > 0)
            w.Line("var preProcessors = " + SP + ".GetServices<" + preIface + ">(" + SVC + ");");
        if (post.Count > 0)
            w.Line("var postProcessors = " + SP + ".GetServices<" + postIface + ">(" + SVC + ");");

        // Inlined async local function: returns the ValueTask<TResponse> the method hands back. Using a
        // local function (rather than making Send_X itself async) keeps the fast-path methods cheap and
        // localizes the state machine to request types that actually have processors.
        w.Open("async global::System.Threading.Tasks.ValueTask<" + responseFqn + "> RunWithProcessors()");

        // FR-25: pre-processors run before the behavior pipeline, in deterministic (registration) order.
        if (pre.Count > 0)
        {
            // IRequestPreProcessor.Process returns ValueTask<Unit> on ns2.0 and ValueTask on net8+;
            // both are awaitable identically, so no #if split is needed here.
            w.Open("foreach (var pre in preProcessors)");
            w.Line("await pre.Process(request, cancellationToken).ConfigureAwait(false);");
            w.Close(); // foreach
        }

        string resultExpr =
            "await global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", " + responseFqn + ">(" +
            "request, behaviors, " + handlerCall + ", cancellationToken).ConfigureAwait(false)";

        bool hasCatch = exHandlers.Count > 0 || exActions.Count > 0;

        // `response` must be declared at the RunWithProcessors scope (not inside the try) whenever a
        // try/catch wraps the pipeline call, because the `return response;` below sits outside the
        // try. This holds even when nothing inside the catch reads `response` (exception-action-only
        // registration): without the function-scoped declaration the emitted `return response;` would
        // reference an undeclared local (CS0103). The fast path (no processors at all) is emitted by
        // the caller and never reaches here. (Defect #1, review 2026-06-07.)
        w.Line(responseFqn + " response;");

        if (hasCatch)
        {
            w.Open("try");
            // FR-27: the behavior pipeline runs through the SAME PipelineExecutor engine inside the
            // processor wrapper — there is no second/competing pipeline.
            w.Line("response = " + resultExpr + ";");

            // FR-25: post-processors run after a successful handler/pipeline completion.
            EmitPostProcessors(w, post);

            w.Close(); // try

            // FR-26: exception actions observe (do not suppress); exception handlers may supply a
            // fallback response and suppress propagation. Both are matched by a statically-typed
            // `is TEx` chain — no reflection.
            EmitExceptionCatch(w, requestFqn, responseFqn, exHandlers, exActions);
        }
        else
        {
            // No exception handling for this request type — just pipeline + post-processors.
            w.Line("response = " + resultExpr + ";");

            EmitPostProcessors(w, post);
        }

        w.Line("return response;");
        w.Close(); // RunWithProcessors local function

        // FR-45 / PD-30: wrap the whole processor dispatch (pre-processors → pipeline → post) in a
        // telemetry scope. Disabled/unsubscribed → StartDispatch returns null and we hand back the
        // RunWithProcessors() ValueTask unwrapped (no extra state machine). Enabled → a second async
        // local function owns the try/catch/finally that observes the outcome.
        w.Line("var __scope = " + RT + ".StartDispatch<" + requestFqn + ">();");
        w.Open("if (__scope is null)");
        w.Line("return RunWithProcessors();");
        w.Close(); // if (__scope is null)
        w.Open("async global::System.Threading.Tasks.ValueTask<" + responseFqn + "> RunWithTelemetry(global::SkathIO.Rogue.DispatchScope scope)");
        w.Line("global::System.Exception? __exc = null;");
        w.Open("try");
        w.Line("return await RunWithProcessors().ConfigureAwait(false);");
        w.Close(); // try
        w.Open("catch (global::System.Exception __ex)");
        w.Line("__exc = __ex;");
        w.Line("throw;");
        w.Close(); // catch
        w.Open("finally");
        w.Line(RT + ".StopDispatch(scope, __exc);");
        w.Close(); // finally
        w.Close(); // RunWithTelemetry local function
        w.Line("return RunWithTelemetry(__scope.Value);");
    }

    private static void EmitPostProcessors(CodeWriter w, List<ProcessorModel> post)
    {
        if (post.Count == 0) return;
        w.Open("foreach (var postProc in postProcessors)");
        w.Line("await postProc.Process(request, response, cancellationToken).ConfigureAwait(false);");
        w.Close(); // foreach
    }

    /// <summary>
    /// Emits the catch block(s) for FR-26. Exception actions (observe-only) run first, then exception
    /// handlers, both matched against the thrown exception via a statically-typed <c>is TEx</c> test
    /// (no reflection — NFR-SEC-1). When a handler marks the exception handled, its fallback response
    /// is returned via the enclosing <c>RunWithProcessors</c> local function; otherwise the original
    /// exception is re-thrown preserving its stack trace.
    /// </summary>
    private static void EmitExceptionCatch(
        CodeWriter w,
        string requestFqn,
        string responseFqn,
        List<ProcessorModel> exHandlers,
        List<ProcessorModel> exActions)
    {
        w.Open("catch (global::System.Exception ex)");

        // FR-26 (Should): observe-only actions. They run regardless of whether a handler suppresses.
        //
        // Actions are grouped by their exception type (TEx) so each distinct TEx produces EXACTLY ONE
        // `if (ex is TEx ...)` block. GetServices<IRequestExceptionAction<TReq,TEx>> already resolves
        // EVERY registered implementation for that closed interface, so one block + one GetServices
        // call covers the full set for that exception type. Emitting one block per *implementation*
        // (the previous shape) made every action fire once per registered implementation sharing the
        // same TEx — N² invocations for N actions on one exception type. (Defect #2, review 2026-06-07.)
        var actionExFqns = DistinctExceptionFqns(exActions);
        for (int i = 0; i < actionExFqns.Count; i++)
        {
            string exFqn = ToGlobalFqn(actionExFqns[i]);
            string iface = "global::SkathIO.Rogue.IRequestExceptionAction<" + requestFqn + ", " + exFqn + ">";
            // Index-based suffix (not MakeSafeName(exFqn)): MakeSafeName is a non-injective character
            // substitution — two distinct exception FQNs could collide on the same safe name and emit
            // duplicate locals (CS0128). The loop index is unique by construction.
            string sfx   = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            w.Open("if (ex is " + exFqn + " exForAction" + sfx + ")");
            w.Line("var actions" + sfx + " = " + SP + ".GetServices<" + iface + ">(" + SVC + ");");
            w.Open("foreach (var action in actions" + sfx + ")");
            w.Line("await action.Execute(request, exForAction" + sfx + ", cancellationToken).ConfigureAwait(false);");
            w.Close(); // foreach
            w.Close(); // if
        }

        // FR-26: exception handlers. Grouped by exception type for the same reason as actions: one
        // `is TEx` block per distinct TEx, one GetServices call resolving the full handler set for
        // that type. The first handler that marks the state handled supplies the fallback response and
        // short-circuits the rest (within and across exception-type blocks via the !Handled guard).
        var handlerExFqns = DistinctExceptionFqns(exHandlers);
        if (handlerExFqns.Count > 0)
        {
            w.Line("var exState = new global::SkathIO.Rogue.RequestExceptionHandlerState<" + responseFqn + ">();");
            for (int i = 0; i < handlerExFqns.Count; i++)
            {
                string exFqn = ToGlobalFqn(handlerExFqns[i]);
                string iface = "global::SkathIO.Rogue.IRequestExceptionHandler<" + requestFqn + ", " + responseFqn + ", " + exFqn + ">";
                // Index-based suffix — see the actions loop above for why MakeSafeName(exFqn) is unsafe here.
                string sfx   = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                w.Open("if (!exState.Handled && ex is " + exFqn + " exForHandler" + sfx + ")");
                w.Line("var handlers" + sfx + " = " + SP + ".GetServices<" + iface + ">(" + SVC + ");");
                w.Open("foreach (var exHandler in handlers" + sfx + ")");
                w.Line("await exHandler.Handle(request, exForHandler" + sfx + ", exState, cancellationToken).ConfigureAwait(false);");
                w.Line("if (exState.Handled) break;");
                w.Close(); // foreach
                w.Close(); // if
            }
            w.Open("if (exState.Handled)");
            w.Line("return exState.Response!;");
            w.Close(); // if
        }

        // Not handled — preserve the original exception (and its stack) unchanged.
        w.Line("throw;");
        w.Close(); // catch
    }

    /// <summary>
    /// Collects the processor models of <paramref name="kind"/> that apply to a request identified by
    /// <paramref name="requestFqn"/> (and <paramref name="responseFqn"/> for response-typed kinds).
    /// Matching uses the model FQNs (no <c>global::</c> prefix) recorded by <c>RogueGenerator</c>.
    /// A void-path handler's response FQN is null and matches a processor recorded against
    /// <c>SkathIO.Rogue.Unit</c>.
    /// </summary>
    private static List<ProcessorModel> CollectProcessors(
        EquatableArray<ProcessorModel> processors,
        string requestFqn,
        string? handlerResponseFqn,
        ProcessorKind kind,
        bool matchResponse)
    {
        // A void handler's response is Unit; processors record the Unit FQN as their response.
        string expectedResponse = handlerResponseFqn ?? "SkathIO.Rogue.Unit";

        var result = new List<ProcessorModel>();
        foreach (var p in processors)
        {
            if (p.Kind != kind) continue;
            if (!string.Equals(p.RequestFqn, requestFqn, System.StringComparison.Ordinal)) continue;
            if (matchResponse)
            {
                string pResponse = p.ResponseFqn ?? "SkathIO.Rogue.Unit";
                if (!string.Equals(pResponse, expectedResponse, System.StringComparison.Ordinal)) continue;
            }
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Returns the distinct, registration-order-preserving set of <c>ExceptionFqn</c> values across
    /// the given exception-handler/action models. Each distinct exception type is emitted as exactly
    /// one <c>is TEx</c> block (the single <c>GetServices&lt;...&gt;</c> call inside resolves the full
    /// implementation set for that closed interface), so two implementations sharing one exception
    /// type do not produce duplicate blocks / duplicate invocation. (Defect #2, review 2026-06-07.)
    /// </summary>
    private static List<string> DistinctExceptionFqns(List<ProcessorModel> models)
    {
        var seen   = new HashSet<string>(System.StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var m in models)
        {
            if (m.ExceptionFqn is null) continue;
            if (seen.Add(m.ExceptionFqn))
                result.Add(m.ExceptionFqn);
        }
        return result;
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Send<TResponse> override
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitSendOverride(CodeWriter w, EquatableArray<HandlerModel> handlers)
    {
        w.Open(
            "public override global::System.Threading.Tasks.ValueTask<TResponse> Send<TResponse>(" +
            "global::SkathIO.Rogue.IRequest<TResponse> request, " +
            "global::System.Threading.CancellationToken cancellationToken = default)");

        if (handlers.Count == 0)
        {
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(request.GetType());");
        }
        else
        {
            w.Open("switch (request)");

            foreach (var handler in handlers)
            {
                bool isVoid    = handler.ResponseFqn is null;
                string reqFqn  = ToGlobalFqn(handler.RequestFqn);
                string resFqn  = isVoid ? "global::SkathIO.Rogue.Unit" : ToGlobalFqn(handler.ResponseFqn!);
                string method  = "Send_" + MakeSafeName(handler.RequestFqn);

                w.Line("case " + reqFqn + " r:");
                w.Indent();
                w.Line("return (global::System.Threading.Tasks.ValueTask<TResponse>)(object)" + method + "(r, cancellationToken);");
                w.Dedent();
            }

            w.Line("default:");
            w.Indent();
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(request.GetType());");
            w.Dedent();

            w.Close(); // switch
        }

        w.Close(); // method
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // SendObject override
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitSendObjectOverride(CodeWriter w, EquatableArray<HandlerModel> handlers)
    {
        w.Open(
            "public override global::System.Threading.Tasks.ValueTask<object?> SendObject(" +
            "object request, global::System.Threading.CancellationToken cancellationToken = default)");

        // The object-dispatch switch is always emitted (PD-3). The runtime
        // RogueOptions.EnableObjectDispatch flag is not consulted at emit time in Phase 4.1 —
        // the switch is always present and unreferenced object types throw at dispatch time.
        // A runtime gate (trim/throw when EnableObjectDispatch is false) can be re-added in
        // Phase 8 packaging when the linker substitution is wired up. (Fix 2 / PD-3)
        if (handlers.Count == 0)
        {
            // No handlers in the compilation — nothing to dispatch to.
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(request.GetType());");
        }
        else
        {
            w.Open("switch (request)");

            foreach (var handler in handlers)
            {
                bool isVoid   = handler.ResponseFqn is null;
                string reqFqn = ToGlobalFqn(handler.RequestFqn);
                string resFqn = isVoid ? "global::SkathIO.Rogue.Unit" : ToGlobalFqn(handler.ResponseFqn!);
                string method = "Send_" + MakeSafeName(handler.RequestFqn);

                w.Line("case " + reqFqn + " r:");
                w.Indent();
                w.Line("return BoxAsync<" + resFqn + ">(" + method + "(r, cancellationToken));");
                w.Dedent();
            }

            w.Line("default:");
            w.Indent();
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(request.GetType());");
            w.Dedent();

            w.Close(); // switch
        }

        w.Close(); // method
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Streaming dispatch (net8+ only — IAsyncEnumerable)
    //
    // Fix 5 / FR-23: emits CreateStream<TResponse> as a switch over discovered
    // IStreamRequestHandler<,> implementations, each delegating to a per-request helper that
    // resolves the handler and folds any registered IStreamPipelineBehavior<,> around it.
    //
    // The fold is CLOSURE-based, not the struct-index pattern used by PipelineExecutor: a ref
    // struct cannot survive a `yield return` suspension point inside an async iterator, so the
    // value-task fast path's zero-alloc trick is not available here. Each behavior depth captures
    // one StreamHandlerDelegate<T> closure — one heap allocation per behavior depth, which is
    // acceptable on the streaming path (streaming inherently allocates enumerator state machines;
    // the 0-alloc guarantee covers only the typed Send value-task path).
    //
    // IStreamPipelineBehavior<,>.Handle returns IAsyncEnumerable<T> directly, so the fold only
    // composes delegates — the behaviors themselves perform `await foreach` / `yield return`. The
    // generated method needs no iterator machinery of its own.
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitCreateStreamMethod(CodeWriter w, StreamHandlerModel sh)
    {
        string requestFqn  = ToGlobalFqn(sh.RequestFqn);
        string elementFqn  = ToGlobalFqn(sh.ResponseElementFqn);
        string handlerIface = "global::SkathIO.Rogue.IStreamRequestHandler<" + requestFqn + ", " + elementFqn + ">";
        string behaviorIface = "global::SkathIO.Rogue.IStreamPipelineBehavior<" + requestFqn + ", " + elementFqn + ">";
        string behaviorListType = "global::System.Collections.Generic.IReadOnlyList<" + behaviorIface + ">";
        string nextDelegate = "global::SkathIO.Rogue.StreamHandlerDelegate<" + elementFqn + ">";
        string methodName  = "CreateStream_" + MakeSafeName(sh.RequestFqn);

        w.Open(
            "private global::System.Collections.Generic.IAsyncEnumerable<" + elementFqn + "> " +
            methodName + "(" + requestFqn + " request, global::System.Threading.CancellationToken cancellationToken)");

        w.Line("var handler = " + SP + ".GetRequiredService<" + handlerIface + ">(" + SVC + ");");

        // Resolve the stream-behavior list (registered by RegistrationEmitter, or Array.Empty).
        w.Line("var behaviors = " + SP + ".GetService<" + behaviorListType + ">(" + SVC + ")");
        w.Line("    ?? ((" + behaviorListType + ")global::System.Array.Empty<" + behaviorIface + ">());");

        // Innermost producer: the handler itself.
        w.Line(nextDelegate + " next = () => handler.Handle(request, cancellationToken);");

        // Fold behaviors from innermost (last) to outermost (first) so behaviors[0] runs first.
        // Each iteration captures the current `next` and the resolved behavior into fresh locals so
        // the closures do not all alias the loop's final values.
        w.Open("for (int i = behaviors.Count - 1; i >= 0; i--)");
        w.Line("var behavior = behaviors[i];");
        w.Line(nextDelegate + " prevNext = next;");
        w.Line("next = () => behavior.Handle(request, prevNext, cancellationToken);");
        w.Close(); // for

        // FR-45 / PD-30: scope the dispatch (DI-resolution + behavior-fold + the next() call that
        // produces the stream). IAsyncEnumerable is lazy, so this scopes the dispatch phase, not the
        // streaming iteration (a v1 choice — see phases.md 9.2). Disabled/unsubscribed →
        // StartDispatch returns null and we return the stream unwrapped.
        w.Line("var __scope = " + RT + ".StartDispatch<" + requestFqn + ">();");
        w.Open("if (__scope is null)");
        w.Line("return next();");
        w.Close(); // if (__scope is null)
        w.Line("global::System.Exception? __exc = null;");
        w.Open("try");
        w.Line("return next();");
        w.Close(); // try
        w.Open("catch (global::System.Exception __ex)");
        w.Line("__exc = __ex;");
        w.Line("throw;");
        w.Close(); // catch
        w.Open("finally");
        w.Line(RT + ".StopDispatch(__scope.Value, __exc);");
        w.Close(); // finally

        w.Close(); // method
    }

    private static void EmitCreateStreamOverride(CodeWriter w, EquatableArray<StreamHandlerModel> streamHandlers)
    {
        w.Open(
            "public override global::System.Collections.Generic.IAsyncEnumerable<TResponse> CreateStream<TResponse>(" +
            "global::SkathIO.Rogue.IStreamRequest<TResponse> request, " +
            "global::System.Threading.CancellationToken cancellationToken)");

        if (streamHandlers.Count == 0)
        {
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(request.GetType());");
        }
        else
        {
            w.Open("switch (request)");

            foreach (var sh in streamHandlers)
            {
                string reqFqn = ToGlobalFqn(sh.RequestFqn);
                string method = "CreateStream_" + MakeSafeName(sh.RequestFqn);

                w.Line("case " + reqFqn + " r:");
                w.Indent();
                w.Line("return (global::System.Collections.Generic.IAsyncEnumerable<TResponse>)(object)" + method + "(r, cancellationToken);");
                w.Dedent();
            }

            w.Line("default:");
            w.Indent();
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(request.GetType());");
            w.Dedent();

            w.Close(); // switch
        }

        w.Close(); // method
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Per-notification-type Publish helper
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitPublishNotifMethod(CodeWriter w, string notifFqn, List<string> handlerFqns)
    {
        string notifType  = ToGlobalFqn(notifFqn);
        string methodName = "Publish_" + MakeSafeName(notifFqn);

        // ns2.0: ValueTask<Unit>; net8+: ValueTask. async: FR-45/PD-30 telemetry wraps the dispatch
        // in a try/finally that observes the outcome.
        w.Line("#if NETSTANDARD2_0");
        w.Open("private async global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> " + methodName + "(" + notifType + " notification, global::System.Threading.CancellationToken cancellationToken)");
        EmitPublishNotifBody(w, notifType, handlerFqns, returnsUnit: true);
        w.Close();
        w.Line("#else");
        w.Open("private async global::System.Threading.Tasks.ValueTask " + methodName + "(" + notifType + " notification, global::System.Threading.CancellationToken cancellationToken)");
        EmitPublishNotifBody(w, notifType, handlerFqns, returnsUnit: false);
        w.Close();
        w.Line("#endif");
    }

    private static void EmitPublishNotifBody(CodeWriter w, string notifTypeFqn, List<string> handlerFqns, bool returnsUnit)
    {
        w.Line("var publisher = " + SP + ".GetRequiredService<global::SkathIO.Rogue.INotificationPublisher>(" + SVC + ");");

        // Resolve all handlers via the interface (they are registered as INotificationHandler<T>).
        string handlerIface = "global::SkathIO.Rogue.INotificationHandler<" + notifTypeFqn + ">";
        w.Line("var handlers = " + SP + ".GetServices<" + handlerIface + ">(" + SVC + ");");
        w.Line("var executors = new global::System.Collections.Generic.List<global::SkathIO.Rogue.NotificationHandlerExecutor>();");
        w.Open("foreach (var h in handlers)");
        w.Line("var hCopy = h;");
        w.Line("executors.Add(new global::SkathIO.Rogue.NotificationHandlerExecutor(hCopy, (n, ct) => hCopy.Handle((" + notifTypeFqn + ")n, ct)));");
        w.Close();

        // FR-45 / PD-30: one dispatch scope per Publish call. The method is async; on the
        // disabled/unsubscribed path StartDispatch returns null and we await the publisher directly
        // (the async state machine is unavoidable here — Publish is already a ValueTask-returning
        // fan-out). On the enabled path the try/catch/finally observes the aggregate outcome.
        w.Line("var __scope = " + RT + ".StartDispatch<" + notifTypeFqn + ">();");
        w.Open("if (__scope is null)");
        if (returnsUnit)
            w.Line("return await publisher.Publish(executors, notification, cancellationToken).ConfigureAwait(false);");
        else
        {
            w.Line("await publisher.Publish(executors, notification, cancellationToken).ConfigureAwait(false);");
            w.Line("return;");
        }
        w.Close(); // if (__scope is null)

        w.Line("global::System.Exception? __exc = null;");
        w.Open("try");
        if (returnsUnit)
            w.Line("return await publisher.Publish(executors, notification, cancellationToken).ConfigureAwait(false);");
        else
            w.Line("await publisher.Publish(executors, notification, cancellationToken).ConfigureAwait(false);");
        w.Close(); // try
        w.Open("catch (global::System.Exception __ex)");
        w.Line("__exc = __ex;");
        w.Line("throw;");
        w.Close(); // catch
        w.Open("finally");
        w.Line(RT + ".StopDispatch(__scope.Value, __exc);");
        w.Close(); // finally
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Publish override
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitPublishOverride(CodeWriter w, Dictionary<string, List<string>> handlersByNotif)
    {
        // ns2.0: returns ValueTask<Unit>
        w.Line("#if NETSTANDARD2_0");
        w.Open("public override global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> Publish(global::SkathIO.Rogue.INotification notification, global::System.Threading.CancellationToken cancellationToken = default)");
        EmitPublishSwitchBody(w, handlersByNotif, returnsUnit: true);
        w.Close();

        w.Line("#else");
        w.Open("public override global::System.Threading.Tasks.ValueTask Publish(global::SkathIO.Rogue.INotification notification, global::System.Threading.CancellationToken cancellationToken = default)");
        EmitPublishSwitchBody(w, handlersByNotif, returnsUnit: false);
        w.Close();
        w.Line("#endif");
    }

    private static void EmitPublishSwitchBody(CodeWriter w, Dictionary<string, List<string>> handlersByNotif, bool returnsUnit)
    {
        if (handlersByNotif.Count == 0)
        {
            // FR-13: notifications may have zero handlers — return completed task
            if (returnsUnit)
                w.Line("return global::SkathIO.Rogue.Unit.Task;");
            else
                w.Line("return default;");
            return;
        }

        w.Open("switch (notification)");

        foreach (var kvp in handlersByNotif)
        {
            string notifFqn   = ToGlobalFqn(kvp.Key);
            string methodName = "Publish_" + MakeSafeName(kvp.Key);

            w.Line("case " + notifFqn + " n:");
            w.Indent();
            w.Line("return " + methodName + "(n, cancellationToken);");
            w.Dedent();
        }

        // Default: no handlers for this notification type — return completed (FR-13)
        w.Line("default:");
        w.Indent();
        if (returnsUnit)
            w.Line("return global::SkathIO.Rogue.Unit.Task;");
        else
            w.Line("return default;");
        w.Dedent();

        w.Close(); // switch
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a model FQN to a global:: C# type reference, replacing C# keyword aliases
    /// with their CLR equivalents (e.g. "string" → "System.String") so generated code compiles.
    /// </summary>
    internal static string ToGlobalFqn(string fqn)
    {
        // Replace keyword aliases with CLR type names so "global::string" becomes "global::System.String"
        switch (fqn)
        {
            case "string":  return "global::System.String";
            case "int":     return "global::System.Int32";
            case "long":    return "global::System.Int64";
            case "bool":    return "global::System.Boolean";
            case "double":  return "global::System.Double";
            case "float":   return "global::System.Single";
            case "decimal": return "global::System.Decimal";
            case "byte":    return "global::System.Byte";
            case "short":   return "global::System.Int16";
            case "char":    return "global::System.Char";
            case "object":  return "global::System.Object";
            case "uint":    return "global::System.UInt32";
            case "ulong":   return "global::System.UInt64";
            case "ushort":  return "global::System.UInt16";
            case "sbyte":   return "global::System.SByte";
            default:        return "global::" + fqn;
        }
    }

    /// <summary>
    /// Converts a fully-qualified type name into a C# identifier-safe name.
    /// E.g. "MyApp.GetUser" → "MyApp_GetUser", "System.String" → "System_String".
    /// </summary>
    internal static string MakeSafeName(string fqn)
    {
        var sb = new StringBuilder(fqn.Length);
        foreach (char c in fqn)
        {
            if (c == '.' || c == '<' || c == '>' || c == ',')
                sb.Append('_');
            else if (c == ' ')
                continue;
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
