using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkathIO.Rogue.Migration.Analyzer;

/// <summary>
/// Rewrites a MediatR marker/handler base-type reference (flagged by ROGM006) onto the post-D5
/// SkathIO.Rogue CQS contract (PD-43/PD-44, FR-13). The migration goes <b>directly to the CQS core</b>
/// (<c>ICommand</c>/<c>IQuery</c>/<c>IEvent</c> + handlers), not to the adapter <c>IRequest</c> shapes;
/// query intent is therefore expressed by the contract itself and the adapter-only <c>[MapAsQuery]</c>
/// attribute is not emitted on this path (PD-44 amendment — "migrates directly to <c>IQuery&lt;T&gt;</c>").
/// </summary>
/// <remarks>
/// Mapping (F8 convention):
/// <list type="bullet">
/// <item><c>INotification</c> → <c>IEvent</c>; <c>INotificationHandler&lt;T&gt;</c> → <c>IEventHandler&lt;T&gt;</c>.</item>
/// <item><c>IStreamRequest&lt;T&gt;</c> → <c>IStreamQuery&lt;T&gt;</c>; <c>IStreamRequestHandler&lt;,&gt;</c> → <c>IStreamQueryHandler&lt;,&gt;</c> (streams are always read-side — no ambiguity).</item>
/// <item><c>IRequest</c> (no response) → <c>ICommand</c> (void command); <c>IRequestHandler&lt;TReq&gt;</c> → <c>ICommandHandler&lt;TReq&gt;</c>.</item>
/// <item><c>IRequest&lt;T&gt;</c> → <c>IQuery&lt;T&gt;</c> when the request name signals a read; else <c>ICommand&lt;T&gt;</c> (safe default + ROGM005 manual-review when ambiguous).</item>
/// <item><c>IRequestHandler&lt;TReq,TResp&gt;</c> → <c>IQueryHandler&lt;,&gt;</c>/<c>ICommandHandler&lt;,&gt;</c> to match the request type's (TReq) inferred side.</item>
/// </list>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MigrateMediatRMarkerTypeCodeFix))]
[Shared]
public sealed class MigrateMediatRMarkerTypeCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(MigrationDiagnostics.MediatRMarkerType.Id);

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];

        // The diagnostic span is the base-type syntax (e.g. `IRequestHandler<...>`). FindNode may return
        // the enclosing BaseTypeSyntax (a BaseTypeSyntax, NOT a TypeSyntax) rather than the inner
        // TypeSyntax, so walking only up via FirstAncestorOrSelf<TypeSyntax> can miss it. Resolve the
        // MediatR base type at, below, or above the located node.
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var baseType = node.FirstAncestorOrSelf<TypeSyntax>(IsMediatRBaseType)
            ?? node.DescendantNodesAndSelf().OfType<TypeSyntax>().FirstOrDefault(IsMediatRBaseType);
        if (baseType is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Migrate to the SkathIO.Rogue CQS contract",
                createChangedDocument: ct => Rewrite(context.Document, baseType, ct),
                equivalenceKey: "MigrateMediatRMarkerType"),
            diagnostic);
    }

    private static bool IsMediatRBaseType(TypeSyntax type)
    {
        var name = GetSimpleName(type);
        return name is not null && MediatRTypeMapping.IsMediatRInterfaceName(name);
    }

    private static async Task<Document> Rewrite(
        Document document,
        TypeSyntax baseType,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        var replacement = BuildReplacement(baseType);
        if (replacement is null) return document;

        var newRoot = root.ReplaceNode(baseType, replacement.WithTriviaFrom(baseType));
        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Builds the CQS replacement for a single MediatR base-type reference. Preserves any qualifier
    /// (e.g. <c>MediatR.IRequest&lt;T&gt;</c>) by replacing only the rightmost name segment, so the
    /// type arguments and surrounding syntax are carried over verbatim.
    /// </summary>
    private static TypeSyntax? BuildReplacement(TypeSyntax baseType)
    {
        // Unwrap a qualified name (MediatR.IRequest<T>) down to its rightmost segment, then re-wrap.
        if (baseType is QualifiedNameSyntax qualified)
        {
            var innerReplacement = BuildReplacement(qualified.Right);
            return innerReplacement; // drop the `MediatR.` qualifier — the CQS contracts live in SkathIO.Rogue
        }

        var simpleName = GetSimpleName(baseType);
        if (simpleName is null) return null;

        switch (simpleName)
        {
            case "INotification":
                return SyntaxFactory.IdentifierName("IEvent");

            case "INotificationHandler" when baseType is GenericNameSyntax notifHandler:
                return notifHandler.WithIdentifier(SyntaxFactory.Identifier("IEventHandler"));

            case "IStreamRequest" when baseType is GenericNameSyntax streamReq:
                return streamReq.WithIdentifier(SyntaxFactory.Identifier("IStreamQuery"));

            case "IStreamRequestHandler" when baseType is GenericNameSyntax streamHandler:
                return streamHandler.WithIdentifier(SyntaxFactory.Identifier("IStreamQueryHandler"));

            case "IRequest" when baseType is IdentifierNameSyntax:
                // No-response request → void command.
                return SyntaxFactory.IdentifierName("ICommand");

            case "IRequest" when baseType is GenericNameSyntax request:
                // Response-bearing request → IQuery<T> if the request name reads, else the safe ICommand<T>.
                return request.WithIdentifier(
                    SyntaxFactory.Identifier(
                        IsQueryName(GetOwningTypeName(request)) ? "IQuery" : "ICommand"));

            case "IRequestHandler" when baseType is GenericNameSyntax handler:
                return RewriteRequestHandler(handler);

            default:
                return null;
        }
    }

    /// <summary>
    /// <c>IRequestHandler&lt;TReq&gt;</c> → <c>ICommandHandler&lt;TReq&gt;</c> (void command handler);
    /// <c>IRequestHandler&lt;TReq,TResp&gt;</c> → <c>IQueryHandler&lt;,&gt;</c>/<c>ICommandHandler&lt;,&gt;</c>
    /// matching the request type's (the first type argument's) inferred side, so the handler lands on the
    /// same CQS contract as its request.
    /// </summary>
    private static TypeSyntax RewriteRequestHandler(GenericNameSyntax handler)
    {
        var args = handler.TypeArgumentList.Arguments;
        if (args.Count == 1)
        {
            // Void-command handler.
            return handler.WithIdentifier(SyntaxFactory.Identifier("ICommandHandler"));
        }

        // Response-bearing handler: classify by the request type (first type argument) name.
        var requestName = MediatRTypeMapping.TryGetSimpleTypeName(args[0]);
        var isQuery = requestName is not null && IsQueryName(requestName);
        return handler.WithIdentifier(
            SyntaxFactory.Identifier(isQuery ? "IQueryHandler" : "ICommandHandler"));
    }

    /// <summary>
    /// True only when the name carries an explicit read signal ("Query"). A name with neither "Query"
    /// nor "Command" classifies as <see cref="MediatRTypeMapping.RequestIntent.Ambiguous"/>, which here
    /// resolves to the safe default (command) — the ROGM005 diagnostic flags it for manual review. This
    /// is deliberately conservative: a true command is never silently turned into a query.
    /// </summary>
    private static bool IsQueryName(string? name) =>
        name is not null
        && MediatRTypeMapping.ClassifyByName(name) == MediatRTypeMapping.RequestIntent.Query;

    /// <summary>
    /// The name of the type whose base list contains <paramref name="baseType"/> — i.e. the request
    /// record/class itself. Used to classify a response-bearing <c>IRequest&lt;T&gt;</c> by the request's
    /// own name. Returns an empty string when the owning declaration cannot be located (which classifies
    /// as ambiguous → safe-default command).
    /// </summary>
    private static string GetOwningTypeName(SyntaxNode baseType)
    {
        var owner = baseType.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        return owner?.Identifier.Text ?? string.Empty;
    }

    private static string? GetSimpleName(TypeSyntax type)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                return id.Identifier.Text;
            case GenericNameSyntax g:
                return g.Identifier.Text;
            case QualifiedNameSyntax q:
                return GetSimpleName(q.Right);
            default:
                return null;
        }
    }
}
