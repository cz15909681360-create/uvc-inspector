using System.Windows.Input;

namespace UvcInspector;

public sealed class UiCommand(Func<Task> execute, Func<bool>? enabled = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(); }
}
