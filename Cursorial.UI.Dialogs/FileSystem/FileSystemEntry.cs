using System;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// One row in a file dialog's listing: a directory, a file, a drive, or a special place. This is the
/// dialog's own view of the file system — deliberately NOT <see cref="System.IO.FileSystemInfo"/>, so an
/// <see cref="IFileSystemProvider"/> can serve entries from an in-memory tree, an archive, or a remote
/// source, and so headless tests can drive a whole browsing session without touching the disk.
/// </summary>
/// <remarks>
/// <para>
/// The entry is a snapshot. Nothing here re-stats the file system on access — a listing is captured by
/// <see cref="IFileSystemProvider.GetEntriesAsync"/> and refreshed wholesale, which is what makes the
/// listing stable while the user is arrowing through it.
/// </para>
/// <para>
/// <see cref="Type"/> is resolved lazily from <see cref="FileTypeIcons"/> and cached, because a details
/// listing reads the icon and the kind label once per row per render, and the lookup is a dictionary
/// probe over the name's extension.
/// </para>
/// </remarks>
public sealed class FileSystemEntry
{
    private FileTypeDescriptor? _type;

    /// <summary>Creates an entry.</summary>
    /// <param name="fullPath">The provider-rooted absolute path. Used for navigation and as the identity.</param>
    /// <param name="name">The display name (the last path segment, or a friendly name for a drive/place).</param>
    /// <param name="isDirectory">True when activating the entry navigates INTO it rather than choosing it.</param>
    public FileSystemEntry(string fullPath, string name, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(name);

        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
    }

    /// <summary>The provider-rooted absolute path — the entry's identity and navigation target.</summary>
    public string FullPath { get; }

    /// <summary>The display name: the last path segment, or a friendly name for a drive or special place.</summary>
    public string Name { get; }

    /// <summary>True when activating this entry navigates into it instead of choosing it.</summary>
    public bool IsDirectory { get; }

    /// <summary>The size in bytes, or <see langword="null"/> for directories and places (the Size column shows an em dash).</summary>
    public long? Size { get; init; }

    /// <summary>The last-write time, or <see langword="null"/> when the provider cannot report one.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>True for entries the platform marks hidden; the dialog filters these out unless "show hidden" is on.</summary>
    public bool IsHidden { get; init; }

    /// <summary>True when the entry could not be read (permission denied, a broken link, an offline share).</summary>
    /// <remarks>An inaccessible directory still lists — it simply fails when navigated into, which is a far
    /// better experience than silently omitting it and leaving the user unable to see why a path is missing.</remarks>
    public bool IsInaccessible { get; init; }

    /// <summary>
    /// The place classification, which selects the icon for drives, the home folder, the parent row, and the
    /// rest of the places rail. Ordinary rows leave this at <see cref="FileSystemPlace.Folder"/> and are
    /// classified by name/extension instead.
    /// </summary>
    public FileSystemPlace Place { get; init; } = FileSystemPlace.Folder;

    /// <summary>
    /// The icon + kind-label descriptor for this entry, resolved once and cached. Places resolve through
    /// <see cref="FileTypeIcons.ForPlace"/>; everything else through the name/extension table.
    /// </summary>
    public FileTypeDescriptor Type =>
        _type ??= Place is not FileSystemPlace.Folder
            ? FileTypeIcons.ForPlace(Place)
            : FileTypeIcons.ForEntry(Name, IsDirectory);

    /// <inheritdoc/>
    public override string ToString() => IsDirectory ? $"{Name}/" : Name;
}
