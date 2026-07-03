namespace Cursorial.UI.Input;

/// <summary>
/// The nullable seam <see cref="UIElement"/>-hosting <c>UIApplication</c> calls once per frame, after layout completes,
/// so the KeyTip overlay controller can re-anchor its badges to their targets' final screen cells and build any
/// badge level whose reveal (a floated band, an opened dropdown) only realized this frame. Mirrors
/// <c>CompletePendingActivationFocus</c> / <c>CompletePendingTransitionGoLive</c>. No-op when no controller is installed.
/// </summary>
public interface IKeyTipLayoutHook
{
    /// <summary>Re-anchor live badges + build any parked next level (post-layout, before render).</summary>
    void CompletePendingLayout();
}
