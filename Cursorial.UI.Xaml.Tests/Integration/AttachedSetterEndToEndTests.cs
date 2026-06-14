// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Testing;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Attached / owner-qualified <c>Setter.Property</c> — Phase 1 end-to-end (xaml-matrix X64a/X64c;
/// <c>docs/ui-layer-design/attached-setter-property-investigation.md</c>): a <see cref="Style"/> authored in
/// XAML with a DOTTED Setter property resolves the OWNER (not the lexical <c>TargetType</c>) and applies the
/// real <see cref="UIProperty"/> through the full <c>BuildSetter</c> → <c>StyleRuleFrame</c> →
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
        using var host = UITestHost.Create();

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
}
