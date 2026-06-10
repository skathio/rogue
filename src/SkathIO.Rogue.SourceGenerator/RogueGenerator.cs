using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SkathIO.Rogue.SourceGenerator;

[Generator]
public sealed class RogueGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Stage 1: cheap syntax filter — any type declaration with a non-empty base list
        // Stage 2: semantic transform — extract model records (no ISymbol/Compilation survives)
        IncrementalValuesProvider<DiscoveredItem?> candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsTypeDeclWithBaseList(node),
                transform: static (ctx, ct) => ExtractModel(ctx, ct))
            .Where(static m => m is not null);

        // Collect all candidate models into a single stable array
        IncrementalValueProvider<ImmutableArray<DiscoveredItem?>> allModels = candidates.Collect();

        // ── Metadata behavior scan (PD-17) ──────────────────────────────────────────────
        // Discover IPipelineBehavior<,> / IStreamPipelineBehavior<,> implementors in DIRECTLY
        // referenced assemblies. Uses MetadataReferencesProvider (NOT CompilationProvider) so this
        // stage re-fires only when the reference set changes, not on every source keystroke.
        //
        // v1 Known Limitation (PD-17 "Transitive assembly scan"): only directly-referenced
        // assemblies are walked. Behaviors in transitively-referenced assemblies are not discovered.
        IncrementalValuesProvider<BehaviorModel?> metadataBehaviors = context.MetadataReferencesProvider
            .Combine(context.CompilationProvider)
            .SelectMany(static (pair, ct) =>
            {
                (MetadataReference reference, Compilation compilation) = pair;
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm)
                    return ImmutableArray<BehaviorModel?>.Empty;

                ImmutableArray<BehaviorModel?>.Builder results =
                    ImmutableArray.CreateBuilder<BehaviorModel?>();
                WalkNamespaceForBehaviors(asm.GlobalNamespace, results, ct);
                return results.ToImmutable();
            })
            .Where(static b => b is not null);

        IncrementalValueProvider<ImmutableArray<BehaviorModel?>> allMetadataBehaviors =
            metadataBehaviors.Collect();

        // Stage 3: combine source-discovered items and metadata-discovered behaviors into a single
        // DiscoveredModels value (pure transformation, no Roslyn objects survive past this point).
        IncrementalValueProvider<DiscoveredModels> discovered = allModels
            .Combine(allMetadataBehaviors)
            .Select(static (pair, _) => BuildDiscoveredModels(pair.Left, pair.Right));

        // Phase 3.2: real diagnostic emission.
        context.RegisterSourceOutput(discovered, static (spc, models) => EmitDiagnostics(spc, models));

        // Phase 4.1: source emission — dispatcher, service collection extensions, pipeline inspector.
        context.RegisterSourceOutput(discovered, static (spc, models) =>
        {
            var emitOpts = new RogueEmitOptions();
            string fileHeader = CodeWriter.Header;

            spc.AddSource("RogueDispatcher.g.cs",
                SourceText.From(fileHeader + DispatcherEmitter.Emit(models, emitOpts), Encoding.UTF8));

            spc.AddSource("RogueServiceCollectionExtensions.g.cs",
                SourceText.From(fileHeader + RegistrationEmitter.Emit(models, emitOpts), Encoding.UTF8));

            spc.AddSource("RoguePipelineInspector.g.cs",
                SourceText.From(fileHeader + InspectorEmitter.Emit(models, emitOpts), Encoding.UTF8));

            spc.AddSource("RogueModuleInit.g.cs",
                SourceText.From(fileHeader + RegistrationEmitter.EmitModuleInit(), Encoding.UTF8));
        });
    }

    // ─── Syntax predicate ───────────────────────────────────────────────────────────

    internal static bool IsTypeDeclWithBaseList(SyntaxNode node)
        => node is TypeDeclarationSyntax { BaseList: { Types.Count: > 0 } };

    // ─── Semantic transform ─────────────────────────────────────────────────────────

    internal static DiscoveredItem? ExtractModel(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not TypeDeclarationSyntax typeDecl)
            return null;

        if (ctx.SemanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol)
            return null;

        return ExtractFromSymbol(typeSymbol, ct);
    }

    /// <summary>
    /// Matches a concrete type symbol against the known Rogue interfaces and produces the
    /// corresponding model. Shared by the incremental transform and the test seam so neither
    /// has to reconstruct a <see cref="GeneratorSyntaxContext"/>.
    /// </summary>
    internal static DiscoveredItem? ExtractFromSymbol(INamedTypeSymbol typeSymbol, CancellationToken ct)
    {
        // Only concrete types (classes/structs/records, not interfaces or abstract)
        // Abstract types are still collected here; ROGUE005 in Phase 3.2 flags them.
        if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return null;

        // Walk implemented interfaces and match against known Rogue interface metadata names
        foreach (INamedTypeSymbol iface in typeSymbol.AllInterfaces)
        {
            ct.ThrowIfCancellationRequested();

            // Build the fully-qualified metadata name: "Namespace.TypeName`arity"
            string ifaceName = GetMetadataFqn(iface.OriginalDefinition);

            // ── Request handlers ──────────────────────────────────────────────────
            if (ifaceName is WellKnownTypeNames.IRequestHandler2
                          or WellKnownTypeNames.ICommandHandler2
                          or WellKnownTypeNames.IQueryHandler2)
            {
                if (iface.TypeArguments.Length == 2)
                {
                    return new DiscoveredItem.Handler(new HandlerModel(
                        TypeFqn: GetFqn(typeSymbol),
                        RequestFqn: GetFqn(iface.TypeArguments[0]),
                        ResponseFqn: GetFqn(iface.TypeArguments[1]),
                        Accessibility: GetAccessibility(typeSymbol),
                        CtorArgTypeFqns: GetCtorArgTypes(typeSymbol),
                        IsAbstract: typeSymbol.IsAbstract,
                        HasPublicCtor: HasPublicConstructor(typeSymbol)));
                }
            }
            else if (ifaceName is WellKnownTypeNames.IRequestHandler1
                               or WellKnownTypeNames.ICommandHandler1)
            {
                if (iface.TypeArguments.Length == 1)
                {
                    return new DiscoveredItem.Handler(new HandlerModel(
                        TypeFqn: GetFqn(typeSymbol),
                        RequestFqn: GetFqn(iface.TypeArguments[0]),
                        ResponseFqn: null,
                        Accessibility: GetAccessibility(typeSymbol),
                        CtorArgTypeFqns: GetCtorArgTypes(typeSymbol),
                        IsAbstract: typeSymbol.IsAbstract,
                        HasPublicCtor: HasPublicConstructor(typeSymbol)));
                }
            }
            // ── Notification handler ──────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.INotificationHandler1)
            {
                if (iface.TypeArguments.Length == 1)
                {
                    return new DiscoveredItem.NotificationHandler(new NotificationHandlerModel(
                        TypeFqn: GetFqn(typeSymbol),
                        NotificationFqn: GetFqn(iface.TypeArguments[0]),
                        Accessibility: GetAccessibility(typeSymbol)));
                }
            }
            // ── Streaming handler ─────────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.IStreamRequestHandler2)
            {
                if (iface.TypeArguments.Length == 2)
                {
                    return new DiscoveredItem.StreamHandler(new StreamHandlerModel(
                        TypeFqn: GetFqn(typeSymbol),
                        RequestFqn: GetFqn(iface.TypeArguments[0]),
                        ResponseElementFqn: GetFqn(iface.TypeArguments[1]),
                        Accessibility: GetAccessibility(typeSymbol)));
                }
            }
            // ── Pipeline behavior ─────────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.IPipelineBehavior2
                  && iface.TypeArguments.Length == 2)
            {
                bool isOpen = typeSymbol.TypeParameters.Length > 0;
                string? closedReqFqn = isOpen ? null : GetFqn(iface.TypeArguments[0]);
                string? closedResFqn = isOpen ? null : GetFqn(iface.TypeArguments[1]);
                return new DiscoveredItem.Behavior(new BehaviorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    IsOpen: isOpen,
                    IsStream: false,
                    IsAbstract: typeSymbol.IsAbstract,
                    HasPublicCtor: HasPublicConstructor(typeSymbol),
                    UnboundTypeFqn: GetUnboundFqn(typeSymbol),
                    ClosedRequestFqn: closedReqFqn,
                    ClosedResponseFqn: closedResFqn,
                    Order: GetBehaviorOrder(typeSymbol),
                    IsMetadata: false));
            }
            else if (ifaceName == WellKnownTypeNames.IStreamPipelineBehavior2
                  && iface.TypeArguments.Length == 2)
            {
                bool isOpen = typeSymbol.TypeParameters.Length > 0;
                string? closedReqFqn = isOpen ? null : GetFqn(iface.TypeArguments[0]);
                string? closedResFqn = isOpen ? null : GetFqn(iface.TypeArguments[1]);
                return new DiscoveredItem.Behavior(new BehaviorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    IsOpen: isOpen,
                    IsStream: true,
                    IsAbstract: typeSymbol.IsAbstract,
                    HasPublicCtor: HasPublicConstructor(typeSymbol),
                    UnboundTypeFqn: GetUnboundFqn(typeSymbol),
                    ClosedRequestFqn: closedReqFqn,
                    ClosedResponseFqn: closedResFqn,
                    Order: GetBehaviorOrder(typeSymbol),
                    IsMetadata: false));
            }
            // ── Pre-processor ─────────────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.IRequestPreProcessor1
                  && iface.TypeArguments.Length == 1)
            {
                return new DiscoveredItem.Processor(new ProcessorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    Kind: ProcessorKind.Pre,
                    RequestFqn: GetFqn(iface.TypeArguments[0]),
                    ResponseFqn: null,
                    ExceptionFqn: null,
                    Accessibility: GetAccessibility(typeSymbol)));
            }
            // ── Post-processor ────────────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.IRequestPostProcessor2
                  && iface.TypeArguments.Length == 2)
            {
                return new DiscoveredItem.Processor(new ProcessorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    Kind: ProcessorKind.Post,
                    RequestFqn: GetFqn(iface.TypeArguments[0]),
                    ResponseFqn: GetFqn(iface.TypeArguments[1]),
                    ExceptionFqn: null,
                    Accessibility: GetAccessibility(typeSymbol)));
            }
            // ── Exception handler ─────────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.IRequestExceptionHandler3
                  && iface.TypeArguments.Length == 3)
            {
                return new DiscoveredItem.Processor(new ProcessorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    Kind: ProcessorKind.ExceptionHandler,
                    RequestFqn: GetFqn(iface.TypeArguments[0]),
                    ResponseFqn: GetFqn(iface.TypeArguments[1]),
                    ExceptionFqn: GetFqn(iface.TypeArguments[2]),
                    Accessibility: GetAccessibility(typeSymbol)));
            }
            // ── Exception action ──────────────────────────────────────────────────
            else if (ifaceName == WellKnownTypeNames.IRequestExceptionAction2
                  && iface.TypeArguments.Length == 2)
            {
                return new DiscoveredItem.Processor(new ProcessorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    Kind: ProcessorKind.ExceptionAction,
                    RequestFqn: GetFqn(iface.TypeArguments[0]),
                    ResponseFqn: null,
                    ExceptionFqn: GetFqn(iface.TypeArguments[1]),
                    Accessibility: GetAccessibility(typeSymbol)));
            }
        }

        // ── Request message types (for ROGUE001 / ROGUE006 cross-check) ──────────────
        // If none of the handler/behavior/processor branches matched, check whether this
        // type IS a request message. Handler types implement IRequestHandler<TReq,TRes>,
        // not IBaseRequest — so this check does not double-count handlers.
        //
        // Scan all interfaces first to identify the most-specific match:
        // IRequest<T> (with response) > IBaseRequest (no response) > INotification.
        // A single pass over AllInterfaces collects all flags, then we decide at the end.
        bool isNotification = false;
        bool isBaseRequest = false;
        bool isBaseStreamRequest = false;
        string? msgResponseFqn = null;
        string? streamResponseFqn = null;

        foreach (INamedTypeSymbol msgIface in typeSymbol.AllInterfaces)
        {
            ct.ThrowIfCancellationRequested();
            string msgName = GetMetadataFqn(msgIface.OriginalDefinition);

            if (msgName == WellKnownTypeNames.IRequest2 && msgIface.TypeArguments.Length == 1)
            {
                // IRequest<TResponse> — most specific; capture and stop scanning for request ifaces
                msgResponseFqn = GetFqn(msgIface.TypeArguments[0]);
                isBaseRequest = true;
                break;
            }

            if (msgName == WellKnownTypeNames.IBaseRequest)
                isBaseRequest = true;

            if (msgName == WellKnownTypeNames.INotification)
                isNotification = true;

            // IStreamRequest<T> — most specific streaming form; prefer over IBaseStreamRequest
            if (msgName == WellKnownTypeNames.IStreamRequest1 && msgIface.TypeArguments.Length == 1)
                streamResponseFqn = GetFqn(msgIface.TypeArguments[0]);

            if (msgName == WellKnownTypeNames.IBaseStreamRequest)
                isBaseStreamRequest = true;
        }

        // Streaming request — return early before the IBaseRequest/INotification path
        if (isBaseStreamRequest || streamResponseFqn is not null)
        {
            return new DiscoveredItem.RequestMessage(new RequestMessageModel(
                TypeFqn: GetFqn(typeSymbol),
                ResponseFqn: streamResponseFqn,       // null for bare IBaseStreamRequest
                IsOpenGeneric: typeSymbol.TypeParameters.Length > 0,
                IsNotification: false,
                IsStream: true));
        }

        if (isBaseRequest || isNotification)
        {
            return new DiscoveredItem.RequestMessage(new RequestMessageModel(
                TypeFqn: GetFqn(typeSymbol),
                ResponseFqn: msgResponseFqn,
                IsOpenGeneric: typeSymbol.TypeParameters.Length > 0,
                IsNotification: isNotification,
                IsStream: false));
        }

        return null;
    }

    // ─── Metadata behavior scan (PD-17) ─────────────────────────────────────────────

    /// <summary>
    /// Recursively walks a namespace symbol collecting <see cref="BehaviorModel"/>s for any
    /// <c>IPipelineBehavior&lt;,&gt;</c> / <c>IStreamPipelineBehavior&lt;,&gt;</c> implementors.
    /// <see cref="INamespaceSymbol.GetTypeMembers"/> returns only types at this exact namespace
    /// level, so a recursive descent through <see cref="INamespaceSymbol.GetNamespaceMembers"/> is
    /// required to reach types in child namespaces.
    /// </summary>
    private static void WalkNamespaceForBehaviors(
        INamespaceSymbol ns,
        ImmutableArray<BehaviorModel?>.Builder results,
        CancellationToken ct)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            BehaviorModel? model = ExtractBehaviorFromMetadataSymbol(type, ct);
            if (model is not null)
                results.Add(model);
        }

        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
        {
            ct.ThrowIfCancellationRequested();
            WalkNamespaceForBehaviors(child, results, ct);
        }
    }

    /// <summary>
    /// Produces a metadata-sourced (<see cref="BehaviorModel.IsMetadata"/> == true)
    /// <see cref="BehaviorModel"/> if the given metadata type symbol is a concrete pipeline/stream
    /// behavior, otherwise null. Shape-identical to the source-discovered behavior branch in
    /// <see cref="ExtractFromSymbol"/>.
    /// </summary>
    private static BehaviorModel? ExtractBehaviorFromMetadataSymbol(INamedTypeSymbol typeSymbol, CancellationToken ct)
    {
        if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return null;

        foreach (INamedTypeSymbol iface in typeSymbol.AllInterfaces)
        {
            ct.ThrowIfCancellationRequested();
            string ifaceName = GetMetadataFqn(iface.OriginalDefinition);

            bool isPipeline = ifaceName == WellKnownTypeNames.IPipelineBehavior2;
            bool isStream   = ifaceName == WellKnownTypeNames.IStreamPipelineBehavior2;
            if ((!isPipeline && !isStream) || iface.TypeArguments.Length != 2)
                continue;

            bool isOpen = typeSymbol.TypeParameters.Length > 0;
            string? closedReqFqn = isOpen ? null : GetFqn(iface.TypeArguments[0]);
            string? closedResFqn = isOpen ? null : GetFqn(iface.TypeArguments[1]);

            return new BehaviorModel(
                TypeFqn: GetFqn(typeSymbol),
                IsOpen: isOpen,
                IsStream: isStream,
                IsAbstract: typeSymbol.IsAbstract,
                HasPublicCtor: HasPublicConstructor(typeSymbol),
                UnboundTypeFqn: GetUnboundFqn(typeSymbol),
                ClosedRequestFqn: closedReqFqn,
                ClosedResponseFqn: closedResFqn,
                Order: GetBehaviorOrder(typeSymbol),
                IsMetadata: true);
        }

        return null;
    }

    // ─── Stage 3: build combined model ──────────────────────────────────────────────

    internal static DiscoveredModels BuildDiscoveredModels(ImmutableArray<DiscoveredItem?> items)
        => BuildDiscoveredModels(items, ImmutableArray<BehaviorModel?>.Empty);

    /// <summary>
    /// Builds the combined model from source-discovered items and metadata-discovered behaviors.
    /// Source-discovered behaviors are added first; a metadata-discovered behavior is added only
    /// if its FQN was not already seen (PD-17 deduplication: a behavior present in both the
    /// project's own source and a referenced assembly is counted once, source winning).
    /// </summary>
    internal static DiscoveredModels BuildDiscoveredModels(
        ImmutableArray<DiscoveredItem?> items,
        ImmutableArray<BehaviorModel?> metadataBehaviors)
    {
        ImmutableArray<HandlerModel>.Builder handlers =
            ImmutableArray.CreateBuilder<HandlerModel>();
        ImmutableArray<BehaviorModel>.Builder behaviors =
            ImmutableArray.CreateBuilder<BehaviorModel>();
        HashSet<string> seenBehaviorFqns = new HashSet<string>(System.StringComparer.Ordinal);
        ImmutableArray<NotificationHandlerModel>.Builder notificationHandlers =
            ImmutableArray.CreateBuilder<NotificationHandlerModel>();
        ImmutableArray<ProcessorModel>.Builder processors =
            ImmutableArray.CreateBuilder<ProcessorModel>();
        ImmutableArray<StreamHandlerModel>.Builder streamHandlers =
            ImmutableArray.CreateBuilder<StreamHandlerModel>();
        ImmutableArray<RequestMessageModel>.Builder requestMessages =
            ImmutableArray.CreateBuilder<RequestMessageModel>();

        foreach (DiscoveredItem? item in items)
        {
            switch (item)
            {
                case DiscoveredItem.Handler h:             handlers.Add(h.Model);             break;
                case DiscoveredItem.Behavior b:
                    if (seenBehaviorFqns.Add(b.Model.TypeFqn)) behaviors.Add(b.Model);
                    break;
                case DiscoveredItem.NotificationHandler n: notificationHandlers.Add(n.Model); break;
                case DiscoveredItem.Processor p:           processors.Add(p.Model);           break;
                case DiscoveredItem.StreamHandler s:       streamHandlers.Add(s.Model);       break;
                case DiscoveredItem.RequestMessage r:      requestMessages.Add(r.Model);      break;
            }
        }

        // Metadata-discovered behaviors second: added only if their FQN is new (source wins).
        foreach (BehaviorModel? mb in metadataBehaviors)
        {
            if (mb is not null && seenBehaviorFqns.Add(mb.TypeFqn))
                behaviors.Add(mb);
        }

        return new DiscoveredModels(
            EquatableArray<HandlerModel>.From(handlers.ToImmutable()),
            EquatableArray<BehaviorModel>.From(behaviors.ToImmutable()),
            EquatableArray<NotificationHandlerModel>.From(notificationHandlers.ToImmutable()),
            EquatableArray<ProcessorModel>.From(processors.ToImmutable()),
            EquatableArray<StreamHandlerModel>.From(streamHandlers.ToImmutable()),
            EquatableArray<RequestMessageModel>.From(requestMessages.ToImmutable()));
    }

    /// <summary>
    /// For testing: runs the discovery pipeline against an existing compilation.
    /// Must mirror Initialize()'s Stage 1 predicate and Stage 2 transform — keep in sync if those change.
    /// </summary>
    internal static DiscoveredModels ExtractFromCompilation(
        Compilation compilation,
        CancellationToken ct = default)
    {
        ImmutableArray<DiscoveredItem?>.Builder items =
            ImmutableArray.CreateBuilder<DiscoveredItem?>();

        foreach (SyntaxTree syntaxTree in compilation.SyntaxTrees)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
            SyntaxNode root = syntaxTree.GetRoot(ct);

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (!IsTypeDeclWithBaseList(node)) continue;
                if (node is not TypeDeclarationSyntax typeDecl) continue;
                if (semanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol) continue;

                items.Add(ExtractFromSymbol(typeSymbol, ct));
            }
        }

        return BuildDiscoveredModels(items.ToImmutable());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the fully-qualified metadata name for a named type symbol's original definition,
    /// in the form "Namespace.TypeName`arity" (matching <see cref="WellKnownTypeNames"/> constants).
    /// </summary>
    private static string GetMetadataFqn(INamedTypeSymbol symbol)
    {
        // Walk the containing-type chain — nested types use '+' in metadata names
        // (e.g. "Namespace.Outer+Inner`1"), not '.'. Dropping this chain (as the prior
        // namespace-only build did) collapses "Outer.LoggingBehavior`2" to "LoggingBehavior`2",
        // which can collide with or fail to match a well-known interface's metadata FQN.
        var parts = new List<string>();
        for (INamedTypeSymbol? current = symbol; current is not null; current = current.ContainingType)
            parts.Add(current.MetadataName);
        parts.Reverse();

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        string nameChain = string.Join("+", parts);
        return ns.Length == 0 ? nameChain : ns + "." + nameChain;
    }

    /// <summary>Returns the fully-qualified display name without the <c>global::</c> prefix.</summary>
    private static string GetFqn(ITypeSymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                 .Replace("global::", string.Empty);

    /// <summary>
    /// Returns the unbound (no type-parameter names) fully-qualified name for a type symbol.
    /// E.g., for <c>MyApp.LoggingBehavior&lt;TReq, TRes&gt;</c> returns <c>"MyApp.LoggingBehavior"</c>.
    /// For non-generic types, returns the same as <see cref="GetFqn(ITypeSymbol)"/>.
    /// Used to emit closed generic constructions in the source generator.
    /// </summary>
    private static string GetUnboundFqn(INamedTypeSymbol symbol)
    {
        if (symbol.TypeParameters.Length == 0 && symbol.ContainingType is null)
            return GetFqn(symbol);

        // Walk the containing-type chain, dropping each level's <T1, T2, ...> suffix — so a
        // nested open-generic behavior like "Outer.LoggingBehavior<TReq, TRes>" collapses to
        // "Namespace.Outer.LoggingBehavior" (nested types are referenced with '.' in C# source,
        // matching SymbolDisplayFormat.FullyQualifiedFormat's display, unlike metadata names'
        // '+'). The prior namespace-only build dropped "Outer" entirely, emitting
        // "global::Namespace.LoggingBehavior<...>" — a CS0234 in the consumer build.
        var parts = new List<string>();
        for (INamedTypeSymbol? current = symbol; current is not null; current = current.ContainingType)
            parts.Add(current.Name);
        parts.Reverse();

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        string nameChain = string.Join(".", parts);
        return ns.Length == 0 ? nameChain : ns + "." + nameChain;
    }

    private static TypeAccessibility GetAccessibility(INamedTypeSymbol symbol) =>
        symbol.DeclaredAccessibility switch
        {
            Accessibility.Public   => TypeAccessibility.Public,
            Accessibility.Internal => TypeAccessibility.Internal,
            _                      => TypeAccessibility.Other,
        };

    private static EquatableArray<string> GetCtorArgTypes(INamedTypeSymbol symbol)
    {
        // Pick the public constructor with the most parameters (DI convention)
        IMethodSymbol? ctor = null;
        int maxParams = -1;
        foreach (IMethodSymbol c in symbol.InstanceConstructors)
        {
            if (c.DeclaredAccessibility == Accessibility.Public
                && c.Parameters.Length > maxParams)
            {
                ctor = c;
                maxParams = c.Parameters.Length;
            }
        }

        if (ctor is null)
            return EquatableArray<string>.Empty;

        ImmutableArray<string>.Builder builder =
            ImmutableArray.CreateBuilder<string>(ctor.Parameters.Length);
        foreach (IParameterSymbol param in ctor.Parameters)
            builder.Add(GetFqn(param.Type));
        return EquatableArray<string>.From(builder.ToImmutable());
    }

    private static bool HasPublicConstructor(INamedTypeSymbol symbol)
    {
        foreach (IMethodSymbol ctor in symbol.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility == Accessibility.Public)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the <c>[BehaviorOrder(int)]</c> value from a behavior type symbol (PD-4). Returns 0
    /// when the attribute is absent or malformed. Works for both source-declared symbols and
    /// referenced-assembly metadata symbols (both expose <see cref="ISymbol.GetAttributes"/>).
    /// </summary>
    private static int GetBehaviorOrder(INamedTypeSymbol symbol)
    {
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass is null) continue;
            if (GetMetadataFqn(attr.AttributeClass) != WellKnownTypeNames.BehaviorOrderAttribute)
                continue;

            if (attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is int order)
            {
                return order;
            }
            return 0;
        }
        return 0;
    }

    // ─── Diagnostic emission (Phase 3.2) ────────────────────────────────────────────

    private static void EmitDiagnostics(SourceProductionContext spc, DiscoveredModels models)
    {
        // ROGUE002 — duplicate handlers for the same request type.
        // ROGUE001 — request message type with no handler (handledRequestFqns below).
        //
        // R7 one-pass optimization: build both the ROGUE002 grouping (handlersByRequest) and the
        // ROGUE001 handled-FQN set (handledRequestFqns) in a single pass over models.Handlers,
        // rather than two separate loops. (Stream handlers contribute only to handledRequestFqns
        // and are folded in just below.)
        Dictionary<string, List<string>> handlersByRequest =
            new Dictionary<string, List<string>>();
        HashSet<string> handledRequestFqns = new HashSet<string>();

        foreach (HandlerModel handler in models.Handlers)
        {
            List<string>? list;
            if (!handlersByRequest.TryGetValue(handler.RequestFqn, out list))
            {
                list = new List<string>();
                handlersByRequest[handler.RequestFqn] = list;
            }
            list.Add(handler.TypeFqn);

            handledRequestFqns.Add(handler.RequestFqn);
        }

        foreach (KeyValuePair<string, List<string>> kvp in handlersByRequest)
        {
            if (kvp.Value.Count > 1)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateHandler,
                    Location.None,
                    ShortName(kvp.Key),
                    string.Join(", ", System.Linq.Enumerable.Select(kvp.Value, ShortName))));
            }
        }

        // ROGUE001 — request message type with no handler.
        //
        // ROGUE001 fires only for request types defined in the current compilation (PD-10).
        // Request types defined in REFERENCED ASSEMBLIES are invisible to the generator's syntax
        // provider — CreateSyntaxProvider only walks the current project's source syntax trees, so
        // those types never become RequestMessageModels and never appear in models.RequestMessages.
        //
        // Consequence for multi-project solutions (FR-33): the composition-root project — which
        // references the library and defines the handlers — sees the handler (in its own source)
        // and the request type (via metadata, NOT source) and therefore does NOT false-positive,
        // because the request type isn't in models.RequestMessages there. The only place ROGUE001
        // can fire is a project that BOTH declares a request type in its own source AND has no
        // handler for it in that same compilation. That is the intended contract: a request whose
        // handler is genuinely absent from the compilation that owns the request. A library that
        // intentionally ships request contracts with no co-located handlers should declare them
        // and let the consumer handle them — the consumer compilation is where dispatch is wired,
        // so ROGUE001 there is correct. No additional cross-project suppression code is required;
        // the syntax-tree scope is the implicit boundary (PD-10 follow-up closed in progress.md).
        //
        // handledRequestFqns is seeded above from models.Handlers in the same pass that builds the
        // ROGUE002 grouping (R7); stream handlers are folded in here.
        foreach (StreamHandlerModel sh in models.StreamHandlers)
            handledRequestFqns.Add(sh.RequestFqn);

        foreach (RequestMessageModel msg in models.RequestMessages)
        {
            if (msg.IsNotification) continue;   // FR-13: notifications may have zero handlers
            if (msg.IsOpenGeneric) continue;     // ROGUE006 handles open-generic requests
            if (!handledRequestFqns.Contains(msg.TypeFqn))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.NoHandler,
                    Location.None,
                    ShortName(msg.TypeFqn)));
            }
        }

        // ROGUE003 — handler response type mismatch
        // Build a lookup: RequestFqn → ResponseFqn declared by the message type
        Dictionary<string, string?> requestResponseByFqn = new Dictionary<string, string?>();
        foreach (RequestMessageModel msg in models.RequestMessages)
        {
            if (!msg.IsNotification && !requestResponseByFqn.ContainsKey(msg.TypeFqn))
                requestResponseByFqn[msg.TypeFqn] = msg.ResponseFqn;
        }

        foreach (HandlerModel handler in models.Handlers)
        {
            string? expectedResponse;
            if (!requestResponseByFqn.TryGetValue(handler.RequestFqn, out expectedResponse))
                continue; // request type not in this compilation (multi-project); skip

            // Only check when both sides declare a response type (non-void path)
            if (expectedResponse is null || handler.ResponseFqn is null)
                continue;

            if (!string.Equals(expectedResponse, handler.ResponseFqn, System.StringComparison.Ordinal))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ResponseTypeMismatch,
                    Location.None,
                    ShortName(handler.TypeFqn),
                    ShortName(handler.ResponseFqn),
                    ShortName(handler.RequestFqn),
                    ShortName(expectedResponse)));
            }
        }

        // ROGUE004 — unconstructable dependency (best-effort ctor-arg check)
        // Build the set of all types the generator knows are registered
        HashSet<string> registeredTypeFqns = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (HandlerModel h in models.Handlers) registeredTypeFqns.Add(h.TypeFqn);
        foreach (BehaviorModel b in models.Behaviors) registeredTypeFqns.Add(b.TypeFqn);
        foreach (NotificationHandlerModel n in models.NotificationHandlers) registeredTypeFqns.Add(n.TypeFqn);
        foreach (ProcessorModel p in models.Processors) registeredTypeFqns.Add(p.TypeFqn);
        foreach (StreamHandlerModel s in models.StreamHandlers) registeredTypeFqns.Add(s.TypeFqn);

        foreach (HandlerModel handler in models.Handlers)
        {
            foreach (string argTypeFqn in handler.CtorArgTypeFqns)
            {
                // Skip well-known framework namespaces — they're registered externally
                if (IsWellKnownFrameworkType(argTypeFqn)) continue;
                if (registeredTypeFqns.Contains(argTypeFqn)) continue;

                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.UnconstructableType,
                    Location.None,
                    ShortName(handler.TypeFqn),
                    ShortName(argTypeFqn),
                    argTypeFqn));
            }
        }

        // ROGUE005 — abstract handler or no public constructor
        foreach (HandlerModel handler in models.Handlers)
        {
            if (handler.IsAbstract || !handler.HasPublicCtor)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AbstractOrNoUsableCtor,
                    Location.None,
                    ShortName(handler.TypeFqn)));
            }
        }

        foreach (BehaviorModel behavior in models.Behaviors)
        {
            if (behavior.IsAbstract || !behavior.HasPublicCtor)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AbstractOrNoUsableCtor,
                    Location.None,
                    ShortName(behavior.TypeFqn)));
            }
        }

        // ROGUE006 — open-generic request type
        foreach (RequestMessageModel msg in models.RequestMessages)
        {
            if (msg.IsOpenGeneric)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.OpenGenericRequest,
                    Location.None,
                    ShortName(msg.TypeFqn)));
            }
        }

        // ROGUE010 — IMediator injection nudge
        // Disabled by default (isEnabledByDefault: false). Detection of constructor
        // injection sites is a Phase 4 / separate analyzer concern — descriptor registered here.
    }

    /// <summary>Returns the simple (unqualified) name from a dot-separated FQN.</summary>
    private static string ShortName(string fqn)
    {
        int dot = fqn.LastIndexOf('.');
        return dot >= 0 ? fqn.Substring(dot + 1) : fqn;
    }

    /// <summary>
    /// Returns true for types in well-known framework namespaces that are registered externally
    /// and should not trigger ROGUE004 (unconstructable dependency).
    /// </summary>
    private static bool IsWellKnownFrameworkType(string fqn)
    {
        return fqn.StartsWith("System.", System.StringComparison.Ordinal)
            || fqn.StartsWith("Microsoft.", System.StringComparison.Ordinal)
            || fqn.StartsWith("SkathIO.", System.StringComparison.Ordinal)
            || fqn.StartsWith("FluentValidation.", System.StringComparison.Ordinal);
    }
}
