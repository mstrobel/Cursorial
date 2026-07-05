using System.Collections.Generic;

using Cursorial.Rendering.Text;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// One button of a <see cref="TaskDialogRequest"/> — either a well-known button (the shared statics)
/// or a custom command-link-style button with a caller-defined label and optional
/// <see cref="Explanation"/>. Records compare by value, so a caller can test
/// <c>result.Button == TaskDialogButton.Save</c> directly.
/// </summary>
/// <param name="Id">
/// The stable identifier the caller branches on (the well-known ids are <c>"ok"</c>, <c>"cancel"</c>,
/// <c>"yes"</c>, <c>"no"</c>, <c>"save"</c>, <c>"dontsave"</c>).
/// </param>
/// <param name="Label">
/// The button caption, in the framework access-key convention (<c>'_'</c> marks the mnemonic, e.g.
/// <c>"_Save"</c>; <c>"__"</c> is a literal underscore).
/// </param>
public sealed record TaskDialogButton(string Id, string Label)
{
    /// <summary>
    /// The command-link explanation line (FB-12). M6's <c>TaskDialog</c> renders buttons carrying one
    /// as command links; the M1 MessageBox-backed implementation drops it and renders the
    /// <see cref="Label"/> as a plain dialog button.
    /// </summary>
    public string? Explanation { get; init; }

    /// <summary>
    /// Indicates whether the <see cref="Explanation"/> property contains markup. If true, the content of
    /// the explanation is assumed to include <see cref="TextMarkup">formatting</see> tags. Uses of open
    /// brackets outside of tag names must be escaped as <c>\[</c>.
    /// </summary>
    public bool ExplanationContainsMarkup { get; init; }

    /// <summary>Whether Enter activates this button from anywhere in the dialog (first marked button wins).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Whether Esc activates this button (first marked button wins).</summary>
    public bool IsCancel { get; init; }

    /// <summary>The well-known OK button.</summary>
    public static TaskDialogButton Ok { get; } = new("ok", "_OK");

    /// <summary>The well-known Cancel button (Esc-activated).</summary>
    public static TaskDialogButton Cancel { get; } = new("cancel", "_Cancel") { IsCancel = true };

    /// <summary>The well-known Yes button.</summary>
    public static TaskDialogButton Yes { get; } = new("yes", "_Yes");

    /// <summary>The well-known No button.</summary>
    public static TaskDialogButton No { get; } = new("no", "_No");

    /// <summary>The well-known Save button (default-marked — the save triad's accept).</summary>
    public static TaskDialogButton Save { get; } = new("save", "_Save") { IsDefault = true };

    /// <summary>The well-known "Don't Save" button (the save triad's discard).</summary>
    public static TaskDialogButton DontSave { get; } = new("dontsave", "Do_n't Save");

    /// <summary>
    /// The FB-12 unsaved-changes triad — <see cref="Save"/> (default), <see cref="DontSave"/>,
    /// <see cref="Cancel"/> (Esc) — ready to assign to <see cref="TaskDialogRequest.Buttons"/>.
    /// </summary>
    public static IReadOnlyList<TaskDialogButton> SaveTriad { get; } = [Save, DontSave, Cancel];
}
