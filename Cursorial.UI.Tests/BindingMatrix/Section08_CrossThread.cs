using System.Collections.Concurrent;

using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §8 — cross-thread, frame coherence, and animated-value filtering (B93–B100). Uses a
/// fake <see cref="IUIDispatcher"/> with the loop-wake hook (the calling thread is the "UI thread").
/// </summary>
public class Section08_CrossThread
{
    public Section08_CrossThread()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B093_SameThread_AppliedSynchronously()
    {
        using var _ = BindingDispatcher.Install(new FakeDispatcher(uiThreadId: Environment.CurrentManagedThreadId));
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        vm.Name = "x"; // same thread → synchronous
        Assert.Equal("x", w.Text);
    }

    [Fact]
    public void B094_ForeignThread_CoalescedDrain_PostedAndWakes()
    {
        var fake = new FakeDispatcher(uiThreadId: Environment.CurrentManagedThreadId);
        using var _ = BindingDispatcher.Install(fake);
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        // Mutate on a foreign thread.
        var t = new Thread(() => vm.Name = "y");
        t.Start();
        t.Join();

        Assert.True(w.Text != "y"); // not yet applied (no drain run)
        Assert.True(fake.WokeCount >= 1); // Post wakes the loop
        fake.RunDrains(); // simulate the next frame's pre-layout drain
        Assert.Equal("y", w.Text);
    }

    [Fact]
    public void B095_NForeignChanges_CoalesceIntoOnePass()
    {
        var fake = new FakeDispatcher(uiThreadId: Environment.CurrentManagedThreadId);
        using var _ = BindingDispatcher.Install(fake);
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        var t = new Thread(() =>
        {
            vm.Name = "b";
            vm.Name = "c";
            vm.Name = "d";
        });
        t.Start();
        t.Join();

        // N changes coalesced into ONE posted drain.
        Assert.Equal(1, fake.PendingDrainCount);
        fake.RunDrains();
        Assert.Equal("d", w.Text); // the last value, re-read once
    }

    [Fact]
    public void B096_Post_WakesIdleLoop()
    {
        var fake = new FakeDispatcher(uiThreadId: Environment.CurrentManagedThreadId);
        using var _ = BindingDispatcher.Install(fake);
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        // No pending drain; a background change must wake the loop (the Invalidate() pattern), not
        // sit until unrelated input.
        var wokeBefore = fake.WokeCount;
        var t = new Thread(() => vm.Name = "bg");
        t.Start();
        t.Join();

        Assert.True(fake.WokeCount > wokeBefore); // Post woke the idle loop
        Assert.Equal(1, fake.PendingDrainCount);  // a drain was scheduled
    }

#if DEBUG
    [Fact]
    public void B097_Install_FromNonUiThread_DebugAsserts()
    {
        var fake = new FakeDispatcher(uiThreadId: Environment.CurrentManagedThreadId);
        using var _ = BindingDispatcher.Install(fake);
        var w = new BindWidget();

        // VerifyAccess (invariant 6) throws off the affine thread in DEBUG.
        Exception? captured = null;
        var t = new Thread(() =>
        {
            captured = Record.Exception(() =>
                BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Name") { Source = new Vm() }));
        });
        t.Start();
        t.Join();

        Assert.NotNull(captured);
        Assert.IsType<InvalidOperationException>(captured);
    }
#endif

    [Fact]
    public void B098_AnimationDrivenTarget_WriteBackSkipped()
    {
        var vm = new Vm { Age = 0 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.OffProperty, new Binding("Age")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var sourceWrites = 0;
        vm.PropertyChanged += (_, _) => sourceWrites++;

        // An animation drives the property at Animation priority each "frame".
        var handle = w.BeginAnimation(BindWidget.OffProperty);
        handle.SetValue(11);
        handle.SetValue(12);
        handle.SetValue(13);

        // BD8 step 3: animated values never round-trip — the source is not spammed.
        Assert.Equal(0, sourceWrites);
        Assert.Equal(0, vm.Age);
    }

    [Fact]
    public void B099_MidAnimationSetCurrentValue_AlsoFilteredFromWriteBack()
    {
        var vm = new Vm { Age = 0 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.OffProperty, new Binding("Age") { Mode = BindingMode.TwoWay });

        var handle = w.BeginAnimation(BindWidget.OffProperty);
        handle.SetValue(5);

        var sourceWrites = 0;
        vm.PropertyChanged += (_, _) => sourceWrites++;

        // A mid-animation SetCurrentValue replaces the animation lane (A11) ⇒ Animation-priority args
        // ⇒ also filtered from write-back (consistent with "animated values never round-trip").
        w.SetCurrentValue(BindWidget.OffProperty, 11);

        Assert.Equal(0, sourceWrites);
        Assert.Equal(0, vm.Age);
    }

    /// <summary>A fake dispatcher: a fixed UI thread id, a queue of posted drains, and a wake counter.</summary>
    private sealed class FakeDispatcher(int uiThreadId) : IUIDispatcher
    {
        private readonly ConcurrentQueue<Action> _drains = new();
        private int _woke;

        public int WokeCount => Volatile.Read(ref _woke);

        public int PendingDrainCount => _drains.Count;

        public bool CheckAccess() => Environment.CurrentManagedThreadId == uiThreadId;

        public void Post(Action callback)
        {
            _drains.Enqueue(callback);
            Interlocked.Increment(ref _woke); // Post MUST wake the loop (BD20)
        }

        public void RunDrains()
        {
            while (_drains.TryDequeue(out var drain))
                drain();
        }
    }
}
