// netstandard2.0 polyfill for the C# features the generator uses (init accessors / records). The
// referenced Frontend.dll has its own internal copy; this one is internal to the generator assembly.

namespace System.Runtime.CompilerServices
{
    /// <summary>Enables <c>init</c> accessors and <c>record</c> types on netstandard2.0.</summary>
    internal static class IsExternalInit
    {
    }
}
