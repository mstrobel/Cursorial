// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// The two FigletPresenter parse-freshness defects the unified-text Scope A slice fixes
/// (UNIFIED-TEXT-SCOPING §1.3/D3 and §2/D12), written FIRST against the defective HEAD:
/// <list type="bullet">
/// <item>Doc defect 3: the figlet cache had no <c>ParseFreshness</c> terms (RichTextPresenter's
/// carries ResourceVersion + ActualThemeVariant), so ink the parse BAKES — the underline color
/// flattened from the theme-reactive <c>Foreground</c> default — persisted across a theme-variant
/// flip. The sibling proofs are <c>Phase5EndToEndTests.TextBlock_ReFormatsOnThemeVariantFlip…</c>
/// and <c>…RichTextPresenter_ReParsesOnThemeVariantFlip…</c>.</item>
/// <item>D12: <c>Text.Split(['\r','\n'])</c> yields an EMPTY entry per CRLF — an extra empty
/// figlet block per <c>\r\n</c> break — where <c>TextBlock.BuildPlainText</c> folds
/// <c>\r\n</c> to one break (the P2.6 text-tier contract, C162).</item>
/// </list>
/// </summary>
public sealed class FigletPresenterFreshnessTests
{
    // ───────────────────────── doc defect 3: parse freshness ─────────────────────────

    /// <summary>
    /// The figlet parse bakes the underline color by flattening the element <c>Foreground</c> —
    /// which, unset, defaults through the theme's <c>Theme.TextBrush</c> (a theme-reactive
    /// default that re-resolves on read with NO property-changed pulse). A variant flip must
    /// therefore invalidate the cached parse via the freshness key (ResourceVersion/Variant —
    /// CD16's pull-based contract), or the pre-flip underline ink persists forever.
    /// </summary>
    [Fact]
    public void FigletPresenter_ReParsesOnThemeVariantFlip_BakedUnderlineInkFollowsTheVariant()
    {
        using var host = UIHeadlessHost.Create();

        var darkInk = Color.FromRgb(200, 210, 240);
        var lightInk = Color.FromRgb(20, 24, 60);
        host.Application.Resources.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Dark, null)] =
            new ResourceDictionary { [ThemeKeys.TextBrush] = new SolidColorBrush(darkInk) };
        host.Application.Resources.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Light, null)] =
            new ResourceDictionary { [ThemeKeys.TextBrush] = new SolidColorBrush(lightInk) };

        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunFrame();

        var figlet = new FigletPresenter
                     {
                         Text = "H",
                         HorizontalAlignment = HorizontalAlignment.Left,
                         VerticalAlignment = VerticalAlignment.Top
                     };
        TextElement.SetUnderline(figlet, UnderlineStyle.Single);

        host.ShowRoot(figlet);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(ThemeBase.Dark, host.Application.ActualThemeVariant.Base);
        Assert.Equal(darkInk, FindUnderlinedCell(host).Style.UnderlineColor); // baked against Dark

        // A re-render under the SAME variant serves the cached parse — still dark.
        figlet.InvalidateVisual();
        host.RunFrame();
        Assert.Equal(darkInk, FindUnderlinedCell(host).Style.UnderlineColor);

        // Flip the variant. The next invalidation-driven render must NOT serve the stale parse:
        // the baked underline ink has to re-flatten against the Light variant's TextBrush.
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();
        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);

        figlet.InvalidateVisual();
        host.RunFrame();
        Assert.Equal(lightInk, FindUnderlinedCell(host).Style.UnderlineColor); // re-parsed — no stale ink
    }

    private static Cell FindUnderlinedCell(UIHeadlessHost host)
    {
        // The figlet face strips the Underline ATTRIBUTE (FigletFont.ForbiddenAttributes), but the
        // baked underline COLOR channel — the parse-time flatten of the element Foreground — still
        // rides the folded cell style; it is the parse's freshness witness.
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Style.UnderlineColor != Color.Default && cell.Style.UnderlineColor != Color.Transparent)
                    return cell;
            }

        Assert.Fail("No cell carrying an underline color found in the frame.");
        return default;
    }

    // ───────────────────────────────── D12: CRLF splitting ─────────────────────────────────

    /// <summary>
    /// <c>\r\n</c> is ONE hard break (the C162 contract TextBlock pins): a CRLF-separated figlet
    /// must lay out exactly like its LF-separated twin, not carry an extra empty block per break.
    /// A GENUINE blank line (<c>\n\n</c>) is content and must keep its rows — that guard keeps
    /// the fix from over-folding.
    /// </summary>
    [Fact]
    public void FigletPresenter_CrlfInput_LaysOutIdenticallyToLfInput()
    {
        var lf = MeasuredRows("AB\nCD");
        var crlf = MeasuredRows("AB\r\nCD");
        var blankLf = MeasuredRows("AB\n\nCD");
        var blankCrlf = MeasuredRows("AB\r\n\r\nCD");
        var loneCr = MeasuredRows("AB\rCD");

        Assert.Equal(lf, crlf);          // \r\n folds to one break — no extra empty block
        Assert.Equal(blankLf, blankCrlf); // …including around a genuine blank line
        Assert.Equal(lf, loneCr);         // a lone \r is one break too
        Assert.True(blankLf > lf,         // and the genuine blank line keeps occupying rows
                    $"expected a blank line to add rows (blank={blankLf}, plain={lf})");
    }

    private static int MeasuredRows(string text)
    {
        var figlet = new FigletPresenter
                     {
                         Text = text,
                         HorizontalAlignment = HorizontalAlignment.Left,
                         VerticalAlignment = VerticalAlignment.Top
                     };
        var slot = new SlotHost(figlet) { SlotRect = new Rect(0, 0, 60, 40) };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(80, 50)
                                               });
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());

        return figlet.DesiredSize.Rows;
    }

    // ───────────────────── task #19a: Padding is a parse input — push freshness ─────────────────────

    /// <summary>
    /// <c>Padding</c> rides the PARSE (<c>rtb.Figlet(..., Padding)</c> — it becomes the figlet
    /// blocks' stacking margins), but it had neither a cache-key term nor a changed-callback on
    /// FigletPresenter: a padding change re-measured into a cache HIT, so the stale parse (the old
    /// block margins) laid out and painted forever. Font/Trimming/Wrapping/Text all carry the
    /// push-based callback; Padding was the one remaining uncovered parse input.
    /// </summary>
    [Fact]
    public void FigletPresenter_PaddingChange_InvalidatesTheCachedParse()
    {
        // Default (Stretch) alignment: the slot arranges the figlet at the full 40 rows, so the
        // re-measure's row budget (ResolveBounds folds current Bounds) can HOLD the padded
        // layout — a Top-aligned figlet is arranged at its old 10-row desired size and the fresh
        // parse would be row-capped right back down, hiding the very rows under test.
        var figlet = new FigletPresenter
                     {
                         Text = "A\nB" // two blocks — vertical padding shows up as inter-block rows
                     };
        var slot = new SlotHost(figlet) { SlotRect = new Rect(0, 0, 60, 40) };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(80, 50)
                                               });
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());

        var before = figlet.DesiredSize.Rows;

        figlet.Padding = new Margins(0, 2); // 2 rows of vertical margin per block ⇒ a 2-row gap
        Assert.True(host.RunUntilIdle());

        Assert.True(figlet.DesiredSize.Rows > before,
                    $"expected the padding to add inter-block rows (before={before}, after={figlet.DesiredSize.Rows})");
    }
}
