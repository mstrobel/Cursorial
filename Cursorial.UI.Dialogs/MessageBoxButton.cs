using System;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The buttons a <see cref="MessageBox"/> shows, as combinable flags (implementation-plan M1.WP10).
/// Individual flags compose freely; the named combinations cover the classic archetypes plus the
/// FB-12 unsaved-changes save triad. Presentation order is fixed (affirmative → negative →
/// dismissive): <see cref="Ok"/>, <see cref="Yes"/>, <see cref="Save"/>, <see cref="No"/>,
/// <see cref="DontSave"/>, <see cref="Cancel"/>.
/// </summary>
[Flags]
public enum MessageBoxButton
{
    /// <summary>No buttons — invalid as a <see cref="MessageBox.ShowAsync"/> argument.</summary>
    None = 0,

    /// <summary>The affirmative OK button.</summary>
    Ok = 1 << 0,

    /// <summary>The dismissive Cancel button.</summary>
    Cancel = 1 << 1,

    /// <summary>The affirmative Yes button.</summary>
    Yes = 1 << 2,

    /// <summary>The negative No button.</summary>
    No = 1 << 3,

    /// <summary>The affirmative Save button (the save-triad accept).</summary>
    Save = 1 << 4,

    /// <summary>The negative "Don't Save" button (the save-triad discard).</summary>
    DontSave = 1 << 5,

    /// <summary>OK + Cancel.</summary>
    OkCancel = Ok | Cancel,

    /// <summary>Yes + No.</summary>
    YesNo = Yes | No,

    /// <summary>Yes + No + Cancel.</summary>
    YesNoCancel = Yes | No | Cancel,

    /// <summary>The FB-12 unsaved-changes triad: Save / Don't Save / Cancel.</summary>
    SaveDontSaveCancel = Save | DontSave | Cancel
}
