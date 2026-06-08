namespace Cursorial.Drawing;

/// <summary>
/// How the open end of a stroke (a lone-direction cell) is drawn. <see cref="None"/> renders the full
/// line glyph; <see cref="Stub"/> renders a half-length stub (light ╴╵╶╷ / heavy ╸╹╺╻). A capped end
/// that later merges into a junction is no longer lone, so the cap is dropped.
/// </summary>
public enum EndCap : byte
{
    /// <summary>No cap — a lone end renders as the full line (─│). The default.</summary>
    None = 0,

    /// <summary>A half-stub at a lone end (╴╵╶╷ / ╸╹╺╻).</summary>
    Stub = 1
}
