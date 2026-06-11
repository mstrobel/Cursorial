namespace Cursorial.UI;

/// <summary>
/// A typed per-instance property observer — the second of the synchronous notification channels
/// (metadata <c>Changed</c> → <b>typed observers</b> → untyped observers → virtual
/// <c>OnPropertyChanged</c>, design doc §2.3). This is the binding/selector-engine watch surface;
/// there is deliberately no <c>IObservable</c>/Rx shape (§2.6-1). Subscribe via
/// <see cref="UIObject.AddObserver{T}(StyledProperty{T}, IValueObserver{T})"/>; subscription does
/// <b>not</b> replay the current value (matrix PD19) — read <c>GetValue</c> at subscribe time.
/// </summary>
public interface IValueObserver<T>
{
    /// <summary>
    /// Called synchronously after the property's effective value changed. Values are copies
    /// snapshotted before dispatch; <paramref name="priority"/> carries the lane of the new
    /// effective value (ledger A10) — except <c>SetCurrentValue</c>, which reports the replaced
    /// lane (A11).
    /// </summary>
    void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority);

    /// <summary>
    /// The winning-base channel (ledger A20), delivered only to subscriptions made with
    /// <see cref="ObserverOptions.IncludeBaseChanges"/>: fires when the effective <em>base</em> —
    /// the winner among sub-Animation priorities — changes, including under an active animation
    /// (where the ordinary channels stay silent on base writes). <paramref name="isAnimated"/>
    /// reports whether an animation currently holds the property. The default implementation does
    /// nothing, so plain observers ignore the channel.
    /// </summary>
    void OnBaseValueChanged(UIObject source, UIProperty property, T oldBaseValue, T newBaseValue, bool isAnimated)
    {
    }
}
