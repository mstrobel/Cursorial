// ReSharper disable CheckNamespace

using Cursorial.Output;          // TextAttributes
using Cursorial.Rendering.Media; // BrushedStyle
using Cursorial.Text;            // TextWeight
using Cursorial.UI.Controls;     // TextElement (the axis properties)

namespace Cursorial.UI;

/// <summary>
/// PROTOTYPE (2-axis — TSF1). The "base" whole text style's contribution to the per-axis attached
/// properties, carried as a removable <see cref="ValueFrame"/> at <see cref="BindingPriority.Style"/> —
/// below an explicit per-axis <c>LocalValue</c>, above <c>Inherited</c>/<c>Default</c>. One structurally
/// stable entry per axis (weight, inverse for the prototype); <see cref="Apply"/> re-folds the carrier
/// and re-emits changed axes in place via <see cref="ValueFrame.OnEntryChanged"/> (never remove/re-add).
/// The store owns arbitration: an explicit per-axis value masks this frame, and clearing that value
/// re-promotes the frame's contribution automatically — the load-bearing property TSF1 pins.
/// </summary>
internal sealed class TextStyleAxisFrame : ValueFrame
{
    private readonly IValueEntry _weight  = TextElement.TextWeightProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _inverse = TextElement.InverseProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry[] _entries;

    internal TextStyleAxisFrame(in BrushedStyle style)
        // One frame per element, so within-Style ordering is irrelevant here — a neutral key.
        : base(StyleSortKey.Create(StyleLayer.App, names: 0, classLike: 0, types: 0, scopeDepth: 0, order: 0),
               isActive: true, BindingPriority.Style)
    {
        _entries = [_weight, _inverse];
        Fold(style); // seed BEFORE install — OnEntryChanged no-ops while Store is null
    }

    public override int EntryCount => _entries.Length;
    public override IValueEntry GetEntry(int index) => _entries[index];

    /// <summary>Re-fold the carrier onto the axis entries; each changed axis re-emits in place.</summary>
    internal void Apply(in BrushedStyle style) => Fold(style);

    private void Fold(in BrushedStyle style)
    {
        // Weight: the reconciled Weight axis (never the Bold/Faint masks — BrushedStyle already folds the
        // SGR-22 pair); null ⇒ leave a valueless entry so the axis falls through natively.
        if (style.Weight is { } w) Set(_weight, w);
        else                       Unset(_weight);

        // Inverse: Applied ⇒ force on, Removed ⇒ force off, in neither mask ⇒ valueless (falls through).
        if      (style.AppliedAttributes.HasFlag(TextAttributes.Inverse)) Set(_inverse, true);
        else if (style.RemovedAttributes.HasFlag(TextAttributes.Inverse)) Set(_inverse, false);
        else                                                              Unset(_inverse);
    }

    private void Set(IValueEntry entry, object value)
    {
        ((IStyleSetterEntry) entry).SetValueBoxed(value);
        OnEntryChanged(entry);
    }

    private void Unset(IValueEntry entry)
    {
        if (!entry.HasValue) return;
        ((IStyleSetterEntry) entry).Unset();
        OnEntryChanged(entry);
    }
}
