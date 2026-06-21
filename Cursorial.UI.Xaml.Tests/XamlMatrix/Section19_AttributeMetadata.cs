using System.Globalization;

using Cursorial.UI.Xaml;

using MarkupTypeConverter = Cursorial.Markup.TypeConverterAttribute;
using MarkupValueSerializer = Cursorial.Markup.ValueSerializerAttribute;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Attribute-driven converter/serializer metadata: <c>Cursorial.Markup.[TypeConverter]</c> and
/// <c>[ValueSerializer]</c> resolve a member's string converter with the WPF <c>GetSerializerFor</c>
/// precedence — member <c>[ValueSerializer]</c> → member <c>[TypeConverter]</c> → type <c>[ValueSerializer]</c>
/// → type <c>[TypeConverter]</c> → the built-in ladder. The BCL <c>System.ComponentModel.TypeConverterAttribute</c>
/// is NOT honored (only Cursorial's, and only when the named type implements our interface).
/// </summary>
public sealed class Section19_AttributeMetadata
{
    // A converter that DOUBLES (distinguishable from the built-in int converter), and a serializer that TRIPLES.
    private sealed class DoublingConverter : ITypeConverter
    {
        public bool IsContextFree => true;
        public object ConvertFromString(string text, in XamlValueContext ctx) => int.Parse(text, ctx.Culture) * 2;
    }

    private sealed class TriplingSerializer : IValueSerializer
    {
        public bool IsContextFree => true;
        public object ConvertFromString(string text, in XamlValueContext ctx) => int.Parse(text, ctx.Culture) * 3;
        public string ConvertToString(object? value, in XamlValueContext ctx) => value?.ToString() ?? string.Empty;
    }

    [MarkupTypeConverter(typeof(DoublingConverter))]
    private sealed class TypeWithConverter { }

    private sealed class Host
    {
        [MarkupTypeConverter(typeof(DoublingConverter))]
        public int MemberConverted { get; set; }

        [MarkupValueSerializer(typeof(TriplingSerializer))]
        [MarkupTypeConverter(typeof(DoublingConverter))]
        public int SerializerWins { get; set; }

        public int Plain { get; set; }
    }

    private static int Convert(ITypeConverter? c, string text)
    {
        var ctx = new XamlValueContext(CultureInfo.InvariantCulture, null, typeof(int), null, 0, 0);
        return (int) c!.ConvertFromString(text, in ctx)!;
    }

    [Fact] // a member-level [TypeConverter] is honored (and beats the built-in int ladder)
    public void MemberTypeConverter_Honored()
    {
        var c = XamlConverters.ForMember(typeof(Host).GetProperty(nameof(Host.MemberConverted)), typeof(int));
        Assert.Equal(10, Convert(c, "5")); // doubled, not the built-in 5
    }

    [Fact] // a member-level [ValueSerializer] WINS over a co-present member-level [TypeConverter] (WPF precedence)
    public void MemberValueSerializer_BeatsMemberTypeConverter()
    {
        var c = XamlConverters.ForMember(typeof(Host).GetProperty(nameof(Host.SerializerWins)), typeof(int));
        Assert.Equal(15, Convert(c, "5")); // tripled (serializer), not 10 (converter)
    }

    [Fact] // a plain member with no attribute falls through to the built-in ladder
    public void PlainMember_UsesLadder()
    {
        var c = XamlConverters.ForMember(typeof(Host).GetProperty(nameof(Host.Plain)), typeof(int));
        Assert.Equal(5, Convert(c, "5")); // the built-in int converter
    }

    [Fact] // a type-level [TypeConverter] is honored via ForMember (the metadata-build path), NOT via the pure
           // reflection-free For ladder — type-level conversion applies where a type is used as a member value.
    public void TypeLevelConverter_HonoredByForMember_NotByPureFor()
    {
        // For stays the pure ladder (no attribute reflection — so the generated/lowered providers bake it AOT-clean):
        // a type with no ladder entry resolves to null, NOT its type-level [TypeConverter].
        Assert.Null(XamlConverters.For(typeof(TypeWithConverter)));
        // ForMember (member null, value type carrying the attribute) honors the type-level converter.
        Assert.Equal(10, Convert(XamlConverters.ForMember(member: null, typeof(TypeWithConverter)), "5"));
    }

    [Fact] // the BCL System.ComponentModel.[TypeConverter] is NOT honored (only Cursorial.Markup's)
    public void BclTypeConverter_NotHonored()
    {
        // BclConverted carries a System.ComponentModel.TypeConverter — it must be ignored, falling to the ladder.
        var c = XamlConverters.ForMember(typeof(BclHost).GetProperty(nameof(BclHost.BclConverted)), typeof(int));
        Assert.Equal(5, Convert(c, "5"));
    }

    private sealed class BclHost
    {
        [System.ComponentModel.TypeConverter(typeof(System.ComponentModel.Int32Converter))]
        public int BclConverted { get; set; }
    }
}
