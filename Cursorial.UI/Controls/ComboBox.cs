using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A single-selection drop-down list (design doc §12.11 — the ListBox-in-Popup recipe). The face is a
/// <see cref="ToggleButton"/> (<c>PART_Toggle</c>) showing the <see cref="SelectingItemsControl.SelectedItem"/>;
/// clicking it (or a keyboard open) drops a <see cref="Popup"/> (<c>PART_Popup</c>) whose items host is this
/// control's <see cref="ItemsControl"/> presenter. Containers are <see cref="ComboBoxItem"/>s; selection rides the
/// <see cref="SelectingItemsControl"/> base. Picking an item (click or Enter) commits the selection and closes the
/// drop-down; Escape / light-dismiss close without changing it. The editable (text-entry) variant is a v2 deferral.
/// </summary>
[TemplatePart(PartPopup, typeof(Popup))]
public class ComboBox : SelectingItemsControl
{
    private const string PartPopup = "PART_Popup";

    /// <summary>Whether the drop-down is open (<c>:open</c>; two-way with the <see cref="Popup"/>).</summary>
    public static readonly DirectProperty<ComboBox, bool> IsDropDownOpenProperty =
        UIProperty.RegisterDirect<ComboBox, bool>(nameof(IsDropDownOpen), static c => c._isDropDownOpen, static (c, v) => c.SetDropDownOpen(v));

    private bool _isDropDownOpen;
    private Popup? _popup;

    /// <summary>Creates a combo box (single selection; focusable — the face is the tab stop).</summary>
    public ComboBox()
    {
        SelectionMode = SelectionMode.Single;
        Focusable = true;
    }

    /// <inheritdoc cref="IsDropDownOpenProperty"/>
    public bool IsDropDownOpen { get => _isDropDownOpen; set => SetDropDownOpen(value); }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new ComboBoxItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is ComboBoxItem;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;

        _popup = GetTemplatePart<Popup>(PartPopup);
        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = PlacementMode.Bottom;
            _popup.KeepOpenOnAnchorPress = true; // a face click closes via OnMouseDown's toggle, not dismiss-then-reopen
            _popup.Closed += OnPopupClosed;
            _popup.SetCurrentValue(Popup.IsOpenProperty, _isDropDownOpen);
        }
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup = null;
        }

        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        SetDropDownOpen(false); // close so the Popup surface doesn't leak on detach
        base.OnDetachedFromTree(e);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        // A left click on the face (not on a dropped item — those are on the Popup's own surface) toggles the list.
        if (!e.Handled && e.Button == MouseButton.Left)
        {
            Focus();
            SetDropDownOpen(!_isDropDownOpen);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    private protected override void OnTextSearchMatch(int containerIndex)
    {
        base.OnTextSearchMatch(containerIndex); // selection-follows (the face updates)
        if (_isDropDownOpen) // when open, move the keyboard highlight too, so :focus-visible tracks the match
            ItemContainerGenerator.ContainerFromIndex(containerIndex)?.Focus(FocusNavigationMethod.Directional);
    }

    // Picking an item (click) commits + closes (ComboBoxItem calls this after selecting through the base).
    internal void CommitAndClose() => SetDropDownOpen(false);

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e) => SetDropDownOpen(false); // light-dismiss / Esc

    private void SetDropDownOpen(bool value)
    {
        if (!SetAndRaise(IsDropDownOpenProperty, ref _isDropDownOpen, value))
            return;

        PseudoClasses.Set(":open", value); // DirectProperty-backed, so the :open class is set imperatively (cf. MenuItem)
        _popup?.SetCurrentValue(Popup.IsOpenProperty, value);

        if (!value)
            Focus(); // restore focus to the face when the drop-down closes
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        if (!_isDropDownOpen)
        {
            // Closed: open on Down/Up/Enter/F4/Space (Alt+Down is a Down with the Alt modifier — covered).
            if (e.Key is Key.DownArrow or Key.UpArrow or Key.Enter or Key.F4 || IsSpace(e))
            {
                SetDropDownOpen(true);
                e.Handled = true;
            }

            return;
        }

        var count = ItemContainerGenerator.ContainerCount;

        switch (e.Key)
        {
            case Key.DownArrow when count > 0:
                MoveSelection(SelectedIndex < 0 ? 0 : Math.Min(count - 1, SelectedIndex + 1));
                break;
            case Key.UpArrow when count > 0:
                MoveSelection(SelectedIndex < 0 ? 0 : Math.Max(0, SelectedIndex - 1));
                break;
            case Key.Home when count > 0:
                MoveSelection(0);
                break;
            case Key.End when count > 0:
                MoveSelection(count - 1);
                break;
            case Key.Enter:
            case Key.Escape:
                SetDropDownOpen(false); // Enter commits the highlighted selection; Escape just closes
                break;
            default:
                if (!IsSpace(e))
                    return;
                SetDropDownOpen(false);
                break;
        }

        e.Handled = true;
    }

    // Selection-follows-highlight while open: change the selection (the face's ContentPresenter updates live) and
    // focus the container for the :focus-visible cue. The drop-down stays open until Enter / click / Escape.
    private void MoveSelection(int index)
    {
        Selection.Select(index);
        ItemContainerGenerator.ContainerFromIndex(index)?.Focus(FocusNavigationMethod.Directional);
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); Key.Space is only NUL→Ctrl+Space.
    private static bool IsSpace(KeyEventArgs e)
        => e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ');
}
