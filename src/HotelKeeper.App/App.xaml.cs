using System.Windows;
using HotelKeeper.App.Services;
using HotelKeeper.App.Views;
using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.ViewModels;

namespace HotelKeeper.App;

public partial class App : Application
{
    readonly WpfDialogService dialogs = new();
    bool switching;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("Something unexpected went wrong:\n\n" + args.Exception.Message, "Hotel Keeper", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        try { AppConfig.Load(AppContext.BaseDirectory); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hotel Keeper - configuration", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        ThemeManager.Initialize();
        ShowLogin();
    }

    void ShowLogin()
    {
        var vm = new LoginViewModel(dialogs);
        var window = new LoginWindow { DataContext = vm };
        vm.SignedIn += session =>
        {
            switching = true;
            OpenShell(session);
            window.Close();
            switching = false;
        };
        window.Closed += (_, _) => { if (!switching) Shutdown(); };
        window.Show();
        _ = vm.InitializeAsync();
    }

    void OpenShell(Session session)
    {
        var ctx = new PageContext(session, dialogs);
        var shell = RoleShells.Create(ctx, "Hotel Keeper");
        shell.ThemeIcon = ThemeManager.IsDark ? "sun" : "moon";
        shell.ToggleThemeCommand = new RelayCommand(() =>
        {
            ThemeManager.ToggleTheme();
            shell.ThemeIcon = ThemeManager.IsDark ? "sun" : "moon";
        });
        var window = new ShellWindow { DataContext = shell, Title = $"Hotel Keeper - {shell.RoleLabel}" };
        shell.SignedOut += () =>
        {
            switching = true;
            shell.Stop();
            window.Close();
            ShowLogin();
            switching = false;
        };
        window.Closed += (_, _) => { shell.Stop(); if (!switching) Shutdown(); };
        window.Show();
        _ = shell.StartAsync();
    }
}
