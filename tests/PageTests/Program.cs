using System.Windows.Input;
using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;
using HotelKeeper.ViewModels;

// Drives the real view models headlessly against a live database, with a scripted dialog service standing in for the WPF dialogs.
AppConfig.ConnectionString = args[0];
int fails = 0;
void Ok(bool c, string what) { Console.WriteLine((c ? "PASS  " : "FAIL  ") + what); if (!c) fails++; }

var dialogs = new ScriptedDialogs();
await DatabaseSetup.EnsureAsync();

async Task<ShellViewModel> Boot(string user, string pass)
{
    var s = await AuthService.LoginAsync(user, pass);
    s.MustChangePassword = false;               // the forced password change is tested separately
    var shell = RoleShells.Create(new PageContext(s, dialogs), "Hotel Keeper");
    return shell;
}

// 1. every page of every role loads without error
foreach (var (u, p) in new[] { ("admin", "Admin12345"), ("reception", "Reception123"), ("housekeeper", "Housekeeper123") })
{
    var shell = await Boot(u, p);
    var keys = shell.Groups.SelectMany(g => g.Items).Select(i => i.Key).ToList();
    var bad = new List<string>();
    foreach (var k in keys)
    {
        await shell.NavigateAsync(k);
        if (shell.Current!.HasError) bad.Add($"{k}: {shell.Current.ErrorMessage}");
    }
    await shell.NavigateAsync("notifications"); if (shell.Current!.HasError) bad.Add("notifications: " + shell.Current.ErrorMessage);
    Ok(bad.Count == 0, $"{u}: {keys.Count} pages load" + (bad.Count > 0 ? " -> " + string.Join("; ", bad) : ""));
}

// 2. front-desk flow through the pages (Walk-in check-in via guest/rooms context action)
var rec = await Boot("reception", "Reception123");
await rec.NavigateAsync("guests");
var guests = (ListPageViewModel)rec.Current!;
dialogs.Next(f => { f["name"].Text = "Ben Reyes"; f["phone"].Text = "0999"; f["user"].Text = "benreyes"; });
dialogs.Next(null);   // acknowledge the "password shown" lines
Run(guests, "Register guest");
await Task.Delay(1500);
Ok(guests.Items.Cast<GuestRow>().Any(g => g.FullName == "Ben Reyes"), "receptionist registers a guest through the page");
var tempPw = dialogs.LastLines.First(l => l.StartsWith("Temporary password")).Split(':')[1].Trim();

var guestRow = (await new GuestService(rec.Session).SearchAsync("Ben")).First();
var offers = await new RoomService(rec.Session).AvailableAsync(DateTime.Today, DateTime.Today.AddDays(2), 1);

// Walk-in check-in directly through Guests page (2-step form)
guests.Selected = guests.Items.Cast<GuestRow>().First(g => g.Id == guestRow.Id);
dialogs.Next(f => {
    f["nights"].Text = "2";
    f["n"].Text = "1";
});
dialogs.Next(f => {
    // accept default auto-assigned room
});
Run(guests, "Walk-in check-in");
await Task.Delay(1500);
Ok(guests.StatusMessage?.Contains("checked in", StringComparison.OrdinalIgnoreCase) == true, "walk-in check-in through guests page: " + guests.StatusMessage + guests.ErrorMessage);

// 3. guest uses their own window
var g = await Boot("benreyes", tempPw);
await g.NavigateAsync("requests");
var reqs = (ListPageViewModel)g.Current!;
dialogs.Next(f => { f["desc"].Text = "Please clean the room"; });
Run(reqs, "Request cleaning");
await Task.Delay(1500);
Ok(reqs.Items.Count == 1, "guest submits a cleaning request, sees it listed");
await g.NavigateAsync("dashboard"); Ok(!g.Current!.HasError, "guest dashboard loads");
Ok(g.Groups.SelectMany(x => x.Items).All(i => i.Key is not ("rooms" or "guests" or "staff" or "audit")), "guest window exposes no staff pages");

// 4. check-out as receptionist from stays page
await rec.NavigateAsync("stays");
var staysPage = (ListPageViewModel)rec.Current!;
staysPage.Selected = staysPage.Items.Cast<StayRow>().First(x => x.GuestName == "Ben Reyes");
dialogs.Next(null);
Run(staysPage, "Check out");
await Task.Delay(1500);
Ok(staysPage.StatusMessage?.Contains("checked out") == true, "check-out through the stays page: " + staysPage.StatusMessage + staysPage.ErrorMessage);

// 5. housekeeper completes the cleaning
var hk = await Boot("housekeeper", "Housekeeper123");
await hk.NavigateAsync("tasks");
var tasks = (ListPageViewModel)hk.Current!;
var task = tasks.Items.Cast<TaskRow>().First(t => t.RoomNumber == offers[0].RoomNumber && t.Status == "Pending");
tasks.Selected = task; Run(tasks, "Start cleaning"); await Task.Delay(1200);
tasks.Selected = tasks.Items.Cast<TaskRow>().First(t => t.Id == task.Id);
dialogs.Next(f => { foreach (var fld in f.Values.Where(x => x.IsCheck)) fld.Checked = true; });
Run(tasks, "Checklist"); await Task.Delay(1200);
tasks.Selected = tasks.Items.Cast<TaskRow>().First(t => t.Id == task.Id);
dialogs.Next(f => { });
Run(tasks, "Complete"); await Task.Delay(1500);
Ok(tasks.StatusMessage?.Contains("Available") == true, "housekeeper completes cleaning: " + tasks.StatusMessage + tasks.ErrorMessage);

// 6. wrong-role access is refused by the services even if a page were constructed by hand
var guestPageForStaff = SharedPages.Rooms(new PageContext(g.Session, dialogs), SharedPages.RoomMode.Manage);
await guestPageForStaff.ReloadAsync();
Ok(guestPageForStaff.HasError, "a guest cannot load the rooms page even if it is built by hand: " + guestPageForStaff.ErrorMessage);

Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails;

static void Run(ListPageViewModel p, string label)
{
    var a = p.Actions.First(x => x.Label == label);
    if (!a.Command.CanExecute(null)) throw new Exception($"'{label}' is disabled");
    a.Command.Execute(null);
}

sealed class ScriptedDialogs : IDialogService
{
    readonly Queue<Action<FieldMap>?> script = new();
    public List<string> LastLines { get; private set; } = new();
    public void Next(Action<FieldMap>? fill) => script.Enqueue(fill);

    public Task<FormResult?> FormAsync(string title, string? message, IReadOnlyList<FormField> fields, string okText = "Save", bool danger = false)
    {
        var fill = script.Count > 0 ? script.Dequeue() : null;
        var map = new FieldMap(fields);
        fill?.Invoke(map);
        var vm = new FormDialogViewModel(title, message, fields, okText, danger);
        if (!vm.Submit()) throw new Exception($"Form '{title}' failed validation: " + string.Join(", ", fields.Where(f => f.HasError).Select(f => f.Label + " " + f.Error)));
        return Task.FromResult<FormResult?>(vm.Result);
    }
    public Task<bool> ConfirmAsync(string title, string message, string okText = "Confirm", bool danger = false) { if (script.Count > 0) script.Dequeue(); return Task.FromResult(true); }
    public Task MessageAsync(string title, string message, bool isError = false) { Console.WriteLine($"   [dialog] {title}: {message}"); return Task.CompletedTask; }
    public Task ShowLinesAsync(string title, string? message, IEnumerable<string> lines) { LastLines = lines.ToList(); if (script.Count > 0) script.Dequeue(); return Task.CompletedTask; }

}

public sealed class FieldMap : Dictionary<string, FormField>
{
    public FieldMap(IReadOnlyList<FormField> f) { foreach (var x in f) this[x.Key] = x; Values = f; }
    public IReadOnlyList<FormField> Values { get; }
}
