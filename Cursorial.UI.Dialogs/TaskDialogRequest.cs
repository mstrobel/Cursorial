using System.Collections.Generic;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// A task-dialog request in the agreed FB-12 shape (architecture §3.2 resolution 3): main instruction,
/// content, severity icon, buttons (including command-link-style custom labels), an optional
/// verification checkbox, and optional expandable details. The <b>shape</b> is the
/// <see cref="ITaskDialogService"/> contract from M1 on — fields the M1 MessageBox-backed
/// implementation does not render (severity, verification, expanded information, command-link
/// explanations) are carried unchanged so M3/M4 callers need no change when M6's full
/// <c>TaskDialog</c> takes over the service.
/// </summary>
/// <param name="MainInstruction">The dialog's main instruction — the one-line question or statement.</param>
public sealed record TaskDialogRequest(string MainInstruction)
{
    /// <summary>The window title (empty when null).</summary>
    public string? Title { get; init; }

    /// <summary>The supporting content below the main instruction (word-wrapped; optional).</summary>
    public string? Content { get; init; }

    /// <summary>The severity icon (default <see cref="TaskDialogSeverity.None"/>; ignored by the M1 implementation).</summary>
    public TaskDialogSeverity Severity { get; init; } = TaskDialogSeverity.None;

    /// <summary>
    /// The buttons, in presentation order. Empty means a lone <see cref="TaskDialogButton.Ok"/>.
    /// The first <see cref="TaskDialogButton.IsDefault"/> button is Enter-activated (else the first
    /// button); the first <see cref="TaskDialogButton.IsCancel"/> button is Esc-activated.
    /// </summary>
    public IReadOnlyList<TaskDialogButton> Buttons { get; init; } = [];

    /// <summary>
    /// The verification-checkbox label (e.g. "Don't ask me again"); null hides the checkbox.
    /// Ignored by the M1 implementation — see <see cref="MessageBoxTaskDialogService"/>.
    /// </summary>
    public string? VerificationText { get; init; }

    /// <summary>The verification checkbox's initial state.</summary>
    public bool VerificationChecked { get; init; }

    /// <summary>
    /// Expandable details (e.g. a recovery journal's timestamp block); null hides the expander.
    /// Ignored by the M1 implementation — see <see cref="MessageBoxTaskDialogService"/>.
    /// </summary>
    public string? ExpandedInformation { get; init; }
}
