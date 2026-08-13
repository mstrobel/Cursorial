using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Terminal;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// Task 20 scope A: the compositor substitutes the terminal's reported RGB defaults for
/// <see cref="Color.Default"/> at its arithmetic inputs, so a translucent / faded overlay composites against
/// the real default instead of taking <see cref="Color.Composite"/>'s opaque-by-kind short-circuit. Each fact
/// is a rung of the scoping's what-breaks table; the <c>NoReportedDefault_*</c> facts are the seam's
/// status-quo proof — <see cref="TerminalColorDefaults.None"/> reproduces the pre-substitution behaviour.
/// </summary>
public class CompositeTerminalDefaultsTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color FgDefault = Color.FromRgb(205, 214, 244);   // the headless truecolor preset
    private static readonly Color BgDefault = Color.FromRgb(30, 30, 46);
    private static readonly Color Veil = Color.FromRgba(255, 0, 0, 128);      // a translucent red overlay

    // Build the seam value the WindowManager screen path hands the compositor — through the REAL production
    // constructor (FromCapabilities), so the test exercises the reported-defaults path, not a test-only shim.
    private static TerminalColorDefaults Reported(Color fg, Color bg) =>
        TerminalColorDefaults.FromCapabilities(
            TerminalCapabilities.None with
            {
                Output = OutputCapabilities.None with
                {
                    Color = ColorCapabilities.None with
                    {
                        Depth = ColorDepth.Truecolor,
                        DefaultForeground = fg,
                        DefaultBackground = bg
                    }
                }
            });

    private static void Fill(Scene scene, IBrush brush) => scene.Draw(ctx => ctx.FillRectangle(scene.Bounds, brush));

    // ---- Rung (a): a translucent veil over a Default background blends, not solid ----

    [Fact]
    public void TranslucentVeil_OverADefaultBackgroundCell_BlendsAgainstTheReportedDefault_NotSolid()
    {
        var scene = Scene.Create(4, 1);
        Fill(scene, new SolidColorBrush(Veil));                 // background-only (glyphless) translucent veil
        var layers = new[] { new SceneLayer(scene) };

        // Base = CellStyle.Default → the destination cell's background is Color.Default (the bug's precondition).
        var target = new CellBuffer(4, 1);
        new SceneCompositor(CellStyle.Default, Reported(FgDefault, BgDefault)).Composite(layers, target.AsView());

        // Blends against the reported default instead of resolving opaque (the mottled-scrim fix, inverted).
        Assert.Equal(Color.Composite(Veil, BgDefault, BlendingModes.Default), target[0, 0].Style.Background);
        Assert.NotEqual(Veil.WithAlpha(255), target[0, 0].Style.Background);
    }

    [Fact]
    public void NoReportedDefault_TranslucentVeilOverDefault_ResolvesOpaque_ByteIdenticalToHead()
    {
        var scene = Scene.Create(4, 1);
        Fill(scene, new SolidColorBrush(Veil));
        var layers = new[] { new SceneLayer(scene) };

        // No reported defaults ⇒ TerminalColorDefaults.None ⇒ every substitution is a no-op ⇒ the pre-change
        // punch (translucent over Default resolves opaque) is preserved. This is the seam's status-quo proof.
        var withNone = new CellBuffer(4, 1);
        new SceneCompositor(CellStyle.Default, TerminalColorDefaults.None).Composite(layers, withNone.AsView());
        Assert.Equal(Veil.WithAlpha(255), withNone[0, 0].Style.Background);   // HEAD's behaviour, unchanged

        // And byte-identical to the pre-change call shape (the constructor with no defaults argument at all).
        var withDefaultCtor = new CellBuffer(4, 1);
        scene.Invalidate();
        Fill(scene, new SolidColorBrush(Veil));
        new SceneCompositor(CellStyle.Default).Composite(layers, withDefaultCtor.AsView());
        Assert.Equal(withDefaultCtor[0, 0].Style, withNone[0, 0].Style);
    }

    // ---- Rung (b): Default-foreground text under a veil ghosts, not vanishes ----

    [Fact]
    public void DefaultForegroundText_UnderATranslucentVeil_Ghosts_InsteadOfVanishing()
    {
        // The destination is a glyph in the terminal's own default colours (fg == Default AND bg == Default).
        var backdrop = new CellBuffer(4, 1);
        backdrop[0, 0] = new Cell("A", CellKind.Single, CellStyle.Default);

        var veil = Scene.Create(4, 1);
        Fill(veil, new SolidColorBrush(Veil));                  // glyphless translucent veil over the glyph

        var target = new CellBuffer(4, 1);
        new SceneCompositor(backdrop, Reported(FgDefault, BgDefault)).Composite([new SceneLayer(veil)], target.AsView());

        var style = target[0, 0].Style;
        Assert.Equal("A", target[0, 0].Grapheme);                                        // glyph kept
        Assert.Equal(Color.Composite(Veil, FgDefault, BlendingModes.Default), style.Foreground);  // fg ghosts...
        Assert.Equal(Color.Composite(Veil, BgDefault, BlendingModes.Default), style.Background);  // ...over the bg
        Assert.NotEqual(style.Foreground, style.Background);                             // so the text does NOT vanish
    }

    [Fact]
    public void NoReportedDefault_DefaultForegroundTextUnderAVeil_Vanishes_ByteIdenticalToHead()
    {
        var backdrop = new CellBuffer(4, 1);
        backdrop[0, 0] = new Cell("A", CellKind.Single, CellStyle.Default);

        var veil = Scene.Create(4, 1);
        Fill(veil, new SolidColorBrush(Veil));

        var target = new CellBuffer(4, 1);
        new SceneCompositor(backdrop, TerminalColorDefaults.None).Composite([new SceneLayer(veil)], target.AsView());

        var style = target[0, 0].Style;
        // HEAD: the tint lands on Default, resolves opaque, and fg == bg — the text vanishes. Status quo.
        Assert.Equal(Veil.WithAlpha(255), style.Foreground);
        Assert.Equal(Veil.WithAlpha(255), style.Background);
        Assert.Equal(style.Foreground, style.Background);
    }

    // ---- Rung (c): a layer-opacity fade fades Default-kind cells ----

    [Fact]
    public void LayerOpacityFade_OverDefaultStyledCells_FadesThem_InsteadOfHoldingFullStrength()
    {
        var source = Scene.Create(2, 1);
        Fill(source, Brushes.Default);                          // Default-styled cells (fg == bg == Default)
        var layers = new[] { new SceneLayer(source, new CompositeParameters(opacity: 128)) };

        // Base = opaque Blue so the fade has something to fade toward.
        var faded = new CellBuffer(2, 1);
        new SceneCompositor(CellStyle.Default.WithBackground(Blue), Reported(FgDefault, BgDefault))
            .Composite(layers, faded.AsView());
        Assert.Equal(Color.Composite(BgDefault.WithAlpha(128), Blue, BlendingModes.Default), faded[0, 0].Style.Background);

        // None defaults ⇒ ScaleAlpha leaves the non-RGB Default untouched, Default wins opaque, no fade — HEAD.
        var unfaded = new CellBuffer(2, 1);
        source.Invalidate();
        Fill(source, Brushes.Default);
        new SceneCompositor(CellStyle.Default.WithBackground(Blue)).Composite(layers, unfaded.AsView());
        Assert.Equal(Color.Default, unfaded[0, 0].Style.Background);
        Assert.NotEqual(faded[0, 0].Style.Background, unfaded[0, 0].Style.Background);
    }

    // ---- A1 discipline: substitution fires ONLY where the source actually blends ----

    [Fact]
    public void ATransparentVeil_OverADefaultCell_LeavesItDefaultKind_PreservingLiveTracking()
    {
        // Scope A is A1: substitute only where blending needs it. A fully transparent source contributes
        // nothing, so the Default backdrop must survive as Default-KIND — it keeps live theme-tracking and its
        // SGR 39/49 emission — rather than being eagerly rewritten to the reported RGB default (which A2 would).
        var backdrop = new CellBuffer(2, 1);
        backdrop[0, 0] = new Cell("A", CellKind.Single, CellStyle.Default);

        var transparent = Scene.Create(2, 1);
        Fill(transparent, new SolidColorBrush(Color.FromRgba(255, 0, 0, 0)));   // alpha 0

        var target = new CellBuffer(2, 1);
        new SceneCompositor(backdrop, Reported(FgDefault, BgDefault)).Composite([new SceneLayer(transparent)], target.AsView());

        Assert.Equal(Color.Default, target[0, 0].Style.Background);
        Assert.Equal(Color.Default, target[0, 0].Style.Foreground);
    }
}
