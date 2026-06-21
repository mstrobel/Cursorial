using System.Text;

using Cursorial.Gallery;
using Cursorial.Gallery.Pages;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Gallery;

// The standalone gallery (#107) is a real TTY app, so it can't be run in CI — this headless canary builds its shell +
// pages through UITestHost and asserts they render without error (the manual harness still gets exercised on every run).
public sealed class GallerySmokeTests(ITestOutputHelper output)
{
    private static string Screen(UITestHost host, int rows)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
            sb.AppendLine(host.GetRowText(r));
        return sb.ToString();
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                if (FindDescendant<T>(child) is { } found)
                    return found;
        return null;
    }

    [Fact]
    public void Shell_WithScrollViewerPage_RendersWithoutError()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var shell = new GalleryShell([new ScrollViewerPage()]);

        var ex = Record.Exception(() =>
        {
            host.ShowRoot(shell.Build());
            host.RunUntilIdle();
        });
        Assert.Null(ex);

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Gallery", screen);      // the title bar
        Assert.Contains("ScrollViewer", screen); // the nav entry
        Assert.Contains("V-bar", screen);        // the ScrollViewer page's toggle bar
        Assert.Contains("row 0", screen);        // the scrollable content
    }

    [Fact] // The chessboard page (#107): content-assisted scrolling snaps the offset to whole tiles via IScrollContentHost.
    public void ChessboardPage_SnapsScrollToWholeTiles()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var board = new Chessboard();
        var sv = new ScrollViewer
        {
            Content = board,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // v1 horizontal Auto degrades; Hidden scrolls without a bar
            Focusable = true,
        };
        host.ShowRoot(sv);
        host.RunUntilIdle();
        sv.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(4, sv.VerticalOffset);   // snapped a whole 4-row tile, not 1 cell

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(8, sv.HorizontalOffset); // snapped a whole 8-column tile

        // Reverse direction at an exact tile boundary snaps a WHOLE tile back (the audit-found boundary case — up/left
        // at a boundary must step the full tile, not degenerate to a 1-cell nudge).
        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(0, sv.VerticalOffset);   // back a whole 4-row tile (was 3 before the LineStep boundary fix)

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(0, sv.HorizontalOffset); // back a whole 8-column tile
    }

    [Fact] // The virtualized ListBox page (#107): a 10k-item list shows instantly (only the band realized) and End jumps
           // across the realization boundary, realizing + scrolling the last item into view (the V3/V3b proof).
    public void VirtualizedListPage_RealizesOnlyTheBand_AndEndJumpsAcrossBoundary()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var root = new VirtualizedListPage().Build();
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Contains("item 000000", Screen(host, 24)); // the top renders
        var lb = FindDescendant<ListBox>(root)!;
        var gen = lb.ItemContainerGenerator;

        gen.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();
        Assert.Null(gen.ContainerFromIndex(9999)); // the last item is far off-band — NOT realized (the virtualization proof)

        host.SendKey(Key.End); // jump to the last item: it scrolls into the window, realizes, then focuses
        host.RunUntilIdle();
        Assert.Equal(9999, lb.SelectedIndex);
        Assert.Contains("item 009999", Screen(host, 24)); // the off-band target materialized + scrolled into view
    }
}
