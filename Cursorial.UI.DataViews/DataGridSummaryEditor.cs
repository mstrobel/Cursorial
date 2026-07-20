using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The summary editor dialog (design doc §2.5 / the mockup's "Edit Summary — Amount"): configures
/// ONE column summary — the aggregate kind, a <see cref="SummaryDescription.Format"/> string, and a
/// <see cref="SummaryDescription.DisplayTemplate"/> (<c>{0}</c> display text) — over a LIVE preview
/// of the value the footer would show. The engine already consumes Format/DisplayTemplate for both
/// group and total summaries; this is the UI that finally sets them (the context menu only toggled
/// the aggregate kind). OK upserts the <see cref="DataGrid.SummaryDescriptions"/> entry for
/// <c>(column, aggregate)</c>.
/// </summary>
internal sealed class DataGridSummaryEditor
{
    private readonly DataGrid _grid;
    private readonly DataGridColumn _column;
    private readonly Window _window;
    private readonly (AggregateKind Kind, string Label)[] _aggregates;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DataGridSummaryEditor(DataGrid grid, DataGridColumn column)
    {
        _grid = grid;
        _column = column;
        _aggregates = AggregatesFor(grid, column);

        // Seed from an existing summary on this column (else Count / blank).
        var existing = grid.SummaryDescriptions.FirstOrDefault(s => ReferenceEquals(s.ColumnKey, column));
        int seedIndex = existing.ColumnKey is null
            ? 0
            : Math.Max(0, Array.FindIndex(_aggregates, a => a.Kind == existing.Aggregate));

        AggregateCombo = new ComboBox
        {
            ItemsSource = _aggregates.Select(a => a.Label).ToList(),
            SelectedIndex = seedIndex,
            MinWidth = 12,
        };
        FormatBox = new TextBox { Text = existing.Format ?? string.Empty, MinWidth = 16 };
        TemplateBox = new TextBox { Text = existing.DisplayTemplate ?? string.Empty, MinWidth = 22 };
        Preview = new TextBlock();
        Preview.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.CoolBrush);

        var content = new StackPanel();
        content.Children.Add(Labeled("Aggregate", AggregateCombo));
        content.Children.Add(Labeled("Format string", FormatBox));
        content.Children.Add(Labeled("Display text", TemplateBox));
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        previewRow.Children.Add(DataGridDialogHelpers.Caption("Preview:"));
        previewRow.Children.Add(Preview);
        content.Children.Add(previewRow);
        content.Children.Add(DataGridDialogHelpers.Caption("Format: a ToString spec (N2, C0, 0.##). Display text: {0} is the value, e.g. \"Σ {0}\"."));

        AggregateCombo.SelectionChanged += (_, _) => UpdatePreview();
        FormatBox.TextChanged += (_, _) => UpdatePreview();
        TemplateBox.TextChanged += (_, _) => UpdatePreview();

        _window = DataGridDialogHelpers.CreateDialogWindow($"Edit Summary — {column.EffectiveHeader}", content,
            ("OK", Ok), ("Cancel", Cancel));
        UpdatePreview();
    }

    /// <summary>Whether the dialog is shown (test hook).</summary>
    internal bool IsOpen => _window.IsShown;

    internal ComboBox AggregateCombo { get; }
    internal TextBox FormatBox { get; }
    internal TextBox TemplateBox { get; }
    internal TextBlock Preview { get; }

    /// <summary>The aggregate kinds a column supports (numeric adds Sum/Average).</summary>
    internal static (AggregateKind Kind, string Label)[] AggregatesFor(DataGrid grid, DataGridColumn column)
    {
        var keyType = grid.Controller?.GetColumnKeyType(column) ?? column.KeySelector?.ReturnType;
        var underlying = keyType is null ? null : Nullable.GetUnderlyingType(keyType) ?? keyType;
        bool numeric = underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) ||
                       underlying == typeof(byte) || underlying == typeof(double) || underlying == typeof(float) ||
                       underlying == typeof(decimal) || underlying == typeof(uint) || underlying == typeof(ulong) ||
                       underlying == typeof(ushort);

        var list = new List<(AggregateKind, string)> { (AggregateKind.Count, "Count") };
        if (numeric)
        {
            list.Add((AggregateKind.Sum, "Sum"));
            list.Add((AggregateKind.Average, "Average"));
        }
        list.Add((AggregateKind.Min, "Min"));
        list.Add((AggregateKind.Max, "Max"));
        return list.ToArray();
    }

    private AggregateKind CurrentKind => _aggregates[Math.Clamp(AggregateCombo.SelectedIndex, 0, _aggregates.Length - 1)].Kind;
    private string? CurrentFormat => string.IsNullOrWhiteSpace(FormatBox.Text) ? null : FormatBox.Text;
    private string? CurrentTemplate => string.IsNullOrWhiteSpace(TemplateBox.Text) ? null : TemplateBox.Text;

    private void UpdatePreview()
        => Preview.Text = _grid.Controller?.ComputeSummaryText(_column, CurrentKind, CurrentFormat, CurrentTemplate) ?? "(no data)";

    internal async Task ShowAsync()
    {
        try
        {
            await _window.ShowDialogAsync();
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    /// <summary>OK: upsert the (column, aggregate) summary with the format + template.</summary>
    internal void Ok()
    {
        var kind = CurrentKind;
        for (int i = _grid.SummaryDescriptions.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_grid.SummaryDescriptions[i].ColumnKey, _column) && _grid.SummaryDescriptions[i].Aggregate == kind)
                _grid.SummaryDescriptions.RemoveAt(i);
        }
        _grid.SummaryDescriptions.Add(new SummaryDescription(_column, kind, CurrentFormat, CurrentTemplate));
        _window.Close(true);
    }

    internal void Cancel() => _window.Close(false);

    internal void CloseWindow()
    {
        if (_window.IsShown)
            _window.Close(false);
    }

    private static StackPanel Labeled(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(DataGridDialogHelpers.Caption($"{label}:"));
        row.Children.Add(control);
        return row;
    }
}
