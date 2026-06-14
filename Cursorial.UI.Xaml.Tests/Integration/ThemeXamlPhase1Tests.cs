// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using System;
using System.Text;

using Cursorial.Rendering;
using Cursorial.UI.Testing;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Theme-in-XAML, the authoring path (the dogfood proof, scoped to what the loader proves today): a
/// Type-keyed control theme authored in XAML — <c>&lt;Style x:Key="{x:Type Button}" TargetType="Button"&gt;</c> —
/// resolves its key to <c>typeof(Button)</c> (the control-theme exact key), resolves its
/// <c>TargetType</c>-scoped Setters (the R2 DynamicResource palette references + a <c>{StaticResource}</c>
/// <c>ControlTemplate</c>), and APPLIES as a control theme over the code-first BuiltIn backstop so a Button
/// renders from it.
///
/// The <c>x:Key</c> and <c>TargetType</c> are ORTHOGONAL (unlike WPF's <c>DictionaryKeyProperty(TargetType)</c>
/// collapse): the <c>x:Key</c> is the dictionary entry's Type; <c>TargetType</c> is what binds the Setters
/// (Setter property names are unqualified-against-TargetType — matrix X64/X65; the qualified
/// <c>Owner.Property</c> / attached-property Setter form is a separate, deferred feature — see
/// <c>docs/ui-layer-design/attached-setter-property-investigation.md</c>). Setting only <c>TargetType</c>
/// would implicit-key the entry as the STRING <c>"Style:Button"</c> (a Theme-layer selector style, matrix
/// X137), not the <c>typeof(Button)</c> control theme.
///
/// Byte-for-byte identity to the reflavored (cell-faithful) C# BuiltIn — which needs the nested state-
/// selector authoring (<c>:focus</c>/<c>:pressed</c>/…) reproduced in XAML — is the FULL dogfood deferred
/// to Task #10 (ARCH-1 + Phase 1).
/// </summary>
public sealed class ThemeXamlPhase1Tests
{
    private static readonly Uri Source = new("cursorial://test/theme.xaml");

    // A Button control theme as XAML: a Type-keyed Style (typeof(Button)) carrying TargetType for Setter
    // resolution, the resting Foreground/Background as DynamicResource palette references (the R2 spine),
    // and a Border>ContentPresenter template wired by {StaticResource}. No BorderPen — the cell-faithful
    // model is fill-bounded (design doc §11.8a).
    private const string ButtonThemeXaml =
        "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
        "  <ControlTemplate x:Key=\"ButtonTemplate\">" +
        "    <Border Padding=\"1,0\" Background=\"{TemplateBinding Background}\">" +
        "      <ContentPresenter x:Name=\"PART_ContentPresenter\" RecognizesAccessKey=\"True\"/>" +
        "    </Border>" +
        "  </ControlTemplate>" +
        "  <Style x:Key=\"{x:Type Button}\" TargetType=\"Button\">" +
        "    <Setter Property=\"Foreground\" Value=\"{DynamicResource {x:Static ThemeKeys.TextBrush}}\"/>" +
        "    <Setter Property=\"Background\" Value=\"{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}\"/>" +
        "    <Setter Property=\"Template\"   Value=\"{StaticResource ButtonTemplate}\"/>" +
        "  </Style>" +
        "</ResourceDictionary>";

    [Fact]
    public void XamlAuthoredButtonTheme_ResolvesTypeKey_AndAppliesAsControlTheme()
    {
        var theme = (Cursorial.UI.ResourceDictionary)new XamlLoader().Load(ButtonThemeXaml, Source);

        // The x:Key="{x:Type Button}" resolved to the typeof(Button) control-theme exact key (PRE-TYPEKEY).
        Assert.True(theme.ContainsKey(typeof(UIControls.Button)));

        // …and applying it renders the Button from the XAML-authored template + TargetType-scoped Setters
        // (the {StaticResource} template + the {DynamicResource} palette spine all resolve through the real
        // loader + frame loop).
        var rows = RenderButton(theme);
        Assert.Contains("OK", string.Concat(rows));
        Assert.NotEqual(new string(' ', rows[0].Length), rows[0]); // the content row carries the button
    }

    private static string[] RenderButton(Cursorial.UI.ResourceDictionary theme)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(14, 3) });
        host.Application.Theme = theme;
        host.ShowRoot(new UIControls.Button { Content = "OK" });
        Assert.True(host.RunUntilIdle());

        var rows = new string[3];
        for (var r = 0; r < 3; r++)
        {
            var sb = new StringBuilder(14);
            for (var c = 0; c < 14; c++)
            {
                var g = host.GetCell(c, r).Grapheme;
                sb.Append(string.IsNullOrEmpty(g) ? " " : g);
            }
            rows[r] = sb.ToString();
        }
        return rows;
    }
}
