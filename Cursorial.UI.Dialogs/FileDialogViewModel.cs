using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Cursorial.UI.Controls;
using Cursorial.UI.Data;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The whole behaviour of <see cref="FileOpenDialog"/> and <see cref="FileSaveDialog"/>, with no
/// <see cref="Window"/> anywhere in sight: the current directory, the listing (filtered, sorted, folders
/// first), the selection, the File name field, the type filter, the sort key and direction, the view mode, the
/// places rail, the back/forward history, the type-ahead buffer and the new-folder session — plus an
/// <see cref="ICommand"/> for every action the chrome can invoke. The dialogs bind to it; the tests drive it
/// directly, which is the point: every rule in this file (folders sort before files, Backspace only navigates
/// up while no type-ahead is running, Save appends the filter's extension, Enter on a directory drills instead
/// of accepting) is asserted without instantiating a single control.
/// <para>
/// <b>WPF/Avalonia parity.</b> WPF's <c>Microsoft.Win32.OpenFileDialog</c> and Avalonia's
/// <c>IStorageProvider.OpenFilePickerAsync</c> are both thin wrappers over a shell dialog whose behaviour is
/// the operating system's and is therefore untestable and unstyleable. There is no shell here, so this class
/// <i>is</i> that behaviour; making it a view-model rather than burying it in a control is what keeps it
/// inspectable and keeps the two dialogs from drifting apart.
/// </para>
/// <para>
/// <b>Thread affinity.</b> Everything here is UI-thread affine, exactly like the controls that bind to it: the
/// provider does its I/O off-thread (<see cref="IFileSystemProvider.GetEntriesAsync"/>) and the awaits here
/// resume on the captured context, which under <c>UIApplication</c> is the dispatcher. A view-model-only test
/// with a synchronous provider never leaves the calling thread at all, because a completed
/// <see cref="ValueTask{T}"/> continues inline.
/// </para>
/// <para>
/// <b>Commands do not auto-requery.</b> This framework has no <c>CommandManager</c>, so every state change that
/// a <c>canExecute</c> predicate reads calls <see cref="DelegateCommand.RaiseCanExecuteChanged"/> explicitly —
/// funnelled through <see cref="RaiseCommandStates"/> so a new command cannot be half-wired.
/// </para>
/// </summary>
public sealed class FileDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>How long a type-ahead buffer survives without a keystroke before the next one starts fresh —
    /// the same one second <c>TextSearchController</c> uses, so the two feel identical.</summary>
    public static readonly TimeSpan TypeAheadIdleTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How many directory listings the completion cache keeps. A path-bar session walks a handful of
    /// directories; the cap exists so a long-lived dialog over a deep tree cannot grow without bound.</summary>
    private const int ListingCacheCapacity = 32;

    private readonly IFileSystemProvider _fileSystem;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];
    private readonly List<FileDialogEntry> _allEntries = [];
    private readonly Dictionary<string, IReadOnlyList<FileSystemEntry>> _listingCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _listingCacheOrder = new();
    private readonly TimeProvider _timeProvider;
    private readonly bool _mustExist;
    private readonly string? _defaultExtension;
    private readonly bool _confirmOverwrite;

    private CancellationTokenSource? _navigation;
    private string _currentDirectory = "";
    private FileDialogEntry? _selectedEntry;
    private string _fileName = "";
    private string _searchText = "";
    private FileDialogFilter? _selectedFilter;
    private FileDialogSortKey _sortKey = FileDialogSortKey.Name;
    private ListViewSortDirection _sortDirection = ListViewSortDirection.Ascending;
    private ListViewViewMode _viewMode = ListViewViewMode.Details;
    private bool _showHiddenEntries;
    private string? _errorMessage;
    private bool _isBusy;
    private bool _isCreatingFolder;
    private string _newFolderName = "";
    private string _typeAheadBuffer = "";
    private DateTimeOffset _lastTypeAheadAt;
    private IReadOnlyList<FileDialogPlaceGroup> _placeGroups = [];
    private bool _disposed;

    /// <summary>Creates the view-model for an Open dialog.</summary>
    /// <param name="request">The caller's request; <see cref="FileOpenDialogRequest.FileSystem"/> is the
    /// hierarchy every operation goes through.</param>
    /// <param name="timeProvider">
    /// The clock the type-ahead idle window is measured against. Injectable so a headless test can drive the
    /// buffer's expiry with <c>FakeTimeProvider</c> instead of sleeping; defaults to
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    public FileDialogViewModel(FileOpenDialogRequest request, TimeProvider? timeProvider = null)
        : this(request?.FileSystem!, request?.Filters!, request?.SelectedFilterIndex ?? 0, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);

        IsSaveDialog = false;
        _mustExist = request.MustExist;
        _confirmOverwrite = false;
        _showHiddenEntries = request.ShowHiddenEntries;
        _viewMode = request.View;
        _fileName = request.InitialFileName ?? "";
        InitialDirectory = request.InitialDirectory;
        CanCreateDirectories = false;
    }

    /// <summary>Creates the view-model for a Save As dialog.</summary>
    /// <param name="request">The caller's request; <see cref="FileSaveDialogRequest.FileSystem"/> is the
    /// hierarchy every operation goes through.</param>
    /// <param name="timeProvider">See the Open overload — the type-ahead clock.</param>
    public FileDialogViewModel(FileSaveDialogRequest request, TimeProvider? timeProvider = null)
        : this(request?.FileSystem!, request?.Filters!, request?.SelectedFilterIndex ?? 0, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);

        IsSaveDialog = true;
        _mustExist = false;
        _confirmOverwrite = request.ConfirmOverwrite;
        _defaultExtension = request.DefaultExtension;
        _showHiddenEntries = request.ShowHiddenEntries;
        _viewMode = request.View;
        _fileName = request.InitialFileName ?? "";
        InitialDirectory = request.InitialDirectory;
        CanCreateDirectories = request.CanCreateDirectories;
    }

    private FileDialogViewModel(IFileSystemProvider fileSystem,
                                IReadOnlyList<FileDialogFilter> filters,
                                int selectedFilterIndex,
                                TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Filters = filters is { Count: > 0 } ? filters : [FileDialogFilter.AllFiles];
        _selectedFilter = Filters[Math.Clamp(selectedFilterIndex, 0, Filters.Count - 1)];

        NavigateCommand = new DelegateCommand(parameter => Run(NavigateAsync(ResolveNavigationTarget(parameter))),
                                              parameter => ResolveNavigationTarget(parameter) is { Length: > 0 });
        BackCommand = new DelegateCommand(() => Run(GoBackAsync()), () => CanGoBack);
        ForwardCommand = new DelegateCommand(() => Run(GoForwardAsync()), () => CanGoForward);
        UpCommand = new DelegateCommand(() => Run(GoUpAsync()), () => CanGoUp);
        RefreshCommand = new DelegateCommand(() => Run(RefreshAsync()));
        NewFolderCommand = new DelegateCommand(BeginCreateFolder, () => CanCreateDirectories && !IsCreatingFolder);
        CommitNewFolderCommand = new DelegateCommand(() => Run(CommitCreateFolderAsync()), () => IsCreatingFolder);
        CancelNewFolderCommand = new DelegateCommand(CancelCreateFolder, () => IsCreatingFolder);
        AcceptCommand = new DelegateCommand(() => Run(AcceptAsync()), () => CanAccept);
        CancelCommand = new DelegateCommand(Cancel);
        SetViewModeCommand = new DelegateCommand(parameter =>
        {
            if (parameter is ListViewViewMode mode)
                ViewMode = mode;
        });
        SortCommand = new DelegateCommand(parameter =>
        {
            if (TryCoerceSortKey(parameter, out var key))
                Sort(key);
        });
        ActivateCommand = new DelegateCommand(parameter =>
        {
            if (parameter is FileDialogEntry entry)
                Run(ActivateAsync(entry));
        });
    }

    // ───────────────────────────── shape ─────────────────────────────

    /// <summary>Whether this is the Save As variant — the write-side affordances (New Folder, the overwrite
    /// confirmation, extension completion) are gated on it, and Accept means "name a file" rather than
    /// "choose an existing one".</summary>
    public bool IsSaveDialog { get; }

    /// <summary>Whether the New Folder affordance is offered (Save As only, and only when the request allows
    /// it — a read-only provider says no).</summary>
    public bool CanCreateDirectories { get; }

    /// <summary>The hierarchy every operation goes through.</summary>
    public IFileSystemProvider FileSystem => _fileSystem;

    /// <summary>
    /// <paramref name="path"/> as it should be SHOWN to the user: the home directory collapsed to <c>~</c>, the
    /// shell convention every terminal user already reads (the design page's <c>~/Projects/assets/</c>). A path
    /// outside home, or a provider with no home place, comes back unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Display only — there is no matching parse step, because both providers' <c>ResolvePath</c> already expand
    /// a leading <c>~</c>. That asymmetry is the point: the text the user edits is the text they typed, and a
    /// path they type with <c>~</c> resolves whether or not it was ever collapsed for them.
    /// </para>
    /// <para>
    /// Compared with <see cref="StringComparison.Ordinal"/>, since the home path and
    /// <see cref="CurrentDirectory"/> both come from the same provider and so share its casing. On a
    /// case-insensitive platform a path that reached the dialog with different casing simply does not collapse
    /// — a cosmetic miss, never a wrong path.
    /// </para>
    /// </remarks>
    /// <param name="path">An absolute, provider-rooted path.</param>
    public string ToDisplayPath(string path)
    {
        if (string.IsNullOrEmpty(path) || _fileSystem.GetPlacePath(FileSystemPlace.Home) is not { Length: > 0 } home)
            return path;

        var separator = _fileSystem.DirectorySeparator;
        var trimmedHome = home.TrimEnd(separator);
        if (trimmedHome.Length == 0)
            return path; // home IS the root; collapsing it would turn every path into "~/…" and say nothing

        if (string.Equals(path.TrimEnd(separator), trimmedHome, StringComparison.Ordinal))
            return "~";

        return path.StartsWith(trimmedHome + separator, StringComparison.Ordinal)
            ? string.Concat("~", path.AsSpan(trimmedHome.Length))
            : path;
    }

    /// <summary>The type-filter rows, in selector order. Never empty — an empty request list becomes a lone
    /// <see cref="FileDialogFilter.AllFiles"/>.</summary>
    public IReadOnlyList<FileDialogFilter> Filters { get; }

    /// <summary>The directory the request asked to open in, before resolution (<see langword="null"/> ⇒ let
    /// <see cref="InitializeAsync"/> pick the home place or the first root).</summary>
    public string? InitialDirectory { get; }

    /// <summary>The result the dialog should close with, or <see langword="null"/> while it is still open.
    /// Set exactly once, immediately before <see cref="Completed"/> is raised.</summary>
    public FileDialogResult? Result { get; private set; }

    /// <summary>
    /// Raised when the view-model has decided the dialog is finished — an accepted file or a cancellation.
    /// The view's only job is to close its window with <see cref="Result"/>; the view-model never touches a
    /// window itself, which is what keeps it testable.
    /// </summary>
    public event EventHandler<FileDialogResult>? Completed;

    /// <summary>
    /// The Save As overwrite confirmation seam: given the colliding file's path, answer whether to replace it.
    /// <see cref="FileSaveDialog"/> fills this with a real <see cref="TaskDialog"/> ("… already exists. Do you
    /// want to replace it?"); a test fills it with a lambda. <see langword="null"/> ⇒ replace without asking,
    /// which is also what <see cref="FileSaveDialogRequest.ConfirmOverwrite"/> <c>= false</c> produces.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? OverwriteConfirmation { get; set; }

    // ───────────────────────────── state ─────────────────────────────

    /// <summary>The directory being listed. Set through <see cref="NavigateAsync"/>, never directly.</summary>
    public string CurrentDirectory
    {
        get => _currentDirectory;
        private set
        {
            if (!SetProperty(ref _currentDirectory, value))
                return;

            OnPropertyChanged(nameof(CanGoUp));
            RebuildSegments();
            RaiseCommandStates();
        }
    }

    /// <summary>The visible listing: hidden entries dropped (unless <see cref="ShowHiddenEntries"/>), the
    /// search text and the type filter applied, folders ahead of files, then ordered by <see cref="SortKey"/>
    /// and <see cref="SortDirection"/>.</summary>
    public ObservableCollection<FileDialogEntry> Entries { get; } = [];

    /// <summary>The breadcrumb's chips, root-first — what <see cref="Controls.ItemsControl.ItemsSource"/> on
    /// the <see cref="Controls.BreadcrumbBar"/> binds to.</summary>
    public ObservableCollection<FileDialogPathSegment> Segments { get; } = [];

    /// <summary>The places rail's bands (Quick access / This PC / Network), filled by
    /// <see cref="InitializeAsync"/>. Bands the provider has nothing for are omitted entirely.</summary>
    public IReadOnlyList<FileDialogPlaceGroup> PlaceGroups
    {
        get => _placeGroups;
        private set => SetProperty(ref _placeGroups, value);
    }

    /// <summary>
    /// The highlighted row. Selecting a FILE mirrors its name into <see cref="FileName"/> (the design page's
    /// footer field follows the list); selecting a DIRECTORY deliberately does not, because the name field
    /// names the file you are opening or saving, not the folder you are standing in.
    /// </summary>
    public FileDialogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value))
                return;

            if (value is { IsDirectory: false })
                FileName = value.Name;

            OnPropertyChanged(nameof(SelectionSummary));
            RaiseCommandStates();
        }
    }

    /// <summary>The File name field's text — what <see cref="AcceptAsync"/> resolves.</summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            if (!SetProperty(ref _fileName, value ?? ""))
                return;

            ErrorMessage = null;
            RaiseCommandStates();
        }
    }

    /// <summary>The search box's text — an ordinal-case-insensitive substring narrowing of the listing (the
    /// design page's <c>⌕ Search assets</c>). Directories are narrowed too: searching is "find this here",
    /// not "filter the file types".</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
                ApplyView();
        }
    }

    /// <summary>The selected type filter. Changing it re-filters the listing and, in a Save dialog, changes
    /// which extension <see cref="AcceptAsync"/> completes an extension-less name with.</summary>
    public FileDialogFilter? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
                ApplyView();
        }
    }

    /// <summary>The column the listing is ordered by (default <see cref="FileDialogSortKey.Name"/>).</summary>
    public FileDialogSortKey SortKey
    {
        get => _sortKey;
        private set
        {
            if (SetProperty(ref _sortKey, value))
                ApplyView();
        }
    }

    /// <summary>The order <see cref="SortKey"/> is applied in. Never <see cref="ListViewSortDirection.None"/>:
    /// a file listing always has SOME order, so the header cycle is ascending ↔ descending only.</summary>
    public ListViewSortDirection SortDirection
    {
        get => _sortDirection;
        private set
        {
            if (SetProperty(ref _sortDirection, value))
                ApplyView();
        }
    }

    /// <summary>The listing's presentation mode — the design page's <c>▤ ▥ ▦</c> switcher.</summary>
    public ListViewViewMode ViewMode
    {
        get => _viewMode;
        set => SetProperty(ref _viewMode, value);
    }

    /// <summary>Whether entries the provider marks hidden are listed.</summary>
    public bool ShowHiddenEntries
    {
        get => _showHiddenEntries;
        set
        {
            if (SetProperty(ref _showHiddenEntries, value))
                ApplyView();
        }
    }

    /// <summary>The inline error line's text (a path that does not resolve, a directory that cannot be
    /// created), or <see langword="null"/> when the footer's error line is hidden.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Whether a listing is in flight. Bound to nothing load-bearing yet; exposed because a slow
    /// share is exactly the case a host may want to show a hint for.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Whether the New Folder row is showing its editor (the design page's inline green row).</summary>
    public bool IsCreatingFolder
    {
        get => _isCreatingFolder;
        private set
        {
            if (!SetProperty(ref _isCreatingFolder, value))
                return;

            RaiseCommandStates();
        }
    }

    /// <summary>The New Folder row's editor text.</summary>
    public string NewFolderName
    {
        get => _newFolderName;
        set => SetProperty(ref _newFolderName, value ?? "");
    }

    /// <summary>
    /// The list's live type-ahead prefix, or empty when none is running. Public because it is the state
    /// <c>Backspace</c> branches on — while it is non-empty Backspace EDITS it, and only once it is empty does
    /// Backspace navigate up (the deliberate correction to the design page, which had the two gestures
    /// collide).
    /// </summary>
    public string TypeAheadBuffer
    {
        get => _typeAheadBuffer;
        private set => SetProperty(ref _typeAheadBuffer, value);
    }

    /// <summary>Whether <see cref="BackCommand"/> has somewhere to go.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Whether <see cref="ForwardCommand"/> has somewhere to go.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Whether <see cref="UpCommand"/> has somewhere to go (false at a root).</summary>
    public bool CanGoUp => _currentDirectory.Length > 0 && _fileSystem.GetParentPath(_currentDirectory) is not null;

    /// <summary>Whether <see cref="AcceptCommand"/> can run — a non-blank name is the whole gate; whether the
    /// name RESOLVES is decided in <see cref="AcceptAsync"/>, where the user can be told why not.</summary>
    public bool CanAccept => !string.IsNullOrWhiteSpace(_fileName);

    /// <summary>The footer's status line — the design page's <c>1 item selected · 1.4 MB</c> / <c>18 items</c>.</summary>
    public string SelectionSummary
    {
        get
        {
            if (_selectedEntry is not { } selected)
                return Entries.Count == 1 ? "1 item" : $"{Entries.Count} items";

            return selected.IsDirectory ? "1 item selected" : $"1 item selected · {selected.SizeText}";
        }
    }

    // ───────────────────────────── commands ─────────────────────────────

    /// <summary>Navigates to a directory. Parameter: a path <see cref="string"/>, a
    /// <see cref="FileDialogPathSegment"/>, a <see cref="FileDialogEntry"/> or a <see cref="FileSystemEntry"/>.</summary>
    public ICommand NavigateCommand { get; }

    /// <summary>Goes back through the navigation history (the design page's <c>◂</c>, <c>Alt+←</c>).</summary>
    public ICommand BackCommand { get; }

    /// <summary>Goes forward through the navigation history (<c>▸</c>, <c>Alt+→</c>).</summary>
    public ICommand ForwardCommand { get; }

    /// <summary>Goes to the containing directory (<c>▴</c>, <c>Alt+↑</c>, and <c>Backspace</c> when no
    /// type-ahead is running).</summary>
    public ICommand UpCommand { get; }

    /// <summary>Re-reads the current directory (<c>↻</c>, <c>F5</c>).</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Opens the New Folder editor (<c>Alt+N</c>; Save As only).</summary>
    public ICommand NewFolderCommand { get; }

    /// <summary>Creates the folder named in <see cref="NewFolderName"/> and refreshes.</summary>
    public ICommand CommitNewFolderCommand { get; }

    /// <summary>Abandons the New Folder editor.</summary>
    public ICommand CancelNewFolderCommand { get; }

    /// <summary>Resolves <see cref="FileName"/> and finishes the dialog (<c>Open</c> / <c>Save</c>,
    /// <c>Alt+O</c>). A name that turns out to be a DIRECTORY navigates into it instead — that is what typing
    /// a folder name into the field means everywhere else, too.</summary>
    public ICommand AcceptCommand { get; }

    /// <summary>Finishes the dialog with <see cref="FileDialogResult.Dismissed"/> (<c>Cancel</c>,
    /// <c>Alt+C</c>, <c>Esc</c> at the dialog's top level).</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Switches the listing's presentation. Parameter: a <see cref="ListViewViewMode"/>.</summary>
    public ICommand SetViewModeCommand { get; }

    /// <summary>Cycles the sort. Parameter: a <see cref="FileDialogSortKey"/>, or the member-path string a
    /// <see cref="Controls.ListViewSortingEventArgs"/> carries (<c>"Name"</c>, <c>"SizeText"</c>, …).</summary>
    public ICommand SortCommand { get; }

    /// <summary>Invokes a row — Enter or a double-click. Parameter: the <see cref="FileDialogEntry"/>.</summary>
    public ICommand ActivateCommand { get; }

    // ───────────────────────────── lifecycle ─────────────────────────────

    /// <summary>
    /// Loads the places rail and the opening listing. Awaited by the dialog before its first frame, and by a
    /// test before its first assertion.
    /// </summary>
    /// <param name="cancellationToken">Abandons the load; never throws through this call.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken);

        await LoadPlacesAsync(linked.Token).ConfigureAwait(true);
        await NavigateCoreAsync(ResolveStartDirectory(), HistoryAction.None, linked.Token).ConfigureAwait(true);
        SelectInitialFileName();
    }

    /// <summary>Navigates to <paramref name="path"/>, pushing the current directory onto the back history.</summary>
    /// <param name="path">The absolute directory path to list.</param>
    /// <param name="cancellationToken">Abandons the listing; never throws through this call.</param>
    public Task NavigateAsync(string path, CancellationToken cancellationToken = default)
        => NavigateCoreAsync(path, HistoryAction.Push, cancellationToken);

    /// <summary>Re-reads the current directory, keeping the selection when the entry survives.</summary>
    /// <param name="cancellationToken">Abandons the listing; never throws through this call.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => NavigateCoreAsync(_currentDirectory, HistoryAction.None, cancellationToken);

    /// <summary>
    /// Invokes <paramref name="entry"/>: a directory is navigated into, a file is chosen (which finishes the
    /// dialog through <see cref="AcceptAsync"/>, overwrite confirmation and all).
    /// </summary>
    /// <param name="entry">The row that was activated.</param>
    /// <param name="cancellationToken">Abandons the operation; never throws through this call.</param>
    public Task ActivateAsync(FileDialogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsDirectory)
            return NavigateAsync(entry.FullPath, cancellationToken);

        FileName = entry.Name;
        return AcceptAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves <see cref="FileName"/> and, when it names a file, finishes the dialog. Returns whether the
    /// dialog is finished — <see langword="false"/> means the name named a directory (navigated into it), did
    /// not resolve (<see cref="ErrorMessage"/> says why), or the overwrite confirmation was declined.
    /// </summary>
    /// <param name="cancellationToken">Abandons the operation; never throws through this call.</param>
    public async Task<bool> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var typed = _fileName.Trim();

        if (typed.Length == 0)
            return false;

        var resolved = _fileSystem.ResolvePath(typed, _currentDirectory);

        if (resolved is null)
        {
            ErrorMessage = $"'{typed}' is not a valid path.";
            return false;
        }

        // A directory in the name field is a NAVIGATION request, not a choice — typing "assets" and pressing
        // Enter has to mean the same thing as double-clicking the assets row, or the field and the list
        // disagree about what a folder is.
        if (_fileSystem.DirectoryExists(resolved))
        {
            FileName = "";
            await NavigateAsync(resolved, cancellationToken).ConfigureAwait(true);
            return false;
        }

        if (IsSaveDialog)
            resolved = ApplyDefaultExtension(resolved);

        var exists = _fileSystem.FileExists(resolved);

        if (!IsSaveDialog && _mustExist && !exists)
        {
            ErrorMessage = $"'{typed}' was not found.";
            return false;
        }

        if (IsSaveDialog && exists && _confirmOverwrite && OverwriteConfirmation is { } confirm)
        {
            if (!await confirm(resolved, cancellationToken).ConfigureAwait(true))
                return false;
        }

        Complete(new FileDialogResult(resolved, _selectedFilter));
        return true;
    }

    /// <summary>Finishes the dialog with <see cref="FileDialogResult.Dismissed"/>.</summary>
    public void Cancel() => Complete(FileDialogResult.Dismissed);

    /// <summary>
    /// Applies text committed in the breadcrumb's edit box. Returns whether the text was ACCEPTED — a
    /// <see langword="false"/> return is what the view turns into
    /// <see cref="Controls.BreadcrumbBarEditEventArgs.KeepEditing"/>, keeping the user in the field with their
    /// text intact rather than silently snapping back to the chips.
    /// </summary>
    /// <param name="text">The raw text the user typed (a <c>~</c> prefix and <c>..</c> segments included —
    /// <see cref="IFileSystemProvider.ResolvePath"/> owns that grammar, not this class).</param>
    /// <remarks>
    /// Only the provider's SYNCHRONOUS predicates are consulted, because this runs on the commit keystroke:
    /// the answer must be immediate, and the navigation it kicks off reports any later failure through
    /// <see cref="ErrorMessage"/> in the usual way.
    /// </remarks>
    public bool CommitPathText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();

        if (trimmed.Length == 0)
            return false;

        var resolved = _fileSystem.ResolvePath(trimmed, _currentDirectory);

        if (resolved is null)
        {
            ErrorMessage = $"'{trimmed}' is not a valid path.";
            return false;
        }

        if (_fileSystem.DirectoryExists(resolved))
        {
            ErrorMessage = null;
            Run(NavigateAsync(resolved));
            return true;
        }

        if (_fileSystem.FileExists(resolved))
        {
            // A file in the path bar means "this one": Open takes it straight through Accept, Save merely
            // parks the name in the field (the design page: "Save dialog: place the name in the filename
            // field and focus it") because the user may still want to change the folder or the type.
            ErrorMessage = null;
            FileName = resolved;

            if (!IsSaveDialog)
                Run(AcceptAsync());

            return true;
        }

        // A not-yet-existing name under a real directory is a legitimate Save target, and typing one is how
        // you save somewhere new; Open has nothing to do with it and says so.
        if (IsSaveDialog && _fileSystem.GetParentPath(resolved) is { } parent && _fileSystem.DirectoryExists(parent))
        {
            ErrorMessage = null;
            FileName = resolved;
            return true;
        }

        ErrorMessage = $"'{trimmed}' was not found.";
        return false;
    }

    /// <summary>Cycles <paramref name="key"/>: a new key sorts ascending, the current key flips direction.</summary>
    /// <param name="key">The column to order by.</param>
    /// <remarks>Ascending ↔ descending only — a third "unsorted" state reads as a broken click in a file
    /// listing, where some order is always in effect (the same rule <see cref="ListView.CycleSort"/> uses).</remarks>
    public void Sort(FileDialogSortKey key)
    {
        if (key == _sortKey)
        {
            SortDirection = _sortDirection == ListViewSortDirection.Ascending
                ? ListViewSortDirection.Descending
                : ListViewSortDirection.Ascending;

            return;
        }

        _sortDirection = ListViewSortDirection.Ascending;
        OnPropertyChanged(nameof(SortDirection));
        SortKey = key;
    }

    // ───────────────────────────── type-ahead ─────────────────────────────

    /// <summary>
    /// Feeds one printable keystroke to the list's type-ahead. Returns whether it MATCHED — a miss leaves the
    /// buffer untouched (the framework's own <c>TextSearchController</c> rule: a mistyped character must not
    /// poison the prefix) and lets the caller leave the key unhandled.
    /// </summary>
    /// <param name="typed">The typed text, normally a single character.</param>
    public bool TypeAhead(string typed)
    {
        if (string.IsNullOrEmpty(typed) || Entries.Count == 0)
            return false;

        var now = _timeProvider.GetUtcNow();

        // The idle window is measured, not timed: a UITimer here would make the view-model host-affine and
        // untestable, and an injected TimeProvider gives a headless test exact control over the boundary.
        if (_typeAheadBuffer.Length > 0 && now - _lastTypeAheadAt > TypeAheadIdleTimeout)
            TypeAheadBuffer = "";

        var candidate = _typeAheadBuffer + typed;

        if (FindByPrefix(candidate) is not { } match)
            return false;

        _lastTypeAheadAt = now;
        TypeAheadBuffer = candidate;
        SelectedEntry = match;
        return true;
    }

    /// <summary>
    /// Applies <c>Backspace</c> to the type-ahead buffer. Returns whether the buffer CONSUMED the key —
    /// <see langword="false"/> means no type-ahead is running and the caller should navigate up instead.
    /// This is the whole of the Backspace rule, in one place, so the view cannot get it wrong.
    /// </summary>
    public bool TypeAheadBackspace()
    {
        if (_typeAheadBuffer.Length == 0)
            return false;

        var candidate = _typeAheadBuffer[..^1];
        _lastTypeAheadAt = _timeProvider.GetUtcNow();
        TypeAheadBuffer = candidate;

        if (candidate.Length > 0 && FindByPrefix(candidate) is { } match)
            SelectedEntry = match;

        return true;
    }

    /// <summary>Clears the type-ahead buffer — navigation, focus leaving the list, and <c>Esc</c> all do this,
    /// so a stale prefix can never turn the next Backspace into a no-op.</summary>
    public void ResetTypeAhead() => TypeAheadBuffer = "";

    // ───────────────────────────── the completion seam ─────────────────────────────

    /// <summary>
    /// The last listing captured for <paramref name="directoryPath"/>, or <see langword="null"/> when none is
    /// cached. This is what makes the path bar's completion provider — which
    /// <see cref="Controls.ICompletionProvider"/> requires to be SYNCHRONOUS — possible over an asynchronous
    /// file system: it answers from the cache and asks <see cref="PrimeListing"/> to fill a miss.
    /// </summary>
    /// <param name="directoryPath">The directory to look up.</param>
    public IReadOnlyList<FileSystemEntry>? TryGetCachedListing(string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        return _listingCache.GetValueOrDefault(directoryPath);
    }

    /// <summary>
    /// Enumerates <paramref name="directoryPath"/> into the cache if it is not there already, then invokes
    /// <paramref name="onLoaded"/>. The completion popup's documented shape for a slow source: answer with
    /// what you have now, kick the work off, and call <c>Refresh()</c> from the continuation, so the late
    /// answer is scored against the CURRENT text and there is no stale-result window to guard.
    /// </summary>
    /// <param name="directoryPath">The directory to enumerate.</param>
    /// <param name="onLoaded">Invoked on the calling context once the listing is cached (also on failure,
    /// where the cache gains an empty listing — the popup then simply stays shut).</param>
    public void PrimeListing(string directoryPath, Action? onLoaded = null)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        if (_listingCache.ContainsKey(directoryPath))
        {
            onLoaded?.Invoke();
            return;
        }

        Run(LoadIntoCacheAsync(directoryPath, onLoaded));
    }

    private async Task LoadIntoCacheAsync(string directoryPath, Action? onLoaded)
    {
        IReadOnlyList<FileSystemEntry> entries;

        try
        {
            entries = await _fileSystem.GetEntriesAsync(directoryPath, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            entries = []; // a provider that throws where the contract says "return empty" must not kill the popup
        }

        CacheListing(directoryPath, entries);
        onLoaded?.Invoke();
    }

    // ───────────────────────────── new folder ─────────────────────────────

    /// <summary>Opens the New Folder editor seeded with a non-colliding default name.</summary>
    public void BeginCreateFolder()
    {
        if (!CanCreateDirectories || IsCreatingFolder)
            return;

        ErrorMessage = null;
        NewFolderName = UniqueFolderName();
        IsCreatingFolder = true;
    }

    /// <summary>Abandons the New Folder editor.</summary>
    public void CancelCreateFolder()
    {
        NewFolderName = "";
        IsCreatingFolder = false;
    }

    /// <summary>
    /// Creates the folder named in <see cref="NewFolderName"/> under the current directory, refreshes the
    /// listing, and selects the new row. Returns whether it was created; a failure leaves the editor open with
    /// the provider's message in <see cref="ErrorMessage"/>, because the message is the only thing that tells
    /// the user whether to rename or to give up.
    /// </summary>
    /// <param name="cancellationToken">Abandons the creation; never throws through this call.</param>
    public async Task<bool> CommitCreateFolderAsync(CancellationToken cancellationToken = default)
    {
        var name = _newFolderName.Trim();

        if (!IsCreatingFolder || name.Length == 0)
            return false;

        using var linked = Link(cancellationToken);

        try
        {
            var created = await _fileSystem.CreateDirectoryAsync(_currentDirectory, name, linked.Token)
                                           .ConfigureAwait(true);

            ErrorMessage = null;

            // Refresh and select FIRST, close the editor LAST: closing it is what hands focus back to the
            // listing, and doing that before the new row exists would land focus on a container the refresh
            // is about to destroy.
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
            SelectedEntry = FindByPath(created.FullPath);

            NewFolderName = "";
            IsCreatingFolder = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not create '{name}': {e.Message}";
            return false;
        }
    }

    // ───────────────────────────── navigation core ─────────────────────────────

    private enum HistoryAction
    {
        /// <summary>Push the outgoing directory onto Back and clear Forward (the ordinary hop).</summary>
        Push,

        /// <summary>Push the outgoing directory onto Forward (a Back hop).</summary>
        Back,

        /// <summary>Push the outgoing directory onto Back without clearing Forward (a Forward hop).</summary>
        Forward,

        /// <summary>Leave the history alone (the initial load and every refresh).</summary>
        None
    }

    private async Task NavigateCoreAsync(string path, HistoryAction history, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path))
            return;

        // One listing at a time: the previous enumeration is abandoned rather than raced, so a user clicking
        // impatiently through a slow share can never have an older answer land on top of a newer one.
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);

        var token = _navigation.Token;
        var previous = _currentDirectory;

        IReadOnlyList<FileSystemEntry> entries;
        IsBusy = true;

        try
        {
            entries = await _fileSystem.GetEntriesAsync(path, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // superseded (or the dialog is closing) — the winning navigation owns the state
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not open '{path}': {e.Message}";
            return;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsBusy = false;
        }

        if (token.IsCancellationRequested)
            return;

        CacheListing(path, entries);
        RecordHistory(history, previous, path);

        ErrorMessage = null;
        ResetTypeAhead();
        CancelCreateFolder();

        _allEntries.Clear();

        foreach (var entry in entries)
            _allEntries.Add(new FileDialogEntry(entry));

        CurrentDirectory = path;
        ApplyView();
    }

    private void RecordHistory(HistoryAction action, string previous, string next)
    {
        if (action == HistoryAction.None || previous.Length == 0 || string.Equals(previous, next, StringComparison.Ordinal))
            return;

        switch (action)
        {
            case HistoryAction.Push:
                _back.Add(previous);
                _forward.Clear();
                break;
            case HistoryAction.Back:
                _forward.Add(previous);
                break;
            case HistoryAction.Forward:
                _back.Add(previous);
                break;
        }

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        RaiseCommandStates();
    }

    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
            return;

        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        await NavigateCoreAsync(target, HistoryAction.Back, default).ConfigureAwait(true);
    }

    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
            return;

        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        await NavigateCoreAsync(target, HistoryAction.Forward, default).ConfigureAwait(true);
    }

    private async Task GoUpAsync()
    {
        if (_fileSystem.GetParentPath(_currentDirectory) is not { } parent)
            return;

        // Coming up from a child selects that child, which is what makes ▴ + ▾ a round trip rather than a
        // one-way trip that loses your place (Explorer and every file manager since do the same).
        var child = _currentDirectory;
        await NavigateCoreAsync(parent, HistoryAction.Push, default).ConfigureAwait(true);
        SelectedEntry = FindByPath(child);
    }

    private string ResolveStartDirectory()
    {
        if (InitialDirectory is { Length: > 0 } requested && _fileSystem.DirectoryExists(requested))
            return requested;

        if (_fileSystem.GetPlacePath(FileSystemPlace.Home) is { Length: > 0 } home && _fileSystem.DirectoryExists(home))
            return home;

        foreach (var group in PlaceGroups)
        {
            foreach (var place in group.Places)
            {
                if (_fileSystem.DirectoryExists(place.FullPath))
                    return place.FullPath;
            }
        }

        // Nothing resolved: fall back to the requested path verbatim so the failure shows up as an empty
        // listing at the path the caller named, rather than as a dialog silently pointed somewhere else.
        return InitialDirectory ?? "";
    }

    private async Task LoadPlacesAsync(CancellationToken cancellationToken)
    {
        var groups = new List<FileDialogPlaceGroup>(3);

        try
        {
            var quick = await _fileSystem.GetPlacesAsync(cancellationToken).ConfigureAwait(true);

            if (quick.Count > 0)
                groups.Add(new FileDialogPlaceGroup("Quick access", quick));

            var roots = await _fileSystem.GetRootsAsync(cancellationToken).ConfigureAwait(true);
            var local = new List<FileSystemEntry>(roots.Count);
            var network = new List<FileSystemEntry>();

            foreach (var root in roots)
            {
                // The design page's rail bands: everything reachable locally is "This PC"; shares and
                // cloud-backed volumes get their own band, because they are the ones that can be OFFLINE and a
                // user scanning for "why is my drive missing" looks at that band first.
                if (root.Place is FileSystemPlace.NetworkDrive or FileSystemPlace.CloudDrive)
                    network.Add(root);
                else
                    local.Add(root);
            }

            if (local.Count > 0)
                groups.Add(new FileDialogPlaceGroup("This PC", local));

            if (network.Count > 0)
                groups.Add(new FileDialogPlaceGroup("Network", network));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // A rail that cannot be built is a degraded dialog, not a failed one — the listing still works.
        }

        PlaceGroups = groups;
    }

    private void SelectInitialFileName()
    {
        if (_fileName.Length == 0)
            return;

        foreach (var entry in Entries)
        {
            if (!entry.IsDirectory && string.Equals(entry.Name, _fileName, StringComparison.OrdinalIgnoreCase))
            {
                SelectedEntry = entry;
                return;
            }
        }
    }

    // ───────────────────────────── the view projection ─────────────────────────────
    //
    // One funnel for hidden-filtering, search, the type filter and the sort, re-run whenever any of them
    // changes. Rebuilding the whole ObservableCollection rather than diffing is deliberate: a listing is
    // bounded by what a directory holds, the ListView is non-virtualizing in v1 anyway, and a diff would have
    // to be perfect to be worth anything — a wrong one shows the user a row that is not there.
    private void ApplyView()
    {
        var selectedPath = _selectedEntry?.FullPath;
        var visible = new List<FileDialogEntry>(_allEntries.Count);

        foreach (var entry in _allEntries)
        {
            if (!_showHiddenEntries && entry.Entry.IsHidden)
                continue;

            if (_searchText.Length > 0 && !entry.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!entry.IsDirectory && _selectedFilter is { } filter && !filter.Matches(entry.Name))
                continue;

            visible.Add(entry);
        }

        visible.Sort(Compare);

        Entries.Clear();

        foreach (var entry in visible)
            Entries.Add(entry);

        // The selection is restored by PATH, not by index: the row may have moved (a sort) or vanished (a
        // filter), and an index-preserved selection would silently point at a different file.
        var restored = selectedPath is null ? null : FindByPath(selectedPath);

        if (!ReferenceEquals(restored, _selectedEntry))
            SelectedEntry = restored;

        OnPropertyChanged(nameof(SelectionSummary));
    }

    // Folders before files, ALWAYS — ahead of the sort key and unaffected by the direction, exactly as the
    // design page requires ("Folders sort first", and in the wrapping views "Folders still group ahead of
    // files"). Descending by name must not float the files above the folders; it reverses within each band.
    private int Compare(FileDialogEntry left, FileDialogEntry right)
    {
        if (left.IsDirectory != right.IsDirectory)
            return left.IsDirectory ? -1 : 1;

        var order = _sortKey switch
        {
            FileDialogSortKey.Size => Nullable.Compare(left.Entry.Size, right.Entry.Size),
            FileDialogSortKey.Type => string.Compare(left.TypeText, right.TypeText, StringComparison.OrdinalIgnoreCase),
            FileDialogSortKey.Modified => Nullable.Compare(left.Entry.LastModified, right.Entry.LastModified),
            _ => 0
        };

        if (order == 0)
            order = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

        return _sortDirection == ListViewSortDirection.Descending ? -order : order;
    }

    private void RebuildSegments()
    {
        var trail = new List<FileDialogPathSegment>();

        for (var path = _currentDirectory; path is { Length: > 0 }; path = _fileSystem.GetParentPath(path))
        {
            trail.Add(new FileDialogPathSegment(SegmentName(path), path));

            if (_fileSystem.GetParentPath(path) is null)
                break;
        }

        trail.Reverse();

        Segments.Clear();

        foreach (var segment in trail)
            Segments.Add(segment);
    }

    // A segment shows its friendly PLACE name when it is one (the design page's "▣ Home ▸ Projects ▸ assets",
    // not "/Users/ada ▸ Projects ▸ assets"); otherwise the last path component, falling back to the whole path
    // for a root, whose last component is empty by construction.
    private string SegmentName(string path)
    {
        foreach (var group in PlaceGroups)
        {
            foreach (var place in group.Places)
            {
                if (string.Equals(place.FullPath, path, StringComparison.Ordinal))
                    return place.Name;
            }
        }

        var separator = _fileSystem.DirectorySeparator;
        var trimmed = path.TrimEnd(separator);
        var index = trimmed.LastIndexOf(separator);
        var leaf = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        return leaf.Length > 0 ? leaf : path;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private FileDialogEntry? FindByPath(string fullPath)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.FullPath, fullPath, StringComparison.Ordinal))
                return entry;
        }

        return null;
    }

    // The framework's type-ahead rule verbatim: search forward from the CURRENT row (inclusive, so extending a
    // prefix keeps the row it already found) and wrap once.
    private FileDialogEntry? FindByPrefix(string prefix)
    {
        var count = Entries.Count;

        if (count == 0)
            return null;

        var start = _selectedEntry is { } selected ? Math.Max(0, Entries.IndexOf(selected)) : 0;

        for (var i = 0; i < count; i++)
        {
            var candidate = Entries[(start + i) % count];

            if (candidate.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private string ApplyDefaultExtension(string path)
    {
        var extension = _defaultExtension ?? _selectedFilter?.DefaultExtension;

        if (extension is not { Length: > 1 })
            return path;

        var separator = _fileSystem.DirectorySeparator;
        var leafStart = path.LastIndexOf(separator) + 1;

        // Only an extension-LESS leaf is completed. "report.png" keeps its extension, and so does "v1.2" —
        // guessing that a dotted name is missing an extension is how a save dialog produces "v1.2.png".
        return path.IndexOf('.', leafStart) >= 0 ? path : path + extension;
    }

    private string UniqueFolderName()
    {
        const string seed = "New folder";

        if (!Exists(seed))
            return seed;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{seed} {i}";

            if (!Exists(candidate))
                return candidate;
        }

        return seed;

        bool Exists(string name)
        {
            foreach (var entry in _allEntries)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    private string ResolveNavigationTarget(object? parameter) => parameter switch
    {
        string path => path,
        FileDialogPathSegment segment => segment.Path,
        FileDialogEntry { IsDirectory: true } entry => entry.FullPath,
        FileSystemEntry { IsDirectory: true } entry => entry.FullPath,
        _ => ""
    };

    private static bool TryCoerceSortKey(object? parameter, out FileDialogSortKey key)
    {
        switch (parameter)
        {
            case FileDialogSortKey typed:
                key = typed;
                return true;

            // The ListView reports its sort through ListViewSortingEventArgs.SortMemberPath — the column's
            // binding path — so the string form accepts both the key names and the row view-model's member
            // names, and the view never has to keep a second lookup table in sync.
            case string path:
                key = path switch
                {
                    "Size" or "SizeText" => FileDialogSortKey.Size,
                    "Type" or "TypeText" => FileDialogSortKey.Type,
                    "Modified" or "ModifiedText" => FileDialogSortKey.Modified,
                    _ => FileDialogSortKey.Name
                };
                return true;

            default:
                key = FileDialogSortKey.Name;
                return false;
        }
    }

    private void CacheListing(string directoryPath, IReadOnlyList<FileSystemEntry> entries)
    {
        if (!_listingCache.ContainsKey(directoryPath))
        {
            _listingCacheOrder.Enqueue(directoryPath);

            while (_listingCacheOrder.Count > ListingCacheCapacity)
                _listingCache.Remove(_listingCacheOrder.Dequeue());
        }

        _listingCache[directoryPath] = entries;
    }

    private void Complete(FileDialogResult result)
    {
        if (Result is not null)
            return; // one outcome per dialog: a late Enter must not overwrite the Cancel that already won

        Result = result;
        Completed?.Invoke(this, result);
    }

    private CancellationTokenSource Link(CancellationToken cancellationToken)
        => CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);

    // Commands are void-returning, so their asynchronous bodies are launched and forgotten HERE and nowhere
    // else: every failure path inside the awaited methods already lands in ErrorMessage, so the only thing
    // this has to guarantee is that nothing escapes onto the dispatcher as an unobserved fault.
    private static void Run(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception; // observe a synchronous failure (a provider that throws from a hot path)
            return;
        }

        _ = task.ContinueWith(static t => _ = t.Exception,
                              CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
    }

    private void RaiseCommandStates()
    {
        (BackCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (ForwardCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (UpCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (AcceptCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (NewFolderCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (CommitNewFolderCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (CancelNewFolderCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Cancels every in-flight listing. Called by the dialog when its window closes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        _navigation?.Dispose();
        _lifetime.Dispose();
    }
}
