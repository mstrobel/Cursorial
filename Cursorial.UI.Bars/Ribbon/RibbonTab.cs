using Cursorial.Input;
using Cursorial.Markup;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// One tab of a <see cref="Ribbon"/> (the guide's <c>.rib-tabs .t</c>). Its <see cref="HeaderedContentControl.Header"/>
/// is the strip label; its children are <see cref="RibbonGroup"/>s that make up the band shown when the tab is
/// selected. It derives from <see cref="TabItem"/>, inheriting selection (<c>:selected</c> two-way), the strip-label
/// rendering, and access-key folding; the band is hosted by the parent <see cref="Ribbon"/>'s content host exactly as
/// a <see cref="TabItem"/>'s content is (single-hosting — the tab's own template renders only the header).
/// <para>Set <see cref="IsFileTab"/> for the special accent File tab: it raises
/// <see cref="Ribbon.BackstageRequestedEvent"/> on click instead of selecting a band.</para>
/// </summary>
[ContentProperty(nameof(Groups))]
public class RibbonTab : TabItem
{
    /// <summary>Whether this is the special File tab (accent-filled; opens Backstage rather than selecting a band).</summary>
    public static readonly StyledProperty<bool> IsFileTabProperty =
        UIProperty.Register<RibbonTab, bool>(nameof(IsFileTab), defaultValue: false, changed: OnIsFileTabChanged);

    private readonly RibbonBand _band = new();

    static RibbonTab()
    {
        Control.ThemeProperty.OverrideDefaultValue<RibbonTab>(CursorialBarsTheme.RibbonTabStyle());
    }

    /// <summary>Creates a ribbon tab; its content is the internally-owned band that hosts <see cref="Groups"/>.</summary>
    public RibbonTab() => Content = _band;

    /// <inheritdoc cref="IsFileTabProperty"/>
    public bool IsFileTab { get => GetValue(IsFileTabProperty); set => SetValue(IsFileTabProperty, value); }

    /// <summary>The groups shown in this tab's band (the XAML content: <c>&lt;RibbonTab&gt;&lt;RibbonGroup/&gt;…</c>).</summary>
    public UIElementCollection Groups => _band.Children;

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        // The File tab is a command, not a selectable band: a click opens Backstage and does NOT change selection.
        if (IsFileTab && !e.Handled && e.Button == MouseButton.Left)
        {
            RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, this));
            e.Handled = true;
            return;
        }

        base.OnMouseDown(e);
    }

    /// <inheritdoc/>
    protected override void OnAccessKey(AccessKeyEventArgs e)
    {
        if (IsFileTab)
        {
            if (!e.IsMultiMatch)
                RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, this));
            return;
        }

        base.OnAccessKey(e);
    }

    private static void OnIsFileTabChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is RibbonTab tab)
            tab.PseudoClasses.Set(":ribbon-file", newValue);
    }
}
