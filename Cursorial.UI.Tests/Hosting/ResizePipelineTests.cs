// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The resize pipeline (design doc §10.6): coalesced last-wins, destructive buffer resize, full
/// relayout + full redraw landing in the <b>same</b> frame.
/// </summary>
public sealed class ResizePipelineTests
{
    [Fact]
    public void Resize_RebuildsBuffer_RelaysOut_AndRepaints_SameFrame()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var probe = new Fit(200, 200) { FillGlyph = "X" }; // fills whatever it is given
        host.ShowRoot(probe);
        Assert.True(host.RunUntilIdle());
        Assert.Equal((80, 24), (host.FrameBuffer.Columns, host.FrameBuffer.Rows));

        var measures = probe.MeasureOverrideCount;
        var renders = probe.RenderCount;
        host.SendResize(100, 30);
        host.RunFrame();

        Assert.Equal((100, 30), (host.FrameBuffer.Columns, host.FrameBuffer.Rows));
        Assert.Equal(measures + 1, probe.MeasureOverrideCount); // relayout landed in the SAME frame
        Assert.Equal(renders + 1, probe.RenderCount);           // full re-raster
        Assert.Equal((100, 30), (probe.Bounds.Columns, probe.Bounds.Rows));
        Assert.True(host.LastFrameBytes.Length > 0);            // full redraw emitted
        Assert.Equal(new string('X', 100), host.GetRowText(29));
    }

    [Fact]
    public void ResizeStorm_CoalescesToLastWins()
    {
        using var host = UITestHost.Create();
        var probe = new Fit(200, 200);
        host.ShowRoot(probe);
        Assert.True(host.RunUntilIdle());

        var measures = probe.MeasureOverrideCount;
        host.SendResize(90, 25);
        host.SendResize(95, 28);
        host.SendResize(120, 40);
        host.RunFrame();

        Assert.Equal(measures + 1, probe.MeasureOverrideCount); // ONE relayout for the storm
        Assert.Equal((120, 40), (host.FrameBuffer.Columns, host.FrameBuffer.Rows));
        Assert.Equal((120, 40), (probe.Bounds.Columns, probe.Bounds.Rows));
    }

    [Fact]
    public void ZeroSizedResize_IsIgnored()
    {
        using var host = UITestHost.Create();
        host.ShowRoot(new Fit(200, 200));
        Assert.True(host.RunUntilIdle());

        host.SendResize(0, 0);
        host.RunFrame();

        Assert.Equal((80, 24), (host.FrameBuffer.Columns, host.FrameBuffer.Rows));
    }

    [Fact]
    public void NotifyResized_InjectsSynthesizedResize()
    {
        using var host = UITestHost.Create();
        host.ShowRoot(new Fit(200, 200));
        Assert.True(host.RunUntilIdle());

        host.Application.NotifyResized(60, 20); // the BYO-embedder path — same coalescing pipeline
        host.RunFrame();

        Assert.Equal((60, 20), (host.FrameBuffer.Columns, host.FrameBuffer.Rows));
    }

    [Fact]
    public void Resize_ShrinkThenRestore_ContentSurvives()
    {
        using var host = UITestHost.Create();
        var probe = new Fit(200, 200) { FillGlyph = "Z" };
        host.ShowRoot(probe);
        Assert.True(host.RunUntilIdle());

        host.SendResize(20, 5);
        host.RunFrame();
        Assert.Equal("ZZZZZZZZZZZZZZZZZZZZ", host.GetRowText(4));

        host.SendResize(80, 24);
        host.RunFrame();
        Assert.Equal(new string('Z', 80), host.GetRowText(23)); // discarded contents fully repainted
    }
}
