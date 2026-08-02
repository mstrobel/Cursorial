using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// ReSharper disable once CheckNamespace

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The default <see cref="IFileSystemProvider"/>: the real disk, via <see cref="System.IO"/>.
/// </summary>
/// <remarks>
/// Enumeration and directory creation run on the thread pool (<see cref="Task.Run(Action)"/>) rather than
/// inline, because a listing of a slow or dead network share on the UI thread would stall the frame loop and
/// wedge the terminal. Everything else here is a cheap in-process call.
/// </remarks>
public sealed class PhysicalFileSystemProvider : IFileSystemProvider
{
    /// <summary>A shared instance; the provider holds no mutable state.</summary>
    public static PhysicalFileSystemProvider Instance { get; } = new();

    /// <inheritdoc/>
    public char DirectorySeparator => Path.DirectorySeparatorChar;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<FileSystemEntry>> GetEntriesAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        return new ValueTask<IReadOnlyList<FileSystemEntry>>(Task.Run(() => Enumerate(directoryPath, cancellationToken), cancellationToken));
    }

    private static IReadOnlyList<FileSystemEntry> Enumerate(string directoryPath, CancellationToken cancellationToken)
    {
        var entries = new List<FileSystemEntry>();
        try
        {
            var info = new DirectoryInfo(directoryPath);

            // EnumerateFileSystemInfos streams, so a huge directory starts producing rows immediately and a
            // cancel (the user typed a new path) stops the walk instead of running it to completion.
            foreach (var child in info.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                long? size = null;
                if (!isDirectory && child is FileInfo file)
                {
                    // Length throws for a file deleted between the enumerate and the stat — a live directory
                    // races us constantly, so a vanished row must not abort the whole listing.
                    try { size = file.Length; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                entries.Add(new FileSystemEntry(child.FullName, child.Name, isDirectory)
                {
                    Size = size,
                    LastModified = SafeLastWrite(child),
                    IsHidden = (child.Attributes & FileAttributes.Hidden) != 0 || child.Name.StartsWith('.'),
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Contractually empty rather than throwing: the caller marks the directory inaccessible.
            return [];
        }

        return entries;
    }

    private static DateTimeOffset? SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTime; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<FileSystemEntry>> GetRootsAsync(CancellationToken cancellationToken = default,
                                                                   bool showHidden = false)
        => new(Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            var roots = new List<FileSystemEntry>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name;
                var ready = false;
                try
                {
                    ready = drive.IsReady;

                    var driveName = drive.Name;

                    if (showHidden is false && IsHiddenVolume(driveName))
                        continue;
                    
                    // VolumeLabel throws on an unready drive, which is exactly when we still want the row.
                    name = ready && drive.VolumeLabel.Length > 0
                               ? drive.Name is { Length: > 0 } n && n != drive.VolumeLabel
                                     ? $"{drive.VolumeLabel} ({n.TrimEnd(Path.DirectorySeparatorChar)})"
                                     : DisplayNameFor(drive)
                               : DisplayNameFor(drive);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    name = drive.Name;
                }

                roots.Add(new FileSystemEntry(drive.RootDirectory.FullName, name, isDirectory: true)
                {
                    Place = PlaceForDrive(drive),
                    IsInaccessible = !ready,
                });
            }

            return roots;
        }, cancellationToken));

    private static bool IsHiddenVolume(string driveName)
    {
        if (OperatingSystem.IsMacOS()) return IsHiddenMacOSVolume(driveName); 
        if (OperatingSystem.IsLinux()) return IsHiddenLinuxVolume(driveName); 
        return OperatingSystem.IsWindows() && IsHiddenWindowsVolume(driveName);
    }

    // ReSharper disable UnusedParameter.Local

    private static bool IsHiddenWindowsVolume(string driveName) => false;

    private static bool IsHiddenLinuxVolume(string driveName)
        => driveName.Equals("/dev", StringComparison.Ordinal) ||
           driveName.Equals("/proc", StringComparison.Ordinal);

    // ReSharper restore UnusedParameter.Local

    private static bool IsHiddenMacOSVolume(string driveName)
    {
        return driveName.StartsWith("/System/Volumes/", StringComparison.Ordinal) ||
               driveName.Equals("/dev", StringComparison.Ordinal) ||
               driveName.StartsWith("/private/", StringComparison.Ordinal);
    }

    private static FileSystemPlace PlaceForDrive(DriveInfo drive)
    {
        try
        {
            return drive.DriveType switch
                   {
                       DriveType.Removable => FileSystemPlace.RemovableDrive,
                       DriveType.Network   => FileSystemPlace.NetworkDrive,
                       _                   => FileSystemPlace.Drive
                   };
        }
        catch (IOException)
        {
            return FileSystemPlace.Drive;
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<FileSystemEntry>> GetPlacesAsync(CancellationToken cancellationToken = default)
    {
        var places = new List<FileSystemEntry>();
        foreach (var place in (ReadOnlySpan<FileSystemPlace>)
                 [
                     FileSystemPlace.Home, FileSystemPlace.Desktop, FileSystemPlace.Documents,
                     FileSystemPlace.Downloads, FileSystemPlace.Pictures, FileSystemPlace.Music,
                     FileSystemPlace.Videos,
                 ])
        {
            if (GetPlacePath(place) is { Length: > 0 } path && Directory.Exists(path))
                places.Add(new FileSystemEntry(path, DisplayNameFor(place, path), isDirectory: true) { Place = place });
        }

        return new ValueTask<IReadOnlyList<FileSystemEntry>>(places);
    }

    private static string DisplayNameFor(FileSystemPlace place, string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return leaf.Length > 0 ? leaf : place.ToString();
    }

    private static string DisplayNameFor(DriveInfo drive)
    {
        const string macVolumesPrefix = "/Volumes/";

        if (OperatingSystem.IsMacOS() &&
            drive is { RootDirectory.FullName: {} path } &&
            path.StartsWith(macVolumesPrefix) && path.LastIndexOf('/') == macVolumesPrefix.Length - 1)
        {
            return path.Substring(macVolumesPrefix.Length);
        }

        return drive.RootDirectory.FullName;
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path);

    /// <inheritdoc/>
    public bool FileExists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <inheritdoc/>
    public string? GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try { return Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar))?.FullName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <inheritdoc/>
    public string Combine(string directoryPath, string name) => Path.Combine(directoryPath, name);

    /// <inheritdoc/>
    public string? ResolvePath(string text, string basePath)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        // A leading ~ is a shell convention the OS path APIs know nothing about, so expand it here — users
        // type it constantly and a file dialog that cannot resolve "~/Projects" feels broken.
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) ||
            (Path.DirectorySeparatorChar == '\\' && trimmed.StartsWith("~\\", StringComparison.Ordinal)))
        {
            var home = GetPlacePath(FileSystemPlace.Home);
            if (home is null)
                return null;
            trimmed = trimmed.Length <= 1 ? home : Path.Combine(home, trimmed[2..]);
        }

        try { return Path.GetFullPath(trimmed, basePath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <inheritdoc/>
    public string? GetPlacePath(FileSystemPlace place)
    {
        var folder = place switch
        {
            FileSystemPlace.Home => Environment.SpecialFolder.UserProfile,
            FileSystemPlace.Desktop => Environment.SpecialFolder.DesktopDirectory,
            FileSystemPlace.Documents => Environment.SpecialFolder.MyDocuments,
            FileSystemPlace.Pictures => Environment.SpecialFolder.MyPictures,
            FileSystemPlace.Music => Environment.SpecialFolder.MyMusic,
            FileSystemPlace.Videos => Environment.SpecialFolder.MyVideos,
            _ => (Environment.SpecialFolder?)null,
        };

        if (folder is { } known)
        {
            var path = Environment.GetFolderPath(known);
            return path.Length > 0 ? path : null;
        }

        // Downloads has no SpecialFolder on any platform, so it is conventionally ~/Downloads.
        if (place is FileSystemPlace.Downloads)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
            {
                var downloads = Path.Combine(home, "Downloads");
                return Directory.Exists(downloads) ? downloads : null;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken = default)
        => new(Task.Run(() =>
        {
            var full = Path.Combine(parentPath, name);
            var info = Directory.CreateDirectory(full);
            return new FileSystemEntry(info.FullName, info.Name, isDirectory: true) { LastModified = info.LastWriteTime };
        }, cancellationToken));
}
