namespace Cursorial.Output;

/// <summary>
/// Built-in <see cref="IBlendingMode"/> singletons. All modes short-circuit to "return source"
/// when either operand is not <see cref="ColorKind.Rgb"/>; the actual blending math runs only
/// for RGB-on-RGB inputs.
/// </summary>
public static class BlendingModes
{
    /// <summary>The conventional name for "no blending" — source replaces backdrop wholesale.</summary>
    public static IBlendingMode SourceOver { get; } = new SourceOverMode();

    /// <summary>
    /// Alias for <see cref="SourceOver"/>. Returned by <c>CellBuffer.CurrentBlendingMode</c>
    /// when the buffer's blend stack is empty.
    /// </summary>
    public static IBlendingMode Default => SourceOver;

    /// <summary>
    /// Multiply blending — channel-wise <c>(source * backdrop) / 255</c>. Always darkens
    /// (multiplying any value by another &lt;= 1.0 produces a value &lt;= the original). Useful for
    /// translucent shadows and tints.
    /// </summary>
    public static IBlendingMode Multiply { get; } = new MultiplyMode();

    /// <summary>
    /// Screen blending — the inverse of multiply: <c>1 - (1-source) * (1-backdrop)</c>. Always
    /// lightens. Useful for highlights and "additive light" effects.
    /// </summary>
    public static IBlendingMode Screen { get; } = new ScreenMode();

    /// <summary>
    /// Overlay blending — multiply for dark backdrops, screen for light backdrops. Preserves
    /// highlights and shadows while letting source color tint the midtones.
    /// </summary>
    public static IBlendingMode Overlay { get; } = new OverlayMode();

    /// <summary>Darken blending — channel-wise <c>min(source, backdrop)</c>.</summary>
    public static IBlendingMode Darken { get; } = new DarkenMode();

    /// <summary>Lighten blending — channel-wise <c>max(source, backdrop)</c>.</summary>
    public static IBlendingMode Lighten { get; } = new LightenMode();

    /// <summary>
    /// Plus / additive blending — channel-wise <c>min(255, source + backdrop)</c>. Useful for
    /// "glow" effects where overlapping colors accumulate.
    /// </summary>
    public static IBlendingMode Plus { get; } = new PlusMode();

    // ---- Implementations ----

    private sealed class SourceOverMode : IBlendingMode
    {
        public Color Blend(Color source, Color backdrop) => source;
    }

    private abstract class RgbOnlyMode : IBlendingMode
    {
        public Color Blend(Color source, Color backdrop)
        {
            if (source.Kind != ColorKind.Rgb || backdrop.Kind != ColorKind.Rgb)
                return source;
            return Color.FromRgb(
                BlendChannel(source.Red, backdrop.Red),
                BlendChannel(source.Green, backdrop.Green),
                BlendChannel(source.Blue, backdrop.Blue));
        }

        protected abstract byte BlendChannel(byte source, byte backdrop);
    }

    private sealed class MultiplyMode : RgbOnlyMode
    {
        protected override byte BlendChannel(byte s, byte b) => (byte)((s * b) / 255);
    }

    private sealed class ScreenMode : RgbOnlyMode
    {
        protected override byte BlendChannel(byte s, byte b) =>
            (byte)(255 - ((255 - s) * (255 - b)) / 255);
    }

    private sealed class OverlayMode : RgbOnlyMode
    {
        protected override byte BlendChannel(byte s, byte b) =>
            b < 128
                ? (byte)((2 * s * b) / 255)
                : (byte)(255 - (2 * (255 - s) * (255 - b)) / 255);
    }

    private sealed class DarkenMode : RgbOnlyMode
    {
        protected override byte BlendChannel(byte s, byte b) => s < b ? s : b;
    }

    private sealed class LightenMode : RgbOnlyMode
    {
        protected override byte BlendChannel(byte s, byte b) => s > b ? s : b;
    }

    private sealed class PlusMode : RgbOnlyMode
    {
        protected override byte BlendChannel(byte s, byte b)
        {
            int sum = s + b;
            return sum > 255 ? (byte)255 : (byte)sum;
        }
    }
}
