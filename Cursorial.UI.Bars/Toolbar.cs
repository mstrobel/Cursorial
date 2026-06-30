using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// Surface A (bars guide §4): a single horizontal row of bar controls. An <see cref="ItemsControl"/> whose items
/// are bar controls (<see cref="BarButton"/>, <see cref="BarToggleButton"/>, <see cref="BarSeparator"/>, …).
/// <para>
/// <b>Phase 1:</b> a plain horizontal row. Discrete overflow (packing the tail into a <c>»</c> popup) lands next
/// as a custom items panel; the row layout and the bar-control themes are in place first.
/// </para>
/// </summary>
public class Toolbar : ItemsControl
{
    static Toolbar()
    {
        Control.ThemeProperty.OverrideDefaultValue<Toolbar>(CursorialBarsTheme.ToolbarStyle());
    }
}
