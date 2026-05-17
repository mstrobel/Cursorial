using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// Decorator that fabricates the parts of the keyboard model VT terminals usually leave out:
/// <see cref="KeyEventKind.Up"/> events on devices that don't report releases, and
/// <see cref="KeyEvent.IsRepeat"/> on devices that don't distinguish auto-repeat from initial
/// activation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two timers per key.</b> A Down event arms two timers measured from the moment of that
/// Down:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Up timer</b> (<see cref="DefaultUpTimeout"/>, default 50 ms) — fires first.
///     The key transitions from <i>held</i> to <i>recently held</i> and a synthesized
///     <see cref="KeyEventKind.Up"/> is emitted. This is the consumer's "the key released"
///     signal.
///   </description></item>
///   <item><description>
///     <b>Repeat timer</b> (<see cref="DefaultRepeatTimeout"/>, default 150 ms) — fires later.
///     The key transitions from <i>recently held</i> to <i>forgotten</i> (removed from
///     tracking). After this, the next Down is treated as a fresh activation.
///   </description></item>
/// </list>
/// <para>
/// Any Down for a still-tracked key (<i>held</i> or <i>recently held</i>) cancels the prior
/// timers, restarts both timers, marks the forwarded event as
/// <see cref="KeyEvent.IsRepeat"/>=true, and sets <see cref="KeyEvent.RepeatCount"/> to the
/// running count since the initial press (2 for the first repeat, 3 for the next, …). A
/// real Up from the inner device (mixed protocols, partially reporting terminals) forgets
/// the key immediately, cancelling both pending timers.
/// </para>
/// <para>
/// The two-timer split lets the synth Up fire quickly (good UX for game-style "fire while
/// held" patterns) while still letting a slightly late next Down be classified as a
/// continuation rather than a fresh activation. For a user holding a key for several
/// seconds with no Downs arriving between t=0 and t=150 ms, the consumer sees: Down @ t=0,
/// synth Up @ t=50, Down (RepeatCount=1, IsRepeat=false) @ t≥150 (fresh press once the
/// repeat window expired), then Down (RepeatCount=N, IsRepeat=true) as the inner sends more
/// Downs, finally synth Up @ t=t_release+50.
/// </para>
/// <para>
/// <b>When the inner already reports repeats.</b> If the inner device's
/// <see cref="KeyboardCapabilities.ReportsRepeats"/> is true (Kitty keyboard, Win32 input
/// mode), we leave the IsRepeat / RepeatCount fields untouched so we don't fight the
/// protocol's classification. Up synthesis still runs for terminals that report repeats but
/// not releases.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// <b>When to use this.</b> Most VT terminals only report <see cref="KeyEventKind.Down"/>
/// events; apps that want a unified down/up model (game-style "fire while held," modal
/// shortcuts that activate on release, etc.) need releases regardless of the protocol the
/// terminal supports natively. Wrap an <see cref="IAsyncInputDevice"/> in this synthesizer
/// and consumers see release events on every key, real or fabricated, plus per-press
/// <see cref="KeyEvent.IsRepeat"/> classification. The <see cref="InputEvent.Synthesized"/>
/// flag distinguishes the fabricated up events from device-reported truth.
/// </para>
/// <para>
/// <b>Capabilities.</b> This decorator reports both
/// <see cref="KeyboardCapabilities.DistinguishesKeyUpDown"/> and
/// <see cref="KeyboardCapabilities.ReportsRepeats"/> as true regardless of the inner device's
/// values. Consumers querying capabilities through the decorator see "this device reports key
/// up and repeats" — true in the sense that they will receive both signals, even if some are
/// timer- / state-derived.
/// </para>
/// <para>
/// <b>Single-shot.</b> Like the other input-device decorators, <see cref="ReadAllAsync"/> can
/// be called at most once; subsequent calls throw. Disposing the synthesizer disposes the
/// inner device.
/// </para>
/// </remarks>
public sealed class KeyReleaseSynthesizer : IAsyncInputDevice, IInputDeviceDecorator
{
    /// <summary>Time after a Down before a release is fabricated, and the key enters the recently held state.</summary>
    public static TimeSpan DefaultUpTimeout { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Time after a Down before the key is forgotten entirely. Between
    /// <see cref="DefaultUpTimeout"/> and this, a new Down still counts as a repeat.
    /// </summary>
    public static TimeSpan DefaultRepeatTimeout { get; } = TimeSpan.FromMilliseconds(150);

    private readonly IAsyncInputDevice _inner;
    private readonly TimeSpan _upTimeout;
    private readonly TimeSpan _repeatTimeout;
    private readonly TimeProvider _time;
    private readonly bool _innerReportsRepeats;
    private readonly Channel<InputEvent> _channel;
    private readonly Dictionary<HeldKeyId, HeldKey> _heldKeys = new();
    private readonly object _heldKeysLock = new();

    private long _versionCounter;
    private int _started;
    private int _disposed;

    public KeyReleaseSynthesizer(IAsyncInputDevice inner,
                                 TimeSpan? upTimeout = null,
                                 TimeSpan? repeatTimeout = null,
                                 TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _upTimeout = upTimeout ?? DefaultUpTimeout;
        _repeatTimeout = repeatTimeout ?? DefaultRepeatTimeout;
        _time = timeProvider ?? TimeProvider.System;

        if (_upTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(upTimeout), "Up timeout must be positive.");

        if (_repeatTimeout < _upTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeatTimeout),
                "Repeat timeout must be at least as long as the up timeout — the key transitions "
                + "from held to recently-held when the up timer fires, then to forgotten when the "
                + "repeat timer fires.");
        }

        // Cache the inner's repeat-reporting capability — we only override IsRepeat on
        // devices that don't already classify it themselves. Kitty keyboard and Win32 input
        // mode set this to true; we don't want to fight them.
        _innerReportsRepeats = inner.Capabilities.Keyboard.ReportsRepeats;

        // Unbounded because the synthesizer's job is to forward events plus inject a small
        // additional rate of synthesized releases — bounding would risk back-pressure stalls
        // when consumers fall briefly behind. Multiple writers because both the inner-pump
        // task and per-key release timers write to the channel.
        _channel = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions
                                                       {
                                                           SingleReader = true,
                                                           SingleWriter = false,
                                                       });
    }

    /// <inheritdoc/>
    public IInputDevice Inner => _inner;

    /// <inheritdoc/>
    public InputCapabilities Capabilities
    {
        get
        {
            var innerCaps = _inner.Capabilities;

            return innerCaps with
                   {
                       Keyboard = innerCaps.Keyboard with
                                  {
                                      DistinguishesKeyUpDown = true,
                                      ReportsRepeats = true,
                                  },
                   };
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<InputEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "ReadAllAsync was already called on this synthesizer. Decorators are single-shot; " +
                "create a new KeyReleaseSynthesizer for a fresh read.");
        }

        // Drive the inner pump on a background task. It writes inner events plus tracks held
        // keys; per-key release timers run as separate tasks and write directly to the channel.
        var pump = Task.Run(() => PumpInnerAsync(cancellationToken), cancellationToken);

        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            // Surface any pump-side exceptions to the caller. The pump completes when the
            // inner enumerator does (normal end, cancellation, or fault).
            // @formatter:off
            try { await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) {}
            // @formatter:on
        }
    }

    private async Task PumpInnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _inner.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var toForward = evt;

                switch (evt)
                {
                    // Initial key-down OR auto-repeat. Synthesized=true is the synthesizer's
                    // own marker; we never re-process our own emissions (defensive — the
                    // inner device can't produce them, but someone could stack two
                    // synthesizers).
                    case KeyEvent { Kind: KeyEventKind.Down, Synthesized: false } down:
                        // Repeat inference: if we're already tracking this key, the inner
                        // device just sent us another press without an intervening release —
                        // that's auto-repeat. Only override when the inner doesn't already
                        // distinguish, and only when the event isn't already marked. The
                        // running repeat count is the Nth press since the initial activation
                        // (2 for the first auto-repeat, 3 for the next, …).
                        if (!_innerReportsRepeats && !down.IsRepeat &&
                            TryGetNextRepeatCount(HeldKeyId.From(down), out var repeatCount))
                        {
                            toForward = down with { IsRepeat = true, RepeatCount = repeatCount };
                        }

                        ScheduleTimers(down, cancellationToken);
                        break;

                    // Real release from a terminal that does report some / all key-ups —
                    // cancel any pending synthesis for that key (the next press will arm a new
                    // timer if needed).
                    case KeyEvent { Kind: KeyEventKind.Up, Synthesized: false } up:
                        ForgetHeldKey(up);
                        break;
                }

                await _channel.Writer.WriteAsync(toForward, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* expected on stop */
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// If the key is currently held, returns the running repeat count to assign to the next
    /// down event (2 for the first repeat, 3 for the next, …). Returns false when the key
    /// isn't held — the caller should treat the event as an initial press.
    /// </summary>
    private bool TryGetNextRepeatCount(HeldKeyId id, out int nextCount)
    {
        lock (_heldKeysLock)
        {
            if (_heldKeys.TryGetValue(id, out var current))
            {
                nextCount = current.RepeatCount + 1;
                return true;
            }
        }

        nextCount = 0;
        return false;
    }

    private void ScheduleTimers(KeyEvent down, CancellationToken cancellationToken)
    {
        long version = Interlocked.Increment(ref _versionCounter);
        var id = HeldKeyId.From(down);
        HeldKey held;

        lock (_heldKeysLock)
        {
            // Carry forward the count from the prior tracking record if this key was already
            // tracked (held or recently-held) — that's an auto-repeat in flight. A fresh entry
            // starts at RepeatCount=1 in the Held state.
            int repeatCount = _heldKeys.TryGetValue(id, out var prior) ? prior.RepeatCount + 1 : 1;
            held = new HeldKey(down, id, version, repeatCount, HeldKeyState.Held);
            _heldKeys[id] = held;
        }

        // Fire and forget both timers. Each task's check under the lock uses the version to
        // detect that a newer Down arrived (then the older timer no-ops). The Up timer
        // additionally guards on State == Held, so it doesn't double-emit if it ever races with
        // its own state transition.
        _ = UpAfterTimeoutAsync(held, cancellationToken);
        _ = ForgetAfterTimeoutAsync(held, cancellationToken);
    }

    private void ForgetHeldKey(KeyEvent up)
    {
        // Try exact-id removal first (Kitty / Win32 protocols populate Text on release). When
        // the up event carries no text and the held entry's Key is a printable Character, the
        // protocol didn't tell us *which* character was released — clear every held entry with
        // the same Key enum to avoid wedged tracking. Non-character keys (LeftArrow, F1, …)
        // are unique by Key alone, so the exact-id removal handles them.
        var id = HeldKeyId.From(up);

        lock (_heldKeysLock)
        {
            if (_heldKeys.Remove(id)) return;

            if (up is { Key: Key.Character, Text.IsEmpty: true })
            {
                List<HeldKeyId>? toRemove = null;

                foreach (var key in _heldKeys.Keys)
                {
                    if (key.Key == Key.Character)
                        (toRemove ??= new()).Add(key);
                }

                if (toRemove is not null)
                    foreach (var k in toRemove)
                        _heldKeys.Remove(k);
            }
        }
    }

    private async Task UpAfterTimeoutAsync(HeldKey held, CancellationToken cancellationToken)
    {
        // @formatter:off
        try { await Task.Delay(_upTimeout, _time, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // @formatter:on

        if (cancellationToken.IsCancellationRequested) return;

        // Re-check under the lock: only emit if this is still the current pending release
        // for this key AND it's still in the Held state. A newer press would have bumped the
        // version (older task no-ops); a real release from the inner would have removed the
        // key. Transition Held → RecentlyHeld in place — the version stays the same, so the
        // matching ForgetAfterTimeoutAsync continues to be valid.
        bool emit = false;

        lock (_heldKeysLock)
        {
            if (_heldKeys.TryGetValue(held.Id, out var current) &&
                current.Version == held.Version &&
                current.State == HeldKeyState.Held)
            {
                _heldKeys[held.Id] = current with { State = HeldKeyState.RecentlyHeld };
                emit = true;
            }
        }

        if (!emit) return;

        var release = held.OriginalDown with
                      {
                          Kind = KeyEventKind.Up,
                          IsRepeat = false,
                          RepeatCount = 1,
                          Synthesized = true,
                          Timestamp = _time.GetUtcNow()
                      };

        // @formatter:off
        try { await _channel.Writer.WriteAsync(release, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* synthesis aborted; fine */ }
        catch (ChannelClosedException) { /* channel completed during dispose; fine */ }
        // @formatter:on
    }

    private async Task ForgetAfterTimeoutAsync(HeldKey held, CancellationToken cancellationToken)
    {
        // @formatter:off
        try { await Task.Delay(_repeatTimeout, _time, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // @formatter:on

        if (cancellationToken.IsCancellationRequested) return;

        // Remove the tracking entry if no newer Down has superseded it. After this fires, the
        // next Down for this key starts a fresh hold (RepeatCount=1, IsRepeat=false).
        lock (_heldKeysLock)
        {
            if (_heldKeys.TryGetValue(held.Id, out var current) &&
                current.Version == held.Version)
            {
                _heldKeys.Remove(held.Id);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Closing the channel unblocks any pending writes from release-timer tasks.
        _channel.Writer.TryComplete();

        // @formatter:off
        try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        // @formatter:on
    }

    /// <summary>
    /// Per-key tracking state. <see cref="Held"/> is the window where the synth Up hasn't fired
    /// yet (within the up timeout of the last Down). <see cref="RecentlyHeld"/> is the window
    /// after the synth Up has fired but before the repeat timeout expires — a Down arriving
    /// here still counts as a repeat continuation rather than a fresh activation.
    /// </summary>
    private enum HeldKeyState
    {
        Held,
        RecentlyHeld
    }

    /// <summary>
    /// Per-key tracking record. <see cref="Version"/> is the discriminator across rescheduled
    /// timers; <see cref="RepeatCount"/> is the running count of presses for this hold
    /// (1 for the initial press, 2+ for subsequent auto-repeats); <see cref="State"/> tracks
    /// whether the synth Up has already fired.
    /// </summary>
    private sealed record HeldKey(
        KeyEvent OriginalDown,
        HeldKeyId Id,
        long Version,
        int RepeatCount,
        HeldKeyState State);

    /// <summary>
    /// Discriminator for the held-keys dictionary. The <see cref="Key"/> enum value alone isn't
    /// enough because <see cref="Key.Character"/> represents *every* printable key — 'a', 'b',
    /// and '!' all share that enum value, and the codepoint lives in
    /// <see cref="KeyEvent.Text"/>. The text payload (materialized to a string for stable
    /// hashing) is what disambiguates one character press from another.
    /// </summary>
    private readonly record struct HeldKeyId(Key Key, string Text)
    {
        public static HeldKeyId From(KeyEvent evt)
            => new(evt.Key, evt.Text.IsEmpty ? string.Empty : evt.Text.ToString());
    }
}