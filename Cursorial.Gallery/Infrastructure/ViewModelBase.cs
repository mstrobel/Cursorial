using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cursorial.Gallery.Infrastructure;

/// <summary>The MVVM base: <see cref="INotifyPropertyChanged"/> with a <see cref="Set{T}"/> helper that raises only
/// on a real change (the gallery's bindings are live, so a no-op set must not pulse).</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns <paramref name="field"/> and raises <see cref="PropertyChanged"/> on a change; returns whether it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="name"/> (the caller member by default).</summary>
    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
