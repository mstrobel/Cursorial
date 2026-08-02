namespace Cursorial.UI.Controls;

/// <summary>
/// The <see cref="ListBox"/> inside a <see cref="CompletionPopup"/>: <see cref="CompletionEntry"/>
/// items, <see cref="CompletionListItem"/> containers, and a back-reference to the popup so a row
/// press can reach the accept funnel.
/// <para>
/// It has its OWN control theme rather than reusing <see cref="ListBox"/>'s, for two reasons. The
/// visual one: a <see cref="ListBox"/> paints a recessed <c>WellBrush</c> field with its own frame,
/// which is wrong inside a popup whose content border already owns the elevation and the chrome — the
/// completion list must be transparent and let the popup's surface show through. The mechanical one:
/// pointing <see cref="Control.ControlThemeKey"/> at <c>typeof(ListBox)</c> resolves the <em>key</em>
/// fine, but the declarative theme half authors its list rule as
/// <c>&lt;Style TargetType="ListBox"&gt;</c>, which does not apply to a subclass — so the XAML theme
/// would leave this list untemplated (no items host ⇒ no visible rows) while the code-first theme
/// worked, and the two halves would silently disagree.
/// </para>
/// <para>
/// The whole list is non-focusable: a completion popup is a hint overlay, and focus stays in the field
/// being completed.
/// </para>
/// </summary>
public class CompletionList : ListBox
{
    /// <summary>Creates a completion list (single selection, never focusable, never a tab stop).</summary>
    public CompletionList()
    {
        SelectionMode = SelectionMode.Single;
        Focusable = false;
        IsTabStop = false;
    }

    /// <summary>
    /// The popup that owns this list, or <see langword="null"/> when it is hosted standalone. Set by
    /// <see cref="CompletionPopup.OnApplyTemplate"/> and cleared on detach, so a row press can route
    /// its accept up out of the popup surface (the logical chain the surface root would otherwise cut).
    /// </summary>
    public CompletionPopup? Owner { get; internal set; }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new CompletionListItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is CompletionListItem;

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(UIElement container, object? item)
    {
        base.PrepareContainerForItemOverride(container, item); // Content = the entry (so {Binding} row templates work)

        if (container is not CompletionListItem row)
            return;

        // The flattened columns are pushed rather than bound because the entry is an immutable
        // snapshot: a filter pass builds a whole new entry list, so there is nothing for a binding to
        // track and a push costs one write per realized row instead of four live binding sites.
        if (item is CompletionEntry entry)
        {
            row.SetCurrentValue(CompletionListItem.DisplayMarkupProperty, entry.DisplayMarkup);
            row.SetCurrentValue(CompletionListItem.IconProperty, entry.Item.Icon);
            row.SetCurrentValue(CompletionListItem.KindLabelProperty, entry.KindLabel);
            row.SetCurrentValue(CompletionListItem.DescriptionProperty, entry.Item.Description);
        }
        else
        {
            // A raw item (a host driving the list directly rather than through CompletionPopup). It goes
            // through the same escape as an entry's display text: PART_Display consumes MARKUP, and an
            // unescaped '[' in someone's ToString() would fault the strict BBCode parser, not merely
            // render oddly.
            row.SetCurrentValue(CompletionListItem.DisplayMarkupProperty, CompletionEntry.EscapeMarkup(item?.ToString() ?? ""));
        }
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(UIElement container, object? item)
    {
        if (container is CompletionListItem row)
        {
            // Clear (not blank) so a recycled container cannot show the previous row's columns for the
            // frame between recycling and the next Prepare.
            row.ClearValue(CompletionListItem.DisplayMarkupProperty);
            row.ClearValue(CompletionListItem.IconProperty);
            row.ClearValue(CompletionListItem.KindLabelProperty);
            row.ClearValue(CompletionListItem.DescriptionProperty);
        }

        base.ClearContainerForItemOverride(container, item);
    }
}
