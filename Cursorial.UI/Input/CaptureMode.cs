namespace Cursorial.UI.Input;

/// <summary>
/// How a held mouse capture redirects uncaptured-position events (design doc §7.6; WPF's
/// <c>System.Windows.Input.CaptureMode</c> kinship). Capture here is routing policy, not OS capture —
/// "None" is simply the absence of a capture holder, so it is not a member.
/// </summary>
public enum CaptureMode : byte
{
    /// <summary>
    /// Every mouse event (except wheel — §7.6) routes to the capture holder regardless of what the
    /// pointer is over. The slider/scrollbar-drag default: the gesture owns all input until release.
    /// </summary>
    Element,

    /// <summary>
    /// Events route normally while the pointer is over the capture holder or an element within its
    /// (visual-then-logical) subtree — so descendants still receive their own mouse events — and
    /// redirect to the holder only when the pointer is outside it. The Menu/ComboBox default: items
    /// inside the open popup stay interactive while an outside press still reaches the owner.
    /// </summary>
    SubTree,
}
