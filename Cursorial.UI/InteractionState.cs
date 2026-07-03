namespace Cursorial.UI;

/// <summary>
/// The framework-written interaction bits (design doc §3.2) backing the interaction pseudo-classes
/// (<c>:pointerover</c>, <c>:pressed</c>, <c>:focus</c>, …). Flips are equality-gated and written
/// only by framework services (S3's dispatcher/focus manager, S4's window manager, S1's
/// effective-enabled plumbing) and control authors via the sanctioned setters — never by styles.
/// </summary>
/// <remarks>
/// P2 ships the storage and the S3 writers (<see cref="InteractionState.PointerOver"/> at the
/// mouse stage); the full <c>IInteractionStateSink</c> batching surface and the styling-engine
/// observer (Fork B, P3) land per the doc's §3.2 contract.
/// </remarks>
[Flags]
public enum InteractionState : uint
{
    /// <summary>No interaction bits set.</summary>
    None = 0,

    /// <summary>The pointer is over this element or a descendant (<c>:pointerover</c>) — the hover chain sets it.</summary>
    PointerOver = 1 << 0,

    /// <summary>Pressed/armed (<c>:pressed</c>) — flows through <c>SetInteractionState</c> only (C8 clearing guarantees).</summary>
    Pressed = 1 << 1,

    /// <summary>This element holds keyboard focus (<c>:focus</c>).</summary>
    Focused = 1 << 2,

    /// <summary>Keyboard focus is on this element or inside its subtree (<c>:focus-within</c>).</summary>
    FocusWithin = 1 << 3,

    /// <summary>Focus should be visually indicated (<c>:focus-visible</c>) — keyboard-driven focus arrival.</summary>
    FocusVisible = 1 << 4,

    /// <summary>The element belongs to the active window (<c>:active-window</c>) — S4 writes it (P7).</summary>
    ActiveWindow = 1 << 5,

    /// <summary>Access-key cues are showing for this scope (<c>:access-keys</c>) — the manager writes it (I4).</summary>
    AccessKeyCue = 1 << 6,

    /// <summary>Effectively disabled (<c>:disabled</c>) — S1's effective-enabled pipeline pushes it.</summary>
    Disabled = 1 << 7,

    /// <summary><c>:modal-attention</c> — S4 sets on a blocked-window press; cleared by an S5 timer (P7).</summary>
    ModalAttention = 1 << 8
}
