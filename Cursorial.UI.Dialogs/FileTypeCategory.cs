namespace Cursorial.UI.Dialogs;

/// <summary>
/// The broad family a file-system entry belongs to — the unit at which the <b>emoji</b>, <b>Unicode</b> and
/// <b>ASCII</b> tiers of <see cref="FileTypeIcons"/> are authored (the design doc's file-dialog page,
/// "Icon pass — emoji glyphs": <i>"Emoji read instantly as broad categories but cannot distinguish, say,
/// .png from .jpg — both are 🖼️"</i>). Per-<i>extension</i> specificity is the Nerd Font tier's job and lives
/// on the individual <see cref="FileTypeDescriptor"/>; everything below that tier degrades to the category.
/// <para>
/// Categories are also useful to consumers beyond icons — a file dialog can group, filter or colorize by
/// category — which is why <see cref="FileTypeDescriptor.Category"/> is part of the public shape rather than
/// an implementation detail of the table.
/// </para>
/// </summary>
public enum FileTypeCategory
{
    /// <summary>An unrecognized file — the table's default (kind label "File").</summary>
    Unknown = 0,

    /// <summary>A directory.</summary>
    Folder,

    /// <summary>A volume: a fixed, removable or network drive, or the machine itself.</summary>
    Drive,

    /// <summary>A special place in the navigation rail (Home, Desktop, Downloads, Recent, Favorites, Trash, …).</summary>
    Place,

    /// <summary>Human-readable prose: plain text, Markdown, README/LICENSE/CHANGELOG.</summary>
    Text,

    /// <summary>A rich document: PDF, Word, Excel, PowerPoint, RTF.</summary>
    Document,

    /// <summary>Program source or a build script.</summary>
    Source,

    /// <summary>Markup and stylesheets: HTML, XML, XAML, CSS.</summary>
    Markup,

    /// <summary>Structured data and configuration: JSON, YAML, TOML, INI, CSV, databases.</summary>
    Data,

    /// <summary>A raster image.</summary>
    Image,

    /// <summary>Vector art and design assets: SVG, swatches, Photoshop/Illustrator documents.</summary>
    Vector,

    /// <summary>An audio file.</summary>
    Audio,

    /// <summary>A video file.</summary>
    Video,

    /// <summary>A compressed archive or tarball.</summary>
    Archive,

    /// <summary>An executable program or installer package.</summary>
    Executable,

    /// <summary>A dynamic/shared library or other linkable binary.</summary>
    Library,

    /// <summary>A font file.</summary>
    Font
}
