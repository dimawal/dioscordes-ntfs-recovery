using System.Windows.Input;

namespace NtfsRecovery.Gui;

/// <summary>Minimal ICommand implementation -- no external MVVM library, to keep dependencies to a minimum.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute is null || canExecute(parameter);

    public void Execute(object? parameter) => execute(parameter);
}
