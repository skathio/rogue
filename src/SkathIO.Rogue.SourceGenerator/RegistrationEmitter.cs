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
        const string SCE = "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";

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
        w.Line(SCE + ".AddScoped<global::SkathIO.Rogue.RogueDispatcher, global::SkathIO.Rogue.Generated.RogueDispatcherImpl>(services);");
        w.Line();

        // ── IRoguePipelineInspector ────────────────────────────────────────────────────
        // Singleton: the inspector holds a static dictionary built at generator time.
        w.Line(SCE + ".AddSingleton<global::SkathIO.Rogue.IRoguePipelineInspector, global::SkathIO.Rogue.Generated.RoguePipelineInspector>(services);");
        w.Line();

        // ── INotificationPublisher ─────────────────────────────────────────────────────
        // The publisher strategy comes from RogueOptions (set at runtime).
        w.Line(SCE + ".AddSingleton<global::SkathIO.Rogue.INotificationPublisher>(services, options.NotificationPublisher);");
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

        // ── Notification handlers ──────────────────────────────────────────────────────
        foreach (var nh in models.NotificationHandlers)
        {
            string notifFqn   = DispatcherEmitter.ToGlobalFqn(nh.NotificationFqn);
            string handlerFqn = DispatcherEmitter.ToGlobalFqn(nh.TypeFqn);
            string iface      = "global::SkathIO.Rogue.INotificationHandler<" + notifFqn + ">";
            EmitDescriptor(w, iface, handlerFqn);
        }

        if (models.NotificationHandlers.Count > 0) w.Line();

        // ── Stream handlers (net8+ only — IStreamRequestHandler exists only off ns2.0) ──
        if (models.StreamHandlers.Count > 0)
        {
            w.Line("#if !NETSTANDARD2_0");
            foreach (var sh in models.StreamHandlers)
            {
                string requestFqn = DispatcherEmitter.ToGlobalFqn(sh.RequestFqn);
                string elementFqn = DispatcherEmitter.ToGlobalFqn(sh.ResponseElementFqn);
                string handlerFqn = DispatcherEmitter.ToGlobalFqn(sh.TypeFqn);
                string iface      = "global::SkathIO.Rogue.IStreamRequestHandler<" + requestFqn + ", " + elementFqn + ">";
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
    /// Emits a module initializer (net5+) that wires the DLL's <c>RogueRegistrationBridge.GeneratedRegistrar</c>
    /// delegate to the consumer-compilation's <c>RogueGeneratedRegistration.Register</c>. This is how the
    /// runtime <c>AddRogue</c> in <c>SkathIO.Rogue.dll</c> reaches the registration generated in the
    /// consumer's compilation without a hard cross-assembly type reference (PD-15). On ns2.0 (no
    /// <c>ModuleInitializer</c>), the consumer calls <c>RogueGeneratedRegistration.Register</c> explicitly.
    /// Output: <c>RogueModuleInit.g.cs</c>.
    /// </summary>
    internal static string EmitModuleInit()
    {
        var w = new CodeWriter();
        w.Line("#if !NETSTANDARD2_0");
        w.Line("namespace SkathIO.Rogue.Generated");
        w.Line("{");
        w.Indent();
        w.Open("internal static class RogueModuleInit");
        w.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        w.Open("internal static void Init()");
        w.Line("global::SkathIO.Rogue.RogueRegistrationBridge.GeneratedRegistrar =");
        w.Line("    (svc, opts) => global::SkathIO.Rogue.Generated.RogueGeneratedRegistration.Register(svc, opts);");
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

    /// <summary>
    /// Emits a <c>services.Add(new ServiceDescriptor(serviceType, implType, options.Lifetime))</c>
    /// line so the consumer's <see cref="RogueOptions.Lifetime"/> is honoured for handlers,
    /// behaviors, notification handlers and processors (Fix 3 / FR — RogueOptions.Lifetime).
    /// The dispatcher, inspector and publisher are registered as singletons separately and are
    /// not lifetime-controlled.
    /// </summary>
    private static void EmitDescriptor(CodeWriter w, string serviceTypeFqn, string implTypeFqn)
    {
        w.Line(
            "services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(" +
            "typeof(" + serviceTypeFqn + "), typeof(" + implTypeFqn + "), options.Lifetime));");
    }

    /// <summary>Emits a self-registration (service type == implementation type) honouring lifetime.</summary>
    private static void EmitDescriptor(CodeWriter w, string typeFqn) => EmitDescriptor(w, typeFqn, typeFqn);

    // ────────────────────────────────────────────────────────────────────────────────────
    // Handler registration
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitHandlerRegistration(CodeWriter w, HandlerModel handler)
    {
        bool isVoid       = handler.ResponseFqn is null;
        string requestFqn = DispatcherEmitter.ToGlobalFqn(handler.RequestFqn);
        string handlerFqn = DispatcherEmitter.ToGlobalFqn(handler.TypeFqn);

        if (isVoid)
        {
            // IRequestHandler<TReq> (no-response path)
            string iface = "global::SkathIO.Rogue.IRequestHandler<" + requestFqn + ">";
            EmitDescriptor(w, iface, handlerFqn);
        }
        else
        {
            string responseFqn = DispatcherEmitter.ToGlobalFqn(handler.ResponseFqn!);
            string iface       = "global::SkathIO.Rogue.IRequestHandler<" + requestFqn + ", " + responseFqn + ">";
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
    /// </summary>
    private static void EmitBehaviorListRegistration(
        CodeWriter w, string behaviorIface, string behaviorListType, List<string> applicableBehaviors)
    {
        // Register each behavior type individually (so DI can construct it)
        foreach (var bFqn in applicableBehaviors)
        {
            EmitDescriptor(w, bFqn);
        }

        // Register IReadOnlyList<TBehaviorIface> via an expression-bodied factory. The list itself
        // follows the consumer's configured lifetime (options.Lifetime); its elements are resolved
        // from the container so they keep their own registered lifetime.
        w.Line("services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(");
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
        w.Line("options.Lifetime));");
        w.Dedent();
        w.Line();
    }

    // ────────────────────────────────────────────────────────────────────────────────────
    // Processor registration
    // ────────────────────────────────────────────────────────────────────────────────────

    private static void EmitProcessorRegistration(CodeWriter w, ProcessorModel p)
    {
        string implFqn    = DispatcherEmitter.ToGlobalFqn(p.TypeFqn);
        string requestFqn = DispatcherEmitter.ToGlobalFqn(p.RequestFqn);

        switch (p.Kind)
        {
            case ProcessorKind.Pre:
            {
                string iface = "global::SkathIO.Rogue.IRequestPreProcessor<" + requestFqn + ">";
                EmitDescriptor(w, iface, implFqn);
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
                EmitDescriptor(w, iface, implFqn);
                break;
            }
            case ProcessorKind.ExceptionHandler:
            {
                string responseFqn  = DispatcherEmitter.ToGlobalFqn(p.ResponseFqn ?? "SkathIO.Rogue.Unit");
                string exFqn        = DispatcherEmitter.ToGlobalFqn(p.ExceptionFqn ?? "System.Exception");
                string iface        = "global::SkathIO.Rogue.IRequestExceptionHandler<" + requestFqn + ", " + responseFqn + ", " + exFqn + ">";
                EmitDescriptor(w, iface, implFqn);
                break;
            }
            case ProcessorKind.ExceptionAction:
            {
                string exFqn  = DispatcherEmitter.ToGlobalFqn(p.ExceptionFqn ?? "System.Exception");
                string iface  = "global::SkathIO.Rogue.IRequestExceptionAction<" + requestFqn + ", " + exFqn + ">";
                EmitDescriptor(w, iface, implFqn);
                break;
            }
        }
    }
}
