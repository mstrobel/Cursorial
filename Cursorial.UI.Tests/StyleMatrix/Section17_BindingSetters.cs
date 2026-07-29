using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.UI;
using Cursorial.UI.Data;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §17 — binding-valued setters (ledger B15). A <see cref="Setter"/> whose value is a
/// <see cref="BindingBase"/> is a descriptor, not a constant: it passes through seal verbatim and
/// <c>StyleRuleFrame.OnInstalled</c> installs it once per styled element through
/// <see cref="BindingOperations.Install(UIObject, UIProperty, BindingBase, ValueFrame)"/> with the frame
/// as host — so the produced entry arbitrates at the rule's own priority and the store evicts and
/// disposes it when the frame is removed.
/// </summary>
public class Section17_BindingSetters
{
    /// <summary>An <c>object?</c>-typed property — the shape the descriptor could be mistaken for a literal on.</summary>
    private static readonly StyledProperty<object?> Obj = UIProperty.Register<Widget, object?>("B15Obj");

    [Fact] // B15a: a binding-valued setter delivers the source value through the style lane
    public void B15a_BindingSetter_DeliversTheSourceValue()
    {
        using var tree = ShowTree(show: false);
        tree.A.DataContext = new Vm { Age = 41 };
        tree.App.Styles.Add(R("Widget", (Widget.P, new Binding("Age"))));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(41, tree.A.GetValue(Widget.P));
    }

    [Fact] // B15b: a source change re-flows without a re-match (the expression is live, not a snapshot)
    public void B15b_SourceChange_ReflowsIntoTheProperty()
    {
        using var tree = ShowTree(show: false);
        var vm = new Vm { Age = 41 };
        tree.A.DataContext = vm;
        tree.App.Styles.Add(R("Widget", (Widget.P, new Binding("Age"))));
        tree.Host.ShowRoot(tree.Root);

        vm.Age = 7;

        Assert.Equal(7, tree.A.GetValue(Widget.P));
    }

    [Fact] // B15c: frame-hosted means the STYLE lane — an explicit local value still wins
    public void B15c_LocalValue_OutranksABindingSetter()
    {
        using var tree = ShowTree(show: false);
        tree.A.DataContext = new Vm { Age = 41 };
        tree.App.Styles.Add(R("Widget", (Widget.P, new Binding("Age"))));
        tree.Host.ShowRoot(tree.Root);

        tree.A.SetValue(Widget.P, 99);
        Assert.Equal(99, tree.A.GetValue(Widget.P));

        // …and clearing it promotes the binding back, rather than leaving the property stranded.
        tree.A.ClearValue(Widget.P);
        Assert.Equal(41, tree.A.GetValue(Widget.P));
    }

    [Fact] // B15d: removing the style retracts the frame, which evicts AND disposes the expression
    public void B15d_StyleRemoval_DisposesTheExpression()
    {
        using var tree = ShowTree(show: false);
        var vm = new Vm { Age = 41 };
        tree.A.DataContext = vm;
        var style = R("Widget", (Widget.P, new Binding("Age")));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(41, tree.A.GetValue(Widget.P));
        Assert.True(vm.SubscriberCount > 0, "the installed expression must subscribe to the source");

        tree.App.Styles.Remove(style);

        Assert.Equal(0, tree.A.GetValue(Widget.P));                      // back to the property default
        Assert.Equal(0, vm.SubscriberCount);                             // …and nothing left listening
    }

    [Fact] // B15e: one authored descriptor serves every matched element — each gets its OWN expression
    public void B15e_OneDescriptor_InstallsPerElement()
    {
        using var tree = ShowTree(show: false);
        tree.A.DataContext = new Vm { Age = 41 };
        tree.B.DataContext = new Vm { Age = 7 };
        tree.App.Styles.Add(R("Widget", (Widget.P, new Binding("Age"))));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(41, tree.A.GetValue(Widget.P));
        Assert.Equal(7, tree.B.GetValue(Widget.P));
    }

    [Fact] // B15f: THE LATENT BUG — a descriptor on an object?-typed property is a binding, never a literal
    public void B15f_ObjectTypedProperty_TakesTheBoundValue_NotTheDescriptor()
    {
        using var tree = ShowTree(show: false);
        tree.A.DataContext = new Vm { Age = 41 };
        tree.App.Styles.Add(R("Widget", (Obj, new Binding("Age"))));
        tree.Host.ShowRoot(tree.Root);

        // BindingBase IS assignable to object?, so the seal ladder's assignability early-out would have
        // taken the descriptor as the value itself — no error, no evaluation, just a Binding sitting in
        // the property. The descriptor check has to sort above that rung for this to hold.
        var value = tree.A.GetValue(Obj);
        Assert.IsNotType<Binding>(value);
        Assert.Equal(41, value);
    }

    [Fact] // B15g: a direct property has no store ladder to host the entry — refused at seal, not leaked
    public void B15g_DirectProperty_BindingSetter_IsASealError()
    {
        var style = new Style();
        style.Setters.Add(new Setter(Direct, new Binding("Age")));

        // Left to run, BindingExpressionCore.Lane would pick DirectProperty over FrameHosted and
        // PushToDirectProperty would write with no entry — nothing for frame retraction to evict, so the
        // value would outlive the rule. Better a named seal error than a value that never goes away.
        var error = Assert.Throws<InvalidOperationException>(style.Seal);
        Assert.Contains("direct property", error.Message);
    }

    [Fact] // B15h: the code-first API — Style.SetBinding mirrors SetResource
    public void B15h_SetBinding_IsTheFluentAuthoringPath()
    {
        using var tree = ShowTree(show: false);
        tree.A.DataContext = new Vm { Age = 41 };
        tree.App.Styles.Add(new Style("Widget", Resolver).SetBinding(Widget.P, new Binding("Age")));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(41, tree.A.GetValue(Widget.P));
    }

    /// <summary>A direct property (no store ladder) — the shape a frame-hosted binding cannot target.</summary>
    private static readonly DirectProperty<Widget, int> Direct =
        UIProperty.RegisterDirect<Widget, int>("B15Direct", static _ => 0, static (_, _) => { });

    private sealed class Vm : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _handlers;
        private int _age;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        /// <summary>Live handler count — the teardown probe (a disposed expression leaves none).</summary>
        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        public int Age
        {
            get => _age;
            set
            {
                if (_age == value)
                    return;

                _age = value;
                Raise();
            }
        }

        private void Raise([CallerMemberName] string? name = null)
            => _handlers?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
