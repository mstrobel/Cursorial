namespace Cursorial.UI.Dialogs;

/// <summary>
/// A file-system location that is <b>not</b> identified by a file name — the rows of the Open/Save dialog's
/// places rail (design doc, "Open File dialog — places rail, breadcrumb, sortable details": Quick access /
/// This PC / Network, with pinned ★, drive ▤ and cloud ☁ glyphs), plus the two entries every listing shows:
/// an ordinary <see cref="Folder"/> and the <see cref="ParentFolder"/> ("..") row.
/// <para>
/// These are looked up with <see cref="FileTypeIcons.ForPlace"/> rather than by extension, and — unlike the
/// extension table, whose lower tiers come from <see cref="FileTypeCategory"/> — each place authors all four
/// tiers explicitly, because the Unicode floor is where the rail carries most of its meaning on a plain
/// terminal (▣ home · ★ pinned · ▤ drive · ☁ cloud).
/// </para>
/// </summary>
public enum FileSystemPlace
{
    /// <summary>An ordinary directory (the listing's folder rows; kind label "Folder").</summary>
    Folder = 0,

    /// <summary>The ".." row that navigates to the containing directory.</summary>
    ParentFolder,

    /// <summary>The current user's home directory.</summary>
    Home,

    /// <summary>The desktop folder.</summary>
    Desktop,

    /// <summary>The documents folder.</summary>
    Documents,

    /// <summary>The downloads folder.</summary>
    Downloads,

    /// <summary>The pictures folder.</summary>
    Pictures,

    /// <summary>The music folder.</summary>
    Music,

    /// <summary>The videos folder.</summary>
    Videos,

    /// <summary>The "Recent" quick-access list.</summary>
    Recent,

    /// <summary>A pinned / favorite location (the rail's ★ rows).</summary>
    Favorites,

    /// <summary>The trash / recycle bin.</summary>
    Trash,

    /// <summary>The machine itself — the rail's "This PC" root.</summary>
    Computer,

    /// <summary>A fixed local volume.</summary>
    Drive,

    /// <summary>A removable volume (USB stick, optical disc, SD card).</summary>
    RemovableDrive,

    /// <summary>A network share or mapped network volume.</summary>
    NetworkDrive,

    /// <summary>A cloud-backed location (OneDrive, Dropbox, iCloud, …).</summary>
    CloudDrive
}
