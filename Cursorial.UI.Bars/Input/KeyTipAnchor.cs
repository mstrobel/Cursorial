namespace Cursorial.UI.Bars.Input;

/// <summary>
/// Where a KeyTip badge sits relative to its target (keytips-design §4/§8). v1 uses <see cref="BottomLeading"/>
/// for every target (the guide's "1–2 cell overlay at the end of its command", clamped into the viewport); the
/// enum is the extension point for a per-control <see cref="IKeyTipProvider.PreferredAnchor"/> override.
/// </summary>
public enum KeyTipAnchor
{
    /// <summary>The target's bottom-leading corner — the v1 default.</summary>
    BottomLeading,

    /// <summary>The target's top-leading corner.</summary>
    TopLeading,

    /// <summary>Centered on the target's bottom edge.</summary>
    BottomCenter,
}
