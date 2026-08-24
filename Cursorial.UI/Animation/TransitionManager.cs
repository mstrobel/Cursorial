// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// Per-element transition arming + the go-live latch (design doc §9.5). Cold — allocated only when transitions
/// arm, so the hover/move hot path never touches it. Subscribes the winning-base channel for each transition's
/// property; a base change is transitioned only after the element has <b>gone live</b> (its first arrange has
/// completed and the post-layout drain flipped the latch), so the initial style application — which writes base
/// values through the same channel at attach and at first-layout template expansion — never transitions.
/// </summary>
internal sealed class TransitionManager
{
    private readonly UIElement _owner;
    private readonly List<IDisposable> _subscriptions = []; // one winning-base subscription per transition
    private TransitionCollection? _armed;
    private bool _everArranged; // raised by the owner's first arrange — the go-live trigger
    private bool _live;         // false = parked (initial application swallowed); true = transitions ignite

    private TransitionManager(UIElement owner) => _owner = owner;

    /// <summary>The attached-property change-callback edge (set / replace / clear).</summary>
    internal static void OnTransitionsPropertyChanged(UIElement element, TransitionCollection? newValue)
    {
        if (newValue is null)
        {
            element.Transitions?.Disarm();
            return;
        }

        (element.Transitions ??= new TransitionManager(element)).Arm(newValue);
    }

    /// <summary>Attach edge (from <c>OnStylingAttached</c>): subscribe a collection that was set while detached.</summary>
    internal static void OnElementAttached(UIElement element)
    {
        // Raw read — the public GetTransitions accessor is get-or-create for XAML collection fill, and an
        // attach edge that runs on EVERY element must never allocate a collection as a side effect.
        if (element.GetValue(Transition.TransitionsProperty) is { } collection)
            (element.Transitions ??= new TransitionManager(element)).Arm(collection);
    }

    /// <summary>Detach edge: dispose subscriptions and re-park (a re-attach's initial application must not transition).</summary>
    internal static void OnElementDetached(UIElement element) => element.Transitions?.Disarm();

    /// <summary>First-arrange edge (from the owner's first <c>Arrange</c>): request go-live at the post-layout drain.</summary>
    internal void OnArranged()
    {
        if (_everArranged)
            return;

        _everArranged = true;
        UIApplication.Current?.RequestTransitionGoLive(this); // flipped live at the post-layout boundary (§9.5)
    }

    /// <summary>Flips the go-live latch (the app's post-layout drain calls this — the FocusManager mirror).</summary>
    /// <remarks>No-op when the manager was disarmed/detached after its go-live request but before the drain
    /// (the §9.5 ordering hole): a re-attach must re-park, so a stale queued GoLive must not re-arm a parked manager.</remarks>
    internal void GoLive()
    {
        if (_armed is not null && _owner.IsAttachedToTree)
            _live = true;
    }

    /// <summary>Whether a base change should ignite a transition: live (past the initial application) and motion enabled.</summary>
    internal bool ShouldTransition() => _live && (AnimationScheduler.CurrentOrNull?.AnimationsEnabled ?? false);

    private void Arm(TransitionCollection collection)
    {
        DisposeSubscriptions();
        _armed = collection;

        // Subscribe only when attached; a collection set on a detached element re-arms at attach (OnElementAttached).
        // _live/_everArranged are NOT reset here — a mid-run replace on an already-live element stays live (N149);
        // only detach re-parks (Disarm).
        // The SEAL also waits for the attached arm — the documented "seals on arm (when attached)" contract:
        // a collection set on a DETACHED element stays mutable so construction-time fill (the XAML loader's
        // attached-collection get-or-create path, N155) can populate it; the attach edge re-arms and seals.
        if (!_owner.IsAttachedToTree)
            return;

        collection.Seal();

        foreach (var transition in collection)
        {
            // Framework-driven arms (style application, the attach edge) must never throw on an authored
            // typo — a wrong-typed transition would abort the style transaction / the attach walk
            // half-done (the W2b-audit findings). Skip it with a diagnostic; the imperative
            // SetTransitions path already threw pre-write for the same defect.
            if (transition.ValidateForArm() is { } error)
            {
                AnimationDiagnostics.RaiseWarning(error);
                continue;
            }

            _subscriptions.Add(transition.Subscribe(_owner, this));
        }

        // Arming on an element that has already had a real (non-collapsed) arrange — runtime SetTransitions on a
        // settled element — goes live now: its initial application is in the past (the manager never observed it),
        // nothing to skip. A freshly (re-)attached element has HasArrangedVisible == false (reset on attach), so it
        // stays PARKED until its next real arrange drives OnArranged → go-live (this is the re-attach re-park).
        if (_owner.HasArrangedVisible)
            _live = true;
    }

    private void Disarm()
    {
        DisposeSubscriptions();
        UIApplication.Current?.CancelTransitionGoLiveRequest(this); // flipped live at the post-layout boundary (§9.5)
        _armed = null;
        _live = false;
        _everArranged = false;
    }

    private void DisposeSubscriptions()
    {
        for (var i = 0; i < _subscriptions.Count; i++)
            _subscriptions[i].Dispose();
        _subscriptions.Clear();
    }
}
