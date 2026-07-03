using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A status-bar cell (the WPF/Avalonia <c>StatusBarItem</c>): the <see cref="ContentControl"/> container the
/// <see cref="StatusBar"/> wraps each item in. Set <c>DockPanel.Dock</c> on it to right-align (or otherwise dock) it
/// within the bar.
/// </summary>
public class StatusBarItem : ContentControl
{
    static StatusBarItem()
    {
        PaddingProperty.OverrideMetadata<StatusBarItem>(new PropertyMetadata<Margins>(DefaultValue: new Margins(1, 0)));
    }
}
