using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// A surface's blank style is part of its contract: code that blanks or clips cells itself has to be
/// able to ask, instead of assuming <see cref="CellStyle.Default"/>. The assumption is wrong on any
/// buffer built against a terminal that reported its own default colors — and <see cref="CellStyle.Default"/>
/// is opaque, so on a translucent surface it also occludes.
/// </summary>
public class CellSurfaceDefaultStyleTests
{
    private static CellBuffer BufferWithBaseStyle(Color foreground, Color background)
        => new(6, 2, TerminalCapabilities.None with
                     {
                         Output = OutputCapabilities.None with
                                  {
                                      Color = ColorCapabilities.None with
                                              {
                                                  DefaultForeground = foreground,
                                                  DefaultBackground = background
                                              }
                                  }
                     });

    [Fact]
    public void CellBuffer_DefaultStyle_IsStyleDefault_WhenTheTerminalReportedNothing()
    {
        Assert.Equal(CellStyle.Default, new CellBuffer(6, 2).DefaultStyle);
    }

    [Fact]
    public void CellBuffer_DefaultStyle_IsWhatClearActuallyWrites()
    {
        var buffer = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 40));

        buffer.Set(0, 0, "a", CellStyle.Default.WithBackground(Color.FromRgb(200, 0, 0)));
        buffer.Clear();

        Assert.NotEqual(CellStyle.Default, buffer.DefaultStyle);
        Assert.Equal(buffer.DefaultStyle, buffer[0, 0].Style);
    }

    [Fact]
    public void CellBuffer_DefaultStyle_IsWhatClearCellsActuallyWrites()
    {
        var buffer = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 40));

        buffer.Set(2, 1, "a", CellStyle.Default.WithBackground(Color.FromRgb(200, 0, 0)));
        buffer.ClearCells(new Rect(2, 1, 1, 1));

        Assert.Equal(buffer.DefaultStyle, buffer[2, 1].Style);
    }

    [Fact]
    public void CellBufferView_DefaultStyle_DelegatesToTheBackingBuffer()
    {
        var buffer = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 40));

        Assert.Equal(buffer.DefaultStyle, buffer.View(1, 0, 3, 2).DefaultStyle);
        Assert.Equal(buffer.DefaultStyle, buffer.AsView().DefaultStyle);
    }

    [Fact]
    public void CellBufferView_DefaultStyle_OnDefaultConstructedView_IsStyleDefault()
    {
        // Every other member of a default-constructed view is a safe no-op rather than a throw;
        // asking one for its blank style must be too.
        Assert.Equal(CellStyle.Default, default(CellBufferView).DefaultStyle);
    }

    // Blank-aware drawing code reaches the property through the generic constraint — the supported
    // way to write against both surfaces without boxing the view.
    private static CellStyle BlankStyleOf<TSurface>(TSurface surface) where TSurface : ICellSurface
        => surface.DefaultStyle;

    [Fact]
    public void DefaultStyle_IsReachableThroughTheGenericConstraint_ForBothSurfaces()
    {
        var buffer = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 40));

        Assert.Equal(buffer.DefaultStyle, BlankStyleOf(buffer));
        Assert.Equal(buffer.DefaultStyle, BlankStyleOf(buffer.View(1, 0, 3, 2)));
    }

    // ---- The blank style as a constructor argument ----------------------------------------

    [Fact]
    public void ConstructorDefaultStyle_IsTheBlankAndTheContents_FromConstruction()
    {
        // The whole reason this is a constructor argument and not an init setter: an init setter runs
        // after the constructor body, so the grid would be filled with the derived blank and only then
        // declare itself transparent. Assert the contents, not just the property.
        var buffer = new CellBuffer(4, 2, defaultStyle: CellStyle.Transparent);

        Assert.Equal(CellStyle.Transparent, buffer.DefaultStyle);

        for (int row = 0; row < buffer.Rows; row++)
        for (int col = 0; col < buffer.Columns; col++)
            Assert.Equal(Cell.Blank with { Style = CellStyle.Transparent }, buffer[col, row]);
    }

    [Fact]
    public void ConstructorDefaultStyle_WinsOverTheCapabilitiesDerivedBlank()
    {
        var capabilities = TerminalCapabilities.None with
                           {
                               Output = OutputCapabilities.None with
                                        {
                                            Color = ColorCapabilities.None with
                                                    {
                                                        DefaultForeground = Color.FromRgb(220, 220, 220),
                                                        DefaultBackground = Color.FromRgb(30, 30, 40)
                                                    }
                                        }
                           };

        var buffer = new CellBuffer(4, 2, capabilities, defaultStyle: CellStyle.Transparent);

        // Not merely "the property says transparent" — the grid must never have been filled opaque.
        Assert.Equal(CellStyle.Transparent, buffer.DefaultStyle);
        Assert.Equal(CellStyle.Transparent, buffer[0, 0].Style);
        Assert.Equal(CellStyle.Transparent, buffer[3, 1].Style);
    }

    [Fact]
    public void ConstructorDefaultStyle_Omitted_KeepsTheCapabilitiesDerivation()
    {
        var buffer = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 40));

        Assert.Equal(CellStyle.Default with
                     {
                         Foreground = Color.FromRgb(220, 220, 220),
                         Background = Color.FromRgb(30, 30, 40)
                     },
                     buffer.DefaultStyle);
        Assert.Equal(buffer.DefaultStyle, buffer[0, 0].Style);
    }

    [Fact]
    public void ConstructorDefaultStyle_SurvivesClearAndResize()
    {
        var buffer = new CellBuffer(4, 2, defaultStyle: CellStyle.Transparent);

        buffer.Set(0, 0, "a", CellStyle.Default.WithBackground(Color.FromRgb(200, 0, 0)));
        buffer.Clear();
        Assert.Equal(CellStyle.Transparent, buffer[0, 0].Style);

        buffer.Resize(6, 3);
        Assert.Equal(CellStyle.Transparent, buffer.DefaultStyle);
        Assert.Equal(CellStyle.Transparent, buffer[5, 2].Style);
    }

    [Fact]
    public void ConstructorDefaultStyle_IsWhatWideOrphanHygieneLeavesBehind()
    {
        // A transparent surface must never acquire an opaque hole. Pair hygiene keeps the pair's own
        // style, which on a cleared transparent buffer IS the transparent blank.
        var buffer = new CellBuffer(4, 1, defaultStyle: CellStyle.Transparent);

        buffer.Set(0, 0, "漢", CellStyle.Transparent);
        buffer.Set(1, 0, "x", CellStyle.Transparent);   // stomps the continuation, orphaning the left half

        Assert.Equal(CellStyle.Transparent, buffer[0, 0].Style);
        Assert.Null(buffer[0, 0].Grapheme);
    }

    // ---- The clear extensions' short-circuit ----------------------------------------------

    [Fact]
    public void ClearExtension_ShortCircuitIsExact_OnATransparentBlankBuffer()
    {
        var buffer = new CellBuffer(4, 2, defaultStyle: CellStyle.Transparent);

        // Clearing to the surface's own blank: the short-circuit fires, and the result is still the
        // blank (the old Style.Default test would have re-filled the whole grid for nothing).
        buffer.Clear(CellStyle.Transparent);
        Assert.Equal(CellStyle.Transparent, buffer[0, 0].Style);

        // Clearing to Style.Default on this surface means the OPAQUE default and must be honoured —
        // the old short-circuit fired here and silently left the surface transparent.
        buffer.Clear(CellStyle.Default);
        Assert.Equal(CellStyle.Default, buffer[0, 0].Style);
        Assert.Equal(CellStyle.Transparent, buffer.DefaultStyle);   // clearing does not re-base the blank
    }

    [Fact]
    public void ClearCellsExtension_ShortCircuitIsExact_OnATransparentBlankBuffer()
    {
        var buffer = new CellBuffer(4, 2, defaultStyle: CellStyle.Transparent);

        buffer.ClearCells(new Rect(1, 0, 2, 1), CellStyle.Default);

        Assert.Equal(CellStyle.Default, buffer[1, 0].Style);
        Assert.Equal(CellStyle.Default, buffer[2, 0].Style);
        Assert.Equal(CellStyle.Transparent, buffer[0, 0].Style);   // outside the rect
    }

    [Fact]
    public void ClearExtension_OnAView_AsksTheBackingBuffersBlank()
    {
        var buffer = new CellBuffer(6, 2, defaultStyle: CellStyle.Transparent);
        var view = buffer.View(1, 0, 3, 2);

        view.Clear(CellStyle.Default);

        Assert.Equal(CellStyle.Default, buffer[1, 0].Style);
        Assert.Equal(CellStyle.Transparent, buffer[0, 0].Style);   // outside the view
    }

    [Fact]
    public void ClearExtension_WithStyleDefault_OnACapabilitiesDerivedBuffer_HonoursTheArgument()
    {
        // BEHAVIOUR CHANGE, pinned deliberately. The old short-circuit fired on Style.Default whatever
        // the surface's blank was, so this call silently produced the RGB materialization of the
        // terminal default (which Clear() had just written) and the argument was ignored. Now the two
        // calls say different things: Clear() means "the surface's blank" — still the RGB form, still
        // the alpha-blendable one, and still what every internal blank path uses — while
        // Clear(Style.Default) means the literal default style the caller named.
        var buffer = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 40));

        buffer.Clear();
        Assert.NotEqual(CellStyle.Default, buffer[0, 0].Style);

        buffer.Clear(CellStyle.Default);
        Assert.Equal(CellStyle.Default, buffer[0, 0].Style);
    }

    [Fact]
    public void ClearExtension_KeepsABlanksGrapheme_TheStyleAloneIsNotTheWholeBlank()
    {
        // Cell.DurableEmpty carries NBSP — what makes an opaque fill non-occludable. Its style IS
        // default, so a style-only short-circuit dropped the grapheme on a default-blank buffer.
        var buffer = new CellBuffer(3, 1);

        buffer.Clear(Cell.DurableEmpty);

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, buffer[0, 0].Grapheme);
    }
}
