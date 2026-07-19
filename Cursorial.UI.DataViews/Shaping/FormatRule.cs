using System.Linq.Expressions;

using Cursorial.Output;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The style verdict one formatting rule produces for a cell (or, for a row rule, a whole row) —
/// design doc §2.7. A small value struct so band-fill evaluation allocates nothing; the painter
/// folds it over the theme's resting brushes at draw time (an unset lane falls through to the
/// theme). Colors are <see cref="Cursorial.Output.Color"/> — the Core value primitive, not a UI
/// brush — so the rule model stays presentation-free and headless-testable (invariant 1's spirit:
/// no UI machinery; Color is pure data).
/// </summary>
public readonly record struct CellFormat(
    Color? Foreground = null,
    Color? Background = null,
    bool Bold = false,
    bool Inverse = false)
{
    /// <summary>Whether every lane is unset (the painter's fast path — pure theme drawing).</summary>
    public bool IsEmpty => Foreground is null && Background is null && !Bold && !Inverse;

    /// <summary>
    /// Folds this (the CELL verdict) over <paramref name="under"/> (the ROW verdict): set color
    /// lanes win per lane; attribute flags OR (a bold threshold on a dimmed row stays both).
    /// </summary>
    public CellFormat OverlayOn(in CellFormat under) => new(
        Foreground ?? under.Foreground,
        Background ?? under.Background,
        Bold || under.Bold,
        Inverse || under.Inverse);
}

/// <summary>
/// One conditional-formatting rule (design doc §2.7 — the panel-widened v1 set: DataBar, Threshold,
/// ColorScale, Predicate; TopBottom deferred behind the stats top-k seam). Rules are immutable
/// description objects authored on <c>DataGridColumn.FormatRules</c>; the controller compiles the
/// active set against its typed key vectors per shape and evaluates at BAND-FILL time — never at
/// paint (§2.7 [panel]).
/// </summary>
public abstract class FormatRule
{
    private protected FormatRule() { }

    /// <summary>The column identity the rule targets (the shaping identity — the grid column instance).</summary>
    public required object ColumnKey { get; init; }

    /// <summary>
    /// Whether the rule participates in band-fill evaluation (the rules manager's "On" toggle —
    /// the one MUTABLE knob on an otherwise immutable description: flipping it must not require
    /// reconstructing the rule the manager lists). The grid's collection funnel is the guard — a
    /// disabled rule is excluded from the set pushed into the engine, so the controller's compiled
    /// kit never sees it; re-enable rides the next <c>RefreshFormatRules</c>/shape push.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The in-cell data bar (the mockup's Amount <c>█░</c> fills): fill fraction = the cell value's
/// position in the column's stats range (min/max over the current filtered view — recomputed once
/// per publish, the same block that anchors <see cref="ColorScaleRule"/> [panel]). Numeric key
/// columns only; the rule is inert elsewhere.
/// </summary>
public sealed class DataBarRule : FormatRule;

/// <summary>
/// Ordered condition → format entries, FIRST MATCH WINS (the mockup's Margin ▲●▼ badge colors /
/// Status tints). Each entry compiles through the column's typed condition builder — the same
/// compiled comparison lane the filter engine uses, so semantics (null ordering, literal
/// conversion) can never drift from filtering.
/// </summary>
public sealed class ThresholdRule : FormatRule
{
    /// <summary>The ordered entries (evaluation stops at the first operator match).</summary>
    public required IReadOnlyList<(FilterOperator Operator, object Value, CellFormat Format)> Entries { get; init; }
}

/// <summary>
/// A 2/3-stop heat scale over the column's stats range (§2.7): the cell foreground interpolates
/// linearly between the stops by the value's normalized position (stop 0 at min … last stop at
/// max). Applies only when no <see cref="ThresholdRule"/> already set the cell's foreground.
/// </summary>
public sealed class ColorScaleRule : FormatRule
{
    /// <summary>The gradient stops, low → high (2 or 3).</summary>
    public required IReadOnlyList<Color> Stops { get; init; }
}

/// <summary>
/// A compiled ROW predicate → row-level format (the mockup's "dim Cancelled" rule): the predicate
/// (an <c>Expression&lt;Func&lt;TRow,bool&gt;&gt;</c>) compiles through the same lane as
/// <see cref="FilterNode.Custom"/>; a matching row's format underlays every cell's own verdict.
/// </summary>
public sealed class PredicateRule : FormatRule
{
    /// <summary>The single-parameter row predicate lambda.</summary>
    public required LambdaExpression RowPredicate { get; init; }

    /// <summary>The row-level format applied when the predicate matches.</summary>
    public required CellFormat Format { get; init; }
}
