namespace Cursorial.Input;

/// <summary>
/// Configuration for the <see cref="MouseClickSynthesizer"/>.
/// </summary>
public sealed record MouseClickOptions
{
    /// <summary>
    /// Maximum time between consecutive presses on the same cell for them to count as a
    /// multi-click. A press that arrives later than this — or on a different cell or button —
    /// resets the running count to 1. Defaults to 500 ms, matching common OS double-click windows.
    /// </summary>
    public TimeSpan MultiClickThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// When true, the synthesizer emits a <see cref="MouseEventKind.Click"/> event immediately
    /// after each <see cref="MouseEventKind.ButtonUp"/> whose press landed on the same cell with no
    /// intervening drag. The raw press / release events are always forwarded regardless. Defaults
    /// to false.
    /// </summary>
    public bool SynthesizeClickEvents { get; init; }

    /// <summary>
    /// Which event of a gesture surfaces the multi-click count. Defaults to
    /// <see cref="ClickCountTarget.ButtonDown"/>.
    /// </summary>
    public ClickCountTarget ClickCount { get; init; } = ClickCountTarget.ButtonDown;
}
