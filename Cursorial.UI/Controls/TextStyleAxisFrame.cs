// ReSharper disable CheckNamespace

using System.Diagnostics;

using Cursorial.Output;          // TextAttributes, UnderlineStyle
using Cursorial.Rendering.Media; // BrushedStyle
using Cursorial.Text;            // TextWeight, TextStyle
using Cursorial.UI.Controls;     // TextElement (the axis properties)

namespace Cursorial.UI;

/// <summary>
/// The <c>BaseTextStyle</c> carrier's contribution to the per-axis text properties, as a removable
/// <see cref="ValueFrame"/> at <see cref="BindingPriority.BaseTextStyle"/> — one tier below the whole
/// style region (trigger/template/style), above Inherited/Default. One structurally-stable entry per
/// axis; <see cref="Apply"/> re-folds the <see cref="BrushedStyle"/> and re-emits changed axes in place
/// via <see cref="ValueFrame.OnEntryChanged"/> (never remove/re-add). The store owns arbitration: an
/// explicit per-axis value (LocalValue) or any style setter masks this, and clearing it re-promotes the
/// base automatically.
/// </summary>
internal sealed class TextStyleAxisFrame : ValueFrame
{
    // One entry per axis, structurally stable (valueless until Fold sets them). BrushedStyle's
    // Background / Hyperlink / Mode have no per-axis property and are out of scope; ToggledAttributes
    // are not honored (a toggle has no absolute base at the element tier — DEBUG heads-up below).
    private readonly IValueEntry _weight         = TextElement.TextWeightProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _posture        = TextElement.TextStyleProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _underline      = TextElement.UnderlineProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _underlineBrush = TextElement.UnderlineBrushProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _foreground     = TextElement.ForegroundProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _strikethrough  = TextElement.StrikethroughProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _overline       = TextElement.OverlineProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _inverse        = TextElement.InverseProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _blink          = TextElement.BlinkProperty.CreateStyleEntry(null, hasValue: false);
    private readonly IValueEntry _concealed      = TextElement.ConcealedProperty.CreateStyleEntry(null, hasValue: false);

    private readonly IValueEntry[] _entries;

    internal TextStyleAxisFrame(in BrushedStyle style)
        // Arbitrates in the BaseTextStyle lane; one frame per element, so its SortKey never competes (moot).
        : base(StyleSortKey.Create(StyleLayer.App, names: 0, classLike: 0, types: 0, scopeDepth: 0, order: 0),
               isActive: true, BindingPriority.BaseTextStyle)
    {
        _entries =
        [
            _weight, _posture, _underline, _underlineBrush, _foreground,
            _strikethrough, _overline, _inverse, _blink, _concealed,
        ];
        Fold(style); // seed BEFORE install — OnEntryChanged no-ops while Store is null
    }

    public override int EntryCount => _entries.Length;
    public override IValueEntry GetEntry(int index) => _entries[index];

    /// <summary>Re-fold the carrier onto the axis entries; each changed axis re-emits in place.</summary>
    internal void Apply(in BrushedStyle style) => Fold(style);

    private void Fold(in BrushedStyle style)
    {
        // Single-axis value channels — null ⇒ no opinion (valueless entry, the axis falls through).
        if (style.Weight  is { } w) Set(_weight,  w);  else Unset(_weight);
        if (style.Posture is { } p) Set(_posture, p);  else Unset(_posture);

        // Brush channels — the reconciled brush, or no opinion. (Foreground is a real per-axis brush;
        // BrushedStyle.UnderlineColor drives the UnderlineBrush axis.)
        if (style.Foreground     is { } fg) Set(_foreground, fg);     else Unset(_foreground);
        if (style.UnderlineColor is { } uc) Set(_underlineBrush, uc); else Unset(_underlineBrush);

        // Underline: presence + shape unified (the axis is UnderlineStyle?). Applied ⇒ on (the stated
        // shape, else Single); Removed ⇒ off (value-bearing null); a bare shape with the flag in neither
        // mask ⇒ on with that shape; otherwise no opinion.
        if (style.AppliedAttributes.HasFlag(TextAttributes.Underline))
            Set(_underline, style.UnderlineShape ?? UnderlineStyle.Single);
        else if (style.RemovedAttributes.HasFlag(TextAttributes.Underline))
            Set(_underline, null); // value-bearing null = force off
        else if (style.UnderlineShape is { } shape)
            Set(_underline, shape);
        else
            Unset(_underline);

        // The boolean axes: Applied ⇒ true, Removed ⇒ false, in neither mask ⇒ no opinion.
        FoldBool(_strikethrough, TextAttributes.Strikethrough, style);
        FoldBool(_overline,      TextAttributes.Overline,      style);
        FoldBool(_inverse,       TextAttributes.Inverse,       style);
        FoldBool(_blink,         TextAttributes.Blink,         style);
        FoldBool(_concealed,     TextAttributes.Hidden,        style); // the Hidden flag drives the Concealed axis

        WarnIgnoredToggles(style.ToggledAttributes);
    }

    private void FoldBool(IValueEntry entry, TextAttributes flag, in BrushedStyle style)
    {
        if (style.AppliedAttributes.HasFlag(flag)) Set(entry, true);
        else if (style.RemovedAttributes.HasFlag(flag)) Set(entry, false);
        else Unset(entry);
    }

    private void Set(IValueEntry entry, object? value)
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

    // A toggle is a compositor-time delta with no absolute base at the element tier, so the base style
    // cannot honor one; the toggled axes stay valueless (the Applied/Removed folds above leave them so).
    // A DEBUG heads-up in case an author meant "force on/off" and reached for a toggle by mistake.
    [Conditional("DEBUG")]
    private static void WarnIgnoredToggles(TextAttributes toggled)
    {
        if (toggled != 0)
            Debug.WriteLine(
                $"TextStyleAxisFrame ignores toggled attributes ({toggled}): a toggle has no absolute base " +
                "at the element tier — use Apply (force on) or Remove (force off) instead.");
    }
}
