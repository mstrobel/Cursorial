// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// How a 2-arm corner is drawn. <see cref="Rounded"/> realizes only on the four all-light corners
/// (╭╮╯╰) — Unicode has no heavy or double arc — so a heavy/double corner degrades to its sharp form.
/// </summary>
public enum CornerStyle : byte
{
    /// <summary>Square corner (┌┐┘└). The default.</summary>
    Sharp = 0,

    /// <summary>Rounded arc (╭╮╯╰), light corners only.</summary>
    Rounded = 1
}
