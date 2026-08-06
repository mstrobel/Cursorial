namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-B3 — build-time <c>x:DataType</c> binding-path validation (binding-matrix B184). A
/// DataContext-relative <c>{Binding}</c> whose lexically-scoped <c>x:DataType</c> declares a type gets its
/// dotted path walked against that type's members; an unresolved segment surfaces the <c>CURG2001</c> assist
/// (a Warning — the runtime reflective binding still applies). Bindings with no <c>x:DataType</c> in scope, or
/// with a <c>Source</c>/indexer/method path, are not validated.
/// </summary>
public class BindingPathValidatorTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static System.Collections.Generic.IReadOnlyList<string> Run(string xaml)
        => GeneratorHarness.Run(("View.xaml", xaml)).Diagnostics
            .Where(d => d.Id == "CURG2001")
            .Select(d => d.GetMessage())
            .ToList();

    [Fact] // an x:DataType-scoped binding to a non-existent member → CURG2001
    public void BadBindingPath_OnXDataType_EmitsAssist()
    {
        var diags = Run($"<StackPanel {Ns} x:DataType=\"Button\"><TextBlock Text=\"{{Binding Frobnicate}}\"/></StackPanel>");
        Assert.Single(diags);
        Assert.Contains("Frobnicate", diags[0]);
    }

    [Fact] // an x:DataType-scoped binding to a real member → no assist
    public void GoodBindingPath_OnXDataType_NoAssist()
    {
        var diags = Run($"<StackPanel {Ns} x:DataType=\"Button\"><TextBlock Text=\"{{Binding Content}}\"/></StackPanel>");
        Assert.Empty(diags);
    }

    [Fact] // without an x:DataType in scope there is no declared type to validate against
    public void BindingPath_NoXDataType_NotValidated()
    {
        var diags = Run($"<StackPanel {Ns}><TextBlock Text=\"{{Binding Frobnicate}}\"/></StackPanel>");
        Assert.Empty(diags);
    }

    [Fact] // a binding with an explicit Source is not DataContext-relative — not validated against x:DataType
    public void SourceBinding_NotValidated()
    {
        var diags = Run($"<StackPanel {Ns} x:DataType=\"Button\"><TextBlock Text=\"{{Binding Frobnicate, Source={{x:Null}}}}\"/></StackPanel>");
        Assert.Empty(diags);
    }
}
