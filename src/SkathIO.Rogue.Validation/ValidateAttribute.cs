using System;

namespace SkathIO.Rogue.Validation;

/// <summary>
/// Marks a <c>partial</c> type for validation. The SkathIO.Rogue source generator emits a validator
/// into the type that checks its rule attributes, recurses into nested <c>[Validate]</c> properties and
/// collections, and then calls the type's optional
/// <c>private static void Rules(T x, ref ValidationErrors errors)</c> hook. When the type is a request,
/// its generated dispatch runs that validator, then every <see cref="IAsyncRules{T}"/> registered for it,
/// before the handler, and throws <see cref="RogueValidationException"/> on any error.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ValidateAttribute : Attribute
{
}
