// xUnit1031 (no blocking task ops) is deliberately disabled — the headless host is single-thread-affine and
// the awaited dialog tasks finish on pure (non-UI) continuations, so a bounded Wait cannot deadlock.
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Dialogs;

/// <summary>
/// <see cref="FileOpenDialog"/> and <see cref="FileSaveDialog"/> driven end to end on a headless host against
/// an <see cref="InMemoryFileSystemProvider"/>: the design page's chrome really renders, the keyboard grammar
/// really works through the real controls, and the dialogs really complete with the right
/// <see cref="FileDialogResult"/>.
/// <para>
/// The behaviour these tests exercise is asserted exhaustively (and far faster) in
/// <c>FileDialogViewModelTests</c>; what is proved HERE is the wiring that a view-model test cannot see —
/// that Enter reaches the list, that Escape unwinds through the completion popup and the breadcrumb before it
/// reaches the dialog, that the overwrite prompt is a real task dialog on a real surface, and that the
/// listing's columns land on the screen.
/// </para>
/// </summary>
public sealed class FileDialogTests
{
    private const string Assets = "/home/ada/Projects/assets";

    /// <summary>The §5.1 capability matrix for rendering/input suites.</summary>
    public static TheoryData<string> CapabilityPresets =>
        new() { nameof(HeadlessCapabilities.KittyTruecolor), nameof(HeadlessCapabilities.Ansi16Legacy) };

    private static TerminalCapabilities Resolve(string preset) => preset switch
    {
        nameof(HeadlessCapabilities.KittyTruecolor) => HeadlessCapabilities.KittyTruecolor,
        nameof(HeadlessCapabilities.Ansi16Legacy) => HeadlessCapabilities.Ansi16Legacy,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    private static UIHeadlessHost CreateHostWithRoot(string? capabilityPreset = null)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            Capabilities = Resolve(capabilityPreset ?? nameof(HeadlessCapabilities.KittyTruecolor)),
            InitialSize = new Size(120, 40)
        });

        host.ShowRoot(new TextBlock { Text = "root stub" });
        Assert.True(host.RunUntilIdle());
        return host;
    }

    /// <summary>The composited screen as one string, for containment assertions.</summary>
    private static string ScreenText(UIHeadlessHost host)
    {
        var text = new StringBuilder();

        for (var row = 0; row < host.FrameBuffer.Rows; row++)
            text.AppendLine(host.GetRowText(row));

        return text.ToString();
    }

    /// <summary>Pumps the host idle, then waits out the dialog task's pure (non-UI) mapping tail.</summary>
    private static FileDialogResult Complete(UIHeadlessHost host, Task<FileDialogResult> task)
    {
        Assert.True(host.RunUntilIdle());
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "the dialog task did not complete");
        return task.Result;
    }

    private static Task<FileDialogResult> ShowOpen(UIHeadlessHost host,
                                                   IFileSystemProvider provider,
                                                   params FileDialogFilter[] filters)
    {
        var task = FileOpenDialog.ShowAsync(host.Application,
                                            new FileOpenDialogRequest("Open File")
                                            {
                                                FileSystem = provider,
                                                InitialDirectory = Assets,
                                                Filters = filters
                                            });

        Assert.True(host.RunUntilIdle());
        return task;
    }

    // ── rendering ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Open_RendersTheToolbar_Breadcrumb_ColumnsAndFooter(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        var screen = ScreenText(host);

        Assert.Contains("Open File", screen);          // the title
        Assert.Contains("assets", screen);             // the trailing breadcrumb chip
        Assert.Contains("Quick access", screen);       // the places rail's first band
        Assert.Contains("Name", screen);               // the details header strip
        Assert.Contains("Modified", screen);
        // A listing row. The PREFIX, not the whole name: the Name cell is now held to its column, and in the
        // emoji tier the 2-cell glyph leaves 14 cells for the text, one short of "hero-banner.png". Before
        // the cell was clipped it simply painted over Size/Type, which is what made the full name appear
        // here regardless of tier — this assertion was reading the bug.
        Assert.Contains("hero-banner.p", screen);
        Assert.Contains("1.4 MB", screen);             // …and its rendered size
        Assert.Contains("PNG image", screen);          // …and its kind label
        Assert.Contains("File name:", screen);         // the footer
        Assert.Contains("12 items", screen);           // the selection summary
        Assert.Contains("Open", screen);
        Assert.Contains("Cancel", screen);

        host.SendKey(Key.Escape); // don't leak the modal
        Complete(host, task);
    }

    [Fact]
    public void Open_FoldersRenderBeforeFiles()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        var screen = ScreenText(host);
        Assert.True(screen.IndexOf("textures", StringComparison.Ordinal)
                    < screen.IndexOf("atlas.lua", StringComparison.Ordinal),
                    "folders must sort ahead of files");

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_TypeFilter_HidesNonMatchingFiles_ButKeepsFolders()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample(), new FileDialogFilter("Images", "*.png;*.jpg;*.svg"));

        var screen = ScreenText(host);
        Assert.Contains("hero-banner.png", screen);
        Assert.Contains("textures", screen);
        Assert.DoesNotContain("credits.pdf", screen);

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    // ── the listing's keyboard ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Open_EnterOnAFolderRow_NavigatesInto()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.Home);   // select the first row — "icons"
        host.SendKey(Key.Enter);  // …and open it
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.DoesNotContain("hero-banner.png", screen);
        Assert.Contains("0 items", screen);
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_TypeAheadThenEnter_ReturnsTheMatchedFile()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendText("hero");   // the list's type-ahead jumps to hero-banner.png
        Assert.True(host.RunUntilIdle());
        Assert.Contains("hero-banner.png", ScreenText(host));

        host.SendKey(Key.Enter);

        var result = Complete(host, task);
        Assert.Equal($"{Assets}/hero-banner.png", result.FilePath);
        Assert.False(result.IsDismissed);
    }

    [Fact]
    public void Open_Backspace_NavigatesUp_OnlyOnceTheTypeAheadBufferIsEmpty()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendText("te");          // buffer "te" → textures
        host.SendKey(Key.Backspace);  // "te" → "t": consumed by the buffer, NOT a navigation
        host.SendKey(Key.Backspace);  // "t"  → "": still consumed
        Assert.True(host.RunUntilIdle());
        Assert.Contains("hero-banner.png", ScreenText(host)); // still in assets

        host.SendKey(Key.Backspace);  // buffer empty ⇒ up one directory
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.DoesNotContain("hero-banner.png", screen);
        Assert.Contains("assets", screen); // now a row in Projects rather than the current directory

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_AltUp_NavigatesUp_AndAltLeft_GoesBack()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.UpArrow, KeyModifiers.Alt);
        Assert.True(host.RunUntilIdle());
        Assert.DoesNotContain("hero-banner.png", ScreenText(host));

        host.SendKey(Key.LeftArrow, KeyModifiers.Alt);
        Assert.True(host.RunUntilIdle());
        Assert.Contains("hero-banner.png", ScreenText(host));

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    // ── Escape is a ladder ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Open_Escape_UnwindsOneLevelPerPress_AndOnlyCancelsAtTheTop()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4); // → EDIT mode, the path pre-selected
        Assert.True(host.RunUntilIdle());
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Escape); // rung 2: edit → chips (the breadcrumb claims it)
        Assert.True(host.RunUntilIdle());
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Escape); // rung 3: chips → the file list (the dialog claims it)
        Assert.True(host.RunUntilIdle());
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Escape); // rung 4: top level → cancel
        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
    }

    [Fact]
    public void Open_Escape_AtTheTopLevel_Cancels()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.Escape);

        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
        Assert.Null(result.FilePath);
    }

    // ── the path bar: edit, completion, drill, commit-as-typed ───────────────────────────────────

    [Fact]
    public void Open_F4_PreselectsThePath_SoTypingReplacesIt_AndAltEnterCommitsAsTyped()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);
        host.SendText("~");                             // replaces the whole (pre-selected) path
        host.SendKey(Key.Enter, KeyModifiers.Alt);      // commit EXACTLY as typed, bypassing completion
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("Projects", screen);            // now listing the home folder
        Assert.DoesNotContain("hero-banner.png", screen);

        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_PathEdit_OffersCompletionsForTheFinalSegment()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);
        host.SendKey(Key.End);   // keep the pre-selected path, park the caret after it
        host.SendText("te");
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("⇥ complete", screen);   // the popup's key-hint footer — proof it is up
        Assert.Contains("matches", screen);        // …and its "N matches" header
        Assert.Contains("template.json", screen);  // a file candidate
        Assert.Contains("folder", screen);         // the folder candidate's kind label

        host.SendKey(Key.Escape); // rung 1: popup → edit
        host.SendKey(Key.Escape); // rung 2: edit → chips
        host.SendKey(Key.Escape); // rung 3: chips → the list
        host.SendKey(Key.Escape); // rung 4: cancel
        Complete(host, task);
    }

    [Fact] // typing the separator by hand is the SAME drill gesture as accepting the folder from the list
    public void Open_PathEdit_TypingTheSeparatorOpensTheNextLevel()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);    // the whole path arrives pre-selected, so typing replaces it

        // Finish the segment BY HAND rather than accepting "Projects" from the list — the popup filters the
        // whole time, but it is never used to complete.
        host.SendText("/home/ada/Projects");
        Assert.True(host.RunUntilIdle());

        host.SendText("/");      // …then type the separator yourself
        Assert.True(host.RunUntilIdle());

        // The session must ADVANCE to the level just entered, exactly as accepting the folder would have.
        // Before the fix the empty final segment suppressed the query and the session CLOSED instead.
        var screen = ScreenText(host);
        Assert.Contains("⇥ complete", screen);   // the popup is still up…
        Assert.Contains("assets", screen);         // …and listing INSIDE Projects

        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact] // the editor shows "~", and a path typed against it still resolves — no un-collapsing step exists
    public void Open_PathEdit_ShowsHomeAsTilde_AndStillCommits()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("~/Projects/assets", screen);        // …not "/home/ada/Projects/assets"
        Assert.DoesNotContain("/home/ada/Projects", screen);

        // And it round-trips: retype a "~" path by hand and commit it.
        host.SendText("~/Projects");
        host.SendKey(Key.Enter);
        Assert.True(host.RunUntilIdle());

        Assert.Contains("assets", ScreenText(host));   // navigated INTO ~/Projects, which contains assets
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact] // the rule that motivated the suppression still holds: arriving in edit mode opens nothing
    public void Open_PathEdit_ArrivingWithATrailingSeparatorDoesNotOpenTheList()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);    // seeded with the current path AND its trailing separator
        host.SendKey(Key.End);
        Assert.True(host.RunUntilIdle());

        // An empty final segment on ARRIVAL must not drop the whole listing over the dialog — that would add
        // a phantom rung to the Escape ladder (design page S2 → S3: the popup opens on ↓/Tab, never on arrival).
        Assert.DoesNotContain("⇥ complete", ScreenText(host));

        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_PathEdit_EnterOnAFolderDrills_AndASecondEnterNavigates()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);
        host.SendKey(Key.End);
        host.SendText("te");
        Assert.True(host.RunUntilIdle());

        host.SendKey(Key.Enter); // folders rank first ⇒ "textures/" is inserted and the session continues
        Assert.True(host.RunUntilIdle());
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Enter); // the completed path commits: navigate there
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.DoesNotContain("hero-banner.png", screen);
        Assert.Contains("0 items", screen); // textures is empty in the sample tree

        host.SendKey(Key.Escape); // focus is back on the (empty) list, so one press cancels
        Complete(host, task);
    }

    [Fact]
    public void Open_PathEdit_EnterOnAFileCommitsTheDialog()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);
        host.SendKey(Key.End);
        host.SendText("logo");
        Assert.True(host.RunUntilIdle());

        host.SendKey(Key.Enter); // a FILE candidate: accept, commit the edit, and finish the dialog

        var result = Complete(host, task);
        Assert.Equal($"{Assets}/logo.svg", result.FilePath);
    }

    [Fact]
    public void Open_PathEdit_Tab_CompletesWhenThereIsAPartial_AndLeavesWhenThereIsNot()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.F4);
        host.SendKey(Key.End);
        host.SendKey(Key.Tab); // an EMPTY final segment: nothing to complete ⇒ Tab does the ordinary thing
        Assert.True(host.RunUntilIdle());
        Assert.DoesNotContain("⇥ complete", ScreenText(host));

        host.SendKey(Key.F4);
        host.SendKey(Key.End);
        host.SendText("te");
        Assert.True(host.RunUntilIdle());

        host.SendKey(Key.Escape); // close the popup, KEEPING the typed text
        Assert.True(host.RunUntilIdle());
        Assert.DoesNotContain("⇥ complete", ScreenText(host));

        host.SendKey(Key.Tab); // a partial segment is there ⇒ Tab completes (re-opens the popup)
        Assert.True(host.RunUntilIdle());
        Assert.Contains("⇥ complete", ScreenText(host));

        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_ActivatingABreadcrumbChip_NavigatesToThatAncestor()
    {
        // A deliberately shallow tree: the trail is two chips, so it cannot fold behind the ellipsis and the
        // ←/→ ring is exactly what the test says it is.
        var provider = new InMemoryFileSystemProvider()
                       .AddFile("/docs/notes.md", 120)
                       .AddFile("/readme.md", 64);

        using var host = CreateHostWithRoot();

        var task = FileOpenDialog.ShowAsync(host.Application,
                                            new FileOpenDialogRequest("Open File")
                                            {
                                                FileSystem = provider,
                                                InitialDirectory = "/docs"
                                            });

        Assert.True(host.RunUntilIdle());
        Assert.Contains("notes.md", ScreenText(host));

        host.SendKey(Key.F4);      // into edit mode…
        host.SendKey(Key.Escape);  // …and straight back out, which parks focus on the trailing chip
        host.SendKey(Key.LeftArrow); // ← moves the ACTIVE chip only — never navigates
        Assert.True(host.RunUntilIdle());
        Assert.Contains("notes.md", ScreenText(host));

        host.SendKey(Key.Enter);   // …Enter is the commitment
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("readme.md", screen);
        Assert.DoesNotContain("notes.md", screen);

        host.SendKey(Key.Escape);
        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Open_AltC_CancelsThroughTheAccessKey()
    {
        using var host = CreateHostWithRoot();
        var task = ShowOpen(host, InMemoryFileSystemProvider.CreateSample());

        host.SendKey(Key.Character, KeyModifiers.Alt, "c");

        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
    }

    // ── Save As ──────────────────────────────────────────────────────────────────────────────────

    private static Task<FileDialogResult> ShowSave(UIHeadlessHost host,
                                                   IFileSystemProvider provider,
                                                   string? initialFileName = null)
    {
        var task = FileSaveDialog.ShowAsync(host.Application,
                                            new FileSaveDialogRequest("Save As")
                                            {
                                                FileSystem = provider,
                                                InitialDirectory = Assets,
                                                InitialFileName = initialFileName,
                                                Filters = [new FileDialogFilter("PNG image", "*.png")]
                                            });

        Assert.True(host.RunUntilIdle());
        return task;
    }

    [Fact]
    public void Save_RendersTheWriteSideChrome()
    {
        using var host = CreateHostWithRoot();
        var task = ShowSave(host, InMemoryFileSystemProvider.CreateSample());

        var screen = ScreenText(host);
        Assert.Contains("Save As", screen);
        Assert.True(screen.Contains("▤＋") || screen.Contains("＋📁"));
        Assert.Contains("Save as type:", screen);
        Assert.Contains("PNG image", screen);
        Assert.Contains("*.png", screen);
        Assert.DoesNotContain("Filter:", screen);

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    [Fact]
    public void Save_AnExistingName_RaisesTheOverwriteTaskDialog_AndDecliningKeepsTheDialogOpen()
    {
        using var host = CreateHostWithRoot();
        var task = ShowSave(host, InMemoryFileSystemProvider.CreateSample(), "hero-banner");

        host.SendKey(Key.Enter); // the default Save button
        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("already exists", screen);
        Assert.Contains("Confirm Save As", screen);
        Assert.Contains("Replace", screen);
        Assert.False(task.IsCompleted);

        host.SendKey(Key.Escape); // decline: the confirmation closes, the save dialog does NOT
        Assert.True(host.RunUntilIdle());
        Assert.False(task.IsCompleted);
        Assert.DoesNotContain("already exists", ScreenText(host));

        host.SendKey(Key.Escape);
        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
    }

    [Fact]
    public void Save_AnExistingName_Replaced_CompletesWithThePath()
    {
        using var host = CreateHostWithRoot();
        var task = ShowSave(host, InMemoryFileSystemProvider.CreateSample(), "hero-banner");

        host.SendKey(Key.Enter); // Save
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter); // Replace (the confirmation's default)

        var result = Complete(host, task);
        Assert.Equal($"{Assets}/hero-banner.png", result.FilePath);
        Assert.Equal(new FileDialogFilter("PNG image", "*.png"), result.Filter);
    }

    [Fact]
    public void Save_ANewName_TakesTheFiltersExtension_WithNoConfirmation()
    {
        using var host = CreateHostWithRoot();
        var task = ShowSave(host, InMemoryFileSystemProvider.CreateSample(), "report-final");

        host.SendKey(Key.Enter);

        var result = Complete(host, task);
        Assert.Equal($"{Assets}/report-final.png", result.FilePath);
    }

    [Fact]
    public void Save_AltN_OpensTheNewFolderEditor_AndEnterCreatesTheFolder()
    {
        var provider = InMemoryFileSystemProvider.CreateSample();

        using var host = CreateHostWithRoot();
        var task = ShowSave(host, provider);

        host.SendKey(Key.Character, KeyModifiers.Alt, "n"); // Alt+N — the always-available new-folder path
        Assert.True(host.RunUntilIdle());

        host.SendText("exports"); // the editor opens pre-selected on "New folder", so typing replaces
        host.SendKey(Key.Enter);
        Assert.True(host.RunUntilIdle());

        Assert.True(provider.DirectoryExists($"{Assets}/exports"));
        Assert.Contains("exports", ScreenText(host));

        host.SendKey(Key.Escape);
        Complete(host, task);
    }

    // ── the no-throw cancellation contract ───────────────────────────────────────────────────────

    [Fact]
    public void Open_ACanceledToken_DismissesWithoutThrowing()
    {
        using var host = CreateHostWithRoot();
        using var cancellation = new CancellationTokenSource();

        var task = FileOpenDialog.ShowAsync(host.Application,
                                            new FileOpenDialogRequest("Open File")
                                            {
                                                FileSystem = InMemoryFileSystemProvider.CreateSample(),
                                                InitialDirectory = Assets
                                            },
                                            cancellation.Token);

        Assert.True(host.RunUntilIdle());
        cancellation.Cancel();

        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
    }

    // Both tiers, because the icon's width is what the name's slot is measured against: the Nerd Font glyph
    // is 1 cell and the emoji fallback is 2, so the emoji tier is where a cut could land mid-grapheme.
    [Theory] // A name wider than the Name column stops at the column — it does not paint across the row
    [MemberData(nameof(CapabilityPresets))]
    public void Open_AnOverlongName_StaysInsideTheNameColumn(string caps)
    {
        const string overlong = "creative-cloud-files-personal-account-archive.png";

        var provider = InMemoryFileSystemProvider.CreateSample();
        provider.AddFile($"{Assets}/{overlong}", size: 1234, lastModified: new DateTimeOffset(2026, 2, 17, 10, 35, 0, TimeSpan.Zero));

        using var host = CreateHostWithRoot(caps);
        var task = ShowOpen(host, provider);

        var row = RowContaining(host, "creative");

        // The row must not carry the whole name — the Name column is far narrower than 48 cells.
        Assert.DoesNotContain(overlong, row);

        // The defect is subtler than plain overflow: the neighbouring cells repaint on top of the escaped
        // text, so the name survives only in the GAPS between them ("…file1 KBrPNG imageount2026-02-17").
        // The invariant that catches it is that the columns are separated by BLANKS and nothing else.
        var listing = row[row.IndexOf("creative", StringComparison.Ordinal)..];
        Assert.Matches(@"^[\w.\-\u2026]+\s+1 KB\s+PNG image\s", listing);

        host.SendKey(Key.Escape); // don't leak the modal
        Complete(host, task);
    }

    /// <summary>The first screen row containing <paramref name="needle"/> (asserts one exists).</summary>
    private static string RowContaining(UIHeadlessHost host, string needle)
    {
        for (var row = 0; row < host.FrameBuffer.Rows; row++)
        {
            var text = host.GetRowText(row);
            if (text.Contains(needle, StringComparison.Ordinal))
                return text;
        }

        Assert.Fail($"no rendered row contained '{needle}'.\n{ScreenText(host)}");
        return string.Empty;
    }
}
