using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>Serialized against the suite: these tests flip the process-wide
/// <see cref="XamlDesignerContext.IsDesignMode"/> ambient, and activators are built once per CLR
/// type — each mode is exercised through its OWN fixture type so cached activators can't leak
/// across assertions.</summary>
[CollectionDefinition("DesignerActivation", DisableParallelization = true)]
public sealed class DesignerActivationCollection;

/// <summary>A view-model shape with NO public parameterless constructor — the curio commandlet
/// pattern (constructors demand runtime services a designer cannot supply).</summary>
public sealed class CtorlessVmRuntime
{
    public CtorlessVmRuntime(object service) => _ = service;
    public string? Title { get; set; }
}

/// <summary>Same shape, dedicated to the design-mode assertion (fresh activator cache entry).</summary>
public sealed class CtorlessVmDesign
{
    public CtorlessVmDesign(object service) => _ = service;
    public string? Title { get; set; }
}

[Collection("DesignerActivation")]
public sealed class DesignerActivationTests
{
    private const string Ns =
        "xmlns=\"https://cursorial.dev/ui\" " +
        "xmlns:t=\"clr-namespace:Cursorial.Tests.UI.Xaml.Integration;assembly=Cursorial.UI.Xaml.Tests\"";

    static DesignerActivationTests() =>
        XamlSchemaContext.Default.RegisterAssembly(typeof(DesignerActivationTests).Assembly);

    [Fact] // runtime contract unchanged: no parameterless ctor => not element-activatable
    public void RuntimeLoading_CtorlessType_IsNotActivatable()
    {
        var original = XamlDesignerContext.IsDesignMode;
        XamlDesignerContext.IsDesignMode = false;
        try
        {
            var loader = new XamlLoader(new XamlLoaderOptions { DiagnosticMode = XamlDiagnosticMode.CollectAll });
            var ex = Record.Exception(() => loader.Load($"<t:CtorlessVmRuntime {Ns} Title=\"x\"/>", null));
            Assert.NotNull(ex); // not activatable — surfaced as a load failure, never an uninitialized instance
        }
        finally
        {
            XamlDesignerContext.IsDesignMode = original;
        }
    }

    [Fact] // design mode materializes WITHOUT running the constructor; properties paint the state
    public void DesignMode_CtorlessType_MaterializesUninitialized()
    {
        var original = XamlDesignerContext.IsDesignMode;
        XamlDesignerContext.IsDesignMode = true;
        try
        {
            var loader = new XamlLoader(new XamlLoaderOptions { DiagnosticMode = XamlDiagnosticMode.CollectAll });
            var instance = loader.Load($"<t:CtorlessVmDesign {Ns} Title=\"designed\"/>", null);

            var vm = Assert.IsType<CtorlessVmDesign>(instance);
            Assert.Equal("designed", vm.Title);
        }
        finally
        {
            XamlDesignerContext.IsDesignMode = original;
        }
    }
}
