using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// How a <see cref="Scene"/> is composited onto a target: an integer cell translation, a uniform
/// opacity, an optional clip, and an optional blend mode. Affine rotation / scale are deliberately
/// absent — a terminal cell grid cannot express sub-cell transforms; v1 offers integer translation
/// only.
/// </summary>
/// <remarks>
/// <c>default(CompositeParameters)</c> is the opaque identity (offset 0, opacity 255, no clip,
/// default blend) — opacity is stored as its complement so the all-zero struct default is fully
/// opaque rather than fully transparent.
/// </remarks>
public readonly record struct CompositeParameters
{
    private readonly byte _opacityComplement;   // 0 => fully opaque (and the struct default)

    /// <summary>
    /// Construct composite parameters. <paramref name="opacity"/> is 0 (transparent) – 255 (opaque)
    /// and defaults to fully opaque.
    /// </summary>
    public CompositeParameters(int offsetColumn = 0, int offsetRow = 0, byte opacity = 255,
                               Rect? clip = null, IBlendingMode? mode = null)
    {
        OffsetColumn = offsetColumn;
        OffsetRow = offsetRow;
        _opacityComplement = (byte) (255 - opacity);
        Clip = clip;
        Mode = mode;
    }

    /// <summary>The opaque identity (offset 0, opacity 255, no clip, default blend).</summary>
    public static CompositeParameters Default => default;

    /// <summary>Horizontal cell translation applied when compositing onto the target.</summary>
    public int OffsetColumn { get; init; }

    /// <summary>Vertical cell translation applied when compositing onto the target.</summary>
    public int OffsetRow { get; init; }

    /// <summary>Uniform opacity, 0 (transparent) – 255 (opaque). Scales each painted source cell's alpha.</summary>
    public byte Opacity => (byte) (255 - _opacityComplement);

    /// <summary>Optional clip rectangle in target coordinates; only cells inside it are written.</summary>
    public Rect? Clip { get; init; }

    /// <summary>Optional blend mode; <see langword="null"/> uses <see cref="BlendingModes.Default"/>.</summary>
    public IBlendingMode? Mode { get; init; }

    /// <summary>Return a copy with the given opacity.</summary>
    public CompositeParameters WithOpacity(byte opacity) =>
        new(OffsetColumn, OffsetRow, opacity, Clip, Mode);

    /// <summary>Return a copy translated to the given cell offset.</summary>
    public CompositeParameters WithOffset(int offsetColumn, int offsetRow) =>
        new(offsetColumn, offsetRow, Opacity, Clip, Mode);

    /// <summary>Return a copy with the given clip rectangle.</summary>
    public CompositeParameters WithClip(Rect? clip) =>
        new(OffsetColumn, OffsetRow, Opacity, clip, Mode);

    /// <summary>Return a copy with the given blend mode (<see langword="null"/> = <see cref="BlendingModes.Default"/>).</summary>
    public CompositeParameters WithMode(IBlendingMode? mode) =>
        new(OffsetColumn, OffsetRow, Opacity, Clip, mode);
}
