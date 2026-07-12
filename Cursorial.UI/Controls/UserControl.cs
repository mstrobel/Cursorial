namespace Cursorial.UI.Controls;

/// <summary>
/// The base class for user-composed XAML views (WPF/Avalonia parity): a <see cref="ContentControl"/>
/// whose <see cref="ContentControl.Content"/> is the view's own markup, presented through the
/// neutral content theme. The view itself is chrome-free plumbing — its interactive children own
/// focus and input.
/// </summary>
public class UserControl : ContentControl
{
    /// <summary>
    /// Derived views inherit the <see cref="UserControl"/> theme: the S7 control-theme lookup is
    /// exact-key with no base-class probing (CD13), so without this pin every <c>x:Class</c> view
    /// subclass would render blank until it registered a theme of its own.
    /// </summary>
    protected override object ControlThemeKey => typeof(UserControl);
}
