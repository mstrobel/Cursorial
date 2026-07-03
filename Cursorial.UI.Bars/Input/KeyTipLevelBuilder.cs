namespace Cursorial.UI.Bars.Input;

/// <summary>
/// Accumulates a level's KeyTip entries and resolves the collision policy at <see cref="Build"/> (keytips-design
/// §4/§5, graft G4). A host adds its targets (leaves + drills); the builder derives each badge letter via
/// <see cref="KeyTipModel"/>, then honors explicit keys unconditionally and drops auto-derived letters that collide
/// (first-in-document-order wins; the loser is dropped with a DEBUG <see cref="KeyTipDiagnostics"/> warning). Authors
/// disambiguate by setting an explicit <c>KeyTip.Key</c>.
/// </summary>
public sealed class KeyTipLevelBuilder
{
    private readonly List<Pending> _pending = [];

    private readonly record struct Pending(
        UIElement Target, string KeyTip, bool Explicit, KeyTipTargetKind Kind, KeyTipAnchor Anchor,
        Action? Activate, Action? Reveal, Func<KeyTipLevel?>? BuildNext, Action? Retract);

    /// <summary>Adds a leaf: typing its badge invokes <paramref name="activate"/> then exits.</summary>
    public void AddActivate(UIElement target, Action activate, KeyTipAnchor anchor = KeyTipAnchor.TopLeading)
    {
        if (!target.IsEffectivelyVisible) // a hidden target (e.g. a collapsed contextual ribbon tab) gets no badge
            return;

        var (keyTip, explicitKey) = KeyTipModel.Resolve(target);
        if (keyTip is null)
            return;

        _pending.Add(new Pending(target, keyTip, explicitKey, KeyTipTargetKind.Activate, anchor, activate, null, null, null));
    }

    /// <summary>Adds a drill: typing its badge performs <paramref name="reveal"/> then pushes the level
    /// <paramref name="buildNext"/> constructs (once the reveal's relayout completes). <paramref name="retract"/>
    /// undoes the reveal on Esc-back.</summary>
    public void AddDrill(
        UIElement target, KeyTipTargetKind kind, Action reveal, Func<KeyTipLevel?> buildNext,
        Action? retract = null, KeyTipAnchor anchor = KeyTipAnchor.TopLeading)
    {
        if (!target.IsEffectivelyVisible)
            return;

        var (keyTip, explicitKey) = KeyTipModel.Resolve(target);
        if (keyTip is null)
            return;

        _pending.Add(new Pending(target, keyTip, explicitKey, kind, anchor, null, reveal, buildNext, retract));
    }

    /// <summary>Adds an entry with an already-resolved badge letter, bypassing the derivation ladder (used for QAT
    /// digits and the ⋯▾/⋰ affordances whose letters are assigned by the host, not derived).</summary>
    public void AddExplicit(
        UIElement target, string keyTip, KeyTipTargetKind kind, Action? activate, Action? reveal,
        Func<KeyTipLevel?>? buildNext = null, Action? retract = null, KeyTipAnchor anchor = KeyTipAnchor.TopLeading)
    {
        if (string.IsNullOrEmpty(keyTip) || !target.IsEffectivelyVisible)
            return;

        _pending.Add(new Pending(target, keyTip.ToUpperInvariant(), true, kind, anchor, activate, reveal, buildNext, retract));
    }

    /// <summary>Resolves collisions and produces the level (empty when no target derived a badge).</summary>
    public KeyTipLevel Build(Action? retract = null)
    {
        var entries = new List<KeyTipEntry>(_pending.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);          // survivors (explicit + auto)

        // Explicit keys always beat auto — even a later-in-order explicit — so reserve every explicit letter up front,
        // then a single document-order walk keeps: an explicit (first-wins on an explicit-vs-explicit clash) and an
        // auto letter only when no explicit reserved it and no earlier survivor took it.
        var reservedExplicit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _pending)
        {
            if (p.Explicit)
                reservedExplicit.Add(p.KeyTip);
        }

        foreach (var p in _pending)
        {
            var collides = p.Explicit ? !claimed.Add(p.KeyTip)
                                      : reservedExplicit.Contains(p.KeyTip) || !claimed.Add(p.KeyTip);
            if (collides)
            {
                KeyTipDiagnostics.Warning(
                    $"KeyTip letter '{p.KeyTip}' collides in this level; dropping the {(p.Explicit ? "duplicate explicit" : "auto-assigned")} badge on {p.Target.GetType().Name}.");
                continue;
            }

            entries.Add(new KeyTipEntry
            {
                Target = p.Target,
                KeyTip = p.KeyTip,
                Kind = p.Kind,
                ExplicitKey = p.Explicit,
                Anchor = p.Anchor,
                Activate = p.Activate,
                Reveal = p.Reveal,
                BuildNext = p.BuildNext,
                Retract = p.Retract,
            });
        }

        WarnOnPrefixSiblings(entries);
        return new KeyTipLevel { Entries = entries, Retract = retract };
    }

    // A keytip that is a strict prefix of a sibling ("F" alongside "FP") can never commit: typing "F" always leaves
    // "FP" still prefix-matching, so the commit condition (exactly one viable AND complete) is never met. Auto-
    // derivation avoids this (unique single letters); it only arises from explicit multi-char KeyTip.Key authoring,
    // so a DEBUG diagnostic points the author at it (keytips-design §5 / audit finding).
    private static void WarnOnPrefixSiblings(List<KeyTipEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = 0; j < entries.Count; j++)
            {
                if (i != j
                    && entries[j].KeyTip.Length > entries[i].KeyTip.Length
                    && entries[j].KeyTip.StartsWith(entries[i].KeyTip, StringComparison.OrdinalIgnoreCase))
                {
                    KeyTipDiagnostics.Warning(
                        $"KeyTip '{entries[i].KeyTip}' is a prefix of sibling '{entries[j].KeyTip}' in this level and can never be committed; give it a distinct letter.");
                }
            }
        }
    }

    /// <summary>Whether any target has been added (before collision resolution).</summary>
    public bool HasPending => _pending.Count > 0;
}
