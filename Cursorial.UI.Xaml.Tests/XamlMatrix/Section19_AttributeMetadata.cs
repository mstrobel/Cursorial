using System.Globalization;

using Cursorial.UI;
using Cursorial.UI.Xaml;

using MarkupTypeConverter = Cursorial.Markup.TypeConverterAttribute;
using MarkupValueSerializer = Cursorial.Markup.ValueSerializerAttribute;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>A XAML-loadable fixture with a BCL-convertible (<c>Guid</c>) member — proves the loader's type-level
/// BCL <c>TypeConverter</c> fallback (via <c>ConvertText</c>) end-to-end.</summary>
public sealed class BclGuidFixture
{
    public System.Guid Id { get; set; }
}

/// <summary>
/// Attribute-driven converter/serializer metadata: <c>Cursorial.Markup.[TypeConverter]</c> and
/// <c>[ValueSerializer]</c> resolve a member's string converter with the WPF <c>GetSerializerFor</c>
/// precedence — member Cursorial <c>[ValueSerializer]</c> → member Cursorial <c>[TypeConverter]</c> → member BCL
/// <c>[System.ComponentModel.TypeConverter]</c> → member-type Cursorial attrs → the built-in ladder → the
/// member-type's BCL converter (the loader's last fallback). Cursorial's attributes win over the BCL one; the BCL
/// <c>TypeConverter</c> model is honored for interop (the curated ladder still wins for types Cursorial handles).
/// </summary>
public sealed class Section19_AttributeMetadata : LoaderTestBase
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

    // A BCL System.ComponentModel.TypeConverter that DOUBLES (distinguishable from the built-in int converter).
    private sealed class BclDoubler : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? c, System.Type t) => t == typeof(string);
        public override object ConvertFrom(System.ComponentModel.ITypeDescriptorContext? c, CultureInfo? cu, object v) => int.Parse((string) v, CultureInfo.InvariantCulture) * 2;
    }

    private sealed class BclHost
    {
        // member-level BCL [System.ComponentModel.TypeConverter] — honored (interop), below Cursorial's own.
        [System.ComponentModel.TypeConverter(typeof(BclDoubler))]
        public int BclDoubled { get; set; }

        // Cursorial's own [TypeConverter] WINS over a co-present BCL one on the same member.
        [MarkupTypeConverter(typeof(DoublingConverter))]
        [System.ComponentModel.TypeConverter(typeof(BclTripler))]
        public int CursorialWins { get; set; }
    }

    private sealed class BclTripler : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? c, System.Type t) => t == typeof(string);
        public override object ConvertFrom(System.ComponentModel.ITypeDescriptorContext? c, CultureInfo? cu, object v) => int.Parse((string) v, CultureInfo.InvariantCulture) * 3;
    }

    [Fact] // a member-level BCL [System.ComponentModel.TypeConverter] IS honored (compat), via the ConvertFrom(string) leg
    public void MemberBclTypeConverter_Honored()
    {
        var c = XamlConverters.ForMember(typeof(BclHost).GetProperty(nameof(BclHost.BclDoubled)), typeof(int));
        Assert.Equal(10, Convert(c, "5")); // the BCL doubler ran
    }

    [Fact] // Cursorial's own [TypeConverter] takes precedence over a co-present BCL one on the same member
    public void CursorialConverter_BeatsBclOnSameMember()
    {
        var c = XamlConverters.ForMember(typeof(BclHost).GetProperty(nameof(BclHost.CursorialWins)), typeof(int));
        Assert.Equal(10, Convert(c, "5")); // Cursorial doubler (10), not the BCL tripler (15)
    }

    [Fact] // type-level BCL compat: TypeDescriptor-resolved converters (e.g. Guid) convert from string; a plain
           // type with no string converter returns null (falls through to the raw string at the loader).
    public void TypeBclTypeConverter_ViaBclConverterForType()
    {
        var guid = System.Guid.NewGuid();
        var c = XamlConverters.BclConverterForType(typeof(System.Guid));
        Assert.NotNull(c);
        var ctx = new XamlValueContext(CultureInfo.InvariantCulture, null, typeof(System.Guid), null, 0, 0);
        Assert.Equal(guid, c!.ConvertFromString(guid.ToString(), in ctx));

        Assert.Null(XamlConverters.BclConverterForType(typeof(NoConverterType))); // no string converter
    }

    private sealed class NoConverterType { }

    private enum Pieces { Pawn, Knight, Bishop }

    private sealed class PieceHost
    {
        // a member-level BCL converter requiring the (Type) ctor — EnumConverter(typeof(Pieces)).
        [System.ComponentModel.TypeConverter(typeof(System.ComponentModel.EnumConverter))]
        public Pieces Piece { get; set; }
    }

    [Fact] // a member-level BCL converter needing the (Type) ctor (EnumConverter/NullableConverter) is activated
    public void MemberBcl_TypeCtorConverter_Honored()
    {
        var c = XamlConverters.ForMember(typeof(PieceHost).GetProperty(nameof(PieceHost.Piece)), typeof(Pieces));
        Assert.NotNull(c);
        var ctx = new XamlValueContext(CultureInfo.InvariantCulture, null, typeof(Pieces), null, 0, 0);
        Assert.Equal(Pieces.Knight, c!.ConvertFromString("Knight", in ctx)); // EnumConverter(Pieces) ran
    }

    [Fact] // end-to-end: a Guid-typed member converts through the loader's ConvertText BCL fallback (type-level interop)
    public void TypeBclTypeConverter_EndToEndThroughLoader()
    {
        var guid = System.Guid.NewGuid();
        var dict = (ResourceDictionary) LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            $"<BclGuidFixture x:Key=\"f\" Id=\"{guid}\"/></ResourceDictionary>");

        Assert.Equal(guid, Assert.IsType<BclGuidFixture>(dict["f"]).Id); // parsed via TypeDescriptor's GuidConverter
    }
}
