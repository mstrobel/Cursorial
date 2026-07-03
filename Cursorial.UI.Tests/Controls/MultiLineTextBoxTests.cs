using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Controls;

// Phase 2 spec for the multi-line TextPresenter: hard-newline + wrapped rows render, MinLines/MaxLines drive
// the field height, and the caret row drives vertical scroll. (Editing — Up/Down/Enter/etc. — is Phase 3.)
public sealed class MultiLineTextBoxTests
{
    private static (UITestHost Host, TextBox Box) Shown(Action<TextBox> configure, int width = 16, int? height = 4)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(30, 10),
            Capabilities = TestCapabilities.KittyTruecolor,
        });
        var box = new TextBox
        {
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (height is { } h)
            box.Height = h;
        configure(box);
        host.ShowRoot(box);
        host.RunUntilIdle();
        return (host, box);
    }

    [Fact] // hard newlines render on consecutive rows
    public void HardNewlines_RenderOnConsecutiveRows()
    {
        var (host, _) = Shown(b => { b.AcceptsReturn = true; b.Text = "AAA\nBBB\nCCC"; });

        Assert.Contains("AAA", host.GetRowText(0));
        Assert.Contains("BBB", host.GetRowText(1));
        Assert.Contains("CCC", host.GetRowText(2));
    }

    [Fact] // word wrap breaks a long line across rows, losslessly
    public void WordWrap_BreaksLongLineAcrossRows()
    {
        var (host, _) = Shown(b => { b.TextWrapping = WrapMode.WordWrap; b.Text = "alpha beta gamma"; }, width: 8);

        // Width 8 forces wrapping; "alpha " then "beta " then "gamma" land on separate rows.
        Assert.Contains("alpha", host.GetRowText(0));
        Assert.DoesNotContain("gamma", host.GetRowText(0)); // it did wrap
        var joined = host.GetRowText(0) + host.GetRowText(1) + host.GetRowText(2);
        Assert.Contains("gamma", joined);
    }

    [Fact] // MinLines reserves height even for a single line of text
    public void MinLines_ReservesHeight()
    {
        var (_, box) = Shown(b => { b.AcceptsReturn = true; b.MinLines = 3; b.Text = "only one line"; }, height: null);

        Assert.True(box.Bounds.Rows >= 3, $"expected ≥3 rows, got {box.Bounds.Rows}");
    }

    [Fact] // MaxLines caps the height; extra lines scroll
    public void MaxLines_CapsHeight()
    {
        var (_, box) = Shown(b => { b.AcceptsReturn = true; b.MaxLines = 2; b.Text = "1\n2\n3\n4"; }, height: null);

        Assert.Equal(2, box.Bounds.Rows);
    }

    [Fact] // moving the caret to a line below the viewport scrolls it into view
    public void CaretBelowViewport_ScrollsDown()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.MaxLines = 2; b.Text = "1\n2\n3\n4"; }, height: 2);
        box.Focus();
        host.RunUntilIdle();
        Assert.Equal(0, box.Presenter!.ScrollRow); // caret at offset 0 → top

        box.CaretIndex = box.Text.Length; // jump to the last line
        host.RunUntilIdle();

        Assert.True(box.Presenter!.ScrollRow > 0, "the field did not scroll the caret line into view");
        Assert.Contains("4", host.GetRowText(0) + host.GetRowText(1)); // the last line is now visible
    }
}
