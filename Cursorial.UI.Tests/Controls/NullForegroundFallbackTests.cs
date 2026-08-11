// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// Maintainer ruling M3 (UNIFIED-TEXT-SCOPING D1): when the resolved element foreground is null,
/// paint with <c>Brushes.Default</c> instead of skipping the draw. The default is backed by a theme
/// key, so a null only arises when an application states one deliberately — but the two presenter
/// families disagreed on what it meant: TextBlock painted with the document's own ink while
/// RichTextPresenter and FigletPresenter gated the ENTIRE draw on <c>GetForeground is not null</c>
/// and painted nothing at all. Paint-with-fallback wins; the RTP/Figlet tests here were RED at the
/// pre-change HEAD (empty frames), and the TextBlock test pins the side that does not move.
/// </summary>
public sealed class NullForegroundFallbackTests
{
    private const int Columns = 24;
    private const int Rows = 8;

    private static (UIHeadlessHost Host, SlotHost Slot) Show(UIElement child)
    {
        var slot = new SlotHost(child) { SlotRect = new Rect(0, 0, Columns, Rows) };
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                         {
                                             InitialSize = new Size(Columns, Rows)
                                         });
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());
        return (host, slot);
    }

    private static bool RowContains(UIHeadlessHost host, int row, string needle)
        => host.GetRowText(row).Contains(needle);

    private static bool AnyInk(UIHeadlessHost host)
    {
        for (var r = 0; r < Rows; r++)
        {
            if (host.GetRowText(r).Trim().Length > 0)
                return true;
        }

        return false;
    }

    [Fact]
    public void RichTextPresenter_NullResolvedForeground_PaintsWithDefaultInk_InsteadOfSkipping()
    {
        var rtp = new RichTextPresenter
                  {
                      Source = "Hello",
                      Foreground = null, // a stated null — the theme-backed default is overridden
                      HorizontalAlignment = HorizontalAlignment.Left,
                      VerticalAlignment = VerticalAlignment.Top,
                  };

        // The premise: the resolved foreground really is null (otherwise this test proves nothing).
        Assert.Null(TextElement.GetForeground(rtp));

        var (host, _) = Show(rtp);
        using var _1 = host;

        // M3: the glyphs paint with Brushes.Default instead of the whole draw being skipped.
        // RED at the pre-change HEAD: the row stayed blank.
        Assert.True(RowContains(host, 0, "Hello"),
                    $"expected the label to paint with default ink; row 0 = '{host.GetRowText(0)}'");
    }

    [Fact]
    public void FigletPresenter_NullResolvedForeground_PaintsWithDefaultInk_InsteadOfSkipping()
    {
        var figlet = new FigletPresenter
                     {
                         Text = "A",
                         Foreground = null,
                         HorizontalAlignment = HorizontalAlignment.Left,
                         VerticalAlignment = VerticalAlignment.Top,
                     };

        Assert.Null(TextElement.GetForeground(figlet));

        var (host, _) = Show(figlet);
        using var _1 = host;

        // The figlet face inks SOMETHING for 'A' in the default font; skipping painted nothing.
        // RED at the pre-change HEAD: every row blank.
        Assert.True(AnyInk(host), "expected the figlet face to ink cells with default ink");
    }

    [Fact]
    public void TextBlock_NullForeground_StillPaintsWithDocumentInk()
    {
        var tb = new TextBlock("Hello")
                 {
                     Foreground = null,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     VerticalAlignment = VerticalAlignment.Top,
                 };

        Assert.Null(TextElement.GetForeground(tb));

        var (host, _) = Show(tb);
        using var _1 = host;

        // The unchanged side (green at HEAD before and after): a null foreground is an absent
        // preference channel; the document's own ink stands.
        Assert.True(RowContains(host, 0, "Hello"),
                    $"expected the document ink to stand; row 0 = '{host.GetRowText(0)}'");
    }
}
