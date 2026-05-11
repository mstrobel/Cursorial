namespace Cursorial.Core.Output;

/// <summary>
/// Describes which cursor-presentation controls a terminal honors.
/// </summary>
/// <param name="ShapeControl">DECSCUSR — block / underline / bar shape selection.</param>
/// <param name="VisibilityControl">DECTCEM — cursor show / hide (DECSET 25).</param>
/// <param name="BlinkControl">Distinct blink / non-blink variants of cursor shape via DECSCUSR.</param>
/// <param name="ColorControl">OSC 12 — set cursor color.</param>
public sealed record class CursorCapabilities(
    bool ShapeControl,
    bool VisibilityControl,
    bool BlinkControl,
    bool ColorControl)
{
    public static CursorCapabilities None { get; } = new(
        ShapeControl: false,
        VisibilityControl: false,
        BlinkControl: false,
        ColorControl: false);
}
