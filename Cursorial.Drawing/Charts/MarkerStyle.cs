// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>The glyph a <see cref="ScatterChart"/> stamps at each point (all width-1).</summary>
public enum MarkerStyle
{
    /// <summary>Filled circle ● (the default).</summary>
    Dot,

    /// <summary>Hollow circle ○.</summary>
    Circle,

    /// <summary>Filled square ■.</summary>
    Square,

    /// <summary>Filled diamond ◆.</summary>
    Diamond,

    /// <summary>Filled up-triangle ▲.</summary>
    Triangle,

    /// <summary>Multiplication sign ✕.</summary>
    Cross,

    /// <summary>A single braille dot — sub-cell precision (multiple points can share a cell).</summary>
    Braille
}
