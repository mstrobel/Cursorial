// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// #22 — prefix-qualified TYPE references (a Style <c>TargetType</c> and an <c>{x:Type}</c> argument) resolve
/// against the prefix's CAPTURED namespace, the same shared <c>ResolveQualifiedType</c> primitive the attached
/// Setter owner uses. The definitive proof is a prefix bound to a CLR namespace OUTSIDE the default xmlns map
/// (the test namespace, reached via <c>clr-namespace:…;assembly=…</c>): the pre-#22 hardcoded-default
/// resolution would miss it (CUR2002 / CUR2110), so resolving here proves the captured namespace — not the
/// default — drove the lookup. (Same-namespace prefix strip is covered at the frontend in X24b/X64e.)
/// </summary>
public sealed class PrefixedTypeRefEndToEndTests
{
    private const string Ns = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";
    private const string TestClrNs =
        " xmlns:t=\"clr-namespace:Cursorial.Tests.UI.Xaml.Integration;assembly=Cursorial.UI.Xaml.Tests\"";
    private static readonly XamlLoader Loader = new();

    [Fact] // {x:Type t:XamlPrefixTarget} — a custom-namespace x:Type argument folds via the captured prefix ns
    public void XTypeArg_PrefixedCustomNamespace_ResolvesCapturedType()
    {
        _ = XamlPrefixTarget.MarkerProperty; // force the type's static ctor (UIProperty registration)

        // DataTemplate.DataType is Type-typed; {x:Type t:XamlPrefixTarget} must fold to typeof(XamlPrefixTarget).
        // XamlPrefixTarget is in the test namespace (NOT the default xmlns map), so only the captured `t:` ns
        // resolves it — the old hardcoded-default x:Type resolution would have been CUR2002.
        var template = Loader.Load<UIControls.DataTemplate>(
            "<DataTemplate" + Ns + TestClrNs + " DataType=\"{x:Type t:XamlPrefixTarget}\"/>");

        Assert.Equal(typeof(XamlPrefixTarget), template.DataType);
    }

    [Fact] // TargetType="t:XamlPrefixTarget" — a custom-namespace Style TargetType resolves + applies its Setter
    public void StyleTargetType_PrefixedCustomNamespace_ResolvesAndAppliesSetter()
    {
        _ = XamlPrefixTarget.MarkerProperty;

        using var host = UIHeadlessHost.Create();

        // The TargetType binds the custom `t:` namespace; the unqualified `Marker` Setter then resolves against
        // that resolved target type. Pre-#22, TargetType="t:XamlPrefixTarget" resolved verbatim against the
        // default ns → null target → the Setter would be CUR2110 at load.
        var style = Loader.Load<Style>(
            "<Style" + Ns + TestClrNs + " TargetType=\"t:XamlPrefixTarget\">" +
              "<Setter Property=\"Marker\" Value=\"42\"/>" +
            "</Style>");
        host.Application.Styles.Add(style);

        var target = new XamlPrefixTarget();
        host.ShowRoot(target);
        Assert.True(host.RunUntilIdle());

        // The typed App-layer style matched the custom type and applied the Setter through the store.
        Assert.Equal(42, target.GetValue(XamlPrefixTarget.MarkerProperty));
    }

    [Fact] // Selector="t|Foo" — the | namespace form (':' is the pseudo-class separator); exact-type match
    public void Selector_QualifiedExactType_MatchesViaCapturedNamespace()
    {
        _ = XamlPrefixTarget.MarkerProperty;

        using var host = UIHeadlessHost.Create();

        // The Selector's `t|XamlPrefixTarget` binds the custom `t` namespace from the document table; the Setter's
        // owner-qualified `t:XamlPrefixTarget.Marker` resolves via the captured ns (the attached/styled member).
        var style = Loader.Load<Style>(
            "<Style" + Ns + TestClrNs + " Selector=\"t|XamlPrefixTarget\">" +
              "<Setter Property=\"t:XamlPrefixTarget.Marker\" Value=\"7\"/>" +
            "</Style>");
        host.Application.Styles.Add(style);

        var panel = new UIControls.StackPanel();
        var baseEl = new XamlPrefixTarget();
        var derived = new XamlPrefixDerived();
        panel.Children.Add(baseEl);
        panel.Children.Add(derived);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(7, baseEl.GetValue(XamlPrefixTarget.MarkerProperty));   // exact CLR-type match
        Assert.Equal(0, derived.GetValue(XamlPrefixTarget.MarkerProperty));  // a derived type is NOT an exact match
    }

    [Fact] // Selector=":is(t|Base)" — the user's case: a namespaced base type resolved through the | form, assignable match
    public void Selector_QualifiedIs_AssignableMatch_IncludesDerived()
    {
        _ = XamlPrefixTarget.MarkerProperty;

        using var host = UIHeadlessHost.Create();

        var style = Loader.Load<Style>(
            "<Style" + Ns + TestClrNs + " Selector=\":is(t|XamlPrefixTarget)\">" +
              "<Setter Property=\"t:XamlPrefixTarget.Marker\" Value=\"9\"/>" +
            "</Style>");
        host.Application.Styles.Add(style);

        var panel = new UIControls.StackPanel();
        var baseEl = new XamlPrefixTarget();
        var derived = new XamlPrefixDerived();
        panel.Children.Add(baseEl);
        panel.Children.Add(derived);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(9, baseEl.GetValue(XamlPrefixTarget.MarkerProperty));
        Assert.Equal(9, derived.GetValue(XamlPrefixTarget.MarkerProperty)); // :is(t|Base) matches the derived type too
    }

    [Fact] // An UNBOUND prefix in a Style TargetType is a loud, positioned error — parity with the prefix|Type selector form
    public void StyleTargetType_UnboundPrefix_IsXamlDiagnostic()
    {
        var ex = Assert.Throws<XamlParseException>(() => Loader.Load<Style>(
            "<Style" + Ns + " TargetType=\"undeclared:Button\"/>"));

        Assert.Equal(XamlDiagnosticCodes.UndeclaredPrefix, ex.Code); // never a silent strip-to-default-namespace
        Assert.True(ex.Line > 0 && ex.Column > 0);
    }

    [Fact] // An unresolvable Style TargetType surfaces as a positioned XAML diagnostic, not a raw SelectorParseException
    public void StyleTargetType_UnknownType_IsXamlDiagnostic_NotRaw()
    {
        var ex = Assert.Throws<XamlParseException>(() => Loader.Load<Style>(
            "<Style" + Ns + " TargetType=\"NoSuchControlType\"/>"));

        Assert.True(ex.Line > 0 && ex.Column > 0); // line+col present (a XamlParseException, not a leaked library exception)
    }

    [Fact] // a BARE (non-{x:Type}) prefixed DataType on a Type-valued member resolves via the captured ns (ConvertText
           // path). Pre-fix it hardcoded the default xmlns → the custom-namespace type was never found.
    public void DataTemplate_BarePrefixedDataType_ResolvesTypeMember()
    {
        _ = XamlPrefixTarget.MarkerProperty;

        var template = Loader.Load<UIControls.DataTemplate>(
            "<DataTemplate" + Ns + TestClrNs + " DataType=\"t:XamlPrefixTarget\"/>");

        Assert.Equal(typeof(XamlPrefixTarget), template.DataType);
    }

    [Fact] // a BARE prefixed DataType on a DataTemplate INSIDE a ResourceDictionary resolves to the implicit
           // DataTemplateKey (TryGetDataType path — the implicit-template lookup the gallery's MVVM nav relies on).
    public void DataTemplate_BarePrefixedDataType_ResolvesImplicitKey()
    {
        _ = XamlPrefixTarget.MarkerProperty;

        var dict = (ResourceDictionary)Loader.Load(
            "<ResourceDictionary" + Ns + TestClrNs + ">" +
              "<DataTemplate DataType=\"t:XamlPrefixTarget\"><TextBlock/></DataTemplate>" +
            "</ResourceDictionary>");

        Assert.True(dict.ContainsKey(new DataTemplateKey(typeof(XamlPrefixTarget))));
    }

    [Fact] // An UNBOUND prefix in an implicit-key DataTemplate DataType is a loud, positioned error — parity with TargetType.
    public void DataTemplate_UnboundPrefixDataType_IsXamlDiagnostic()
    {
        var ex = Assert.Throws<XamlParseException>(() => Loader.Load(
            "<ResourceDictionary" + Ns + ">" +
              "<DataTemplate DataType=\"undeclared:XamlPrefixTarget\"><TextBlock/></DataTemplate>" +
            "</ResourceDictionary>"));

        Assert.Equal(XamlDiagnosticCodes.UndeclaredPrefix, ex.Code);
        Assert.True(ex.Line > 0 && ex.Column > 0);
    }
}

/// <summary>A test-only <see cref="UIElement"/> that lives OUTSIDE the Cursorial.UI namespaces — the probe for
/// prefix-qualified type references (Style <c>TargetType</c> / <c>{x:Type}</c>): it resolves only if the
/// prefix's captured namespace is honored.</summary>
public class XamlPrefixTarget : UIElement
{
    public static readonly StyledProperty<int> MarkerProperty =
        UIProperty.Register<XamlPrefixTarget, int>("Marker");
}

/// <summary>A subtype of <see cref="XamlPrefixTarget"/> — the probe for <c>:is(t|Base)</c> assignable matching
/// vs <c>t|Base</c> exact matching through the namespace-qualified selector form.</summary>
public sealed class XamlPrefixDerived : XamlPrefixTarget;
