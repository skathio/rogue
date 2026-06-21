#if NETSTANDARD2_0
// Polyfill: enables C# 9+ record types, init-only setters on netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }
}
#endif
