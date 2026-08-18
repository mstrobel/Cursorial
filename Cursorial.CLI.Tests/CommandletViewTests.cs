using Cursorial.CLI.Commandlets;
using Cursorial.CLI.Views;
using Cursorial.CLI.Wire;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.CLI;

/// <summary>
/// The three M0 commandlet views, driven through the real input path in a headless host: initial focus
/// lands on the interactive element (OnAttachedToTree), the root KeyBindings fire from it, and the VM
/// completes with the right exit code and wire result.
/// </summary>
public class CommandletViewTests
{
    private static UIHeadlessHost Host() => UIHeadlessHost.Create(new UIHeadlessHostOptions
    {
        InitialSize = new Size(60, 12),
        Capabilities = HeadlessCapabilities.KittyTruecolor,
    });

    [Fact]
    public void Choose_ArrowsAndEnter_AcceptWithLabelAndIndex()
    {
        using var host = Host();
        var vm = new ChooseViewModel(host.Application, "Pick:", ["alpha", "beta", "gamma"]);
        host.ShowRoot(new ChooseView { DataContext = vm });
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal("beta", vm.Selected); // focus landed on the list without a click

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(ExitCodes.Accepted, vm.CompletedCode);
        var result = vm.BuildResult("pick");
        Assert.Equal(VariableKind.Selection, result.Kind);
        Assert.Equal("beta", Assert.Single(result.Values));
        Assert.Equal(1, Assert.Single(result.Indices));
    }

    [Fact]
    public void Input_TypeAndEnter_AcceptsText()
    {
        using var host = Host();
        var vm = new InputViewModel(host.Application, "name>", "");
        host.ShowRoot(new InputView { DataContext = vm });
        host.RunUntilIdle();

        host.SendText("mike");
        host.RunUntilIdle();
        Assert.Equal("mike", vm.Text); // per-keystroke two-way push through the focused editor

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(ExitCodes.Accepted, vm.CompletedCode);
        Assert.Equal("mike", Assert.Single(vm.BuildResult("name").Values));
    }

    [Fact]
    public void Confirm_YAccepts_NDeclines()
    {
        using var host = Host();
        var vm = new ConfirmViewModel(host.Application, "Delete branch?");
        host.ShowRoot(new ConfirmView { DataContext = vm });
        host.RunUntilIdle();

        host.SendKey(Key.Character, text: "y");
        host.RunUntilIdle();
        Assert.Equal(ExitCodes.Accepted, vm.CompletedCode);

        using var host2 = Host();
        var vm2 = new ConfirmViewModel(host2.Application, "Delete branch?");
        host2.ShowRoot(new ConfirmView { DataContext = vm2 });
        host2.RunUntilIdle();

        host2.SendKey(Key.Character, text: "n");
        host2.RunUntilIdle();
        Assert.Equal(ExitCodes.Canceled, vm2.CompletedCode);
    }
}
