using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// <see cref="UserControl"/>: the composed-view base. The class itself is nearly empty — what these
/// tests pin is the part that is easy to break silently: the S7 control-theme lookup is exact-key,
/// so a DERIVED view only renders because <see cref="UserControl"/> pins its
/// <c>ControlThemeKey</c>; and the inherited <c>[ContentProperty]</c> makes the view's markup its
/// <see cref="ContentControl.Content"/>.
/// </summary>
public class UserControlTests
{
    private sealed class MyView : UserControl
    {
        public MyView() => Content = new TextBlock { Text = "composed" };
    }

    [Fact]
    public void UserControl_PresentsContent_ThroughNeutralTheme()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        host.ShowRoot(new UserControl
        {
            Content = new TextBlock { Text = "hello" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        });
        host.RunUntilIdle();

        Assert.Contains("hello", host.GetRowText(0));
    }

    [Fact]
    public void DerivedView_InheritsTheUserControlTheme()
    {
        // Exact-key lookup + no theme registered for MyView: only the ControlThemeKey pin makes
        // this render — remove the override and the presenter (and this text) disappears.
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        host.ShowRoot(new MyView { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        host.RunUntilIdle();

        Assert.Contains("composed", host.GetRowText(0));
    }

    [Fact]
    public void UserControl_LoadsFromXaml_WithMarkupAsContent()
    {
        // The inherited [ContentProperty("Content")] routes the view's markup into Content.
        var view = (UserControl)Cursorial.UI.Xaml.XamlLoader.Shared.Load(
            """
            <UserControl xmlns="https://cursorial.dev/ui">
                <TextBlock Text="from markup"/>
            </UserControl>
            """);

        var text = Assert.IsType<TextBlock>(view.Content);
        Assert.Equal("from markup", text.Text);
    }
}
