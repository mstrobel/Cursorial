using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5 — type-qualified binding paths (<c>(Type.Member)</c>, BindingPath's qualified step). Parentheses in a
/// binding path are NOT method calls (the grammar has none): a qualified hop resolves its member on the
/// qualifier type and lowers as an <c>as</c>-cast access, mirroring the runtime's qualified-CLR lane. These
/// pin the compiled emission, the live behavior, and the accurate reflective bails (UIObject owners,
/// unrelated qualifiers).
/// </summary>
public class TypeQualifiedBindingLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string Lower(string xaml, CSharpCompilation compilation) => GeneratorHarness.LowerView(compilation, xaml);

    private const string Vms = @"
using System.ComponentModel;
using Cursorial.UI.Controls;
namespace TestApp
{
    public interface IHasTitle { string Title { get; } }

    public sealed class QualVm : INotifyPropertyChanged, IHasTitle
    {
        private string _caption = ""first"";
        public string Caption
        {
            get => _caption;
            set { _caption = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption))); }
        }
        public string Title => ""the-title"";
        public InnerVm Inner { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class InnerVm : INotifyPropertyChanged
    {
        private string _name = ""inner-name"";
        public string Name
        {
            get => _name;
            set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class UnrelatedVm { public string Caption => ""unrelated""; }
}";

    private static (string Lowered, CSharpCompilation Compilation) LowerWithVms(string bindingPath, string cls)
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.{cls}\" x:DataType=\"t:QualVm\">" +
            $"<TextBlock x:Name=\"Label\" Text=\"{{Binding {bindingPath}}}\"/>" +
            "</StackPanel>";
        var view = $"namespace TestApp {{ public partial class {cls} : Cursorial.UI.Controls.StackPanel {{ public {cls}() => InitializeComponent(); }} }}";

        var compilation = GeneratorHarness.ReferencedCompilation("QualifiedHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Vms), CSharpSyntaxTree.ParseText(view));
        return (Lower(xaml, compilation), compilation);
    }

    [Fact] // an identity-qualified hop compiles and binds live — the historic "method call" misclassification
    public void Lowered_TypeQualifiedHop_CompilesAndBindsLive()
    {
        var (lowered, compilation) = LowerWithVms("(t:QualVm.Caption)", "QualView1");

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::TestApp.QualVm, string>", lowered);
        Assert.Contains("as global::TestApp.QualVm)?.Caption", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.QualView1")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        var vm = Activator.CreateInstance(assembly.GetType("TestApp.QualVm")!)!;
        view.DataContext = vm;
        Assert.Equal("first", label.Text);

        vm.GetType().GetProperty("Caption")!.SetValue(vm, "second");
        Assert.Equal("second", label.Text); // INPC rewiring works by member name for qualified hops
    }

    [Fact] // an interface-qualified (upcast) hop compiles — the WPF disambiguation/clarity form
    public void Lowered_InterfaceQualifiedHop_CompilesAndBindsLive()
    {
        var (lowered, compilation) = LowerWithVms("(t:IHasTitle.Title)", "QualView2");

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::TestApp.QualVm, string>", lowered);
        Assert.Contains("as global::TestApp.IHasTitle)?.Title", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.QualView2")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        view.DataContext = Activator.CreateInstance(assembly.GetType("TestApp.QualVm")!)!;
        Assert.Equal("the-title", label.Text);
    }

    [Fact] // a qualified hop mid-chain composes with plain hops
    public void Lowered_QualifiedHop_MidChain_CompilesAndBindsLive()
    {
        var (lowered, compilation) = LowerWithVms("Inner.(t:InnerVm.Name)", "QualView3");

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::TestApp.QualVm, string>", lowered);
        Assert.Contains("as global::TestApp.InnerVm)?.Name", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.QualView3")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        var vm = Activator.CreateInstance(assembly.GetType("TestApp.QualVm")!)!;
        view.DataContext = vm;
        Assert.Equal("inner-name", label.Text);

        var inner = vm.GetType().GetProperty("Inner")!.GetValue(vm)!;
        inner.GetType().GetProperty("Name")!.SetValue(inner, "renamed");
        Assert.Equal("renamed", label.Text); // the inner INPC hop rewires by member name
    }

    [Fact] // an unrelated qualifier can never match by cast — stays on the reflective lane (by-name fallback)
    public void Lowered_QualifiedHop_UnrelatedType_StaysReflective()
    {
        var (lowered, _) = LowerWithVms("(t:UnrelatedVm.Caption)", "QualView4");

        Assert.DoesNotContain("CompiledBinding", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.Binding", lowered); // faithful reflective fallback
    }

    [Fact] // a UIObject-owned qualifier resolves to a UIProperty at runtime — accurately not compiled
    public void Lowered_QualifiedHop_UIObjectOwner_StaysReflective()
    {
        var (lowered, _) = LowerWithVms("(TextBlock.Text)", "QualView5");

        Assert.DoesNotContain("CompiledBinding", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.Binding", lowered);
    }
}
