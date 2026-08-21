using System.Globalization;

using Cursorial.UI;
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

    [Fact] // B187 — For(StyledProperty): tree-free descriptor — carried registration, qualified PathText, live observer updates
    public void B187_ForStyled_DescriptorShape_And_LiveUpdate()
    {
        var src = new BindWidget { Num = 5 };
        var descriptor = CompiledBinding.For(BindWidget.NumProperty, source: src);
        Assert.Same(BindWidget.NumProperty, descriptor.Steps.Span[0].UIProperty); // observer channel, no registry probe
        Assert.Equal("Num", descriptor.PathText); // builder convention: bare for plain styled, qualified for attached/direct

        var target = new BindWidget();
        target.SetBinding(BindWidget.NumProperty, descriptor);
        Assert.Equal(5, target.Num);

        src.Num = 9; // property-store change delivered through the carried-registration observer
        Assert.Equal(9, target.Num);
    }

    [Fact] // B188 — For(StyledProperty) TwoWay: a target write pushes back through SetValue (durable LocalValue)
    public void B188_ForStyled_TwoWay_WritesBack()
    {
        var src = new BindWidget { Num = 1 };
        var target = new BindWidget();
        target.SetBinding(BindWidget.NumProperty,
                          CompiledBinding.For(BindWidget.NumProperty, source: src, mode: BindingMode.TwoWay));
        Assert.Equal(1, target.Num);

        target.Num = 42;
        Assert.Equal(42, src.GetValue(BindWidget.NumProperty));
    }

    [Fact] // B189 — For on a read-only styled registration: null Setter ⇒ TwoWay degrades to OneWay (BD10)
    public void B189_ForStyled_ReadOnly_IsOneWay()
    {
        Assert.Null(CompiledBinding.For(BindWidget.RoProperty, mode: BindingMode.TwoWay).Setter);
    }

    [Fact] // B190 — For(DirectProperty): the registration's own delegates serve verbatim — a registered setter makes TwoWay real
    public void B190_ForDirect_TwoWay_WritesThroughRegistrationSetter()
    {
        var src = new BindWidget();
        var descriptor = CompiledBinding.For(BindWidget.DirProperty, source: src, mode: BindingMode.TwoWay);
        Assert.NotNull(descriptor.Setter); // the tree-based factory dropped this silently
        Assert.Same(BindWidget.DirProperty, descriptor.Steps.Span[0].UIProperty);
        Assert.Equal("(BindWidget.Dir)", descriptor.PathText);

        var target = new BindWidget();
        target.SetBinding(BindWidget.NumProperty, descriptor);
        target.Num = 7;
        Assert.Equal(7, BindWidget.DirProperty.Getter(src)); // reached the backing field through the registration setter
    }

    [Fact] // B191 — For on a getter-only DirectProperty stays one-way, matching its registration
    public void B191_ForDirect_ReadOnly_IsOneWay()
    {
        Assert.Null(CompiledBinding.For(Cursorial.UI.Controls.MenuItem.HasItemsProperty).Setter);
    }

    [Fact] // B192 — descriptor-only steps (null GetStep): rewiring materializes intermediates through the carried registration
    public void B192_DescriptorOnlySteps_MaterializeAndRewire()
    {
        var root = new ChainHost();
        var mid = new ChainHost();
        root.SetValue(ChainHost.NextProperty, mid);
        mid.SetValue(ChainHost.NumProperty, 3);

        var descriptor = new CompiledBinding<ChainHost, int>(
            static r => r.GetValue(ChainHost.NextProperty)?.GetValue(ChainHost.NumProperty) ?? 0,
            null,
            new[]
            {
                new CompiledPathStep("Next") { UIProperty = ChainHost.NextProperty },
                new CompiledPathStep("Num") { UIProperty = ChainHost.NumProperty },
            },
            "(ChainHost.Next).(ChainHost.Num)") { Source = root };
        Assert.Null(descriptor.Steps.Span[0].GetStep); // descriptor-only — no closure anywhere in the chain

        var target = new BindWidget();
        target.SetBinding(BindWidget.NumProperty, descriptor);
        Assert.Equal(3, target.Num);

        mid.SetValue(ChainHost.NumProperty, 8);       // leaf hop change → observer push
        Assert.Equal(8, target.Num);

        var other = new ChainHost();
        other.SetValue(ChainHost.NumProperty, 21);
        root.SetValue(ChainHost.NextProperty, other); // hop-0 change → the tail re-materializes descriptor-only
        Assert.Equal(21, target.Num);

        mid.SetValue(ChainHost.NumProperty, 99);      // the abandoned intermediate must be unsubscribed
        Assert.Equal(21, target.Num);

        other.SetValue(ChainHost.NumProperty, 34);    // the new intermediate must be live
        Assert.Equal(34, target.Num);
    }

    private sealed class ChainHost : UIObject
    {
        public static readonly StyledProperty<ChainHost?> NextProperty =
            UIProperty.Register<ChainHost, ChainHost?>("Next");

        public static readonly StyledProperty<int> NumProperty =
            UIProperty.Register<ChainHost, int>("Num");
    }

    [Fact] // B193 — For(styled, Default) keeps its setter for a writable source: Default resolves per
    // the TARGET property (BindsTwoWayByDefault), which the factory cannot see — so predicting the
    // mode source-side would silently strip write-back (the rework regression this pins against).
    public void B193_ForStyled_DefaultMode_TargetTwoWayByDefault_WritesBack()
    {
        var src = new BindWidget { Flag = false };
        var target = new BindWidget();
        target.SetBinding(BindWidget.FlagProperty, CompiledBinding.For(BindWidget.FlagProperty, source: src));

        target.Flag = true; // Flag is BindsTwoWayByDefault on the TARGET ⇒ effective TwoWay ⇒ setter must exist
        Assert.True(src.GetValue(BindWidget.FlagProperty));
    }

    [Fact] // B194 — For(getter-only DirectProperty, explicit TwoWay) degrades at wire (BD10/B152),
    // never throws at build: the engine-wide null-setter contract, one lane, one behavior.
    public void B194_ForDirect_ReadOnly_TwoWay_DegradesInsteadOfThrowing()
    {
        var descriptor = CompiledBinding.For(Cursorial.UI.Controls.MenuItem.HasItemsProperty, mode: BindingMode.TwoWay);
        Assert.Null(descriptor.Setter);

        var target = new BindWidget();
        target.SetBinding(BindWidget.FlagProperty, descriptor); // wires OneWay-degraded, no throw
    }

    [Fact] // B195 — a builder chain with an accessor-less INTERIOR step is rejected at Build() (it
    // would silently kill subscription below it); the closure-carrying Step overload satisfies it,
    // and an accessor-less LEAF stays legal (the typed getter reads the leaf).
    public void B195_Builder_InteriorStepNeedsAccessor()
    {
        var builder = CompiledBinding.Build((Vm m) => m.Name!.Length)
                                     .Step(nameof(Vm.Name))       // interior, no accessor
                                     .Step(nameof(string.Length));
        Assert.Throws<InvalidOperationException>(() => builder.Build());

        var ok = CompiledBinding.Build((Vm m) => m.Name!.Length)
                                .Step(nameof(Vm.Name), static o => o is Vm m ? m.Name : null)
                                .Step(nameof(string.Length))
                                .Build();
        Assert.Equal(2, ok.Steps.Length);
    }

    [Fact] // B196 — an explicit-null fallback (BindingBase.NullValue) survives For(): resolved ONCE
    // to null (set), never double-resolved into UnsetValue (unset).
    public void B196_ForStyled_NullValueFallback_SurvivesSingleResolve()
    {
        var descriptor = CompiledBinding.For(BindWidget.TextProperty, source: new BindWidget(),
                                             fallbackValue: BindingBase.NullValue);
        Assert.Null(descriptor.FallbackValue);
        Assert.NotEqual(UIProperty.UnsetValue, descriptor.FallbackValue);
    }

    [Fact] // B197 — TemplateBinding.From enforces BD15 exactly like the runtime descriptor: one-way
    // only, no UpdateSourceTrigger, and the produced descriptor is FORCED OneWay with a null setter.
    public void B197_TemplateBindingFrom_EnforcesBD15()
    {
        Assert.Throws<InvalidOperationException>(
            () => TemplateBinding.From(BindWidget.FlagProperty, mode: BindingMode.TwoWay));
        Assert.Throws<InvalidOperationException>(
            () => TemplateBinding.From(BindWidget.FlagProperty, updateSourceTrigger: UpdateSourceTrigger.Explicit));

        var descriptor = TemplateBinding.From(BindWidget.FlagProperty); // Flag is BindsTwoWayByDefault — must NOT leak a setter
        Assert.Equal(BindingMode.OneWay, descriptor.Mode);
        Assert.Null(descriptor.Setter);
        Assert.Equal(RelativeSource.TemplatedParent, descriptor.RelativeSource);
    }

    private sealed class TimesTen : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int i ? i * 10 : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int i ? i / 10 : value;
    }
}
