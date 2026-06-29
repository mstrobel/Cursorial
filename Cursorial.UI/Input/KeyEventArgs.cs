using Cursorial.Input;
using Cursorial.Input.Events;

namespace Cursorial.UI.Input;

public abstract class InputEventArgs : RoutedEventArgs
{
    protected InputEventArgs() {}
    protected InputEventArgs(RoutedEvent routedEvent, UIElement source) : base(routedEvent, source) {}
}

/// <summary>
/// Args for the <c>PreviewKeyDown</c>/<c>KeyDown</c>/<c>PreviewKeyUp</c>/<c>KeyUp</c> events,
/// wrapping the immutable device record (design doc §7.2). Match shortcuts against
/// <see cref="Modifiers"/> (lock-free), never <see cref="ExtendedModifiers"/>; the identity of a
/// printable key is <c>(Key, Text)</c>, not <see cref="Key"/> alone.
/// </summary>
public sealed class KeyEventArgs : InputEventArgs
{
    private KeyEvent? _device;

    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public KeyEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args wrapping <paramref name="device"/>.</summary>
    /// <param name="routedEvent">The key event to raise.</param>
    /// <param name="source">The dispatch target.</param>
    /// <param name="device">The device record (immutable; retainable).</param>
    public KeyEventArgs(RoutedEvent<KeyEventArgs> routedEvent, UIElement source, KeyEvent device)
        : base(routedEvent, source)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>The wrapped device record — immutable and always retainable (copy this, never the args).</summary>
    public KeyEvent Device
    {
        get
        {
            ThrowIfStale();
            return _device ?? throw new InvalidOperationException("This KeyEventArgs carries no device record.");
        }
    }

    /// <summary>The key. <see cref="Cursorial.Input.Key.Character"/> means "printable key — see <see cref="Text"/>".</summary>
    public Key Key => Device.Key;

    /// <summary>Input modifiers only (lock bits guaranteed absent) — match shortcuts against this.</summary>
    public KeyModifiers Modifiers => Device.Modifiers;

    /// <summary>A strict superset of <see cref="Modifiers"/> including lock-toggle bits; never used for matching.</summary>
    public KeyModifiers ExtendedModifiers => Device.ExtendedModifiers;

    /// <summary>Composed printable text (the <c>(Key, Text)</c> identity for <see cref="Cursorial.Input.Key.Character"/>).</summary>
    public ReadOnlyMemory<char> Text => Device.Text;

    /// <summary>Auto-repeat marker (only ever true on key-down; controls guard activation on it).</summary>
    public bool IsRepeat => Device.IsRepeat;

    /// <summary>The repeat ordinal (1 for the initial press).</summary>
    public int RepeatCount => Device.RepeatCount;

    /// <summary>The platform raw key code when known (layout-independent fallback).</summary>
    public uint? RawCode => Device.RawCode;

    /// <summary>Framework-dispatch payload initialization.</summary>
    internal void InitializeKey(KeyEvent device) => _device = device;

    /// <inheritdoc/>
    internal override void ResetPooledState() => _device = null;
}
