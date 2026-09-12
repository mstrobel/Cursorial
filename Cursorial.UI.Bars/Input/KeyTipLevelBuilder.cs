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
        Action? Activate, Action? Reveal, Func<KeyTipLevel?>? BuildNext, Action? Retract, bool KeepsOverlay = false);

    /// <summary>Adds a leaf: typing its badge invokes <paramref name="activate"/> then exits.</summary>
    public void AddActivate(UIElement target, Action activate, KeyTipAnchor anchor = KeyTipAnchor.TopLeading, bool keepsOverlay = false)
    {
        if (!target.IsEffectivelyVisible) // a hidden target (e.g. a collapsed contextual ribbon tab) gets no badge
            return;

        var (keyTip, explicitKey) = KeyTipModel.Resolve(target);
        if (keyTip is null)
            return;

        _pending.Add(new Pending(target, keyTip, explicitKey, KeyTipTargetKind.Activate, anchor, activate, null, null, null, keepsOverlay));
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

        // Explicit keys always beat auto — even a later-in-order explicit — so reserve every explicit letter up front.
        // Then one document-order walk: an explicit letter is kept first-wins (a duplicate explicit is dropped); an
        // auto letter an explicit reserved is dropped; auto letters that collide with EACH OTHER all survive with a
        // digit suffix in document order — `B`, `B` → `B0`, `B1` (the earlier survivor is renamed when the second
        // arrives), so no control loses its badge to a same-letter sibling (maintainer, 2026-09-12).
        var reservedExplicit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _pending)
        {
            if (p.Explicit)
                reservedExplicit.Add(p.KeyTip);
        }

        var claimedExplicit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var autoByLetter = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase); // letter → ENTRY indices

        foreach (var p in _pending)
        {
            var keyTip = p.KeyTip;

            if (p.Explicit)
            {
                if (!claimedExplicit.Add(keyTip))
                {
                    KeyTipDiagnostics.Warning($"KeyTip '{keyTip}' collides in this level; dropping the duplicate explicit badge on {p.Target.GetType().Name}.");
                    continue;
                }
            }
            else if (reservedExplicit.Contains(keyTip))
            {
                KeyTipDiagnostics.Warning($"KeyTip letter '{keyTip}' is claimed by an explicit key in this level; dropping the auto-assigned badge on {p.Target.GetType().Name}.");
                continue;
            }
            else
            {
                if (!autoByLetter.TryGetValue(keyTip, out var siblings))
                    autoByLetter[keyTip] = siblings = [];

                if (siblings.Count == 1)
                    entries[siblings[0]].KeyTip = keyTip + "0"; // the first of a now-colliding pair takes suffix 0

                if (siblings.Count > 0)
                    keyTip += siblings.Count.ToString();

                siblings.Add(entries.Count);
            }

            entries.Add(new KeyTipEntry
            {
                Target = p.Target,
                KeyTip = keyTip,
                Kind = p.Kind,
                ExplicitKey = p.Explicit,
                Anchor = p.Anchor,
                Activate = p.Activate,
                Reveal = p.Reveal,
                BuildNext = p.BuildNext,
                Retract = p.Retract,
                KeepsOverlay = p.KeepsOverlay,
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
