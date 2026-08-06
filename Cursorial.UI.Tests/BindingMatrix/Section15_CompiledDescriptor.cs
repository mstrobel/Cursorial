using System.Globalization;

using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §15 — the compiled descriptor (B179–B186): the <see cref="CompiledBinding{TSource,TValue}"/>
/// shape, caching/sharing, the converter forfeit of the typed fast path (B182), the generator-emitted ≡
/// reflective-fallback equivalence (B183), the descriptor's <c>PathText</c> diagnostic hook (B184), the
/// reflective fallback as the v1 producer (B185), and the frame-hosted compiled install (B186).
/// </summary>
public class Section15_CompiledDescriptor
{
    public Section15_CompiledDescriptor()
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

    [Fact] // B179 — the descriptor exposes Getter/Setter/Steps/PathText; null Setter ⇒ one-way; inherits AnchoredBinding
    public void B179_Descriptor_Shape()
    {
        var descriptor = Binding.Compiled(static (Vm m) => m.IsDirty);
        Assert.NotNull(descriptor.Getter);
        Assert.NotNull(descriptor.Setter);                 // IsDirty is writable
        Assert.Equal("IsDirty", descriptor.PathText);
        Assert.Equal(1, descriptor.Steps.Length);

        var readOnly = new CompiledBinding<Vm, int>(static m => m.ReadOnlyAge, setter: null, default, "ReadOnlyAge");
        Assert.Null(readOnly.Setter);                      // ⇒ one-way only

        // The full AnchoredBinding surface is available on the compiled descriptor.
        var anchored = new CompiledBinding<Vm, string?>(static m => m.Name, null, default, "Name")
            { Source = new Vm(), ElementName = null, Mode = BindingMode.OneTime };
        Assert.NotNull(anchored.Source);
        Assert.Equal(BindingMode.OneTime, anchored.Mode);
    }

    [Fact] // B180 — a constant-index hop's MemberName is "Item[]"; GetStep applies the captured index
    public void B180_IndexerStep_ItemConvention()
    {
        var descriptor = Binding.Compiled(static (Vm m) => m.Tags[0]);
        // The chain is Tags → [0]; find the "Item[]" indexer step (the INPC convention).
        var found = false;
        foreach (var step in descriptor.Steps.ToArray())
            if (step.MemberName == "Item[]")
                found = true;
        Assert.True(found, "expected an 'Item[]' indexer step");
    }

    [Fact] // B181 — the descriptor is construction-immutable + instance-shareable across elements
    public void B181_Descriptor_SharedAcrossElements()
    {
        var shared = Binding.Compiled(static (Vm m) => m.Name);

        var vm1 = new Vm { Name = "one" };
        var vm2 = new Vm { Name = "two" };
        var (_, a) = Tree(vm1);
        var (_, b) = Tree(vm2);

        a.SetBinding(BindWidget.TextProperty, shared);
        b.SetBinding(BindWidget.TextProperty, shared); // one descriptor serves both; per-target state in the expressions

        Assert.Equal("one", a.Text);
        Assert.Equal("two", b.Text);

        vm1.Name = "one!";
        Assert.Equal("one!", a.Text);
        Assert.Equal("two", b.Text); // independent
    }

    [Fact] // B182 — a Converter forfeits the typed zero-box push → the boxed pipeline (the value still converts)
    public void B182_Converter_ForfeitsTypedPath()
    {
        var vm = new Vm { Age = 5 };
        var (_, a) = Tree(vm);

        var descriptor = new CompiledBinding<Vm, int>(static m => m.Age, setter: null, default, "Age")
            { Converter = new TimesTen() };
        a.SetBinding(BindWidget.NumProperty, descriptor);

        Assert.Equal(50, a.Num); // converter applied through the boxed pipeline (typed fast path forfeited)
    }

    [Fact] // B183 — a hand-written (generator-equivalent) CompiledBinding ctor ≡ Binding.Compiled at runtime
    public void B183_GeneratorEquivalent_ToReflectiveFallback()
    {
        var vm = new Vm { Name = "gen" };

        // The "generator" emits the ctor directly with real delegates (no lambda analysis).
        var generated = new CompiledBinding<Vm, string?>(
            getter: static m => m.Name,
            setter: static (m, v) => m.Name = v,
            steps: new[] { new CompiledPathStep("Name", static o => ((Vm)o!).Name) },
            pathText: "Name");

        var analyzed = Binding.Compiled(static (Vm m) => m.Name);

        var (_, g) = Tree(vm);
        var (_, n) = Tree(vm);
        g.SetBinding(BindWidget.TextProperty, generated);
        n.SetBinding(BindWidget.TextProperty, analyzed);

        Assert.Equal(n.Text, g.Text);
        vm.Name = "gen2";
        Assert.Equal(n.Text, g.Text); // identical runtime behavior
        Assert.Equal("gen2", g.Text);
    }

    [Fact] // B184 — the descriptor carries PathText for build-time path diagnostics (the B3 generator hook)
    public void B184_Descriptor_CarriesPathTextForDiagnostics()
    {
        var descriptor = Binding.Compiled(static (Vm m) => m.Sub!.Name);
        // The build-time path validation (against x:DataType) lands at B3 in the generator; the descriptor
        // shape exposes PathText for exactly that diagnostic. v1 (reflective) produces a runtime trace instead.
        Assert.Equal("Sub.Name", descriptor.PathText);
    }

    [Fact] // B185 — the reflective fallback IS the v1 producer: Binding.Compiled installs + produces at runtime
    public void B185_ReflectiveFallback_IsV1Producer()
    {
        var vm = new Vm { Age = 11 };
        var (_, a) = Tree(vm);

        a.SetBinding(BindWidget.NumProperty, Binding.Compiled(static (Vm m) => m.Age));
        Assert.Equal(11, a.Num); // works pre-X4 via expr.Compile()

        vm.Age = 22;
        Assert.Equal(22, a.Num);
    }

    [Fact] // B186 — a frame-hosted compiled install routes through the frame and dies on its removal
    public void B186_FrameHosted_CompiledInstall()
    {
        var vm = new Vm { IsDirty = true };
        var w = new BindWidget { DataContext = vm };
        var frame = new TestFrame();
        w.AddFrame(frame);

        var expr = BindingOperations.Install(w, BindWidget.FlagProperty, Binding.Compiled(static (Vm m) => m.IsDirty), frame);
        Assert.True(w.Flag);
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.FlagProperty)); // frame-hosted ⇒ not LocalValue (BD7)

        w.RemoveFrame(frame);
        Assert.True(expr.IsDisposed); // evicted with the frame
    }

    private sealed class TimesTen : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int i ? i * 10 : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int i ? i / 10 : value;
    }
}
