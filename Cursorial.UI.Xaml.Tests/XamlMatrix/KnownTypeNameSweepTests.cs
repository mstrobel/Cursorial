using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The known-type-name sweep offers only AUTHORABLE names: compiler-synthesized unspeakables
/// (<c>&lt;G&gt;$…</c>, <c>&lt;M&gt;$…</c>, display classes) are exported types in any
/// generator-bearing assembly, but their names are illegal in XML — they were flooding the
/// designer's DataType/x:TypeArguments completion lists ahead of every real control.
/// </summary>
public sealed class KnownTypeNameSweepTests
{
    [Fact]
    public void Sweep_offers_only_authorable_names()
    {
        var names = XamlSchemaContext.Default.GetKnownTypeNames("https://cursorial.dev/ui");

        Assert.NotEmpty(names);
        Assert.Contains("Border", names);
        Assert.DoesNotContain(names, n => n.Contains('<') || n.Contains('$') || n.Contains('>'));
    }
}
