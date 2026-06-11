using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §18 — read surfaces: <c>GetValue(maxPriority)</c>, <c>GetBaseValue</c>, <c>GetValueSource</c>, diagnostics (M259–M264).</summary>
public class Section18_ReadSurfaces
{
    [Fact]
    public void M259_FullStack_LocalValueProbe_EqualsGetBaseValue()
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(7, leaf.GetValue(Pi, BindingPriority.LocalValue));
        Assert.Equal(leaf.GetBaseValue(Pi), leaf.GetValue(Pi, BindingPriority.LocalValue)); // PD16
    }

    [Fact]
    public void M260_FullStack_StyleProbe_SkipsAnimationAndLocal()
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(5, leaf.GetValue(Pi, BindingPriority.Style));
    }

    [Fact]
    public void M261_FullStack_InheritedAndDefaultProbes()
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(2, leaf.GetValue(Pi, BindingPriority.Inherited));
        Assert.Equal(0, leaf.GetValue(Pi, BindingPriority.Default));
    }

    [Fact]
    public void M262_GetValue_UnsetMaxPriority_Throws()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.GetValue(P, BindingPriority.Unset)); // PD16
        Assert.Throws<ArgumentException>(() => host.GetValue((UIProperty)P, BindingPriority.Unset)); // untyped parity
    }

    [Theory]
    [InlineData("fresh", BindingPriority.Default, false)]
    [InlineData("inherited", BindingPriority.Inherited, false)]
    [InlineData("frame", BindingPriority.Style, false)]
    [InlineData("local", BindingPriority.LocalValue, false)]
    [InlineData("animated", BindingPriority.Animation, false)]
    [InlineData("scv-overwritten", BindingPriority.Style, true)]
    public void M263_GetValueSource_FamilyTable(string scenario, BindingPriority expectedPriority, bool expectedCurrent)
    {
        var host = new RecordingHost();
        switch (scenario)
        {
            case "fresh":
                break;
            case "inherited":
                var root = new RecordingHost();
                root.SetValue(Pi, 5);
                host.SetInheritanceParent(root);
                break;
            case "frame":
                host.AddFrame(new TestValueFrame(10).With(Pi, 5));
                break;
            case "local":
                host.SetValue(Pi, 5);
                break;
            case "animated":
                host.BeginAnimation(Pi).SetValue(9);
                break;
            case "scv-overwritten":
                host.AddFrame(new TestValueFrame(10).With(Pi, 5));
                host.SetCurrentValue(Pi, 6);
                break;
        }

        var source = host.GetValueSource(Pi);
        Assert.Equal(expectedPriority, source.Priority); // never Unset
        Assert.Equal(expectedCurrent, source.IsCurrentValue);
    }

    [Fact]
    public void M264_DiagnosticsEnumeration_ListsTheFullStack()
    {
        var (root, leaf, frame, _) = BuildM112Stack();

        var rows = leaf.GetValueDiagnostics(Pi);

        Assert.Equal(4, rows.Count); // strongest-first: animation, local raw, frame entry, inherited provenance
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.Animation, 9, HasValue: true), rows[0]);
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.LocalValue, 7, HasValue: true), rows[1]); // the RAW local
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.Style, 5, HasValue: true, frame.SortKey, IsActive: true), rows[2]);
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.Inherited, 2, HasValue: true, InheritedFrom: root), rows[3]);
    }
}
