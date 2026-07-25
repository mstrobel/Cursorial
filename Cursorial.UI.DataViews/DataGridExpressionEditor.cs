using System.Diagnostics.CodeAnalysis;

using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.DataViews.Shaping.Expressions;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The criteria text editor (design doc §9.1; the mockup's "Filter — text editor"): a modal window
/// carrying a multi-line criteria <see cref="TextBox"/>, Columns/Functions token inserters, a LIVE
/// completion popup (§10.3), and the LIVE validation strip — every text change re-parses AND
/// re-compiles against the grid's fields (green "✓ Valid boolean expression" / red "✕ &lt;message&gt;
/// — column N", the mockup's vstrip), so Apply can never land an invalid tree. Apply lowers through
/// <see cref="DataGrid.TryApplyFilterExpression"/> — the ONE authority that stores the tree in
/// <c>Filter</c> and the SOURCE TEXT in <c>FilterExpressionText</c> (the §9.1 amendment: a
/// Custom-lowered filter keeps its original text grid-side). The completion popup (§10.3) offers
/// field names inside a <c>[…]</c> bracket and function tokens for a bare identifier, filtered by
/// the partial token under the caret; ↑/↓ move, Enter/Tab accept, Esc dismisses. LIVE SYNTAX
/// HIGHLIGHTING stays deferred — it needs a second orthogonal colored-run dimension on the
/// TextBox/TextPresenter (they render one brush, splitting runs only at the selection), which is
/// genuine text-tier surgery; the completion popup is the tractable slice.
/// </summary>
[RequiresDynamicCode("Criteria expressions compile against the grid's row type.")]
internal sealed class DataGridExpressionEditor
{
    private readonly DataGrid _grid;
    private readonly IReadOnlyList<CriteriaExpression.Field> _fields;
    private readonly Window _window;
    private readonly TextBox _text;
    private readonly TextBlock _strip;
    private readonly ComboBox _columnsMenu;
    private readonly ComboBox _functionsMenu;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Non-null when this instance is a duplicate-open rider adopting the LIVE dialog.</summary>
    private readonly DataGridExpressionEditor? _live;
    private bool _valid;
    private bool _syncingInsert;

    // §10.3 completion popup state (on the live instance; a duplicate rider adopts its _text).
    private Popup? _completionPopup;
    private ListBox? _completionList;
    private readonly List<(string Display, string Insert)> _completionItems = [];
    private int _completionStart;
    private int _completionCaret;
    private CompletionKind _completionKind;
    private bool _acceptingCompletion;

    private enum CompletionKind { Field, Function }

    /// <summary>The insertable function tokens (the §9.1 grammar's function set, call-shaped).</summary>
    private static readonly string[] FunctionTokens =
    [
        "Contains(", "StartsWith(", "EndsWith(", "Upper(", "Lower(",
        "Len(", "Trim(", "Abs(", "Round(", "IsNull(", "IsNullOrEmpty(",
        "Iif(", "Min(", "Max(", "Floor(", "Ceiling(", "Sqrt(", "Pow(", "Mod(",
    ];

    public DataGridExpressionEditor(DataGrid grid, string seedText)
    {
        _grid = grid;

        if (grid.ActiveFilterEditor is { } open)
        {
            // Already-open guard (§9.1 dialogs): the fire-and-forget entry point can run twice in
            // one input batch (a double-click posts two opens before the first modal blocks its
            // trigger). The duplicate never builds a second window — it ADOPTS the live dialog
            // (same window, same text box, the user's in-progress text preserved), so the grid's
            // tracking handle (re-stamped onto the duplicate) still reaches the ONE real dialog,
            // and ShowAsync rides its outcome instead of stacking a second modal.
            var live = open._live ?? open;
            _live = live;
            _fields = live._fields;
            _window = live._window;
            _text = live._text;
            _strip = live._strip;
            _columnsMenu = live._columnsMenu;
            _functionsMenu = live._functionsMenu;
            return;
        }

        _fields = grid.BuildCriteriaFields();

        var content = new StackPanel(); // vertical

        // The inserter row (the mockup's "ƒ Functions ▾ / [ ] Columns ▾" toolbar, ComboBox-shaped:
        // pick ⇒ insert the token at the caret, then reset so the face stays a menu, not a value).
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        toolbar.Children.Add(DataGridDialogHelpers.Caption("[ ] Columns:"));
        _columnsMenu = new ComboBox { ItemsSource = _fields.Select(f => f.DisplayName ?? f.Name).ToList(), MinWidth = 10 };
        _columnsMenu.SelectionChanged += (_, _) => OnInserterPicked(_columnsMenu,
            index => $"[{_fields[index].Name}]");
        toolbar.Children.Add(_columnsMenu);
        toolbar.Children.Add(DataGridDialogHelpers.Caption("ƒ Functions:"));
        _functionsMenu = new ComboBox { ItemsSource = FunctionTokens.Select(f => f.TrimEnd('(')).ToList(), MinWidth = 10 };
        _functionsMenu.SelectionChanged += (_, _) => OnInserterPicked(_functionsMenu,
            index => FunctionTokens[index]);
        toolbar.Children.Add(_functionsMenu);
        content.Children.Add(toolbar);

        _text = new TextBox
        {
            AcceptsReturn = true,
            MinLines = 3,
            MinWidth = 48,
            Text = seedText,
        };
        _text.TextChanged += (_, _) =>
        {
            Revalidate();
            UpdateCompletions(); // §10.3 — refresh the completion popup for the token under the caret
        };
        _text.PreviewKeyDown += OnTextPreviewKeyDown;
        content.Children.Add(_text);

        _strip = new TextBlock();
        content.Children.Add(_strip);

        _window = DataGridDialogHelpers.CreateDialogWindow(
            "Filter Editor — text mode",
            content,
            defaultButton: "_Apply",
            cancelButton: "_Cancel",
            minSize: new(0, 12),
            maxSize: null,
            ("⧉ _Designer", () => _ = RequestEditInBuilderAsync()),
            ("_Apply", Apply),
            ("_Cancel", Cancel));

        // Focus the criteria box once the dialog surface materializes (the parked-work idiom).
        _window.ContentRendered += (_, _) => UIApplication.Current?.Dispatcher.Post(() =>
        {
            if (IsOpen)
                _text.Focus(FocusNavigationMethod.Programmatic);
        });

        Revalidate();
    }

    /// <summary>Whether the dialog is currently shown (the grid's test hook gates on it).</summary>
    internal bool IsOpen => _window.IsShown;

    /// <summary>The criteria text box (tests type through the real control).</summary>
    internal TextBox TextBox => _text;

    /// <summary>The validation strip (tests assert the live verdict).</summary>
    internal TextBlock ValidationStrip => _strip;

    internal ComboBox ColumnsMenu => _columnsMenu;

    internal ComboBox FunctionsMenu => _functionsMenu;

    /// <summary>The live parse+compile verdict for the current text.</summary>
    internal bool IsValid => (_live ?? this)._valid;

    /// <summary>Shows modally; true ⇔ Apply landed a filter (Cancel/✕ ⇒ false).</summary>
    internal async Task<bool> ShowAsync()
    {
        if (_live is { } live)
            return await live._result.Task; // a duplicate open completes with the live dialog's outcome

        try
        {
            bool applied = await _window.ShowDialogAsync() is true;
            _result.TrySetResult(applied);
            return applied;
        }
        finally
        {
            _result.TrySetResult(false); // the throw path (dialog cancellation)
        }
    }

    /// <summary>An inserter pick: splice the token at the caret, then reset the menu face.</summary>
    private void OnInserterPicked(ComboBox menu, Func<int, string> tokenOf)
    {
        if (_syncingInsert || menu.SelectedIndex < 0)
            return;

        string token = tokenOf(menu.SelectedIndex);
        int caret = Math.Clamp(_text.CaretIndex, 0, _text.Text.Length);
        _text.Text = _text.Text.Insert(caret, token);
        _text.CaretIndex = caret + token.Length;

        _syncingInsert = true;
        try
        {
            menu.SelectedIndex = -1; // back to menu-face; re-picking the same item must fire again
        }
        finally
        {
            _syncingInsert = false;
        }
        _text.Focus(FocusNavigationMethod.Programmatic);
    }

    // ── §10.3 completion popup ────────────────────────────────────────────────────────────────────

    /// <summary>Whether the completion popup is open (test hook).</summary>
    internal bool IsCompletionOpen => (_live ?? this)._completionPopup?.IsOpen == true;

    /// <summary>The current completion candidates' display labels (test hook).</summary>
    internal IReadOnlyList<string> CompletionItems => (_live ?? this)._completionItems.Select(i => i.Display).ToList();

    /// <summary>The token under the caret (a field partial inside <c>[…]</c>, or a bare-identifier
    /// function partial) + where its replacement begins, or null when there is nothing to complete.</summary>
    private (int Start, string Prefix, CompletionKind Kind)? CompletionContext()
    {
        string text = _text.Text;
        int caret = Math.Clamp(_text.CaretIndex, 0, text.Length);

        // A field bracket: walk back to an unmatched '[' on the same line (stop at ']' / newline).
        int j = caret - 1;
        while (j >= 0 && text[j] is not ('[' or ']' or '\n'))
            j--;
        if (j >= 0 && text[j] == '[')
            return (j, text.Substring(j + 1, caret - (j + 1)), CompletionKind.Field);

        // A bare identifier run ending at the caret ⇒ a function/keyword partial.
        int k = caret;
        while (k > 0 && (char.IsLetterOrDigit(text[k - 1]) || text[k - 1] == '_'))
            k--;
        if (k < caret)
            return (k, text.Substring(k, caret - k), CompletionKind.Function);

        return null;
    }

    private void UpdateCompletions()
    {
        if (_acceptingCompletion)
            return;
        if (CompletionContext() is not { } context)
        {
            CloseCompletions();
            return;
        }

        _completionItems.Clear();
        if (context.Kind == CompletionKind.Field)
        {
            foreach (var field in _fields)
            {
                var display = field.DisplayName ?? field.Name;
                if (display.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase) ||
                    field.Name.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _completionItems.Add((display, $"[{field.Name}]"));
                }
            }
        }
        else
        {
            foreach (var token in FunctionTokens)
            {
                var display = token.TrimEnd('(');
                if (display.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
                    _completionItems.Add((display, token));
            }
        }

        if (_completionItems.Count == 0)
        {
            CloseCompletions();
            return;
        }

        _completionStart = context.Start;
        _completionCaret = Math.Clamp(_text.CaretIndex, 0, _text.Text.Length);
        _completionKind = context.Kind;
        EnsureCompletionUi();
        _completionList!.ItemsSource = _completionItems.Select(i => i.Display).ToList();
        _completionList.SelectedIndex = 0;
        _completionPopup!.PlacementTarget = _text;
        _completionPopup.Placement = PlacementMode.Bottom;
        _completionPopup.SetCurrentValue(Popup.IsOpenProperty, true);
    }

    private void EnsureCompletionUi()
    {
        if (_completionPopup is not null)
            return;
        // Non-focusable: the popup is a hint overlay — focus stays in the text box so typing keeps
        // driving it, and the box's PreviewKeyDown owns ↑/↓/Enter/Tab/Esc while it is open.
        _completionList = new ListBox { MinWidth = 16, MaxHeight = 6, Focusable = false, IsTabStop = false };
        _completionList.SetResourceReference(Control.BackgroundProperty, ThemeKeys.ElevationDialog);
        _completionPopup = new Popup { StaysOpen = true, Child = _completionList }; // lifecycle owned here, not light-dismiss
    }

    /// <summary>Accepts a candidate: splice its insert text over the token under the caret (test hook).</summary>
    internal void AcceptCompletion(int index)
    {
        if (index < 0 || index >= _completionItems.Count)
            return;
        string insert = _completionItems[index].Insert;
        string text = _text.Text;
        int start = Math.Clamp(_completionStart, 0, text.Length);
        int caret = Math.Clamp(_completionCaret, start, text.Length);
        // Audit fix: replace the WHOLE token, not just [start..caret) — the caret may sit mid-token
        // (e.g. "[Pr|ce]"), and leaving the "ce]" tail would corrupt the text ("[Price]ce]"). Scan
        // forward past the token's remaining chars (the insert supplies its own closing ] / '(').
        int end = TokenEnd(text, caret, _completionKind, insert.EndsWith('('));

        _acceptingCompletion = true;
        try
        {
            _text.Text = text.Remove(start, end - start).Insert(start, insert);
            _text.CaretIndex = start + insert.Length;
        }
        finally
        {
            _acceptingCompletion = false;
        }
        CloseCompletions();
        Revalidate();
        _text.Focus(FocusNavigationMethod.Programmatic);
    }

    /// <summary>The end offset of the token being completed (from the caret forward past its tail):
    /// a field runs to its closing <c>]</c> (consumed — the insert supplies one); a function runs to
    /// the end of its identifier (and a following <c>(</c> the insert supplies).</summary>
    private static int TokenEnd(string text, int caret, CompletionKind kind, bool insertHasParen)
    {
        int end = caret;
        if (kind == CompletionKind.Field)
        {
            while (end < text.Length && text[end] is not (']' or '[' or '\n'))
                end++;
            if (end < text.Length && text[end] == ']')
                end++; // the [Name] insert carries its own close bracket
        }
        else
        {
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                end++;
            if (insertHasParen && end < text.Length && text[end] == '(')
                end++; // the Name( insert carries its own open paren
        }
        return end;
    }

    private void CloseCompletions() => _completionPopup?.SetCurrentValue(Popup.IsOpenProperty, false);

    private void OnTextPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_completionPopup?.IsOpen != true || _completionList is null)
            return;
        switch (e.Key)
        {
            case Key.DownArrow:
                _completionList.SelectedIndex = Math.Min(_completionItems.Count - 1, _completionList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.UpArrow:
                _completionList.SelectedIndex = Math.Max(0, _completionList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                AcceptCompletion(_completionList.SelectedIndex);
                e.Handled = true; // Enter accepts the completion instead of inserting a newline
                break;
            case Key.Escape:
                CloseCompletions();
                e.Handled = true; // swallow — Esc dismisses the popup, not the dialog
                break;
        }
    }

    /// <summary>
    /// The per-keystroke validation lane: parse THEN compile (parse alone misses unknown fields /
    /// non-boolean roots — the §9.1 pipeline's semantic band), verdict onto the strip.
    /// </summary>
    private void Revalidate()
    {
        string text = _text.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStrip(valid: true, "✓ Empty — clears the filter");
            return;
        }

        if (_grid.RowType is not { } rowType)
        {
            SetStrip(valid: false, "✕ No row source — attach an ItemsSource first");
            return;
        }

        var result = CriteriaExpression.ToFilterNode(text, rowType, _fields);
        if (result.IsValid)
        {
            SetStrip(valid: true, "✓ Valid boolean expression");
            return;
        }

        if (result.Diagnostics.Count > 0)
        {
            var first = result.Diagnostics[0];
            SetStrip(valid: false, $"✕ {first.Message} — {PositionOf(text, first.Start)}");
        }
        else
        {
            SetStrip(valid: false, "✕ The expression is not valid");
        }
    }

    /// <summary>Offset → the mockup's "column N" (line-qualified past line 1 — the box is multi-line).</summary>
    private static string PositionOf(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        int column = offset - lineStart + 1;
        return line == 1 ? $"column {column}" : $"line {line}, column {column}";
    }

    private void SetStrip(bool valid, string message)
    {
        _valid = valid;
        _strip.Text = message;
        _strip.SetResourceReference(TextElement.ForegroundProperty,
            valid ? ThemeKeys.GreenBrush : ThemeKeys.RedBrush);
    }

    /// <summary>
    /// Apply: hand the text to the grid's one lowering authority; an invalid expression re-surfaces
    /// on the strip and the dialog stays open (the veto lives here, not in the scaffold).
    /// </summary>
    internal void Apply()
    {
        if (_live is { } live)
        {
            live.Apply(); // the strip verdict + _valid must land on the instance IsValid reads
            return;
        }

        string text = _text.Text;
        if (!_grid.TryApplyFilterExpression(string.IsNullOrWhiteSpace(text) ? null : text, out var diagnostics))
        {
            if (diagnostics.Count > 0)
                SetStrip(valid: false, $"✕ {diagnostics[0].Message} — {PositionOf(text, diagnostics[0].Start)}");
            return;
        }
        _window.Close(true);
    }

    /// <summary>Cancel: close without writing.</summary>
    internal void Cancel()
    {
        CloseCompletions();
        _window.Close(false);
    }

    /// <summary>Set when the dialog closed via the ⧉ Designer hop (the grid's entry point chains
    /// into the Filter Builder seeded from <see cref="BuilderSeedFilter"/>/<see cref="BuilderSeedText"/>).</summary>
    internal bool HopToBuilder => (_live ?? this)._hopToBuilder;

    /// <summary>The hop's lowered draft (null = seed the designer from the grid's active filter).</summary>
    internal FilterNode? BuilderSeedFilter => (_live ?? this)._builderSeedFilter;

    /// <summary>The hop draft's SOURCE TEXT (labels a compiled draft's locked ƒ row; re-stored on
    /// an un-edited apply — the §9.1 pair).</summary>
    internal string? BuilderSeedText => (_live ?? this)._builderSeedText;

    private bool _hopToBuilder;
    private FilterNode? _builderSeedFilter;
    private string? _builderSeedText;

    /// <summary>
    /// The reverse of the Builder's "ƒ Edit as Text" hop (⧉ Designer): lowers the CURRENT draft
    /// and reopens it in the designer — side-effect-free (nothing applies until the designer's
    /// OK). An empty draft hops seeded from the grid's active filter; invalid text vetoes on the
    /// strip; a draft that only lowers to a COMPILED predicate warns first — the designer shows it
    /// as one locked ƒ row it cannot edit, and the source text survives a designer round-trip only
    /// while that row is left as-is.
    /// </summary>
    internal Task RequestEditInBuilderAsync()
    {
        // The entry-point marshaling idiom: the confirm's dialog continuation resumes through the
        // CALLER'S sync context (RunContinuationsAsynchronously) — a caller without the UI context
        // (headless tests, bare threads) would land it on the thread pool and fault the Close with
        // a cross-thread access. InvokeAsync runs the core under the application's UI context.
        var application = UIApplication.Current;
        return application is null ? Task.CompletedTask : application.Dispatcher.InvokeAsync(RequestEditInBuilderCoreAsync);
    }

    private async Task RequestEditInBuilderCoreAsync()
    {
        if (_live is { } live)
        {
            await live.RequestEditInBuilderCoreAsync();
            return;
        }

        string text = _text.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _hopToBuilder = true; // nothing drafted — the designer opens on the active filter
            _window.Close(false);
            return;
        }

        if (_grid.RowType is not { } rowType)
        {
            SetStrip(valid: false, "✕ No row source — attach an ItemsSource first");
            return;
        }

        var lowered = CriteriaExpression.ToFilterNode(text, rowType, _fields);
        if (!lowered.IsValid || lowered.Filter is null)
        {
            Revalidate(); // the strip carries the veto — fix the draft (or Cancel) first
            return;
        }

        if (lowered.Filter is FilterPredicateNode)
        {
            bool proceed = await DataGridDialogHelpers.ConfirmAsync(
                "Open in designer?",
                [
                    "This expression isn't fully representable in the designer:",
                    "it will appear as a single locked ƒ row you cannot edit there.",
                    "Conditions can be added around it, but if you change the tree",
                    "and apply, the original text is kept only inside that row.",
                ],
                "Open designer", "Stay here");
            if (!proceed)
                return;
        }

        _builderSeedFilter = lowered.Filter;
        _builderSeedText = text;
        _hopToBuilder = true;
        _window.Close(false);
    }

    /// <summary>The teardown funnel (the grid closes an open dialog when it tears down).</summary>
    internal void CloseWindow()
    {
        CloseCompletions();
        if (_window.IsShown)
            _window.Close(false);
    }
}
