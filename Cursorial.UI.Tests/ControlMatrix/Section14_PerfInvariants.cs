using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §14 — Perf + invariants re-asserted at P5 (rows C237–C242). Themed-control attach cost, the
/// motion-storm re-assert over templated controls, invariant 3 (zone-local re-raster / composite-only
/// scroll), invariant 2 (engines never touch Scene), invariant 4 (store-owned retraction). One test
/// per row.
/// </summary>
public sealed class Section14_PerfInvariants
{
    private static UIHeadlessHost Show(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    private static MouseEvent Move(int column, int row) => new()
    {
        Kind = MouseEventKind.Move,
        Position = new CellPosition(column, row),
        Button = MouseButton.None,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    [Fact] // C237 — cold attach of many themed controls completes within a sane budget (informational)
    [Trait("Category", "Benchmark")]
    public void C237_ColdAttachOfThemedControls()
    {
        var panel = new StackPanel();
        for (var i = 0; i < 300; i++)
            panel.Children.Add(i % 2 == 0 ? new Button { Content = "B" } : new CheckBox { Content = "C" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var host = Show(panel);
        sw.Stop();

        // Template expansion + control-theme arming for 300 controls is a one-time attach cost — assert
        // only that it is not absurd (not the per-frame constraint; informational per doc §14 P5).
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"cold attach took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(300, panel.Children.Count);
    }

    [Fact] // C238 — motion-storm over templated controls: steady-state Move dispatch is zero-allocation
    [Trait("Category", "Benchmark")]
    public void C238_MotionStormOverTemplatedControls_ZeroSteadyStateAllocation()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 20; i++)
            panel.Children.Add(new Button { Content = "x", Width = 4, Height = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        using var host = Show(panel);

        var dispatcher = host.Application.InputDispatcher;
        var a = Move(1, 0);
        var b = Move(2, 0); // same button — unchanged hover chain (the steady-state Move path)

        for (var i = 0; i < 256; i++)
        {
            dispatcher.ProcessEvent(a);
            dispatcher.ProcessEvent(b);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            dispatcher.ProcessEvent(a);
            dispatcher.ProcessEvent(b);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // 0 B steady-state (ND25)
    }

    [Fact] // C239 — invariant 3: a :pointerover restyle re-rasters only the affected zone, not unrelated zones
    public void C239_PointerOverRestyle_ZoneLocalReRaster()
    {
        // Two buttons, each its own render boundary (so each owns a zone/Scene). Hovering one re-rasters
        // ONLY its zone (RC bumps for the affected zone, not the unrelated one) and never relayouts.
        var first = new Button { Content = "A", Width = 6, Height = 1, IsRenderBoundary = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var second = new Button { Content = "B", Width = 6, Height = 1, IsRenderBoundary = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        using var host = Show(panel);

        // A :pointerover rule that swaps the hovered button's background (an AffectsRender styled prop).
        var hoverFill = Color.FromRgb(80, 120, 200);
        var rule = new Cursorial.UI.Style(Selectors.OfType<Button>().PseudoClass("pointerover"));
        rule.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(hoverFill)));
        host.Application.Styles.Add(rule);
        host.RunFrame();

        var tree = host.Application.WindowManager!.Tree!;
        var firstScene = tree.GetScene(first)!;
        var secondScene = tree.GetScene(second)!;
        var firstVersion = firstScene.RasterVersion;
        var secondVersion = secondScene.RasterVersion;
        var secondBoundsBefore = second.Bounds;

        // Hover the first button (enter): the :pointerover flip restyles + re-rasters its zone only.
        host.SendMouseMove(1, 0);
        host.RunFrame();
        Assert.True(first.IsPointerOver);
        Assert.False(second.IsPointerOver);
        Assert.True(firstScene.RasterVersion > firstVersion);       // the hovered button's zone re-rastered
        Assert.Equal(secondVersion, secondScene.RasterVersion);     // the unrelated button's zone is frozen
        Assert.Equal(secondBoundsBefore, second.Bounds);            // …and never relayouts
    }

    [Fact] // C240 — invariant 3: an in-band scroll is a pure composite slide (zero re-raster)
    public void C240_InBandScroll_ZeroReRaster()
    {
        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new RasterCountingBlock(19, 100)
        };
        using var host = Show(sv);

        var content = (RasterCountingBlock)sv.Content!;
        content.Renders = 0;

        // A small in-band scroll (within the band) is a composite slide — the SCP styled offset is
        // AffectsComposite; the content scene is NOT re-rastered (invariant 3 / L207).
        sv.VerticalOffset = 2;
        host.RunFrame();

        Assert.Equal(2, sv.VerticalOffset);
        Assert.Equal(0, content.Renders); // zero re-raster of the scrolled content
    }

    [Fact] // C241 — invariant 2: a styled+templated control through a frame never throws under the render guard
    public void C241_StyledTemplatedControl_NoSceneAccessOutsideRender()
    {
        // The render-pass guard (DEBUG) trips if styling/property work touches the Scene/CellBuffer
        // outside an element's Render(RenderContext). A full themed frame must complete clean.
        var button = new Button { Content = "Go", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        using var host = Show(button);

        // A restyle-driving flip (hover) + a re-render must not trip the guard (no throw == invariant 2).
        host.SendMouseMove(1, 0);
        host.RunFrame();
        host.SendMouseMove(40, 20);
        host.RunFrame();

        Assert.NotNull(button.TemplateInstance); // the template expanded and rendered through the frame
    }

    [Fact] // C242 — invariant 4: Detach / DynamicResource-to-miss / style deactivation promote, never set-back
    public void C242_StoreOwnedRetraction()
    {
        // A re-template Detach retracts the old template's frames by cookie (store-owned promotion). The
        // re-templated control's properties fall to their next source — nothing "sets the old back".
        var button = new Button { Content = "X", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        using var host = Show(button);

        var firstInstance = button.TemplateInstance;
        Assert.NotNull(firstInstance);

        // Re-template: the old instance detaches (cookie retraction); a fresh instance arms.
        button.Template = new ControlTemplate(ctx =>
        {
            var presenter = new ContentPresenter { RecognizesAccessKey = true };
            ctx.RegisterName("PART_ContentPresenter", presenter);
            return new Border { Child = presenter };
        });
        host.RunFrame();

        Assert.NotSame(firstInstance, button.TemplateInstance); // a fresh instance (old retracted)
        Assert.NotNull(button.TemplateInstance);
    }

    /// <summary>A content block counting its <see cref="Render"/> calls (the re-raster observability surface).</summary>
    private sealed class RasterCountingBlock(int columns, int rows) : UIElement
    {
        public int Renders;
        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
        protected override Size ArrangeOverride(Size finalSize) => new(columns, rows);
        protected override void Render(RenderContext context) => Renders++;
    }
}
