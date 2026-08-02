using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// Phase 4 integration: the design doc §14 P4 exit criteria proven end-to-end through
/// <see cref="UIHeadlessHost"/> — every scenario crosses the full spine (binding install → source
/// notification → the property store → styling/layout/render → composited cells), not an engine
/// harness. Covers: (a) an MVVM TwoWay round trip against an INPC viewmodel (VM change → cell, target
/// write → VM through <see cref="UIObject.SetCurrentValue{T}(StyledProperty{T}, T)"/> with the binding
/// preserved), (b) the same round trip against a POCO using the convention-matched <c>[PropertyName]Changed</c>
/// ladder rung (no INPC), (c) DataContext inheritance + rebind re-targeting a child binding, (d) <c>ElementName</c>
/// and <see cref="RelativeSource"/> <c>FindAncestor</c> resolution, (e) When-driven styling — a
/// viewmodel bool flips a <see cref="Style"/> rule's activation, asserted via the styled property's
/// effective value and the rendered cell (the P3+P4 seam closed), (f) the teardown-sweep no-leak proof
/// (detach a subtree bound to a long-lived VM; the VM has no surviving subscriptions — the closed P1
/// gap), and (g) the LostFocus-trigger flush on terminal focus-out (the <c>EditCommitRequested</c>
/// pulse, focus RETAINED).
/// </summary>
public sealed class Phase4EndToEndTests
{
    // ───────────────────────────── local elements ─────────────────────────────
    // Self-contained on purpose (the Phase2/3 convention): the BindingMatrix fixtures define
    // `BindWidget`/`Vm`, so this file brings its own leaves so the namespace stays isolated.

    /// <summary>
    /// A bindable card leaf. <see cref="LabelProperty"/> is the bound text (rendered into the row),
    /// <see cref="FillProperty"/> the background (AffectsRender), <see cref="ActiveProperty"/> the
    /// styled flag the <c>When</c>-rule flips. <see cref="LabelProperty"/> is two-way-by-default so
    /// the default binding mode picks TwoWay (the edit round trip).
    /// </summary>
    private sealed class Card : UIElement
    {
        public static readonly StyledProperty<string?> LabelProperty =
            UIProperty.Register<Card, string?>("Label");

        public static readonly StyledProperty<Color> FillProperty =
            UIProperty.Register<Card, Color>("Fill");

        public static readonly StyledProperty<int> ActiveProperty =
            UIProperty.Register<Card, int>("Active");

        static Card()
        {
            AffectsRender<Card>(LabelProperty, FillProperty, ActiveProperty);
            BindsTwoWayByDefault<Card>(LabelProperty);
        }

        public Card(bool focusable = false) => Focusable = focusable;

        public string? Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public Color Fill => GetValue(FillProperty);

        protected override Size MeasureOverride(Size availableSize) => new(12, 1);

        protected override void Render(RenderContext context)
        {
            context.FillOpaque(context.Bounds, Fill);
            if (Label is { Length: > 0 } label)
                context.DrawText(0, 0, label, Color.FromRgb(255, 255, 255));
        }
    }

    /// <summary>The selector type resolver over the file's private leaf.</summary>
    private sealed class Resolver : ISelectorTypeResolver
    {
        public static readonly Resolver Instance = new();

        public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
        {
            ambiguousCandidates = null;
            return typeName == "Card" ? typeof(Card) : null;
        }
    }

    /// <summary>A style with a parsed selector and constant setters (the matrix's <c>R(sel){…}</c> shape).</summary>
    private static Style R(string selector, params (UIProperty Property, object? Value)[] setters)
    {
        var style = new Style(selector, Resolver.Instance);
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    /// <summary>The canonical INPC viewmodel for the MVVM rows.</summary>
    private sealed class CardVm : INotifyPropertyChanged
    {
        private string? _title;
        private bool _highlighted;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string? Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public bool Highlighted
        {
            get => _highlighted;
            set => Set(ref _highlighted, value);
        }

        /// <summary>Subscriber count via the backing delegate's invocation list (the leak proof).</summary>
        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// A POCO with NO <see cref="INotifyPropertyChanged"/> — change notification rides the
    /// convention-matched <c>[PropertyName]Changed</c> ladder rung (the 2026-06-11 source-ladder pin).
    /// </summary>
    private sealed class PocoVm
    {
        private string? _title;

        public event EventHandler? TitleChanged;

        public string? Title
        {
            get => _title;
            set
            {
                _title = value;
                TitleChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Subscriber count on the convention event (the per-hop wiring proof).</summary>
        public int TitleChangedSubscriberCount => TitleChanged?.GetInvocationList().Length ?? 0;
    }

    /// <summary>A converter projecting a bool → one of two fill colors (the When-style alternative path probe).</summary>
    // ReSharper disable once UnusedType.Local
    private sealed class BoolToFillConverter : IValueConverter
    {
        public Color WhenTrue { get; init; }

        public Color WhenFalse { get; init; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? WhenTrue : WhenFalse;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private static readonly Color BaseFill = Color.FromRgb(10, 10, 10);
    private static readonly Color HotFill = Color.FromRgb(200, 60, 40);

    // ───────────────────────────── (a) MVVM TwoWay round trip (INPC) ─────────────────────────────

    /// <summary>
    /// The MVVM round trip end-to-end: a TwoWay binding on an INPC viewmodel. The forward leg — a VM
    /// property change reaches the bound styled property and re-rasters into the composited cells. The
    /// reverse leg — a target write through <see cref="UIObject.SetCurrentValue{T}(StyledProperty{T}, T)"/>
    /// flows back to the VM with the binding PRESERVED (the §13 SetCurrentValue contract), and a subsequent
    /// VM change still drives the target (proving the binding survived the local write).
    /// </summary>
    [Fact]
    public void MvvmTwoWayRoundTrip_Inpc_VmChangeRendersToCells_TargetWriteFlowsBack_BindingPreserved()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var card = new Card();
        var vm = new CardVm { Title = "hello" };
        card.DataContext = vm;
        root.Children.Add(card);

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        // TwoWay by default (LabelProperty is BindsTwoWayByDefault) — no explicit Mode needed.
        card.SetBinding(Card.LabelProperty, new Binding(nameof(CardVm.Title)));

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // Forward: the initial VM value reached the target and rendered (card is the only child, row 0).
        Assert.Equal("hello", card.Label);
        Assert.StartsWith("hello", host.GetRowText(0));

        // Forward: a VM change drives the target and re-renders the SAME frame on the next pump.
        vm.Title = "world!";
        host.RunFrame();
        Assert.Equal("world!", card.Label);
        Assert.StartsWith("world!", host.GetRowText(0));

        // Reverse: SetCurrentValue writes the target and flows back to the VM, binding preserved.
        card.SetCurrentValue(Card.LabelProperty, "typed");
        Assert.Equal("typed", vm.Title);                  // PropertyChanged-triggered flush reached the VM
        Assert.NotNull(BindingOperations.GetBindingExpression(card, Card.LabelProperty)); // binding still live

        // Forward again: the binding survived the local write — a fresh VM change still drives the target.
        vm.Title = "again";
        host.RunFrame();
        Assert.Equal("again", card.Label);
    }

    // ───────────────────────────── (b) [PropertyName]Changed ladder (POCO) ─────────────────────────────

    /// <summary>
    /// The convention-matched <c>[PropertyName]Changed</c> ladder rung: the same round trip against a
    /// POCO that exposes a <c>TitleChanged</c> CLR event and no INPC. The forward leg drives the cells,
    /// the reverse leg writes the POCO back — and exactly one subscription rides the convention event
    /// (the per-hop wiring proof).
    /// </summary>
    [Fact]
    public void MvvmTwoWayRoundTrip_PropertyNameChangedLadder_NoInpc_RoundTripsThroughConventionEvent()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var card = new Card();
        var vm = new PocoVm { Title = "poco" };
        card.DataContext = vm;
        root.Children.Add(card);

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        card.SetBinding(Card.LabelProperty, new Binding(nameof(PocoVm.Title))
        {
            Mode = BindingMode.TwoWay
        });

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.Equal("poco", card.Label);
        Assert.StartsWith("poco", host.GetRowText(0)); // card is the only child (row 0)
        Assert.Equal(1, vm.TitleChangedSubscriberCount); // the convention rung wired exactly one handler

        // Forward: a POCO change raises TitleChanged → the target updates and renders.
        vm.Title = "changed";
        host.RunFrame();
        Assert.Equal("changed", card.Label);
        Assert.StartsWith("changed", host.GetRowText(0));

        // Reverse: a target write flows back to the POCO.
        card.SetCurrentValue(Card.LabelProperty, "back");
        Assert.Equal("back", vm.Title);
        Assert.Equal(1, vm.TitleChangedSubscriberCount); // still one handler (binding preserved, no leak)
    }

    // ───────────────────────────── (c) DataContext inheritance + rebind ─────────────────────────────

    /// <summary>
    /// DataContext inheritance + rebind: a default-anchored child binding inherits the parent's
    /// DataContext; swapping the PARENT'S DataContext re-targets every descendant binding in one
    /// inherited-value walk — proven in the rendered cells before and after the swap.
    /// </summary>
    [Fact]
    public void DataContextInheritance_ParentSwap_ReTargetsChildBindings_RenderedTruth()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();      // carries the inherited DataContext
        var card = new Card();            // no own DataContext — inherits root's
        root.Children.Add(card);

        var first = new CardVm { Title = "first" };
        root.DataContext = first;

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        card.SetBinding(Card.LabelProperty, new Binding(nameof(CardVm.Title)));

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal("first", card.Label);
        Assert.StartsWith("first", host.GetRowText(0)); // card is the only child (row 0)

        // Swap the parent's DataContext: the inherited change re-anchors the child binding.
        var second = new CardVm { Title = "second" };
        root.DataContext = second;
        host.RunFrame();
        Assert.Equal("second", card.Label);
        Assert.StartsWith("second", host.GetRowText(0));

        // The original VM was fully released — the rebind disposed its subscription.
        Assert.Equal(0, first.SubscriberCount);
        Assert.Equal(1, second.SubscriberCount);
    }

    // ───────────────────────────── (d) ElementName + FindAncestor ─────────────────────────────

    /// <summary>
    /// ElementName + RelativeSource FindAncestor resolve end-to-end. <c>ElementName</c> resolves
    /// through the registered <see cref="NameScope"/> (path read against the named element); a
    /// <c>FindAncestor</c> binding walks the visual/logical chain to a typed ancestor. Both feed the
    /// rendered cells.
    /// </summary>
    [Fact]
    public void ElementNameAndFindAncestor_ResolveThroughTreeAndScope_RenderedTruth()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var source = new Card { Label = "src" };  // the named element ElementName resolves to
        var byName = new Card();                   // binds Label ← ElementName source.Label
        var byAncestor = new Card();               // binds Label ← FindAncestor StackPanel-of-name
        root.Children.Add(source);
        root.Children.Add(byName);
        root.Children.Add(byAncestor);

        var scope = new NameScopeDictionary();
        scope.Register("source", source);
        NameScope.SetNameScope(root, scope);

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // ElementName: byName.Label ← source.Label, resolved via the enclosing name scope.
        byName.SetBinding(Card.LabelProperty, new Binding(nameof(Card.Label)) { ElementName = "source" });
        host.RunFrame();
        Assert.Equal("src", byName.Label);
        Assert.StartsWith("src", host.GetRowText(1)); // byName is the 2nd child (row 1)

        // FindAncestor: byAncestor.Label ← the StackPanel ancestor's Orientation, walking the tree to
        // the typed ancestor (a known element-tree property read).
        byAncestor.SetBinding(Card.LabelProperty,
            new Binding(nameof(StackPanel.Orientation)) { RelativeSource = RelativeSource.Ancestor<StackPanel>(1) });
        host.RunFrame();
        Assert.Equal("Vertical", byAncestor.Label);    // the StackPanel ancestor's default Orientation
        Assert.StartsWith("Vertical", host.GetRowText(2)); // byAncestor is the 3rd child (row 2)
    }

    // ───────────────────────────── (e) When-driven styling (the P3+P4 seam) ─────────────────────────────

    /// <summary>
    /// When-driven styling end-to-end: a viewmodel bool flips a <see cref="Style"/> rule's activation
    /// through <see cref="DataCondition"/> (the P3+P4 seam the <see cref="BindingOperations.Watch"/>
    /// surface closes). Asserted via the styled property's effective value and the rendered cell — the
    /// VM flip reaches the composited frame on the next pump, the unmet-again retracts to the base rule.
    /// </summary>
    [Fact]
    public void WhenDrivenStyling_VmBoolFlipsRuleActivation_StyledEffectiveValueAndCells()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var card = new Card();
        var vm = new CardVm { Highlighted = false };
        card.DataContext = vm;
        root.Children.Add(card);

        // Base rule + a When-guarded rule that activates when the VM's Highlighted flips true.
        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        var hot = R("Card", (Card.FillProperty, HotFill));
        hot.When.Add(new DataCondition(new Binding(nameof(CardVm.Highlighted)), true));
        host.Application.Styles.Add(hot);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // Unmet: the base rule wins; the When rule is armed-inactive.
        Assert.Equal(BaseFill, card.Fill);
        Assert.Equal(BaseFill, host.GetCell(0, 0).Style.Background);

        // Flip the VM bool: the watch delivers, the When rule activates, and the next frame renders hot.
        vm.Highlighted = true;
        host.RunFrame();
        Assert.Equal(HotFill, card.Fill);
        Assert.Equal(HotFill, host.GetCell(0, 0).Style.Background);

        // Flip back: the rule deactivates and the store promotes the base rule, rendered.
        vm.Highlighted = false;
        host.RunFrame();
        Assert.Equal(BaseFill, card.Fill);
        Assert.Equal(BaseFill, host.GetCell(0, 0).Style.Background);
    }

    // ───────────────────────────── (f) teardown-sweep no-leak proof (the closed P1 gap) ─────────────────────────────

    /// <summary>
    /// The teardown-sweep no-leak proof (the recorded P1 gap close —
    /// <c>BindingOperations.TearDown(this)</c> in <see cref="UIElement.TearDown"/>): a subtree bound to
    /// a long-lived VM is detached and torn down; every binding (and the When watch) is disposed, so the
    /// VM has NO surviving subscriptions. A subsequent VM change reaches no torn-down target.
    /// </summary>
    [Fact]
    public void TeardownSweep_DetachedSubtree_BoundToLongLivedVm_NoSurvivingSubscriptions()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var subtree = new StackPanel();
        var cardOne = new Card();
        var cardTwo = new Card();
        var vm = new CardVm { Title = "live", Highlighted = true };
        subtree.DataContext = vm; // one inherited context for the whole subtree
        subtree.Children.Add(cardOne);
        subtree.Children.Add(cardTwo);
        root.Children.Add(subtree);

        // Two ordinary bindings + a When watch on the same long-lived VM.
        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        var hot = R("Card", (Card.FillProperty, HotFill));
        hot.When.Add(new DataCondition(new Binding(nameof(CardVm.Highlighted)), true));
        host.Application.Styles.Add(hot);

        cardOne.SetBinding(Card.LabelProperty, new Binding(nameof(CardVm.Title)));
        cardTwo.SetBinding(Card.LabelProperty, new Binding(nameof(CardVm.Title)));

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal("live", cardOne.Label);
        Assert.Equal(HotFill, cardOne.Fill); // the When rule is active
        Assert.True(vm.SubscriberCount > 0);  // bindings + When watch are live

        // Detach + tear down the whole subtree. TearDown sweeps the binding registry bottom-up.
        root.Children.Remove(subtree);
        subtree.TearDown();
        host.RunFrame();

        // The closed P1 gap: every subscription on the long-lived VM is gone.
        Assert.Equal(0, vm.SubscriberCount);

        // A later VM change reaches no torn-down target (the targets keep their last value).
        var before = cardOne.Label;
        vm.Title = "after-teardown";
        host.RunFrame();
        Assert.Equal(before, cardOne.Label);
    }

    // ───────────────────────────── (g) LostFocus flush on terminal focus-out ─────────────────────────────

    /// <summary>
    /// The LostFocus-trigger flush on terminal focus-out: a TwoWay/LostFocus binding holds a dirty edit;
    /// a terminal focus-out (<c>FocusEvent{HasFocus:false}</c>) fires the <c>EditCommitRequested</c>
    /// pulse that flushes the pending edit to the VM — while keyboard focus is RETAINED (doc §13, ND24)
    /// and NO routed <see cref="UIElement.LostFocusEvent"/> is raised.
    /// </summary>
    [Fact]
    public void LostFocusTrigger_TerminalFocusOut_EditCommitPulseFlushes_FocusRetained_NoLostFocusEvent()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var card = new Card(focusable: true);
        var vm = new CardVm { Title = "a" };
        card.DataContext = vm;
        root.Children.Add(card);

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        host.Application.FocusManager.SetFocus(card);
        card.SetBinding(Card.LabelProperty, new Binding(nameof(CardVm.Title))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });

        // A dirty edit not yet flushed (LostFocus trigger holds it).
        card.Label = "edited";
        Assert.Equal("a", vm.Title);

        var lostFocusRaises = 0;
        card.AddHandler(UIElement.LostFocusEvent, (_, _) => lostFocusRaises++);

        // Terminal focus-out → EditCommitRequested(card) pulse flushes the pending edit.
        host.SendInput(new FocusEvent { HasFocus = false, Timestamp = default });
        host.RunFrame();

        Assert.Equal("edited", vm.Title);                                  // the pulse flushed
        Assert.Same(card, host.Application.FocusManager.FocusedElement);   // focus RETAINED
        Assert.Equal(0, lostFocusRaises);                                  // no LostFocusEvent raised
    }
}
