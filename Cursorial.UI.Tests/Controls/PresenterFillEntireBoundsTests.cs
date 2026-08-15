// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// The never-forwarded <c>FillEntireBounds</c> properties (task #18, resolving the unified-text
/// scoping finding): <see cref="RichTextPresenter.FillEntireBounds"/> and
/// <see cref="FigletPresenter.FillEntireBounds"/> were registered, documented and invalidating,
/// but no path forwarded them to the produced <see cref="FormattedText"/> — setting them had no
/// effect. Written FIRST against that defective HEAD (both tests red before the wiring), so the
/// green run proves the property now reaches the layout: the document re-centres vertically, and
/// the surround takes the background fill the design section gives the flag.
/// </summary>
public sealed class PresenterFillEntireBoundsTests
{
    private static readonly Color Navy = Color.FromRgb(0, 24, 64);

    [Fact]
    public void RichTextPresenter_FillEntireBounds_FillsTheSurroundAndReCentres()
    {
        // A document whose DefaultStyle states a background — the fill's document rung. With the
        // flag wired, the surround of the presenter's bounds takes that background as the durable
        // fill cell, and the one-line document re-centres vertically ((5 - 1) / 2 = row 2).
        var document = new RichTextBuilder(PartialStyle.WithBackground(Navy)).Run("hi").Build();

        var presenter = new RichTextPresenter
                        {
                            Source = document,
                            FillEntireBounds = true,
                            TextWrapping = WrapMode.NoWrap
                        };
        var slot = new SlotHost(presenter) { SlotRect = new Rect(0, 0, 10, 5) };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 8) });
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());

        // The surround cell: filled with the document background, owned durably. At the defective
        // HEAD the flag never reached the FormattedText and this cell held the theme backdrop.
        var surround = host.GetCell(9, 4);
        Assert.Equal(Navy, surround.Style.Background);
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, surround.Grapheme);

        // And the document re-centred: "hi" on row 2, not row 0.
        Assert.Equal("h", host.GetCell(0, 2).Grapheme);
        Assert.NotEqual("h", host.GetCell(0, 0).Grapheme);
    }

    [Fact]
    public void FigletPresenter_FillEntireBounds_ReCentresTheDocumentVertically()
    {
        // The figlet document states no background anywhere (its block style is transparent-ink),
        // so the fill's source ladder resolves nothing and the surround stays untouched — the
        // flag's observable here is the vertical re-centring. Self-calibrating: the top inked row
        // with the flag OFF, then ON, must move down. At the defective HEAD it did not move.
        var presenter = new FigletPresenter { Text = "H" };
        var slot = new SlotHost(presenter) { SlotRect = new Rect(0, 0, 30, 20) };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 24) });
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());

        int before = TopInkedRow(host);

        presenter.FillEntireBounds = true;
        Assert.True(host.RunUntilIdle());

        int after = TopInkedRow(host);
        Assert.True(after > before,
                    $"expected FillEntireBounds to re-centre the figlet downwards (before={before}, after={after})");
    }

    private static int TopInkedRow(UIHeadlessHost host)
    {
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                if (!string.IsNullOrWhiteSpace(host.GetCell(c, r).Grapheme))
                    return r;
            }

        Assert.Fail("No inked cell found in the frame.");
        return -1;
    }
}
