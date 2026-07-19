// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One converted setter inside a <see cref="CompiledRule"/>: the target property plus either the
/// seal-converted constant or the valueless marker (an <see cref="UIProperty.UnsetValue"/> setter —
/// Fork A ledger A8: the entry contributes nothing while the rule is active).
/// </summary>
internal readonly struct CompiledSetter(UIProperty property, object? value, bool isUnset)
{
    /// <summary>The target property.</summary>
    internal UIProperty Property { get; } = property;

    /// <summary>The seal-converted constant (null only when legal for the property type, or when <see cref="IsUnset"/>).</summary>
    internal object? Value { get; } = value;

    /// <summary>Whether this is a valueless (A8) entry.</summary>
    internal bool IsUnset { get; } = isUnset;
}

/// <summary>
/// One flattened, seal-compiled rule of a <see cref="Style"/> (design doc §3.3): the composed
/// structural selector branch (nesting AND-composed through <see cref="Style.Children"/>; one rule
/// per selector-list member), the flattened converted setters (<c>BasedOn</c> chains appended
/// base-first, later wins per property), and the specificity components counted over the full
/// flattened branch (SD5). Scope-dependent fields — layer, scope depth, declaration order — join
/// when a scope arms the rule (<see cref="Styles.BuildScopeRules"/> packs the full
/// <see cref="StyleSortKey"/>).
/// </summary>
internal sealed class CompiledRule
{
    private static readonly AncestorStateCompound[] NoAncestorState = [];
    private static readonly string[] NoCustomPseudos = [];
    private static readonly DataCondition[] NoConditions = [];

    internal CompiledRule(
        Style ownerStyle, Style declaringStyle, int ruleIndex, SelectorBranch? branch, string selectorText,
        int names, int classLike, int types, CompiledSetter[] setters, DataCondition[]? whenConditions = null,
        StyleCapabilities requiresCapabilities = StyleCapabilities.None)
    {
        RequiresCapabilities = requiresCapabilities;
        OwnerStyle = ownerStyle;
        DeclaringStyle = declaringStyle;
        RuleIndex = ruleIndex;
        Branch = branch;
        SelectorText = selectorText;
        Names = names;
        ClassLike = classLike;
        Types = types;
        Setters = setters;
        WhenConditions = whenConditions ?? NoConditions;

        SubjectCustomPseudoClasses = NoCustomPseudos;
        AncestorStateCompounds = NoAncestorState;

        if (branch is null)
            return;

        // Precompute the activation requirements once per rule (shared across every element that
        // arms it): the subject compound's pseudo simples split into the InteractionState mask and
        // the interned custom names; non-subject compounds with pseudo simples become
        // ancestor-state descriptors the matcher binds to concrete ancestors at arm time.
        (SubjectStateBits, SubjectCustomPseudoClasses) = ExtractStateRequirements(branch.Subject);

        var combinators = branch.Combinators;
        foreach (var combinator in combinators)
        {
            if (combinator == SelectorCombinator.Template)
            {
                HasTemplateHop = true;
                break;
            }
        }

        List<AncestorStateCompound>? ancestorState = null;
        for (var i = 0; i < branch.Compounds.Length - 1; i++)
        {
            var (bits, customs) = ExtractStateRequirements(branch.Compounds[i]);
            if (bits != InteractionState.None || customs.Length > 0)
                (ancestorState ??= []).Add(new AncestorStateCompound(i, bits, customs));
        }

        if (ancestorState is not null)
            AncestorStateCompounds = [.. ancestorState];
    }

    private static (InteractionState Bits, string[] Customs) ExtractStateRequirements(SelectorCompound compound)
    {
        var bits = InteractionState.None;
        List<string>? customs = null;

        foreach (var simple in compound.Simples)
        {
            if (simple.Kind != SimpleSelectorKind.PseudoClass)
                continue;

            var name = string.Intern(":" + simple.Value);
            if (InteractionPseudoClasses.TryGetState(name, out var state))
                bits |= state;
            else
                (customs ??= []).Add(name);
        }

        return (bits, customs is null ? NoCustomPseudos : [.. customs]);
    }

    /// <summary>The top-level style whose <see cref="Style.Seal"/> produced this rule (diagnostics naming).</summary>
    internal Style OwnerStyle { get; }

    /// <summary>The flattened capability requirement (union across nesting/BasedOn — AND semantics); the
    /// match pass gates on it against the engine's effective set before structural evaluation.</summary>
    internal StyleCapabilities RequiresCapabilities { get; }

    /// <summary>The style whose setters/edge actions this rule carries (the nested child for composed rules; else the owner).</summary>
    internal Style DeclaringStyle { get; }

    /// <summary>The rule's index in the owner style's depth-first flattened rule list (seal-error naming + document order).</summary>
    internal int RuleIndex { get; }

    /// <summary>
    /// The flattened structural branch, or <see langword="null"/> for a selector-less rule (an
    /// explicit <c>UIElement.Style</c> arms it always-active; a keyed theme style resolves by key
    /// at P5 — selector-less rules never match through a scoped index).
    /// </summary>
    internal SelectorBranch? Branch { get; }

    /// <summary>The canonical flattened selector text (empty for selector-less rules; diagnostics render those as <c>(explicit)</c>).</summary>
    internal string SelectorText { get; }

    /// <summary>The <c>#name</c> specificity count over the flattened branch.</summary>
    internal int Names { get; }

    /// <summary>The class-like specificity count (classes + pseudo-classes; <c>When</c> conditions join at P4).</summary>
    internal int ClassLike { get; }

    /// <summary>The type specificity count (bare and <c>:is()</c> types alike).</summary>
    internal int Types { get; }

    /// <summary>
    /// The flattened, converted setters — shared by reference across the rules of one style
    /// (selector-list members share their setters; doc §3.1).
    /// </summary>
    internal CompiledSetter[] Setters { get; }

    /// <summary>
    /// The flattened <c>When</c> data-condition conjunction (own + <c>BasedOn</c> + the nesting
    /// parent's; design doc §3.1 / §3.3). Empty = unconditional. The styling engine arms one watch per
    /// condition when the rule structurally matches; all must hold for the rule to be active.
    /// </summary>
    internal DataCondition[] WhenConditions { get; }

    /// <summary>Whether the rule carries any <c>When</c> data conditions (the arm-time watch trigger).</summary>
    internal bool HasWhenConditions => WhenConditions.Length > 0;

    /// <summary>The subject compound's required <see cref="InteractionState"/> bits (the frame's interest mask input).</summary>
    internal InteractionState SubjectStateBits { get; }

    /// <summary>The subject compound's required custom pseudo-classes (interned, with leading colon).</summary>
    internal string[] SubjectCustomPseudoClasses { get; }

    /// <summary>Whether the branch contains a <c>/template/</c> combinator (the SD8 barrier exemption).</summary>
    internal bool HasTemplateHop { get; }

    /// <summary>The non-subject compounds carrying pseudo requirements (doc §3.3 ancestor-state rules; rare by design).</summary>
    internal AncestorStateCompound[] AncestorStateCompounds { get; }

    /// <summary>Whether the rule carries any pseudo-class requirements at all (subject or ancestor).</summary>
    internal bool HasStateRequirements
        => SubjectStateBits != InteractionState.None
           || SubjectCustomPseudoClasses.Length > 0
           || AncestorStateCompounds.Length > 0;

    /// <summary>
    /// Whether the rule is CONDITIONAL — it carries an activation condition of any kind: a
    /// pseudo-class or <c>.class</c> simple on any compound, or a <c>When</c> data-condition.
    /// Conditional rules arm at <see cref="BindingPriority.StyleTrigger"/> (above the Template lane);
    /// purely structural rules (types / <c>#name</c>s / combinators only) arm at
    /// <see cref="BindingPriority.Style"/> (below it) — the Avalonia activator split, completed
    /// 2026-07-12. <see cref="ClassLike"/> is exactly this predicate: <c>CountSpecificity</c> buckets
    /// every Class + PseudoClass simple across ALL compounds into classLike, and
    /// <c>Style.EmitRules</c> folds the <c>When</c> count in (SD5) — conditional-ness IS class-like
    /// specificity being non-zero. Classes count as conditions (not just pseudo-classes) because they
    /// toggle at runtime — the <c>.obscured</c> modal overlay and <c>.caps-*</c> capability rules
    /// depend on piercing template-authored values — matching Avalonia ("style class, pseudo class …
    /// are conditional; name and type selectors are not").
    /// </summary>
    internal bool IsConditional => ClassLike > 0;

    /// <inheritdoc/>
    public override string ToString() => $"rule {RuleIndex} \"{SelectorText}\" of {OwnerStyle}";
}

/// <summary>
/// One non-subject compound with pseudo requirements (<c>Pane:focus-within Widget</c> — the
/// ancestor-state shape, design doc §3.3): the compound's branch index plus the required interaction
/// bits and custom pseudo names. The matcher binds these to concrete ancestors at arm time
/// (<see cref="AncestorStateRequirement"/>).
/// </summary>
internal readonly struct AncestorStateCompound(int compoundIndex, InteractionState bits, string[] customPseudoClasses)
{
    /// <summary>The compound's index in the flattened branch (left-to-right).</summary>
    internal int CompoundIndex { get; } = compoundIndex;

    /// <summary>The required <see cref="InteractionState"/> bits on the matched ancestor.</summary>
    internal InteractionState Bits { get; } = bits;

    /// <summary>The required custom pseudo-classes on the matched ancestor (interned, with colon).</summary>
    internal string[] CustomPseudoClasses { get; } = customPseudoClasses;
}