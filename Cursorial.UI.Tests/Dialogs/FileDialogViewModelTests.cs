using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;

using Microsoft.Extensions.Time.Testing;

namespace Cursorial.Tests.UI.Dialogs;

/// <summary>
/// <see cref="FileDialogViewModel"/> driven with <b>no window at all</b> — which is the whole point of putting
/// the dialogs' behaviour in a view-model. Every rule the design page states about browsing, ordering,
/// filtering, the keyboard's type-ahead buffer, path-edit commits, folder creation and what Accept means is
/// asserted here against an <see cref="InMemoryFileSystemProvider"/>, so the rendered-frame tests in
/// <c>FileDialogTests</c> only have to prove the wiring.
/// <para>
/// The provider answers synchronously, so a completed <c>ValueTask</c> continues inline and each awaited call
/// is settled by the time it returns: no dispatcher, no pumping, no timing.
/// </para>
/// </summary>
public sealed class FileDialogViewModelTests
{
    private const string Assets = "/home/ada/Projects/assets";

    private static FileOpenDialogRequest OpenRequest(IFileSystemProvider provider,
                                                     string? directory = Assets,
                                                     params FileDialogFilter[] filters)
        => new("Open File")
           {
               FileSystem = provider,
               InitialDirectory = directory,
               Filters = filters
           };

    private static async Task<FileDialogViewModel> OpenAsync(IFileSystemProvider? provider = null,
                                                             string? directory = Assets,
                                                             TimeProvider? time = null,
                                                             params FileDialogFilter[] filters)
    {
        var model = new FileDialogViewModel(OpenRequest(provider ?? InMemoryFileSystemProvider.CreateSample(), directory, filters), time);
        await model.InitializeAsync();
        return model;
    }

    private static string[] Names(FileDialogViewModel model)
    {
        var names = new string[model.Entries.Count];

        for (var i = 0; i < names.Length; i++)
            names[i] = model.Entries[i].Name;

        return names;
    }

    // ── listing, ordering, filtering ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_ListsTheDirectory_FoldersFirstThenNameOrder()
    {
        using var model = await OpenAsync();

        Assert.Equal(Assets, model.CurrentDirectory);
        Assert.Equal(["icons", "textures", "atlas.lua", "build.sh", "credits.pdf", "gradients.json",
                      "hero-banner.png", "LICENSE", "logo.svg", "palette.aco", "template.json", "thumbnail.jpg"],
                     Names(model));
    }

    [Fact]
    public async Task Initialize_HidesHiddenEntriesUntilAsked()
    {
        using var model = await OpenAsync();

        Assert.DoesNotContain(".gitkeep", Names(model));

        model.ShowHiddenEntries = true;
        Assert.Contains(".gitkeep", Names(model));
    }

    [Fact]
    public async Task Sort_Descending_ReversesWithinTheBands_ButFoldersStayFirst()
    {
        using var model = await OpenAsync();

        model.Sort(FileDialogSortKey.Name); // same key ⇒ flip direction

        Assert.Equal(ListViewSortDirection.Descending, model.SortDirection);
        Assert.Equal(["textures", "icons"], Names(model)[..2]);
        Assert.Equal("atlas.lua", Names(model)[^1]);
    }

    [Fact]
    public async Task Sort_ByANewKey_StartsAscending()
    {
        using var model = await OpenAsync();

        model.Sort(FileDialogSortKey.Name);  // Name ▼
        model.Sort(FileDialogSortKey.Size);  // a different column always starts ▲

        Assert.Equal(FileDialogSortKey.Size, model.SortKey);
        Assert.Equal(ListViewSortDirection.Ascending, model.SortDirection);

        // Directories have no size and keep name order among themselves; the files climb by byte count.
        Assert.Equal(["icons", "textures", "LICENSE", "build.sh"], Names(model)[..4]);
    }

    [Fact]
    public async Task SortCommand_AcceptsTheMemberPathTheListViewReports()
    {
        using var model = await OpenAsync();

        // ListViewSortingEventArgs carries the column's SortMemberPath, so the view never needs a second
        // lookup table: both the key names and the row view-model's member names resolve.
        model.SortCommand.Execute("Modified");
        Assert.Equal(FileDialogSortKey.Modified, model.SortKey);

        model.SortCommand.Execute("SizeText");
        Assert.Equal(FileDialogSortKey.Size, model.SortKey);

        model.SortCommand.Execute("Name");
        Assert.Equal(FileDialogSortKey.Name, model.SortKey);
    }

    [Fact]
    public async Task SetViewModeCommand_SwitchesThePresentation()
    {
        using var model = await OpenAsync();

        Assert.Equal(ListViewViewMode.Details, model.ViewMode);

        model.SetViewModeCommand.Execute(ListViewViewMode.SmallIcons);
        Assert.Equal(ListViewViewMode.SmallIcons, model.ViewMode);

        model.SetViewModeCommand.Execute(ListViewViewMode.Tiles);
        Assert.Equal(ListViewViewMode.Tiles, model.ViewMode);
    }

    [Fact]
    public async Task SelectedFilter_NarrowsFilesButNeverFolders()
    {
        using var model = await OpenAsync(filters: new FileDialogFilter("Images", "*.png;*.jpg;*.svg"));

        Assert.Equal(["icons", "textures", "hero-banner.png", "logo.svg", "thumbnail.jpg"], Names(model));

        model.SelectedFilter = FileDialogFilter.AllFiles;
        Assert.Contains("credits.pdf", Names(model));
    }

    [Fact]
    public async Task SearchText_NarrowsByOrdinalCaseInsensitiveSubstring()
    {
        using var model = await OpenAsync();

        model.SearchText = "GRAD";
        Assert.Equal(["gradients.json"], Names(model));

        model.SearchText = "";
        Assert.Equal(12, model.Entries.Count);
    }

    [Fact]
    public async Task SelectionSummary_ReportsTheCountThenTheSelection()
    {
        using var model = await OpenAsync();

        Assert.Equal("12 items", model.SelectionSummary);

        model.SelectedEntry = model.Entries.First(e => e.Name == "hero-banner.png");
        Assert.Equal("1 item selected · 1.4 MB", model.SelectionSummary);

        model.SelectedEntry = model.Entries.First(e => e.Name == "icons");
        Assert.Equal("1 item selected", model.SelectionSummary);
    }

    [Fact]
    public async Task SelectingAFile_MirrorsItsNameIntoTheField_ButAFolderDoesNot()
    {
        using var model = await OpenAsync();

        model.SelectedEntry = model.Entries.First(e => e.Name == "logo.svg");
        Assert.Equal("logo.svg", model.FileName);

        model.SelectedEntry = model.Entries.First(e => e.Name == "textures");
        Assert.Equal("logo.svg", model.FileName); // a folder is where you are, not what you are naming
    }

    // ── navigation + history ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Navigate_Back_Forward_And_Up_TrackTheHistory()
    {
        using var model = await OpenAsync();

        Assert.False(model.CanGoBack);
        Assert.False(model.CanGoForward);

        await model.NavigateAsync($"{Assets}/textures");
        Assert.Equal($"{Assets}/textures", model.CurrentDirectory);
        Assert.True(model.CanGoBack);

        model.BackCommand.Execute(null);
        Assert.Equal(Assets, model.CurrentDirectory);
        Assert.True(model.CanGoForward);

        model.ForwardCommand.Execute(null);
        Assert.Equal($"{Assets}/textures", model.CurrentDirectory);

        model.UpCommand.Execute(null);
        Assert.Equal(Assets, model.CurrentDirectory);

        // Coming up from a child selects it, so ▴ then ▾ is a round trip rather than a lost place.
        Assert.Equal("textures", model.SelectedEntry?.Name);
    }

    [Fact]
    public async Task Segments_AreTheBreadcrumbTrail_RootFirst_WithPlaceNames()
    {
        using var model = await OpenAsync();

        // The root chip shows its PLACE name ("▤ Local (C:)") and so does the home folder ("▣ Home") — the
        // design page's trail, not a raw path split.
        Assert.Equal(["Local (C:)", "home", "Home", "Projects", "assets"], model.Segments.Select(s => s.Name).ToArray());
        Assert.Equal(Assets, model.Segments[^1].Path);
    }

    // ── the type-ahead buffer + the Backspace rule ───────────────────────────────────────────────

    [Fact]
    public async Task TypeAhead_ExtendsThePrefix_AndAMissLeavesTheBufferAlone()
    {
        using var model = await OpenAsync(time: new FakeTimeProvider());

        Assert.True(model.TypeAhead("t"));
        Assert.Equal("textures", model.SelectedEntry?.Name);

        Assert.True(model.TypeAhead("e"));
        Assert.Equal("te", model.TypeAheadBuffer);
        Assert.Equal("textures", model.SelectedEntry?.Name);

        Assert.False(model.TypeAhead("z"));
        Assert.Equal("te", model.TypeAheadBuffer); // a mistyped character must not poison the prefix
    }

    [Fact]
    public async Task TypeAhead_StartsFreshAfterTheIdleWindow()
    {
        var time = new FakeTimeProvider();
        using var model = await OpenAsync(time: time);

        Assert.True(model.TypeAhead("t"));
        time.Advance(FileDialogViewModel.TypeAheadIdleTimeout + TimeSpan.FromMilliseconds(1));

        Assert.True(model.TypeAhead("i"));
        Assert.Equal("i", model.TypeAheadBuffer); // not "ti" — the idle window expired
        Assert.Equal("icons", model.SelectedEntry?.Name);
    }

    [Fact]
    public async Task Backspace_EditsTheTypeAheadBuffer_AndOnlyNavigatesUpOnceItIsEmpty()
    {
        var time = new FakeTimeProvider();
        using var model = await OpenAsync(time: time);

        model.TypeAhead("t");
        model.TypeAhead("e");

        Assert.True(model.TypeAheadBackspace());   // "te" → "t": consumed by the buffer
        Assert.Equal("t", model.TypeAheadBuffer);

        Assert.True(model.TypeAheadBackspace());   // "t" → "": still consumed
        Assert.Equal("", model.TypeAheadBuffer);

        Assert.False(model.TypeAheadBackspace());  // empty ⇒ the caller navigates up instead
        Assert.Equal(Assets, model.CurrentDirectory);
    }

    [Fact]
    public async Task Navigating_ClearsTheTypeAheadBuffer()
    {
        using var model = await OpenAsync(time: new FakeTimeProvider());

        model.TypeAhead("t");
        await model.NavigateAsync($"{Assets}/icons");

        Assert.Equal("", model.TypeAheadBuffer);
    }

    // ── the path bar's commit ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitPathText_NavigatesToADirectory()
    {
        using var model = await OpenAsync();

        Assert.True(model.CommitPathText("~/Projects/assets/textures"));
        Assert.Equal($"{Assets}/textures", model.CurrentDirectory);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task CommitPathText_ResolvesDotDotAgainstTheCurrentDirectory()
    {
        using var model = await OpenAsync();

        Assert.True(model.CommitPathText("../"));
        Assert.Equal("/home/ada/Projects", model.CurrentDirectory);
    }

    [Fact]
    public async Task CommitPathText_OnAFile_AcceptsIt_InAnOpenDialog()
    {
        using var model = await OpenAsync();

        Assert.True(model.CommitPathText($"{Assets}/logo.svg"));
        Assert.Equal($"{Assets}/logo.svg", model.Result?.FilePath);
    }

    [Fact]
    public async Task CommitPathText_OnAMissingPath_Rejects_SoTheBarKeepsEditing()
    {
        using var model = await OpenAsync();

        Assert.False(model.CommitPathText($"{Assets}/nowhere/at/all"));
        Assert.Contains("not found", model.ErrorMessage);
        Assert.Equal(Assets, model.CurrentDirectory);
    }

    // ── Accept / Cancel ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_OnAnExistingFile_CompletesWithThePathAndTheFilter()
    {
        var images = new FileDialogFilter("Images", "*.png;*.jpg;*.svg");
        using var model = await OpenAsync(filters: images);

        model.FileName = "hero-banner.png";
        Assert.True(await model.AcceptAsync());

        Assert.Equal($"{Assets}/hero-banner.png", model.Result?.FilePath);
        Assert.Equal(images, model.Result?.Filter);
        Assert.False(model.Result?.IsDismissed);
    }

    [Fact]
    public async Task Accept_OnADirectoryName_NavigatesInsteadOfCompleting()
    {
        using var model = await OpenAsync();

        model.FileName = "textures";
        Assert.False(await model.AcceptAsync());

        Assert.Null(model.Result);
        Assert.Equal($"{Assets}/textures", model.CurrentDirectory);
        Assert.Equal("", model.FileName);
    }

    [Fact]
    public async Task Accept_OnAMissingFile_Reports_WhenMustExist()
    {
        using var model = await OpenAsync();

        model.FileName = "nowhere.png";
        Assert.False(await model.AcceptAsync());

        Assert.Null(model.Result);
        Assert.Contains("not found", model.ErrorMessage);
    }

    [Fact]
    public async Task Accept_OnAMissingFile_Succeeds_WhenMustExistIsOff()
    {
        var model = new FileDialogViewModel(new FileOpenDialogRequest("Open File")
                                            {
                                                FileSystem = InMemoryFileSystemProvider.CreateSample(),
                                                InitialDirectory = Assets,
                                                MustExist = false
                                            });

        using (model)
        {
            await model.InitializeAsync();
            model.FileName = "brand-new.png";

            Assert.True(await model.AcceptAsync());
            Assert.Equal($"{Assets}/brand-new.png", model.Result?.FilePath);
        }
    }

    [Fact]
    public async Task Cancel_CompletesDismissed()
    {
        using var model = await OpenAsync();

        FileDialogResult? reported = null;
        model.Completed += (_, result) => reported = result;

        model.Cancel();

        Assert.True(model.Result?.IsDismissed);
        Assert.Same(FileDialogResult.Dismissed, reported);
    }

    [Fact]
    public async Task Cancel_AfterAccept_DoesNotOverwriteTheOutcome()
    {
        using var model = await OpenAsync();

        model.FileName = "logo.svg";
        await model.AcceptAsync();
        model.Cancel();

        Assert.Equal($"{Assets}/logo.svg", model.Result?.FilePath);
    }

    // ── Save As ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<FileDialogViewModel> SaveAsync(IFileSystemProvider provider,
                                                             params FileDialogFilter[] filters)
    {
        var model = new FileDialogViewModel(new FileSaveDialogRequest("Save As")
                                            {
                                                FileSystem = provider,
                                                InitialDirectory = Assets,
                                                Filters = filters
                                            });

        await model.InitializeAsync();
        return model;
    }

    [Fact]
    public async Task Save_AppendsTheSelectedFiltersExtensionToAnExtensionlessName()
    {
        using var model = await SaveAsync(InMemoryFileSystemProvider.CreateSample(), new FileDialogFilter("PNG image", "*.png"));

        model.FileName = "report-final";
        Assert.True(await model.AcceptAsync());
        Assert.Equal($"{Assets}/report-final.png", model.Result?.FilePath);
    }

    [Fact]
    public async Task Save_LeavesAnAlreadyExtendedNameAlone()
    {
        using var model = await SaveAsync(InMemoryFileSystemProvider.CreateSample(), new FileDialogFilter("PNG image", "*.png"));

        model.FileName = "notes.md";
        Assert.True(await model.AcceptAsync());
        Assert.Equal($"{Assets}/notes.md", model.Result?.FilePath);
    }

    [Fact]
    public async Task Save_OnACollision_AsksBeforeReplacing_AndADeclinedAnswerKeepsTheDialogOpen()
    {
        using var model = await SaveAsync(InMemoryFileSystemProvider.CreateSample(), new FileDialogFilter("PNG image", "*.png"));

        var asked = 0;
        var answer = false;
        model.OverwriteConfirmation = (_, _) => { asked++; return Task.FromResult(answer); };

        model.FileName = "hero-banner.png";

        Assert.False(await model.AcceptAsync());
        Assert.Equal(1, asked);
        Assert.Null(model.Result);

        answer = true;
        Assert.True(await model.AcceptAsync());
        Assert.Equal(2, asked);
        Assert.Equal($"{Assets}/hero-banner.png", model.Result?.FilePath);
    }

    [Fact]
    public async Task Save_OnANewName_NeverAsks()
    {
        using var model = await SaveAsync(InMemoryFileSystemProvider.CreateSample(), new FileDialogFilter("PNG image", "*.png"));

        var asked = false;
        model.OverwriteConfirmation = (_, _) => { asked = true; return Task.FromResult(true); };

        model.FileName = "brand-new";
        Assert.True(await model.AcceptAsync());
        Assert.False(asked);
    }

    // ── New folder ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewFolder_CreatesTheDirectory_RefreshesAndSelectsIt()
    {
        var provider = InMemoryFileSystemProvider.CreateSample();
        using var model = await SaveAsync(provider);

        model.BeginCreateFolder();
        Assert.True(model.IsCreatingFolder);
        Assert.Equal("New folder", model.NewFolderName);

        model.NewFolderName = "exports";
        Assert.True(await model.CommitCreateFolderAsync());

        Assert.False(model.IsCreatingFolder);
        Assert.Contains("exports", Names(model));
        Assert.Equal("exports", model.SelectedEntry?.Name);
        Assert.True(provider.DirectoryExists($"{Assets}/exports"));
    }

    [Fact]
    public async Task NewFolder_OnACollision_ReportsAndKeepsTheEditorOpen()
    {
        using var model = await SaveAsync(InMemoryFileSystemProvider.CreateSample());

        model.BeginCreateFolder();
        model.NewFolderName = "textures";

        Assert.False(await model.CommitCreateFolderAsync());
        Assert.True(model.IsCreatingFolder);
        Assert.Contains("already exists", model.ErrorMessage);
    }

    [Fact]
    public async Task NewFolder_SeedsANonCollidingDefaultName()
    {
        var provider = InMemoryFileSystemProvider.CreateSample().AddDirectory($"{Assets}/New folder");
        using var model = await SaveAsync(provider);

        model.BeginCreateFolder();
        Assert.Equal("New folder 2", model.NewFolderName);
    }

    [Fact]
    public async Task NewFolder_IsUnavailableInAnOpenDialog()
    {
        using var model = await OpenAsync();

        Assert.False(model.CanCreateDirectories);
        Assert.False(model.NewFolderCommand.CanExecute(null));

        model.BeginCreateFolder();
        Assert.False(model.IsCreatingFolder);
    }

    // ── the completion cache seam ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimeListing_FillsTheCacheThePathCompletionReadsSynchronously()
    {
        using var model = await OpenAsync();

        Assert.NotNull(model.TryGetCachedListing(Assets)); // the navigation cached its own listing
        Assert.Null(model.TryGetCachedListing($"{Assets}/icons"));

        var loaded = 0;
        model.PrimeListing($"{Assets}/icons", () => loaded++);

        Assert.Equal(1, loaded);
        Assert.NotNull(model.TryGetCachedListing($"{Assets}/icons"));
    }

    // ── commands re-query, because there is no CommandManager ────────────────────────────────────

    [Fact]
    public async Task Commands_RaiseCanExecuteChanged_WhenTheStateTheyGateOnMoves()
    {
        using var model = await OpenAsync();

        var backRaised = 0;
        var acceptRaised = 0;
        model.BackCommand.CanExecuteChanged += (_, _) => backRaised++;
        model.AcceptCommand.CanExecuteChanged += (_, _) => acceptRaised++;

        await model.NavigateAsync($"{Assets}/icons");
        Assert.True(backRaised > 0);
        Assert.True(model.BackCommand.CanExecute(null));

        Assert.False(model.AcceptCommand.CanExecute(null));
        model.FileName = "logo.svg";
        Assert.True(acceptRaised > 0);
        Assert.True(model.AcceptCommand.CanExecute(null));
    }

    // ── the home collapse (display only) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ToDisplayPath_CollapsesHome()
    {
        using var model = await OpenAsync();

        Assert.Equal("~/Projects/assets", model.ToDisplayPath("/home/ada/Projects/assets"));
        Assert.Equal("~", model.ToDisplayPath("/home/ada"));
        Assert.Equal("~", model.ToDisplayPath("/home/ada/"));   // a trailing separator is not a segment
    }

    [Fact] // only home itself, and only on a SEGMENT boundary
    public async Task ToDisplayPath_LeavesEverythingElseAlone()
    {
        using var model = await OpenAsync();

        Assert.Equal("/mnt/cloud", model.ToDisplayPath("/mnt/cloud"));
        Assert.Equal("/home", model.ToDisplayPath("/home"));            // the PARENT of home is not home
        Assert.Equal("/home/adam", model.ToDisplayPath("/home/adam"));  // a string prefix of home is not home
        Assert.Equal("", model.ToDisplayPath(""));
    }

    [Fact] // display only — a "~" path resolves with no un-collapsing step, which is why there is no parse half
    public async Task TildePath_ResolvesBackToTheRealDirectory()
    {
        using var model = await OpenAsync();

        var displayed = model.ToDisplayPath(model.CurrentDirectory);
        Assert.StartsWith("~", displayed);
        Assert.Equal(model.CurrentDirectory, model.FileSystem.ResolvePath(displayed, model.CurrentDirectory));
    }
}
