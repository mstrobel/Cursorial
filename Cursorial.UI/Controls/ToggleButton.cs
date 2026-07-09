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
    /// <summary>The checked state: <see langword="false"/> / <see langword="true"/> / <see langword="null"/> (indeterminate). Two-way bindable; <c>:checked</c>/<c>:indeterminate</c> mirror it (CD26). A command-owned <see cref="ICheckableCommandParameter"/> can coerce the effective value (FB-27, see <see cref="CoerceIsChecked"/>).</summary>
    public static readonly StyledProperty<bool?> IsCheckedProperty =
        UIProperty.Register<ToggleButton, bool?>(nameof(IsChecked), defaultValue: false, coerce: CoerceIsChecked, changed: OnIsCheckedChanged);

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

    /// <summary>The direct event raised whenever the value of <see cref="IsChecked"/> changes.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsCheckedChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(IsCheckedChanged), RoutingStrategy.Direct, typeof(ToggleButton));

    static ToggleButton()
    {
        // bool? → :checked (true) / :indeterminate (null) / none (false), one-pass multi-class (CD26).
        PseudoClassMapping.Register<ToggleButton, bool?>(
            IsCheckedProperty,
            static value => value switch { true => ":checked", null => ":indeterminate", _ => null },
            ":checked", ":indeterminate");
        
        AddGlobalEffects(PropertyEffects.BindsTwoWayByDefault, IsCheckedProperty);
        
        CommandParameterProperty.OverrideMetadata<ToggleButton>(new PropertyMetadata<object?>(Coerce: CoerceCommandParameter));
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

    /// <summary>CLR sugar over <see cref="IsCheckedChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? IsCheckedChanged { add => AddHandler(IsCheckedChangedEvent, value!); remove => RemoveHandler(IsCheckedChangedEvent, value!); }

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
                        true  => IsThreeState ? null : false,
                        null  => false
                    };
    }

    /// <inheritdoc/>
    protected override void OnClick()
    {
        OnToggle();
        base.OnClick();
    }

    // ReSharper disable once RedundantOverriddenMember
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
                              true  => CheckedEvent,
                              false => UncheckedEvent,
                              null  => IndeterminateEvent
                          };

        toggle.OnIsCheckedChangedCore(oldValue, newValue);

        if (toggle.IsAttachedToTree)
        {
            var args = toggle.RentEvent(routedEvent);
            toggle.RaiseEvent(args);
            args = toggle.RentEvent(IsCheckedChangedEvent);
            toggle.RaiseEvent(args);
            
            toggle.SyncCheckedWithCommandParameter();
        }
    }

    /// <summary>The control-author hook called after <see cref="IsChecked"/> changes, before the routed event (RadioButton group uncheck rides this).</summary>
    private protected virtual void OnIsCheckedChangedCore(bool? oldValue, bool? newValue)
    {
    }

    // ───────────────────────────── command-owned checked state (coercion, FB-27) ─────────────────────────────

    // The per-control default checkable parameter — allocated lazily (see OnCommandStateChanged) so a standalone
    // toggle works and stays command-agnostic. Superseded by an app/command/Bars-supplied ICheckableCommandParameter
    // on CommandParameter, which is what EffectiveCheckableParameter prefers.
    private CheckableCommandParameter? _defaultCheckableParameter;

    // The checkable command parameter this toggle's checked state answers to, or null. A checkable parameter on
    // CommandParameter (a Bars surface points every control bound to one command at the same instance, so the command
    // drives them all) wins; otherwise the per-control default. The CommandParameter coercion below reads it.
    private ICheckableCommandParameter? EffectiveCheckableParameter
        => CommandParameter as ICheckableCommandParameter ?? _defaultCheckableParameter;

    // CommandParameter coercion (FB-27): while no CommandParameter has been provided, fall back to the auto-generated
    // CheckableCommandParameter provided by the framework.
    private static object? CoerceCommandParameter(UIObject sender, object? baseValue)
    {
        if (sender is ToggleButton toggle && baseValue is null)
            return toggle.EffectiveCheckableParameter;

        return baseValue;
    }

    // IsChecked coercion (FB-27): while a checkable command parameter is Handled, the effective checked state is the
    // parameter's forced override — an override at EITHER polarity (greyed+unchecked or on-but-locked), pair it with a
    // false CanExecute to gray/lock. Otherwise, the base value passes through UNCHANGED, so an unhandled parameter and
    // every non-checkable toggle behave exactly as before (zero-cost backward-compat). The base value is never
    // mutated, so the control's own preference reappears automatically when Handled clears — the store re-coerces the
    // raw base value (no restore bookkeeping).
    private static bool? CoerceIsChecked(UIObject sender, bool? baseValue)
    {
        if (sender is ToggleButton { Command: not null, CommandParameter: ICheckableCommandParameter cp })
        {
            if (cp is CheckableCommandParameter wcp)
                wcp.IsChecked = baseValue;
            return cp.IsCheckedEffective;
        }

        return baseValue;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The checkable family assigns a command parameter here (see <see cref="ButtonBase.OnCommandStateChanged"/>'s
    /// remarks): a per-control default is installed only when nothing has provided a checked source yet, and the same
    /// hook re-coerces <see cref="IsChecked"/> so a <see cref="ICheckableCommandParameter.Handled"/> override (which is
    /// not a dependency property, so it does not self-invalidate) snaps in on the command's re-query.
    /// </remarks>
    protected override void OnCommandStateChanged()
    {
        SyncCheckedWithCommandParameter();

        base.OnCommandStateChanged();

        var checkedSourceIsDefault = GetValueSource(IsCheckedProperty).Kind == ValueSourceKind.Default;

        // Per-control default (FB-27 point 4): allocate ONLY when nothing has provided a checked source yet — the
        // IsChecked base value is still at its Default source. A Bars-layer override reflects a command-SHARED
        // parameter into the base value BEFORE calling base (its sync-before-base inversion), which makes the
        // source non-Default, so this skips the default and the shared parameter (on CommandParameter) governs.
        if (_defaultCheckableParameter is null && checkedSourceIsDefault)
        {
            _defaultCheckableParameter = new CheckableCommandParameter(GetBaseValue(IsCheckedProperty));

            // Enable coercion of the default checkable parameter (coercion will not run with Default value source).
            if (GetValueSource(CommandParameterProperty).Kind == ValueSourceKind.Default)
                SetCurrentValue(CommandParameterProperty, _defaultCheckableParameter);

            CoerceValue(CommandParameterProperty);
        }

        // A Handled parameter must be able to force a value even onto a toggle that has never carried one.
        // CoerceValue (in `SyncCheckedWithCommandParameter` below) no-ops on the Default lane — the store
        // never coerces the metadata default (PD8) — so when a Handled override needs to apply and nothing
        // has set IsChecked yet, graft a current-value base first: SetCurrentValue coerces inline (applying
        // the override if one exists), and the grafted RAW value (the control's own current value, derived
        // from the default value and applied as a local value) is the preference that reappears the instant
        // Handled clears (the store re-coerces that raw value — no bookkeeping).
        if (checkedSourceIsDefault)
            SetCurrentValue(IsCheckedProperty, IsCheckedProperty.GetMetadata(GetType()).DefaultValue);
    }

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        SyncCheckedWithCommandParameter(); // initial reflect + snap (SetCurrentValue coerces inline; no CanExecuteChanged yet)
    }

    // Reflect the command-shared parameter's IsChecked into the IsChecked BASE value (SetCurrentValue preserves a
    // two-way binding). This is the backward-compatible consumption path: for an unhandled parameter it drives the
    // control's checked state exactly as before; for a Handled one it establishes the base (preference) that the
    // coercer overrides — and that the control falls back to the instant Handled clears.
    protected void SyncCheckedWithCommandParameter()
    {
        if (Command is null) return;

        // Re-coercion trigger (FB-27 point 3): the parameter is not a DP, so a Handled/override change does not
        // auto-invalidate IsChecked — re-coerce on the same command-state hook that re-queries CanExecute.
        if (CommandParameter is ICheckableCommandParameter readableChecked)
            SetCurrentValue(IsCheckedProperty, readableChecked.IsChecked);
    }

    // ───────────────────────────── focus caret (the box indicator, design doc §5.9) ─────────────────────────────

    // CheckBox/RadioButton focus is the caret-in-box, NOT reverse-video: a check/radio in a (vertical)
    // StackPanel stretches to the panel width, so a reverse-video focus fill would span the whole row like
    // a selection bar instead of cueing the box. The caret is a precise in-box keyboard-focus cue. Driven
    // in code off the control's :focus-visible bit rather than a `^:focus-visible /template/ Caret` style
    // rule — the styling engine documents /template/ combined with an ancestor-state pseudo as a
    // non-re-evaluating approximation. Null when a custom template omits the part.
    private Caret? _focusCaret;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _focusCaret = GetTemplatePart<Caret>("PART_Caret");
        UpdateFocusCaret(); // sync if focus-visible was already set before the template applied
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_focusCaret is { } caret)
            caret.IsCaretShown = false;
        _focusCaret = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    private protected override void OnInteractionStateChangedCore(InteractionState oldState, InteractionState newState)
    {
        base.OnInteractionStateChangedCore(oldState, newState);
        if (((oldState ^ newState) & InteractionState.FocusVisible) != 0)
            UpdateFocusCaret();
    }

    // Show the box caret exactly while the toggle is keyboard-focused (:focus-visible); pointer focus
    // leaves it hidden. The Caret publishes the real terminal cursor at its arranged origin (the box's
    // inner cell).
    private void UpdateFocusCaret()
    {
        if (_focusCaret is { } caret)
            caret.IsCaretShown = (InteractionStateInternal & InteractionState.FocusVisible) != 0;
    }
}
