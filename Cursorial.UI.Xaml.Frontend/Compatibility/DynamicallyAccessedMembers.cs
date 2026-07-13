// netstandard2.0 does not ship the trim-analysis annotation attributes. ILLink/ILC and the trim
// analyzers match these by FULL NAME (the defining assembly is irrelevant), so this internal
// polyfill lets the frontend annotate [XamlMetadataProvider]'s provider type — the trimmer then
// keeps the advertised provider's Instance field and constructors for the loader's reflection-based
// pull discovery. Compiled into this assembly only (internal); harmless on runtimes that define it.

// ReSharper disable CheckNamespace

#if NETSTANDARD2_0
namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Specifies the member kinds that are dynamically accessed on the annotated type.
    /// Polyfill for netstandard2.0 — values mirror the BCL exactly.</summary>
    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0002 | PublicParameterlessConstructor,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None
    }

    /// <summary>Indicates which members of a <see cref="Type"/> value are dynamically accessed, so the
    /// trimmer preserves them. Polyfill for netstandard2.0.</summary>
    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
        AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method,
        Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
            => MemberTypes = memberTypes;

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }
}
#endif
