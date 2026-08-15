// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// The modal scrim (<c>Theme.ObscuredOverlayBrush</c> — <c>#080910</c> at <c>Opacity = 0.55</c>) must veil the
/// root band UNIFORMLY, whether or not the root painted a background under it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Color.Composite"/> only alpha-blends RGB over RGB: with a non-RGB backdrop it returns the source
/// at full opacity, discarding the source's alpha. <c>CellBuffer.DeriveDefaultStyle</c> exists to prevent
/// exactly that on the screen buffer — it promotes the terminal's reported default fg/bg to RGB "so we can take
/// advantage of alpha blending". The frame compositor resets each frame's damage union to its base style before
/// compositing, so if that base is <see cref="CellStyle.Default"/> the derived RGB blank is overwritten with
/// <see cref="Color.Default"/> and every translucent cell over an UNPAINTED region resolves opaque — the scrim
/// rendering mottled: correctly veiled where a window painted an RGB background, near-solid where it did not.
/// </para>
/// <para>
/// Since the guaranteed root background (<c>RootElementHost.BackgroundProperty</c>, default resource
/// <c>Theme.ElevationDesktop</c>), no cell of the root band is truly bare anymore: cells the app leaves
/// unpainted carry the theme's Desktop-elevation background (<c>#080910</c> on the default dark theme), and
/// the scrim blends against THAT — the terminal's derived default (<c>#1E1E2E</c> on the headless truecolor
/// preset) is no longer any root cell's blend base. The two backdrop cases these tests pin are therefore
/// "app-painted" (an explicit element background) and "app-bare" (the root's guaranteed background).
/// </para>
/// </remarks>
public sealed class ObscuredOverlayBlendTests
{
    // Theme.ObscuredOverlayBrush on the RGB palette — CursorialTheme's `new SolidColorBrush(#080910) { Opacity = 0.55 }`.
    private static readonly Color Scrim = Color.FromRgba(0x08, 0x09, 0x10, 140);

    // An explicit, opaque RGB background painted by the root content — the case that already blends today.
    private static readonly Color PaintedBackground = Color.FromRgb(0x00, 0x00, 0xC8);

    private const int PaintedColumns = 10;
    private const int PaintedRows = 3;

    // A cell inside the painted Border, and one in the bare band below it — both clear of the dialog + its shadow.
    private static readonly (int Column, int Row) PaintedCell = (1, 1);
    private static readonly (int Column, int Row) BareCell = (1, 18);

    private static (UIHeadlessHost Host, WindowManager Wm) ObscuredRoot()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });

        // A root that paints an explicit RGB background over PART of the frame and leaves the rest bare.
        host.ShowRoot(new UIControls.StackPanel
                      {
                          Children =
                          {
                              new Border
                              {
                                  HorizontalAlignment = HorizontalAlignment.Left,   // pin the footprint to (0,0)
                                  Background = new SolidColorBrush(PaintedBackground),
                                  Width = PaintedColumns,
                                  Height = PaintedRows
                              }
                          }
                      });

        Assert.True(host.RunUntilIdle());

        var wm = host.Application.WindowManager!;

        // A modal puts the root band behind the obscured overlay (RootElementHost.SetObscured).
        var dialog = host.NewWindow(width: 20, height: 6, left: 30, top: 10);
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.Same(dialog, wm.TopmostModal);

        return (host, wm);
    }

    // The guaranteed root background — Theme.ElevationDesktop resolved through the ACTIVE theme, so the pin
    // derives from the theme dictionary rather than restating a constant the theme owns.
    private static Color GuaranteedRootBackground(UIHeadlessHost host)
        => ((SolidColorBrush)host.Application.RootElement!.FindResource(ThemeKeys.ElevationDesktop)!).Color;

    [Fact]
    public void ModalScrim_OverAnAppBareRegion_BlendsAgainstTheRootsGuaranteedBackground_NotTheTerminalDefault()
    {
        var (host, _) = ObscuredRoot();
        using var hostScope = host;

        // The root guarantees a background (Theme.ElevationDesktop — #080910 on the default dark theme), so
        // an app-bare cell's backdrop under the scrim is the theme value, not the terminal default.
        var guaranteed = GuaranteedRootBackground(host);
        Assert.Equal(Color.FromRgb(0x08, 0x09, 0x10), guaranteed);

        var terminalDefault = host.FrameBuffer.DefaultStyle.Background;
        Assert.Equal(ColorKind.Rgb, terminalDefault.Kind);   // the derived blank still exists...
        Assert.Equal(Color.FromRgb(30, 30, 46), terminalDefault);

        var actual = host.FrameBuffer[BareCell.Column, BareCell.Row].Style.Background;

        // The blend, computed against the guaranteed root background. On the default dark theme this is
        // DEGENERATE — the scrim's own RGB (#080910) equals ElevationDesktop, so blending it over itself
        // lands on the shared value; the proof that the scrim's alpha is honored (blended, not painted at
        // full opacity) lives in ModalScrim_OverAPaintedRegion_StillBlendsAsItAlreadyDid, whose backdrop
        // is distinct from the scrim.
        Assert.Equal(Color.Composite(Scrim, guaranteed, BlendingModes.Default), actual);
        Assert.Equal(Color.FromRgb(0x08, 0x09, 0x10), actual);

        // ... but the terminal default is no longer the blend base: the old contract's #11121D
        // (scrim over #1E1E2E) must NOT appear under an app-bare cell.
        Assert.NotEqual(Color.Composite(Scrim, terminalDefault, BlendingModes.Default), actual);
    }

    [Fact]
    public void ModalScrim_OverAPaintedRegion_StillBlendsAsItAlreadyDid()
    {
        var (host, _) = ObscuredRoot();
        using var hostScope = host;

        var actual = host.FrameBuffer[PaintedCell.Column, PaintedCell.Row].Style.Background;

        Assert.Equal(Color.Composite(Scrim, PaintedBackground, BlendingModes.Default), actual);
        Assert.Equal(Color.FromRgb(0x04, 0x04, 0x62), actual);
    }

    [Fact]
    public void NoModal_TheBareRootBand_ReadsTheGuaranteedThemeBackground_NotTheTerminalDefaultBlank()
    {
        // The root's guaranteed background changes what "appearance" a bare band has AT ALL: with no scrim in
        // play, a cell the app never painted no longer shows the terminal's own default through — it reads the
        // theme's Desktop-elevation background (RootElementHost.BackgroundProperty's default resource), which
        // deliberately DIFFERS from the terminal default so the app controls its canvas on any terminal.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());

        var guaranteed = GuaranteedRootBackground(host);
        Assert.Equal(Color.FromRgb(0x08, 0x09, 0x10), guaranteed); // default dark theme's ElevationDesktop

        var actual = host.FrameBuffer[BareCell.Column, BareCell.Row].Style.Background;

        Assert.Equal(guaranteed, actual);
        Assert.NotEqual(host.FrameBuffer.DefaultStyle.Background, actual); // the terminal blank no longer shows through
    }
}
