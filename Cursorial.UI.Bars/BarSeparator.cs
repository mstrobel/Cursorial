using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// The bar separator (bars guide §3): a single <c>│</c> cell dividing related clusters on a bar surface. The one
/// load-bearing separator — split buttons carry their own tinted dropdown zone instead. The <see cref="Toolbar"/>'s
/// overflow trims trailing separators from the visible row and leading separators from the popup (later phase).
/// </summary>
public class BarSeparator : Control
{
    static BarSeparator()
    {
        FocusableProperty.OverrideDefaultValue<BarSeparator>(false);
        Control.ThemeProperty.OverrideDefaultValue<BarSeparator>(CursorialBarsTheme.SeparatorStyle());
    }
}
