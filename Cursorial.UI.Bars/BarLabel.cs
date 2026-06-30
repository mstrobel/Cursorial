using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A non-interactive caption on a bar surface (the bars guide's text/label element): static text (or an
/// <see cref="Icon"/>) used to title a cluster of bar controls. Derives from <see cref="ContentControl"/> so it shows
/// either, is not focusable, and renders in the muted bar foreground. It packs and overflows like any
/// <see cref="ToolbarOverflowMode.AsNeeded"/> item on a <see cref="Toolbar"/>.
/// </summary>
public class BarLabel : ContentControl
{
    static BarLabel()
    {
        FocusableProperty.OverrideDefaultValue<BarLabel>(false);
        Control.ThemeProperty.OverrideDefaultValue<BarLabel>(CursorialBarsTheme.BarLabelStyle());
    }
}
