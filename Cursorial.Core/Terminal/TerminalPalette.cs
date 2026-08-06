using Cursorial.Input.Events;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Terminal;

/// <summary>
/// Query and replace the terminal's 256-color palette via the OSC 4 / OSC 104 protocols.
/// <see cref="Set(int, in Color)"/> writes a palette entry; <see cref="QueryAsync"/> asks the
/// terminal for a specific entry and awaits its reply. Modifications are tracked across the
/// instance's lifetime and reverted on <see cref="Dispose"/> via OSC 104 so applications can
/// safely apply a custom theme without leaking it past process exit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring queries.</b> Replies arrive as
/// <see cref="DeviceResponseEvent"/>s with <see cref="DeviceResponseKind.PaletteColor"/>. The
/// controller doesn't tap the input device itself — the application routes input events it
/// observes (typically while iterating <c>session.Input.ReadAllAsync</c>) through
/// <see cref="OnInputEvent"/>. This keeps a single owner for the input stream and avoids the
/// tee-the-pipe complexity that would otherwise come with concurrent consumers.
/// </para>
/// <para>
/// <b>Capability gate.</b> When <see cref="OutputCapabilities.Color"/>.<c>OscPaletteSet</c> is
/// false, <see cref="IsSupported"/> is false and every operation becomes a no-op. Useful so
/// callers can write protocol-agnostic application code that gracefully degrades on
/// non-supporting terminals.
/// </para>
/// <para>
/// <b>Thread safety.</b> The internal pending-query map and modification set are guarded by a
/// single lock; concurrent <see cref="Set(int, in Color)"/> / <see cref="QueryAsync"/> /
/// <see cref="OnInputEvent"/> calls are safe. The underlying <see cref="IOutputByteSink"/>'s
/// thread safety is the caller's responsibility — if multiple threads share a sink, serialize
/// access externally.
/// </para>
/// </remarks>
public sealed class TerminalPalette : IDisposable
{
    [Flags]
    private enum ModifiedDefaults : byte { None = 0, Foreground = 1, Background = 2, Cursor = 4 }

    /// <summary>Default budget for a single <see cref="QueryAsync"/> call.</summary>
    public static TimeSpan DefaultQueryTimeout { get; } = TimeSpan.FromMilliseconds(250);

    private readonly IOutputByteSink _sink;
    private readonly bool _supported;
    private readonly ModifiedDefaults _supportedDefaults;
    private readonly object _lock = new();
    private readonly HashSet<byte> _modified = new();
    private readonly Dictionary<byte, TaskCompletionSource<Color?>> _pending = new();
    private ModifiedDefaults _modifiedDefaults;
    private int _disposed;

    /// <summary>
    /// Construct a controller bound to the supplied sink + capabilities. The capabilities are
    /// snapshotted at construction time; revoking palette-set support later (uncommon) doesn't
    /// retroactively disable the controller.
    /// </summary>
    public TerminalPalette(IOutputByteSink sink, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(capabilities);

        _sink = sink;
        _supported = capabilities.Color.OscPaletteSet;

        // Default-color control. Foreground / background (OSC 10/11 set, OSC 110/111 reset) ride
        // on DefaultColorReset. CURSOR color (OSC 12 set, OSC 112 reset) is a SEPARATE capability
        // — Cursor.ColorControl — and must be gated on it, not lumped under DefaultColorReset:
        // some terminals reset default fg/bg fine but don't implement cursor color at all (Apple
        // Terminal), and emitting an unsupported OSC 12 / 112 there is mishandled (it surfaces as
        // a stray glyph in the output). Gate each independently so we never send a sequence the
        // negotiated capabilities say the terminal can't handle.
        var defaults = ModifiedDefaults.None;
        if (capabilities.Color.DefaultColorReset)
            defaults |= ModifiedDefaults.Foreground | ModifiedDefaults.Background;
        if (capabilities.Cursor.ColorControl)
            defaults |= ModifiedDefaults.Cursor;
        _supportedDefaults = defaults;
    }

    /// <summary>
    /// True when the negotiated terminal advertises OSC 4 palette-set support. When false,
    /// <see cref="Set(int, in Color)"/>, <see cref="QueryAsync"/>, <see cref="Reset(int)"/>,
    /// and <see cref="ResetAll"/> are no-ops.
    /// </summary>
    public bool IsSupported => _supported;

    /// <summary>
    /// Snapshot of palette indices the controller has modified during its lifetime — useful for
    /// auditing or testing. The set is mutable internally; the returned array is a copy.
    /// </summary>
    public byte[] ModifiedIndices
    {
        get
        {
            lock (_lock) return _modified.ToArray();
        }
    }

    /// <summary>
    /// Set palette entry <paramref name="index"/> to <paramref name="color"/>. The color must be
    /// <see cref="ColorKind.Rgb"/>; <see cref="ColorKind.Default"/> and <see cref="ColorKind.Palette"/>
    /// throw <see cref="ArgumentException"/> (they have no defined RGB value).
    /// </summary>
    public void Set(int index, in Color color)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (index is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(index), "Palette index must be between 0 and 255 inclusive.");

        if (color.Kind != ColorKind.Rgb)
            throw new ArgumentException("Palette entries must be set to an RGB color.", nameof(color));

        if (!_supported) return;

        PaletteWriter.WriteSet(_sink.Writer, (byte) index, color.Red, color.Green, color.Blue);

        lock (_lock) _modified.Add((byte) index);
    }

    /// <summary>
    /// Set palette terminal foreground to <paramref name="color"/>. The color must be
    /// <see cref="ColorKind.Rgb"/>; <see cref="ColorKind.Default"/> and <see cref="ColorKind.Palette"/>
    /// throw <see cref="ArgumentException"/> (they have no defined RGB value).
    /// </summary>
    public void SetForeground(in Color color)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        
        if (color.Kind != ColorKind.Rgb)
            throw new ArgumentException("Palette entries must be set to an RGB color.", nameof(color));

        if (!_supportedDefaults.HasFlag(ModifiedDefaults.Foreground)) return;

        PaletteWriter.WriteSetForeground(_sink.Writer, color.Red, color.Green, color.Blue);

        lock (_lock) _modifiedDefaults |= ModifiedDefaults.Foreground;
    }

    /// <summary>
    /// Resets the terminal's default foreground color.
    /// </summary>
    public void ResetForeground()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_supportedDefaults.HasFlag(ModifiedDefaults.Foreground)) return;

        PaletteWriter.WriteResetForeground(_sink.Writer);

        lock (_lock) _modifiedDefaults &= ~ModifiedDefaults.Foreground;
    }

    /// <summary>
    /// Set palette terminal background to <paramref name="color"/>. The color must be
    /// <see cref="ColorKind.Rgb"/>; <see cref="ColorKind.Default"/> and <see cref="ColorKind.Palette"/>
    /// throw <see cref="ArgumentException"/> (they have no defined RGB value).
    /// </summary>
    public void SetBackground(in Color color)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        
        if (color.Kind != ColorKind.Rgb)
            throw new ArgumentException("Palette entries must be set to an RGB color.", nameof(color));

        if (!_supportedDefaults.HasFlag(ModifiedDefaults.Background)) return;

        PaletteWriter.WriteSetBackground(_sink.Writer, color.Red, color.Green, color.Blue);

        lock (_lock) _modifiedDefaults |= ModifiedDefaults.Background;
    }

    /// <summary>
    /// Resets the terminal's default background color.
    /// </summary>
    public void ResetBackground()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_supportedDefaults.HasFlag(ModifiedDefaults.Background)) return;

        PaletteWriter.WriteResetBackground(_sink.Writer);

        lock (_lock) _modifiedDefaults &= ~ModifiedDefaults.Background;
    }

    /// <summary>
    /// Set palette terminal cursor to <paramref name="color"/>. The color must be
    /// <see cref="ColorKind.Rgb"/>; <see cref="ColorKind.Default"/> and <see cref="ColorKind.Palette"/>
    /// throw <see cref="ArgumentException"/> (they have no defined RGB value).
    /// </summary>
    public void SetCursor(in Color color)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        
        if (color.Kind != ColorKind.Rgb)
            throw new ArgumentException("Palette entries must be set to an RGB color.", nameof(color));

        if (!_supportedDefaults.HasFlag(ModifiedDefaults.Cursor)) return;

        PaletteWriter.WriteSetCursor(_sink.Writer, color.Red, color.Green, color.Blue);

        lock (_lock) _modifiedDefaults |= ModifiedDefaults.Cursor;
    }

    /// <summary>
    /// Resets the terminal's default cursor color.
    /// </summary>
    public void ResetCursor()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_supportedDefaults.HasFlag(ModifiedDefaults.Cursor)) return;

        PaletteWriter.WriteResetCursor(_sink.Writer);

        lock (_lock) _modifiedDefaults &= ~ModifiedDefaults.Cursor;
    }

    /// <summary>
    /// Issue an OSC 4 query for entry <paramref name="index"/> and await the matching response.
    /// Returns the current color on success, <see langword="null"/> when the terminal doesn't
    /// reply within <paramref name="timeout"/> (defaults to <see cref="DefaultQueryTimeout"/>)
    /// or when palette support isn't advertised.
    /// </summary>
    /// <remarks>
    /// A subsequent <see cref="QueryAsync"/> for the same index before the first has resolved
    /// cancels the earlier task. The controller does not flush the sink writer after queuing
    /// the query — callers should ensure the sink is flushed (most async I/O paths do this
    /// implicitly, but a fully bespoke wiring may need an explicit
    /// <c>session.Output.Writer.FlushAsync()</c>).
    /// </remarks>
    public async Task<Color?> QueryAsync(int index,
                                         TimeSpan? timeout = null,
                                         CancellationToken cancellationToken = default)
    {
        return await QueryCoreAsync(index, true, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Color?> QueryCoreAsync(int index,
                                              bool flushWriter,
                                              TimeSpan? timeout = null,
                                              CancellationToken cancellationToken = default)
    {
        if (index is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(index), "Palette index must be between 0 and 255 inclusive.");

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_supported) return null;

        var tcs = new TaskCompletionSource<Color?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_pending.TryGetValue((byte) index, out var existing))
                existing.TrySetCanceled(cancellationToken);

            _pending[(byte) index] = tcs;
        }

        PaletteWriter.WriteQuery(_sink.Writer, (byte) index);
        
        try
        {
            if (flushWriter)
                await _sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock) _pending.Remove((byte) index);
            throw;
        }

        await using var registration = cancellationToken.Register(static state => { ((TaskCompletionSource<Color?>) state!).TrySetCanceled(); }, tcs);

        var timeoutTask = Task.Delay(timeout ?? DefaultQueryTimeout, cancellationToken);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

        if (completed != tcs.Task)
        {
            lock (_lock) _pending.Remove((byte) index);
            return null;
        }

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Issues OSC 4 queries for every entry in the extended 256-color palette and await the
    /// matching response. Returns the current color on success, <see langword="null"/> when the terminal doesn't
    /// reply within <paramref name="timeout"/> (defaults to <see cref="DefaultQueryTimeout"/>)
    /// or when palette support isn't advertised.
    /// </summary>
    /// <remarks>
    /// If the terminal does not return a complete palette, the returned palette will be
    /// truncated at the last valid entry. Intermediate entries that did not produce a value
    /// will be populated with <see cref="Color.Default"/>.
    /// </remarks>
    public Task<IColorPalette?> QueryExtendedPaletteAsync(TimeSpan? timeout = null,
                                                          CancellationToken cancellationToken = default)
    {
        return QueryPaletteAsync(256, timeout, cancellationToken);
    }

    /// <summary>
    /// Issues OSC 4 queries for every entry in the standard 16-color palette and await the
    /// matching response. Returns the current color on success, <see langword="null"/> when the terminal doesn't
    /// reply within <paramref name="timeout"/> (defaults to <see cref="DefaultQueryTimeout"/>)
    /// or when palette support isn't advertised.
    /// </summary>
    /// <remarks>
    /// If the terminal does not return a complete palette, the returned palette will be
    /// truncated at the last valid entry. Intermediate entries that did not produce a value
    /// will be populated with <see cref="Color.Default"/>.
    /// </remarks>
    public Task<IColorPalette?> QueryStandardPaletteAsync(TimeSpan? timeout = null,
                                                          CancellationToken cancellationToken = default)
    {
        return QueryPaletteAsync(16, timeout, cancellationToken);
    }

    private async Task<IColorPalette?> QueryPaletteAsync(int count,
                                                         TimeSpan? timeout = null,
                                                         CancellationToken cancellationToken = default)
    {
        var ctsTimeout = new CancellationTokenSource(timeout ?? DefaultQueryTimeout);
        var combinedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ctsTimeout.Token, cancellationToken);
        var combinedToken = combinedTokenSource.Token;

        Task<Color?>[] subtasks = Enumerable.Range(0, count)
                                            .Select(i => QueryCoreAsync(i, i == count - 1, timeout, combinedToken))
                                            .ToArray();

        Task<Color[]> colors = Task.WhenAll(subtasks)
                                   .ContinueWith(t =>
                                                 {
                                                     if (t.IsCanceled || t.IsFaulted)
                                                         return Array.Empty<Color>();

                                                     return t.Result
                                                             .Select(c => c.GetValueOrDefault(Color.Default))
                                                             .Reverse()
                                                             .SkipWhile(c => c == Color.Default)
                                                             .Reverse()
                                                             .ToArray();
                                                 },
                                                 cancellationToken);

        try
        {
            var paletteArray = await colors.ConfigureAwait(false);

            if (paletteArray.Length is 0)
                return IColorPalette.Empty;

            return new ColorPalette(paletteArray);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            if (combinedToken.IsCancellationRequested)
                return null;

            throw;
        }
    }

    /// <summary>
    /// Reset palette entry <paramref name="index"/> to the terminal's startup default. Drops it
    /// from the tracked-modifications set so it isn't re-reset on <see cref="Dispose"/>.
    /// </summary>
    public void Reset(int index)
    {
        if (index is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(index), "Palette index must be between 0 and 255 inclusive.");

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_supported) return;

        PaletteWriter.WriteReset(_sink.Writer, (byte) index);
        
        lock (_lock) _modified.Remove((byte) index);
    }

    /// <summary>
    /// Reset every entry of the 256-color palette to the terminal's startup default. Clears the
    /// tracked-modifications set.
    /// </summary>
    public void ResetAll()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_supported) return;

        PaletteWriter.WriteResetAll(_sink.Writer);

        lock (_lock)
        {
            _modified.Clear();
            _modifiedDefaults = ModifiedDefaults.None;
        }
    }

    /// <summary>
    /// Route a <see cref="DeviceResponseEvent"/> (typically observed while iterating
    /// <c>session.Input.ReadAllAsync</c>) through the controller so palette-color replies can
    /// resolve pending <see cref="QueryAsync"/> tasks. Non-palette events are ignored — calling
    /// this for every event you see is safe and cheap.
    /// </summary>
    public void OnInputEvent(InputEvent inputEvent)
    {
        if (inputEvent is not DeviceResponseEvent { Kind: DeviceResponseKind.PaletteColor } response)
            return;

        if (!TryParsePalettePayload(response.Payload.Span, out int idx, out byte r, out byte g, out byte b))
            return;

        if (idx is < 0 or > 255) return;

        TaskCompletionSource<Color?>? tcs;

        lock (_lock)
        {
            if (!_pending.Remove((byte) idx, out tcs))
                return;
        }

        tcs.TrySetResult(Color.FromRgb(r, g, b));
    }

    /// <summary>
    /// Reset every palette entry the controller modified during its lifetime, cancel any
    /// outstanding queries, and mark the instance disposed. Idempotent; safe to call from a
    /// <c>finally</c> block or <c>using</c> statement.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        
        ModifiedDefaults modifiedDefaults;
        byte[]? modified = null;
        TaskCompletionSource<Color?>[]? pending = null;

        lock (_lock)
        {
            if (_modified.Count > 0)
            {
                int i = 0;
                modified = new byte[_modified.Count];
                foreach (var m in _modified) modified[i++] = m;
                _modified.Clear();
            }

            modifiedDefaults = _modifiedDefaults;
            _modifiedDefaults = ModifiedDefaults.None;

            if (_pending.Count > 0)
            {
                pending = _pending.Values.ToArray();
                _pending.Clear();
            }
        }

        // @formatter:off
        if (modified is not null && _supported)
        {
            try { PaletteWriter.WriteResetMany(_sink.Writer, modified); }
            catch { /* best-effort during teardown */ }
        }
        
        if (modifiedDefaults is not ModifiedDefaults.None)
        {
            // No need to check `_supportedDefaults` here; we wouldn't have modified
            // any defaults if we didn't support them.

            if (modifiedDefaults.HasFlag(ModifiedDefaults.Foreground))
            {
                try { PaletteWriter.WriteResetForeground(_sink.Writer); }
                catch { /* best-effort during teardown */ }
            }
            
            if (modifiedDefaults.HasFlag(ModifiedDefaults.Background))
            {
                try { PaletteWriter.WriteResetBackground(_sink.Writer); }
                catch { /* best-effort during teardown */ }
            }
            
            if (modifiedDefaults.HasFlag(ModifiedDefaults.Cursor))
            {
                try { PaletteWriter.WriteResetCursor(_sink.Writer); }
                catch { /* best-effort during teardown */ }
            }
        } 
        // @formatter:on

        if (pending is not null)
        {
            foreach (var tcs in pending)
                tcs.TrySetCanceled();
        }
    }

    // ---- Payload parsing ----

    /// <summary>
    /// Parse <c>&lt;index&gt;;rgb:RRRR/GGGG/BBBB</c> — the OSC 4 reply payload as it arrives in
    /// <see cref="DeviceResponseEvent.Payload"/>. Channels accept 1–4 hex digits.
    /// </summary>
    internal static bool TryParsePalettePayload(ReadOnlySpan<byte> payload,
                                                out int index, out byte red, out byte green, out byte blue)
    {
        index = 0;
        red = green = blue = 0;

        int separator = payload.IndexOf((byte) ';');
        if (separator < 0) return false;

        if (!TryParseAsciiInt(payload[..separator], out index)) return false;
        return TryParseRgbColor(payload[(separator + 1)..], out red, out green, out blue);
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.IsEmpty) return false;
        long acc = 0;

        foreach (byte b in bytes)
        {
            if (b is < (byte) '0' or > (byte) '9') return false;
            acc = acc * 10 + (b - (byte) '0');
            if (acc > int.MaxValue) return false;
        }

        value = (int) acc;
        return true;
    }

    private static bool TryParseRgbColor(ReadOnlySpan<byte> payload, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;

        ReadOnlySpan<byte> prefix = "rgb:"u8;

        if (payload.Length < prefix.Length) return false;
        if (!payload[..prefix.Length].SequenceEqual(prefix)) return false;

        var rest = payload[prefix.Length..];

        int slash1 = rest.IndexOf((byte) '/');
        if (slash1 < 0) return false;

        var redSpan = rest[..slash1];
        var afterRed = rest[(slash1 + 1)..];

        int slash2 = afterRed.IndexOf((byte) '/');
        if (slash2 < 0) return false;

        var greenSpan = afterRed[..slash2];
        var blueSpan = afterRed[(slash2 + 1)..];

        return TryParseHexChannel(redSpan, out r) &&
               TryParseHexChannel(greenSpan, out g) &&
               TryParseHexChannel(blueSpan, out b);
    }

    private static bool TryParseHexChannel(ReadOnlySpan<byte> hex, out byte value)
    {
        value = 0;

        if (hex.IsEmpty || hex.Length > 4) return false;

        if (hex.Length == 1)
        {
            if (!TryParseHexDigit(hex[0], out int d)) return false;
            value = (byte) ((d << 4) | d);
            return true;
        }

        if (!TryParseHexDigit(hex[0], out int hi)) return false;
        if (!TryParseHexDigit(hex[1], out int lo)) return false;

        value = (byte) ((hi << 4) | lo);
        return true;
    }

    private static bool TryParseHexDigit(byte b, out int value)
    {
        value = 0;

        if (b is >= (byte) '0' and <= (byte) '9')
        {
            value = b - (byte) '0';
            return true;
        }

        if (b is >= (byte) 'a' and <= (byte) 'f')
        {
            value = b - (byte) 'a' + 10;
            return true;
        }

        if (b is >= (byte) 'A' and <= (byte) 'F')
        {
            value = b - (byte) 'A' + 10;
            return true;
        }

        return false;
    }
}