using System.Diagnostics;

using Cursorial.Output;
using Cursorial.Rendering.Media;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>The kinds of diagnostics the drawing layer emits in DEBUG builds.</summary>
public enum DrawingDiagnosticKind
{
    /// <summary>A tab character reached <see cref="DrawingContext.DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>;
    /// it was substituted with one space (the cell grid has no tab stops — expand tabs upstream).</summary>
    TabInText,

    /// <summary>A C0/C1 control character (other than a line break or tab) reached
    /// <see cref="DrawingContext.DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>;
    /// it was skipped (zero columns) rather than stored as a junk cell.</summary>
    ControlCharacterInText,

    /// <summary>Multi-line text reached a single-line slot (e.g. a <see cref="PanelTitle"/>); only
    /// the first line was used.</summary>
    MultiLineTextInSingleLineSlot,

    /// <summary>
    /// <see cref="SceneCompositor"/>'s replacing-blank marker — an intermediate-surface signal that must be
    /// translated by the pass writing the final target — was still on a <b>final</b> target after that pass.
    /// A compositor bug, not a caller error: some write path skipped the translation, or the base layer handed
    /// to the compositor is itself a group surface. Unlike the other kinds here the annotated behavior is
    /// <i>not</i> benign — the cell holds a Unicode noncharacter no font draws.
    /// </summary>
    ReplacingBlankReachedFinalTarget,
}

/// <summary>One emitted drawing diagnostic: the kind and a human-readable message.</summary>
/// <param name="Kind">The diagnostic kind.</param>
/// <param name="Message">A human-readable description.</param>
public readonly record struct DrawingDiagnosticEvent(DrawingDiagnosticKind Kind, string Message);

/// <summary>
/// Diagnostics hook for the drawing layer — the repo's DEBUG-diagnostic convention (mirrors
/// <c>Cursorial.UI</c>'s <c>LayoutDiagnostics</c> shape). Emission sites compile away entirely in
/// release builds; the drawing behavior they annotate (tab substitution, control-character skip,
/// first-line sanitization) is identical in both configurations.
/// </summary>
public static class DrawingDiagnostics
{
    /// <summary>
    /// Raised (DEBUG builds only) when the drawing layer emits a diagnostic. Process-global —
    /// parallel test collections cross-wire on it; treat it as a logging channel, not a unit seam.
    /// </summary>
    public static event Action<DrawingDiagnosticEvent>? DiagnosticRaised;

    /// <summary>Raises the diagnostic; the call site compiles away in release builds.</summary>
    [Conditional("DEBUG")]
    internal static void Emit(DrawingDiagnosticKind kind, string message)
        => DiagnosticRaised?.Invoke(new DrawingDiagnosticEvent(kind, message));
}
