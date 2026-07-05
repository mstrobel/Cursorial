using System.Windows.Input;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A command button for a bar surface (the bars guide's <c>BarButton</c>). It derives from
/// <see cref="ButtonBase"/> (not plain <see cref="Control"/>), inheriting the <see cref="ButtonBase.Command"/>
/// /<c>CanExecute</c> coupling, the routed <see cref="ButtonBase.Click"/>, <see cref="ButtonBase.IsPressed"/>, and
/// Space/Enter activation. The button's label is its inherited <see cref="ContentControl.Content"/> (access-key
/// literals folded, e.g. <c>"_Paste"</c>); it adds an <see cref="Icon"/> and an <see cref="InputGestureText"/>
/// accelerator hint. When its <see cref="ButtonBase.Command"/> is a <see cref="BarCommand"/>, the button
/// <b>auto-fills</b> any unset Content / Icon / InputGestureText from the command (via <c>SetCurrentValue</c>, so an
/// explicit author or style value still wins) — one command declaration drives the label, icon, and gesture text
/// on every surface that hosts it.
/// </summary>
public class BarButton : ButtonBase
{
    private readonly BarCommandSync _commandSync = new();

    /// <summary>The button's icon — an <see cref="Controls.Icon"/>/icon source or a glyph string (rendered beside the label).</summary>
    public static readonly StyledProperty<object?> IconProperty =
        UIProperty.Register<BarButton, object?>(nameof(Icon));

    /// <summary>The accelerator hint shown beside the label (display-only; register the real <c>KeyBinding</c> separately).</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.Register<BarButton, string?>(nameof(InputGestureText));

    static BarButton()
    {
        // :has-icon marks a bar control carrying an Icon (the shared IconProperty — AddOwner'd by the toggle/dropdown
        // buttons, so this one registration covers them all). The ribbon density Compact cascade drops the label to
        // icon-only ONLY for :has-icon controls (a label-only button keeps its label). Registered HERE, in the bar-
        // button static ctor, so it is live before any Icon is set — the PseudoClassMapping is change-only, and a
        // control's Icon is often set (initializer / BarCommand auto-fill) before a Ribbon is ever constructed.
        PseudoClassMapping.Register<UIElement, object?>(
            IconProperty, static icon => icon is not null ? ":has-icon" : null, ":has-icon");
    }

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc/>
    protected override void OnCommandStateChanged()
    {
        base.OnCommandStateChanged();
        _commandSync.AutoFill(this, Command, IconProperty, InputGestureTextProperty);
    }
}

/// <summary>Per-control <see cref="BarCommand"/> → display-metadata auto-fill (the define-once Text/Icon/gesture
/// flowing into a control's Content/Icon/InputGestureText). One instance per <see cref="ButtonBase"/>-derived bar
/// control (<see cref="BarButton"/>, <see cref="BarToggleButton"/>, and the split/popup buttons), so the behavior is
/// identical everywhere. It remembers which properties IT filled, so a runtime <see cref="ButtonBase.Command"/> swap
/// refreshes or clears the auto-filled values instead of leaving the old command's label stranded — while never
/// clobbering an explicit author/style value (which it never recorded as its own).</summary>
public sealed class BarCommandSync
{
    private bool _filledContent, _filledIcon, _filledGesture, _filledTip;

    /// <summary>Reconciles Content/Icon/InputGestureText (and a SuperTip) against <paramref name="command"/>'s display
    /// metadata. A non-<see cref="BarCommand"/> <see cref="ICommand"/> (or <see langword="null"/>) supplies nothing, so
    /// any value this helper previously filled is cleared.</summary>
    public void AutoFill(ContentControl control, ICommand? command, StyledProperty<object?> iconProperty, StyledProperty<string?> gestureProperty)
    {
        var bar = command as BarCommand;
        Apply(control, ContentControl.ContentProperty, bar?.Text, ref _filledContent);
        Apply(control, iconProperty, bar?.Icon, ref _filledIcon);
        Apply(control, gestureProperty, bar?.InputGestureText, ref _filledGesture);
        ApplyTip(control, bar);
    }

    // A BarCommand with a Description auto-provisions a SuperTip (rich hover help, identical wherever the command
    // appears). Only when the control has no explicit tip the author set; our own prior auto-tip is refreshed/cleared
    // on a command change. Uses the attached ToolTipService.Tip (not a StyledProperty, so it can't route through Apply).
    private void ApplyTip(ContentControl control, BarCommand? bar)
    {
        var existing = ToolTipService.GetTip(control);
        if (existing is not null && !_filledTip)
            return; // an explicit tip the author/style set — it wins

        if (bar?.Description is not null)
        {
            var tip = SuperTip.FromCommand(bar);
            tip.Anchor = control; // so the tip can auto-compute its KeyTip hop sequence from the control's ribbon position
            ToolTipService.SetTip(control, tip);
            _filledTip = true;
        }
        else if (_filledTip)
        {
            ToolTipService.SetTip(control, null); // our stale auto-tip — the new command has no rich help
            _filledTip = false;
        }
    }

    // Fill from the command when the control carries no explicit author/style value; refresh our OWN prior fill on a
    // command change; clear it when the new command supplies nothing (a swap to null / a raw ICommand). The `filled`
    // flag is how we tell our SetCurrentValue graft apart from an author value — after the first fill both look like a
    // LocalValue, so IsSet alone can't (the stale-label bug).
    private static void Apply<T>(ContentControl control, StyledProperty<T> property, T? value, ref bool filled)
    {
        if (control.IsSet(property) && !filled)
            return; // an explicit author/style value we didn't put there — it wins

        if (value is not null)
        {
            control.SetCurrentValue(property, value);
            filled = true;
        }
        else if (filled)
        {
            control.ClearValue(property); // our stale auto-fill — the new command has nothing to offer here
            filled = false;
        }
    }
}
