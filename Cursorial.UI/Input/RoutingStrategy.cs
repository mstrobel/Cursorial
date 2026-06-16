namespace Cursorial.UI.Input;

/// <summary>How a routed event traverses the element tree (design doc §7.2).</summary>
public enum RoutingStrategy : byte
{
    /// <summary>Raised on the target element only — no route walk (e.g. <c>MouseEnter</c>/<c>MouseLeave</c>).</summary>
    Direct,

    /// <summary>Raised target → surface root (with the logical hop at surface roots, doc §7.5).</summary>
    Bubble,

    /// <summary>Raised surface root → target (the <c>Preview*</c> phase).</summary>
    Tunnel
}
