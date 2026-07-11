using Cursorial.Drawing.Media;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §10 (X113–X128): markup extensions end-to-end — the live attach. StaticResource eager, DynamicResource
/// late, Binding/TemplateBinding live, custom extensions; results route through the deferred-value seams
/// (XD7), never a sentinel through SetValue.
/// </summary>
public sealed class Section10_MarkupExtensionsLive : LoaderTestBase
{
    [Fact] // X113
    public void X113_StaticResource_ResolvesEagerly()
    {
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><TestBrush x:Key=\"A\" Color=\"Red\"/></Border.Resources>" +
            "<Button Background=\"{StaticResource A}\"/></Border>");
        var button = (UIControls.Button)border.Child!;
        Assert.NotNull(button.Background);
        Assert.IsType<TestBrush>(button.Background);
    }

    [Fact] // X114
    public void X114_StaticResourceMissing_CUR2103WithChain()
    {
        var ex = ThrowsLoad(XamlDiagnosticCodes.ResourceNotFound,
            () => Load<UIControls.Button>("<Button Background=\"{StaticResource Missing}\"/>"));
        Assert.Contains("Missing", ex.Message);
        Assert.Contains("Searched scopes", ex.Message);
    }

    [Fact] // X115 — deferred-dictionary deviation from strict WPF load-order (matrix amendment, see XD ledger note)
    public void X115_SameDictionaryReference_ResolvesUnderDeferredEntries()
    {
        // XD9 pins StaticResource as load-order explicit (a forward reference is a miss in WPF). The
        // loader's deferred-dictionary optimization (X141 — all keyed entries authored before any
        // realization) makes a same-dictionary reference ORDER-INDEPENDENT: a sibling key, declared
        // before OR after, is present (deferred) by the time another entry realizes. This is a deliberate,
        // documented deviation driven by the X141 optimization; a genuinely-undefined key is still
        // CUR2103 (X114). The reference here resolves rather than erroring.
        var border = (UIControls.Border)LoadRaw(
            "<Border xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Border.Resources>" +
            "<TestBrush x:Key=\"First\" Color=\"Red\"/>" +
            "<TestBrush x:Key=\"Second\" Color=\"Blue\"/>" +
            "</Border.Resources>" +
            "<Button Background=\"{StaticResource Second}\"/></Border>");
        var button = (UIControls.Button)border.Child!;
        Assert.IsType<TestBrush>(button.Background);
    }

    [Fact] // X116
    public void X116_DynamicResource_OnDirectProperty_InstallsProducer()
    {
        // The producer re-resolves on attach against the runtime chain (the part isn't parented at load),
        // so attach the tree and pump a frame, then assert it resolved at LocalValue (XD7 — no sentinel).
        using var host = UIHeadlessHost.Create();
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><TestBrush x:Key=\"A\" Color=\"Red\"/></Border.Resources>" +
            "<Button Background=\"{DynamicResource A}\"/></Border>");
        host.ShowRoot(border);
        host.RunFrame();

        var button = (UIControls.Button)border.Child!;
        Assert.Equal(BindingPriority.LocalValue, button.GetValueSource(UIControls.Control.BackgroundProperty).Priority);
        Assert.IsType<TestBrush>(button.Background);
        Assert.IsNotType<ResourceReference>(button.Background);
    }

    [Fact] // X117
    public void X117_DynamicResource_OnSetterValue_StoresCarrier()
    {
        var dict = (ResourceDictionary)LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Style x:Key=\"S\" TargetType=\"Button\">" +
            "<Setter Property=\"Background\" Value=\"{DynamicResource A}\"/></Style></ResourceDictionary>");
        var style = (Style)dict["S"]!;
        var setter = style.Setters[0];
        Assert.IsType<ResourceReference>(setter.Value);
        Assert.Equal("A", ((ResourceReference)setter.Value!).Key);
    }

    [Fact] // X116a — DynamicResource on a styled property with a nested {x:Static} key (XD7a)
    public void X116a_DynamicResource_NestedStaticKey_ResolvesAndInstalls()
    {
        // {x:Static ThemeKeys.SurfaceBrush} resolves to the const "Theme.SurfaceBrush" at instantiate;
        // the DynamicResource then keys off that — the real demo scenario.
        using var host = UIHeadlessHost.Create();
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><TestBrush x:Key=\"Theme.SurfaceBrush\" Color=\"Red\"/></Border.Resources>" +
            "<Button Background=\"{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}\"/></Border>");
        host.ShowRoot(border);
        host.RunFrame();

        var button = (UIControls.Button)border.Child!;
        Assert.Equal(BindingPriority.LocalValue, button.GetValueSource(UIControls.Control.BackgroundProperty).Priority);
        Assert.IsType<TestBrush>(button.Background);
        Assert.IsNotType<ResourceReference>(button.Background);
    }

    [Fact] // X113a — StaticResource with a nested {x:Static} key resolves eagerly
    public void X113a_StaticResource_NestedStaticKey_ResolvesEagerly()
    {
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><TestBrush x:Key=\"Theme.AccentBrush\" Color=\"Blue\"/></Border.Resources>" +
            "<Button Background=\"{StaticResource {x:Static ThemeKeys.AccentBrush}}\"/></Border>");
        var button = (UIControls.Button)border.Child!;
        Assert.IsType<TestBrush>(button.Background);
    }

    [Fact] // X117a — Setter.Value DynamicResource with a nested {x:Static} key stores the resolved key
    public void X117a_DynamicResource_NestedStaticKey_OnSetterValue_StoresResolvedKey()
    {
        var dict = (ResourceDictionary)LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Style x:Key=\"S\" TargetType=\"Button\">" +
            "<Setter Property=\"Background\" Value=\"{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}\"/></Style></ResourceDictionary>");
        var setter = ((Style)dict["S"]!).Setters[0];
        Assert.IsType<ResourceReference>(setter.Value);
        Assert.Equal("Theme.SurfaceBrush", ((ResourceReference)setter.Value!).Key); // the resolved const, not "ThemeKeys.SurfaceBrush"
    }

    [Fact] // X114a — nested-key resolution failures are position-carrying, never a silent empty key
    public void X114a_NestedKey_UnresolvableStatic_And_NullKey_AreDiagnosed()
    {
        // {x:Static} to an unresolvable member → MemberNotFound with line+col.
        var ex1 = Assert.Throws<XamlParseException>(() =>
            Load<UIControls.Button>("<Button Background=\"{StaticResource {x:Static Bogus.Member}}\"/>"));
        Assert.Equal(XamlDiagnosticCodes.MemberNotFound, ex1.Code);
        Assert.True(ex1.Line > 0 && ex1.Column > 0);

        // {x:Null} key → the null-key guard (ResourceNotFound), NOT a bare ArgumentNullException downstream.
        var ex2 = Assert.Throws<XamlParseException>(() =>
            Load<UIControls.Button>("<Button Background=\"{StaticResource {x:Null}}\"/>"));
        Assert.Equal(XamlDiagnosticCodes.ResourceNotFound, ex2.Code);
        Assert.True(ex2.Line > 0 && ex2.Column > 0);
    }

    [Fact] // X118
    public void X118_Binding_LiveAttach_PushesValue()
    {
        var vm = new TestVm();
        var button = Load<UIControls.Button>("<Button Content=\"{Binding Name}\"/>");
        button.DataContext = vm;
        vm.Name = "x";
        Assert.Equal("x", button.Content);
    }

    [Fact] // X119
    public void X119_Binding_NamedArgs_ModeAndPath()
    {
        var vm = new TestVm { Name = "start" };
        var button = Load<UIControls.Button>("<Button Content=\"{Binding Path=Name, Mode=TwoWay}\"/>");
        button.DataContext = vm;
        Assert.Equal("start", button.Content);
        // Two-way: a target write flows back to the source.
        button.Content = "edited";
        Assert.Equal("edited", vm.Name);
    }

    [Fact] // X120
    public void X120_BindingToNonBindableMember_CUR2210AtParse()
    {
        // {Binding} on a CLR-only member (no registered UIProperty backing) is CUR2210 at parse. A custom
        // test type exposes a CLR-only string property to make the non-bindable target concrete.
        ThrowsLoad(XamlDiagnosticCodes.BindingTargetNotBindable,
            () => Load<ClrOnlyHost>("<ClrOnlyHost ClrOnly=\"{Binding Name}\"/>"));
    }

    [Fact] // X121
    public void X121_Binding_NestedConverterStaticResource()
    {
        var vm = new TestVm { Status = "ok" };
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><StatusToBrush x:Key=\"StatusToBrush\"/></Border.Resources>" +
            "<Button Foreground=\"{Binding Status, Converter={StaticResource StatusToBrush}}\"/></Border>");
        var button = (UIControls.Button)border.Child!;
        button.DataContext = vm;
        Assert.IsType<SolidColorBrush>(button.Foreground);
        Assert.Equal(Output.Color.FromRgb(0, 255, 0), ((SolidColorBrush)button.Foreground!).Color);
    }

    [Fact] // X122
    public void X122_XStatic_AssignedDirectly()
    {
        // {x:Static Colors.Red} folds at parse (X26) and is assigned as the value (no extension object).
        var button = Load<UIControls.Button>("<Button Background=\"{x:Static Brushes.Red}\"/>");
        Assert.NotNull(button.Background);
    }

    [Fact] // X123
    public void X123_XNull_SetsNull()
    {
        var button = Load<UIControls.Button>("<Button Background=\"{x:Null}\"/>");
        Assert.Null(button.Background);
    }

    [Fact] // X124
    public void X124_XType_OnTypeMember()
    {
        // {x:Type Button} folds to typeof(Button) and assigns to a Type-typed member (DataTemplate.DataType).
        var template = Load<UIControls.DataTemplate>("<DataTemplate DataType=\"{x:Type Button}\"/>");
        Assert.Equal(typeof(UIControls.Button), template.DataType);
    }

    [Fact] // X125
    public void X125_CustomExtension_ProvideValue()
    {
        // The custom extension activates, its named members are set, ProvideValue(services) runs and the
        // result is assigned (the test namespace is in the default map via the loader test base).
        var button = Load<UIControls.Button>("<Button Content=\"{Repeat Count=3, Text=ab}\"/>");
        Assert.Equal("ababab", button.Content);
    }

    [Fact] // X126
    public void X126_CustomExtension_PositionalArgs()
    {
        var button = Load<UIControls.Button>("<Button Content=\"{Add 1, 2}\"/>");
        Assert.Equal("3", button.Content);
    }

    [Fact] // X127 (also asserted end-to-end in §13 X160)
    public void X127_TemplateBinding_InsideTemplate_TracksParent()
    {
        // The parse-time restriction passes inside a template body (X56); at build the value tracks the
        // templated parent's Background (one-way).
        var template = Load<UIControls.ControlTemplate>(
            "<ControlTemplate><Border Background=\"{TemplateBinding Background}\"/></ControlTemplate>");
        var owner = new UIControls.Button { Background = new SolidColorBrush(Output.Color.FromRgb(1, 2, 3)) };
        template.TargetType = typeof(UIControls.Button);
        var instance = template.Instantiate(owner);
        var part = (UIControls.Border)instance.Root;
        Assert.IsType<SolidColorBrush>(part.Background);
        Assert.Equal(owner.Background, part.Background);
    }

    [Fact] // X128
    public void X128_DeferredValueSeam_NoSentinelThroughSetValue()
    {
        // DynamicResource installs a producer at LocalValue; the stored value is never a ResourceReference
        // sentinel (the §4.11-rejected pattern, XD7).
        using var host = UIHeadlessHost.Create();
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><TestBrush x:Key=\"A\" Color=\"Red\"/></Border.Resources>" +
            "<Button Background=\"{DynamicResource A}\"/></Border>");
        host.ShowRoot(border);
        host.RunFrame();

        var button = (UIControls.Button)border.Child!;
        Assert.IsNotType<ResourceReference>(button.Background);
        Assert.IsType<TestBrush>(button.Background);
    }

    [Fact] // {Icon …} resolves to IconExtension (not the Icon CONTROL) and yields a populated Icon as content
    public void IconExtension_AsContent_YieldsIcon()
    {
        var button = Load<UIControls.Button>("<Button Content=\"{Icon Glyph=G, Text=T}\"/>");
        var icon = Assert.IsType<UIControls.Icon>(button.Content);
        Assert.Equal("G", icon.Glyph);
        Assert.Equal("T", icon.Text);
    }

    [Fact] // the <Icon …/> element form also resolves (the same control, addressed as an element)
    public void Icon_AsElement_Resolves()
    {
        var icon = Load<UIControls.Icon>("<Icon Glyph=\"G\" Text=\"T\"/>");
        Assert.Equal("G", icon.Glyph);
        Assert.Equal("T", icon.Text);
    }
}
