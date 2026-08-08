// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

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
/// Concretely, on the headless truecolor preset (default background <c>#1E1E2E</c>) with the scrim's alpha at
/// <c>0.55 → 140</c>: a bare cell must composite to <c>#11121D</c>, not the scrim's own <c>#080910</c>.
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

    [Fact]
    public void ModalScrim_OverABareRegion_BlendsAgainstTheTerminalDefault_NotItsOwnColorAtFullOpacity()
    {
        var (host, _) = ObscuredRoot();
        using var hostScope = host;

        var terminalDefault = host.FrameBuffer.DefaultStyle.Background;
        Assert.Equal(ColorKind.Rgb, terminalDefault.Kind);   // the derived blank the fix depends on
        Assert.Equal(Color.FromRgb(30, 30, 46), terminalDefault);

        var actual = host.FrameBuffer[BareCell.Column, BareCell.Row].Style.Background;

        // The blend, computed the way the compositor would if the base were the target's own blank.
        Assert.Equal(Color.Composite(Scrim, terminalDefault, BlendingModes.Default), actual);

        // ... which is #11121D. NOT #080910 — the scrim's own color at full opacity, i.e. alpha discarded.
        Assert.Equal(Color.FromRgb(0x11, 0x12, 0x1D), actual);
        Assert.NotEqual(Color.FromRgb(0x08, 0x09, 0x10), actual);
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
    public void NoModal_TheBareRootBand_StillReadsTheTerminalDefaultBlank()
    {
        // The guard on "appearance must not change": the derived blank IS the terminal's own default promoted
        // to RGB, so with no scrim in play an unpainted cell must read exactly the buffer's blank — the same
        // value every cell the compositor never touched already holds.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());

        Assert.Equal(host.FrameBuffer.DefaultStyle.Background,
                     host.FrameBuffer[BareCell.Column, BareCell.Row].Style.Background);
    }
}
