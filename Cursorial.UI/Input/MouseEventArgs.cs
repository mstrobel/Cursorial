using Cursorial.Input;
using Cursorial.Input.Events;

namespace Cursorial.UI.Input;

/// <summary>
/// Args for the mouse vocabulary (design doc §7.2), wrapping the immutable device record.
/// Positions are integer cells: <see cref="ScreenPosition"/> is terminal coordinates;
/// <see cref="GetPosition"/> translates into any element's local frame (may be negative under
/// capture or composite slides — signed math, never a throw).
/// </summary>
public class MouseEventArgs : RoutedEventArgs
{
    private MouseEvent? _device;

    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public MouseEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args wrapping <paramref name="device"/>.</summary>
    /// <param name="routedEvent">The mouse event to raise.</param>
    /// <param name="source">The dispatch target.</param>
    /// <param name="device">The device record (immutable; retainable).</param>
    public MouseEventArgs(RoutedEvent<MouseEventArgs> routedEvent, UIElement source, MouseEvent device)
        : base(routedEvent, source)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>Derived-type caller-owned construction.</summary>
    private protected MouseEventArgs(RoutedEvent routedEvent, UIElement source, MouseEvent device)
        : base(routedEvent, source)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>The wrapped device record — immutable and always retainable (copy this, never the args).</summary>
    public MouseEvent Device
    {
        get
        {
            ThrowIfStale();
            return _device ?? throw new InvalidOperationException("This MouseEventArgs carries no device record.");
        }
    }

    /// <summary>
    /// The position in terminal (screen) coordinates — the carrier S4's screen-anchored drag math
    /// reads. At the P2 single-root topology, window coordinates equal terminal coordinates.
    /// </summary>
    public CellPosition ScreenPosition => Device.Position;

    /// <summary>The held-button mask accumulated by the input layer (drags and moves carry it).</summary>
    public MouseButtons ButtonsHeld => Device.ButtonsHeld;

    /// <summary>Keyboard modifiers held during the mouse event (lock-free).</summary>
    public KeyModifiers Modifiers => Device.Modifiers;

    /// <summary>
    /// The position in <paramref name="relativeTo"/>'s element-local cells, computed through
    /// terminal coordinates + the live parent-chain translation — well-defined for any attached
    /// element (cross-surface use is legal) and possibly negative under capture. Never throws on
    /// out-of-bounds positions.
    /// </summary>
    /// <param name="relativeTo">The element whose local frame to translate into.</param>
    public CellPosition GetPosition(UIElement relativeTo)
    {
        ArgumentNullException.ThrowIfNull(relativeTo);
        var position = Device.Position;
        var (column, row) = relativeTo.TranslateToLocal(position.Column, position.Row);
        return new CellPosition(column, row);
    }

    /// <summary>Framework-dispatch payload initialization.</summary>
    internal void InitializeMouse(MouseEvent device) => _device = device;

    /// <inheritdoc/>
    internal override void ResetPooledState() => _device = null;
}

/// <summary>
/// Args for <c>PreviewMouseDown</c>/<c>MouseDown</c>/<c>PreviewMouseUp</c>/<c>MouseUp</c>.
/// </summary>
public sealed class MouseButtonEventArgs : MouseEventArgs
{
    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public MouseButtonEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args wrapping <paramref name="device"/>.</summary>
    /// <param name="routedEvent">The mouse-button event to raise.</param>
    /// <param name="source">The dispatch target.</param>
    /// <param name="device">The device record (immutable; retainable).</param>
    public MouseButtonEventArgs(RoutedEvent<MouseButtonEventArgs> routedEvent, UIElement source, MouseEvent device)
        : base(routedEvent, source, device)
    {
    }

    /// <summary>The button that changed state.</summary>
    public MouseButton Button => Device.Button;

    /// <summary>
    /// The multi-click count, baked in upstream by <c>MouseClickSynthesizer</c> (the dispatcher adds
    /// no timing). Under the mandated pipeline (<c>ClickCountTarget.ButtonDown</c>) this reads &gt; 1
    /// <b>only on MouseDown</b>; MouseUp always reads 1 — double-click logic belongs in
    /// <c>OnMouseDown</c>.
    /// </summary>
    public int ClickCount => Device.ClickCount;
}

/// <summary>Args for <c>PreviewMouseWheel</c>/<c>MouseWheel</c>.</summary>
public sealed class MouseWheelEventArgs : MouseEventArgs
{
    private int _linesPerNotch = DefaultLinesPerNotch;

    private const int DefaultLinesPerNotch = 3;

    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public MouseWheelEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args wrapping <paramref name="device"/>.</summary>
    /// <param name="routedEvent">The wheel event to raise.</param>
    /// <param name="source">The dispatch target.</param>
    /// <param name="device">The device record (immutable; retainable).</param>
    public MouseWheelEventArgs(RoutedEvent<MouseWheelEventArgs> routedEvent, UIElement source, MouseEvent device)
        : base(routedEvent, source, device)
    {
    }

    /// <summary>The vertical wheel delta in 1/120-notch units (positive = away from the user).</summary>
    public int WheelDeltaY => Device.WheelDeltaY;

    /// <summary>The horizontal wheel delta in 1/120-notch units (positive = right).</summary>
    public int WheelDeltaX => Device.WheelDeltaX;

    /// <summary>A consumer hint for notch → line conversion (ScrollViewer reads it; default 3).</summary>
    public int LinesPerNotch
    {
        get
        {
            ThrowIfStale();
            return _linesPerNotch;
        }
        set
        {
            ThrowIfStale();
            _linesPerNotch = value;
        }
    }

    /// <inheritdoc/>
    internal override void ResetPooledState()
    {
        base.ResetPooledState();
        _linesPerNotch = DefaultLinesPerNotch;
    }
}
