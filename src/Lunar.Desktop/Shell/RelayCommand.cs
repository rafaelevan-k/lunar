using System.Windows.Input;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Minimal synchronous command for ViewModels. <see cref="CanExecuteChanged"/> is raised only by
/// <see cref="NotifyCanExecuteChanged"/>, always on the thread that owns the ViewModel.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            _execute();
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
