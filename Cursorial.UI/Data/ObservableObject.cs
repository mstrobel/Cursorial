using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cursorial.UI.Data;

/// <summary>
/// A minimal <see cref="INotifyPropertyChanged"/> base for view-models: field-set-and-notify via
/// <see cref="SetProperty{T}"/> plus explicit <see cref="OnPropertyChanged"/> for computed
/// properties. The framework's own view-models (the user-options UI) build on it; applications
/// are welcome to as well — bindings subscribe through the standard INPC rung of the
/// source-notification ladder.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="propertyName"/> (the caller member by default).</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Sets <paramref name="field"/> and raises <see cref="PropertyChanged"/> when the value
    /// actually changed. Returns whether it did.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
