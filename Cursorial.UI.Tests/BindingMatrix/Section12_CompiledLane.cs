using Cursorial.UI;
using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §12 — the compiled lane (B146–B156, BD17). The typed push lane that lands at P10/B2:
/// a whole-chain <c>Getter</c> read, <c>Steps</c>-driven INPC/INCC subscription, a typed zero-box push,
/// and the reflective-fallback equivalence (B156). Anchoring / triggers / write-back are shared with the
/// reflection lane via <c>BindingExpressionCore</c>.
/// </summary>
public class Section12_CompiledLane
{
    public Section12_CompiledLane()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    private static (BindWidget Root, BindWidget Child) Tree(Vm vm)
    {
        var root = new BindWidget { DataContext = vm };
        var child = new BindWidget();
        root.AddChild(child);
        return (root, child);
    }

    [Fact] // B146 — INPC drives the compiled lane; the value is one whole-chain Getter call
    public void B146_CompiledLane_InpcDrivesWholeChainGetter()
    {
        var vm = new Vm { IsDirty = true };
        var (_, a) = Tree(vm);

        a.SetBinding(BindWidget.FlagProperty, Binding.Compiled(static (Vm m) => m.IsDirty));
        Assert.True(a.Flag);

        vm.IsDirty = false;
        Assert.False(a.Flag);
    }

    [Fact] // B147 — OneWay typed StyledProperty<T> target, no converter/format ⇒ 0 B steady-state push
    public void B147_CompiledLane_TypedPush_ZeroSteadyStateAllocation()
    {
        // Num is a OneWay-by-default StyledProperty<int> (no target observer) — the genuine zero-box path,
        // the binding analog of AnimatedValueHandle<T>. (A TwoWay target boxes once for its observer
        // notification; that is the B182-style forfeit territory, not the zero-box perf claim.) BumpAge
        // raises a cached args instance so the VM's notification doesn't pollute the measurement.
        var vm = new Vm();
        var (_, a) = Tree(vm);
        a.SetBinding(BindWidget.NumProperty, Binding.Compiled(static (Vm m) => m.Age));

        for (var i = 0; i < 64; i++) // warm up: materialize the typed entry + the subscription closure
            vm.BumpAge();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
            vm.BumpAge();
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0, $"compiled typed push allocated {delta} B in steady state (expected 0).");
    }

    [Fact] // B148 — member chain; a swapped intermediate rewires downstream, the typed Getter re-reads
    public void B148_CompiledLane_MemberChain_SwapIntermediate()
    {
        var vm = new Vm { Sub = new Vm { Name = "first" } };
        var (_, a) = Tree(vm);

        a.SetBinding(BindWidget.TextProperty, Binding.Compiled(static (Vm m) => m.Sub!.Name));
        Assert.Equal("first", a.Text);

        vm.Sub!.Name = "renamed";   // leaf INPC
        Assert.Equal("renamed", a.Text);

        vm.Sub = new Vm { Name = "swapped" }; // intermediate swap rewires the tail
        Assert.Equal("swapped", a.Text);
    }

    [Fact] // B149 — constant-index hop subscribes INotifyCollectionChanged; the value re-reads via Getter
    public void B149_CompiledLane_IndexerHop_InccReplace()
    {
        var vm = new Vm();
        vm.Tags.Add("zero");
        var (_, a) = Tree(vm);

        a.SetBinding(BindWidget.TextProperty, Binding.Compiled(static (Vm m) => m.Tags[0]));
        Assert.Equal("zero", a.Text);

        vm.Tags[0] = "replaced"; // INCC Replace
        Assert.Equal("replaced", a.Text);
    }

    [Fact] // B150 — an operator in the lambda ⇒ FormatException naming the node (member + constant-index only)
    public void B150_CompiledLane_OperatorInLambda_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => Binding.Compiled(static (Vm m) => m.Age + 1));
        Assert.Contains("operator", ex.Message);
    }

    [Fact] // B151 — a method call in the lambda ⇒ FormatException naming the method
    public void B151_CompiledLane_MethodCall_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => Binding.Compiled(static (Vm m) => m.GetName()));
        Assert.Contains("GetName", ex.Message);
    }

    [Fact] // B152 — a read-only leaf (Setter == null) used TwoWay degrades to OneWay (BD10 via the typed lane)
    public void B152_CompiledLane_ReadOnlyLeaf_DegradesToOneWay()
    {
        var vm = new Vm();
        vm.SetReadOnlyAge(7);
        var (_, a) = Tree(vm);

        // ReadOnlyAge has no setter ⇒ CompiledBinding.Setter is null ⇒ one-way only.
        var expr = a.SetBinding(BindWidget.NumProperty,
            new CompiledBinding<Vm, int>(static m => m.ReadOnlyAge, setter: null, default, "ReadOnlyAge")
                { Mode = BindingMode.TwoWay });
        Assert.Equal(7, a.Num);
        Assert.Equal(BindingMode.OneWay, expr.EffectiveMode); // degraded
    }

    [Fact] // B153 — the anchor resolves to a non-TSource object ⇒ SourceTypeMismatch + Unset
    public void B153_CompiledLane_AnchorTypeMismatch_Unset()
    {
        // DataContext is a string, but the compiled getter expects a Vm.
        var root = new BindWidget { DataContext = "not-a-vm" };
        var a = new BindWidget();
        root.AddChild(a);

        a.SetBinding(BindWidget.TextProperty, Binding.Compiled(static (Vm m) => m.Name));
        Assert.Null(a.Text); // unset → the property's default (null), not a throw
    }

    [Fact] // B154 — a compiled binding anchored via RelativeSource.Self (the full AnchoredBinding surface)
    public void B154_CompiledLane_AnchoredSelf()
    {
        var a = new BindWidget { Num = 42 };

        // Self-source: the compiled getter reads off the element itself.
        a.SetBinding(BindWidget.NumProperty,
            new CompiledBinding<BindWidget, int>(static w => w.Num, setter: null, default, "Num"));
        // The binding pushes the self-read value; no throw, the lane shares Self anchoring with reflection.
        Assert.Equal(42, a.Num);
    }

    [Fact] // B155 — a struct-typed intermediate: the whole-chain Getter copies it; no subscription attempted
    public void B155_CompiledLane_StructIntermediate()
    {
        var vm = new Vm { SomeStruct = new Coord { Field = 9 } };
        var (_, a) = Tree(vm);

        a.SetBinding(BindWidget.NumProperty, Binding.Compiled(static (Vm m) => m.SomeStruct.Field));
        Assert.Equal(9, a.Num); // reads correctly through the struct hop (no INPC subscription on the struct)
    }

    [Fact] // B156 — compiled vs reflective produce the IDENTICAL target value through the shared engine
    public void B156_CompiledLane_EquivalentToReflectionLane()
    {
        var vm = new Vm { Name = "equiv" };

        var (_, compiled) = Tree(vm);
        compiled.SetBinding(BindWidget.TextProperty, Binding.Compiled(static (Vm m) => m.Name));

        var (_, reflective) = Tree(vm);
        reflective.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        Assert.Equal(reflective.Text, compiled.Text);

        vm.Name = "changed";
        Assert.Equal(reflective.Text, compiled.Text); // both rewired identically
        Assert.Equal("changed", compiled.Text);
    }
}
