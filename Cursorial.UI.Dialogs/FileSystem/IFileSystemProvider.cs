using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The file dialogs' view of a browsable hierarchy. Everything the dialogs do — list a directory, walk to a
/// parent, resolve a typed path, create a folder — goes through this seam, so the same dialog can browse the
/// real disk, an in-memory tree in a headless test, an archive, or a remote source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enumeration is asynchronous on purpose.</b> The framework is UI-thread affine and the frame loop runs
/// on that thread, so enumerating a slow or dead network share synchronously would freeze the whole
/// application mid-frame — with a terminal UI that means a wedged terminal, not a greyed window. Providers
/// must do the actual I/O off the UI thread; the dialog awaits and marshals the result back.
/// </para>
/// <para>
/// <b>The cheap predicates are synchronous</b> (<see cref="DirectoryExists"/>, <see cref="FileExists"/>) so
/// path-edit validation can run per keystroke without a state machine. Providers whose backing store can
/// block MUST make these fast — cache, or answer from the last listing — because they are called from the
/// UI thread. When in doubt, answer optimistically and let the subsequent navigation fail loudly.
/// </para>
/// <para>
/// Paths are provider-defined strings. The dialogs never parse them with <see cref="System.IO.Path"/>;
/// they call <see cref="GetParentPath"/>, <see cref="Combine"/> and <see cref="DirectorySeparator"/> so a
/// provider can use a separator and rooting scheme of its own.
/// </para>
/// </remarks>
public interface IFileSystemProvider
{
    /// <summary>The separator this provider puts between path segments (<c>/</c> or <c>\</c>).</summary>
    char DirectorySeparator { get; }

    /// <summary>
    /// Lists the immediate children of <paramref name="directoryPath"/>. Must not throw for an unreadable
    /// directory — return an empty list; the caller surfaces the condition from
    /// <see cref="FileSystemEntry.IsInaccessible"/> on the directory's own entry.
    /// </summary>
    ValueTask<IReadOnlyList<FileSystemEntry>> GetEntriesAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The roots of the hierarchy — drives on Windows, a single <c>/</c> on Unix, or whatever a synthetic
    /// provider chooses. Populates the places rail's "This PC" group.
    /// </summary>
    ValueTask<IReadOnlyList<FileSystemEntry>> GetRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>The well-known places for the rail's "Quick access" group, in display order.</summary>
    ValueTask<IReadOnlyList<FileSystemEntry>> GetPlacesAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a directory exists. Called per keystroke during path editing — must be fast.</summary>
    bool DirectoryExists(string path);

    /// <summary>Whether a file exists. Called per keystroke during path editing — must be fast.</summary>
    bool FileExists(string path);

    /// <summary>The containing directory of <paramref name="path"/>, or <see langword="null"/> at a root.</summary>
    string? GetParentPath(string path);

    /// <summary>Joins a directory and a child name with <see cref="DirectorySeparator"/>.</summary>
    string Combine(string directoryPath, string name);

    /// <summary>
    /// Normalizes a user-typed path: expands a leading <c>~</c>, resolves <c>.</c> and <c>..</c>, and roots a
    /// relative path against <paramref name="basePath"/>. Returns <see langword="null"/> when the text cannot
    /// be interpreted as a path at all.
    /// </summary>
    string? ResolvePath(string text, string basePath);

    /// <summary>The path of a well-known place, or <see langword="null"/> when the provider has no such place.</summary>
    string? GetPlacePath(FileSystemPlace place);

    /// <summary>
    /// Creates a directory and returns its entry. Throws on failure — the caller reports the message to the
    /// user, so the exception text matters.
    /// </summary>
    ValueTask<FileSystemEntry> CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken = default);
}
