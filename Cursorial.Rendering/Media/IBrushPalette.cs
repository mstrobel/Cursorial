using System.Collections;

using Cursorial.Media;

namespace Cursorial.Rendering.Media;

public interface IBrushPalette : IReadOnlyList<IBrush>
{
    static IBrushPalette Empty { get; } = new BrushPalette(Array.Empty<IBrush>());

    IBrush this[Ansi16Color color] { get; }
}

public sealed class BrushPalette : IBrushPalette
{
    #region Ansi Palettes

    public static readonly BrushPalette Ansi16 = BuildPalette(16);
    public static readonly BrushPalette Ansi256 = BuildPalette(256);

    private static BrushPalette BuildPalette(int size)
    {
        if (size is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(size), "Palette size must be between 0 and 256");

        IBrush[] brushes = ColorPalette.Ansi256
                                       .Take(size)
                                       .Select(o => (IBrush) new SolidColorBrush(o))
                                       .ToArray();

        return new BrushPalette(brushes);
    }

    #endregion

    private readonly IReadOnlyList<IBrush> _colors;

    public BrushPalette(IReadOnlyList<IBrush> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        _colors = colors;
    }

    public IEnumerator<IBrush> GetEnumerator()
    {
        return _colors.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public int Count => _colors.Count;

    public IBrush this[int index] => _colors[index];

    public IBrush this[Ansi16Color color]
    {
        get
        {
            if (Enum.IsDefined(color))
                return _colors[(int) color];
            throw new ArgumentOutOfRangeException(nameof(color));
        }
    }
}
