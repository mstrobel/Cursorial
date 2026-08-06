using Cursorial.Output;

namespace Cursorial.Media;

/// <summary>
/// Combines a "source" color with a "backdrop" color to produce a result. Used by
/// <c>CellBuffer.PushBlendingMode</c> to control how new cells composite over existing ones —
/// the cell-buffer blends each color channel of the source <see cref="CellStyle"/> with the
/// existing cell's matching channel through the active blending mode, then stores the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless and pure.</b> Implementations MUST be deterministic and side-effect-free.
/// Built-in modes in <see cref="BlendingModes"/> are singletons; custom modes following the
/// same contract can be safely shared across buffers and threads.
/// </para>
/// <para>
/// <b>Non-RGB inputs.</b> Without an alpha channel, blending modes only have meaningful results
/// when both <c>source</c> and <c>backdrop</c> are <see cref="ColorKind.Rgb"/>.
/// Implementations SHOULD short-circuit to "return source" when either side is
/// <see cref="ColorKind.Default"/> (no known RGB equivalent) or <see cref="ColorKind.Palette"/>
/// (round-tripping through RGB would be lossy and surprising). The built-in modes follow that
/// convention; consumers building custom modes are encouraged to match it.
/// </para>
/// </remarks>
public interface IBlendingMode
{
    /// <summary>
    /// Compute the composite color of <paramref name="source"/> over <paramref name="backdrop"/>.
    /// </summary>
    /// <param name="source">The color being drawn — typically the new <see cref="CellStyle"/>'s field.</param>
    /// <param name="backdrop">The color already present at the target position.</param>
    Color Blend(Color source, Color backdrop);
}