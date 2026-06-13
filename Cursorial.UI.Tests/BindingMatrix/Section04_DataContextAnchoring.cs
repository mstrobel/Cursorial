using Cursorial.UI;
using Cursorial.UI.Data;
using Xunit;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §4 — DataContext, the as-target special case, and Source (B37–B46).</summary>
public class Section04_DataContextAnchoring
{
    public Section04_DataContextAnchoring()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B037_DefaultAnchor_InheritedDataContext_DeliversToDescendant()
    {
        var vm = new Vm { Name = "n" };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);

        a.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("n", a.Text); // inherited DataContext reaches the entry-less descendant
    }

    [Fact]
    public void B038_DataContextChange_FullRebind()
    {
        var vm = new Vm { Name = "first" };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);
        a.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("first", a.Text);

        var vm2 = new Vm { Name = "second" };
        root.DataContext = vm2; // whole-subtree inherited change
        Assert.Equal("second", a.Text);
    }

    [Fact]
    public void B039_EmptyPath_SourceItself()
    {
        var a = new BindWidget { DataContext = "literal" };
        a.SetBinding(BindWidget.TextProperty, new Binding(""));
        Assert.Equal("literal", a.Text);
    }

    [Fact]
    public void B040_DataContextAsTarget_AnchorsOnParentDataContext_NoOscillation()
    {
        var s1 = new Vm { Name = "s1" };
        var s2 = new Vm { Name = "s2" };
        var vm = new Vm { Sub = s1 };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);

        // a.DataContext ← {Binding Sub} (default source). Anchors on the LOGICAL PARENT's DataContext.
        a.SetBinding(UIElement.DataContextProperty, new Binding("Sub"));
        Assert.Same(s1, a.DataContext);

        vm.Sub = s2;
        Assert.Same(s2, a.DataContext); // re-anchored on the parent, no oscillation
    }

    [Fact]
    public void B041_DataContextAsTarget_NoLogicalParentYet_ParksThenResolvesOnAttach()
    {
        var s1 = new Vm { Name = "s1" };
        var vm = new Vm { Sub = s1 };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();

        // Install while `a` has no logical parent yet: parks SourceMissing, no trace.
        var expr = a.SetBinding(UIElement.DataContextProperty, new Binding("Sub"));
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);
        Assert.Null(a.DataContext);

        // On attach, the parent anchor resolves and the binding produces.
        root.AddChild(a);
        Assert.Equal(BindingStatus.Active, expr.Status);
        Assert.Same(s1, a.DataContext);
    }

    [Fact]
    public void B045_DataContextPathSegment_ResolvesViaUIPropertyLane()
    {
        // "DataContext.Tags[0]" with Source = b: DataContext resolves as a registered UIProperty hop,
        // then Tags[0]. (the §2.8 idiom)
        var vm = new Vm();
        vm.Tags.Add("t");
        var b = new BindWidget { DataContext = vm };
        var a = new BindWidget();

        a.SetBinding(BindWidget.TextProperty, new Binding("DataContext.Tags[0]") { Source = b });
        Assert.Equal("t", a.Text);
    }

    [Fact]
    public void B042_Source_Fixed_IgnoresDataContextChange()
    {
        var vm = new Vm { Name = "src" };
        var root = new BindWidget { DataContext = new Vm { Name = "ctx" } };
        var a = new BindWidget();
        root.AddChild(a);

        a.SetBinding(BindWidget.TextProperty, new Binding("Name") { Source = vm });
        Assert.Equal("src", a.Text);

        root.DataContext = new Vm { Name = "other" };
        Assert.Equal("src", a.Text); // Source never re-resolves
    }

    [Fact]
    public void B043_ConflictingAnchors_Throws()
    {
        var a = new BindWidget();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            a.SetBinding(BindWidget.TextProperty, new Binding("Name") { Source = new Vm(), ElementName = "x" }));
        Assert.Contains("mutually exclusive", ex.Message);
    }

    [Fact]
    public void B044_DefaultSourceOnNonElement_InstallError()
    {
        var nonElement = new NonElementObject();
        var binding = new Binding("Name");
        // Activation parks SourceMissing and traces — no DataContext on a non-UIElement target. This
        // case can never recover (no element to carry a DataContext), so it traces a tailored
        // install-time message naming the workaround rather than the generic "parked until attach".
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = BindingOperations.Install(nonElement, NonElementObject.ValueProperty, binding);
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);
        Assert.Contains(BindingDiagnostics.RecentEvents,
            e => e.Kind == BindingFailureKind.SourceMissing && e.Message.Contains("non-UIElement target has no DataContext; use Source"));
    }

    [Fact]
    public void B046_WholeWindowDataContextSwap_RebindsDescendants()
    {
        var vm = new Vm { Name = "a" };
        var root = new BindWidget { DataContext = vm };
        var x = new BindWidget();
        var y = new BindWidget();
        root.AddChild(x);
        root.AddChild(y);
        x.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        y.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("a", x.Text);
        Assert.Equal("a", y.Text);

        root.DataContext = new Vm { Name = "z" };
        Assert.Equal("z", x.Text);
        Assert.Equal("z", y.Text);
    }
}

/// <summary>A non-<c>UIElement</c> <c>UIObject</c> with a styled property — the B44 install-error target.</summary>
public sealed class NonElementObject : UIObject
{
    public static readonly StyledProperty<string?> ValueProperty =
        UIProperty.Register<NonElementObject, string?>("Value");

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
