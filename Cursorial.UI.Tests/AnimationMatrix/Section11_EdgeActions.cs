using Cursorial.Animation;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §11 (edge actions + AnimationDiagnostics, ignited through the real StyleEngine) — N122–N130.
// Activation is driven by a public CSS class flip (structural ⇒ re-matched synchronously, SD12).
public sealed class Section11_EdgeActions
{
    private static (UITestHost Host, Animatable Element) ShowStylable()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, element);
    }

    private static Storyboard StoryboardFor(StyledProperty<double> property, double to, int ms)
    {
        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetProperty = property, From = 0.0, To = to, Duration = Ms(ms) });
        return sb;
    }

    [Fact] // N122: a rule's Enter BeginStoryboard begins the storyboard on the matched element
    public void Enter_BeginsStoryboard()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var sb = StoryboardFor(Animatable.VProperty, 10.0, 100);
        var style = new Style(".go");
        style.Enter.Add(new BeginStoryboard { Storyboard = sb });
        host.Application.Styles.Add(style);

        element.Classes.Add("go"); // structural re-match ⇒ rule activates synchronously ⇒ OnActivated
        Assert.True(host.Scheduler().HasActiveAnimations);

        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, element.V);
    }

    [Fact] // N123: the same BeginStoryboard in BOTH Enter and Exit is do/undo — begins on activate, stops on deactivate
    public void DoUndo_BeginsOnEnter_StopsOnExit()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var sb = StoryboardFor(Animatable.VProperty, 10.0, 100);
        var begin = new BeginStoryboard { Storyboard = sb };
        var style = new Style(".go");
        style.Enter.Add(begin); // OnActivated ⇒ begin
        style.Exit.Add(begin);  // OnRetracted ⇒ stop (SD16 delivers OnRetracted only to Exit entries)
        host.Application.Styles.Add(style);

        element.Classes.Add("go");
        host.AdvanceTime(Ms(33));
        Assert.InRange(element.V, 0.0, 10.0);

        element.Classes.Remove("go"); // exit edge ⇒ OnRetracted ⇒ stop
        Assert.Equal(0.0, element.V);  // base resurfaced
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N124: a BeginStoryboard in Enter only ⇒ deactivation leaves it running (no Exit undo; Fill governs)
    public void EnterOnly_KeepsRunningOnExit()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var sb = StoryboardFor(Animatable.VProperty, 10.0, 100);
        var style = new Style(".go");
        style.Enter.Add(new BeginStoryboard { Storyboard = sb }); // no Exit entry
        host.Application.Styles.Add(style);

        element.Classes.Add("go");
        host.AdvanceTime(Ms(33));
        element.Classes.Remove("go"); // no Exit undo

        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, element.V); // ran to its end regardless
    }

    [Fact] // N125: rules with distinct BeginStoryboard igniters keep independent (igniter, scope) instances
    public void TwoRules_DistinctIgniters_IndependentInstances()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        // Distinct igniters on DIFFERENT properties so the per-(igniter,scope) keying is observable (a single
        // shared instance would be torn down by stopping either rule). A's BeginStoryboard is do/undo (Enter+Exit).
        var sbA = StoryboardFor(Animatable.VProperty, 10.0, 200);
        var sbB = StoryboardFor(Animatable.WProperty, 20.0, 200);
        var beginA = new BeginStoryboard { Storyboard = sbA };
        var styleA = new Style(".a");
        styleA.Enter.Add(beginA);
        styleA.Exit.Add(beginA); // do/undo ⇒ Classes.Remove("a") stops A's instance
        var styleB = new Style(".b");
        styleB.Enter.Add(new BeginStoryboard { Storyboard = sbB });
        host.Application.Styles.Add(styleA);
        host.Application.Styles.Add(styleB);

        element.Classes.Add("a"); // instance A (igniter beginA) animates V
        element.Classes.Add("b"); // instance B (distinct igniter) animates W
        host.AdvanceTime(Ms(66));
        Assert.InRange(element.V, 0.0, 10.0);
        Assert.InRange(element.W, 0.0, 20.0);

        element.Classes.Remove("a"); // stops ONLY A's instance (its own igniter key); B is untouched
        Assert.Equal(0.0, element.V);                       // A retracted
        Assert.True(host.Scheduler().HasActiveAnimations);  // B still running

        host.AdvanceTime(Ms(200));
        Assert.Equal(20.0, element.W);                      // B ran independently to its end
    }

    [Fact] // N126: a StopStoryboard (by reference) stops every live instance of that storyboard on the element
    public void StopStoryboard_StopsByReference()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var sb = StoryboardFor(Animatable.VProperty, 10.0, 100);
        var start = new Style(".go");
        start.Enter.Add(new BeginStoryboard { Storyboard = sb }); // Enter only ⇒ no auto-stop
        var stop = new Style(".stop");
        stop.Enter.Add(new StopStoryboard { Storyboard = sb });
        host.Application.Styles.Add(start);
        host.Application.Styles.Add(stop);

        element.Classes.Add("go");
        host.AdvanceTime(Ms(33));
        Assert.InRange(element.V, 0.0, 10.0);

        element.Classes.Add("stop"); // StopStoryboard.OnActivated ⇒ stops the sb instance on the element
        Assert.Equal(0.0, element.V);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N127: an edge-ignited unresolvable TargetName does NOT throw — routes to AnimationDiagnostics; siblings proceed
    public void EdgeIgnited_UnresolvableTarget_RoutesToDiagnostics()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetProperty = Animatable.VProperty, From = 0.0, To = 10.0, Duration = Ms(100) }); // good
        sb.Children.Add(new DoubleTrack { TargetName = "missing", TargetProperty = Animatable.WProperty, To = 5.0, Duration = Ms(100) }); // bad
        var style = new Style(".go");
        style.Enter.Add(new BeginStoryboard { Storyboard = sb });
        host.Application.Styles.Add(style);

        StoryboardTrackError? error = null;
        void Handler(StoryboardTrackError e) => error = e;
        AnimationDiagnostics.TrackError += Handler;
        try
        {
            element.Classes.Add("go"); // must NOT throw
        }
        finally
        {
            AnimationDiagnostics.TrackError -= Handler;
        }

        Assert.NotNull(error);                       // the bad track surfaced
        Assert.Contains("missing", error!.Message);
        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, element.V);               // the good sibling track proceeded
    }

    [Fact] // N128: multiple edge actions run in rule-document order on the edge
    public void EdgeActions_RunInDocumentOrder()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var order = new List<int>();
        var style = new Style(".go");
        style.Enter.Add(new RecordingAction(order, 1));
        style.Enter.Add(new RecordingAction(order, 2));
        style.Enter.Add(new RecordingAction(order, 3));
        host.Application.Styles.Add(style);

        element.Classes.Add("go");
        Assert.Equal([1, 2, 3], order);
    }

    [Fact] // N129: detaching the scope while an edge-ignited storyboard runs retracts it; no Completed
    public void DetachScope_EdgeIgnited_Retracts()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var sb = StoryboardFor(Animatable.VProperty, 10.0, 100);
        var style = new Style(".go");
        style.Enter.Add(new BeginStoryboard { Storyboard = sb });
        host.Application.Styles.Add(style);

        element.Classes.Add("go");
        host.AdvanceTime(Ms(33));
        Assert.True(host.Scheduler().HasActiveAnimations);

        root.Children.Remove(element);
        host.RunUntilIdle();
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N130: a null Storyboard on an edge action is a no-op (no throw)
    public void NullStoryboard_NoOp()
    {
        var (host, element) = ShowStylable();
        using var _ = host;

        var style = new Style(".go");
        style.Enter.Add(new BeginStoryboard { Storyboard = null });
        style.Enter.Add(new StopStoryboard { Storyboard = null });
        host.Application.Styles.Add(style);

        element.Classes.Add("go"); // must not throw
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    private sealed class RecordingAction(List<int> log, int id) : IStyleEdgeAction
    {
        public void OnActivated(UIElement element) => log.Add(id);
        public void OnRetracted(UIElement element) { }
    }
}
