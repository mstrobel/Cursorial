using System.Collections.Frozen;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The binding table between <see cref="InteractionState"/> bits and their pseudo-class names
/// (style matrix §0.3). These names are framework-written only: they flow through
/// <c>SetInteractionState</c> and are rejected by <see cref="PseudoClassSet.Set"/> (ledger B9 /
/// SD11) and by <see cref="ClassSet"/> (all <c>':'</c>-prefixed names are).
/// </summary>
internal static class InteractionPseudoClasses
{
    private static readonly FrozenDictionary<string, InteractionState> ByName
        = new Dictionary<string, InteractionState>(StringComparer.Ordinal)
          {
              [":pointerover"] = InteractionState.PointerOver,
              [":pressed"] = InteractionState.Pressed,
              [":focus"] = InteractionState.Focused,
              [":focus-within"] = InteractionState.FocusWithin,
              [":focus-visible"] = InteractionState.FocusVisible,
              [":active-window"] = InteractionState.ActiveWindow,
              [":access-keys"] = InteractionState.AccessKeyCue,
              [":disabled"] = InteractionState.Disabled,
              [":modal-attention"] = InteractionState.ModalAttention
          }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Maps a <c>':'</c>-prefixed pseudo-class name to its interaction bit, when one backs it.</summary>
    internal static bool TryGetState(string pseudoClass, out InteractionState state)
        => ByName.TryGetValue(pseudoClass, out state);

    /// <summary>Whether <paramref name="pseudoClass"/> (with leading colon) is an interaction-state-backed name.</summary>
    internal static bool IsInteractionBacked(string pseudoClass) => ByName.ContainsKey(pseudoClass);
}
