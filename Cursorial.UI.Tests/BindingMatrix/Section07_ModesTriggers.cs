using System.Globalization;
using Cursorial.UI;
using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §7 — modes, triggers, and target → source write-back (B71–B92b; the
/// compiled-lane twin B92c lives in <c>Section12_CompiledLane</c>).</summary>
public class Section07_ModesTriggers
{
    public Section07_ModesTriggers()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B071_OneWay_NoWriteBack()
    {
        var vm = new Vm { Name = "x" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.OneWay });

        vm.Name = "x"; // already x
        Assert.Equal("x", w.Text);

        w.Text = "y"; // target write does NOT reach the source
        Assert.Equal("x", vm.Name);
    }

    [Fact]
    public void B072_TwoWay_PropertyChanged_WritesBack()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        w.Text = "z";
        Assert.Equal("z", vm.Name);

        vm.Name = "w";
        Assert.Equal("w", w.Text);
    }

    [Fact]
    public void B073_OneTime_NoPathSubscription()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.OneTime });
        Assert.Equal("a", w.Text);

        vm.Name = "b"; // not seen (no subscription)
        Assert.Equal("a", w.Text);
    }

    [Fact]
    public void B074_OneTime_ReEvaluatesOnDataContextChange()
    {
        var vm = new Vm { Name = "a" };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);
        a.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.OneTime });
        Assert.Equal("a", a.Text);

        root.DataContext = new Vm { Name = "c" };
        Assert.Equal("c", a.Text); // OneTime re-evaluates per DataContext (BD3)
    }

    [Fact]
    public void B075_OneWayToSource_InitialSync()
    {
        var vm = new Vm { Name = "stale" };
        var w = new BindWidget { DataContext = vm, Text = "init" };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.OneWayToSource });
        Assert.Equal("init", vm.Name); // initial target → source push
    }

    [Fact]
    public void B076_OneWayToSource_TargetWriteReachesSource()
    {
        var vm = new Vm { Name = "stale" };
        var w = new BindWidget { DataContext = vm, Text = "init" };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.OneWayToSource });

        w.Text = "edit";
        Assert.Equal("edit", vm.Name);

        vm.Name = "x"; // does NOT flow to the target (no path subscription)
        Assert.Equal("edit", w.Text);
    }

    [Fact]
    public void B077_OneWayToSource_PerWriteReResolve()
    {
        var s1 = new Vm();
        var s2 = new Vm();
        var vm = new Vm { Sub = s1 };
        var w = new BindWidget { DataContext = vm, Text = "init" };
        w.SetBinding(BindWidget.TextProperty, new Binding("Sub.Name") { Mode = BindingMode.OneWayToSource });

        vm.Sub = s2; // swap intermediate
        w.Text = "k";
        Assert.Equal("k", s2.Name);    // the write re-resolved the chain and landed on s2
        Assert.NotEqual("k", s1.Name); // never the stale s1
    }

    [Fact]
    public void B078_BindsTwoWayByDefault_ResolvesTwoWay()
    {
        var vm = new Vm { IsDirty = false };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.FlagProperty, new Binding("IsDirty") { Mode = BindingMode.Default });
        Assert.Equal(BindingMode.TwoWay, expr.EffectiveMode);

        w.Flag = true;
        Assert.True(vm.IsDirty); // write-back
    }

    [Fact]
    public void B079_NoBindsTwoWay_ResolvesOneWay()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.Default });
        Assert.Equal(BindingMode.OneWay, expr.EffectiveMode);
    }

    [Fact]
    public void B080_ReadOnlyLeaf_DegradesToOneWay()
    {
        var vm = new Vm();
        vm.SetReadOnlyAge(5);
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = w.SetBinding(BindWidget.NumProperty, new Binding("ReadOnlyAge") { Mode = BindingMode.TwoWay });

        w.Num = 9; // source-direction write dropped (read-only leaf)
        Assert.Equal(5, vm.ReadOnlyAge);
        Assert.Equal(BindingMode.OneWay, expr.EffectiveMode);
    }

    [Fact]
    public void B081_ReadOnlyLeafBecomesWritable_OnRewire_ReEnablesWriteBack()
    {
        var vm = new LeafSwapVm { Leaf = new ReadOnlyLeaf(5) };
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = w.SetBinding(BindWidget.NumProperty, new Binding("Leaf.Value") { Mode = BindingMode.TwoWay });
        Assert.Equal(5, w.Num);

        w.Num = 9; // read-only leaf ⇒ degrade, no write
        Assert.Equal(BindingMode.OneWay, expr.EffectiveMode);

        // Swap the intermediate to a writable-leaf type: write-back re-enables (BD10 per-rewire).
        vm.Leaf = new WritableLeaf { Value = 1 };
        Assert.Equal(1, w.Num);
        Assert.Equal(BindingMode.TwoWay, expr.EffectiveMode);

        w.Num = 42;
        Assert.Equal(42, ((WritableLeaf)vm.Leaf!).Value);
    }

    [Fact]
    public void B092_AnimationHandleDisposal_ResurfacesPushedBase_NoWriteBack()
    {
        // The asynchronous-echo discriminator (BD8 step 2): when an animation handle on a two-way-bound
        // property disposes, the pushed base resurfaces at LocalValue. The resurfaced value equals
        // _lastPushedValue, so write-back is skipped — the source is untouched.
        var vm = new Vm { Name = "x" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.TwoWay });

        w.Text = "pushed"; // a genuine edit rounds through; the engine records "pushed" as _lastPushedValue
        Assert.Equal("pushed", vm.Name);

        var sourceWrites = 0;
        vm.PropertyChanged += (_, _) => sourceWrites++;

        var handle = w.BeginAnimation(BindWidget.TextProperty);
        handle.SetValue("animated"); // Animation lane masks the base
        handle.Dispose();            // "pushed" resurfaces at LocalValue — equals _lastPushedValue

        Assert.Equal("pushed", vm.Name);
        Assert.Equal(0, sourceWrites); // the resurfaced echo was NOT re-written to the source
    }

    [Fact]
    public void B092a_EchoSuppression_UsesPropertyComparer_NotObjectEquals()
    {
        // BD8 step 2 pins the asynchronous-echo discriminator to the TARGET PROPERTY's comparer, not
        // object.Equals (design doc §6.6 / spec §3.6 step 2). Cmp has Comparer = OrdinalIgnoreCase.
        // A case-only resurfaced own value must be SUPPRESSED — object.Equals would let it spuriously
        // round-trip a case-only edit back to the VM.
        var vm = new Vm { Name = "abc" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.CmpProperty, new Binding("Name") { Mode = BindingMode.TwoWay });
        Assert.Equal("abc", w.Cmp);

        // The VM changes to a case-variant. The binding pushes "ABC" → _lastPushedValue == "ABC", but
        // the store's OrdinalIgnoreCase comparer treats it as unchanged, so the effective stays "abc".
        vm.Name = "ABC";

        var sourceWrites = 0;
        vm.PropertyChanged += (_, _) => sourceWrites++;

        // Resurface the LocalValue base ("abc") via an animation handle round-trip; this fires
        // OnTargetValueChanged at LocalValue. Store-current "abc" vs _lastPushedValue "ABC" differ by
        // case only: the comparer suppresses (no write); object.Equals would have written "abc" back.
        var handle = w.BeginAnimation(BindWidget.CmpProperty);
        handle.SetValue("zzz");
        handle.Dispose();

        Assert.Equal(0, sourceWrites);   // the case-only resurfaced echo was NOT written back
        Assert.Equal("ABC", vm.Name);    // the VM is untouched
    }

    [Fact]
    public void B092b_WriteBack_ClearsEchoComparand_NonNotifyingSource()
    {
        // BD8 step 2 amendment: the echo comparand is cleared on every target → source write-back and
        // re-armed by the next forward push. A NON-notifying source (ladder rung 3, one-time read) has
        // no post-write re-read to re-stamp it, so a stale comparand would swallow any target edit that
        // RETURNS to the source's original value — the checkbox toggle-on/toggle-off case.
        var poco = new PlainHolder { Note = "orig" };
        var w = new BindWidget { DataContext = poco };
        w.SetBinding(BindWidget.TextProperty, new Binding("Note") { Mode = BindingMode.TwoWay });
        Assert.Equal("orig", w.Text); // the initial forward push arms the comparand with "orig"

        w.Text = "edited";
        Assert.Equal("edited", poco.Note); // a genuine edit writes back (and disarms the comparand)

        w.Text = "orig"; // returns to the initially pushed value — a GENUINE edit, not an echo
        Assert.Equal("orig", poco.Note); // stale-comparand suppression would have left "edited"
    }

    [Fact]
    public void B082_Explicit_FlushesOnUpdateSource()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });

        w.Text = "p";
        Assert.Equal("a", vm.Name); // nothing yet

        expr.UpdateSource();
        Assert.Equal("p", vm.Name);
    }

    [Fact]
    public void B083_ConvertBack_RunsOnTargetWrite()
    {
        var vm = new Vm { Age = 0 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.NumProperty, new Binding("Age")
            { Mode = BindingMode.TwoWay, Converter = new DoubleConverter() });

        w.Num = 9;
        Assert.Equal(18, vm.Age); // ConvertBack doubled
    }

    [Fact]
    public void B084_ConvertBackFails_NoWrite()
    {
        var vm = new Vm { Age = 7 };
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.SetBinding(BindWidget.NumProperty, new Binding("Age")
            { Mode = BindingMode.TwoWay, Converter = new FailingBackConverter() });

        w.Num = 9;
        Assert.Equal(7, vm.Age); // no write
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.ConvertBackFailed);
    }

    [Fact]
    public void B085_TargetNullValue_ReverseMapping()
    {
        var vm = new Vm { Name = "x" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, TargetNullValue = "<none>" });

        w.Text = "<none>";
        Assert.Null(vm.Name); // reverse mapping → null
    }

    [Fact]
    public void B086_StringFormat_ExactlyZero_ReverseParse()
    {
        var vm = new Vm { Age = 0 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Age")
            { Mode = BindingMode.TwoWay, StringFormat = "{0}" });

        w.Text = "12";
        Assert.Equal(12, vm.Age); // reverse parse applies
    }

    [Fact]
    public void B087_StringFormat_Composite_NoReverse()
    {
        var vm = new Vm { Age = 0 };
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.SetBinding(BindWidget.TextProperty, new Binding("Age")
            { Mode = BindingMode.TwoWay, StringFormat = "Age: {0}" });

        w.Text = "Age: 12";
        Assert.Equal(0, vm.Age); // composite ⇒ no write
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.ConvertBackFailed);
    }

    [Fact]
    public void B088_NoConverter_TypeGap_ConversionLadder()
    {
        var vm = new Vm { Age = 0 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Age") { Mode = BindingMode.TwoWay });

        w.Text = "5";
        Assert.Equal(5, vm.Age);

        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.Text = "notanumber";
        Assert.Equal(5, vm.Age); // non-numeric ⇒ no write
    }

    [Fact]
    public void B089_SourceNormalizesDuringWrite_Converges()
    {
        var vm = new ClampingVm();
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.NumProperty, new Binding("Value")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        w.Num = 5; // the VM clamps 5 → 3
        Assert.Equal(3, vm.Value);
        Assert.Equal(3, w.Num); // one coalesced post-write re-read converges the target
    }

    [Fact]
    public void B090_SetCurrentValue_PreservesTwoWayBinding()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.TwoWay });

        w.SetCurrentValue(BindWidget.TextProperty, "typed"); // the TextBox keystroke model
        Assert.Equal("typed", vm.Name); // write-back

        vm.Name = "ext";
        Assert.Equal("ext", w.Text); // the binding survives
    }

    [Fact]
    public void B091_SetValue_TransientOverride_ClearValueKills()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Mode = BindingMode.TwoWay });

        w.Text = "lv"; // plain SetValue at LocalValue → write-back; the binding survives
        Assert.Equal("lv", vm.Name);

        vm.Name = "later";
        Assert.Equal("later", w.Text); // the binding re-wins on the next produce

        w.ClearValue(BindWidget.TextProperty); // the documented kill
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));
    }

    private sealed class DoubleConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int i ? i * 2 : UIProperty.UnsetValue;
    }

    private sealed class FailingBackConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => UIProperty.UnsetValue;
    }
}

/// <summary>A viewmodel that clamps <c>Value</c> to ≤ 3 and re-raises INPC during the set (B89).</summary>
public sealed class ClampingVm : System.ComponentModel.INotifyPropertyChanged
{
    private int _value;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Min(value, 3);
            if (_value == clamped)
                return;
            _value = clamped;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
        }
    }
}
