// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The control-author pseudo-class surface (design doc §3.2; exposed as the protected
/// <c>UIElement.PseudoClasses</c>). <see cref="Set"/> is sanctioned for control-semantic classes
/// with no <see cref="InteractionState"/> bit (<c>:open</c>, <c>:highlighted</c>, …): the §0.3
/// interaction-backed names (<c>:pressed</c>, <c>:focus</c>, …) are rejected — they flow only
/// through <c>SetInteractionState</c> so the dispatcher-held clearing guarantees hold (ledger B9 /
/// SD11).
/// </summary>
public sealed class PseudoClassSet
{
    private readonly UIElement _owner;
    private readonly List<string> _custom = [];

    internal PseudoClassSet(UIElement owner) => _owner = owner;

    /// <summary>
    /// Sets or clears a custom pseudo-class; returns whether the state changed (a no-change set is
    /// restyle-free). The name must be <c>':'</c>-prefixed.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="pseudoClass"/> is null/empty or lacks the <c>':'</c> prefix.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="pseudoClass"/> is an <see cref="InteractionState"/>-backed name (B9).</exception>
    public bool Set(string pseudoClass, bool active)
    {
        pseudoClass = Validate(pseudoClass);

        bool changed;

        if (active)
        {
            changed = !_custom.Contains(pseudoClass);

            if (changed)
                _custom.Add(pseudoClass);
        }
        else
        {
            changed = _custom.Remove(pseudoClass);
        }

        if (changed)
            _owner.OnStylingPseudoClassChanged(pseudoClass, active);

        return changed;
    }

    /// <summary>
    /// Whether <paramref name="pseudoClass"/> is currently set — custom names check the custom set;
    /// interaction-backed names (§0.3) reflect the element's <see cref="InteractionState"/> bits.
    /// </summary>
    public bool Contains(string pseudoClass)
    {
        ArgumentException.ThrowIfNullOrEmpty(pseudoClass);

        return InteractionPseudoClasses.TryGetState(pseudoClass, out var state)
                   ? (_owner.InteractionStateInternal & state) != 0
                   : _custom.Contains(pseudoClass);
    }

    /// <summary>The custom (non-interaction) pseudo-classes currently set, for the matcher and diagnostics.</summary>
    internal IReadOnlyList<string> CustomClasses => _custom;

    /// <summary>Whether <paramref name="pseudoClass"/> is in the custom set (ordinal; interaction bits NOT consulted).</summary>
    internal bool ContainsCustom(string pseudoClass) => _custom.Contains(pseudoClass);

    /// <summary>
    /// The <see cref="PseudoClassMapping"/> write path. Mapping targets are custom names by
    /// registration-time validation, and classify results re-validate through <see cref="Set"/>'s
    /// ordinary guards — the B9 sanction cannot be escaped through a misbehaving classifier.
    /// </summary>
    internal bool SetInternal(string pseudoClass, bool active) => Set(pseudoClass, active);

    private static string Validate(string pseudoClass)
    {
        ArgumentException.ThrowIfNullOrEmpty(pseudoClass);

        if (pseudoClass[0] != ':')
        {
            throw new ArgumentException(
                $"Pseudo-class name '{pseudoClass}' is invalid: pseudo-class names are ':'-prefixed (SD11).",
                nameof(pseudoClass));
        }

        if (pseudoClass.Length == 1)
            throw new ArgumentException("Pseudo-class name ':' is empty.", nameof(pseudoClass));

        if (InteractionPseudoClasses.IsInteractionBacked(pseudoClass))
        {
            throw new InvalidOperationException(
                $"Pseudo-class '{pseudoClass}' is backed by an InteractionState bit and flows only through " +
                "SetInteractionState (ledger B9) — direct PseudoClasses.Set is sanctioned for " +
                "control-semantic classes without an interaction bit only.");
        }

        return string.Intern(pseudoClass);
    }
}