using System.Windows.Input;

using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The clickable command source base (design doc §12.7): mouse capture on press (both
/// <see cref="ClickMode"/>s), the <c>:pressed</c> interaction state (<see cref="IsPressed"/> mirror),
/// Space/Enter activation (Space on Down, <c>IsRepeat</c>-guarded — CD23), the routed
/// <see cref="ClickEvent"/>, and the <see cref="Command"/> coupling (<see cref="IsEnabledCore"/>
/// includes <c>CanExecute</c>, CD25).
/// </summary>
public abstract class ButtonBase : ContentControl, IAccessKeyTarget
{
    private bool _captured;       // we hold mouse capture (press in flight)
    private bool _spaceLatched;   // Space is held (keyboard press in flight)

    /// <summary>When the button raises its click (default <see cref="ClickMode.Release"/> — doc §12.7).</summary>
    public static readonly StyledProperty<ClickMode> ClickModeProperty =
        UIProperty.Register<ButtonBase, ClickMode>(nameof(ClickMode), defaultValue: ClickMode.Release);

    /// <summary>The command invoked on click when its <c>CanExecute</c> is true (BCL <see cref="ICommand"/>).</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<ButtonBase, ICommand?>(nameof(Command), changed: OnCommandChanged);

    /// <summary>The parameter passed to <see cref="Command"/>.</summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        UIProperty.Register<ButtonBase, object?>(nameof(CommandParameter), changed: OnCommandParameterChanged);

    private static readonly UIPropertyKey<bool> IsPressedPropertyKey =
        UIProperty.RegisterReadOnly<ButtonBase, bool>(nameof(IsPressed));

    /// <summary>Whether the button is pressed — the read-only mirror of <see cref="InteractionState.Pressed"/> (CD24).</summary>
    public static readonly StyledProperty<bool> IsPressedProperty = IsPressedPropertyKey.Property;

    /// <summary>The bubbling click event (<c>RoutedEvent&lt;ClickEventArgs&gt;</c>, doc §12.7).</summary>
    public static readonly RoutedEvent<ClickEventArgs> ClickEvent =
        RoutedEvent<ClickEventArgs>.Register(nameof(Click), RoutingStrategy.Bubble, typeof(ButtonBase));

    static ButtonBase()
    {
        // ButtonBase.Content folds access-key literals (doc §12.5 producer ②, resolved at runtime type).
        ContentProperty.OverrideMetadata<ButtonBase>(new PropertyMetadata<object?>() { ParsesAccessKeyLiterals = true });
        FocusableProperty.OverrideDefaultValue<ButtonBase>(true);
    }

    /// <summary>Creates a button base.</summary>
    protected ButtonBase()
    {
    }

    /// <inheritdoc cref="ClickModeProperty"/>
    public ClickMode ClickMode { get => GetValue(ClickModeProperty); set => SetValue(ClickModeProperty, value); }

    /// <inheritdoc cref="CommandProperty"/>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <inheritdoc cref="CommandParameterProperty"/>
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    /// <inheritdoc cref="IsPressedProperty"/>
    public bool IsPressed => GetValue(IsPressedProperty);

    /// <summary>The CLR sugar over <see cref="ClickEvent"/>.</summary>
    public event EventHandler<ClickEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value!);
        remove => RemoveHandler(ClickEvent, value!);
    }

    // ───────────────────────────── click / command (doc §12.7) ─────────────────────────────

    /// <summary>
    /// Raises <see cref="ClickEvent"/> (bubbles) then executes <see cref="Command"/> when its
    /// <c>CanExecute</c> is true (doc §12.7).
    /// </summary>
    protected virtual void OnClick()
    {
        var args = RentEvent(ClickEvent);
        RaiseEvent(args);

        if (Command is { } command)
        {
            var parameter = CommandParameter;
            if (command.CanExecute(parameter))
                command.Execute(parameter);
        }
    }

    /// <summary>
    /// The command-aware enabled gate (CD25): <c>Command is null || Command.CanExecute(CommandParameter)</c>.
    /// Effective-enabled folds this through S1's plumbing into <see cref="InteractionState.Disabled"/>.
    /// </summary>
    protected override bool IsEnabledCore
        => Command is not { } command || command.CanExecute(CommandParameter);

    // ───────────────────────────── mouse (capture + :pressed, doc §12.7) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        e.Handled = true;
        Focus(FocusNavigationMethod.Pointer);
        _captured = CaptureMouse(); // capture for BOTH ClickModes (CD23)
        SetPressed(true);

        if (ClickMode == ClickMode.Press)
            OnClick();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_captured)
            SetPressed(IsPointerOver); // pressed tracks pointer-over while captured (C186)
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left || !_captured)
            return;

        e.Handled = true;
        var over = IsPointerOver;
        ReleaseMouseCapture(); // → OnLostMouseCapture clears _captured + pressed
        _captured = false;
        SetPressed(false);

        if (over && ClickMode == ClickMode.Release)
            OnClick(); // up over self ⇒ click (C187); off self ⇒ no click (C188)
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _captured = false;
        SetPressed(false); // capture stolen ⇒ unpressed, no click (C189)
    }

    // ───────────────────────────── keyboard (down-activation, CD23) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        switch (e.Key)
        {
            case Key.Space:
                // Space activates on Down (IsRepeat-guarded, CD23); the pressed-latch visual is a
                // capability-gated nicety where Up is reported.
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return; // auto-repeat does not re-activate (C192)
                }

                e.Handled = true;
                _spaceLatched = true;
                SetPressed(true);
                OnClick();
                break;

            case Key.Enter:
                e.Handled = true;
                OnClick(); // immediate click, no pressed latch (C193)
                break;
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space && _spaceLatched)
        {
            _spaceLatched = false;
            SetPressed(false);
        }
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_spaceLatched)
        {
            _spaceLatched = false; // the Space latch clears on focus loss, no click (C194)
            SetPressed(false);
        }
    }

    // ───────────────────────────── access key (doc §12.5) ─────────────────────────────

    /// <inheritdoc/>
    bool IAccessKeyTarget.IsAccessKeyEligible => IsEffectivelyEnabled && IsEffectivelyVisible;

    /// <inheritdoc/>
    void IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs e) => OnAccessKey(e);

    /// <summary>The access-key reaction (doc §12.5): a button clicks. Multi-match focuses only (ND18).</summary>
    protected virtual void OnAccessKey(AccessKeyEventArgs e)
    {
        if (e.IsMultiMatch)
            return; // the manager already focused us; multi-match never invokes (ND18)

        OnClick();
    }

    // ───────────────────────────── attach lifecycle (access key + command coupling) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e); // ContentControl registers the access key (doc §12.5)
        SubscribeCanExecute();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        UnsubscribeCanExecute();
        base.OnDetachedFromTree(in e); // ContentControl unregisters the access key
    }

    // ───────────────────────────── command CanExecute coupling (CD25) ─────────────────────────────

    private void SubscribeCanExecute()
    {
        if (Command is { } command)
            command.CanExecuteChanged += OnCanExecuteChanged;
    }

    private void UnsubscribeCanExecute()
    {
        if (Command is { } command)
            command.CanExecuteChanged -= OnCanExecuteChanged;
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => InvalidateIsEnabledCore();

    private static void OnCommandChanged(UIObject sender, ICommand? oldValue, ICommand? newValue)
    {
        if (sender is not ButtonBase button)
            return;

        // Unsubscribe the old command (on detach AND on Command change, CD25), subscribe the new.
        if (oldValue is { } old && button.IsAttachedToTree)
            old.CanExecuteChanged -= button.OnCanExecuteChanged;
        if (newValue is { } @new && button.IsAttachedToTree)
            @new.CanExecuteChanged += button.OnCanExecuteChanged;

        button.InvalidateIsEnabledCore();
    }

    private static void OnCommandParameterChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is ButtonBase button)
            button.InvalidateIsEnabledCore();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void SetPressed(bool pressed)
    {
        // Pressed flows through S3's SetInteractionState so terminal focus-out clears it window-wide
        // (the pressed-holder set, ND12/CD24). The IsPressed mirror tracks the bit via
        // OnInteractionStateChangedCore — including a window-wide clear that bypasses this method.
        SetInteractionState(InteractionState.Pressed, pressed);
    }

    /// <inheritdoc/>
    private protected override void OnInteractionStateChangedCore(InteractionState oldState, InteractionState newState)
    {
        base.OnInteractionStateChangedCore(oldState, newState);
        if (((oldState ^ newState) & InteractionState.Pressed) != 0)
            SetValue(IsPressedPropertyKey, (newState & InteractionState.Pressed) != 0);
    }
}
