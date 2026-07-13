using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

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

    [Fact] // B041a — the OTHER late-arrival order: parented, but the parent's DataContext arrives after
    // install (the loader's construction order — children built and bound BEFORE the parent's context
    // flows; a designer d:DataContext or a late VM assignment). The parent-DataContext observer must be
    // installed on the FAILED resolve too — tree events fire at parenting time and never again, so the
    // observer is the only wake-up signal. (The Cursorial.Samples Layers-pane bug: every XAML-authored
    // DataContext="{Binding …}" re-scope parked permanently.)
    public void B041a_DataContextAsTarget_ParentContextArrivesLate_WakesOnArrival()
    {
        var s1 = new Vm { Name = "s1" };
        var root = new BindWidget();
        var a = new BindWidget();
        root.AddChild(a);

        // Install while the PARENT's DataContext is still null: parks SourceMissing.
        var expr = a.SetBinding(UIElement.DataContextProperty, new Binding("Sub"));
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);
        Assert.Null(a.DataContext);

        // The parent's context arrives late — the parked binding must wake and re-scope.
        root.DataContext = new Vm { Sub = s1 };
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
    public void B044_DefaultSourceOnNonElement_NoInheritanceParent_ParksSilently()
    {
        var nonElement = new NonElementObject();
        var binding = new Binding("Name");
        // A non-UIElement target has no DataContext of its own; it anchors on the nearest UIElement up its
        // inheritance chain (BD13). With no inheritance parent yet, it parks SourceMissing SILENTLY (it
        // recovers if a parent leading to a UIElement is later set) — no tailored install-time trace.
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = BindingOperations.Install(nonElement, NonElementObject.ValueProperty, binding);
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);
        Assert.DoesNotContain(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.SourceMissing);
    }

    [Fact]
    public void B044a_InputBindingCommand_BoundThenAdded_ResolvesViaOwnerDataContext()
    {
        var cmd = new StubCommand();
        var owner = new BindWidget { DataContext = new CommandVm { Cmd = cmd } };

        var kb = new KeyBinding();
        // The XAML/loader order: the Command="{Binding}" installs BEFORE the gesture is added to its owner.
        kb.SetBinding(InputBinding.CommandProperty, new Binding("Cmd"));
        Assert.Null(kb.Command); // no inheritance parent yet → parked SourceMissing

        owner.InputBindings.Add(kb); // SetInheritanceParent(owner) → re-anchors on owner and resolves
        Assert.Same(cmd, kb.Command);
    }

    [Fact]
    public void B044a_InputBindingCommand_AddedThenBound_ResolvesAtInstall()
    {
        var cmd = new StubCommand();
        var owner = new BindWidget { DataContext = new CommandVm { Cmd = cmd } };

        var kb = new KeyBinding();
        owner.InputBindings.Add(kb); // owner (and its DataContext) set first
        kb.SetBinding(InputBinding.CommandProperty, new Binding("Cmd")); // resolves at install
        Assert.Same(cmd, kb.Command);
    }

    [Fact]
    public void B044a_InputBindingCommand_RemovedFromOwner_ReParks()
    {
        var cmd = new StubCommand();
        var owner = new BindWidget { DataContext = new CommandVm { Cmd = cmd } };

        var kb = new KeyBinding();
        owner.InputBindings.Add(kb);
        kb.SetBinding(InputBinding.CommandProperty, new Binding("Cmd"));
        Assert.Same(cmd, kb.Command);

        owner.InputBindings.Remove(kb); // SetInheritanceParent(null) → anchor lost
        Assert.Equal(BindingStatus.SourceMissing,
            BindingOperations.GetBindingExpression(kb, InputBinding.CommandProperty)!.Status);
        Assert.Null(kb.Command);
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

/// <summary>A non-<c>UIElement</c> <c>UIObject</c> with a styled property — the B44 silent-park target.</summary>
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

/// <summary>A view-model exposing a command — the B44a InputBinding-anchoring source.</summary>
public sealed class CommandVm
{
    public ICommand? Cmd { get; set; }
}

/// <summary>A do-nothing <see cref="ICommand"/> for identity assertions.</summary>
public sealed class StubCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) { }
}
