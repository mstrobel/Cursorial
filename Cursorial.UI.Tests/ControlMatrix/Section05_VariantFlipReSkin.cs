using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §5 — the R2 palette-spine: a BuiltIn-themed control consumes the per-variant theme palette through
/// the control theme's <see cref="ResourceReference"/> setters, so a <c>RequestedThemeBase</c> /
/// <c>RequestedColorTier</c> flip re-resolves the palette AND re-renders the affected control zones
/// (the bug "cycling the theme has no visible effect"). Rows C113–C119.
///
/// Cell-faithful model (design doc §11.8a): controls are FILL-BOUNDED, not line-bounded — there is no
/// resting border to inspect. The button family's resting spine is Foreground = <c>TextBrush</c> on a
/// Background = <c>SurfaceBrush</c> fill; <c>:focus</c> is REVERSE-VIDEO (Background = <c>TextBrush</c>,
/// Foreground = <c>WindowBackground</c>). A root-level Button is not auto-focused by
/// <see cref="UITestHost.ShowRoot"/>, so it renders the resting spine directly; the focus look is reached
/// by an explicit <see cref="UIElement.Focus"/>.
/// </summary>
public sealed class Section05_VariantFlipReSkin
{
    private static UITestHost TruecolorHost()
        => UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.KittyTruecolor });

    // The cell-faithful BuiltIn palette ink/fill the rows pin (design doc §11.8a; Tokyo Night). Resting
    // spine: Foreground = TextBrush, Background = SurfaceBrush. Focus = reverse-video (Background = TextBrush,
    // Foreground = WindowBackground). No border (controls are fill-bounded).
    private static readonly Color DarkText = Color.FromRgb(0xC0, 0xCA, 0xF5);     // (Dark,Ansi256)  TextBrush
    private static readonly Color LightText = Color.FromRgb(0x34, 0x3B, 0x58);    // (Light,Ansi256) TextBrush
    private static readonly Color DarkFill = Color.FromRgb(0x24, 0x28, 0x3B);     // (Dark,Ansi256)  SurfaceBrush
    private static readonly Color LightFill = Color.FromRgb(0xCB, 0xCC, 0xD1);    // (Light,Ansi256) SurfaceBrush
    private static readonly Color DarkWindowBg = Color.FromRgb(0x0D, 0x0F, 0x18); // (Dark,Ansi256)  WindowBackground (focus ink)

    // ───────────────────────────── C113 — the dark/light flip changes a default-themed control's cells ─────────────────────────────

    [Fact] // C113 — the headline bug: a base flip visibly re-skins a BuiltIn control with no explicit colors.
    public void C113_BaseFlip_ReSkinsDefaultThemedButton_CellsChange()
    {
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        // A default-themed Button — NO explicit Background/Foreground. Its look must come from the BuiltIn
        // control theme's per-variant palette spine (the R2 ResourceReference setters). A root-level button
        // is not auto-focused, so it renders the resting spine.
        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // The fill-bounded template rendered: the "OK" content sits on the TOP row (no border row above it).
        Assert.Contains("OK", host.GetRowText(0));

        // The dark variant resolved the dark palette: the content ink is the dark TextBrush, the fill is the
        // dark SurfaceBrush (the cell-faithful spine — there is no border to inspect).
        var darkInk = FindForeground(host, "OK");
        var darkFill = FillColor(host, "OK");
        Assert.Equal(DarkText, darkInk);
        Assert.Equal(DarkFill, darkFill);

        // FLIP to Light — the bug repro. The variant flips, the palette re-resolves, and the control's cells
        // MUST change (this failed before the R2 spine: the control theme wired constant brushes and never
        // read the palette, so the dark ink/fill survived the flip).
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();

        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);

        var lightInk = FindForeground(host, "OK");
        var lightFill = FillColor(host, "OK");
        Assert.Equal(LightText, lightInk);
        Assert.NotEqual(darkInk, lightInk);                 // the ink visibly changed
        Assert.Equal(LightFill, lightFill);
        Assert.NotEqual(darkFill, lightFill);               // the fill visibly changed

        // And back to Dark restores the dark palette (the producer re-resolves both directions).
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunFrame();
        Assert.Equal(DarkText, FindForeground(host, "OK"));
        Assert.Equal(DarkFill, FillColor(host, "OK"));
    }

    // ───────────────────────────── C114 — only the affected zone re-rasters (invariant 3) ─────────────────────────────

    [Fact] // C114 — a base flip re-rasters the themed control's zone, not an unrelated sibling boundary.
    public void C114_BaseFlip_ReRastersOnlyThemedZone_SiblingFrozen()
    {
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var root = new StackPanel();
        var button = new Button { Content = "OK", IsRenderBoundary = true };
        // A sibling whose look is fully explicit — it reads no theme palette, so a variant flip must NOT
        // re-raster it (invariant 3: only zones whose values changed re-raster).
        var sibling = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            IsRenderBoundary = true,
            Width = 8,
            Height = 1,
        };
        root.Children.Add(button);
        root.Children.Add(sibling);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.RenderSystem!.Tree!;
        var buttonScene = tree.GetScene(button)!;
        var siblingScene = tree.GetScene(sibling)!;
        var buttonVersion = buttonScene.RasterVersion;
        var siblingVersion = siblingScene.RasterVersion;

        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();

        Assert.True(buttonScene.RasterVersion > buttonVersion);   // the themed zone re-rastered
        Assert.Equal(siblingVersion, siblingScene.RasterVersion); // the explicit sibling stayed frozen
    }

    // ───────────────────────────── C115 — an explicit local value wins over the theme palette ─────────────────────────────

    [Fact] // C115 — the theme palette arms BELOW LocalValue: an explicit Foreground wins and is variant-immune.
    public void C115_ExplicitForeground_WinsOverThemePalette_AndSurvivesFlip()
    {
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var explicitInk = Color.FromRgb(0x12, 0x88, 0xEE);
        var button = new Button
        {
            Content = "OK",
            Foreground = new SolidColorBrush(explicitInk), // an explicit LocalValue
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(explicitInk, FindForeground(host, "OK")); // the explicit value wins over the dark palette

        // A variant flip does not disturb the explicit value (it sits above the theme palette producer).
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();
        Assert.Equal(explicitInk, FindForeground(host, "OK"));
    }

    // ───────────────────────────── C116 — overriding a palette KEY re-skins the control (the R2 contract) ─────────────────────────────

    [Fact] // C116 — shadowing ThemeKeys.TextBrush at app scope re-points the control theme's ink (zero template work).
    public void C116_PaletteKeyShadow_ReSkinsBuiltInControl()
    {
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // The resting ink is the dark TextBrush (a root-level button is not auto-focused).
        Assert.Equal(DarkText, FindForeground(host, "OK"));

        // Shadow the palette key at app scope — the control theme's ResourceReference setter re-resolves to
        // the override and the control re-skins with zero template work (the R2 promise).
        var custom = Color.FromRgb(0xCC, 0x33, 0x99);
        host.Application.Resources[ThemeKeys.TextBrush] = new SolidColorBrush(custom);
        host.RunFrame();
        Assert.Equal(custom, FindForeground(host, "OK"));
    }

    // ───────────────────────────── C117 — the honest tier-flip expectation ─────────────────────────────

    [Fact] // C117 — a tier flip re-resolves the palette to a different-KIND brush even on a fixed terminal.
    public void C117_TierFlip_ReResolvesPaletteKind_OnFixedTruecolorTerminal()
    {
        // The honest tier story (design doc §11.2/CD8): RequestedColorTier re-points the EFFECTIVE tier,
        // so a default-themed control's palette ResourceReference re-resolves to that tier's entry — RGB
        // at Ansi256 (served at Truecolor by descent), a hand-picked PALETTE index at Ansi16. The control
        // re-skins in the cell buffer regardless of the negotiated terminal depth; the FrameRenderer then
        // quantizes to the NEGOTIATED depth at emit time, so on this truecolor terminal both an RGB and a
        // palette-index foreground reach the wire verbatim — a real, observable difference.
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var button = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // Default (negotiated truecolor): the (Dark,Ansi256) RGB TextBrush serves via descent.
        Assert.Equal(ColorKind.Rgb, FindForeground(host, "OK").Kind);
        Assert.Equal(DarkText, FindForeground(host, "OK"));

        // Flip the effective tier to Ansi16: the palette re-resolves to the hand-picked palette index.
        host.Application.RequestedColorTier = ColorDepth.Ansi16;
        host.RunFrame();
        Assert.Equal(ColorDepth.Ansi16, host.Application.ActualThemeVariant.Tier);
        var ansi16Ink = FindForeground(host, "OK");
        Assert.Equal(ColorKind.Palette, ansi16Ink.Kind);       // a palette index, not RGB — the KIND changed
        Assert.Equal(Color.FromPalette(15), ansi16Ink);        // the (Dark,Ansi16) hand-picked TextBrush = Palette(15)

        // Clear the override → back to the negotiated truecolor tier → RGB again.
        host.Application.RequestedColorTier = null;
        host.RunFrame();
        Assert.Equal(ColorKind.Rgb, FindForeground(host, "OK").Kind);
    }

    // ───────────────────────────── C118 — the honest tier flip reaches the WIRE on a fixed truecolor terminal ─────────────────────────────

    [Fact] // C118 — the tier flip re-rasters AND emits a different foreground SGR on the wire (not just a buffer value).
    public void C118_TierFlip_ReRastersAndEmitsDifferentForegroundSgr_OnFixedTruecolorTerminal()
    {
        // The "honest tier" caveat made airtight (candidate (d)): a tier flip on a FIXED truecolor
        // terminal is genuinely observable — the FrameRenderer quantizes to the negotiated depth, but on
        // truecolor the (Dark,Ansi256) RGB foreground emits a 24-bit SGR while the (Dark,Ansi16) hand-
        // picked palette index emits a palette SGR. The two byte streams differ, so the pixels change.
        // Each asserted frame is a flip-INDUCED re-emit (the resting frame is emitted during RunUntilIdle,
        // so a steady-state frame would be empty — the flips force the relevant cells back onto the wire).
        using var host = UITestHost.Create(new UITestHostOptions
        {
            Capabilities = TestCapabilities.KittyTruecolor,
            CaptureFrameBytes = true,
        });
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var button = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        var rgbScene = host.Application.RenderSystem!.Tree!.GetScene(button)!;
        var rgbVersion = rgbScene.RasterVersion;

        // Flip the effective tier to Ansi16 → the palette re-resolves the TextBrush to a hand-picked palette
        // index. The themed zone re-rasters and the frame emits a PALETTE foreground SGR — never the 24-bit
        // RGB SGR (the (Dark,Ansi16) TextBrush is Color.FromPalette(15)).
        host.Application.RequestedColorTier = ColorDepth.Ansi16;
        host.RunFrame();
        Assert.True(rgbScene.RasterVersion > rgbVersion);
        Assert.False(Contains(host.LastFrameBytes, "38;2;192;202;245"u8), "the dark RGB foreground SGR was emitted on an Ansi16 tier");
        var ansi16Version = rgbScene.RasterVersion;

        // Flip back to the negotiated truecolor tier → the TextBrush re-resolves to RGB and the frame emits
        // the 24-bit foreground SGR again. The two byte streams genuinely differ on a fixed truecolor
        // terminal — the tier flip reached the pixels, not just the resource value.
        host.Application.RequestedColorTier = null;
        host.RunFrame();
        Assert.True(rgbScene.RasterVersion > ansi16Version);
        Assert.True(Contains(host.LastFrameBytes, "38;2;192;202;245"u8), "the dark RGB foreground SGR was not re-emitted on the truecolor wire");
    }

    // ───────────────────────────── C119 — :focus is palette reverse-video, not terminal-default (P2-1) ─────────────────────────────

    [Fact] // C119 — focusing a default-themed button flips it to the palette reverse-video :focus look (not terminal-default).
    public void C119_Focus_RendersReverseVideo_PaletteDerived_NotTerminalDefault()
    {
        // Cell-faithful focus (design doc §11.8a): a button has no FocusPen ring — focus is REVERSE-VIDEO,
        // a brush-pair flip into the palette spine (Background = TextBrush, Foreground = WindowBackground).
        // The focus cue is therefore palette-derived on BOTH axes, never Color.Default (the old P2-1
        // regression where a brush-less Pens.Heavy stranded the border at terminal-default).
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // Resting (a root-level button is not auto-focused): TextBrush ink on a SurfaceBrush fill.
        Assert.False(button.IsFocused);
        Assert.Equal(DarkText, FindForeground(host, "OK"));
        Assert.Equal(DarkFill, FillColor(host, "OK"));

        // FOCUS the button → the :focus rule arms and flips to reverse-video: the "OK" ink becomes the dark
        // WindowBackground and the fill becomes the dark TextBrush. Both are palette-derived (not Default)
        // and visibly distinct from the resting spine. This assertion failed before the cell-faithful model
        // (the constant Pens.Heavy stranded a null brush → Color.Default reached the wire on focus).
        Assert.True(button.Focus(FocusNavigationMethod.Tab));
        host.RunFrame();
        Assert.True(button.IsFocused);

        var focusedInk = FindForeground(host, "OK");
        var focusedFill = FillColor(host, "OK");
        Assert.Equal(DarkWindowBg, focusedInk);          // reverse-video ink = WindowBackground
        Assert.Equal(DarkText, focusedFill);             // reverse-video fill = TextBrush
        Assert.NotEqual(Color.Default, focusedInk);      // NOT terminal-default (the P2-1 regression)
        Assert.NotEqual(Color.Default, focusedFill);
        Assert.NotEqual(DarkText, focusedInk);           // distinct from the resting ink
        Assert.NotEqual(DarkFill, focusedFill);          // distinct from the resting fill

        // Blurring restores the resting spine (the :focus rule retracts).
        host.Application.FocusManager.ClearFocus();
        host.RunFrame();
        Assert.Equal(DarkText, FindForeground(host, "OK"));
        Assert.Equal(DarkFill, FillColor(host, "OK"));
    }

    // ───────────────────────────── finders ─────────────────────────────

    // Whether `haystack` contains the contiguous byte subsequence `needle` (wire-SGR search).
    private static bool Contains(ReadOnlyMemory<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.Span.IndexOf(needle) >= 0;

    // The foreground of the cell carrying the first char of text (the content ink).
    private static Color FindForeground(UITestHost host, string text)
        => CellOf(host, text).Style.Foreground;

    // The background fill of the cell carrying the first char of text (the cell-faithful spine has no border;
    // a control's extent is its fill, so the content cell's background IS the resting/state fill).
    private static Color FillColor(UITestHost host, string text)
        => CellOf(host, text).Style.Background;

    private static Cell CellOf(UITestHost host, string text)
    {
        var first = text[0];
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
        {
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme is { Length: > 0 } g && g[0] == first)
                    return cell;
            }
        }

        return default;
    }
}
