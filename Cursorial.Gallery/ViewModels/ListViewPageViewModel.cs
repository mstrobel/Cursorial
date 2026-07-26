using System.Collections.ObjectModel;

using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Themes;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The List View page: one <see cref="ListView"/> over 40 synthetic rows, shown in all four presentations
/// (<see cref="ListViewViewMode.Details"/> / <see cref="ListViewViewMode.List"/> /
/// <see cref="ListViewViewMode.SmallIcons"/> / <see cref="ListViewViewMode.Tiles"/>) through a switcher bound to
/// <see cref="ViewMode"/>. Switching is a single property write, and what it costs is worth watching: the control
/// swaps its items panel, flips the scroll axis and rebuilds every row's cell composition in one step.
/// <para>
/// <b>Sorting.</b> The four Details columns are sortable and a header click cycles ascending → descending, lighting
/// the <c>▲</c>/<c>▼</c> indicator. The page opts INTO <see cref="ListView.IsBuiltInSortEnabled"/> — reasonable
/// here because <see cref="Rows"/> is the page's own observable collection — and still listens on
/// <see cref="ListView.Sorting"/>, purely to report. It never sets <c>Handled</c> or <c>Cancel</c>, so the built-in
/// comparer does the reorder. Note that <i>Size</i> and <i>Modified</i> sort on
/// <see cref="AssetRow.SizeBytes"/>/<see cref="AssetRow.Modified"/> while displaying their formatted twins: sorting
/// "12.4 KB" as text would put 9 B after 12.4 KB.
/// </para>
/// <para>
/// <b>Icons.</b> Every row's glyph comes from <see cref="FileTypeIcons"/>, so the capability tiers are visible here
/// too — one Nerd Font codepoint per extension, a double-width emoji per category, and a single-width Unicode floor
/// that is always renderable. Toggle the color tier with <c>⌥+t</c> to watch the ladder resolve.
/// </para>
/// <para>
/// <b>Keyboard.</b> The items host is ONE tab stop with arrow navigation inside, and the arrows follow the CURRENT
/// wrap: in Details <c>↑</c>/<c>↓</c> walk the list and <c>←</c>/<c>→</c> stay unhandled; in List/Small Icons the
/// wrap is column-major, so <c>↓</c> walks <i>down</i> a column and <c>←</c>/<c>→</c> jump a whole column; in Tiles
/// the two swap. <c>Home</c>/<c>End</c> jump, <c>PageUp</c>/<c>PageDown</c> move a screenful of lines, and — because
/// selection is <see cref="SelectionMode.Multiple"/> — <c>Space</c> toggles a row, <c>Shift</c>+arrow extends from
/// the anchor and <c>Ctrl</c>+arrow moves the focus without touching the selection. <c>Enter</c> or a double-click
/// invokes. Type-ahead jumps to a name.
/// </para>
/// </summary>
public sealed class ListViewPageViewModel : PageViewModel
{
    // 40 rows: six directories and 34 files chosen so that the file-type table's per-extension glyph tier and its
    // Type-column labels are both on display (a well-known name, a stem row, a compound extension, …).
    private static readonly (string Name, bool IsDirectory)[] Entries =
    [
        ("src", true),
        ("assets", true),
        ("docs", true),
        ("build", true),
        ("node_modules", true),
        (".git", true),
        ("README.md", false),
        ("LICENSE", false),
        ("CHANGELOG.md", false),
        ("Makefile", false),
        (".gitignore", false),
        (".editorconfig", false),
        ("package.json", false),
        ("package-lock.json", false),
        ("tsconfig.json", false),
        ("docker-compose.yml", false),
        ("Program.cs", false),
        ("ShellViewModel.cs", false),
        ("GalleryApp.cs", false),
        ("FuzzyMatcher.cs", false),
        ("Shell.xaml", false),
        ("index.html", false),
        ("theme-dark.css", false),
        ("color_tokens.scss", false),
        ("hero_banner.png", false),
        ("icon_sprite.svg", false),
        ("user-avatar.jpg", false),
        ("gradient-map.json", false),
        ("splash_screen.mp4", false),
        ("ambient_loop.mp3", false),
        ("build-matrix.yaml", false),
        ("keymap_default.toml", false),
        ("telemetry-schema.proto", false),
        ("deploy-preview.sh", false),
        ("bootstrap_env.ps1", false),
        ("lua_bindings.lua", false),
        ("archive.tar.gz", false),
        ("release-notes.pdf", false),
        ("crash_report.log", false),
        ("metrics.csv", false),
    ];

    private ListView? _view;
    private ListViewViewMode _viewMode = ListViewViewMode.Details;
    private string _selectionSummary;
    private string _sortSummary = "unsorted";
    private string _invokedSummary = "";

    public ListViewPageViewModel()
    {
        Rows = new ObservableCollection<AssetRow>(BuildRows());
        _selectionSummary = $"0 of {Rows.Count} selected";
    }

    public override string Title => "List View";

    public override string Summary =>
        "40 rows in four view modes with sortable Details columns and multi-select. Details: ↑/↓; List & Small " +
        "Icons wrap column-major (↓ walks the column, ←/→ jump one); Tiles wrap row-major. Space toggles, " +
        "Shift+arrow extends, Ctrl+arrow moves focus only, Enter or double-click invokes.";

    /// <summary>The rows — an <see cref="ObservableCollection{T}"/> because the built-in sort permutes the LIVE
    /// list with Remove/Insert and needs a source that can report each hop.</summary>
    public ObservableCollection<AssetRow> Rows { get; }

    /// <summary>The presentation mode, two-way with both the view switcher and <see cref="ListView.View"/>.</summary>
    public ListViewViewMode ViewMode { get => _viewMode; set => Set(ref _viewMode, value); }

    /// <summary>The live status line: the selection count, the sort in effect, and the last invocation.</summary>
    public string Status => _invokedSummary is { Length: > 0 }
        ? $"{_selectionSummary} · sort: {_sortSummary} · {_invokedSummary}"
        : $"{_selectionSummary} · sort: {_sortSummary}";

    // ───────────────────────────── the control wiring ─────────────────────────────
    //
    // SelectedItems is a SNAPSHOT property with no change notification (it materializes the selected indexes on
    // every read), so a live "3 of 40 selected" readout cannot be a binding — it has to ride SelectionChanged.
    // Sorting and ItemInvoked are routed events for the same reason. The page's view hands the control over here
    // (see Pages/GalleryListView.cs) rather than the view-model reaching into the tree.

    /// <summary>Attaches to the page's list view (idempotent — a re-attach after a DataContext swap is harmless).</summary>
    /// <param name="view">The list view this page is showing.</param>
    internal void Connect(ListView view)
    {
        if (ReferenceEquals(_view, view))
            return;

        Disconnect(_view);
        _view = view;

        view.SelectionChanged += OnSelectionChanged;
        view.Sorting += OnSorting;
        view.ItemInvoked += OnItemInvoked;

        UpdateSelectionSummary();
    }

    /// <summary>Detaches from <paramref name="view"/> (a no-op unless it is the connected one).</summary>
    /// <param name="view">The list view leaving the tree.</param>
    internal void Disconnect(ListView? view)
    {
        if (view is null || !ReferenceEquals(_view, view))
            return;

        view.SelectionChanged -= OnSelectionChanged;
        view.Sorting -= OnSorting;
        view.ItemInvoked -= OnItemInvoked;
        _view = null;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelectionSummary();

    private void OnSorting(object? sender, ListViewSortingEventArgs e)
    {
        // Report only. Leaving Handled and Cancel alone is what lets IsBuiltInSortEnabled do the reorder; a page
        // that owned a server-side ordering would sort its own collection here and set Handled.
        var arrow = e.Direction == ListViewSortDirection.Descending ? "▼" : "▲";
        _sortSummary = $"{e.Column.Header} {arrow} (key: {e.SortMemberPath})";
        Raise(nameof(Status));
    }

    private void OnItemInvoked(object? sender, ItemActivatedEventArgs e)
    {
        _invokedSummary = e.Item is AssetRow row ? $"invoked “{row.Name}”" : "";
        Raise(nameof(Status));
    }

    private void UpdateSelectionSummary()
    {
        var count = _view?.SelectedItems.Count ?? 0;
        _selectionSummary = $"{count} of {Rows.Count} selected";
        Raise(nameof(Status));
    }

    // ───────────────────────────── the synthetic rows ─────────────────────────────

    // Seeded per instance (not per process), so every ListViewPageViewModel renders the identical table — the
    // same determinism discipline as the DataGrid page, which a shared static Random would quietly lose the
    // moment a second shell is built (as the headless tests do).
    private static List<AssetRow> BuildRows()
    {
        var random = new Random(2026);
        var newest = new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc);
        var rows = new List<AssetRow>(Entries.Length);

        foreach (var (name, isDirectory) in Entries)
        {
            var type = FileTypeIcons.ForEntry(name, isDirectory);

            // Directories carry no size. −1 (not 0) so an ascending Size sort files them together ahead of an
            // empty file rather than interleaving with one. Everything else is sized by CATEGORY, because a
            // 20 MB README makes the Size column read as noise instead of as data.
            var bytes = isDirectory ? -1L : RandomSizeFor(random, type.Category);
            var size = isDirectory ? "—" : FormatSize(bytes);
            var modified = newest.AddMinutes(-random.Next(0, 60 * 24 * 400));

            rows.Add(new AssetRow
            {
                Name = name,
                SizeBytes = bytes,
                Size = size,
                Kind = type.KindLabel,
                Modified = modified,
                ModifiedText = modified.ToString("yyyy-MM-dd HH:mm"),
                Detail = $"{type.KindLabel} · {size}",
                Icon = type.ToIconCarrier(),
            });
        }

        return rows;
    }

    // A plausible byte count for the category, so the Size column sorts over a realistic spread: prose and
    // configuration are kilobytes, art and audio are megabytes, media and archives are tens of megabytes.
    private static long RandomSizeFor(Random random, FileTypeCategory category) => category switch
    {
        FileTypeCategory.Text or FileTypeCategory.Data or FileTypeCategory.Source or FileTypeCategory.Markup
            => random.NextInt64(180, 220L * 1024),
        FileTypeCategory.Image or FileTypeCategory.Vector or FileTypeCategory.Audio
            => random.NextInt64(24L * 1024, 4L * 1024 * 1024),
        FileTypeCategory.Video or FileTypeCategory.Archive or FileTypeCategory.Document
            => random.NextInt64(1L * 1024 * 1024, 60L * 1024 * 1024),
        _   => random.NextInt64(1024, 512L * 1024)
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:0.#} KB",
        _                    => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    /// <summary>
    /// One synthetic listing row. Immutable — nothing on this page mutates a row, so there is no
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/> to implement (the DataGrid page is the live-feed
    /// canary; this one is about presentation and ordering).
    /// </summary>
    public sealed class AssetRow
    {
        /// <summary>The entry name — the Details <i>Name</i> column, the primary line in every other view, and the
        /// text type-ahead matches.</summary>
        public required string Name { get; init; }

        /// <summary>The raw byte count — the <i>Size</i> column's SORT key (−1 for a directory).</summary>
        public required long SizeBytes { get; init; }

        /// <summary>The human-readable size — what the <i>Size</i> column DISPLAYS.</summary>
        public required string Size { get; init; }

        /// <summary>The file-type table's Type-column label ("PNG image", "Folder", "Shell script").</summary>
        public required string Kind { get; init; }

        /// <summary>The modification timestamp — the <i>Modified</i> column's sort key.</summary>
        public required DateTime Modified { get; init; }

        /// <summary>The formatted timestamp — what the <i>Modified</i> column displays.</summary>
        public required string ModifiedText { get; init; }

        /// <summary>The tile view's second line (kind · size).</summary>
        public required string Detail { get; init; }

        /// <summary>The capability-tiered icon from <see cref="FileTypeIcons"/>. An <see cref="IconCarrier"/>, not
        /// an <see cref="Icon"/>: a carrier is an immutable value that the icon <c>DataTemplate</c> materializes
        /// per cell, so the same row can appear in a Details cell and a tile without an element living twice.</summary>
        public required IconCarrier Icon { get; init; }
    }
}
