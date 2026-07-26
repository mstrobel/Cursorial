using System.Collections.Generic;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// Everything <see cref="FileSaveDialog.ShowAsync"/> needs — the WPF <c>SaveFileDialog</c> property bag as an
/// immutable record. Identical to <see cref="FileOpenDialogRequest"/> apart from the three write-side members
/// the design page's Save As page adds (<see cref="DefaultExtension"/>, <see cref="ConfirmOverwrite"/>,
/// <see cref="CanCreateDirectories"/>) and the absence of <c>MustExist</c>, which has no meaning when the
/// point of the dialog is to name a file that does not exist yet.
/// <para>
/// <b>The two requests are deliberately separate records, not one with a mode flag.</b> They are the public
/// contract of two different entry points, and a shared record would put <c>MustExist</c> in front of Save
/// callers and <c>ConfirmOverwrite</c> in front of Open callers — properties that do nothing there. The
/// duplication is four lines; the confusion would be permanent.
/// </para>
/// </summary>
/// <param name="Title">The window title, e.g. <c>"Save As"</c>.</param>
public sealed record FileSaveDialogRequest(string Title)
{
    /// <summary>
    /// The hierarchy to browse. Defaults to the real disk; a headless test, a demo, or a dialog over an archive
    /// substitutes its own (see <see cref="InMemoryFileSystemProvider"/>).
    /// </summary>
    public IFileSystemProvider FileSystem { get; init; } = PhysicalFileSystemProvider.Instance;

    /// <summary>
    /// The directory to open in. <see langword="null"/> ⇒ the provider's home place, else its first root — the
    /// dialog never opens on "nowhere". A path that does not resolve falls back the same way.
    /// </summary>
    public string? InitialDirectory { get; init; }

    /// <summary>The name the File name field starts with, e.g. <c>"report-final"</c>. The design page's field
    /// omits the extension because the type selector supplies it (see <see cref="DefaultExtension"/>).</summary>
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

    /// <summary>The listing's presentation mode at open time (default <see cref="ListViewViewMode.Details"/>).</summary>
    public ListViewViewMode View { get; init; } = ListViewViewMode.Details;

    /// <summary>
    /// The extension (leading <c>'.'</c> included) appended to an extension-less typed name. When
    /// <see langword="null"/>, the selected filter's <see cref="FileDialogFilter.DefaultExtension"/> supplies
    /// it — which is the design page's behaviour: "the filename field omits the extension since the type
    /// selector supplies it". Set this to pin one extension regardless of the selected filter.
    /// </summary>
    public string? DefaultExtension { get; init; }

    /// <summary>
    /// Whether Save on an existing name raises the "already exists… replace?" confirmation (default
    /// <see langword="true"/>; the Win32 <c>OFN_OVERWRITEPROMPT</c> behaviour). The confirmation is a real
    /// <see cref="TaskDialog"/> — the dialog does not hand-roll a second modal look.
    /// </summary>
    public bool ConfirmOverwrite { get; init; } = true;

    /// <summary>Whether the New Folder affordance (<c>Alt+N</c>) is offered (default <see langword="true"/>).
    /// Off for a provider that cannot create directories.</summary>
    public bool CanCreateDirectories { get; init; } = true;
}
