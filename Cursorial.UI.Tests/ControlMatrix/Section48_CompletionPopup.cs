using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Matching;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §46 — <see cref="CompletionPopup"/>, the generalized completion / "IntelliSense" overlay: a
/// non-focusable popup list over a <see cref="TextBox"/>, driven by an
/// <see cref="ICompletionProvider"/>, ranked and highlighted by <see cref="FuzzyMatcher"/>, and
/// committed through a host-supplied hook.
/// <para>
/// The rows pin the contracts the two reference consumers depend on: the DataViews criteria editor's
/// <c>(ReplaceStart, ReplaceEnd)</c> span (which is NOT <c>[start..caret)</c> — the caret can sit
/// mid-token), and a file path bar's folders-before-files ordering plus its Tab/Enter divergence.
/// Everything is asserted against real rendered frames or real routed input; nothing is mocked.
/// </para>
/// </summary>
public sealed class Section48_CompletionPopup
{
    // ───────────────────────────── fixtures ─────────────────────────────

    private sealed class CountingProvider(Func<CompletionQuery, CompletionContext?> callback) : ICompletionProvider
    {
        public int Calls { get; private set; }

        public CompletionTrigger LastTrigger { get; private set; }

        public string LastText { get; private set; } = "";

        public CompletionContext? GetCompletions(in CompletionQuery query)
        {
            Calls++;
            LastTrigger = query.Trigger;
            LastText = query.Text;
            return callback(query);
        }
    }

    /// <summary>
    /// The DataViews criteria grammar in miniature — the exact shape
    /// <c>DataGridExpressionEditor.CompletionContext()</c> implements: walk back to an unmatched
    /// <c>'['</c> for the pattern, then walk FORWARD past the token's tail (through its closing
    /// <c>']'</c>) for the replace end. That forward walk is the whole point: the caret may sit in the
    /// middle of the token.
    /// </summary>
    private static CountingProvider ExpressionProvider(params string[] fields)
        => new(query =>
        {
            var text = query.Text;
            var caret = query.CaretIndex;

            var open = caret - 1;
            while (open >= 0 && text[open] is not ('[' or ']' or '\n'))
                open--;

            if (open < 0 || text[open] != '[')
                return null;

            var end = caret;
            while (end < text.Length && text[end] is not (']' or '[' or '\n'))
                end++;
            if (end < text.Length && text[end] == ']')
                end++; // the [Name] insert carries its own close bracket

            var items = new List<CompletionItem>();
            foreach (var field in fields)
                items.Add(new CompletionItem(field) { InsertText = $"[{field}]", KindLabel = "field" });

            return new CompletionContext(open, end, text[(open + 1)..caret], items);
        });

    /// <summary>A path bar's provider: the segment after the last <c>'/'</c>, folders in sort group 0 and files in 1.</summary>
    private static CountingProvider PathProvider(params (string Name, bool IsFolder)[] entries)
        => new(query =>
        {
            var text = query.Text;
            var caret = query.CaretIndex;
            var start = caret == 0 ? 0 : text.LastIndexOf('/', caret - 1) + 1;

            var items = new List<CompletionItem>();

            foreach (var (name, isFolder) in entries)
            {
                items.Add(new CompletionItem(name)
                {
                    InsertText = isFolder ? name + "/" : name,
                    KindLabel = isFolder ? "folder" : "file",
                    SortGroup = isFolder ? 0 : 1,
                    ContinuesSession = isFolder,
                    Data = isFolder,
                });
            }

            return new CompletionContext(start, text.Length, text[start..caret], items);
        });

    /// <summary>
    /// A PICKER's provider: a fixed candidate list handed back whole, with the popup's own
    /// <see cref="CompletionPopup.Pattern"/> arriving as the query text (there is no field to read).
    /// </summary>
    private static CountingProvider PickerProvider(params string[] names)
        => new(query => new CompletionContext(
            0,
            query.Text.Length,
            query.Text,
            names.Select(name => new CompletionItem(name)).ToList()));

    private static (UIHeadlessHost Host, TextBox Box, CompletionPopup Popup) Show(
        ICompletionProvider provider,
        string seed = "",
        VerticalAlignment align = VerticalAlignment.Top,
        Size? size = null,
        int topMargin = 0)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = size ?? new Size(60, 20),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            CaptureFrameBytes = true,
        });

        var box = new TextBox
        {
            Text = seed,
            Width = 24,
            Height = 1,
            Margin = new Margins(0, topMargin, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = align,
        };

        // The overlay is layout-free (its template root is a Grid holding only the Popup), so it drops
        // straight into the field's own Grid cell without displacing it.
        var popup = new CompletionPopup { Target = box, Provider = provider };

        var root = new Grid();
        root.Children.Add(box);
        root.Children.Add(popup);

        host.ShowRoot(root);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();

        return (host, box, popup);
    }

    /// <summary>
    /// The picker fixture: a focusable <see cref="Button"/> standing in for "any element at all" and a
    /// target-less popup anchored to it, in the same <see cref="Grid"/> cell (the overlay is layout-free).
    /// </summary>
    private static (UIHeadlessHost Host, Button Anchor, CompletionPopup Popup) ShowPicker(ICompletionProvider provider)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(60, 20),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            CaptureFrameBytes = true,
        });

        var anchor = new Button
        {
            Content = "open",
            Width = 10,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var popup = new CompletionPopup { Anchor = anchor, Provider = provider };

        var root = new Grid();
        root.Children.Add(anchor);
        root.Children.Add(popup);

        host.ShowRoot(root);
        host.RunUntilIdle();
        anchor.Focus();
        host.RunUntilIdle();

        return (host, anchor, popup);
    }

    /// <summary>Finds <paramref name="needle"/> in the composited frame, or <c>(-1, -1)</c>.</summary>
    private static (int Column, int Row) FindOnScreen(UIHeadlessHost host, string needle)
    {
        for (var row = 0; row < host.FrameBuffer.Rows; row++)
        {
            var column = host.GetRowText(row).IndexOf(needle, StringComparison.Ordinal);

            if (column >= 0)
                return (column, row);
        }

        return (-1, -1);
    }

    private static bool IsBold(UIHeadlessHost host, int column, int row)
        => (host.GetCell(column, row).Style.Attributes & TextAttributes.Bold) != 0;

    // ───────────────────────────── rows ─────────────────────────────

    [Fact] // C46.1: the provider's (ReplaceStart, ReplaceEnd) span — not [start..caret) — is what an accept replaces
    public void C46_1_MidTokenCaret_ReplacesTheWholeToken()
    {
        // The audit case from DataGridExpressionEditor: the caret sits INSIDE "[Prce]" after "Pr".
        // Replacing only up to the caret leaves the "ce]" tail and corrupts the text ("[Price]ce]").
        var provider = ExpressionProvider("Price", "Product");
        var (host, box, popup) = Show(provider, seed: "[Prce]");
        using var _ = host;

        box.CaretIndex = 3;              // "[Pr|ce]"
        popup.Refresh();                 // the explicit-trigger path (also what an async provider calls back on)
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);
        Assert.Equal(0, popup.ReplaceStart); // the '['
        Assert.Equal(6, popup.ReplaceEnd);   // PAST the ']' — after the caret, which is the whole point
        Assert.Equal("Price", popup.SelectedEntry!.Item.Display);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal("[Price]", box.Text); // not "[Price]ce]"
        Assert.Equal(7, box.CaretIndex);
        Assert.False(popup.IsOpen);
    }

    [Fact] // C46.2: the FuzzyMatcher's MatchSpans reach the cells — matched characters render Bold, unmatched do not
    public void C46_2_MatchedCharacters_RenderBold()
    {
        var provider = ExpressionProvider("Price");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("[Pr");
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);

        var entry = popup.Entries[0];
        Assert.Equal(new MatchSpan(0, 2), Assert.Single(entry.MatchSpans.ToArray()));
        Assert.Equal("[b]Pr[/b]ice", entry.DisplayMarkup);

        // …and end-to-end on the composited frame: "Pr" bold, "ice" not.
        var (column, row) = FindOnScreen(host, "Price");
        Assert.True(column >= 0, "the completion row never rendered");
        Assert.True(IsBold(host, column, row));
        Assert.True(IsBold(host, column + 1, row));
        Assert.False(IsBold(host, column + 2, row));
    }

    [Fact] // C46.3: SortGroup wins over the score — a folder always lists above a file that matches better
    public void C46_3_FoldersBeforeFiles()
    {
        var provider = PathProvider(("source-images", true), ("src.txt", false));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("src");
        host.RunUntilIdle();

        // "src.txt" is a literal PREFIX (the top matcher tier); "source-images" is a scattered
        // subsequence (the bottom one). Pure ranking would put the file first…
        Assert.Equal(FuzzyMatchKind.Prefix, popup.Entries.Single(e => e.Item.Display == "src.txt").MatchKind);
        Assert.Equal(FuzzyMatchKind.Subsequence, popup.Entries.Single(e => e.Item.Display == "source-images").MatchKind);

        // …but the sort group is applied first, so the folder still leads.
        Assert.Equal("source-images", popup.Entries[0].Item.Display);
        Assert.Equal("src.txt", popup.Entries[1].Item.Display);
    }

    [Fact] // C46.3b: without SortGroup the ranking alone leads — proving C46.3's order is the group, not luck
    public void C46_3b_WithoutSortGroup_TheBetterMatchLeads()
    {
        var provider = new CountingProvider(query => new CompletionContext(
            0,
            query.Text.Length,
            query.Text,
            [new CompletionItem("source-images"), new CompletionItem("src.txt")]));

        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("src");
        host.RunUntilIdle();

        Assert.Equal("src.txt", popup.Entries[0].Item.Display);
    }

    [Fact] // C46.4: a scattered-subsequence row is labelled "fuzzy" beside its kind; a prefix row is not
    public void C46_4_FuzzyMatchesAreLabelled()
    {
        var provider = PathProvider(("source-images", true), ("src.txt", false));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("src");
        host.RunUntilIdle();

        Assert.Equal("folder · fuzzy", popup.Entries[0].KindLabel);
        Assert.True(popup.Entries[0].IsFuzzyMatch);
        Assert.Equal("file", popup.Entries[1].KindLabel);
        Assert.False(popup.Entries[1].IsFuzzyMatch);
    }

    [Fact] // C46.5: the commit hook receives the accepting gesture, and its NewText/NewCaretIndex/KeepOpen are honored
    public void C46_5_CommitHook_TabDrills_EnterOnAFileCommits()
    {
        var provider = PathProvider(("docs", true), ("diagram.png", false));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        var reasons = new List<CompletionAcceptReason>();

        // The path bar's policy: Tab always keeps completing; Enter drills into a FOLDER but commits on
        // a FILE. The expression editor's policy is the default one (accept, close) — which is exactly
        // why this decision is the host's and not the popup's.
        popup.CommitHandler = accept =>
        {
            reasons.Add(accept.Reason);
            var spliced = CompletionCommit.Splice(accept);
            var isFolder = accept.Item.Data is true;
            return spliced with { KeepOpen = accept.Reason == CompletionAcceptReason.Tab || isFolder };
        };

        host.SendText("d");
        host.RunUntilIdle();
        Assert.Equal("docs", popup.SelectedEntry!.Item.Display); // the folder sorts first

        host.SendKey(Key.Enter); // Enter on a FOLDER: insert + separator, keep drilling
        host.RunUntilIdle();

        Assert.Equal([CompletionAcceptReason.Enter], reasons);
        Assert.Equal("docs/", box.Text);
        Assert.Equal(5, box.CaretIndex);
        Assert.True(popup.IsOpen);                                   // the session survived
        Assert.Equal(CompletionTrigger.Continued, provider.LastTrigger); // …and re-queried against the POST-commit text
        Assert.Equal("docs/", provider.LastText);

        host.SendKey(Key.DownArrow); // move onto the file
        host.RunUntilIdle();
        Assert.Equal("diagram.png", popup.SelectedEntry!.Item.Display);

        host.SendKey(Key.Enter); // Enter on a FILE: commit and close
        host.RunUntilIdle();

        Assert.Equal([CompletionAcceptReason.Enter, CompletionAcceptReason.Enter], reasons);
        Assert.Equal("docs/diagram.png", box.Text);
        Assert.False(popup.IsOpen);
    }

    [Fact] // C46.5b: Tab and Enter are distinguishable at the hook — same row, different reason and lifetime
    public void C46_5b_CommitHook_SeesTabDistinctFromEnter()
    {
        var provider = PathProvider(("diagram.png", false));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        CompletionAcceptReason? seen = null;

        popup.CommitHandler = accept =>
        {
            seen = accept.Reason;
            return CompletionCommit.Splice(accept) with { KeepOpen = accept.Reason == CompletionAcceptReason.Tab };
        };

        host.SendText("d");
        host.RunUntilIdle();

        host.SendKey(Key.Tab);
        host.RunUntilIdle();

        Assert.Equal(CompletionAcceptReason.Tab, seen);
        Assert.Equal("diagram.png", box.Text);
        Assert.True(popup.IsOpen); // Tab kept the session; the same row on Enter would have closed it
    }

    [Fact] // C46.6: ↑/↓/PgUp/PgDn move the highlight and keyboard focus never leaves the field
    public void C46_6_KeyboardNavigation_FocusStaysInTheField()
    {
        var provider = PathProvider(("alpha", true), ("beta", true), ("gamma", true), ("delta", true));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        popup.Refresh(); // an empty pattern shows everything
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);
        Assert.Equal(4, popup.MatchCount);
        Assert.Equal(0, popup.SelectedIndex);
        Assert.True(box.IsFocused);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, popup.SelectedIndex);
        Assert.True(box.IsFocused);
        Assert.Same(box, host.Application.FocusManager.FocusedElement);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(0, popup.SelectedIndex);

        host.SendKey(Key.UpArrow); // clamps at the top rather than wrapping
        host.RunUntilIdle();
        Assert.Equal(0, popup.SelectedIndex);

        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.Equal(3, popup.SelectedIndex);
        Assert.True(box.IsFocused);

        // The field still owns its own keys: typing keeps landing in the text box, not the list.
        host.SendText("a");
        host.RunUntilIdle();
        Assert.Equal("a", box.Text);
    }

    [Fact] // C46.7: Escape dismisses the popup and KEEPS the typed text (a dismissal is never a revert)
    public void C46_7_Escape_ClosesAndKeepsText()
    {
        var provider = PathProvider(("alpha", true), ("apex", true));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("ap");
        host.RunUntilIdle();
        Assert.True(popup.IsOpen);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(popup.IsOpen);
        Assert.Equal("ap", box.Text);                       // the text survives the dismissal
        Assert.Empty(popup.Entries);
        Assert.Equal(-1, FindOnScreen(host, "alpha").Row);  // and the surface really went away
    }

    [Fact] // C46.8: accepting is re-entrancy safe — the commit's own TextChanged never re-opens the session
    public void C46_8_Accept_IsReentrancySafe()
    {
        var provider = ExpressionProvider("Price");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("[Pr");
        host.RunUntilIdle();
        Assert.True(popup.IsOpen);

        var before = provider.Calls;

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        // The accept writes Text (⇒ TextChanged) and CaretIndex (⇒ SelectionChanged). Without the
        // guard each would re-enter the funnel and resurrect the session being closed.
        Assert.Equal(before, provider.Calls);
        Assert.False(popup.IsOpen);
        Assert.Equal("[Price]", box.Text);
    }

    [Fact] // C46.8b: a KeepOpen commit re-queries exactly once, with the Continued trigger
    public void C46_8b_KeepOpenCommit_ReQueriesExactlyOnce()
    {
        var provider = PathProvider(("docs", true));
        var (host, box, popup) = Show(provider);
        using var _ = host;

        popup.CommitHandler = accept => CompletionCommit.Splice(accept) with { KeepOpen = true };

        host.SendText("d");
        host.RunUntilIdle();

        var before = provider.Calls;

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(before + 1, provider.Calls);
        Assert.Equal(CompletionTrigger.Continued, provider.LastTrigger);
    }

    [Fact] // C46.9: a null context or an empty match set closes; an empty pattern shows everything in PROVIDER order
    public void C46_9_NullContextCloses_EmptyPatternKeepsProviderOrder()
    {
        var provider = ExpressionProvider("Price", "Product");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("[");
        host.RunUntilIdle();

        // Empty pattern ⇒ every candidate, and NOT re-sorted by the matcher's length-then-ordinal
        // tiebreak (which would put the shorter "Price" first regardless of what the provider meant).
        Assert.True(popup.IsOpen);
        Assert.Equal(["Price", "Product"], popup.Entries.Select(e => e.Item.Display));
        Assert.All(popup.Entries, e => Assert.Equal(FuzzyMatchKind.Empty, e.MatchKind));

        host.SendText("zz"); // nothing matches ⇒ the popup closes without a provider veto
        host.RunUntilIdle();
        Assert.False(popup.IsOpen);

        host.SendKey(Key.Backspace);
        host.SendKey(Key.Backspace);
        host.SendKey(Key.Backspace); // deletes the '[' ⇒ the provider returns null
        host.RunUntilIdle();
        Assert.False(popup.IsOpen);
        Assert.Equal("", box.Text);
    }

    [Fact] // C46.10: the popup picks its side BEFORE opening — Bottom with room below, Top when the field is at the floor
    public void C46_10_Placement_ChoosesTopWhenThereIsNoRoomBelow()
    {
        var provider = PathProvider(("alpha", true), ("apex", true), ("aspen", true));

        var (topHost, _, topAnchored) = Show(provider, align: VerticalAlignment.Top);
        using (topHost)
        {
            topHost.SendText("a");
            topHost.RunUntilIdle();

            Assert.Equal(PlacementMode.Bottom, topAnchored.PopupPart!.Placement);
            Assert.True(FindOnScreen(topHost, "alpha").Row > 0); // rendered BELOW the field on row 0
        }

        var (bottomHost, _, bottomAnchored) = Show(provider, align: VerticalAlignment.Bottom);
        using (bottomHost)
        {
            bottomHost.SendText("a");
            bottomHost.RunUntilIdle();

            // The window manager only CLAMPS a popup into the viewport — it never flips one — so a
            // Bottom placement here would be shoved back up over the field it decorates. The control
            // therefore chooses Top itself.
            Assert.Equal(PlacementMode.Top, bottomAnchored.PopupPart!.Placement);

            var (_, row) = FindOnScreen(bottomHost, "alpha");
            Assert.True(row >= 0);
            Assert.True(row < 19); // above the field, which occupies the last row
        }
    }

    [Fact] // C46.10b: a live list that outgrows its side RELOCATES — a popup only places on the closed→open edge
    public void C46_10b_Placement_RelocatesWhenTheListOutgrowsItsSide()
    {
        // Anchored at row 12 of 20 there are 7 rows below and 12 above: one candidate fits underneath,
        // eight do not. Writing Placement on a live popup would change nothing (the window manager
        // places on the closed→open edge and on a content refit, never on a property write), so the
        // control has to close and re-open to move — which is what this row pins.
        var provider = PathProvider(
            ("aa", true), ("ab", true), ("ac", true), ("ad", true),
            ("ae", true), ("af", true), ("ag", true), ("ah-only", true), ("zz-aardvark", true));

        var (host, box, popup) = Show(provider, topMargin: 12);
        using var _ = host;

        var closes = 0;
        popup.Closed += (_, _) => closes++;

        host.SendText("a");
        host.RunUntilIdle();

        Assert.Equal(9, popup.MatchCount);
        Assert.Equal(PlacementMode.Top, popup.PopupPart!.Placement);
        Assert.True(FindOnScreen(host, "aa").Row < 12);

        host.SendText("h"); // narrows to one row, which now fits below the field
        host.RunUntilIdle();

        Assert.Equal(1, popup.MatchCount);
        Assert.Equal(PlacementMode.Bottom, popup.PopupPart!.Placement);
        Assert.True(FindOnScreen(host, "ah-only").Row > 12); // the display, not the "ah" now in the field

        // The relocation's intermediate close is internal bookkeeping, not a dismissal: the session
        // stayed open throughout and no Closed event escaped.
        Assert.True(popup.IsOpen);
        Assert.Equal(0, closes);
    }

    [Fact] // C46.10c: the highlight scrolls into view — completion rows are never focused, so the popup scrolls itself
    public void C46_10c_SelectionScrollsIntoView()
    {
        var provider = PathProvider(
            ("one", true), ("two", true), ("three", true),
            ("four", true), ("five", true), ("six", true));

        var (host, box, popup) = Show(provider);
        using var _ = host;

        popup.MaxVisibleItems = 3;
        popup.Refresh();
        host.RunUntilIdle();

        Assert.Equal(6, popup.MatchCount);
        Assert.True(FindOnScreen(host, "one").Row >= 0);
        Assert.Equal(-1, FindOnScreen(host, "six").Row); // past the 3-row window

        for (var i = 0; i < 5; i++)
            host.SendKey(Key.DownArrow);

        host.RunUntilIdle();

        // ScrollViewer.OnDescendantGotFocus cannot help here (the rows are deliberately unfocusable),
        // so the popup drives the offset itself.
        Assert.Equal(5, popup.SelectedIndex);
        Assert.True(FindOnScreen(host, "six").Row >= 0);
        Assert.Equal(-1, FindOnScreen(host, "one").Row); // scrolled off the top
    }

    [Fact] // C46.11: a pointer press on a row accepts it, reported as CompletionAcceptReason.Pointer
    public void C46_11_PointerPress_Accepts()
    {
        var provider = ExpressionProvider("Price", "Product");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        CompletionAcceptReason? seen = null;
        popup.CommitHandler = accept =>
        {
            seen = accept.Reason;
            return CompletionCommit.Splice(accept);
        };

        host.SendText("[P");
        host.RunUntilIdle();

        var (column, row) = FindOnScreen(host, "Product");
        Assert.True(column >= 0, "the 'Product' row never rendered");

        host.SendClick(column, row);
        host.RunUntilIdle();

        Assert.Equal(CompletionAcceptReason.Pointer, seen);
        Assert.Equal("[Product]", box.Text);
        Assert.False(popup.IsOpen);
    }

    [Fact] // C46.12: the header and footer are optional chrome — both templated, both suppressible
    public void C46_12_HeaderAndFooter_AreSuppressible()
    {
        var provider = ExpressionProvider("Price", "Product");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("[P");
        host.RunUntilIdle();

        Assert.Equal(2, popup.MatchCount);
        Assert.Equal("2 matches", popup.HeaderContent);
        Assert.True(FindOnScreen(host, "2 matches").Row >= 0);
        Assert.True(FindOnScreen(host, "⇥ complete  ↑↓ select  ⏎ accept  ⎋ cancel").Row >= 0);

        popup.ShowHeader = false;
        popup.ShowFooter = false;
        host.RunUntilIdle();

        Assert.Equal(-1, FindOnScreen(host, "2 matches").Row);
        Assert.Equal(-1, FindOnScreen(host, "Tab/Enter accept").Row);
        Assert.True(FindOnScreen(host, "Price").Row >= 0); // the rows themselves are untouched

        // Singular/plural, and the header follows the live filter ("Pi" is a subsequence of Price only).
        popup.ShowHeader = true;
        host.SendText("i");
        host.RunUntilIdle();
        Assert.Equal("1 match", popup.HeaderContent);
        Assert.True(FindOnScreen(host, "1 match").Row >= 0);
    }

    [Fact] // C46.13: BBCode metacharacters in a candidate are escaped, so a bracketed display never breaks the row
    public void C46_13_DisplayMarkup_EscapesMetacharacters()
    {
        var provider = new CountingProvider(query => new CompletionContext(
            0,
            query.Text.Length,
            query.Text,
            [new CompletionItem(@"[Unit Price]"), new CompletionItem(@"C:\Users")]));

        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("u");
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);
        Assert.Equal(2, popup.MatchCount);

        var bracketed = popup.Entries.Single(e => e.Item.Display == "[Unit Price]");
        var windowsPath = popup.Entries.Single(e => e.Item.Display == @"C:\Users");

        Assert.Equal(@"\[[b]U[/b]nit Price\]", bracketed.DisplayMarkup);
        Assert.Equal(@"C:\\[b]U[/b]sers", windowsPath.DisplayMarkup);

        // …and the rows render the LITERAL text, metacharacters and all. Unescaped, the strict BBCode
        // parser would have thrown FormatException on the first of them ("unknown tag name").
        Assert.True(FindOnScreen(host, "[Unit Price]").Row >= 0);
        Assert.True(FindOnScreen(host, @"C:\Users").Row >= 0);
    }

    [Fact] // C46.14: the popup is a hint overlay — non-focusable rows, StaysOpen, and zero footprint in the host layout
    public void C46_14_HintOverlay_Shape()
    {
        var provider = ExpressionProvider("Price");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        Assert.False(popup.Focusable);
        Assert.False(popup.IsTabStop);
        Assert.False(popup.IsHitTestVisible);
        Assert.Equal(new Size(0, 0), popup.DesiredSize); // contributes nothing to the field's layout

        host.SendText("[P");
        host.RunUntilIdle();

        Assert.True(popup.PopupPart!.StaysOpen);
        Assert.False(popup.ListPart!.Focusable);
        Assert.Single(host.Application.WindowManager!.Popups);

        var row = (CompletionListItem) popup.ListPart.ItemContainerGenerator.ContainerFromIndex(0)!;
        Assert.False(row.Focusable);
        Assert.False(row.IsTabStop);
        Assert.True(row.IsSelected);
        Assert.True(box.IsFocused); // still, with a realized and selected row on screen

        popup.Close();
        host.RunUntilIdle();
        Assert.Empty(host.Application.WindowManager!.Popups);
    }

    [Fact] // C46.15: detaching the control closes the popup surface — it can never outlive its owner
    public void C46_15_Detach_ClosesTheSurface()
    {
        var provider = ExpressionProvider("Price");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        host.SendText("[P");
        host.RunUntilIdle();
        Assert.Single(host.Application.WindowManager!.Popups);

        ((Grid) popup.VisualParent!).Children.Remove(popup);
        host.RunUntilIdle();

        Assert.False(popup.IsOpen);
        Assert.Empty(host.Application.WindowManager!.Popups);
    }

    // ───────────────────────────── picker mode (Anchor, no Target) ─────────────────────────────

    [Fact] // C46.16: an Anchor and no Target — keys come from the anchor, and accepting splices no text anywhere
    public void C46_16_PickerMode_ReportsThePick_AndEditsNoText()
    {
        var provider = PickerProvider("docs", "downloads", "desktop", "music");
        var (host, anchor, popup) = ShowPicker(provider);
        using var _ = host;

        Assert.True(popup.IsPicker);
        Assert.True(popup.Open());
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);
        Assert.Equal(4, popup.MatchCount);
        Assert.Same(anchor, host.Application.FocusManager.FocusedElement); // still a hint overlay: focus never moves

        var picks = new List<CompletionItem>();
        var commits = 0;
        var clicks = 0;
        popup.Picked += (_, e) => picks.Add(e.Item);
        popup.Committed += (_, _) => commits++;
        anchor.Click += (_, _) => clicks++;

        host.SendText("do"); // the popup OWNS the pattern — there is no field for the keys to land in
        host.RunUntilIdle();

        Assert.Equal("do", popup.Pattern);

        // "music" is gone; the two prefix hits lead and "desktop" trails as a scattered subsequence
        // (d…o) — the same FuzzyMatcher ranking the text mode uses, on a pattern the popup owns.
        Assert.Equal(["docs", "downloads", "desktop"], popup.Entries.Select(e => e.Item.Display));

        host.SendKey(Key.DownArrow);
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(["downloads"], picks.Select(item => item.Display));
        Assert.Equal(0, commits); // Committed is the TEXT path's report; nothing was written to anything
        Assert.Equal(0, clicks);  // …and the picker claimed Enter, so the anchor button never activated
        Assert.False(popup.IsOpen);
        Assert.Equal("", popup.Pattern); // a closed picker owns no filter — the next Open() starts whole
    }

    [Fact] // C46.17: the picker's filter is VISIBLE (in the header) and reversible — Backspace, then Escape
    public void C46_17_PickerPattern_IsShown_AndEscapePeelsItBeforeDismissing()
    {
        var provider = PickerProvider("docs", "downloads", "desktop", "music");
        var (host, _, popup) = ShowPicker(provider);
        using var __ = host;

        popup.Open();
        host.RunUntilIdle();
        Assert.Equal("4 matches", popup.HeaderContent);

        host.SendText("de");
        host.RunUntilIdle();

        Assert.Equal("desktop", Assert.Single(popup.Entries).Item.Display);
        Assert.Equal("de · 1 match", popup.HeaderContent);
        Assert.True(FindOnScreen(host, "de · 1 match").Row >= 0); // …and it really reaches the frame

        host.SendKey(Key.Backspace);
        host.RunUntilIdle();
        Assert.Equal("d", popup.Pattern);
        Assert.Equal(3, popup.MatchCount);

        // Escape peels the FILTER off first. The pattern is invisible to everything but this header, so an
        // Escape that always dismissed would make one mistyped character cost the whole list.
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.True(popup.IsOpen);
        Assert.Equal("", popup.Pattern);
        Assert.Equal(4, popup.MatchCount);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(popup.IsOpen);
    }

    [Fact] // C46.18: a picker filtered to NOTHING stays up on "no matches" — the one place it must not close
    public void C46_18_PickerStaysOpenWithNoMatches()
    {
        var provider = PickerProvider("docs", "downloads");
        var (host, _, popup) = ShowPicker(provider);
        using var __ = host;

        popup.Open();
        host.RunUntilIdle();

        host.SendText("dz");
        host.RunUntilIdle();

        // Text mode closes here and is right to: the field still holds what was typed. A picker cannot —
        // the pattern lives only in the popup, so closing would discard it AND leave the Backspace that
        // fixes the typo with nowhere to land.
        Assert.True(popup.IsOpen);
        Assert.Equal(0, popup.MatchCount);
        Assert.Equal("dz · no matches", popup.HeaderContent);
        Assert.Equal(-1, popup.SelectedIndex);
        Assert.False(popup.AcceptSelected(CompletionAcceptReason.Enter)); // nothing to take

        host.SendKey(Key.Backspace);
        host.RunUntilIdle();

        Assert.Equal(2, popup.MatchCount);
        Assert.Equal(0, popup.SelectedIndex);
    }

    [Fact] // C46.19: Anchor is DORMANT while a Target is set — picker mode is purely additive to the text path
    public void C46_19_TargetWinsOverAnchor()
    {
        var provider = ExpressionProvider("Price");
        var (host, box, popup) = Show(provider);
        using var _ = host;

        var anchor = new Button { Content = "x", Width = 3, Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        ((Grid) popup.VisualParent!).Children.Add(anchor);
        host.RunUntilIdle();

        host.SendText("[P");
        host.RunUntilIdle();
        Assert.True(popup.IsOpen);

        popup.Anchor = anchor; // writing a dormant property must not disturb a live session
        host.RunUntilIdle();

        Assert.False(popup.IsPicker);
        Assert.True(popup.IsOpen);
        Assert.Same(box, popup.PopupPart!.PlacementTarget); // still hanging off the FIELD

        var committed = 0;
        var picked = 0;
        popup.Committed += (_, _) => committed++;
        popup.Picked += (_, _) => picked++;

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal("[Price]", box.Text); // the accept still splices
        Assert.Equal(1, committed);
        Assert.Equal(0, picked);
    }

    [Fact] // C46.20: a pick may KEEP the picker up — the drill-down chooser, restarted on a cleared pattern
    public void C46_20_PickerKeepOpen_ClearsThePatternAndReQueries()
    {
        var level = 0;

        var provider = new CountingProvider(query => new CompletionContext(
            0,
            query.Text.Length,
            query.Text,
            level == 0
                ? new List<CompletionItem> { new("folder"), new("other") }
                : new List<CompletionItem> { new("inner-a"), new("inner-b") }));

        var (host, _, popup) = ShowPicker(provider);
        using var __ = host;

        popup.Picked += (_, e) =>
        {
            if (e.Item.Display != "folder")
                return;

            level = 1;
            e.KeepOpen = true;
        };

        popup.Open();
        host.RunUntilIdle();

        host.SendText("fo");
        host.RunUntilIdle();
        Assert.Equal("folder", Assert.Single(popup.Entries).Item.Display);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);
        Assert.Equal("", popup.Pattern); // the pattern that found the folder cannot filter the folder's contents
        Assert.Equal(["inner-a", "inner-b"], popup.Entries.Select(e => e.Item.Display));
        Assert.Equal(CompletionTrigger.Continued, provider.LastTrigger);
    }

    // ───────────────── C46.21 — CloseOnEscape=false: the host owns the Esc gesture ─────────────────

    [Fact] // C46.21: text mode — Esc neither closes the session nor is handled; the host's handler sees it
    public void C46_21_CloseOnEscapeFalse_TextMode_EscapeBubbles_AndTheSessionStays()
    {
        var provider = PickerProvider("alpha", "amber", "beta"); // the whole-text shape serves text mode too
        var (host, box, popup) = Show(provider);
        using var _ = host;

        popup.CloseOnEscape = false;

        var reachedHost = 0;
        box.VisualParent!.KeyDown += (_, e) => { if (e.Key == Key.Escape) reachedHost++; };

        host.SendText("a");
        host.RunUntilIdle();
        Assert.True(popup.IsOpen);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.True(popup.IsOpen); // the session survived: Esc means "cancel the SURFACE" to this host
        Assert.Equal(1, reachedHost); // and the key actually arrived — nothing marked it handled on the way
        Assert.Equal("a", box.Text); // the field kept what was typed, same as a normal dismiss would

        popup.CloseOnEscape = true; // the default contract is unchanged: Esc closes and is consumed
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(popup.IsOpen);
        Assert.Equal(1, reachedHost); // handled — the host never saw the second Esc
    }

    [Fact] // C46.21b: picker mode — with CloseOnEscape=false not even the pattern peel may eat the key
    public void C46_21_CloseOnEscapeFalse_Picker_SkipsThePatternPeel_AndBubbles()
    {
        var provider = PickerProvider("docs", "downloads");
        var (host, anchor, popup) = ShowPicker(provider);
        using var _ = host;

        popup.CloseOnEscape = false;

        var reachedHost = 0;
        anchor.VisualParent!.KeyDown += (_, e) => { if (e.Key == Key.Escape) reachedHost++; };

        popup.Open();
        host.SendText("do");
        host.RunUntilIdle();
        Assert.Equal("do", popup.Pattern);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.True(popup.IsOpen);
        Assert.Equal("do", popup.Pattern); // the peel is Esc behaviour too, and Esc is not ours to spend
        Assert.Equal(1, reachedHost);
    }
}
