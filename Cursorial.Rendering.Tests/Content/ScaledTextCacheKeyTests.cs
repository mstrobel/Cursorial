using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Content;

/// <summary>
/// <see cref="ScaledText"/>'s two style-keyed caches: the OSC 66 fragment (style is the SGR
/// backdrop baked into the emission) and the realized FIGlet placeholder (style rides the
/// glyph runs). Both compare the RESOLVED style — see the CACHE-KEY census in the product
/// sources — and each cache has one test per failure direction: a missed
/// rebuild shows stale ink, and a spurious rebuild churns instances, which for a fragment
/// means RemoveFragment marks the footprint dirty and flips the renderer into
/// dirty-region-only mode.
/// </summary>
public class ScaledTextCacheKeyTests
{
    private static OutputCapabilities WithTextSizing()
        => OutputCapabilities.None with
           {
               TextSizing = new TextSizingCapabilities(Width: true, Scale: true)
           };

    private static readonly CellStyle Selected = CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 128));

    // ---- Fragment path (OSC 66 supported) ----

    [Fact]
    public void Fragment_RepaintWithUnchangedStyle_ReusesTheInstance()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2));
        var buffer = new CellBuffer(40, 6);

        content.Paint(buffer, 0, 0, Selected, WithTextSizing());
        var first = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);

        content.Paint(buffer, 0, 0, Selected, WithTextSizing());
        var second = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);

        // Same instance, not merely an equal one: the renderer's diff is reference-keyed, so a
        // fresh-but-equal fragment would re-transmit an unchanged payload.
        Assert.Same(first, second);
    }

    [Fact]
    public void Fragment_StyleChangeAtUnchangedSize_RecreatesWithTheNewBackdrop()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2));
        var buffer = new CellBuffer(40, 6);

        content.Paint(buffer, 0, 0, CellStyle.Default, WithTextSizing());
        var first = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);

        // Same size, different style — the size check alone would call the cache fresh, but the
        // style is baked into the emission as the OSC 66 SGR backdrop, so a selection highlight
        // arriving at an unchanged size must rebuild.
        content.Paint(buffer, 0, 0, Selected, WithTextSizing());
        var second = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);

        Assert.NotSame(first, second);
        Assert.Equal(CellStyle.Default, first.Style);
        Assert.Equal(Selected, second.Style);
    }

    // ---- Placeholder path (OSC 66 unsupported) ----

    [Fact]
    public void Placeholder_RepaintWithUnchangedStyle_ReusesTheRealization()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2), FigletFonts.Mini);
        var buffer = new CellBuffer(40, 8);

        content.Paint(buffer, 0, 0, Selected, OutputCapabilities.None);
        var first = content.RealizedPlaceholder;

        content.Paint(buffer, 0, 0, Selected, OutputCapabilities.None);

        Assert.NotNull(first);
        Assert.Same(first, content.RealizedPlaceholder);
    }

    [Fact]
    public void Placeholder_StyleChange_RebuildsAndTheBackdropReachesTheCells()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2), FigletFonts.Mini);

        var plainBuffer = new CellBuffer(40, 8);
        content.Paint(plainBuffer, 0, 0, CellStyle.Default, OutputCapabilities.None);
        var first = content.RealizedPlaceholder;

        var selectedBuffer = new CellBuffer(40, 8);
        content.Paint(selectedBuffer, 0, 0, Selected, OutputCapabilities.None);

        // The realization bakes the style into its glyph runs, so the arriving selection style
        // must rebuild it — the old ??= cached the first style forever, which is why figlet
        // selection highlighting never showed.
        Assert.NotNull(first);
        Assert.NotSame(first, content.RealizedPlaceholder);

        // And the backdrop must reach the cells: a background style fills the piece rect first,
        // because a highlight that only tints sparse glyph ink is unreadable.
        Assert.Equal(Color.FromRgb(0, 0, 128), selectedBuffer[0, 0].Style.Background);
    }
}
