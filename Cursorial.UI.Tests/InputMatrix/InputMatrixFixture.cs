using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>
/// The input-matrix §0.1 <c>Probe</c>: a rigid leaf/container recording every <c>On*</c> virtual
/// invocation into a shared ordered log as <c>{Name}.{Virtual}</c>.
/// </summary>
public class Probe : UIElement
{
    public Probe(string name, List<string> log)
    {
        LogName = name;
        Log = log;
    }

    public Probe(string name, List<string> log, int columns, int rows)
        : this(name, log)
        => Natural = new Size(columns, rows);

    public string LogName { get; }

    public List<string> Log { get; }

    /// <summary>Rigid natural size (the matrix's <c>Probe(w×h)</c>).</summary>
    public Size Natural { get; set; } = new(1, 1);

    /// <summary>Counts <see cref="UIElement.HitTestCore"/> invocations (the matrix's counting probe — N59/N80).</summary>
    public int HitTestCoreCalls { get; set; }

    /// <summary>Replaces the <c>HitTestCore</c> verdict when set (N59's reject probe); default accepts.</summary>
    public Func<int, int, bool>? HitTestCoreOverride { get; set; }

    public void AddChild(UIElement child) => AddVisualChild(child);

    public void RemoveChild(UIElement child) => RemoveVisualChild(child);

    public void AddLogical(UIElement child) => AddLogicalChild(child);

    /// <summary>Exposes the protected <see cref="UIElement.RentEvent{TArgs}"/> for pooling rows.</summary>
    public TArgs Rent<TArgs>(RoutedEvent<TArgs> routedEvent)
        where TArgs : RoutedEventArgs, new()
        => RentEvent(routedEvent);

    /// <summary>Exposes the protected <c>SetInteractionState</c> (the control-author seam — §10 rows).</summary>
    public void SetState(InteractionState state, bool active) => SetInteractionState(state, active);

    /// <summary>Exposes the protected <c>BeginInteractionUpdate</c> (the ND11 batching scope).</summary>
    public InteractionUpdateScope BeginUpdate() => BeginInteractionUpdate();

    /// <summary>Adds a logging instance handler recording <paramref name="label"/> (plus an optional action).</summary>
    public void AddLog<TArgs>(RoutedEvent<TArgs> routedEvent, string label, bool handledEventsToo = false, Action<TArgs>? action = null)
        where TArgs : RoutedEventArgs
        => AddHandler(
            routedEvent,
            (_, e) =>
            {
                Log.Add(label);
                action?.Invoke(e);
            },
            handledEventsToo);

    protected override Size MeasureOverride(Size availableSize) => Natural;

    protected override bool HitTestCore(int column, int row)
    {
        HitTestCoreCalls++;
        return HitTestCoreOverride?.Invoke(column, row) ?? true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e) => Log.Add($"{LogName}.OnPreviewKeyDown");

    protected override void OnKeyDown(KeyEventArgs e) => Log.Add($"{LogName}.OnKeyDown");

    protected override void OnPreviewKeyUp(KeyEventArgs e) => Log.Add($"{LogName}.OnPreviewKeyUp");

    protected override void OnKeyUp(KeyEventArgs e) => Log.Add($"{LogName}.OnKeyUp");

    protected override void OnPreviewTextInput(TextInputEventArgs e) => Log.Add($"{LogName}.OnPreviewTextInput");

    protected override void OnTextInput(TextInputEventArgs e) => Log.Add($"{LogName}.OnTextInput");

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e) => Log.Add($"{LogName}.OnPreviewMouseDown");

    protected override void OnMouseDown(MouseButtonEventArgs e) => Log.Add($"{LogName}.OnMouseDown");

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e) => Log.Add($"{LogName}.OnPreviewMouseUp");

    protected override void OnMouseUp(MouseButtonEventArgs e) => Log.Add($"{LogName}.OnMouseUp");

    protected override void OnPreviewMouseMove(MouseEventArgs e) => Log.Add($"{LogName}.OnPreviewMouseMove");

    protected override void OnMouseMove(MouseEventArgs e) => Log.Add($"{LogName}.OnMouseMove");

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e) => Log.Add($"{LogName}.OnPreviewMouseWheel");

    protected override void OnMouseWheel(MouseWheelEventArgs e) => Log.Add($"{LogName}.OnMouseWheel");

    protected override void OnMouseEnter(MouseEventArgs e) => Log.Add($"{LogName}.OnMouseEnter");

    protected override void OnMouseLeave(MouseEventArgs e) => Log.Add($"{LogName}.OnMouseLeave");

    protected override void OnGotFocus(FocusChangedEventArgs e) => Log.Add($"{LogName}.OnGotFocus");

    protected override void OnLostFocus(FocusChangedEventArgs e) => Log.Add($"{LogName}.OnLostFocus");

    protected override void OnLostMouseCapture(RoutedEventArgs e) => Log.Add($"{LogName}.OnLostMouseCapture");
}

/// <summary>The matrix's <c>Btn</c>: a <see cref="Probe"/> with <c>Focusable</c> set as a local value.</summary>
public class Btn : Probe
{
    public Btn(string name, List<string> log)
        : base(name, log)
        => Focusable = true;
}

/// <summary>
/// The §12 access-key target: a focusable <see cref="Probe"/> implementing
/// <see cref="IAccessKeyTarget"/>, recording <c>OnAccessKey</c> args (recording only — the manager
/// owns the multi-match focus move).
/// </summary>
public class AkTarget : Probe, IAccessKeyTarget
{
    public AkTarget(string name, List<string> log)
        : base(name, log)
        => Focusable = true;

    /// <summary>Extra eligibility veto on top of the conventional visible+enabled gate.</summary>
    public bool Eligible { get; set; } = true;

    public List<(char Key, bool IsMultiMatch)> Activations { get; } = [];

    public bool IsAccessKeyEligible => Eligible && IsEffectivelyVisible && IsEffectivelyEnabled;

    public void OnAccessKey(AccessKeyEventArgs e)
    {
        Log.Add($"{LogName}.OnAccessKey({(e.IsMultiMatch ? "multi" : "single")})");
        Activations.Add((e.Key, e.IsMultiMatch));
    }
}

/// <summary>
/// The §11 command-coupled control (the ButtonBase pattern minus ButtonBase — N146/N164):
/// <c>IsEnabledCore</c> follows a command's <c>CanExecute</c> via <c>InvalidateIsEnabledCore</c>.
/// </summary>
public class CommandBtn : Btn
{
    private bool _isEnabledCore = true;
    private System.Windows.Input.ICommand? _command;

    public CommandBtn(string name, List<string> log)
        : base(name, log)
    {
    }

    protected override bool IsEnabledCore => _isEnabledCore;

    /// <summary>Direct core-enabled flip (the N146 shape).</summary>
    public void SetIsEnabledCore(bool value)
    {
        if (_isEnabledCore == value)
            return;

        _isEnabledCore = value;
        InvalidateIsEnabledCore();
    }

    /// <summary>Hooks <c>CanExecuteChanged</c> (the N164 shape; strong handler — test-scoped).</summary>
    public void AttachCommand(System.Windows.Input.ICommand command)
    {
        _command = command;
        command.CanExecuteChanged += (_, _) => SetIsEnabledCore(_command!.CanExecute(null));
        SetIsEnabledCore(command.CanExecute(null));
    }
}

/// <summary>A scriptable <c>ICommand</c> for the §11 rows.</summary>
public sealed class TestCommand : System.Windows.Input.ICommand
{
    public bool CanExecuteResult { get; set; } = true;

    public List<object?> Executions { get; } = [];

    public int CanExecuteCalls { get; private set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        CanExecuteCalls++;
        return CanExecuteResult;
    }

    public void Execute(object? parameter) => Executions.Add(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The matrix §0.1 <c>sink</c>: an <see cref="IInteractionStateObserver"/> recording ordered
/// <c>(element, old → new)</c> notifications, optionally mirroring them into the shared log as
/// <c>state:{Name}:{old}-&gt;{new}</c> (the N49 order markers).
/// </summary>
public sealed class StateSink(List<string>? log = null) : IInteractionStateObserver
{
    public List<(UIElement Element, InteractionState OldState, InteractionState NewState)> Notifications { get; } = [];

    public void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState)
    {
        Notifications.Add((element, oldState, newState));
        log?.Add($"state:{(element as Probe)?.LogName ?? element.GetType().Name}:{oldState}->{newState}");
    }
}

/// <summary>
/// A <see cref="Probe"/> that lays out like a <see cref="Canvas"/> (children at the
/// <c>Canvas.Left/Top</c> attached coordinates with their desired sizes) while keeping the probe's
/// rigid natural size and <c>On*</c> logging — the matrix §5 tree's container shape.
/// </summary>
public class CanvasProbe : Probe
{
    public CanvasProbe(string name, List<string> log, int columns, int rows)
        : base(name, log, columns, rows)
    {
    }

    /// <summary>Adds <paramref name="child"/> at the canvas-local cell (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public void Place(UIElement child, int left, int top)
    {
        Canvas.SetLeft(child, left);
        Canvas.SetTop(child, top);
        AddChild(child);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (VisualChildrenList is { } children)
        {
            var unbounded = new Size(LayoutMath.Unbounded, LayoutMath.Unbounded);
            for (var i = 0; i < children.Count; i++)
                children[i].Measure(unbounded);
        }

        return Natural;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (VisualChildrenList is { } children)
        {
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var desired = child.DesiredSize;
                child.Arrange(new Rect(
                    Canvas.GetLeft(child) ?? 0,
                    Canvas.GetTop(child) ?? 0,
                    desired.Columns,
                    desired.Rows));
            }
        }

        return finalSize;
    }
}

/// <summary>
/// The matrix §5–§7 mouse fixture: <c>Root</c> (Canvas-shaped, fills the 80×24 viewport) with
/// <c>A</c> at (10,5,10×4), <c>B</c> at (30,5,10×4), and <c>D</c> a child of <c>A</c> at terminal
/// (12,6,4×2). Shown and settled (one frame) so <c>RenderTree.HitTest</c> is valid; the log is
/// cleared after the settle.
/// </summary>
public sealed class MouseHost : IDisposable
{
    private MouseHost(UITestHost host, List<string> log, CanvasProbe root, CanvasProbe a, Probe b, Probe d)
    {
        Host = host;
        Log = log;
        Root = root;
        A = a;
        B = b;
        D = d;
    }

    public UITestHost Host { get; }

    public List<string> Log { get; }

    public CanvasProbe Root { get; }

    public CanvasProbe A { get; }

    public Probe B { get; }

    public Probe D { get; }

    public InputDispatcher Dispatcher => Host.Application.InputDispatcher;

    /// <summary>The root's render tree (the delegation oracle — N58/N59/N60).</summary>
    public RenderTree Tree => Host.Application.WindowManager!.Tree!;

    public static MouseHost Create(bool show = true)
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var a = new CanvasProbe("A", log, 10, 4);
        var b = new Probe("B", log, 10, 4);
        var d = new Probe("D", log, 4, 2);
        root.Place(a, 10, 5);
        root.Place(b, 30, 5);
        a.Place(d, 2, 1); // terminal (12, 6)

        if (show)
        {
            host.ShowRoot(root);
            host.RunFrame(); // settle layout + the first render pass — HitTest is valid after it
            log.Clear();
        }

        return new MouseHost(host, log, root, a, b, d);
    }

    // ───────────────────────────── injectors (direct ProcessEvent, UI thread) ─────────────────────────────

    public InputDispatchResult Move(int column, int row)
        => Dispatcher.ProcessEvent(MouseEvt(MouseEventKind.Move, column, row));

    public InputDispatchResult Drag(int column, int row, MouseButtons held)
        => Dispatcher.ProcessEvent(MouseEvt(MouseEventKind.Drag, column, row, buttonsHeld: held));

    public InputDispatchResult Down(int column, int row, MouseButton button = MouseButton.Left, int clicks = 1)
        => Dispatcher.ProcessEvent(MouseEvt(MouseEventKind.ButtonDown, column, row, button, clickCount: clicks));

    public InputDispatchResult Up(int column, int row, MouseButton button = MouseButton.Left)
        => Dispatcher.ProcessEvent(MouseEvt(MouseEventKind.ButtonUp, column, row, button));

    public InputDispatchResult Wheel(int column, int row, int deltaY)
        => Dispatcher.ProcessEvent(MouseEvt(MouseEventKind.Wheel, column, row) with { WheelDeltaY = deltaY });

    public MouseEvent MouseEvt(
        MouseEventKind kind,
        int column,
        int row,
        MouseButton button = MouseButton.None,
        MouseButtons buttonsHeld = MouseButtons.None,
        int clickCount = 1)
        => new()
        {
            Kind = kind,
            Position = new CellPosition(column, row),
            Button = button,
            ButtonsHeld = buttonsHeld,
            Modifiers = KeyModifiers.None,
            ClickCount = clickCount,
            Timestamp = Host.Time.GetUtcNow(),
        };

    public void Dispose() => Host.Dispose();
}

/// <summary>
/// The shared host fixture: a <see cref="UITestHost"/> with the default chain
/// <c>Root → A → B</c> (B a focusable <see cref="Btn"/>) shown as the application root, plus
/// device-event factories stamped on the fake clock.
/// </summary>
public sealed class InputHost : IDisposable
{
    private InputHost(UITestHost host, List<string> log, Probe root, Probe a, Btn b)
    {
        Host = host;
        Log = log;
        Root = root;
        A = a;
        B = b;
    }

    public UITestHost Host { get; }

    public List<string> Log { get; }

    public Probe Root { get; }

    public Probe A { get; }

    public Btn B { get; }

    public InputDispatcher Dispatcher => Host.Application.InputDispatcher;

    /// <summary>
    /// Creates the default chain host: <c>Root → A → B</c>, shown (Root is the active root).
    /// Window activation focuses the first tab-ordered focusable (N115), so B starts focused; the
    /// activation's GotFocus entries are cleared from the log. Rows needing "nothing focused" call
    /// <c>ClearFocus()</c> explicitly.
    /// </summary>
    public static InputHost CreateChain(bool show = true)
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Probe("A", log);
        var b = new Btn("B", log);
        root.AddChild(a);
        a.AddChild(b);
        if (show)
        {
            host.ShowRoot(root);
            log.Clear(); // drop the activation-restore GotFocus entries (N115 auto-focuses B)
        }

        return new InputHost(host, log, root, a, b);
    }

    /// <summary>Creates a host with no root ever shown (N22/N71 setups).</summary>
    public static InputHost CreateUnshown()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Probe("A", log);
        var b = new Btn("B", log);
        root.AddChild(a);
        a.AddChild(b);
        return new InputHost(host, log, root, a, b);
    }

    public KeyEvent Key(
        Key key,
        KeyModifiers modifiers = KeyModifiers.None,
        string? text = null,
        KeyEventKind kind = KeyEventKind.Down,
        bool synthesized = false,
        bool isRepeat = false,
        int repeatCount = 1,
        KeyModifiers extendedModifiers = KeyModifiers.None)
        => new()
        {
            Key = key,
            Modifiers = modifiers,
            ExtendedModifiers = extendedModifiers,
            Kind = kind,
            Text = (text ?? string.Empty).AsMemory(),
            Synthesized = synthesized,
            IsRepeat = isRepeat,
            RepeatCount = repeatCount,
            Timestamp = Host.Time.GetUtcNow(),
        };

    public MouseEvent Mouse(
        MouseEventKind kind,
        int column,
        int row,
        MouseButton button = MouseButton.None,
        MouseButtons buttonsHeld = MouseButtons.None,
        int clickCount = 1,
        bool synthesized = false)
        => new()
        {
            Kind = kind,
            Position = new CellPosition(column, row),
            Button = button,
            ButtonsHeld = buttonsHeld,
            Modifiers = KeyModifiers.None,
            ClickCount = clickCount,
            Synthesized = synthesized,
            Timestamp = Host.Time.GetUtcNow(),
        };

    public FocusEvent Focus(bool hasFocus)
        => new() { HasFocus = hasFocus, Timestamp = Host.Time.GetUtcNow() };

    public PasteEvent Paste(string text)
        => new() { Text = text.AsMemory(), Timestamp = Host.Time.GetUtcNow() };

    public void Dispose() => Host.Dispose();
}
