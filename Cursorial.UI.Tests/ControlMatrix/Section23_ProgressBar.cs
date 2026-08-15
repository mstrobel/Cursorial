using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C10 — ProgressBar (P9.7): determinate fill ratio + clamping + the :indeterminate pseudo-class;
// the bar is painted directly in Render (filled cells differ from the track). P2A: the indeterminate marquee is a
// perpetual S5 animation on the normalized IndeterminatePhase (started on attach, stopped when not indeterminate).
public sealed class Section23_ProgressBar
{
    // A ProgressBar pinned top-left at a known size so Render cells land at a predictable origin.
    private static (UIHeadlessHost Host, ProgressBar Bar) Shown(double value, double max = 100, int width = 10, bool indeterminate = false)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 6) });
        var bar = new ProgressBar
        {
            Maximum = max,
            Value = value,
            IsIndeterminate = indeterminate,
            Width = width,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(bar);
        host.RunUntilIdle();
        return (host, bar);
    }

    [Fact] // C10.1: FilledFraction is the (Value−Min)/(Max−Min) ratio
    public void C10_1_FilledFraction()
    {
        var bar = new ProgressBar { Maximum = 100, Value = 50 };
        Assert.Equal(0.5, bar.FilledFraction);
        bar.Value = 25;
        Assert.Equal(0.25, bar.FilledFraction);
    }

    [Fact] // C10.2: Value is coerced into [Minimum, Maximum]
    public void C10_2_ValueClamped()
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100 };
        bar.Value = 150;
        Assert.Equal(100, bar.Value); // clamped to Maximum
        bar.Value = -10;
        Assert.Equal(0, bar.Value);   // clamped to Minimum
    }

    [Fact] // C10.3: the ends — 0 at Value=Min, 1 at Value=Max
    public void C10_3_Ends()
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        Assert.Equal(0.0, bar.FilledFraction);
        bar.Value = 100;
        Assert.Equal(1.0, bar.FilledFraction);
    }

    [Fact] // C10.4: an empty range never divides by zero (FilledFraction 0)
    public void C10_4_EmptyRange()
    {
        var bar = new ProgressBar { Minimum = 5, Maximum = 5, Value = 5 };
        Assert.Equal(0.0, bar.FilledFraction);
    }

    [Fact] // C10.5: a non-zero Minimum offsets the ratio correctly
    public void C10_5_OffsetRange()
    {
        var bar = new ProgressBar { Minimum = 10, Maximum = 20, Value = 15 };
        Assert.Equal(0.5, bar.FilledFraction);
    }

    [Fact] // C10.6: IsIndeterminate flips the :indeterminate pseudo-class
    public void C10_6_IndeterminatePseudoClass()
    {
        var (host, bar) = Shown(0, indeterminate: true);
        using var _ = host;
        Assert.True(bar.HasCustomPseudoClass(":indeterminate"));

        bar.IsIndeterminate = false;
        host.RunUntilIdle();
        Assert.False(bar.HasCustomPseudoClass(":indeterminate"));
    }

    [Fact] // C10.7: a determinate bar renders distinct fill vs track cells at the fill ratio
    public void C10_7_DeterminateRenders()
    {
        var (host, bar) = Shown(30, max: 100, width: 10); // 30% of 10 → 3 filled cells (0,1,2)
        using var _ = host;
        var o = bar.TranslateToWindow(0, 0);

        var filledBg = host.GetCell(o.Column, o.Row).Style.Background;       // cell 0 — filled
        var trackBg = host.GetCell(o.Column + 9, o.Row).Style.Background;    // cell 9 — track
        Assert.NotEqual(filledBg, trackBg); // the bar is visible: the fill differs from the track
        Assert.Equal(filledBg, host.GetCell(o.Column + 2, o.Row).Style.Background); // cell 2 still filled (round(3))
        Assert.Equal(trackBg, host.GetCell(o.Column + 5, o.Row).Style.Background);  // cell 5 is track
    }

    [Fact] // C10.8: a full bar fills every cell; an empty bar fills none (all track)
    public void C10_8_FullAndEmpty()
    {
        var (full, fullBar) = Shown(100, max: 100, width: 8);
        using (full)
        {
            var o = fullBar.TranslateToWindow(0, 0);
            var c0 = full.GetCell(o.Column, o.Row).Style.Background;
            var c7 = full.GetCell(o.Column + 7, o.Row).Style.Background;
            Assert.Equal(c0, c7); // uniformly filled — first and last match
        }

        var (empty, emptyBar) = Shown(0, max: 100, width: 8);
        using (empty)
        {
            var o = emptyBar.TranslateToWindow(0, 0);
            var c0 = empty.GetCell(o.Column, o.Row).Style.Background;
            var c7 = empty.GetCell(o.Column + 7, o.Row).Style.Background;
            Assert.Equal(c0, c7); // uniformly track — nothing filled
        }
    }

    [Fact] // C10.9: the indeterminate marquee renders a visible ~width/3 sweep block (distinct fill vs track cells)
    public void C10_9_IndeterminateBlockRenders()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 6) });
        using var _ = host;
        var bar = new ProgressBar
        {
            IsIndeterminate = true,
            Fill = new SolidColorBrush(Color.FromRgb(0, 200, 0)),
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            Width = 12, Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(bar);
        host.RunFrame(); // one frame: style + the marquee's first tick (RunUntilIdle never idles under a perpetual anim)
        var o = bar.TranslateToWindow(0, 0);

        // Exactly blockWidth (12/3 = 4) cells carry the fill color; the rest are track — a visible sweep block.
        var fillCells = Enumerable.Range(0, 12)
            .Count(c => host.GetCell(o.Column + c, o.Row).Style.Background == Color.FromRgb(0, 200, 0));
        Assert.Equal(4, fillCells);
    }

    [Fact] // C10.10 (P2A): IsIndeterminate runs a perpetual marquee advancing IndeterminatePhase; turning it off stops + resets it
    public void C10_10_MarqueeAnimatesAndStops()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 6) });
        using var _ = host;
        var bar = new ProgressBar
        {
            IsIndeterminate = true, Width = 12, Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(bar);
        host.RunFrame();

        var p0 = bar.IndeterminatePhase;
        host.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.NotEqual(p0, bar.IndeterminatePhase);   // the marquee advanced the normalized phase
        Assert.InRange(bar.IndeterminatePhase, 0.0, 1.0);

        // Turning indeterminate off stops the animation; the phase resurfaces to its base (0) and holds.
        bar.IsIndeterminate = false;
        host.RunFrame();
        var stopped = bar.IndeterminatePhase;
        Assert.Equal(0.0, stopped);
        host.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.Equal(stopped, bar.IndeterminatePhase); // no longer animating
    }
}
