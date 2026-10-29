using System.Windows.Input;
using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>Sign-in screen: prepares the database on first run, signs in, and lets new guests create an account.</summary>
public sealed class LoginViewModel : ObservableObject
{
    readonly IDialogService dialogs;
    string username = "", error = "", info = "", busyText = "";
    bool busy, ready;

    public LoginViewModel(IDialogService dialogs)
    {
        this.dialogs = dialogs;
        SignInCommand = new AsyncCommand(SignInAsync, ex => Fail(ErrorText.For(ex)), () => ready);
        RegisterCommand = new AsyncCommand(RegisterAsync, ex => Fail(ErrorText.For(ex)), () => ready);
        RetryCommand = new AsyncCommand(InitializeAsync, ex => Fail(ErrorText.For(ex)));
    }

    public string Username { get => username; set => Set(ref username, value); }
    /// <summary>Set by the view from the PasswordBox just before sign-in (passwords are never bound as plain properties).</summary>
    public Func<string> PasswordProvider { get; set; } = () => "";
    public Action ClearPassword { get; set; } = () => { };
    public string ErrorMessage { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); } }
    public string InfoMessage { get => info; private set { Set(ref info, value); Raise(nameof(HasInfo)); } }
    public bool HasError => error.Length > 0;
    public bool HasInfo => info.Length > 0;
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public string BusyText { get => busyText; private set => Set(ref busyText, value); }
    public bool IsReady { get => ready; private set { Set(ref ready, value); Raise(nameof(NeedsRetry)); } }
    public bool NeedsRetry => !ready && !busy && HasError;
    public ICommand SignInCommand { get; }
    public ICommand RegisterCommand { get; }
    public ICommand RetryCommand { get; }

    /// <summary>Raised with the new session after a successful sign-in.</summary>
    public event Action<Session>? SignedIn;

    void Fail(string message) { InfoMessage = ""; ErrorMessage = message; Raise(nameof(NeedsRetry)); }

    public async Task InitializeAsync()
    {
        IsBusy = true; BusyText = "Connecting to the database…"; ErrorMessage = "";
        try { await DatabaseSetup.EnsureAsync(); IsReady = true; }
        catch (Exception ex) { IsReady = false; Fail("Could not set up the database. " + ErrorText.For(ex) + " Check appsettings.local.json and that MySQL is running, then retry."); }
        finally { IsBusy = false; Raise(nameof(NeedsRetry)); (SignInCommand as AsyncCommand)?.RaiseCanExecuteChanged(); (RegisterCommand as AsyncCommand)?.RaiseCanExecuteChanged(); }
    }

    async Task SignInAsync()
    {
        ErrorMessage = ""; InfoMessage = "";
        var password = PasswordProvider();
        IsBusy = true; BusyText = "Signing in…";
        try
        {
            var session = await AuthService.LoginAsync(Username, password);
            ClearPassword();
            SignedIn?.Invoke(session);
        }
        finally { IsBusy = false; }
    }

    async Task RegisterAsync()
    {
        ErrorMessage = ""; InfoMessage = "";
        var f = await dialogs.FormAsync("Create a guest account", "Create an account to book rooms and manage your stay.", new[]
        {
            FormField.Txt("name", "Full name", required: true), FormField.Txt("email", "Email"), FormField.Txt("phone", "Phone", hint: "Phone or email is required."),
            FormField.Txt("user", "Username", required: true), FormField.Pass("pw", "Password", hint: "10+ characters with upper case, lower case and a digit."), FormField.Pass("pw2", "Repeat password"),
        }, "Create account");
        if (f is null) return;
        if (f.Str("pw") != f.Str("pw2")) { Fail("The two passwords do not match."); return; }
        IsBusy = true; BusyText = "Creating your account…";
        try
        {
            await AuthService.RegisterGuestAsync(f.Str("name"), f.Str("email"), f.Str("phone"), f.Str("user"), f.Str("pw"));
            Username = f.Str("user"); InfoMessage = "Account created. Sign in with your new username and password.";
        }
        finally { IsBusy = false; }
    }
}
