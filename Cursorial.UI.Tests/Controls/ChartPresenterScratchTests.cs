using Cursorial.Drawing.Charts;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

// ChartPresenter caches a compositing buffer for the ILayeredChart path. These pin that the cache
// stays CORRECT across the lifecycle events that invalidate it — a stale or wrongly-sized scratch
// renders silently wrong rather than crashing, so the guards need behavioural cover.
public sealed class ChartPresenterScratchTests
{
    private static readonly Color Green = Color.FromRgb(0, 200, 0);

    private static MultiLineChart Chart() => new(
    [
        new ChartSeries([new PointD(0, 0), new PointD(9, 9)], Green),
    ]);

    private static int PaintedCells(UIHeadlessHost host, int columns, int rows)
    {
        int painted = 0;
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
            if (!string.IsNullOrEmpty(host.FrameBuffer[c, r].Grapheme)) painted++;
        return painted;
    }

    [Fact] // the scratch is released outright when the presenter leaves the tree
    public void Detach_ReleasesTheScratch()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(), Width = 20, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var panel = new StackPanel();
        panel.Children.Add(presenter);
        host.ShowRoot(panel);
        host.RunUntilIdle();
        Assert.NotNull(presenter.CompositingScratchSize);

        panel.Children.Remove(presenter);
        host.RunUntilIdle();
        Assert.Null(presenter.CompositingScratchSize);   // a detached page must not hold a full buffer
    }

    [Fact] // the scratch is an intermediate surface: its BLANK must be transparent, not merely its contents
    public void Scratch_BlankIsTransparent()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(), Width = 20, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        // Cells the chart doesn't paint are blitted onwards and must contribute nothing. Filling the
        // buffer transparent after construction gets the CONTENTS right but leaves DefaultStyle
        // answering the opaque Style.Default — the answer every blank-a-cell path in the buffer reads.
        Assert.Equal(Output.CellStyle.Transparent, presenter.CompositingScratchBlank);
    }

    [Fact] // a source that cannot use the scratch releases it
    public void NonLayeredSource_ReleasesTheScratch()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(), Width = 20, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        Assert.NotNull(presenter.CompositingScratchSize);

        presenter.Source = new LineChart([new PointD(0, 0), new PointD(9, 9)], Green);
        host.RunUntilIdle();
        Assert.Null(presenter.CompositingScratchSize);
    }

    [Fact] // a presenter that shrank must not keep its peak allocation resident
    public void Shrinking_RebuildsTheScratchSmaller()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(), Width = 30, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        var big = presenter.CompositingScratchSize!.Value;

        presenter.Width = 8;
        presenter.Height = 4;
        host.RunUntilIdle();

        var small = presenter.CompositingScratchSize!.Value;
        Assert.True(small.Columns * small.Rows < big.Columns * big.Rows,
                    $"the scratch stayed {small.Columns}x{small.Rows} against a peak of {big.Columns}x{big.Rows}");
    }

    [Fact] // shrinking past the 2x waste threshold rebuilds the scratch — and still renders
    public void Shrinking_RebuildsTheScratch_AndKeepsRendering()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(),
            Width = 30, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        Assert.True(PaintedCells(host, 40, 16) > 0);

        presenter.Width = 8;    // area drops well past half → the cached buffer is now wasteful
        presenter.Height = 4;
        host.RunUntilIdle();

        Assert.True(PaintedCells(host, 40, 16) > 0, "the chart must still render after the scratch is rebuilt");
    }

    [Fact] // growing beyond the cached scratch rebuilds it rather than clipping the chart
    public void Growing_RebuildsTheScratch_AndFillsTheLargerArea()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(),
            Width = 8, Height = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        var small = PaintedCells(host, 40, 16);

        presenter.Width = 30;
        presenter.Height = 12;
        host.RunUntilIdle();

        Assert.True(PaintedCells(host, 40, 16) > small, "a larger presenter must paint more, not stay clipped to the old scratch");
    }

    [Fact] // detach releases the scratch; re-attach rebuilds it transparently
    public void DetachThenReattach_StillRenders()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(),
            Width = 20, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var panel = new StackPanel();
        panel.Children.Add(presenter);
        host.ShowRoot(panel);
        host.RunUntilIdle();
        Assert.True(PaintedCells(host, 40, 16) > 0);

        panel.Children.Remove(presenter);
        host.RunUntilIdle();
        Assert.Equal(0, PaintedCells(host, 40, 16));

        panel.Children.Add(presenter);
        host.RunUntilIdle();
        Assert.True(PaintedCells(host, 40, 16) > 0, "the scratch must rebuild after a detach released it");
    }

    [Fact] // swapping to a non-layered source releases the scratch without disturbing rendering
    public void SwitchingToANonLayeredSource_StillRenders()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 16),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var presenter = new ChartPresenter
        {
            Source = Chart(),
            Width = 20, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        Assert.True(PaintedCells(host, 40, 16) > 0);

        presenter.Source = new LineChart([new PointD(0, 0), new PointD(9, 9)], Green);  // not layered
        host.RunUntilIdle();
        Assert.True(PaintedCells(host, 40, 16) > 0);

        presenter.Source = Chart();   // back to layered: the scratch rebuilds
        host.RunUntilIdle();
        Assert.True(PaintedCells(host, 40, 16) > 0);
    }
}
