using System.Collections.ObjectModel;
using System.Windows.Input;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>One link in the sidebar. The page is created lazily the first time it is opened.</summary>
public sealed class NavItemVm : ObservableObject
{
    bool selected, visible = true; int badge;
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required Func<PageViewModel> Create { get; init; }
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public bool IsVisible { get => visible; set => Set(ref visible, value); }
    public int Badge { get => badge; set { Set(ref badge, value); Raise(nameof(HasBadge)); } }
    public bool HasBadge => badge > 0;
    public ICommand Command { get; set; } = null!;
}

public sealed class NavGroupVm : ObservableObject
{
    bool expanded = true, visible = true;
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required IReadOnlyList<NavItemVm> Items { get; init; }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public bool IsVisible { get => visible; set => Set(ref visible, value); }
    public bool IsActive => Items.Any(i => i.IsSelected);
    public void RaiseActive() => Raise(nameof(IsActive));
    public ICommand ToggleCommand => new RelayCommand(() => IsExpanded = !IsExpanded);
    /// <summary>Opens the first page of the group (used by the icon rail).</summary>
    public ICommand OpenCommand { get; set; } = null!;
}

public sealed record NavDefinition(string Title, string Icon, IReadOnlyList<(string key, string title, string icon, Func<PageViewModel> create)> Items);

/// <summary>
/// The window-level view model for one role: sidebar, icon rail, current page, notifications and sign-out.
/// Admin / Receptionist / Housekeeper / Guest differ only in the <see cref="NavDefinition"/>s they pass in.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    readonly PageContext ctx;
    PageViewModel? current; string navSearch = ""; int unread; string? bootError;
    string themeIcon = "sun";
    public string ThemeIcon { get => themeIcon; set => Set(ref themeIcon, value); }
    public ICommand ToggleThemeCommand { get; set; } = null!;
    readonly Dictionary<string, PageViewModel> cache = new();
    CancellationTokenSource? poll;

    public ShellViewModel(PageContext ctx, string appName, IEnumerable<NavDefinition> groups)
    {
        this.ctx = ctx; AppName = appName;
        ctx.Navigate = NavigateAsync;
        ctx.RefreshBadges = RefreshUnreadAsync;
        ctx.ChangePassword = ChangePasswordAsync;

        foreach (var g in groups)
        {
            var items = g.Items.Select(i =>
            {
                var vm = new NavItemVm { Key = i.key, Title = i.title, Icon = i.icon, Create = i.create };
                vm.Command = new AsyncCommand(() => NavigateAsync(vm.Key), Report);
                return vm;
            }).ToList();
            var group = new NavGroupVm { Title = g.Title, Icon = g.Icon, Items = items };
            group.OpenCommand = new AsyncCommand(() => NavigateAsync(items[0].Key), Report);
            Groups.Add(group);
        }
        RailGroups = Groups;
        SignOutCommand = new RelayCommand(() => SignedOut?.Invoke());
        ChangePasswordCommand = new AsyncCommand(ChangePasswordAsync, Report);
        NotificationsCommand = new AsyncCommand(() => NavigateAsync("notifications"), Report);
        SettingsCommand = new AsyncCommand(() => NavigateAsync(Groups.SelectMany(g => g.Items).Any(i => i.Key == "settings") ? "settings" : "account"), Report);
    }

    public string AppName { get; }
    public ObservableCollection<NavGroupVm> Groups { get; } = new();
    public IReadOnlyList<NavGroupVm> RailGroups { get; }
    public Session Session => ctx.Session;
    public string UserName => Session.FullName;
    public string RoleLabel => Session.Role == Roles.Guest ? "Guest" : Session.Role;
    public string Subtitle => string.IsNullOrWhiteSpace(Session.Email) ? RoleLabel : Session.Email;
    public string Initials => string.Concat(Session.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
    public PageViewModel? Current { get => current; private set => Set(ref current, value); }
    public int UnreadCount { get => unread; private set { Set(ref unread, value); Raise(nameof(HasUnread)); } }
    public bool HasUnread => unread > 0;
    public string? BootError { get => bootError; set { Set(ref bootError, value); Raise(nameof(HasBootError)); } }
    public bool HasBootError => !string.IsNullOrEmpty(bootError);
    public ICommand SignOutCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand NotificationsCommand { get; }
    public ICommand SettingsCommand { get; }
    public event Action? SignedOut;

    public string NavSearch
    {
        get => navSearch;
        set
        {
            if (!Set(ref navSearch, value)) return;
            var q = value.Trim();
            foreach (var g in Groups)
            {
                foreach (var i in g.Items) i.IsVisible = q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
                g.IsVisible = g.Items.Any(i => i.IsVisible);
                if (q.Length > 0) g.IsExpanded = true;
            }
        }
    }

    void Report(Exception ex) => BootError = ErrorText.For(ex);

    public async Task StartAsync()
    {
        if (Session.MustChangePassword) await ChangePasswordAsync(forced: true);
        if (Session.MustChangePassword) { SignedOut?.Invoke(); return; }
        var first = Groups.SelectMany(g => g.Items).First();
        await NavigateAsync(first.Key);
        await RefreshUnreadAsync();
        poll = new CancellationTokenSource();
        _ = PollAsync(poll.Token);
    }

    public void Stop() { poll?.Cancel(); poll = null; }

    async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(ct)) await RefreshUnreadAsync(); } catch (OperationCanceledException) { }
    }

    public async Task RefreshUnreadAsync()
    {
        try { UnreadCount = await new NotificationService(Session).UnreadCountAsync(); } catch { /* the badge is a convenience; the page shows real errors */ }
    }

    public async Task NavigateAsync(string key)
    {
        BootError = null;
        var item = Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Key == key);
        PageViewModel page;
        if (item != null)
        {
            if (!cache.TryGetValue(key, out page!)) cache[key] = page = item.Create();
        }
        else if (key == "notifications") { if (!cache.TryGetValue(key, out page!)) cache[key] = page = SharedPages.Notifications(ctx); }
        else if (key == "account") { if (!cache.TryGetValue(key, out page!)) cache[key] = page = SharedPages.Account(ctx); }
        else return;

        foreach (var g in Groups) { foreach (var i in g.Items) i.IsSelected = i.Key == key; g.RaiseActive(); }
        Current = page;
        await page.ReloadAsync();
    }

    public async Task ChangePasswordAsync() => await ChangePasswordAsync(false);

    async Task ChangePasswordAsync(bool forced)
    {
        while (true)
        {
            var form = await ctx.Dialogs.FormAsync(forced ? "Choose a new password" : "Change password",
                forced ? "This account uses a temporary password. Choose your own before continuing." : null,
                new[]
                {
                    FormField.Pass("current", "Current password"),
                    FormField.Pass("next", "New password", hint: "10+ characters with upper case, lower case and a digit."),
                    FormField.Pass("again", "Repeat new password"),
                }, "Change password");
            if (form is null) return;
            if (form.Str("next") != form.Str("again")) { await ctx.Dialogs.MessageAsync("Passwords do not match", "Type the new password twice exactly the same.", true); continue; }
            try
            {
                await AuthService.ChangePasswordAsync(Session, form.Str("current"), form.Str("next"));
                await ctx.Dialogs.MessageAsync("Password changed", "Your password has been updated.");
                return;
            }
            catch (Exception ex) { await ctx.Dialogs.MessageAsync("Could not change password", ErrorText.For(ex), true); }
        }
    }
}
