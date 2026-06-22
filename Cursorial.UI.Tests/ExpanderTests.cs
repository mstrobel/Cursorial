using System.Text;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI;

/// <summary>
/// The <see cref="Expander"/> (#113): a collapsible HeaderedContentControl. Covers the IsExpanded-gated content
/// visibility + twisty glyph, the header-click toggle, and the Expanded/Collapsed events.
/// </summary>
public class ExpanderTests
{
    private static (UITestHost Host, Expander Exp) Make(bool expanded = false)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 10) });
        var exp = new Expander
        {
            Header = "Details", Content = "Body text", IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(exp);
        host.RunUntilIdle();
        return (host, exp);
    }

    private static T Part<T>(Expander e, string name) where T : UIElement => (T)e.TemplateInstance!.NameScope.Find(name)!;

    private static string Screen(UITestHost host, int rows = 10)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
            sb.AppendLine(host.GetRowText(r));
        return sb.ToString();
    }

    [Fact]
    public void Collapsed_HidesContent_AndShowsClosedGlyph()
    {
        var (host, exp) = Make(expanded: false);
        using var _ = host;

        Assert.Equal(Visibility.Collapsed, Part<ContentPresenter>(exp, "PART_Content").Visibility);
        Assert.Equal(">", Part<TextBlock>(exp, "PART_Glyph").Text);

        var screen = Screen(host);
        Assert.Contains("Details", screen);       // header always shown
        Assert.DoesNotContain("Body text", screen); // content hidden
    }

    [Fact]
    public void Expanded_ShowsContent_AndOpenGlyph()
    {
        var (host, exp) = Make(expanded: true);
        using var _ = host;

        Assert.Equal(Visibility.Visible, Part<ContentPresenter>(exp, "PART_Content").Visibility);
        Assert.Equal("v", Part<TextBlock>(exp, "PART_Glyph").Text);
        Assert.Contains("Body text", Screen(host));
    }

    [Fact]
    public void HeaderClick_Toggles_AndRaisesEvents()
    {
        var (host, exp) = Make(expanded: false);
        using var _ = host;

        var expanded = 0;
        var collapsed = 0;
        exp.Expanded += (_, _) => expanded++;
        exp.Collapsed += (_, _) => collapsed++;

        var header = Part<Border>(exp, "PART_Header");
        var (col, row) = header.TranslateToWindow(0, 0);

        host.SendClick(col, row);
        host.RunUntilIdle();
        Assert.True(exp.IsExpanded);
        Assert.Equal(1, expanded);

        host.SendClick(col, row);
        host.RunUntilIdle();
        Assert.False(exp.IsExpanded);
        Assert.Equal(1, collapsed);
    }
}
