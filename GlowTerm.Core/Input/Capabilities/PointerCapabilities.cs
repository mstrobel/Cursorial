namespace GlowTerm.Core.Input;

/// <summary>
/// Describes pen, stylus, and touch input fidelity. On most terminals these are unsupported;
/// the Windows console host may surface them on platforms that pass pointer device events
/// through to console clients.
/// </summary>
/// <param name="Pen">True when pen/stylus events are reported.</param>
/// <param name="Pressure">True when normalized pressure is reported.</param>
/// <param name="Tilt">True when X/Y tilt is reported.</param>
/// <param name="Touch">True when finger-touch events (with multi-touch IDs) are reported.</param>
public sealed record class PointerCapabilities(
    bool Pen,
    bool Pressure,
    bool Tilt,
    bool Touch)
{
    public static PointerCapabilities None { get; } = new(
        Pen: false,
        Pressure: false,
        Tilt: false,
        Touch: false);
}
