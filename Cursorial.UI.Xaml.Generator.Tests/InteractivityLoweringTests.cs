using Cursorial.UI.Controls;
using Cursorial.UI.Interactivity;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The GENERATOR half of the Interactivity §7 XAML shape: <c>&lt;i:Interaction.Triggers&gt;</c> (an
/// attached-property collection filled via the owner's static <c>Get{Name}</c> get-or-create accessor —
/// the loader's twin) lowers to real code, with the trigger/action graph built generically and a
/// <c>Command="{Binding …}"</c> installed on the action.
/// </summary>
public class InteractivityLoweringTests
{
    private const string Ns =
        "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
        "xmlns:i=\"clr-namespace:Cursorial.UI.Interactivity;assembly=Cursorial.UI.Interactivity\"";

    private static CSharpCompilation WithInteractivity(CSharpCompilation compilation)
        => compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Interaction).Assembly.Location));

    [Fact] // the canonical shape lowers end-to-end: trigger + action land in Interaction.GetTriggers(button)
    public void Lowered_InteractionTriggers_BuildsTriggerGraph()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IxView1\">" +
            "<Button x:Name=\"Go\">" +
              "<i:Interaction.Triggers>" +
                "<i:EventTrigger EventName=\"Click\">" +
                  "<i:ChangePropertyAction PropertyName=\"Payload\" Value=\"fired\"/>" +
                "</i:EventTrigger>" +
              "</i:Interaction.Triggers>" +
            "</Button>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class IxView1 : Cursorial.UI.Controls.StackPanel { public IxView1() => InitializeComponent(); } }";

        var compilation = WithInteractivity(GeneratorHarness.ReferencedCompilation("IxHost"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.IxView1")!)!;
        var button = (Button)root.Children[0];

        var trigger = Assert.IsType<EventTrigger>(Assert.Single(Interaction.GetTriggers(button)));
        Assert.Equal("Click", trigger.EventName);
        var action = Assert.IsType<ChangePropertyAction>(Assert.Single(trigger.Actions));
        Assert.Equal("Payload", action.PropertyName);
        Assert.Equal("fired", action.Value);
    }

    public sealed class SaveVm
    {
        public RecordingCommand SaveCommand { get; } = new();
    }

    public sealed class RecordingCommand : System.Windows.Input.ICommand
    {
        public readonly List<object?> Executions = [];

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Executions.Add(parameter);

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    [Fact] // flagship parity with the loader lane: a GENERATED Command="{Binding SaveCommand}" action anchors
    // on the host's DataContext (the BD13 inheritance hookup) and executes on a real Click raise
    public void Lowered_InvokeCommand_BindingCommand_ExecutesOnClick()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IxView2\">" +
            "<Button x:Name=\"Go\">" +
              "<i:Interaction.Triggers>" +
                "<i:EventTrigger EventName=\"Click\">" +
                  "<i:InvokeCommandAction Command=\"{Binding SaveCommand}\"/>" +
                "</i:EventTrigger>" +
              "</i:Interaction.Triggers>" +
            "</Button>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class IxView2 : Cursorial.UI.Controls.StackPanel { public IxView2() => InitializeComponent(); } }";

        var compilation = WithInteractivity(GeneratorHarness.ReferencedCompilation("IxHost"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.IxView2")!)!;
        var button = (Button)root.Children[0];

        var vm = new SaveVm();
        root.DataContext = vm; // inherited: root → button → (BD13) trigger → action

        using var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(40, 10) });
        host.ShowRoot(root);
        host.RunUntilIdle();

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));

        Assert.IsType<ClickEventArgs>(Assert.Single(vm.SaveCommand.Executions));
    }

    [Fact] // audit REGRESSION: the attached-access gate keyed on xm.Property (set for EVERY registered member) —
    // an INSTANCE-backed registered collection property emitted Owner.GetX(var) (CS0117). Now gated on IsAttachable.
    public void Lowered_InstanceBackedRegisteredCollection_FillsViaInstance()
    {
        var host = @"
using System.Collections.ObjectModel;
namespace GenApp
{
    public sealed class HuntItem { }
    public sealed class HuntItems : ObservableCollection<HuntItem> { }
    public class HuntHost : Cursorial.UI.Controls.Panel
    {
        public static readonly Cursorial.UI.StyledProperty<HuntItems?> ItemsXProperty =
            Cursorial.UI.UIProperty.Register<HuntHost, HuntItems?>(""ItemsX"");
        public HuntItems? ItemsX { get => GetValue(ItemsXProperty); set => SetValue(ItemsXProperty, value); }
        public HuntHost() => ItemsX = new HuntItems();
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"using:GenApp\" x:Class=\"GenApp.HuntView\">" +
            "<g:HuntHost><g:HuntHost.ItemsX><g:HuntItem/><g:HuntItem/></g:HuntHost.ItemsX></g:HuntHost>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class HuntView : Cursorial.UI.Controls.StackPanel { public HuntView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("IxHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("GetItemsX", lowered); // NOT the attached accessor — the instance property fills

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.HuntView")!)!;
        dynamic huntHost = root.Children[0];
        Assert.Equal(2, (int)huntHost.ItemsX.Count); // both items filled the ctor-created collection
    }

    [Fact] // audit: DataTrigger.Binding (a Binding-typed CLR member) was TODO-dropped — the generated app threw
    // at attach while the loader assigned the descriptor. Now the descriptor lowers (loader parity).
    public void Lowered_DataTriggerBinding_AssignsDescriptor()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IxView3\">" +
            "<Button x:Name=\"Go\">" +
              "<i:Interaction.Triggers>" +
                "<i:DataTrigger Binding=\"{Binding State}\" Value=\"go\">" +
                  "<i:ChangePropertyAction PropertyName=\"Payload\" Value=\"fired\"/>" +
                "</i:DataTrigger>" +
              "</i:Interaction.Triggers>" +
            "</Button>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class IxView3 : Cursorial.UI.Controls.StackPanel { public IxView3() => InitializeComponent(); } }";

        var compilation = WithInteractivity(GeneratorHarness.ReferencedCompilation("IxHost"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.IxView3")!)!;
        var button = (Button)root.Children[0];
        var trigger = Assert.IsType<DataTrigger>(Assert.Single(Interaction.GetTriggers(button)));
        Assert.NotNull(trigger.Binding); // the descriptor was assigned (the loader's AttachBinding branch twin)

        // …and the trigger ARMS cleanly (the dropped descriptor previously threw "DataTrigger requires a Binding")
        using var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(40, 10) });
        host.ShowRoot(root);
        host.RunUntilIdle();
    }
}
