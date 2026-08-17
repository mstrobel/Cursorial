using System.ComponentModel;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

// The clamping-source echo repaint guarantee: a TwoWay-bound source that normalizes mid-write (e.g. a
// (0, 360) angle clamp) echoes the transformed string back into Text reentrantly, inside the edit
// funnel. The funnel owns the repaint (_isApplyingEdit suppresses OnTextChanged's refresh), and must
// guarantee it even when the transform dodges both incidental repaint triggers — the clamped caret
// landing exactly on the pre-edit caret (SetCaretAndSelection's no-op guard) AND an equal measured
// width (the AffectsMeasure pass). Historic bug: appending to "120" (echo "360": same width, caret
// 3 -> clamp(4) = 3) left the stale "120" raster on screen while Text held "360" until focus change.
public sealed class TextBoxBindingEchoRepaintTests
{
    private sealed class ClampVm : INotifyPropertyChanged
    {
        private double _angle;
        public double Angle
        {
            get => _angle;
            set
            {
                var clamped = Math.Clamp(value, 0d, 360d);
                if (_angle == clamped) return;
                _angle = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Angle)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static (UIHeadlessHost Host, TextBox Box, ClampVm Vm) Shown(double initialAngle)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 4),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var box = new TextBox
        {
            Width = 12,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();

        var vm = new ClampVm();
        box.DataContext = vm;
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(ClampVm.Angle)) { Mode = BindingMode.TwoWay });
        host.RunUntilIdle();

        vm.Angle = initialAngle;
        host.RunUntilIdle();

        return (host, box, vm);
    }

    private static string ScreenRow0(UIHeadlessHost host)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < 30; c++)
            sb.Append(host.FrameBuffer[c, 0].Grapheme);
        return sb.ToString().Trim();
    }

    [Fact] // the historic stuck case: same-width echo + caret clamped back onto the pre-edit caret
    public void AppendAtEnd_ClampedEcho_RepaintsImmediately()
    {
        var (host, box, vm) = Shown(initialAngle: 120);
        using var _ = host;

        Assert.Contains("120", ScreenRow0(host));

        host.SendKey(Key.End);
        host.RunUntilIdle();
        host.SendText("0");            // "1200" -> source clamps to 360, echo "360"
        host.RunUntilIdle();

        Assert.Equal(360d, vm.Angle);
        Assert.Equal("360", box.Text);
        Assert.Equal(3, box.CaretIndex);              // clamped to the end of the echo, never 4
        Assert.Contains("360", ScreenRow0(host));     // repaints immediately — no focus change needed
        Assert.DoesNotContain("120", ScreenRow0(host));
    }

    [Fact] // overflow on the third digit: the clamped caret moves (2 -> 3) — always repainted
    public void ThirdDigitOverflow_ClampedEcho_RepaintsImmediately()
    {
        var (host, box, vm) = Shown(initialAngle: 99);
        using var _ = host;

        host.SendKey(Key.End);
        host.RunUntilIdle();
        host.SendText("9");            // "999" -> clamps to 360
        host.RunUntilIdle();

        Assert.Equal(360d, vm.Angle);
        Assert.Equal("360", box.Text);
        Assert.Equal(3, box.CaretIndex);
        Assert.Contains("360", ScreenRow0(host));
    }

    [Fact] // insert away from the end: the caret moves (0 -> 1) — always repainted; caret keeps its index
    public void MidInsert_ClampedEcho_RepaintsImmediately()
    {
        var (host, box, vm) = Shown(initialAngle: 120);
        using var _ = host;

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        host.SendText("9");            // "9120" -> clamps to 360
        host.RunUntilIdle();

        Assert.Equal(360d, vm.Angle);
        Assert.Equal("360", box.Text);
        Assert.Equal(1, box.CaretIndex);              // index preserved within the echoed text
        Assert.Contains("360", ScreenRow0(host));
    }

    [Fact] // the undo funnel has the same guarantee: undoing the transform restores "120" with the
           // recorded caret (3) equal to the current caret (3) and equal width — must still repaint
    public void UndoOfTransform_RestoredText_RepaintsImmediately()
    {
        var (host, box, vm) = Shown(initialAngle: 120);
        using var _ = host;

        host.SendKey(Key.End);
        host.RunUntilIdle();
        host.SendText("0");            // "1200" -> clamped, undo records "120" -> "360" full-replace
        host.RunUntilIdle();
        Assert.Contains("360", ScreenRow0(host));

        host.SendKey(Key.Character, KeyModifiers.Control, "z");
        host.RunUntilIdle();

        Assert.Equal("120", box.Text);
        Assert.Equal(3, box.CaretIndex);              // the recorded pre-edit caret
        Assert.Contains("120", ScreenRow0(host));     // ApplyReverse repaints despite the caret no-op
        Assert.DoesNotContain("360", ScreenRow0(host));
    }
}
