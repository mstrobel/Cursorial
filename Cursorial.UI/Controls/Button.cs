using System.Windows.Input;

using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A push button (design doc §12.7): a <see cref="ButtonBase"/> with the <see cref="IsDefault"/> /
/// <see cref="IsCancel"/> window-key affordances. A default/cancel button installs an Enter/Esc
/// <see cref="KeyBinding"/> on the surface root on attach and removes it on detach — focused-element-
/// wins falls out of bubble order, so no window-key registry exists.
/// </summary>
public class Button : ButtonBase
{
    private KeyBinding? _defaultBinding;
    private KeyBinding? _cancelBinding;
    private UIElement? _bindingRoot;

    /// <summary>Whether this is the default button (Enter activates it from anywhere on the surface); <c>:default</c> pseudo-class.</summary>
    public static readonly StyledProperty<bool> IsDefaultProperty =
        UIProperty.Register<Button, bool>(nameof(IsDefault), changed: OnIsDefaultChanged);

    /// <summary>Whether this is the cancel button (Esc activates it from anywhere on the surface).</summary>
    public static readonly StyledProperty<bool> IsCancelProperty =
        UIProperty.Register<Button, bool>(nameof(IsCancel), changed: OnIsCancelChanged);

    static Button()
    {
        // :default flips via the PseudoClassMapping (a control-semantic class with no InteractionState bit).
        PseudoClassMapping.Register<Button>(IsDefaultProperty, ":default");
    }

    /// <inheritdoc cref="IsDefaultProperty"/>
    public bool IsDefault { get => GetValue(IsDefaultProperty); set => SetValue(IsDefaultProperty, value); }

    /// <inheritdoc cref="IsCancelProperty"/>
    public bool IsCancel { get => GetValue(IsCancelProperty); set => SetValue(IsCancelProperty, value); }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        _bindingRoot = VisualRoot;
        UpdateWindowKeyBindings();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        RemoveWindowKeyBindings();
        _bindingRoot = null;
        base.OnDetachedFromTree(in e);
    }

    private void UpdateWindowKeyBindings()
    {
        if (_bindingRoot is not { } root)
            return;

        if (IsDefault && _defaultBinding is null)
        {
            _defaultBinding = new KeyBinding(new KeyGesture(Key.Enter), new ActivateCommand(this));
            root.InputBindings.Add(_defaultBinding);
        }
        else if (!IsDefault && _defaultBinding is { } existing)
        {
            root.InputBindings.Remove(existing);
            _defaultBinding = null;
        }

        if (IsCancel && _cancelBinding is null)
        {
            _cancelBinding = new KeyBinding(new KeyGesture(Key.Escape), new ActivateCommand(this));
            root.InputBindings.Add(_cancelBinding);
        }
        else if (!IsCancel && _cancelBinding is { } existingCancel)
        {
            root.InputBindings.Remove(existingCancel);
            _cancelBinding = null;
        }
    }

    private void RemoveWindowKeyBindings()
    {
        if (_bindingRoot is not { } root)
            return;

        if (_defaultBinding is { } d)
            root.InputBindings.Remove(d);
        if (_cancelBinding is { } c)
            root.InputBindings.Remove(c);
        _defaultBinding = null;
        _cancelBinding = null;
    }

    private static void OnIsDefaultChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is Button button && button.IsAttachedToTree)
            button.UpdateWindowKeyBindings();
    }

    private static void OnIsCancelChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is Button button && button.IsAttachedToTree)
            button.UpdateWindowKeyBindings();
    }

    // The command the window KeyBindings fire: clicks the button (focused-element-wins is bubble order,
    // so this only reaches the button when nothing else consumed Enter/Esc).
    private sealed class ActivateCommand(Button button) : ICommand
    {
        public bool CanExecute(object? parameter) => button.IsEffectivelyEnabled && button.IsEffectivelyVisible;

        public void Execute(object? parameter) => button.OnClick();

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
