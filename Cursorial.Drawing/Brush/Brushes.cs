using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>
/// Predefined <see cref="SolidColorBrush"/> instances for the terminal default, transparent, true
/// black / white, and the 16 standard ANSI palette colors — the brush counterparts of the
/// corresponding <see cref="Colors"/> entries. (The extended-palette samples <c>Colors.Extended1/2</c>
/// are intentionally not mirrored; construct <c>new SolidColorBrush(Colors.Extended1)</c> if needed.)
/// Because a <see cref="SolidColorBrush"/> is immutable, these singletons are safe to share — reach for
/// them instead of allocating a fresh <c>new SolidColorBrush(Color.Xxx)</c> for the common solid cases.
/// </summary>
public static class Brushes
{
    /// <summary>A brush over <see cref="Colors.Default"/> — the terminal's default color.</summary>
    public static readonly SolidColorBrush Default = new(Colors.Default);

    /// <summary>A fully transparent brush (alpha 0); samples to <see cref="Colors.Transparent"/> everywhere.</summary>
    public static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    /// <summary>A brush over <see cref="Colors.TrueBlack"/> — opaque RGB (0, 0, 0).</summary>
    public static readonly SolidColorBrush TrueBlack = new(Colors.TrueBlack);

    /// <summary>A brush over <see cref="Colors.TrueWhite"/> — opaque RGB (255, 255, 255).</summary>
    public static readonly SolidColorBrush TrueWhite = new(Colors.TrueWhite);

    /// <summary>A brush over the ANSI palette 'black' (<see cref="Colors.Black"/>).</summary>
    public static readonly SolidColorBrush Black = new(Colors.Black);

    /// <summary>A brush over the ANSI palette 'red' (<see cref="Colors.Red"/>).</summary>
    public static readonly SolidColorBrush Red = new(Colors.Red);

    /// <summary>A brush over the ANSI palette 'green' (<see cref="Colors.Green"/>).</summary>
    public static readonly SolidColorBrush Green = new(Colors.Green);

    /// <summary>A brush over the ANSI palette 'yellow' (<see cref="Colors.Yellow"/>).</summary>
    public static readonly SolidColorBrush Yellow = new(Colors.Yellow);

    /// <summary>A brush over the ANSI palette 'blue' (<see cref="Colors.Blue"/>).</summary>
    public static readonly SolidColorBrush Blue = new(Colors.Blue);

    /// <summary>A brush over the ANSI palette 'magenta' (<see cref="Colors.Magenta"/>).</summary>
    public static readonly SolidColorBrush Magenta = new(Colors.Magenta);

    /// <summary>A brush over the ANSI palette 'cyan' (<see cref="Colors.Cyan"/>).</summary>
    public static readonly SolidColorBrush Cyan = new(Colors.Cyan);

    /// <summary>A brush over the ANSI palette 'white' (<see cref="Colors.White"/>).</summary>
    public static readonly SolidColorBrush White = new(Colors.White);

    /// <summary>A brush over the ANSI palette 'light black' (<see cref="Colors.LightBlack"/>).</summary>
    public static readonly SolidColorBrush LightBlack = new(Colors.LightBlack);

    /// <summary>A brush over the ANSI palette 'light red' (<see cref="Colors.LightRed"/>).</summary>
    public static readonly SolidColorBrush LightRed = new(Colors.LightRed);

    /// <summary>A brush over the ANSI palette 'light green' (<see cref="Colors.LightGreen"/>).</summary>
    public static readonly SolidColorBrush LightGreen = new(Colors.LightGreen);

    /// <summary>A brush over the ANSI palette 'light yellow' (<see cref="Colors.LightYellow"/>).</summary>
    public static readonly SolidColorBrush LightYellow = new(Colors.LightYellow);

    /// <summary>A brush over the ANSI palette 'light blue' (<see cref="Colors.LightBlue"/>).</summary>
    public static readonly SolidColorBrush LightBlue = new(Colors.LightBlue);

    /// <summary>A brush over the ANSI palette 'light magenta' (<see cref="Colors.LightMagenta"/>).</summary>
    public static readonly SolidColorBrush LightMagenta = new(Colors.LightMagenta);

    /// <summary>A brush over the ANSI palette 'light cyan' (<see cref="Colors.LightCyan"/>).</summary>
    public static readonly SolidColorBrush LightCyan = new(Colors.LightCyan);

    /// <summary>A brush over the ANSI palette 'light white' (<see cref="Colors.LightWhite"/>).</summary>
    public static readonly SolidColorBrush LightWhite = new(Colors.LightWhite);
}
