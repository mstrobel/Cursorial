namespace Cursorial.Core.Output;

/// <summary>
/// Cursor shape and blink behavior as configured via DECSCUSR (<c>CSI Ps SP q</c>). The pairs
/// of "blinking / steady" forms map to the SGR-Ps values defined by xterm; terminals that
/// don't honor a specific shape typically render the closest one they support.
/// </summary>
public enum CursorShape : byte
{
    /// <summary>Restore terminal default — usually a blinking block.</summary>
    Default = 0,

    /// <summary>Blinking block (DECSCUSR 1).</summary>
    BlinkingBlock = 1,

    /// <summary>Steady block (DECSCUSR 2).</summary>
    SteadyBlock = 2,

    /// <summary>Blinking underline (DECSCUSR 3).</summary>
    BlinkingUnderline = 3,

    /// <summary>Steady underline (DECSCUSR 4).</summary>
    SteadyUnderline = 4,

    /// <summary>Blinking vertical bar (DECSCUSR 5, xterm extension).</summary>
    BlinkingBar = 5,

    /// <summary>Steady vertical bar (DECSCUSR 6, xterm extension).</summary>
    SteadyBar = 6,
}
