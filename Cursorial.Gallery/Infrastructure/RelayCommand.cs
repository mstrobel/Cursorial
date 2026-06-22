using System.Windows.Input;

namespace Cursorial.Gallery.Infrastructure;

/// <summary>A minimal <see cref="ICommand"/> over delegates (the <c>Button.Command</c> target the gallery's
/// toggle/cycle buttons bind to). Parameterless — the gallery's commands act on their owning view-model.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Re-queries <see cref="CanExecute"/> (raise after state that gates the command changes).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A minimal <see cref="ICommand"/> over delegates (the <c>Button.Command</c> target the gallery's
/// toggle/cycle buttons bind to). Parameterless — the gallery's commands act on their owning view-model.</summary>
public sealed class RelayCommand<TParameter>(Action<TParameter> execute, Func<TParameter, bool>? canExecute = null) : ICommand
{
    private readonly Action<TParameter> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke((TParameter)parameter!) ?? true;

    public void Execute(object? parameter) => _execute((TParameter)parameter!);

    /// <summary>Re-queries <see cref="CanExecute"/> (raise after state that gates the command changes).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
