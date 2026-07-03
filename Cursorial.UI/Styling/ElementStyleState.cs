// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The per-element styling state (design doc §3.3): the armed <see cref="StyleRuleFrame"/>s, the
/// pseudo-interest masks for the O(1) Phase-2 early-out, and the ancestor-dependency registrations
/// of <em>descendant</em> frames whose rules carry pseudo requirements on this element
/// (<c>Pane:focus-within Widget</c>). Created on first arm (or first dependency registration);
/// dropped entirely on permanent detach (SD15 — reattach rebuilds from scratch).
/// </summary>
internal sealed class ElementStyleState
{
    private static readonly StyleRuleFrame[] NoFrames = [];

    /// <summary>The armed frames, in arm/discovery order (the store owns arbitration order; this array's order carries no semantics).</summary>
    internal StyleRuleFrame[] Frames = NoFrames;

    /// <summary>The union of the frames' subject interaction-bit requirements — the one-AND flip early-out (doc §3.3).</summary>
    internal InteractionState SubjectInterest;

    /// <summary>Whether any armed frame requires a custom pseudo-class on the subject.</summary>
    internal bool HasCustomSubjectInterest;

    /// <summary>
    /// Ancestor-state requirements registered by descendant frames on <b>this</b> element — a flip
    /// here walks only this list (doc §3.3: "a flip walks only its dependency list"). Null when none.
    /// </summary>
    internal List<AncestorStateRequirement>? Dependents;

    /// <summary>The union of <see cref="Dependents"/>' interaction bits (early-out for ancestor flips).</summary>
    internal InteractionState AncestorInterest;

    /// <summary>Whether any dependent requirement involves a custom pseudo-class.</summary>
    internal bool HasCustomAncestorInterest;

    /// <summary>Dedupe flag for the engine's pending-reconcile queue (SD12 queue/fixpoint).</summary>
    internal bool QueuedForReconcile;

    /// <summary>Whether the state holds nothing and can be dropped from the element.</summary>
    internal bool IsEmpty => Frames.Length == 0 && Dependents is not { Count: > 0 };

    /// <summary>Recomputes the subject-interest masks from <see cref="Frames"/> (called after every re-match).</summary>
    internal void RebuildSubjectInterest()
    {
        var interest = InteractionState.None;
        var custom = false;

        foreach (var frame in Frames)
        {
            interest |= frame.Rule.SubjectStateBits;
            custom |= frame.Rule.SubjectCustomPseudoClasses.Length > 0;
        }

        SubjectInterest = interest;
        HasCustomSubjectInterest = custom;
    }

    /// <summary>Registers a descendant frame's requirement on this element and refreshes the ancestor-interest masks.</summary>
    internal void AddDependent(AncestorStateRequirement requirement)
    {
        (Dependents ??= []).Add(requirement);
        AncestorInterest |= requirement.Bits;
        HasCustomAncestorInterest |= requirement.CustomPseudoClasses.Length > 0;
    }

    /// <summary>Unregisters a requirement and rebuilds the ancestor-interest masks.</summary>
    internal void RemoveDependent(AncestorStateRequirement requirement)
    {
        if (Dependents is not { } dependents || !dependents.Remove(requirement))
            return;

        var interest = InteractionState.None;
        var custom = false;

        foreach (var dependent in dependents)
        {
            interest |= dependent.Bits;
            custom |= dependent.CustomPseudoClasses.Length > 0;
        }

        AncestorInterest = interest;
        HasCustomAncestorInterest = custom;
    }
}

/// <summary>
/// One armed rule's ancestor-state requirement, bound to its concrete candidate ancestors at arm
/// time (design doc §3.3 <c>AncestorDependency</c>): the requirement is satisfied while <b>any</b>
/// candidate carries all the required bits and custom pseudo-classes. Each requirement is checked
/// <b>independently</b> of the rule's other ancestor-state compounds — exact CSS semantics for a
/// single ancestor-state compound; an approximation (two compounds may share one satisfying
/// ancestor) for the SD23-②-flagged >1-compound shape (style-matrix SD23 amendment / correctness
/// review finding 3). Registered on each candidate's
/// <see cref="ElementStyleState.Dependents"/>; a flip on a candidate reconciles the owning frame.
/// </summary>
internal sealed class AncestorStateRequirement(
    StyleRuleFrame owner, InteractionState bits, string[] customPseudoClasses, UIElement[] candidates)
{
    /// <summary>The dependent frame (lives on the descendant element).</summary>
    internal StyleRuleFrame Owner { get; } = owner;

    /// <summary>The required interaction bits on a satisfying candidate.</summary>
    internal InteractionState Bits { get; } = bits;

    /// <summary>The required custom pseudo-classes on a satisfying candidate (interned, with colon).</summary>
    internal string[] CustomPseudoClasses { get; } = customPseudoClasses;

    /// <summary>The structurally valid ancestors this requirement may be satisfied by.</summary>
    internal UIElement[] Candidates { get; } = candidates;

    /// <summary>Whether any candidate currently satisfies the requirement (live read — no cached counters to drift).</summary>
    internal bool IsSatisfied()
    {
        foreach (var candidate in Candidates)
        {
            if ((candidate.InteractionStateInternal & Bits) != Bits)
                continue;

            var customsHold = true;
            foreach (var pseudo in CustomPseudoClasses)
            {
                if (!candidate.HasCustomPseudoClass(pseudo))
                {
                    customsHold = false;
                    break;
                }
            }

            if (customsHold)
                return true;
        }

        return false;
    }
}
