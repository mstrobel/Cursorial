using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
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
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is "ok" ? new SolidColorBrush(Color.FromRgb(0, 255, 0)) : new SolidColorBrush(Color.FromRgb(255, 0, 0));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A custom markup extension with a named member (matrix X125): repeats a string N times.</summary>
public sealed class RepeatExtension : MarkupExtension
{
    public int Count { get; set; } = 1;
    public string Text { get; set; } = "x";

    public override object? ProvideValue(IServiceProvider serviceProvider)
        => string.Concat(System.Linq.Enumerable.Repeat(Text, Count));
}

/// <summary>A custom markup extension with positional args (matrix X126): adds two integers.</summary>
public sealed class AddExtension : MarkupExtension
{
    public int A { get; set; }
    public int B { get; set; }

    public override object? ProvideValue(IServiceProvider serviceProvider) => (A + B).ToString(CultureInfo.InvariantCulture);
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
