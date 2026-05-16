namespace Cursorial.Rendering.Fragments;

/// <summary>
/// Classifies a fragment by where in the terminal's rendering stack its protocol payload
/// lands. The renderer treats the two layers differently for cell-emission, diffing, and
/// removal — see the individual values for the contract.
/// </summary>
public enum FragmentLayer
{
    /// <summary>
    /// <para>
    /// The fragment's payload <b>is</b> cell content — the terminal treats the fragment's
    /// footprint cells as the fragment's own. OSC 66 sized text, Kitty graphics in the default
    /// text-overwrite mode, iTerm2 inline images.
    /// </para>
    /// <para>
    /// <b>Rendering implications.</b> Cells in the fragment's footprint are skipped during the
    /// cell-emit pass — emitting glyphs there would corrupt the fragment's payload. The cell's
    /// <em>background</em> still paints (as a space-with-background) so panel backgrounds and
    /// other ambient colors carry through wherever the fragment doesn't paint pixels.
    /// </para>
    /// <para>
    /// <b>Removal.</b> Cell content overwriting cell content erases the fragment's visual,
    /// so removal just stops emitting the fragment — the cell pass repaints with the
    /// back-buffer's actual glyphs and the fragment is gone. <see cref="IBufferFragment.EmitErase"/>
    /// is a no-op for this layer.
    /// </para>
    /// </summary>
    Cells = 0,

    /// <summary>
    /// <para>
    /// The fragment's payload sits on a separate display plane the terminal composites over
    /// cells. Future Kitty graphics with placement IDs, hardware-composited overlays, alpha-
    /// blended highlights.
    /// </para>
    /// <para>
    /// <b>Rendering implications.</b> Cells in the fragment's footprint render normally in the
    /// cell-emit pass — anything the caller painted underneath remains visible wherever the
    /// overlay doesn't cover.
    /// </para>
    /// <para>
    /// <b>Removal.</b> The overlay's pixels persist on the terminal's overlay plane after the
    /// fragment registration is gone. <see cref="IBufferFragment.EmitErase"/> must emit the
    /// protocol-specific delete command (Kitty graphics <c>a=d</c>, …).
    /// </para>
    /// </summary>
    Overlay = 1,
}
