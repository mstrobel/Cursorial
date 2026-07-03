namespace Cursorial.UI.Data;

/// <summary>
/// When a two-way (or one-way-to-source) binding flushes a target edit back to the source.
/// <see cref="Default"/> resolves to <see cref="PropertyChanged"/> — a deliberate divergence from
/// WPF's per-property <c>LostFocus</c> default (design doc §6.11.2): a terminal keystroke source
/// update is a few struct copies and one store write, and live <c>When</c> conditions are the
/// showcase behavior.
/// </summary>
public enum UpdateSourceTrigger : byte
{
    /// <summary>Resolves to <see cref="PropertyChanged"/>.</summary>
    Default,

    /// <summary>Flush on every target effective-value change.</summary>
    PropertyChanged,

    /// <summary>
    /// Flush when physical focus leaves the element (S3's routed <c>LostFocusEvent</c>) or on the
    /// terminal-focus-out edit-commit pulse (<c>InputDispatcher.EditCommitRequested</c>); keyboard
    /// focus is retained on terminal focus-out (design doc §6.11.6).
    /// </summary>
    LostFocus,

    /// <summary>Flush only when <c>BindingExpressionBase.UpdateSource()</c> is called.</summary>
    Explicit
}
