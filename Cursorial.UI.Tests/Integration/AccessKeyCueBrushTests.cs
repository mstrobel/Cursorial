// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine, so the test body IS the UI thread and RunUntilIdle must block on it.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// The access-key cue samples its <see cref="AccessTextPresenter.IndicatorBrush"/> against the TEXT's
/// own box, not against <c>Bounds</c>.
/// </summary>
/// <remarks>
/// <para>
/// The cue's column is element-local — it indexes the space <c>DrawText(0, 0, labelText, …)</c> paints
/// into. Pairing it with the parent-relative <c>Bounds</c> drove a gradient's parameter negative for
/// any element not sitting at its parent's origin, so every cue clamped to the brush's head.
/// </para>
/// <para>
/// It went unnoticed for two reasons worth recording: a SOLID brush ignores the bounds argument
/// entirely, so the defect is invisible unless the indicator is a gradient; and the negative
/// coordinate used to be masked by <c>Rect</c>'s origin validation, which turned it into a render-loop
/// throw rather than a wrong colour. Relaxing that validation (signed <c>Rect</c>) removed the throw
/// and left the wrong colour, which is how it surfaced.
/// </para>
/// </remarks>
public sealed class AccessKeyCueBrushTests
{
    private const int WindowColumns = 32;
    private const int WindowRows = 4;

    /// <summary>
    /// The invariant: the cue's colour depends on WHERE IN THE TEXT the key is, never on where the
    /// element sits in its parent. Same label, same key, three placements — one colour.
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

        // ...and NOT merely constant. The defect clamped every cue to the gradient's head, which
        // satisfies the equality above trivially — everything broken the same way is still "equal".
        // So also pin that the ramp is actually being traversed. Compared against the LAST index
        // rather than an adjacent one: the headless host quantizes to a palette, and neighbouring
        // samples of a 4-cell ramp land on the same entry.
        Assert.NotEqual(RenderCueColour(slotColumn, keyIndex: Label.Length - 1), colour);
    }

    /// <summary>
    /// ...and it DOES move with the key's position in the text, so the test above is not passing by
    /// the brush being sampled at a constant.
    /// </summary>
    [Fact]
    public void CueColour_TracksTheKeysPositionWithinTheText()
    {
        var first = RenderCueColour(slotColumn: 6, keyIndex: 0);
        var last  = RenderCueColour(slotColumn: 6, keyIndex: 3);

        Assert.NotEqual(first, last);
        Assert.True(last.Red > first.Red,
                    $"a black->white gradient should lighten toward the end: first={first}, last={last}");
    }

    private const string Label = "WXYZ";

    private static Color RenderCueColour(int slotColumn, int keyIndex)
    {
        var presenter = new AccessTextPresenter
                        {
                            Text = new AccessText(Label, Label[keyIndex], keyIndex),
                            // Default StartPoint/EndPoint are TopLeft -> TopRight: a horizontal ramp,
                            // so the sampled colour is a direct readout of the column parameter.
                            IndicatorBrush      = new LinearGradientBrush(Colors.Black, Colors.White),
                            KeyUnderline        = UnderlineStyle.Single,
                            Foreground          = Brushes.Red,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment   = VerticalAlignment.Top,
                        };

        // Without this the whole cue block early-outs and every cell reads a transparent underline —
        // which two of these assertions would then satisfy VACUOUSLY, by comparing nothing to nothing.
        AccessKeyManager.SetShowUnderline(presenter, true);

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
