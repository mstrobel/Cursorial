using System.Collections.Generic;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// Everything <see cref="FileOpenDialog.ShowAsync"/> needs — the WPF <c>OpenFileDialog</c> property bag as an
/// immutable record: which hierarchy to browse, where to start, what to offer, and how to present it. Same
/// shape rule as <see cref="TaskDialogRequest"/>: one required positional (the window title, the only value
/// with no defensible default) and init-only properties carrying their defaults inline, so a caller writes
/// only what differs from the norm.
/// <code>
///   var result = await FileOpenDialog.ShowAsync(app, new FileOpenDialogRequest("Open File")
///                                                    {
///                                                        InitialDirectory = projectRoot,
///                                                        Filters = [new("Images", "*.png;*.jpg;*.svg")]
///                                                    });
/// </code>
/// </summary>
/// <param name="Title">The window title, e.g. <c>"Open File"</c>.</param>
public sealed record FileOpenDialogRequest(string Title)
{
    /// <summary>
    /// The hierarchy to browse. Defaults to the real disk; a headless test, a demo, or a dialog over an archive
    /// substitutes its own (see <see cref="InMemoryFileSystemProvider"/>).
    /// </summary>
    public IFileSystemProvider FileSystem { get; init; } = PhysicalFileSystemProvider.Instance;

    /// <summary>
    /// The directory to open in. <see langword="null"/> ⇒ the provider's home place, else its first root — the
    /// dialog never opens on "nowhere". A path that does not resolve falls back the same way rather than
    /// failing the show, because a stale persisted directory must not cost the user their dialog.
    /// </summary>
    public string? InitialDirectory { get; init; }

    /// <summary>The text the File name field starts with (also the initially selected row when it names an
    /// entry in the opening directory). <see langword="null"/> ⇒ empty.</summary>
    public string? InitialFileName { get; init; }

    /// <summary>
    /// The type-filter rows, in selector order. Empty ⇒ a lone <see cref="FileDialogFilter.AllFiles"/>, which
    /// is also what an unrecognized <see cref="SelectedFilterIndex"/> falls back to.
    /// </summary>
    public IReadOnlyList<FileDialogFilter> Filters { get; init; } = [];

    /// <summary>The index into <see cref="Filters"/> that starts selected (clamped; default the first row).</summary>
    public int SelectedFilterIndex { get; init; }

    /// <summary>Whether entries the provider marks hidden are listed (default <see langword="false"/>).</summary>
    public bool ShowHiddenEntries { get; init; }

    /// <summary>The listing's presentation mode at open time — the design page's <c>▤ ▥ ▦</c> view switcher
    /// (default <see cref="ListViewViewMode.Details"/>, the sortable four-column list).</summary>
    public ListViewViewMode View { get; init; } = ListViewViewMode.Details;

    /// <summary>
    /// Whether Open refuses a name that does not exist (default <see langword="true"/> — the Win32
    /// <c>OFN_FILEMUSTEXIST</c> behaviour). Turn it off for the "open or create" flows where the caller will
    /// create the file itself; the dialog then returns the typed path unchecked.
    /// </summary>
    public bool MustExist { get; init; } = true;

    /// <summary>The window title, e.g. <c>"Open File"</c>.</summary>
    public string Title { get; init; } = Title;

    public FileOpenDialogRequest() : this("Open File") {
    }
}
