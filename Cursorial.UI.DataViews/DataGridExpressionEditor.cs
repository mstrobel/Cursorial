using System.Diagnostics.CodeAnalysis;

using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.DataViews.Shaping.Expressions;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The criteria text editor (design doc §9.1; the mockup's "Filter — text editor"): a modal window
/// carrying a multi-line criteria <see cref="TextBox"/>, Columns/Functions token inserters, and the
/// LIVE validation strip — every text change re-parses AND re-compiles against the grid's fields
/// (green "✓ Valid boolean expression" / red "✕ &lt;message&gt; — column N", the mockup's vstrip),
/// so Apply can never land an invalid tree. Apply lowers through
/// <see cref="DataGrid.TryApplyFilterExpression"/> — the ONE authority that stores the tree in
/// <c>Filter</c> and the SOURCE TEXT in <c>FilterExpressionText</c> (the §9.1 amendment: a
/// Custom-lowered filter keeps its original text grid-side). Syntax highlighting and IntelliSense
/// are recorded polish deferrals — the strip + inserters are the v2 surface.
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

    /// <summary>The insertable function tokens (the §9.1 grammar's function set, call-shaped).</summary>
    private static readonly string[] FunctionTokens =
    [
        "Contains(", "StartsWith(", "EndsWith(", "Upper(", "Lower(",
        "Len(", "Trim(", "Abs(", "Round(", "IsNull(", "IsNullOrEmpty(",
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
        _text.TextChanged += (_, _) => Revalidate();
        content.Children.Add(_text);

        _strip = new TextBlock();
        content.Children.Add(_strip);

        _window = DataGridDialogHelpers.CreateDialogWindow("Filter Editor — text mode", content,
            ("⧉ Designer", () => _ = RequestEditInBuilderAsync()), ("Apply", Apply), ("Cancel", Cancel));

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
    internal void Cancel() => _window.Close(false);

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
        if (_window.IsShown)
            _window.Close(false);
    }
}
