using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A bar drop-opener whose WHOLE control opens its dropdown (the bars guide's <c>BarPopupButton</c> — e.g. "Align"),
/// marked by a bare <c>▾</c> caret. One hit target: a click / Space / Enter / Down toggles the dropdown
/// <see cref="Popup"/> over the <see cref="BarDropDownButton.DropDownContent"/>. It has no separate primary action
/// (unlike <see cref="BarSplitButton"/>), so the WHOLE control is the retaining focus-scope opener — opening never
/// trips an enclosing <see cref="Toolbar"/>'s auto-return. Auto-fills Content/Icon/InputGestureText from a
/// <see cref="BarCommand"/> like the other bar buttons.
/// </summary>
public class BarPopupButton : BarDropDownButton
{
    static BarPopupButton()
    {
        // The whole control is the drop-opener → a retaining focus scope (a FindReturningScope barrier), so a
        // pointer-click-open never trips an enclosing non-retaining Toolbar's auto-return (mirrors the overflow chevron).
        FocusManager.IsFocusScopeProperty.OverrideDefaultValue<BarPopupButton>(true);
    }

    /// <inheritdoc/>
    protected override void OnClick() => ToggleDropDown(); // the whole control toggles the dropdown (no primary command)

    /// <inheritdoc/>
    protected override void OnCommandStateChanged()
    {
        // Sync BEFORE base — the base-last inversion, kept uniform across the bar-button family (FB-27 point 5): a bar
        // control that assigns command-derived state runs its sync first so the checkable base (ToggleButton) can't
        // clobber it with its default command parameter. See ButtonBase.OnCommandStateChanged's remarks. Do NOT re-tidy
        // to call base first.
        CommandSync.AutoFill(this, Command, IconProperty, InputGestureTextProperty);
        base.OnCommandStateChanged();
    }
}
