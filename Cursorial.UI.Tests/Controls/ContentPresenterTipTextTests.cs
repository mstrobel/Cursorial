// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// Task #19b: <c>ContentPresenter.TipTextProperty</c> — the per-child-type map naming which
/// property the trimmed-content tip must TRACK — omitted <see cref="FigletPresenter"/>, so the
/// tip-text observer never installed for figlet children. The observer's contract is the
/// SYNCHRONOUS refresh: the tip follows the text at property-set time (the observer pulses
/// <c>RebuildToolTip</c> directly), not at the next layout pass. The end-of-frame state happens to
/// be masked today — measure invalidation bubbles unconditionally, so the presenter's arrange-time
/// rebuild also refreshes the tip eventually — which is why these pins assert BEFORE pumping
/// layout: that is where the omitted observer is observable, and where RichTextPresenter (whose
/// arm was never omitted) already behaves.
/// </summary>
public sealed class ContentPresenterTipTextTests
{
    private static (UIHeadlessHost Host, ContentPresenter Presenter) Show(UIElement content, int columns, int rows)
    {
        var presenter = new ContentPresenter { Content = content, ShowTrimmedContentInToolTip = true };
        var slot = new SlotHost(presenter) { SlotRect = new Rect(0, 0, columns, rows) };

        var host = UIHeadlessHost.Create();
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());
        return (host, presenter);
    }

    [Fact]
    public void ContentPresenter_FigletTip_TracksTextAtPropertySetTime_LikeTheOtherPresenters()
    {
        // Fixed Width/Height clamp DesiredSize to (12, 2) for both texts — the swap is purely a
        // content change under a steady trimmed state.
        var figlet = new FigletPresenter { Text = "AAAA", Width = 12, Height = 2 };
        var (host, presenter) = Show(figlet, columns: 12, rows: 2);
        using var _1 = host;

        Assert.True(figlet.GetValue(TextBlock.IsTrimmedProperty)); // a figlet in 2 rows is trimmed
        var tip = Assert.IsType<TextBlock>(presenter.GetValue(ToolTipService.TipProperty));
        Assert.Contains("AAAA", tip.Text);

        // No layout pump between the set and the assert: the tip-text observer is the ONLY thing
        // that can refresh the payload here — exactly the arm TipTextProperty omitted for figlets.
        figlet.Text = "BBBB";

        var freshTip = Assert.IsType<TextBlock>(presenter.GetValue(ToolTipService.TipProperty));
        Assert.Contains("BBBB", freshTip.Text);
    }

    /// <summary>The parity control: RichTextPresenter's arm was never omitted, so its tip already
    /// refreshes at property-set time — the contract the figlet arm joins.</summary>
    [Fact]
    public void ContentPresenter_RichTextTip_AlreadyTracksSourceAtPropertySetTime()
    {
        var rtp = new RichTextPresenter { Source = "one two three four five six", Width = 8, Height = 1 };
        var (host, presenter) = Show(rtp, columns: 8, rows: 1);
        using var _1 = host;

        Assert.True(rtp.GetValue(TextBlock.IsTrimmedProperty));
        var tip = Assert.IsType<TextBlock>(presenter.GetValue(ToolTipService.TipProperty));
        Assert.Contains("six", tip.Text);

        rtp.Source = "seven eight nine ten eleven twelve";

        var freshTip = Assert.IsType<TextBlock>(presenter.GetValue(ToolTipService.TipProperty));
        Assert.Contains("twelve", freshTip.Text);
    }
}
