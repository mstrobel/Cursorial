using System.Diagnostics.CodeAnalysis;

using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.DataViews.Shaping.Expressions;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>How the Filter Builder dialog closed (the grid's entry point dispatches on it).</summary>
internal enum FilterBuilderOutcome
{
    Cancelled,
    Applied,
    /// <summary>The "ƒ Edit as Text" hop: reopen as the expression editor seeded with the tree's text.</summary>
    EditAsText,
}

/// <summary>
/// The visual Filter Builder (design doc §9.1; the mockup's "Filter Builder — visual condition
/// tree"): a modal window rendering an editable condition tree over a MUTABLE mirror of
/// <see cref="FilterNode"/> — group rows ("And ▾"/"Or ▾" toggles + ＋ condition / ＋ Group / ✕),
/// condition rows as [field ▾][operator ▾][value] cells joined by ├─ └─ │ box-drawing prefixes.
/// Seeding: the grid's current structural <see cref="DataGrid.Filter"/> maps node-for-node
/// (conditions editable; an InSet keeps its membership as a read-only "is any of" row); a
/// <see cref="FilterPredicateNode"/> seeds a read-only "expression" row labeled from
/// <see cref="DataGrid.FilterExpressionText"/> (§9.1 — the compiled fallback doesn't text-round-
/// trip, so the builder shows it opaque and preserves the node verbatim). OK rebuilds the
/// <see cref="FilterNode"/> tree and writes <c>grid.Filter</c>; "ƒ Edit as Text" hops to the
/// expression editor carrying <see cref="CriteriaExpression.ToText"/> of the CURRENT model.
/// </summary>
[RequiresDynamicCode("The lowered filter compiles against the grid's row type.")]
internal sealed class DataGridFilterBuilder
{
    // ── The mutable model (a builder-editable mirror of the immutable FilterNode tree) ────────────

    internal abstract class Node;

    internal sealed class Group : Node
    {
        public FilterGroupOperator Operator;
        public readonly List<Node> Children = [];
    }

    internal sealed class Condition : Node
    {
        public DataGridColumn? Column;
        public FilterOperator Operator = FilterOperator.Equals;
        public string ValueText = string.Empty;
        public string SecondValueText = string.Empty;
        /// <summary>
        /// True while the row is a machine-seeded starter the user never touched (any field/
        /// operator/value edit through the rendered cells clears it). A pristine row never lowers —
        /// an untouched builder's OK must apply NO filter, not "[FirstColumn] = (Blanks)".
        /// </summary>
        public bool Pristine;
    }

    /// <summary>A non-editable leaf carried through verbatim (InSet / Not / compiled predicate).</summary>
    internal sealed class Opaque(FilterNode node, string label) : Node
    {
        public FilterNode FilterNode { get; } = node;
        public string Label { get; } = label;
    }

    /// <summary>One rendered condition row's live controls (tests drive the real cells).</summary>
    internal sealed record ConditionRow(Condition Model, ComboBox Field, ComboBox Operator,
                                        TextBox Value, TextBox SecondValue);

    /// <summary>One rendered group row's live controls.</summary>
    internal sealed record GroupRow(Group Model, Button OperatorToggle);

    private static readonly (FilterOperator Op, string Label)[] Operators =
    [
        (FilterOperator.Equals, "="),
        (FilterOperator.NotEquals, "<>"),
        (FilterOperator.LessThan, "<"),
        (FilterOperator.LessThanOrEqual, "<="),
        (FilterOperator.GreaterThan, ">"),
        (FilterOperator.GreaterThanOrEqual, ">="),
        (FilterOperator.Contains, "contains"),
        (FilterOperator.StartsWith, "starts with"),
        (FilterOperator.EndsWith, "ends with"),
        (FilterOperator.Between, "between"),
    ];

    private readonly DataGrid _grid;
    private readonly Window _window;
    private readonly StackPanel _treeHost = new();
    private readonly TextBlock _strip = new();
    private readonly List<DataGridColumn> _fieldColumns;
    private readonly List<ConditionRow> _conditionRows = [];
    private readonly List<GroupRow> _groupRows = [];
    private readonly TaskCompletionSource<FilterBuilderOutcome> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Non-null when this instance is a duplicate-open rider adopting the LIVE dialog.</summary>
    private readonly DataGridFilterBuilder? _live;
    private Group _root;
    private string _editAsTextSeed = string.Empty;
    private bool _rebuilding;

    public DataGridFilterBuilder(DataGrid grid)
    {
        _grid = grid;

        if (grid.ActiveFilterBuilder is { } open)
        {
            // Already-open guard (§9.1 dialogs): the fire-and-forget entry point can run twice in
            // one input batch (a double-click posts two opens before the first modal blocks its
            // trigger). The duplicate never builds a second window — it ADOPTS the live dialog
            // (same window, same model, same rendered rows), so the grid's tracking handle (which
            // the entry point re-stamps onto the duplicate) still reaches the ONE real dialog,
            // and ShowAsync rides its outcome instead of stacking a second modal.
            var live = open._live ?? open;
            _live = live;
            _window = live._window;
            _treeHost = live._treeHost;
            _strip = live._strip;
            _fieldColumns = live._fieldColumns;
            _conditionRows = live._conditionRows;
            _groupRows = live._groupRows;
            _root = live._root;
            return;
        }

        _fieldColumns = grid.Columns.Where(c => c.FieldName is not null || c.KeySelector is not null).ToList();
        _root = Seed(grid.Filter);

        var content = new StackPanel(); // vertical
        content.Children.Add(new ScrollViewer
        {
            Content = _treeHost,
            MaxHeight = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // The structural toolbar (the mockup's footer band): root-level add/clear + the text hop.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        var addCondition = new Button { Content = "＋ Condition" };
        addCondition.Click += (_, _) => AddCondition(Root);
        var addGroup = new Button { Content = "＋ Group" };
        addGroup.Click += (_, _) => AddGroup(Root);
        var clear = new Button { Content = "⌫ Clear" };
        clear.Click += (_, _) => Clear();
        var editAsText = new Button { Content = "ƒ Edit as Text" };
        editAsText.Click += (_, _) => RequestEditAsText();
        tools.Children.Add(addCondition);
        tools.Children.Add(addGroup);
        tools.Children.Add(clear);
        tools.Children.Add(editAsText);
        content.Children.Add(tools);

        content.Children.Add(_strip);

        _window = DataGridDialogHelpers.CreateDialogWindow("Filter Builder", content,
            ("OK", Ok), ("Cancel", Cancel));

        Rebuild();
    }

    /// <summary>The mutable root group (tests inspect and mutate the model directly).</summary>
    internal Group Root => (_live ?? this)._root;

    /// <summary>Whether the dialog is currently shown (the grid's test hook gates on it).</summary>
    internal bool IsOpen => _window.IsShown;

    /// <summary>The rendered condition rows, tree order (rebuilt on every structural change).</summary>
    internal IReadOnlyList<ConditionRow> ConditionRows => _conditionRows;

    /// <summary>The rendered group rows, tree order (index 0 is the root).</summary>
    internal IReadOnlyList<GroupRow> GroupRows => _groupRows;

    /// <summary>The validation strip (value-parse failures surface here on OK).</summary>
    internal TextBlock ValidationStrip => _strip;

    /// <summary>The criteria text the "Edit as Text" hop carries (set when the outcome is <see cref="FilterBuilderOutcome.EditAsText"/>).</summary>
    internal string EditAsTextSeed => (_live ?? this)._editAsTextSeed;

    internal async Task<FilterBuilderOutcome> ShowAsync()
    {
        if (_live is { } live)
        {
            // A duplicate open rides the live dialog and completes with ITS outcome. EditAsText
            // maps to Cancelled for the rider: the live call's owner chains the expression-editor
            // hop — chaining it from both entry tasks would stack two editors.
            var ridden = await live._outcome.Task;
            return ridden == FilterBuilderOutcome.EditAsText ? FilterBuilderOutcome.Cancelled : ridden;
        }

        try
        {
            var result = await _window.ShowDialogAsync();
            var outcome = result is FilterBuilderOutcome closed ? closed : FilterBuilderOutcome.Cancelled;
            _outcome.TrySetResult(outcome);
            return outcome;
        }
        finally
        {
            _outcome.TrySetResult(FilterBuilderOutcome.Cancelled); // the throw path (dialog cancellation)
        }
    }

    // ── Seeding (FilterNode → the mutable mirror) ─────────────────────────────────────────────────

    private Group Seed(FilterNode? filter)
    {
        if (filter is null)
        {
            // An empty builder starts with one blank condition row (the mockup's affordance —
            // the user picks a field rather than hunting for an add button first). Pristine:
            // the machine-seeded starter never lowers until the user touches it.
            var empty = new Group { Operator = FilterGroupOperator.And };
            empty.Children.Add(new Condition { Column = _fieldColumns.FirstOrDefault(), Pristine = true });
            return empty;
        }

        var seeded = SeedNode(filter);
        if (seeded is Group group)
            return group;

        // A non-group root (single condition / opaque leaf) wraps in an And group so the tree
        // always has a group row to hang the add buttons on.
        var wrapper = new Group { Operator = FilterGroupOperator.And };
        wrapper.Children.Add(seeded);
        return wrapper;
    }

    private Node SeedNode(FilterNode node)
    {
        switch (node)
        {
            case FilterGroupNode group:
            {
                var mirror = new Group { Operator = group.Operator };
                foreach (var child in group.Children)
                    mirror.Children.Add(SeedNode(child));
                return mirror;
            }

            case FilterConditionNode condition when condition.ColumnKey is DataGridColumn column &&
                                                    _fieldColumns.Contains(column):
                return new Condition
                {
                    Column = column,
                    Operator = condition.Operator,
                    ValueText = DataGridDialogHelpers.FormatLiteral(condition.Value),
                    SecondValueText = DataGridDialogHelpers.FormatLiteral(condition.SecondValue),
                };

            case FilterValueSetNode set:
                return new Opaque(set, $"[{ColumnCaption(set.ColumnKey)}] is any of {SetDigest(set)}");

            case FilterNotNode not:
                return new Opaque(not, $"Not ({DescribeOpaque(not.Child)})");

            case FilterPredicateNode predicate:
                // §9.1: the compiled fallback keeps its ORIGINAL SOURCE TEXT grid-side — the
                // builder shows it as a read-only expression row rather than pretending to edit it.
                return new Opaque(predicate,
                    _grid.FilterExpressionText is { Length: > 0 } text
                        ? $"ƒ {text}"
                        : "ƒ (custom predicate)");

            default:
                return new Opaque(node, DescribeOpaque(node));
        }
    }

    private string DescribeOpaque(FilterNode node) => node switch
    {
        FilterConditionNode c => $"[{ColumnCaption(c.ColumnKey)}] … {DataGridDialogHelpers.FormatLiteral(c.Value)}",
        FilterValueSetNode s => $"[{ColumnCaption(s.ColumnKey)}] is any of {SetDigest(s)}",
        FilterPredicateNode => "custom predicate",
        FilterNotNode n => $"Not ({DescribeOpaque(n.Child)})",
        FilterGroupNode g => $"({g.Children.Count} conditions)",
        _ => node.GetType().Name,
    };

    private string ColumnCaption(object columnKey)
        => columnKey is DataGridColumn column ? column.EffectiveHeader : columnKey.ToString() ?? "?";

    private static string SetDigest(FilterValueSetNode set)
    {
        var parts = set.Values.Take(4)
            .Select(v => v is null ? "(Blanks)" : v is string s ? $"'{s}'" : DataGridDialogHelpers.FormatLiteral(v));
        string digest = string.Join(", ", parts);
        return set.Values.Count > 4 ? $"{digest}, …" : digest;
    }

    // ── Rendering (model → rows; full rebuild per structural change, the chooser's idiom) ─────────

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            _conditionRows.Clear();
            _groupRows.Clear();
            _treeHost.Children.Clear();
            RenderGroup(Root, headerIndent: string.Empty, childBase: string.Empty, isRoot: true);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// Renders a group: its header row wears <paramref name="headerIndent"/> (the ├─/└─ connector
    /// at ITS position); its children derive from <paramref name="childBase"/> (the │-continuation
    /// column — so grandchildren line up under the group, the mockup's nested-Or indent).
    /// </summary>
    private void RenderGroup(Group group, string headerIndent, string childBase, bool isRoot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        if (headerIndent.Length > 0)
            row.Children.Add(Prefix(headerIndent));

        var toggle = new Button { Content = group.Operator == FilterGroupOperator.And ? "And ▾" : "Or ▾" };
        toggle.Click += (_, _) =>
        {
            group.Operator = group.Operator == FilterGroupOperator.And
                ? FilterGroupOperator.Or
                : FilterGroupOperator.And;
            Rebuild();
        };
        row.Children.Add(toggle);

        var add = new Button { Content = "＋" };
        add.Click += (_, _) => AddCondition(group);
        row.Children.Add(add);

        var addGroup = new Button { Content = "＋ Group" };
        addGroup.Click += (_, _) => AddGroup(group);
        row.Children.Add(addGroup);

        if (!isRoot)
        {
            var remove = new Button { Content = "✕" };
            remove.Click += (_, _) => Remove(group);
            row.Children.Add(remove);
        }

        row.Children.Add(DataGridDialogHelpers.Caption(group.Operator == FilterGroupOperator.And
            ? (isRoot ? "— all must match" : "— all match")
            : (isRoot ? "— any may match" : "— any matches")));

        _groupRows.Add(new GroupRow(group, toggle));
        _treeHost.Children.Add(row);

        for (int i = 0; i < group.Children.Count; i++)
        {
            bool last = i == group.Children.Count - 1;
            string childIndent = childBase + (last ? "└─ " : "├─ ");
            string continuation = childBase + (last ? "   " : "│  ");
            switch (group.Children[i])
            {
                case Group nested:
                    RenderGroup(nested, childIndent, continuation, isRoot: false);
                    break;
                case Condition condition:
                    RenderCondition(condition, childIndent);
                    break;
                case Opaque opaque:
                    RenderOpaque(opaque, childIndent);
                    break;
            }
        }
    }

    private void RenderCondition(Condition condition, string indent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(Prefix(indent));

        var field = new ComboBox
        {
            ItemsSource = _fieldColumns.Select(c => c.EffectiveHeader).ToList(),
            MinWidth = 8,
            SelectedIndex = condition.Column is null ? -1 : _fieldColumns.IndexOf(condition.Column),
        };
        field.SelectionChanged += (_, _) =>
        {
            if (!_rebuilding && field.SelectedIndex >= 0)
            {
                condition.Column = _fieldColumns[field.SelectedIndex];
                condition.Pristine = false;
            }
        };
        row.Children.Add(field);

        var op = new ComboBox
        {
            ItemsSource = Operators.Select(o => o.Label).ToList(),
            MinWidth = 4,
            SelectedIndex = Array.FindIndex(Operators, o => o.Op == condition.Operator),
        };
        row.Children.Add(op);

        var value = new TextBox { Text = condition.ValueText, MinWidth = 10 };
        value.TextChanged += (_, _) =>
        {
            if (!_rebuilding)
            {
                condition.ValueText = value.Text;
                condition.Pristine = false;
            }
        };
        row.Children.Add(value);

        var ellipsis = DataGridDialogHelpers.Caption("…");
        var second = new TextBox { Text = condition.SecondValueText, MinWidth = 10 };
        second.TextChanged += (_, _) =>
        {
            if (!_rebuilding)
            {
                condition.SecondValueText = second.Text;
                condition.Pristine = false;
            }
        };
        bool between = condition.Operator == FilterOperator.Between;
        ellipsis.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        second.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(ellipsis);
        row.Children.Add(second);

        op.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || op.SelectedIndex < 0)
                return;
            condition.Operator = Operators[op.SelectedIndex].Op;
            condition.Pristine = false;
            bool showSecond = condition.Operator == FilterOperator.Between;
            ellipsis.Visibility = showSecond ? Visibility.Visible : Visibility.Collapsed;
            second.Visibility = showSecond ? Visibility.Visible : Visibility.Collapsed;
        };

        var add = new Button { Content = "＋" };
        add.Click += (_, _) => AddConditionAfter(condition);
        row.Children.Add(add);
        var remove = new Button { Content = "✕" };
        remove.Click += (_, _) => Remove(condition);
        row.Children.Add(remove);

        _conditionRows.Add(new ConditionRow(condition, field, op, value, second));
        _treeHost.Children.Add(row);
    }

    private void RenderOpaque(Opaque opaque, string indent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(Prefix(indent));
        var label = new TextBlock { Text = opaque.Label };
        label.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
        row.Children.Add(label);
        var remove = new Button { Content = "✕" };
        remove.Click += (_, _) => Remove(opaque);
        row.Children.Add(remove);
        _treeHost.Children.Add(row);
    }

    private static TextBlock Prefix(string indent)
    {
        var block = new TextBlock { Text = indent };
        block.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        return block;
    }

    // ── Structural edits (buttons + test entry points) ────────────────────────────────────────────

    internal void AddCondition(Group group)
    {
        if (_live is { } live)
        {
            live.AddCondition(group);
            return;
        }
        group.Children.Add(new Condition { Column = _fieldColumns.FirstOrDefault(), Pristine = true });
        Rebuild();
    }

    /// <summary>The row-level ＋: insert a sibling immediately after <paramref name="anchor"/>.</summary>
    internal void AddConditionAfter(Condition anchor)
    {
        if (_live is { } live)
        {
            live.AddConditionAfter(anchor);
            return;
        }
        var (parent, index) = Locate(anchor);
        if (parent is null)
            return;
        parent.Children.Insert(index + 1, new Condition { Column = anchor.Column, Pristine = true });
        Rebuild();
    }

    internal void AddGroup(Group parent)
    {
        if (_live is { } live)
        {
            live.AddGroup(parent);
            return;
        }
        // A fresh nested group starts on the OPPOSITE operator (the mockup's Or-inside-And) with
        // one blank condition so it renders as an editable row, not a bare header.
        var nested = new Group
        {
            Operator = parent.Operator == FilterGroupOperator.And ? FilterGroupOperator.Or : FilterGroupOperator.And,
        };
        nested.Children.Add(new Condition { Column = _fieldColumns.FirstOrDefault(), Pristine = true });
        parent.Children.Add(nested);
        Rebuild();
    }

    internal void Remove(Node node)
    {
        if (_live is { } live)
        {
            live.Remove(node);
            return;
        }
        var (parent, index) = Locate(node);
        if (parent is null)
            return;
        parent.Children.RemoveAt(index);
        Rebuild();
    }

    internal void Clear()
    {
        if (_live is { } live)
        {
            live.Clear();
            return;
        }
        _root = new Group { Operator = FilterGroupOperator.And };
        _root.Children.Add(new Condition { Column = _fieldColumns.FirstOrDefault(), Pristine = true });
        _strip.Text = string.Empty;
        Rebuild();
    }

    private (Group? Parent, int Index) Locate(Node node) => Locate(Root, node);

    private static (Group? Parent, int Index) Locate(Group group, Node node)
    {
        for (int i = 0; i < group.Children.Count; i++)
        {
            if (ReferenceEquals(group.Children[i], node))
                return (group, i);
            if (group.Children[i] is Group nested && Locate(nested, node) is { Parent: not null } found)
                return found;
        }
        return (null, -1);
    }

    // ── Lowering (the mutable mirror → FilterNode) ────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="FilterNode"/> tree from the model. Blank condition rows (no column),
    /// untouched machine-seeded starter rows (<see cref="Condition.Pristine"/>), and empty groups
    /// are skipped — a starter row must not block OK NOR lower to an unintended
    /// "[FirstColumn] = (Blanks)"; a condition whose value doesn't parse against its column's key
    /// type is an error (never a silently-wrong literal — the §9.1 exactness discipline). Null with
    /// no error = "no filter" (clears).
    /// </summary>
    internal FilterNode? BuildResult(out string? error)
    {
        error = null;
        return BuildGroup(Root, ref error);
    }

    private FilterNode? BuildGroup(Group group, ref string? error)
    {
        var children = new List<FilterNode>();
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case Group nested:
                {
                    var built = BuildGroup(nested, ref error);
                    if (error is not null)
                        return null;
                    if (built is not null)
                        children.Add(built);
                    break;
                }
                case Condition condition:
                {
                    if (condition.Column is null || condition.Pristine)
                        break; // a blank/untouched starter row never lowers
                    var built = BuildCondition(condition, ref error);
                    if (error is not null)
                        return null;
                    if (built is not null)
                        children.Add(built);
                    break;
                }
                case Opaque opaque:
                    children.Add(opaque.FilterNode);
                    break;
            }
        }

        if (children.Count == 0)
            return null;
        if (children.Count == 1)
            return children[0];
        return group.Operator == FilterGroupOperator.And
            ? FilterNode.And(children.ToArray())
            : FilterNode.Or(children.ToArray());
    }

    private FilterNode? BuildCondition(Condition condition, ref string? error)
    {
        var column = condition.Column!;
        var keyType = _grid.Controller?.GetColumnKeyType(column) ?? column.KeySelector?.ReturnType;
        string header = column.EffectiveHeader;

        if (condition.Operator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith)
        {
            // Text operators carry the raw text (the auto-filter grammar's shape); they only make
            // sense on string keys — the engine's condition builder has no text lane for numbers.
            var underlying = keyType is null ? typeof(string) : Nullable.GetUnderlyingType(keyType) ?? keyType;
            if (underlying != typeof(string))
            {
                error = $"'{Operators.First(o => o.Op == condition.Operator).Label}' needs a text column — {header}";
                return null;
            }
            if (condition.ValueText.Length == 0)
                return null; // an empty contains matches everything — treat as a blank row
            return FilterNode.Condition(column, condition.Operator, condition.ValueText);
        }

        if (!DataGridDialogHelpers.TryParseLiteral(keyType, condition.ValueText, out var value))
        {
            error = $"'{condition.ValueText}' is not a valid {FriendlyType(keyType)} — {header}";
            return null;
        }

        if (condition.Operator == FilterOperator.Between)
        {
            if (!DataGridDialogHelpers.TryParseLiteral(keyType, condition.SecondValueText, out var second))
            {
                error = $"'{condition.SecondValueText}' is not a valid {FriendlyType(keyType)} — {header}";
                return null;
            }
            if (value is null || second is null)
            {
                error = $"Between needs both bounds — {header}";
                return null;
            }
            return FilterNode.Condition(column, FilterOperator.Between, value, second);
        }

        if (value is null)
        {
            if (condition.Operator is not (FilterOperator.Equals or FilterOperator.NotEquals))
            {
                error = $"'{Operators.First(o => o.Op == condition.Operator).Label}' needs a value — {header}";
                return null;
            }

            // (Blanks) =/<> is meaningful only where the key can HOLD null; a null literal against
            // a non-nullable value key would throw in the engine's literal conversion inside the
            // POSTED shape push — after the dialog already closed reporting success. Veto on the
            // strip instead (§9.1: never a silently-wrong or crashing literal).
            bool nullableKey = keyType is null || !keyType.IsValueType ||
                               Nullable.GetUnderlyingType(keyType) is not null;
            if (!nullableKey)
            {
                error = $"{header} needs a value — a {FriendlyType(keyType)} column has no (Blanks)";
                return null;
            }
        }

        return FilterNode.Condition(column, condition.Operator, value);
    }

    private static string FriendlyType(Type? keyType)
    {
        if (keyType is null)
            return "value";
        var underlying = Nullable.GetUnderlyingType(keyType) ?? keyType;
        return underlying == typeof(string) ? "text" : underlying.Name;
    }

    // ── Commit / hop ──────────────────────────────────────────────────────────────────────────────

    /// <summary>OK: lower the model and write <c>grid.Filter</c> (parse failures veto on the strip).</summary>
    internal void Ok()
    {
        var built = BuildResult(out string? error);
        if (error is not null)
        {
            SetError(error);
            return;
        }

        // Belt-and-braces (the auto-filter row's pre-write probe): the REAL compile runs inside a
        // posted shape push, where an uncompilable tree would be an unhandled dispatcher exception
        // AFTER the dialog closed reporting success. Any fragment the engine rejects — not just the
        // literal shapes BuildCondition anticipates — vetoes here with the dialog still open.
        if (built is not null && _grid.Controller is { } controller && !controller.CanCompileFilter(built))
        {
            SetError("the filter does not apply to the current columns");
            return;
        }

        // A zero-edit OK lowers back to the seeded tree verbatim (a single preserved opaque root ⇒
        // the SAME FilterNode reference). The §9.1-retained source text still describes it exactly —
        // writing through the Filter setter would clear FilterExpressionText for nothing.
        if (!ReferenceEquals(built, _grid.Filter))
        {
            // The public Filter setter deliberately: the tree is now BUILDER-authored, so any
            // stored expression text is stale — the editor re-derives via ToText on its next
            // open (§9.1).
            _grid.Filter = built;
        }
        _window.Close(FilterBuilderOutcome.Applied);
    }

    internal void Cancel() => _window.Close(FilterBuilderOutcome.Cancelled);

    /// <summary>
    /// "ƒ Edit as Text": lower the CURRENT model to criteria text and close with the hop outcome
    /// (the grid's entry point chains into the expression editor). A model that doesn't lower
    /// (value parse error) vetoes — the user fixes the row first.
    /// </summary>
    internal void RequestEditAsText()
    {
        if (_live is { } live)
        {
            // The hop must land its seed on the LIVE instance — its owning entry task is the one
            // that chains into the expression editor (the rider maps EditAsText to Cancelled).
            live.RequestEditAsText();
            return;
        }

        var built = BuildResult(out string? error);
        if (error is not null)
        {
            SetError(error);
            return;
        }

        _editAsTextSeed = built is null ? string.Empty : CriteriaExpression.ToText(built, _grid.CriteriaFieldName);
        _window.Close(FilterBuilderOutcome.EditAsText);
    }

    private void SetError(string message)
    {
        _strip.Text = $"✕ {message}";
        _strip.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.RedBrush);
    }

    /// <summary>The teardown funnel (the grid closes an open dialog when it tears down).</summary>
    internal void CloseWindow()
    {
        if (_window.IsShown)
            _window.Close(FilterBuilderOutcome.Cancelled);
    }
}
