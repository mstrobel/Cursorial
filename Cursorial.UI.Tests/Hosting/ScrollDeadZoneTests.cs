// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The scroll dead zone end to end, through the REAL lane: raw SGR wheel bytes → VT interpreter →
/// the frame loop's decorated device chain (§10.4, where <see cref="Cursorial.Input.WheelAxisLock"/>
/// is always assembled) → S3 routing → <see cref="ScrollViewer"/>. <c>SendBytes</c> is the one
/// headless entry that exercises the transformer; <c>SendInput</c> deliberately bypasses the pump.
/// </summary>
public sealed class ScrollDeadZoneTests
{
    // SGR wheel reports at cell (5,5): cb = 64 (wheel bit) | direction; M = press-style report.
    private static readonly byte[] WheelDown = "\x1b[<65;5;5M"u8.ToArray();
    private static readonly byte[] WheelRight = "\x1b[<67;5;5M"u8.ToArray();

    private static (UIHeadlessHost Host, ScrollViewer Scroller) CreateScrollingHost()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });

        var scroller = new ScrollViewer
        {
            // Horizontal Auto degrades to Disabled in v1 — Hidden keeps the axis wheel-scrollable.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Probe(200, 100),
        };

        host.ShowRoot(scroller);
        Assert.True(host.RunUntilIdle());
        return (host, scroller);
    }

    private static void SendWheel(UIHeadlessHost host, params byte[][] reports)
    {
        foreach (var report in reports)
            host.SendBytes(report);

        host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
        Assert.True(host.RunUntilIdle());
    }

    [Fact]
    public void Enabled_DriftTickInsideAVerticalGesture_DoesNotScrollHorizontally()
    {
        var (host, scroller) = CreateScrollingHost();
        using var _ = host;

        host.Application.ScrollDeadZoneEnabled = true;

        // A vertical gesture shedding one horizontal drift tick mid-stream (all events share the
        // frozen test clock, so they are one gesture by construction).
        SendWheel(host, WheelDown, WheelDown, WheelRight, WheelDown);

        Assert.True(scroller.VerticalOffset > 0);
        Assert.Equal(0, scroller.HorizontalOffset); // the drift tick died in the dead zone
    }

    [Fact]
    public void Disabled_SameStream_ScrollsBothAxes()
    {
        var (host, scroller) = CreateScrollingHost();
        using var _ = host;

        Assert.False(host.Application.ScrollDeadZoneEnabled); // the documented default is off

        SendWheel(host, WheelDown, WheelDown, WheelRight, WheelDown);

        Assert.True(scroller.VerticalOffset > 0);
        Assert.True(scroller.HorizontalOffset > 0); // raw deltas — the drift lands
    }

    [Fact]
    public void Toggle_ReachesTheRunningChain_WithoutRebuildingIt()
    {
        var (host, scroller) = CreateScrollingHost();
        using var _ = host;

        host.Application.ScrollDeadZoneEnabled = true;
        SendWheel(host, WheelDown, WheelDown, WheelRight);
        Assert.Equal(0, scroller.HorizontalOffset);

        // Flip live: the single-shot chain cannot be reassembled, so this must reach the
        // transformer already in it.
        host.Application.ScrollDeadZoneEnabled = false;
        SendWheel(host, WheelRight);
        Assert.True(scroller.HorizontalOffset > 0);
    }
}
