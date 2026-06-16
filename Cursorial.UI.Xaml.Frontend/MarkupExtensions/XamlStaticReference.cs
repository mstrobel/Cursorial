using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A parse-time placeholder for an <c>{x:Static M}</c> member path the frontend could not fully
/// resolve (the frontend has no field/property reflector — that lives in the loader's metadata
/// provider). The loader folds this to the real field/property value during a deferred resolution
/// step, or the parse provider resolves it eagerly when one is wired. Carrying the path as a typed
/// constant keeps the node graph well-formed without a <c>Cursorial.UI</c> dependency.
/// </summary>
public readonly struct XamlStaticReference : IEquatable<XamlStaticReference>
{
    /// <summary>Creates a static reference from a member path (e.g. <c>Colors.Red</c>).</summary>
    public XamlStaticReference(string memberPath)
        => MemberPath = memberPath ?? throw new ArgumentNullException(nameof(memberPath));

    /// <summary>The <c>Type.Member</c> path as written.</summary>
    public string MemberPath { get; }

    /// <inheritdoc/>
    public bool Equals(XamlStaticReference other) => string.Equals(MemberPath, other.MemberPath, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XamlStaticReference other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => MemberPath.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => $"{{x:Static {MemberPath}}}";
}
