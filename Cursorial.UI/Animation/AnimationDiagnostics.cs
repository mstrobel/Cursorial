// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// One track that failed to resolve or build during an <b>edge-ignited</b> storyboard begin (design doc §9.3).
/// Edge ignitions are driven by pseudo-class flips under any-event motion, so they never throw — the failing
/// track is skipped, its siblings proceed, and the failure surfaces here.
/// </summary>
public sealed class StoryboardTrackError
{
    internal StoryboardTrackError(Storyboard storyboard, AnimationTrack track, UIElement scope, string message)
    {
        Storyboard = storyboard;
        Track = track;
        Scope = scope;
        Message = message;
    }

    /// <summary>The storyboard whose ignition failed.</summary>
    public Storyboard Storyboard { get; }

    /// <summary>The specific track that could not be resolved/built.</summary>
    public AnimationTrack Track { get; }

    /// <summary>The scope the ignition targeted.</summary>
    public UIElement Scope { get; }

    /// <summary>A human-readable description of the failure.</summary>
    public string Message { get; }

    /// <inheritdoc/>
    public override string ToString() => $"Storyboard track error on {Scope}: {Message}";
}

/// <summary>Diagnostics sink for the animation subsystem (design doc §9.3/§9.6/§9.9).</summary>
public static class AnimationDiagnostics
{
    /// <summary>Raised (on the UI thread) when an edge-ignited storyboard track fails to resolve or build.</summary>
    public static event Action<StoryboardTrackError>? TrackError;

    /// <summary>
    /// A DEBUG-only authoring warning (design doc §9.6/§9.9): a perpetual animation on a layout-affecting
    /// property, or an animation whose target never entered the tree (a probable leak). Never raised in Release.
    /// </summary>
    public static event Action<string>? Warning;

    internal static void RaiseTrackError(StoryboardTrackError error) => TrackError?.Invoke(error);

    internal static void RaiseWarning(string message) => Warning?.Invoke(message);
}
