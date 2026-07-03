// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// A style edge action (design doc §9.3) that begins a <see cref="Storyboard"/> on the matched element.
/// The per-run instance is keyed <c>(this action, element)</c>, so two rules sharing one storyboard resource
/// never fight. Edge-ignited begins are <b>no-throw</b>: an unresolvable target/property routes to
/// <see cref="AnimationDiagnostics"/> and the failing track is skipped, siblings proceed.
/// </summary>
/// <remarks>
/// The Fork B seam (SD16) invokes <see cref="IStyleEdgeAction.OnActivated"/> only on <see cref="Style.Enter"/>
/// entries (the activate edge) and <see cref="IStyleEdgeAction.OnRetracted"/> only on <see cref="Style.Exit"/>
/// entries (the deactivate edge) — an action receives exactly one signal per placement. This action is therefore
/// <b>do/undo</b>: <see cref="OnActivated"/> begins, <see cref="OnRetracted"/> stops. The common
/// begin-on-enter + auto-stop-on-exit pattern adds the <b>same instance</b> to both
/// <see cref="Style.Enter"/> and <see cref="Style.Exit"/>; begin-and-let-<see cref="FillBehavior"/>-hold adds it
/// to <see cref="Style.Enter"/> only.
/// </remarks>
public sealed class BeginStoryboard : IStyleEdgeAction
{
    /// <summary>The storyboard to begin (null ⇒ the action is a no-op).</summary>
    public Storyboard? Storyboard { get; set; }

    /// <summary>How a re-activation combines with a still-running instance on the same key.</summary>
    public HandoffBehavior Handoff { get; set; } = HandoffBehavior.SnapshotAndReplace;

    /// <summary>Begins the storyboard (the activate edge when placed in <see cref="Style.Enter"/>).</summary>
    public void OnActivated(UIElement element)
    {
        if (Storyboard is null)
            return;

        // Scope = the matched element; igniter = THIS action (so two rules sharing one storyboard keep distinct
        // (igniter, scope) instances). No-throw: track failures route to AnimationDiagnostics.
        AnimationScheduler.CurrentOrNull?.BeginStoryboard(this, element, Storyboard, Handoff, throwOnFailure: false);
    }

    /// <summary>Stops the instance this action began (the deactivate edge when placed in <see cref="Style.Exit"/>).</summary>
    public void OnRetracted(UIElement element)
    {
        if (Storyboard is null)
            return;

        AnimationScheduler.CurrentOrNull?.StopStoryboardByKey(this, element);
    }

    /// <inheritdoc/>
    public void SealReferences() => Storyboard?.Seal(); // surface track type errors at attach (§9.3)
}

/// <summary>
/// A style edge action (design doc §9.3) that stops a <see cref="Storyboard"/> on the matched element —
/// <b>by object reference</b> (no name registry; resources give identity). Stops every live instance of that
/// storyboard on the element, across igniters. Edge-symmetric (stop is its own inverse): place it in
/// <see cref="Style.Enter"/> to stop on the activate edge, or <see cref="Style.Exit"/> to stop on deactivate.
/// </summary>
public sealed class StopStoryboard : IStyleEdgeAction
{
    /// <summary>The storyboard whose instances to stop (null ⇒ a no-op).</summary>
    public Storyboard? Storyboard { get; set; }

    /// <inheritdoc/>
    public void OnActivated(UIElement element) => Stop(element);

    /// <inheritdoc/>
    public void OnRetracted(UIElement element) => Stop(element);

    private void Stop(UIElement element)
    {
        if (Storyboard is not null)
            AnimationScheduler.CurrentOrNull?.StopStoryboardOnScope(Storyboard, element);
    }
}
