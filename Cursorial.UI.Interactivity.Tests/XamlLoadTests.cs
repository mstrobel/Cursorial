using Cursorial.UI.Controls;
using Cursorial.UI.Interactivity;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Interactivity;

/// <summary>
/// The §7 XAML shape through the RUNTIME loader: <c>&lt;i:Interaction.Triggers&gt;</c> is an
/// attached-property collection member (the S8 <c>Transition.Transitions</c> precedent), so it must load
/// with no new loader path — triggers + actions as ordinary object graphs.
/// </summary>
public sealed class XamlLoadTests
{
    private static readonly Uri Source = new("cursorial://test/interactivity.xaml");
    private static readonly XamlLoader Loader = new();

    private const string Ns =
        "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
        "xmlns:i=\"clr-namespace:Cursorial.UI.Interactivity;assembly=Cursorial.UI.Interactivity\"";

    [Fact] // the design's canonical shape: a Button with an EventTrigger + action, loaded from markup
    public void InteractionTriggers_AttachedCollection_Loads()
    {
        var button = (Button)Loader.Load(
            $"<Button {Ns}>" +
            "<i:Interaction.Triggers>" +
              "<i:EventTrigger EventName=\"Click\">" +
                "<i:ChangePropertyAction PropertyName=\"Payload\" Value=\"fired\"/>" +
              "</i:EventTrigger>" +
            "</i:Interaction.Triggers>" +
            "</Button>", Source);

        var triggers = Interaction.GetTriggers(button);
        var trigger = Assert.IsType<EventTrigger>(Assert.Single(triggers));
        Assert.Equal("Click", trigger.EventName);
        var action = Assert.IsType<ChangePropertyAction>(Assert.Single(trigger.Actions));
        Assert.Equal("Payload", action.PropertyName);
        Assert.Equal("fired", action.Value);
    }

    private sealed class SaveVm
    {
        public readonly RecordingCommand Save = new();
        public RecordingCommand SaveCommand => Save;
    }

    private sealed class RecordingCommand : System.Windows.Input.ICommand
    {
        public readonly List<object?> Executions = [];

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Executions.Add(parameter);

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    [Fact] // THE flagship §7 scenario end-to-end: Command="{Binding SaveCommand}" on a XAML-loaded action
    // anchors on the host's DataContext (the BD13 inheritance hookup) and executes on a real Click raise
    public void InvokeCommand_BindingCommand_ExecutesOnClick()
    {
        var button = (Cursorial.UI.Controls.Button)Loader.Load(
            $"<Button {Ns}>" +
            "<i:Interaction.Triggers>" +
              "<i:EventTrigger EventName=\"Click\">" +
                "<i:InvokeCommandAction Command=\"{Binding SaveCommand}\"/>" +
              "</i:EventTrigger>" +
            "</i:Interaction.Triggers>" +
            "</Button>", Source);

        var vm = new SaveVm();
        button.DataContext = vm;

        using var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(40, 10) });
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));

        Assert.IsType<ClickEventArgs>(Assert.Single(vm.Save.Executions)); // the bound command executed with the args
    }
}
