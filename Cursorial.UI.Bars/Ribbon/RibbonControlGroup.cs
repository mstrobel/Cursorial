using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// Groups a handful of related bar controls INSIDE a <see cref="RibbonGroup"/> and stacks them into the band's rows —
/// the construct that stops a 2-row band (forced tall by a <see cref="RibbonButtonSize.Large"/> hero button) from
/// wasting its second row: three small commands render as a 2×2 block beside the hero instead of a centered 1-row
/// strip. It is atomic in the group's horizontal run; its children are the SAME bar controls a group hosts directly
/// (no wrappers).
/// </summary>
/// <remarks>
/// <para>
/// <b>Packing.</b> With two rows available the children split into two rows at the contiguous definition-order point
/// that minimizes the wider row (the Actipro two-row rule): wide children (combos) naturally claim a row of their own,
/// small buttons pair up. <see cref="RowBreakProperty"/> pins the split when the width heuristic guesses wrong (the
/// Font-group pattern: combos on row one, toggles on row two). With one row available (per
/// <see cref="RowBehaviorProperty"/> and the band's authored height) children lay out flat in definition order.
/// </para>
/// <para>
/// <b>Sizing.</b> <see cref="ItemSizeProperty"/> (default <see cref="RibbonButtonSize.Medium"/>) is stamped as the
/// group's inherited <see cref="Ribbon.ButtonSizeProperty"/>, so children wear their 1-row faces without per-child
/// authoring; a child's own authored size still wins. <see cref="RibbonButtonSize.Large"/> children are unsupported
/// inside a control group (a 2-row face cannot stack in a 2-row band) — they clamp to their row.
/// Density is orthogonal and untouched: <c>:density-compact</c> drops labels within the stack (denser columns, same
/// rows), and the group's Collapsed flyout move carries this control group whole.
/// </para>
/// <para>
/// <b>Nesting is unsupported</b>: a control group inside a control group packs as one opaque child and its
/// commands lose their KeyTips (the flatten is one level, matching the packer) — compose sibling control groups
/// instead.
/// </para>
/// <para>
/// Like <see cref="RibbonGroup"/> itself, a control group is transparent to keyboard navigation — no focus scope, no
/// tab stop, no directional container — so arrows flow through the stack geometrically (up/down within a column,
/// left/right across) via the band's single directional plane.
/// </para>
/// </remarks>
public class RibbonControlGroup : ItemsControl
{
    static RibbonControlGroup()
    {
        // A RowBreak flipped at runtime must re-pack the hosting panel (audit F1: with no effect registration the
        // pin only took hold when something else invalidated the panel). Registered here — the property's declaring
        // type — per the global-effects-lane freeze rule.
        AffectsParentMeasure<UIElement>(RowBreakProperty);
    }

    /// <summary>How this control group decides its row count (see <see cref="RibbonControlGroupRowBehavior"/>).</summary>
    public static readonly StyledProperty<RibbonControlGroupRowBehavior> RowBehaviorProperty =
        UIProperty.Register<RibbonControlGroup, RibbonControlGroupRowBehavior>(
            nameof(RowBehavior), defaultValue: RibbonControlGroupRowBehavior.Auto, changed: OnLayoutInputChanged);

    /// <summary>
    /// The face size stamped for this group's children (via the inherited <see cref="Ribbon.ButtonSizeProperty"/> —
    /// a child's own authored size wins). Default <see cref="RibbonButtonSize.Medium"/>: the guide's labeled stacked
    /// row (<c>[icon] Cut</c>); set <see cref="RibbonButtonSize.Small"/> for icon-only columns.
    /// </summary>
    public static readonly StyledProperty<RibbonButtonSize> ItemSizeProperty =
        UIProperty.Register<RibbonControlGroup, RibbonButtonSize>(
            nameof(ItemSize), defaultValue: RibbonButtonSize.Medium, changed: OnItemSizeChanged);

    /// <summary>
    /// Attached to a child: pins the two-row split point — this child starts the SECOND row, overriding the
    /// min-width packing heuristic. Only the first marked child takes effect; ignored in single-row layouts.
    /// </summary>
    public static readonly AttachedProperty<bool> RowBreakProperty =
        UIProperty.RegisterAttached<RibbonControlGroup, UIElement, bool>("RowBreak", defaultValue: false);

    /// <summary>Creates a ribbon control group hosting bar controls.</summary>
    public RibbonControlGroup()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ => new RibbonControlGroupPanel());
        // The stamp children inherit; an explicit SetValue so the value shadows a Large default inherited from the
        // OWNING group — a stack's children must wear 1-row faces (the whole point of stacking).
        SetValue(Ribbon.ButtonSizeProperty, ItemSize);
    }

    /// <summary>Control themes resolve exact-key; the plain <see cref="ItemsControl"/> theme (a bare items presenter)
    /// is exactly what a control group needs — its look is its children's (the GalleryRibbon opt-in precedent).</summary>
    protected override object ControlThemeKey => typeof(ItemsControl);

    private RibbonControlGroupPanel? _panel;

    // The items-host panel registers itself so the analytic density estimates can run the live packer over
    // hypothetical widths (the RibbonGroup.RegisterGroupPanel precedent, minus the flyout hosting).
    internal void RegisterPanel(RibbonControlGroupPanel? panel) => _panel = panel;

    /// <summary>
    /// The analytic width this control group would pack to under <c>:density-compact</c> (each icon-bearing child at
    /// its icon-only face) — the owning group's Compact estimate recurses here so the band's fold sees STACK-aware
    /// savings (both rows narrow together; a naive per-child sum would double-count). Same packer, hypothetical
    /// widths; errs high like every fold estimate.
    /// </summary>
    internal int EstimateCompactWidth()
        => _panel?.EstimateWidth(static child => RibbonGroup.CompactWidthOf(child)) ?? DesiredSize.Columns;

    /// <inheritdoc cref="RowBehaviorProperty"/>
    public RibbonControlGroupRowBehavior RowBehavior
    {
        get => GetValue(RowBehaviorProperty);
        set => SetValue(RowBehaviorProperty, value);
    }

    /// <inheritdoc cref="ItemSizeProperty"/>
    public RibbonButtonSize ItemSize { get => GetValue(ItemSizeProperty); set => SetValue(ItemSizeProperty, value); }

    /// <inheritdoc cref="RowBreakProperty"/>
    public static void SetRowBreak(UIElement element, bool value) => element.SetValue(RowBreakProperty, value);

    /// <inheritdoc cref="RowBreakProperty"/>
    public static bool GetRowBreak(UIElement element) => element.GetValue(RowBreakProperty);

    private static void OnItemSizeChanged(UIObject sender, RibbonButtonSize oldValue, RibbonButtonSize newValue)
    {
        if (sender is RibbonControlGroup group)
        {
            group.SetValue(Ribbon.ButtonSizeProperty, newValue);
            group.InvalidateMeasure();
        }
    }

    private static void OnLayoutInputChanged(UIObject sender, RibbonControlGroupRowBehavior oldValue, RibbonControlGroupRowBehavior newValue)
    {
        if (sender is not RibbonControlGroup group)
            return;

        group.InvalidateMeasure();
        // A TwoRow pin changes the BAND's authored height, which every sibling's stacking reads — walk up so the
        // band recomputes (the panel's parent chain re-measures anyway; the band's own measure re-derives the rows).
        (group.VisualParent as UIElement)?.InvalidateMeasure();
    }
}
