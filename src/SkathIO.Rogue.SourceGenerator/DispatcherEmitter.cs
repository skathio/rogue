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
            EmitSendMethod(w, handler);
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

    private static void EmitSendMethod(CodeWriter w, HandlerModel handler)
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

        w.Open(
            "private global::System.Threading.Tasks.ValueTask<" + responseFqn + "> " +
            methodName + "(" + requestFqn + " request, global::System.Threading.CancellationToken cancellationToken)");

        // Resolve handler
        w.Line("var handler = " + SP + ".GetRequiredService<" + handlerIface + ">(" + SVC + ");");

        // Resolve behavior list (IReadOnlyList registered by RegistrationEmitter, or Array.Empty)
        w.Line("var behaviors = " + SP + ".GetService<" + behaviorListType + ">(" + SVC + ")");
        w.Line("    ?? ((" + behaviorListType + ")global::System.Array.Empty<" + behaviorIface + ">());");

        // Execute pipeline
        if (isVoid)
        {
            // IRequestHandler<TReq>.Handle returns ValueTask<Unit> on ns2.0, ValueTask on net8+.
            // PipelineExecutor always works with ValueTask<TResponse>, so we wrap the void case.
            w.Line("#if NETSTANDARD2_0");
            w.Line("return global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", global::SkathIO.Rogue.Unit>(");
            w.Line("    request, behaviors, () => handler.Handle(request, cancellationToken), cancellationToken);");
            w.Line("#else");
            w.Line("return global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", global::SkathIO.Rogue.Unit>(");
            w.Line("    request, behaviors,");
            w.Line("    () =>");
            w.Line("    {");
            w.Line("        var voidVt = handler.Handle(request, cancellationToken);");
            w.Line("        if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;");
            w.Line("        return AwaitVoidThenUnit(voidVt);");
            w.Line("    },");
            w.Line("    cancellationToken);");
            w.Line("#endif");
        }
        else
        {
            w.Line("return global::SkathIO.Rogue.PipelineExecutor.Execute<" + requestFqn + ", " + responseFqn + ">(");
            w.Line("    request, behaviors, () => handler.Handle(request, cancellationToken), cancellationToken);");
        }

        w.Close(); // method
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

        w.Line("return next();");

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

        // ns2.0: ValueTask<Unit>; net8+: ValueTask
        w.Line("#if NETSTANDARD2_0");
        w.Open("private global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> " + methodName + "(" + notifType + " notification, global::System.Threading.CancellationToken cancellationToken)");
        EmitPublishNotifBody(w, notifType, handlerFqns, returnsUnit: true);
        w.Close();
        w.Line("#else");
        w.Open("private global::System.Threading.Tasks.ValueTask " + methodName + "(" + notifType + " notification, global::System.Threading.CancellationToken cancellationToken)");
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

        w.Line("return publisher.Publish(executors, notification, cancellationToken);");
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
