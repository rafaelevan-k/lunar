using System.ComponentModel;
using System.Windows.Input;

namespace Lunar.Desktop.Shell;

/// <summary>
/// A command that runs an asynchronous operation and never lets its failure escape (the one guarded <c>async void</c>
/// this needs is <see cref="Execute"/>, as WPF requires). While the operation runs the command cannot be executed again
/// and <see cref="IsRunning"/> is true; cancellation is silent; any other exception is handed to <c>onFault</c> instead of
/// crashing the app. Create and use it on the UI thread.
/// </summary>
public sealed class AsyncRelayCommand : ICommand, INotifyPropertyChanged
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onFault;
    private readonly CancellationToken _lifetime;
    private bool _isRunning;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onFault = null,
        CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _onFault = onFault;
        _lifetime = lifetime;
    }

    public event EventHandler? CanExecuteChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value)
                return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            NotifyCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_isRunning && !_lifetime.IsCancellationRequested && (_canExecute?.Invoke() ?? true);

    // async void is limited to this ICommand entry point and every path inside is guarded.
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        IsRunning = true;
        try
        {
            await _execute(_lifetime);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by shutdown or by the operation itself: nothing to report.
        }
        catch (Exception ex)
        {
            _onFault?.Invoke(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
