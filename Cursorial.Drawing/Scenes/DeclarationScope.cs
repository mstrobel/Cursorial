// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// The coordinate space a per-run <see cref="BrushedStyle"/> brush samples against — the geometry of the
/// element the brush was <em>declared</em> on (see design doc §8). A run's gradient spans the thing you put it
/// on: itself, its block, or the whole document.
/// </summary>
public enum DeclarationScope
{
    /// <summary>Sample against the run's own rect (1-D reading-order strip). The default for a run-level brush.</summary>
    Inline,

    /// <summary>Sample against the enclosing block's rect (2-D — a paragraph fill).</summary>
    Block,

    /// <summary>Sample against the whole painted document bounds.</summary>
    Document,
}
