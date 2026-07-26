using System;
using System.Globalization;

using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// One row of a file dialog's listing, ready to bind: the underlying <see cref="FileSystemEntry"/> plus the
/// four already-rendered strings the design page's Details view shows (<c>Name</c>, right-aligned
/// <c>Size</c>, <c>Type</c>, <c>Modified</c>) and the row <see cref="Icon"/>.
/// <para>
/// <b>Why a row view-model rather than binding the entry directly.</b> A <see cref="Controls.ListViewColumn"/>
/// binds by property PATH, and the columns need <i>rendered</i> values — <c>1.4 MB</c>, not
/// <c>1_468_006</c>; <c>2026-06-01 11:20</c>, not a <see cref="DateTimeOffset"/>; <c>—</c> for a directory's
/// size, not an empty cell. The framework has no value-converter rung on <see cref="Controls.ListViewColumn"/>
/// (and adding one to bind four strings would be the wrong trade), so the projection lives here, where it is
/// also directly unit-testable without a Window. The strings are computed ONCE per row rather than per render
/// pass, which matters for a listing the user is arrowing through.
/// </para>
/// <para>
/// Equality is REFERENCE equality (a plain class, deliberately not a record): a listing may legitimately
/// contain two rows the same in every field — say two empty <c>.gitkeep</c> files reached through different
/// symlinked directories — and the selection model must be able to tell them apart.
/// </para>
/// </summary>
public sealed class FileDialogEntry
{
    /// <summary>The em dash the Size column shows for a directory or a place (the design page's <c>—</c>).</summary>
    private const string NoSize = "—";

    /// <summary>Creates a row for <paramref name="entry"/>.</summary>
    /// <param name="entry">The provider's snapshot of the file-system entry.</param>
    public FileDialogEntry(FileSystemEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;

        // Resolved once, not per read: a details row reads the icon on every cell build, and handing out a
        // fresh IconCarrier each time would both allocate per render and defeat the binding's own equality
        // check, re-stamping an identical icon on every pass.
        Icon = entry.Type.ToIconCarrier();

        SizeText = entry.IsDirectory || entry.Size is null ? NoSize : FormatSize(entry.Size.Value);
        ModifiedText = entry.LastModified is { } modified
            ? modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>The provider's snapshot this row presents.</summary>
    public FileSystemEntry Entry { get; }

    /// <summary>The display name — the Name column's text and the type-ahead match text.</summary>
    public string Name => Entry.Name;

    /// <summary>The provider-rooted absolute path — the navigation target and the row's identity.</summary>
    public string FullPath => Entry.FullPath;

    /// <summary>Whether activating the row navigates INTO it rather than choosing it.</summary>
    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>The capability-tiered row icon (Nerd Font → emoji → Unicode), resolved from
    /// <see cref="FileTypeIcons"/> through <see cref="FileSystemEntry.Type"/>.</summary>
    public IconCarrier Icon { get; }

    /// <summary>The <i>Type</i> column's text — <c>"PNG image"</c>, <c>"Folder"</c>, <c>"Shell script"</c>.</summary>
    public string TypeText => Entry.Type.KindLabel;

    /// <summary>The <i>Size</i> column's text — <c>"1.4 MB"</c>, <c>"22 KB"</c>, <c>"0 B"</c>, or <c>"—"</c>
    /// for a directory.</summary>
    public string SizeText { get; }

    /// <summary>The <i>Modified</i> column's text (<c>yyyy-MM-dd HH:mm</c> in local time), or empty when the
    /// provider reports no timestamp.</summary>
    public string ModifiedText { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;

    // ───────────────────────────── size rendering ─────────────────────────────
    //
    // The design page's own column, pinned exactly: bytes below 1 KiB ("0 B"), whole kibibytes up to 1 MiB
    // ("22 KB", "240 KB", "920 KB"), then ONE decimal from mebibytes up ("1.4 MB", "1.1 MB"). Binary units with
    // decimal-looking labels is what every file manager does; being clever here would only make the column
    // disagree with the rest of the user's desktop. InvariantCulture on purpose — the column is aligned by
    // character count, and a locale that groups with a space would break the alignment the Right alignment buys.
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");

        double value = bytes;
        var unit = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 1
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value):F0} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {units[unit]}");
    }
}
