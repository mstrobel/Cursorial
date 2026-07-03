using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// One destination in a <see cref="Backstage"/> (the guide's rail item — New, Open, Export, …). It derives from
/// <see cref="TabItem"/>, inheriting selection (<c>:selected</c> two-way, click/access-key/keyboard select),
/// <see cref="TabItem.IsSelectable"/> (set <see langword="false"/> for a non-selectable rail row — a
/// <c>[separator]</c> or section header), and access-key folding of the <see cref="HeaderedContentControl.Header"/>
/// (Alt+mnemonic jumps to the destination). Its <see cref="HeaderedContentControl.Header"/> is the rail label; its
/// <see cref="ContentControl.Content"/> is the detail pane shown (in the <see cref="Backstage"/>'s content host) when
/// the destination is selected — the rail row's own template renders only the Header (single-hosting, like a
/// <see cref="TabItem"/>). Only the skin differs from a tab (a rail row, not a strip tab), so the base behavior is
/// reused verbatim.
/// </summary>
public class BackstageItem : TabItem
{
    static BackstageItem()
    {
        Control.ThemeProperty.OverrideDefaultValue<BackstageItem>(CursorialBarsTheme.BackstageItemStyle());
    }
}
