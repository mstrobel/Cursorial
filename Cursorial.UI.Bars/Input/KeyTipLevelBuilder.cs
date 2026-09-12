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

    /// <summary>The suffix alphabet for colliding auto letters: digits then letters, so a group of up to 36 gets one-character suffixes.</summary>
    internal const string SuffixAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Resolves collisions and produces the level (empty when no target derived a badge).</summary>
    public KeyTipLevel Build(Action? retract = null)
    {
        // Explicit keys always beat auto — even a later-in-order explicit — so they are reserved up front (first-wins on
        // an explicit-vs-explicit clash, the duplicate dropped with a diagnostic). Auto letters that collide with each
        // other ALL survive, suffixed in document order from a 0–9A–Z alphabet at a FIXED width per letter group (one
        // character for up to 36 colliders, two beyond) so no suffix is a prefix of a sibling's — `B1` next to `B10`
        // could never commit (maintainer, 2026-09-12). A lone auto letter that an explicit key equals is dropped; one
        // that is a strict PREFIX of an explicit key (`B` beside an explicit `BX`) joins the suffixed form so it can
        // commit; a generated suffix that would land on an explicit key is skipped.
        var explicitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new HashSet<int>();
        for (var i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (p.Explicit && !explicitKeys.Add(p.KeyTip))
            {
                KeyTipDiagnostics.Warning($"KeyTip '{p.KeyTip}' collides in this level; dropping the duplicate explicit badge on {p.Target.GetType().Name}.");
                dropped.Add(i);
            }
        }

        var assigned = new string[_pending.Count];
        var autoGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase); // letter → PENDING indices, document order
        for (var i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (dropped.Contains(i))
                continue;

            if (p.Explicit)
            {
                assigned[i] = p.KeyTip;
                continue;
            }

            if (explicitKeys.Contains(p.KeyTip))
            {
                KeyTipDiagnostics.Warning($"KeyTip letter '{p.KeyTip}' is claimed by an explicit key in this level; dropping the auto-assigned badge on {p.Target.GetType().Name}.");
                dropped.Add(i);
                continue;
            }

            if (!autoGroups.TryGetValue(p.KeyTip, out var group))
                autoGroups[p.KeyTip] = group = [];
            group.Add(i);
        }

        foreach (var (letter, group) in autoGroups)
        {
            bool prefixOfExplicit = explicitKeys.Any(k => k.Length > letter.Length && k.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
            if (group.Count == 1 && !prefixOfExplicit)
            {
                assigned[group[0]] = letter;
                continue;
            }

            int width = SuffixWidthFor(group.Count);
            var next = 0;
            foreach (var index in group)
            {
                string? keyTip = null;
                while (keyTip is null)
                {
                    if (next >= Pow(SuffixAlphabet.Length, width))
                    {
                        // Only reachable if explicit keys blanket the suffix space — drop the rest with a diagnostic.
                        KeyTipDiagnostics.Warning($"KeyTip letter '{letter}' has no free suffix left in this level; dropping the auto-assigned badge on {_pending[index].Target.GetType().Name}.");
                        dropped.Add(index);
                        break;
                    }

                    var candidate = letter + Suffix(next++, width);
                    if (!explicitKeys.Contains(candidate))
                        keyTip = candidate;
                }

                if (keyTip is not null)
                    assigned[index] = keyTip;
            }
        }

        var entries = new List<KeyTipEntry>(_pending.Count);
        for (var i = 0; i < _pending.Count; i++)
        {
            if (dropped.Contains(i))
                continue;

            var p = _pending[i];
            entries.Add(new KeyTipEntry
            {
                Target = p.Target,
                KeyTip = assigned[i],
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

    /// <summary>The suffix width a group of <paramref name="colliders"/> needs so every suffix has the same length (prefix-free): 1 for up to 36, 2 up to 1296, and so on.</summary>
    internal static int SuffixWidthFor(int colliders)
    {
        var width = 1;
        var capacity = SuffixAlphabet.Length;
        while (capacity < colliders)
        {
            width++;
            capacity *= SuffixAlphabet.Length;
        }

        return width;
    }

    /// <summary>The <paramref name="ordinal"/>-th suffix of <paramref name="width"/> characters over <see cref="SuffixAlphabet"/> (base-36, zero-padded).</summary>
    internal static string Suffix(int ordinal, int width)
    {
        Span<char> chars = stackalloc char[width];
        for (var i = width - 1; i >= 0; i--)
        {
            chars[i] = SuffixAlphabet[ordinal % SuffixAlphabet.Length];
            ordinal /= SuffixAlphabet.Length;
        }

        return new string(chars);
    }

    private static int Pow(int b, int e)
    {
        var r = 1;
        for (var i = 0; i < e; i++) r *= b;
        return r;
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
