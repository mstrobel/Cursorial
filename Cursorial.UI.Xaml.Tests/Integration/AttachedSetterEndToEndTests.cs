// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Attached / owner-qualified <c>Setter.Property</c> — Phase 1 end-to-end (xaml-matrix X64a/X64c;
/// <c>docs/ui-layer-design/attached-setter-property-investigation.md</c>): a <see cref="Cursorial.UI.Style"/>
/// authored in XAML with a DOTTED Setter property resolves the OWNER (not the lexical <c>TargetType</c>)
/// and applies the real <see cref="UIProperty"/> through the full <c>BuildSetter</c> → <c>StyleRuleFrame</c> →
/// <c>AttachedProperty</c> store path — with zero loader change (the gap was purely producing the right
/// member at parse time). Proves an ATTACHED value (<c>Grid.Row</c>) reaches the Grid attached property and
/// an owner-qualified plain value (<c>Control.Foreground</c>) reaches the shared <c>ForegroundProperty</c>,
/// on the real <see cref="UIApplication"/> frame loop.
/// </summary>
public sealed class AttachedSetterEndToEndTests
{
    private const string Ns = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";
    private static readonly XamlLoader Loader = new();

    [Fact] // X64a (attached) + X64c (owner-qualified plain)
    public void DottedSetterProperty_ResolvesOwner_AndAppliesThroughStore()
    {
        using var host = UIHeadlessHost.Create();

        // A TargetType="Button" style whose Setters are an ATTACHED property (Grid.Row) and an owner-
        // qualified plain property (Control.Foreground) — both DOTTED, so they resolve the OWNER (Grid /
        // Control via the default xmlns), not the lexical TargetType (Button). Armed at the App layer.
        var style = Loader.Load<Cursorial.UI.Style>(
            "<Style" + Ns + " TargetType=\"Button\">" +
              "<Setter Property=\"Grid.Row\" Value=\"2\"/>" +
              "<Setter Property=\"Control.Foreground\" Value=\"#ff8800\"/>" +
            "</Style>");
        host.Application.Styles.Add(style);

        var grid = new UIControls.Grid();
        var button = new UIControls.Button { Content = "OK" };
        grid.Children.Add(button);
        host.ShowRoot(grid);
        Assert.True(host.RunUntilIdle());

        // The attached Grid.Row reached the Grid.RowProperty store on the Button (App-layer style; the
        // BuiltIn control theme never sets Grid.Row) — proof the resolved attached UIProperty applies.
        Assert.Equal(2, UIControls.Grid.GetRow(button));

        // The owner-qualified Control.Foreground resolved the shared ForegroundProperty and applied #ff8800
        // (App layer beats the control theme's resting TextBrush).
        var brush = Assert.IsType<SolidColorBrush>(button.Foreground);
        Assert.Equal(Color.FromRgb(0xFF, 0x88, 0x00), brush.Color);
    }

    [Fact] // X64e (Phase 2) — a PREFIXED Setter owner OUTSIDE the Cursorial.UI namespaces resolves via the captured ns
    public void PrefixedSetterOwner_OutsideUiNamespace_ResolvesAndApplies()
    {
        _ = XamlPhase2Attached.MarkerProperty; // force the attached registration (the type's static ctor)

        using var host = UIHeadlessHost.Create();

        // `t:` maps (clr-namespace) to the TEST namespace — outside Cursorial.UI. The Setter owner
        // `t:XamlPhase2Attached.Marker` therefore resolves ONLY if the prefix's namespace, captured at the
        // attribute while the reader was live (Phase 2 / 4C), is honored — the Phase-1 default-namespace
        // resolution would never find XamlPhase2Attached. The prefix is on the Setter VALUE, not the attribute.
        var style = Loader.Load<Cursorial.UI.Style>(
            "<Style" + Ns + " xmlns:t=\"clr-namespace:Cursorial.Tests.UI.Xaml.Integration;assembly=Cursorial.UI.Xaml.Tests\" TargetType=\"Button\">" +
              "<Setter Property=\"t:XamlPhase2Attached.Marker\" Value=\"42\"/>" +
            "</Style>");
        host.Application.Styles.Add(style);

        var button = new UIControls.Button { Content = "OK" };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(42, XamlPhase2Attached.GetMarker(button));
    }
}

/// <summary>A test-only attached property that lives OUTSIDE the Cursorial.UI namespaces — the probe for
/// attached-Setter Phase 2: a prefixed Setter owner resolves it only if the prefix's captured namespace is
/// honored (it is invisible to the default Cursorial.UI resolution).</summary>
public sealed class XamlPhase2Attached
{
    public static readonly AttachedProperty<int> MarkerProperty =
        UIProperty.RegisterAttached<XamlPhase2Attached, UIElement, int>("Marker");

    public static int GetMarker(UIElement element) => element.GetValue(MarkerProperty);
    public static void SetMarker(UIElement element, int value) => element.SetValue(MarkerProperty, value);
}
