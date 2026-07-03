using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C26 — ContentControl Horizontal/VerticalContentAlignment (WPF parity). The control's content
// alignment positions its content within the control; ContentPresenter.ArrangeOverride reads the templated parent's
// Horizontal/VerticalContentAlignment and places the realized child accordingly. Default Stretch ⇒ Arrange(0,0,finalSize),
// byte-identical to the prior fill behavior; a live change re-arranges via the presenter's observer on the parent.
public sealed class Section37_ContentAlignment
{
    private static int ContentColumn(HorizontalAlignment align)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 3) });
        host.ShowRoot(new Button
        {
            Content = "Hi",
            Width = 24,
            HorizontalContentAlignment = align,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        });
        host.RunUntilIdle();
        return host.GetRowText(0).IndexOf("Hi", StringComparison.Ordinal);
    }

    [Fact] // C26.1: HorizontalContentAlignment positions the content — Left < Center < Right
    public void C26_1_HorizontalContentAlignment()
    {
        var left = ContentColumn(HorizontalAlignment.Left);
        var center = ContentColumn(HorizontalAlignment.Center);
        var right = ContentColumn(HorizontalAlignment.Right);
        Assert.True(left >= 0 && center > left && right > center, $"left={left} center={center} right={right}");
    }

    private static int ContentRow(VerticalAlignment align)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(14, 8) });
        host.ShowRoot(new Button
        {
            Content = "Hi",
            Width = 10,
            Height = 6,
            VerticalContentAlignment = align,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        });
        host.RunUntilIdle();
        for (var r = 0; r < 6; r++)
            if (host.GetRowText(r).Contains("Hi", StringComparison.Ordinal))
                return r;
        return -1;
    }

    [Fact] // C26.2: VerticalContentAlignment positions the content — Top < Center < Bottom
    public void C26_2_VerticalContentAlignment()
    {
        var top = ContentRow(VerticalAlignment.Top);
        var center = ContentRow(VerticalAlignment.Center);
        var bottom = ContentRow(VerticalAlignment.Bottom);
        Assert.True(top >= 0 && center > top && bottom > center, $"top={top} center={center} bottom={bottom}");
    }

    [Fact] // C26.3: defaults are Stretch (WPF parity; preserves the prior fill behavior)
    public void C26_3_DefaultsAreStretch()
    {
        var btn = new Button();
        Assert.Equal(HorizontalAlignment.Stretch, btn.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Stretch, btn.VerticalContentAlignment);
    }

    [Fact] // C26.4: a live HorizontalContentAlignment change re-positions the content (the presenter's observer tracks it)
    public void C26_4_LiveChangeRepositions()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 3) });
        var btn = new Button
        {
            Content = "Hi",
            Width = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(btn);
        host.RunUntilIdle();
        var before = host.GetRowText(0).IndexOf("Hi", StringComparison.Ordinal);

        btn.HorizontalContentAlignment = HorizontalAlignment.Right;
        host.RunUntilIdle();
        var after = host.GetRowText(0).IndexOf("Hi", StringComparison.Ordinal);
        Assert.True(after > before, $"before={before} after={after}");
    }
}
