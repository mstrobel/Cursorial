using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

using Microsoft.Extensions.Time.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.Input;

public class KeyReleaseSynthesizerTests
{
    // Timing constants used across the suite. The synthesizer's internal Task.Delay calls run
    // against a FakeTimeProvider, so these are advanced manually via fakeTime.Advance(...) —
    // no wall-clock dependency. The values are kept human-readable so the test assertions
    // about "fire at upTimeout" / "release after repeatTimeout" still line up with the real
    // production defaults.
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RepeatTimeout = TimeSpan.FromMilliseconds(150);

    // Wall-clock budget for the per-event synchronization in Collector.WaitForCountAtLeastAsync.
    // The synthesizer's logical timing is all fake; this is just "how long do we wait for the
    // pump's background task to consume an already-enqueued event and write to the channel."
    // 5 seconds is huge compared to the real cost (microseconds) but covers any plausible CI
    // pause without false-negative timeouts.
    private static readonly TimeSpan PumpSyncTimeout = TimeSpan.FromSeconds(5);

    private static KeyReleaseSynthesizer NewSynth(IAsyncInputDevice inner, TimeProvider timeProvider) =>
        new(inner, upTimeout: UpTimeout, repeatTimeout: RepeatTimeout, timeProvider: timeProvider);

    private static KeyEvent KeyDown(Key key,
                                    KeyModifiers mods = KeyModifiers.None,
                                    bool isRepeat = false,
                                    string text = "")
        => new()
           {
               Timestamp = DateTimeOffset.UtcNow,
               Key = key,
               Kind = KeyEventKind.Down,
               Modifiers = mods,
               IsRepeat = isRepeat,
               Text = text.AsMemory(),
           };

    private static KeyEvent KeyUp(Key key,
                                  KeyModifiers mods = KeyModifiers.None,
                                  string text = "")
        => new()
           {
               Timestamp = DateTimeOffset.UtcNow,
               Key = key,
               Kind = KeyEventKind.Up,
               Modifiers = mods,
               Text = text.AsMemory(),
           };

    private static KeyEvent CharDown(string ch) => KeyDown(Key.Character, text: ch);
    private static KeyEvent CharUp(string ch) => KeyUp(Key.Character, text: ch);

    // ---- Timing-driven tests (FakeTimeProvider) -------------------------------------

    [Fact]
    public async Task SingleKeyDown_AfterIdleTimeout_EmitsSynthesizedUp()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        // Fire the up timer.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(2);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        Assert.Equal(2, collected.Count);

        var down = Assert.IsType<KeyEvent>(collected[0]);
        Assert.Equal(KeyEventKind.Down, down.Kind);
        Assert.False(down.Synthesized);

        var up = Assert.IsType<KeyEvent>(collected[1]);
        Assert.Equal(KeyEventKind.Up, up.Kind);
        Assert.True(up.Synthesized);
        Assert.Equal(Key.Character, up.Key);
    }

    [Fact]
    public async Task AutoRepeatPresses_ResetTimer_OnlyOneSynthesizedUp()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        // Three rapid down events. Each must be observed by the pump (synchronously visible
        // in collector.Snapshot()) before the next so the held-keys dictionary updates atop
        // the prior entry — that's how the synthesizer recognizes them as continuations of
        // the same hold rather than independent presses.
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        // No wall-clock advance between presses — the synthesizer's logical clock is the
        // FakeTimeProvider, which hasn't moved. The three downs all land while the synth
        // believes they're at the same logical moment, which is fine because the held-keys
        // dictionary disambiguates by version.
        device.Enqueue(KeyDown(Key.Character, isRepeat: true));
        await collector.WaitForCountAtLeastAsync(2);

        device.Enqueue(KeyDown(Key.Character, isRepeat: true));
        await collector.WaitForCountAtLeastAsync(3);

        // Advance past the up timeout — exactly one synth up should fire (for the latest
        // armed timer; the prior two are superseded via version mismatch).
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(4);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        Assert.Equal(4, collected.Count); // 3 downs + 1 synthesized up
        var ups = collected.OfType<KeyEvent>().Where(k => k.Kind == KeyEventKind.Up).ToList();
        Assert.Single(ups);
        Assert.True(ups[0].Synthesized);
    }

    [Fact]
    public async Task RealUpFromInnerDevice_CancelsPendingSynthesis()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        // Real release from a terminal that does report key-up — should cancel the synth
        // timer so we don't double-emit.
        device.Enqueue(KeyUp(Key.Character));
        await collector.WaitForCountAtLeastAsync(2);

        // Advance past the up timeout — the cancelled timer must NOT fire.
        fakeTime.Advance(UpTimeout + TimeSpan.FromMilliseconds(10));

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        Assert.Equal(2, collected.Count);
        var down = Assert.IsType<KeyEvent>(collected[0]);
        Assert.Equal(KeyEventKind.Down, down.Kind);
        var up = Assert.IsType<KeyEvent>(collected[1]);
        Assert.Equal(KeyEventKind.Up, up.Kind);
        // Real release — not synthesized.
        Assert.False(up.Synthesized);
    }

    [Fact]
    public async Task MultipleHeldKeys_EachGetsOwnSynthesizedUp()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.LeftArrow));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(KeyDown(Key.RightArrow));
        await collector.WaitForCountAtLeastAsync(2);

        // Both keys' up timers fire on the same advance.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(4);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();
        Assert.Equal(2, ups.Count);
        Assert.Contains(ups, u => u.Key == Key.LeftArrow);
        Assert.Contains(ups, u => u.Key == Key.RightArrow);
        Assert.All(ups, u => Assert.True(u.Synthesized));
    }

    // ---- Functional tests (no timing dependency) ----------------------------------

    [Fact]
    public async Task NonKeyEvent_PassesThroughUntouched()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);

        var paste = new PasteEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Text = "hello".AsMemory(),
                    };
        device.Enqueue(paste);
        device.Complete();

        var collected = new List<InputEvent>();
        await foreach (var evt in sync.ReadAllAsync().ConfigureAwait(false))
            collected.Add(evt);

        Assert.Single(collected);
        Assert.Same(paste, collected[0]);
    }

    [Fact]
    public async Task InnerCompletes_StreamCompletesCleanly()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);

        device.Enqueue(KeyDown(Key.Character));
        device.Complete();

        // No time advance — the synth Up timer is armed but never fires, so the consumer sees
        // exactly the inner's stream (one Down) before the channel completes.
        var collected = new List<InputEvent>();
        await foreach (var evt in sync.ReadAllAsync())
            collected.Add(evt);

        Assert.NotEmpty(collected);
        Assert.Equal(KeyEventKind.Down, ((KeyEvent) collected[0]).Kind);
    }

    [Fact]
    public void Capabilities_ReportsKeyUpDownAndRepeats_RegardlessOfInnerCaps()
    {
        var device = new TestInputDevice
                     {
                         Capabilities = InputCapabilities.None with
                                        {
                                            Keyboard = new KeyboardCapabilities(
                                                DistinguishesKeyUpDown: false,
                                                ReportsRepeats: false,
                                                DetailedModifiers: false,
                                                TextInput: true),
                                        },
                     };
        using var sync_disposed_ignored = new SyncWrap(device);

        Assert.True(sync_disposed_ignored.Synthesizer.Capabilities.Keyboard.DistinguishesKeyUpDown);
        Assert.True(sync_disposed_ignored.Synthesizer.Capabilities.Keyboard.ReportsRepeats);
        Assert.True(sync_disposed_ignored.Synthesizer.Capabilities.Keyboard.TextInput);
        Assert.False(sync_disposed_ignored.Synthesizer.Capabilities.Keyboard.DetailedModifiers);
    }

    [Fact]
    public async Task ReadAllAsync_CalledTwice_Throws()
    {
        var device = new TestInputDevice();
        await using var sync = new KeyReleaseSynthesizer(device);

        device.Complete();
        await foreach (var _ in sync.ReadAllAsync()) { }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sync.ReadAllAsync()) { }
        });
    }

    [Fact]
    public void Inner_PropertyReturnsWrappedDevice()
    {
        var device = new TestInputDevice();
        using var sync = new SyncWrap(device);
        Assert.Same(device, sync.Synthesizer.Inner);
    }

    // ---- Recently-held window --------------------------------------------------------

    [Fact]
    public async Task PressInsideRecentlyHeldWindow_MarkedAsRepeat()
    {
        // Down at t=0 → synth Up fires at t=upTimeout (50 ms). Between then and the repeat
        // timeout (150 ms) the key is "recently held": a fresh Down in that window still
        // counts as a continuation rather than a new activation.
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        // Fire the up timer.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(2);

        // Half-way through the recently-held window — still tracked. Second Down lands as a
        // continuation.
        fakeTime.Advance(TimeSpan.FromMilliseconds(40));
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(3);

        // Fire the second up timer.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(4);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.True(downs[1].IsRepeat);
        Assert.Equal(2, downs[1].RepeatCount);

        var sequence = collected.OfType<KeyEvent>()
                                .Select(k => (k.Kind, k.Synthesized, k.IsRepeat))
                                .ToList();
        Assert.Equal(
            new[]
            {
                (KeyEventKind.Down, false, false),
                (KeyEventKind.Up, true, false),
                (KeyEventKind.Down, false, true),
                (KeyEventKind.Up, true, false),
            },
            sequence);
    }

    [Fact]
    public async Task UpTimeoutShorterThanRepeatTimeout_BothFireOnLongIdle()
    {
        // For a key held briefly with no further activity, the consumer sees exactly one
        // synth Up — emitted at upTimeout. The repeat timer's effect after upTimeout is
        // internal (forgetting the key) and not observable.
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(2);

        // Advance the rest of the way past the repeat timeout. No new observable event.
        fakeTime.Advance(RepeatTimeout);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        Assert.Equal(2, collected.Count);
        var up = Assert.IsType<KeyEvent>(collected[1]);
        Assert.Equal(KeyEventKind.Up, up.Kind);
        Assert.True(up.Synthesized);
    }

    // ---- Constructor validation ------------------------------------------------------

    [Fact]
    public void Ctor_RepeatTimeoutShorterThanUpTimeout_Throws()
    {
        var device = new TestInputDevice();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeyReleaseSynthesizer(
                device,
                upTimeout: TimeSpan.FromMilliseconds(100),
                repeatTimeout: TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Ctor_NonPositiveUpTimeout_Throws()
    {
        var device = new TestInputDevice();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeyReleaseSynthesizer(device, upTimeout: TimeSpan.Zero));
    }

    // ---- Repeat inference ------------------------------------------------------------

    [Fact]
    public async Task FirstPress_IsNotMarkedAsRepeat()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);

        device.Enqueue(KeyDown(Key.Character));
        device.Complete();

        var collected = new List<InputEvent>();
        await foreach (var evt in sync.ReadAllAsync())
            collected.Add(evt);

        var firstDown = collected.OfType<KeyEvent>().First(k => k.Kind == KeyEventKind.Down);
        Assert.False(firstDown.IsRepeat);
    }

    [Fact]
    public async Task SecondPress_WhileHeld_MarkedAsRepeat()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        // Second press arrives while the first is still "held" (no time advance, definitely
        // within the up-timeout window). The synthesizer infers auto-repeat.
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(2);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.Equal(1, downs[0].RepeatCount);
        Assert.True(downs[1].IsRepeat);
        Assert.Equal(2, downs[1].RepeatCount);
    }

    [Fact]
    public async Task SuccessiveRepeats_RepeatCountIncrementsPerHold()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(2);
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(3);
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(4);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(4, downs.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, downs.Select(d => d.RepeatCount));
        Assert.Equal(new[] { false, true, true, true }, downs.Select(d => d.IsRepeat));
    }

    [Fact]
    public async Task RepeatCount_ResetsAfterRepeatTimeout()
    {
        // After the synthesizer's repeat timeout fires (key transitions held → recently-held
        // → forgotten), the next press starts fresh with RepeatCount=1.
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(KeyDown(Key.Character)); // RepeatCount=2 while held
        await collector.WaitForCountAtLeastAsync(2);

        // Fire both timers. Up at upTimeout, forget at repeatTimeout. After this, the key is
        // fully untracked.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(3); // synth Up
        fakeTime.Advance(RepeatTimeout - UpTimeout + TimeSpan.FromMilliseconds(10));

        device.Enqueue(KeyDown(Key.Character)); // fresh hold, RepeatCount=1
        await collector.WaitForCountAtLeastAsync(4);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(3, downs.Count);
        Assert.Equal(1, downs[0].RepeatCount);
        Assert.Equal(2, downs[1].RepeatCount);
        Assert.Equal(1, downs[2].RepeatCount);
        Assert.False(downs[2].IsRepeat);
    }

    [Fact]
    public async Task PressAfterRepeatTimeout_NotMarkedAsRepeat()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);

        // Fire up timer, then forget timer — key fully untracked.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(2);
        fakeTime.Advance(RepeatTimeout - UpTimeout + TimeSpan.FromMilliseconds(10));

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(3);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.False(downs[1].IsRepeat);
    }

    [Fact]
    public async Task DifferentCharacters_NotMarkedAsRepeat()
    {
        // 'a' and 'b' share Key.Character; the held-keys dictionary discriminates by Text.
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(CharDown("a"));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(CharDown("b"));
        await collector.WaitForCountAtLeastAsync(2);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.False(downs[1].IsRepeat);
        Assert.Equal(1, downs[0].RepeatCount);
        Assert.Equal(1, downs[1].RepeatCount);
    }

    [Fact]
    public async Task DifferentCharacters_EachGetOwnSynthesizedRelease()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(CharDown("a"));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(CharDown("b"));
        await collector.WaitForCountAtLeastAsync(2);

        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(4); // both Ups

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();
        Assert.Equal(2, ups.Count);
        Assert.Contains(ups, u => u.Text.Span.SequenceEqual("a"));
        Assert.Contains(ups, u => u.Text.Span.SequenceEqual("b"));
        Assert.All(ups, u => Assert.True(u.Synthesized));
    }

    [Fact]
    public async Task SameCharacterPressedTwice_IsMarkedAsRepeat()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(CharDown("a"));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(CharDown("a"));
        await collector.WaitForCountAtLeastAsync(2);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var aDowns = collected.OfType<KeyEvent>()
                              .Where(k => k.Kind == KeyEventKind.Down && k.Text.Span.SequenceEqual("a"))
                              .ToList();
        Assert.Equal(2, aDowns.Count);
        Assert.False(aDowns[0].IsRepeat);
        Assert.True(aDowns[1].IsRepeat);
        Assert.Equal(2, aDowns[1].RepeatCount);
    }

    [Fact]
    public async Task RealUpFromInner_WithMatchingText_ClearsExactHold()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(CharDown("a"));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(CharDown("b"));
        await collector.WaitForCountAtLeastAsync(2);

        // Real release for 'a' only.
        device.Enqueue(CharUp("a"));
        await collector.WaitForCountAtLeastAsync(3);

        // Fire timers — 'a' is gone, only 'b' produces a synth Up.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(4);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();

        var realA = ups.SingleOrDefault(u => !u.Synthesized && u.Text.Span.SequenceEqual("a"));
        Assert.NotNull(realA);
        var synthB = ups.SingleOrDefault(u => u.Synthesized && u.Text.Span.SequenceEqual("b"));
        Assert.NotNull(synthB);
    }

    [Fact]
    public async Task RealUpFromInner_WithoutText_ClearsAllCharacterHolds()
    {
        // Ambiguous release (Key.Character with empty Text) clears every character hold.
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(CharDown("a"));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(CharDown("b"));
        await collector.WaitForCountAtLeastAsync(2);
        device.Enqueue(KeyDown(Key.LeftArrow));
        await collector.WaitForCountAtLeastAsync(3);

        device.Enqueue(KeyUp(Key.Character)); // ambiguous — clears 'a' and 'b'
        await collector.WaitForCountAtLeastAsync(4);

        // Fire timers. LeftArrow still has a pending up; 'a' and 'b' don't.
        fakeTime.Advance(UpTimeout);
        await collector.WaitForCountAtLeastAsync(5);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();

        Assert.Contains(ups, u => u.Key == Key.LeftArrow && u.Synthesized);
        Assert.DoesNotContain(ups, u => u.Key == Key.Character && u.Synthesized);
    }

    [Fact]
    public async Task DifferentKey_NotMarkedAsRepeat()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice();
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.LeftArrow));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(KeyDown(Key.RightArrow));
        await collector.WaitForCountAtLeastAsync(2);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var rightDown = collected.OfType<KeyEvent>()
                                 .First(k => k.Key == Key.RightArrow && k.Kind == KeyEventKind.Down);
        Assert.False(rightDown.IsRepeat);
    }

    [Fact]
    public async Task InnerAlreadyReportsRepeats_NotOverridden()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice
                     {
                         Capabilities = InputCapabilities.None with
                                        {
                                            Keyboard = new KeyboardCapabilities(
                                                DistinguishesKeyUpDown: true,
                                                ReportsRepeats: true,
                                                DetailedModifiers: false,
                                                TextInput: true),
                                        },
                     };
        await using var sync = NewSynth(device, fakeTime);
        var collector = new Collector(sync);

        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(1);
        device.Enqueue(KeyDown(Key.Character));
        await collector.WaitForCountAtLeastAsync(2);

        device.Complete();
        await collector.PumpTask;

        var collected = collector.Snapshot();
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.False(downs[1].IsRepeat);
    }

    [Fact]
    public async Task InnerMarkedAsRepeat_PreservedThroughSynthesizer()
    {
        var fakeTime = new FakeTimeProvider();
        var device = new TestInputDevice
                     {
                         Capabilities = InputCapabilities.None with
                                        {
                                            Keyboard = new KeyboardCapabilities(
                                                DistinguishesKeyUpDown: true,
                                                ReportsRepeats: true,
                                                DetailedModifiers: false,
                                                TextInput: true),
                                        },
                     };
        await using var sync = NewSynth(device, fakeTime);

        device.Enqueue(KeyDown(Key.Character, isRepeat: true));
        device.Complete();

        var collected = new List<InputEvent>();
        await foreach (var evt in sync.ReadAllAsync())
            collected.Add(evt);

        var down = Assert.IsType<KeyEvent>(collected[0]);
        Assert.True(down.IsRepeat);
    }

    // ---- Test helpers ----

    /// <summary>
    /// Background collector that drains the synthesizer's output into a list, exposing both
    /// a snapshot of what's been observed so far and a "wait until at least N events have
    /// arrived" primitive for synchronization between test steps. The wait is a bounded
    /// poll — the synth's logical clock is fake, so the only thing we're waiting for is the
    /// background pump task to commit an event to the channel and our collector loop to read
    /// it. That's microseconds in practice; the timeout is generous to absorb CI scheduling
    /// hiccups without false negatives.
    /// </summary>
    private sealed class Collector
    {
        private readonly List<InputEvent> _events = new();
        private readonly object _lock = new();

        public Task PumpTask { get; }

        public Collector(KeyReleaseSynthesizer sync)
        {
            PumpTask = Task.Run(async () =>
            {
                await foreach (var evt in sync.ReadAllAsync().ConfigureAwait(false))
                {
                    lock (_lock) _events.Add(evt);
                }
            });
        }

        public IReadOnlyList<InputEvent> Snapshot()
        {
            lock (_lock) return _events.ToList();
        }

        public async Task WaitForCountAtLeastAsync(int count)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < PumpSyncTimeout)
            {
                lock (_lock)
                {
                    if (_events.Count >= count) return;
                }
                await Task.Delay(2).ConfigureAwait(false);
            }

            int actual;
            lock (_lock) actual = _events.Count;
            throw new TimeoutException(
                $"Expected at least {count} events from the synthesizer; only observed {actual} " +
                $"within {PumpSyncTimeout}.");
        }
    }

    private sealed class TestInputDevice : IAsyncInputDevice
    {
        private readonly Channel<InputEvent> _channel =
            Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });

        public InputCapabilities Capabilities { get; init; } = InputCapabilities.None;

        public void Enqueue(InputEvent evt) => _channel.Writer.WriteAsync(evt).AsTask().Wait();
        public void Complete() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<InputEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return evt;
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>RAII wrapper for tests that don't need await for disposal.</summary>
    private sealed class SyncWrap : IDisposable
    {
        public KeyReleaseSynthesizer Synthesizer { get; }

        public SyncWrap(IAsyncInputDevice inner)
        {
            Synthesizer = new KeyReleaseSynthesizer(inner);
        }

        public void Dispose() => Synthesizer.DisposeAsync().AsTask().Wait();
    }
}
