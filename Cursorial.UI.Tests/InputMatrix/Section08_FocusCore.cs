using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

using Keys = Cursorial.Input.Key;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>
/// Input matrix §8 — focus core: <c>Focus()</c>, the physical singleton, scopes (N95–N117).
/// One test per row.
/// </summary>
public class Section08_FocusCore
{
    private static KeyEvent Key(Key key, KeyModifiers modifiers = KeyModifiers.None) => new()
    {
        Key = key,
        Modifiers = modifiers,
        Kind = KeyEventKind.Down,
        Text = ReadOnlyMemory<char>.Empty,
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private static MouseEvent MouseMove(int column, int row) => new()
    {
        Kind = MouseEventKind.Move,
        Position = new CellPosition(column, row),
        Button = MouseButton.None,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private static bool HasState(Probe probe, InteractionState state) => (probe.InteractionStateInternal & state) != 0;

    /// <summary>Shown chain <c>Root → A(Btn), B(Btn)</c> with the activation auto-focus cleared and the log empty.</summary>
    private static (UITestHost Host, List<string> Log, Probe Root, Btn A, Btn B, FocusManager Focus) CreatePair()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Btn("A", log);
        var b = new Btn("B", log);
        root.AddChild(a);
        root.AddChild(b);
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;
        focus.ClearFocus(); // activation auto-focused A (N115); these rows establish focus explicitly
        log.Clear();
        return (host, log, root, a, b, focus);
    }

    [Fact]
    public void N095_DefaultFocusableFalse_FocusRefused()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var probe = new Probe("P", log);
        root.AddChild(probe);
        host.ShowRoot(root);

        Assert.False(probe.Focus());
        Assert.Null(host.Application.FocusManager.FocusedElement);
        Assert.False(probe.IsFocused);
    }

    [Fact]
    public void N096_Focus_SetsSingleton_Mirrors_FocusWithinChain_FocusVisible()
    {
        var (host, _, root, a, b, focus) = CreatePair();
        using var _host = host;

        Assert.True(b.Focus()); // Programmatic; LastModality starts Keyboard (ND8)

        Assert.Same(b, focus.FocusedElement);
        Assert.True(b.IsFocused);
        Assert.True(HasState(b, InteractionState.Focused));
        Assert.True(b.IsKeyboardFocusWithin);
        Assert.True(root.IsKeyboardFocusWithin); // every ancestor
        Assert.True(HasState(b, InteractionState.FocusWithin));
        Assert.True(HasState(root, InteractionState.FocusWithin));
        Assert.False(a.IsKeyboardFocusWithin);
        Assert.True(HasState(b, InteractionState.FocusVisible)); // ND8: startup modality Keyboard
    }

    [Fact]
    public void N097_DetachedElement_FocusRefused()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        host.ShowRoot(root);
        var detached = new Btn("B", log); // never attached

        Assert.False(detached.Focus());
        Assert.Null(host.Application.FocusManager.FocusedElement);
    }

    [Theory]
    [InlineData(false)] // B itself disabled
    [InlineData(true)]  // disabled ancestor
    public void N098_Disabled_FocusRefused(bool disableAncestor)
    {
        var (host, _, _, a, _, focus) = CreatePair();
        using var _host = host;
        var b = new Btn("B2", a.Log);
        a.AddChild(b);
        if (disableAncestor)
            a.IsEnabled = false;
        else
            b.IsEnabled = false;

        Assert.False(b.Focus());
        Assert.Null(focus.FocusedElement);
    }

    [Theory]
    [InlineData(Visibility.Collapsed, false)]
    [InlineData(Visibility.Hidden, false)]
    [InlineData(Visibility.Hidden, true)] // Hidden ancestor — pinned: Hidden gates focus too
    public void N099_NotEffectivelyVisible_FocusRefused(Visibility visibility, bool onAncestor)
    {
        var (host, _, _, a, _, focus) = CreatePair();
        using var _host = host;
        var b = new Btn("B2", a.Log);
        a.AddChild(b);
        if (onAncestor)
            a.Visibility = visibility;
        else
            b.Visibility = visibility;

        Assert.False(b.Focus());
        Assert.Null(focus.FocusedElement);
    }

    [Fact]
    public void N100_NonFocusableTarget_NoAncestorFallback()
    {
        var (host, _, _, a, _, focus) = CreatePair();
        using var _host = host;
        var target = new Probe("T", a.Log); // not focusable, under the focusable A
        a.AddChild(target);

        Assert.False(target.Focus());

        Assert.Null(focus.FocusedElement); // P untouched — no ancestor fallback (doc §7.7)
        Assert.False(a.IsFocused);
    }

    [Fact]
    public void N101_FocusMove_StateCommits_ThenLostFocus_ThenGotFocus()
    {
        var (host, log, _, a, b, focus) = CreatePair();
        using var _host = host;
        Assert.True(a.Focus());
        log.Clear();

        (UIElement? Old, UIElement? New, FocusNavigationMethod Method)? lostArgs = null;
        (UIElement? Old, UIElement? New, FocusNavigationMethod Method)? gotArgs = null;
        a.AddLog(UIElement.LostFocusEvent, "A.h-lost", action: e =>
        {
            lostArgs = (e.OldFocus, e.NewFocus, e.Method);

            // State committed before events (one batch at the styling stage — ND11): the
            // handler observes the post-transition world.
            Assert.Same(b, focus.FocusedElement);
            Assert.True(HasState(b, InteractionState.Focused));
            Assert.False(HasState(a, InteractionState.Focused));
            Assert.False(HasState(a, InteractionState.FocusVisible));
        });
        b.AddLog(UIElement.GotFocusEvent, "B.h-got", action: e => gotArgs = (e.OldFocus, e.NewFocus, e.Method));

        Assert.True(b.Focus());

        Assert.Equal(
            [
                "A.OnLostFocus", "A.h-lost", "Root.OnLostFocus", // LostFocus bubbles from A
                "B.OnGotFocus", "B.h-got", "Root.OnGotFocus",    // then GotFocus bubbles from B
            ],
            log);
        Assert.Equal((a, b, FocusNavigationMethod.Programmatic), lostArgs);
        Assert.Equal((a, b, FocusNavigationMethod.Programmatic), gotArgs);
    }

    private sealed class CountingBoolObserver : IValueObserver<bool>
    {
        public int Changes;

        public void OnPropertyChanged(UIObject source, UIProperty property, bool oldValue, bool newValue, BindingPriority priority)
            => Changes++;
    }

    [Fact]
    public void N102_CommonAncestor_FocusWithinStable_ZeroNotifications()
    {
        var (host, _, root, a, b, _) = CreatePair();
        using var _host = host;
        Assert.True(a.Focus());
        Assert.True(root.IsKeyboardFocusWithin);

        var observer = new CountingBoolObserver();
        using var subscription = root.AddObserver(UIElement.IsKeyboardFocusWithinProperty, observer);

        Assert.True(b.Focus());

        Assert.True(root.IsKeyboardFocusWithin);   // stays true across the move
        Assert.Equal(0, observer.Changes);         // common-prefix diff + equality gate (N102)
    }

    [Fact]
    public void N103_ReadOnlyMirrors_KeylessWritesThrow()
    {
        var (host, _, _, _, b, _) = CreatePair();
        using var _host = host;

        Assert.Throws<InvalidOperationException>(() => b.SetValue(UIElement.IsFocusedProperty, true));
        Assert.Throws<InvalidOperationException>(() => b.SetValue(UIElement.IsKeyboardFocusWithinProperty, true));
    }

    [Theory]
    [InlineData(FocusNavigationMethod.Tab, false, true)]
    [InlineData(FocusNavigationMethod.Directional, false, true)]
    [InlineData(FocusNavigationMethod.AccessKey, false, true)]
    [InlineData(FocusNavigationMethod.Restore, false, true)]
    [InlineData(FocusNavigationMethod.Restore, true, true)] // Restore always sets — even under pointer modality
    [InlineData(FocusNavigationMethod.Programmatic, false, true)]  // Programmatic ∧ Keyboard modality
    [InlineData(FocusNavigationMethod.Programmatic, true, false)]  // Programmatic ∧ Pointer modality
    [InlineData(FocusNavigationMethod.Pointer, false, false)]
    public void N104_FocusVisiblePolicy_PerMethodAndModality(FocusNavigationMethod method, bool pointerModality, bool expected)
    {
        var (host, _, _, _, b, focus) = CreatePair();
        using var _host = host;
        if (pointerModality)
        {
            host.RunFrame(); // settle layout so the hit test is valid
            host.Application.InputDispatcher.ProcessEvent(MouseMove(0, 0)); // LastModality → Pointer
        }

        Assert.True(focus.SetFocus(b, method));

        Assert.Equal(expected, HasState(b, InteractionState.FocusVisible));
    }

    [Fact]
    public void N105_PointerFocusedElement_ActivationRestore_SetsFocusVisible()
    {
        var (host, _, root, _, b, focus) = CreatePair();
        using var _host = host;
        host.RunFrame();
        host.Application.InputDispatcher.ProcessEvent(MouseMove(0, 0)); // pointer modality
        Assert.True(b.Focus(FocusNavigationMethod.Pointer));
        Assert.False(HasState(b, InteractionState.FocusVisible));

        focus.OnWindowDeactivated(root);
        focus.OnWindowActivated(root); // restore from scope memory, method = Restore

        Assert.Same(b, focus.FocusedElement);
        Assert.True(HasState(b, InteractionState.FocusVisible)); // Restore always sets (doc §7.7)
    }

    [Fact]
    public void N106_SameElementRefocus_NoReRaise_FocusVisibleUpdates()
    {
        var (host, log, _, _, b, _) = CreatePair();
        using var _host = host;
        host.RunFrame();
        host.Application.InputDispatcher.ProcessEvent(MouseMove(0, 0));
        Assert.True(b.Focus(FocusNavigationMethod.Pointer)); // FocusVisible off
        Assert.False(HasState(b, InteractionState.FocusVisible));
        log.Clear();

        Assert.True(b.Focus(FocusNavigationMethod.Tab));

        Assert.DoesNotContain(log, static entry => entry.Contains("GotFocus") || entry.Contains("LostFocus"));
        Assert.True(HasState(b, InteractionState.FocusVisible)); // updates per method
        // I4 asserts the ak.OnFocusChanged notification leg of this row.
    }

    [Fact]
    public void N107_ReentrantSetFocus_LastWins_EventsExactlyOnce_DebugDepthCap()
    {
        var (host, log, _, _, b, focus) = CreatePair();
        using var _host = host;
        var c = new Btn("C", log);
        ((Probe)b.VisualParent!).AddChild(c);
        b.AddHandler(UIElement.GotFocusEvent, (_, _) => c.Focus());
        log.Clear();

        b.Focus();

        Assert.Same(c, focus.FocusedElement); // last-wins
        Assert.Equal(1, log.Count(static entry => entry == "B.OnGotFocus"));
        Assert.Equal(1, log.Count(static entry => entry == "B.OnLostFocus"));
        Assert.Equal(1, log.Count(static entry => entry == "C.OnGotFocus"));

#if DEBUG
        // The pathological loop: two handlers refocusing each other — the depth cap (8) refuses
        // the deepest request with a diagnostic instead of overflowing.
        var violations = new List<string>();
        Action<string> capture = violations.Add;
        InputDiagnostics.ContractViolation += capture;
        try
        {
            var d = new Btn("D", log);
            var e = new Btn("E", log);
            ((Probe)b.VisualParent!).AddChild(d);
            ((Probe)b.VisualParent!).AddChild(e);
            d.AddHandler(UIElement.GotFocusEvent, (_, _) => e.Focus());
            e.AddHandler(UIElement.GotFocusEvent, (_, _) => d.Focus());

            d.Focus();

            Assert.NotEmpty(violations);
        }
        finally
        {
            InputDiagnostics.ContractViolation -= capture;
        }
#endif
    }

    [Fact]
    public void N108_ClearFocus_StateCleared_LostFocusRaised_KeysTargetActiveRoot()
    {
        var (host, log, _, a, _, focus) = CreatePair();
        using var _host = host;
        Assert.True(a.Focus());
        log.Clear();

        focus.ClearFocus();

        Assert.Null(focus.FocusedElement);
        Assert.False(a.IsFocused);
        Assert.False(HasState(a, InteractionState.Focused | InteractionState.FocusVisible));
        Assert.Contains("A.OnLostFocus", log);

        log.Clear();
        host.Application.InputDispatcher.ProcessEvent(Key(Keys.Enter));
        Assert.Equal(["Root.OnPreviewKeyDown", "Root.OnKeyDown"], log); // fallback target = ActiveRoot
    }

    [Fact]
    public void N109_FocusedElementDetached_RepairsToNearestFocusableAncestor()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Btn("A", log); // the focusable ancestor
        var b = new Btn("B", log);
        root.AddChild(a);
        a.AddChild(b);
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;
        Assert.True(b.Focus());
        FocusNavigationMethod? method = null;
        a.AddHandler(UIElement.GotFocusEvent, (_, e) => method = e.Method);

        a.RemoveChild(b);

        Assert.Same(a, focus.FocusedElement);
        Assert.Equal(FocusNavigationMethod.Programmatic, method);
        Assert.False(HasState(a, InteractionState.FocusVisible)); // repair never shows the visual
    }

    [Fact]
    public void N110_NoFocusableAncestor_RepairsToScopeRootFirstTabStop()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var pane = new Probe("P", log);
        var b = new Btn("B", log);
        var c = new Btn("C", log);
        root.AddChild(pane);
        pane.AddChild(b);
        root.AddChild(c);
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;
        Assert.True(b.Focus());

        pane.RemoveChild(b);

        Assert.Same(c, focus.FocusedElement); // scope root (Root) → first tab-ordered focusable
    }

    [Fact]
    public void N111_NothingFocusableRemains_FocusCleared_KeysFallBackToRoot()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var b = new Btn("B", log);
        root.AddChild(b);
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;
        Assert.Same(b, focus.FocusedElement); // activation auto-focus (N115)

        root.RemoveChild(b);

        Assert.Null(focus.FocusedElement);
        log.Clear();
        host.Application.InputDispatcher.ProcessEvent(Key(Keys.Enter));
        Assert.Equal(["Root.OnPreviewKeyDown", "Root.OnKeyDown"], log);
    }

    /// <summary>Builds the N112 nested-scope tree: <c>Root(scope) → { A(Btn), Pane(scope) → Btn }</c>, shown.</summary>
    private static (UITestHost Host, Probe Root, Btn A, Probe Pane, Btn Inner) CreateNestedScopeTree()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Btn("A", log);
        var pane = new Probe("Pane", log);
        var inner = new Btn("Btn", log);
        root.AddChild(a);
        root.AddChild(pane);
        pane.AddChild(inner);
        FocusManager.SetIsFocusScope(pane, true);
        host.ShowRoot(root); // the harness marks Root a scope (N117)
        return (host, root, a, pane, inner);
    }

    [Fact]
    public void N112_GetFocusScope_NearestSelfOrAncestorScope()
    {
        var (host, root, a, pane, inner) = CreateNestedScopeTree();
        using var _host = host;

        Assert.Same(pane, FocusManager.GetFocusScope(inner)); // nearest ancestor scope
        Assert.Same(pane, FocusManager.GetFocusScope(pane));  // self-inclusive
        Assert.Same(root, FocusManager.GetFocusScope(a));     // outside Pane → Root
    }

    [Fact]
    public void N113_ScopeMemory_RecordedOnNearestScopeOnly()
    {
        var (host, root, a, pane, inner) = CreateNestedScopeTree();
        using var _host = host;
        Assert.True(a.Focus()); // records Root's memory = A
        Assert.Same(a, FocusManager.GetFocusedElement(root));

        Assert.True(inner.Focus());

        Assert.Same(inner, FocusManager.GetFocusedElement(pane)); // nearest scope records
        Assert.Same(a, FocusManager.GetFocusedElement(root));     // outer memory survives the inner excursion
    }

    [Fact]
    public void N114_ScopeMemory_ClearedEagerlyOnDetach_RestoreFallsToFirstTabStop()
    {
        var (host, root, a, pane, inner) = CreateNestedScopeTree();
        using var _host = host;
        Assert.True(inner.Focus());
        Assert.Same(inner, FocusManager.GetFocusedElement(pane));

        pane.RemoveChild(inner);

        Assert.Null(FocusManager.GetFocusedElement(pane)); // eager — no pinned subtree (N114)

        // Restore later falls to the first tab stop: clear the root scope's own memory so the
        // activation can only consult the (now empty) record, then cycle activation.
        FocusManager.SetFocusedElement(root, null);
        var focus = host.Application.FocusManager;
        focus.OnWindowDeactivated(root);
        focus.OnWindowActivated(root);
        Assert.Same(a, focus.FocusedElement); // first tab-ordered focusable
    }

    [Fact]
    public void N115_ShowRoot_Activates_InitialFocusFirstTabStop_RestoreMethod()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var f2 = new Btn("F2", log);
        root.AddChild(f1);
        root.AddChild(f2);
        FocusNavigationMethod? method = null;
        f1.AddHandler(UIElement.GotFocusEvent, (_, e) => method = e.Method);

        host.ShowRoot(root);

        var focus = host.Application.FocusManager;
        Assert.Same(root, focus.ActiveRoot); // OnWindowActivated ran
        Assert.Same(f1, focus.FocusedElement); // no memory → first tab-ordered focusable
        Assert.Equal(FocusNavigationMethod.Restore, method);
        Assert.True(HasState(f1, InteractionState.FocusVisible)); // Restore always sets

        // Zero focusables: no focus; keys still target the root.
        using var host2 = UITestHost.Create();
        var log2 = new List<string>();
        var root2 = new Probe("Root", log2);
        root2.AddChild(new Probe("P", log2));
        host2.ShowRoot(root2);
        Assert.Null(host2.Application.FocusManager.FocusedElement);
        host2.Application.InputDispatcher.ProcessEvent(Key(Keys.Enter));
        Assert.Equal(["Root.OnPreviewKeyDown", "Root.OnKeyDown"], log2);
    }

    [Fact]
    public void N116_DeactivateActivate_RestoresFromMemory_DisabledFallsToFirstTabStop()
    {
        var (host, _, root, a, b, focus) = CreatePair();
        using var _host = host;
        Assert.True(b.Focus()); // memory = B on the root scope

        focus.OnWindowDeactivated(root);
        Assert.Null(focus.ActiveRoot);
        focus.OnWindowActivated(root);
        Assert.Same(b, focus.FocusedElement); // restored from memory, validated

        // Disabled meanwhile → restore falls to the first tab-ordered focusable instead.
        b.IsEnabled = false;
        focus.OnWindowDeactivated(root);
        focus.OnWindowActivated(root);
        Assert.Same(a, focus.FocusedElement);
    }

    [Fact]
    public void N117_IsFocusScope_DefaultFalse_WindowRootTrue_Settable()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Probe("A", log);
        root.AddChild(a);

        Assert.False(FocusManager.GetIsFocusScope(a));    // default false
        Assert.False(FocusManager.GetIsFocusScope(root)); // not yet shown

        host.ShowRoot(root);
        Assert.True(FocusManager.GetIsFocusScope(root));  // set by the harness convention (P2; S4 at P7)

        FocusManager.SetIsFocusScope(a, true);            // settable on any element
        Assert.True(FocusManager.GetIsFocusScope(a));
        Assert.Same(a, FocusManager.GetFocusScope(a));
    }

    [Theory]
    [InlineData("disable-self")]
    [InlineData("collapse-self")]
    [InlineData("disable-ancestor")]
    public void N208_InPlaceInvalidation_RepairsFocus_ND28(string mode)
    {
        // Root → panel → leaf(Btn focused); other(Btn) a sibling under Root — the repair target
        // (no focusable ancestor exists, so repair falls to the scope root's first tab stop).
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var panel = new Probe("Panel", log);
        var leaf = new Btn("Leaf", log);
        var other = new Btn("Other", log);
        root.AddChild(panel);
        panel.AddChild(leaf);
        root.AddChild(other);
        host.ShowRoot(root);

        var focus = host.Application.FocusManager;
        Assert.True(leaf.Focus());

        FocusNavigationMethod? lostMethod = null;
        leaf.AddHandler(UIElement.LostFocusEvent, (_, e) => lostMethod = e.Method);
        log.Clear();

        switch (mode)
        {
            case "disable-self":
                leaf.IsEnabled = false;
                break;
            case "collapse-self":
                leaf.Visibility = Visibility.Collapsed;
                break;
            default:
                panel.IsEnabled = false; // an ancestor containing focus
                break;
        }

        // Repair exactly as on detach: LostFocus at the old element, method Programmatic,
        // no FocusVisible on the repaired target.
        Assert.Same(other, focus.FocusedElement);
        Assert.False(leaf.IsFocused);
        Assert.Contains("Leaf.OnLostFocus", log);
        Assert.Equal(FocusNavigationMethod.Programmatic, lostMethod);
        Assert.Equal(0u, (uint)(other.InteractionStateInternal & InteractionState.FocusVisible));

        // Subsequent keys route to the repaired target, never to the disabled/hidden element.
        log.Clear();
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent
        {
            Key = Keys.Enter,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = ReadOnlyMemory<char>.Empty,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        Assert.Contains("Other.OnKeyDown", log);
        Assert.DoesNotContain("Leaf.OnKeyDown", log);
    }

    [Fact]
    public void N209_SubtreeDetachWithFocusInside_OneTransition_OutsideTheDoomedSubtree()
    {
        // Focus on a deep leaf; its focusable parent and grandparent live INSIDE the removed
        // subtree (would be valid repair targets mid-walk); `outside` is the only valid target.
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var subtreeRoot = new Btn("Grand", log);   // focusable grandparent (doomed)
        var parent = new Btn("Parent", log);       // focusable parent (doomed)
        var leaf = new Btn("Leaf", log);
        var outside = new Btn("Outside", log);
        root.AddChild(subtreeRoot);
        subtreeRoot.AddChild(parent);
        parent.AddChild(leaf);
        root.AddChild(outside);
        host.ShowRoot(root);

        var focus = host.Application.FocusManager;
        Assert.True(leaf.Focus());

        // Got/LostFocus bubble — filter on Source so each entry names the element that actually
        // gained/lost focus (Source == the dispatch target, ND22), not every route node.
        var transitions = new List<string>();
        foreach (var probe in new[] { subtreeRoot, parent, leaf, outside })
        {
            var name = probe.LogName;
            var self = probe;
            probe.AddHandler(UIElement.GotFocusEvent, (_, e) =>
            {
                if (ReferenceEquals(e.Source, self))
                    transitions.Add($"got:{name}");
            });
            probe.AddHandler(UIElement.LostFocusEvent, (_, e) =>
            {
                if (ReferenceEquals(e.Source, self))
                    transitions.Add($"lost:{name}");
            });
        }

        root.RemoveChild(subtreeRoot);

        // Exactly ONE transition (ND30): Leaf → Outside; the doomed focusable ancestors never
        // receive focus or GotFocus even though the bottom-up walk leaves them attached-looking.
        Assert.Same(outside, focus.FocusedElement);
        Assert.Equal(["lost:Leaf", "got:Outside"], transitions);
        Assert.False(subtreeRoot.IsFocused);
        Assert.False(parent.IsFocused);
    }

    [Fact]
    public void N210_NestedTransitionFromLostFocusHandler_WinsTheGotFocusTail_ND31()
    {
        var (host, _, _, a, b, focus) = CreatePair();
        using var _host = host;
        var c = new Btn("C", a.Log);
        ((Probe)a.VisualParent!).AddChild(c);

        Assert.True(a.Focus());

        var events = new List<string>();
        bool? gotWhileUnfocused = null;
        // ReSharper disable AccessToModifiedClosure
        a.AddHandler(UIElement.LostFocusEvent, (_, _) =>
        {
            events.Add("A.LostFocus");
            c.Focus(); // the nested transition completes inside the handler
        });
        b.AddHandler(UIElement.LostFocusEvent, (_, _) => events.Add("B.LostFocus"));
        b.AddHandler(UIElement.GotFocusEvent, (_, _) =>
        {
            events.Add("B.GotFocus");
            gotWhileUnfocused = !b.IsFocused;
        });
        c.AddHandler(UIElement.GotFocusEvent, (_, _) => events.Add("C.GotFocus"));
        // ReSharper restore AccessToModifiedClosure

        b.Focus();

        // The nested transition wins (last-wins): A.LostFocus → B.LostFocus → C.GotFocus, and NO
        // GotFocus at B — an element never observes GotFocus while IsFocused == false.
        Assert.Same(c, focus.FocusedElement);
        Assert.Equal(["A.LostFocus", "B.LostFocus", "C.GotFocus"], events);
        Assert.Null(gotWhileUnfocused);
    }
}
