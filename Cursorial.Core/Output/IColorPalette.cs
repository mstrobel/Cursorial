using System.Collections;

namespace Cursorial.Output;

public interface IColorPalette : IReadOnlyList<Color>
{
    static IColorPalette Empty { get; } = new ColorPalette(Array.Empty<Color>());
}

public sealed class ColorPalette : IColorPalette
{
    private readonly IReadOnlyList<Color> _colors;

    public ColorPalette(IReadOnlyList<Color> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        _colors = colors;
    }

    public IEnumerator<Color> GetEnumerator()
    {
        return _colors.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public int Count => _colors.Count;

    public Color this[int index] => _colors[index];
}