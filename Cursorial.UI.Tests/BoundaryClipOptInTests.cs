// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI;

/// <summary>
/// The clip-inheritance contract: <b>a boundary clips its descendants only when it said so.</b>
/// <c>ClipToBounds</c>, a <see cref="UIElement.CompositeClip"/> and a scroll viewport are the opt-ins;
/// mere promotion to a render boundary — <see cref="UIElement.IsRenderBoundary"/>, a sub-1
/// <see cref="UIElement.Opacity"/>, a non-zero render offset — is a compositing hint and must not
/// clip anything (WPF: a parent does not clip its children unless it says so).
/// </summary>
/// <remarks>
/// <para>
/// The reported symptom: a <see cref="RichTextPresenter"/> with <c>Margin="-6,0,-6,0"</c> measured
/// 57×6 naturally, so <c>DesiredSize</c> was 45×6 and its arranged <c>Bounds</c> were 57×6 at
/// column −6 — correct layout, matching WPF's "negative margins enlarge the arranged rect". It
/// rendered centred and intact but short exactly 6 columns per side: clipped to [0, 45), the
/// <em>pre-expansion</em> slot, because an ancestor 45 columns wide happened to be a render
/// boundary.
/// </para>
/// <para>
/// Two mechanisms produce that same visible cut, and only one of them is a clip. A <b>boundary</b>
/// child rasters into its own correctly-sized <c>Scene</c> and is withheld at composite time by
/// <c>RenderTree.ComputeClip</c>'s ancestor intersection — recoverable, and what these tests pin. An
/// <b>inline</b> child paints into its ancestor boundary's <c>Scene</c>, which is rented at that
/// boundary's bounds, so its overflow never enters any buffer — a surface-extent limit, not a clip,
/// and documented as such by <see cref="InlineChild_OfANonClippingBoundary_StillStopsAtTheZoneSurface"/>.
/// </para>
/// </remarks>
public sealed class BoundaryClipOptInTests(ITestOutputHelper output)
{
    /// <summary>24 distinct glyphs, one per cell — a loss of any column is unambiguous.</summary>
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWX";

    // ───────────────────────────── ① the reported symptom ─────────────────────────────

    /// <summary>
    /// The maintainer's repro, with the real control. <see cref="RichTextPresenter"/> is a boundary
    /// from attach (<c>DrawnContentPresenter</c> coerces <c>ClipToBounds</c>), so its own 24×1 scene
    /// holds every glyph; the container is a boundary only because <c>IsRenderBoundary</c> was set,
    /// which asks for compositing, not for clipping. Every glyph must reach the screen.
    /// </summary>
    [Fact]
    public void RichTextPresenter_WithSymmetricNegativeMargins_IsNotClippedByANonClippingBoundary()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 4) });

        var text = new RichTextPresenter
                   {
                       Source = Alphabet,
                       Margin = new Margins(-6, 0, -6, 0),
                       HorizontalAlignment = HorizontalAlignment.Left,
                       VerticalAlignment = VerticalAlignment.Top
                   };

        // A container sized to the child's DesiredSize (24 − 2·6 = 12) and centred: the arrange slot
        // is 12 wide, the child re-expands to 24 at local −6. Exactly the maintainer's 45/57 shape.
        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsRenderBoundary = true
                    };
        panel.Children.Add(text);

        var root = new StackPanel();
        root.Children.Add(panel);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        text.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        output.WriteLine($"panel.Bounds     = {panel.Bounds}");
        output.WriteLine($"text.Bounds      = {text.Bounds}   (negative origin — layout is correct)");
        output.WriteLine($"panel.Clip       = {tree.Parameters(panel).Clip}");
        output.WriteLine($"text.Clip        = {tree.Parameters(text).Clip}");
        output.WriteLine($"text scene       = {SceneSize(tree, text)}   (the raster holds every glyph)");
        output.WriteLine($"row 0            = \"{host.GetRowText(0)}\"");

        // The child spans [8, 32); the container's own footprint is the 12 columns [14, 26).
        Assert.Equal(new Rect(-6, 0, 24, 1), text.Bounds);
        Assert.Equal(new Size(24, 1), SceneSize(tree, text));
        Assert.Equal(new Rect(8, 0, 24, 1), tree.Parameters(text).Clip);
        Assert.Equal(Alphabet, host.GetRowText(0).Trim());
    }

    /// <summary>
    /// The same shape without the text stack: a boundary child pulled out of a 6-column container by
    /// symmetric <c>-3</c> margins. With the container a plain (non-boundary) parent this already
    /// paints in full; setting <c>IsRenderBoundary</c> must not change a single cell.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundaryChild_PulledOutByNegativeMargins_PaintsInFull_BoundaryParentOrNot(bool boundaryParent)
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 6) });

        var (parent, child) = PulledOut(boundaryParent, boundaryChild: true);
        host.ShowRoot(Backdrop(24, 6, parent, left: 8, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        output.WriteLine($"boundaryParent={boundaryParent}  child.Bounds={child.Bounds}  child.Clip={tree.Parameters(child).Clip}");

        Assert.Equal(new Rect(-3, 0, 12, 2), child.Bounds);
        Assert.Equal(new Rect(5, 2, 12, 2), tree.Parameters(child).Clip);

        AssertGrid(host, 24, 6,
                   "........................",
                   "........................",
                   ".....############.......",
                   ".....############.......",
                   "........................",
                   "........................");
    }

    /// <summary>
    /// The other two promotion-without-intent predicates, on the vertical axis as well: a render
    /// offset on the parent, and a translucent <em>leaf</em> boundary parent (no descendant boundary
    /// to group, so no group surface is materialised and nothing bounds the overflow).
    /// </summary>
    [Fact]
    public void RenderOffsetParent_DoesNotClipItsBoundaryChild()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var parent = new Canvas { Width = 8, Height = 3, RenderOffsetColumn = 1 };
        var child = new Probe(10, 4) { FillGlyph = "#", IsRenderBoundary = true };
        parent.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);

        host.ShowRoot(Backdrop(24, 8, parent, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        output.WriteLine($"child.Clip = {tree.Parameters(child).Clip}");

        // parent at window (4,2) + render offset 1 → (5,2); child at local (3,1) → (8,3), 10×4.
        Assert.Equal(new Rect(8, 3, 10, 4), tree.Parameters(child).Clip);

        AssertGrid(host, 24, 8,
                   "........................",
                   "........................",
                   "........................",
                   "........##########......",
                   "........##########......",
                   "........##########......",
                   "........##########......",
                   "........................");
    }

    // ───────────────────────────── ② the opt-ins still clip ─────────────────────────────

    /// <summary>
    /// <c>ClipToBounds</c> is the opt-in and must keep cutting an overflowing <b>boundary</b> child —
    /// the child's own 10×4 scene holds it entirely, so only the inherited clip can withhold it.
    /// </summary>
    [Fact]
    public void ClipToBoundsParent_StillClipsAnOverflowingBoundaryChild()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var parent = new Canvas { Width = 8, Height = 3, ClipToBounds = true };
        var child = new Probe(10, 4) { FillGlyph = "#", IsRenderBoundary = true };
        parent.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);

        host.ShowRoot(Backdrop(24, 8, parent, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        Assert.Equal(new Size(10, 4), SceneSize(tree, child)); // rastered in full — the clip is what cuts
        Assert.Equal(new Rect(7, 3, 5, 2), tree.Parameters(child).Clip);

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

    /// <summary>A <see cref="UIElement.CompositeClip"/> is the other opt-in and must still cut a boundary child.</summary>
    [Fact]
    public void CompositeClipParent_StillClipsAnOverflowingBoundaryChild()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var parent = new Canvas { Width = 8, Height = 3, CompositeClip = new Rect(0, 0, 6, 2) };
        var child = new Probe(10, 4) { FillGlyph = "#", IsRenderBoundary = true };
        parent.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);

        host.ShowRoot(Backdrop(24, 8, parent, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        Assert.Equal(new Rect(4, 2, 6, 2), tree.Parameters(parent).Clip);
        Assert.Equal(new Rect(7, 3, 3, 1), tree.Parameters(child).Clip);

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
    /// The opt-in is <b>inherited</b>, not merely applied one level: a clip-to-bounds ancestor keeps
    /// cutting a boundary grandchild that reaches it through a non-clipping boundary in between.
    /// </summary>
    [Fact]
    public void ClipToBoundsAncestor_StillClipsThroughANonClippingBoundary()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var outer = new Canvas { Width = 8, Height = 3, ClipToBounds = true };
        var middle = new Canvas { Width = 8, Height = 3, IsRenderBoundary = true }; // promoted, never asked to clip
        var child = new Probe(10, 4) { FillGlyph = "#", IsRenderBoundary = true };
        middle.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);
        outer.Children.Add(middle);
        Canvas.SetLeft(middle, 0);
        Canvas.SetTop(middle, 0);

        host.ShowRoot(Backdrop(24, 8, outer, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        output.WriteLine($"middle.Clip = {tree.Parameters(middle).Clip}   child.Clip = {tree.Parameters(child).Clip}");

        Assert.Equal(new Rect(4, 2, 8, 3), tree.Parameters(middle).Clip);
        Assert.Equal(new Rect(7, 3, 5, 2), tree.Parameters(child).Clip); // outer's 8×3 still bounds it

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

    // ───────────────────────────── ③ the documented surface limits ─────────────────────────────

    /// <summary>
    /// <b>Accepted limit, not a clip.</b> An inline (non-boundary) child paints into its ancestor
    /// boundary's <c>Scene</c>, which <c>RenderTree.EnsureScene</c> rents at the boundary's bounds.
    /// Its overflow never enters a buffer, so no composite-time change can recover it; recovering it
    /// needs ink-extent-sized zone surfaces, which is a separate piece of work.
    /// </summary>
    [Fact]
    public void InlineChild_OfANonClippingBoundary_StillStopsAtTheZoneSurface()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var parent = new Canvas { Width = 8, Height = 3, IsRenderBoundary = true };
        var child = new Probe(10, 4) { FillGlyph = "#" }; // inline — no boundary predicate holds
        parent.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);

        host.ShowRoot(Backdrop(24, 8, parent, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        Assert.Equal(new Size(8, 3), SceneSize(tree, parent));            // no room on the surface
        Assert.Equal(new Rect(4, 2, 8, 3), tree.Parameters(parent).Clip); // the clip is NOT what cuts

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
    /// <b>Accepted limit, not a clip.</b> A translucent boundary with descendant boundaries roots an
    /// opacity group, and its whole subtree composites into a private surface sized to the group
    /// root's own scene. A member's overflow has no cells on that surface to land in, so a group root
    /// keeps bounding its subtree — and must, or <c>VerifyGroupContainment</c> fires and
    /// <c>CellBuffer.AddFragment</c> throws.
    /// </summary>
    [Fact]
    public void GroupRoot_StillBoundsItsSubtreeToItsGroupSurface()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 8) });

        var parent = new Canvas { Width = 8, Height = 3, Opacity = 0.5 };
        var child = new Probe(10, 4) { FillGlyph = "#", IsRenderBoundary = true };
        parent.Children.Add(child);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 1);

        host.ShowRoot(Backdrop(24, 8, parent, left: 4, top: 2));
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        output.WriteLine($"parent.Clip = {tree.Parameters(parent).Clip}   child.Clip = {tree.Parameters(child).Clip}");

        // The group surface is 8×3 at (4,2); the member is contained in it, exactly as before.
        Assert.Equal(new Rect(7, 3, 5, 2), tree.Parameters(child).Clip);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>
    /// The maintainer's shape: a container that sizes to its child's <see cref="UIElement.DesiredSize"/>
    /// and arranges the child into exactly that slot. The child's <c>-3</c> horizontal margins shrink
    /// the desired size to 6 and re-expand the arranged rect back to 12 at column <c>-3</c>.
    /// </summary>
    private static (Host Parent, Probe Child) PulledOut(bool boundaryParent, bool boundaryChild)
    {
        var parent = new Host { IsRenderBoundary = boundaryParent };
        var child = new Probe(12, 2)
                    {
                        FillGlyph = "#",
                        Margin = new Margins(-3, 0, -3, 0),
                        IsRenderBoundary = boundaryChild
                    };
        parent.Add(child);
        return (parent, child);
    }

    private static Size SceneSize(RenderTree tree, UIElement boundary)
    {
        var scene = tree.GetScene(boundary)!;
        return new Size(scene.Columns, scene.Rows);
    }

    private static Canvas Backdrop(int columns, int rows, UIElement subject, int left, int top)
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
        text.AppendLine($"── {label} ──");
        for (var row = 0; row < grid.Count; row++)
            text.AppendLine($"{row,3} |{grid[row]}|");

        text.AppendLine($"    {new string('─', columns)}");
        return text.ToString();
    }
}
