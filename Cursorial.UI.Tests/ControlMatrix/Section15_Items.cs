using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

using Cursorial.Drawing.Media;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using Style = Cursorial.UI.Style;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C2 — the ItemsControl pipeline (ItemContainerGenerator / ItemsPresenter / punch-43).
public sealed class Section15_Items
{
    private static (UITestHost Host, T Control) Show<T>(T control) where T : UIElement
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 10) });
        host.ShowRoot(control);
        host.RunUntilIdle();
        return (host, control);
    }

    private static ItemContainerGenerator Gen(ItemsControl ic) => ic.ItemContainerGenerator;

    [Fact] // C2.1: a bound ItemsSource realizes one container per item
    public void ItemsSource_RealizesContainerPerItem()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new ObservableCollection<string> { "a", "b", "c" } });
        using var _ = host;

        Assert.NotNull(Gen(ic).ContainerFromIndex(0));
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));
        Assert.NotNull(Gen(ic).ContainerFromIndex(2));
        Assert.Null(Gen(ic).ContainerFromIndex(3));
    }

    [Fact] // C2.1b: the direct Items lane realizes containers too
    public void DirectItems_RealizesContainerPerItem()
    {
        var ic = new ItemsControl();
        ic.Items.Add("x");
        ic.Items.Add("y");
        var (host, _) = Show(ic);
        using var __ = host;

        Assert.NotNull(Gen(ic).ContainerFromIndex(0));
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));
    }

    [Fact] // C2.3: setting both Items (populated) and ItemsSource throws
    public void ItemsSourceAndItems_MutuallyExclusive()
    {
        var ic = new ItemsControl();
        ic.Items.Add("x");
        Assert.Throws<InvalidOperationException>(() => ic.ItemsSource = new[] { "a" });

        var ic2 = new ItemsControl { ItemsSource = new[] { "a" } };
        Assert.Throws<InvalidOperationException>(() => ic2.Items.Add("y")); // mutate Items while ItemsSource set
    }

    [Fact] // C2.4: the default container is a ContentPresenter
    public void DefaultContainer_IsContentPresenter()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a" } });
        using var _ = host;
        Assert.IsType<ContentPresenter>(Gen(ic).ContainerFromIndex(0));
    }

    [Fact] // C2.5: a UIElement item is its own container (no presenter wrapper)
    public void UIElementItem_IsItsOwnContainer()
    {
        var leaf = new TextBlock("inline");
        var (host, ic) = Show(new ItemsControl { ItemsSource = new object[] { leaf } });
        using var _ = host;
        Assert.Same(leaf, Gen(ic).ContainerFromIndex(0)); // used directly, not wrapped
    }

    [Fact] // C2.6/C2.7 (punch 43): the container is a LOGICAL child of the ItemsControl, a VISUAL child of the panel
    public void Container_LogicalOfControl_VisualOfPanel()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a" } });
        using var _ = host;

        var container = Gen(ic).ContainerFromIndex(0)!;
        Assert.Same(ic, container.LogicalParent);          // logical parent = the ItemsControl ⇒ inheritance flows from it
        Assert.IsType<StackPanel>(container.VisualParent); // visual parent = the (default) items panel
        Assert.NotSame(ic, container.VisualParent);
    }

    [Fact] // C2.6/C2.7 payoff: resources resolve UP through the container to the ItemsControl (logical parentage works)
    public void Container_ResolvesResourcesThroughControl()
    {
        var ic = new ItemsControl { ItemsSource = new[] { "a" } };
        ic.Resources["K"] = "value-from-control"; // a resource on the control, reachable via the container's logical ancestry
        var (host, _) = Show(ic);
        using var _ = host;

        var container = Gen(ic).ContainerFromIndex(0)!;
        Assert.True(container.TryFindResource("K", out var v)); // the punch-43 logical-parentage payoff, end-to-end
        Assert.Equal("value-from-control", v);
    }

    [Fact] // C2.8: Add at an index realizes one container; later indices shift
    public void AddAtIndex_RealizesAndShifts()
    {
        var source = new ObservableCollection<string> { "a", "c" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var c0 = Gen(ic).ContainerFromIndex(0);

        source.Insert(1, "b");
        host.RunUntilIdle();
        Assert.Same(c0, Gen(ic).ContainerFromIndex(0)); // index 0 unchanged
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));   // new container at 1
        Assert.NotNull(Gen(ic).ContainerFromIndex(2));   // "c" shifted to 2
    }

    [Fact] // C2.9: Remove runs the full 4-step retraction — ClearContainer (step 1) AND the visual+logical detach
    public void Remove_Unrealizes_RunsFullRetraction()
    {
        var source = new ObservableCollection<string> { "a", "b" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source, ItemContainerStyle = new Style() });
        using var _ = host;
        var removed = (ContentPresenter)Gen(ic).ContainerFromIndex(0)!;
        Assert.True(removed.IsSet(ContentPresenter.ContentProperty)); // baseline: prepared

        source.RemoveAt(0);
        host.RunUntilIdle();

        // Step 1 (ClearContainer): Content/ContentTemplate cleared and the ItemContainerStyle dropped.
        Assert.False(removed.IsSet(ContentPresenter.ContentProperty));
        Assert.False(removed.IsSet(ContentPresenter.ContentTemplateProperty));
        Assert.Null(removed.Style);
        // Steps 2-3 (visual + logical detach):
        Assert.Null(removed.LogicalParent);
        Assert.Null(removed.VisualParent);
        // Step 4 (DataContext clear) + the item↔container stamp cleared:
        Assert.Null(removed.DataContext);
        Assert.Null(Gen(ic).ItemFromContainer(removed));
        // …and it is evicted, the survivor shifts up:
        Assert.Equal(-1, Gen(ic).IndexFromContainer(removed));
        Assert.NotNull(Gen(ic).ContainerFromIndex(0)); // "b" survived at 0
    }

    [Fact] // C2.9b (CD-P9-3 ordering): ClearContainer + the visual detach precede the logical removal
    public void Remove_RetractionOrdering_VisualDetachAndClearPrecedeLogicalRemove()
    {
        var source = new ObservableCollection<string> { "a", "b" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source, ItemContainerStyle = new Style() });
        using var _ = host;
        var removed = (ContentPresenter)Gen(ic).ContainerFromIndex(0)!;

        UIElement? visualParentAtLogicalDetach = removed; // sentinel ≠ null
        var contentClearedAtLogicalDetach = false;
        var styleNullAtLogicalDetach = false;
        var fired = false;
        removed.DetachedFromLogicalTree += (_, _) =>
        {
            fired = true;
            visualParentAtLogicalDetach = removed.VisualParent;                       // expect already null
            contentClearedAtLogicalDetach = !removed.IsSet(ContentPresenter.ContentProperty);
            styleNullAtLogicalDetach = removed.Style is null;
        };

        source.RemoveAt(0);
        host.RunUntilIdle();

        Assert.True(fired);
        Assert.Null(visualParentAtLogicalDetach);     // visual detach ran BEFORE logical removal (the store-retraction trigger)
        Assert.True(contentClearedAtLogicalDetach);    // ClearContainer (bindings live) ran BEFORE the detach
        Assert.True(styleNullAtLogicalDetach);
    }

    [Fact] // C2.10: an own-container is left untouched by ClearContainer (its user-set Style survives), but is still detached
    public void Remove_OwnContainer_StyleUntouched()
    {
        var own = new TextBlock("x");
        var userStyle = new Style();
        own.Style = userStyle;
        var source = new ObservableCollection<object> { own, "b" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        Assert.Same(own, Gen(ic).ContainerFromIndex(0)); // own-container in use

        source.RemoveAt(0);
        host.RunUntilIdle();

        Assert.Same(userStyle, own.Style); // ClearContainer SKIPPED the own-container — the user's Style is preserved
        Assert.Null(own.LogicalParent);     // …but it was still detached (logical + visual)
        Assert.Null(own.VisualParent);
        Assert.Equal(-1, Gen(ic).IndexFromContainer(own));
    }

    [Fact] // C2.11: Move reorders the same containers (no realize/unrealize)
    public void Move_ReordersSameContainers()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var a = Gen(ic).ContainerFromIndex(0)!;
        var b = Gen(ic).ContainerFromIndex(1)!;

        source.Move(0, 2); // a → end
        host.RunUntilIdle();
        Assert.Same(b, Gen(ic).ContainerFromIndex(0));   // b shifted up
        Assert.Same(a, Gen(ic).ContainerFromIndex(2));   // a at the end — same instance, not re-realized
    }

    [Fact] // C2.12: Replace unrealizes the old container and realizes a NEW one in place; neighbors + count unchanged
    public void Replace_UnrealizesOld_RealizesNew()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var a = Gen(ic).ContainerFromIndex(0)!;
        var oldB = Gen(ic).ContainerFromIndex(1)!;

        source[1] = "B";
        host.RunUntilIdle();

        var newAt1 = Gen(ic).ContainerFromIndex(1)!;
        Assert.NotSame(oldB, newAt1);                    // re-realized (new instance), not re-prepared in place
        Assert.Null(oldB.LogicalParent);                 // the old container was unrealized
        Assert.Same(a, Gen(ic).ContainerFromIndex(0));   // neighbor unchanged
        Assert.NotNull(Gen(ic).ContainerFromIndex(2));   // count preserved (3)
        Assert.Null(Gen(ic).ContainerFromIndex(3));
    }

    [Fact] // C2.13: Reset (clear) unrealizes everything
    public void Reset_UnrealizesAll()
    {
        var source = new ObservableCollection<string> { "a", "b" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var c0 = Gen(ic).ContainerFromIndex(0)!;

        source.Clear();
        host.RunUntilIdle();
        Assert.Null(Gen(ic).ContainerFromIndex(0));
        Assert.Null(c0.LogicalParent); // the staged teardown ran (CD-P9-3) even on the wholesale reset
        Assert.Null(c0.VisualParent);
    }

    [Fact] // C2.14: ItemContainerStyle applies to each container at the Explicit layer
    public void ItemContainerStyle_AppliedToContainers()
    {
        var style = new Style(); // selector-less explicit style
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a" }, ItemContainerStyle = style });
        using var _ = host;
        Assert.Same(style, Gen(ic).ContainerFromIndex(0)!.Style);
    }

    [Fact] // C2.14b (CD-P9-5): the ItemContainerStyle is the EXPLICIT layer — it beats an app type-selector style,
    // which still composes underneath it on properties the Explicit style leaves alone.
    public void ItemContainerStyle_ExplicitLayer_BeatsTypeSelector_TypeStillComposes()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 10) });
        using var _ = host;
        host.Application.Styles.Add(new Style(Selectors.OfType<ContentPresenter>())
            .Set(UIElement.MinWidthProperty, 5)    // contested with the Explicit ItemContainerStyle
            .Set(UIElement.MinHeightProperty, 7)); // uncontested — only the type layer sets it

        var icStyle = new Style().Set(UIElement.MinWidthProperty, 20); // Explicit (container.Style)
        var ic = new ItemsControl { ItemsSource = new[] { "a" }, ItemContainerStyle = icStyle };
        host.ShowRoot(ic);
        host.RunUntilIdle();

        var container = Gen(ic).ContainerFromIndex(0)!;
        Assert.Equal(20, container.GetValue(UIElement.MinWidthProperty));  // Explicit wins over the Style layer
        Assert.Equal(7, container.GetValue(UIElement.MinHeightProperty));  // …the type-selector layer still contributes underneath
    }

    [Fact] // C2.15: a runtime ItemTemplate change re-realizes (the v1 Reset policy — containers are replaced)
    public void RuntimeItemTemplateChange_ReRealizes()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a", "b" } });
        using var _ = host;
        var before = Gen(ic).ContainerFromIndex(0);

        ic.ItemTemplate = new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock("templated")) };
        host.RunUntilIdle();
        Assert.NotSame(before, Gen(ic).ContainerFromIndex(0)); // re-realized
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));
    }

    [Fact] // C2.16: setting ItemsSource → null reverts to the direct Items lane (the inverse of CD-P9-4)
    public void ItemsSourceToNull_RevertsToDirectItemsLane()
    {
        var ic = new ItemsControl { ItemsSource = new ObservableCollection<string> { "a", "b" } };
        var (host, _) = Show(ic);
        using var __ = host;
        Assert.True(ic.HasItemsSource);

        ic.ItemsSource = null;
        host.RunUntilIdle();
        Assert.False(ic.HasItemsSource);
        Assert.Null(Gen(ic).ContainerFromIndex(0)); // containers dropped

        ic.Items.Add("z"); // mutation no longer throws — the lane reverted to direct Items
        host.RunUntilIdle();
        Assert.NotNull(Gen(ic).ContainerFromIndex(0)); // …and the direct lane realizes
    }

    [Fact] // C2.17: the item↔container stamp — ItemFromContainer round-trips (own-container ⇒ the item itself)
    public void ItemFromContainer_RoundTrips()
    {
        var leaf = new TextBlock("inline");
        var (host, ic) = Show(new ItemsControl { ItemsSource = new object[] { "a", leaf } });
        using var _ = host;

        var generated = Gen(ic).ContainerFromIndex(0)!;
        var own = Gen(ic).ContainerFromIndex(1)!;
        Assert.Equal("a", Gen(ic).ItemFromContainer(generated)); // generated ⇒ the data item
        Assert.Same(leaf, Gen(ic).ItemFromContainer(own));       // own-container ⇒ the container itself
    }

    [Fact] // C2.19 (CD-P9-7): a runtime ItemsPanel change re-hosts the SAME containers in a new panel (no re-realize, no dup)
    public void RuntimeItemsPanelChange_ReHostsSameContainers()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a", "b" } });
        using var _ = host;
        var c0 = Gen(ic).ContainerFromIndex(0)!;
        var oldPanel = c0.VisualParent;

        ic.ItemsPanel = new FuncTemplateContent(_ => new StackPanel());
        host.RunUntilIdle();

        Assert.Same(c0, Gen(ic).ContainerFromIndex(0)); // SAME container instance — re-hosted, not re-realized
        var newPanel = c0.VisualParent;
        Assert.NotSame(oldPanel, newPanel);             // …in a freshly-built panel
        Assert.IsType<StackPanel>(newPanel);
        Assert.Equal(2, ((Panel)newPanel).Children.Count); // exactly the two containers — no double-adoption
    }

    [Fact] // C2.18: HeaderedItemsControl exposes Header + the items host (MenuItem's base — smoke)
    public void HeaderedItemsControl_HasHeaderAndItems()
    {
        var hic = new HeaderedItemsControl { Header = "File" };
        hic.Items.Add("Open");
        Assert.Equal("File", hic.Header);
        Assert.NotNull(hic.ItemContainerGenerator.ContainerFromIndex(0));
    }

    [Fact] // C2.21: an idle frame with a REALIZED items tree present adds no per-frame allocation — the items
    // pipeline is event-driven (Realize/Unrealize/Move/SyncAll fire on collection-change/attach/template change,
    // never on a clean frame), so its mere presence must not wire any per-frame churn (a stray timer / per-frame
    // SyncAll would trip this). Steady-state items *mutations* allocate (a new container) and are not in scope here.
    [Trait("Category", "Benchmark")]
    public void StaticItemsTree_IdleFrameAddsNoAllocation()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 10) });
        using var _ = host;
        var ic = new ItemsControl { ItemsSource = new[] { "a", "b", "c", "d" } };
        host.ShowRoot(ic);
        host.RunUntilIdle();
        host.RunFrames(64); // warm
        Assert.Equal(4, Gen(ic).ContainerCount); // non-vacuous: the tree is actually realized + laid out

        var before = GC.GetAllocatedBytesForCurrentThread();
        host.RunFrames(1_000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // 0 B idle frames with the items tree present
    }

    [Fact] // C2.11b: a FORWARD multi-item Move keeps the PANEL's visual order in lockstep with the generator's
    // logical order (the case the per-slot reorder got wrong — visual/render order diverged from data order).
    public void MultiItemMove_PanelOrderMatchesGeneratorOrder()
    {
        var source = new MovableList("a", "b", "c", "d", "e", "f");
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var panel = (Panel)Gen(ic).ContainerFromIndex(0)!.VisualParent!;

        source.RaiseMove(oldIndex: 0, newIndex: 3, count: 2); // generator order ⇒ c,d,e,a,b,f
        host.RunUntilIdle();

        Assert.Equal(6, panel.Children.Count);
        for (var i = 0; i < 6; i++)
            Assert.Same(Gen(ic).ContainerFromIndex(i), panel.Children[i]); // panel visual order == generator logical order
    }

    [Fact] // C2.16b (ItemsSourceView): an IList source that is NOT INotifyCollectionChanged is live-by-index but static
    public void NonNotifyingIList_IsStatic()
    {
        var list = new List<string> { "a", "b" }; // List<T> is IList, not INotifyCollectionChanged
        var (host, ic) = Show(new ItemsControl { ItemsSource = list });
        using var _ = host;
        Assert.Equal(2, Gen(ic).ContainerCount);

        list.Add("c"); // mutate underneath — no INCC, so the generator cannot and must not react
        host.RunUntilIdle();
        Assert.Equal(2, Gen(ic).ContainerCount); // unchanged (static — no subscription)
    }

    [Fact] // C2.16c (ItemsSourceView): an INCC source that is NOT an IList is a FROZEN snapshot (no subscription)
    public void NotifyingNonIList_IsSnapshotNotSubscribed()
    {
        var src = new NotifyingEnumerable("a", "b"); // INotifyCollectionChanged but NOT IList
        var (host, ic) = Show(new ItemsControl { ItemsSource = src });
        using var _ = host;
        Assert.Equal(2, Gen(ic).ContainerCount);

        src.RaiseReset(); // later notifications are ignored — it was snapshotted, never subscribed
        host.RunUntilIdle();
        Assert.Equal(2, Gen(ic).ContainerCount); // unchanged (frozen snapshot)
    }

    [Fact] // teardown: TearDown releases the bound source so a live external collection no longer pins the control
    public void TearDown_ReleasesItemsSourceSubscription()
    {
        var source = new ObservableCollection<string> { "a", "b" };
        var ic = new ItemsControl { ItemsSource = source };
        Assert.NotNull(Gen(ic).ContainerFromIndex(1)); // realized eagerly (no host needed for the generator)

        ic.TearDown();

        source.Add("c"); // mutate after teardown — the generator must NOT react (source released via OnTearDown)
        Assert.Null(Gen(ic).ContainerFromIndex(2)); // no new container realized
        Assert.Equal(2, Gen(ic).ContainerCount);
    }

    [Fact] // ItemsSource A→B swap: old containers unrealized + old source released; new realized
    public void ItemsSourceSwap_RealizesNew_ReleasesOldSource()
    {
        var a = new ObservableCollection<string> { "a1", "a2" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = a });
        using var _ = host;
        var oldC0 = Gen(ic).ContainerFromIndex(0)!;

        ic.ItemsSource = new ObservableCollection<string> { "b1", "b2", "b3" };
        host.RunUntilIdle();
        Assert.Null(oldC0.LogicalParent);            // old containers unrealized
        Assert.Equal(3, Gen(ic).ContainerCount);     // new source realized

        a.Add("a4"); // the OLD source was disposed on swap — no reaction
        host.RunUntilIdle();
        Assert.Equal(3, Gen(ic).ContainerCount); // still the B generation
    }

    [Fact] // presenter lifecycle: detach then re-attach re-adopts the SAME containers exactly once (no double-adoption)
    public void DetachReattach_ReadoptsContainersOnce()
    {
        var ic = new ItemsControl { ItemsSource = new[] { "a", "b" } };
        var wrap = new StackPanel();
        wrap.Children.Add(ic);
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 10) });
        using var _ = host;
        host.ShowRoot(wrap);
        host.RunUntilIdle();
        var c0 = Gen(ic).ContainerFromIndex(0)!;

        wrap.Children.Remove(ic); // detach (presenter clears its panel + unsubscribes)
        host.RunUntilIdle();
        wrap.Children.Add(ic);    // re-attach (presenter rebuilds + re-adopts the control-lifetime containers)
        host.RunUntilIdle();

        Assert.Same(c0, Gen(ic).ContainerFromIndex(0));          // same instances — generator is control-lifetime
        var panel = (Panel)Gen(ic).ContainerFromIndex(0)!.VisualParent!;
        Assert.Equal(2, panel.Children.Count);                   // exactly two — no duplicate adoption
    }

    // ───────────────────────────── :alternate row-striping (CD-P9-25, C2.22–C2.25) ─────────────────────────────

    private static bool IsAlternate(ItemContainerGenerator gen, int index) =>
        gen.ContainerFromIndex(index)!.HasCustomPseudoClass(":alternate");

    [Fact] // C2.22: the generator stamps :alternate on odd 0-based-indexed containers (opt-in look — no default stripe)
    public void C2_22_Alternate_StampedOnOddIndexedContainers()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new ObservableCollection<string> { "a", "b", "c", "d" } });
        using var _ = host;

        Assert.False(IsAlternate(Gen(ic), 0));
        Assert.True(IsAlternate(Gen(ic), 1));
        Assert.False(IsAlternate(Gen(ic), 2));
        Assert.True(IsAlternate(Gen(ic), 3));
    }

    [Fact] // C2.23: inserting re-stripes the shifted survivors
    public void C2_23_Alternate_ReStripesOnInsert()
    {
        var source = new ObservableCollection<string> { "a", "b", "c", "d" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;

        source.Insert(1, "x"); // a, x, b, c, d — survivors from index 1 shift parity
        host.RunUntilIdle();

        Assert.False(IsAlternate(Gen(ic), 0));
        Assert.True(IsAlternate(Gen(ic), 1));
        Assert.False(IsAlternate(Gen(ic), 2));
        Assert.True(IsAlternate(Gen(ic), 3));
        Assert.False(IsAlternate(Gen(ic), 4));
    }

    [Fact] // C2.24: removing re-stripes the shifted survivors
    public void C2_24_Alternate_ReStripesOnRemove()
    {
        var source = new ObservableCollection<string> { "a", "b", "c", "d" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;

        source.RemoveAt(0); // b, c, d — survivors shift up one, parity flips
        host.RunUntilIdle();

        Assert.False(IsAlternate(Gen(ic), 0)); // b
        Assert.True(IsAlternate(Gen(ic), 1));  // c
        Assert.False(IsAlternate(Gen(ic), 2)); // d
    }

    [Fact] // C2.24b (W7): a Move re-stripes — parity follows each container's NEW index, not its old one
    public void C2_24b_Alternate_ReStripesOnMove()
    {
        var source = new ObservableCollection<string> { "a", "b", "c", "d" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;

        // A Move reuses the same containers (C2.11), so capture them and prove their parity swaps with position.
        var containerA = Gen(ic).ContainerFromIndex(0)!; // "a" — even, not alternate at rest
        var containerB = Gen(ic).ContainerFromIndex(1)!; // "b" — odd, alternate at rest
        Assert.False(containerA.HasCustomPseudoClass(":alternate"));
        Assert.True(containerB.HasCustomPseudoClass(":alternate"));

        source.Move(0, 1); // b, a, c, d — the two containers swap positions, so their parity must swap
        host.RunUntilIdle();

        Assert.Same(containerB, Gen(ic).ContainerFromIndex(0)); // same instances, reordered (no realize/unrealize)
        Assert.Same(containerA, Gen(ic).ContainerFromIndex(1));
        Assert.False(IsAlternate(Gen(ic), 0)); // b now even
        Assert.True(IsAlternate(Gen(ic), 1));  // a now odd
        Assert.False(IsAlternate(Gen(ic), 2));
        Assert.True(IsAlternate(Gen(ic), 3));
    }

    [Fact] // C2.25: an app ListBoxItem:alternate rule applies on odd rows (the end-to-end striping payoff)
    public void C2_25_Alternate_StripingRuleApplies_OnOddRows()
    {
        var lb = new ListBox { ItemsSource = new[] { "a", "b", "c", "d" } };

        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 10) });
        host.Application.Styles.Add(new Style(Selectors.OfType<ListBoxItem>().PseudoClass("alternate"))
            .Set(Control.BackgroundProperty, Brushes.Magenta));
        host.ShowRoot(lb);
        host.RunUntilIdle();
        using var _ = host;

        // The :alternate rule matched the odd-row containers (its Background setter applied), not the even rows.
        Assert.Same(Brushes.Magenta, ((ListBoxItem)Gen(lb).ContainerFromIndex(1)!).Background);
        Assert.Same(Brushes.Magenta, ((ListBoxItem)Gen(lb).ContainerFromIndex(3)!).Background);
        Assert.NotSame(Brushes.Magenta, ((ListBoxItem)Gen(lb).ContainerFromIndex(0)!).Background);
        Assert.NotSame(Brushes.Magenta, ((ListBoxItem)Gen(lb).ContainerFromIndex(2)!).Background);
    }

    /// <summary>A minimal <see cref="IList"/> + <see cref="INotifyCollectionChanged"/> source that can raise a
    /// multi-item <see cref="NotifyCollectionChangedAction.Move"/> (which <see cref="ObservableCollection{T}"/>
    /// never does — it only moves one element at a time).</summary>
    private sealed class MovableList(params object?[] items) : IList, INotifyCollectionChanged
    {
        private readonly List<object?> _inner = [.. items];

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public void RaiseMove(int oldIndex, int newIndex, int count)
        {
            var moved = _inner.GetRange(oldIndex, count);
            _inner.RemoveRange(oldIndex, count);
            _inner.InsertRange(newIndex, moved);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Move, moved, newIndex, oldIndex));
        }

        public int Count => _inner.Count;
        public object? this[int index] { get => _inner[index]; set => _inner[index] = value; }
        public int IndexOf(object? value) => _inner.IndexOf(value);
        public bool Contains(object? value) => _inner.Contains(value);
        public IEnumerator GetEnumerator() => _inner.GetEnumerator();
        public void CopyTo(Array array, int index) => ((ICollection)_inner).CopyTo(array, index);
        public int Add(object? value) { _inner.Add(value); return _inner.Count - 1; }
        public void Insert(int index, object? value) => _inner.Insert(index, value);
        public void Remove(object? value) => _inner.Remove(value);
        public void RemoveAt(int index) => _inner.RemoveAt(index);
        public void Clear() => _inner.Clear();
        public bool IsFixedSize => false;
        public bool IsReadOnly => false;
        public bool IsSynchronized => false;
        public object SyncRoot => _inner;
    }

    /// <summary>An <see cref="INotifyCollectionChanged"/> source that is deliberately NOT an <see cref="IList"/> —
    /// it must be snapshotted (never subscribed) by <c>ItemsSourceView</c>.</summary>
    private sealed class NotifyingEnumerable(params object?[] items) : IEnumerable<object?>, INotifyCollectionChanged
    {
        private readonly List<object?> _inner = [.. items];

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public void RaiseReset() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        public IEnumerator<object?> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }
}
