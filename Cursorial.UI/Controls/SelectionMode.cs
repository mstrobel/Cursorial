namespace Cursorial.UI.Controls;

/// <summary>
/// How many items a <see cref="SelectionModel"/> (and the controls over it — ListBox/TabControl) may select at once
/// (design doc §12.6 / ledger CD-P9-10). WPF's <c>Extended</c> is not a distinct mode: its modifier policy
/// (plain-click = replace, Ctrl = toggle, Shift = range-from-anchor) is the control's input mapping onto the model's
/// <see cref="SelectionModel.Select"/>/<see cref="SelectionModel.Toggle"/>/<see cref="SelectionModel.SelectRangeFromAnchor"/>
/// primitives.
/// </summary>
public enum SelectionMode
{
    /// <summary>At most one item is selected; every operation caps the selection at one.</summary>
    Single,

    /// <summary>Any number of items may be selected (toggle + range-from-anchor).</summary>
    Multiple
}
