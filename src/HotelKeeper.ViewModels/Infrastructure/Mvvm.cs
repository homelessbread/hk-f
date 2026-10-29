using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HotelKeeper.Data;

namespace HotelKeeper.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Synchronous command. Call <see cref="RaiseCanExecuteChanged"/> when the state it depends on changes.</summary>
public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public RelayCommand(Action run) : this(_ => run()) { }
    public bool CanExecute(object? parameter) => can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => run(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command: ignores clicks while it is running (no double submits), and routes every failure to
/// <paramref name="onError"/> so the user always gets a message instead of a crash.
/// </summary>
public sealed class AsyncCommand(Func<object?, Task> run, Action<Exception> onError, Func<object?, bool>? can = null) : ICommand
{
    bool running;
    public event EventHandler? CanExecuteChanged;
    public AsyncCommand(Func<Task> run, Action<Exception> onError, Func<bool>? can = null)
        : this(_ => run(), onError, can is null ? null : _ => can()) { }
    public bool CanExecute(object? parameter) => !running && (can?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter)
    {
        if (running) return;
        running = true; RaiseCanExecuteChanged();
        try { await run(parameter); }
        catch (Exception ex) { onError(ex); }
        finally { running = false; RaiseCanExecuteChanged(); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public static class ErrorText
{
    /// <summary>Business-rule messages are shown as they are; anything unexpected is summarised so no stack trace or SQL reaches the user.</summary>
    public static string For(Exception ex) => ex switch
    {
        DomainException d => d.Message,
        System.Data.Common.DbException db => "Database error: " + db.Message + " (check the connection string in appsettings.local.json and that MySQL is running).",
        _ => "Something went wrong: " + ex.Message
    };
}
