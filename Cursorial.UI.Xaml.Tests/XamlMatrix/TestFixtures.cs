using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using Cursorial.Drawing.Media;
using Cursorial.Markup;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// A XAML-constructible test brush (a parameterless ctor + a settable string-convertible <c>Color</c>):
/// the resource-dictionary brush value the matrix illustrates with <c>SolidColorBrush</c> (which is
/// immutable / not XAML-activatable). Lives in the test assembly's namespace, resolved via
/// <c>using:</c> (the test base registers the assembly with the default schema).
/// </summary>
public sealed class TestBrush : IBrush
{
    /// <summary>The brush color (string-convertible — <c>#hex</c> / named ANSI).</summary>
    public Color Color { get; set; }

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds) => Color;
}

/// <summary>
/// The shared X2/X3 fixtures: a view-model (the <c>{Binding}</c> oracle), a value converter (the nested
/// <c>{StaticResource}</c> converter, matrix X121), and a custom markup extension (matrix X125/X126).
/// </summary>
internal sealed class TestVm : INotifyPropertyChanged
{
    private string? _name;
    private string? _status;

    public string? Name
    {
        get => _name;
        set { _name = value; Raise(); }
    }

    public string? Status
    {
        get => _status;
        set { _status = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>A one-way converter mapping a status string to a brush (matrix X121's nested converter).</summary>
internal sealed class StatusToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is "ok" ? new SolidColorBrush(Color.FromRgb(0, 255, 0)) : new SolidColorBrush(Color.FromRgb(255, 0, 0));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A custom markup extension with a named member (matrix X125): repeats a string N times.</summary>
public sealed class RepeatExtension : MarkupExtension
{
    public int Count { get; set; } = 1;
    public string Text { get; set; } = "x";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => string.Concat(Enumerable.Repeat(Text, Count));
}

/// <summary>A custom markup extension with positional args (matrix X126): adds two integers.</summary>
public sealed class AddExtension : MarkupExtension
{
    public int A { get; set; }
    public int B { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => (A + B).ToString(CultureInfo.InvariantCulture);
}

/// <summary>A custom markup extension whose <c>ProvideValue</c> yields an <see cref="IValueConverter"/> — the
/// nested-CUSTOM-extension converter case (<c>{Binding …, Converter={StatusConverter}}</c>), the twin of X121's
/// <c>{StaticResource}</c> converter.</summary>
public sealed class StatusConverterExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new StatusToBrush();
}

/// <summary>A custom markup extension whose member is a public instance FIELD (not a property) — the reflection
/// loader must set it via a named argument, matching the generator's object-initializer lowering.</summary>
public sealed class FieldExtension : MarkupExtension
{
    public string Value = "default";

    public override object ProvideValue(IServiceProvider serviceProvider) => Value;
}

/// <summary>The root of an <c>{x:Static A.B.C.D}</c> chain: a static member (<see cref="B"/>) followed by instance
/// member accesses (<c>.C.D</c>) on the running value — beyond WPF's <c>Type.Static</c>.</summary>
public sealed class StaticChain
{
    public static ChainB B { get; } = new();
}

/// <summary>Intermediate instance node in the <see cref="StaticChain"/> fixture.</summary>
public sealed class ChainB
{
    public ChainC C { get; } = new();
}

/// <summary>Leaf instance node in the <see cref="StaticChain"/> fixture.</summary>
public sealed class ChainC
{
    public string D => "deep";
}

/// <summary>A control with a <see cref="Type"/>-typed styled property — the target of a Type-token Setter.Value.</summary>
public sealed class TypeSetterHost : Cursorial.UI.Controls.Control
{
    public static readonly StyledProperty<Type?> KindProperty = UIProperty.Register<TypeSetterHost, Type?>(nameof(Kind));

    public Type? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
}

/// <summary>A control whose CONTENT is a collection-typed <c>UIProperty</c> (a concrete <see cref="List{T}"/> — a bare
/// <c>IList&lt;T&gt;</c> interface type isn't classed as a collection), filled by child elements — the reflection
/// loader must read the collection back and Add, as the generator does via the CLR wrapper.</summary>
[ContentProperty("Items")]
public sealed class ListHost : Cursorial.UI.Controls.Control
{
    public static readonly StyledProperty<List<object>?> ItemsProperty = UIProperty.Register<ListHost, List<object>?>(nameof(Items));

    public ListHost() => SetValue(ItemsProperty, new List<object>());

    public List<object>? Items => GetValue(ItemsProperty);
}

/// <summary>
/// A control with a CLR-only (non-<c>UIProperty</c>-backed) string property — the concrete non-bindable
/// binding target for matrix X120 (a <c>{Binding}</c> on it is <c>CUR2210</c> at parse).
/// </summary>
public sealed class ClrOnlyHost : Cursorial.UI.Controls.Control
{
    /// <summary>A plain CLR property — no registered <c>UIProperty</c> backs it, so it is not bindable.</summary>
    public string? ClrOnly { get; set; }
}
