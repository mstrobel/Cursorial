using System.Text;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// One level of the KeyTip drill stack (keytips-design §5): the badge entries shown together, plus the matched-prefix
/// buffer for this level. A pushed level may carry a <see cref="Retract"/> that undoes the drill that created it
/// (collapse a floated band, close a dropdown) when the level is popped by Esc.
/// </summary>
public sealed class KeyTipLevel
{
    /// <summary>The entries (badges) shown at this level, in document order.</summary>
    public required IReadOnlyList<KeyTipEntry> Entries { get; init; }

    /// <summary>The case-folded prefix typed so far at this level (drives the matched-prefix filter).</summary>
    public StringBuilder Typed { get; } = new();

    /// <summary>Undoes the drill that opened this level (run on Esc-pop). Null for level 0 / irreversible reveals.
    /// Settable so the controller can attach the committing drill's retract to the level built by its parked builder.</summary>
    public Action? Retract { get; set; }

    /// <summary>The SURVIVING KeyTip letter for <paramref name="target"/> in this level (post collision-resolution +
    /// eligibility filter), or <see langword="null"/> when it has no badge here — dropped by a collision, or filtered
    /// as ineligible/hidden. Lets <see cref="KeyTip.GetHopSequence"/> report a hop that exactly matches the badges.</summary>
    internal string? KeyTipFor(UIElement target)
    {
        foreach (var entry in Entries)
        {
            if (ReferenceEquals(entry.Target, target))
                return entry.KeyTip;
        }

        return null;
    }
}
