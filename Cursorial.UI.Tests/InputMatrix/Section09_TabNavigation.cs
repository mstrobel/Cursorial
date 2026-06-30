using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Input matrix §9 — Tab and directional navigation (N118–N135, N214–N221). One test per row. Tab rows use the
/// matrix tree <c>Root → F1, F2, F3</c> (root default Cycle); directional rows use a Canvas-arranged
/// grid (window-translated bounds feed the ND17 scoring).
/// </summary>
public class Section09_TabNavigation
{
    private static KeyEvent Key(Key key, KeyModifiers modifiers = KeyModifiers.None) => new()
    {
        Key = key,
        Modifiers = modifiers,
        Kind = KeyEventKind.Down,
        Text = ReadOnlyMemory<char>.Empty,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static bool HasState(Probe probe, InteractionState state) => (probe.InteractionStateInternal & state) != 0;

    /// <summary>The §9 Tab tree: <c>Root → F1, F2, F3</c> (Btns, document order), shown — activation focuses F1.</summary>
    private static (UITestHost Host, List<string> Log, Probe Root, Btn F1, Btn F2, Btn F3) CreateTabTree()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var f2 = new Btn("F2", log);
        var f3 = new Btn("F3", log);
        root.AddChild(f1);
        root.AddChild(f2);
        root.AddChild(f3);
        host.ShowRoot(root);
        log.Clear();
        return (host, log, root, f1, f2, f3);
    }

    [Fact]
    public void N118_Tab_DocumentOrderOnTiedIndexes_Handled_TabMethod()
    {
        var (host, _, _, f1, f2, _) = CreateTabTree();
        using var _host = host;
        var focus = host.Application.FocusManager;
        Assert.Same(f1, focus.FocusedElement);
        FocusNavigationMethod? method = null;
        f2.AddHandler(UIElement.GotFocusEvent, (_, e) => method = e.Method);

        var result = host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));

        Assert.Same(f2, focus.FocusedElement); // document order when TabIndex ties at int.MaxValue
        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal(FocusNavigationMethod.Tab, method);
        Assert.True(HasState(f2, InteractionState.FocusVisible));
    }

    [Fact]
    public void N119_TabIndexAscending_TiesByDocumentOrder()
    {
        var (host, _, _, f1, f2, f3) = CreateTabTree();
        using var _host = host;
        var focus = host.Application.FocusManager;
        f1.TabIndex = 2;
        f2.TabIndex = 1;
        f3.TabIndex = 2;
        Assert.True(f2.Focus());

        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f1, focus.FocusedElement); // F2(1) → F1(2, earlier in document order)
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f3, focus.FocusedElement); // → F3(2)
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f2, focus.FocusedElement); // → wrap
    }

    [Fact]
    public void N120_TabFromLast_WrapsToFirst_RootDefaultCycle()
    {
        var (host, _, _, f1, _, f3) = CreateTabTree();
        using var _host = host;
        Assert.True(f3.Focus());

        host.SendKey(Cursorial.Input.Key.Tab); // end-to-end through the harness pipeline
        host.RunFrame();

        Assert.Same(f1, host.Application.FocusManager.FocusedElement);
    }

    [Fact]
    public void N121_ShiftTabFromFirst_WrapsToLast()
    {
        var (host, _, _, f1, _, f3) = CreateTabTree();
        using var _host = host;
        Assert.Same(f1, host.Application.FocusManager.FocusedElement);

        host.SendKey(Cursorial.Input.Key.Tab, KeyModifiers.Shift);
        host.RunFrame();

        Assert.Same(f3, host.Application.FocusManager.FocusedElement);
    }

    [Theory]
    [InlineData(true)]  // Tab → first
    [InlineData(false)] // Shift+Tab → last
    public void N122_NoFocusedElement_StartsAtActiveRootFirstOrLast(bool forward)
    {
        var (host, _, _, f1, _, f3) = CreateTabTree();
        using var _host = host;
        var focus = host.Application.FocusManager;
        focus.ClearFocus();

        var result = host.Application.InputDispatcher.ProcessEvent(
            Key(Cursorial.Input.Key.Tab, forward ? KeyModifiers.None : KeyModifiers.Shift));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Same(forward ? f1 : f3, focus.FocusedElement);
    }

    [Fact]
    public void N123_IsTabStopFalse_SkippedByTab_StillProgrammaticallyFocusable()
    {
        var (host, _, _, f1, f2, f3) = CreateTabTree();
        using var _host = host;
        f2.IsTabStop = false;
        Assert.Same(f1, host.Application.FocusManager.FocusedElement);

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f3, host.Application.FocusManager.FocusedElement); // skips F2

        Assert.True(f2.Focus()); // programmatic focus still works
        Assert.Same(f2, host.Application.FocusManager.FocusedElement);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("hidden")]
    [InlineData("collapsed")]
    public void N124_IneligibleStops_Skipped(string kind)
    {
        var (host, _, _, f1, f2, f3) = CreateTabTree();
        using var _host = host;
        switch (kind)
        {
            case "disabled":
                f2.IsEnabled = false;
                break;
            case "hidden":
                f2.Visibility = Visibility.Hidden;
                break;
            default:
                f2.Visibility = Visibility.Collapsed;
                break;
        }

        Assert.True(f1.Focus());
        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));

        Assert.Same(f3, host.Application.FocusManager.FocusedElement);
    }

    [Fact]
    public void N125_TabNavigationNone_ExcludesDescendants()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var pane = new Probe("Pane", log);
        var f2 = new Btn("F2", log);
        var f3 = new Btn("F3", log);
        root.AddChild(f1);
        root.AddChild(pane);
        pane.AddChild(f2);
        root.AddChild(f3);
        KeyboardNavigation.SetTabNavigation(pane, KeyboardNavigationMode.None);
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;
        Assert.Same(f1, focus.FocusedElement);

        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f3, focus.FocusedElement); // F2 unreachable
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f1, focus.FocusedElement); // the cycle is {F1, F3}
    }

    [Fact]
    public void N126_InnerCycleContainer_TrapsTab()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var inner = new Probe("Inner", log);
        var g1 = new Btn("G1", log);
        var g2 = new Btn("G2", log);
        root.AddChild(f1);
        root.AddChild(inner);
        inner.AddChild(g1);
        inner.AddChild(g2);
        KeyboardNavigation.SetTabNavigation(inner, KeyboardNavigationMode.Cycle);
        host.ShowRoot(root);
        Assert.True(g2.Focus());

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));

        // Nearest self-or-ancestor Cycle is the inner container: wrap inside it, never out to F1.
        Assert.Same(g1, host.Application.FocusManager.FocusedElement);
    }

    /// <summary>The ND16 tree: <c>Root → F1, L(Once) → G1, G2, F3</c>, shown (activation focuses F1).</summary>
    private static (UITestHost Host, Probe L, Btn F1, Btn G1, Btn G2, Btn F3) CreateOnceTree()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var l = new Probe("L", log);
        var g1 = new Btn("G1", log);
        var g2 = new Btn("G2", log);
        var f3 = new Btn("F3", log);
        root.AddChild(f1);
        root.AddChild(l);
        l.AddChild(g1);
        l.AddChild(g2);
        root.AddChild(f3);
        KeyboardNavigation.SetTabNavigation(l, KeyboardNavigationMode.Once);
        host.ShowRoot(root);
        return (host, l, f1, g1, g2, f3);
    }

    [Fact]
    public void N127_Once_EntersAtFirstEligible_NextTabExitsPastContainer()
    {
        var (host, _, f1, g1, _, f3) = CreateOnceTree();
        using var _host = host;
        var focus = host.Application.FocusManager;
        Assert.Same(f1, focus.FocusedElement);

        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(g1, focus.FocusedElement); // entry at the first eligible descendant (no memory)
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(f3, focus.FocusedElement); // exits past the whole container
    }

    [Fact]
    public void N128_Once_ScopeMemoryEntry()
    {
        var (host, l, _, _, g2, _) = CreateOnceTree();
        using var _host = host;
        FocusManager.SetFocusedElement(l, g2); // the ListBox shape: memory primes the entry

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));

        Assert.Same(g2, host.Application.FocusManager.FocusedElement);
    }

    [Fact]
    public void N129_Once_ShiftTabEntersOnce_ThenExitsBackward()
    {
        var (host, _, f1, g1, _, f3) = CreateOnceTree();
        using var _host = host;
        var focus = host.Application.FocusManager;
        Assert.True(f3.Focus());

        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab, KeyModifiers.Shift));
        Assert.Same(g1, focus.FocusedElement); // entry (either direction) = memory/first (ND16)
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab, KeyModifiers.Shift));
        Assert.Same(f1, focus.FocusedElement); // exits past the whole container
    }

    [Fact]
    public void N130_NoOtherCandidate_FocusUnmoved_Unhandled()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        root.AddChild(f1);
        host.ShowRoot(root);
        Assert.Same(f1, host.Application.FocusManager.FocusedElement);

        var result = host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));

        Assert.Same(f1, host.Application.FocusManager.FocusedElement);
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result); // handled only when focus moved
    }

    // ───────────────────────────── directional (ND17) ─────────────────────────────

    /// <summary>
    /// The directional grid: a Canvas-shaped container (<paramref name="mode"/>) holding 2×1 Btns —
    /// Center (10,5), Left (4,5), Right (16,5), Up (10,2), Down (10,8) — laid out (one frame).
    /// </summary>
    private static (UITestHost Host, Btn Center, Btn Left, Btn Right, Btn Up, Btn Down) CreateGrid(
        DirectionalNavigationMode mode)
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var container = new CanvasProbe("Grid", log, 40, 20);
        root.Place(container, 0, 0);
        KeyboardNavigation.SetDirectionalNavigation(container, mode);

        var center = NewButton("C", log);
        var left = NewButton("L", log);
        var right = NewButton("R", log);
        var up = NewButton("U", log);
        var down = NewButton("D", log);
        container.Place(center, 10, 5);
        container.Place(left, 4, 5);
        container.Place(right, 16, 5);
        container.Place(up, 10, 2);
        container.Place(down, 10, 8);

        host.ShowRoot(root);
        host.RunFrame(); // arrange — directional scoring reads window-translated bounds
        return (host, center, left, right, up, down);

        static Btn NewButton(string name, List<string> log) => new(name, log) { Natural = new Size(2, 1) };
    }

    [Fact]
    public void N131_Contained_RightArrow_NearestCandidate_DirectionalMethod()
    {
        var (host, center, _, right, _, _) = CreateGrid(DirectionalNavigationMode.Contained);
        using var _host = host;
        Assert.True(center.Focus());
        FocusNavigationMethod? method = null;
        right.AddHandler(UIElement.GotFocusEvent, (_, e) => method = e.Method);

        var result = host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.RightArrow));

        Assert.Same(right, host.Application.FocusManager.FocusedElement);
        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal(FocusNavigationMethod.Directional, method);
    }

    [Fact]
    public void N132_Scoring_RowAlignedBeatsNearerButOffset()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var container = new CanvasProbe("Grid", log, 40, 20);
        root.Place(container, 0, 0);
        KeyboardNavigation.SetDirectionalNavigation(container, DirectionalNavigationMode.Contained);

        var center = new Btn("C", log) { Natural = new Size(2, 1) };
        var near = new Btn("Near", log) { Natural = new Size(2, 1) };     // facing 1, rows offset 2 → 1 + 2×2 = 5
        var aligned = new Btn("Aligned", log) { Natural = new Size(2, 1) }; // facing 4, gap 0 → 4
        container.Place(center, 10, 5);
        container.Place(near, 13, 8);
        container.Place(aligned, 16, 5);
        host.ShowRoot(root);
        host.RunFrame();
        Assert.True(center.Focus());

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.RightArrow));

        // facing + 2 × orthogonal-gap: gap 0 beats nearer-but-offset when 2×gap exceeds the delta.
        Assert.Same(aligned, host.Application.FocusManager.FocusedElement);
    }

    [Fact]
    public void N133_Contained_AtEdge_FocusUnmoved_ArrowNotStolen()
    {
        var (host, _, _, right, _, _) = CreateGrid(DirectionalNavigationMode.Contained);
        using var _host = host;
        Assert.True(right.Focus());

        var result = host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.RightArrow));

        Assert.Same(right, host.Application.FocusManager.FocusedElement);
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
    }

    [Fact]
    public void N134_Cycle_AtEdge_WrapsToFarthestOpposite()
    {
        var (host, _, left, right, _, _) = CreateGrid(DirectionalNavigationMode.Cycle);
        using var _host = host;
        Assert.True(right.Focus());

        var result = host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.RightArrow));

        Assert.Same(left, host.Application.FocusManager.FocusedElement); // leftmost = farthest opposite
        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
    }

    [Fact]
    public void N135_DefaultNone_ArrowsNeverEngage()
    {
        var (host, center, _, _, _, _) = CreateGrid(DirectionalNavigationMode.None);
        using var _host = host;
        Assert.True(center.Focus());

        var result = host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.RightArrow));

        Assert.Same(center, host.Application.FocusManager.FocusedElement);
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result); // arrows stay free for controls
    }

    // ───────────────────────────── generalized scope entry (ND33) ─────────────────────────────

    /// <summary>The ND33 tree: <c>Root → F1, S(IsFocusScope[, Once]) → G1, G2, F3</c>, shown (activation focuses F1).</summary>
    private static (UITestHost Host, Probe S, Btn F1, Btn G1, Btn G2, Btn F3) CreateScopeTree(bool once)
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var s = new Probe("S", log);
        var g1 = new Btn("G1", log);
        var g2 = new Btn("G2", log);
        var f3 = new Btn("F3", log);
        root.AddChild(f1);
        root.AddChild(s);
        s.AddChild(g1);
        s.AddChild(g2);
        root.AddChild(f3);
        FocusManager.SetIsFocusScope(s, true);
        if (once)
            KeyboardNavigation.SetTabNavigation(s, KeyboardNavigationMode.Once);
        host.ShowRoot(root);
        return (host, s, f1, g1, g2, f3);
    }

    [Fact] // N214 — entry into a non-Once focus scope with no memory falls to the first eligible (document order)
    public void N214_NonOnceScope_NoMemory_EntersAtFirstEligible()
    {
        var (host, _, _, g1, _, _) = CreateScopeTree(once: false);
        using var _host = host;
        var focus = host.Application.FocusManager;

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab)); // F1 → enter S

        Assert.Same(g1, focus.FocusedElement);
    }

    [Fact] // N215 — entry into ANY focus scope restores its remembered focus (the generalized memory clause)
    public void N215_NonOnceScope_ScopeMemoryEntry()
    {
        var (host, s, _, _, g2, _) = CreateScopeTree(once: false);
        using var _host = host;
        FocusManager.SetFocusedElement(s, g2); // memory on the (non-Once) scope

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab)); // F1 → enter S at memory

        Assert.Same(g2, host.Application.FocusManager.FocusedElement);
    }

    [Fact] // N216 — an intra-scope move is NOT redirected to memory (the trap-prevention gate)
    public void N216_IntraScopeMove_NotRedirectedToMemory()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var s = new Probe("S", log);
        var g1 = new Btn("G1", log);
        var g2 = new Btn("G2", log);
        var g3 = new Btn("G3", log);
        var f3 = new Btn("F3", log);
        root.AddChild(f1);
        root.AddChild(s);
        s.AddChild(g1);
        s.AddChild(g2);
        s.AddChild(g3);
        root.AddChild(f3);
        FocusManager.SetIsFocusScope(s, true);
        host.ShowRoot(root);

        Assert.True(g1.Focus());               // focus inside S (this records memory = G1)
        FocusManager.SetFocusedElement(s, g3); // override S's memory to G3 (focus stays on G1)

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab)); // intra-scope: G1 → next

        // Document order (G2), NOT the memory (G3) — tabbing THROUGH a scope's members must not snap to memory.
        Assert.Same(g2, host.Application.FocusManager.FocusedElement);
    }

    [Fact] // N217 — a Once+IsFocusScope host resolves through the single Once ladder (no double redirect; ≡ N128)
    public void N217_OnceAndFocusScope_SingleOnceLadder()
    {
        var (host, s, _, _, g2, _) = CreateScopeTree(once: true);
        using var _host = host;
        FocusManager.SetFocusedElement(s, g2);

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab)); // F1 → enter the Once scope at memory

        Assert.Same(g2, host.Application.FocusManager.FocusedElement);
    }

    [Fact] // N218 — an outward Tab move into an ENCLOSING scope's own member is NOT trapped back inside (audit fix)
    public void N218_OutwardTab_NotTrappedInNestedScope()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Probe("A", log);
        var b = new Probe("B", log);
        var b1 = new Btn("b1", log);
        var b2 = new Btn("b2", log);
        var a2 = new Btn("a2", log);
        var tail = new Btn("tail", log);
        root.AddChild(a);
        a.AddChild(b);
        b.AddChild(b1);
        b.AddChild(b2);
        a.AddChild(a2);     // a2 is A's own member (sibling of the inner scope B)
        root.AddChild(tail);
        FocusManager.SetIsFocusScope(a, true);
        FocusManager.SetIsFocusScope(b, true);
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;
        Assert.True(b1.Focus());

        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(b2, focus.FocusedElement);   // intra-B
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(a2, focus.FocusedElement);   // OUT of B into A's own member — not snapped back to b1 (the trap)
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab));
        Assert.Same(tail, focus.FocusedElement); // OUT of A past the whole nest
    }

    [Fact] // N219 — the same outward move under directional navigation reaches the enclosing scope's member
    public void N219_OutwardDirectional_NotTrappedInNestedScope()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var a = new CanvasProbe("A", log, 80, 24);
        var b = new CanvasProbe("B", log, 40, 20);
        root.Place(a, 0, 0);
        a.Place(b, 0, 0);
        KeyboardNavigation.SetDirectionalNavigation(root, DirectionalNavigationMode.Contained);
        FocusManager.SetIsFocusScope(a, true);
        FocusManager.SetIsFocusScope(b, true);

        var g1 = new Btn("g1", log) { Natural = new Size(2, 1) };
        var g2 = new Btn("g2", log) { Natural = new Size(2, 1) };
        var a2 = new Btn("a2", log) { Natural = new Size(2, 1) };
        b.Place(g1, 2, 5);
        b.Place(g2, 10, 5);
        a.Place(a2, 40, 5);   // A's own member, to the right of B
        host.ShowRoot(root);
        host.RunFrame();
        Assert.True(g2.Focus());

        host.Application.InputDispatcher.ProcessEvent(Key(Cursorial.Input.Key.RightArrow));

        Assert.Same(a2, host.Application.FocusManager.FocusedElement); // not redirected back to g1 inside B
    }

    [Fact] // N220 — backward (Shift+Tab) entry into a no-memory non-Once scope lands on the LAST member (reverse order)
    public void N220_BackwardEntry_LandsOnLastMember()
    {
        var (host, _, _, g1, g2, f3) = CreateScopeTree(once: false);
        using var _host = host;
        var focus = host.Application.FocusManager;
        Assert.True(f3.Focus());

        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab, KeyModifiers.Shift));
        Assert.Same(g2, focus.FocusedElement); // enters S at its LAST member, not the first
        dispatcher.ProcessEvent(Key(Cursorial.Input.Key.Tab, KeyModifiers.Shift));
        Assert.Same(g1, focus.FocusedElement); // continues reverse document order within S
    }

    [Fact] // N221 — a non-stop marker (Label) inside a scope forwards to the next document-order stop, not scope memory
    public void N221_MarkerInsideScope_FindNext_NotRedirectedToMemory()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var s = new Probe("S", log);
        var lbl = new Probe("Lbl", log); // a non-focusable marker (Label stand-in)
        var g1 = new Btn("G1", log);
        var g2 = new Btn("G2", log);
        root.AddChild(s);
        s.AddChild(lbl);
        s.AddChild(g1);
        s.AddChild(g2);
        FocusManager.SetIsFocusScope(s, true);
        host.ShowRoot(root);
        FocusManager.SetFocusedElement(s, g2); // S remembers G2

        var next = host.Application.FocusManager.FindNext(lbl);

        Assert.Same(g1, next); // the next document-order stop in S — NOT the memory (G2)
    }
}
