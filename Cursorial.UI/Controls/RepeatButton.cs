namespace Cursorial.UI.Controls;

/// <summary>
/// A button that raises <see cref="ButtonBase.Click"/> repeatedly while held (design doc §12.7,
/// CD29): <see cref="ButtonBase.ClickMode"/> defaults to <see cref="ClickMode.Press"/>; after
/// <see cref="Delay"/> the first repeat fires, then one per <see cref="Interval"/> while the button
/// stays pressed and the pointer stays over it. The repeats ride the P2 <see cref="UITimer"/> slice
/// (frame-aligned, ND20 coalescing — at most one fire per frame). The timer is canceled and unhooked
/// on release / capture loss (the unhook-before-rewire reference impl). Scrollbar line buttons use it.
/// </summary>
public class RepeatButton : ButtonBase
{
    private UITimer? _timer;
    private bool _delayElapsed;

    /// <summary>The delay (ms) before the first repeat after press (default 400).</summary>
    public static readonly StyledProperty<int> DelayProperty =
        UIProperty.Register<RepeatButton, int>(nameof(Delay), defaultValue: 400);

    /// <summary>The interval (ms) between repeats once repeating (default 60).</summary>
    public static readonly StyledProperty<int> IntervalProperty =
        UIProperty.Register<RepeatButton, int>(nameof(Interval), defaultValue: 60);

    static RepeatButton()
    {
        // RepeatButton clicks on press (CD29) — its repeats begin from the press, not a release.
        ClickModeProperty.OverrideDefaultValue<RepeatButton>(ClickMode.Press);
    }

    /// <inheritdoc cref="DelayProperty"/>
    public int Delay { get => GetValue(DelayProperty); set => SetValue(DelayProperty, value); }

    /// <inheritdoc cref="IntervalProperty"/>
    public int Interval { get => GetValue(IntervalProperty); set => SetValue(IntervalProperty, value); }

    /// <summary>
    /// Drives the repeat timer off the pressed + pointer-over state (CD29/C202): pressed-and-over
    /// (re)arms the delay timer; losing either pauses it (release / capture loss / pointer leaving).
    /// </summary>
    private protected override void OnInteractionStateChangedCore(InteractionState oldState, InteractionState newState)
    {
        base.OnInteractionStateChangedCore(oldState, newState);

        var pressed = (newState & InteractionState.Pressed) != 0;
        var over = (newState & InteractionState.PointerOver) != 0;

        // Repeats run only while pressed AND pointer-over. Keyboard activation (Space) presses without
        // pointer-over, so it does not repeat (the OS auto-repeat re-enters OnKeyDown for that case).
        if (pressed && over)
            EnsureTimer();
        else
            StopTimer();
    }

    private void EnsureTimer()
    {
        if (_timer is { IsRunning: true })
            return;

        _delayElapsed = false;
        StopTimer(); // unhook-before-rewire
        _timer = UITimer.Start(TimeSpan.FromMilliseconds(Math.Max(0, Delay)), OnRepeatTick);
    }

    private void OnRepeatTick()
    {
        if ((InteractionStateInternal & (InteractionState.Pressed | InteractionState.PointerOver))
            != (InteractionState.Pressed | InteractionState.PointerOver))
        {
            StopTimer();
            return;
        }

        OnClick();

        if (!_delayElapsed)
        {
            // The delay one-shot fired; re-arm as a repeating interval timer (frame-aligned, ND20).
            _delayElapsed = true;
            _timer?.Stop();
            _timer = UITimer.Start(
                TimeSpan.FromMilliseconds(Math.Max(1, Interval)),
                TimeSpan.FromMilliseconds(Math.Max(1, Interval)),
                OnRepeatTick);
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        _delayElapsed = false;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        StopTimer();
        base.OnDetachedFromTree(in e);
    }
}
