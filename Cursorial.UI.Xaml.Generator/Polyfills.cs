// netstandard2.0 polyfills for the C# features the generator (and the source-linked Frontend) use.
// The Roslyn analyzer target framework is netstandard2.0, which lacks these compiler-required types.

namespace System.Runtime.CompilerServices
{
    /// <summary>Enables <c>init</c> accessors and <c>record</c> types on netstandard2.0.</summary>
    internal static class IsExternalInit
    {
    }
}
