using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// An <see cref="IFileSystemProvider"/> over a synthetic in-memory tree — the headless twin of
/// <see cref="PhysicalFileSystemProvider"/>. Everything the dialogs do goes through the provider seam
/// precisely so that this can exist: a whole browsing session (navigate, sort, filter, type-ahead, path-edit
/// with completion, new folder, overwrite confirm) is driven against a tree the test AUTHORS, with no disk, no
/// temp directory to clean up, no platform-dependent home folder, and no timing.
/// <para>
/// It also carries the demo tree the design page draws — <see cref="CreateSample"/> builds
/// <c>~/Projects/assets</c> exactly as the mock shows it, down to the file sizes and timestamps — so the
/// gallery and the samples can show a live file dialog on a machine whose real disk holds nothing
/// interesting.
/// </para>
/// <para>
/// <b>Every operation completes synchronously.</b> The interface is asynchronous because a real provider must
/// not stall the frame loop, not because the dialogs need it to be; answering inline means an <c>await</c> in
/// the view-model continues on the calling thread, which is what lets a view-model test assert immediately
/// after the call with no dispatcher pumping at all.
/// </para>
/// <para>
/// Paths use <c>'/'</c> and are rooted at <c>"/"</c> on every platform, deliberately — a synthetic tree that
/// changed shape between Windows and macOS would defeat the entire purpose.
/// </para>
/// </summary>
public sealed class InMemoryFileSystemProvider : IFileSystemProvider
{
    /// <summary>The tree's root path. Also the sole entry of <see cref="GetRootsAsync"/> unless
    /// <see cref="AddRoot"/> is called.</summary>
    public const string RootPath = "/";

    private readonly Node _root = new("", isDirectory: true);
    private readonly Dictionary<FileSystemPlace, string> _placePaths = [];
    private readonly List<(string Path, string Name, FileSystemPlace Place)> _places = [];
    private readonly List<(string Path, string Name, FileSystemPlace Place)> _roots = [];

    /// <inheritdoc/>
    public char DirectorySeparator => '/';

    // ───────────────────────────── authoring ─────────────────────────────

    /// <summary>
    /// Creates every missing directory along <paramref name="path"/> (the <c>mkdir -p</c> shape) and returns
    /// the provider, so a whole tree can be authored as one fluent chain.
    /// </summary>
    /// <param name="path">An absolute directory path, e.g. <c>"/home/ada/Projects/assets"</c>.</param>
    public InMemoryFileSystemProvider AddDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsureDirectory(Normalize(path));
        return this;
    }

    /// <summary>Adds a file, creating its ancestors as needed. Returns the provider for chaining.</summary>
    /// <param name="path">An absolute file path.</param>
    /// <param name="size">The reported byte size (the Size column).</param>
    /// <param name="lastModified">The reported last-write time, or <see langword="null"/> for none.</param>
    /// <param name="isHidden">Whether the entry is hidden. Defaults to the Unix rule — a leading dot.</param>
    public InMemoryFileSystemProvider AddFile(string path,
                                              long size = 0,
                                              DateTimeOffset? lastModified = null,
                                              bool? isHidden = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = Normalize(path);
        var name = LeafOf(normalized);
        var parent = EnsureDirectory(ParentOf(normalized) ?? RootPath);

        parent.Children[name] = new Node(name, isDirectory: false)
                                {
                                    Size = size,
                                    LastModified = lastModified,
                                    IsHidden = isHidden ?? name.StartsWith('.')
                                };

        return this;
    }

    /// <summary>
    /// Registers a well-known place: it is answered by <see cref="GetPlacePath"/> (so <c>~</c> expansion and
    /// the "start in the home folder" fallback work) and, unless <paramref name="showInRail"/> is
    /// <see langword="false"/>, listed in the rail's Quick access band.
    /// </summary>
    /// <param name="place">The place classification, which also selects the rail row's icon.</param>
    /// <param name="path">The absolute directory path. Created if it does not exist.</param>
    /// <param name="name">The rail's caption; <see langword="null"/> ⇒ the path's last component.</param>
    /// <param name="showInRail">Whether the place appears in the Quick access band.</param>
    public InMemoryFileSystemProvider AddPlace(FileSystemPlace place,
                                               string path,
                                               string? name = null,
                                               bool showInRail = true)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = Normalize(path);
        EnsureDirectory(normalized);
        _placePaths[place] = normalized;

        if (showInRail)
            _places.Add((normalized, name ?? DisplayLeaf(normalized), place));

        return this;
    }

    /// <summary>
    /// Registers a root for the rail's "This PC" / "Network" bands (a
    /// <see cref="FileSystemPlace.NetworkDrive"/> or <see cref="FileSystemPlace.CloudDrive"/> lands in
    /// Network, everything else in This PC). Without any call to this, <see cref="GetRootsAsync"/> reports the
    /// single synthetic root.
    /// </summary>
    /// <param name="path">The absolute directory path. Created if it does not exist.</param>
    /// <param name="name">The rail's caption, e.g. <c>"Local (C:)"</c>.</param>
    /// <param name="place">The volume classification, which selects the icon.</param>
    public InMemoryFileSystemProvider AddRoot(string path, string name, FileSystemPlace place = FileSystemPlace.Drive)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);

        var normalized = Normalize(path);
        EnsureDirectory(normalized);
        _roots.Add((normalized, name, place));
        return this;
    }

    /// <summary>
    /// The design page's demo tree: <c>/home/ada</c> as Home with Desktop / Documents / Downloads places, a
    /// <c>Projects/assets</c> folder holding the mock's twelve entries (folders <c>textures</c> and
    /// <c>icons</c> first, then <c>hero-banner.png</c> … <c>LICENSE</c>) with the mock's sizes and dates, and
    /// a local + cloud root pair for the rail's lower bands.
    /// </summary>
    public static InMemoryFileSystemProvider CreateSample()
    {
        var provider = new InMemoryFileSystemProvider();

        provider.AddPlace(FileSystemPlace.Home, "/home/ada", "Home")
                .AddPlace(FileSystemPlace.Desktop, "/home/ada/Desktop")
                .AddPlace(FileSystemPlace.Documents, "/home/ada/Documents")
                .AddPlace(FileSystemPlace.Downloads, "/home/ada/Downloads")
                .AddRoot("/", "Local (C:)")
                .AddRoot("/mnt/cloud", "Cloud Drive", FileSystemPlace.CloudDrive);

        const string assets = "/home/ada/Projects/assets";

        provider.AddDirectory($"{assets}/textures")
                .AddDirectory($"{assets}/icons")
                .AddFile($"{assets}/hero-banner.png", 1_468_006, Stamp("2026-06-01 11:20"))
                .AddFile($"{assets}/logo.svg", 22_528, Stamp("2026-05-29 16:08"))
                .AddFile($"{assets}/thumbnail.jpg", 90_112, Stamp("2026-05-30 10:15"))
                .AddFile($"{assets}/palette.aco", 4_096, Stamp("2026-05-12 13:33"))
                .AddFile($"{assets}/credits.pdf", 245_760, Stamp("2026-05-22 09:30"))
                .AddFile($"{assets}/atlas.lua", 12_288, Stamp("2026-05-25 17:50"))
                .AddFile($"{assets}/build.sh", 2_048, Stamp("2026-05-19 08:14"))
                .AddFile($"{assets}/gradients.json", 6_144, Stamp("2026-05-28 11:11"))
                .AddFile($"{assets}/template.json", 2_048, Stamp("2026-05-11 10:00"))
                .AddFile($"{assets}/.gitkeep", 0, Stamp("2026-04-02 08:00"))
                .AddFile($"{assets}/LICENSE", 1_024, Stamp("2026-04-02 08:00"));

        return provider;

        static DateTimeOffset Stamp(string text) => DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.AssumeLocal);
    }

    // ───────────────────────────── IFileSystemProvider ─────────────────────────────

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<FileSystemEntry>> GetEntriesAsync(string directoryPath,
                                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(directoryPath);

        if (Find(normalized) is not { IsDirectory: true } directory)
            return new ValueTask<IReadOnlyList<FileSystemEntry>>((IReadOnlyList<FileSystemEntry>) []);

        var entries = new List<FileSystemEntry>(directory.Children.Count);

        foreach (var (name, child) in directory.Children)
        {
            entries.Add(new FileSystemEntry(Combine(normalized, name), name, child.IsDirectory)
                        {
                            Size = child.IsDirectory ? null : child.Size,
                            LastModified = child.LastModified,
                            IsHidden = child.IsHidden
                        });
        }

        return new ValueTask<IReadOnlyList<FileSystemEntry>>(entries);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<FileSystemEntry>> GetRootsAsync(CancellationToken cancellationToken = default,
                                                                   bool showHidden = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_roots.Count == 0)
        {
            return new ValueTask<IReadOnlyList<FileSystemEntry>>(
                [new FileSystemEntry(RootPath, RootPath, isDirectory: true) { Place = FileSystemPlace.Drive }]);
        }

        var roots = new List<FileSystemEntry>(_roots.Count);

        foreach (var (path, name, place) in _roots)
            roots.Add(new FileSystemEntry(path, name, isDirectory: true) { Place = place });

        return new ValueTask<IReadOnlyList<FileSystemEntry>>(roots);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<FileSystemEntry>> GetPlacesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var places = new List<FileSystemEntry>(_places.Count);

        foreach (var (path, name, place) in _places)
            places.Add(new FileSystemEntry(path, name, isDirectory: true) { Place = place });

        return new ValueTask<IReadOnlyList<FileSystemEntry>>(places);
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
        => !string.IsNullOrEmpty(path) && Find(Normalize(path)) is { IsDirectory: true };

    /// <inheritdoc/>
    public bool FileExists(string path)
        => !string.IsNullOrEmpty(path) && Find(Normalize(path)) is { IsDirectory: false };

    /// <inheritdoc/>
    public string? GetParentPath(string path)
        => string.IsNullOrEmpty(path) ? null : ParentOf(Normalize(path));

    /// <inheritdoc/>
    public string Combine(string directoryPath, string name)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(name);

        var directory = Normalize(directoryPath);
        return directory == RootPath ? RootPath + name : directory + "/" + name;
    }

    /// <inheritdoc/>
    public string? ResolvePath(string text, string basePath)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        // A leading ~ is a shell convention no path API knows about, and users type it constantly — the
        // physical provider expands it too, so the two behave the same under the same dialog.
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            if (!_placePaths.TryGetValue(FileSystemPlace.Home, out var home))
                return null;

            trimmed = trimmed.Length <= 1 ? home : home + "/" + trimmed[2..];
        }

        var rooted = trimmed.StartsWith('/')
            ? trimmed
            : (string.IsNullOrEmpty(basePath) ? RootPath : Normalize(basePath)) + "/" + trimmed;

        var segments = new List<string>();

        foreach (var segment in rooted.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case ".." when segments.Count > 0:
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                case "..":
                    continue; // ".." at the root is the root, not an error (the POSIX rule)
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count == 0 ? RootPath : RootPath + string.Join('/', segments);
    }

    /// <inheritdoc/>
    public string? GetPlacePath(FileSystemPlace place) => _placePaths.GetValueOrDefault(place);

    /// <inheritdoc/>
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string parentPath,
                                                           string name,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentPath);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        if (name.Length == 0 || name.Contains('/'))
            throw new ArgumentException($"'{name}' is not a valid folder name.", nameof(name));

        var parent = Find(Normalize(parentPath))
                     ?? throw new InvalidOperationException($"The directory '{parentPath}' does not exist.");

        if (!parent.IsDirectory)
            throw new InvalidOperationException($"'{parentPath}' is not a directory.");

        if (parent.Children.ContainsKey(name))
            throw new InvalidOperationException($"'{name}' already exists.");

        var created = new Node(name, isDirectory: true) { LastModified = DateTimeOffset.Now };
        parent.Children[name] = created;

        var entry = new FileSystemEntry(Combine(parentPath, name), name, isDirectory: true)
                    {
                        LastModified = created.LastModified
                    };

        return new ValueTask<FileSystemEntry>(entry);
    }

    // ───────────────────────────── the tree ─────────────────────────────

    private sealed class Node(string name, bool isDirectory)
    {
        // Ordinal-sorted so an enumeration is deterministic without the dialog's own sort having run yet —
        // a provider whose listing order depended on insertion would make "does the sort work?" untestable.
        public SortedDictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public string Name { get; } = name;
        public bool IsDirectory { get; } = isDirectory;
        public long Size { get; init; }
        public DateTimeOffset? LastModified { get; init; }
        public bool IsHidden { get; init; }
    }

    private Node EnsureDirectory(string path)
    {
        var node = _root;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(segment, out var child))
                node.Children[segment] = child = new Node(segment, isDirectory: true);

            node = child;
        }

        return node;
    }

    private Node? Find(string path)
    {
        var node = _root;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(segment, out var child))
                return null;

            node = child;
        }

        return node;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
            return RootPath;

        var replaced = path.Replace('\\', '/');
        var trimmed = replaced.TrimEnd('/');
        return trimmed.Length == 0 ? RootPath : (trimmed.StartsWith('/') ? trimmed : "/" + trimmed);
    }

    private static string? ParentOf(string normalized)
    {
        if (normalized == RootPath)
            return null;

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? RootPath : normalized[..index];
    }

    private static string LeafOf(string normalized)
    {
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    private static string DisplayLeaf(string normalized)
    {
        var leaf = LeafOf(normalized);
        return leaf.Length > 0 ? leaf : normalized;
    }
}
