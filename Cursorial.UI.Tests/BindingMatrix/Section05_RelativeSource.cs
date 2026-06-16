using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §5 — RelativeSource anchoring (B47–B54). FindAncestor rows use a real
/// <see cref="StackPanel"/> tree and read its <c>Spacing</c> property off the resolved ancestor.
/// </summary>
public class Section05_RelativeSource
{
    public Section05_RelativeSource()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B047_Self_PathAgainstTarget()
    {
        var a = new BindWidget { Num = 5 };
        a.SetBinding(BindWidget.TextProperty, new Binding("Num") { RelativeSource = RelativeSource.Self });
        Assert.Equal("5", a.Text);

        a.Num = 9;
        Assert.Equal("9", a.Text);
    }

    [Fact]
    public void B048_TemplatedParent()
    {
        var ctrl = new BindWidget { Num = 3 };
        var p = new BindWidget();
        p.StampTemplatedParent(ctrl);

        p.SetBinding(BindWidget.TextProperty, new Binding("Num") { RelativeSource = RelativeSource.TemplatedParent });
        Assert.Equal("3", p.Text);
    }

    [Fact]
    public void B049_TemplatedParent_Null_SourceMissing()
    {
        var p = new BindWidget();
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = p.SetBinding(BindWidget.TextProperty, new Binding("Num") { RelativeSource = RelativeSource.TemplatedParent });
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);
    }

    [Fact]
    public void B050_FindAncestor_Level1()
    {
        var root = new StackPanel();
        var paneA = new StackPanel { Spacing = 7 };
        var a = new BindWidget();
        root.Children.Add(paneA);
        paneA.Children.Add(a);

        a.SetBinding(BindWidget.TextProperty,
            new Binding("Spacing") { RelativeSource = RelativeSource.Ancestor<StackPanel>(1) });
        Assert.Equal("7", a.Text); // nearest StackPanel = paneA
    }

    [Fact]
    public void B051_FindAncestor_Level2_CountsMatches()
    {
        var root = new StackPanel { Spacing = 100 };
        var paneA = new StackPanel { Spacing = 7 };
        var a = new BindWidget();
        root.Children.Add(paneA);
        paneA.Children.Add(a);

        a.SetBinding(BindWidget.TextProperty,
            new Binding("Spacing") { RelativeSource = RelativeSource.Ancestor<StackPanel>(2) });
        Assert.Equal("100", a.Text); // the SECOND assignable match = root
    }

    [Fact]
    public void B052_FindAncestor_Reparent_ReResolves()
    {
        var root = new StackPanel();
        var paneA = new StackPanel { Spacing = 1 };
        var paneB = new StackPanel { Spacing = 2 };
        var a = new BindWidget();
        root.Children.Add(paneA);
        root.Children.Add(paneB);
        paneA.Children.Add(a);

        a.SetBinding(BindWidget.TextProperty,
            new Binding("Spacing") { RelativeSource = RelativeSource.Ancestor<StackPanel>(1) });
        Assert.Equal("1", a.Text);

        paneA.Children.Remove(a);
        paneB.Children.Add(a); // reparent
        Assert.Equal("2", a.Text); // re-resolved to the new nearest StackPanel
    }

    [Fact]
    public void B054_FindAncestor_FromTemplatePart_CrossesTemplateBoundary()
    {
        // A template part's FindAncestor walks the LOGICAL tree (part → templated parent → beyond),
        // crossing the template boundary the way the logical tree does.
        var outer = new StackPanel { Spacing = 11 };
        var ctrl = new BindWidget();
        outer.Children.Add(ctrl);

        var part = new BindWidget();
        part.StampTemplatedParent(ctrl);
        ctrl.AddChild(part); // the part is a logical child of the templated parent

        part.SetBinding(BindWidget.TextProperty,
            new Binding("Spacing") { RelativeSource = RelativeSource.Ancestor<StackPanel>(1) });
        Assert.Equal("11", part.Text); // reached past the templated parent to the outer StackPanel
    }

    [Fact]
    public void B053_FindAncestor_NotFound_Traces()
    {
        var root = new StackPanel();
        var a = new BindWidget();
        root.Children.Add(a);
        BindingDiagnostics.Level = BindingTraceLevel.Warning;

        var expr = a.SetBinding(BindWidget.TextProperty,
            new Binding("Num") { RelativeSource = RelativeSource.Ancestor<TestCard>(1) });
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.AncestorNotFound);
    }
}

/// <summary>A type with no instances in the test tree — the B53 "no Card ancestor" target.</summary>
public sealed class TestCard : UIElement;
