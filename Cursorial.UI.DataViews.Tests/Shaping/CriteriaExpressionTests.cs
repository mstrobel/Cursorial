using System.Linq.Expressions;

using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.DataViews.Shaping.Expressions;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The criteria language (design doc §9.1): grammar/precedence/positions, typed compilation
/// (promotion, null semantics, strings, Like, functions), and the Filter-Builder structural bridge
/// (text ⇄ FilterNode round-trips).
/// </summary>
public class CriteriaExpressionTests
{
    private enum Status { Shipped, Processing, OnHold, Cancelled }

    private sealed record Order(string Id, string? Region, decimal Amount, double? Margin, DateOnly Date, Status Status);

    private static readonly Order[] Rows =
    [
        new("SO-1042", "East", 12450m, 0.38, new(2026, 5, 3), Status.Shipped),
        new("SO-1044", "East", 31900m, 0.41, new(2026, 5, 6), Status.Processing),
        new("SO-1046", "South", 19800m, 0.28, new(2026, 5, 9), Status.Shipped),
        new("SO-1047", "West", 27300m, null, new(2026, 5, 11), Status.OnHold),
        new("SO-1049", null, 19800m, 0.31, new(2026, 5, 14), Status.Cancelled),
    ];

    private static CriteriaCompiler.FieldBinding Bind<TKey>(string name, Expression<Func<Order, TKey>> selector, string? display = null,
                                                            StringComparison mode = StringComparison.CurrentCulture)
        => new(name, display, selector, mode);

    private static readonly CriteriaCompiler.FieldBinding[] Fields =
    [
        Bind("Id", o => o.Id),
        Bind("Region", o => o.Region),
        Bind("Amount", o => o.Amount, "Order Total"),
        Bind("Margin", o => o.Margin),
        Bind("Date", o => o.Date),
        Bind("Status", o => o.Status),
    ];

    /// <summary>Parses + compiles + runs the predicate over the sample rows, returning matching Ids.</summary>
    private static string[] Run(string text)
    {
        var parsed = CriteriaParser.Parse(text);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics));
        var compiled = CriteriaCompiler.Compile(parsed.Root!, typeof(Order), Fields);
        Assert.True(compiled.IsValid, string.Join("; ", compiled.Diagnostics));
        var predicate = (Func<Order, bool>)compiled.Predicate!.Compile();
        return Rows.Where(predicate).Select(o => o.Id).ToArray();
    }

    private static IReadOnlyList<CriteriaDiagnostic> FailCompile(string text)
    {
        var parsed = CriteriaParser.Parse(text);
        Assert.True(parsed.IsValid, "expected a PARSE success with a COMPILE failure");
        var compiled = CriteriaCompiler.Compile(parsed.Root!, typeof(Order), Fields);
        Assert.False(compiled.IsValid);
        return compiled.Diagnostics;
    }

    // ── Grammar + precedence ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Comparisons_and_boolean_composition()
    {
        Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("[Amount] > 20000"));
        Assert.Equal(new[] { "SO-1042", "SO-1044" }, Run("[Region] = 'East'"));
        Assert.Equal(new[] { "SO-1044" }, Run("[Region] = 'East' And [Amount] >= 20000"));
        Assert.Equal(new[] { "SO-1042", "SO-1044", "SO-1047" }, Run("[Region] = 'East' Or [Amount] = 27300"));
    }

    [Fact]
    public void And_binds_tighter_than_Or()
        // Or(A, And(B, C)) — NOT And(Or(A,B), C).
        => Assert.Equal(new[] { "SO-1042", "SO-1044", "SO-1046" },
                        Run("[Region] = 'East' Or [Region] = 'South' And [Amount] < 20000"));

    [Fact]
    public void Not_negates_the_whole_comparison()
        // Not [Region] = 'East' ≡ Not([Region] = 'East') — the criteria semantic. Null Region rows
        // pass (null <> 'East' under the null-ness rule: string compare treats null < any, ≠ 0).
        => Assert.Equal(new[] { "SO-1046", "SO-1047", "SO-1049" }, Run("Not [Region] = 'East'"));

    [Fact]
    public void Arithmetic_promotes_and_groups()
    {
        Assert.Equal(new[] { "SO-1044" }, Run("[Amount] * 2 > 60000"));
        Assert.Equal(new[] { "SO-1042" }, Run("[Amount] + 50 = 12500"));
        Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("([Amount] - 10000) / 2 > 8000"));
    }

    [Fact]
    public void In_Between_Like()
    {
        Assert.Equal(new[] { "SO-1042", "SO-1044", "SO-1046" }, Run("[Region] In ('East', 'South')"));
        Assert.Equal(new[] { "SO-1046", "SO-1049" }, Run("[Amount] Between 15000 And 20000"));
        // Like follows the COLUMN's string mode (§9.1 one-authority): Id binds CurrentCulture
        // (case-sensitive), so a lowercase pattern matches nothing…
        Assert.Equal(Array.Empty<string>(), Run("[Id] Like 'so-%'"));
        Assert.Equal(new[] { "SO-1044" }, Run("[Id] Like '%44'"));
        Assert.Equal(new[] { "SO-1042" }, Run("[Id] Like 'SO-104_' And [Amount] < 15000"));
    }

    [Fact]
    public void Like_follows_an_ignore_case_column_mode()
    {
        // …while an IgnoreCase-mode column matches case-blind (the same pattern, different column
        // semantics — the engine's Condition lane and this compiled lane agree by construction).
        CriteriaCompiler.FieldBinding[] fields =
            [Bind("Id", o => o.Id, mode: StringComparison.OrdinalIgnoreCase)];
        var parsed = CriteriaParser.Parse("[Id] Like 'so-%'");
        var compiled = CriteriaCompiler.Compile(parsed.Root!, typeof(Order), fields);
        Assert.True(compiled.IsValid, string.Join("; ", compiled.Diagnostics));
        var predicate = (Func<Order, bool>)compiled.Predicate!.Compile();
        Assert.Equal(5, Rows.Count(predicate));
    }

    [Fact]
    public void Dates_and_enums_compare_via_literals()
    {
        Assert.Equal(new[] { "SO-1047", "SO-1049" }, Run("[Date] > #2026-05-09#"));
        Assert.Equal(new[] { "SO-1042", "SO-1046" }, Run("[Status] = 'Shipped'"));
    }

    [Fact]
    public void Null_semantics()
    {
        Assert.Equal(new[] { "SO-1047" }, Run("[Margin] = null"));
        Assert.Equal(new[] { "SO-1049" }, Run("[Region] = null"));
        Assert.Equal(new[] { "SO-1042", "SO-1044", "SO-1046", "SO-1047" }, Run("[Region] <> null"));
        // Null-first relational (the ENGINE semantics — §9.1 panel-unification: both lowering
        // paths agree): a null Margin sorts before every value, so it satisfies '< 0.35'.
        Assert.Equal(new[] { "SO-1046", "SO-1047", "SO-1049" }, Run("[Margin] < 0.35"));
    }

    [Fact]
    public void Functions()
    {
        Assert.Equal(new[] { "SO-1044" }, Run("Contains([Id], '44')"));
        Assert.Equal(new[] { "SO-1042", "SO-1044" }, Run("StartsWith(Upper([Region]), 'EA')"));
        Assert.Equal(new[] { "SO-1049" }, Run("IsNullOrEmpty([Region])"));
        Assert.Equal(new[] { "SO-1047" }, Run("IsNull([Margin], 0.99) > 0.5"));
        Assert.Equal(new[] { "SO-1042", "SO-1044", "SO-1046", "SO-1047", "SO-1049" }, Run("Len([Id]) = 7"));
    }

    [Fact]
    public void Field_binds_by_display_alias_too()
        => Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("[Order Total] > 20000"));

    // ── Diagnostics (the validation strip) ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_errors_carry_positions()
    {
        var result = CriteriaParser.Parse("[Amount] > (5 + 3");
        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("Unbalanced parenthesis", diagnostic.Message);
        Assert.Equal(18, diagnostic.Column); // 1-based, at end-of-input

        Assert.False(CriteriaParser.Parse("[Amount] >").IsValid);
        Assert.False(CriteriaParser.Parse("'unterminated").IsValid);
        Assert.False(CriteriaParser.Parse("[NoBracket").IsValid);
        Assert.False(CriteriaParser.Parse("[A] = 1 extra").IsValid);
    }

    [Fact]
    public void Compile_errors_name_the_problem()
    {
        Assert.Contains(FailCompile("[Nope] = 1"), d => d.Message.Contains("Unknown field"));
        Assert.Contains(FailCompile("[Amount] = 'text'"), d => d.Message.Contains("Cannot compare"));
        Assert.Contains(FailCompile("[Amount] < null"), d => d.Message.Contains("Relational"));
        Assert.Contains(FailCompile("Frobnicate([Id])"), d => d.Message.Contains("Unknown function"));
        Assert.Contains(FailCompile("[Amount] + 1"), d => d.Message.Contains("not boolean"));
        Assert.Contains(FailCompile("Not [Amount]"), d => d.Message.Contains("boolean operand"));
        Assert.Contains(FailCompile("Iif([Amount] > 1)"), d => d.Message.Contains("Iif takes"));
    }

    // ── Iif ternary + Min/Max + numeric builtins + the custom function registry ──────────────────

    [Fact]
    public void Iif_ternary_evaluates_the_branch()
    {
        // Amount > 20000 ⇒ 31900 (SO-1044), 27300 (SO-1047).
        Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("Iif([Amount] > 20000, 1, 0) = 1"));
    }

    [Fact]
    public void Min_and_max_fold_numeric_arguments()
    {
        // Min(Amount, 15000) == 15000 ⇔ Amount >= 15000 (all but SO-1042's 12450).
        Assert.Equal(new[] { "SO-1044", "SO-1046", "SO-1047", "SO-1049" }, Run("Min([Amount], 15000) = 15000"));
        // Max(Amount, 25000) == Amount ⇔ Amount >= 25000 ⇒ 31900, 27300.
        Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("Max([Amount], 25000) = [Amount]"));
    }

    [Fact]
    public void Numeric_builtins_from_the_registry()
    {
        // Sqrt(31900)=178.6, Sqrt(27300)=165.2 exceed 150; the rest don't.
        Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("Sqrt([Amount]) > 150"));
        // 31900 mod 10000 = 1900.
        Assert.Equal(new[] { "SO-1044" }, Run("Mod([Amount], 10000) = 1900"));
    }

    [Fact]
    public void A_custom_registered_function_compiles_and_runs()
    {
        // Register a bespoke function that emits its own expression tree (double the value).
        CriteriaFunctions.Register("Twice", args => Expression.Multiply(args[0], Expression.Constant(2m)), minArgs: 1, maxArgs: 1);
        try
        {
            // Twice(Amount) > 40000 ⇔ Amount > 20000 ⇒ SO-1044, SO-1047.
            Assert.Equal(new[] { "SO-1044", "SO-1047" }, Run("Twice([Amount]) > 40000"));
            Assert.Contains("Twice", CriteriaFunctions.Names, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CriteriaFunctions.Unregister("Twice");
        }
        // Once unregistered it's an unknown function again.
        Assert.Contains(FailCompile("Twice([Amount]) > 1"), d => d.Message.Contains("Unknown function"));
    }

    // ── The Filter-Builder bridge ────────────────────────────────────────────────────────────────

    private static readonly CriteriaExpression.Field[] BridgeFields =
        Fields.Select(f => new CriteriaExpression.Field(f.Name, f.DisplayName, f.Selector, (object)f.Name)).ToArray();

    [Fact]
    public void Structural_subset_lowers_to_real_tree_nodes()
    {
        var result = CriteriaExpression.ToFilterNode(
            "[Region] In ('East', 'West') And [Amount] >= 10000 And Not [Status] = 'Cancelled'",
            typeof(Order), BridgeFields);
        Assert.True(result.IsValid);

        var root = Assert.IsType<FilterGroupNode>(result.Filter);
        Assert.Equal(FilterGroupOperator.And, root.Operator);
        Assert.Contains(root.Children, c => c is FilterValueSetNode);
        Assert.Contains(root.Children, c => c is FilterNotNode);
    }

    [Fact]
    public void Non_structural_shapes_fall_back_to_a_compiled_predicate()
    {
        var result = CriteriaExpression.ToFilterNode(
            "Contains(Upper([Id]), '104') Or [Amount] * 2 > 60000", typeof(Order), BridgeFields);
        Assert.True(result.IsValid);
        Assert.IsType<FilterPredicateNode>(result.Filter);
    }

    [Fact]
    public void Flipped_literal_comparisons_normalize()
    {
        var result = CriteriaExpression.ToFilterNode("20000 < [Amount]", typeof(Order), BridgeFields);
        var condition = Assert.IsType<FilterConditionNode>(result.Filter);
        Assert.Equal(FilterOperator.GreaterThan, condition.Operator); // 20000 < A ⇒ A > 20000
        Assert.Equal(20000m, condition.Value);
    }

    [Fact]
    public void Text_round_trip_through_the_bridge()
    {
        const string text = "[Region] = 'East' And ([Amount] > 20000 Or [Amount] Between 1 And 5)";
        var first = CriteriaExpression.ToFilterNode(text, typeof(Order), BridgeFields);
        Assert.True(first.IsValid);

        string rendered = CriteriaExpression.ToText(first.Filter!, key => (string)key);
        var second = CriteriaExpression.ToFilterNode(rendered, typeof(Order), BridgeFields);
        Assert.True(second.IsValid);

        // Semantic equivalence: both compile against the engine and select the same rows.
        string[] Select(FilterNode node)
        {
            var columns = Fields.Select(f => ShapingCodegen.CreateColumn<Order>(f.Name, f.Selector)).ToList();
            foreach (var column in columns)
            {
                column.EnsureCapacity(Rows.Length);
                for (int i = 0; i < Rows.Length; i++)
                    column.ExtractKeyUntyped(Rows[i], i);
            }
            var predicate = ShapingFilter.Compile(node,
                key => columns.FirstOrDefault(c => Equals(c.Identity, key)),
                (Func<int, Order>)(slot => Rows[slot]));
            return Enumerable.Range(0, Rows.Length).Where(predicate).Select(i => Rows[i].Id).ToArray();
        }

        Assert.Equal(Select(first.Filter!), Select(second.Filter!));
    }

    [Fact]
    public void Escaped_quotes_round_trip()
    {
        Assert.Equal(Array.Empty<string>(), Run("[Region] = 'O''Brien'"));
        var parsed = CriteriaParser.Parse("[Region] = 'O''Brien'");
        var literal = Assert.IsType<CriteriaLiteralNode>(Assert.IsType<CriteriaBinaryNode>(parsed.Root).Right);
        Assert.Equal("O'Brien", literal.Value);
    }

    // ── Authoring names: the token a UI should INSERT (the inverse of the resolver) ───────────────

    private static CriteriaExpression.Field Fld(string name, string? display)
        => new(name, display, (Expression<Func<Order, string>>)(o => o.Id), name);

    [Fact] // a user edits in the vocabulary the grid SHOWS, so an unambiguous header alias is what gets authored
    public void Authoring_name_prefers_the_display_alias()
    {
        var amount = BridgeFields.Single(f => f.Name == "Amount");
        Assert.Equal("Order Total", CriteriaExpression.GetAuthoringName(amount, BridgeFields));
    }

    [Fact] // no alias (or an alias that only differs by case) ⇒ the canonical name
    public void Authoring_name_falls_back_when_there_is_no_alias()
    {
        var region = BridgeFields.Single(f => f.Name == "Region");
        Assert.Equal("Region", CriteriaExpression.GetAuthoringName(region, BridgeFields));

        CriteriaExpression.Field[] same = [Fld("Region", "region")];
        Assert.Equal("Region", CriteriaExpression.GetAuthoringName(same[0], same));
    }

    [Fact] // THE DANGEROUS CASE: an alias that is another column's CANONICAL name would bind the WRONG column,
           // because the resolver's exact-name pass runs first. Authoring the canonical name is the only safe move.
    public void Authoring_name_falls_back_when_the_alias_shadows_another_columns_canonical_name()
    {
        CriteriaExpression.Field[] fields = [Fld("Amount", "Total"), Fld("Total", null)];

        Assert.Equal("Amount", CriteriaExpression.GetAuthoringName(fields[0], fields));
        Assert.Equal("Total", CriteriaExpression.GetAuthoringName(fields[1], fields));
    }

    [Fact] // two columns sharing a header are ambiguous to the resolver (a diagnostic, not a guess), so neither
           // may be authored by alias — otherwise the editor hands the user an expression that cannot compile
    public void Authoring_name_falls_back_when_the_alias_is_ambiguous()
    {
        CriteriaExpression.Field[] fields = [Fld("NetAmount", "Total"), Fld("GrossAmount", "Total")];

        Assert.Equal("NetAmount", CriteriaExpression.GetAuthoringName(fields[0], fields));
        Assert.Equal("GrossAmount", CriteriaExpression.GetAuthoringName(fields[1], fields));
    }

    [Fact] // the whole point: the authored token compiles, and binds the column the user picked
    public void Authored_alias_compiles_and_binds_the_same_column()
    {
        var amount = BridgeFields.Single(f => f.Name == "Amount");
        var token = CriteriaExpression.GetAuthoringName(amount, BridgeFields);

        Assert.Equal("Order Total", token);          // authored in the user's vocabulary…
        var byAlias = Run($"[{token}] >= 27000");

        Assert.Equal(Run("[Amount] >= 27000"), byAlias);  // …and identical to the canonical spelling
        Assert.Equal(["SO-1044", "SO-1047"], byAlias);
    }
}
