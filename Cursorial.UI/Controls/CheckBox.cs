using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A labeled checkbox (design doc §12.7): a <see cref="ToggleButton"/> whose default theme draws a
/// glyph cell + a space + the <see cref="ContentControl.Content"/> presenter. The glyphs are theme
/// resources (strings) selected by the capability color/ascii classes — ASCII <c>[ ] [x] [-]</c>
/// everywhere safe, a Unicode <c>☐ ☑ ◪</c>-class swap on a <c>caps-unicode</c> tier (CD26).
/// </summary>
public class CheckBox : ToggleButton
{
    private bool _focusedByAccessKey;

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        _focusedByAccessKey = false;
        base.OnLostFocus(e);
    }

    protected override void OnAccessKey(AccessKeyEventArgs e)
    {
        if (_focusedByAccessKey)
        {
            _focusedByAccessKey = false;
            return;
        }

        base.OnAccessKey(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        if (e.Method is FocusNavigationMethod.AccessKey)
            _focusedByAccessKey = true;
    }
}
