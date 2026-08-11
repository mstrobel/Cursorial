// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// Per-adopter WIRING pins for the shared <see cref="FormattedTextCache"/>: the key mechanics are
/// killed term-by-term in <see cref="FormattedTextCacheKeyTests"/>; these prove each presenter
/// actually routes its state INTO those terms — a presenter passing a stale or wrong value into a
/// correct key is the same bug as a missing term (which is exactly how the FigletPresenter
/// photocopy regressed). TextBlock is the pull-only adopter (no push callbacks — the key is its
/// entire freshness story), so it carries the per-term frame assertions; RTP/Figlet add the
/// arrange-time grow-back wiring the TextBlock spelling doesn't share.
/// </summary>
public sealed class FormattedTextCacheAdoptionTests
{
    private static (UIHeadlessHost Host, SlotHost Slot) Show(UIElement child, int columns, int rows)
    {
        var slot = new SlotHost(child) { SlotRect = new Rect(0, 0, columns, rows) };
        var host = UIHeadlessHost.Create();
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());
        return (host, slot);
    }

    private static void Resize(UIHeadlessHost host, SlotHost slot, int columns, int rows)
    {
        slot.SlotRect = new Rect(0, 0, columns, rows);
        slot.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());
    }

    // ─────────────────────────── TextBlock: the pull-only adopter ───────────────────────────

    [Fact]
    public void TextBlock_TextChange_RepaintsFreshGlyphs()
    {
        var tb = new TextBlock("AA");
        var (host, _) = Show(tb, columns: 10, rows: 1);
        using var _1 = host;
        Assert.StartsWith("AA", host.GetRowText(0));

        // Same width, same options — only the SOURCE term can force the reformat.
        tb.Text = "BB";
        Assert.True(host.RunUntilIdle());
        Assert.StartsWith("BB", host.GetRowText(0));
    }

    [Fact]
    public void TextBlock_SwappingLiteralTextForIdenticalMarkup_ReformatsAsMarkup()
    {
        // The SAME string, first as literal Text, then as Markup: the key's source VALUE is
        // identical both times (`Markup ?? Text`), so only the markup-lane bit can tell the
        // literal layout from the parsed one.
        const string s = "[fg=Theme.AccentBrush]Hi[/fg]";

        var tb = new TextBlock { Text = s };
        var (host, _) = Show(tb, columns: 40, rows: 1);
        using var _1 = host;
        Assert.Equal("[", host.GetCell(0, 0).Grapheme); // literal — brackets render

        tb.Markup = s; // markup wins over Text (doc §12.7)
        Assert.True(host.RunUntilIdle());
        Assert.Equal("H", host.GetCell(0, 0).Grapheme); // parsed — brackets consumed
    }

    [Fact]
    public void TextBlock_WidthChange_Rewraps()
    {
        // SlotHost constrains at ARRANGE (measure sees the full window), so the columns term is
        // exercised through the arranged/painted layout, not DesiredSize.
        var tb = new TextBlock("one two three") { TextWrapping = WrapMode.WordWrap };
        var (host, slot) = Show(tb, columns: 20, rows: 5);
        using var _1 = host;
        Assert.StartsWith("one two three", host.GetRowText(0)); // one line at 20 columns
        Assert.Equal(string.Empty, host.GetRowText(1).TrimEnd());

        Resize(host, slot, columns: 7, rows: 5);
        // The columns term reformatted — "one two" fits row 0 exactly, "three" wraps to row 1.
        Assert.Contains("three", host.GetRowText(1));
    }

    [Fact]
    public void TextBlock_WrapModeFlip_Reformats()
    {
        var tb = new TextBlock("one two three")
                 {
                     TextWrapping = WrapMode.WordWrap,
                     TextTrimming = TextTrimming.CharacterEllipsis
                 };
        var (host, _) = Show(tb, columns: 7, rows: 5);
        using var _1 = host;
        Assert.Contains("three", host.GetRowText(1)); // word-wrapped: "one two" / "three"
        Assert.False(tb.IsTrimmed);

        // TextWrapping has NO change callback on TextBlock (pull-only) — without the Wrap term
        // the re-format would serve the word-wrapped layout forever.
        tb.TextWrapping = WrapMode.NoWrap;
        Assert.True(host.RunUntilIdle());
        Assert.True(tb.IsTrimmed);                       // one over-long line, ellipsis-trimmed
        Assert.Equal("…", host.GetCell(6, 0).Grapheme);
    }

    [Fact]
    public void TextBlock_AlignmentFlip_RepositionsGlyphs()
    {
        var tb = new TextBlock("Hi") { TextAlignment = TextAlignment.Right };
        var (host, _) = Show(tb, columns: 10, rows: 1);
        using var _1 = host;
        Assert.Equal("H", host.GetCell(8, 0).Grapheme); // right-aligned in the 10-column slot

        tb.TextAlignment = TextAlignment.Left;
        Assert.True(host.RunUntilIdle());
        Assert.Equal("H", host.GetCell(0, 0).Grapheme);
    }

    [Fact]
    public void TextBlock_TrimmingFlip_Reformats()
    {
        var tb = new TextBlock("abcdefgh")
                 {
                     TextWrapping = WrapMode.NoWrap,
                     TextTrimming = TextTrimming.CharacterEllipsis
                 };
        var (host, _) = Show(tb, columns: 5, rows: 1);
        using var _1 = host;
        Assert.Equal("…", host.GetCell(4, 0).Grapheme);

        tb.TextTrimming = TextTrimming.ClipFromEnd;
        Assert.True(host.RunUntilIdle());
        Assert.Equal("e", host.GetCell(4, 0).Grapheme); // bare clip — no ellipsis
    }

    [Fact]
    public void TextBlock_ResourceSwap_RepaintsFreshInk()
    {
        // C165 asserts the version BUMPS; this pins what the bump is FOR — the re-resolved brush
        // must reach the cells (a dropped version term serves the old baked ink and still
        // renders "H", which C165 alone cannot distinguish).
        var red = Color.FromRgb(0xFF, 0x00, 0x00);
        var green = Color.FromRgb(0x00, 0xFF, 0x00);

        var tb = new TextBlock { Markup = "[fg=K]Hi[/fg]", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var host = UIHeadlessHost.Create();
        using var _1 = host;
        host.Application.Resources["K"] = new SolidColorBrush(red);
        host.ShowRoot(tb);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(red, host.GetCell(0, 0).Style.Foreground);

        host.Application.Resources["K"] = new SolidColorBrush(green);
        tb.InvalidateVisual(); // no subscription (CD16) — the next pull must see the new version
        host.RunFrame();
        Assert.Equal(green, host.GetCell(0, 0).Style.Foreground);
    }

    // ─────────────── RTP/Figlet: the arrange-time grow-back wiring (D6's other spelling) ───────────────

    [Fact]
    public void RichTextPresenter_HeightTrimmed_UntrimsWhenTheSpaceComesBack()
    {
        var presenter = new RichTextPresenter
                        {
                            Source = "one two three four five six seven eight",
                            TextWrapping = WrapMode.WordWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Width = 10 // SlotHost measures at the full window — constrain the wrap width
                        };

        var (host, slot) = Show(presenter, columns: 10, rows: 2);
        using var _ = host;
        Assert.True(presenter.GetValue(TextBlock.IsTrimmedProperty)); // ~5 rows in a 2-row slot

        // Grow the slot back: the layout was capped at 2 rows (RowCapBit) — reusing it for the
        // taller slot is how text used to stay trimmed forever after the space came back.
        Resize(host, slot, columns: 10, rows: 20);
        Assert.False(presenter.GetValue(TextBlock.IsTrimmedProperty));
    }

    [Fact]
    public void FigletPresenter_HeightTrimmed_UntrimsWhenTheSpaceComesBack()
    {
        var presenter = new FigletPresenter { Text = "Hi" }; // Small font: taller than 2 rows

        var (host, slot) = Show(presenter, columns: 30, rows: 2);
        using var _ = host;
        Assert.True(presenter.GetValue(TextBlock.IsTrimmedProperty));

        Resize(host, slot, columns: 30, rows: 30);
        Assert.False(presenter.GetValue(TextBlock.IsTrimmedProperty));
    }
}
