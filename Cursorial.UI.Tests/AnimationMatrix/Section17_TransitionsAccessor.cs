using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

/// <summary>
/// Animation-matrix §17 — the split <c>Transition</c> accessors (the W1 XAML sweep + its audit):
/// <c>GetTransitions</c> is a PURE read of the effective value (style/theme-provided collections stay
/// reachable; a read never pins), while <c>GetOrCreateTransitions</c> is the construction-time fill hook
/// (the loader's <c>GetOrCreate{Name}</c> attached-collection convention) — create + attach on first
/// access, mutable through the detached fill window, sealed + subscribed at the attached arm. The
/// framework's own attach edge reads the property raw so a mere attach never allocates.
/// </summary>
public sealed class Section17_TransitionsAccessor
{
    [Fact] // N155: GetOrCreateTransitions creates + attaches on first access; same instance thereafter
    public void GetOrCreate_CreatesAttachesIdempotent()
    {
        var element = new Border();
        Assert.Null(element.GetValue(Transition.TransitionsProperty)); // pristine

        var created = Transition.GetOrCreateTransitions(element);
        Assert.Empty(created);
        Assert.Same(created, element.GetValue(Transition.TransitionsProperty)); // ATTACHED, not orphaned
        Assert.Same(created, Transition.GetOrCreateTransitions(element));       // stable across reads
        Assert.Same(created, Transition.GetTransitions(element));               // the pure read sees it too
    }

    [Fact] // N156: an explicitly set collection is returned by both accessors, never replaced
    public void ExplicitCollection_Wins()
    {
        var element = new Border();
        var mine = new TransitionCollection();
        Transition.SetTransitions(element, mine);

        Assert.Same(mine, Transition.GetTransitions(element));
        Assert.Same(mine, Transition.GetOrCreateTransitions(element));
    }

    [Fact] // N157: attaching an element to a live tree does NOT allocate a collection (the attach edge
    // reads raw — get-or-create on the framework's per-element attach walk would arm a manager on EVERY element)
    public void Attach_DoesNotAllocateCollection()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var element = new Border();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Null(element.GetValue(Transition.TransitionsProperty)); // attach left the property untouched
        Assert.Null(root.GetValue(Transition.TransitionsProperty));
    }

    [Fact] // N158: the construction-time lifecycle — a GetOrCreateTransitions collection on a DETACHED
    // element stays MUTABLE (the loader's fill window; seal waits for the attached arm), then the attach
    // edge arms it (seals + subscribes) and its transitions run
    public void GetOrCreate_MutableDetached_SealedAndLiveAtAttach()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();

        var collection = Transition.GetOrCreateTransitions(element); // create on the DETACHED element
        collection.Add(new DoubleTransition(Animatable.VProperty) { Duration = Anim.Ms(100) }); // fill window — no seal throw

        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle(); // attach edge: re-arm → seal + subscribe; first arrange parks go-live

        Assert.True(collection.IsSealed); // sealed exactly at the attached arm ("seals on arm (when attached)")
        Assert.Throws<InvalidOperationException>(() => collection.Add(
            new DoubleTransition(Animatable.VProperty) { Duration = Anim.Ms(50) }));

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");

        Assert.Equal(0.0, element.V); // the transition ignited from the old base — the filled collection is armed
        host.AdvanceTime(Anim.Ms(200));
        host.RunUntilIdle();
        Assert.Equal(10.0, element.V);
    }

    [Fact] // N159 (audit): a GetTransitions READ never pins — a style-provided collection stays the
    // effective value after any number of reads (the pre-audit get-or-create read pinned an empty
    // LocalValue that permanently masked the Window theme's inactive-fade setter)
    public void PureRead_NeverPins_StyleProvidedCollectionStaysEffective()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Null(Transition.GetTransitions(element)); // a pure read on pristine: null, NO pin
        Assert.Same(UIProperty.UnsetValue, element.ReadLocalValue(Transition.TransitionsProperty)); // …and nothing written

        var styled = new TransitionCollection { new DoubleTransition(Animatable.VProperty) { Duration = Anim.Ms(100) } };
        host.Application.Styles.Add(new Style(".fade").Set(Transition.TransitionsProperty, styled));
        element.Classes.Add("fade");
        host.RunUntilIdle();

        Assert.Same(styled, Transition.GetTransitions(element)); // the style-provided collection WINS —
        Assert.Same(styled, Transition.GetTransitions(element)); // …and repeat reads still don't pin over it
    }

    [Fact] // N160 (W2b CR6): a wrong-typed / unset Property surfaces at ARM as a clear diagnostic naming
    // the transition, the property, and the expected value type — the one downcast between the base-typed
    // markup member and the typed pipeline
    public void WrongTypedProperty_ThrowsClearArmDiagnostic()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var wrongTyped = new TransitionCollection
        {
            new DoubleTransition { Property = UIElement.VisibilityProperty }, // a StyledProperty<Visibility>, not <double>
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Transition.SetTransitions(element, wrongTyped));
        Assert.Contains("DoubleTransition", ex.Message);
        Assert.Contains("Visibility", ex.Message);
        Assert.Contains("StyledProperty<Double>", ex.Message);

        var unset = new TransitionCollection { new DoubleTransition() }; // Property never set
        var ex2 = Assert.Throws<InvalidOperationException>(() => Transition.SetTransitions(element, unset));
        Assert.Contains("unset", ex2.Message);
    }

    [Fact] // N161 (audit): SetTransitions is ALL-OR-NOTHING — the throw happens BEFORE the store writes,
    // so the element's prior transitions stay intact and armed (no half-applied seal/subscriptions)
    public void SetTransitions_InvalidEntry_PriorStateIntact()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var good = new TransitionCollection { new DoubleTransition(Animatable.VProperty) { Duration = Anim.Ms(100) } };
        Transition.SetTransitions(element, good);

        var bad = new TransitionCollection
        {
            new DoubleTransition(Animatable.VProperty) { Duration = Anim.Ms(50) },
            new DoubleTransition { Property = UIElement.VisibilityProperty }, // invalid — throws pre-write
        };
        Assert.Throws<InvalidOperationException>(() => Transition.SetTransitions(element, bad));

        Assert.Same(good, element.GetValue(Transition.TransitionsProperty)); // the store never mutated
        Assert.False(bad.IsSealed);                                          // the rejected collection untouched

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");
        Assert.Equal(0.0, element.V); // the PRIOR arm still runs — subscriptions were never disturbed
    }

    [Fact] // N162 (audit): a FRAMEWORK-driven arm (style application / the attach edge) never throws on
    // an authored typo — the invalid transition is skipped with an AnimationDiagnostics warning and the
    // valid siblings arm (a bad markup transition must not abort a style transaction or an attach walk)
    public void FrameworkArm_InvalidEntry_SkipsWithWarning_SiblingsArm()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var warnings = new List<string>();
        Action<string> capture = warnings.Add;
        AnimationDiagnostics.Warning += capture;
        try
        {
            // The style lane bypasses SetTransitions (the setter writes the store directly) — the arm
            // runs inside the style transaction and must skip, not throw.
            var mixed = new TransitionCollection
            {
                new DoubleTransition { Property = UIElement.VisibilityProperty }, // invalid — skipped
                new DoubleTransition(Animatable.VProperty) { Duration = Anim.Ms(100) }, // valid — arms
            };
            host.Application.Styles.Add(new Style(".mix").Set(Transition.TransitionsProperty, mixed));
            element.Classes.Add("mix");
            host.RunUntilIdle(); // no throw — the transaction completed

            Assert.Contains(warnings, w => w.Contains("DoubleTransition") && w.Contains("Visibility"));
            Assert.Same(mixed, element.GetValue(Transition.TransitionsProperty)); // the style value applied

            host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
            element.Classes.Add("hi");
            Assert.Equal(0.0, element.V); // the VALID sibling armed and ignites
            host.AdvanceTime(Anim.Ms(200));
            host.RunUntilIdle();
            Assert.Equal(10.0, element.V);
        }
        finally
        {
            AnimationDiagnostics.Warning -= capture;
        }
    }
}
