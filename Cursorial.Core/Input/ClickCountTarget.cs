namespace Cursorial.Input;

/// <summary>
/// Selects which event of a click gesture carries the multi-click count computed by the
/// <see cref="MouseClickSynthesizer"/>. The count is determined at the press (the WPF model) and
/// surfaced on the chosen event; the other events of the gesture keep the default single-click
/// value of 1.
/// </summary>
public enum ClickCountTarget
{
    /// <summary>Don't surface a multi-click count on any event — every event keeps the default of 1.</summary>
    None,

    /// <summary>Surface the count on the <see cref="MouseEventKind.ButtonDown"/> event (WPF's canonical site).</summary>
    ButtonDown,

    /// <summary>Surface the count on the <see cref="MouseEventKind.ButtonUp"/> event (Terminal.Gui's convention).</summary>
    ButtonUp,

    /// <summary>
    /// Surface the count on the synthesized <see cref="MouseEventKind.Click"/> event. Requires
    /// <see cref="MouseClickOptions.SynthesizeClickEvents"/> to be enabled, or the
    /// <see cref="MouseClickSynthesizer"/> constructor throws.
    /// </summary>
    Click
}
