using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Tests.Input;

public class MouseClickSynthesizerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MouseEvent Mouse(MouseEventKind kind, int col, int row, int atMs, MouseButton button = MouseButton.Left)
        => new()
           {
               Timestamp = T0.AddMilliseconds(atMs),
               Kind = kind,
               Position = new CellPosition(col, row),
               Button = kind is MouseEventKind.Move or MouseEventKind.Drag ? MouseButton.None : button,
               ButtonsHeld = MouseButtons.None,
               Modifiers = KeyModifiers.None,
           };

    private static MouseEvent Down(int col, int row, int atMs, MouseButton button = MouseButton.Left)
        => Mouse(MouseEventKind.ButtonDown, col, row, atMs, button);

    private static MouseEvent Up(int col, int row, int atMs, MouseButton button = MouseButton.Left)
        => Mouse(MouseEventKind.ButtonUp, col, row, atMs, button);

    private static MouseEvent Drag(int col, int row, int atMs) => Mouse(MouseEventKind.Drag, col, row, atMs);

    private static async IAsyncEnumerable<InputEvent> ToAsync(InputEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var e in events) yield return e;
    }

    private static async Task<List<InputEvent>> Run(MouseClickOptions options, params InputEvent[] events)
    {
        var synth = new MouseClickSynthesizer(options);
        var result = new List<InputEvent>();
        await foreach (var e in synth.TransformAsync(ToAsync(events)))
            result.Add(e);
        return result;
    }

    private static List<MouseEvent> Mice(IEnumerable<InputEvent> events) => events.OfType<MouseEvent>().ToList();

    // ---- Multi-click counting ----

    [Fact]
    public async Task SingleClick_CountIsOne()
    {
        var outp = Mice(await Run(new MouseClickOptions(), Down(2, 3, 0), Up(2, 3, 10)));

        Assert.Equal(MouseEventKind.ButtonDown, outp[0].Kind);
        Assert.Equal(1, outp[0].ClickCount);
    }

    [Fact]
    public async Task DoubleClick_WithinThreshold_SecondDownCountsTwo()
    {
        var outp = Mice(await Run(new MouseClickOptions(),
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(2, 3, 100), Up(2, 3, 110)));

        Assert.Equal(1, outp[0].ClickCount);   // first down
        Assert.Equal(2, outp[2].ClickCount);   // second down — double-click
    }

    [Fact]
    public async Task TripleClick_WithinThreshold_ThirdDownCountsThree()
    {
        var outp = Mice(await Run(new MouseClickOptions(),
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(2, 3, 100), Up(2, 3, 110),
                                  Down(2, 3, 200), Up(2, 3, 210)));

        Assert.Equal(3, outp[4].ClickCount);
    }

    [Fact]
    public async Task SecondClick_AfterThreshold_ResetsToOne()
    {
        var outp = Mice(await Run(new MouseClickOptions(),   // default 500ms
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(2, 3, 600), Up(2, 3, 610)));

        Assert.Equal(1, outp[2].ClickCount);
    }

    [Fact]
    public async Task SecondClick_DifferentCell_ResetsToOne()
    {
        var outp = Mice(await Run(new MouseClickOptions(),
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(8, 8, 100), Up(8, 8, 110)));

        Assert.Equal(1, outp[2].ClickCount);
    }

    [Fact]
    public async Task DifferentButtons_TrackedIndependently()
    {
        var outp = Mice(await Run(new MouseClickOptions(),
                                  Down(2, 3, 0, MouseButton.Left), Up(2, 3, 10, MouseButton.Left),
                                  Down(2, 3, 100, MouseButton.Right), Up(2, 3, 110, MouseButton.Right)));

        // The right press is the first right-click — not a continuation of the left.
        Assert.Equal(1, outp[2].ClickCount);
    }

    // ---- ClickCount target ----

    [Fact]
    public async Task Target_ButtonDown_SurfacesOnDownNotUp()
    {
        var outp = Mice(await Run(new MouseClickOptions { ClickCount = ClickCountTarget.ButtonDown },
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(2, 3, 100), Up(2, 3, 110)));

        Assert.Equal(2, outp[2].ClickCount);   // second down carries it
        Assert.Equal(1, outp[3].ClickCount);   // its up stays at the default
    }

    [Fact]
    public async Task Target_ButtonUp_SurfacesOnUpNotDown()
    {
        var outp = Mice(await Run(new MouseClickOptions { ClickCount = ClickCountTarget.ButtonUp },
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(2, 3, 100), Up(2, 3, 110)));

        Assert.Equal(1, outp[2].ClickCount);   // second down stays at default
        Assert.Equal(2, outp[3].ClickCount);   // its up carries the count
    }

    [Fact]
    public async Task Target_None_LeavesEveryEventAtOne()
    {
        var outp = Mice(await Run(new MouseClickOptions { ClickCount = ClickCountTarget.None },
                                  Down(2, 3, 0), Up(2, 3, 10),
                                  Down(2, 3, 100), Up(2, 3, 110)));

        Assert.All(outp, e => Assert.Equal(1, e.ClickCount));
    }

    // ---- Click synthesis ----

    [Fact]
    public async Task ClickSynthesis_EmitsClickAfterRelease_OnSameCell()
    {
        var outp = Mice(await Run(new MouseClickOptions { SynthesizeClickEvents = true },
                                  Down(2, 3, 0), Up(2, 3, 10)));

        Assert.Equal(3, outp.Count);
        Assert.Equal(MouseEventKind.Click, outp[2].Kind);
        Assert.True(outp[2].Synthesized);
        Assert.Equal(new CellPosition(2, 3), outp[2].Position);
    }

    [Fact]
    public async Task ClickSynthesis_ReleaseOnDifferentCell_NoClick()
    {
        var outp = Mice(await Run(new MouseClickOptions { SynthesizeClickEvents = true },
                                  Down(2, 3, 0), Up(5, 5, 10)));

        Assert.DoesNotContain(outp, e => e.Kind == MouseEventKind.Click);
    }

    [Fact]
    public async Task ClickSynthesis_DragOffCancelsClick()
    {
        var outp = Mice(await Run(new MouseClickOptions { SynthesizeClickEvents = true },
                                  Down(2, 3, 0), Drag(5, 5, 5), Up(2, 3, 10)));

        // Even though the release returns to the press cell, the intervening drag cancels the click.
        Assert.DoesNotContain(outp, e => e.Kind == MouseEventKind.Click);
    }

    [Fact]
    public async Task Target_Click_SurfacesCountOnClickEvent()
    {
        var outp = Mice(await Run(
                            new MouseClickOptions { SynthesizeClickEvents = true, ClickCount = ClickCountTarget.Click },
                            Down(2, 3, 0), Up(2, 3, 10),
                            Down(2, 3, 100), Up(2, 3, 110)));

        var clicks = outp.Where(e => e.Kind == MouseEventKind.Click).ToList();
        Assert.Equal(2, clicks.Count);
        Assert.Equal(1, clicks[0].ClickCount);
        Assert.Equal(2, clicks[1].ClickCount);   // second click is a double-click
        // Down/Up stay at the default since the target is the Click event.
        Assert.All(outp.Where(e => e.Kind is MouseEventKind.ButtonDown or MouseEventKind.ButtonUp),
                   e => Assert.Equal(1, e.ClickCount));
    }

    // ---- Pass-through + validation + capabilities ----

    [Fact]
    public async Task NonMouseEvents_PassThroughUnchanged()
    {
        var key = new KeyEvent
                  {
                      Timestamp = T0, Key = Key.Character, Kind = KeyEventKind.Down,
                      Modifiers = KeyModifiers.None, Text = "a".AsMemory(),
                  };

        var outp = await Run(new MouseClickOptions { SynthesizeClickEvents = true }, key);

        Assert.Single(outp);
        Assert.Same(key, outp[0]);
    }

    [Fact]
    public void ClickTargetWithoutSynthesis_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new MouseClickSynthesizer(new MouseClickOptions { ClickCount = ClickCountTarget.Click }));
    }

    [Fact]
    public void NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MouseClickSynthesizer(new MouseClickOptions { MultiClickThreshold = TimeSpan.Zero }));
    }

    [Fact]
    public void TransformCapabilities_ReflectsOptions()
    {
        var synth = new MouseClickSynthesizer(
            new MouseClickOptions { SynthesizeClickEvents = true, ClickCount = ClickCountTarget.ButtonUp });

        var caps = synth.TransformCapabilities(InputCapabilities.None);

        Assert.True(caps.Mouse.SynthesizesClickCounts);
        Assert.True(caps.Mouse.SynthesizesClicks);
    }

    [Fact]
    public void TransformCapabilities_NoneTarget_DoesNotClaimClickCounts()
    {
        var synth = new MouseClickSynthesizer(new MouseClickOptions { ClickCount = ClickCountTarget.None });

        var caps = synth.TransformCapabilities(InputCapabilities.None);

        Assert.False(caps.Mouse.SynthesizesClickCounts);
        Assert.False(caps.Mouse.SynthesizesClicks);
    }
}
