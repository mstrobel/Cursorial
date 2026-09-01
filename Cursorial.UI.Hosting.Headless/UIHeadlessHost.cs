using System.Diagnostics;
using System.Text;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

using Microsoft.Extensions.Time.Testing;

namespace Cursorial.UI.Hosting.Headless;

/// <summary>
/// The headless test harness (design doc §10.10) — every subsystem's integration substrate and the
/// reason no test ever needs a TTY: a <see cref="SyntheticTerminalHost"/> with scripted
/// capabilities over an in-memory sink, a <see cref="FakeTimeProvider"/> as the single clock
/// domain, and manual frame stepping. <b>The calling thread becomes the UI thread</b> (no
/// ownership transfer); frames run only when stepped. Single-thread-affine — parallel hosts never
/// cross-wire (<see cref="UIApplication.Current"/> is thread-local).
/// </summary>
public sealed class UIHeadlessHost : IAsyncDisposable, IDisposable
{
    private readonly SyntheticTerminalHost _terminal;
    private byte[] _lastFrameBytes = [];
    private bool _disposed;

    private UIHeadlessHost(UIHeadlessHostOptions options, SyntheticTerminalHost terminal, FakeTimeProvider time, UIApplication application)
    {
        Options = options;
        _terminal = terminal;
        Time = time;
        Application = application;
    }

    /// <summary>
    /// Creates and starts a headless application synchronously: no probes, no TTY, no negotiation.
    /// Set <see cref="UIApplication.RootElement"/> (or call <see cref="ShowRoot"/>) and step frames.
    /// </summary>
    public static UIHeadlessHost Create(UIHeadlessHostOptions? options = null)
    {
        options ??= new UIHeadlessHostOptions();
        var time = new FakeTimeProvider();
        var terminal = new SyntheticTerminalHost(options.Capabilities, options.InitialSize, time);

        var builder = UIApplication.CreateBuilder()
                                   .WithService<IGraphemeCache>(new GraphemeCache())
                                   .WithTerminalHost(terminal, disposeWithApp: true)
                                   .WithTimeProvider(time)
                                   .UseAlternateScreen(options.UseAlternateScreen);

        options.ConfigureBuilder?.Invoke(builder);
        var application = builder.Build();
        var host = new UIHeadlessHost(options, terminal, time, application);
        application.StartHeadless();
        terminal.DrainOutput(); // discard the UI-mode entry bytes — LastFrameBytes is per-frame
        return host;
    }

    /// <summary>The application under test.</summary>
    public UIApplication Application { get; }

    /// <summary>The application's dispatcher (owner = the creating thread).</summary>
    public UIDispatcher Dispatcher => Application.Dispatcher;

    /// <summary>The manual clock: animations, gesture thresholds, and parser timing all sample it.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The synthetic terminal host (capability snapshot, raw byte access).</summary>
    public SyntheticTerminalHost Terminal => _terminal;

    /// <summary>
    /// LIVE accessor for the application's current back buffer — the composited screen. The
    /// underlying instance is replaced by renegotiation; do not cache across frames.
    /// </summary>
    public CellBuffer FrameBuffer => Application.FrameBufferInternal
                                     ?? throw new InvalidOperationException("The application has not started.");

    /// <summary>The wire bytes the last <see cref="RunFrame"/> emitted (when <see cref="UIHeadlessHostOptions.CaptureFrameBytes"/>).</summary>
    public ReadOnlyMemory<byte> LastFrameBytes => _lastFrameBytes;

    /// <summary>The canonical-teardown byte sequence, available after <see cref="DisposeAsync"/>.</summary>
    public ReadOnlyMemory<byte> TeardownBytes => _terminal.FinalOutput;

    /// <summary>Encapsulates configuration options for the UI test host.</summary>
    public UIHeadlessHostOptions Options { get; }

    /// <summary>
    /// Sets the root element tree — the P1 stand-in for <c>ShowWindow</c> (S4's window plumbing
    /// replaces it at P7; this member keeps the call shape).
    /// </summary>
    public void ShowRoot(UIElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Application.RootElement = root;
    }

    /// <summary>Runs ONE full frame (phases 0–6), synchronously, on this thread.</summary>
    public void RunFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Application.StepFrame();
        CaptureFrame();
    }

    /// <summary>Runs up to <paramref name="count"/> frames; returns the number actually run (early-exits on shutdown).</summary>
    public int RunFrames(int count)
    {
        var run = 0;
        for (; run < count; run++)
        {
            if (Dispatcher.ShutdownToken.IsCancellationRequested)
                break;

            RunFrame();
        }

        return run;
    }

    /// <summary>
    /// Steps frames until the idle predicate holds — input queue ∧ dispatcher jobs ∧ pending
    /// layout ∧ dirty visuals ∧ styling activations ∧ animations all empty — or returns
    /// <see langword="false"/> when <paramref name="maxFrames"/> is hit first.
    /// </summary>
    public bool RunUntilIdle(int maxFrames = 100)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            if (Application.IsIdle)
                return true;

            RunFrame();
        }

        return Application.IsIdle;
    }

    /// <summary>The default deadline for <see cref="RunUntilCompleted"/> — generous, because only a genuinely hung flow reaches it.</summary>
    public static TimeSpan DefaultCompletionTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long each poll parks on the task's completion signal before pumping another frame.</summary>
    private static readonly TimeSpan CompletionPollInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Steps frames until <paramref name="task"/> completes — the wait for a task that may resume ON
    /// the UI thread — or returns <see langword="false"/> when <paramref name="timeout"/> (default
    /// 5 s) is hit first. Completion means <em>terminal state</em>: a faulted or canceled task
    /// returns <see langword="true"/> with its outcome left on the task for the caller to observe.
    /// <para>
    /// <see cref="Create"/> makes the calling thread the UI thread, and a frame installs the
    /// application's <c>SynchronizationContext</c> — so an <c>await</c> reached inside a frame
    /// captures it and posts its resumption back to the dispatcher, where only a later frame can run
    /// it. Work that has meanwhile left for the thread pool is invisible to <c>UIApplication.IsIdle</c>
    /// (it inspects the dispatcher-side queues only), so <see cref="RunUntilIdle"/> can legitimately
    /// report idle in the window before the pool thread posts. A caller that then BLOCKS this thread
    /// on the task — <c>task.Wait(…)</c> — guarantees the queued resumption can never run: a hard
    /// deadlock that ends at the timeout instead of at completion. Pumping while polling closes that
    /// window.
    /// </para>
    /// <para>
    /// <b>Bounded by TIME, not by frames</b>, alone among this type's <c>RunUntil…</c> methods — and
    /// deliberately so: the work being waited on has left the dispatcher, and no number of frames
    /// makes a pool thread post its resumption any sooner. A frame bound would express a budget for
    /// something the frames do not drive.
    /// </para>
    /// <para>
    /// The poll comes BEFORE the pump: a task whose tail never touches the UI thread completes on the
    /// first short poll, so the common case pumps no extra frames at all and no frame-sensitive
    /// assertion moves.
    /// </para>
    /// </summary>
    public bool RunUntilCompleted(Task task, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(task);

        var limit = timeout ?? DefaultCompletionTimeout;
        var clock = Stopwatch.StartNew();
        var pending = new[] { task };

        // WaitAny (not Wait) parks on the completion signal without propagating the task's outcome:
        // a fault belongs to the caller's own await/Result, not to the wait that observed it.
        while (Task.WaitAny(pending, CompletionPollInterval) < 0)
        {
            if (clock.Elapsed >= limit)
                return false;

            RunFrame();
        }

        return true;
    }

    /// <summary>
    /// Advances <see cref="Time"/> in <see cref="UIHeadlessHostOptions.FrameInterval"/> steps with one
    /// <see cref="RunFrame"/> per step; a non-multiple remainder is applied as one final partial
    /// step with a closing frame.
    /// </summary>
    public void AdvanceTime(TimeSpan delta)
    {
        var step = Options.FrameInterval;
        while (delta > TimeSpan.Zero)
        {
            var advance = delta < step ? delta : step;
            Time.Advance(advance);
            delta -= advance;
            RunFrame();
        }
    }

    // ───────────────────────────── deterministic input ─────────────────────────────

    /// <summary>
    /// Enqueues <paramref name="inputEvent"/> DIRECTLY into the frame loop's queue — no pump hop,
    /// no race. A default <c>Timestamp</c> is replaced with <see cref="Time"/>'s now for the
    /// common event types.
    /// </summary>
    public void SendInput(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        if (inputEvent.Timestamp == default)
        {
            var now = Time.GetUtcNow();
            inputEvent = inputEvent switch
            {
                KeyEvent k => k with { Timestamp = now },
                MouseEvent m => m with { Timestamp = now },
                ResizeEvent r => r with { Timestamp = now },
                FocusEvent f => f with { Timestamp = now },
                PasteEvent p => p with { Timestamp = now },
                _ => inputEvent
            };
        }

        Application.EnqueueInput(inputEvent);
    }

    /// <summary>Sends a key-down (and optionally the matching key-up) stamped on the fake clock.</summary>
    public void SendKey(Key key, KeyModifiers modifiers = default, string? text = null, bool withRelease = false)
    {
        SendInput(new KeyEvent
        {
            Key = key,
            Modifiers = modifiers,
            Kind = KeyEventKind.Down,
            Text = (text ?? string.Empty).AsMemory(),
            Timestamp = Time.GetUtcNow()
        });

        if (withRelease)
        {
            SendInput(new KeyEvent
            {
                Key = key,
                Modifiers = modifiers,
                Kind = KeyEventKind.Up,
                Text = (text ?? string.Empty).AsMemory(),
                Timestamp = Time.GetUtcNow()
            });
        }
    }

    /// <summary>Sends one character-key event per rune of <paramref name="text"/>.</summary>
    public void SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
            SendKey(Key.Character, text: rune.ToString());
    }

    /// <summary>Sends a mouse-move at the cell position (optionally with buttons held / modifiers — e.g. during a drag).</summary>
    public void SendMouseMove(int column, int row, MouseButtons buttonsHeld = MouseButtons.None, KeyModifiers modifiers = default)
        => SendInput(new MouseEvent
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(column, row),
            Button = MouseButton.None,
            ButtonsHeld = buttonsHeld,
            Modifiers = modifiers,
            Timestamp = Time.GetUtcNow()
        });

    /// <summary>Sends a mouse button-down (press) at the cell — the start of a drag; pair with <see cref="SendMouseUp"/>.</summary>
    public void SendMouseDown(int column, int row, MouseButton button = MouseButton.Left, KeyModifiers modifiers = default, int clickCount = 1)
        => SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(column, row),
            Button = button,
            ButtonsHeld = MouseButtons.None,
            Modifiers = modifiers,
            ClickCount = clickCount,
            Timestamp = Time.GetUtcNow()
        });

    /// <summary>Sends a mouse button-up (release) at the cell — the end of a drag.</summary>
    public void SendMouseUp(int column, int row, MouseButton button = MouseButton.Left, KeyModifiers modifiers = default)
        => SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp,
            Position = new CellPosition(column, row),
            Button = button,
            ButtonsHeld = MouseButtons.None,
            Modifiers = modifiers,
            Timestamp = Time.GetUtcNow()
        });

    /// <summary>
    /// Sends a press/release pair at the cell, with <paramref name="clickCount"/> riding the
    /// button-down (the S3 contract's <see cref="ClickCountTarget.ButtonDown"/> default).
    /// </summary>
    public void SendClick(int column, int row, MouseButton button = MouseButton.Left, int clickCount = 1, KeyModifiers modifiers = default)
    {
        var position = new CellPosition(column, row);

        SendInput(new MouseEvent
                  {
                      Kind = MouseEventKind.ButtonDown,
                      Position = position,
                      Button = button,
                      ButtonsHeld = MouseButtons.None,
                      Modifiers = modifiers,
                      ClickCount = clickCount,
                      Timestamp = Time.GetUtcNow()
                  });

        SendInput(new MouseEvent
                  {
                      Kind = MouseEventKind.ButtonUp,
                      Position = position,
                      Button = button,
                      ButtonsHeld = MouseButtons.None,
                      Modifiers = modifiers,
                      Timestamp = Time.GetUtcNow()
                  });
    }

    /// <summary>Sends a resize event (coalesced last-wins inside one frame, like production).</summary>
    public void SendResize(int columns, int rows)
        => SendInput(new ResizeEvent { Columns = columns, Rows = rows, Timestamp = Time.GetUtcNow() });

    /// <summary>
    /// The parser-inclusive path: raw wire bytes through the host's REAL <c>VtInputDevice</c>,
    /// constructed on <see cref="Time"/> — bare-ESC ambiguity and multi-click thresholds live on
    /// the fake clock. A trailing lone ESC commits only after <see cref="AdvanceTime"/> crosses
    /// the ambiguity window; await <see cref="DrainParsedInputAsync"/> before stepping the frame
    /// that should observe the events.
    /// </summary>
    public void SendBytes(ReadOnlySpan<byte> rawBytes) => _terminal.WriteInputBytes(rawBytes);

    /// <summary>
    /// Awaits pump catch-up for <see cref="SendBytes"/>: first the deterministic leg (the parser
    /// consumed every injected byte — events are in the device channel synchronously at that
    /// point), then a short quiet window for the channel → loop-queue hop. Wall-clock bounded by
    /// <paramref name="timeout"/> (default 5 s).
    /// </summary>
    public async Task DrainParsedInputAsync(TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (_terminal.InputBytesConsumed < _terminal.InputBytesWritten)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("The input parser did not consume the injected bytes in time.");

            await Task.Delay(1).ConfigureAwait(false);
        }

        // The channel → ConcurrentQueue hop is thread-pool-side; give it a settle window.
        for (var quiet = 0; quiet < 3; quiet++)
            await Task.Delay(1).ConfigureAwait(false);
    }

    // ───────────────────────────── assertions ─────────────────────────────

    /// <summary>The composited text of one row (wide-cell continuations skipped, blanks as spaces).</summary>
    public string GetRowText(int row)
    {
        var buffer = FrameBuffer;
        var text = new StringBuilder(buffer.Columns);
        for (var column = 0; column < buffer.Columns; column++)
        {
            var cell = buffer[column, row];
            if (cell.Kind == CellKind.WideContinuation)
                continue;

            text.Append(string.IsNullOrWhiteSpace(cell.Grapheme) ? " " : cell.Grapheme);
        }

        return text.ToString();
    }

    /// <summary>One composited cell.</summary>
    public Cell GetCell(int column, int row) => FrameBuffer[column, row];

    /// <summary>
    /// Full canonical teardown into the captured sink: the post-dispose drain of
    /// <see cref="Terminal"/>.<see cref="SyntheticTerminalHost.DrainOutput"/> returns the
    /// teardown byte sequence (renderer close, show cursor, SGR reset, leave alt screen).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Application.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Synchronous disposal sugar (the headless teardown path has no real awaits).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void CaptureFrame()
    {
        // Drain the output pipe each frame to bound its growth. Only COPY the bytes out (an allocation) when the
        // caller asked to capture them — otherwise discard non-allocatingly, so a benchmark's measured per-frame
        // allocation reflects the framework, not the harness's ToArray (CaptureFrameBytes is off by default).
        if (Options.CaptureFrameBytes)
            _lastFrameBytes = _terminal.DrainOutput();
        else
            _terminal.DiscardOutput();
    }

    public Window NewWindow(string? title = null, object? content = null, Window? owner = null, 
                            int left = 0, int top = 0, int? width = null, int? height = null, IBrush? background = null,
                            WindowStartupLocation windowStartupLocation = default, WindowStyle windowStyle = default,
                            double? opacity = null)
    {
        var window = new Window
                     {
                         Width = width,
                         Height = height 
                     };

        if (title is {} t)
            window.Title = t;

        if (content is {} c)
            window.Content = c;

        if (owner is {} o)
            window.Owner = o;

        if (background is {} b)
            window.Background = b;

        if (windowStartupLocation != default)
            window.WindowStartupLocation = windowStartupLocation;
        
        if (windowStyle != default)
            window.WindowStyle = windowStyle;
        
        if (width is {} w)
            window.Width = w;

        if (height is {} h)
            window.Height = h;

        if (left != 0)
            window.Left = left;
        
        if (top != 0)
            window.Top = top;
        
        if (opacity is {} p)
            window.Opacity = p;

        if (Options.DisableInactiveWindowTransitions)
            Transition.SetTransitions(window, null);

        return window;
    }
}
