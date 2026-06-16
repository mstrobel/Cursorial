using System.Globalization;
using Cursorial.UI.Xaml;
using Cursorial.Rendering;
using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Hand-written, context-free converters for the X0 frontend matrix rows. They prove the
/// constant-folding path (XD3): each is <c>IsContextFree</c>, so the parser folds literals at parse.
/// The production terminal-converter ladder (reusing <c>StyleSetterConverter</c>) lands at X1; these
/// fixtures model the same shapes (integer cells, geometry, enums, bool, double, grid length) so the
/// frontend's fold/diagnostic behavior is exercisable now.
/// </summary>
internal static class TestConverters
{
    public static readonly ITypeConverter IntCell = new IntCellConverter();
    public static readonly ITypeConverter Margins = new MarginsConverter();
    public static readonly ITypeConverter Bool = new BoolConverter();
    public static readonly ITypeConverter Double = new DoubleConverter();
    public static readonly ITypeConverter GridLength = new GridLengthConverter();

    public static ITypeConverter ForEnum<TEnum>() where TEnum : struct, Enum => new EnumConverter<TEnum>();

    private static XamlParseException Fail(string message, in XamlValueContext ctx)
        => new(XamlDiagnostic.Error(XamlDiagnosticCodes.ConversionFailed, message, ctx.Source, ctx.Line, ctx.Column));

    private sealed class IntCellConverter : ITypeConverter
    {
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            if (int.TryParse(text, NumberStyles.Integer, ctx.Culture, out int v))
                return v;
            // a fractional component is CUR2401 (XD12).
            if (double.TryParse(text, NumberStyles.Float, ctx.Culture, out _))
                throw Fail($"Cells are atomic; '{text}' is not an integer cell count.", ctx);
            throw Fail($"'{text}' is not a valid integer.", ctx);
        }
    }

    private sealed class MarginsConverter : ITypeConverter
    {
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
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

    private sealed class BoolConverter : ITypeConverter
    {
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (bool.TryParse(text.Trim(), out bool v))
                return v;
            throw Fail($"'{text}' is not a valid Boolean.", ctx);
        }
    }

    private sealed class DoubleConverter : ITypeConverter
    {
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, ctx.Culture, out double v))
                return v;
            throw Fail($"'{text}' is not a valid number.", ctx);
        }
    }

    private sealed class EnumConverter<TEnum> : ITypeConverter where TEnum : struct, Enum
    {
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            if (Enum.TryParse<TEnum>(text, ignoreCase: false, out var v) && Enum.IsDefined(typeof(TEnum), v))
                return v;
            // an integral underlying value is also accepted (matches the StyleSetterConverter enum rule)
            if (int.TryParse(text, out int n) && Enum.IsDefined(typeof(TEnum), n))
                return (TEnum)Enum.ToObject(typeof(TEnum), n);
            throw Fail($"'{text}' is not a member of {typeof(TEnum).Name}.", ctx);
        }
    }

    private sealed class GridLengthConverter : ITypeConverter
    {
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            if (string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase))
                return UIControls.GridLength.Auto;
            if (text == "*")
                return UIControls.GridLength.Star(1);
            if (text.EndsWith("*", StringComparison.Ordinal))
            {
                var weightText = text.Substring(0, text.Length - 1);
                if (double.TryParse(weightText, NumberStyles.Float, ctx.Culture, out double w))
                    return UIControls.GridLength.Star(w);
                throw Fail($"'{text}' is not a valid star grid length.", ctx);
            }
            if (int.TryParse(text, NumberStyles.Integer, ctx.Culture, out int cells))
                return UIControls.GridLength.FromCells(cells);
            if (double.TryParse(text, NumberStyles.Float, ctx.Culture, out _))
                throw Fail($"Cells are atomic; '{text}' is not an integer cell count.", ctx);
            throw Fail($"'{text}' is not a valid grid length.", ctx);
        }
    }
}
