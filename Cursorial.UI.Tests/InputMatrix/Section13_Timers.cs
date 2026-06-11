using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Input matrix §13 — the early-S5 <see cref="FrameClock"/>/<see cref="UITimer"/> slice
/// (N190–N195). All rows on the fake clock; the same types S5's A0 absorbs at P8.
/// </summary>
public class Section13_Timers
{
    private static UITestHost CreateHost()
    {
        var host = UITestHost.Create();
        host.ShowRoot(new Canvas());
        host.RunFrame(); // settle — Clock.Now frozen at 0
        return host;
    }

    [Fact]
    public void N190_OneShot_FiresOnceAtFrozenFrameTime()
    {
        using var host = CreateHost();
        var scheduler = host.Application.AnimationScheduler;
        var fired = 0;
        TimeSpan? observedNow = null;
        var timer = UITimer.Start(TimeSpan.FromMilliseconds(100), () =>
        {
            fired++;
            observedNow = scheduler.Clock.Now;
        });

        host.Time.Advance(TimeSpan.FromMilliseconds(99));
        host.RunFrame();
        Assert.Equal(0, fired); // not due yet

        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        host.RunFrame();
        Assert.Equal(1, fired); // fired during the frame's tick…
        Assert.Equal(TimeSpan.FromMilliseconds(100), observedNow); // …at the FROZEN FrameTime
        Assert.False(timer.IsRunning); // one-shot

        host.RunFrame();
        Assert.Equal(1, fired); // exactly once
    }

    [Fact]
    public void N191_Repeating_CoalescedToOnePerFrame_ReArmsFromFrozenClock()
    {
        using var host = CreateHost();
        var fired = 0;
        using var timer = UITimer.Start(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), () => fired++);

        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        host.RunFrame();
        Assert.Equal(1, fired); // first due

        host.Time.Advance(TimeSpan.FromMilliseconds(200)); // four intervals elapse…
        host.RunFrame();
        Assert.Equal(2, fired); // …but the frame-frozen clock coalesces to ONE fire (ND20)

        // Re-armed from the frozen clock (300 ms + 50 ms): not due at 349, due at 350.
        host.Time.Advance(TimeSpan.FromMilliseconds(49));
        host.RunFrame();
        Assert.Equal(2, fired);

        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        host.RunFrame();
        Assert.Equal(3, fired);
    }

    [Fact]
    public void N192_StopIdempotent_RestartReArmsFromCurrentFrameClock()
    {
        using var host = CreateHost();
        var fired = 0;
        var timer = UITimer.Start(TimeSpan.FromMilliseconds(100), () => fired++);

        timer.Stop();
        timer.Dispose();
        timer.Dispose(); // idempotent

        host.Time.Advance(TimeSpan.FromMilliseconds(200));
        host.RunFrame();
        Assert.Equal(0, fired); // a stopped timer never fires

        timer.Restart(); // re-arms dueTime from the CURRENT frame clock (200 ms)
        Assert.True(timer.IsRunning);

        host.Time.Advance(TimeSpan.FromMilliseconds(99));
        host.RunFrame();
        Assert.Equal(0, fired);

        host.Time.Advance(TimeSpan.FromMilliseconds(1)); // 300 ms = 200 + 100
        host.RunFrame();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void N193_PendingTimer_BlocksIdle_FiringRestoresIt()
    {
        using var host = CreateHost();
        var fired = 0;
        using var timer = UITimer.Start(TimeSpan.FromMilliseconds(50), () => fired++);

        Assert.False(host.RunUntilIdle(maxFrames: 5)); // the loop never reports idle while a timer pends

        host.Time.Advance(TimeSpan.FromMilliseconds(50));
        host.RunFrame();
        Assert.Equal(1, fired);

        Assert.True(host.RunUntilIdle()); // after the one-shot fires, idle returns
    }

    [Fact]
    public void N194_ThrowingCallback_FunnelPolicy_NoReFire_OthersUnaffected()
    {
        using var host = CreateHost();
        var handled = 0;
        host.Application.DispatcherUnhandledException += (_, e) =>
        {
            handled++;
            e.Handled = true; // funnel policy: continue the frame
        };

        var throwerFires = 0;
        var otherFires = 0;
        using var thrower = UITimer.Start(TimeSpan.FromMilliseconds(50), () =>
        {
            throwerFires++;
            throw new InvalidOperationException("timer boom");
        });
        using var other = UITimer.Start(TimeSpan.FromMilliseconds(50), () => otherFires++);

        host.Time.Advance(TimeSpan.FromMilliseconds(50));
        host.RunFrame(); // the thrower aborts this tick; the funnel handles it

        Assert.Equal(1, handled);
        Assert.Equal(1, throwerFires);
        Assert.False(thrower.IsRunning); // state updated BEFORE the callback — no re-fire

        host.RunFrame(); // the other timer is still due and unaffected

        Assert.Equal(1, throwerFires); // never re-fired
        Assert.Equal(1, otherFires);
        Assert.False(other.IsRunning);
    }

    [Fact]
    public void N195_CallbackStartsTimer_ArmedAtFrozenClock_NoSameFrameCascade()
    {
        using var host = CreateHost();
        var innerFires = 0;
        UITimer? inner = null;
        using var outer = UITimer.Start(
            TimeSpan.FromMilliseconds(50),
            () => inner = UITimer.Start(TimeSpan.FromMilliseconds(100), () => innerFires++));

        host.Time.Advance(TimeSpan.FromMilliseconds(50));
        host.RunFrame();

        Assert.NotNull(inner);
        Assert.True(inner.IsRunning);
        Assert.Equal(0, innerFires); // no same-frame cascade (count snapshot)

        host.Time.Advance(TimeSpan.FromMilliseconds(99)); // 149 ms — armed at the frozen 50 ms, due 150
        host.RunFrame();
        Assert.Equal(0, innerFires);

        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        host.RunFrame();
        Assert.Equal(1, innerFires); // fires on its own schedule
        inner.Dispose();
    }
}
