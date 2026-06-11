using System.Diagnostics;

namespace Cursorial.UI;

/// <summary>The kinds of layout diagnostics the tree/layout engine emits in DEBUG builds.</summary>
public enum LayoutDiagnosticKind
{
    /// <summary>A negative <c>Margin</c> component was coerced to 0 (v1 has no signed margins — doc §5.2).</summary>
    NegativeMarginCoerced,

    /// <summary>A <c>MeasureOverride</c> returned <see cref="LayoutMath.Unbounded"/>; the desired size was clamped to <see cref="LayoutMath.MaxExtent"/> (LD2).</summary>
    DesiredSizeUnbounded,

    /// <summary>An arrange position or extent exceeded <see cref="LayoutMath.MaxExtent"/> and was clamped before <c>Rect</c> construction (doc §5.2).</summary>
    ArrangeRectClamped,

    /// <summary>The layout pass hit the 16-pass cap with work still pending — a layout cycle (doc §5.3).</summary>
    LayoutCycle,

    /// <summary>Work was still pending after the bounded <c>LayoutUpdated</c> re-run; it slips to the next tick (doc §5.3).</summary>
    ResidualLayoutWork,

    /// <summary>A <c>ScrollContentPresenter</c>'s content desired more than <see cref="LayoutLimits.MaxScrollExtent"/>; the published extent was capped (doc §5.7; matrix L215 — one-time per presenter).</summary>
    ScrollExtentClamped,

    /// <summary>A push-stack-uncovered draw call straddled a banded scroll scene's top edge and was dropped — <c>K</c> sizes these edges outside the viewport clip (doc §5.7).</summary>
    BandStraddlingDrawDropped,
}

/// <summary>One emitted layout diagnostic: the kind, the element it names (when one is identifiable), and a message.</summary>
/// <param name="Kind">The diagnostic kind.</param>
/// <param name="Element">The element the diagnostic names, when one is identifiable.</param>
/// <param name="Message">A human-readable description.</param>
public readonly record struct LayoutDiagnosticEvent(LayoutDiagnosticKind Kind, UIElement? Element, string Message);

/// <summary>
/// Diagnostics hooks for the element tree and layout engine — the repo's DEBUG-diagnostic
/// convention (assert in DEBUG builds, silent in release). Emission sites compile away entirely in
/// release builds; the layout behavior they annotate (clamping, coercion, pass caps) is identical
/// in both configurations. Mirrors the shape of <see cref="UIDiagnostics"/>. The <b>primary</b>
/// channel is per-root (<see cref="LayoutManager.DiagnosticRaised"/>); the static event here is
/// the process-global fallback for diagnostics with no reachable manager (detached elements) and
/// for whole-process logging.
/// </summary>
public static class LayoutDiagnostics
{
    /// <summary>
    /// Raised (DEBUG builds only) when the layout engine emits a diagnostic — process-global, so
    /// parallel test collections cross-wire on it; prefer the per-root
    /// <see cref="LayoutManager.DiagnosticRaised"/> where a manager exists.
    /// </summary>
    public static event Action<LayoutDiagnosticEvent>? DiagnosticRaised;

    /// <summary>
    /// Raises the diagnostic on the element's <see cref="LayoutManager"/> (when attached) and on
    /// the static fallback; the call site compiles away in release builds.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void Emit(LayoutDiagnosticKind kind, UIElement? element, string message)
    {
        var diagnostic = new LayoutDiagnosticEvent(kind, element, message);
        element?.GetLayoutManager()?.RaiseDiagnostic(in diagnostic);
        DiagnosticRaised?.Invoke(diagnostic);
    }
}
