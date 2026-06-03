using System.Runtime.CompilerServices;

using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// An <see cref="IInputTransformer"/> that recognizes click gestures in a mouse-event stream:
/// it counts rapid repeated clicks on the same cell (single / double / triple …) and, optionally,
/// emits synthesized <see cref="MouseEventKind.Click"/> events. Terminals report only raw press /
/// release, never click multiplicity, so this is the layer that adds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>WPF-style timing.</b> The click count is determined at the <see cref="MouseEventKind.ButtonDown"/>:
/// a press on the same cell as the previous press of the same button, within
/// <see cref="MouseClickOptions.MultiClickThreshold"/>, increments the running count; anything else
/// resets it to 1. The count is carried to the matching release (and the synthesized click) so all
/// events of one gesture agree, and <see cref="MouseClickOptions.ClickCount"/> selects which one
/// actually surfaces it (the others keep the default of 1). "Same cell" compares column / row only,
/// ignoring sub-cell pixel coordinates.
/// </para>
/// <para>
/// <b>Click synthesis.</b> When <see cref="MouseClickOptions.SynthesizeClickEvents"/> is on, a
/// <see cref="MouseEventKind.Click"/> event (with <see cref="InputEvent.Synthesized"/> = true) is
/// emitted right after a release whose press landed on the same cell with no intervening drag. The
/// raw press / release events are always forwarded either way.
/// </para>
/// <para>
/// <b>Purely synchronous.</b> Unlike <see cref="KeyReleaseSynthesizer"/>, no event is fabricated on
/// a timer — every output is produced inline as input flows through, using each event's
/// <see cref="InputEvent.Timestamp"/> for the timing comparison. There is no background pump, no
/// channel, and no <see cref="TimeProvider"/> dependency, which makes it deterministic to test.
/// All gesture state is local to a single <see cref="TransformAsync"/> call, so one synthesizer
/// instance can be reused across streams.
/// </para>
/// </remarks>
public sealed class MouseClickSynthesizer : IInputTransformer
{
    private readonly MouseClickOptions _options;

    /// <summary>Construct a synthesizer with the supplied options (or the defaults when null).</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="MouseClickOptions.ClickCount"/> is <see cref="ClickCountTarget.Click"/> but
    /// <see cref="MouseClickOptions.SynthesizeClickEvents"/> is false — there would be no click
    /// event to carry the count.
    /// </exception>
    public MouseClickSynthesizer(MouseClickOptions? options = null)
    {
        _options = options ?? new MouseClickOptions();

        if (_options.MultiClickThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.MultiClickThreshold, "MultiClickThreshold must be positive.");
        }

        if (_options.ClickCount == ClickCountTarget.Click && !_options.SynthesizeClickEvents)
        {
            throw new ArgumentException(
                "ClickCountTarget.Click requires SynthesizeClickEvents to be enabled — there is no "
                + "Click event to carry the count otherwise.",
                nameof(options));
        }
    }

    /// <inheritdoc/>
    public InputCapabilities TransformCapabilities(InputCapabilities sourceCapabilities)
    {
        ArgumentNullException.ThrowIfNull(sourceCapabilities);

        return sourceCapabilities with
               {
                   Mouse = sourceCapabilities.Mouse with
                           {
                               SynthesizesClickCounts = _options.ClickCount != ClickCountTarget.None,
                               SynthesizesClicks = _options.SynthesizeClickEvents,
                           },
               };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<InputEvent> TransformAsync(
        IAsyncEnumerable<InputEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Per-button gesture state, local to this enumeration so the transformer stays reusable
        // and free of cross-run interference.
        var states = new Dictionary<MouseButton, ButtonGesture>();

        await foreach (var evt in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (evt is not MouseEvent mouse)
            {
                yield return evt;
                continue;
            }

            switch (mouse.Kind)
            {
                case MouseEventKind.ButtonDown:
                {
                    int count = RegisterPress(states, mouse);
                    yield return _options.ClickCount == ClickCountTarget.ButtonDown
                                     ? mouse with { ClickCount = count }
                                     : mouse;
                    break;
                }

                case MouseEventKind.ButtonUp:
                {
                    var (count, isClick) = RegisterRelease(states, mouse);

                    yield return _options.ClickCount == ClickCountTarget.ButtonUp
                                     ? mouse with { ClickCount = count }
                                     : mouse;

                    if (isClick && _options.SynthesizeClickEvents)
                    {
                        yield return mouse with
                                     {
                                         Kind = MouseEventKind.Click,
                                         Synthesized = true,
                                         ClickCount = _options.ClickCount == ClickCountTarget.Click ? count : 1,
                                     };
                    }

                    break;
                }

                case MouseEventKind.Drag:
                    // A drag off the press cell cancels the pending click for the buttons anchored there.
                    InvalidateClickCandidates(states, mouse.Position);
                    yield return mouse;
                    break;

                default:
                    yield return mouse;
                    break;
            }
        }
    }

    private int RegisterPress(Dictionary<MouseButton, ButtonGesture> states, MouseEvent press)
    {
        if (press.Button == MouseButton.None) return 1;

        int count = 1;
        if (states.TryGetValue(press.Button, out var prior) &&
            SameCell(prior.PressColumn, prior.PressRow, press.Position) &&
            press.Timestamp - prior.LastPressTime <= _options.MultiClickThreshold)
        {
            count = prior.Count + 1;
        }

        states[press.Button] = new ButtonGesture(
            press.Position.Column, press.Position.Row, press.Timestamp, count, ClickCandidate: true);

        return count;
    }

    private static (int Count, bool IsClick) RegisterRelease(
        Dictionary<MouseButton, ButtonGesture> states, MouseEvent release)
    {
        if (release.Button == MouseButton.None || !states.TryGetValue(release.Button, out var gesture))
            return (1, false);

        bool isClick = gesture.ClickCandidate && SameCell(gesture.PressColumn, gesture.PressRow, release.Position);

        // The press is consumed (no longer a click candidate); the count persists so a quick
        // follow-up press can extend the multi-click run.
        states[release.Button] = gesture with { ClickCandidate = false };

        return (gesture.Count, isClick);
    }

    private static void InvalidateClickCandidates(
        Dictionary<MouseButton, ButtonGesture> states, CellPosition position)
    {
        List<MouseButton>? moved = null;

        foreach (var (button, gesture) in states)
        {
            if (gesture.ClickCandidate && !SameCell(gesture.PressColumn, gesture.PressRow, position))
                (moved ??= []).Add(button);
        }

        if (moved is null) return;

        foreach (var button in moved)
            states[button] = states[button] with { ClickCandidate = false };
    }

    private static bool SameCell(int column, int row, CellPosition position)
        => position.Column == column && position.Row == row;

    /// <summary>
    /// Per-button gesture record. <see cref="Count"/> is the running multi-click count assigned at
    /// the last press; <see cref="ClickCandidate"/> is true between a press and its release while
    /// the gesture is still eligible to become a click (cleared by a release or an off-cell drag).
    /// </summary>
    private readonly record struct ButtonGesture(
        int PressColumn,
        int PressRow,
        DateTimeOffset LastPressTime,
        int Count,
        bool ClickCandidate);
}
