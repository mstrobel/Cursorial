using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// Implemented by a <see cref="ScrollContentPresenter"/>'s content (or an element forwarding to its panel) so the
/// SCP <b>delegates</b> extent reporting + viewport hand-off instead of measuring all children (design doc §12.6 —
/// the virtualization seam). Cursorial keeps exactly ONE offset coordinate (the SCP's styled
/// <see cref="ScrollContentPresenter.ScrollOffsetRow"/>/<see cref="ScrollContentPresenter.ScrollOffsetColumn"/>);
/// this host only advertises the extent estimate + step size — the offset stays SCP-owned and storyboard-animatable
/// (the deliberate deviation from WPF <c>IScrollInfo</c>, which makes the panel own the offset). Cell-integer
/// throughout; <b>public</b> — a consumer can implement it on custom content to drive content-assisted scrolling
/// (e.g. whole-tile snapping), and the SCP delegates to any content that implements it (opt-in by type).
/// </summary>
public interface IScrollContentHost
{
    /// <summary>The master opt-in: when <see langword="true"/> the SCP delegates extent/viewport/steps to this host;
    /// when <see langword="false"/> the SCP ignores the host and runs its legacy path (measure the content at the
    /// full scroll extent), exactly as if the interface were not implemented. A consumer implementing this interface
    /// to drive scrolling returns <see langword="true"/>; the <see langword="false"/> state lets a forwarder
    /// (e.g. <c>ItemsPresenter</c>) advertise "no virtualizing panel right now" without the SCP null-checking.</summary>
    bool IsScrollClient { get; }

    /// <summary>The SCP injects itself once (adopt) / clears (disown); the host raises
    /// <see cref="ScrollContentPresenter.InvalidateScrollExtent"/> through it when its estimate refines (the
    /// back-channel). <b>Set by the SCP, not the consumer</b> — read it to observe adopt/disown (non-null ⇒
    /// adopted). A plain auto-property satisfies the contract.</summary>
    ScrollContentPresenter? ScrollOwner { get; set; }

    /// <summary>Whether the host scrolls horizontally. <b>Written by the SCP</b> (it pushes its own axis-enable into
    /// the host before each measure); a consumer exposes a settable backing field and reads it — setting it from
    /// outside the SCP has no effect (the SCP overwrites it next measure).</summary>
    bool CanScrollHorizontally { get; set; }

    /// <summary>Whether the host scrolls vertically. <b>Written by the SCP</b> — see
    /// <see cref="CanScrollHorizontally"/>.</summary>
    bool CanScrollVertically { get; set; }

    /// <summary>The total scrollable content in CELLS, estimated (realized-exact + estimated-unrealized). The SCP
    /// publishes this as its <see cref="ScrollContentPresenter.Extent"/> instead of <c>content.DesiredSize</c>.</summary>
    Size GetExtent();

    /// <summary>The SCP hands the host its arranged viewport (at the end of arrange, before the host's next measure
    /// realizes its band).</summary>
    void SetViewport(Size viewport);

    /// <summary>The SCP's band re-anchored (the realization window moved) — the host must re-realize its band on the
    /// next measure (an in-band composite slide does NOT call this, so invariant 3 holds for free; only a re-anchor
    /// does, at the re-anchor cadence). A non-virtualizing host ignores it.</summary>
    void InvalidateRealization();

    /// <summary>The line-scroll (arrow/wheel) step as an UNSIGNED cell <b>magnitude</b> from
    /// <paramref name="currentOffset"/>: the caller applies <paramref name="sign"/> (+1 = down/right) — return a
    /// positive distance, never a signed value (a value &lt; 1 is clamped to 1, so a signed return silently
    /// degrades to 1). <paramref name="sign"/> is supplied so the host can land on the boundary in that direction
    /// (e.g. up snaps to the previous unit's top once already at a boundary). Cells always — the SCP's offset stays
    /// one cell axis. Only consulted when <see cref="IsLogicalScroll"/> is <see langword="true"/>.</summary>
    int LineStep(int currentOffset, int sign, bool vertical);

    /// <summary>The page-scroll step as an UNSIGNED cell magnitude from <paramref name="currentOffset"/> — see
    /// <see cref="LineStep"/> for the sign/magnitude contract. Only consulted when <see cref="IsLogicalScroll"/> is
    /// <see langword="true"/>.</summary>
    int PageStep(int currentOffset, int sign, bool vertical);

    /// <summary>Whether this host scrolls by whole logical units (items/tiles). <b>Gates step delegation</b>: the
    /// <c>ScrollViewer</c> sources its keyboard line/page step from <see cref="LineStep"/>/<see cref="PageStep"/>
    /// only when this is <see langword="true"/> (and <see cref="IsScrollClient"/> is <see langword="true"/>);
    /// otherwise it uses the legacy fixed step (one cell / one viewport). The offset stays cells regardless.</summary>
    bool IsLogicalScroll { get; }
}

/// <summary>
/// Adds whole-item navigation helpers that need the item↔cell mapping the panel owns (design doc §12.6).
/// Implemented by <c>VirtualizingStackPanel</c>; forwarded by <see cref="ItemsPresenter"/>.
/// </summary>
public interface ILogicalScrollHost : IScrollContentHost
{
    /// <summary>Realize (if needed) + return the estimated cell content-rect of an item so the SCP/<c>ScrollViewer</c>
    /// can bring it into view (realization rides the panel's own next measure — §5.3 — so this returns the estimate
    /// immediately and pins the item; a corrective pass settles the exact position).</summary>
    Rect BringItemIntoView(int itemIndex);

    /// <summary>The logical item count (the extent numerator).</summary>
    int ItemCount { get; }

    /// <summary>The item whose top is at or just above a cell row (the inverse map — keyboard/thumb).</summary>
    int EstimateItemAt(int offsetRow);
}
