using Cursorial.UI.DataViews.Shaping;

namespace Cursorial.UI.DataViews.Annotations;

// Grid-specific declarative column config, read at auto-generation. A deliberate DataViews sub-
// namespace (no XAML mapping) that complements the standard System.ComponentModel.DataAnnotations
// (header/format/order) with the grid's own concepts: default sort/grouping and conditional-format
// rules. Applied only when the grid has no authored sort/grouping (annotations never clobber the
// consumer's explicit state); format rules always add.

/// <summary>Declares the row type's default sort on this column (auto-gen). <see cref="Level"/>
/// orders multi-column sorts (lower first). Ignored when the grid already has sort descriptions.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DefaultSortAttribute(SortDirection direction = SortDirection.Ascending, int level = 0) : Attribute
{
    /// <summary>The sort direction.</summary>
    public SortDirection Direction { get; } = direction;

    /// <summary>The sort level (lower is more significant).</summary>
    public int Level { get; } = level;
}

/// <summary>Declares that the grid groups by this column by default (auto-gen). <see cref="Level"/>
/// orders nested grouping (lower is the outer level). Ignored when the grid is already grouped.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DefaultGroupAttribute(int level = 0) : Attribute
{
    /// <summary>The nesting level (lower is the outer group).</summary>
    public int Level { get; } = level;
}

/// <summary>Adds an in-cell data-bar conditional format to this column (auto-gen; numeric columns).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DataBarAttribute : Attribute;

/// <summary>Adds a color-scale (heat) conditional format to this column (auto-gen). Stops are
/// <c>#RRGGBB</c> hex, low → high (2 or 3).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColorScaleAttribute(params string[] stops) : Attribute
{
    /// <summary>The gradient stop hex colors, low → high.</summary>
    public string[] Stops { get; } = stops;
}

/// <summary>Adds a highlight (threshold) conditional format to this column (auto-gen). Repeatable —
/// each attribute is one first-match entry. The <see cref="Value"/> is a literal converted to the
/// column's key type; <see cref="Foreground"/>/<see cref="Background"/> are <c>#RRGGBB</c> hex.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class HighlightAttribute(FilterOperator @operator, object value,
                                       string? foreground = null, string? background = null, bool bold = false) : Attribute
{
    /// <summary>The comparison operator.</summary>
    public FilterOperator Operator { get; } = @operator;

    /// <summary>The comparison value (a literal converted to the column's key type at compile).</summary>
    public object Value { get; } = value;

    /// <summary>The matched-cell foreground (#RRGGBB), or null.</summary>
    public string? Foreground { get; } = foreground;

    /// <summary>The matched-cell background (#RRGGBB), or null.</summary>
    public string? Background { get; } = background;

    /// <summary>Whether the matched cell is bold.</summary>
    public bool Bold { get; } = bold;
}
