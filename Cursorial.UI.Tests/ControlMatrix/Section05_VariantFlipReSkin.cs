using Cursorial.Drawing.Media;
using Cursorial.Output;
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
/// (the bug "cycling the theme has no visible effect"). Rows C113–C116.
/// </summary>
public sealed class Section05_VariantFlipReSkin
{
    private static UITestHost TruecolorHost()
        => UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.KittyTruecolor });

    // The BuiltIn (Dark/Ansi256) vs (Light/Ansi256) palette ink the test pins.
    private static readonly Color DarkText = Color.FromRgb(0xE0, 0xE0, 0xE0);
    private static readonly Color LightText = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color DarkBorder = Color.FromRgb(0x55, 0x55, 0x55);
    private static readonly Color LightBorder = Color.FromRgb(0xAA, 0xAA, 0xAA);

    // ───────────────────────────── C113 — the dark/light flip changes a default-themed control's cells ─────────────────────────────

    [Fact] // C113 — the headline bug: a base flip visibly re-skins a BuiltIn control with no explicit colors.
    public void C113_BaseFlip_ReSkinsDefaultThemedButton_CellsChange()
    {
        using var host = TruecolorHost();
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        // A default-themed Button — NO explicit Background/Foreground/BorderPen. Its look must come from
        // the BuiltIn control theme's per-variant palette (the R2 ResourceReference setters).
        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // The themed template rendered: a bordered face with the "OK" content on the inner row.
        Assert.Contains("OK", host.GetRowText(1));

        // The dark variant resolved the dark palette: the content ink is the dark TextBrush, the frame
        // is the dark BorderPen.
        var darkInk = FindForeground(host, "OK");
        Assert.Equal(DarkText, darkInk);
        Assert.Equal(DarkBorder, BorderColor(host));

        // FLIP to Light — the bug repro. The variant flips, the palette re-resolves, and the control's
        // cells MUST change (this assertion fails before the fix: the control theme wired constant pens
        // and never read the palette, so the dark ink/border survived the flip).
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();

        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);

        var lightInk = FindForeground(host, "OK");
        Assert.Equal(LightText, lightInk);
        Assert.NotEqual(darkInk, lightInk);                 // the ink visibly changed
        Assert.Equal(LightBorder, BorderColor(host));
        Assert.NotEqual(DarkBorder, BorderColor(host));     // the border visibly changed

        // And back to Dark restores the dark palette (the producer re-resolves both directions).
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunFrame();
        Assert.Equal(DarkText, FindForeground(host, "OK"));
        Assert.Equal(DarkBorder, BorderColor(host));
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
        Assert.Equal(DarkText, FindForeground(host, "OK"));

        // Shadow the palette key at app scope — the control theme's ResourceReference setter re-resolves
        // to the override and the control re-skins with zero template work (the R2 promise).
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
        Assert.Equal(Color.FromPalette(7), ansi16Ink);         // the (Dark,Ansi16) hand-picked TextBrush

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

        // The negotiated-truecolor frame emits the dark RGB foreground SGR (38;2;224;224;224 — 24-bit).
        Assert.True(Contains(host.LastFrameBytes, "38;2;224;224;224"u8), "the dark RGB foreground SGR was not emitted on the truecolor wire");

        // Flip the effective tier to Ansi16 → the palette re-resolves to a hand-picked palette index.
        host.Application.RequestedColorTier = ColorDepth.Ansi16;
        host.RunFrame();

        // The themed zone re-rastered (the flip reached the pixels, not just the resource value).
        Assert.True(rgbScene.RasterVersion > rgbVersion);

        // And the wire no longer carries the 24-bit RGB ink for this content — the foreground SGR
        // genuinely changed on the wire (the (Dark,Ansi16) TextBrush is Color.FromPalette(7), emitted as
        // a palette SGR, never as 38;2;224;224;224). The pixels differ on a fixed truecolor terminal.
        Assert.False(Contains(host.LastFrameBytes, "38;2;224;224;224"u8), "the dark RGB foreground SGR survived a tier flip to Ansi16");
    }

    // ───────────────────────────── C119 — the :focus border carries the palette ACCENT, not terminal-default (P2-1) ─────────────────────────────

    [Fact] // C119 — focusing a default-themed button re-skins the frame to the palette FocusPen (accent + heavy), not Default.
    public void C119_Focus_BorderResolvesAccentColor_NotTerminalDefault()
    {
        // P6.1 P2-1 fix: the :focus escalation was the brush-less Pens.Heavy constant, so focusing a
        // default-themed button bumped the border weight but stranded the brush → Colors.Default at draw
        // (the frame went from palette gray to TERMINAL-DEFAULT on focus). The fix wires :focus to the
        // already-populated ThemeKeys.FocusPen DynamicResource (accent color + heavy weight), so the
        // focus frame is the palette accent — a colored cue, not a color regression.
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

        // Resting (unfocused): the frame is the dark resting BorderPen palette gray.
        Assert.False(button.IsFocused);
        Assert.Equal(DarkBorder, BorderColor(host));

        // FOCUS the button → the :focus rule arms and re-resolves BorderPen to ThemeKeys.FocusPen. The
        // rendered frame is the dark ACCENT color (0x66,0xD9,0xEF), NOT Color.Default and NOT the resting
        // gray. This assertion fails before the fix (the constant Pens.Heavy stranded a null brush →
        // Color.Default reached the wire).
        Assert.True(button.Focus(FocusNavigationMethod.Tab));
        host.RunFrame();
        Assert.True(button.IsFocused);

        var accent = Color.FromRgb(0x66, 0xD9, 0xEF);
        var focusedBorder = BorderColor(host);
        Assert.Equal(accent, focusedBorder);                 // the palette accent (the FocusPen brush)
        Assert.NotEqual(Color.Default, focusedBorder);       // NOT terminal-default (the P2-1 regression)
        Assert.NotEqual(DarkBorder, focusedBorder);          // and visibly distinct from the resting gray

        // Blurring restores the resting palette gray (the :focus rule retracts).
        host.Application.FocusManager.ClearFocus();
        host.RunFrame();
        Assert.Equal(DarkBorder, BorderColor(host));
    }

    // ───────────────────────────── finders ─────────────────────────────

    // Whether `haystack` contains the contiguous byte subsequence `needle` (wire-SGR search).
    private static bool Contains(ReadOnlyMemory<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.Span.IndexOf(needle) >= 0;

    // The foreground of the cell carrying the first char of text (the content ink).
    private static Color FindForeground(UITestHost host, string text)
    {
        var first = text[0];
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
        {
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme is { Length: > 0 } g && g[0] == first)
                    return cell.Style.Foreground;
            }
        }

        return Color.Default;
    }

    // The foreground color of a box-drawing border cell (top-left corner of the themed Button frame).
    private static Color BorderColor(UITestHost host)
    {
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
        {
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme is { Length: > 0 } g && IsBoxDrawing(g[0]))
                    return cell.Style.Foreground;
            }
        }

        return Color.Default;
    }

    private static bool IsBoxDrawing(char ch) => ch is >= '─' and <= '╿';
}
