using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.Input;

public class KeyReleaseSynthesizerTests
{
    // Real-time tests run with a 50 ms up-timeout, a 150 ms repeat-timeout, and a 400 ms
    // collection window — short enough to keep test runtime in the hundreds-of-ms but long
    // enough that scheduler jitter doesn't cause flakes. If these become flaky in CI we can
    // swap to FakeTimeProvider from Microsoft.Extensions.TimeProvider.Testing.
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RepeatTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CollectionWindow = TimeSpan.FromMilliseconds(400);

    private static KeyReleaseSynthesizer NewSynth(IAsyncInputDevice inner) =>
        new(inner, upTimeout: UpTimeout, repeatTimeout: RepeatTimeout);

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

    private static async Task<List<InputEvent>> CollectAsync(KeyReleaseSynthesizer sync,
                                                             TimeSpan window,
                                                             CancellationToken cancellationToken = default)
    {
        var collected = new List<InputEvent>();
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowCts.CancelAfter(window);

        try
        {
            await foreach (var evt in sync.ReadAllAsync(windowCts.Token).ConfigureAwait(false))
                collected.Add(evt);
        }
        catch (OperationCanceledException) { /* expected when the window expires or caller cancels */ }

        return collected;
    }

    [Fact]
    public async Task SingleKeyDown_AfterIdleTimeout_EmitsSynthesizedUp()
    {
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        device.Enqueue(KeyDown(Key.Character));

        var collected = await CollectAsync(sync, CollectionWindow);

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
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        // Three rapid down events; each should re-arm the timer. After they stop arriving,
        // exactly one synthesized release fires (not three).
        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            await Task.Delay(20);
            device.Enqueue(KeyDown(Key.Character, isRepeat: true));
            await Task.Delay(20);
            device.Enqueue(KeyDown(Key.Character, isRepeat: true));
        });

        var collected = await CollectAsync(sync, CollectionWindow);

        // 3 down events + 1 synthesized up = 4 total
        Assert.Equal(4, collected.Count);
        var ups = collected.OfType<KeyEvent>().Where(k => k.Kind == KeyEventKind.Up).ToList();
        Assert.Single(ups);
        Assert.True(ups[0].Synthesized);
    }

    [Fact]
    public async Task RealUpFromInnerDevice_CancelsPendingSynthesis()
    {
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            // Real release from a terminal that does report key-up; should cancel the timer
            // so we don't emit a duplicate synthesized release.
            await Task.Delay(10);
            device.Enqueue(KeyUp(Key.Character));
        });

        var collected = await CollectAsync(sync, CollectionWindow);

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
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(() =>
        {
            device.Enqueue(KeyDown(Key.LeftArrow));
            device.Enqueue(KeyDown(Key.RightArrow));
        });

        var collected = await CollectAsync(sync, CollectionWindow);

        // 2 downs + 2 synthesized ups
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();
        Assert.Equal(2, ups.Count);
        Assert.Contains(ups, u => u.Key == Key.LeftArrow);
        Assert.Contains(ups, u => u.Key == Key.RightArrow);
        Assert.All(ups, u => Assert.True(u.Synthesized));
    }

    [Fact]
    public async Task NonKeyEvent_PassesThroughUntouched()
    {
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        var paste = new PasteEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Text = "hello".AsMemory(),
                    };
        device.Enqueue(paste);
        device.Complete();

        var collected = await CollectAsync(sync, CollectionWindow);
        Assert.Single(collected);
        Assert.Same(paste, collected[0]);
    }

    [Fact]
    public async Task InnerCompletes_StreamCompletesCleanly()
    {
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        device.Enqueue(KeyDown(Key.Character));
        device.Complete();

        // Don't pass a cancellation timeout — completion of the inner device must end our
        // stream on its own.
        var collected = new List<InputEvent>();
        await foreach (var evt in sync.ReadAllAsync())
            collected.Add(evt);

        // We saw the down. The synthesized up fires after the inner completes (the timer was
        // armed before completion). Outer enumeration ends when the channel completes —
        // which happens when the inner pump finishes AND any pending writes from the release
        // timer are done.
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

        // The synthesizer fills in both up/down distinction and repeat inference, so it
        // reports both as true even when the inner doesn't.
        Assert.True(sync_disposed_ignored.Synthesizer.Capabilities.Keyboard.DistinguishesKeyUpDown);
        Assert.True(sync_disposed_ignored.Synthesizer.Capabilities.Keyboard.ReportsRepeats);
        // Other keyboard caps pass through unchanged.
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
        // counts as a continuation rather than a new activation. Consumer sees Down(initial),
        // synth Up, Down(IsRepeat=true) — the latter at t≈100 ms in this test.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            // Wait long enough for synth Up to fire (> 50 ms), but stay inside the repeat
            // window (< 150 ms).
            await Task.Delay(90);
            device.Enqueue(KeyDown(Key.Character));
        });

        var collected = await CollectAsync(sync, CollectionWindow);

        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.True(downs[1].IsRepeat);
        Assert.Equal(2, downs[1].RepeatCount);

        // Two synth Ups land in the collection window: one fires at upTimeout after the
        // first Down, the other fires at upTimeout after the second Down. Their ordering
        // relative to the Down stream is: Down(initial), synth Up #1, Down(repeat),
        // synth Up #2. The mid-stream Up is the whole point of the recently-held window —
        // we already told the consumer the key released, then a continuation arrived.
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
        // For a key held briefly then released (no further activity), the consumer sees
        // exactly one synth Up — emitted at upTimeout. The repeat timer's only effect after
        // upTimeout is to remove the key from internal tracking, which the consumer doesn't
        // observe.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        device.Enqueue(KeyDown(Key.Character));

        var collected = await CollectAsync(sync, CollectionWindow);

        Assert.Equal(2, collected.Count); // Down + synth Up
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
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        device.Enqueue(KeyDown(Key.Character));
        device.Complete();

        var collected = await CollectAsync(sync, CollectionWindow);
        var firstDown = collected.OfType<KeyEvent>().First(k => k.Kind == KeyEventKind.Down);
        Assert.False(firstDown.IsRepeat);
    }

    [Fact]
    public async Task SecondPress_WhileHeld_MarkedAsRepeat()
    {
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            // Second press arrives while the first is still considered held (within the idle
            // timeout) — the synthesizer infers auto-repeat and flips IsRepeat.
            await Task.Delay(20);
            device.Enqueue(KeyDown(Key.Character));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
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
        // For a single hold (no intervening release), each subsequent press carries an
        // incrementing RepeatCount: 1, 2, 3, 4 …
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            await Task.Delay(15);
            device.Enqueue(KeyDown(Key.Character));
            await Task.Delay(15);
            device.Enqueue(KeyDown(Key.Character));
            await Task.Delay(15);
            device.Enqueue(KeyDown(Key.Character));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
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
        // The running count is per-hold. After the synthesizer's repeat timeout fires (the
        // key transitions from recently-held to forgotten), the next press starts a fresh
        // hold with RepeatCount=1. Wait past the longer repeat timeout — waiting only past
        // the up timeout would leave the key recently-held, and the next Down would still be
        // marked as a repeat.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            await Task.Delay(15);
            device.Enqueue(KeyDown(Key.Character)); // RepeatCount=2
            // Wait past the repeat timeout (150 ms) — synth Up fires at 50, forget at 150.
            await Task.Delay(220);
            device.Enqueue(KeyDown(Key.Character)); // fresh hold, RepeatCount=1
        });

        var collected = await CollectAsync(sync, TimeSpan.FromMilliseconds(500));
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(3, downs.Count);
        Assert.Equal(1, downs[0].RepeatCount);
        Assert.Equal(2, downs[1].RepeatCount);
        Assert.Equal(1, downs[2].RepeatCount); // reset after the release
        Assert.False(downs[2].IsRepeat);
    }

    [Fact]
    public async Task PressAfterRepeatTimeout_NotMarkedAsRepeat()
    {
        // Once the synthesized release fires AND the repeat-timeout window elapses, the key
        // is fully forgotten. A subsequent press is a fresh activation, not a repeat. Waiting
        // only past the up-timeout would leave the key in the "recently held" state, where a
        // new Down would still count as a repeat — so wait past the longer repeat timeout.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(KeyDown(Key.Character));
            // Wait past the repeat timeout (150 ms) — synth Up fires at 50 ms, key transitions
            // to recently-held, then forget timer fires at 150 ms.
            await Task.Delay(220);
            device.Enqueue(KeyDown(Key.Character));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.False(downs[1].IsRepeat); // fresh activation
    }

    [Fact]
    public async Task DifferentCharacters_NotMarkedAsRepeat()
    {
        // Regression: every printable key shares Key.Character — 'a' and 'b' have the same
        // Key enum value, distinguished only by Text. If the synthesizer keyed its held-keys
        // dictionary on Key alone, pressing 'a' then 'b' would falsely treat 'b' as an auto-
        // repeat of 'a' (and the synthesized release for 'a' would emit with Text="b").
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(() =>
        {
            device.Enqueue(CharDown("a"));
            device.Enqueue(CharDown("b"));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.False(downs[1].IsRepeat); // different character, fresh activation
        Assert.Equal(1, downs[0].RepeatCount);
        Assert.Equal(1, downs[1].RepeatCount);
    }

    [Fact]
    public async Task DifferentCharacters_EachGetOwnSynthesizedRelease()
    {
        // Each character's hold should produce its own synthesized release carrying its own
        // Text, not a release whose Text bleeds from another character's tracking entry.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        device.Enqueue(CharDown("a"));
        device.Enqueue(CharDown("b"));
        // Don't complete the device — completing it tears down the pump and the channel,
        // which racing release timers can't write to. The CollectionWindow elapsing is what
        // ends the test's enumeration.

        var collected = await CollectAsync(sync, CollectionWindow);
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
        // The flip side of DifferentCharacters: pressing 'a' twice in quick succession IS an
        // auto-repeat. The discriminator must consider Text equality.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(CharDown("a"));
            await Task.Delay(15);
            device.Enqueue(CharDown("a"));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
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
        // When the inner protocol carries Text on the up event (Kitty keyboard, Win32 input),
        // the synthesizer should clear precisely that hold and leave other held characters
        // alone.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(CharDown("a"));
            device.Enqueue(CharDown("b"));
            await Task.Delay(10);
            device.Enqueue(CharUp("a")); // real release for 'a' only
        });

        var collected = await CollectAsync(sync, CollectionWindow);
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();

        // Expect: one real up for 'a' (not synthesized), plus one synthesized up for 'b'
        // after its idle timeout (since 'b' was never released by the inner).
        var realA = ups.SingleOrDefault(u => !u.Synthesized && u.Text.Span.SequenceEqual("a"));
        Assert.NotNull(realA);
        var synthB = ups.SingleOrDefault(u => u.Synthesized && u.Text.Span.SequenceEqual("b"));
        Assert.NotNull(synthB);
    }

    [Fact]
    public async Task RealUpFromInner_WithoutText_ClearsAllCharacterHolds()
    {
        // Some protocols may report Key.Character up events without populating Text. In that
        // case we can't tell which character was released — defensively clear every held
        // character entry to avoid wedged tracking. Non-character holds are left untouched.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(async () =>
        {
            device.Enqueue(CharDown("a"));
            device.Enqueue(CharDown("b"));
            device.Enqueue(KeyDown(Key.LeftArrow));
            await Task.Delay(10);
            device.Enqueue(KeyUp(Key.Character)); // ambiguous release — clears 'a' and 'b'
        });

        var collected = await CollectAsync(sync, CollectionWindow);
        var ups = collected.OfType<KeyEvent>()
                           .Where(k => k.Kind == KeyEventKind.Up)
                           .ToList();

        // The ambiguous release itself is forwarded (we don't drop inner events). LeftArrow
        // is a non-character hold, so it gets its own synthesized release once the timeout
        // fires. The 'a' and 'b' character holds are forgotten by the ambiguous release —
        // no synthesized character ups should appear.
        Assert.Contains(ups, u => u.Key == Key.LeftArrow && u.Synthesized);
        Assert.DoesNotContain(ups, u => u.Key == Key.Character && u.Synthesized);
    }

    [Fact]
    public async Task DifferentKey_NotMarkedAsRepeat()
    {
        // Repeats are per-key. Pressing 'a' then 'b' while 'a' is held doesn't make 'b' a repeat.
        var device = new TestInputDevice();
        await using var sync = NewSynth(device);

        _ = Task.Run(() =>
        {
            device.Enqueue(KeyDown(Key.LeftArrow));
            device.Enqueue(KeyDown(Key.RightArrow));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
        var rightDown = collected.OfType<KeyEvent>()
                                 .First(k => k.Key == Key.RightArrow && k.Kind == KeyEventKind.Down);
        Assert.False(rightDown.IsRepeat);
    }

    [Fact]
    public async Task InnerAlreadyReportsRepeats_NotOverridden()
    {
        // When the inner device has KeyboardCapabilities.ReportsRepeats=true (Kitty keyboard,
        // Win32 input mode), the synthesizer leaves IsRepeat alone — we never want to fight
        // a protocol that's reporting the real state.
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
        await using var sync = NewSynth(device);

        _ = Task.Run(() =>
        {
            // Inner sends two presses, neither marked as repeat. With ReportsRepeats=true on
            // the inner caps, the synthesizer trusts the inner's classification — both arrive
            // with IsRepeat=false even though the second is for the same held key.
            device.Enqueue(KeyDown(Key.Character));
            device.Enqueue(KeyDown(Key.Character));
        });

        var collected = await CollectAsync(sync, CollectionWindow);
        var downs = collected.OfType<KeyEvent>()
                             .Where(k => k.Kind == KeyEventKind.Down)
                             .ToList();
        Assert.Equal(2, downs.Count);
        Assert.False(downs[0].IsRepeat);
        Assert.False(downs[1].IsRepeat); // synthesizer respected the inner's classification
    }

    [Fact]
    public async Task InnerMarkedAsRepeat_PreservedThroughSynthesizer()
    {
        // If the inner DOES set IsRepeat (e.g., Kitty keyboard repeat event), the synthesizer
        // forwards it untouched even when its own held-key tracking would say "not held yet."
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
        await using var sync = NewSynth(device);

        device.Enqueue(KeyDown(Key.Character, isRepeat: true));
        device.Complete();

        var collected = await CollectAsync(sync, CollectionWindow);
        var down = Assert.IsType<KeyEvent>(collected[0]);
        Assert.True(down.IsRepeat);
    }

    // ---- Test helpers ----

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

    /// <summary>RAII wrapper that uses synchronous disposal — useful in tests that don't need await.</summary>
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
