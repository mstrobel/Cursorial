namespace Cursorial.Output.Capabilities;

/// <summary>
/// Describes which cursor-presentation controls a terminal honors.
/// </summary>
/// <param name="ShapeControl">DECSCUSR — block / underline / bar shape selection.</param>
/// <param name="VisibilityControl">DECTCEM — cursor show / hide (DECSET 25).</param>
/// <param name="BlinkControl">Distinct blink / non-blink variants of cursor shape via DECSCUSR.</param>
/// <param name="ColorControl">OSC 12 — set cursor color.</param>
/// <param name="MultipleCursors">
/// The Kitty multiple-cursors protocol (<c>CSI &gt; SHAPE ; COORD_TYPE : COORDS SP q</c>) with
/// beam extra cursors — realized only when the terminal answered the support query
/// (<c>CSI &gt; SP q</c>) with a shape list including 2 (beam), the shape the glyph-height
/// caret band emits. Extra cursors share the main cursor's color / opacity / blink and are
/// screen-fixed (IND/RI never move them), so consumers re-emit or clear them per frame.
/// See <see href="https://sw.kovidgoyal.net/kitty/multiple-cursors-protocol/"/>.
/// </param>
public sealed record CursorCapabilities(bool ShapeControl,
                                        bool VisibilityControl,
                                        bool BlinkControl,
                                        bool ColorControl,
                                        bool MultipleCursors = false)
{
    public static CursorCapabilities None { get; } = new(ShapeControl: false,
                                                         VisibilityControl: false,
                                                         BlinkControl: false,
                                                         ColorControl: false,
                                                         MultipleCursors: false);
}