using System.Collections.ObjectModel;
using System.ComponentModel;

using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix-virt §V0 — ItemContainerGenerator virtualization API (sparse realized store + the
// ContainersRealizedChanged channel + recycling pool + focus keep-alive). Generator-only: the panel/SCP land at
// V1/V2, so these drive RealizeRange/UnrealizeRange directly. The keystone invariant is ContainerCount == itemCount
// in BOTH modes (selection/keyboard nav stay item-indexed), with ContainerFromIndex null for an unrealized index.
public sealed class Section38_Virtualization
{
    private static (ListBox List, ItemContainerGenerator Gen, ObservableCollection<string> Source) MakeVirtual(
        int count = 20, VirtualizationMode mode = VirtualizationMode.Recycling)
    {
        var lb = new ListBox();
        lb.ItemContainerGenerator.EnableVirtualization(mode);
        var src = new ObservableCollection<string>(Enumerable.Range(0, count).Select(i => $"i{i}"));
        lb.ItemsSource = src;
        return (lb, lb.ItemContainerGenerator, src);
    }

    private static int CountRealized(ItemContainerGenerator gen, int itemCount)
    {
        var n = 0;
        for (var i = 0; i < itemCount; i++)
            if (gen.ContainerFromIndex(i) is not null)
                n++;
        return n;
    }

    // Stand-in for the V2 VirtualizingStackPanel: adopts/releases via the materialization channel's AUTHORITATIVE
    // instance lists (RealizedContainers / RemovedContainers), never by iterating [StartIndex, StartIndex+Count) —
    // the span can bridge a kept-alive interior container, so iterating it would double-adopt.
    private static StackPanel WireStandInPanel(ItemContainerGenerator gen)
    {
        var panel = new StackPanel { IsItemsHost = true };
        gen.ContainersRealizedChanged += (_, e) =>
        {
            if (e.Action == ContainersChangedAction.Realized && e.RealizedContainers is { } realized)
                foreach (var c in realized)
                    panel.Children.Add(c);
            else if (e.Action == ContainersChangedAction.Unrealized && e.RemovedContainers is { } removed)
                foreach (var c in removed)
                    panel.Children.Remove(c);
        };
        return panel;
    }

    [Fact] // VV0.1: EnableVirtualization + SetSource ⇒ ContainerCount == itemCount, zero realized
    public void VV0_1_EnableVirtualization_ZeroRealized()
    {
        var (_, gen, _) = MakeVirtual(50);
        Assert.True(gen.IsVirtualizing);
        Assert.Equal(50, gen.ContainerCount);
        Assert.Equal(0, CountRealized(gen, 50));
        Assert.Null(gen.ContainerFromIndex(0));
        Assert.Null(gen.ContainerFromIndex(49));
    }

    [Fact] // VV0.2: RealizeRange materializes exactly the window; ContainerCount unchanged
    public void VV0_2_RealizeRange_MaterializesWindow()
    {
        var (_, gen, _) = MakeVirtual(50);
        gen.RealizeRange(10, 5); // 10..14

        for (var i = 0; i < 50; i++)
            Assert.Equal(i is >= 10 and < 15, gen.ContainerFromIndex(i) is not null);

        Assert.Equal(50, gen.ContainerCount); // still item-count
        Assert.Equal(5, CountRealized(gen, 50));
    }

    [Fact] // VV0.3: RealizeRange fires ContainersRealizedChanged(Realized), NOT ContainersChanged
    public void VV0_3_RealizeRange_FiresMaterializationChannelOnly()
    {
        var (_, gen, _) = MakeVirtual(50);
        var structural = 0;
        ContainersChangedEventArgs? realized = null;
        gen.ContainersChanged += (_, _) => structural++;
        gen.ContainersRealizedChanged += (_, e) => realized = e;

        gen.RealizeRange(10, 5);

        Assert.Equal(0, structural); // no spurious SelectionModel.ItemsInserted
        Assert.NotNull(realized);
        Assert.Equal(ContainersChangedAction.Realized, realized!.Action);
        Assert.Equal(10, realized.StartIndex);
        Assert.Equal(5, realized.Count);
        Assert.NotNull(realized.RealizedContainers);
        Assert.Equal(5, realized.RealizedContainers!.Count); // the authoritative newly-realized instance list
        Assert.Same(gen.ContainerFromIndex(10), realized.RealizedContainers[0]);
    }

    [Fact] // VV0.4: UnrealizeRange clears the slots + fires ContainersRealizedChanged(Unrealized), NOT ContainersChanged
    public void VV0_4_UnrealizeRange_FiresMaterializationChannelOnly()
    {
        var (_, gen, _) = MakeVirtual(50);
        gen.RealizeRange(10, 5);

        var structural = 0;
        ContainersChangedEventArgs? unreal = null;
        gen.ContainersChanged += (_, _) => structural++;
        gen.ContainersRealizedChanged += (_, e) => unreal = e;

        gen.UnrealizeRange(10, 5);

        Assert.Equal(0, structural); // no spurious SelectionModel.ItemsRemoved
        Assert.NotNull(unreal);
        Assert.Equal(ContainersChangedAction.Unrealized, unreal!.Action);
        Assert.NotNull(unreal.RemovedContainers);
        Assert.Equal(5, unreal.RemovedContainers!.Count);
        Assert.Equal(unreal.RemovedContainers.Count, unreal.Count); // Count mirrors the authoritative instance list
        Assert.Equal(0, CountRealized(gen, 50));
        Assert.Equal(50, gen.ContainerCount); // item count untouched
    }

    [Fact] // VV0.5: Unrealize honors the CD-P9-3 4-step order (ClearContainer → event → FinishUnrealize)
    public void VV0_5_Unrealize_StepOrder()
    {
        var (_, gen, _) = MakeVirtual(20);
        gen.RealizeRange(0, 3);
        var c1 = (ListBoxItem)gen.ContainerFromIndex(1)!;

        var stampStillSetDuringEvent = false;
        var contentClearedDuringEvent = false;
        gen.ContainersRealizedChanged += (_, e) =>
        {
            if (e.Action != ContainersChangedAction.Unrealized || e.RemovedContainers?.Contains(c1) != true)
                return;
            stampStillSetDuringEvent = gen.ItemFromContainer(c1) is not null; // FinishUnrealize (clears stamp) runs AFTER
            contentClearedDuringEvent = c1.Content is null;                   // ClearContainerForItem ran BEFORE
        };

        gen.UnrealizeRange(0, 3);

        Assert.True(stampStillSetDuringEvent, "the unrealize event must fire before FinishUnrealize clears the stamp");
        Assert.True(contentClearedDuringEvent, "ClearContainerForItem must run before the unrealize event");
        Assert.Null(gen.ItemFromContainer(c1)); // stamp cleared after
        Assert.Null(c1.LogicalParent);          // logical detached after
        Assert.Equal(-1, gen.IndexFromContainer(c1));
    }

    [Fact] // VV0.6: IndexFromContainer is the O(1) back-map; reflects structural shifts
    public void VV0_6_IndexFromContainer_BackMap()
    {
        var (_, gen, src) = MakeVirtual(20);
        gen.RealizeRange(3, 2); // 3,4
        var c4 = gen.ContainerFromIndex(4)!;
        Assert.Equal(4, gen.IndexFromContainer(c4));
        Assert.Equal(-1, gen.IndexFromContainer(new ListBoxItem())); // a stranger

        src.Insert(0, "head"); // everything shifts +1
        Assert.Equal(5, gen.IndexFromContainer(c4));
        Assert.Same(c4, gen.ContainerFromIndex(5));
    }

    [Fact] // VV0.7: :alternate parity is item-indexed (not realization-order / realized-position)
    public void VV0_7_Alternate_ItemIndexed()
    {
        var (_, gen, _) = MakeVirtual(20);
        gen.RealizeRange(4, 3); // realize 4,5,6 in one call (order independent of parity)
        Assert.False(((ListBoxItem)gen.ContainerFromIndex(4)!).HasCustomPseudoClass(":alternate")); // even
        Assert.True(((ListBoxItem)gen.ContainerFromIndex(5)!).HasCustomPseudoClass(":alternate"));   // odd
        Assert.False(((ListBoxItem)gen.ContainerFromIndex(6)!).HasCustomPseudoClass(":alternate"));  // even
    }

    [Fact] // VV0.8: keep-alive — UnrealizeRange skips a keyboard-focused container
    public void VV0_8_KeepAlive_SkipsFocusedContainer()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 14) });
        var lb = new ListBox();
        var gen = lb.ItemContainerGenerator;
        gen.EnableVirtualization();
        lb.ItemsSource = new ObservableCollection<string>(Enumerable.Range(0, 20).Select(i => $"i{i}"));

        var panel = WireStandInPanel(gen); // the V2-VSP stand-in (adopts via RealizedContainers)

        host.ShowRoot(panel);
        gen.RealizeRange(0, 10);
        host.RunUntilIdle();

        var focused = (ListBoxItem)gen.ContainerFromIndex(3)!;
        focused.Focus();
        host.RunUntilIdle();
        Assert.True(focused.IsKeyboardFocusWithin);

        gen.UnrealizeRange(0, 10);

        Assert.Same(focused, gen.ContainerFromIndex(3)); // pinned — stays realized
        Assert.Contains(focused, panel.Children);        // not visually detached
        Assert.Null(gen.ContainerFromIndex(0));          // the rest unrealized
        Assert.Null(gen.ContainerFromIndex(9));
        Assert.Equal(1, CountRealized(gen, 20));
    }

    [Fact] // VV0.9: Recycling reuses the same instance; Standard creates fresh
    public void VV0_9_Recycling_ReusesInstance()
    {
        var (_, recGen, _) = MakeVirtual(20, VirtualizationMode.Recycling);
        recGen.RealizeRange(0, 1);
        var first = recGen.ContainerFromIndex(0)!;
        recGen.UnrealizeRange(0, 1);  // first → pool
        recGen.RealizeRange(1, 1);
        Assert.Same(first, recGen.ContainerFromIndex(1)); // reused
        Assert.Equal("i1", ((ListBoxItem)recGen.ContainerFromIndex(1)!).Content); // re-prepared for the new item

        var (_, stdGen, _) = MakeVirtual(20, VirtualizationMode.Standard);
        stdGen.RealizeRange(0, 1);
        var s0 = stdGen.ContainerFromIndex(0)!;
        stdGen.UnrealizeRange(0, 1);  // discarded (no pool)
        stdGen.RealizeRange(1, 1);
        Assert.NotSame(s0, stdGen.ContainerFromIndex(1)); // fresh
    }

    [Fact] // VV0.10: recycle reset contract — a recycled container's selection does not bleed across items
    public void VV0_10_Recycle_ResetContract()
    {
        var (_, gen, _) = MakeVirtual(20, VirtualizationMode.Recycling);
        gen.RealizeRange(0, 1);
        var c = (ListBoxItem)gen.ContainerFromIndex(0)!;
        ((ISelectableContainer)c).SetIsSelectedFromOwner(true);
        Assert.True(c.IsSelected);

        gen.UnrealizeRange(0, 1); // c → pool (still IsSelected — ClearContainerForItem doesn't touch selection)
        gen.RealizeRange(1, 1);   // pops c, ResetRecycledContainer clears selection before re-preparing

        Assert.Same(c, gen.ContainerFromIndex(1));
        Assert.False(c.IsSelected);                       // reset — no bleed
        Assert.False(c.HasCustomPseudoClass(":selected")); // and the pseudo-class cleared
    }

    [Fact] // VV0.11: own-container (item IS a UIElement) is never recycled / cleared
    public void VV0_11_OwnContainer_NeverRecycled()
    {
        var lb = new ListBox();
        lb.ItemContainerGenerator.EnableVirtualization();
        var a = new ListBoxItem { Content = "A" };
        var b = new ListBoxItem { Content = "B" };
        lb.ItemsSource = new ObservableCollection<object> { a, b };
        var gen = lb.ItemContainerGenerator;

        gen.RealizeRange(0, 1);
        Assert.Same(a, gen.ContainerFromIndex(0)); // the item IS the container
        gen.UnrealizeRange(0, 1);
        Assert.Equal("A", a.Content);              // intact — ClearContainerForItem is a no-op for an own-container

        gen.RealizeRange(1, 1);
        Assert.Same(b, gen.ContainerFromIndex(1)); // NOT the pooled 'a' — own-containers never pool
        gen.RealizeRange(0, 1);
        Assert.Same(a, gen.ContainerFromIndex(0)); // re-realizing item 0 returns the same user element
    }

    [Fact] // VV0.12: item structural change keeps ContainerCount == itemCount + fires ContainersChanged
    public void VV0_12_StructuralChange_TracksItemCount()
    {
        var (_, gen, src) = MakeVirtual(10);
        gen.RealizeRange(2, 4); // 2..5
        var c3 = gen.ContainerFromIndex(3)!;

        ContainersChangedEventArgs? insert = null;
        gen.ContainersChanged += (_, e) => insert = e;

        src.Insert(1, "new");
        Assert.Equal(11, gen.ContainerCount);
        Assert.NotNull(insert);
        Assert.Equal(ContainersChangedAction.Realized, insert!.Action);
        Assert.Equal(1, insert.StartIndex);
        Assert.Equal(1, insert.Count);
        Assert.Same(c3, gen.ContainerFromIndex(4)); // shifted +1
        Assert.Null(gen.ContainerFromIndex(1));     // the inserted item stays unrealized

        var c4Now = gen.ContainerFromIndex(4)!;
        src.RemoveAt(2); // remove a realized item below c4Now
        Assert.Equal(10, gen.ContainerCount);
        Assert.Same(c4Now, gen.ContainerFromIndex(3)); // shifted −1
    }

    [Fact] // VV0.12b: removing a realized item tears down its container (recycled) and fires both channels
    public void VV0_12b_StructuralRemove_TearsDownRealized()
    {
        var (_, gen, src) = MakeVirtual(10);
        gen.RealizeRange(0, 4);
        var removedContainer = gen.ContainerFromIndex(1)!;

        var structural = 0;
        var materialization = 0;
        gen.ContainersChanged += (_, e) => { if (e.Action == ContainersChangedAction.Unrealized) structural++; };
        gen.ContainersRealizedChanged += (_, e) => { if (e.Action == ContainersChangedAction.Unrealized) materialization++; };

        src.RemoveAt(1);

        Assert.Equal(1, structural);            // SelectionModel sees ItemsRemoved
        Assert.Equal(1, materialization);       // the host detaches the destroyed realized container
        Assert.Equal(-1, gen.IndexFromContainer(removedContainer));
        Assert.Equal(9, gen.ContainerCount);
    }

    [Fact] // VV0.13: eager mode is untouched (no EnableVirtualization ⇒ eager full-realize)
    public void VV0_13_EagerMode_Untouched()
    {
        var lb = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
        var gen = lb.ItemContainerGenerator;
        Assert.False(gen.IsVirtualizing);
        Assert.Equal(3, gen.ContainerCount);
        Assert.NotNull(gen.ContainerFromIndex(0)); // eager: all realized
        Assert.NotNull(gen.ContainerFromIndex(2));
        Assert.Throws<InvalidOperationException>(() => gen.RealizeRange(0, 1)); // realize API is virtualizing-only
    }

    [Fact] // VV0.14: teardown unhooks the source (no INCC leak) and clears the recycle pool
    public void VV0_14_Teardown_UnhooksSourceAndClearsPool()
    {
        var (lb, gen, src) = MakeVirtual(10, VirtualizationMode.Recycling);
        gen.RealizeRange(0, 3);
        var pooledCandidate = gen.ContainerFromIndex(0)!;
        gen.UnrealizeRange(0, 3); // pooledCandidate → recycle pool

        gen.ReleaseSource(); // unhook the view's INotifyCollectionChanged + clear the pool

        src.Add("extra");
        Assert.Equal(10, gen.ContainerCount); // detached source no longer drives the generator (no leak)

        // The pool was cleared: re-sourcing + realizing does NOT hand back the dropped pooled instance.
        lb.ItemsSource = new ObservableCollection<string> { "x", "y", "z" };
        gen.RealizeRange(0, 1);
        Assert.NotSame(pooledCandidate, gen.ContainerFromIndex(0));
    }

    [Fact] // VV0.15: RealizeRange/UnrealizeRange are idempotent
    public void VV0_15_Idempotence()
    {
        var (_, gen, _) = MakeVirtual(20);
        gen.RealizeRange(0, 5);
        var c2 = gen.ContainerFromIndex(2);

        var events = 0;
        gen.ContainersRealizedChanged += (_, _) => events++;
        gen.RealizeRange(0, 5); // all already realized → no-op, no event, no duplicate
        Assert.Equal(0, events);
        Assert.Same(c2, gen.ContainerFromIndex(2));

        gen.RealizeRange(3, 5);  // 3,4 already realized; 5,6,7 new
        Assert.Equal(1, events); // one event for the newly-realized span
        Assert.Equal(8, CountRealized(gen, 20)); // 0..7

        events = 0;
        gen.UnrealizeRange(10, 5); // none realized → no-op, no event
        Assert.Equal(0, events);
    }

    [Fact] // VV0.16: VirtualizingPanel attached properties round-trip with the documented defaults
    public void VV0_16_VirtualizingPanel_AttachedProperties()
    {
        var lb = new ListBox();
        Assert.False(VirtualizingPanel.GetIsVirtualizing(lb));                              // default false (opt-in)
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(lb)); // default Recycling
        Assert.Equal(ScrollUnit.Item, VirtualizingPanel.GetScrollUnit(lb));                // default Item

        VirtualizingPanel.SetIsVirtualizing(lb, true);
        VirtualizingPanel.SetVirtualizationMode(lb, VirtualizationMode.Standard);
        VirtualizingPanel.SetScrollUnit(lb, ScrollUnit.Cell);

        Assert.True(VirtualizingPanel.GetIsVirtualizing(lb));
        Assert.Equal(VirtualizationMode.Standard, VirtualizingPanel.GetVirtualizationMode(lb));
        Assert.Equal(ScrollUnit.Cell, VirtualizingPanel.GetScrollUnit(lb));
    }

    [Fact] // VV0.17: switching an already-eager, sourced generator to virtualizing tears down + resets
    public void VV0_17_SwitchEagerToVirtualizing()
    {
        var lb = new ListBox { ItemsSource = new[] { "a", "b", "c", "d" } };
        var gen = lb.ItemContainerGenerator;
        Assert.Equal(4, CountRealized(gen, 4)); // eager: all realized

        gen.EnableVirtualization();
        Assert.True(gen.IsVirtualizing);
        Assert.Equal(4, gen.ContainerCount);    // item count preserved
        Assert.Equal(0, CountRealized(gen, 4)); // but nothing realized now
    }

    // ── audit-driven regression rows (V0 adversarial review) ──────────────────────────────────────────

    [Fact] // VV0.18: virtualizing Move re-keys realized containers to their post-move item index (forward + backward)
    public void VV0_18_Move_ReKeysRealized()
    {
        var (_, gen, src) = MakeVirtual(10);
        gen.RealizeRange(0, 10); // realize all so every move is observable

        ContainersChangedEventArgs? moved = null;
        var materialization = 0;
        gen.ContainersChanged += (_, e) => { if (e.Action == ContainersChangedAction.Moved) moved = e; };
        gen.ContainersRealizedChanged += (_, _) => materialization++;

        src.Move(2, 7); // forward
        AssertContainersMatchSource(gen, src);
        Assert.NotNull(moved);
        Assert.Equal(7, moved!.StartIndex);
        Assert.Equal(2, moved.OldStartIndex);
        Assert.Equal(0, materialization); // move is structural-only — no realize/unrealize

        src.Move(8, 1); // backward
        AssertContainersMatchSource(gen, src);
        Assert.Equal(0, materialization);
    }

    // Each realized container must sit at the index where the live source now holds its item, with a consistent
    // back-map and item-indexed :alternate parity. (Kills an identity-RemapMoveIndex / no-op-RestripeVirtual mutant.)
    private static void AssertContainersMatchSource(ItemContainerGenerator gen, ObservableCollection<string> src)
    {
        for (var i = 0; i < src.Count; i++)
        {
            var c = gen.ContainerFromIndex(i)!;
            Assert.Equal(src[i], gen.ItemFromContainer(c));
            Assert.Equal(i, gen.IndexFromContainer(c));
            Assert.Equal((i & 1) == 1, ((ListBoxItem)c).HasCustomPseudoClass(":alternate"));
        }
    }

    [Fact] // VV0.19: re-realizing a band across a kept-alive interior hole reports only the new instances (no double-adopt)
    public void VV0_19_RealizeAcrossKeepAliveHole()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 14) });
        var lb = new ListBox();
        var gen = lb.ItemContainerGenerator;
        gen.EnableVirtualization();
        lb.ItemsSource = new ObservableCollection<string>(Enumerable.Range(0, 20).Select(i => $"i{i}"));
        var panel = WireStandInPanel(gen);
        host.ShowRoot(panel);

        gen.RealizeRange(0, 10);
        host.RunUntilIdle();
        var pinned = (ListBoxItem)gen.ContainerFromIndex(5)!;
        pinned.Focus();
        host.RunUntilIdle();

        gen.UnrealizeRange(0, 10); // index 5 kept alive (focused); 0..4, 6..9 unrealized
        Assert.Same(pinned, gen.ContainerFromIndex(5));
        Assert.Same(pinned, Assert.Single(panel.Children)); // only the pinned one remains adopted

        ContainersChangedEventArgs? realized = null;
        gen.ContainersRealizedChanged += (_, e) => { if (e.Action == ContainersChangedAction.Realized) realized = e; };

        // Re-realize the band spanning the still-realized index 5 — the stand-in adopts RealizedContainers, so it
        // must NOT re-adopt the pinned container (which would throw "already in this collection").
        var ex = Record.Exception(() => gen.RealizeRange(0, 10));
        Assert.Null(ex);
        Assert.NotNull(realized);
        Assert.DoesNotContain(pinned, realized!.RealizedContainers!); // 5 is not reported as newly realized
        Assert.Equal(9, realized.RealizedContainers!.Count);          // 0..4, 6..9
        Assert.Equal(10, panel.Children.Count);                       // all 10 adopted, none doubled
    }

    [Fact] // VV0.20: a structural shift re-stamps :alternate to the NEW item-index parity (kills a no-op RestripeVirtual)
    public void VV0_20_Alternate_ReStampedOnShift()
    {
        var (_, gen, src) = MakeVirtual(20);
        gen.RealizeRange(4, 1); // item index 4 (even → no :alternate)
        var c = (ListBoxItem)gen.ContainerFromIndex(4)!;
        Assert.False(c.HasCustomPseudoClass(":alternate"));

        src.Insert(0, "head"); // c shifts 4 → 5 (odd → :alternate)
        Assert.Equal(5, gen.IndexFromContainer(c));
        Assert.True(c.HasCustomPseudoClass(":alternate"));

        src.RemoveAt(0); // c shifts back 5 → 4 (even → no :alternate)
        Assert.Equal(4, gen.IndexFromContainer(c));
        Assert.False(c.HasCustomPseudoClass(":alternate"));
    }

    [Fact] // VV0.21: a virtualizing generator under the eager ItemsPresenter no-ops structural events (no crash)
    public void VV0_21_RealItemsPresenter_Virtualizing_NoCrash()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var lb = new ListBox();
        lb.ItemContainerGenerator.EnableVirtualization();
        var src = new ObservableCollection<string>(Enumerable.Range(0, 20).Select(i => $"i{i}"));
        lb.ItemsSource = src;
        host.ShowRoot(lb); // the real control template → the real ItemsPresenter hosts the default StackPanel
        host.RunUntilIdle();

        // Pre-fix, the eager ItemsPresenter's index-aligned Moved/SyncAll handlers crashed on the sparse store.
        Assert.Null(Record.Exception(() => { src.Move(0, 8); host.RunUntilIdle(); }));
        Assert.Null(Record.Exception(() => { src.Insert(3, "x"); host.RunUntilIdle(); }));
        Assert.Null(Record.Exception(() => { src.RemoveAt(2); host.RunUntilIdle(); }));
        Assert.Null(Record.Exception(() => { src.Clear(); host.RunUntilIdle(); }));
    }

    [Fact] // VV0.22: SelectedItem/SelectedItems resolve for a selected-but-unrealized index (source-indexed, not via container)
    public void VV0_22_SelectedItem_UnrealizedIndex()
    {
        var (lb, gen, _) = MakeVirtual(50);
        Assert.Equal(0, CountRealized(gen, 50)); // nothing realized

        lb.SelectedIndex = 30;
        Assert.Equal(30, lb.SelectedIndex);
        Assert.Equal("i30", lb.SelectedItem);                  // resolved from the source, not a (null) container
        Assert.Equal("i30", Assert.Single(lb.SelectedItems));

        lb.SelectedItem = "i12"; // reverse: select by item with nothing realized — must NOT silently clear
        Assert.Equal(12, lb.SelectedIndex);
        Assert.Equal("i12", lb.SelectedItem);
    }

    [Fact] // VV0.23: Standard mode never pools; own-containers never pool (the recycle push-guards are load-bearing)
    public void VV0_23_PoolPushGuards()
    {
        var (_, std, _) = MakeVirtual(10, VirtualizationMode.Standard);
        std.RealizeRange(0, 3);
        std.UnrealizeRange(0, 3);
        Assert.Equal(0, std.PooledCount); // Standard discards — pool stays empty

        var lb = new ListBox();
        var gen = lb.ItemContainerGenerator;
        gen.EnableVirtualization(VirtualizationMode.Recycling);
        lb.ItemsSource = new ObservableCollection<object> { new ListBoxItem { Content = "own" }, "generated" };

        gen.RealizeRange(0, 1); // own-container
        gen.UnrealizeRange(0, 1);
        Assert.Equal(0, gen.PooledCount); // own-container NOT pooled

        gen.RealizeRange(1, 1); // generated
        gen.UnrealizeRange(1, 1);
        Assert.Equal(1, gen.PooledCount); // generated IS pooled
    }

    [Fact] // VV0.24: ReleaseSource tears down pooled containers — a Source binding's INPC subscription is released
    public void VV0_24_ReleaseSource_TearsDownPooledContainers()
    {
        var (_, gen, _) = MakeVirtual(10, VirtualizationMode.Recycling);
        gen.RealizeRange(0, 1);
        var c = (ListBoxItem)gen.ContainerFromIndex(0)!;

        var vm = new CountingSource();
        c.SetBinding(ListBoxItem.IsSelectedProperty, new Binding(nameof(CountingSource.Flag)) { Source = vm });
        Assert.Equal(1, vm.SubscriberCount); // the binding subscribed to the source INPC

        gen.UnrealizeRange(0, 1); // c → pool (a Source binding survives the detach — disposed only on TearDown)
        gen.ReleaseSource();      // tears down pooled containers before clearing

        Assert.Equal(0, vm.SubscriberCount); // the binding was torn down — no leak
    }

    [Fact] // VV0.25: TreeView recycle resets IsExpanded/IsSelected (owner reset hook for a non-ISelectableContainer)
    public void VV0_25_TreeView_RecycleResetsExpandedAndSelected()
    {
        var tv = new TreeView();
        var gen = tv.ItemContainerGenerator;
        gen.EnableVirtualization(VirtualizationMode.Recycling);
        tv.ItemsSource = new ObservableCollection<string> { "n0", "n1" };

        gen.RealizeRange(0, 1);
        var node = (TreeViewItem)gen.ContainerFromIndex(0)!;
        node.IsExpanded = true;
        node.IsSelected = true;

        gen.UnrealizeRange(0, 1); // node → pool
        gen.RealizeRange(1, 1);   // pops the same node, re-prepared for "n1"

        Assert.Same(node, gen.ContainerFromIndex(1)); // recycled
        Assert.False(node.IsExpanded);                // reset — no bleed
        Assert.False(node.IsSelected);                // reset — no bleed
    }

    private sealed class CountingSource : INotifyPropertyChanged
    {
        private bool _flag;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Flag
        {
            get => _flag;
            set
            {
                _flag = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flag)));
            }
        }

        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;
    }
}
