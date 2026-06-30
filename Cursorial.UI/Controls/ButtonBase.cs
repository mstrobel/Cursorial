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

    // Invoke the click, then return focus to where it came from when this button sits in a NON-retaining focus scope
    // (FocusManager.RetainsFocus = false, e.g. a Toolbar: click Bold, keep typing). The return is gated by HOW the
    // scope was ENTERED, not by the invoke modality: TryAutoReturnFocus returns only for a Pointer/AccessKey entry —
    // so a mouse click returns, an access-key-then-arrow-then-Enter returns (the access-key entry latched it), but a
    // plain Tab-in + Enter stays (the user is navigating; Escape returns). Skipped when the click itself moved focus
    // elsewhere (a drop-down button that focused its popup). A cheap no-op outside a non-retaining scope.
    private void InvokeClickRetaining(FocusNavigationMethod modality)
    {
        var focus = UIApplication.Current?.FocusManager;
        var before = focus?.FocusedElement;
        OnClick();
        if (focus is not null && ReferenceEquals(focus.FocusedElement, before))
            focus.TryAutoReturnFocus(this, modality);
    }

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
            InvokeClickRetaining(FocusNavigationMethod.Pointer);
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
            InvokeClickRetaining(FocusNavigationMethod.Pointer); // up over self ⇒ click (C187); off self ⇒ no click (C188)
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _captured = false;
        SetPressed(false); // capture stolen ⇒ unpressed, no click (C189)
    }

    // ───────────────────────────── keyboard (down-activation, CD23) ─────────────────────────────

    /// <summary>
    /// Whether <paramref name="e"/> is the spacebar (the button-activation key). Per the P2 input
    /// matrix ND10 the spacebar's identity is <c>(Key.Character, " ")</c> on every protocol path
    /// (VT 0x20, Kitty codepoint 32, Win32 VK_SPACE); <see cref="Key.Space"/> is emitted only for
    /// NUL→Ctrl+Space, which always carries <see cref="KeyModifiers.Control"/>. Both forms are
    /// matched — but only when modifier-free — so the real character-form spacebar (and a synthetic
    /// modifier-free <see cref="Key.Space"/>) activates while Ctrl+Space (and any other modified
    /// chord) does not (ND10 / N158 — the activation gesture is <c>(Character, " ", None)</c>).
    /// </summary>
    internal static bool IsActivationSpace(KeyEventArgs e)
        => e.Modifiers == KeyModifiers.None &&
           (e.Key == Key.Space ||
            e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ');

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        if (IsActivationSpace(e))
        {
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
            InvokeClickRetaining(FocusNavigationMethod.Restore); // returns iff the scope was entered Pointer/AccessKey
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            InvokeClickRetaining(FocusNavigationMethod.Restore); // immediate click, no pressed latch (C193)
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_spaceLatched && IsActivationSpace(e))
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

        InvokeClickRetaining(FocusNavigationMethod.AccessKey);
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

    private void OnCanExecuteChanged(object? sender, EventArgs e)
    {
        InvalidateIsEnabledCore();
        OnCommandStateChanged();
    }

    /// <summary>
    /// Called whenever the command's effective state may have changed — its <c>CanExecuteChanged</c> fired, or the
    /// <see cref="Command"/>/<see cref="CommandParameter"/> itself changed. A no-op here; a derived control overrides
    /// it to reflect command-owned state that rides the same re-query (e.g. a toggle pulling its checked state from
    /// an <c>ICheckableCommandParameter</c>), so a single <c>CanExecuteChanged</c> updates both enabled and checked.
    /// </summary>
    protected virtual void OnCommandStateChanged()
    {
    }

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
        button.OnCommandStateChanged();
    }

    private static void OnCommandParameterChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is ButtonBase button)
        {
            button.InvalidateIsEnabledCore();
            button.OnCommandStateChanged();
        }
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
