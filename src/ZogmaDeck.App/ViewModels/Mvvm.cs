using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ZogmaDeck.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Commande asynchrone : les exceptions sont capturées et transmises au gestionnaire d'erreurs.</summary>
public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception>? onError = null) : ICommand
{
    private bool _busy;

    public AsyncCommand(Func<Task> execute, Action<Exception>? onError = null) : this(_ => execute(), onError) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_busy;

    public async void Execute(object? parameter)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Error("Commande en échec", ex);
            onError?.Invoke(ex);
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Couple valeur / libellé pour les listes déroulantes.</summary>
public sealed record Option(object? Value, string Label)
{
    public override string ToString() => Label;
}
