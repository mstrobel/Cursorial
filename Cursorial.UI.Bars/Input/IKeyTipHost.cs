namespace Cursorial.UI.Bars.Input;

/// <summary>
/// A KeyTip surface family adapter (keytips-design §6): one small internal implementation per bar surface (Ribbon,
/// Toolbar, Menu). The controller discovers hosts by walking the active surface roots (a Ribbon/Toolbar/Menu found
/// in the tree is wrapped), then asks each for its level-0 entries. Drill entries carry their own next-level
/// builders as closures, so the one FSM drives ribbon tab→group→control and (single-level) toolbar/menu uniformly.
/// </summary>
public interface IKeyTipHost
{
    /// <summary>The surface element this host badges (its subtree is not re-scanned for nested hosts).</summary>
    UIElement SurfaceElement { get; }

    /// <summary>Contributes this host's top-level KeyTip entries (badges + drill closures) to <paramref name="into"/>.</summary>
    void BuildRootLevel(KeyTipLevelBuilder into);
}
