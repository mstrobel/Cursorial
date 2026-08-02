using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml; // ITypeConverter / XamlValueContext (frontend)

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §16 — <see cref="PropertyPath"/> (the WPF <c>PropertyPath</c> analog): the lazy string form,
/// the xmlns-preprocessed form, the compile-time-checked <c>params UIProperty[]</c> form, the <c>[TypeConverter]</c>
/// converter, and the <c>Binding.Path</c> integration. Also pins the "a type qualification is NOT necessarily an
/// attached property — it may be a regular property qualified for disambiguation/clarity" contract.
/// </summary>
public class Section16_PropertyPath
{
    public Section16_PropertyPath()
    {
        BindingMatrixFixture.Ensure();
        AccessorCache.ResetForTests();
    }

    [Fact] // A bare string is lazily resolved (unprefixed owners resolve at bind time) and round-trips its text.
    public void P001_StringForm_IsLazy_AndRoundTrips()
    {
        var path = new PropertyPath("Sub.Name");

        Assert.False(path.IsPreResolved);
        Assert.Equal("Sub.Name", path.Path);
        Assert.Equal("Sub.Name", path.ToString());
        Assert.Equal(2, path.ToBindingPath(null).SegmentCount);
    }

    [Fact] // The implicit string conversion is the code-first ergonomic path; null is Empty.
    public void P002_ImplicitStringConversion()
    {
        PropertyPath path = "Name";
        Assert.Equal("Name", path.Path);

        PropertyPath empty = (string?) null;
        Assert.Same(PropertyPath.Empty, empty);
        Assert.Equal(string.Empty, PropertyPath.Empty.Path);
    }

    [Fact] // The compile-time-checked form: a chain of resolved UIProperty steps, baked in (pre-resolved, no parse).
    public void P003_PropertiesForm_CompileTimeChecked()
    {
        var path = new PropertyPath(BindWidget.TextProperty);

        Assert.True(path.IsPreResolved);
        // A single UIProperty renders as its type-qualified owner form.
        Assert.Equal("(BindWidget.Text)", path.Path);

        var built = path.ToBindingPath(null);
        Assert.Equal(1, built.SegmentCount);
        Assert.Equal("(BindWidget.Text)", built.ToString());
    }

    [Fact] // A multi-property chain: (Owner.Member).(Owner.Member). The ctor requires a first property.
    public void P004_PropertiesForm_Chain()
    {
        var chain = new PropertyPath(BindWidget.NumProperty, BindWidget.FlagProperty);
        Assert.Equal("(BindWidget.Num).(BindWidget.Flag)", chain.Path);
        Assert.Equal(2, chain.ToBindingPath(null).SegmentCount);

        // A null property step is rejected (compile-time-checked intent + explicit runtime guard).
        Assert.Throws<ArgumentException>(() => new PropertyPath(BindWidget.NumProperty, [null!]));
    }

    [Fact] // The degenerate forms: the empty path is PropertyPath.Empty (the properties ctor requires ≥1 property, so
    // `new PropertyPath()` / `new PropertyPath(null)` are compile errors that steer here). A typed null string is Empty-equivalent.
    public void P004a_EmptyForm()
    {
        Assert.True(PropertyPath.Empty.ToBindingPath(null).IsEmpty);
        Assert.False(PropertyPath.Empty.IsPreResolved);

        var fromNullString = new PropertyPath((string?) null);
        Assert.Equal(string.Empty, fromNullString.Path);
        Assert.True(fromNullString.ToBindingPath(null).IsEmpty);
    }

    [Fact] // THE POINT (user's concern): a type-qualified segment resolves a REGULAR (non-attached) property too —
    // (BindWidget.Num) reads the styled Num property, not just an attached property. The parse never assumes attached.
    public void P005_TypeQualified_ResolvesRegularProperty_NotOnlyAttached()
    {
        // BindWidget.Num is a plain StyledProperty (NOT an AttachedProperty) — the disambiguation/clarity case.
        Assert.IsNotType<AttachedProperty<int>>(BindWidget.NumProperty);

        var segment = new PropertyPath(BindWidget.NumProperty).ToBindingPath(null).Segments[0];

        var widget = new BindWidget { Num = 42 };
        var accessor = AccessorCache.ResolveProperty(widget, in segment);

        Assert.IsType<UIPropertyAccessor>(accessor);
        Assert.Equal(42, accessor.GetValue(widget));
        accessor.SetValue(widget, 7);
        Assert.Equal(7, widget.Num);
    }

    [Fact] // The same via the parsed string form: (BindWidget.Num) is accepted and resolves the regular property.
    public void P006_TypeQualifiedString_RegularProperty_Resolves()
    {
        var segment = BindingPath.Parse("(BindWidget.Num)").Segments[0];
        Assert.Equal(PathSegmentKind.TypeQualified, segment.Kind);
        Assert.Equal(typeof(BindWidget), segment.QualifierType);

        var widget = new BindWidget { Num = 5 };
        var accessor = AccessorCache.ResolveProperty(widget, in segment);
        Assert.Equal(5, accessor.GetValue(widget));
    }

    [Fact] // Binding.Path is now a PropertyPath; the string ctor + { Path = "…" } both go through the implicit conversion.
    public void P007_BindingPath_RoundTrips()
    {
        var a = new Binding("Sub.Name");
        Assert.Equal("Sub.Name", a.Path.Path);

        var b = new Binding { Path = "Customer.City" };
        Assert.Equal("Customer.City", b.Path.Path);

        var c = new Binding { Path = new PropertyPath(BindWidget.TextProperty) };
        Assert.True(c.Path.IsPreResolved);
        Assert.Equal("(BindWidget.Text)", c.Path.Path);
    }

    [Fact] // The [TypeConverter] converter produces the lazy form with NO services (code-first / no loader context).
    public void P008_Converter_NoServices_ProducesLazyPath()
    {
        var converter = new PropertyPathConverter();
        Assert.False(converter.IsContextFree);

        var ctx = new XamlValueContext(System.Globalization.CultureInfo.InvariantCulture, null, typeof(PropertyPath), null, 1, 1);
        var value = Assert.IsType<PropertyPath>(converter.ConvertFromString("Sub.Name", in ctx));
        Assert.Equal("Sub.Name", value.Path);
        Assert.False(value.IsPreResolved);
    }

    [Fact] // WITH a resolver from services (the loader's seam) the converter PREPROCESSES: type qualifications resolve
    // then (IsPreResolved), so a prefixed/qualified owner binds at load rather than lazily. This is "pull xmlns
    // resolver from services" — the user's contextual-converter design, now real via XamlValueContext.Services.
    public void P009_Converter_WithServices_Preprocesses()
    {
        var converter = new PropertyPathConverter();
        var services = new StubServiceProvider(new StubResolver(("W", typeof(BindWidget))));
        var ctx = new XamlValueContext(
            System.Globalization.CultureInfo.InvariantCulture, null, typeof(PropertyPath), null, 1, 1, services);

        var value = Assert.IsType<PropertyPath>(converter.ConvertFromString("(W.Num)", in ctx));
        Assert.True(value.IsPreResolved);                          // resolved at conversion, not lazily
        Assert.Equal("(W.Num)", value.Path);                       // Path keeps the AUTHORED text (round-trip fidelity)

        // The RESOLVED owner is baked into the parsed path — the prefix 'W' bound to BindWidget via services.
        var built = value.ToBindingPath(null);
        Assert.Equal("(BindWidget.Num)", built.ToString());
        Assert.Equal(typeof(BindWidget), built.Segments[0].QualifierType);

        var widget = new BindWidget { Num = 9 };
        var accessor = AccessorCache.ResolveProperty(widget, in built.Segments[0]);
        Assert.Equal(9, accessor.GetValue(widget));
    }

    [Fact] // ResolvedProperty is a proper value type: equality compares the WRAPPED member (not the boxed
    // struct), the hash is null-safe, and a default instance reports unresolved — which keeps the
    // FromProperties guard live (an ArgumentException, not an NRE downstream).
    public void P010_ResolvedProperty_Equality_DefaultIsUnresolved()
    {
        ResolvedProperty a = BindWidget.TextProperty;
        ResolvedProperty b = BindWidget.TextProperty;

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.IsResolved);
        Assert.False(a == ResolvedProperty.Unresolved);

        Assert.False(default(ResolvedProperty).IsResolved);
        Assert.True(default(ResolvedProperty) == ResolvedProperty.Unresolved);
        Assert.Equal(0, default(ResolvedProperty).GetHashCode());

        // The guard fires again for a default (unresolved) step instead of NRE-ing on segment use.
        Assert.Throws<ArgumentException>(static () => BindingPath.FromProperties(new ResolvedProperty[1]));
    }

    private sealed class StubServiceProvider(IPathTypeResolver resolver) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IPathTypeResolver) ? resolver : null;
    }

    private sealed class StubResolver((string Prefix, Type Type) binding) : IPathTypeResolver
    {
        public Type? Resolve(string typeToken)
        {
            var colon = typeToken.IndexOf(':');
            var prefix = colon < 0 ? typeToken : typeToken[..colon];
            return prefix == binding.Prefix ? binding.Type : DefaultPathTypeResolver.Instance.Resolve(typeToken);
        }
    }
}
