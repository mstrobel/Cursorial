namespace Cursorial.UI.Dialogs;

/// <summary>
/// The severity of a <see cref="TaskDialogRequest"/>, presented as the dialog's icon (FB-12: severity
/// glyph resolved through the framework Icon ladder). The M1 MessageBox-backed implementation ignores
/// it; M6's <c>TaskDialog</c> renders it.
/// </summary>
public enum TaskDialogSeverity
{
    /// <summary>No severity icon.</summary>
    None = 0,

    /// <summary>Informational.</summary>
    Information,

    /// <summary>A question / confirmation.</summary>
    Question,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error.</summary>
    Error,

    /// <summary>A success indicator.</summary>
    Success
}
