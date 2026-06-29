#pragma warning disable xUnit1031
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

// A window's Opacity must be applied EXACTLY ONCE — by the Window element's render-tree root opacity boundary
// (live, via AffectsComposite) — never folded into surface.Opacity too. The regression: SizeAndPositionWindow ran
// before FinishShow activated the window, so a `When IsActive==false` opacity setter froze surface.Opacity at the
// inactive value, dimming the window even when active (1.0 × 0.70) and doubling it when inactive (0.70 × 0.70).
public sealed class WindowOpacityTests
{
    [Fact]
    public void WindowOpacity_AppliedOnce_NotDoubledViaSurface()
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(20, 10),
            Capabilities = TestCapabilities.KittyTruecolor
        });
        using var _ = host;

        // An RGB root backdrop so opacity compositing is observable (alpha engages only RGB-on-RGB).
        var rootBg = Color.FromRgb(0, 0, 200);
        host.ShowRoot(new UIControls.Border { Background = new SolidColorBrush(rootBg) });
        host.RunUntilIdle();

        var wm = host.Application.WindowManager!;
        var winBg = Color.FromRgb(200, 0, 0);

        var window = host.NewWindow(
            windowStyle: WindowStyle.None,
            windowStartupLocation: WindowStartupLocation.Manual,
            width: 6,
            height: 4,
            left: 2,
            top: 2,
            content: new UIControls.Border { Background = new SolidColorBrush(winBg) },
            opacity: 0.70 // shown WITH opacity < 1 (the inactive-at-show case that froze surface.Opacity)
        );

        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        // The fix: the surface carries NO opacity — the Window's render-tree root boundary applies it.
        Assert.Equal(1.0, window.HostSurface!.Opacity);

        // The window body cell = winBg blended ONCE at 0.70 over rootBg ≈ (140, 0, 60), NOT doubled at 0.49 ≈ (98,0,102).
        var cell = host.GetCell(4, 3).Style.Background;
        Assert.Equal(ColorKind.Rgb, cell.Kind);
        Assert.InRange(cell.Red, 130, 150);  // single 0.70 ≈ 140; the double-applied 0.49 ≈ 98 would fail here
        Assert.InRange(cell.Blue, 50, 70);   // single ≈ 60; double ≈ 102

        // Raising opacity to 1.0 makes the window fully opaque (no stale surface dim lingering).
        window.Opacity = 1.0;
        Assert.True(host.RunUntilIdle());
        var opaque = host.GetCell(4, 3).Style.Background;
        Assert.Equal(winBg, opaque); // exactly winBg — fully opaque, not the stale 0.70 translucency
    }
}
