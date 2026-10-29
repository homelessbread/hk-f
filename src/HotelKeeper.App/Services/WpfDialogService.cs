using System.Windows;
using HotelKeeper.App.Views.Shared;
using HotelKeeper.ViewModels;

namespace HotelKeeper.App.Services;


public sealed class WpfDialogService : IDialogService
{
    static Window? Owner() => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public Task<FormResult?> FormAsync(string title, string? message, IReadOnlyList<FormField> fields, string okText = "Save", bool danger = false)
    {
        var vm = new FormDialogViewModel(title, message, fields, okText, danger);
        var win = new FormDialog { DataContext = vm, Owner = Owner() };
        return Task.FromResult(win.ShowDialog() == true ? vm.Result : null);
    }

    public Task<bool> ConfirmAsync(string title, string message, string okText = "Confirm", bool danger = false)
    {
        var win = new MessageDialog(title, message, okText, showCancel: true, isError: false, danger: danger, lines: null) { Owner = Owner() };
        return Task.FromResult(win.ShowDialog() == true);
    }

    public Task MessageAsync(string title, string message, bool isError = false)
    {
        var win = new MessageDialog(title, message, "OK", showCancel: false, isError: isError, danger: false, lines: null) { Owner = Owner() };
        win.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowLinesAsync(string title, string? message, IEnumerable<string> lines)
    {
        var win = new MessageDialog(title, message ?? "", "Close", showCancel: false, isError: false, danger: false, lines: lines.ToList()) { Owner = Owner() };
        win.ShowDialog();
        return Task.CompletedTask;
    }
}
