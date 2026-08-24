using System.Collections.Concurrent;
using System.Globalization;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace

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

    /// <summary>The CR7 bridge rung (W2d) — the loader's LAST conversion fallback, after the ladder and
    /// the BCL rung: a single-parameter route into the type (implicit/explicit operator, ctor, static
    /// <c>Parse(string)</c>) from a ladder-convertible source. Unwraps <c>Nullable&lt;T&gt;</c> like the
    /// ladder (the boxed <c>T</c> assigns to a <c>T?</c> member unchanged). See <see cref="ConversionBridge"/>.</summary>
    public static ITypeConverter? BridgeConverterForType(Type targetType)
        => ConversionBridge.For(Nullable.GetUnderlyingType(targetType) ?? targetType);

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

    /// <summary>
    /// The converter for a MEMBER's value, with WPF <c>GetSerializerFor</c> precedence: member
    /// Cursorial <c>[ValueSerializer]</c> → member Cursorial <c>[TypeConverter]</c> → member BCL
    /// <c>[System.ComponentModel.TypeConverter]</c> → member-type Cursorial <c>[ValueSerializer]</c> →
    /// member-type Cursorial <c>[TypeConverter]</c> → the built-in ladder (<see cref="For"/>). This is the ONLY
    /// entry point that consults attributes; <see cref="For"/> itself stays a pure, reflection-free ladder so the
    /// generated/lowered providers can bake <c>For(typeof(T))</c> AOT-clean. Attribute reflection therefore lives
    /// only here (the reflection metadata provider's metadata-build path) — never on a baked code path. The
    /// member-TYPE's BCL converter is the loader's last conversion fallback (<c>BclConverterForType</c>, after the
    /// ladder), not resolved here — so the ladder keeps precedence over a BCL converter for the types we curate.
    /// </summary>
    public static ITypeConverter? ForMember(System.Reflection.MemberInfo? member, Type memberType)
    {
        var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;
        return (member is not null ? SerializerFromAttribute(member) : null)     // member Cursorial [ValueSerializer]
            ?? (member is not null ? ConverterFromAttribute(member) : null)      // member Cursorial [TypeConverter]
            ?? (member is not null ? BclConverterFromMember(member, underlying) : null) // member BCL [System.ComponentModel.TypeConverter]
            ?? SerializerFromAttribute(underlying)                               // member-type Cursorial [ValueSerializer]
            ?? ConverterFromAttribute(underlying)                               // member-type Cursorial [TypeConverter]
            ?? For(memberType);                                                // the built-in ladder
    }

    private static ITypeConverter? Build(Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(int)) return Int32CellConverter.Instance;
        if (underlying == typeof(double)) return DoubleConverter.Instance;
        if (underlying == typeof(bool)) return BoolConverter.Instance;
        if (underlying == typeof(string)) return StringPassthroughConverter.Instance;
        if (underlying == typeof(Uri)) return UriConverter.Instance;
        if (underlying == typeof(TimeSpan)) return TimeSpanConverter.Instance;
        if (underlying == typeof(Margins)) return MarginsConverter.Instance;
        if (underlying == typeof(GridLength)) return GridLengthConverter.Instance;
        if (underlying == typeof(Size)) return SizeConverter.Instance;
        if (underlying == typeof(RelativePoint)) return RelativePointConverter.Instance;
        if (underlying == typeof(Color)) return ColorConverter.Instance;
        if (underlying == typeof(TextAttributes)) return TextAttributesConverter.Instance;
        if (underlying == typeof(KeyGesture)) return KeyGestureConverter.Instance;
        if (underlying == typeof(InputGesture)) return KeyGestureConverter.Instance;
        if (underlying == typeof(Pen)) return PenConverter.Instance;
        if (underlying == typeof(Selector)) return SelectorConverter.Instance;
        if (underlying == typeof(Cursorial.UI.Data.PropertyPath)) return new Cursorial.UI.Data.PropertyPathConverter();
        if (underlying == typeof(Cursorial.Animation.Easing)) return EasingConverter.Instance;
        if (underlying == typeof(RepeatBehavior)) return RepeatBehaviorConverter.Instance;
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Optional<>))
            return CloseOptionalConverter(underlying); // the typed generic converter, closed reflectively (W2c CR4)
        if (underlying == typeof(Themes.GlyphSetCarrier)) return GlyphSetCarrierConverter.Instance;
        if (underlying == typeof(Type)) return null; // x:Type handles this; no plain text converter
        if (typeof(IBrush).IsAssignableFrom(underlying)) return BrushConverter.Instance;
        if (underlying.IsEnum) return new EnumConverter(underlying);
        if (typeof(IConvertible).IsAssignableFrom(underlying) && (underlying.IsPrimitive || underlying == typeof(decimal)))
            return new ConvertibleConverter(underlying);

        return null;
    }

    // Resolves an ITypeConverter declared by a Cursorial.Markup.[TypeConverter] on the type (matched by FULL
    // name to distinguish it from the BCL System.ComponentModel.TypeConverterAttribute; only honored when the
    // named converter implements OUR ITypeConverter). Inherited, so a subclass picks up its base's converter.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Reads a [TypeConverter]-shaped attribute and instantiates the named ITypeConverter — an opt-in consumer reflection seam.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2057:TypeGetType", Justification = "Late-bound converter type name is consumer-provided.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:CreateInstance", Justification = "Converter type is consumer-provided; preserved by the consumer for trimming.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Converter activation is an opt-in consumer reflection seam.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The converter type is named BY an attribute instance already materialized on the member being " +
                        "converted — a type referenced from an attribute blob is preserved together with the attribute, " +
                        "and a miss falls through to the built-in ladder.")]
    private static ITypeConverter? ConverterFromAttribute(System.Reflection.MemberInfo target)
    {
        foreach (var attr in target.GetCustomAttributes(inherit: true))
        {
            var attrType = attr.GetType();
            if (attrType.FullName != "Cursorial.Markup.TypeConverterAttribute")
                continue;

            var converterType = attrType.GetProperty("ConverterType")?.GetValue(attr) as Type;
            if (converterType is null &&
                attrType.GetProperty("ConverterTypeName")?.GetValue(attr) is string typeName && typeName.Length > 0)
            {
                converterType = Type.GetType(typeName, throwOnError: false);
            }

            if (converterType is null || !typeof(ITypeConverter).IsAssignableFrom(converterType))
                continue; // a BCL [TypeConverter] (System.ComponentModel) or a misdeclared converter → fall to the ladder

            try
            {
                if (Activator.CreateInstance(converterType) is ITypeConverter converter)
                    return converter;
            }
            catch
            {
                // No usable parameterless ctor — fall through to the ladder rather than crashing resolution.
            }
        }

        return null;
    }

    // Resolves an IValueSerializer declared by a Cursorial.Markup.[ValueSerializer] on the member/type, adapted
    // to an ITypeConverter (its ConvertFromString leg) for the load path — WPF's GetSerializerFor prefers a
    // ValueSerializer over a TypeConverter. Matched by FULL name; only honored for our IValueSerializer.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Reads a [ValueSerializer]-shaped attribute and instantiates the named IValueSerializer — an opt-in consumer reflection seam.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2057:TypeGetType", Justification = "Late-bound serializer type name is consumer-provided.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:CreateInstance", Justification = "Serializer type is consumer-provided; preserved by the consumer for trimming.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Serializer activation is an opt-in consumer reflection seam.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The serializer type is named BY an attribute instance already materialized on the member being " +
                        "converted — a type referenced from an attribute blob is preserved together with the attribute, " +
                        "and a miss falls through to the built-in ladder.")]
    private static ITypeConverter? SerializerFromAttribute(System.Reflection.MemberInfo target)
    {
        foreach (var attr in target.GetCustomAttributes(inherit: true))
        {
            var attrType = attr.GetType();
            if (attrType.FullName != "Cursorial.Markup.ValueSerializerAttribute")
                continue;

            var serializerType = attrType.GetProperty("ValueSerializerType")?.GetValue(attr) as Type;
            if (serializerType is null &&
                attrType.GetProperty("ValueSerializerTypeName")?.GetValue(attr) is string typeName && typeName.Length > 0)
            {
                serializerType = Type.GetType(typeName, throwOnError: false);
            }

            if (serializerType is null || !typeof(IValueSerializer).IsAssignableFrom(serializerType))
                continue;

            try
            {
                if (Activator.CreateInstance(serializerType) is IValueSerializer serializer)
                    return AdaptSerializer(serializer);
            }
            catch
            {
                // No usable parameterless ctor — fall through (TypeConverter / ladder).
            }
        }

        return null;
    }

    /// <summary>Adapts an <see cref="IValueSerializer"/> to an <see cref="ITypeConverter"/> for the load path
    /// (its deserialize leg). Public so a generated provider can bake the same adaptation.</summary>
    public static ITypeConverter AdaptSerializer(IValueSerializer serializer)
        => new ValueSerializerConverter(serializer ?? throw new ArgumentNullException(nameof(serializer)));

    private sealed class ValueSerializerConverter(IValueSerializer serializer) : ITypeConverter
    {
        public bool IsContextFree => serializer.IsContextFree;
        public object? ConvertFromString(string text, in XamlValueContext ctx) => serializer.ConvertFromString(text, in ctx);
    }

    // ── BCL System.ComponentModel.TypeConverter interop ──────────────────────────────────────────────

    /// <summary>
    /// The member-level BCL converter for a member carrying <c>[System.ComponentModel.TypeConverter]</c> — its
    /// <c>ConvertFrom(string)</c> leg adapted to <see cref="ITypeConverter"/>. WPF parity: the BCL <c>TypeConverter</c>
    /// model is honored for interop, below Cursorial's own <c>[TypeConverter]</c>/<c>[ValueSerializer]</c>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "BCL TypeConverter interop is an opt-in reflection seam (consumer-declared [TypeConverter]).")]
    private static ITypeConverter? BclConverterFromMember(System.Reflection.MemberInfo member, Type targetType)
    {
        if (System.Attribute.GetCustomAttribute(member, typeof(System.ComponentModel.TypeConverterAttribute), inherit: true)
            is not System.ComponentModel.TypeConverterAttribute attr)
            return null;

        var converterType = Type.GetType(attr.ConverterTypeName, throwOnError: false);
        return AdaptBcl(converterType, targetType);
    }

    /// <summary>
    /// The type-level BCL converter for a value type — <c>TypeDescriptor.GetConverter(type)</c> adapted to
    /// <see cref="ITypeConverter"/> when it can convert from string. This is the loader's LAST conversion fallback
    /// (after the curated ladder, so our terminal-aware converters keep precedence for the types we handle), giving
    /// XAML compatibility with any BCL/consumer type carrying a <c>[TypeConverter]</c> (and default converters for
    /// enums, <c>Guid</c>, <c>Version</c>, …). Returns <see langword="null"/> when the type has no string converter.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, ITypeConverter?> BclCache = new();

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "BCL TypeConverter interop is an opt-in reflection seam (TypeDescriptor over a consumer type).")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "TypeDescriptor.GetConverter over a value type whose converter the consumer preserves for trimming.")]
    public static ITypeConverter? BclConverterForType(Type type)
        => BclCache.GetOrAdd(type, static t =>
        {
            var bcl = System.ComponentModel.TypeDescriptor.GetConverter(t);
            return bcl.CanConvertFrom(typeof(string)) ? new BclTypeConverterAdapter(bcl) : null;
        });

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:CreateInstance",
        Justification = "BCL converter type is consumer-provided; preserved by the consumer for trimming.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "BCL TypeConverter adaptation is the reflective ladder's last rung: the converter type arrives " +
                        "from a preserved attribute reference, and a constructor the trimmer removed yields null — the " +
                        "ladder reports no converter, not a crash.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "BCL TypeConverter adaptation is the reflective ladder's last rung: the converter type arrives " +
                        "from a preserved attribute reference, and a constructor the trimmer removed yields null — the " +
                        "ladder reports no converter, not a crash.")]
    private static ITypeConverter? AdaptBcl(Type? converterType, Type targetType)
    {
        if (converterType is null || !typeof(System.ComponentModel.TypeConverter).IsAssignableFrom(converterType))
            return null;

        try
        {
            // The parameterless ctor (the common converter shape), else the (Type) ctor the stock
            // EnumConverter / NullableConverter require — passing the member's value type. Selecting the ctor by
            // reflection (rather than letting Activator throw) lets the (Type) form be reached.
            var bcl = converterType.GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance(converterType) as System.ComponentModel.TypeConverter
                : converterType.GetConstructor([typeof(Type)])?.Invoke([targetType]) as System.ComponentModel.TypeConverter;

            if (bcl is not null && bcl.CanConvertFrom(typeof(string)))
                return new BclTypeConverterAdapter(bcl);
        }
        catch
        {
            // No usable ctor / activation failure — fall through to the next resolution step.
        }

        return null;
    }

    // Adapts a System.ComponentModel.TypeConverter's ConvertFrom(string) to ITypeConverter. Context-DEPENDENT
    // (IsContextFree=false): a BCL converter may consult the type-descriptor context, so it never folds at parse —
    // it runs in stage 2 (load), where the (null) context + culture are passed through.
    private sealed class BclTypeConverterAdapter(System.ComponentModel.TypeConverter inner) : ITypeConverter
    {
        public bool IsContextFree => false;

        public object? ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return inner.ConvertFromString(context: null, ctx.Culture, text);
            }
            catch (Exception ex) when (ex is not XamlParseException)
            {
                throw Fail($"'{text}' is not a valid value (BCL {inner.GetType().Name}): {ex.Message}", ctx, ex);
            }
        }
    }

    internal static XamlParseException Fail(string message, in XamlValueContext ctx, Exception? inner = null)
        => new(XamlDiagnostic.Error(XamlDiagnosticCodes.ConversionFailed, message, ctx.Source, ctx.Line, ctx.Column), inner);

    // ── Integer cells (matrix XD12) ──────────────────────────────────────────────────────────────

    private sealed class Int32CellConverter : ITypeConverter
    {
        public static readonly Int32CellConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
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

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, ctx.Culture, out double v))
                return v;
            throw Fail($"'{text}' is not a valid number.", ctx);
        }
    }

    // A URI value (e.g. Image.SourceUri="embedded://App/logo.png") — relative-or-absolute so embedded://, file://,
    // http(s):// and bare relative paths all parse; the resource loader resolves the scheme at load time.
    private sealed class UriConverter : ITypeConverter
    {
        public static readonly UriConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (Uri.TryCreate(text.Trim(), UriKind.RelativeOrAbsolute, out var uri))
                return uri;
            throw Fail($"'{text}' is not a valid URI.", ctx);
        }
    }

    private sealed class BoolConverter : ITypeConverter
    {
        public static readonly BoolConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (bool.TryParse(text.Trim(), out bool v))
                return v;
            throw Fail($"'{text}' is not a valid Boolean.", ctx);
        }
    }

    // A TimeSpan value (e.g. <x:TimeSpan>00:00:05</x:TimeSpan>) — TimeSpan is a XAML2009 built-in but is neither
    // IsPrimitive nor IConvertible, so it would otherwise fall through Build to null (XD28 — the lone built-in gap).
    private sealed class TimeSpanConverter : ITypeConverter
    {
        public static readonly TimeSpanConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (TimeSpan.TryParse(text.Trim(), ctx.Culture, out var v))
                return v;
            throw Fail($"'{text}' is not a valid TimeSpan.", ctx);
        }
    }

    private sealed class StringPassthroughConverter : ITypeConverter
    {
        public static readonly StringPassthroughConverter Instance = new();

        // A string slot is context-dependent: the access-key fold (XD11) is decided in stage 2 against
        // the target runtime type. Folding here at parse would lose that hook — keep strings as Text.
        public bool IsContextFree => false;

        public object ConvertFromString(string text, in XamlValueContext ctx) => text;
    }

    // ── Animation (design doc §9.10 — the Fork C converter wiring) ──────────────────────────────

    // An easing: a catalog name ("QuadInOut", case-insensitive) or the cubic-bezier(x1,y1,x2,y2)
    // functional form, both via Easings.TryParse (the parse half was authored with this row in mind).
    private sealed class EasingConverter : ITypeConverter
    {
        public static readonly EasingConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (Cursorial.Animation.Easings.TryParse(text, out var easing))
                return easing;
            throw Fail($"'{text}' is not a recognized easing — expected a catalog name (e.g. 'QuadInOut') " +
                       "or 'cubic-bezier(x1,y1,x2,y2)'.", ctx);
        }
    }

    // A repeat behavior: "Forever", "Nx", or a bare iteration count "N" via RepeatBehavior.TryParse
    // (doc-labeled "Fork C converter; §9.10" at the parse half).
    private sealed class RepeatBehaviorConverter : ITypeConverter
    {
        public static readonly RepeatBehaviorConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            if (RepeatBehavior.TryParse(text, out var value))
                return value;
            throw Fail($"'{text}' is not a valid RepeatBehavior — expected 'Forever', 'Nx', or an iteration count.", ctx);
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2076",
        Justification = "The W2c generic-converter closing (the ONE MakeGenericType in the ladder): runs only " +
                        "in the RUC reflective lane — the lowered lane bakes the closed `new OptionalConverter<T>()` " +
                        "form statically and never calls this.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Value-type generic instantiation via MakeGenericType is exactly what NativeAOT cannot " +
                        "create — and exactly why the strict-AOT lane routes through the emitted closed form instead.")]
    private static ITypeConverter? CloseOptionalConverter(Type closedOptional)
    {
        var inner = closedOptional.GetGenericArguments()[0];
        if ((For(inner) ?? BclConverterForType(inner)) is null)
            return null; // no inner grammar — no rung (the caller reports no converter, as before W1)

        return (ITypeConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(inner))!;
    }

    // ── Geometry (matrix XD12) ───────────────────────────────────────────────────────────────────

    private sealed class MarginsConverter : ITypeConverter
    {
        public static readonly MarginsConverter Instance = new();
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

    private sealed class GridLengthConverter : ITypeConverter
    {
        public static readonly GridLengthConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
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

    private sealed class SizeConverter : ITypeConverter
    {
        public static readonly SizeConverter Instance = new();
        private static readonly char[] Delimiters = [',', 'x'];
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            // ctx.Culture like every sibling (Int32CellConverter), and Size documents non-negative
            // components — "-1x-2" is as invalid as "80x".
            if (text.Split(Delimiters, StringSplitOptions.TrimEntries) is not { Length: 2 } parts ||
                !int.TryParse(parts[0], NumberStyles.Integer, ctx.Culture, out var columns) ||
                !int.TryParse(parts[1], NumberStyles.Integer, ctx.Culture, out var rows) ||
                columns < 0 || rows < 0)
            {
                throw Fail($"'{text}' is not a valid {nameof(Size)}.", ctx);
            }

            return new Size(columns, rows);
        }
    }

    // ── Color / brush / pen (matrix XD13) ────────────────────────────────────────────────────────

    private sealed class ColorConverter : ITypeConverter
    {
        public static readonly ColorConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx) => Parse(text, ctx);

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
            // One hex parser, one convention (proposal-textattributes-decomposition §10): Core owns
            // the digits — 8-digit is #RRGGBBAA (alpha LAST; a deliberate DEV from WPF's #AARRGGBB
            // so one alpha convention holds across the whole stack, and StyleDiagnostics.FormatValue
            // output round-trips through this converter).
            if (Color.TryParseHex(text.AsSpan(), out var color, out _))
                return color;

            throw Fail($"'{text}' is not a valid hex color (expected #RGB, #RRGGBB, or #RRGGBBAA).", ctx);
        }
    }

    private sealed class BrushConverter : ITypeConverter
    {
        public static readonly BrushConverter Instance = new();

        // The BrushMarkup gradient grammar (linear:/radial:/conic:) is context-free text, as is a plain
        // color → SolidColorBrush; fold at parse.
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
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

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            // "Heavy", "Double Rounded", "Dashed #888" — space-separated preset tokens + an optional color.
            var tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var pen = Pens.Light;
            bool any = false;

            foreach (var token in tokens)
            {
                switch (token.ToLowerInvariant())
                {
                    case "light": pen = pen with { Weight = StrokeWeight.Light }; break;
                    case "heavy": pen = pen with { Weight = StrokeWeight.Heavy }; break;
                    case "double": pen = pen with { Weight = StrokeWeight.Double }; break;
                    case "rounded": pen = pen with { Corners = CornerStyle.Rounded }; break;
                    case "dashed": pen = pen with { Dash = LineDash.Triple }; break;
                    case "ascii": pen = pen with { GlyphSet = GlyphSet.Ascii }; break;
                    default:
                        // A non-preset token is the stroke color, folded onto the pen (preserving the preset fields
                        // via WithColor): #hex, a named ANSI color, Palette(n), Default, or Transparent — the full
                        // ColorConverter grammar (ColorConverter.Parse throws a color-specific error if it isn't one).
                        pen = pen.WithColor(ColorConverter.Parse(token, ctx));
                        break;
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

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            text = text.Trim();
            // Enum names in XAML are case-insensitive (WPF/Avalonia parity, P6 review P1-6).
            if (Enum.TryParse(_enumType, text, ignoreCase: true, out var v) && IsValidValue(v))
                return v;
            if (long.TryParse(text, NumberStyles.Integer, ctx.Culture, out long n) && IsValidValue(Enum.ToObject(_enumType, n)))
                return Enum.ToObject(_enumType, n);
            throw Fail($"'{text}' is not a member of {_enumType.Name}.", ctx);
        }

        // A single defined member always passes; a [Flags] enum additionally accepts a COMBINATION
        // ("NoColor, Motion" — Enum.TryParse parses the comma form, but IsDefined rejects the combined
        // value) as long as every set bit is covered by the defined members (WPF parity).
        private bool IsValidValue(object value)
        {
            if (Enum.IsDefined(_enumType, value))
                return true;

            if (!_enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
                return false;

            var bits = ToBits(value);
            ulong all = 0;

            foreach (var defined in Enum.GetValuesAsUnderlyingType(_enumType))
                all |= ToBits(defined);

            return bits != 0 && (bits & ~all) == 0;

            static ulong ToBits(object member)
                => unchecked((ulong)Convert.ToInt64(member, CultureInfo.InvariantCulture));
        }
    }

    private sealed class TextAttributesConverter : ITypeConverter
    {
        public static readonly TextAttributesConverter Instance = new();
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            // A comma-separated flags list — Enum.Parse's [Flags] syntax (NOT pipe; the prior comment
            // claimed pipe, which Enum.Parse does not accept). Case-insensitive to match the other enum
            // converters. Retained for the remaining TextAttributes-typed member (AccessTextPresenter.
            // KeyAttributes) after the inherited aggregate's retirement (proposal §4.1).
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

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return KeyGesture.Parse(text.Trim());
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw Fail($"'{text}' is not a valid key gesture: {ex.Message}", ctx, ex);
            }
        }
    }

    private sealed class SelectorConverter : ITypeConverter
    {
        public static readonly SelectorConverter Instance = new();

        // NOT context-free: a 'prefix|Type' token needs the document's xmlns table to bind the prefix, which
        // only exists at the loader (#23). A Style's Selector is therefore built at activation with the
        // namespace-aware resolver (XamlObjectGraphBuilder.BuildSelector) — this converter remains the fallback
        // for any non-Style Selector-typed member and resolves simple names via the default resolver.
        public bool IsContextFree => false;

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return Selector.Parse(text.Trim());
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw Fail($"'{text}' is not a valid selector: {ex.Message}", ctx, ex);
            }
        }
    }

    private sealed class RelativePointConverter : ITypeConverter
    {
        public static readonly RelativePointConverter Instance = new();

        // A bounds-relative point authored as "x,y" fractions (e.g. StartPoint="0,0" EndPoint="1,1" on a
        // gradient brush). Named points (TopLeft/Center/…) are reachable via {x:Static RelativePoint.Center}.
        public bool IsContextFree => true;

        public object ConvertFromString(string text, in XamlValueContext ctx)
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

        public object ConvertFromString(string text, in XamlValueContext ctx)
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

        public object ConvertFromString(string text, in XamlValueContext ctx)
        {
            try
            {
                return Convert.ChangeType(text.Trim(), _targetType, ctx.Culture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw Fail($"'{text}' is not convertible to {_targetType.Name}: {ex.Message}", ctx, ex);
            }
        }
    }
}

/// <summary>
/// The typed <c>Optional&lt;T&gt;</c> converter (W2c CR4 — the generic-converter shape: the converter's
/// type argument MIRRORS the converted type's). The LOWERED lane bakes the closed
/// <c>new OptionalConverter&lt;double&gt;()</c> statically — a fully typed body, reflection-free under
/// strict AOT; the runtime ladder closes the open generic via <c>MakeGenericType</c> in its RUC lane.
/// Inner conversion routes through the LADDER, live-resolved per call so a later
/// <see cref="XamlConverters.Register"/> for the inner type wins — never <c>TypeDescriptor</c> (the
/// retired reflective <c>OptionalConverter</c>'s ladder-blindness was the W1-sweep
/// <c>Optional&lt;Color&gt;</c> break).
/// </summary>
public sealed class OptionalConverter<T> : ITypeConverter
{
    /// <inheritdoc/>
    public bool IsContextFree
        => (XamlConverters.For(typeof(T)) ?? XamlConverters.BclConverterForType(typeof(T)))?.IsContextFree ?? false;

    /// <inheritdoc/>
    public object ConvertFromString(string text, in XamlValueContext context)
    {
        var inner = XamlConverters.For(typeof(T)) ?? XamlConverters.BclConverterForType(typeof(T))
                    ?? throw XamlConverters.Fail($"No converter for Optional inner type '{typeof(T).Name}'.", context);

        return new Cursorial.UI.Optional<T>((T)inner.ConvertFromString(text, context)!);
    }
}

/// <summary>
/// The CR7 conversion-bridge rung (W2d, design doc <c>xaml-conversion-routes.md</c>): when NO converter
/// exists for a member type <c>T</c>, probe a single-parameter route INTO <c>T</c> declared ON <c>T</c> —
/// implicit operator &gt; explicit operator &gt; constructor &gt; <c>static T Parse(string)</c> (the
/// Avalonia sibling convention) — from a source type <c>S</c> the pure ladder can convert. Between KINDS
/// the precedence is fixed; WITHIN a kind exactly one viable candidate is required — two is a loud
/// ambiguity error, never a silent pick. Registered/attributed/ladder/BCL converters all keep precedence
/// (this rung is LAST). Reflective by design (the RUC loader lane); the emitter mirrors the probe over
/// Roslyn symbols and emits the typed form directly, with a drift test pinning agreement until the W2e
/// route probe makes the decision once for both lanes.
/// </summary>
internal static class ConversionBridge
{
    private static readonly ConcurrentDictionary<Type, ITypeConverter?> Cache = new();

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The bridge probe reflects over the member type's public surface — the RUC " +
                        "reflective lane only; the lowered lane emits the typed route statically.")]
    public static ITypeConverter? For(Type targetType)
        => Cache.GetOrAdd(targetType, static t =>
        {
            if (t.IsAbstract || t.IsInterface || t == typeof(string) || t == typeof(object))
                return null;

            // Style is DENIED (audit): its Selector ctor is a viable route, so a text Style attribute
            // would silently construct an empty, setterless style where the pre-bridge behavior was a
            // loud rejection — a silent styling no-op. The principled per-type opt-out joins the W2e
            // route vocabulary; until then the one framework type with a semantically-wrong route is
            // excluded by name.
            if (t == typeof(Cursorial.UI.Style))
                return null;

            // Kind 1/2: conversion operators DECLARED ON T producing T. op_Implicit beats op_Explicit;
            // within a kind, more than one ladder-convertible source parameter is ambiguous.
            var implicitRoute = FindOperatorRoute(t, "op_Implicit", out var implicitAmbiguous);
            if (implicitAmbiguous)
                return new AmbiguousBridge(t, "implicit operators");
            if (implicitRoute is not null)
                return implicitRoute;

            var explicitRoute = FindOperatorRoute(t, "op_Explicit", out var explicitAmbiguous);
            if (explicitAmbiguous)
                return new AmbiguousBridge(t, "explicit operators");
            if (explicitRoute is not null)
                return explicitRoute;

            // Kind 3: a public single-parameter constructor from a ladder-convertible S.
            System.Reflection.ConstructorInfo? ctorRoute = null;
            ITypeConverter? ctorSource = null;
            foreach (var ctor in t.GetConstructors())
            {
                if (ctor.GetParameters() is not [{ ParameterType: { } s }] || s == t || s == typeof(object))
                    continue;
                if (XamlConverters.For(s) is not { } sourceConverter)
                    continue;
                if (ctorRoute is not null)
                    return new AmbiguousBridge(t, "single-parameter constructors");
                ctorRoute = ctor;
                ctorSource = sourceConverter;
            }
            if (ctorRoute is not null)
            {
                var boundCtor = ctorRoute;
                return new BridgeConverter(ctorRoute.GetParameters()[0].ParameterType, ctorSource!.IsContextFree, value => boundCtor.Invoke([value]));
            }

            // Kind 4: static T Parse(string) (the culture-free single-arg form only).
            if (t.GetMethod("Parse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                            [typeof(string)]) is { } parse && parse.ReturnType == t && !parse.IsGenericMethod)
                return new BridgeConverter(typeof(string), sourceContextFree: true, value => parse.Invoke(null, [value])!);

            return null;
        });

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Operator probing over the member type's public statics — the RUC lane.")]
    private static ITypeConverter? FindOperatorRoute(Type t, string opName, out bool ambiguous)
    {
        ambiguous = false;
        System.Reflection.MethodInfo? route = null;
        ITypeConverter? source = null;

        foreach (var method in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (!string.Equals(method.Name, opName, StringComparison.Ordinal) || method.ReturnType != t || method.IsGenericMethod)
                continue;
            if (method.GetParameters() is not [{ ParameterType: { } s }] || s == t || s == typeof(object))
                continue;
            if (XamlConverters.For(s) is not { } sourceConverter)
                continue;
            if (route is not null)
            {
                ambiguous = true;
                return null;
            }
            route = method;
            source = sourceConverter;
        }

        if (route is null)
            return null;

        var boundRoute = route;
        return new BridgeConverter(route.GetParameters()[0].ParameterType, source!.IsContextFree, value => boundRoute.Invoke(null, [value])!);
    }

    /// <summary>
    /// A resolved bridge: parse the text as S through the ladder, take the route into T. The SOURCE
    /// converter re-resolves per conversion (audit — a later <see cref="XamlConverters.Register"/> for S
    /// must win, the same live-ladder rule the Optional rung follows); route exceptions re-surface as
    /// POSITIONED CUR2401 diagnostics, never a raw TargetInvocationException.
    /// </summary>
    private sealed class BridgeConverter(Type sourceType, bool sourceContextFree, Func<object?, object> route) : ITypeConverter
    {
        public bool IsContextFree => sourceContextFree;

        public object ConvertFromString(string text, in XamlValueContext context)
        {
            var source = XamlConverters.For(sourceType)
                         ?? throw XamlConverters.Fail($"No converter for bridge source type '{sourceType.Name}'.", context);
            try
            {
                return route(source.ConvertFromString(text, context));
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is { } inner)
            {
                throw XamlConverters.Fail($"'{text}' failed the conversion route: {inner.Message}", context, inner);
            }
        }
    }

    /// <summary>The CR3 ambiguity rule: two viable routes of one kind error LOUDLY at conversion, never a silent pick.</summary>
    private sealed class AmbiguousBridge(Type t, string kind) : ITypeConverter
    {
        public bool IsContextFree => false; // never folded — the error must surface with the document position

        public object ConvertFromString(string text, in XamlValueContext context)
            => throw XamlConverters.Fail(
                $"Ambiguous conversion routes into '{t.Name}' ({kind}) — add a converter for the type.", context);
    }

}
