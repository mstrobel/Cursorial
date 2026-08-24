using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// Begins an S5 <see cref="Storyboard"/> when the trigger fires (design doc §5): the storyboard runs on
/// the target scope (<see cref="Scope"/>, else the firing trigger's host — which must be a
/// <see cref="UIElement"/>). The last <see cref="StoryboardHandle"/> is retained so a paired
/// <see cref="ControlStoryboardAction"/> can pause/resume/stop/skip the running instance.
/// </summary>
public class BeginStoryboardAction : TriggerAction
{
    /// <summary>The storyboard to begin (typically a <c>{StaticResource}</c>).</summary>
    public Storyboard? Storyboard { get; set; }

    /// <summary>An explicit animation scope; default: the firing trigger's host.</summary>
    public UIElement? Scope { get; set; }

    /// <summary>The property-system handoff (default <see cref="HandoffBehavior.SnapshotAndReplace"/>).</summary>
    public HandoffBehavior Handoff { get; set; } = HandoffBehavior.SnapshotAndReplace;

    /// <summary>The most recent run's handle (null before the first fire) — the pair seam for
    /// <see cref="ControlStoryboardAction.BeginAction"/>.</summary>
    public StoryboardHandle? LastHandle { get; private set; }

    /// <inheritdoc/>
    protected override void Invoke(object? sender, object? parameter)
    {
        var storyboard = Storyboard
            ?? throw new InvalidOperationException("BeginStoryboardAction requires a Storyboard.");

        var scope = Scope ?? sender as UIElement
            ?? throw new InvalidOperationException(
                "BeginStoryboardAction has no scope (set Scope, or attach the trigger to a UIElement).");

        LastHandle = storyboard.Begin(scope, Handoff);
    }
}

/// <summary>What <see cref="ControlStoryboardAction"/> does to the running instance.</summary>
public enum ControlStoryboardOperation
{
    /// <summary>Pause the paired instance's clock.</summary>
    Pause,

    /// <summary>Resume the paired instance's clock.</summary>
    Resume,

    /// <summary>Stop and retract the paired instance (no <c>Completed</c>).</summary>
    Stop,

    /// <summary>Jump the paired instance to its final values.</summary>
    SkipToEnd,
}

/// <summary>
/// Controls a running storyboard instance (design doc §5): pairs with a <see cref="BeginStoryboardAction"/>
/// (reference it via <c>{x:Reference}</c>/code) and drives its <see cref="BeginStoryboardAction.LastHandle"/>.
/// Alternatively <see cref="Storyboard"/> + <see cref="ControlStoryboardOperation.Stop"/> stops the
/// imperatively-keyed instance on the scope (<c>Storyboard.Stop(scope)</c>) without a handle. A fire with
/// nothing to control is a no-op (the instance may simply have completed) — misconfiguration (neither
/// <see cref="BeginAction"/> nor <see cref="Storyboard"/>) throws.
/// </summary>
public class ControlStoryboardAction : TriggerAction
{
    /// <summary>The paired begin action whose last run this action controls.</summary>
    public BeginStoryboardAction? BeginAction { get; set; }

    /// <summary>A storyboard to stop BY KEY on the scope (the handle-less <see cref="ControlStoryboardOperation.Stop"/> path).</summary>
    public Storyboard? Storyboard { get; set; }

    /// <summary>An explicit scope for the <see cref="Storyboard"/> path; default: the firing trigger's host.</summary>
    public UIElement? Scope { get; set; }

    /// <summary>The operation (default <see cref="ControlStoryboardOperation.Stop"/>).</summary>
    public ControlStoryboardOperation Operation { get; set; } = ControlStoryboardOperation.Stop;

    /// <inheritdoc/>
    protected override void Invoke(object? sender, object? parameter)
    {
        if (BeginAction is { } begin)
        {
            if (begin.LastHandle is not { } handle)
                return; // nothing has begun yet (or it completed and the handle went inert) — a benign no-op

            switch (Operation)
            {
                case ControlStoryboardOperation.Pause: handle.Pause(); break;
                case ControlStoryboardOperation.Resume: handle.Resume(); break;
                case ControlStoryboardOperation.Stop: handle.Stop(); break;
                case ControlStoryboardOperation.SkipToEnd: handle.SkipToEnd(); break;
            }

            return;
        }

        if (Storyboard is { } storyboard)
        {
            if (Operation != ControlStoryboardOperation.Stop)
                throw new InvalidOperationException(
                    "ControlStoryboardAction with a Storyboard (no BeginAction) supports only Operation=Stop " +
                    "(pause/resume/skip need the running instance's handle — pair with a BeginStoryboardAction).");

            var scope = Scope ?? sender as UIElement
                ?? throw new InvalidOperationException(
                    "ControlStoryboardAction has no scope (set Scope, or attach the trigger to a UIElement).");

            storyboard.Stop(scope);
            return;
        }

        throw new InvalidOperationException(
            "ControlStoryboardAction requires a BeginAction (handle control) or a Storyboard (keyed Stop).");
    }
}
