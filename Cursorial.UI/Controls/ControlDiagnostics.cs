using System.Diagnostics;

namespace Cursorial.UI.Controls;

/// <summary>The kinds of control-infrastructure diagnostics S8 emits in DEBUG builds.</summary>
public enum ControlDiagnosticKind
{
    /// <summary>A <see cref="Control"/> measured with a <see langword="null"/> <c>Template</c> — no visual child (doc §12.2 step 2, C135).</summary>
    NoTemplate,

    /// <summary>A <see cref="Control"/>'s <c>ControlThemeKey</c> resolved no control theme in the chain (CD13 — a one-time miss).</summary>
    ControlThemeMiss,

    /// <summary>A <see cref="ContentPresenter"/> recursion guard fired — a self-referential content/template (doc §12.3, C147).</summary>
    ContentRecursion,

    /// <summary>A <see cref="ScrollViewer"/> requested horizontal <c>Auto</c>, which v1 treats as <c>Disabled</c> (the horizontal axis is unbanded — doc §5.7/CD28).</summary>
    HorizontalAutoUnsupported
}

/// <summary>One emitted control diagnostic.</summary>
/// <param name="Kind">The diagnostic kind.</param>
/// <param name="Element">The element the diagnostic names.</param>
/// <param name="Message">A human-readable description.</param>
public readonly record struct ControlDiagnosticEvent(ControlDiagnosticKind Kind, UIElement? Element, string Message);

/// <summary>
/// DEBUG-only diagnostics for S8 control infrastructure (the repo's diagnostic convention: emit in
/// DEBUG, compile away in release; behavior identical in both). Process-global, mirroring
/// <see cref="LayoutDiagnostics"/>.
/// </summary>
public static class ControlDiagnostics
{
    /// <summary>Raised (DEBUG only) when S8 emits a control diagnostic.</summary>
    public static event Action<ControlDiagnosticEvent>? DiagnosticRaised;

    [Conditional("DEBUG")]
    internal static void NoTemplate(UIElement element)
        => Emit(ControlDiagnosticKind.NoTemplate, element,
                $"Control '{element.GetType().Name}' measured with a null Template — no visual child (doc §12.2).");

    [Conditional("DEBUG")]
    internal static void ControlThemeMiss(UIElement element, object key)
        => Emit(ControlDiagnosticKind.ControlThemeMiss, element,
                $"No control theme found for key '{key}' in the chain of '{element.GetType().Name}' (CD13).");

    [Conditional("DEBUG")]
    internal static void ContentRecursion(UIElement element)
        => Emit(ControlDiagnosticKind.ContentRecursion, element,
                $"ContentPresenter '{element.GetType().Name}' recursion guard fired — self-referential content (doc §12.3).");

    [Conditional("DEBUG")]
    internal static void HorizontalAutoUnsupported(UIElement element)
        => Emit(ControlDiagnosticKind.HorizontalAutoUnsupported, element,
                $"ScrollViewer '{element.GetType().Name}': HorizontalScrollBarVisibility.Auto acts as Disabled in v1 — "
                + "the horizontal axis is unbanded (doc §5.7). Use Hidden to allow wheel/key scrolling, or Visible.");

    [Conditional("DEBUG")]
    private static void Emit(ControlDiagnosticKind kind, UIElement? element, string message)
        => DiagnosticRaised?.Invoke(new ControlDiagnosticEvent(kind, element, message));
}
