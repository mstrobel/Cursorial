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

    // A <DataTemplate DataType="X"> establishes the SAME scope the compiled-binding lowering already uses (the
    // template DataContext is an X). Without this the validator only saw x:DataType directives, so a binding inside
    // a template whose content root omitted a redundant x:DataType was validated against the OUTER scope — a false
    // CURG2001 (the Shell.xaml ThemesViewModel/ThemeEntry regression).

    [Fact] // {Binding Content} inside <DataTemplate DataType="Button"> validates against Button, NOT the outer StackPanel
    public void DataTemplateDataType_EstablishesScope_NoFalseAssist()
    {
        var diags = Run(
            $"<ContentControl {Ns} x:DataType=\"StackPanel\">" +      // outer scope: StackPanel (no Content member)
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate DataType=\"Button\">" +                 // inner scope from DataType: Button (has Content)
                  "<TextBlock Text=\"{Binding Content}\"/>" +          // no redundant x:DataType on the root
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>");
        Assert.Empty(diags);
    }

    [Fact] // a bad inner path is reported against the DataTemplate's DataType (proving the scope is Button, not StackPanel)
    public void DataTemplateDataType_BadInnerPath_ReportsAgainstDataType()
    {
        var diags = Run(
            $"<ContentControl {Ns} x:DataType=\"StackPanel\">" +
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate DataType=\"Button\">" +
                  "<TextBlock Text=\"{Binding Frobnicate}\"/>" +
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>");
        Assert.Single(diags);
        Assert.Contains("Frobnicate", diags[0]);
        Assert.Contains("Button", diags[0]);   // scoped to the DataType, not the outer StackPanel
    }

    [Fact] // an explicit x:DataType on the content root still wins over the DataTemplate's DataType (ForObject ?? DataType)
    public void DataTemplateDataType_ExplicitRootXDataType_Wins()
    {
        var diags = Run(
            $"<ContentControl {Ns} x:DataType=\"StackPanel\">" +
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate DataType=\"Button\">" +
                  "<TextBlock x:DataType=\"CheckBox\" Text=\"{Binding Frobnicate}\"/>" +
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>");
        Assert.Single(diags);
        Assert.Contains("CheckBox", diags[0]);  // the root's own x:DataType won over the template's Button
    }
}
