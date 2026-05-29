using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

using Cursorial.Input;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Windows <see cref="IInputByteSource"/> that reads <c>INPUT_RECORD</c>s from a console input
/// handle via <c>ReadConsoleInputW</c> and translates them locally to the VT byte sequences
/// the rest of the framework's parser expects (Win32 Input Mode <c>CSI Vk;Sc;Uc;Kd;Cs;Rc_</c>
/// for keyboard events, SGR mouse for mouse events, <c>CSI I</c> / <c>CSI O</c> for focus
/// events). This is the same pattern crossterm, Terminal.Gui, and Consolonia all use; it
/// deliberately bypasses the conhost VT byte-stream view that
/// <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> would otherwise populate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>ReadFile + ENABLE_VIRTUAL_TERMINAL_INPUT</c>.</b> The conhost-translated VT
/// byte stream lives in a buffer adjacent to (but not the same as) the input-record queue.
/// <c>FlushConsoleInputBuffer</c> drains the record queue but doesn't fully drain the
/// translated byte view — leftover bytes (e.g., a partly consumed Win32 Input Mode sequence
/// from the last key the user pressed during a session) can survive into cooked mode, where
/// the orphan ESC + CSI parameters confuse <see cref="Console.ReadLine"/>'s reader into
/// consuming the user's first post-session keystroke. That's the "first keystroke after
/// exit is swallowed" symptom — and it's fundamental to the ReadFile-on-VT-input pattern.
/// </para>
/// <para>
/// <b>What changes when we read INPUT_RECORDs instead.</b> Conhost never creates a VT
/// byte-stream view (we don't set <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c>), so there's no
/// orphan buffer to leak. Disposal flushes the record queue with
/// <c>FlushConsoleInputBuffer</c> and that's the entire input state — fully drained, fully
/// clean for the next reader. The keystroke-eating bug can't happen because the storage
/// that would have held the orphan bytes never exists in the first place.
/// </para>
/// <para>
/// <b>The cancellation story is simpler too.</b> The pump blocks in
/// <c>WaitForMultipleObjects([stdinHandle, cancelEvent])</c>; on wake, it calls
/// <c>GetNumberOfConsoleInputEvents</c> to see if there's actually a record (handles spurious
/// wakes), then <c>ReadConsoleInputW</c> to consume one. Both reads are non-blocking once
/// we've confirmed events exist, so the pump can never get stuck inside a blocking syscall —
/// <c>SetEvent</c> on the cancel handle reliably wakes it. No <c>CancelIoEx</c> needed.
/// </para>
/// <para>
/// <b>Handle ownership.</b> The supplied stdin handle is not closed at disposal — it's the
/// process-global value returned by <c>GetStdHandle(STD_INPUT_HANDLE)</c>, and we mustn't
/// close it. The auto-reset cancel event handle IS owned and closed.
/// </para>
/// <para>
/// <b>BYO transports.</b> Callers using <c>TerminalSession.OpenAsync(source, sink)</c> with
/// a hand-built <c>StreamInputByteSource</c> over <see cref="Console.OpenStandardInput()"/>
/// inherit the legacy zombie-read behavior — the caller chose that transport. This class is
/// only constructed by the happy-path <c>TerminalSession.OpenAsync()</c> on Windows when
/// stdin is a real console handle.
/// </para>
/// </remarks>
// ReSharper disable once RedundantExtendsListEntry
internal sealed partial class WindowsConsoleInputByteSource : IInputByteSource, IPausableInputByteSource
{
    // ReSharper disable UnusedMember.Local

    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_OBJECT_1 = WAIT_OBJECT_0 + 1;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    // INPUT_RECORD event types.
    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;
    private const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;
    private const ushort MENU_EVENT = 0x0008;
    private const ushort FOCUS_EVENT = 0x0010;

    // MOUSE_EVENT_RECORD.dwEventFlags.
    private const uint MOUSE_MOVED = 0x0001;
    private const uint DOUBLE_CLICK = 0x0002;
    private const uint MOUSE_WHEELED = 0x0004;
    private const uint MOUSE_HWHEELED = 0x0008;

    // Read batch size — one syscall can drain up to this many records.
    private const int ReadBufferRecords = 32;

    // ReSharper restore UnusedMember.Local

    private readonly IntPtr _stdinHandle;
    private readonly IntPtr _cancelEvent;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;
    private readonly ManualResetEventSlim _runEvent = new(initialState: true);
    private readonly bool _passThroughVtBytes;

    private readonly object _stateLock = new();
    private int _pauseRefCount;
    private TaskCompletionSource? _pauseCompleted;

    // Mouse state — held across events because SGR mouse sequences encode the *currently
    // pressed* button, and Windows' MOUSE_EVENT_RECORD doesn't tell us which button changed
    // state; we have to diff against the previous button-state mask.
    private uint _prevMouseButtonState;

    private int _disposed;

    /// <param name="stdinHandle">Standard input console handle from <c>GetStdHandle</c>.</param>
    /// <param name="passThroughVtBytes">
    /// When true, <c>KEY_EVENT</c> records that carry an ASCII <c>UnicodeChar</c> with no
    /// <c>VirtualKeyCode</c> set (the byte-per-record shape ConPTY uses for VT sequences it
    /// can't classify as a key — DA1 / XTVERSION / OSC color responses, Kitty keyboard
    /// reports from third-party emulators, etc.) are emitted as the raw byte instead of
    /// being wrapped in a Win32 Input Mode envelope. This is the path third-party emulators
    /// running atop ConPTY (Alacritty, WezTerm, Ghostty on Windows, MinTTY, …) take so the
    /// negotiator's classifier sees their device responses verbatim. The default of false
    /// preserves the lossless conhost / Windows Terminal behavior — under those families
    /// every key, even one with <c>vk=0</c> (notably IME composition results), gets the
    /// Win32 Input Mode envelope.
    /// </param>
    public WindowsConsoleInputByteSource(IntPtr stdinHandle, bool passThroughVtBytes = false)
    {
        _stdinHandle = stdinHandle;
        _passThroughVtBytes = passThroughVtBytes;

        // Auto-reset event. Initial state: non-signaled. SetEvent makes the next
        // WaitForMultipleObjects on this handle return; the wait itself implicitly resets the
        // event back to non-signaled, so subsequent waits block again until the next SetEvent.
        _cancelEvent = CreateEvent(IntPtr.Zero, bManualReset: false, bInitialState: false, lpName: null);

        if (_cancelEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateEvent failed for the input-pump wakeup event (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _pipe = new Pipe();
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <inheritdoc/>
    public PipeReader Reader => _pipe.Reader;

    /// <summary>
    /// Whether the translator emits raw ASCII bytes for ConPTY-passed-through VT sequences in
    /// addition to the Win32 Input Mode envelopes it produces for real key events. See the
    /// <c>passThroughVtBytes</c> constructor parameter.
    /// </summary>
    public bool PassThroughVtBytes => _passThroughVtBytes;

    private async Task PumpAsync()
    {
        Exception? completion = null;
        try
        {
            var handles = new[] { _stdinHandle, _cancelEvent };
            var records = new INPUT_RECORD[ReadBufferRecords];
            var emit = new ArrayBufferWriter<byte>(initialCapacity: 256);

            while (true)
            {
                // Pause check (runs unconditionally at the top of each iteration).
                bool shouldPark;
                TaskCompletionSource? pauseTcs = null;
                lock (_stateLock)
                {
                    shouldPark = _pauseRefCount > 0;
                    if (shouldPark) pauseTcs = _pauseCompleted;
                }

                if (shouldPark)
                {
                    pauseTcs?.TrySetResult();
                    _runEvent.Wait();
                    if (Volatile.Read(ref _disposed) != 0) break;
                    continue;
                }

                uint waitResult = WaitForMultipleObjects(2, ref handles[0], false, INFINITE);

                if (Volatile.Read(ref _disposed) != 0) break;

                if (waitResult == WAIT_FAILED)
                {
                    int err = Marshal.GetLastWin32Error();
                    completion = new IOException($"WaitForMultipleObjects failed on stdin (Win32 error {err}).");
                    break;
                }

                if (waitResult == WAIT_OBJECT_1)
                {
                    // Cancel/wakeup event signaled — top-of-loop state checks handle it.
                    continue;
                }

                if (waitResult != WAIT_OBJECT_0)
                {
                    completion = new IOException($"WaitForMultipleObjects returned unexpected value 0x{waitResult:X8}.");
                    break;
                }

                // stdin handle signaled. Check how many records are available before reading —
                // some signaling cases don't produce a readable record (rare, but the docs
                // don't promise otherwise). If the count is 0, it was a spurious wake; loop
                // back to wait without calling ReadConsoleInput (which would otherwise block
                // until a record actually arrives, defeating the WFMO cancellability).
                if (!GetNumberOfConsoleInputEvents(_stdinHandle, out uint available))
                {
                    int err = Marshal.GetLastWin32Error();
                    completion = new IOException($"GetNumberOfConsoleInputEvents failed (Win32 error {err}).");
                    break;
                }

                if (available == 0) continue;

                uint toRead = Math.Min(available, (uint) records.Length);
                if (!ReadConsoleInputW(_stdinHandle, records, toRead, out uint actuallyRead))
                {
                    int err = Marshal.GetLastWin32Error();
                    completion = new IOException($"ReadConsoleInputW failed (Win32 error {err}).");
                    break;
                }

                if (actuallyRead == 0) continue;

                emit.ResetWrittenCount();
                for (uint i = 0; i < actuallyRead; i++)
                    TranslateRecord(in records[i], emit);

                if (emit.WrittenCount > 0)
                {
                    var dst = _pipe.Writer.GetSpan(emit.WrittenCount);
                    emit.WrittenSpan.CopyTo(dst);
                    _pipe.Writer.Advance(emit.WrittenCount);

                    var flush = await _pipe.Writer.FlushAsync().ConfigureAwait(false);
                    if (flush.IsCompleted || flush.IsCanceled) break;
                }
            }
        }
        catch (Exception ex)
        {
            completion = ex;
        }
        finally
        {
            // @formatter:off
            try { await _pipe.Writer.CompleteAsync(completion).ConfigureAwait(false); }
            catch { /* best-effort */ }
            // @formatter:on
        }
    }

    // ---- Record → VT translation ----

    private void TranslateRecord(in INPUT_RECORD record, ArrayBufferWriter<byte> output)
    {
        switch (record.EventType)
        {
            case KEY_EVENT:
                EmitKeyEvent(in record.KeyEvent, output);
                break;
            case MOUSE_EVENT:
                EmitMouseEvent(in record.MouseEvent, output);
                break;
            case FOCUS_EVENT:
                EmitFocusEvent(in record.FocusEvent, output);
                break;
            // WINDOW_BUFFER_SIZE_EVENT is handled by the separate resize monitor.
            // MENU_EVENT is never useful to forward.
        }
    }

    /// <summary>
    /// Emit a Win32 Input Mode key event sequence: <c>CSI Vk;Sc;Uc;Kd;Cs;Rc _</c>. The
    /// framework's <c>VtInputInterpreter</c> already decodes this format end-to-end (it's the
    /// same sequence the terminal would emit if Win32 Input Mode were enabled via DECSET 9001),
    /// so the byte stream this source produces is indistinguishable from a Win32-Input-Mode
    /// terminal's native output.
    /// </summary>
    /// <remarks>
    /// When <see cref="PassThroughVtBytes"/> is set and the record carries an ASCII
    /// <c>UnicodeChar</c> with <c>VirtualKeyCode == 0</c> on a key-down (the byte-per-record
    /// shape ConPTY produces for VT bytes it can't classify as a key — DA1 / XTVERSION /
    /// Kitty keyboard / etc.), the raw ASCII byte is emitted instead, repeated
    /// <c>RepeatCount</c> times. Key-up events for the same shape are dropped to avoid
    /// echoing each VT byte twice. Anything else (real keys, including IME composition with
    /// <c>vk=0</c> and non-ASCII <c>uc</c>) still rides the Win32 Input Mode envelope so the
    /// interpreter can route through its full key-translation path.
    /// </remarks>
    private void EmitKeyEvent(in KEY_EVENT_RECORD evt, ArrayBufferWriter<byte> output)
    {
        // ASCII byte-per-record from ConPTY → emit verbatim, skip the envelope. ASCII-only
        // because IME composition can drop a single non-ASCII char as a vk=0 KEY_EVENT and
        // the consumer expects to see Win32 Input Mode for it (text routing depends on the
        // envelope's KeyDown / repeat / modifier fields).
        if (_passThroughVtBytes &&
            evt.VirtualKeyCode == 0 &&
            evt.UnicodeChar is > 0 and < 0x80)
        {
            if (evt.KeyDown == 0) return;

            ushort copies = evt.RepeatCount;
            if (copies == 0) copies = 1;

            var dst = output.GetSpan(copies);
            dst[..copies].Fill((byte) evt.UnicodeChar);
            output.Advance(copies);
            return;
        }

        // The framework will repeat events per RepeatCount; emit one sequence with the actual
        // repeat count rather than RepeatCount copies of a Rc=1 sequence.
        ushort repeats = evt.RepeatCount;
        if (repeats == 0) repeats = 1;

        Span<byte> scratch = stackalloc byte[64];
        int written = 0;

        // ESC [
        scratch[written++] = 0x1b;
        scratch[written++] = (byte) '[';

        AppendUInt(evt.VirtualKeyCode, scratch, ref written);
        scratch[written++] = (byte) ';';
        AppendUInt(evt.VirtualScanCode, scratch, ref written);
        scratch[written++] = (byte) ';';
        AppendUInt(evt.UnicodeChar, scratch, ref written);
        scratch[written++] = (byte) ';';
        scratch[written++] = (byte) (evt.KeyDown != 0 ? '1' : '0');
        scratch[written++] = (byte) ';';
        AppendUInt(evt.ControlKeyState, scratch, ref written);
        scratch[written++] = (byte) ';';
        AppendUInt(repeats, scratch, ref written);
        scratch[written++] = (byte) '_';

        var dst2 = output.GetSpan(written);
        scratch[..written].CopyTo(dst2);
        output.Advance(written);
    }

    /// <summary>
    /// Translate a Windows mouse event to an SGR mouse sequence (<c>CSI &lt; b;x;y M|m</c>),
    /// the encoding the rest of the framework already speaks. The interpreter side accepts
    /// SGR coordinates as 1-based; we provide them that way.
    /// </summary>
    private void EmitMouseEvent(in MOUSE_EVENT_RECORD evt, ArrayBufferWriter<byte> output)
    {
        uint flags = evt.EventFlags;
        uint buttonState = evt.ButtonState;
        uint changed = buttonState ^ _prevMouseButtonState;
        _prevMouseButtonState = buttonState;

        // SGR mouse coordinates are 1-based; Windows reports 0-based.
        int x = evt.MousePositionX + 1;
        int y = evt.MousePositionY + 1;

        // The "b" parameter encodes button index + motion / wheel flags + modifiers.
        // See the xterm SGR mouse specification for the bit layout we reuse.
        int modifiers = 0;
        const uint shiftPressed = 0x0010;
        const uint leftAltPressed = 0x0002;
        const uint rightAltPressed = 0x0001;
        const uint leftCtrlPressed = 0x0008;
        const uint rightCtrlPressed = 0x0004;

        if ((evt.ControlKeyState & shiftPressed) != 0) modifiers |= 4;
        if ((evt.ControlKeyState & (leftAltPressed | rightAltPressed)) != 0) modifiers |= 8;
        if ((evt.ControlKeyState & (leftCtrlPressed | rightCtrlPressed)) != 0) modifiers |= 16;

        if ((flags & MOUSE_WHEELED) != 0)
        {
            // High-word of dwButtonState is the wheel delta, signed.
            int delta = unchecked((int) buttonState) >> 16;
            int wheelButton = delta > 0 ? 64 : 65;
            EmitSgrMouse(output, wheelButton | modifiers, x, y, press: true);
            return;
        }

        if ((flags & MOUSE_HWHEELED) != 0)
        {
            int delta = unchecked((int) buttonState) >> 16;
            int wheelButton = delta > 0 ? 66 : 67;
            EmitSgrMouse(output, wheelButton | modifiers, x, y, press: true);
            return;
        }

        if ((flags & MOUSE_MOVED) != 0)
        {
            // Motion event. Encode as button + 32 (motion bit). Pick the lowest currently-
            // pressed button, or 3 (no button) if none are down.
            int button = LowestButtonIndex(buttonState);
            EmitSgrMouse(output, button | 32 | modifiers, x, y, press: true);
            return;
        }

        // Button press / release. "changed" tells us which button(s) flipped; we typically
        // only get one change per event.
        if (changed != 0)
        {
            int button = LowestButtonIndex(changed);
            bool pressed = (buttonState & changed) != 0;
            EmitSgrMouse(output, button | modifiers, x, y, pressed);
            return;
        }

        // Double-click without state change: emit as a press for the lowest down button.
        if ((flags & DOUBLE_CLICK) != 0 && buttonState != 0)
        {
            int button = LowestButtonIndex(buttonState);
            EmitSgrMouse(output, button | modifiers, x, y, press: true);
        }
    }

    private static int LowestButtonIndex(uint buttonState)
    {
        // Windows FROM_LEFT_*_BUTTON_PRESSED layout maps to xterm SGR button numbers:
        //   bit 0 → left   (xterm 0)
        //   bit 1 → right  (xterm 2)
        //   bit 2 → middle (xterm 1)
        //   bit 3 → X1     (xterm 128)
        //   bit 4 → X2     (xterm 129)
        if ((buttonState & 0x0001) != 0) return 0;
        if ((buttonState & 0x0004) != 0) return 1;
        if ((buttonState & 0x0002) != 0) return 2;
        if ((buttonState & 0x0008) != 0) return 128;
        if ((buttonState & 0x0010) != 0) return 129;
        return 3; // no button
    }

    private static void EmitSgrMouse(ArrayBufferWriter<byte> output, int button, int x, int y, bool press)
    {
        Span<byte> scratch = stackalloc byte[48];
        int written = 0;
        scratch[written++] = 0x1b;
        scratch[written++] = (byte) '[';
        scratch[written++] = (byte) '<';
        AppendInt(button, scratch, ref written);
        scratch[written++] = (byte) ';';
        AppendInt(x, scratch, ref written);
        scratch[written++] = (byte) ';';
        AppendInt(y, scratch, ref written);
        scratch[written++] = (byte) (press ? 'M' : 'm');

        var dst = output.GetSpan(written);
        scratch[..written].CopyTo(dst);
        output.Advance(written);
    }

    private static void EmitFocusEvent(in FOCUS_EVENT_RECORD evt, ArrayBufferWriter<byte> output)
    {
        // CSI I (focus gained) / CSI O (focus lost).
        var dst = output.GetSpan(3);
        dst[0] = 0x1b;
        dst[1] = (byte) '[';
        dst[2] = (byte) (evt.SetFocus != 0 ? 'I' : 'O');
        output.Advance(3);
    }

    // ---- Decimal-ASCII helpers ----

    private static void AppendUInt(uint value, Span<byte> dest, ref int written)
    {
        if (value == 0)
        {
            dest[written++] = (byte) '0';
            return;
        }

        Span<byte> tmp = stackalloc byte[10];
        int len = 0;
        while (value > 0)
        {
            tmp[len++] = (byte) ('0' + (value % 10));
            value /= 10;
        }
        for (int i = len - 1; i >= 0; i--)
            dest[written++] = tmp[i];
    }

    private static void AppendInt(int value, Span<byte> dest, ref int written)
    {
        if (value < 0)
        {
            dest[written++] = (byte) '-';
            value = -value;
        }
        AppendUInt((uint) value, dest, ref written);
    }

    // ---- Pause / resume ----

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> PauseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsConsoleInputByteSource));

        cancellationToken.ThrowIfCancellationRequested();

        Task? completion;
        bool wakeupNeeded;
        lock (_stateLock)
        {
            _pauseRefCount++;

            if (_pauseRefCount == 1)
            {
                _runEvent.Reset();
                _pauseCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = _pauseCompleted.Task;
                wakeupNeeded = true;
            }
            else if (_pauseCompleted is { Task.IsCompleted: false } pending)
            {
                completion = pending.Task;
                wakeupNeeded = false;
            }
            else
            {
                completion = null;
                wakeupNeeded = false;
            }
        }

        if (wakeupNeeded) SetEvent(_cancelEvent);

        try
        {
            if (completion is not null)
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DecrementPause();
            throw;
        }

        return new PauseScope(this);
    }

    private void DecrementPause()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        bool wake;
        lock (_stateLock)
        {
            if (_pauseRefCount == 0) return;
            _pauseRefCount--;
            wake = _pauseRefCount == 0;
            if (wake) _pauseCompleted = null;
        }

        if (wake) _runEvent.Set();
    }

    private sealed class PauseScope : IAsyncDisposable
    {
        private WindowsConsoleInputByteSource? _source;
        public PauseScope(WindowsConsoleInputByteSource source) => _source = source;
        public ValueTask DisposeAsync()
        {
            var source = Interlocked.Exchange(ref _source, null);
            source?.DecrementPause();
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // @formatter:off
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        SetEvent(_cancelEvent);
        _runEvent.Set();

        TaskCompletionSource? pendingPause;
        lock (_stateLock) { pendingPause = _pauseCompleted; _pauseCompleted = null; }
        pendingPause?.TrySetException(new ObjectDisposedException(nameof(WindowsConsoleInputByteSource)));

        try { await _pumpTask.ConfigureAwait(false); }
        catch { /* best-effort */ }

        _runEvent.Dispose();

        // Close the cancel event we created. Do NOT close _stdinHandle — it's process-global.
        if (_cancelEvent != IntPtr.Zero)
            CloseHandle(_cancelEvent);
        // @formatter:on
    }

    // ---- INPUT_RECORD layout ----

    // ReSharper disable InconsistentNaming
    
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        // 2 bytes of padding follow EventType so the union begins on a 4-byte boundary.
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        [FieldOffset(4)] public FOCUS_EVENT_RECORD FocusEvent;
        // WINDOW_BUFFER_SIZE_EVENT / MENU_EVENT records are smaller; their fields overlay the
        // start of the union, but we don't translate them, so we don't model the structs here.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int KeyDown;                    // BOOL
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;             // union with AsciiChar — we treat as WCHAR
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public short MousePositionX;
        public short MousePositionY;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FOCUS_EVENT_RECORD
    {
        public int SetFocus;                   // BOOL
    }

    // ReSharper restore InconsistentNaming
    
    // ---- P/Invokes ----

    // ReSharper disable UnusedMethodReturnValue.Local

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForMultipleObjects(
        uint nCount,
        ref IntPtr lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadConsoleInputW(
        IntPtr hConsoleInput,
        [Out] INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    // ReSharper restore UnusedMethodReturnValue.Local
}
