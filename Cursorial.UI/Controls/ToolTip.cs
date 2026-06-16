namespace Cursorial.UI.Controls;

/// <summary>
/// A tooltip content host (design doc §12.7): a <see cref="ContentControl"/> that
/// <see cref="ToolTipService"/> shows in a hit-test-transparent, never-focused <see cref="Popup"/> above all
/// windows. It is not a focus stop and not hit-test-visible — it floats over content without stealing hover or
/// clicks. The themed control template renders an occluding bordered panel (max width 40, content wraps).
/// </summary>
public sealed class ToolTip : ContentControl
{
    /// <summary>Creates a tooltip (never focusable, never hit-tested).</summary>
    public ToolTip()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }
}
