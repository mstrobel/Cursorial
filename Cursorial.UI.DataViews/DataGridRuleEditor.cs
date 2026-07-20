using System.Diagnostics.CodeAnalysis;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.DataViews.Shaping.Expressions;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>The add/edit dialog's rule-type axis (the mockup's left-pane list).</summary>
internal enum RuleEditorKind
{
    /// <summary>Condition → cell highlight ⇒ a multi-entry <see cref="ThresholdRule"/> (§10.4).</summary>
    Highlight,
    /// <summary>Top/bottom-N ⇒ <see cref="TopBottomRule"/> (grayed out until the engine's TopK seam lands).</summary>
    TopBottom,
    /// <summary>In-cell fill bar ⇒ <see cref="DataBarRule"/>.</summary>
    DataBar,
    /// <summary>Heat gradient ⇒ <see cref="ColorScaleRule"/>.</summary>
    ColorScale,
    /// <summary>▲●▼ badge glyphs ⇒ a three-entry <see cref="ThresholdRule"/> convenience preset.</summary>
    IconSet,
    /// <summary>Row-level criteria expression ⇒ <see cref="PredicateRule"/>.</summary>
    Expression,
}

/// <summary>
/// The two-pane add/edit rule dialog (design doc §2.7 UI; the mockup's "Add / edit rule" windows):
/// rule types listed left (Highlight Cell / Top/Bottom / Data Bar / Color Scale / Icon Set /
/// Expression), the right pane reconfiguring per type with a live preview line sampled from the
/// target column. OK constructs the immutable <see cref="FormatRule"/> into <see cref="Result"/>;
/// the rules manager lands it on <see cref="TargetColumn"/>. The §10 deferred items land here: the
/// Highlight pane is a MULTI-ENTRY condition list (§10.4) with a Between two-bound operator (§10.5),
/// every format picker offers a "Custom…" reveal (arbitrary fg/bg/bold/inverse — §10.6), the color
/// scale takes custom stops, and the icon set's glyphs are editable. Top/Bottom is disabled while
/// <see cref="TopBottomRule.EngineSupportsTopK"/> is false (the §9.5 TopK stats seam).
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

    /// <summary>The Highlight pane's operators — Between (§10.5) reveals a second value box.</summary>
    private static readonly (FilterOperator Op, string Label)[] HighlightOperators =
    [
        (FilterOperator.GreaterThan, "Greater than"),
        (FilterOperator.GreaterThanOrEqual, "Greater or equal"),
        (FilterOperator.LessThan, "Less than"),
        (FilterOperator.LessThanOrEqual, "Less or equal"),
        (FilterOperator.Equals, "Equals"),
        (FilterOperator.NotEquals, "Not equal"),
        (FilterOperator.Between, "Between"),
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
    // Highlight: a list of condition entries (multi-entry — §10.4). Each carries its own format slot.
    private readonly List<HighlightEntry> _highlightEntries = [];
    private readonly List<HighlightRowControls> _highlightRows = [];
    // TopBottom / Expression: one shared format slot (preset or custom — §10.6).
    private readonly FormatSlot _sharedFormat = new();
    private bool _top = true;
    private string _countText = "3";
    private bool _percent;
    // ColorScale: a preset index (== ScalePresets.Length ⇒ custom) + the custom stop hex list (§10.6).
    private int _scaleIndex;
    private readonly List<string> _customStops = ["#F7768E", "#E0AF68", "#9ECE6A"];
    // IconSet: editable glyphs + thresholds (§10.6).
    private string _iconHighGlyph = "▲", _iconMidGlyph = "●", _iconLowGlyph = "▼";
    private string _iconHighText = string.Empty;
    private string _iconMidText = string.Empty;
    // Expression
    private string _expressionText = string.Empty;
    // The Highlight pane's live entries host (rebuilt on add/remove).
    private StackPanel? _highlightHost;

    public DataGridRuleEditor(DataGrid grid, FormatRule? seed = null, DataGridColumn? seedColumn = null)
    {
        _grid = grid;
        _columns = grid.Columns.Where(c => c.FieldName is not null || c.KeySelector is not null).ToList();
        if (seedColumn is not null)
            _columnIndex = Math.Max(0, _columns.IndexOf(seedColumn));
        SeedFrom(seed);
        if (_highlightEntries.Count == 0)
            _highlightEntries.Add(new HighlightEntry());

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

        var right = new StackPanel { MinWidth = 34 };
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
    internal TextBox? SecondValueBox { get; private set; }
    internal ComboBox? FormatCombo { get; private set; }
    internal ComboBox? ScaleCombo { get; private set; }
    internal TextBox? ExpressionBox { get; private set; }
    internal TextBox? CountBox { get; private set; }

    /// <summary>The Highlight pane's condition rows (§10.4 — tests drive multi-entry through them).</summary>
    internal IReadOnlyList<HighlightRowControls> HighlightRows => _highlightRows;

    /// <summary>The custom fg/bg/attr controls of the shared (TopBottom/Expression) format slot (§10.6).</summary>
    internal CustomFormatControls? SharedFormatControls { get; private set; }

    /// <summary>The color-scale custom-stop hex boxes (ColorScale pane; §10.6).</summary>
    internal IReadOnlyList<TextBox> ScaleStopBoxes => _scaleStopBoxes;

    /// <summary>The icon-set glyph boxes ▲/●/▼ (IconSet pane; §10.6).</summary>
    internal IReadOnlyList<TextBox> IconGlyphBoxes => _iconGlyphBoxes;

    /// <summary>The icon-set threshold boxes (▲ when ≥ / ● when ≥).</summary>
    internal IReadOnlyList<TextBox> IconThresholdBoxes => _iconThresholdBoxes;

    /// <summary>The format combo's "Custom…" index (== the preset count).</summary>
    internal static int CustomFormatIndex => DataGridDialogHelpers.FormatPresets.Length;

    /// <summary>The scale combo's "Custom…" index (== the preset count).</summary>
    internal static int CustomScaleIndex => ScalePresets.Length;

    private readonly List<TextBox> _scaleStopBoxes = [];
    private readonly List<TextBox> _iconGlyphBoxes = [];
    private readonly List<TextBox> _iconThresholdBoxes = [];

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
                int presetIndex = -1;
                for (int i = 0; i < ScalePresets.Length; i++)
                {
                    if (ScalePresets[i].Stops.SequenceEqual(scale.Stops))
                    {
                        presetIndex = i;
                        break;
                    }
                }
                if (presetIndex >= 0)
                {
                    _scaleIndex = presetIndex;
                }
                else
                {
                    // A non-preset scale seeds the Custom… stops (§10.6).
                    _scaleIndex = ScalePresets.Length;
                    _customStops.Clear();
                    foreach (var stop in scale.Stops)
                        _customStops.Add(DataGridDialogHelpers.ColorToHex(stop));
                }
                break;

            case ThresholdRule threshold when threshold.Entries.Count > 0:
                if (threshold.Entries.Count == 3 && threshold.Entries.All(e => e.Format.Icon is not null))
                {
                    // The Icon Set shape (a glyph per bucket — the editor's own construction).
                    _kind = RuleEditorKind.IconSet;
                    _iconHighGlyph = threshold.Entries[0].Format.Icon ?? "▲";
                    _iconMidGlyph = threshold.Entries[1].Format.Icon ?? "●";
                    _iconLowGlyph = threshold.Entries[2].Format.Icon ?? "▼";
                    _iconHighText = DataGridDialogHelpers.FormatLiteral(threshold.Entries[0].Value);
                    _iconMidText = DataGridDialogHelpers.FormatLiteral(threshold.Entries[1].Value);
                }
                else
                {
                    // A plain (icon-less) threshold seeds the multi-entry Highlight list (§10.4).
                    _kind = RuleEditorKind.Highlight;
                    _highlightEntries.Clear();
                    foreach (var e in threshold.Entries)
                    {
                        var entry = new HighlightEntry
                        {
                            OpIndex = Math.Max(0, Array.FindIndex(HighlightOperators, o => o.Op == e.Operator)),
                            Value = DataGridDialogHelpers.FormatLiteral(e.Value),
                            Second = DataGridDialogHelpers.FormatLiteral(e.SecondValue),
                        };
                        entry.Format.Seed(e.Format);
                        _highlightEntries.Add(entry);
                    }
                }
                break;

            case PredicateRule predicate:
                // Seeds from the rule's carried SourceText (§10.8 — the field used to start empty;
                // a hand-built lambda rule with no SourceText still offers none). The row format is
                // seeded too, custom colors included (§10.6).
                _kind = RuleEditorKind.Expression;
                _expressionText = predicate.SourceText ?? string.Empty;
                _sharedFormat.Seed(predicate.Format);
                break;

            case TopBottomRule topBottom:
                _kind = RuleEditorKind.TopBottom;
                _top = topBottom.Top;
                _countText = topBottom.Count.ToString();
                _percent = topBottom.Percent;
                _sharedFormat.Seed(topBottom.Format);
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
        SecondValueBox = null;
        FormatCombo = null;
        ScaleCombo = null;
        ExpressionBox = null;
        CountBox = null;
        SharedFormatControls = null;
        _highlightRows.Clear();
        _strip.Text = string.Empty;

        _paneTitle.Text = $"{Types.First(t => t.Kind == kind).Caption[2..]} — {TargetColumn?.EffectiveHeader ?? "(no column)"}";

        AddColumnPicker();
        switch (kind)
        {
            case RuleEditorKind.Highlight:
            {
                if (_highlightEntries.Count == 0)
                    _highlightEntries.Add(new HighlightEntry());
                _highlightHost = new StackPanel();
                BuildHighlightEntries();
                _pane.Children.Add(_highlightHost);
                break;
            }

            case RuleEditorKind.TopBottom:
                AddCombo("Rank", ["Top", "Bottom"], _top ? 0 : 1, i => _top = i == 0);
                AddText("Count", _countText, t => _countText = t, box => CountBox = box);
                AddCombo("Measure", ["Items", "Percent"], _percent ? 1 : 0, i => _percent = i == 1);
                AppendFormatEditor(_pane, _sharedFormat, UpdatePreview, combo => FormatCombo = combo,
                                   controls => SharedFormatControls = controls);
                if (!TopBottomRule.EngineSupportsTopK)
                    _pane.Children.Add(DataGridDialogHelpers.Caption("(inert until the engine's TopK stats seam lands)"));
                break;

            case RuleEditorKind.DataBar:
                _pane.Children.Add(DataGridDialogHelpers.Caption("Fill = the value's share of the column's min→max range."));
                break;

            case RuleEditorKind.ColorScale:
            {
                var names = ScalePresets.Select(p => p.Name).Append("Custom…").ToList();
                var scaleCombo = new ComboBox { ItemsSource = names, SelectedIndex = Math.Clamp(_scaleIndex, 0, names.Count - 1), MinWidth = 14 };
                var custom = new StackPanel();
                _scaleStopBoxes.Clear();
                for (int s = 0; s < 3; s++)
                {
                    int si = s;
                    while (_customStops.Count <= si)
                        _customStops.Add(string.Empty);
                    var box = new TextBox { Text = _customStops[si], MinWidth = 9 };
                    box.TextChanged += (_, _) => { _customStops[si] = box.Text; UpdatePreview(); };
                    _scaleStopBoxes.Add(box);
                    custom.Children.Add(LabeledRow($"Stop {si + 1} #", box));
                }
                custom.Children.Add(DataGridDialogHelpers.Caption("2 or 3 #RRGGBB stops, low → high (leave stop 3 blank for a 2-stop scale)."));
                void SyncScaleCustom() => custom.Visibility = _scaleIndex >= ScalePresets.Length ? Visibility.Visible : Visibility.Collapsed;
                scaleCombo.SelectionChanged += (_, _) =>
                {
                    if (scaleCombo.SelectedIndex >= 0)
                    {
                        _scaleIndex = scaleCombo.SelectedIndex;
                        SyncScaleCustom();
                        UpdatePreview();
                    }
                };
                SyncScaleCustom();
                ScaleCombo = scaleCombo;
                AddLabeled("Scale", scaleCombo);
                _pane.Children.Add(custom);
                break;
            }

            case RuleEditorKind.IconSet:
                _iconGlyphBoxes.Clear();
                _iconThresholdBoxes.Clear();
                _pane.Children.Add(DataGridDialogHelpers.Caption("A glyph + threshold per bucket (▼ otherwise)."));
                AddText("▲ glyph", _iconHighGlyph, t => _iconHighGlyph = t, box => _iconGlyphBoxes.Add(box));
                AddText("▲ when >=", _iconHighText, t => _iconHighText = t, box => { ValueBox = box; _iconThresholdBoxes.Add(box); });
                AddText("● glyph", _iconMidGlyph, t => _iconMidGlyph = t, box => _iconGlyphBoxes.Add(box));
                AddText("● when >=", _iconMidText, t => _iconMidText = t, box => _iconThresholdBoxes.Add(box));
                AddText("▼ glyph", _iconLowGlyph, t => _iconLowGlyph = t, box => _iconGlyphBoxes.Add(box));
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
                AppendFormatEditor(_pane, _sharedFormat, UpdatePreview, combo => FormatCombo = combo,
                                   controls => SharedFormatControls = controls);
                ValidateExpressionLive();
                break;
        }

        UpdatePreview();
    }

    // ── The Highlight multi-entry list (§10.4) ────────────────────────────────────────────────────

    /// <summary>Appends a blank condition row (the ＋ button; also the tests' multi-entry hook — §10.4).</summary>
    internal void AddHighlightCondition()
    {
        if (_kind != RuleEditorKind.Highlight || _highlightHost is null)
            return;
        _highlightEntries.Add(new HighlightEntry());
        BuildHighlightEntries();
        UpdatePreview();
    }

    private void BuildHighlightEntries()
    {
        if (_highlightHost is not { } host)
            return;
        host.Children.Clear();
        _highlightRows.Clear();
        OperatorCombo = null;
        ValueBox = null;
        SecondValueBox = null;
        FormatCombo = null;
        for (int i = 0; i < _highlightEntries.Count; i++)
            BuildHighlightRow(host, i);

        var add = new Button { Content = "＋ Add condition", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => AddHighlightCondition();
        host.Children.Add(add);
    }

    private void BuildHighlightRow(StackPanel host, int index)
    {
        var entry = _highlightEntries[index];
        var block = new StackPanel { Spacing = 0 };

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        var op = new ComboBox
        {
            ItemsSource = HighlightOperators.Select(o => o.Label).ToList(),
            SelectedIndex = Math.Clamp(entry.OpIndex, 0, HighlightOperators.Length - 1),
            MinWidth = 8,
        };
        var value = new TextBox { Text = entry.Value, MinWidth = 7 };
        var andCaption = DataGridDialogHelpers.Caption("and");
        var second = new TextBox { Text = entry.Second, MinWidth = 7 };
        var remove = new Button { Content = "✕", IsEnabled = _highlightEntries.Count > 1 };

        void SyncBetween()
        {
            bool between = HighlightOperators[Math.Clamp(entry.OpIndex, 0, HighlightOperators.Length - 1)].Op == FilterOperator.Between;
            andCaption.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
            second.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        }

        op.SelectionChanged += (_, _) =>
        {
            if (op.SelectedIndex >= 0)
            {
                entry.OpIndex = op.SelectedIndex;
                SyncBetween();
                UpdatePreview();
            }
        };
        value.TextChanged += (_, _) => { entry.Value = value.Text; UpdatePreview(); };
        second.TextChanged += (_, _) => { entry.Second = second.Text; UpdatePreview(); };
        int captured = index;
        remove.Click += (_, _) =>
        {
            if (captured < _highlightEntries.Count && _highlightEntries.Count > 1)
                _highlightEntries.RemoveAt(captured);
            BuildHighlightEntries();
            UpdatePreview();
        };

        line.Children.Add(op);
        line.Children.Add(value);
        line.Children.Add(andCaption);
        line.Children.Add(second);
        line.Children.Add(remove);
        block.Children.Add(line);

        ComboBox? rowFormatCombo = null;
        CustomFormatControls? rowCustom = null;
        AppendFormatEditor(block, entry.Format, UpdatePreview,
                           combo => rowFormatCombo = combo, custom => rowCustom = custom);
        SyncBetween();
        host.Children.Add(block);

        _highlightRows.Add(new HighlightRowControls(op, value, second, remove, rowFormatCombo!, rowCustom!));
        if (index == 0)
        {
            OperatorCombo = op;
            ValueBox = value;
            SecondValueBox = second;
            FormatCombo = rowFormatCombo;
        }
    }

    // ── The format editor (preset + Custom… reveal — §10.6) ───────────────────────────────────────

    private void AppendFormatEditor(Panel host, FormatSlot slot, Action onChange,
                                    Action<ComboBox>? exposeCombo = null, Action<CustomFormatControls>? exposeCustom = null)
    {
        int customIndex = DataGridDialogHelpers.FormatPresets.Length;
        var names = DataGridDialogHelpers.FormatPresets.Select(p => p.Name).Append("Custom…").ToList();
        var combo = new ComboBox { ItemsSource = names, SelectedIndex = Math.Clamp(slot.Index, 0, names.Count - 1), MinWidth = 10 };

        var custom = new StackPanel();
        var fg = new TextBox { Text = DataGridDialogHelpers.ColorToHex(slot.Fg), MinWidth = 9 };
        fg.TextChanged += (_, _) => { slot.Fg = DataGridDialogHelpers.TryParseColor(fg.Text, out var c) ? c : null; onChange(); };
        var bg = new TextBox { Text = DataGridDialogHelpers.ColorToHex(slot.Bg), MinWidth = 9 };
        bg.TextChanged += (_, _) => { slot.Bg = DataGridDialogHelpers.TryParseColor(bg.Text, out var c) ? c : null; onChange(); };
        var bold = new CheckBox { Content = "Bold", IsChecked = slot.Bold };
        bold.AddHandler(ToggleButton.IsCheckedChangedEvent, (object? _, RoutedEventArgs _) => { slot.Bold = bold.IsChecked == true; onChange(); });
        var inverse = new CheckBox { Content = "Inverse", IsChecked = slot.Inverse };
        inverse.AddHandler(ToggleButton.IsCheckedChangedEvent, (object? _, RoutedEventArgs _) => { slot.Inverse = inverse.IsChecked == true; onChange(); });
        custom.Children.Add(LabeledRow("Text #", fg));
        custom.Children.Add(LabeledRow("Fill #", bg));
        var attrRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        attrRow.Children.Add(bold);
        attrRow.Children.Add(inverse);
        custom.Children.Add(attrRow);

        void SyncCustom() => custom.Visibility = slot.Index >= customIndex ? Visibility.Visible : Visibility.Collapsed;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                slot.Index = combo.SelectedIndex;
                SyncCustom();
                onChange();
            }
        };
        SyncCustom();

        host.Children.Add(LabeledRow("Format", combo));
        host.Children.Add(custom);
        exposeCombo?.Invoke(combo);
        exposeCustom?.Invoke(new CustomFormatControls(fg, bg, bold, inverse));
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
        box.TextChanged += (_, _) => { onChange(box.Text); UpdatePreview(); };
        expose?.Invoke(box);
        AddLabeled(label, box);
    }

    private void AddLabeled(string label, UIElement control) => _pane.Children.Add(LabeledRow(label, control));

    private static StackPanel LabeledRow(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(DataGridDialogHelpers.Caption($"{label}:"));
        row.Children.Add(control);
        return row;
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
                _previewBorder.Child = DataGridDialogHelpers.ScaleSwatch(PreviewScaleStops());
                _previewBorder.ClearValue(Border.BackgroundProperty);
                return;

            case RuleEditorKind.Highlight:
                ApplyFormatToPreview(SampleText(), _highlightEntries.Count > 0 ? _highlightEntries[0].Format.ToFormat() : default);
                return;

            case RuleEditorKind.IconSet:
                ApplyFormatToPreview($"{_iconHighGlyph} 41%  {_iconMidGlyph} 18%  {_iconLowGlyph} 8%", IconHighFormat);
                return;

            default:
                ApplyFormatToPreview(SampleText(), _sharedFormat.ToFormat());
                return;
        }
    }

    private Color[] PreviewScaleStops()
    {
        if (_scaleIndex < ScalePresets.Length)
            return ScalePresets[Math.Clamp(_scaleIndex, 0, ScalePresets.Length - 1)].Stops;
        var parsed = new List<Color>();
        foreach (var s in _customStops)
        {
            if (DataGridDialogHelpers.TryParseColor(s, out var c))
                parsed.Add(c);
        }
        return parsed.Count >= 2 ? parsed.ToArray() : ScalePresets[0].Stops; // preview fallback on an incomplete custom scale
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
                if (_highlightEntries.Count == 0)
                {
                    SetStrip(valid: false, "✕ Add at least one condition");
                    return;
                }
                var built = new List<ThresholdEntry>(_highlightEntries.Count);
                foreach (var entry in _highlightEntries)
                {
                    if (!TryBuildThresholdEntry(column, keyType, entry, out var thresholdEntry))
                        return; // the veto is on the strip
                    built.Add(thresholdEntry);
                }
                Result = new ThresholdRule { ColumnKey = column, Entries = built };
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
                    Format = _sharedFormat.ToFormat(),
                };
                break;
            }

            case RuleEditorKind.DataBar:
                Result = new DataBarRule { ColumnKey = column };
                break;

            case RuleEditorKind.ColorScale:
            {
                Color[] stops;
                if (_scaleIndex < ScalePresets.Length)
                {
                    stops = ScalePresets[Math.Clamp(_scaleIndex, 0, ScalePresets.Length - 1)].Stops;
                }
                else
                {
                    var parsed = new List<Color>();
                    foreach (var s in _customStops)
                    {
                        if (!string.IsNullOrWhiteSpace(s) && DataGridDialogHelpers.TryParseColor(s, out var c))
                            parsed.Add(c);
                        else if (!string.IsNullOrWhiteSpace(s))
                        {
                            SetStrip(valid: false, $"✕ '{s}' is not a valid #RRGGBB color");
                            return;
                        }
                    }
                    if (parsed.Count is < 2 or > 3)
                    {
                        SetStrip(valid: false, "✕ A custom scale needs 2 or 3 valid stops (low → high)");
                        return;
                    }
                    stops = parsed.ToArray();
                }
                Result = new ColorScaleRule { ColumnKey = column, Stops = stops };
                break;
            }

            case RuleEditorKind.IconSet:
            {
                if (!DataGridDialogHelpers.TryParseLiteral(keyType, _iconHighText, out var high) || high is null ||
                    !DataGridDialogHelpers.TryParseLiteral(keyType, _iconMidText, out var mid) || mid is null)
                {
                    SetStrip(valid: false, "✕ Both thresholds need values");
                    return;
                }
                var highFormat = IconHighFormat with { Icon = NullIfBlank(_iconHighGlyph) };
                var midFormat = IconMidFormat with { Icon = NullIfBlank(_iconMidGlyph) };
                var lowFormat = IconLowFormat with { Icon = NullIfBlank(_iconLowGlyph) };
                Result = new ThresholdRule
                {
                    ColumnKey = column,
                    Entries =
                    [
                        new ThresholdEntry(FilterOperator.GreaterThanOrEqual, high, highFormat),
                        new ThresholdEntry(FilterOperator.GreaterThanOrEqual, mid, midFormat),
                        new ThresholdEntry(FilterOperator.LessThan, mid, lowFormat),
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
                    Format = _sharedFormat.ToFormat(),
                    SourceText = _expressionText,
                };
                break;
            }
        }

        _window.Close(true);
    }

    /// <summary>Builds one Highlight condition entry, vetoing on the strip (returns false on any failure).</summary>
    private bool TryBuildThresholdEntry(DataGridColumn column, Type? keyType, HighlightEntry entry, out ThresholdEntry result)
    {
        result = default;
        var (op, opLabel) = HighlightOperators[Math.Clamp(entry.OpIndex, 0, HighlightOperators.Length - 1)];

        object? value;
        if (op is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith)
        {
            // Text operators apply to string keys only (the Filter Builder's gate, mirrored): the
            // engine's condition builder has no text lane for other key types and THROWS inside the
            // rules recompile after the dialog closed. Veto on the strip instead.
            var underlying = keyType is null ? typeof(string) : Nullable.GetUnderlyingType(keyType) ?? keyType;
            if (underlying != typeof(string))
            {
                SetStrip(valid: false, $"✕ '{opLabel}' needs a text column — {column.EffectiveHeader}");
                return false;
            }
            value = entry.Value; // text operators carry the raw text
        }
        else if (!DataGridDialogHelpers.TryParseLiteral(keyType, entry.Value, out value))
        {
            SetStrip(valid: false, $"✕ '{entry.Value}' is not a valid value for {column.EffectiveHeader}");
            return false;
        }
        if (value is null)
        {
            SetStrip(valid: false, "✕ Enter a condition value");
            return false;
        }

        object? second = null;
        if (op == FilterOperator.Between)
        {
            if (!DataGridDialogHelpers.TryParseLiteral(keyType, entry.Second, out second) || second is null)
            {
                SetStrip(valid: false, $"✕ 'Between' needs an upper bound for {column.EffectiveHeader}");
                return false;
            }
        }

        // Belt-and-braces: a threshold entry compiles through the same per-column condition builder
        // as a filter fragment — probe it (the auto-filter row's pre-write idiom) so OK can never
        // land a rule the engine's rules recompile throws on. Gated on the column being SHAPED.
        if (_grid.Controller is { } controller && controller.GetColumnKeyType(column) is not null &&
            !controller.CanCompileFilter(FilterNode.Condition(column, op, value, second)))
        {
            SetStrip(valid: false, $"✕ '{entry.Value}' does not apply to {column.EffectiveHeader}");
            return false;
        }

        result = new ThresholdEntry(op, value, entry.Format.ToFormat(), second);
        return true;
    }

    private static string? NullIfBlank(string glyph) => string.IsNullOrEmpty(glyph) ? null : glyph;

    internal void Cancel() => _window.Close(false);

    /// <summary>The teardown funnel.</summary>
    internal void CloseWindow()
    {
        if (_window.IsShown)
            _window.Close(false);
    }

    // ── State holders ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One Highlight condition entry's mutable state (§10.4).</summary>
    private sealed class HighlightEntry
    {
        public int OpIndex;
        public string Value = string.Empty;
        public string Second = string.Empty;
        public readonly FormatSlot Format = new();
    }

    /// <summary>
    /// A format choice: a preset index, or (when <see cref="Index"/> == the preset count) a custom
    /// fg/bg/bold/inverse (§10.6). <see cref="ToFormat"/> yields the immutable <see cref="CellFormat"/>.
    /// </summary>
    private sealed class FormatSlot
    {
        public int Index;
        public Color? Fg;
        public Color? Bg;
        public bool Bold;
        public bool Inverse;

        public CellFormat ToFormat() => Index < DataGridDialogHelpers.FormatPresets.Length
            ? DataGridDialogHelpers.FormatPresets[Index].Format
            : new CellFormat(Fg, Bg, Bold, Inverse);

        /// <summary>Seeds from an existing format: an exact preset match, else Custom… with its lanes.</summary>
        public void Seed(CellFormat format)
        {
            int exact = DataGridDialogHelpers.ExactPresetIndexOf(format);
            if (exact >= 0)
            {
                Index = exact;
                return;
            }
            Index = DataGridDialogHelpers.FormatPresets.Length;
            Fg = format.Foreground;
            Bg = format.Background;
            Bold = format.Bold;
            Inverse = format.Inverse;
        }
    }

    /// <summary>A Highlight condition row's live controls (tests drive multi-entry through these).</summary>
    internal sealed record HighlightRowControls(ComboBox Operator, TextBox Value, TextBox Second, Button Remove,
                                                ComboBox Format, CustomFormatControls Custom);

    /// <summary>The custom-format reveal's live controls (tests assert §10.6 custom colors).</summary>
    internal sealed record CustomFormatControls(TextBox Foreground, TextBox Background, CheckBox Bold, CheckBox Inverse);
}
