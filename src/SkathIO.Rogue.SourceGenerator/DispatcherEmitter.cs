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

    // D5 (PD-2): the maximum behavior depth for which the generator emits a statically-typed per-request
    // chain method (Send_X_Chain_1 .. Send_X_Chain_8). Beyond this depth the dispatcher falls back to
    // PipelineExecutor.Execute (the runtime struct-index fold). 8 covers the overwhelming majority of
    // real pipelines while bounding generated code size at M requests × 8 chain methods.
    private const int MAX_STATIC_CHAIN_DEPTH = 8;

    internal static string Emit(DiscoveredModels models, RogueEmitOptions opts)
    {
        var w = new CodeWriter();

        // File header is written once by the caller (RogueGenerator), not here.
        // We write the source body only.
        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();

        w.Open("internal sealed class RogueDispatcherImpl : global::SkathIO.Rogue.RogueDispatcher");

        // ── Event → handler-FQN map (built once, used by the constructor fields and the
        //    per-event Publish helpers) ────────────────────────────────────────────────
        // D1: each event type gets a per-instance `Func<IEventHandler<TEvent>>[]` field populated
        // in the constructor, so Publish_X iterates cached factory delegates instead of calling
        // serviceProvider.GetServices<IEventHandler<TEvent>>() on every dispatch.
        var handlersByEvent = new Dictionary<string, List<string>>();
        foreach (var eh in models.EventHandlers)
        {
            if (!handlersByEvent.TryGetValue(eh.EventFqn, out var list))
            {
                list = new List<string>();
                handlersByEvent[eh.EventFqn] = list;
            }
            list.Add(eh.TypeFqn);
        }

        // ── D1: per-event factory-delegate array fields ───────────────────────────────
        EmitHandlerFactoryFields(w, handlersByEvent);

        // ── Constructor ───────────────────────────────────────────────────────────────
        // Passes the service provider to the base, which stores it in the protected
        // _serviceProvider field that the override methods below read, then (D1) populates the
        // per-event factory-delegate arrays so handler resolution per Publish is a delegate call,
        // not a DI service enumeration.
        EmitConstructor(w, handlersByEvent);
        w.Line();

        // ── void-path async helper ────────────────────────────────────────────────────
        // Used when ICommandHandler<TCommand> returns bare ValueTask on net8+ and we need ValueTask<Unit>.
        w.Line("#if !NETSTANDARD2_0");
        w.Open("private static async global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> AwaitVoidThenUnit(global::System.Threading.Tasks.ValueTask vt)");
        w.Line("await vt.ConfigureAwait(false);");
        w.Line("return global::SkathIO.Rogue.Unit.Value;");
        w.Close(); // AwaitVoidThenUnit
        w.Line("#endif");
        w.Line();

        // ── Unit-discarding helper for the void Send(ICommand) override ─────────────────
        // The concrete Send_X methods return ValueTask<Unit> on every TFM; the ISender void-command
        // overload returns bare ValueTask. Discard the Unit. (The void-command path is not the AC-C
        // 0-alloc target — that is the typed Send command/query path, re-established in 11.3.)
        w.Open("private static async global::System.Threading.Tasks.ValueTask IgnoreUnit(global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> vt)");
        w.Line("await vt.ConfigureAwait(false);");
        w.Close(); // IgnoreUnit
        w.Line();

        // ── BoxAsync helper for SendObject ────────────────────────────────────────────
        w.Open("private static async global::System.Threading.Tasks.ValueTask<object?> BoxAsync<T>(global::System.Threading.Tasks.ValueTask<T> vt)");
        w.Line("return await vt.ConfigureAwait(false);");
        w.Close(); // BoxAsync
        w.Line();

        // ── Per-request Send methods ──────────────────────────────────────────────────
        // D4/D5: an open NON-STREAM generic behavior applies to ALL non-stream requests, so its presence
        // anywhere in the compilation vetoes both the per-request behavior-list bypass (D4) and the static
        // behavior chain (D5) for every Send_X. Computed once here and threaded into EmitSendMethod. The
        // veto is stream-filtered (`stream: false`): Send_X handles only non-stream requests, which can
        // never see an open IStreamPipelineBehavior<,>, so an unrelated open STREAM behavior must NOT
        // disable the optimization for closed-behavior commands/queries.
        bool hasOpenBehavior = RegistrationEmitter.HasUsableOpenBehavior(models, stream: false);
        foreach (var handler in models.Handlers)
        {
            EmitSendMethod(w, handler, models, hasOpenBehavior);
            w.Line();
        }

        // ── Send overrides (ISender dispatch switch, one per CQS overload — PD-40 clean break) ──
        EmitSendCommandVoidOverride(w, models.Handlers);
        w.Line();
        EmitSendCommandOverride(w, models.Handlers);
        w.Line();
        EmitSendQueryOverride(w, models.Handlers);
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

        // ── Per-event Publish helper methods ──────────────────────────────────────────
        // handlersByEvent was built at the top of the class so the constructor could emit the
        // matching factory-delegate fields (D1). Each Publish_X iterates the cached array.
        foreach (var kvp in handlersByEvent)
        {
            EmitPublishEventMethod(w, kvp.Key, kvp.Value);
            w.Line();
        }

        // ── Publish override ──────────────────────────────────────────────────────────
        EmitPublishOverride(w, handlersByEvent);

        w.Close(); // class RogueDispatcherImpl

        w.Dedent();
        w.Line("}"); // namespace SkathIO.Rogue.Generated

        return w.ToString();
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // D1: per-event handler factory-delegate fields + constructor population
    // ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// D1: the field name for an event type's factory-delegate array. Shares
    /// <see cref="MakeSafeName"/> with the Send-method naming so two emitters never disagree.
    /// </summary>
    private static string HandlerFieldName(string eventFqn) => "_handlers_" + MakeSafeName(eventFqn);

    /// <summary>
    /// D1: emits one <c>private readonly Func&lt;IEventHandler&lt;TEvent&gt;&gt;[]</c> field per event
    /// type that has at least one registered handler. The array caches the factory delegates so
    /// <c>Publish_X</c> never calls <c>GetServices&lt;IEventHandler&lt;TEvent&gt;&gt;()</c> on the hot
    /// path. Event types with zero handlers never enter <paramref name="handlersByEvent"/>, so no
    /// field is emitted for them (their Publish falls through the switch default).
    /// </summary>
    private static void EmitHandlerFactoryFields(CodeWriter w, Dictionary<string, List<string>> handlersByEvent)
    {
        if (handlersByEvent.Count == 0) return;

        foreach (var kvp in handlersByEvent)
        {
            string eventType   = ToGlobalFqn(kvp.Key);
            string handlerIface = "global::SkathIO.Rogue.IEventHandler<" + eventType + ">";
            w.Line(
                "private readonly global::System.Func<" + handlerIface + ">[] " +
                HandlerFieldName(kvp.Key) + ";");
        }

        w.Line();
    }

    /// <summary>
    /// Emits the constructor. Always chains <c>: base(serviceProvider)</c> (the base stores the
    /// provider in the protected <c>_serviceProvider</c> field the overrides read). When there are
    /// event handlers (D1), the body initializes each per-event factory-delegate array; each element
    /// is a closure over <c>serviceProvider</c> that resolves a fresh handler instance via
    /// <c>GetRequiredService&lt;THandler&gt;()</c> on every call — preserving transient/scoped
    /// lifetimes (a fresh instance per factory call per Publish). With no event handlers the
    /// constructor is body-less.
    /// </summary>
    private static void EmitConstructor(CodeWriter w, Dictionary<string, List<string>> handlersByEvent)
    {
        if (handlersByEvent.Count == 0)
        {
            w.Line("public RogueDispatcherImpl(global::System.IServiceProvider serviceProvider)");
            w.Line("    : base(serviceProvider) { }");
            return;
        }

        w.Line("public RogueDispatcherImpl(global::System.IServiceProvider serviceProvider)");
        w.Line("    : base(serviceProvider)");
        w.Line("{");
        w.Indent();

        foreach (var kvp in handlersByEvent)
        {
            string eventType    = ToGlobalFqn(kvp.Key);
            string handlerIface = "global::SkathIO.Rogue.IEventHandler<" + eventType + ">";

            w.Line(HandlerFieldName(kvp.Key) + " = new global::System.Func<" + handlerIface + ">[]");
            w.Line("{");
            w.Indent();
            // Resolve fresh per call (closes over the constructor's `serviceProvider` parameter, which
            // is the same provider the base stored): GetRequiredService<THandler>() respects the
            // handler's DI lifetime — transient/scoped yield a new instance per Publish, singleton the same.
            foreach (var handlerFqn in kvp.Value)
            {
                string handlerType = ToGlobalFqn(handlerFqn);
                w.Line("() => " + SP + ".GetRequiredService<" + handlerType + ">(serviceProvider),");
            }
            w.Dedent();
            w.Line("};");
        }

        w.Dedent();
        w.Line("}"); // constructor body
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Per-request Send_XXX method
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitSendMethod(CodeWriter w, HandlerModel handler, DiscoveredModels models, bool hasOpenBehavior)
    {
        var processors = models.Processors;
        bool isVoid = handler.ResponseFqn is null;
        string responseFqn = isVoid ? "global::SkathIO.Rogue.Unit" : ToGlobalFqn(handler.ResponseFqn!);
        string requestFqn  = ToGlobalFqn(handler.RequestFqn);

        // Register/resolve lockstep (PD-43/PD-48): the handler-service interface resolved here must match
        // the one RegistrationEmitter registered the handler under. For native CQS handlers that is
        // ICommandHandler (incl. the void path) / IQueryHandler. For MediatR-adapter-mapped handlers
        // (PD-48) the handler implements the adapter's OWN Compatibility.IRequestHandler<TReq,TResp> /
        // IRequestHandler<TReq> — that is the interface application code implements and the one DI knows
        // it by, so we resolve/register against it (NOT the core CQS interface the adapter does not
        // implement). The F8 command-vs-query decision lives in handler.Kind and drives the dispatch
        // entry point, not the resolved handler interface.
        string handlerIface = AdapterHandlerIface(handler, requestFqn, responseFqn, isVoid);

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

        // D4 behavior-list bypass: when the generator already knows at COMPILE time that this request
        // has zero applicable behaviors, the per-dispatch GetService<IReadOnlyList<IPipelineBehavior<,>>>
        // lookup + runtime `behaviors.Count == 0` branch are pure overhead — there is no IReadOnlyList
        // factory registered for it (RegistrationEmitter.EmitBehaviorRegistrations skips the registration
        // for a request with no applicable behaviors), so GetService would return null every time and the
        // count check would always take the direct-handler branch anyway. We compute applicability with
        // the SAME helper RegistrationEmitter uses (CollectApplicableBehaviorsFor → CollectApplicableBehaviors),
        // so the two emitters can never disagree about which behaviors apply.
        //
        // CRITICAL guard: an OPEN generic behavior (e.g. LoggingBehavior<TReq,TRes> registered as
        // IPipelineBehavior<,>) closes for — and therefore applies to — EVERY request, so its
        // per-request applicable list is non-empty and RegistrationEmitter DOES register an IReadOnlyList
        // factory for it. `hasOpenBehavior` (computed once over the whole compilation) vetoes the bypass
        // for every request: if it is set we must keep the runtime lookup so the open behavior is woven in.
        var applicableBehaviors = RegistrationEmitter.CollectApplicableBehaviorsFor(
            models, requestFqn, responseFqn, stream: false);
        bool bypassBehaviorList = !hasProcessors && !hasOpenBehavior && applicableBehaviors.Count == 0;

        // D5 (PD-2) static behavior chain: when this request has at least one COMPILE-TIME-known closed
        // behavior and NO open behavior in the compilation and NO processors, the behavior list passed to
        // _WithBehaviors is statically bounded (every applicable behavior is a closed, per-request match —
        // open behaviors, whose runtime list length is not statically known, veto this path via
        // hasOpenBehavior). That lets _WithBehaviors switch on behaviors.Count into per-request chain
        // methods (Send_X_Chain_1..MAX_STATIC_CHAIN_DEPTH) that take each behavior as a typed parameter,
        // eliminating PipelineState's per-link struct-boxing closure. Depth > MAX_STATIC_CHAIN_DEPTH falls
        // back to PipelineExecutor.Execute. Processor-bearing and open-behavior requests keep the existing
        // PipelineExecutor.Execute body unchanged.
        bool useStaticChain = !hasProcessors && !hasOpenBehavior && applicableBehaviors.Count > 0;

        // D3: the per-request entry point is emitted `internal` (not `private`) so the generated
        // public RogueExtensions class (same consumer assembly) can downcast RogueDispatcher to this
        // impl and call it directly — the 0-alloc concrete fast path. The static _Direct/_WithBehaviors/
        // _WithTelemetry companions below stay `private static`: they are call-shape factoring only and
        // are never reached except through this entry point.
        w.Open(
            "internal global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "(" + requestFqn + " request, global::System.Threading.CancellationToken cancellationToken)");

        // Resolve handler
        w.Line("var handler = " + SP + ".GetRequiredService<" + handlerIface + ">(" + SVC + ");");

        if (bypassBehaviorList)
        {
            // D4 BYPASS PATH — compile-time-known zero behaviors, no processors. No
            // GetService<IReadOnlyList<...>>() call and no `behaviors.Count == 0` runtime branch: call
            // the handler directly via the {methodName}_Direct companion. The telemetry path is still
            // emitted (FR-45 / PD-30), but its _WithTelemetry companion invokes _Direct rather than
            // threading an empty behavior list through PipelineExecutor.Execute.
            //
            // The direct call is factored into {methodName}_Direct (one source of truth for the
            // handler-call shape, shared by the telemetry-off return below and the telemetry-on
            // companion) so EmitDirectHandlerReturn — with its #if TFM split and void/adapter wrapping —
            // is emitted exactly once. _Direct has no captured locals, so it allocates no closure.
            w.Line("var __scope = " + RT + ".StartDispatch<" + requestFqn + ">();");
            w.Open("if (__scope is null)");
            w.Line("return " + methodName + "_Direct(handler, request, cancellationToken);");
            w.Close(); // if (__scope is null)
            w.Line();
            w.Line("return " + methodName + "_DirectWithTelemetry(handler, request, cancellationToken, __scope.Value);");

            w.Close(); // method

            w.Line();
            EmitSendDirectMethod(w, methodName, handlerIface, requestFqn, responseFqn, isVoid, handler.IsAdapterMapped);
            w.Line();
            EmitSendDirectWithTelemetryMethod(w, methodName, handlerIface, requestFqn, responseFqn);
            return;
        }

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
            // This path is emitted only when the generator found at least one applicable behavior at
            // compile time (a matched closed behavior) OR any open behavior exists in the compilation
            // (D4 guard). For the open-behavior case the resolved list's length is a RUNTIME fact (the
            // open behavior is closed-and-registered per request, but DI ultimately decides what the
            // IReadOnlyList contains), so the empty-list check is retained as a runtime branch on
            // `behaviors.Count == 0`. Requests with provably zero applicable behaviors took the D4
            // bypass above and never reach here.
            // FR-45 / PD-30: begin a dispatch scope. When telemetry is disabled or unsubscribed,
            // StartDispatch returns null and we take the existing fast path verbatim — no Activity,
            // no allocation, PD-31's 0-byte guarantee fully preserved.
            w.Line("var __scope = " + RT + ".StartDispatch<" + requestFqn + ">();");
            w.Open("if (__scope is null)");

            w.Open("if (behaviors.Count == 0)");
            EmitDirectHandlerReturn(w, isVoid, handler.IsAdapterMapped);
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
            EmitHandlerCallDelegate(w, isVoid, responseFqn, handler.IsAdapterMapped);
            EmitProcessorPath(w, requestFqn, responseFqn, "handlerCall", pre, post, exHandlers, exActions);
        }

        w.Close(); // method

        if (!hasProcessors)
        {
            w.Line();
            EmitSendWithBehaviorsMethod(w, methodName, handlerIface, requestFqn, responseFqn, behaviorListType, isVoid, handler.IsAdapterMapped, useStaticChain);
            w.Line();
            EmitSendWithTelemetryMethod(w, methodName, handlerIface, requestFqn, responseFqn, behaviorListType);

            // D5 (PD-2): emit the per-request static chain methods that _WithBehaviors switches into.
            // Only the closed-behavior path (useStaticChain) routes through them; the telemetry path
            // reaches them transitively via _WithBehaviors. C# does not require declaration order within
            // a class, so emitting these after the companions above is fine.
            if (useStaticChain)
            {
                w.Line();
                EmitChainMethods(w, methodName, handlerIface, behaviorIface, requestFqn, responseFqn, isVoid, handler.IsAdapterMapped);
            }
        }
    }

    /// <summary>
    /// PD-48: the handler-service interface the dispatcher resolves and (in lockstep) the registration
    /// registers under. For native CQS handlers it is the core <c>ICommandHandler</c>/<c>IQueryHandler</c>
    /// (the void path is always <c>ICommandHandler&lt;TReq&gt;</c>). For MediatR-adapter-mapped handlers
    /// (<see cref="HandlerModel.IsAdapterMapped"/>) it is the adapter's own
    /// <c>SkathIO.Rogue.Compatibility.IRequestHandler&lt;TReq,TResp&gt;</c> / <c>IRequestHandler&lt;TReq&gt;</c>
    /// — the interface the application type actually implements and DI knows it by. The F8 command-vs-query
    /// decision (in <see cref="HandlerModel.Kind"/>) does not change the resolved adapter interface.
    /// </summary>
    private static string AdapterHandlerIface(HandlerModel handler, string requestFqn, string responseFqn, bool isVoid)
    {
        if (handler.IsAdapterMapped)
        {
            return isVoid
                ? "global::SkathIO.Rogue.Compatibility.IRequestHandler<" + requestFqn + ">"
                : "global::SkathIO.Rogue.Compatibility.IRequestHandler<" + requestFqn + ", " + responseFqn + ">";
        }

        string handlerIfaceName = handler.Kind == HandlerKind.Query ? "IQueryHandler" : "ICommandHandler";
        return isVoid
            ? "global::SkathIO.Rogue.ICommandHandler<" + requestFqn + ">"
            : "global::SkathIO.Rogue." + handlerIfaceName + "<" + requestFqn + ", " + responseFqn + ">";
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
    /// D4 behavior-list bypass: the direct handler call for a request the generator knows has zero
    /// applicable behaviors and no processors. No <c>RequestHandlerDelegate</c> is built and no
    /// <c>PipelineExecutor.Execute</c> is reached — this is just <see cref="EmitDirectHandlerReturn"/>
    /// (the same shape the no-behavior fast path already used) lifted into a parameterless-capture
    /// <c>private static</c> method so its body is the single source of truth for the call, shared by
    /// the telemetry-off return and the <c>_DirectWithTelemetry</c> companion. Being <c>static</c> with
    /// no captured locals, it allocates no closure.
    /// </summary>
    private static void EmitSendDirectMethod(
        CodeWriter w, string methodName, string handlerIface, string requestFqn, string responseFqn,
        bool isVoid, bool isAdapter)
    {
        w.Open(
            "private static global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "_Direct(" +
            handlerIface + " handler, " + requestFqn + " request, " +
            "global::System.Threading.CancellationToken cancellationToken)");

        EmitDirectHandlerReturn(w, isVoid, isAdapter);

        w.Close(); // method
    }

    /// <summary>
    /// FR-45 / PD-30: the telemetry-on continuation of the D4 bypass path. Reached only when
    /// <c>StartDispatch</c> returned a non-null scope (telemetry enabled AND subscribed), so this path
    /// is already allocating an async state machine. Unlike <see cref="EmitSendWithTelemetryMethod"/>
    /// it does NOT thread a behavior list through <c>PipelineExecutor.Execute</c> — there are provably
    /// zero applicable behaviors — it awaits the direct handler call (<c>_Direct</c>) inside the
    /// try/catch/finally that observes the outcome and stops the scope.
    /// </summary>
    private static void EmitSendDirectWithTelemetryMethod(
        CodeWriter w, string methodName, string handlerIface, string requestFqn, string responseFqn)
    {
        w.Open(
            "private static async global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "_DirectWithTelemetry(" +
            handlerIface + " handler, " + requestFqn + " request, " +
            "global::System.Threading.CancellationToken cancellationToken, " +
            "global::SkathIO.Rogue.DispatchScope scope)");

        w.Line("global::System.Exception? __exc = null;");
        w.Open("try");
        w.Line("return await " + methodName + "_Direct(handler, request, cancellationToken).ConfigureAwait(false);");
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
        string behaviorListType, bool isVoid, bool isAdapter, bool useStaticChain)
    {
        w.Open(
            "private static global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "_WithBehaviors(" +
            handlerIface + " handler, " + requestFqn + " request, " + behaviorListType + " behaviors, " +
            "global::System.Threading.CancellationToken cancellationToken)");

        if (useStaticChain)
        {
            // D5 (PD-2): switch on the (statically bounded) behavior count into the per-request chain
            // methods. Each Send_X_Chain_N takes the N behaviors as typed parameters, so no PipelineState
            // struct is captured/boxed per link. Depth 0 is unreachable here (the no-behavior branch in the
            // entry point returns before _WithBehaviors is called) but is emitted as a defensive arm — for
            // an open-behavior-free, closed-behavior request DI could still theoretically resolve an empty
            // list, in which case the handler is called directly. Depth > MAX_STATIC_CHAIN_DEPTH falls back
            // to PipelineExecutor.Execute (the deferred RequestHandlerDelegate is needed there).
            w.Open("switch (behaviors.Count)");
            w.Line("case 0:");
            w.Indent();
            EmitDirectHandlerReturn(w, isVoid, isAdapter);
            w.Dedent();
            for (int n = 1; n <= MAX_STATIC_CHAIN_DEPTH; n++)
            {
                w.Line("case " + n + ":");
                w.Indent();
                w.Line("return " + methodName + "_Chain_" + n + "(request, " +
                    ChainBehaviorArgs(n) + "handler, cancellationToken);");
                w.Dedent();
            }
            w.Line("default:");
            w.Indent();
            EmitHandlerCallDelegate(w, isVoid, responseFqn, isAdapter);
            w.Line("return global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", " + responseFqn + ">(");
            w.Line("    request, behaviors, handlerCall, cancellationToken);");
            w.Dedent();
            w.Close(); // switch
        }
        else
        {
            EmitHandlerCallDelegate(w, isVoid, responseFqn, isAdapter);
            w.Line("return global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", " + responseFqn + ">(");
            w.Line("    request, behaviors, handlerCall, cancellationToken);");
        }

        w.Close(); // method
    }

    /// <summary>
    /// Emits the comma-terminated <c>behaviors[0], behaviors[1], …, behaviors[N-1], </c> argument fragment
    /// used by the <c>_WithBehaviors</c> switch when dispatching into <c>Send_X_Chain_N</c>.
    /// </summary>
    private static string ChainBehaviorArgs(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append("behaviors[").Append(i).Append("], ");
        }
        return sb.ToString();
    }

    /// <summary>
    /// D5 (PD-2): emits the per-request statically-typed behavior chain methods
    /// <c>Send_X_Chain_1 .. Send_X_Chain_{MAX_STATIC_CHAIN_DEPTH}</c>. Each method takes its N behaviors as
    /// explicit, strongly-typed parameters and recursively delegates the <c>next</c> step to
    /// <c>Send_X_Chain_{N-1}</c>. Because the chain threads <c>request</c>/<c>handler</c>/<c>ct</c> as
    /// parameters rather than capturing a <c>PipelineState</c> struct, the per-link struct-boxing the
    /// runtime <see cref="global::SkathIO.Rogue.PipelineExecutor"/> incurs is gone.
    ///
    /// The innermost step (<c>Chain_1</c>'s <c>next</c>) is still a <c>RequestHandlerDelegate&lt;TResponse&gt;</c>
    /// closure over <c>handler</c>/<c>request</c>/<c>ct</c> (the same shape <see cref="EmitHandlerCallDelegate"/>
    /// builds), and each <c>Chain_N</c> link allocates one <c>() =&gt;</c> delegate — so this reduces, but does
    /// not eliminate, allocation. The aspirational "0 B" AC is governed by the Phase 5 benchmark, not asserted
    /// here (NFR-PERF-5 honesty).
    /// </summary>
    private static void EmitChainMethods(
        CodeWriter w, string methodName, string handlerIface, string behaviorIface,
        string requestFqn, string responseFqn, bool isVoid, bool isAdapter)
    {
        for (int n = 1; n <= MAX_STATIC_CHAIN_DEPTH; n++)
        {
            if (n > 1) w.Line();

            // Signature: (request, b0, b1, …, b{n-1}, handler, ct)
            var sig = new StringBuilder();
            sig.Append(requestFqn).Append(" request, ");
            for (int i = 0; i < n; i++)
            {
                sig.Append(behaviorIface).Append(" b").Append(i).Append(", ");
            }
            sig.Append(handlerIface).Append(" handler, ");
            sig.Append("global::System.Threading.CancellationToken ct");

            w.Open(
                "private static global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
                methodName + "_Chain_" + n + "(" + sig + ")");

            if (n == 1)
            {
                // Innermost link: next() invokes the handler directly. Build it as the same
                // RequestHandlerDelegate<TResponse> EmitHandlerCallDelegate emits (handles the void/adapter
                // TFM split), then hand it to b0.Handle as `next`.
                EmitChainInnermostNext(w, isVoid, responseFqn, isAdapter);
                w.Line("return b0.Handle(request, next, ct);");
            }
            else
            {
                // next() delegates to the next-shorter chain, forwarding b1..b{n-1}. The lambda captures
                // request/b1../handler/ct — one delegate per link, but no PipelineState struct box.
                var forward = new StringBuilder();
                for (int i = 1; i < n; i++)
                {
                    forward.Append("b").Append(i).Append(", ");
                }
                w.Line(
                    "return b0.Handle(request, () => " + methodName + "_Chain_" + (n - 1) +
                    "(request, " + forward + "handler, ct), ct);");
            }

            w.Close(); // Chain_n method
        }
    }

    /// <summary>
    /// D5 (PD-2): emits the innermost <c>next</c> delegate for <c>Send_X_Chain_1</c> — a
    /// <c>RequestHandlerDelegate&lt;TResponse&gt; next = () =&gt; handler.Handle(request, ct)</c> with the
    /// SAME void/adapter/TFM wrapping <see cref="EmitHandlerCallDelegate"/> produces (the chain's handler
    /// parameter is named <c>handler</c> and its cancellation token <c>ct</c>, matching this body). The
    /// delegate is named <c>next</c> so the caller can pass it straight to <c>b0.Handle(request, next, ct)</c>.
    /// </summary>
    private static void EmitChainInnermostNext(CodeWriter w, bool isVoid, string responseFqn, bool isAdapter)
    {
        if (isVoid)
        {
            if (isAdapter)
            {
                // PD-48: adapter void handler returns bare ValueTask on every TFM — wrap to ValueTask<Unit>.
                w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> next = () =>");
                w.Line("{");
                w.Line("    var voidVt = handler.Handle(request, ct);");
                w.Line("    if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
                w.Line("    return AwaitVoidThenUnit(voidVt);");
                w.Line("};");
                return;
            }

            w.Line("#if NETSTANDARD2_0");
            w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> next = () => handler.Handle(request, ct);");
            w.Line("#else");
            w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> next = () =>");
            w.Line("{");
            w.Line("    var voidVt = handler.Handle(request, ct);");
            w.Line("    if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
            w.Line("    return AwaitVoidThenUnit(voidVt);");
            w.Line("};");
            w.Line("#endif");
        }
        else
        {
            w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> next = () => handler.Handle(request, ct);");
        }
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
    private static void EmitDirectHandlerReturn(CodeWriter w, bool isVoid, bool isAdapter)
    {
        if (isVoid)
        {
            if (isAdapter)
            {
                // PD-48: the adapter's Compatibility.IRequestHandler<TReq>.Handle returns BARE ValueTask
                // on EVERY TFM (it declares its own `ValueTask Handle`, unlike the core void handler whose
                // ns2.0 variant returns ValueTask<Unit>). So the ValueTask -> ValueTask<Unit> wrap is
                // unconditional here — no #if split.
                w.Line("var voidVt = handler.Handle(request, cancellationToken);");
                w.Line("if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
                w.Line("return AwaitVoidThenUnit(voidVt);");
                return;
            }

            // ICommandHandler<TCommand>.Handle returns ValueTask<Unit> on netstandard2.0 (no bare
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
    private static void EmitHandlerCallDelegate(CodeWriter w, bool isVoid, string responseFqn, bool isAdapter)
    {
        if (isVoid)
        {
            if (isAdapter)
            {
                // PD-48: adapter void handler returns bare ValueTask on every TFM (see
                // EmitDirectHandlerReturn) — the ValueTask -> ValueTask<Unit> wrap is unconditional.
                w.Line("global::SkathIO.Rogue.RequestHandlerDelegate<" + responseFqn + "> handlerCall = () =>");
                w.Line("{");
                w.Line("    var voidVt = handler.Handle(request, cancellationToken);");
                w.Line("    if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
                w.Line("    return AwaitVoidThenUnit(voidVt);");
                w.Line("};");
                return;
            }

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

    // PD-40 clean break: the single Send<TResponse>(IRequest<TResponse>) override is replaced by three
    // overrides — one per ISender overload — switching over the relevant handler family. No shared
    // marker is reached; overload resolution at the call site picks the path from the argument's
    // interface. (Behavioural end-to-end validation of all four dispatch shapes is 11.3; the override
    // shapes are forced here by the reshaped RogueDispatcher base virtuals.)

    /// <summary>Override for void commands: <c>Send(ICommand, CancellationToken)</c>.</summary>
    private static void EmitSendCommandVoidOverride(CodeWriter w, EquatableArray<HandlerModel> handlers)
    {
        w.Open(
            "public override global::System.Threading.Tasks.ValueTask Send(" +
            "global::SkathIO.Rogue.ICommand command, " +
            "global::System.Threading.CancellationToken cancellationToken)");

        // PD-48: adapter-mapped handlers are EXCLUDED from the typed-Send switches. Their adapter message
        // type does not implement the core ICommand/ICommand<T>/IQuery<T> markers (it is self-contained),
        // so a `case <AdapterType> r:` inside this `switch ((ICommand) command)` would be CS8121
        // (unrelated-type pattern). Adapter messages dispatch only through SendObject (the object switch).
        bool any = false;
        foreach (var h in handlers)
            if (h.Kind == HandlerKind.Command && h.ResponseFqn is null && !h.IsAdapterMapped) { any = true; break; }

        if (!any)
        {
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(command.GetType());");
        }
        else
        {
            w.Open("switch (command)");
            foreach (var handler in handlers)
            {
                if (handler.Kind != HandlerKind.Command || handler.ResponseFqn is not null || handler.IsAdapterMapped) continue;
                string reqFqn = ToGlobalFqn(handler.RequestFqn);
                string method = "Send_" + MakeSafeName(handler.RequestFqn);
                w.Line("case " + reqFqn + " r:");
                w.Indent();
                // Send_X returns ValueTask<Unit>; the override returns bare ValueTask — discard the Unit.
                w.Line("return IgnoreUnit(" + method + "(r, cancellationToken));");
                w.Dedent();
            }
            w.Line("default:");
            w.Indent();
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(command.GetType());");
            w.Dedent();
            w.Close(); // switch
        }

        w.Close(); // method
    }

    /// <summary>Override for typed commands: <c>Send&lt;TResponse&gt;(ICommand&lt;TResponse&gt;, CancellationToken)</c>.</summary>
    private static void EmitSendCommandOverride(CodeWriter w, EquatableArray<HandlerModel> handlers)
    {
        EmitTypedSendOverride(w, handlers, HandlerKind.Command, "ICommand", "command");
    }

    /// <summary>Override for queries: <c>Send&lt;TResponse&gt;(IQuery&lt;TResponse&gt;, CancellationToken)</c>.</summary>
    private static void EmitSendQueryOverride(CodeWriter w, EquatableArray<HandlerModel> handlers)
    {
        EmitTypedSendOverride(w, handlers, HandlerKind.Query, "IQuery", "query");
    }

    private static void EmitTypedSendOverride(
        CodeWriter w, EquatableArray<HandlerModel> handlers, HandlerKind kind, string markerName, string paramName)
    {
        w.Open(
            "public override global::System.Threading.Tasks.ValueTask<TResponse> Send<TResponse>(" +
            "global::SkathIO.Rogue." + markerName + "<TResponse> " + paramName + ", " +
            "global::System.Threading.CancellationToken cancellationToken)");

        // Minor #1 (11.3) — void-command typed-dispatch. A void command implements `ICommand`, which
        // derives from `ICommand<Unit>`, so it is a legal argument to this typed overload with
        // TResponse == Unit. Its Send_X returns ValueTask<Unit>, so the same (ValueTask<TResponse>)(object)
        // cast that the typed-command arms use is correct. Queries always have a response, so for the
        // query override a void handler can never appear — Qualifies() reduces to ResponseFqn is not null.
        //
        // PD-48: adapter-mapped handlers are EXCLUDED. Their adapter message does not implement the core
        // ICommand<T>/IQuery<T> markers (self-contained), so a `case <AdapterType> r:` inside this typed
        // switch would be CS8121. Adapter messages dispatch only through SendObject.
        bool Qualifies(HandlerModel h) =>
            h.Kind == kind && !h.IsAdapterMapped && (h.ResponseFqn is not null || kind == HandlerKind.Command);

        bool any = false;
        foreach (var h in handlers)
            if (Qualifies(h)) { any = true; break; }

        if (!any)
        {
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(" + paramName + ".GetType());");
        }
        else
        {
            w.Open("switch (" + paramName + ")");
            foreach (var handler in handlers)
            {
                if (!Qualifies(handler)) continue;
                string reqFqn = ToGlobalFqn(handler.RequestFqn);
                string method = "Send_" + MakeSafeName(handler.RequestFqn);
                w.Line("case " + reqFqn + " r:");
                w.Indent();
                // For a void command, Send_X returns ValueTask<Unit> and the call site bound TResponse
                // to Unit; for a typed command/query it returns ValueTask<ResponseFqn> == ValueTask<TResponse>.
                // Either way the (ValueTask<TResponse>)(object) cast is the correct erasure-bridging cast.
                w.Line("return (global::System.Threading.Tasks.ValueTask<TResponse>)(object)" + method + "(r, cancellationToken);");
                w.Dedent();
            }
            w.Line("default:");
            w.Indent();
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(" + paramName + ".GetType());");
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
        //
        // PD-48: this switch includes EVERY handler — native CQS handlers AND MediatR-adapter-mapped ones.
        // `request` is typed `object`, so a `case <AdapterMessageType> r:` is always a valid pattern
        // regardless of which markers the adapter type implements. This is the ONLY dispatch entry point
        // for adapter messages (they are excluded from the typed-Send switches — see EmitTypedSendOverride
        // / EmitSendCommandVoidOverride), reached via IMediator.Send(object) / ISender object dispatch.
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
    // Fix 5 / FR-23: emits CreateStream<TItem> as a switch over discovered IStreamQueryHandler<,>
    // implementations (PD-40 clean break — keyed on the core IStreamQuery<T> family), each delegating to
    // a per-request helper that resolves the handler and folds any registered IStreamPipelineBehavior<,>
    // around it. MediatR-adapter stream handlers are picked up here transitively: PD-48 declares
    // Compatibility.IStreamRequestHandler<,> as an IS-A sub-interface of IStreamQueryHandler<,>, so the
    // discovery below finds them with no adapter-specific branch.
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
        string handlerIface = "global::SkathIO.Rogue.IStreamQueryHandler<" + requestFqn + ", " + elementFqn + ">";
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
            "public override global::System.Collections.Generic.IAsyncEnumerable<TItem> CreateStream<TItem>(" +
            "global::SkathIO.Rogue.IStreamQuery<TItem> query, " +
            "global::System.Threading.CancellationToken cancellationToken)");

        if (streamHandlers.Count == 0)
        {
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(query.GetType());");
        }
        else
        {
            w.Open("switch (query)");

            foreach (var sh in streamHandlers)
            {
                string reqFqn = ToGlobalFqn(sh.RequestFqn);
                string method = "CreateStream_" + MakeSafeName(sh.RequestFqn);

                w.Line("case " + reqFqn + " r:");
                w.Indent();
                w.Line("return (global::System.Collections.Generic.IAsyncEnumerable<TItem>)(object)" + method + "(r, cancellationToken);");
                w.Dedent();
            }

            w.Line("default:");
            w.Indent();
            w.Line("throw new global::SkathIO.Rogue.RogueUnregisteredRequestException(query.GetType());");
            w.Dedent();

            w.Close(); // switch
        }

        w.Close(); // method
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Per-notification-type Publish helper
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitPublishEventMethod(CodeWriter w, string eventFqn, List<string> handlerFqns)
    {
        string eventType  = ToGlobalFqn(eventFqn);
        string methodName = "Publish_" + MakeSafeName(eventFqn);
        // D1: the factory-delegate array field emitted by EmitConstructor for this event type.
        string fieldName  = HandlerFieldName(eventFqn);

        // ns2.0: ValueTask<Unit>; net8+: ValueTask. async: FR-45/PD-30 telemetry wraps the dispatch
        // in a try/finally that observes the outcome.
        w.Line("#if NETSTANDARD2_0");
        w.Open("private async global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> " + methodName + "(" + eventType + " ev, global::System.Threading.CancellationToken cancellationToken)");
        EmitPublishEventBody(w, eventType, fieldName, returnsUnit: true);
        w.Close();
        w.Line("#else");
        w.Open("private async global::System.Threading.Tasks.ValueTask " + methodName + "(" + eventType + " ev, global::System.Threading.CancellationToken cancellationToken)");
        EmitPublishEventBody(w, eventType, fieldName, returnsUnit: false);
        w.Close();
        w.Line("#endif");
    }

    private static void EmitPublishEventBody(CodeWriter w, string eventTypeFqn, string fieldName, bool returnsUnit)
    {
        w.Line("var publisher = " + SP + ".GetRequiredService<global::SkathIO.Rogue.IEventPublisher>(" + SVC + ");");

        // D1: iterate the constructor-cached factory delegates instead of calling
        // GetServices<IEventHandler<TEvent>>() per Publish. Each factory resolves a fresh handler
        // instance (transient/scoped lifetimes preserved). The executors array is built once per
        // Publish; arrays implement IEnumerable<EventHandlerExecutor> natively, so it passes to
        // IEventPublisher.Publish with no boxing and no signature change.
        w.Line("var __factories = " + fieldName + ";");
        w.Line("var executors = new global::SkathIO.Rogue.EventHandlerExecutor[__factories.Length];");
        w.Open("for (int __i = 0; __i < __factories.Length; __i++)");
        w.Line("var __h = __factories[__i]();");
        w.Line("executors[__i] = new global::SkathIO.Rogue.EventHandlerExecutor(__h, (__ev, __ct) => __h.Handle((" + eventTypeFqn + ")__ev, __ct));");
        w.Close();

        // FR-45 / PD-30: one dispatch scope per Publish call. The method is async; on the
        // disabled/unsubscribed path StartDispatch returns null and we await the publisher directly
        // (the async state machine is unavoidable here — Publish is already a ValueTask-returning
        // fan-out). On the enabled path the try/catch/finally observes the aggregate outcome.
        w.Line("var __scope = " + RT + ".StartDispatch<" + eventTypeFqn + ">();");
        w.Open("if (__scope is null)");
        if (returnsUnit)
            w.Line("return await publisher.Publish(executors, ev, cancellationToken).ConfigureAwait(false);");
        else
        {
            w.Line("await publisher.Publish(executors, ev, cancellationToken).ConfigureAwait(false);");
            w.Line("return;");
        }
        w.Close(); // if (__scope is null)

        w.Line("global::System.Exception? __exc = null;");
        w.Open("try");
        if (returnsUnit)
            w.Line("return await publisher.Publish(executors, ev, cancellationToken).ConfigureAwait(false);");
        else
            w.Line("await publisher.Publish(executors, ev, cancellationToken).ConfigureAwait(false);");
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

    private static void EmitPublishOverride(CodeWriter w, Dictionary<string, List<string>> handlersByEvent)
    {
        // ns2.0: returns ValueTask<Unit>
        w.Line("#if NETSTANDARD2_0");
        w.Open("public override global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> Publish(global::SkathIO.Rogue.IEvent ev, global::System.Threading.CancellationToken cancellationToken = default)");
        EmitPublishSwitchBody(w, handlersByEvent, returnsUnit: true);
        w.Close();

        w.Line("#else");
        w.Open("public override global::System.Threading.Tasks.ValueTask Publish(global::SkathIO.Rogue.IEvent ev, global::System.Threading.CancellationToken cancellationToken = default)");
        EmitPublishSwitchBody(w, handlersByEvent, returnsUnit: false);
        w.Close();
        w.Line("#endif");
    }

    private static void EmitPublishSwitchBody(CodeWriter w, Dictionary<string, List<string>> handlersByEvent, bool returnsUnit)
    {
        if (handlersByEvent.Count == 0)
        {
            // FR-13: events may have zero handlers — return completed task
            if (returnsUnit)
                w.Line("return global::SkathIO.Rogue.Unit.Task;");
            else
                w.Line("return default;");
            return;
        }

        w.Open("switch (ev)");

        foreach (var kvp in handlersByEvent)
        {
            string eventFqn   = ToGlobalFqn(kvp.Key);
            string methodName = "Publish_" + MakeSafeName(kvp.Key);

            w.Line("case " + eventFqn + " n:");
            w.Indent();
            w.Line("return " + methodName + "(n, cancellationToken);");
            w.Dedent();
        }

        // Default: no handlers for this event type — return completed (FR-13)
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
    // D3: public concrete-dispatch extension methods (RogueExtensions.g.cs)
    // ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// D3: emits the public <c>RogueExtensions</c> class — one typed extension method per
    /// <c>ICommand&lt;T&gt;</c> / <c>IQuery&lt;T&gt;</c> / void <c>ICommand</c> handler — into a SEPARATE
    /// top-level file (<c>RogueExtensions.g.cs</c>). Each method receives the public
    /// <c>RogueDispatcher</c> base, downcasts it to the generated <c>RogueDispatcherImpl</c> (a static,
    /// AOT-safe cast — no reflection), and calls the matching <c>internal Send_X</c> directly. This is
    /// the 0-alloc concrete fast path (AC-6): it bypasses the <c>ISender</c> interface dispatch, which
    /// boxes one <c>ValueTask&lt;T&gt;</c> by design.
    ///
    /// The downcast is safe because the registration emitter wires <c>RogueDispatcher</c> to
    /// <c>RogueDispatcherImpl</c> as its only concrete implementation; consumers MUST inject
    /// <c>RogueDispatcher</c> (not <c>ISender</c>, which resolves to <c>Mediator</c> and would fail the
    /// cast). Stream handlers are excluded: they live in <c>models.StreamHandlers</c> (a separate
    /// collection, never in <c>models.Handlers</c>) and return <c>IAsyncEnumerable&lt;T&gt;</c> — a
    /// different signature outside this fast-path's scope. Adapter-mapped handlers are also excluded:
    /// their request type does not implement the core CQS markers and dispatches only via SendObject.
    /// </summary>
    internal static string EmitExtensionsClass(EquatableArray<HandlerModel> handlers)
    {
        var w = new CodeWriter();

        // Collect the handlers that get a typed concrete entry point (PD-48: adapter-mapped handlers
        // dispatch only through SendObject — their request type does not implement ICommand<T>/IQuery<T>,
        // so there is no typed Send_X to expose; stream handlers live in a separate model collection and
        // never reach here).
        var emitted = new List<HandlerModel>();
        foreach (var handler in handlers)
            if (!handler.IsAdapterMapped) emitted.Add(handler);

        // Suppress the public type entirely when there are no applicable handlers (mirrors PD-45's
        // module-init suppression). The library's OWN self-compilation ships no handlers, so emitting a
        // `public static class RogueExtensions` there would add an empty, useless public type to the
        // SkathIO.Rogue package surface (and trip the PublicApiAnalyzers RS0016 surface gate). Consumer
        // compilations with handlers emit the real per-handler methods below. The file is still produced
        // (stable hint name) — it just carries no public symbol.
        if (emitted.Count == 0)
        {
            w.Line("// No ICommand<T>/IQuery<T>/void-command handlers in this compilation — no public");
            w.Line("// concrete-dispatch extensions are emitted (see DispatcherEmitter.EmitExtensionsClass).");
            return w.ToString();
        }

        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();

        w.Open("public static class RogueExtensions");

        for (int i = 0; i < emitted.Count; i++)
        {
            if (i > 0) w.Line();
            EmitExtensionMethod(w, emitted[i]);
        }

        w.Close(); // class RogueExtensions

        w.Dedent();
        w.Line("}"); // namespace

        return w.ToString();
    }

    private static void EmitExtensionMethod(CodeWriter w, HandlerModel handler)
    {
        bool isVoid        = handler.ResponseFqn is null;
        string requestFqn  = ToGlobalFqn(handler.RequestFqn);
        string sendMethod  = "Send_" + MakeSafeName(handler.RequestFqn);
        // Method name uses the request type's SIMPLE name (e.g. "PingRequest" → "SendPingRequest"),
        // NOT the namespace-mangled MakeSafeName(RequestFqn) the impl's Send_X uses. The public
        // extension is overload-resolved by its parameter type, so two requests sharing a simple name in
        // different namespaces produce valid OVERLOADS (distinct parameter types) — no collision — while
        // giving callers a clean, predictable name. MakeSafeName is still applied to the simple name to
        // sanitise any generic/nested arity decoration into an identifier.
        string extMethod   = "Send" + MakeSafeName(SimpleName(handler.RequestFqn));

        // Void commands: Send_X returns ValueTask<Unit> on every TFM. We expose it AS ValueTask<Unit>
        // (Unit is already public API) rather than unwrapping to ValueTask, because unwrapping the
        // generic value task to a non-generic one cannot be done without either an allocation (AsTask)
        // or access to the impl's private IgnoreUnit helper — and this is the 0-alloc fast path, so we
        // keep it zero-alloc and let the caller discard the Unit. Typed commands/queries expose the
        // natural ValueTask<TResponse>.
        string returnType = isVoid
            ? "global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit>"
            : "global::System.Threading.Tasks.ValueTask<" + ToGlobalFqn(handler.ResponseFqn!) + ">";

        // CS0051 guard: a `public` method cannot expose a less-accessible parameter (request) or return
        // (response) type. When either is not public, emit the extension `internal` — still valid on the
        // public RogueDispatcher and still callable from within the consumer assembly (where fast-path
        // callers and the benchmark live). The dispatch path itself (Send_X on the internal impl) is
        // unaffected; this only constrains the public-surface entry point.
        bool isPublic =
            handler.RequestAccessibility == TypeAccessibility.Public &&
            handler.ResponseAccessibility == TypeAccessibility.Public;
        string visibility = isPublic ? "public" : "internal";

        if (isVoid)
        {
            w.Line("/// <summary>");
            w.Line("/// 0-alloc concrete dispatch for the void command <c>" + handler.RequestFqn + "</c>.");
            w.Line("/// Returns <c>ValueTask&lt;Unit&gt;</c> (the handler produces no value — discard the");
            w.Line("/// <c>Unit</c>); this keeps the fast path allocation-free.");
            w.Line("/// </summary>");
        }
        else
        {
            w.Line("/// <summary>0-alloc concrete dispatch for <c>" + handler.RequestFqn + "</c> — bypasses the ISender box.</summary>");
        }

        w.Open(
            visibility + " static " + returnType + " " + extMethod + "(" +
            "this global::SkathIO.Rogue.RogueDispatcher dispatcher, " +
            requestFqn + " request, " +
            "global::System.Threading.CancellationToken cancellationToken = default)");

        w.Line(
            "return ((global::SkathIO.Rogue.Generated.RogueDispatcherImpl)dispatcher)." +
            sendMethod + "(request, cancellationToken);");

        w.Close(); // method
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

    /// <summary>
    /// Returns the SIMPLE (unqualified) type name from a fully-qualified name — the segment after the
    /// last namespace/containing-type <c>.</c> that precedes any generic-argument list. E.g.
    /// "MyApp.PingRequest" → "PingRequest"; "MyApp.Page&lt;MyApp.Row&gt;" → "Page&lt;MyApp.Row&gt;"
    /// (the dot inside the type-argument list is NOT a separator). Used to name the D3 public extension
    /// method, which is overload-resolved by parameter type and so does not need the namespace in its
    /// identifier (unlike the impl's Send_X, which uses the full mangled FQN for uniqueness). The result
    /// is passed through <see cref="MakeSafeName"/> by the caller to sanitise any remaining generic
    /// decoration into a valid identifier.
    /// </summary>
    internal static string SimpleName(string fqn)
    {
        int genericStart = fqn.IndexOf('<');
        // Only consider dots BEFORE the generic argument list — a dot inside "<...>" qualifies a type
        // argument, not the type's own name.
        int searchEnd = genericStart < 0 ? fqn.Length : genericStart;
        int lastDot = fqn.LastIndexOf('.', searchEnd - 1);
        return lastDot < 0 ? fqn : fqn.Substring(lastDot + 1);
    }
}
