using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix-virt §V1 — IScrollContentHost/ILogicalScrollHost + ScrollContentPresenter delegation. The SCP
// delegates extent reporting + viewport hand-off to a host that opts in (IsScrollClient), and lifts the 32K extent
// cap under a host (the band bounds the scene, not the extent). The OFF-path (no host / IsScrollClient false) runs
// the verbatim legacy measure/arrange — byte-identical, proven by the existing ScrollViewer/ListBox suites.
public sealed class Section39_ScrollHost
{
    [Fact] // VV1.1: the interface shape — ILogicalScrollHost : IScrollContentHost, internal, cell-integer
    public void VV1_1_InterfaceShape()
    {
        Assert.True(typeof(IScrollContentHost).IsAssignableFrom(typeof(ILogicalScrollHost)));
        Assert.False(typeof(IScrollContentHost).IsPublic); // internal
        Assert.False(typeof(ILogicalScrollHost).IsPublic);
    }

    [Fact] // VV1.2: the SCP injects itself as the host's ScrollOwner on adopt, clears it on a content swap
    public void VV1_2_HostDiscovery_ScrollOwnerLifetime()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var a = new StubScrollHost();
        var b = new StubScrollHost();
        var sv = new ScrollViewer { Content = a };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.Same(sv.Presenter, a.ScrollOwner); // adopted

        sv.Content = b;
        host.RunUntilIdle();
        Assert.Null(a.ScrollOwner);                // disowned — no stale back-channel
        Assert.Same(sv.Presenter, b.ScrollOwner);  // re-adopted
    }

    [Fact] // VV1.3: OFF-path — plain content runs the legacy path (Extent == content desired, no host engaged)
    public void VV1_3_OffPath_Legacy()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var content = new FixedSizeElement { Desired = new Size(5, 100) };
        var sv = new ScrollViewer { Content = content };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.Equal(100, sv.Extent.Rows); // the legacy content.DesiredSize path, NOT a host estimate
    }

    [Fact] // VV1.4: host-active measure — content measured at the viewport (not MaxScrollExtent); Extent == GetExtent
    public void VV1_4_HostMeasure_ViewportConstraint()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 400) };
        var sv = new ScrollViewer { Content = stub };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.Equal(400, sv.Extent.Rows); // the host's GetExtent(), not content.DesiredSize (which is Empty)
        Assert.NotEqual(LayoutLimits.MaxScrollExtent, stub.RecordedConstraint.Rows); // measured at the viewport
        Assert.True(stub.RecordedConstraint.Rows is > 0 and < 100);                  // ≈ the 12-row host viewport
        Assert.True(stub.CanScrollVertically); // the SCP flowed its axis enable to the host
    }

    [Fact] // VV1.4b: under a host, the panel's measure constraint is clamped FINITE even with an unbounded parent
    public void VV1_4b_HostMeasure_UnboundedParentClamped()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 400) };
        // A vertical StackPanel measures its child at unbounded height — the SCP must still hand the panel a finite
        // constraint (never int.MaxValue), exactly as the legacy path substitutes MaxScrollExtent.
        var outer = new StackPanel();
        outer.Children.Add(new ScrollViewer { Content = stub });
        host.ShowRoot(outer);
        host.RunUntilIdle();

        Assert.Equal(LayoutLimits.MaxScrollExtent, stub.RecordedConstraint.Rows); // clamped, not Unbounded
    }

    [Fact] // VV1.5: the legacy 32K cap is LIFTED under a host (up to the Rect ceiling) + the offset reaches the tail
    public void VV1_5_CapLifted_UnderHost()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 60_000) }; // > MaxScrollExtent (32K), ≤ Rect.MaxDimension
        var sv = new ScrollViewer { Content = stub };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.True(sv.Extent.Rows > LayoutLimits.MaxScrollExtent); // past the legacy cap
        Assert.Equal(60_000, sv.Extent.Rows);

        sv.VerticalOffset = 9_999_999; // coerces against the FULL extent, not the 32K cap
        host.RunUntilIdle();
        Assert.Equal(60_000 - sv.Viewport.Rows, sv.VerticalOffset); // reaches the tail
    }

    [Fact] // VV1.5b: the host extent caps at Rect.MaxDimension (the arrange-rect ceiling), not the int range
    public void VV1_5b_HostExtent_CapsAtRectCeiling()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 200_000) }; // beyond the Rect dimension limit
        var sv = new ScrollViewer { Content = stub };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.Equal(ushort.MaxValue, sv.Extent.Rows); // clamped to the arrange-Rect ceiling (no overflow at arrange)
    }

    [Fact] // VV1.6: host-active arrange hands the host its viewport before its next measure
    public void VV1_6_SetViewport_HandOff()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 400) };
        var sv = new ScrollViewer { Content = stub };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.True(stub.LastViewport.Rows > 0);
        Assert.Equal(sv.Viewport.Rows, stub.LastViewport.Rows);
    }

    [Fact] // VV1.7: InvalidateScrollExtent re-publishes the extent + re-coerces the offset
    public void VV1_7_InvalidateScrollExtent()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 400) };
        var sv = new ScrollViewer { Content = stub };
        host.ShowRoot(sv);
        host.RunUntilIdle();
        sv.VerticalOffset = 300;
        host.RunUntilIdle();
        Assert.Equal(300, sv.VerticalOffset);

        // The host's estimate shrinks — the refinement back-channel re-publishes + re-coerces the now-out-of-range offset.
        stub.Extent = new Size(5, 50);
        sv.Presenter!.InvalidateScrollExtent();
        host.RunUntilIdle();

        Assert.Equal(50, sv.Extent.Rows);
        Assert.Equal(50 - sv.Viewport.Rows, sv.VerticalOffset); // snapped back into range
    }

    [Fact] // VV1.8: ItemsPresenter forwards the host contract to its panel (IsScrollClient false when not a host)
    public void VV1_8_ItemsPresenter_Forwards()
    {
        using var offHost = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var eager = new ListBox { ItemsSource = new[] { "a", "b", "c" } }; // default StackPanel — not a host
        offHost.ShowRoot(eager);
        offHost.RunUntilIdle();
        Assert.False(((IScrollContentHost)eager.ItemsHost!).IsScrollClient);

        using var onHost = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        StubLogicalPanel? panel = null;
        var virt = new ListBox
        {
            ItemsSource = new[] { "a", "b", "c" },
            ItemsPanel = new FuncTemplateContent(_ => panel = new StubLogicalPanel { Extent = new Size(5, 500) }),
        };
        onHost.ShowRoot(virt);
        onHost.RunUntilIdle();
        var presenterHost = (IScrollContentHost)virt.ItemsHost!;
        Assert.True(presenterHost.IsScrollClient);        // forwards the panel's IsScrollClient
        Assert.Equal(500, presenterHost.GetExtent().Rows); // forwards GetExtent
        Assert.NotNull(panel!.ScrollOwner);                // the panel's back-channel is wired on the up-front load (Finding A)
    }

    [Fact] // VV1.8b: an ItemsPanel host→host swap wires the new panel and disowns the old (no stale back-channel — Finding B)
    public void VV1_8b_PanelSwap_DisownsOld()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        StubLogicalPanel? first = null;
        StubLogicalPanel? second = null;
        var lb = new ListBox
        {
            ItemsSource = new[] { "a", "b" },
            ItemsPanel = new FuncTemplateContent(_ => first = new StubLogicalPanel()),
        };
        host.ShowRoot(lb);
        host.RunUntilIdle();
        Assert.NotNull(first!.ScrollOwner);

        lb.ItemsPanel = new FuncTemplateContent(_ => second = new StubLogicalPanel());
        host.RunUntilIdle();

        Assert.Null(first.ScrollOwner);     // disowned on the swap — no dangling back-channel
        Assert.NotNull(second!.ScrollOwner); // the new panel is wired
    }

    [Fact] // VV1.7b: InvalidateScrollExtent marks measure dirty so a GROWN extent re-publishes even with no offset change
    public void VV1_7b_InvalidateScrollExtent_GrowsWithoutOffsetChange()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var stub = new StubScrollHost { Extent = new Size(5, 400) };
        var sv = new ScrollViewer { Content = stub };
        host.ShowRoot(sv);
        host.RunUntilIdle();
        sv.VerticalOffset = 10; // stays in range when the extent grows — so the offset-coercion side channel can't drive the mirror
        host.RunUntilIdle();

        stub.Extent = new Size(5, 600);
        sv.Presenter!.InvalidateScrollExtent();
        host.RunUntilIdle();

        Assert.Equal(600, sv.Extent.Rows);    // re-published — only the InvalidateMeasure leg reaches the ScrollViewer mirror here
        Assert.Equal(10, sv.VerticalOffset);   // unchanged (proves the offset side channel was NOT the cause)
    }

    [Fact] // VV1.9: the X174-analog OFF-path drift gate — a ScrollViewer over plain content renders + scrolls unperturbed
    public void VV1_9_OffPath_DriftGate()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(16, 6) });
        var content = new StackPanel();
        for (var i = 0; i < 30; i++)
            content.Children.Add(new TextBlock { Text = $"row{i:00}" });
        var sv = new ScrollViewer { Content = content };
        host.ShowRoot(sv);
        host.RunUntilIdle();

        Assert.Contains("row00", host.GetRowText(0)); // legacy path renders from the top
        Assert.Contains("row01", host.GetRowText(1));

        sv.VerticalOffset = 5; // the legacy composite-slide scroll
        host.RunUntilIdle();
        Assert.Equal(5, sv.VerticalOffset);
        Assert.Contains("row05", host.GetRowText(0)); // scrolled, OFF-path unperturbed by the delegation code
    }

    // ── stubs ────────────────────────────────────────────────────────────────────────────────────────

    // A scroll host that is a UIElement (hosted directly as the SCP's content). Records what it was measured at.
    private sealed class StubScrollHost : UIElement, ILogicalScrollHost
    {
        public Size Extent = new(5, 400);
        public Size LastViewport;
        public Size RecordedConstraint;

        public bool IsScrollClient => true;
        public ScrollContentPresenter? ScrollOwner { get; set; }
        public bool CanScrollHorizontally { get; set; }
        public bool CanScrollVertically { get; set; } = true;
        public bool IsLogicalScroll => true;
        public Size GetExtent() => Extent;
        public void SetViewport(Size viewport) => LastViewport = viewport;
        public int LineStep(int currentOffset, int sign, bool vertical) => 1;
        public int PageStep(int currentOffset, int sign, bool vertical) => Math.Max(1, LastViewport.Rows - 1);
        public Rect BringItemIntoView(int itemIndex) => new(0, itemIndex, 1, 1);
        public int ItemCount => Extent.Rows;
        public int EstimateItemAt(int offsetRow) => offsetRow;

        protected override Size MeasureOverride(Size availableSize)
        {
            RecordedConstraint = availableSize;
            return Size.Empty;
        }
    }

    // A virtualizing-panel host (a real Panel set as an ItemsPanel) for the forwarding test.
    private sealed class StubLogicalPanel : VirtualizingPanel, ILogicalScrollHost
    {
        public Size Extent = new(5, 500);

        public bool IsScrollClient => true;
        public ScrollContentPresenter? ScrollOwner { get; set; }
        public bool CanScrollHorizontally { get; set; }
        public bool CanScrollVertically { get; set; } = true;
        public bool IsLogicalScroll => true;
        public Size GetExtent() => Extent;
        public void SetViewport(Size viewport) { }
        public int LineStep(int currentOffset, int sign, bool vertical) => 1;
        public int PageStep(int currentOffset, int sign, bool vertical) => 1;
        public Rect BringItemIntoView(int itemIndex) => new(0, itemIndex, 1, 1);
        public int ItemCount => Extent.Rows;
        public int EstimateItemAt(int offsetRow) => offsetRow;

        protected override Size MeasureOverride(Size availableSize) => Size.Empty;
        protected override Size ArrangeOverride(Size finalSize) => finalSize;
    }

    // A leaf element with a fixed desired size — drives the SCP's legacy (non-host) extent path.
    private sealed class FixedSizeElement : UIElement
    {
        public Size Desired;
        protected override Size MeasureOverride(Size availableSize) => Desired;
    }
}
