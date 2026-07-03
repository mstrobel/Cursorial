namespace Cursorial.UI.Bars.Input;

/// <summary>
/// One resolved KeyTip target within a <see cref="KeyTipLevel"/> (keytips-design §5): a badge letter over a
/// control, plus the closure that runs when it is chosen. A leaf (<see cref="KeyTipTargetKind.Activate"/>) carries
/// <see cref="Activate"/>; a drill carries <see cref="Reveal"/> (the reveal to perform) + <see cref="BuildNext"/>
/// (the next level, built after the reveal's relayout) and optionally <see cref="Retract"/> (undone on Esc-back).
/// Mutable <see cref="Badge"/>/<see cref="Hidden"/> hold the live view state during a level.
/// </summary>
public sealed class KeyTipEntry
{
    /// <summary>The control the badge sits over (positioned via <see cref="UIElement.TranslateToScreen"/>).</summary>
    public required UIElement Target { get; init; }

    /// <summary>The badge letters (uppercased; 1–2 chars in v1). Matched case-insensitively against typed input.</summary>
    public required string KeyTip { get; init; }

    /// <summary>What choosing this entry does.</summary>
    public required KeyTipTargetKind Kind { get; init; }

    /// <summary>Whether the derived <see cref="KeyTip"/> came from an explicit <c>KeyTip.Key</c> (never dropped on a
    /// collision) vs. auto-derivation (dropped when its letter collides — keytips-design §4 graft G4).</summary>
    public bool ExplicitKey { get; init; }

    /// <summary>The anchor preference for this entry's badge. Default <see cref="KeyTipAnchor.TopLeading"/> — a
    /// terminal-appropriate deviation from the design's desktop bottom-leading default: on a 1–2 cell bar control the
    /// top-leading corner keeps the badge on the control's first row (over a tab header, not down in the band).</summary>
    public KeyTipAnchor Anchor { get; init; } = KeyTipAnchor.TopLeading;

    /// <summary>Leaf activation (<see cref="KeyTipTargetKind.Activate"/> only).</summary>
    public Action? Activate { get; init; }

    /// <summary>The drill reveal — select a tab, open a dropdown/flyout (the <c>Drill*</c> kinds).</summary>
    public Action? Reveal { get; init; }

    /// <summary>Builds the level pushed after <see cref="Reveal"/> relayouts; may return <see langword="null"/> until
    /// the revealed subtree/surface exists (the controller retries next frame, bounded — keytips-design §9/Risk 3).</summary>
    public Func<KeyTipLevel?>? BuildNext { get; init; }

    /// <summary>Undoes <see cref="Reveal"/> when this drill's level is popped by Esc (collapse a floated band, close a
    /// dropdown). Null when the reveal is not reversible / needs no undo.</summary>
    public Action? Retract { get; init; }

    /// <summary>The live badge element for this entry within the current level (null before placement).</summary>
    public KeyTipBadge? Badge { get; set; }

    /// <summary>Whether this entry is currently filtered out by a typed prefix (its badge is collapsed).</summary>
    public bool Hidden { get; set; }
}
