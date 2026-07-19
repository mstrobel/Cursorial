using System.Diagnostics.CodeAnalysis;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.DataViews.Shaping.Expressions;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>The add/edit dialog's rule-type axis (the mockup's left-pane list).</summary>
internal enum RuleEditorKind
{
    /// <summary>Single-condition cell highlight ⇒ a one-entry <see cref="ThresholdRule"/>.</summary>
    Highlight,
    /// <summary>Top/bottom-N ⇒ <see cref="TopBottomRule"/> (grayed out until the engine's TopK seam lands).</summary>
    TopBottom,
    /// <summary>In-cell fill bar ⇒ <see cref="DataBarRule"/>.</summary>
    DataBar,
    /// <summary>Heat gradient ⇒ <see cref="ColorScaleRule"/>.</summary>
    ColorScale,
    /// <summary>▲●▼ badge colors ⇒ a three-entry <see cref="ThresholdRule"/> convenience preset.</summary>
    IconSet,
    /// <summary>Row-level criteria expression ⇒ <see cref="PredicateRule"/>.</summary>
    Expression,
}

/// <summary>
/// The two-pane add/edit rule dialog (design doc §2.7 UI; the mockup's "Add / edit rule" windows):
/// rule types listed left (Highlight Cell / Top/Bottom / Data Bar / Color Scale / Icon Set /
/// Expression), the right pane reconfiguring per type with a live preview line sampled from the
/// target column. OK constructs the immutable <see cref="FormatRule"/> into <see cref="Result"/>;
/// the rules manager lands it on <see cref="TargetColumn"/>. Notes: Icon Set is a
/// <see cref="ThresholdRule"/> convenience preset — <see cref="CellFormat"/> carries colors, not
/// glyphs, so the ▲●▼ marks themselves stay with the column's text formatting (documented
/// limitation); Top/Bottom is disabled while <see cref="TopBottomRule.EngineSupportsTopK"/> is
/// false (the §9.5 TopK stats seam belongs to the engine workstream).
/// </summary>
[RequiresDynamicCode("Expression rules compile against the grid's row type.")]
internal sealed class DataGridRuleEditor
{
    private static readonly (RuleEditorKind Kind, string Caption)[] Types =
    [
        (RuleEditorKind.Highlight, "▦ Highlight Cell"),
        (RuleEditorKind.TopBottom, "⇅ Top / Bottom"),
        (RuleEditorKind.DataBar, "▭ Data Bar"),
        (RuleEditorKind.ColorScale, "▒ Color Scale"),
        (RuleEditorKind.IconSet, "◆ Icon Set"),
        (RuleEditorKind.Expression, "ƒ Expression"),
    ];

    /// <summary>The Highlight pane's operators (Between is a recorded polish deferral — two-bound UI).</summary>
    private static readonly (FilterOperator Op, string Label)[] HighlightOperators =
    [
        (FilterOperator.GreaterThan, "Greater than"),
        (FilterOperator.GreaterThanOrEqual, "Greater or equal"),
        (FilterOperator.LessThan, "Less than"),
        (FilterOperator.LessThanOrEqual, "Less or equal"),
        (FilterOperator.Equals, "Equals"),
        (FilterOperator.NotEquals, "Not equal"),
        (FilterOperator.Contains, "Text contains"),
        (FilterOperator.StartsWith, "Text starts with"),
    ];

    private static readonly (string Name, Color[] Stops)[] ScalePresets =
    [
        ("Red → Green", [Color.FromRgb(0xF7, 0x76, 0x8E), Color.FromRgb(0x9E, 0xCE, 0x6A)]),
        ("Green → Red", [Color.FromRgb(0x9E, 0xCE, 0x6A), Color.FromRgb(0xF7, 0x76, 0x8E)]),
        ("Red → Amber → Green", [Color.FromRgb(0xF7, 0x76, 0x8E), Color.FromRgb(0xE0, 0xAF, 0x68), Color.FromRgb(0x9E, 0xCE, 0x6A)]),
        ("Blue → Red", [Color.FromRgb(0x7A, 0xA2, 0xF7), Color.FromRgb(0xF7, 0x76, 0x8E)]),
    ];

    private static readonly CellFormat IconHighFormat = new(Foreground: Color.FromRgb(0x9E, 0xCE, 0x6A), Icon: "▲");
    private static readonly CellFormat IconMidFormat = new(Foreground: Color.FromRgb(0xE0, 0xAF, 0x68), Icon: "●");
    private static readonly CellFormat IconLowFormat = new(Foreground: Color.FromRgb(0xF7, 0x76, 0x8E), Icon: "▼");

    private readonly DataGrid _grid;
    private readonly List<DataGridColumn> _columns;
    private readonly Window _window;
    private readonly StackPanel _pane = new();
    private readonly TextBlock _paneTitle = new();
    private readonly Border _previewBorder;
    private readonly TextBlock _previewText;
    private readonly TextBlock _strip = new();
    private readonly List<(RuleEditorKind Kind, Button Button)> _typeButtons = [];

    // The per-kind state (fields, not controls — panes are rebuilt per type switch and re-seed from here).
    private RuleEditorKind _kind = RuleEditorKind.Highlight;
    private int _columnIndex;
    private int _highlightOpIndex;
    private string _highlightValue = string.Empty;
    private int _formatIndex; // shared by Highlight / TopBottom / Expression
    private bool _top = true;
    private string _countText = "3";
    private bool _percent;
    private int _scaleIndex;
    private string _iconHighText = string.Empty;
    private string _iconMidText = string.Empty;
    private string _expressionText = string.Empty;

    public DataGridRuleEditor(DataGrid grid, FormatRule? seed = null, DataGridColumn? seedColumn = null)
    {
        _grid = grid;
        _columns = grid.Columns.Where(c => c.FieldName is not null || c.KeySelector is not null).ToList();
        if (seedColumn is not null)
            _columnIndex = Math.Max(0, _columns.IndexOf(seedColumn));
        SeedFrom(seed);

        // Two panes side by side: the type list left, the configurator right.
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        var typeList = new StackPanel();
        foreach (var (kind, caption) in Types)
        {
            var button = new Button
            {
                Content = caption,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = kind != RuleEditorKind.TopBottom || TopBottomRule.EngineSupportsTopK ||
                            _kind == RuleEditorKind.TopBottom, // an EDIT of an authored TopBottom stays reachable
            };
            var captured = kind;
            button.Click += (_, _) => SetKind(captured);
            _typeButtons.Add((kind, button));
            typeList.Children.Add(button);
        }
        body.Children.Add(typeList);

        var right = new StackPanel { MinWidth = 30 };
        _paneTitle.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        right.Children.Add(_paneTitle);
        right.Children.Add(_pane);

        _previewText = new TextBlock { Text = "Sample" };
        _previewBorder = new Border { Child = _previewText };
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        previewRow.Children.Add(DataGridDialogHelpers.Caption("Preview:"));
        previewRow.Children.Add(_previewBorder);
        right.Children.Add(previewRow);
        right.Children.Add(_strip);
        body.Children.Add(right);

        _window = DataGridDialogHelpers.CreateDialogWindow("Edit Formatting Rule", body,
            ("OK", Ok), ("Cancel", Cancel));

        SetKind(_kind);
    }

    /// <summary>Whether the dialog is currently shown (the manager's test hook gates on it).</summary>
    internal bool IsOpen => _window.IsShown;

    /// <summary>The constructed rule (set by a successful OK).</summary>
    internal FormatRule? Result { get; private set; }

    /// <summary>The column the rule lands on (the manager adds/replaces there).</summary>
    internal DataGridColumn? TargetColumn => _columns.Count > 0 ? _columns[Math.Clamp(_columnIndex, 0, _columns.Count - 1)] : null;

    /// <summary>The active type pane.</summary>
    internal RuleEditorKind Kind => _kind;

    /// <summary>The type buttons (tests assert the Top/Bottom gray-out and switch panes).</summary>
    internal IReadOnlyList<(RuleEditorKind Kind, Button Button)> TypeButtons => _typeButtons;

    /// <summary>The validation strip.</summary>
    internal TextBlock ValidationStrip => _strip;

    /// <summary>The preview host (tests assert the ColorScale swatch runs and the child restore).</summary>
    internal Border PreviewBorder => _previewBorder;

    // The active pane's live controls (rebuilt per SetKind; tests drive the real cells).
    internal ComboBox? ColumnCombo { get; private set; }
    internal ComboBox? OperatorCombo { get; private set; }
    internal TextBox? ValueBox { get; private set; }
    internal ComboBox? FormatCombo { get; private set; }
    internal TextBox? ExpressionBox { get; private set; }
    internal TextBox? CountBox { get; private set; }

    internal async Task<bool> ShowAsync()
    {
        var result = await _window.ShowDialogAsync();
        return result is true;
    }

    /// <summary>Infers the editing pane from an existing rule (the manager's ✎ Edit path).</summary>
    private void SeedFrom(FormatRule? seed)
    {
        switch (seed)
        {
            case DataBarRule:
                _kind = RuleEditorKind.DataBar;
                break;

            case ColorScaleRule scale:
                _kind = RuleEditorKind.ColorScale;
                for (int i = 0; i < ScalePresets.Length; i++)
                {
                    if (ScalePresets[i].Stops.SequenceEqual(scale.Stops))
                    {
                        _scaleIndex = i;
                        break;
                    }
                }
                break;

            case ThresholdRule { Entries.Count: 3 } icons:
                // The 3-entry shape reads as the Icon Set preset (the one the editor itself makes).
                _kind = RuleEditorKind.IconSet;
                _iconHighText = DataGridDialogHelpers.FormatLiteral(icons.Entries[0].Value);
                _iconMidText = DataGridDialogHelpers.FormatLiteral(icons.Entries[1].Value);
                break;

            case ThresholdRule threshold when threshold.Entries.Count > 0:
                // Multi-entry thresholds edit their FIRST entry (full multi-entry editing is a
                // recorded polish deferral — the manager still lists and orders them).
                _kind = RuleEditorKind.Highlight;
                _highlightOpIndex = Math.Max(0, Array.FindIndex(HighlightOperators,
                    o => o.Op == threshold.Entries[0].Operator));
                _highlightValue = DataGridDialogHelpers.FormatLiteral(threshold.Entries[0].Value);
                _formatIndex = DataGridDialogHelpers.PresetIndexOf(threshold.Entries[0].Format);
                break;

            case PredicateRule predicate:
                // Seeds from the rule's carried SourceText (live-canary fix — the field used to
                // start empty; a hand-built lambda rule still has no text to offer).
                _kind = RuleEditorKind.Expression;
                _expressionText = predicate.SourceText ?? string.Empty;
                _formatIndex = DataGridDialogHelpers.PresetIndexOf(predicate.Format);
                break;

            case TopBottomRule topBottom:
                _kind = RuleEditorKind.TopBottom;
                _top = topBottom.Top;
                _countText = topBottom.Count.ToString();
                _percent = topBottom.Percent;
                _formatIndex = DataGridDialogHelpers.PresetIndexOf(topBottom.Format);
                break;
        }
    }

    // ── The right pane (rebuilt per type — the mockup's reconfiguring configurator) ───────────────

    internal void SetKind(RuleEditorKind kind)
    {
        _kind = kind;
        foreach (var (buttonKind, button) in _typeButtons)
        {
            if (buttonKind == kind)
                button.SetResourceReference(Control.BackgroundProperty, ThemeKeys.SelectionBrush);
            else
                button.ClearValue(Control.BackgroundProperty);
        }

        _pane.Children.Clear();
        OperatorCombo = null;
        ValueBox = null;
        FormatCombo = null;
        ExpressionBox = null;
        CountBox = null;
        _strip.Text = string.Empty;

        _paneTitle.Text = $"{Types.First(t => t.Kind == kind).Caption[2..]} — {TargetColumn?.EffectiveHeader ?? "(no column)"}";

        AddColumnPicker();
        switch (kind)
        {
            case RuleEditorKind.Highlight:
                AddCombo("Condition", HighlightOperators.Select(o => o.Label).ToList(), _highlightOpIndex,
                         i => _highlightOpIndex = i, combo => OperatorCombo = combo);
                AddText("Value", _highlightValue, t => _highlightValue = t, box => ValueBox = box);
                AddFormatPicker();
                break;

            case RuleEditorKind.TopBottom:
                AddCombo("Rank", ["Top", "Bottom"], _top ? 0 : 1, i => _top = i == 0);
                AddText("Count", _countText, t => _countText = t, box => CountBox = box);
                AddCombo("Measure", ["Items", "Percent"], _percent ? 1 : 0, i => _percent = i == 1);
                AddFormatPicker();
                if (!TopBottomRule.EngineSupportsTopK)
                {
                    var note = DataGridDialogHelpers.Caption("(inert until the engine's TopK stats seam lands)");
                    _pane.Children.Add(note);
                }
                break;

            case RuleEditorKind.DataBar:
                _pane.Children.Add(DataGridDialogHelpers.Caption("Fill = the value's share of the column's min→max range."));
                break;

            case RuleEditorKind.ColorScale:
                AddCombo("Scale", ScalePresets.Select(p => p.Name).ToList(), _scaleIndex, i => _scaleIndex = i);
                break;

            case RuleEditorKind.IconSet:
                _pane.Children.Add(DataGridDialogHelpers.Caption("Style: ▲●▼ (glyph + color per bucket)"));
                AddText("▲ when >=", _iconHighText, t => _iconHighText = t, box => ValueBox = box);
                AddText("● when >=", _iconMidText, t => _iconMidText = t);
                _pane.Children.Add(DataGridDialogHelpers.Caption("▼ otherwise"));
                break;

            case RuleEditorKind.Expression:
                _pane.Children.Add(DataGridDialogHelpers.Caption("Condition (boolean, whole-row scope):"));
                var expression = new TextBox { Text = _expressionText, MinWidth = 30 };
                expression.TextChanged += (_, _) =>
                {
                    _expressionText = expression.Text;
                    ValidateExpressionLive();
                };
                ExpressionBox = expression;
                _pane.Children.Add(expression);
                AddFormatPicker();
                ValidateExpressionLive();
                break;
        }

        UpdatePreview();
    }

    private void AddColumnPicker()
    {
        var combo = new ComboBox
        {
            ItemsSource = _columns.Select(c => c.EffectiveHeader).ToList(),
            SelectedIndex = _columns.Count > 0 ? Math.Clamp(_columnIndex, 0, _columns.Count - 1) : -1,
            MinWidth = 10,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                _columnIndex = combo.SelectedIndex;
                _paneTitle.Text = $"{Types.First(t => t.Kind == _kind).Caption[2..]} — {TargetColumn?.EffectiveHeader}";
                UpdatePreview();
            }
        };
        ColumnCombo = combo;
        AddLabeled(_kind == RuleEditorKind.Expression ? "Owner column" : "Apply to column", combo);
    }

    private void AddFormatPicker()
    {
        var combo = new ComboBox
        {
            ItemsSource = DataGridDialogHelpers.FormatPresets.Select(p => p.Name).ToList(),
            SelectedIndex = _formatIndex,
            MinWidth = 10,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                _formatIndex = combo.SelectedIndex;
                UpdatePreview();
            }
        };
        FormatCombo = combo;
        AddLabeled("Format", combo);
    }

    private void AddCombo(string label, IReadOnlyList<string> items, int selected, Action<int> onPick,
                          Action<ComboBox>? expose = null)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, MinWidth = 8 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                onPick(combo.SelectedIndex);
                UpdatePreview();
            }
        };
        expose?.Invoke(combo);
        AddLabeled(label, combo);
    }

    private void AddText(string label, string seed, Action<string> onChange, Action<TextBox>? expose = null)
    {
        var box = new TextBox { Text = seed, MinWidth = 10 };
        box.TextChanged += (_, _) => onChange(box.Text);
        expose?.Invoke(box);
        AddLabeled(label, box);
    }

    private void AddLabeled(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(DataGridDialogHelpers.Caption($"{label}:"));
        row.Children.Add(control);
        _pane.Children.Add(row);
    }

    // ── Live preview + expression validation ──────────────────────────────────────────────────────

    private void UpdatePreview()
    {
        // Non-scale kinds render through the single preview TextBlock; the ColorScale kind swaps a
        // stepped swatch panel into the border (one TextBlock cannot wear per-cell foregrounds).
        if (_kind != RuleEditorKind.ColorScale && !ReferenceEquals(_previewBorder.Child, _previewText))
            _previewBorder.Child = _previewText;

        switch (_kind)
        {
            case RuleEditorKind.DataBar:
                _previewText.Text = "████████░░";
                _previewText.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.CoolBrush);
                _previewText.TextWeight = TextWeight.Normal;
                _previewBorder.ClearValue(Border.BackgroundProperty);
                return;

            case RuleEditorKind.ColorScale:
            {
                var stops = ScalePresets[Math.Clamp(_scaleIndex, 0, ScalePresets.Length - 1)].Stops;
                _previewBorder.Child = DataGridDialogHelpers.ScaleSwatch(stops);
                _previewBorder.ClearValue(Border.BackgroundProperty);
                return;
            }

            case RuleEditorKind.IconSet:
                ApplyFormatToPreview("▲ 41%  ● 18%  ▼ 8%", IconHighFormat);
                return;

            default:
                ApplyFormatToPreview(SampleText(), DataGridDialogHelpers.FormatPresets[
                    Math.Clamp(_formatIndex, 0, DataGridDialogHelpers.FormatPresets.Length - 1)].Format);
                return;
        }
    }

    private void ApplyFormatToPreview(string text, CellFormat format)
    {
        _previewText.Text = text;
        if (format.Foreground is { } fg)
            _previewText.Foreground = new SolidColorBrush(fg);
        else
            _previewText.ClearValue(TextBlock.ForegroundProperty);
        if (format.Background is { } bg)
            _previewBorder.Background = new SolidColorBrush(bg);
        else
            _previewBorder.ClearValue(Border.BackgroundProperty);
        _previewText.TextWeight = format.Bold ? TextWeight.Bold : TextWeight.Normal;
    }

    /// <summary>A live sample from the target column (the mockup previews real values), else "Sample".</summary>
    private string SampleText()
    {
        if (TargetColumn is { } column && _grid.Controller is { } controller)
        {
            foreach (var (formatted, raw, _) in controller.GetDistinctValues(column, maxCount: 4))
            {
                if (raw is not null && formatted.Length > 0)
                    return formatted;
            }
        }
        return "Sample";
    }

    private void ValidateExpressionLive()
    {
        if (_kind != RuleEditorKind.Expression)
            return;
        if (string.IsNullOrWhiteSpace(_expressionText))
        {
            SetStrip(valid: false, "✕ Enter a boolean expression");
            return;
        }
        var compiled = CompileExpression(_expressionText);
        if (compiled.Predicate is not null && compiled.Diagnostics.Count == 0)
            SetStrip(valid: true, "✓ Valid boolean expression");
        else if (compiled.Diagnostics.Count > 0)
            SetStrip(valid: false, $"✕ {compiled.Diagnostics[0].Message} — column {compiled.Diagnostics[0].Start + 1}");
        else
            SetStrip(valid: false, "✕ The expression is not valid");
    }

    /// <summary>
    /// Compiles criteria text to the ROW-PREDICATE lambda (the <see cref="PredicateRule"/> shape —
    /// deliberately the compiler, not <c>ToFilterNode</c>: a predicate rule needs the lambda even
    /// for structurally-simple text).
    /// </summary>
    private CriteriaCompiler.Result CompileExpression(string text)
    {
        if (_grid.RowType is not { } rowType)
            return new CriteriaCompiler.Result(null, [new CriteriaDiagnostic("No row source", 0, 1)]);
        var parsed = CriteriaParser.Parse(text);
        if (parsed.Root is null || parsed.Diagnostics.Count > 0)
            return new CriteriaCompiler.Result(null, parsed.Diagnostics);
        var bindings = _grid.BuildCriteriaFields()
            .Select(f => new CriteriaCompiler.FieldBinding(f.Name, f.DisplayName, f.Selector, f.StringMode))
            .ToArray();
        return CriteriaCompiler.Compile(parsed.Root, rowType, bindings);
    }

    private void SetStrip(bool valid, string message)
    {
        _strip.Text = message;
        _strip.SetResourceReference(TextElement.ForegroundProperty,
            valid ? ThemeKeys.GreenBrush : ThemeKeys.RedBrush);
    }

    // ── Commit ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>OK: validate the pane and construct <see cref="Result"/> (validation vetoes on the strip).</summary>
    internal void Ok()
    {
        if (TargetColumn is not { } column)
        {
            SetStrip(valid: false, "✕ Pick a column");
            return;
        }

        var keyType = _grid.Controller?.GetColumnKeyType(column) ?? column.KeySelector?.ReturnType;
        switch (_kind)
        {
            case RuleEditorKind.Highlight:
            {
                var (op, opLabel) = HighlightOperators[Math.Clamp(_highlightOpIndex, 0, HighlightOperators.Length - 1)];
                object? value;
                if (op is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith)
                {
                    // Text operators apply to string keys only (the Filter Builder's gate,
                    // mirrored): the engine's condition builder has no text lane for other key
                    // types and THROWS — inside CompileFormatRules, after the dialog closed,
                    // poisoning every later rules recompile. Veto on the strip instead.
                    var underlying = keyType is null ? typeof(string) : Nullable.GetUnderlyingType(keyType) ?? keyType;
                    if (underlying != typeof(string))
                    {
                        SetStrip(valid: false, $"✕ '{opLabel}' needs a text column — {column.EffectiveHeader}");
                        return;
                    }
                    value = _highlightValue; // text operators carry the raw text
                }
                else if (!DataGridDialogHelpers.TryParseLiteral(keyType, _highlightValue, out value))
                {
                    SetStrip(valid: false, $"✕ '{_highlightValue}' is not a valid value for {column.EffectiveHeader}");
                    return;
                }
                if (value is null)
                {
                    SetStrip(valid: false, "✕ Enter a condition value");
                    return;
                }

                // Belt-and-braces: a threshold entry compiles through the same per-column condition
                // builder as a filter fragment — probe it (the auto-filter row's pre-write idiom)
                // so OK can never land a rule the engine's rules recompile throws on. Gated on the
                // column being SHAPED: an unshaped column is the engine's documented silent skip.
                if (_grid.Controller is { } controller && controller.GetColumnKeyType(column) is not null &&
                    !controller.CanCompileFilter(FilterNode.Condition(column, op, value)))
                {
                    SetStrip(valid: false, $"✕ '{_highlightValue}' does not apply to {column.EffectiveHeader}");
                    return;
                }

                Result = new ThresholdRule
                {
                    ColumnKey = column,
                    Entries = [(op, value, CurrentPresetFormat())],
                };
                break;
            }

            case RuleEditorKind.TopBottom:
            {
                if (!int.TryParse(_countText, out int count) || count <= 0)
                {
                    SetStrip(valid: false, "✕ Count must be a positive number");
                    return;
                }
                Result = new TopBottomRule
                {
                    ColumnKey = column,
                    Top = _top,
                    Count = count,
                    Percent = _percent,
                    Format = CurrentPresetFormat(),
                };
                break;
            }

            case RuleEditorKind.DataBar:
                Result = new DataBarRule { ColumnKey = column };
                break;

            case RuleEditorKind.ColorScale:
                Result = new ColorScaleRule
                {
                    ColumnKey = column,
                    Stops = ScalePresets[Math.Clamp(_scaleIndex, 0, ScalePresets.Length - 1)].Stops,
                };
                break;

            case RuleEditorKind.IconSet:
            {
                if (!DataGridDialogHelpers.TryParseLiteral(keyType, _iconHighText, out var high) || high is null ||
                    !DataGridDialogHelpers.TryParseLiteral(keyType, _iconMidText, out var mid) || mid is null)
                {
                    SetStrip(valid: false, "✕ Both thresholds need values");
                    return;
                }
                Result = new ThresholdRule
                {
                    ColumnKey = column,
                    Entries =
                    [
                        (FilterOperator.GreaterThanOrEqual, high, IconHighFormat),
                        (FilterOperator.GreaterThanOrEqual, mid, IconMidFormat),
                        (FilterOperator.LessThan, mid, IconLowFormat),
                    ],
                };
                break;
            }

            case RuleEditorKind.Expression:
            {
                var compiled = CompileExpression(_expressionText);
                if (compiled.Predicate is null || compiled.Diagnostics.Count > 0)
                {
                    ValidateExpressionLive();
                    return;
                }
                Result = new PredicateRule
                {
                    ColumnKey = column,
                    RowPredicate = compiled.Predicate,
                    Format = CurrentPresetFormat(),
                    SourceText = _expressionText,
                };
                break;
            }
        }

        _window.Close(true);
    }

    private CellFormat CurrentPresetFormat()
        => DataGridDialogHelpers.FormatPresets[
            Math.Clamp(_formatIndex, 0, DataGridDialogHelpers.FormatPresets.Length - 1)].Format;

    internal void Cancel() => _window.Close(false);

    /// <summary>The teardown funnel.</summary>
    internal void CloseWindow()
    {
        if (_window.IsShown)
            _window.Close(false);
    }
}
