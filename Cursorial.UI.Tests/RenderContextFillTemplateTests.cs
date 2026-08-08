// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// <see cref="RenderContext"/>'s rectangle fills take a <see cref="StyleDeltaTemplate"/> too — and
/// the veneer's own DEFAULTS are the thing to pin. <c>FillOpaque</c> narrows
/// <see cref="Cursorial.Drawing.DrawingContext"/>'s <c>overwrite: true</c> to <c>false</c> at this
/// boundary, and that narrower default is load-bearing: it is what lets
/// <c>TextPresenter</c>'s inverse band fill paint OVER a FIGlet face without erasing the ink, because
/// <see cref="CellBuffer.Set"/> rescues the grapheme only on the non-overwriting path.
/// <para>
/// Every assertion reads the RENDERED frame.
/// </para>
/// </summary>
public sealed class RenderContextFillTemplateTests
{
    private static readonly Color Teal = Color.FromRgb(0, 128, 128);

    private const int Columns = 12;
    private const int Rows = 4;

    /// <summary>An element that hands its render context straight to the test.</summary>
    private sealed class Painter(Action<RenderContext> draw) : UIElement
    {
        protected override Size MeasureOverride(Size availableSize) => availableSize;

        protected override void Render(RenderContext context) => draw(context);
    }

    private static UIHeadlessHost Shown(Action<RenderContext> draw)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(Columns, Rows) });
        host.ShowRoot(new Painter(draw));
        host.RunUntilIdle();
        return host;
    }

    /// <summary>
    /// The template reaches the primitive through the veneer: the background lands and so do the
    /// attribute channels a <see cref="Color"/> plus a flag word could carry only between them.
    /// </summary>
    [Fact]
    public void FillOpaque_Template_PaintsThroughTheVeneer()
    {
        using var host = Shown(ctx => ctx.FillOpaque(new Rect(0, 0, 2, 1),
                                                     new StyleDeltaTemplate { Background = new SolidColorBrush(Teal) }
                                                         .Applying(TextAttributes.Inverse)));

        Assert.Equal(Teal, host.GetCell(0, 0).Style.Background);
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse),
                    $"Inverse never arrived: {host.GetCell(0, 0).Style.Attributes}");
    }

    /// <summary>
    /// <b>Trap 3.</b> <c>RenderContext.FillOpaque</c> defaults to <c>overwrite: false</c> — narrower
    /// than the drawing layer's <c>true</c> — so the glyph underneath survives. The band fill an
    /// inverse glyph editor paints under its face is exactly this call, and the default is what keeps
    /// the ink.
    /// </summary>
    [Fact]
    public void FillOpaque_Template_DefaultsToNotOverwriting_SoTheGlyphUnderneathSurvives()
    {
        using var host = Shown(ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillOpaque(new Rect(0, 0, 2, 1),
                           new StyleDeltaTemplate { Background = Brushes.Transparent }
                               .Applying(TextAttributes.Inverse));
        });

        Assert.Equal("X", host.GetCell(0, 0).Grapheme);
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse),
                    $"the fill's attribute never arrived: {host.GetCell(0, 0).Style.Attributes}");
    }

    /// <summary>...and the same call with the existing <see cref="Color"/> overload — the one
    /// <c>TextPresenter</c> actually makes — behaves identically, which is what "the wrappers are
    /// unchanged" has to mean at this boundary.</summary>
    [Fact]
    public void FillOpaque_ColorOverload_KeepsItsNarrowerDefault()
    {
        using var host = Shown(ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillOpaque(new Rect(0, 0, 2, 1), Colors.Transparent, TextAttributes.Inverse);
        });

        Assert.Equal("X", host.GetCell(0, 0).Grapheme);
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse),
                    $"the fill's attribute never arrived: {host.GetCell(0, 0).Style.Attributes}");
    }

    /// <summary>
    /// The word an element hands the veneer may carry AXIS-OWNING flags — <c>TextPresenter</c>'s
    /// allowlist admits Bold, Faint and Italic — and the fold must decompose per axis rather than
    /// throwing. Through <see cref="RenderContext"/>, on a real frame.
    /// </summary>
    [Fact]
    public void FillOpaque_WordCarryingAxisFlags_ReachesTheFrame()
    {
        var word = TextAttributes.Bold | TextAttributes.Italic | TextAttributes.Inverse;

        using var host = Shown(ctx => ctx.FillOpaque(new Rect(0, 0, 2, 1), Teal, word, overwrite: true));

        Assert.Equal(word, host.GetCell(0, 0).Style.Attributes & word);
    }

    /// <summary>
    /// <c>FillRectangle</c> stays background-only through the template overload: no glyph is written,
    /// so the cell the compositor produces keeps whatever grapheme was beneath it.
    /// </summary>
    [Fact]
    public void FillRectangle_Template_StaysBackgroundOnly()
    {
        using var host = Shown(ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillRectangle(new Rect(0, 0, 2, 1), new StyleDeltaTemplate { Background = new SolidColorBrush(Teal) });
        });

        // The raw write cleared the glyph WITHIN the scene (FillRectangle has no overwrite switch),
        // and the cell carries the fill's background rather than a space.
        Assert.True(string.IsNullOrEmpty(host.GetCell(0, 0).Grapheme), $"a glyph survived: '{host.GetCell(0, 0).Grapheme}'");
        Assert.Equal(Teal, host.GetCell(0, 0).Style.Background);
    }

    /// <summary><c>PaintRectangle</c> keeps its own <c>overwrite: false</c> default through the
    /// template overload — the intra-scene blend, which leaves a same-scene glyph standing.</summary>
    [Fact]
    public void PaintRectangle_Template_DefaultsToNotOverwriting()
    {
        using var host = Shown(ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.PaintRectangle(new Rect(0, 0, 2, 1), new StyleDeltaTemplate { Background = Brushes.Transparent });
        });

        Assert.Equal("X", host.GetCell(0, 0).Grapheme);
    }
}
