using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A button that maintains an on/off (or three-state) checked status (design doc §12.7): each
/// activation (Space/Enter/click/access-key) cycles <see cref="IsChecked"/> through the WPF order
/// (CD26) — <c>false → true → false</c> when <see cref="IsThreeState"/> is false, or
/// <c>false → true → null → false</c> when true. <c>:checked</c> (true) and <c>:indeterminate</c>
/// (null) flip via <see cref="PseudoClassMapping"/>'s multi-class projection. The base for
/// <see cref="CheckBox"/> and <see cref="RadioButton"/>.
/// </summary>
public class ToggleButton : ButtonBase
{
    /// <summary>The checked state: <see langword="false"/> / <see langword="true"/> / <see langword="null"/> (indeterminate). Two-way bindable; <c>:checked</c>/<c>:indeterminate</c> mirror it (CD26).</summary>
    public static readonly StyledProperty<bool?> IsCheckedProperty =
        UIProperty.Register<ToggleButton, bool?>(nameof(IsChecked), defaultValue: false, changed: OnIsCheckedChanged);

    /// <summary>Whether the toggle includes the indeterminate (<see langword="null"/>) state in its cycle (default false — WPF).</summary>
    public static readonly StyledProperty<bool> IsThreeStateProperty =
        UIProperty.Register<ToggleButton, bool>(nameof(IsThreeState));

    /// <summary>The bubbling event raised whenever the toggle is checked (<see cref="IsChecked"/> ⇒ true).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CheckedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Checked), RoutingStrategy.Bubble, typeof(ToggleButton));

    /// <summary>The bubbling event raised whenever the toggle is unchecked (<see cref="IsChecked"/> ⇒ false).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> UncheckedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Unchecked), RoutingStrategy.Bubble, typeof(ToggleButton));

    /// <summary>The bubbling event raised whenever the toggle enters the indeterminate (<see langword="null"/>) state.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IndeterminateEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Indeterminate), RoutingStrategy.Bubble, typeof(ToggleButton));

    static ToggleButton()
    {
        // bool? → :checked (true) / :indeterminate (null) / none (false), one-pass multi-class (CD26).
        PseudoClassMapping.Register<ToggleButton, bool?>(
            IsCheckedProperty,
            static value => value switch { true => ":checked", null => ":indeterminate", _ => null },
            ":checked", ":indeterminate");
    }

    /// <inheritdoc cref="IsCheckedProperty"/>
    public bool? IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    /// <inheritdoc cref="IsThreeStateProperty"/>
    public bool IsThreeState { get => GetValue(IsThreeStateProperty); set => SetValue(IsThreeStateProperty, value); }

    /// <summary>CLR sugar over <see cref="CheckedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Checked { add => AddHandler(CheckedEvent, value!); remove => RemoveHandler(CheckedEvent, value!); }

    /// <summary>CLR sugar over <see cref="UncheckedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Unchecked { add => AddHandler(UncheckedEvent, value!); remove => RemoveHandler(UncheckedEvent, value!); }

    /// <summary>CLR sugar over <see cref="IndeterminateEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Indeterminate { add => AddHandler(IndeterminateEvent, value!); remove => RemoveHandler(IndeterminateEvent, value!); }

    /// <summary>
    /// The activation cycle (CD26, WPF order): <c>false → true → false</c> (two-state) or
    /// <c>false → true → null → false</c> (three-state). A click/Space/Enter routes through
    /// <see cref="OnClick"/>, which calls <see cref="OnToggle"/> before raising the click.
    /// </summary>
    protected virtual void OnToggle()
    {
        var current = IsChecked;
        IsChecked = current switch
        {
            false => true,
            true => IsThreeState ? null : false,
            null => false,
        };
    }

    /// <inheritdoc/>
    protected override void OnClick()
    {
        OnToggle();
        base.OnClick();
    }

    /// <inheritdoc/>
    protected override void OnAccessKey(AccessKeyEventArgs e)
    {
        // Access key toggles via the same path as Space/click (C206). Multi-match never invokes (ND18).
        base.OnAccessKey(e);
    }

    private static void OnIsCheckedChanged(UIObject sender, bool? oldValue, bool? newValue)
    {
        if (sender is not ToggleButton toggle)
            return;

        var routedEvent = newValue switch
        {
            true => CheckedEvent,
            false => UncheckedEvent,
            null => IndeterminateEvent,
        };

        toggle.OnIsCheckedChangedCore(oldValue, newValue);

        if (toggle.IsAttachedToTree)
        {
            var args = toggle.RentEvent(routedEvent);
            toggle.RaiseEvent(args);
        }
    }

    /// <summary>The control-author hook called after <see cref="IsChecked"/> changes, before the routed event (RadioButton group uncheck rides this).</summary>
    private protected virtual void OnIsCheckedChangedCore(bool? oldValue, bool? newValue)
    {
    }
}
