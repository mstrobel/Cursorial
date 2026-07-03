// netstandard2.0 does not ship System.Runtime.CompilerServices.IsExternalInit, which the C# compiler
// requires to emit `init` accessors and `record` types. This is the standard polyfill; it is
// compiled into this assembly only (internal) and is harmless on runtimes that also define it.

// ReSharper disable CheckNamespace

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    using ComponentModel;

    /// <summary>Reserved for the compiler to support <c>init</c> accessors. Polyfill for netstandard2.0.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
