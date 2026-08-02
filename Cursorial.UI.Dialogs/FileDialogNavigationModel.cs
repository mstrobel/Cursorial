using System.Collections.Generic;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// Which column a file dialog's listing is ordered by. Distinct from a bare column reference because the
/// ordering must survive a view switch — the design page's <c>▥</c> Small Icons view has no header strip at
/// all, yet "Folders still group ahead of files" and the name order still holds — and because a persisted
/// sort is restored before any <see cref="Controls.ListViewColumn"/> exists to point at.
/// </summary>
public enum FileDialogSortKey
{
    /// <summary>By display name, ordinal-case-insensitive (the default, the design page's <c>Name ▲</c>).</summary>
    Name = 0,

    /// <summary>By byte size; directories (which have none) keep name order among themselves.</summary>
    Size,

    /// <summary>By the <i>Type</i> column's kind label, then by name inside a kind.</summary>
    Type,

    /// <summary>By last-write time; entries with no timestamp sort last.</summary>
    Modified
}

/// <summary>
/// One chip of the dialog's <see cref="Controls.BreadcrumbBar"/>: the display name of a path segment and the
/// absolute path activating it navigates to. The bar is deliberately path-agnostic — it "knows nothing about
/// file systems, paths, or separators in strings" — so this is the dialog's own model, produced by walking
/// <see cref="IFileSystemProvider.GetParentPath"/> up from the current directory.
/// </summary>
/// <param name="Name">The chip's caption — the last path component, or a place's friendly name at a
/// well-known location (the design page's <c>▣ Home</c> rather than <c>/Users/ada</c>).</param>
/// <param name="Path">The absolute path this chip navigates to.</param>
public sealed record FileDialogPathSegment(string Name, string Path)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// One band of the places rail — the design page's <c>Quick access</c> / <c>This PC</c> / <c>Network</c>
/// grouping. A plain grouping record rather than a control concept: the rail is composed as a header
/// <see cref="Controls.TextBlock"/> followed by one activatable row per place, which is what lets the whole
/// rail be a single tab stop with arrow navigation inside (ND16) while the headers stay unfocusable.
/// </summary>
/// <param name="Header">The band's caption, e.g. <c>"Quick access"</c>.</param>
/// <param name="Places">The band's rows, in display order. A band with none is not shown at all.</param>
public sealed record FileDialogPlaceGroup(string Header, IReadOnlyList<FileSystemEntry> Places);
