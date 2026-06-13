using System.Diagnostics;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A reusable bundle of setters activated by a structural selector and the element's pseudo-class
/// state (design doc §3; the <c>When</c> data-condition conjunct joins at P4). A style is sealed —
/// validated, <see cref="BasedOn"/>-flattened, nesting-composed, and compiled — exactly once;
/// sealing happens automatically when the style attaches (to an attached <see cref="Styles"/>
/// collection or as an explicit <see cref="UIElement.Style"/>) and is idempotent. Sealed styles are
/// immutable and freely shareable across collections (style matrix SD19) — each scope compiles its
/// own order indices over the shared rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placement rules</b> (SD17): a style inside a <see cref="Styles"/> collection needs a
/// selector (a selector-less, key-less style is an attach-time error; <c>^</c>-rooted selectors are
/// likewise invalid at the top level of a collection). An explicit <see cref="UIElement.Style"/>
/// must be selector-less or <c>^</c>-rooted. <see cref="Children"/> selectors must start with
/// <c>^</c> (seal error naming style and rule index).
/// </para>
/// <para>
/// <b>Seal-time validation</b> (doc §3.3): setter constants convert once through the SD9 ladder;
/// errors name (style, rule index, property). <see cref="BasedOn"/> chains flatten (derived setters
/// append after base; the derived selector alone carries specificity — S46) and are cycle-checked.
/// </para>
/// </remarks>
public sealed class Style
{
    private static readonly DataCondition[] NoConditions = [];

    private SetterCollection? _setters;
    private WhenCollection? _when;
    private StyleCollection? _children;
    private EdgeActionCollection? _enter;
    private EdgeActionCollection? _exit;
    private CompiledSetter[]? _flattenedSetters;
    private DataCondition[]? _flattenedWhen;
    private CompiledRule[]? _compiledRules;

    /// <summary>Creates a selector-less style (legal as an explicit <see cref="UIElement.Style"/> or a keyed theme entry).</summary>
    public Style() {}

    /// <summary>Creates a style activated by <paramref name="selector"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="selector"/> has an open combinator (an unfinished fluent chain such as <c>Selectors.OfType&lt;Widget&gt;().Child()</c>).</exception>
    public Style(Selector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ThrowIfDanglingCombinator(selector, nameof(selector));
        Selector = selector;
    }

    /// <summary>Creates a style whose selector is parsed from <paramref name="selector"/> (see <see cref="UI.Selector.Parse"/>).</summary>
    public Style(string selector, ISelectorTypeResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Selector = Selector.Parse(selector, resolver);
    }

    /// <summary>The structural activation selector; <see langword="null"/> = selector-less (explicit/keyed-theme placement only).</summary>
    public Selector? Selector { get; init; }

    /// <summary>
    /// The setter-inheritance base: flattened at <see cref="Seal"/> (base setters first, derived
    /// appended — nearest-derived wins per property); a cycle is an
    /// <see cref="InvalidOperationException"/> naming both styles. <c>BasedOn</c> contributes
    /// setters, never specificity (S46).
    /// </summary>
    public Style? BasedOn { get; init; }

    /// <summary>The optional resource key (keyed-theme lookup at P5; also the style's diagnostics identity).</summary>
    public string? Key { get; init; }

    /// <summary>The style's setters (constants / <see cref="UIProperty.UnsetValue"/> at P3 — SD9).</summary>
    public SetterCollection Setters => _setters ??= new SetterCollection(this);

    /// <summary>
    /// The style's data-condition conjunction (design doc §3.1 / §3.3): a rule of this style is active
    /// iff its structural selector matches, all required pseudo-classes are set, <b>and</b> every
    /// <see cref="DataCondition"/> here holds. Empty = always (the structural/pseudo verdict alone).
    /// Each condition counts 1 classLike toward specificity (SD5). <c>BasedOn</c> conditions compose
    /// base-first (all must hold; a derived style is at least as restrictive as its base).
    /// </summary>
    public WhenCollection When => _when ??= new WhenCollection(this);

    /// <summary>
    /// Adds a type-checked setter and returns <see langword="this"/> for chaining — the low-friction
    /// code-first authoring path (<c>new Style("Widget:focus").Set(Widget.P, 5).Set(Widget.Fill,
    /// brush)</c>). The value is constrained to the property's type at compile time, so a wrong-type
    /// constant is a build error rather than a seal-time failure; conversion still runs once at
    /// <see cref="Seal"/> (SD9) for the assignable/convertible cases. Throws if the style is already
    /// sealed (S42).
    /// </summary>
    /// <typeparam name="T">The property's value type.</typeparam>
    /// <param name="property">The target styled property.</param>
    /// <param name="value">The setter value (compile-time checked against <typeparamref name="T"/>).</param>
    /// <returns>This style, for fluent chaining.</returns>
    public Style Set<T>(StyledProperty<T> property, T value)
    {
        ArgumentNullException.ThrowIfNull(property);
        Setters.Add(new Setter(property, value));
        return this;
    }

    /// <summary>
    /// Adds a DynamicResource setter and returns <see langword="this"/> for chaining (the R2 palette
    /// spine — design doc §11.5 / B10): the setter's value is resolved per styled element against its
    /// resource chain at <see cref="UIApplication.ActualThemeVariant"/>, and re-resolved live on every
    /// variant-flip / chain-shadowing pulse reaching the element's root — so a built-in control theme
    /// that wires its color setters this way re-skins on a dark/light flip with zero template work. The
    /// contribution arms at the rule's style layer (below <see cref="BindingPriority.LocalValue"/>), so
    /// an explicit local value always wins; a resource miss leaves the setter valueless (the store
    /// promotes the next source). Throws if the style is already sealed (S42).
    /// </summary>
    /// <typeparam name="T">The property's value type.</typeparam>
    /// <param name="property">The target styled property.</param>
    /// <param name="key">The resource key resolved against the styled element's chain.</param>
    /// <returns>This style, for fluent chaining.</returns>
    public Style SetResource<T>(StyledProperty<T> property, object key)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(key);
        Setters.Add(new Setter(property, new ResourceReference(key)));
        return this;
    }

    /// <summary>
    /// Nested styles whose selectors start with <c>^</c> and AND-compose with this style's selector
    /// into flattened rules (<c>Widget.primary</c> + <c>^:pointerover</c> ⇒ one rule
    /// <c>Widget.primary:pointerover</c>; composition is transitive through grandchildren).
    /// </summary>
    public StyleCollection Children => _children ??= new StyleCollection(this);

    /// <summary>Edge actions run, in document order, every time a rule of this style activates (ledger B5; SD16).</summary>
    public EdgeActionCollection Enter => _enter ??= new EdgeActionCollection(this);

    /// <summary>Edge actions run, in document order, every time a rule of this style retracts (including detach).</summary>
    public EdgeActionCollection Exit => _exit ??= new EdgeActionCollection(this);

    /// <summary>Whether <see cref="Seal"/> has completed; sealed styles reject all mutation (S42).</summary>
    public bool IsSealed { get; private set; }

    /// <summary>
    /// Validates, flattens, and compiles the style (idempotent; called automatically on attach —
    /// doc §3.3). Errors leave the style unsealed and name (style, rule index, property) for setter
    /// problems, (style, rule index) for placement problems, and both styles for a
    /// <see cref="BasedOn"/> cycle.
    /// </summary>
    /// <exception cref="InvalidOperationException">A seal-time validation failure.</exception>
    public void Seal()
    {
        if (IsSealed)
            return;

        // A fluent selector with an open combinator (e.g. Selectors.OfType<Widget>().Child() with no
        // following compound) reaches here only via the `init`-set Selector path; reject it rather
        // than silently compiling the completed-so-far form (correctness review finding 6a).
        if (Selector is not null)
            ThrowIfDanglingCombinator(Selector, paramName: null);

        // A prior Seal() that threw after partially populating the caches leaves this style (and its
        // children) unsealed but with stale _flattenedSetters/_compiledRules. A re-seal after a
        // setter add must recompute from current state, so drop the caches across the subtree first
        // (correctness review finding 5). Sealed styles never reach here — IsSealed short-circuits.
        ClearCompileCaches(this);

        // Compile the full flattened rule set (own rule(s) + Children, depth-first). This walks and
        // validates every nested style (and BasedOn chain) and converts every setter exactly once (SD9).
        var rules = new List<CompiledRule>();
        EmitRules(rules, this, parentBranches: null, parentWhen: NoConditions, isNested: false, this);
        _compiledRules = [.. rules];

        LintHoverParity();
        SealSubtree(this);
    }

    /// <summary>Drops the compile caches of an unsealed style and its unsealed <see cref="Children"/> (a failed-seal re-seal recomputes from current state).</summary>
    private static void ClearCompileCaches(Style style)
    {
        if (style.IsSealed)
            return; // a sealed (immutable) child shares its cached rules by reference — never clear

        style._flattenedSetters = null;
        style._flattenedWhen = null;
        style._compiledRules = null;

        if (style._children is { Count: > 0 } children)
        {
            foreach (var child in children)
                ClearCompileCaches(child);
        }
    }

    /// <summary>
    /// The §3.8 hover-parity lint (style matrix SD23-③, DEBUG builds only, once per offending
    /// style): a flattened rule set containing a <c>:pointerover</c>-requiring rule for property
    /// set S with no focus-parity rule (<c>:focus</c>, <c>:focus-within</c>, or
    /// <c>:focus-visible</c> — any keyboard-visible affordance silences) covering any property in
    /// S. Subject-level requirements only — ancestor-state compounds describe a different element.
    /// </summary>
    [Conditional("DEBUG")]
    private void LintHoverParity()
    {
        var rules = _compiledRules!;

        const InteractionState focusParity =
            InteractionState.Focused | InteractionState.FocusWithin | InteractionState.FocusVisible;

        HashSet<UIProperty>? focusCovered = null;

        foreach (var rule in rules)
        {
            if ((rule.SubjectStateBits & focusParity) == 0)
                continue;

            focusCovered ??= [];
            foreach (var setter in rule.Setters)
                focusCovered.Add(setter.Property);
        }

        foreach (var rule in rules)
        {
            if ((rule.SubjectStateBits & InteractionState.PointerOver) == 0 || rule.Setters.Length == 0)
                continue;

            var covered = false;

            if (focusCovered is not null)
            {
                foreach (var setter in rule.Setters)
                {
                    if (focusCovered.Contains(setter.Property))
                    {
                        covered = true;
                        break;
                    }
                }
            }

            if (!covered)
            {
                StyleDebugDiagnostics.WarnHoverParity(this, rule);
                return; // one finding per style
            }
        }
    }

    // ───────────────────────────── internal compile surface ─────────────────────────────

    /// <summary>
    /// The depth-first flattened rule list (own selector members first, then <see cref="Children"/>
    /// recursively). Available once sealed. Scope-independent: layer / scope depth / order join at
    /// arm time (<see cref="Styles.BuildScopeRules"/>). A style sealed as a nested child compiles
    /// its standalone rules lazily on first use (the SD19 sharing path — a sealed style is
    /// immutable, so the compile is deterministic whenever it runs).
    /// </summary>
    internal CompiledRule[] CompiledRules
    {
        get
        {
            if (_compiledRules is null)
            {
                if (!IsSealed)
                    throw new InvalidOperationException($"Style '{IdentityForDiagnostics}' has not been sealed.");

                var rules = new List<CompiledRule>();
                EmitRules(rules, this, parentBranches: null, parentWhen: NoConditions, isNested: false, this);
                _compiledRules = [.. rules];
            }

            return _compiledRules;
        }
    }

    /// <summary>The diagnostics identity: <c>Key ?? selector text</c> (the §3.3 error-naming contract).</summary>
    internal string IdentityForDiagnostics => Key ?? Selector?.ToString() ?? "(selector-less)";

    /// <summary>The <see cref="Enter"/> actions without lazy allocation (the engine's edge-invocation read).</summary>
    internal EdgeActionCollection? EnterOrNull => _enter;

    /// <summary>The <see cref="Exit"/> actions without lazy allocation.</summary>
    internal EdgeActionCollection? ExitOrNull => _exit;

    /// <summary>Throws when sealed — the collections' mutation guard (S42).</summary>
    internal void ThrowIfSealed()
    {
        if (IsSealed)
        {
            throw new InvalidOperationException(
                $"Style '{IdentityForDiagnostics}' is sealed and cannot be modified. Sealed styles are " +
                "immutable; create a new style (or use BasedOn) instead.");
        }
    }

    // ───────────────────────────── seal mechanics ─────────────────────────────

    private static void ThrowIfDanglingCombinator(Selector selector, string? paramName)
    {
        if (selector.PendingCombinator is null)
            return;

        const string message =
            "The selector has an open combinator — an unfinished fluent chain (e.g. " +
            "Selectors.OfType<Widget>().Child() with no following compound). Complete the chain before " +
            "using it as a style selector.";

        throw paramName is null
            ? new InvalidOperationException(message)
            : new ArgumentException(message, paramName);
    }

    private void ThrowOnBasedOnCycle()
    {
        if (BasedOn is null)
            return;

        var seen = new HashSet<Style>(ReferenceEqualityComparer.Instance) { this };
        var chain = new List<string> { IdentityForDiagnostics };

        for (var ancestor = BasedOn; ancestor is not null; ancestor = ancestor.BasedOn)
        {
            chain.Add(ancestor.IdentityForDiagnostics);

            if (!seen.Add(ancestor))
            {
                // Name every style on the cycle (S45: "naming both styles").
                throw new InvalidOperationException(
                    $"BasedOn cycle detected while sealing style '{IdentityForDiagnostics}': " +
                    $"{string.Join(" <- ", chain.Select(static identity => $"'{identity}'"))}.");
            }
        }
    }

    /// <summary>
    /// The flattened, converted setter array: the <see cref="BasedOn"/> chain base-first, then this
    /// style's own setters, deduplicated per property with the <b>last</b> occurrence winning
    /// ("later wins within the rule" — S43). Cached; shared by reference across this style's rules
    /// and across every scope arming them.
    /// </summary>
    private CompiledSetter[] GetFlattenedSetters(int ruleIndexForErrors)
    {
        if (_flattenedSetters is not null)
            return _flattenedSetters;

        var flattened = new List<CompiledSetter>();
        AppendFlattenedSetters(flattened, this, ruleIndexForErrors);

        // Dedupe per property, last occurrence wins; survivors keep their last-occurrence order.
        for (var i = flattened.Count - 1; i >= 0; i--)
        {
            for (var j = i - 1; j >= 0; j--)
            {
                if (ReferenceEquals(flattened[j].Property, flattened[i].Property))
                {
                    flattened.RemoveAt(j);
                    i--;
                }
            }
        }

        return _flattenedSetters = [.. flattened];
    }

    private static void AppendFlattenedSetters(List<CompiledSetter> flattened, Style style, int ruleIndexForErrors)
    {
        // The BasedOn chain was sealed before this style's setters flatten (EmitRules), so the
        // base's own flattened (deduped) array is cached and equivalent under the final dedupe.
        if (style.BasedOn is {} basedOn)
            flattened.AddRange(basedOn.GetFlattenedSetters(0));

        if (style._setters is not { Count: > 0 } setters)
            return;

        foreach (var setter in setters)
            flattened.Add(StyleSetterConverter.Convert(style, ruleIndexForErrors, setter));
    }

    /// <summary>
    /// The flattened <see cref="When"/> conjunction (base-first, no dedupe — every condition must
    /// hold; a derived style is at least as restrictive as its base, design doc §3.1). Shared by
    /// reference across the style's rules; cached after the first compute.
    /// </summary>
    private DataCondition[] GetFlattenedWhen()
    {
        if (_flattenedWhen is not null)
            return _flattenedWhen;

        // The common case — no conditions anywhere up the chain — interns the empty array (zero
        // per-rule cost for the overwhelmingly common unconditional style).
        if (BasedOn is null && _when is not { Count: > 0 })
            return _flattenedWhen = NoConditions;

        var flattened = new List<DataCondition>();
        AppendFlattenedWhen(flattened, this);
        return _flattenedWhen = flattened.Count == 0 ? NoConditions : [.. flattened];
    }

    private static void AppendFlattenedWhen(List<DataCondition> flattened, Style style)
    {
        // BasedOn conditions compose base-first (the base's are AND-ed in; sealed before this style
        // flattens — EmitRules order). No dedupe: two conditions over the same binding are legal and
        // both count toward specificity (SD5).
        if (style.BasedOn is { } basedOn)
            flattened.AddRange(basedOn.GetFlattenedWhen());

        if (style._when is { Count: > 0 } when)
            flattened.AddRange(when);
    }

    /// <summary>
    /// Emits the depth-first flattened rules of <paramref name="style"/> into
    /// <paramref name="rules"/>: one rule per selector-list member (consecutive indices, shared
    /// setters), then each child of <see cref="Children"/> recursively, AND-composed against this
    /// style's composed branches (doc §3.3; SD5 order).
    /// </summary>
    private static void EmitRules(
        List<CompiledRule> rules, Style style, SelectorBranch[]? parentBranches, DataCondition[] parentWhen,
        bool isNested, Style ownerStyle)
    {
        style.ThrowOnBasedOnCycle();
        style.BasedOn?.Seal();

        SelectorBranch[]? composed;

        if (!isNested)
        {
            // The top-level style: its branches as written ('^' roots are preserved — explicit
            // styles anchor them to the styled element).
            composed = style.Selector?.Branches;
        }
        else
        {
            // A nested child: every branch must be '^'-rooted (SD17)...
            if (style.Selector is null || !style.Selector.IsNestingRooted)
            {
                throw new InvalidOperationException(
                    $"Style '{ownerStyle.IdentityForDiagnostics}': rule {rules.Count}: a Children style's selector " +
                    "must start with the nesting anchor '^' (SD17). " +
                    (style.Selector is null ? "The child style has no selector." : $"Got '{style.Selector}'."));
            }

            if (parentBranches is null)
            {
                // ... under a selector-less (explicit) parent the '^' anchor stays unresolved —
                // it binds to the styled element at arm time.
                composed = style.Selector.Branches;
            }
            else
            {
                // ... and AND-composes with each parent branch (member order: child members outer,
                // parent members inner — document order).
                var childBranches = style.Selector.Branches;
                composed = new SelectorBranch[parentBranches.Length * childBranches.Length];
                var index = 0;

                foreach (var childBranch in childBranches)
                {
                    foreach (var parentBranch in parentBranches)
                        composed[index++] = ComposeBranch(parentBranch, childBranch, ownerStyle, rules.Count);
                }
            }
        }

        var setters = style.GetFlattenedSetters(rules.Count);

        // The rule's data-condition conjunction: the nesting parent's conditions (already flattened)
        // AND this style's own flattened conditions (own + BasedOn). A composed child rule requires
        // both — the structural compound was AND-composed, so the gating conditions are too (doc §3.3).
        var ownWhen = style.GetFlattenedWhen();
        var combinedWhen = Combine(parentWhen, ownWhen);

        // Each DataCondition counts 1 classLike toward specificity (SD5 — data conditions ARE
        // specificity; a When-guarded style beats its unguarded base with no extra mechanism). The
        // StyleSortKey factory saturates the classLike field, so an overlong conjunction never
        // overflows into a neighbor.
        var whenSpecificity = combinedWhen.Length;

        if (composed is null)
        {
            rules.Add(new CompiledRule(
                          ownerStyle, style, rules.Count, branch: null, selectorText: string.Empty,
                          names: 0, classLike: whenSpecificity, types: 0, setters, combinedWhen));
        }
        else
        {
            foreach (var branch in composed)
            {
                var (names, classLike, types) = branch.CountSpecificity();

                rules.Add(new CompiledRule(
                              ownerStyle, style, rules.Count, branch, branch.ToCanonicalString(),
                              names, classLike + whenSpecificity, types, setters, combinedWhen));
            }
        }

        if (style._children is { Count: > 0 } children)
        {
            foreach (var child in children)
                EmitRules(rules, child, composed, combinedWhen, isNested: true, ownerStyle: ownerStyle);
        }
    }

    /// <summary>AND-concatenates two flattened condition lists (parent-first), interning the empty/identity cases.</summary>
    private static DataCondition[] Combine(DataCondition[] parent, DataCondition[] own)
    {
        if (parent.Length == 0)
            return own;
        if (own.Length == 0)
            return parent;
        return [.. parent, .. own];
    }

    /// <summary>
    /// AND-composes a parent branch with a <c>^</c>-rooted child branch (doc §3.3): the child's
    /// anchor compound merges into the parent's subject (parent simples first, child's appended in
    /// declaration order); compounds right of the anchor append with their combinators
    /// (<c>^ /template/ #chrome</c> under <c>Widget</c> ⇒ <c>Widget /template/ #chrome</c> — S50).
    /// </summary>
    private static SelectorBranch ComposeBranch(
        SelectorBranch parent, SelectorBranch child, Style ownerStyle, int ruleIndex)
    {
        var parentSubject = parent.Subject;
        var anchor = child.Compounds[0];

        if (anchor.TypeName is not null && parentSubject.TypeName is not null)
        {
            throw new InvalidOperationException(
                $"Style '{ownerStyle.IdentityForDiagnostics}': rule {ruleIndex}: the nested selector " +
                $"'{child.ToCanonicalString()}' adds a type test to a parent compound that already has one " +
                $"('{parentSubject.TypeName}').");
        }

        var mergedType = parentSubject.TypeName is not null ? parentSubject : anchor;

        var merged = new SelectorCompound(
            isNesting: parentSubject.IsNesting,
            type: mergedType.Type,
            isAssignableType: mergedType.IsAssignableType,
            typeName: mergedType.TypeName,
            simples: [.. parentSubject.Simples, .. anchor.Simples]);

        var compounds = new SelectorCompound[parent.Compounds.Length + child.Compounds.Length - 1];
        parent.Compounds.AsSpan(..^1).CopyTo(compounds);
        compounds[parent.Compounds.Length - 1] = merged;
        child.Compounds.AsSpan(1..).CopyTo(compounds.AsSpan(parent.Compounds.Length));

        var combinators = new SelectorCombinator[parent.Combinators.Length + child.Combinators.Length];
        parent.Combinators.CopyTo(combinators, 0);
        child.Combinators.CopyTo(combinators, parent.Combinators.Length);

        return new SelectorBranch(compounds, combinators);
    }

    private static void SealSubtree(Style style)
    {
        style.IsSealed = true;

        if (style._children is { Count: > 0 } children)
        {
            foreach (var child in children)
                SealSubtree(child);
        }
    }
}