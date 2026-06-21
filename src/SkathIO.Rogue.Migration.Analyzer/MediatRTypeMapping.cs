using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkathIO.Rogue.Migration.Analyzer;

/// <summary>
/// Maps the MediatR marker/handler interface names onto the post-D5 SkathIO.Rogue CQS contracts
/// (PD-43/PD-44, FR-13). The migration target is the <b>CQS core</b>
/// (<c>ICommand</c>/<c>IQuery</c>/<c>IEvent</c> + handlers), <b>not</b> the adapter
/// <c>SkathIO.Rogue.Compatibility.IRequest</c> shapes — those are an opt-in transitional aid, not the
/// migration target (PD-32a). Because the migration goes directly to <c>IQuery&lt;T&gt;</c>, the
/// adapter-only <c>[MapAsQuery]</c> attribute is not emitted on this path: query intent is expressed by
/// the contract itself (PD-44 amendment — "migrates directly to <c>IQuery&lt;T&gt;</c>").
/// </summary>
internal static class MediatRTypeMapping
{
    /// <summary>
    /// The MediatR marker/handler interface names this migration rewrites. Streaming
    /// (<c>IStreamRequest</c>/<c>IStreamRequestHandler</c>) is included for completeness even though the
    /// shipped sample does not exercise it; a stream is always read-side, so it maps unambiguously to the
    /// query side (<c>IStreamQuery</c>/<c>IStreamQueryHandler</c>) with no command-vs-query ambiguity.
    /// </summary>
    public static bool IsMediatRInterfaceName(string simpleName) =>
        simpleName == "IRequest" ||
        simpleName == "IRequestHandler" ||
        simpleName == "INotification" ||
        simpleName == "INotificationHandler" ||
        simpleName == "IStreamRequest" ||
        simpleName == "IStreamRequestHandler";

    /// <summary>
    /// The intent a response-bearing MediatR <c>IRequest&lt;T&gt;</c> / <c>IRequestHandler&lt;,&gt;</c>
    /// is migrated under. <see cref="Ambiguous"/> means the name signalled neither read nor write; the
    /// type is migrated to the safe-default command and a manual-review diagnostic (ROGM005) is raised —
    /// never silently mis-mapped as a query (F8 E1, PD-44 amendment).
    /// </summary>
    public enum RequestIntent
    {
        /// <summary>Name signals a read (e.g. ends in/contains "Query"). Maps to <c>IQuery&lt;T&gt;</c>.</summary>
        Query,

        /// <summary>Name signals a write (e.g. ends in/contains "Command"). Maps to <c>ICommand&lt;T&gt;</c>.</summary>
        Command,

        /// <summary>Name signals neither. Maps to the safe default <c>ICommand&lt;T&gt;</c> + ROGM005.</summary>
        Ambiguous,
    }

    /// <summary>
    /// Classifies a response-bearing request by its <b>type name</b> (the request record/class, not the
    /// handler). Naming convention only — a "Query"-suffixed/embedded name reads, a "Command"-suffixed/
    /// embedded name writes; anything else is ambiguous. This is deliberately conservative: it never
    /// guesses "query" from anything other than an explicit read signal, so a true command is never
    /// silently turned into a query (a query that mutates is the dangerous mis-map, per F8).
    /// </summary>
    public static RequestIntent ClassifyByName(string requestTypeName)
    {
        if (requestTypeName is null) throw new ArgumentNullException(nameof(requestTypeName));

        if (ContainsToken(requestTypeName, "Query")) return RequestIntent.Query;
        if (ContainsToken(requestTypeName, "Command")) return RequestIntent.Command;
        return RequestIntent.Ambiguous;
    }

    private static bool ContainsToken(string name, string token) =>
        name.IndexOf(token, StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Resolves the simple type name of an <paramref name="argument"/> that is the request-type argument
    /// of an <c>IRequest&lt;T&gt;</c> base — used by the response-bearing handler rewrite to read the
    /// request type's name (the handler's first type argument) so it maps to the same CQS side as the
    /// request itself. Returns <c>null</c> when the name cannot be read syntactically.
    /// </summary>
    public static string? TryGetSimpleTypeName(TypeSyntax argument)
    {
        switch (argument)
        {
            case IdentifierNameSyntax id:
                return id.Identifier.Text;
            case GenericNameSyntax g:
                return g.Identifier.Text;
            case QualifiedNameSyntax q:
                return TryGetSimpleTypeName(q.Right);
            default:
                return null;
        }
    }
}
