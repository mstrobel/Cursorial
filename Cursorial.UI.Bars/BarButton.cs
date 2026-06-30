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
    /// <summary>The button's icon — an <see cref="Controls.Icon"/>/icon source or a glyph string (rendered beside the label).</summary>
    public static readonly StyledProperty<object?> IconProperty =
        UIProperty.Register<BarButton, object?>(nameof(Icon));

    /// <summary>The accelerator hint shown beside the label (display-only; register the real <c>KeyBinding</c> separately).</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.Register<BarButton, string?>(nameof(InputGestureText));

    static BarButton()
    {
        Control.ThemeProperty.OverrideDefaultValue<BarButton>(CursorialBarsTheme.BarButtonStyle());
    }

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc/>
    protected override void OnCommandStateChanged()
    {
        base.OnCommandStateChanged();
        BarCommandSync.AutoFill(this, Command, IconProperty, InputGestureTextProperty);
    }
}

/// <summary>Shared <see cref="BarCommand"/> → bar-control auto-fill (the define-once display metadata flowing into a
/// control's Content/Icon/InputGestureText). Used by every <see cref="ButtonBase"/>-derived bar control so the
/// behavior is identical across <see cref="BarButton"/>, <see cref="BarToggleButton"/>, and the split/popup buttons.</summary>
internal static class BarCommandSync
{
    /// <summary>Fills the control's unset Content/Icon/InputGestureText from a <see cref="BarCommand"/> via
    /// <c>SetCurrentValue</c> (so an explicit local/style value wins, and the fill is a harmless idempotent re-run on
    /// each command-state change). A non-<see cref="BarCommand"/> <see cref="ICommand"/> supplies nothing.</summary>
    public static void AutoFill(ContentControl control, ICommand? command, StyledProperty<object?> iconProperty, StyledProperty<string?> gestureProperty)
    {
        if (command is not BarCommand bar)
            return;

        if (bar.Text is { } text && !control.IsSet(ContentControl.ContentProperty))
            control.SetCurrentValue(ContentControl.ContentProperty, text);
        if (bar.Icon is { } icon && !control.IsSet(iconProperty))
            control.SetCurrentValue(iconProperty, icon);
        if (bar.InputGestureText is { } gesture && !control.IsSet(gestureProperty))
            control.SetCurrentValue(gestureProperty, gesture);
    }
}
