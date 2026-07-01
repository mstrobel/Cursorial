using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A bar-surface combobox (the bars guide's <c>BarComboBox</c> — <c>[value ▾]</c>): the existing
/// <see cref="ComboBox"/> with a flat, compact bar face (no chrome border — the bar field tint IS the affordance). It
/// inherits every ComboBox behavior (single selection, the drop-down list, type-ahead, the editable text mode). Its
/// drop-down rows are ordinary <c>ComboBoxItem</c>s (the built-in item theme). On a <see cref="Toolbar"/> it packs and
/// overflows like any bar item.
/// </summary>
public class BarComboBox : ComboBox
{
    static BarComboBox()
    {
        Control.ThemeProperty.OverrideDefaultValue<BarComboBox>(CursorialBarsTheme.BarComboBoxStyle());
    }
}
