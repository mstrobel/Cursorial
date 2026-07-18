using System.Diagnostics.CodeAnalysis;

using Cursorial.UI.Controls;
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
            ("Apply", Apply), ("Cancel", Cancel));

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
    internal bool IsValid => _valid;

    /// <summary>Shows modally; true ⇔ Apply landed a filter (Cancel/✕ ⇒ false).</summary>
    internal async Task<bool> ShowAsync()
    {
        var result = await _window.ShowDialogAsync();
        return result is true;
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

    /// <summary>The teardown funnel (the grid closes an open dialog when it tears down).</summary>
    internal void CloseWindow()
    {
        if (_window.IsShown)
            _window.Close(false);
    }
}
