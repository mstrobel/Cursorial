// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The per-scope rule index (design doc §3.3 <c>StyleIndex</c>): each rule is bucketed by its
/// subject's most selective discriminator — <b>type first</b> (exact, then <c>:is</c> base — the
/// matrix's S61 "type-keyed candidate exclusion": a typed rule is never evaluated against elements
/// of unrelated types), else <c>#name</c>, else the first class, else the diagnostics-warned
/// universal bucket. Candidate sets per element are typically &lt;10 rules; the full structural
/// match still runs per candidate.
/// </summary>
/// <remarks>
/// Built lazily per attached <see cref="Styles"/> collection at a given (layer, scope depth) and
/// cached on the collection (<see cref="Styles.GetOrBuildIndex"/>); any mutation drops the cache
/// (SD21 — the coarse re-match tier rebuilds it). Also exposes the ancestor-interesting class/name
/// sets that bound subtree re-matches (doc §3.3 <c>AncestorInterestingClasses</c>).
/// </remarks>
internal sealed class StyleScopeIndex
{
    private readonly Dictionary<Type, List<ScopeRule>>? _byExactType;
    private readonly Dictionary<Type, List<ScopeRule>>? _byIsType;
    private readonly Dictionary<string, List<ScopeRule>>? _byName;
    private readonly Dictionary<string, List<ScopeRule>>? _byClass;
    private readonly List<ScopeRule>? _universal;

    internal StyleScopeIndex(StyleLayer layer, int scopeDepth, int orderBase, List<ScopeRule> rules)
    {
        Layer = layer;
        ScopeDepth = scopeDepth;
        OrderBase = orderBase;
        Rules = rules;

        HashSet<string>? ancestorClasses = null;
        HashSet<string>? ancestorNames = null;

        foreach (var scopeRule in rules)
        {
            var branch = scopeRule.Rule.Branch;
            if (branch is null)
                continue; // selector-less keyed styles (the P5 theme channel) never match through the index

            var subject = branch.Subject;

            if (subject.Type is { } type)
            {
                var buckets = subject.IsAssignableType
                    ? _byIsType ??= []
                    : _byExactType ??= [];

                AddToBucket(buckets, type, scopeRule);
            }
            else if (FindSimple(subject, SimpleSelectorKind.Name) is { } name)
            {
                AddToBucket(_byName ??= new Dictionary<string, List<ScopeRule>>(StringComparer.Ordinal), name, scopeRule);
            }
            else if (FindSimple(subject, SimpleSelectorKind.Class) is { } className)
            {
                AddToBucket(_byClass ??= new Dictionary<string, List<ScopeRule>>(StringComparer.Ordinal), className, scopeRule);
            }
            else
            {
                (_universal ??= []).Add(scopeRule);
                StyleDebugDiagnostics.WarnUniversalBucketRule(scopeRule.Rule);
            }

            if (scopeRule.Rule.AncestorStateCompounds.Length > 1)
                StyleDebugDiagnostics.WarnMultipleAncestorStateCompounds(scopeRule.Rule);

            // Ancestor-interesting discriminators: classes/names on NON-subject compounds — a
            // change to one on any element warrants a bounded subtree re-match (doc §3.3).
            for (var i = 0; i < branch.Compounds.Length - 1; i++)
            {
                foreach (var simple in branch.Compounds[i].Simples)
                {
                    switch (simple.Kind)
                    {
                        case SimpleSelectorKind.Class:
                            (ancestorClasses ??= new HashSet<string>(StringComparer.Ordinal)).Add(simple.Value);
                            break;
                        case SimpleSelectorKind.Name:
                            (ancestorNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(simple.Value);
                            break;
                    }
                }
            }
        }

        AncestorInterestingClasses = ancestorClasses;
        AncestorInterestingNames = ancestorNames;
    }

    /// <summary>The channel layer this index was built for.</summary>
    internal StyleLayer Layer { get; }

    /// <summary>The scope depth baked into the rules' keys (SD6; 0 for non-Scoped layers).</summary>
    internal int ScopeDepth { get; }

    /// <summary>The DFS order base baked into the rules' keys — non-zero only for the app-theme leg, so its
    /// rules sort ABOVE the BuiltIn framework leg within <see cref="StyleLayer.Theme"/> (app.Theme overrides BuiltIn).</summary>
    internal int OrderBase { get; }

    /// <summary>The scope's full DFS rule list (keys packed).</summary>
    internal List<ScopeRule> Rules { get; }

    /// <summary>Classes appearing in non-subject compounds (a change re-matches the subtree — S131/S132).</summary>
    internal HashSet<string>? AncestorInterestingClasses { get; }

    /// <summary>Names appearing in non-subject compounds (the <c>#name</c> sibling of the class set).</summary>
    internal HashSet<string>? AncestorInterestingNames { get; }

    /// <summary>
    /// Appends every candidate rule for <paramref name="element"/>: its exact-type bucket, the
    /// <c>:is</c> buckets along its base chain, its name bucket, one bucket per class, and the
    /// universal bucket. Each rule lives in exactly one bucket, so no deduplication is needed.
    /// </summary>
    internal void GatherCandidates(UIElement element, List<ScopeCandidate> candidates, object scopeOwner)
    {
        if (_byExactType is { } exact && exact.TryGetValue(element.GetType(), out var exactRules))
            Append(exactRules, candidates, scopeOwner);

        if (_byIsType is { } isType)
        {
            for (var type = element.GetType(); type is not null; type = type.BaseType)
            {
                if (isType.TryGetValue(type, out var isRules))
                    Append(isRules, candidates, scopeOwner);
            }
        }

        if (_byName is { } byName && element.Name is { } name && byName.TryGetValue(name, out var nameRules))
            Append(nameRules, candidates, scopeOwner);

        if (_byClass is { } byClass && element.ClassesOrNull is { } classes)
        {
            foreach (var className in classes)
            {
                if (byClass.TryGetValue(className, out var classRules))
                    Append(classRules, candidates, scopeOwner);
            }
        }

        if (_universal is { } universal)
            Append(universal, candidates, scopeOwner);
    }

    private void Append(List<ScopeRule> rules, List<ScopeCandidate> candidates, object scopeOwner)
    {
        foreach (var rule in rules)
            candidates.Add(new ScopeCandidate(rule.Rule, rule.Key, Layer, scopeOwner));
    }

    private static void AddToBucket<TKey>(Dictionary<TKey, List<ScopeRule>> buckets, TKey key, ScopeRule rule)
        where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var list))
            buckets[key] = list = [];

        list.Add(rule);
    }

    private static string? FindSimple(SelectorCompound compound, SimpleSelectorKind kind)
    {
        foreach (var simple in compound.Simples)
        {
            if (simple.Kind == kind)
                return simple.Value;
        }

        return null;
    }
}

/// <summary>One gathered candidate: the rule plus its scope-assigned key, layer, and owner (the matcher's working unit).</summary>
internal readonly struct ScopeCandidate(CompiledRule rule, StyleSortKey key, StyleLayer layer, object scopeOwner)
{
    /// <summary>The compiled rule.</summary>
    internal CompiledRule Rule { get; } = rule;

    /// <summary>The fully packed sort key (layer + specificity + scope depth + DFS order).</summary>
    internal StyleSortKey Key { get; } = key;

    /// <summary>The channel layer.</summary>
    internal StyleLayer Layer { get; } = layer;

    /// <summary>The arming scope's owner (frame-identity component for the SD21 diff).</summary>
    internal object ScopeOwner { get; } = scopeOwner;
}
