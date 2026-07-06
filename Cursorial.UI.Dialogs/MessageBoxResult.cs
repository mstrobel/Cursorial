using System.Threading;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The button a <see cref="MessageBox"/> was answered with. <see cref="None"/> means the box was
/// dismissed without a choice — the dialog was force-closed by a canceled
/// <see cref="CancellationToken"/> or closed through window chrome; callers should treat it as a
/// cancel.
/// </summary>
public enum MessageBoxResult
{
    /// <summary>Dismissed without a button choice (cancellation or chrome close) — treat as cancel.</summary>
    None = 0,

    /// <summary>The OK button.</summary>
    Ok,

    /// <summary>The Cancel button.</summary>
    Cancel,

    /// <summary>The Yes button.</summary>
    Yes,

    /// <summary>The No button.</summary>
    No,

    /// <summary>The Save button.</summary>
    Save,

    /// <summary>The "Don't Save" button.</summary>
    DontSave
}
