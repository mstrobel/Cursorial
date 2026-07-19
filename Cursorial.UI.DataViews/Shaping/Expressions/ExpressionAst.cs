namespace Cursorial.UI.DataViews.Shaping.Expressions;

/// <summary>
/// The criteria-language AST (design doc §9.1): positioned nodes shared by the text editor (parse +
/// validation strip), the Filter Builder (structural editing), and CF Expression rules. Positions
/// are 0-based character offsets into the source text; the diagnostics surface converts to columns.
/// </summary>
public abstract class CriteriaNode
{
    /// <summary>The source span (offset + length) this node covers.</summary>
    public required int Start { get; init; }
    public required int Length { get; init; }
}

/// <summary>The binary operators (comparisons, boolean composition, arithmetic).</summary>
public enum CriteriaBinaryOperator
{
    // Boolean composition.
    And,
    Or,
    // Comparisons (a total-order lane consistent with the engine's null-first comparisons).
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    /// <summary>SQL-style wildcard match (<c>%</c> any run, <c>_</c> one char); compiles to a cached regex.</summary>
    Like,
    // Arithmetic (numeric promotion applies).
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
}

/// <summary>A binary operation.</summary>
public sealed class CriteriaBinaryNode : CriteriaNode
{
    public required CriteriaBinaryOperator Operator { get; init; }
    public required CriteriaNode Left { get; init; }
    public required CriteriaNode Right { get; init; }
}

/// <summary>Boolean negation (<c>Not …</c> — binds looser than comparisons: <c>Not [A] = 1</c> negates the comparison).</summary>
public sealed class CriteriaNotNode : CriteriaNode
{
    public required CriteriaNode Operand { get; init; }
}

/// <summary>Arithmetic negation (<c>-x</c>).</summary>
public sealed class CriteriaNegateNode : CriteriaNode
{
    public required CriteriaNode Operand { get; init; }
}

/// <summary>A column reference (<c>[Field]</c>); binds FieldName-first, then header, case-insensitive.</summary>
public sealed class CriteriaFieldNode : CriteriaNode
{
    public required string Name { get; init; }
}

/// <summary>A literal: number (invariant), 'string' ('' escapes a quote), #date#, true/false, null.</summary>
public sealed class CriteriaLiteralNode : CriteriaNode
{
    /// <summary>The parsed value: decimal/double for numbers, string, DateOnly/DateTime, bool, or null.</summary>
    public required object? Value { get; init; }
}

/// <summary><c>[A] In (v1, v2, …)</c> — membership over literal-ish operands.</summary>
public sealed class CriteriaInNode : CriteriaNode
{
    public required CriteriaNode Operand { get; init; }
    public required IReadOnlyList<CriteriaNode> Items { get; init; }
}

/// <summary><c>[A] Between lo And hi</c> — inclusive range.</summary>
public sealed class CriteriaBetweenNode : CriteriaNode
{
    public required CriteriaNode Operand { get; init; }
    public required CriteriaNode Low { get; init; }
    public required CriteriaNode High { get; init; }
}

/// <summary>A function call (the fixed catalog — design doc §9.1).</summary>
public sealed class CriteriaFunctionNode : CriteriaNode
{
    public required string Name { get; init; }
    public required IReadOnlyList<CriteriaNode> Arguments { get; init; }
}

/// <summary>One parse/bind diagnostic (the validation strip's line: message + 1-based column).</summary>
public readonly record struct CriteriaDiagnostic(string Message, int Start, int Length)
{
    /// <summary>The 1-based display column.</summary>
    public int Column => Start + 1;

    public override string ToString() => $"{Message} (column {Column})";
}
