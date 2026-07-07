using System.ComponentModel;

using Cursorial.UI;

namespace Cursorial.Tests.UI;

// ValueCommandParameter<T> — the value-carrying CheckableCommandParameter (the Actipro pattern): the carrier's own
// contract, independent of any control. Value/PreviewValue/Action raise INotifyPropertyChanged exactly like the
// base's members (raise on change, silent on same-value), the non-generic IValueCommandParameter surface round-trips
// through the typed properties, and the inherited FB-27 checkable state (IsChecked / Override / Release) is intact.
public sealed class ValueCommandParameterTests
{
    [Fact] // the ctor seeds Value (+ the inherited IsChecked); Action defaults to Commit; PreviewValue to default(T)
    public void Ctor_SeedsValue_DefaultsToCommit()
    {
        var param = new ValueCommandParameter<string>("Center", isChecked: true);

        Assert.Equal("Center", param.Value);
        Assert.Equal(true, param.IsChecked);
        Assert.Equal(ValueCommandParameterAction.Commit, param.Action);
        Assert.Null(param.PreviewValue);
        Assert.False(param.Handled); // the FB-27 gate starts released (backward-compatible default)
    }

    [Fact] // Value / PreviewValue / Action raise PropertyChanged on change and stay silent on a same-value write
    public void PropertyChanged_RaisesOnChange_SilentOnSame()
    {
        var param = new ValueCommandParameter<int>(1);
        var raised = new List<string?>();
        param.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        param.Value = 2;
        param.PreviewValue = 3;
        param.Action = ValueCommandParameterAction.Preview;
        Assert.Equal(new[] { nameof(param.Value), nameof(param.PreviewValue), nameof(param.Action) }, raised);

        raised.Clear();
        param.Value = 2;                                    // same value — silent
        param.PreviewValue = 3;                             // same value — silent
        param.Action = ValueCommandParameterAction.Preview; // same value — silent
        Assert.Empty(raised);
    }

    [Fact] // the non-generic IValueCommandParameter surface reads/writes through the typed properties (object? <-> T)
    public void NonGenericSurface_RoundTripsTypedValues()
    {
        var param = new ValueCommandParameter<int>(10);
        IValueCommandParameter untyped = param;

        Assert.Equal(10, untyped.Value);

        untyped.Value = 20;          // the framework-control write path (a control never knows T)
        untyped.PreviewValue = 30;
        untyped.Action = ValueCommandParameterAction.Preview;

        Assert.Equal(20, param.Value);
        Assert.Equal(30, param.PreviewValue);
        Assert.Equal(ValueCommandParameterAction.Preview, param.Action);

        untyped.PreviewValue = null; // clearing the candidate is expressible untyped (T? on the typed surface)
        Assert.Equal(0, param.PreviewValue);
    }

    [Fact] // a wrong-typed non-generic write is a programming error — the cast throws, it never corrupts Value
    public void NonGenericSurface_WrongTypeThrows()
    {
        IValueCommandParameter param = new ValueCommandParameter<int>(1);

        Assert.ThrowsAny<InvalidCastException>(() => param.Value = "not an int");
        Assert.Equal(1, ((ValueCommandParameter<int>)param).Value);
    }

    [Fact] // the inherited FB-27 checkable machinery is untouched: IsChecked raises, Override/Release gate as before
    public void InheritedCheckableState_ComposesUnchanged()
    {
        var param = new ValueCommandParameter<string>("Left");
        var raised = new List<string?>();
        param.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        param.IsChecked = true;
        Assert.Contains(nameof(param.IsChecked), raised);

        param.Override(isChecked: false); // the context gate (grey+force) rides the value parameter unchanged
        Assert.True(param.Handled);
        Assert.Equal(false, param.IsCheckedOverride);

        param.Release();
        Assert.False(param.Handled);
        Assert.Equal(true, param.IsChecked); // the reflected state survives the gate round-trip
    }
}
