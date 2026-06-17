// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;
using Cursorial.UI.Themes.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// ARCH-1 (Task #10): the data-shipped <c>Cursorial.UI.Themes.Xaml</c> assembly's control themes —
/// authored in embedded <c>.xaml</c> and loaded via <see cref="CursorialXamlTheme.LoadControls"/> — render
/// byte-for-byte identically to the code-first <c>CursorialTheme.BuiltIn</c> ones. The button family is
/// checked at REST and FOCUSED (exercising the nested <c>^:focus</c> reverse-video sub-rule authored through
/// the Selector converter + <c>&lt;Style.Children&gt;</c>); the ScrollBar/ScrollViewer at rest (their
/// <c>Track</c>/arrow/SCP composition through the parameterless <c>Track()</c> + <c>TemplatedParent</c>
/// owner resolution). The dogfood proof that the declarative theme reproduces the code-first one end-to-end
/// on the real frame loop.
/// </summary>
public sealed class ArchOneXamlThemeTests
{
    [Theory]
    [InlineData("Button")]
    [InlineData("RepeatButton")]
    [InlineData("ToggleButton")]
    [InlineData("CheckBox")]    // unchecked: "[ ]" in both ASCII + caps-unicode, so byte-identity is tier-robust
    [InlineData("RadioButton")] // unchecked: "( )" likewise
    public void XamlControlTheme_RendersIdenticallyToCSharpBuiltIn_RestAndFocus(string control)
    {
        // The XAML theme (app.Theme) layers over the code-first BuiltIn; the typeof(control) entry it carries
        // replaces BuiltIn's. Compare the rendered cells (glyph + fg + bg) to the BuiltIn oracle, rest + focus.
        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 14, 3, () => MakeControl(control)),
            CaptureCells(xaml: true,  focus: false, 14, 3, () => MakeControl(control)));
        Assert.Equal(
            CaptureCells(xaml: false, focus: true, 14, 3, () => MakeControl(control)),
            CaptureCells(xaml: true,  focus: true, 14, 3, () => MakeControl(control)));
    }

    [Fact]
    public void XamlScrollBarTheme_RendersIdenticallyToCSharpBuiltIn()
    {
        // A vertical ScrollBar: ▲ rail+thumb ▼ down the column. The XAML twin authors the arrow RepeatButtons
        // + Track (parameterless ctor, owner resolved from TemplatedParent) and must match the code-first one.
        static UIControls.Control MakeScrollBar() => new UIControls.ScrollBar
        {
            Orientation = UIControls.Orientation.Vertical,
            Width = 1,
            Height = 10,
            Minimum = 0,
            Maximum = 90,
            ViewportSize = 10,
        };

        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 2, 12, MakeScrollBar),
            CaptureCells(xaml: true,  focus: false, 2, 12, MakeScrollBar));
    }

    [Fact]
    public void XamlScrollViewerTheme_RendersIdenticallyToCSharpBuiltIn()
    {
        // A ScrollViewer over tall content: the SCP fills, a vertical ScrollBar docks right. The XAML twin
        // composes ScrollBar + ScrollContentPresenter via the parameterless parts and must match.
        static UIControls.Control MakeScrollViewer() => new UIControls.ScrollViewer
        {
            Width = 13,
            Height = 5,
            Content = new UIControls.Border
            {
                Width = 12,
                Height = 40,
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            },
        };

        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 14, 6, MakeScrollViewer),
            CaptureCells(xaml: true,  focus: false, 14, 6, MakeScrollViewer));
    }

    [Fact]
    public void XamlGlyphSetCarrierResources_InstantiateFromXaml_WithBuiltInValues()
    {
        // GlyphSetCarrier (record class: parameterless ctor + init props) is authored as <GlyphSetCarrier>
        // resource elements in the theme XAML; prove the loader instantiates them and the dict carries the
        // same triples as the code-first CursorialTheme.BuiltIn (true-ASCII defaults). The x:Key is an
        // {x:Static ThemeKeys.*Glyphs} string key.
        var dict = CursorialXamlTheme.LoadControls();

        AssertGlyphs(dict, ThemeKeys.CheckBoxGlyphs, "[ ]", "[x]", "[-]");
        AssertGlyphs(dict, ThemeKeys.RadioGlyphs, "( )", "(*)", "(-)");
        AssertGlyphs(dict, ThemeKeys.ScrollArrowGlyphs, "^", "v", ""); // arrow pair: empty Indeterminate default

        static void AssertGlyphs(ResourceDictionary dict, string key, string @unchecked, string @checked, string indeterminate)
        {
            Assert.True(dict.TryGetValue(key, out var value), $"theme dict missing glyph resource '{key}'");
            var glyphs = Assert.IsType<GlyphSetCarrier>(value);
            Assert.Equal((@unchecked, @checked, indeterminate), (glyphs.Unchecked, glyphs.Checked, glyphs.Indeterminate));
        }
    }

    [Theory] // W5 (ARCH-1 parity): the P9 controls that render inline render identically to the code-first BuiltIn
    [InlineData("TextBox")]
    [InlineData("ProgressBar")]
    [InlineData("Separator")]
    [InlineData("ListBox")]       // exercises the ListBox AND ListBoxItem XAML themes
    [InlineData("ItemsControl")]
    public void XamlP9ControlTheme_RendersIdenticallyToCSharpBuiltIn_AtRest(string control)
    {
        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 16, 5, () => MakeP9Control(control)),
            CaptureCells(xaml: true,  focus: false, 16, 5, () => MakeP9Control(control)));
    }

    [Fact] // W5: every P9 control theme parses + is present (covers the popup-rooted ones that can't render inline)
    public void AllP9ControlThemes_Parse_AndArePresentInTheDictionary()
    {
        var dict = CursorialXamlTheme.LoadControls();
        Type[] themed =
        [
            typeof(UIControls.ItemsControl), typeof(UIControls.ListBox), typeof(UIControls.ListBoxItem),
            typeof(UIControls.Menu), typeof(UIControls.MenuItem), typeof(UIControls.ContextMenu),
            typeof(UIControls.Separator), typeof(UIControls.ToolTip), typeof(UIControls.TabControl),
            typeof(UIControls.TabItem), typeof(UIControls.ProgressBar), typeof(UIControls.TextBox),
        ];
        foreach (var t in themed)
            Assert.True(dict.TryGetValue(t, out _), $"XAML theme missing the control theme for {t.Name}");
    }

    private static UIControls.Control MakeControl(string control) => control switch
    {
        "RepeatButton" => new UIControls.RepeatButton { Content = "OK" },
        "ToggleButton" => new UIControls.ToggleButton { Content = "OK" },
        "CheckBox"     => new UIControls.CheckBox { Content = "OK" },
        "RadioButton"  => new UIControls.RadioButton { Content = "OK" },
        _              => new UIControls.Button { Content = "OK" },
    };

    private static UIControls.Control MakeP9Control(string control) => control switch
    {
        "TextBox"      => new UIControls.TextBox { Width = 14 },
        "ProgressBar"  => new UIControls.ProgressBar { Width = 14, Height = 1 },
        "Separator"    => new UIControls.Separator { Width = 14 },
        "ListBox"      => new UIControls.ListBox { ItemsSource = new[] { "a", "b", "c" }, Width = 14, Height = 4 },
        "ItemsControl" => new UIControls.ItemsControl { ItemsSource = new[] { "a", "b" }, Width = 14, Height = 4 },
        _              => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    private static (string Glyph, Color Fg, Color Bg)[] CaptureCells(
        bool xaml, bool focus, int cols, int rows, Func<UIControls.Control> factory)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(cols, rows) });
        if (xaml)
            host.Application.Theme = CursorialXamlTheme.LoadControls();

        var element = factory();
        host.ShowRoot(element);
        Assert.True(host.RunUntilIdle());
        if (focus)
        {
            Assert.True(element.Focus(FocusNavigationMethod.Tab));
            host.RunFrame();
        }

        var cells = new List<(string, Color, Color)>(cols * rows);
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var cell = host.GetCell(c, r);
            cells.Add((cell.Grapheme ?? string.Empty, cell.Style.Foreground, cell.Style.Background));
        }
        return cells.ToArray();
    }
}
