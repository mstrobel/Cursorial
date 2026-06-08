namespace Cursorial.Drawing;

/// <summary>
/// How a stroke resolves against an existing stroke from a <em>different</em> draw call where they
/// cross within the same scene. (A stroke always self-merges with its own call's cells — a box's
/// corners always close regardless of this mode.)
/// </summary>
public enum JunctionMode : byte
{
    /// <summary>Per-direction max-union — junctions form (┼ ╂ ┿ ╋). The default.</summary>
    Merge = 0,

    /// <summary>The incoming stroke yields at the conflict cell; the prior stroke stays continuous and
    /// the incoming one shows a one-cell gap.</summary>
    Break = 1,

    /// <summary>The incoming stroke replaces the conflict cell; the incoming one reads continuous and
    /// the prior stroke shows a one-cell gap.</summary>
    Overlay = 2
}
