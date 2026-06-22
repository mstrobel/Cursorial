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
public class InputBinding : UIObject
{
    /// <summary>
    /// The gesture that triggers <see cref="Command"/>; a binding without one never matches.
    /// Typed as the abstract <see cref="InputGesture"/> so future gesture kinds (a revisited
    /// <c>MouseBinding</c> — doc §7.12) arrive without a source break; <see cref="KeyGesture"/> is
    /// the only concrete kind in v1.
    /// </summary>
    public static readonly StyledProperty<InputGesture?>
        GestureProperty = UIProperty.Register<InputBinding, InputGesture?>(nameof(Gesture));

    /// <summary>The command to execute; a binding without one never matches.</summary>
    public static readonly StyledProperty<ICommand?>
        CommandProperty = UIProperty.Register<InputBinding, ICommand?>(nameof(Command));

    /// <summary>Passed to both <c>CanExecute</c> and <c>Execute</c>.</summary>
    public static readonly StyledProperty<object?>
        CommandParameterProperty = UIProperty.Register<InputBinding, object?>(nameof(CommandParameter));

    /// <inheritdoc cref="GestureProperty"/>
    public InputGesture? Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <inheritdoc cref="CommandParameter"/>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }


    /// <inheritdoc cref="CommandParameterProperty"/>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}

/// <summary>A key-gesture <see cref="InputBinding"/> (the only gesture kind in v1 — doc §7.12).</summary>
public sealed class KeyBinding : InputBinding
{
    /// <summary>Creates an empty binding (object-initializer / XAML shape).</summary>
    public KeyBinding() {}

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
public sealed class InputBindingCollection : Collection<InputBinding>
{
    private readonly UIElement _owner;

    public InputBindingCollection(UIElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    protected override void ClearItems()
    {
        while (Count > 0)
            RemoveAt(Count - 1);
    }

    protected override void InsertItem(int index, InputBinding item)
    {
        base.InsertItem(index, item);
        item.SetInheritanceParent(_owner);
    }

    protected override void RemoveItem(int index)
    {
        Items[index].SetInheritanceParent(null);
        base.RemoveItem(index);
    }

    protected override void SetItem(int index, InputBinding item)
    {
        if (Items[index] is {} oldItem)
            oldItem.SetInheritanceParent(null);

        base.SetItem(index, item);

        Items[index].SetInheritanceParent(_owner);
    }
}