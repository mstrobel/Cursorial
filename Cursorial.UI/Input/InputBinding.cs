using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Cursorial.UI.Input;

/// <summary>
/// One gesture → command association on an element (design doc §7.9). Swept per node during the
/// <c>KeyDown</c> bubble — after the node's virtual and instance handlers, while unhandled — so
/// element-scoped shortcuts compose with focus naturally (a binding on the window root fires for
/// any unhandled key in that window; a binding on a pane only while focus is inside it).
/// </summary>
/// <remarks>
/// <c>ICommand</c> is the BCL <see cref="System.Windows.Input.ICommand"/> — no
/// <c>RoutedCommand</c>/<c>CommandManager</c> exists in v1 (doc §7.12); MVVM-style commands raise
/// their own <c>CanExecuteChanged</c> (cross-thread raises must be marshaled via
/// <c>UIDispatcher.Post</c>).
/// </remarks>
public class InputBinding
{
    /// <summary>
    /// The gesture that triggers <see cref="Command"/>; a binding without one never matches.
    /// Typed as the abstract <see cref="InputGesture"/> so future gesture kinds (a revisited
    /// <c>MouseBinding</c> — doc §7.12) arrive without a source break; <see cref="KeyGesture"/> is
    /// the only concrete kind in v1.
    /// </summary>
    public InputGesture? Gesture { get; set; }

    /// <summary>The command to execute; a binding without one never matches.</summary>
    public ICommand? Command { get; set; }

    /// <summary>Passed to both <c>CanExecute</c> and <c>Execute</c>.</summary>
    public object? CommandParameter { get; set; }
}

/// <summary>A key-gesture <see cref="InputBinding"/> (the only gesture kind in v1 — doc §7.12).</summary>
public sealed class KeyBinding : InputBinding
{
    /// <summary>Creates an empty binding (object-initializer / XAML shape).</summary>
    public KeyBinding()
    {
    }

    /// <summary>Creates a bound gesture.</summary>
    /// <param name="gesture">The triggering gesture.</param>
    /// <param name="command">The command to execute.</param>
    public KeyBinding(KeyGesture gesture, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(command);
        Gesture = gesture;
        Command = command;
    }
}

/// <summary>
/// The ordered binding collection on <see cref="UIElement.InputBindings"/> — <b>ordering is the
/// priority mechanism</b> (doc §7.9): the sweep executes the first matching gesture whose command
/// can execute and never consults later entries.
/// </summary>
public sealed class InputBindingCollection : Collection<InputBinding>;
