// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The stable diagnostic-code catalog. The banding is fixed (design doc §4.2 / matrix XD1):
/// <list type="bullet">
/// <item><c>CUR1xxx</c> — parse errors (XML / grammar / intrinsics / whitespace structure).</item>
/// <item><c>CUR2xxx</c> — resolution errors (type / member / setter / forward-reference / namescope).</item>
/// <item><c>CUR3xxx</c> — instantiation errors (activation / duplicate name / template build).</item>
/// </list>
/// X0 (the frontend) emits the parse + resolution codes; the instantiation codes are defined here so
/// the contract is one catalog, but they are produced by the loader (X1+).
/// </summary>
public static class XamlDiagnosticCodes
{
    // ── CUR1xxx — parse ────────────────────────────────────────────────────────────────────────

    /// <summary>A DTD / DOCTYPE / external entity was encountered (DtdProcessing = Prohibit).</summary>
    public const string DtdProhibited = "CUR1001";

    /// <summary>Malformed XML the underlying <c>XmlReader</c> rejected (wraps an <c>XmlException</c>).</summary>
    public const string MalformedXml = "CUR1002";

    /// <summary>A property was set both as an attribute and as a property element.</summary>
    public const string DuplicatePropertyAssignment = "CUR1101";

    /// <summary>An unknown <c>x:</c> intrinsic directive.</summary>
    public const string UnknownIntrinsic = "CUR1201";

    /// <summary><c>x:TypeArguments</c> (generic instantiation) is unsupported in v1.</summary>
    public const string UnsupportedTypeArguments = "CUR1202";

    /// <summary>A recorded-out <c>x:</c> intrinsic (Reference / Array / FieldModifier / Shared / Uid).</summary>
    public const string UnsupportedIntrinsic = "CUR1203";

    /// <summary>An <c>&lt;x:Array&gt;</c> element with no <c>Type</c> attribute (the element type is required).</summary>
    public const string ArrayMissingType = "CUR1204";

    /// <summary>An unterminated markup extension (no closing brace).</summary>
    public const string UnterminatedExtension = "CUR1301";

    /// <summary>A malformed markup-extension argument (e.g. empty name before <c>=</c>).</summary>
    public const string MalformedExtensionArgument = "CUR1302";

    // ── CUR2xxx — resolution ─────────────────────────────────────────────────────────────────────

    /// <summary>A local name resolves to two or more types across the mapped namespaces.</summary>
    public const string AmbiguousType = "CUR2001";

    /// <summary>A type was not found in the resolved namespace (carries a did-you-mean suggestion).</summary>
    public const string TypeNotFound = "CUR2002";

    /// <summary>An undeclared xmlns prefix.</summary>
    public const string UndeclaredPrefix = "CUR2003";

    /// <summary>An xmlns declaration on a non-root element (the top-level-only policy — Avalonia parity).</summary>
    public const string NamespaceNotOnRoot = "CUR2004";

    /// <summary>An <c>mc:Ignorable</c> entry names a prefix with no xmlns declaration (warning).</summary>
    public const string IgnorablePrefixNotDeclared = "CUR2005";

    /// <summary>A design-time (<c>d:</c>) attribute value could not be interpreted (warning; the attribute is ignored).</summary>
    public const string DesignValueInvalid = "CUR2006";

    /// <summary>No member of the given name exists on the target type (carries the member list).</summary>
    public const string MemberNotFound = "CUR2102";

    /// <summary>A type rejects implicit content (no content property).</summary>
    public const string NoContentProperty = "CUR2104";

    /// <summary>A collection member has neither a getter nor a setter.</summary>
    public const string NoCollectionAccess = "CUR2105";

    /// <summary>A <c>Setter</c> has no resolvable target type (no lexical <c>TargetType</c> / selector context).</summary>
    public const string SetterNoTarget = "CUR2110";

    /// <summary>A prefixed attached/owner-qualified <c>Setter.Property</c> owner (e.g. <c>my:Grid.Row</c>) — a v1 deferral (attached-setter Phase 2, see the investigation doc).</summary>
    public const string PrefixedSetterOwnerUnsupported = "CUR2111";

    /// <summary>An <c>{x:Reference Name}</c> named no element registered in the document name scope.</summary>
    public const string ReferenceNotFound = "CUR2112";

    /// <summary>A <c>StaticResource</c> key was not found / is a forward reference.</summary>
    public const string ResourceNotFound = "CUR2103";

    /// <summary><c>{TemplateBinding}</c> used outside a template body.</summary>
    public const string TemplateBindingOutsideTemplate = "CUR2202";

    /// <summary>A <c>{Binding}</c> targets a non-bindable property.</summary>
    public const string BindingTargetNotBindable = "CUR2210";

    /// <summary>An event attribute appears inside deferred (template) content.</summary>
    public const string EventInDeferredContent = "CUR2301";

    /// <summary><c>x:Name</c> inside a resource dictionary (no namescope).</summary>
    public const string NameInResourceDictionary = "CUR2304";

    /// <summary>Two entries in one resource dictionary carry the same <c>x:Key</c> (warning-severity).</summary>
    public const string DuplicateResourceKey = "CUR2305";

    /// <summary>A value failed conversion (e.g. a fractional cell, an unparseable enum/theme key).</summary>
    public const string ConversionFailed = "CUR2401";

    // ── CUR3xxx — instantiation (loader, X1+) ───────────────────────────────────────────────────

    /// <summary>A type has no usable public parameterless constructor.</summary>
    public const string NoParameterlessConstructor = "CUR3001";

    /// <summary>A duplicate <c>x:Name</c> within one namescope.</summary>
    public const string DuplicateName = "CUR3002";

    /// <summary>The document has no root element (valid but empty XML).</summary>
    public const string EmptyDocument = "CUR3003";

    /// <summary>A member assignment threw a runtime fault while building the object (a user setter or
    /// coercion callback faulted) — surfaced only when it must be combined with an EndInit fault.</summary>
    public const string MemberAssignmentFailed = "CUR3004";

    /// <summary>An <c>ISupportInitialize.EndInit()</c> threw while finalizing a bracketed object.</summary>
    public const string InitializationFailed = "CUR3005";
}
