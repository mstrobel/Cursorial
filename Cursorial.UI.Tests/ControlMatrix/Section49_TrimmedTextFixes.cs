// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// Pins for the 2026-08 review round's text/tooltip fixes: the zero-row arrange crash, the
/// height-capped cache that never un-trimmed, grapheme-safe AccessTextPresenter trimming, the
/// inverted ToolTipService old-value guard (a leak per discarded tip), and the uncapped
/// trimmed-content tooltip payload.
/// </summary>
public sealed class Section49_TrimmedTextFixes
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

    [Fact]
    public void TextBlock_ZeroRowArrange_DoesNotThrow()
    {
        // A collapsed star row / shrunken window legally arranges a visible element at 0 rows;
        // the formatter throws on maxRows <= 0, so the arrange clamp is load-bearing.
        var text = new TextBlock { Text = "hello world" };
        var (host, _) = Show(text, columns: 8, rows: 0);
        using var _1 = host; // no throw during show/arrange = pass
    }

    [Fact]
    public void TextBlock_HeightTrimmed_UntrimsWhenTheSpaceComesBack()
    {
        var text = new TextBlock
        {
            Text = "one two three four five six seven eight",
            TextWrapping = WrapMode.WordWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var (host, slot) = Show(text, columns: 10, rows: 2);
        using var _ = host;
        Assert.True(text.IsTrimmed); // ~8 rows of text in a 2-row slot

        // Grow the slot back: the cache was formatted under maxRows=2 and used to be reused
        // forever ("cached fits" was the only invalidation); the text must un-trim.
        Resize(host, slot, columns: 10, rows: 20);
        Assert.False(text.IsTrimmed);
    }

    [Fact]
    public void AccessTextPresenter_WideGlyphLabel_TrimsWithoutThrowing()
    {
        // "日本語" is 3 chars / 6 columns: the old char-index Substring(0, columns-1) threw
        // ArgumentOutOfRangeException in arrange for any wide-cluster label.
        var presenter = new AccessTextPresenter { Text = AccessText.Parse("日本語") };
        var (host, _) = Show(presenter, columns: 5, rows: 1);
        using var _1 = host;

        Assert.True(presenter.GetValue(TextBlock.IsTrimmedProperty));
        // 4-column budget after the ellipsis: two whole clusters survive, no split pair.
        Assert.StartsWith("日本…", host.GetRowText(0));
    }

    [Fact]
    public void ToolTipService_ClearingAServiceInstalledTip_SeversTheInheritanceLink()
    {
        using var host = UIHeadlessHost.Create();
        var owner = new Border();
        host.ShowRoot(owner);
        Assert.True(host.RunUntilIdle());

        var tip = new TextBlock { Text = "tip" };
        ToolTipService.SetTip(owner, tip);
        Assert.Same(owner, tip.GetInheritanceParent()); // service installed the link

        // The old guard acted only when the parent was NOT the sender — i.e. never for links it
        // created — so every discarded tip stayed strongly referenced by the owner (a leak per
        // tooltip churn cycle, and trimmed-content tips churn on every text change).
        ToolTipService.SetTip(owner, null);
        Assert.Null(tip.GetInheritanceParent());
    }

    [Fact]
    public void ToolTipService_ForeignlyParentedTip_IsLeftAlone()
    {
        using var host = UIHeadlessHost.Create();
        var owner = new Border();
        host.ShowRoot(owner);
        Assert.True(host.RunUntilIdle());

        var foreignParent = new Border();
        var tip = new TextBlock { Text = "tip" };
        tip.SetInheritanceParent(foreignParent);

        ToolTipService.SetTip(owner, tip);   // author-parented: the service must not re-parent
        ToolTipService.SetTip(owner, null);  // …and must not touch it on clear either (the old
                                             // guard re-parented exactly these foreign tips)
        Assert.Same(foreignParent, tip.GetInheritanceParent());
    }

    [Fact]
    public void RichTextPresenter_GetUntrimmedText_IsNotCappedAtItsOwnRows()
    {
        // The trimmed-content tooltip exists to reveal what the presenter's bounds hid — the old
        // payload was capped at those same bounds and re-hid the tail lines.
        var presenter = new RichTextPresenter { Source = "one two three four five six seven eight nine ten" };
        var (host, _) = Show(presenter, columns: 10, rows: 2);
        using var _1 = host;

        var full = presenter.GetUntrimmedText(maxWidth: 12);

        Assert.NotNull(full);
        Assert.Contains("ten", full);
    }
}
