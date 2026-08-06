// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// The reported shape, with real controls: a text presenter carrying a <b>negative</b> margin
/// (<c>Margin="-6,0,-6,0"</c>). Two independent hazards meet here.
/// <para>
/// First, <see cref="UIElement.Bounds"/> is a <c>LayoutRect</c> precisely so it can hold a negative
/// origin, and a negative left margin puts one there. The presenters' <c>ResolveBounds</c> narrowed
/// it through <c>LayoutRect.ToRect()</c> — the explicit affordance for a caller that has
/// <em>proven</em> a non-negative origin — so measure threw once the first arrange had happened.
/// Both callers only read <c>.Columns</c> / <c>.Rows</c>, so the narrowing bought nothing.
/// </para>
/// <para>
/// Second, a text presenter is a forced render boundary (<c>DrawnContentPresenter</c> coerces
/// <c>ClipToBounds</c> to true), so it always paints into its own zone at origin <c>(0, 0)</c> and
/// reaches the window through the compositor's offset. Its text must survive that route unchanged
/// whether or not the margin pulled it out of its slot.
/// </para>
/// </summary>
public sealed class NegativeMarginPresenterTests
{
    private const int WindowColumns = 32;
    private const int WindowRows = 6;

    [Theory]
    [InlineData(-6, 10)]  // pulled left, still inside the window
    [InlineData(-6, 0)]   // pulled off the window's left edge
    [InlineData(-2, 4)]
    public void RichTextPresenter_WithANegativeMargin_LaysOutAndRenders(int marginLeft, int slotColumn)
    {
        var block = new RichTextPresenter
                    {
                        Source = "ABCDEFGHIJKL",
                        Margin = new Margins(marginLeft, 0, marginLeft, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top
                    };

        var root = new SlotHost(block)
                   {
                       SlotRect = new Rect(slotColumn, 0, WindowColumns - slotColumn, WindowRows)
                   };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);

        // Several frames: ResolveBounds reads the PREVIOUS pass's Bounds, so the negative origin only
        // reaches it on the second and later measures — a first-frame-only assertion would miss it.
        Assert.True(host.RunUntilIdle());
        block.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());

        Assert.Equal(slotColumn + marginLeft, block.Bounds.Column);

        // Whatever of the text is on screen, is on screen at the arranged origin.
        var expected = "ABCDEFGHIJKL";
        var start = slotColumn + marginLeft;
        for (var i = 0; i < expected.Length; i++)
        {
            var column = start + i;
            if (column < 0 || column >= WindowColumns) continue;
            Assert.Equal(expected[i].ToString(), host.GetCell(column, 0).Grapheme);
        }
    }

    /// <summary>
    /// The same for the FIGlet presenter, whose <c>ResolveBounds</c> clamped the origin to zero before
    /// narrowing — no throw, but the same gratuitous narrowing of a coordinate its callers never read.
    /// </summary>
    [Theory]
    [InlineData(-6, 10)]
    [InlineData(-6, 0)]
    public void FigletPresenter_WithANegativeMargin_LaysOutAndRenders(int marginLeft, int slotColumn)
    {
        var figlet = new FigletPresenter
                     {
                         Text = "AB",
                         Margin = new Margins(marginLeft, 0, marginLeft, 0),
                         HorizontalAlignment = HorizontalAlignment.Left,
                         VerticalAlignment = VerticalAlignment.Top
                     };

        var root = new SlotHost(figlet)
                   {
                       SlotRect = new Rect(slotColumn, 0, WindowColumns - slotColumn, WindowRows)
                   };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        figlet.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());

        Assert.Equal(slotColumn + marginLeft, figlet.Bounds.Column);
        Assert.NotEqual(0, InkCells(host));   // the headline rendered something
    }

    private static int InkCells(UIHeadlessHost host)
    {
        var count = 0;
        for (var row = 0; row < WindowRows; row++)
        for (var column = 0; column < WindowColumns; column++)
        {
            var grapheme = host.GetCell(column, row).Grapheme;
            if (!string.IsNullOrEmpty(grapheme) && grapheme != " ") count++;
        }

        return count;
    }
}
