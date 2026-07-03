namespace Cursorial.UI.Bars.Input;

/// <summary>
/// An optional per-control override for KeyTip derivation (keytips-design §4). A control implementing this is
/// consulted first by <see cref="KeyTipModel"/> — before the explicit-key / access-key / command / first-letter
/// ladder — so a control can supply its own badge text and preferred anchor. Ships in v1 as the extension point.
/// </summary>
public interface IKeyTipProvider
{
    /// <summary>The badge text this control wants, or <see langword="null"/> to fall through to the ladder.</summary>
    string? ResolveKeyTip();

    /// <summary>Where the badge should sit relative to this control.</summary>
    KeyTipAnchor PreferredAnchor { get; }
}
