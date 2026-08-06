namespace Cursorial.Text;

/// <summary>
/// The text posture axis (proposal-TextAttributes-decomposition §1, amended 2026-07-13): the enum
/// shape (rather than a bare bool) keeps the <c>Text*</c> property family discoverable as a set
/// (<see cref="TextWeight"/>/<see cref="TextStyle"/>) and leaves headroom for future terminal
/// posture standards (SGR 20 fraktur is the historical precedent) — while still refusing WPF's
/// <c>Oblique</c>, which has no terminal encoding.
/// </summary>
public enum TextStyle : byte
{
    /// <summary>Upright text (SGR 23 — the reset state).</summary>
    Normal = 0,

    /// <summary>SGR 3 — italic.</summary>
    Italic,
}