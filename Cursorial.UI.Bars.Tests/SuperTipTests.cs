using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// SuperTip (the bars guide's rich hover help): a titled, multi-line tip (command name + accelerator + description),
// shown through the SAME ToolTipService as a plain tip (the tip may be any object). A BarCommand with a Description
// auto-provisions one on every bound bar control — identical wherever the command appears.
public sealed class SuperTipTests
{
    private const int H = 8;

    private static UITestHost NewHost(int w = 48, int h = H) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static string AllRows(UITestHost host) =>
        string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));

    [Fact] // a SuperTip renders its title, accelerator, and description (shown directly here; a ToolTip hosts it live)
    public void SuperTip_RendersTitleGestureAndDescription()
    {
        using var host = NewHost();
        var tip = new SuperTip { Title = "Save", InputGestureText = "Ctrl+S", Description = "Save the current document." };
        host.ShowRoot(tip);
        host.RunUntilIdle();

        var all = AllRows(host);
        Assert.Contains("Save", all);
        Assert.Contains("Ctrl+S", all);
        Assert.Contains("Save the current document.", all);
    }

    [Fact] // FromCommand maps the command's metadata (Title strips the access-key underscore)
    public void SuperTip_FromCommand_MapsFields()
    {
        var command = new BarCommand(() => { })
        {
            Text = "_Save", InputGestureText = "Ctrl+S", Description = "Save the current document.",
        };

        var tip = SuperTip.FromCommand(command);

        Assert.Equal("Save", tip.Title); // underscore stripped
        Assert.Equal("Ctrl+S", tip.InputGestureText);
        Assert.Equal("Save the current document.", tip.Description);
    }

    [Fact] // a BarButton bound to a command WITH a Description auto-provisions a SuperTip as its tooltip
    public void BarButton_DescribedCommand_AutoProvisionsSuperTip()
    {
        var command = new BarCommand(() => { }) { Text = "_Save", InputGestureText = "Ctrl+S", Description = "Save it." };
        var button = new BarButton { Command = command };

        var tip = ToolTipService.GetTip(button);
        var superTip = Assert.IsType<SuperTip>(tip);
        Assert.Equal("Save", superTip.Title);
        Assert.Equal("Save it.", superTip.Description);
    }

    [Fact] // a command WITHOUT a Description provisions NO SuperTip (a plain one-line label needs no rich tip)
    public void BarButton_UndescribedCommand_NoSuperTip()
    {
        var command = new BarCommand(() => { }) { Text = "Save", InputGestureText = "Ctrl+S" }; // no Description
        var button = new BarButton { Command = command };

        Assert.Null(ToolTipService.GetTip(button));
    }

    [Fact] // an explicit author tooltip is NOT overwritten by the command's auto-SuperTip
    public void BarButton_ExplicitTip_NotOverwrittenByCommand()
    {
        var button = new BarButton();
        ToolTipService.SetTip(button, "my explicit tip");
        button.Command = new BarCommand(() => { }) { Text = "Save", Description = "Save it." };

        Assert.Equal("my explicit tip", ToolTipService.GetTip(button));
    }

    [Fact] // swapping to a command with no Description clears the auto-SuperTip we provisioned
    public void BarButton_AutoSuperTip_ClearedOnCommandSwap()
    {
        var button = new BarButton { Command = new BarCommand(() => { }) { Text = "Save", Description = "Save it." } };
        Assert.IsType<SuperTip>(ToolTipService.GetTip(button));

        button.Command = new BarCommand(() => { }) { Text = "Plain" }; // no Description
        Assert.Null(ToolTipService.GetTip(button));
    }
}
