// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.UI;
using Cursorial.UI.Testing;
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

        using var host = UITestHost.Create();

        // The TargetType binds the custom `t:` namespace; the unqualified `Marker` Setter then resolves against
        // that resolved target type. Pre-#22, TargetType="t:XamlPrefixTarget" resolved verbatim against the
        // default ns → null target → the Setter would be CUR2110 at load.
        var style = Loader.Load<Cursorial.UI.Style>(
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
}

/// <summary>A test-only <see cref="UIElement"/> that lives OUTSIDE the Cursorial.UI namespaces — the probe for
/// prefix-qualified type references (Style <c>TargetType</c> / <c>{x:Type}</c>): it resolves only if the
/// prefix's captured namespace is honored.</summary>
public sealed class XamlPrefixTarget : UIElement
{
    public static readonly StyledProperty<int> MarkerProperty =
        UIProperty.Register<XamlPrefixTarget, int>("Marker");
}
