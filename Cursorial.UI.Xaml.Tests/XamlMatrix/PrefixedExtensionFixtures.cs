using System.Globalization;

using Cursorial.UI.Data;
using Cursorial.UI.Xaml;

// A DELIBERATELY separate CLR namespace from the matrix fixtures' Cursorial.Tests.UI.Xaml.XamlMatrix (which
// LoaderTestBase maps into the DEFAULT UI xmlns). Types here are reachable ONLY through an explicit
// clr-namespace: xmlns prefix — never the default namespace — which is what lets these fixtures reproduce the
// Gallery's {Binding Converter={i:EnumItemConverter}} bug: a nested custom extension under a BUILT-IN outer
// extension used to be resolved in the default UI xmlns (the unstamped fallback), so a prefixed project
// extension was CUR2002. If these lived in the default-mapped namespace the fallback would mask the bug.
namespace Cursorial.Tests.UI.Xaml.XamlMatrix.Prefixed;

/// <summary>A one-way converter reachable only via a clr-namespace prefix — the nested converter the
/// prefixed-extension regression drives (the twin of the matrix's default-namespace <c>StatusToBrush</c>).</summary>
public sealed class PrefixConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A custom markup extension (prefix-only namespace) whose <c>ProvideValue</c> yields a
/// <see cref="PrefixConverter"/> — nested under <c>{Binding Converter=…}</c> it exercises the namespace stamp
/// that a built-in outer extension must now propagate to its nested arguments.</summary>
public sealed class PrefixConverterExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new PrefixConverter();
}
