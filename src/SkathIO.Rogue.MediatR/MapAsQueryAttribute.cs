using System;

namespace SkathIO.Rogue.MediatR;

/// <summary>
/// Marks a MediatR-shaped adapter request (<c>SkathIO.Rogue.Compatibility.IRequest&lt;TResponse&gt;</c>)
/// to be mapped onto the CQS dispatcher as a <c>IQuery&lt;TResponse&gt;</c> instead of the default
/// <c>ICommand&lt;TResponse&gt;</c> (PD-43 amendment, F8 convention).
/// </summary>
/// <remarks>
/// The generator does not act on this attribute until 11.4 (the adapter build-out wires the
/// adapter-mapping discovery rule, <c>[MapAsQuery]</c> handling, and ROGUE012). The type exists and is
/// public from 11.2 so the 10.3 <c>PublicAPI.Shipped</c> freeze captures it. <c>[MapAsQuery]</c> on a
/// no-response request is a conflict (a query must return a value) and raises ROGUE012 in 11.4.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MapAsQueryAttribute : Attribute
{
}
