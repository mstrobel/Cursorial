namespace Cursorial.UI.Input;

/// <summary>The direction of a <see cref="FocusManager.MoveFocus"/> request (design doc §7.7).</summary>
public enum FocusNavigationDirection : byte
{
    /// <summary>The next element in tab order (Tab). With no focused element, starts at the active root's first tab stop.</summary>
    Next,

    /// <summary>The previous element in tab order (Shift+Tab). With no focused element, starts at the active root's last tab stop.</summary>
    Previous,

    /// <summary>Directionally upward (UpArrow) — engages only inside a <see cref="KeyboardNavigation.DirectionalNavigationProperty"/> container.</summary>
    Up,

    /// <summary>Directionally downward (DownArrow).</summary>
    Down,

    /// <summary>Directionally left (LeftArrow).</summary>
    Left,

    /// <summary>Directionally right (RightArrow).</summary>
    Right,
}
