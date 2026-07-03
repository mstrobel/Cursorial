// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The untyped observer lane (ledger A10) — the third synchronous notification channel, after typed
/// observers and before the virtual <c>OnPropertyChanged</c>. Values arrive as boxed copies; the
/// XAML/diagnostics consumers this lane exists for don't sit on hot paths, so the boxing is
/// accepted there and only there. Subscribe via
/// <see cref="UIObject.AddObserver(UIProperty, IUntypedValueObserver)"/>.
/// </summary>
public interface IUntypedValueObserver
{
    /// <summary>
    /// Called synchronously after the property's effective value changed, with boxed copies of the
    /// old and new values and the change's <see cref="BindingPriority"/> (A10/A11 — same priority
    /// semantics as the typed channel).
    /// </summary>
    void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority);
}
