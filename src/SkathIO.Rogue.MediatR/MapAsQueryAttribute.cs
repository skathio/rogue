using System;

namespace SkathIO.Rogue.MediatR;

/// <summary>
/// Marks a MediatR-shaped adapter request (<c>SkathIO.Rogue.Compatibility.IRequest&lt;TResponse&gt;</c>)
/// to be mapped onto the CQS dispatcher as a <c>IQuery&lt;TResponse&gt;</c> instead of the default
/// <c>ICommand&lt;TResponse&gt;</c>. Adapter requests map to commands by default, because treating a
/// request that mutates state as a query is the more dangerous mistake.
/// </summary>
/// <remarks>
/// The source generator reads this attribute when it maps adapter handlers onto the CQS dispatcher.
/// <c>[MapAsQuery]</c> on a no-response request is a conflict (a query must return a value) and
/// raises ROGUE012.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MapAsQueryAttribute : Attribute
{
}
