#if NETSTANDARD2_0
// Enables init-only setters when compiling for netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
