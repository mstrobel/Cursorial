using System.Text;

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI;

/// <summary>
/// <b>The regression net for the boundary-clip work.</b> Every assertion here is a property that has to
/// hold <em>before and after</em> any change to <c>RenderTree.ComputeClip</c>: the clipping that a caller
/// explicitly asked for must keep clipping. Each test renders through a real
/// <see cref="UIHeadlessHost"/> and asserts the whole composited grid, so a diff of the printed output is
/// the change, cell for cell.
/// </summary>
/// <remarks>
/// The tree shape is the same everywhere: a <see cref="Canvas"/> root filling the window, a full-window
/// <c>.</c> backdrop painted first, and the subject placed over it with <see cref="Canvas.LeftProperty"/>
/// / <see cref="Canvas.TopProperty"/>. Canvas arranges a child at its <em>desired</em> size, so a child
/// larger than its parent genuinely overflows — that is the only way to ask "does this clip?" honestly.
/// </remarks>
public sealed class BoundaryClipBaselineTests(ITestOutputHelper output)
{
    // ───────────────────────────── ① ScrollViewer clips at its viewport ─────────────────────────────

    /// <summary>
    /// <see cref="ScrollContentPresenter"/> is <c>IsAlwaysRenderBoundary</c> and the viewport clip is the
    /// whole point of it: the extent is 12 rows of 16 columns inside a 12×4 viewport, scrolled down by 3,
    /// and exactly the 12×4 window of it may reach the screen. Content above, below, and to the right of
    /// the viewport must not paint over the backdrop.
    /// </summary>
    [Fact]
    public void ScrolledScrollViewer_ClipsItsContentToTheViewport()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 10) });

        var scroller = new ScrollViewer
        {
            Width = 12,
            Height = 4,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = Rows("0123456789AB", 16),
        };

        host.ShowRoot(Scene(30, 10, scroller, left: 2, top: 2));
        Assert.True(host.RunUntilIdle());

        scroller.VerticalOffset = 3;
        Assert.True(host.RunUntilIdle());

        Assert.Equal(3, scroller.VerticalOffset);

        // The load-bearing detail: a scroll host's scene is BANDED (full content width, viewport + 2K
        // rows), so at this size the surface holds every cell of the content. Nothing is withheld by
        // running out of surface — the published viewport clip is the only thing standing between the
        // off-screen rows and the screen.
        var tree = host.Application.WindowManager!.Tree!;
        var presenter = scroller.Presenter!;
        Assert.Equal(new Size(16, 12), SceneSize(tree, presenter)); // 16 wide and all 12 rows
        Assert.Equal(new Rect(2, 2, 12, 4), tree.Parameters(presenter).Clip);

        AssertGrid(host, 30, 10,
                   "..............................",
                   "..............................",
                   "..333333333333................",
                   "..444444444444................",
                   "..555555555555................",
                   "..666666666666................",
                   "..............................",
                   "..............................",
                   "..............................",
                   "..............................");
    }

    /// <summary>
    /// The same viewport clip with no scroll at all — the horizontal leg. The content is 16 columns wide
    /// in a 12-column viewport, so four columns fall off the right edge with nothing scrolled anywhere.
    /// </summary>
    [Fact]
    public void UnscrolledScrollViewer_ClipsContentWiderThanTheViewport()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 10) });

        var scroller = new ScrollViewer
        {
            Width = 12,
            Height = 4,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = Rows("0123456789AB", 16),
        };

        host.ShowRoot(Scene(30, 10, scroller, left: 2, top: 2));
        Assert.True(host.RunUntilIdle());

        Assert.Equal(0, scroller.VerticalOffset);
        AssertGrid(host, 30, 10,
                   "..............................",
                   "..............................",
                   "..000000000000................",
                   "..111111111111................",
                   "..222222222222................",
                   "..333333333333................",
                   "..............................",
                   "..............................",
                   "..............................",
                   "..............................");
    }

    /// <summary>
    /// The viewport clip reaching a <b>boundary</b> descendant — a different code path from the two above,
    /// and the one most at risk. A boundary inside the scrolled content owns its own zone and its own
    /// composited layer, so nothing about the presenter's surface constrains it: only
    /// <c>ComputeClip</c>'s intersection with the presenter's clip keeps it inside the viewport. Here row
    /// <c>1</c> is a boundary that is four columns too wide, and row <c>6</c> is a boundary that sits
    /// entirely below the viewport — inside the realization band, so it really is rastered.
    /// </summary>
    [Fact]
    public void ScrollViewer_ClipsBoundaryDescendantsToTheViewportToo()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 10) });

        var content = Rows("0123456789AB", 16);
        var inside = (Probe)content.Children[1];  // visible, but 16 columns wide in a 12-column viewport
        var below = (Probe)content.Children[6];   // entirely below the viewport
        inside.IsRenderBoundary = true;
        below.IsRenderBoundary = true;

        var scroller = new ScrollViewer
        {
            Width = 12,
            Height = 4,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = content,
        };

        host.ShowRoot(Scene(30, 10, scroller, left: 2, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        Assert.Equal(new Rect(2, 3, 12, 1), tree.Parameters(inside).Clip); // cut to the viewport's width
        Assert.Equal(Rect.Empty, tree.Parameters(below).Clip);             // outside it entirely

        AssertGrid(host, 30, 10,
                   "..............................",
                   "..............................",
                   "..000000000000................",
                   "..111111111111................",
                   "..222222222222................",
                   "..333333333333................",
                   "..............................",
                   "..............................",
                   "..............................",
                   "..............................");
    }

    // ───────────────────────────── ② ClipToBounds clips ─────────────────────────────

    /// <summary>
    /// The opt-in. A <c>10×4</c> child sits at <c>(3, 1)</c> inside an <c>8×3</c> canvas, so it overflows
    /// five columns right and two rows down; <see cref="UIElement.ClipToBounds"/> is what says "cut that
    /// off". The surviving rectangle is the intersection of the two, and nothing outside the parent's own
    /// footprint may paint.
    /// </summary>
    [Fact]
    public void ClipToBounds_ClipsAChildThatOverflowsItsParent()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var clipper = Overflowing(clipToBounds: true);

        host.ShowRoot(Scene(24, 8, clipper, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        // clipper occupies window columns 4..11, rows 2..4; the child columns 7..16, rows 3..6.
        // The intersection is columns 7..11 × rows 3..4.
        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   ".......#####............",
                   ".......#####............",
                   "........................",
                   "........................",
                   "........................");
    }

    /// <summary>
    /// <see cref="UIElement.ClipToBounds"/> on the element that overflows its <em>own</em> bounds, rather
    /// than on its parent: a <c>10×4</c> canvas whose child is arranged at <c>(6, 2)</c> at its full
    /// <c>8×3</c> desired size hangs four columns and one row outside itself.
    /// </summary>
    [Fact]
    public void ClipToBounds_ClipsAnElementsOwnOverflowingContent()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var clipper = new Canvas { Width = 10, Height = 4, ClipToBounds = true };
        var child = new Probe(8, 3) { FillGlyph = "#" };
        clipper.Children.Add(child);
        Canvas.SetLeft(child, 6);
        Canvas.SetTop(child, 2);

        host.ShowRoot(Scene(24, 8, clipper, left: 3, top: 1));
        Assert.True(host.RunUntilIdle());

        // clipper: columns 3..12, rows 1..4. child: columns 9..16, rows 3..5.
        // Intersection: columns 9..12 × rows 3..4.
        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   ".........####...........",
                   ".........####...........",
                   "........................",
                   "........................",
                   "........................");
    }

    /// <summary>
    /// <see cref="UIElement.ClipToBounds"/> cutting a child that is itself a render boundary. The child
    /// has its own zone and its own surface — big enough to hold it entirely — so the only thing that can
    /// cut it is the published clip.
    /// </summary>
    [Fact]
    public void ClipToBounds_ClipsAnOverflowingBoundaryChild()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var clipper = Overflowing(clipToBounds: true, boundaryChild: true);

        host.ShowRoot(Scene(24, 8, clipper, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        Assert.Equal(new Rect(7, 3, 5, 2), tree.Parameters(clipper.Children[0]).Clip);

        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   ".......#####............",
                   ".......#####............",
                   "........................",
                   "........................",
                   "........................");
    }

    // ───────────────────────────── ③ CompositeClip clips ─────────────────────────────

    /// <summary>
    /// The explicit composite-time clip, in element-local coordinates, on a leaf that paints its whole
    /// footprint. Only the named rectangle reaches the screen.
    /// </summary>
    [Fact]
    public void CompositeClip_ClipsALeafToTheNamedRectangle()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var subject = new Probe(10, 4) { FillGlyph = "#", CompositeClip = new Rect(2, 1, 4, 2) };

        host.ShowRoot(Scene(24, 8, subject, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        // subject: columns 4..13, rows 2..5. CompositeClip (2, 1, 4, 2) → columns 6..9, rows 3..4.
        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   "......####..............",
                   "......####..............",
                   "........................",
                   "........................",
                   "........................");
    }

    /// <summary>
    /// <see cref="UIElement.CompositeClip"/> on a container, cutting a child that overflows it. This is
    /// the combination the fix must not weaken: an explicit clip narrower than the element's own bounds,
    /// applied to content that legitimately extends past both.
    /// </summary>
    [Fact]
    public void CompositeClip_ClipsAnOverflowingChild()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var clipper = Overflowing(clipToBounds: false);
        clipper.CompositeClip = new Rect(0, 0, 6, 2); // element-local, narrower than the 8×3 bounds

        host.ShowRoot(Scene(24, 8, clipper, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        // clipper: columns 4..11, rows 2..4; composite clip → columns 4..9, rows 2..3.
        // The child starts at column 7, row 3 → the survivor is columns 7..9 × row 3.
        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   ".......###..............",
                   "........................",
                   "........................",
                   "........................",
                   "........................");
    }

    /// <summary>
    /// <see cref="UIElement.CompositeClip"/> cutting a child that is itself a render boundary — the
    /// boundary-child leg of the explicit composite clip.
    /// </summary>
    [Fact]
    public void CompositeClip_ClipsAnOverflowingBoundaryChild()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var clipper = Overflowing(clipToBounds: false, boundaryChild: true);
        clipper.CompositeClip = new Rect(0, 0, 6, 2);

        host.ShowRoot(Scene(24, 8, clipper, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        Assert.Equal(new Rect(4, 2, 6, 2), tree.Parameters(clipper).Clip);
        Assert.Equal(new Rect(7, 3, 3, 1), tree.Parameters(clipper.Children[0]).Clip);

        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   ".......###..............",
                   "........................",
                   "........................",
                   "........................",
                   "........................");
    }

    // ───────────────────────────── ④ the scene extent still clamps ─────────────────────────────

    /// <summary>
    /// The window has no negative cell. An element pulled left and up by a negative margin so that part of
    /// it falls off the top-left corner must paint only the part that is on screen — and must not wrap,
    /// throw, or shift.
    /// </summary>
    [Fact]
    public void NegativeMargin_AtTheWindowCorner_PaintsOnlyTheOnScreenPart()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(16, 6) });

        var subject = new Probe(6, 3) { FillGlyph = "#", Margin = new Margins(-2, -1, 0, 0) };

        host.ShowRoot(Scene(16, 6, subject, left: 0, top: 0));
        Assert.True(host.RunUntilIdle());

        AssertGrid(host, 16, 6,
                   "####............",
                   "####............",
                   "................",
                   "................",
                   "................",
                   "................");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>An <c>8×3</c> canvas whose <c>10×4</c> child is arranged at <c>(3, 1)</c> — overflow by construction.</summary>
    private static Canvas Overflowing(bool clipToBounds, bool boundaryChild = false)
    {
        var clipper = new Canvas { Width = 8, Height = 3, ClipToBounds = clipToBounds };
        var child = new Probe(10, 4) { FillGlyph = "#", IsRenderBoundary = boundaryChild };
        clipper.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);
        return clipper;
    }

    private static Size SceneSize(RenderTree tree, UIElement boundary)
    {
        var scene = tree.GetScene(boundary)!;
        return new Size(scene.Columns, scene.Rows);
    }

    /// <summary>A stack of one-row probes, one glyph per row, each <paramref name="columns"/> wide.</summary>
    private static StackPanel Rows(string glyphs, int columns)
    {
        var stack = new StackPanel();
        foreach (var glyph in glyphs)
            stack.Children.Add(new Probe(columns, 1) { FillGlyph = glyph.ToString() });

        return stack;
    }

    /// <summary>A window-filling canvas: a <c>.</c> backdrop with <paramref name="subject"/> placed over it.</summary>
    private static Canvas Scene(int columns, int rows, UIElement subject, int left, int top)
    {
        var root = new Canvas();
        var backdrop = new Probe(columns, rows) { FillGlyph = "." };
        root.Children.Add(backdrop);
        Canvas.SetLeft(backdrop, 0);
        Canvas.SetTop(backdrop, 0);

        root.Children.Add(subject);
        Canvas.SetLeft(subject, left);
        Canvas.SetTop(subject, top);
        return root;
    }

    private void AssertGrid(UIHeadlessHost host, int columns, int rows, params string[] expected)
    {
        var actual = new string[rows];
        for (var row = 0; row < rows; row++)
            actual[row] = host.GetRowText(row);

        output.WriteLine(Render("expected", expected, columns));
        output.WriteLine(Render("actual  ", actual, columns));
        Assert.Equal(expected, actual);
    }

    private static string Render(string label, IReadOnlyList<string> grid, int columns)
    {
        var text = new StringBuilder();
        text.AppendLine($"── {label} ──  0123456789".PadRight(0));
        for (var row = 0; row < grid.Count; row++)
            text.AppendLine($"{row,3} |{grid[row]}|");

        text.AppendLine($"    {new string('─', columns)}");
        return text.ToString();
    }
}
