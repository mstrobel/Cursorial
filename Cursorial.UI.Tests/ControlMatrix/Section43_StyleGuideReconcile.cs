using System.Linq;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §43 — the P10 style-guide reconciliation (#103): control looks brought to the FINAL gallery
/// (tokyo-night-control-gallery-final.html) where they genuinely diverged. Each fix is a per-control-key
/// alias retarget (resolved through §11.4a chasing) or a template change. Pins the gallery values:
/// item/tree/calendar reverse-video focus ink = --panel (`.item.rev`/`.cal .focus`); ProgressBar empty
/// track = --faint; Tab active = --surface fill + --text ink + an --accent underline bar, inactive ink =
/// --text-dim. (The reverse-video focus model + check/radio focus-caret + radio = --accent mark already
/// match the final gallery and are unchanged.)
/// </summary>
public sealed class Section43_StyleGuideReconcile
{
    // Dark (Truecolor) palette hex the gallery pins.
    private static readonly Color Text = Color.FromHex("#c0caf5");      // --text
    private static readonly Color TextDim = Color.FromHex("#a9b1d6");   // --text-dim
    private static readonly Color Surface = Color.FromHex("#24283b");   // --surface
    private static readonly Color Panel = Color.FromHex("#222639");     // --panel
    private static readonly Color Accent = Color.FromHex("#7aa2f7");    // --accent
    private static readonly Color Faint = Color.FromHex("#414868");     // --faint
    private static readonly Color Green = Color.FromHex("#9ece6a");     // --green

    private static UITestHost DarkHost(int w = 40, int h = 12)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        return host;
    }

    private static Color BrushColor(UIElement e, string key) => ((SolidColorBrush)e.FindResource(key)!).Color;

    // ───────────────────────────── focus-ink → --panel (the .item.rev / .cal .focus reconciliation) ─────────────────────────────

    [Fact] // C103.1 — list/tree/calendar reverse-video focus ink now resolves to --panel (was --bg), per the gallery.
    public void C103_1_ItemFocusInk_IsPanel()
    {
        using var host = DarkHost();
        var probe = new StackPanel();
        host.ShowRoot(probe);
        host.RunUntilIdle();

        var panel = BrushColor(probe, ThemeKeys.PanelBrush);
        Assert.Equal(Panel, panel);
        Assert.Equal(panel, BrushColor(probe, ThemeKeys.ListItemForegroundFocus)); // chases the alias → --panel
        Assert.Equal(panel, BrushColor(probe, ThemeKeys.TreeItemForegroundFocus));
        Assert.Equal(panel, BrushColor(probe, ThemeKeys.CalendarDayForegroundFocus));
        // The reverse-video FILL stays --text (unchanged), and a Button's focus ink stays --bg (gallery `.rev`).
        Assert.Equal(Text, BrushColor(probe, ThemeKeys.ListItemBackgroundFocus));
        Assert.NotEqual(panel, BrushColor(probe, ThemeKeys.ButtonForegroundFocus)); // button keeps --bg
    }

    // ───────────────────────────── ProgressBar track → --faint ─────────────────────────────

    [Fact] // C103.2 — the ProgressBar empty track resolves to --faint (was --well), per the gallery.
    public void C103_2_ProgressTrack_IsFaint()
    {
        using var host = DarkHost();
        var probe = new StackPanel();
        host.ShowRoot(probe);
        host.RunUntilIdle();
        Assert.Equal(Faint, BrushColor(probe, ThemeKeys.ProgressTrackBrush));
    }

    [Fact] // C103.2b — end-to-end: a determinate default ProgressBar paints a --faint track + --green fill.
    public void C103_2b_ProgressBar_RendersFaintTrack_GreenFill()
    {
        using var host = DarkHost(24, 4);
        var bar = new ProgressBar
        {
            Maximum = 100, Value = 30, Width = 10, Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(bar);
        Assert.True(host.RunUntilIdle());
        var o = bar.TranslateToWindow(0, 0);

        Assert.Equal(Green, host.GetCell(o.Column, o.Row).Style.Background);      // cell 0 — filled (--green)
        Assert.Equal(Faint, host.GetCell(o.Column + 9, o.Row).Style.Background);  // cell 9 — empty track (--faint)
    }

    // ───────────────────────────── Tab active look (surface fill + text ink + accent bar; inactive text-dim) ─────────────────────────────

    [Fact] // C103.3 — the Tab per-control keys resolve to the gallery's active/inactive tokens.
    public void C103_3_TabKeys_ResolveToGallery()
    {
        using var host = DarkHost();
        var probe = new StackPanel();
        host.ShowRoot(probe);
        host.RunUntilIdle();

        Assert.Equal(TextDim, BrushColor(probe, ThemeKeys.TabForegroundNormal));   // inactive ink = --text-dim
        Assert.Equal(Text, BrushColor(probe, ThemeKeys.TabForegroundActive));      // active ink = --text
        Assert.Equal(Surface, BrushColor(probe, ThemeKeys.TabBackgroundSelected)); // active fill = --surface

        var pen = (Pen)probe.FindResource(ThemeKeys.TabUnderlinePen)!;             // underline rule = Heavy --accent pen
        Assert.Equal(StrokeWeight.Heavy, pen.Weight);
        Assert.Equal(Accent, ((SolidColorBrush)pen.Brush!).Color);
    }

    [Fact] // C103.3b — end-to-end: the active tab is --text ink + an --accent underline bar; inactive tabs are --text-dim.
    public void C103_3b_TabControl_ActiveLook_RendersGallery()
    {
        using var host = DarkHost(48, 16);
        var a = new TabItem { Header = "Build", Content = "x" };
        var b = new TabItem { Header = "Tests", Content = "y" };
        var tabs = new TabControl { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        tabs.Items.Add(a);
        tabs.Items.Add(b);
        host.ShowRoot(tabs);
        Assert.True(host.RunUntilIdle()); // tab 0 (Build) auto-selects

        Color Ink(string headerChar)
        {
            for (var r = 0; r < host.FrameBuffer.Rows; r++)
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme == headerChar)
                    return cell.Style.Foreground;
            }
            return Color.Default;
        }

        Assert.Equal(Text, Ink("B"));     // active tab "Build" ink = --text
        Assert.Equal(TextDim, Ink("T"));  // inactive tab "Tests" ink = --text-dim

        // Selecting a tab paints its accent underline rule (the Separator's Heavy --accent pen, drawn as ━
        // glyphs in the --accent foreground — the gallery "active tab marked by accent bar (━ cells)").
        tabs.SelectedIndex = 1;
        Assert.True(host.RunUntilIdle());
        var hasAccentRule = Enumerable.Range(0, host.FrameBuffer.Rows).Any(r =>
            Enumerable.Range(0, host.FrameBuffer.Columns).Any(c => host.GetCell(c, r).Style.Foreground == Accent));
        Assert.True(hasAccentRule, "the selected tab's --accent underline rule should be painted");
    }
}
