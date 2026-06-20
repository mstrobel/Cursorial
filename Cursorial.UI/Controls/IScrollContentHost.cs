using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// Implemented by a <see cref="ScrollContentPresenter"/>'s content (or an element forwarding to its panel) so the
/// SCP <b>delegates</b> extent reporting + viewport hand-off instead of measuring all children (design doc §12.6 —
/// the virtualization seam). Cursorial keeps exactly ONE offset coordinate (the SCP's styled
/// <see cref="ScrollContentPresenter.ScrollOffsetRow"/>/<see cref="ScrollContentPresenter.ScrollOffsetColumn"/>);
/// this host only advertises the extent estimate + step size — the offset stays SCP-owned and storyboard-animatable
/// (the deliberate deviation from WPF <c>IScrollInfo</c>, which makes the panel own the offset). Cell-integer
/// throughout; internal — opt-in by type.
/// </summary>
internal interface IScrollContentHost
{
    /// <summary>When <see langword="false"/>, the SCP runs its verbatim legacy measure-at-<c>MaxScrollExtent</c>
    /// path (the OFF-path) — discovery is harmless for a non-virtualizing host.</summary>
    bool IsScrollClient { get; }

    /// <summary>The SCP injects itself once (adopt) / clears (disown); the host raises
    /// <see cref="ScrollContentPresenter.InvalidateScrollExtent"/> through it when its estimate refines (the
    /// back-channel).</summary>
    ScrollContentPresenter? ScrollOwner { get; set; }

    /// <summary>Whether the host scrolls horizontally — flows from the SCP's own axis enable.</summary>
    bool CanScrollHorizontally { get; set; }

    /// <summary>Whether the host scrolls vertically — flows from the SCP's own axis enable.</summary>
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

    /// <summary>The cell step for a line scroll (arrow/wheel) from <paramref name="currentOffset"/> in
    /// <paramref name="sign"/> direction (+1 down/right). Cells always — the SCP's offset stays one cell axis.</summary>
    int LineStep(int currentOffset, int sign, bool vertical);

    /// <summary>The cell step for a page scroll. Cells always.</summary>
    int PageStep(int currentOffset, int sign, bool vertical);

    /// <summary>Whether this host scrolls by whole items (diagnostics + which step source <c>ScrollViewer</c>
    /// consults); the offset stays cells regardless.</summary>
    bool IsLogicalScroll { get; }
}

/// <summary>
/// Adds whole-item navigation helpers that need the item↔cell mapping the panel owns (design doc §12.6).
/// Implemented by <c>VirtualizingStackPanel</c>; forwarded by <see cref="ItemsPresenter"/>.
/// </summary>
internal interface ILogicalScrollHost : IScrollContentHost
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
