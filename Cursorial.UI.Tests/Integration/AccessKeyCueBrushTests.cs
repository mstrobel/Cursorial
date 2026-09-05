// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine, so the test body IS the UI thread and RunUntilIdle must block on it.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// The access-key cue's <see cref="AccessTextPresenter.IndicatorBrush"/> is a declaration ON THE
/// MNEMONIC RUN (UNIFIED-B2: the indicator is pipeline data), so its brushes sample the run's own
/// strip — the scope-inference rule: a brush samples the geometry of its declaration site.
/// </summary>
/// <remarks>
/// <para>
/// History: the hand-rolled second <c>DrawText</c> sampled the indicator brush against the whole
/// label's box, so a gradient indicator's colour tracked the key's position within the text (and,
/// before the fix this file originally pinned, wrongly tracked the element's position in its
/// PARENT). Under the shared pipeline the cue rides the mnemonic run's carrier, and a run-declared
/// brush samples the run's own strip — a single cluster — so a gradient indicator now resolves to
/// its ramp head wherever the key sits. Element-position independence still holds (the strip is
/// element-local either way). If the run rung's sampling strip is ever widened (task #15's run-leg
/// geometry), the position-tracking behaviour becomes expressible again and these pins move with
/// that decision.
/// </para>
/// </remarks>
public sealed class AccessKeyCueBrushTests
{
    private const int WindowColumns = 32;
    private const int WindowRows = 4;

    /// <summary>
    /// The invariant that survives the pipeline reroute: the cue's colour never depends on where
    /// the element sits in its parent. Same label, same key, three placements — one colour.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(10)]
    public void CueColour_IsIndependentOfTheElementsPositionInItsParent(int slotColumn)
    {
        var colour = RenderCueColour(slotColumn, keyIndex: 1);

        // Independent of the slot...
        Assert.Equal(RenderCueColour(0, keyIndex: 1), colour);

        // ...and not vacuously so: the cue really painted an underline colour (a cue-less render
        // reads the transparent default off the same cell).
        Assert.NotEqual(RenderCueColour(slotColumn, keyIndex: 1, showCue: false), colour);
    }

    /// <summary>
    /// The run-carrier scope contract: the gradient indicator resolves at the MNEMONIC RUN's own
    /// strip (one cluster), so the cue colour is independent of the key's position within the text
    /// — and equal to the colour a SINGLE-cluster label paints, which is the discriminating pin:
    /// under the old whole-label sampling a 4-cell label's head cell and a 1-cell label resolved
    /// the ramp at different widths and disagreed. (The pre-B2 hand-rolled draw sampled the whole
    /// label, so the colour used to track the key's position.)
    /// </summary>
    [Fact]
    public void CueColour_IsIndependentOfTheKeysPositionWithinTheText_TheDeltaSamplesItsOwnRun()
    {
        var first = RenderCueColour(slotColumn: 6, keyIndex: 0);
        var last  = RenderCueColour(slotColumn: 6, keyIndex: Label.Length - 1);

        Assert.Equal(first, last);

        // Non-vacuous: the cue really painted an underline colour.
        Assert.NotEqual(RenderCueColour(slotColumn: 6, keyIndex: 0, showCue: false), first);

        // The strip is the RUN, not the label: a one-cluster label samples identically.
        Assert.Equal(RenderCueColour(slotColumn: 6, keyIndex: 0, label: "W"), first);
    }

    private const string Label = "WXYZ";

    private static Color RenderCueColour(int slotColumn, int keyIndex, bool showCue = true,
                                         string label = Label)
    {
        var presenter = new AccessTextPresenter
                        {
                            Text = new AccessText(label, label[keyIndex], keyIndex),
                            // Default StartPoint/EndPoint are TopLeft -> TopRight: a horizontal ramp,
                            // so the sampled colour is a direct readout of the column parameter.
                            ActiveCueStyle = BrushedStyle.Identity
                                                         .Underlining(UnderlineStyle.Single,
                                                                      new LinearGradientBrush(
                                                                          Colors.Black, Colors.White)),
                            Foreground = Brushes.Red,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                        };

        // Without this the whole cue block early-outs and every cell reads a transparent underline —
        // the `showCue: false` renders above use exactly that as the non-vacuousness control.
        AccessKeyManager.SetShowUnderline(presenter, showCue);

        var root = new SlotHost(presenter)
                   {
                       SlotRect = new Rect(slotColumn, 0, WindowColumns - slotColumn, WindowRows)
                   };

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(slotColumn, presenter.Bounds.Column);

        return host.GetCell(slotColumn + keyIndex, 0).Style.UnderlineColor;
    }
}
