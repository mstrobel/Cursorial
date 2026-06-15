using System;
using System.Collections.Concurrent;
using System.Globalization;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The terminal-converter registry (matrix §7 / C-15): the public, load-independent runtime seam that
/// resolves an <see cref="ITypeConverter"/> for a target CLR type. The built-in ladder mirrors
/// <c>Cursorial.UI.StyleSetterConverter</c>'s shapes — integer-cell geometry, <c>Color</c>/hex,
/// <c>IBrush</c>, enums, <c>bool</c>/<c>int</c>/<c>double</c> via <see cref="IConvertible"/> — and adds
/// the XAML-specific terminal converters (<c>GridLength</c>, <c>Margins</c>, <c>TextAttributes</c>,
/// <c>KeyGesture</c>, named ANSI colors, the <c>BrushMarkup</c> gradient grammar). <see cref="Register"/>
/// overrides a registration; <see cref="For"/> returns the registered or built-in converter, or
/// <c>null</c> when the type has no string converter (matrix X100).
/// </summary>
public static class XamlConverters
{
    private static readonly ConcurrentDictionary<Type, ITypeConverter?> Cache = new();
    private static readonly ConcurrentDictionary<Type, ITypeConverter> Overrides = new();

    /// <summary>
    /// The converter for <paramref name="targetType"/>: an explicit override, then a built-in, then
    /// <c>null</c> (the type has no string converter — the loader leaves the value as a string or
    /// reports a non-conversion). Enums and <see cref="IConvertible"/> primitives resolve generically.
    /// </summary>
    public static ITypeConverter? For(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        if (Overrides.TryGetValue(targetType, out var overridden))
            return overridden;

        return Cache.GetOrAdd(targetType, Build);
    }

    /// <summary>
    /// Registers (or overrides) the converter for <paramref name="targetType"/> — the C-15 seam S2 and
    /// S7 consume for target-type fallback / DynamicResource conversion. Subsequent <see cref="For"/>
    /// calls return <paramref name="converter"/>.
    /// </summary>
    public static void Register(Type targetType, ITypeConverter converter)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(converter);
        Overrides[targetType] = converter;
    }

    private static ITypeConverter? Build(Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(int)) return Int32CellConverter.Instance;
        if (underlying == typeof(double)) return DoubleConverter.Instance;
        if (underlying == typeof(bool)) return BoolConverter.Instance;
        if (underlying == typeof(string)) return StringPassthroughConverter.Instance;
        if (underlying == typeof(Margins)) return MarginsConverter.Instance;
        if (underlying == typeof(GridLength)) return GridLengthConverter.Instance;
        if (underlying == typeof(RelativePoint)) return RelativePointConverter.Instance;
        if (underlying == typeof(Color)) return ColorConverter.Instance;
        if (underlying == typeof(TextAttributes)) return TextAttributesConverter.Instance;
        if (underlying == typeof(KeyGesture)) return KeyGestureConverter.Instance;
        if (underlying == typeof(Pen)) return PenConverter.Instance;
        if (underlying == typeof(Selector)) return SelectorConverter.Instance;
        if (underlying == typeof(Themes.GlyphSetCarrier)) return GlyphSetCarrierConverter.Instance;
        if (underlying == typeof(Type)) return null; // x:Type handles this; no plain text converter
        if (typeof(IBrush).IsAssignableFrom(underlying)) return BrushConverter.Instance;
        if (underlying.IsEnum) return new EnumConverter(underlying);
        if (typeof(IConvertible).IsAssignableFrom(underlying) && (underlying.IsPrimitive || underlying == typeof(decimal)))
            return new ConvertibleConverter(underlying);

        return null;
    }

    internal static XamlParseException Fail(string message, in XamlValueContext ctx)
        => new(XamlDiagnostic.Error(XamlDiagnosticCodes.ConversionFailed, message, ctx.Source, ctx.Line, ctx.Column));

    // ── Integer cells (matrix XD12) ──────────────────────────────────────────────────────────────

    private sealed class Int32CellConverter : ITypeConverter
    {
        public static readonly Int32CellConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign, ctx.Culture, out int v))
                return v;
            if (double.TryParse(text, NumberStyles.Float, ctx.Culture, out _))
                throw Fail($"Cells are atomic; '{text}' is not an integer cell count.", ctx);
            throw Fail($"'{text}' is not a valid integer.", ctx);
        }
    }

    private sealed class DoubleConverter : ITypeConverter
    {
        public static readonly DoubleConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, ctx.Culture, out double v))
                return v;
            throw Fail($"'{text}' is not a valid number.", ctx);
        }
    }

    private sealed class BoolConverter : ITypeConverter
    {
        public static readonly BoolConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (bool.TryParse(text.Trim(), out bool v))
                return v;
            throw Fail($"'{text}' is not a valid Boolean.", ctx);
        }
    }

    private sealed class StringPassthroughConverter : ITypeConverter
    {
        public static readonly StringPassthroughConverter Instance = new();

        // A string slot is context-dependent: the access-key fold (XD11) is decided in stage 2 against
        // the target runtime type. Folding here at parse would lose that hook — keep strings as Text.
        public bool IsContextFree => false;

        public object? ConvertFromString(string text, in XamlValueContext ctx) => text;
    }

    // ── Geometry (matrix XD12) ───────────────────────────────────────────────────────────────────

    private sealed class MarginsConverter : ITypeConverter
    {
        public static readonly MarginsConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            var parts = text.Split(',');
            int[] vals = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (!int.TryParse(p, NumberStyles.Integer | NumberStyles.AllowLeadingSign, ctx.Culture, out vals[i]))
                {
                    if (double.TryParse(p, NumberStyles.Float, ctx.Culture, out _))
                        throw Fail($"Cells are atomic; '{p}' is not an integer cell count.", ctx);
                    throw Fail($"'{p}' is not a valid margin component.", ctx);
                }
            }

            return vals.Length switch
            {
                1 => new Margins(vals[0]),
                2 => new Margins(vals[0], vals[1]),
                4 => new Margins(vals[0], vals[1], vals[2], vals[3]),
                _ => throw Fail("Margin accepts 1, 2, or 4 components.", ctx),
            };
        }
    }

    private sealed class GridLengthConverter : ITypeConverter
    {
        public static readonly GridLengthConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            if (string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase))
                return GridLength.Auto;
            if (text == "*")
                return GridLength.Star(1);
            if (text.EndsWith("*", StringComparison.Ordinal))
            {
                var weightText = text.Substring(0, text.Length - 1);
                if (double.TryParse(weightText, NumberStyles.Float, ctx.Culture, out double w))
                    return GridLength.Star(w);
                throw Fail($"'{text}' is not a valid star grid length.", ctx);
            }
            if (int.TryParse(text, NumberStyles.Integer, ctx.Culture, out int cells))
                return GridLength.FromCells(cells);
            if (double.TryParse(text, NumberStyles.Float, ctx.Culture, out _))
                throw Fail($"Cells are atomic; '{text}' is not an integer cell count.", ctx);
            throw Fail($"'{text}' is not a valid grid length.", ctx);
        }
    }

    // ── Color / brush / pen (matrix XD13) ────────────────────────────────────────────────────────

    private sealed class ColorConverter : ITypeConverter
    {
        public static readonly ColorConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx) => Parse(text, ctx);

        internal static Color Parse(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal))
                return ParseHex(text, ctx);

            // Palette(n)
            if (text.StartsWith("Palette(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(")", StringComparison.Ordinal))
            {
                var inner = text.Substring("Palette(".Length, text.Length - "Palette(".Length - 1).Trim();
                if (byte.TryParse(inner, NumberStyles.Integer, ctx.Culture, out byte index))
                    return Color.FromPalette(index);
                throw Fail($"'{text}' is not a valid palette color (expected Palette(0..255)).", ctx);
            }

            if (NamedColors.TryGet(text, out var named))
                return named;

            throw Fail($"'{text}' is not a recognized color (use #hex, a named ANSI color, Palette(n), Default, or Transparent).", ctx);
        }

        private static Color ParseHex(string text, in XamlValueContext ctx)
        {
            var body = text.Substring(1);
            // #AARRGGBB / #ARGB extension: an 8- or 4-digit hex carries leading alpha.
            try
            {
                if (body.Length == 8)
                {
                    byte a = ParseByte(body, 0);
                    byte r = ParseByte(body, 2);
                    byte g = ParseByte(body, 4);
                    byte b = ParseByte(body, 6);
                    return Color.FromRgba(r, g, b, a);
                }
                if (body.Length is 3 or 6)
                    return Color.FromHex(text.AsSpan());
            }
            catch (ArgumentException)
            {
                // fall through to the diagnostic
            }

            throw Fail($"'{text}' is not a valid hex color (expected #RGB, #RRGGBB, or #AARRGGBB).", ctx);
        }

        private static byte ParseByte(string s, int offset)
            => byte.Parse(s.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private sealed class BrushConverter : ITypeConverter
    {
        public static readonly BrushConverter Instance = new();

        // The BrushMarkup gradient grammar (linear:/radial:/conic:) is context-free text, as is a plain
        // color → SolidColorBrush; fold at parse.
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();

            // A gradient brush: "kind:colorA,colorB[,colorC…]" with stops spread evenly across [0,1].
            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                var kind = text.Substring(0, colon).Trim().ToLowerInvariant();
                if (kind is "linear" or "radial" or "conic")
                    return ParseGradient(kind, text.Substring(colon + 1), ctx);
            }

            // A plain color: the cached Brushes.* singleton when one exists, else a fresh SolidColorBrush.
            var color = ColorConverter.Parse(text, ctx);
            return NamedBrushes.ForOrCreate(color);
        }

        private static IBrush ParseGradient(string kind, string body, in XamlValueContext ctx)
        {
            var tokens = body.Split(',');
            if (tokens.Length < 2)
                throw Fail($"A {kind} gradient needs at least two color stops.", ctx);

            var stops = new GradientStop[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                var color = ColorConverter.Parse(tokens[i].Trim(), ctx);
                double offset = tokens.Length == 1 ? 0.0 : (double)i / (tokens.Length - 1);
                stops[i] = new GradientStop(offset, color);
            }

            return kind switch
            {
                "linear" => new LinearGradientBrush(stops),
                "radial" => new RadialGradientBrush(stops),
                _ => new ConicGradientBrush(stops),
            };
        }
    }

    private sealed class PenConverter : ITypeConverter
    {
        public static readonly PenConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            // "Heavy", "Double Rounded", "Dashed #888" — space-separated preset tokens + an optional color.
            var tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var pen = Pens.Light;
            bool any = false;

            foreach (var token in tokens)
            {
                if (token.StartsWith("#", StringComparison.Ordinal))
                {
                    // Fold the #hex color stroke onto the pen (preserving the preset fields via `with`).
                    // Previously the parsed color was discarded — an uncolored pen. (Palette / named pen
                    // colors remain a future extension; the theme's RGB-tier pens use #hex.)
                    pen = pen.WithColor(ColorConverter.Parse(token, ctx));
                    any = true;
                    continue;
                }

                switch (token.ToLowerInvariant())
                {
                    case "light": pen = pen with { Weight = StrokeWeight.Light }; break;
                    case "heavy": pen = pen with { Weight = StrokeWeight.Heavy }; break;
                    case "double": pen = pen with { Weight = StrokeWeight.Double }; break;
                    case "rounded": pen = pen with { Corners = CornerStyle.Rounded }; break;
                    case "dashed": pen = pen with { Dash = LineDash.Triple }; break;
                    case "ascii": pen = pen with { GlyphSet = GlyphSet.Ascii }; break;
                    default:
                        throw Fail($"'{token}' is not a recognized pen preset (Light/Heavy/Double/Rounded/Dashed/Ascii).", ctx);
                }
                any = true;
            }

            if (!any)
                throw Fail($"'{text}' is not a valid pen.", ctx);

            return pen;
        }
    }

    // ── Enums / IConvertible (matrix X90/X92) ────────────────────────────────────────────────────

    private sealed class EnumConverter : ITypeConverter
    {
        private readonly Type _enumType;

        public EnumConverter(Type enumType) => _enumType = enumType;

        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            // Enum names in XAML are case-insensitive (WPF/Avalonia parity, P6 review P1-6).
            if (Enum.TryParse(_enumType, text, ignoreCase: true, out var v) && v is not null && Enum.IsDefined(_enumType, v))
                return v;
            if (long.TryParse(text, NumberStyles.Integer, ctx.Culture, out long n) && Enum.IsDefined(_enumType, Enum.ToObject(_enumType, n)))
                return Enum.ToObject(_enumType, n);
            throw Fail($"'{text}' is not a member of {_enumType.Name}.", ctx);
        }
    }

    private sealed class TextAttributesConverter : ITypeConverter
    {
        public static readonly TextAttributesConverter Instance = new();
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            // A comma- or pipe-separated flags list — Enum.Parse honors both with the [Flags] enum;
            // case-insensitive to match the other enum converters (P6 review P1-6).
            text = text.Trim();
            if (Enum.TryParse<TextAttributes>(text, ignoreCase: true, out var v))
                return v;
            throw Fail($"'{text}' is not a valid TextAttributes flag combination.", ctx);
        }
    }

    private sealed class KeyGestureConverter : ITypeConverter
    {
        public static readonly KeyGestureConverter Instance = new();

        // KeyGesture.Parse needs no services and never touches the tree — context-free.
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return KeyGesture.Parse(text.Trim());
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw Fail($"'{text}' is not a valid key gesture: {ex.Message}", ctx);
            }
        }
    }

    private sealed class SelectorConverter : ITypeConverter
    {
        public static readonly SelectorConverter Instance = new();

        // Selector.Parse needs no XAML services (it carries the same default type resolver the code-first
        // themes use for simple type names) and never touches the tree — context-free. Enables authoring a
        // control theme's nested state sub-rules as `<Style Selector="^:focus">` inside `<Style.Children>`.
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return Selector.Parse(text.Trim());
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw Fail($"'{text}' is not a valid selector: {ex.Message}", ctx);
            }
        }
    }

    private sealed class RelativePointConverter : ITypeConverter
    {
        public static readonly RelativePointConverter Instance = new();

        // A bounds-relative point authored as "x,y" fractions (e.g. StartPoint="0,0" EndPoint="1,1" on a
        // gradient brush). Named points (TopLeft/Center/…) are reachable via {x:Static RelativePoint.Center}.
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            var parts = text.Split(',');
            if (parts.Length == 2
                && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && double.IsFinite(x) && double.IsFinite(y)) // mirror GradientBrush's RequireFinite (no NaN/Infinity geometry)
                return new RelativePoint(x, y);

            throw Fail($"'{text}' is not a valid relative point (expected finite 'x,y', e.g. '0,0' or '0.5,1').", ctx);
        }
    }

    private sealed class GlyphSetCarrierConverter : ITypeConverter
    {
        public static readonly GlyphSetCarrierConverter Instance = new();

        // A theme glyph triple authored as a compact '|'-separated string: "unchecked|checked[|indeterminate]"
        // (a glyph run never contains '|'; spaces inside a run — e.g. the "[ ]" unchecked box — are preserved,
        // so the parts are NOT trimmed). The 2-part form leaves Indeterminate empty (the two-arg carrier). Used
        // by the caps-unicode theme styles' ToggleGlyph.Glyphs setter. Context-free.
        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            var parts = text.Split('|');
            if (parts.Length is < 2 or > 3)
                throw Fail($"'{text}' is not a valid glyph set: expected 'unchecked|checked' or 'unchecked|checked|indeterminate'.", ctx);

            return new Themes.GlyphSetCarrier(parts[0], parts[1], parts.Length == 3 ? parts[2] : string.Empty);
        }
    }

    private sealed class ConvertibleConverter : ITypeConverter
    {
        private readonly Type _targetType;

        public ConvertibleConverter(Type targetType) => _targetType = targetType;

        public bool IsContextFree => true;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return Convert.ChangeType(text.Trim(), _targetType, ctx.Culture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw Fail($"'{text}' is not convertible to {_targetType.Name}: {ex.Message}", ctx);
            }
        }
    }
}
