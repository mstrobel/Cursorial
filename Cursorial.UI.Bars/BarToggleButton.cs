using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A toggleable command button for a bar surface (the bars guide's <c>BarToggleButton</c> — checked = accent
/// whole-cell fill). Derives from <see cref="ToggleButton"/>, so it inherits the <see cref="ToggleButton.IsChecked"/>
/// state, the <c>:checked</c> pseudo-class, and the command coupling. Like <see cref="BarButton"/> it adds an
/// <see cref="Icon"/> + <see cref="InputGestureText"/> and auto-fills them from a <see cref="BarCommand"/>.
/// <para>
/// When its <see cref="ButtonBase.CommandParameter"/> is an <see cref="ICheckableCommandParameter"/>, the checked
/// state is <b>command-owned</b>: the button does not self-toggle on click (the command's <c>Execute</c> mutates the
/// parameter), and it re-reads <see cref="ICheckableCommandParameter.IsChecked"/> on every command-state re-query
/// (the <see cref="ButtonBase.OnCommandStateChanged"/> hook fired by <c>CanExecuteChanged</c>) — so one command
/// drives the checked state of every surface that hosts it, with no <c>IsChecked</c> binding. Without such a
/// parameter it self-toggles like a plain <see cref="ToggleButton"/>.
/// </para>
/// </summary>
public class BarToggleButton : ToggleButton
{
    private readonly BarCommandSync _commandSync = new();

    /// <inheritdoc cref="BarButton.IconProperty"/>
    public static readonly StyledProperty<object?> IconProperty =
        BarButton.IconProperty.AddOwner<BarToggleButton>(); // same identity as BarButton's, so one template binds both

    /// <inheritdoc cref="BarButton.InputGestureTextProperty"/>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        BarButton.InputGestureTextProperty.AddOwner<BarToggleButton>();

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <summary>Whether the checked state is owned by an <see cref="ICheckableCommandParameter"/> (so the button
    /// reflects the command rather than self-toggling).</summary>
    private bool IsCheckedCommandOwned => CommandParameter is ICheckableCommandParameter;

    /// <inheritdoc/>
    protected override void OnToggle()
    {
        if (IsCheckedCommandOwned)
            return; // command-owned: Execute mutates the parameter; the sync reflects it (no self-toggle)
        base.OnToggle();
    }

    /// <inheritdoc/>
    protected override void OnCommandStateChanged()
    {
        base.OnCommandStateChanged();
        _commandSync.AutoFill(this, Command, IconProperty, InputGestureTextProperty);
        SyncCheckedFromCommand();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        _commandSync.AutoFill(this, Command, IconProperty, InputGestureTextProperty);
        SyncCheckedFromCommand(); // initial reflect (no CanExecuteChanged has fired yet)
    }

    private void SyncCheckedFromCommand()
    {
        if (CommandParameter is ICheckableCommandParameter checkable)
            SetCurrentValue(IsCheckedProperty, (bool?)checkable.IsChecked);
    }
}
