using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C3 — the SelectionModel (pure index-based; reused by ListBox/TabControl). One test per row.
public sealed class Section16_Selection
{
    private static SelectionModel Single() => new() { Mode = SelectionMode.Single };
    private static SelectionModel Multiple() => new() { Mode = SelectionMode.Multiple };

    private static (SelectionModel Model, List<SelectionChangedEventArgs> Events) WithEvents(SelectionModel model)
    {
        var events = new List<SelectionChangedEventArgs>();
        model.SelectionChanged += (_, e) => events.Add(e);
        return (model, events);
    }

    [Fact] // C3.1: Single Select sets the set, lead, and anchor
    public void C3_1_Single_Select()
    {
        var m = Single();
        m.Select(2);
        Assert.Equal([2], m.SelectedIndexes);
        Assert.Equal(2, m.SelectedIndex);
        Assert.Equal(2, m.AnchorIndex);
    }

    [Fact] // C3.2: Single Select replaces
    public void C3_2_Single_SelectReplaces()
    {
        var m = Single();
        m.Select(1);
        m.Select(3);
        Assert.Equal([3], m.SelectedIndexes);
        Assert.False(m.IsSelected(1));
    }

    [Fact] // C3.3: Single Toggle of an unselected index selects it
    public void C3_3_Single_ToggleSelects()
    {
        var m = Single();
        m.Toggle(2);
        Assert.Equal([2], m.SelectedIndexes);
    }

    [Fact] // C3.4: Single Toggle of the selected index deselects it
    public void C3_4_Single_ToggleDeselects()
    {
        var m = Single();
        m.Select(2);
        m.Toggle(2);
        Assert.Empty(m.SelectedIndexes);
        Assert.Equal(-1, m.SelectedIndex);
    }

    [Fact] // C3.5: Single SelectRangeFromAnchor collapses to a single select
    public void C3_5_Single_RangeCollapses()
    {
        var m = Single();
        m.Select(1);
        m.SelectRangeFromAnchor(4);
        Assert.Equal([4], m.SelectedIndexes);
    }

    [Fact] // C3.6: Multiple Select replaces (plain-click semantics)
    public void C3_6_Multiple_SelectReplaces()
    {
        var m = Multiple();
        m.Toggle(0);
        m.Select(2);
        Assert.Equal([2], m.SelectedIndexes);
    }

    [Fact] // C3.7: Multiple Toggle accumulates, ascending
    public void C3_7_Multiple_ToggleAccumulates()
    {
        var m = Multiple();
        m.Toggle(3);
        m.Toggle(1);
        Assert.Equal([1, 3], m.SelectedIndexes); // ascending regardless of toggle order
    }

    [Fact] // C3.8: Multiple Toggle of a selected index removes it
    public void C3_8_Multiple_ToggleRemoves()
    {
        var m = Multiple();
        m.Toggle(1);
        m.Toggle(3);
        m.Toggle(1);
        Assert.Equal([3], m.SelectedIndexes);
    }

    [Fact] // C3.8b: toggling the LEAD off with others remaining promotes Max(remaining) to lead (CD-P9-12)
    public void C3_8b_Multiple_ToggleLeadOff_PromotesMax()
    {
        var m = Multiple();
        m.Toggle(1);
        m.Toggle(3); // lead = 3
        m.Toggle(3); // remove the lead
        Assert.Equal([1], m.SelectedIndexes);
        Assert.Equal(1, m.SelectedIndex); // Max of {1}
    }

    [Fact] // C3.9: Multiple forward range from anchor; anchor preserved, endpoint is the lead
    public void C3_9_Multiple_RangeForward()
    {
        var m = Multiple();
        m.Select(1);
        m.SelectRangeFromAnchor(4);
        Assert.Equal([1, 2, 3, 4], m.SelectedIndexes);
        Assert.Equal(1, m.AnchorIndex);
        Assert.Equal(4, m.SelectedIndex);
    }

    [Fact] // C3.10: Multiple backward range from anchor
    public void C3_10_Multiple_RangeBackward()
    {
        var m = Multiple();
        m.Select(4);
        m.SelectRangeFromAnchor(1);
        Assert.Equal([1, 2, 3, 4], m.SelectedIndexes);
        Assert.Equal(1, m.SelectedIndex);
    }

    [Fact] // C3.11: SelectedIndex setter selects then clears
    public void C3_11_SelectedIndexSetter()
    {
        var m = Multiple();
        m.SelectedIndex = 2;
        Assert.Equal([2], m.SelectedIndexes);
        m.SelectedIndex = -1;
        Assert.Empty(m.SelectedIndexes);
        Assert.Equal(-1, m.SelectedIndex);
    }

    [Fact] // C3.12: a manually-set anchor is honored by SelectRangeFromAnchor
    public void C3_12_ManualAnchor()
    {
        var m = Multiple();
        m.AnchorIndex = 0;
        m.SelectRangeFromAnchor(2);
        Assert.Equal([0, 1, 2], m.SelectedIndexes);
    }

    [Fact] // C3.13: an insertion before the selection shifts it; no SelectionChanged (same item)
    public void C3_13_InsertBeforeShifts_NoEvent()
    {
        var (m, events) = WithEvents(Multiple());
        m.Select(2);
        events.Clear();
        m.ItemsInserted(0, 2);
        Assert.Equal([4], m.SelectedIndexes);
        Assert.Empty(events); // membership unchanged
    }

    [Fact] // C3.14: insert-after leaves the selection; insert-at shifts it
    public void C3_14_InsertAtVsAfter()
    {
        var m = Multiple();
        m.Select(2);
        m.ItemsInserted(3, 1);
        Assert.Equal([2], m.SelectedIndexes); // insert after the selected index — no shift
        m.ItemsInserted(2, 1);
        Assert.Equal([3], m.SelectedIndexes); // insert AT the selected index — pushed down
    }

    [Fact] // C3.15: a removal below the selection shifts it down; no SelectionChanged
    public void C3_15_RemoveBelowShifts_NoEvent()
    {
        var (m, events) = WithEvents(Multiple());
        m.Select(3);
        events.Clear();
        m.ItemsRemoved(0, 2);
        Assert.Equal([1], m.SelectedIndexes);
        Assert.Empty(events);
    }

    [Fact] // C3.16: Single — removing the selected index empties the model (the ListBox re-targets at P9.3)
    public void C3_16_Single_RemoveSelected_GoesEmpty()
    {
        var (m, events) = WithEvents(Single());
        m.Select(2);
        events.Clear();
        m.ItemsRemoved(2, 1);
        Assert.Empty(m.SelectedIndexes);
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal([2], Assert.Single(events).RemovedIndexes);
    }

    [Fact] // C3.17: Multiple — removing below a surviving lead shifts everything; lead survives
    public void C3_17_Multiple_RemoveBelow_LeadSurvives()
    {
        var m = Multiple();
        m.Toggle(1);
        m.Toggle(3);
        m.Toggle(5); // lead = 5
        m.ItemsRemoved(1, 1); // drop 1; 3→2, 5→4
        Assert.Equal([2, 4], m.SelectedIndexes);
        Assert.Equal(4, m.SelectedIndex); // lead 5 survived, shifted to 4
    }

    [Fact] // C3.17b: a NON-lead selected index is dropped while the lead survives — the event still fires
    public void C3_17b_Multiple_DropNonLead_LeadSurvives_Fires()
    {
        var (m, events) = WithEvents(Multiple());
        m.Toggle(1);
        m.Toggle(3); // lead = 3
        events.Clear();
        m.ItemsRemoved(1, 1); // drop 1 (a non-lead selected index); 3→2
        Assert.Equal([2], m.SelectedIndexes);
        Assert.Equal(2, m.SelectedIndex);       // lead 3 survived, shifted to 2
        Assert.Equal([1], Assert.Single(events).RemovedIndexes);
    }

    [Fact] // C3.18: Multiple — the lead itself is dropped → relocates to the nearest surviving selected index
    public void C3_18_Multiple_RemoveLead_Relocates()
    {
        var (m, events) = WithEvents(Multiple());
        m.Toggle(1);
        m.Toggle(5);
        m.Toggle(3); // lead = 3
        events.Clear();
        m.ItemsRemoved(3, 1); // drop the lead (3); 5→4
        Assert.Equal([1, 4], m.SelectedIndexes);
        Assert.Equal(4, m.SelectedIndex); // nearest survivor to index 3 is 4 (dist 1) over 1 (dist 2)
        Assert.Equal([3], Assert.Single(events).RemovedIndexes);
    }

    [Fact] // C3.18b: lead dropped with two EQUIDISTANT survivors — the tie resolves to the smaller (CD-P9-9)
    public void C3_18b_Multiple_RemoveLead_TieResolvesSmaller()
    {
        var m = Multiple();
        m.Toggle(2);
        m.Toggle(5);
        m.Toggle(3); // lead = 3
        m.ItemsRemoved(3, 1); // drop the lead (3); 5→4 → survivors {2,4}, both distance 1 from index 3
        Assert.Equal([2, 4], m.SelectedIndexes);
        Assert.Equal(2, m.SelectedIndex); // tie → the smaller index
    }

    [Fact] // C3.19: Reset clears everything + fires with the removed set
    public void C3_19_Reset()
    {
        var (m, events) = WithEvents(Multiple());
        m.Select(1);
        m.Toggle(3);
        events.Clear();
        m.Reset();
        Assert.Empty(m.SelectedIndexes);
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(-1, m.AnchorIndex);
        Assert.Equal([1, 3], Assert.Single(events).RemovedIndexes);
    }

    [Fact] // C3.20: SelectionChanged carries the added/removed delta on Toggle
    public void C3_20_SelectionChangedDelta()
    {
        var (m, events) = WithEvents(Multiple());
        m.Toggle(2);
        var e = Assert.Single(events);
        Assert.Equal([2], e.AddedIndexes);
        Assert.Empty(e.RemovedIndexes);
    }

    [Fact] // C3.21: an idempotent re-Select raises nothing
    public void C3_21_IdempotentSelect_NoEvent()
    {
        var (m, events) = WithEvents(Multiple());
        m.Select(2);
        events.Clear();
        m.Select(2);
        Assert.Empty(events);
    }

    [Fact] // C3.22: Single caps cardinality — Toggle of a new index replaces the prior one
    public void C3_22_Single_ToggleCapsCardinality()
    {
        var m = Single();
        m.Select(1);
        m.Toggle(3);
        Assert.Equal([3], m.SelectedIndexes);
    }

    [Fact] // C3.23: range with no anchor behaves as a plain Select (and sets the anchor)
    public void C3_23_RangeNoAnchor()
    {
        var m = Multiple(); // anchor starts at -1
        m.SelectRangeFromAnchor(2);
        Assert.Equal([2], m.SelectedIndexes);
        Assert.Equal(2, m.AnchorIndex);
    }

    [Fact] // C3.24: narrowing Multiple→Single collapses to the lead + fires the membership delta
    public void C3_24_ModeNarrow_CollapsesToLead()
    {
        var (m, events) = WithEvents(Multiple());
        m.Toggle(1);
        m.Toggle(2);
        m.Toggle(3); // lead = 3
        events.Clear();
        m.Mode = SelectionMode.Single;
        Assert.Equal([3], m.SelectedIndexes);
        Assert.Equal(3, m.SelectedIndex);
        Assert.Equal([1, 2], Assert.Single(events).RemovedIndexes);
    }

    [Fact] // C3.25: a removal below shifts both the anchor and the lead
    public void C3_25_RemoveBelow_ShiftsAnchorAndLead()
    {
        var m = Multiple();
        m.Select(2);                // anchor = 2
        m.SelectRangeFromAnchor(5); // {2,3,4,5}, anchor 2, lead 5
        m.ItemsRemoved(0, 1);       // shift everything down by 1
        Assert.Equal([1, 2, 3, 4], m.SelectedIndexes);
        Assert.Equal(1, m.AnchorIndex);
        Assert.Equal(4, m.SelectedIndex);
    }

    [Fact] // C3.26: when the anchor falls in the removed range it re-anchors to the (relocated) lead
    public void C3_26_RemoveAnchor_ReAnchorsToLead()
    {
        var m = Multiple();
        m.Select(2); // anchor = 2, lead = 2
        m.ItemsRemoved(2, 1); // drop the anchor+lead → model empties
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(-1, m.AnchorIndex); // re-anchored to the (now -1) lead
    }

    [Fact] // C3.27: an insertion before the anchor shifts it (with the lead)
    public void C3_27_InsertBefore_ShiftsAnchor()
    {
        var m = Multiple();
        m.Select(2); // anchor = 2, lead = 2
        m.ItemsInserted(0, 1);
        Assert.Equal(3, m.AnchorIndex);
        Assert.Equal(3, m.SelectedIndex);
    }

    [Fact] // C3.28: out-of-range structural hooks are no-ops (no negative leak); SelectedIndexes is read-only
    public void C3_28_OutOfRange_NoOp_AndReadOnly()
    {
        var m = Multiple();
        m.Toggle(0);
        m.Toggle(1);
        m.ItemsRemoved(-1, 1); // out-of-range → no-op; must NOT inject a negative index
        m.ItemsInserted(-3, 2); // out-of-range → no-op
        Assert.Equal([0, 1], m.SelectedIndexes); // unchanged, no negatives

        Assert.IsNotType<int[]>(m.SelectedIndexes); // a read-only wrapper, not the live backing array
    }
}
