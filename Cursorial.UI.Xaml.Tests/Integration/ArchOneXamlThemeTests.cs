// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using System.Collections.Generic;

using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// ARCH-1 (Task #10): the data-shipped <c>Cursorial.UI.Themes.Xaml</c> assembly's Button control theme —
/// authored in embedded <c>.xaml</c> and loaded via <see cref="CursorialXamlTheme.LoadControls"/> — renders
/// byte-for-byte identically to the code-first <c>CursorialTheme.BuiltIn</c> Button, at REST and FOCUSED
/// (exercising the nested <c>^:focus</c> reverse-video sub-rule authored through the Selector converter +
/// <c>&lt;Style.Children&gt;</c>). The dogfood proof that the declarative theme reproduces the code-first one
/// end-to-end on the real frame loop.
/// </summary>
public sealed class ArchOneXamlThemeTests
{
    [Fact]
    public void XamlButtonTheme_RendersIdenticallyToCSharpBuiltIn_RestAndFocus()
    {
        // The XAML theme (app.Theme) layers over the code-first BuiltIn; the Button entry it carries replaces
        // BuiltIn's. Compare the rendered cells (glyph + fg + bg) to the BuiltIn oracle, resting and focused.
        Assert.Equal(RenderButtonCells(xaml: false, focus: false), RenderButtonCells(xaml: true, focus: false));
        Assert.Equal(RenderButtonCells(xaml: false, focus: true),  RenderButtonCells(xaml: true, focus: true));
    }

    private static (string Glyph, Color Fg, Color Bg)[] RenderButtonCells(bool xaml, bool focus)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(14, 3) });
        if (xaml)
            host.Application.Theme = CursorialXamlTheme.LoadControls();

        var button = new UIControls.Button { Content = "OK" };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());
        if (focus)
        {
            Assert.True(button.Focus(FocusNavigationMethod.Tab));
            host.RunFrame();
        }

        var cells = new List<(string, Color, Color)>(14 * 3);
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 14; c++)
        {
            var cell = host.GetCell(c, r);
            cells.Add((cell.Grapheme ?? string.Empty, cell.Style.Foreground, cell.Style.Background));
        }
        return cells.ToArray();
    }
}
