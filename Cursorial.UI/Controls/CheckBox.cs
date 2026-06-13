namespace Cursorial.UI.Controls;

/// <summary>
/// A labeled checkbox (design doc §12.7): a <see cref="ToggleButton"/> whose default theme draws a
/// glyph cell + a space + the <see cref="ContentControl.Content"/> presenter. The glyphs are theme
/// resources (strings) selected by the capability color/ascii classes — ASCII <c>[ ] [x] [-]</c>
/// everywhere safe, a Unicode <c>☐ ☑ ◪</c>-class swap on a <c>caps-unicode</c> tier (CD26).
/// </summary>
public class CheckBox : ToggleButton
{
}
