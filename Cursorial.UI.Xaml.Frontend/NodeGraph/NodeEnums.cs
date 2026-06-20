using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Per-<see cref="ObjectRecord"/> flags (proposal §3.1).
/// </summary>
[Flags]
internal enum ObjectFlags : ushort
{
    None = 0,

    /// <summary>The document root object.</summary>
    IsRoot = 1 << 0,

    /// <summary>Carries an <c>x:Name</c> directive.</summary>
    HasName = 1 << 1,

    /// <summary>Carries an <c>x:Key</c> directive.</summary>
    HasKey = 1 << 2,

    /// <summary>The type implements <c>ISupportInitialize</c> (BeginInit/EndInit bracketing).</summary>
    NeedsBeginInit = 1 << 3,

    /// <summary>A get-object: a read-only collection member's existing value, not a fresh activation.</summary>
    IsGetObject = 1 << 4,

    /// <summary>This object lives inside a deferred (template) slice — events here are CUR2301.</summary>
    InDeferredContent = 1 << 5,

    /// <summary>This object lives inside a resource dictionary (no namescope — x:Name is CUR2304).</summary>
    InResourceDictionary = 1 << 6,
}

/// <summary>
/// What a <see cref="MemberRecord"/>'s value is, and which document array <see cref="MemberRecord.ValueIndex"/>
/// indexes into (proposal §3.1).
/// </summary>
internal enum XamlValueKind : byte
{
    /// <summary>A constant folded at parse time → <see cref="XamlDocument.Constants"/>.</summary>
    Folded,

    /// <summary>An unconverted string → <see cref="XamlDocument.Strings"/> (context-dependent convert in stage 2).</summary>
    Text,

    /// <summary>A single child object → <see cref="XamlDocument.Objects"/> (the child's record index).</summary>
    Object,

    /// <summary>A run of child objects (a collection) → an item run; <see cref="MemberRecord.ValueIndex"/> is the first child index, <see cref="MemberRecord.ItemCount"/> the count.</summary>
    Items,

    /// <summary>A markup extension → <see cref="XamlDocument.Extensions"/>.</summary>
    Extension,

    /// <summary>A deferred node-graph slice (template content) → <see cref="XamlDocument.Objects"/> (the slice-head index).</summary>
    Deferred,

    /// <summary>An event handler binding → <see cref="XamlDocument.Strings"/> (the handler name).</summary>
    Event,

    /// <summary>A directive (x:Name/x:Key/x:Class) → <see cref="XamlDocument.Strings"/>.</summary>
    Directive,
}

/// <summary>
/// The built-in markup extensions (a closed enum); user extensions parse as <see cref="Custom"/>
/// carrying an ordinary object subtree (proposal §3.1).
/// </summary>
internal enum ExtensionKind : byte
{
    /// <summary><c>{x:Null}</c> — folded to the null constant at parse.</summary>
    Null,

    /// <summary><c>{x:Static M}</c> — folded to the resolved field/property value at parse.</summary>
    Static,

    /// <summary><c>{x:Type T}</c> — folded to <c>typeof(T)</c> at parse.</summary>
    Type,

    /// <summary><c>{StaticResource Key}</c> — resolved eagerly in stage 2.</summary>
    StaticResource,

    /// <summary><c>{DynamicResource Key}</c> — late; constructs a reference in stage 2.</summary>
    DynamicResource,

    /// <summary><c>{Binding …}</c> — live attach in stage 2.</summary>
    Binding,

    /// <summary><c>{TemplateBinding M}</c> — template-body-restricted; applied at build.</summary>
    TemplateBinding,

    /// <summary>A user <c>MarkupExtension</c> subtype.</summary>
    Custom,

    /// <summary><c>{x:Reference Name}</c> — resolves to the element registered under <c>Name</c> in the document
    /// name scope (a forward reference is resolved after the whole tree is built; payload = the name string).</summary>
    Reference,
}

/// <summary>
/// Which <c>x:</c> intrinsic directive an attribute is. The set is closed; an unknown one is
/// <c>CUR1201</c>, a recorded-out one (TypeArguments/Reference/Array/FieldModifier/Shared/Uid) a
/// specific <c>CUR12xx</c> (matrix X27–X29).
/// </summary>
internal enum XamlDirectiveKind : byte
{
    /// <summary><c>x:Class</c> — the code-behind class name on the root.</summary>
    Class,

    /// <summary><c>x:Name</c> — the namescope registration name.</summary>
    Name,

    /// <summary><c>x:Key</c> — the dictionary key.</summary>
    Key,

    /// <summary><c>x:DataType</c> — recorded for X4 path validation (parsed, not validated at P6).</summary>
    DataType,
}
