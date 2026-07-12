using System.Collections.Generic;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Emits the standalone registration class <c>RogueGeneratedRegistration</c> in the
/// <c>SkathIO.Rogue.Generated</c> namespace. Its <c>Register</c> method performs all
/// handler/behavior/processor registration and wires the generated dispatcher subclass
/// (<c>RogueDispatcherImpl</c>) as the <c>RogueDispatcher</c> singleton. This class is ALWAYS
/// emitted (even with an empty body for zero-handler compilations) so that
/// <c>RogueServiceCollectionExtensions.AddRogue</c> in the runtime DLL can always reference it
/// (PD-14 — replaces the cross-assembly <c>partial</c> method). Output:
/// <c>RogueServiceCollectionExtensions.g.cs</c>.
/// </summary>
internal static class RegistrationEmitter
{
    internal static string Emit(DiscoveredModels models, RogueEmitOptions opts)
    {
        var w = new CodeWriter();

        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();

        w.Open("internal static class RogueGeneratedRegistration");

        w.Open("internal static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::SkathIO.Rogue.RogueOptions options)");

        // The generated file carries no `using` directives (ImplicitUsings is disabled and there
        // are no global usings), so every extension-method call is fully qualified through its
        // declaring static class, matching the style used in the dispatcher emitter.
        //
        // PD-38: registration is idempotent. Single-instance services use TryAdd{Scoped,Singleton,Transient}
        // (the generic helpers live on ServiceCollectionDescriptorExtensions in
        // Microsoft.Extensions.DependencyInjection.Extensions, available on every TFM via M.E.DI.Abstractions),
        // and ServiceDescriptor-based registrations use TryAdd / TryAddEnumerable (see EmitDescriptor).
        // Invoking a registrar's Register(...) any number of times therefore has the same effect as once.
        const string SCDE = "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";

        // ── RogueDispatcher ────────────────────────────────────────────────────────────
        // Scoped: the dispatcher captures the IServiceProvider it is constructed with and resolves
        // handlers/behaviors from it on each call. A SINGLETON dispatcher captures the ROOT provider,
        // so a handler with a SCOPED dependency (e.g. a per-request tracker) cannot be resolved —
        // it throws under scope validation ("cannot resolve scoped service from root provider") and,
        // worse, silently becomes a captive dependency when validation is off. Registering the
        // dispatcher as scoped means that when the transient Mediator is resolved from a request
        // scope, the dispatcher it resolves is bound to that same scope, so scoped handler
        // dependencies resolve correctly (required by FR-24 configured lifetimes / FR-35 scoped
        // handlers). The dispatcher itself is otherwise stateless; one instance per scope is cheap.
        // TryAddScoped preserves PD-14's scoped lifetime while making the registration idempotent.
        w.Line(SCDE + ".TryAddScoped<global::SkathIO.Rogue.RogueDispatcher, global::SkathIO.Rogue.Generated.RogueDispatcherImpl>(services);");
        w.Line();

        // ── IRoguePipelineInspector ────────────────────────────────────────────────────
        // Singleton: the inspector holds a static dictionary built at generator time.
        w.Line(SCDE + ".TryAddSingleton<global::SkathIO.Rogue.IRoguePipelineInspector, global::SkathIO.Rogue.Generated.RoguePipelineInspector>(services);");
        w.Line();

        // ── IEventPublisher ────────────────────────────────────────────────────────────
        // The publisher strategy comes from RogueOptions (set at runtime). TryAdd of the instance
        // overload: the first registrar to run wins the publisher (consistent across registrars in a
        // single process, where RogueOptions is the same instance via AddRogue).
        w.Line(SCDE + ".TryAddSingleton<global::SkathIO.Rogue.IEventPublisher>(services, options.EventPublisher);");
        w.Line();

        // ── Request handlers ───────────────────────────────────────────────────────────
        foreach (var handler in models.Handlers)
        {
            EmitHandlerRegistration(w, handler);
        }

        if (models.Handlers.Count > 0) w.Line();

        // ── Pipeline behaviors ─────────────────────────────────────────────────────────
        // Strategy:
        // 1. For each request type, collect all applicable behaviors (open and closed).
        // 2. Register a factory for IReadOnlyList<IPipelineBehavior<TReq,TRes>> that resolves
        //    each applicable behavior and returns them as an array.
        // 3. Also register each behavior type itself so DI can resolve it (for factory pattern).
        EmitBehaviorRegistrations(w, models);

        // ── Event handlers ─────────────────────────────────────────────────────────────
        foreach (var eh in models.EventHandlers)
        {
            string eventFqn   = DispatcherEmitter.ToGlobalFqn(eh.EventFqn);
            string handlerFqn = DispatcherEmitter.ToGlobalFqn(eh.TypeFqn);
            string iface      = "global::SkathIO.Rogue.IEventHandler<" + eventFqn + ">";
            // Multi-registration: Publish fans out to all handlers; dedup by impl type (PD-38).
            // This interface registration is retained for the MediatR-compat path (adapter
            // INotificationHandler<T> is-a IEventHandler<T>) and any GetServices<IEventHandler<T>>
            // consumer.
            EmitEnumerableDescriptor(w, iface, handlerFqn);

            // D1 register/resolve lockstep: the generated dispatcher caches per-event
            // Func<IEventHandler<T>>[] factory delegates whose bodies are
            // GetRequiredService<TConcreteHandler>() (DispatcherEmitter.EmitConstructor), eliminating
            // the per-Publish GetServices<IEventHandler<T>>() enumeration. That resolution needs the
            // concrete handler type to be a registered service key — the interface registration above
            // is keyed by the interface, not the concrete type — so register the handler under its own
            // type too, honouring options.Lifetime (TryAdd is idempotent by the concrete service type,
            // preserving PD-38).
            EmitDescriptor(w, handlerFqn);
        }

        if (models.EventHandlers.Count > 0) w.Line();

        // ── Stream query handlers (net8+ only — IStreamQueryHandler exists only off ns2.0) ──
        if (models.StreamHandlers.Count > 0)
        {
            w.Line("#if !NETSTANDARD2_0");
            foreach (var sh in models.StreamHandlers)
            {
                string requestFqn = DispatcherEmitter.ToGlobalFqn(sh.RequestFqn);
                string elementFqn = DispatcherEmitter.ToGlobalFqn(sh.ResponseElementFqn);
                string handlerFqn = DispatcherEmitter.ToGlobalFqn(sh.TypeFqn);
                string iface      = "global::SkathIO.Rogue.IStreamQueryHandler<" + requestFqn + ", " + elementFqn + ">";
                EmitDescriptor(w, iface, handlerFqn);
            }
            w.Line("#endif");
            w.Line();
        }

        // ── Pre-processors ─────────────────────────────────────────────────────────────
        foreach (var p in models.Processors)
        {
            EmitProcessorRegistration(w, p);
        }

        w.Close(); // Register

        w.Close(); // class RogueGeneratedRegistration

        w.Dedent();
        w.Line("}"); // namespace SkathIO.Rogue.Generated

        return w.ToString();
    }

    /// <summary>
    /// Returns <c>true</c> when the compilation discovered nothing Rogue-registrable — no request
    /// handlers, notification handlers, stream handlers, processors or behaviors. Such a compilation's
    /// <c>RogueGeneratedRegistration.Register</c> would register only the empty dispatcher / inspector /
    /// publisher, contributing nothing useful while actively conflicting with a populated registrar (its
    /// empty <c>RogueDispatcherImpl</c> would shadow the real one under <c>TryAdd</c>'s first-wins — see
    /// <see cref="EmitModuleInit"/>).
    /// </summary>
    private static bool HasNothingToRegister(DiscoveredModels models)
        => models.Handlers.Count == 0
        && models.EventHandlers.Count == 0
        && models.StreamHandlers.Count == 0
        && models.Processors.Count == 0
        && models.Behaviors.Count == 0;

    /// <summary>
    /// Emits a module initializer (net5+) that appends the consumer-compilation's
    /// <c>RogueGeneratedRegistration.Register</c> to the DLL's append-only
    /// <c>RogueRegistrationBridge</c> registry via <c>RogueRegistrationBridge.Register(...)</c>
    /// (PD-33/PD-38 — the non-obsolete entry point, so freshly-generated consumers never trip the
    /// <c>[Obsolete]</c> <c>GeneratedRegistrar</c> warning under <c>TreatWarningsAsErrors</c>). This is
    /// how the runtime <c>AddRogue</c> in <c>SkathIO.Rogue.dll</c> reaches the registration generated in
    /// the consumer's compilation without a hard cross-assembly type reference (PD-15). On ns2.0 (no
    /// <c>ModuleInitializer</c>), the consumer calls <c>RogueGeneratedRegistration.Register</c> explicitly.
    /// <para>
    /// PD-38 amendment (see decisions.md PD-45): the module initializer is suppressed for a compilation
    /// that discovered nothing to register. Under PD-33's append-only registry <em>every</em> registrar
    /// runs, and under PD-38's idempotent <c>TryAddScoped&lt;RogueDispatcher, RogueDispatcherImpl&gt;</c>
    /// the <em>first</em> dispatcher registration wins. <c>SkathIO.Rogue.dll</c> itself runs the generator
    /// over its own (handler-less) source and would otherwise emit a module initializer that appends an
    /// <em>empty</em> registrar — present in <em>every</em> real consumer's process — whose empty
    /// <c>RogueDispatcherImpl</c> could win the race and shadow the consumer's populated dispatcher
    /// ("no handler registered" at dispatch). Suppressing the empty registrar removes that conflict; a
    /// genuinely handler-less consumer still gets a working <c>RogueDispatcher</c> from
    /// <c>AddRogue</c>'s fallback registration. Output: <c>RogueModuleInit.g.cs</c> (empty when suppressed).
    /// </para>
    /// </summary>
    internal static string EmitModuleInit(DiscoveredModels models)
    {
        var w = new CodeWriter();
        w.Line("#if !NETSTANDARD2_0");

        if (HasNothingToRegister(models))
        {
            // Nothing to contribute: do not append an empty registrar (it would only register the empty
            // dispatcher/inspector/publisher and could shadow a populated registrar — PD-45).
            w.Line("#endif");
            return w.ToString();
        }

        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();
        w.Open("internal static class RogueModuleInit");
        w.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        w.Open("internal static void Init()");
        w.Line("global::SkathIO.Rogue.RogueRegistrationBridge.Register(");
        w.Line("    (svc, opts) => global::SkathIO.Rogue.Generated.RogueGeneratedRegistration.Register(svc, opts));");
        w.Close(); // Init
        w.Close(); // RogueModuleInit
        w.Dedent();
        w.Line("}");
        w.Line("#endif");
        return w.ToString();
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Lifetime-aware registration helper
    // ────────────────────────────────────────────────────────────────────────────────────

    private const string SCDE_FQN =
        "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";

    /// <summary>
    /// D3 carve-out: the behavior self-registrations and the <c>IReadOnlyList&lt;IPipelineBehavior&lt;,&gt;&gt;</c>
    /// factory in <see cref="EmitBehaviorListRegistration"/> are always Transient, regardless of
    /// <see cref="RogueOptions.Lifetime"/> — see that method's remarks for the rationale.
    /// </summary>
    private const string TransientLifetimeFqn =
        "global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient";

    /// <summary>
    /// Emits an idempotent single-registration (PD-38): <c>ServiceCollectionDescriptorExtensions.TryAdd(
    /// services, new ServiceDescriptor(serviceType, implType, options.Lifetime))</c>. <c>TryAdd</c>
    /// no-ops if the <em>service type</em> is already registered, so re-invoking the registrar (a
    /// double-<c>AddRogue()</c> or a duplicate append) does not double-register. The consumer's
    /// <see cref="RogueOptions.Lifetime"/> is honoured (Fix 3 / FR — RogueOptions.Lifetime). Used for
    /// kinds the dispatcher resolves via singular <c>GetService&lt;&gt;</c> — exactly one implementation
    /// per service type is correct: request handlers (ROGUE002 forbids duplicates) and stream handlers.
    /// Multi-registration kinds the dispatcher resolves via <c>GetServices&lt;&gt;</c> (notification
    /// handlers and all four processor kinds) use <see cref="EmitEnumerableDescriptor"/> instead (PD-45).
    /// D3 carve-out: behavior self-registration and the <c>IReadOnlyList&lt;IPipelineBehavior&lt;,&gt;&gt;</c>
    /// factory do NOT go through this helper for their lifetime — see
    /// <see cref="EmitBehaviorListRegistration"/>, which hard-codes <see cref="TransientLifetimeFqn"/>
    /// instead, so a Singleton-configured handler lifetime can never make a behavior a captive
    /// dependency over a Scoped service (e.g. a FluentValidation <c>IValidator&lt;T&gt;</c>).
    /// </summary>
    private static void EmitDescriptor(CodeWriter w, string serviceTypeFqn, string implTypeFqn)
    {
        w.Line(
            SCDE_FQN + ".TryAdd(services, new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(" +
            "typeof(" + serviceTypeFqn + "), typeof(" + implTypeFqn + "), options.Lifetime));");
    }

    /// <summary>Emits a self-registration (service type == implementation type) honouring lifetime.</summary>
    private static void EmitDescriptor(CodeWriter w, string typeFqn) => EmitDescriptor(w, typeFqn, typeFqn);

    /// <summary>
    /// Emits an idempotent multi-registration (PD-38/PD-45): <c>ServiceCollectionDescriptorExtensions.TryAddEnumerable(
    /// services, ServiceDescriptor.Describe(serviceType, implType, options.Lifetime))</c>. Used for every
    /// kind the dispatcher resolves via <c>GetServices&lt;&gt;</c> and fans out across: notification
    /// handlers (<c>Publish</c> calls all <c>INotificationHandler&lt;T&gt;</c>) and all four processor
    /// kinds (pre/post processors and exception handlers/actions — each runs every registered impl).
    /// Plain <c>TryAdd</c> would be wrong here (it keeps only the first and silently drops the rest).
    /// <c>TryAddEnumerable</c> dedups by <em>implementation type</em>, so re-invoking the registrar does
    /// not add the same impl twice, while two <em>distinct</em> impls for the same closed interface both
    /// survive.
    /// </summary>
    private static void EmitEnumerableDescriptor(CodeWriter w, string serviceTypeFqn, string implTypeFqn)
    {
        w.Line(
            SCDE_FQN + ".TryAddEnumerable(services, global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Describe(" +
            "typeof(" + serviceTypeFqn + "), typeof(" + implTypeFqn + "), options.Lifetime));");
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Handler registration
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitHandlerRegistration(CodeWriter w, HandlerModel handler)
    {
        bool isVoid       = handler.ResponseFqn is null;
        string requestFqn = DispatcherEmitter.ToGlobalFqn(handler.RequestFqn);
        string handlerFqn = DispatcherEmitter.ToGlobalFqn(handler.TypeFqn);

        // Register/resolve lockstep (PD-43/PD-48): the service interface here must match the one the
        // dispatcher resolves with GetRequiredService (DispatcherEmitter.EmitSendMethod /
        // AdapterHandlerIface). For MediatR-adapter-mapped handlers (PD-48) that is the adapter's OWN
        // Compatibility.IRequestHandler<TReq,TResp> / IRequestHandler<TReq> — the interface the type
        // implements — NOT the core ICommandHandler/IQueryHandler (the adapter does not implement those).
        if (handler.IsAdapterMapped)
        {
            string iface = isVoid
                ? "global::SkathIO.Rogue.Compatibility.IRequestHandler<" + requestFqn + ">"
                : "global::SkathIO.Rogue.Compatibility.IRequestHandler<" + requestFqn + ", " + DispatcherEmitter.ToGlobalFqn(handler.ResponseFqn!) + ">";
            EmitDescriptor(w, iface, handlerFqn);
            return;
        }

        if (isVoid)
        {
            // Void command (ICommandHandler<TCommand>) — only a command can be void.
            string iface = "global::SkathIO.Rogue.ICommandHandler<" + requestFqn + ">";
            EmitDescriptor(w, iface, handlerFqn);
        }
        else
        {
            string responseFqn = DispatcherEmitter.ToGlobalFqn(handler.ResponseFqn!);
            string ifaceName   = handler.Kind == HandlerKind.Query ? "IQueryHandler" : "ICommandHandler";
            string iface       = "global::SkathIO.Rogue." + ifaceName + "<" + requestFqn + ", " + responseFqn + ">";
            EmitDescriptor(w, iface, handlerFqn);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Behavior registration
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitBehaviorRegistrations(CodeWriter w, DiscoveredModels models)
    {
        if (models.Behaviors.Count == 0)
            return;

        // Ordering model (PD-13a): sort the discovered behavior list once, deterministically:
        //   1. [BehaviorOrder(int)] ascending (lower = outermost, executes first; default 0).
        //   2. Tie-break: source-discovered before metadata-discovered.
        //   3. Secondary tie-break: FQN lexicographic order.
        // The sort is applied to the merged (already FQN-deduplicated) behavior list so every
        // per-request/per-stream projection below inherits the same global order. ROGUE007 is NOT
        // implemented in v1 (PD-17) — the runtime options.BehaviorRegistrations list is read only
        // to feed the same sort key, not to validate against the discovery results.
        var ordered = new List<BehaviorModel>(models.Behaviors.Count);
        ordered.AddRange(models.Behaviors);
        ordered.Sort(CompareBehaviorOrder);

        // ── Non-stream IPipelineBehavior<,> registrations (per request handler) ──────────
        if (models.Handlers.Count > 0)
        {
            foreach (var handler in models.Handlers)
            {
                bool isVoid        = handler.ResponseFqn is null;
                string requestFqn  = DispatcherEmitter.ToGlobalFqn(handler.RequestFqn);
                string responseFqn = isVoid ? "global::SkathIO.Rogue.Unit" : DispatcherEmitter.ToGlobalFqn(handler.ResponseFqn!);

                string behaviorIface    = "global::SkathIO.Rogue.IPipelineBehavior<" + requestFqn + ", " + responseFqn + ">";
                string behaviorListType = "global::System.Collections.Generic.IReadOnlyList<" + behaviorIface + ">";

                var applicableBehaviors = CollectApplicableBehaviors(ordered, requestFqn, responseFqn, stream: false);
                if (applicableBehaviors.Count == 0) continue;

                EmitBehaviorListRegistration(w, behaviorIface, behaviorListType, applicableBehaviors);
            }
        }

        // ── Stream IStreamPipelineBehavior<,> registrations (per stream handler, net8+) ──
        if (models.StreamHandlers.Count > 0)
        {
            w.Line("#if !NETSTANDARD2_0");
            foreach (var sh in models.StreamHandlers)
            {
                string requestFqn = DispatcherEmitter.ToGlobalFqn(sh.RequestFqn);
                string elementFqn = DispatcherEmitter.ToGlobalFqn(sh.ResponseElementFqn);

                string behaviorIface    = "global::SkathIO.Rogue.IStreamPipelineBehavior<" + requestFqn + ", " + elementFqn + ">";
                string behaviorListType = "global::System.Collections.Generic.IReadOnlyList<" + behaviorIface + ">";

                var applicableBehaviors = CollectApplicableBehaviors(ordered, requestFqn, elementFqn, stream: true);
                if (applicableBehaviors.Count == 0) continue;

                EmitBehaviorListRegistration(w, behaviorIface, behaviorListType, applicableBehaviors);
            }
            w.Line("#endif");
            w.Line();
        }
    }

    /// <summary>
    /// PD-13a comparison: <c>[BehaviorOrder]</c> ascending, then source-before-metadata, then FQN
    /// lexicographic order for determinism.
    /// </summary>
    internal static int CompareBehaviorOrder(BehaviorModel a, BehaviorModel b)
    {
        int byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0) return byOrder;

        // false (source) sorts before true (metadata)
        int bySource = a.IsMetadata.CompareTo(b.IsMetadata);
        if (bySource != 0) return bySource;

        return string.CompareOrdinal(a.TypeFqn, b.TypeFqn);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="models"/> contains at least one usable open generic
    /// behavior whose stream-ness matches <paramref name="stream"/> (e.g. an open
    /// <c>LoggingBehavior&lt;TReq,TRes&gt;</c> registered as <c>IPipelineBehavior&lt;,&gt;</c> for
    /// <c>stream: false</c>, or an open <c>IStreamPipelineBehavior&lt;,&gt;</c> for <c>stream: true</c>).
    /// An open behavior is closed for — and therefore applies to — EVERY request <em>of its own
    /// stream/non-stream family</em>, so its presence is a compile-time fact that vetoes the D4
    /// behavior-list bypass and the D5 static chain in <see cref="DispatcherEmitter"/>: when such a
    /// behavior exists, no <c>Send_X</c> of that family may skip the per-dispatch
    /// <c>GetService&lt;IReadOnlyList&lt;...&gt;&gt;</c> lookup, because that list is the only place the
    /// open behavior gets woven in. The stream filter is essential: a non-stream <c>Send_X</c> can never
    /// see an open <c>IStreamPipelineBehavior&lt;,&gt;</c> (and vice versa), so cross-family open
    /// behaviors must NOT veto the optimization — doing so over-conservatively disables D5 chains for
    /// every closed-behavior command/query whenever an unrelated open stream behavior exists. Abstract /
    /// no-public-ctor entries are excluded for the same reason <see cref="CollectApplicableBehaviors"/>
    /// skips them — they are never instantiated, so they never apply.
    /// </summary>
    internal static bool HasUsableOpenBehavior(DiscoveredModels models, bool stream)
    {
        foreach (var behavior in models.Behaviors)
        {
            if (behavior.IsAbstract || !behavior.HasPublicCtor) continue;
            if (behavior.IsOpen && behavior.IsStream == stream) return true;
        }
        return false;
    }

    /// <summary>
    /// Orders <paramref name="models"/>' behaviors (PD-13a) and projects them onto a single
    /// request/response pair, returning the closed behavior FQNs that apply. This is the
    /// emitter-facing entry point that <see cref="DispatcherEmitter"/> uses to decide the D4 bypass:
    /// it performs the SAME ordering + matching <see cref="EmitBehaviorRegistrations"/> performs, so
    /// the dispatcher's "are there zero applicable behaviors?" decision and the registration's
    /// "should I emit an IReadOnlyList factory?" decision are computed from one shared implementation
    /// (a divergence would silently skip behaviors or emit a dead lookup). Re-ordering per call is
    /// cheap relative to codegen and avoids leaking the sorted-list lifetime across the two emitters.
    /// </summary>
    internal static List<string> CollectApplicableBehaviorsFor(
        DiscoveredModels models, string requestFqn, string responseFqn, bool stream)
    {
        var ordered = new List<BehaviorModel>(models.Behaviors.Count);
        ordered.AddRange(models.Behaviors);
        ordered.Sort(CompareBehaviorOrder);
        return CollectApplicableBehaviors(ordered, requestFqn, responseFqn, stream);
    }

    /// <summary>
    /// Projects the already-sorted behavior list onto a single request/response (or
    /// request/element) pair, producing the closed behavior FQNs that apply. Open behaviors are
    /// closed for the pair; closed behaviors are included only on an exact type-argument match.
    /// </summary>
    private static List<string> CollectApplicableBehaviors(
        List<BehaviorModel> ordered, string requestFqn, string responseFqn, bool stream)
    {
        var applicable = new List<string>();

        foreach (var behavior in ordered)
        {
            if (behavior.IsAbstract || !behavior.HasPublicCtor) continue;
            if (behavior.IsStream != stream) continue;

            if (behavior.IsOpen)
            {
                // Open generic: close it for this request/response pair, e.g.
                // global::MyApp.LoggingBehavior<global::MyApp.GetUser, global::MyApp.UserDto>
                string closedBehaviorFqn = DispatcherEmitter.ToGlobalFqn(behavior.UnboundTypeFqn) + "<" + requestFqn + ", " + responseFqn + ">";
                applicable.Add(closedBehaviorFqn);
            }
            else
            {
                if (behavior.ClosedRequestFqn is null || behavior.ClosedResponseFqn is null) continue;

                string closedReqFqn = DispatcherEmitter.ToGlobalFqn(behavior.ClosedRequestFqn);
                string closedResFqn = DispatcherEmitter.ToGlobalFqn(behavior.ClosedResponseFqn);

                if (closedReqFqn == requestFqn && closedResFqn == responseFqn)
                    applicable.Add(DispatcherEmitter.ToGlobalFqn(behavior.TypeFqn));
            }
        }

        return applicable;
    }

    /// <summary>
    /// Emits the per-pair self-registration of each behavior type plus the
    /// <c>IReadOnlyList&lt;TBehaviorIface&gt;</c> factory the dispatcher resolves at dispatch time.
    /// D3: both the self-registrations below and the list factory are pinned to
    /// <see cref="TransientLifetimeFqn"/>, decoupled from <see cref="RogueOptions.Lifetime"/> — a
    /// Singleton-configured handler lifetime must never make a behavior a captive dependency over a
    /// Scoped service (e.g. a FluentValidation <c>IValidator&lt;T&gt;</c>). Handler (and event-handler,
    /// stream-query-handler) self-registrations are unaffected and continue to honour
    /// <c>options.Lifetime</c> via <see cref="EmitDescriptor(CodeWriter, string)"/>.
    /// </summary>
    private static void EmitBehaviorListRegistration(
        CodeWriter w, string behaviorIface, string behaviorListType, List<string> applicableBehaviors)
    {
        // Register each behavior type individually (so DI can construct it). Inline (not via
        // EmitDescriptor) because the lifetime here is hard-coded Transient (D3), not
        // options.Lifetime — EmitDescriptor's shared helper must stay untouched for its other five
        // call sites (handlers, event-handler self-reg, stream-query-handler self-reg).
        foreach (var bFqn in applicableBehaviors)
        {
            w.Line(
                SCDE_FQN + ".TryAdd(services, new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(" +
                "typeof(" + bFqn + "), typeof(" + bFqn + "), " + TransientLifetimeFqn + "));");
        }

        // Register IReadOnlyList<TBehaviorIface> via an expression-bodied factory. The list itself is
        // also pinned Transient (D3, same rationale as the self-registrations above); its elements
        // are resolved from the container so they keep their own registered lifetime. TryAdd (PD-38)
        // makes the factory registration idempotent — re-invoking the registrar does not add a second
        // factory for the same IReadOnlyList<TBehaviorIface> service type (which would duplicate the
        // pipeline).
        w.Line(SCDE_FQN + ".TryAdd(services, new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(");
        w.Indent();
        w.Line("typeof(" + behaviorListType + "),");
        w.Line("sp => new " + behaviorIface + "[]");
        w.Line("{");
        w.Indent();
        foreach (var bFqn in applicableBehaviors)
        {
            // Fully qualified through the declaring static class — the generated file carries no
            // `using` directives, so the GetRequiredService extension must not be called as an
            // instance-style extension method.
            w.Line("global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<" + bFqn + ">(sp),");
        }
        w.Dedent();
        w.Line("},");
        // Closes: ServiceDescriptor(...) ')', then TryAdd(services, ...) ')', plus the trailing ';'.
        w.Line(TransientLifetimeFqn + "));");
        w.Dedent();
        w.Line();
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Processor registration
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitProcessorRegistration(CodeWriter w, ProcessorModel p)
    {
        // PD-45: ALL four processor kinds are multi-registration (fan-out). The dispatcher resolves
        // each via GetServices<...> (DispatcherEmitter lines ~364/366/491/513) and runs EVERY registered
        // implementation — multiple pre/post processors, and multiple exception handlers/actions for the
        // same (request[, response], exception) are all expected to fire. So they route through
        // TryAddEnumerable (dedup by impl type) — NOT plain TryAdd, which would keep only the first and
        // silently drop the rest. (PD-38 mis-grouped exception-handler/-action under single-TryAdd; the
        // GetServices resolution makes them enumerable, same category as notification handlers.)
        string implFqn    = DispatcherEmitter.ToGlobalFqn(p.TypeFqn);
        string requestFqn = DispatcherEmitter.ToGlobalFqn(p.RequestFqn);

        switch (p.Kind)
        {
            case ProcessorKind.Pre:
            {
                string iface = "global::SkathIO.Rogue.IRequestPreProcessor<" + requestFqn + ">";
                EmitEnumerableDescriptor(w, iface, implFqn);
                break;
            }
            case ProcessorKind.Post:
            {
                // Use ToGlobalFqn (not a bare "global::" concat) so keyword-aliased response types
                // such as `string` become `global::System.String`, not the invalid `global::string`
                // (which fails the consumer compile with "Identifier expected"). Surfaced by the
                // pass-2 compile-verification tests (review 2026-06-07).
                string responseFqn = DispatcherEmitter.ToGlobalFqn(p.ResponseFqn ?? "SkathIO.Rogue.Unit");
                string iface       = "global::SkathIO.Rogue.IRequestPostProcessor<" + requestFqn + ", " + responseFqn + ">";
                EmitEnumerableDescriptor(w, iface, implFqn);
                break;
            }
            case ProcessorKind.ExceptionHandler:
            {
                string responseFqn  = DispatcherEmitter.ToGlobalFqn(p.ResponseFqn ?? "SkathIO.Rogue.Unit");
                string exFqn        = DispatcherEmitter.ToGlobalFqn(p.ExceptionFqn ?? "System.Exception");
                string iface        = "global::SkathIO.Rogue.IRequestExceptionHandler<" + requestFqn + ", " + responseFqn + ", " + exFqn + ">";
                EmitEnumerableDescriptor(w, iface, implFqn);
                break;
            }
            case ProcessorKind.ExceptionAction:
            {
                string exFqn  = DispatcherEmitter.ToGlobalFqn(p.ExceptionFqn ?? "System.Exception");
                string iface  = "global::SkathIO.Rogue.IRequestExceptionAction<" + requestFqn + ", " + exFqn + ">";
                EmitEnumerableDescriptor(w, iface, implFqn);
                break;
            }
        }
    }
}
