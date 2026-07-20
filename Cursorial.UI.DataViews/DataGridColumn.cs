using System.Linq.Expressions;

using Cursorial.Rendering.Text;
using Cursorial.UI.DataViews.Shaping;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The context handed to a <see cref="DataGridColumn.Validator"/> at commit (§10.2): the target
/// <see cref="Column"/>, the proposed edit's raw <see cref="Text"/>, and the row it applies to
/// (<see cref="RowId"/> ≥ 0 for an existing row; <see cref="IsNewRow"/> for the new-row template,
/// where the id is not yet assigned). Cross-cell rules can close over the grid in the delegate.
/// </summary>
public readonly record struct DataGridCellValidationContext(DataGridColumn Column, string Text, int RowId, bool IsNewRow);

/// <summary>The auto-filter row's per-column cell kind (design doc §3.4 — the mockup's mixed filter row).</summary>
public enum FilterCellKind
{
    /// <summary>A text condition editor with the operator grammar (<c>&gt; &gt;= &lt; &lt;= = &lt;&gt;</c>; bare text = contains/starts-with).</summary>
    Text,
    /// <summary>The distinct-value checklist anchored to the cell (the mockup's <c>(All) ▾</c> cells).</summary>
    DistinctPicker,
    /// <summary>No filter cell for this column.</summary>
    Disabled,
}

/// <summary>
/// The cell editor a column hosts while editing (design doc §3.2 — the edit-row element host; the
/// mockup's "Inline editing" per-column editors). <see cref="Auto"/> resolves from the column's CLR
/// key type at begin-edit time (bool/enum → <see cref="Combo"/>, DateOnly/DateTime →
/// <see cref="Date"/>, numeric → <see cref="Spin"/>, everything else → <see cref="Text"/>);
/// <see cref="None"/> disables editing for the column regardless of setter availability.
/// </summary>
public enum DataGridEditorKind
{
    /// <summary>Resolve the editor from the column's key type (the default).</summary>
    Auto,
    /// <summary>A TextBox editor (the v1 path — free text through the compiled setter).</summary>
    Text,
    /// <summary>A ComboBox editor (enum names / true–false / the column's distinct values).</summary>
    Combo,
    /// <summary>A DatePicker editor (typed date text + the ▤ calendar drop-down).</summary>
    Date,
    /// <summary>A TextBox editor with ▲▼ stepping (Up/Down ±1, Shift ±10 — the mockup's spin editor).</summary>
    Spin,
    /// <summary>The column is not editable, even when a compiled setter exists.</summary>
    None,
}

/// <summary>
/// Where a column is pinned while the grid scrolls horizontally (design doc §9.2 — frozen columns).
/// Fixed columns resolve FIRST in the layout (x 0..frozenWidth regardless of declaration position
/// among scrolling columns) and draw unshifted over the scrolled band.
/// </summary>
public enum DataGridColumnFixed
{
    /// <summary>The column scrolls with the horizontal offset (the default).</summary>
    None,
    /// <summary>The column is pinned at the left edge (drawn over scrolled-under columns).</summary>
    Left,
}

/// <summary>
/// One grid column (design doc §3.1 — the panel-mandated public column API): a
/// <see cref="UIObject"/>-backed description object (XAML-instantiable; the loader fills the grid's
/// get-only <c>Columns</c>; property-system-backed so bindings/dynamic resources can arrive without
/// a break). The grid maps columns onto the engine's <see cref="ShapingColumnDescription"/>; the
/// column instance itself is the shaping identity every sort/group/filter description references.
/// </summary>
public class DataGridColumn : UIObject
{
    /// <summary>The header caption (defaults to <see cref="FieldName"/> when unset).</summary>
    public static readonly StyledProperty<string?> HeaderProperty =
        UIProperty.Register<DataGridColumn, string?>(nameof(Header));

    /// <summary>The dotted property path into the row type (the XAML lane; <see cref="KeySelector"/> is the code lane).</summary>
    public static readonly StyledProperty<string?> FieldNameProperty =
        UIProperty.Register<DataGridColumn, string?>(nameof(FieldName));

    /// <summary>The width (fixed cells / Auto band-limited best-fit / star — design doc §1).</summary>
    public static readonly StyledProperty<DataGridLength> WidthProperty =
        UIProperty.Register<DataGridColumn, DataGridLength>(nameof(Width), DataGridLength.Auto,
            changed: static (sender, _, _) => ((DataGridColumn)sender).RaiseGeometryChanged());

    /// <summary>The lower width bound in cells (applies to every unit).</summary>
    public static readonly StyledProperty<int> MinWidthProperty =
        UIProperty.Register<DataGridColumn, int>(nameof(MinWidth), 3,
            changed: static (sender, _, _) => ((DataGridColumn)sender).RaiseGeometryChanged());

    /// <summary>The upper width bound in cells (0 = unbounded).</summary>
    public static readonly StyledProperty<int> MaxWidthProperty =
        UIProperty.Register<DataGridColumn, int>(nameof(MaxWidth),
            changed: static (sender, _, _) => ((DataGridColumn)sender).RaiseGeometryChanged());

    /// <summary>The display format string (also the group-caption/summary format for this column).</summary>
    public static readonly StyledProperty<string?> FormatProperty =
        UIProperty.Register<DataGridColumn, string?>(nameof(Format));

    /// <summary>Cell text alignment (numeric columns typically right-align — auto-generation's per-type default).</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        UIProperty.Register<DataGridColumn, TextAlignment>(nameof(TextAlignment));

    /// <summary>Whether header gestures may sort by this column.</summary>
    public static readonly StyledProperty<bool> AllowSortProperty =
        UIProperty.Register<DataGridColumn, bool>(nameof(AllowSort), true);

    /// <summary>Whether this column may be a grouping level.</summary>
    public static readonly StyledProperty<bool> AllowGroupProperty =
        UIProperty.Register<DataGridColumn, bool>(nameof(AllowGroup), true);

    /// <summary>Whether the filter surfaces (popup + auto-filter row) apply to this column.</summary>
    public static readonly StyledProperty<bool> AllowFilterProperty =
        UIProperty.Register<DataGridColumn, bool>(nameof(AllowFilter), true);

    /// <summary>The auto-filter row's cell kind for this column.</summary>
    public static readonly StyledProperty<FilterCellKind> FilterCellKindProperty =
        UIProperty.Register<DataGridColumn, FilterCellKind>(nameof(FilterCellKind));

    /// <summary>Whether the column renders (hidden columns keep their shaping identity).</summary>
    public static readonly StyledProperty<bool> VisibleProperty =
        UIProperty.Register<DataGridColumn, bool>(nameof(Visible), true,
            changed: static (sender, _, _) => ((DataGridColumn)sender).RaiseGeometryChanged());

    /// <summary>String sort/group comparison mode (CurrentCulture default; Ordinal is the perf opt-in — design doc §2.2).</summary>
    public static readonly StyledProperty<StringComparison> SortModeProperty =
        UIProperty.Register<DataGridColumn, StringComparison>(nameof(SortMode), StringComparison.CurrentCulture);

    /// <summary>The cell editor this column hosts while editing (<see cref="DataGridEditorKind.Auto"/> resolves by key type — §3.2).</summary>
    public static readonly StyledProperty<DataGridEditorKind> EditorKindProperty =
        UIProperty.Register<DataGridColumn, DataGridEditorKind>(nameof(EditorKind));

    /// <summary>Pins the column while the grid scrolls horizontally (design doc §9.2).</summary>
    public static readonly StyledProperty<DataGridColumnFixed> FixedProperty =
        UIProperty.Register<DataGridColumn, DataGridColumnFixed>(nameof(Fixed),
            changed: static (sender, _, _) => ((DataGridColumn)sender).RaiseGeometryChanged());

    /// <summary>
    /// Raised when a LAYOUT-bearing property changes (Width/Min/Max/Visible/Fixed — audit W2-6):
    /// the owning grid subscribes and routes into its geometry funnel, so a runtime property write
    /// re-resolves the bands without the author knowing about the internal funnel. (The gesture
    /// surfaces still call the funnel directly — the double invalidation coalesces.)
    /// </summary>
    internal event EventHandler? GeometryChanged;

    private void RaiseGeometryChanged() => GeometryChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc cref="HeaderProperty"/>
    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <inheritdoc cref="FieldNameProperty"/>
    public string? FieldName { get => GetValue(FieldNameProperty); set => SetValue(FieldNameProperty, value); }

    /// <inheritdoc cref="WidthProperty"/>
    public DataGridLength Width { get => GetValue(WidthProperty); set => SetValue(WidthProperty, value); }

    /// <inheritdoc cref="MinWidthProperty"/>
    public int MinWidth { get => GetValue(MinWidthProperty); set => SetValue(MinWidthProperty, value); }

    /// <inheritdoc cref="MaxWidthProperty"/>
    public int MaxWidth { get => GetValue(MaxWidthProperty); set => SetValue(MaxWidthProperty, value); }

    /// <inheritdoc cref="FormatProperty"/>
    public string? Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    /// <inheritdoc cref="TextAlignmentProperty"/>
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    /// <inheritdoc cref="AllowSortProperty"/>
    public bool AllowSort { get => GetValue(AllowSortProperty); set => SetValue(AllowSortProperty, value); }

    /// <inheritdoc cref="AllowGroupProperty"/>
    public bool AllowGroup { get => GetValue(AllowGroupProperty); set => SetValue(AllowGroupProperty, value); }

    /// <inheritdoc cref="AllowFilterProperty"/>
    public bool AllowFilter { get => GetValue(AllowFilterProperty); set => SetValue(AllowFilterProperty, value); }

    /// <inheritdoc cref="FilterCellKindProperty"/>
    public FilterCellKind FilterCellKind { get => GetValue(FilterCellKindProperty); set => SetValue(FilterCellKindProperty, value); }

    /// <inheritdoc cref="VisibleProperty"/>
    public bool Visible { get => GetValue(VisibleProperty); set => SetValue(VisibleProperty, value); }

    /// <inheritdoc cref="SortModeProperty"/>
    public StringComparison SortMode { get => GetValue(SortModeProperty); set => SetValue(SortModeProperty, value); }

    /// <inheritdoc cref="EditorKindProperty"/>
    public DataGridEditorKind EditorKind { get => GetValue(EditorKindProperty); set => SetValue(EditorKindProperty, value); }

    /// <inheritdoc cref="FixedProperty"/>
    public DataGridColumnFixed Fixed { get => GetValue(FixedProperty); set => SetValue(FixedProperty, value); }

    /// <summary>The typed row→key selector (code-only authoring lane; wins over <see cref="FieldName"/>).</summary>
    public LambdaExpression? KeySelector { get; set; }

    /// <summary>
    /// The conditional-formatting rules authored on this column (design doc §2.7 — get-only, the
    /// collection idiom). The grid collects every column's rules into the engine on each shape push;
    /// a <see cref="Shaping.PredicateRule"/>'s row-level format rides here too (declared on the
    /// column whose rule set owns it, evaluated row-wide).
    /// </summary>
    public IList<FormatRule> FormatRules { get; } = [];

    /// <summary>
    /// An optional per-column commit-time validator (§10.2): invoked with the proposed edit's raw
    /// text (and the row context) BEFORE it is written through the compiled setter. Return
    /// <see langword="null"/> to accept the value, or an error message to REJECT it — the commit is
    /// vetoed, the hosted editor stays open in the error look, and the message shows on the edit bar.
    /// A code-only lane (delegates aren't XAML-bindable); it runs on both the existing-row and
    /// new-row commit paths. Text-level by design — for typed checks, parse
    /// <see cref="DataGridCellValidationContext.Text"/> against the column's key type in the delegate.
    /// </summary>
    public Func<DataGridCellValidationContext, string?>? Validator { get; set; }

    /// <summary>The effective header caption.</summary>
    public string EffectiveHeader => Header ?? FieldName ?? string.Empty;

    /// <summary>Maps this column onto the engine description (the grid's shaping bridge).</summary>
    internal ShapingColumnDescription ToShapingDescription() => new()
    {
        Key = this,
        FieldName = FieldName,
        KeySelector = KeySelector,
        Format = Format,
        StringComparison = SortMode,
    };
}
