// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using System;
using System.Text;

using Cursorial.Rendering;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Theme-in-XAML, the authoring path (the dogfood proof): a Type-keyed control theme authored in XAML —
/// <c>&lt;Style x:Key="{x:Type Button}" TargetType="Button"&gt;</c> — resolves its key to <c>typeof(Button)</c>
/// (the control-theme exact key), resolves its <c>TargetType</c>-scoped Setters (the R2 DynamicResource
/// palette spine + a <c>{StaticResource}</c> <c>ControlTemplate</c>), AND authors a nested
/// <c>:focus</c> state sub-rule via <c>&lt;Style.Children&gt;&lt;Style Selector="^:focus"&gt;</c> (the
/// string→Selector converter), so the XAML theme reproduces the cell-faithful Button — resting spine plus
/// the reverse-video focus state — and applies over the code-first BuiltIn backstop.
///
/// The <c>x:Key</c> and <c>TargetType</c> are ORTHOGONAL (unlike WPF's <c>DictionaryKeyProperty(TargetType)</c>
/// collapse): the <c>x:Key</c> is the dictionary entry's Type; <c>TargetType</c> binds the Setters (Setter
/// property names are unqualified-against-TargetType — matrix X64/X65). A nested sub-rule carries BOTH a
/// <c>Selector</c> (for matching) and a <c>TargetType</c> (for its own setters' resolution).
///
/// Full byte-for-byte identity to the entire reflavored C# BuiltIn (every control, all states, the caps-*
/// theme-styles) is the ARCH-1 milestone (Task #10) this builds toward.
/// </summary>
public sealed class ThemeXamlPhase1Tests
{
    private static readonly Uri Source = new("cursorial://test/theme.xaml");

    // A Button control theme as XAML: a Type-keyed Style (typeof(Button)) carrying TargetType for Setter
    // resolution, the resting Foreground/Background as DynamicResource palette references (the R2 spine), a
    // Border>ContentPresenter template wired by {StaticResource}, and a nested ^:focus reverse-video sub-rule
    // (Background→TextBrush, Foreground→WindowBackground) authored via <Style.Children>.
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
        "    <Style.Children>" +
        "      <Style Selector=\"^:focus\" TargetType=\"Button\">" +
        "        <Setter Property=\"Background\" Value=\"{DynamicResource {x:Static ThemeKeys.TextBrush}}\"/>" +
        "        <Setter Property=\"Foreground\" Value=\"{DynamicResource {x:Static ThemeKeys.WindowBackground}}\"/>" +
        "      </Style>" +
        "    </Style.Children>" +
        "  </Style>" +
        "</ResourceDictionary>";

    [Fact]
    public void XamlAuthoredButtonTheme_ResolvesTypeKey_AndAppliesAsControlTheme()
    {
        var theme = (Cursorial.UI.ResourceDictionary)new XamlLoader().Load(ButtonThemeXaml, Source);

        // The x:Key="{x:Type Button}" resolved to the typeof(Button) control-theme exact key (PRE-TYPEKEY),
        // and the nested <Style.Children> ^:focus sub-rule was authored (the Selector converter).
        Assert.True(theme.ContainsKey(typeof(UIControls.Button)));

        // …and applying it renders the Button from the XAML-authored template + TargetType-scoped Setters.
        var rows = RenderRestingButton(theme);
        Assert.Contains("OK", string.Concat(rows));
        Assert.NotEqual(new string(' ', rows[0].Length), rows[0]); // the content row carries the button
    }

    [Fact] // The XAML-authored ^:focus state sub-rule flips reverse-video on focus (the Selector-converter payoff).
    public void XamlAuthoredButtonTheme_FocusState_RendersReverseVideo()
    {
        var theme = (Cursorial.UI.ResourceDictionary)new XamlLoader().Load(ButtonThemeXaml, Source);

        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(14, 3) });
        host.Application.Theme = theme;

        var button = new UIControls.Button { Content = "OK" };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // Resting (a root-level button is not auto-focused): the XAML spine — TextBrush ink on a SurfaceBrush fill.
        var resting = FindCell(host, 'O');
        var restFg = resting.Style.Foreground;
        var restBg = resting.Style.Background;

        // Focus → the XAML-authored ^:focus sub-rule (nested via <Style.Children>) flips reverse-video.
        Assert.True(button.Focus(FocusNavigationMethod.Tab));
        host.RunFrame();
        var focused = FindCell(host, 'O');

        Assert.NotEqual(restFg, focused.Style.Foreground);  // the ^:focus sub-rule changed the ink…
        Assert.NotEqual(restBg, focused.Style.Background);   // …and the fill
        Assert.Equal(restFg, focused.Style.Background);      // reverse-video: the focused fill IS the resting ink (TextBrush)
    }

    private static string[] RenderRestingButton(Cursorial.UI.ResourceDictionary theme)
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

    private static Cell FindCell(UITestHost host, char first)
    {
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
        {
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme is { Length: > 0 } g && g[0] == first)
                    return cell;
            }
        }

        return default;
    }
}
