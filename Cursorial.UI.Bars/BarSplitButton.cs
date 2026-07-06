using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A bar drop-opener with TWO hit zones (the bars guide's <c>BarSplitButton</c> — e.g. "Paste"): the <b>label</b> runs
/// the primary action (<see cref="ButtonBase.Click"/> / <see cref="ButtonBase.Command"/>, exactly like a
/// <see cref="BarButton"/>), and a tinted <c>▾</c> zone (<c>PART_DropDown</c>) opens the dropdown <see cref="Popup"/>.
/// The primary action auto-returns focus on a non-retaining <see cref="Toolbar"/> like any bar button; only the ▾ zone
/// is a retaining focus-scope opener (so opening the dropdown from it doesn't yank focus). Auto-fills Content/Icon/
/// InputGestureText from a <see cref="BarCommand"/>.
/// </summary>
[TemplatePart(PartDropDown, typeof(ButtonBase))]
public class BarSplitButton : BarDropDownButton
{
    private const string PartDropDown = "PART_DropDown";

    private ButtonBase? _dropZone;

    internal ButtonBase? DropZoneForTests => _dropZone;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_dropZone is not null)
            _dropZone.Click -= OnDropZoneClick;

        _dropZone = GetTemplatePart<ButtonBase>(PartDropDown);

        if (_dropZone is not null)
        {
            _dropZone.Click -= OnDropZoneClick;
            _dropZone.Click += OnDropZoneClick;

            // Only the ▾ zone is the barrier — opening the dropdown from it doesn't trip the Toolbar's auto-return,
            // while the split button's PRIMARY label action (base ButtonBase OnClick) still auto-returns normally.
            FocusManager.SetIsFocusScope(_dropZone, true);
        }
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_dropZone is not null)
            _dropZone.Click -= OnDropZoneClick;

        base.OnTemplateDetaching(old);
    }

    private void OnDropZoneClick(object? sender, ClickEventArgs e)
    {
        ToggleDropDown();
        e.Handled = true; // the ▾ zone's click is the dropdown toggle, never the primary action
    }

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
