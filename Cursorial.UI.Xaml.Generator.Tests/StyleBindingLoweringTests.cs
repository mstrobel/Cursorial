using System.ComponentModel;

using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// Binding-valued <c>Setter.Value</c> (ledger B15) through the AOT lane. The generator shares the
/// frontend with the reflection loader, so once the parser admits <c>{Binding}</c> in a setter the
/// lowering has to emit it too — otherwise the same document parses cleanly and then quietly loses the
/// setter to the CURG3001 "not lowered" gap, and a lowered build styles differently from a loaded one.
/// What is emitted is the <b>descriptor</b>, never an install: the styling engine installs it per styled
/// element in either lane.
/// </summary>
public class StyleBindingLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // the setter lowers to a Binding descriptor with no TODO — the AOT lane keeps the setter
    public void Lowered_SetterValue_Binding_EmitsTheDescriptor()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SetterBindingView\">" +
            "<StackPanel.Styles>" +
              "<Style TargetType=\"Button\">" +
                "<Setter Property=\"MinWidth\" Value=\"{Binding Width}\"/>" +
              "</Style>" +
            "</StackPanel.Styles>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SetterBindingView : StackPanel { public SetterBindingView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
                                          .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));

        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("Setters.Add(new global::Cursorial.UI.Setter(", lowered);
        Assert.Contains("Cursorial.UI.Data.Binding", lowered);
    }

    [Fact] // …and the lowered style behaves exactly as the loader's: installed per element, tracking the source
    public void Lowered_SetterValue_Binding_MatchesTheLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SetterBindingRuntimeView\">" +
            "<StackPanel.Styles>" +
              "<Style TargetType=\"Button\">" +
                "<Setter Property=\"MinWidth\" Value=\"{Binding Width}\"/>" +
              "</Style>" +
            "</StackPanel.Styles>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SetterBindingRuntimeView : StackPanel { public SetterBindingRuntimeView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
                                          .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));

        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));

        using var host = UIHeadlessHost.Create();
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.SetterBindingRuntimeView")!)!;
        view.DataContext = new SizeVm { Width = 20 };

        host.ShowRoot(view);
        host.RunUntilIdle();

        var button = Assert.IsType<Button>(view.Children[0]);
        Assert.Equal(20d, button.MinWidth);

        ((SizeVm)view.DataContext!).Width = 35;
        host.RunUntilIdle();
        Assert.Equal(35d, button.MinWidth);
    }

    private sealed class SizeVm : INotifyPropertyChanged
    {
        private double _width;

        public double Width
        {
            get => _width;
            set
            {
                _width = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
