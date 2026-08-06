// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// The P2.6 signed-margin batch (matrix LD19), proven end-to-end through <see cref="UIHeadlessHost"/>:
/// the <c>UIPanelsDemo</c> sidebar pull-up — a label with <c>Margin = (0, −1, 0, 0)</c> inside a
/// <c>Spacing = 1</c> StackPanel pulls itself onto the spacing row beneath its predecessor (and
/// everything below moves up one row). Matrix §13 covers the unit-level rows; this test crosses
/// the full spine (frame loop → layout → render zones → compositor → retained buffer).
/// </summary>
public sealed class SignedMarginIntegrationTests
{
    private static readonly Color Ink = Color.FromRgb(220, 224, 240);

    [Fact]
    public void UIPanelsDemo_PullUpLabel_NegativeTopMargin_LandsOnTheSpacingRow()
    {
        // The demo shape (UIPanelsDemo.BuildSidebar): keys rendered as two adjacent description
        // lines inside a Spacing = 1 stack — the second line's top margin of −1 eats the gap.
        var first = Label("cards=hand");
        var pulled = Label("glass=I-beam", new Margins(0, -1, 0, 0));
        var next = Label("slide panel");
        var sidebar = new StackPanel { Spacing = 1 };
        sidebar.Children.Add(first);
        sidebar.Children.Add(pulled);
        sidebar.Children.Add(next);

        using var host = UIHeadlessHost.Create();
        host.ShowRoot(sidebar);
        Assert.True(host.RunUntilIdle());

        // Layout: the pulled label's desired height clamps to 0 (L37), its bounds land on the
        // spacing row (slot row 2, minus the margin), and the rest of the stack moves up one row.
        Assert.Equal(new Size(12, 0), pulled.DesiredSize);
        Assert.Equal(new Rect(0, 0, 10, 1), first.Bounds);
        Assert.Equal(new Rect(0, 1, 12, 1), pulled.Bounds); // the gap row — adjacent to its predecessor
        Assert.Equal(new Rect(0, 3, 11, 1), next.Bounds);   // spacing still applies below

        // Cells: the overlap renders — the pulled label paints on the row directly beneath the
        // first label, the next spacing gap stays blank, and the following label sits below it.
        Assert.Equal("c", host.GetCell(0, 0).Grapheme);  // "cards=hand"
        Assert.Equal("g", host.GetCell(0, 1).Grapheme);  // "glass=I-beam" — pulled up one row
        Assert.Equal("m", host.GetCell(11, 1).Grapheme); // its full text rendered
        Assert.Null(host.GetCell(0, 2).Grapheme);        // the spacing row below stays blank
        Assert.Equal("s", host.GetCell(0, 3).Grapheme);  // "slide panel"
    }

    private static Probe Label(string text, Margins margin = default) => new(text.Length, 1)
    {
        Margin = margin,
        HorizontalAlignment = HorizontalAlignment.Left,
        OnRender = (_, context) => context.DrawText(0, 0, text, Ink)
    };
}
