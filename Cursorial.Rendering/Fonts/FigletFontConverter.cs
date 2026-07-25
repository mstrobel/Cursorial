using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Cursorial.Rendering.Fonts;

public sealed class FigletFontConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) ||
               sourceType == typeof(Uri);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
    {
        return destinationType == typeof(string) ||
               destinationType == typeof(Uri);
    }

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string nameOrUri)
            return ResolveFigletFont(nameOrUri);

        if (value is Uri uri)
            return FigletFontParser.LoadFromUri(uri);

        throw GetConvertFromException(value);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is FigletFont font)
        {
            if (destinationType == typeof(Uri))
                return font.SourceUri;

            if (destinationType == typeof(string))
                return font.Name;
        }

        throw GetConvertToException(value, destinationType);
    }

    private static IGlyphFont ResolveFigletFont(string nameOrUri)
    {
        return nameOrUri.ToLowerInvariant() switch
               {
                   "standard"        => FigletFonts.Standard,
                   "slant"           => FigletFonts.Slant,
                   "small"           => FigletFonts.Small,
                   "big"             => FigletFonts.Big,
                   "mini"            => FigletFonts.Mini,
                   "ansishadow"      => FigletFonts.AnsiShadow,
                   "hp2640largetype" => FigletFonts.Hp2640LargeType,
                   "miniwi"          => FigletFonts.MiniWi,
                   "cga"             => FigletFonts.CGA,
                   "lcdmatrix"       => FigletFonts.LCDMatrix,
                   "led"             => FigletFonts.LED,
                   "roman"           => FigletFonts.Roman,
                   "smallslant"      => FigletFonts.SmallSlant,

                   _ when Uri.TryCreate(nameOrUri,
                                        UriKind.RelativeOrAbsolute,
                                        out var uri) => FigletFontParser.LoadFromUri(uri),

                   _ => throw new InvalidOperationException(
                            $"Unknown figlet font '{nameOrUri}'. Built-ins: standard, slant, small, " +
                            "big, mini, ansishadow, hp2640largetype, miniwi, cga, lcdmatrix, led, roman, " +
                            "smallslant.")
               };
    }
}
