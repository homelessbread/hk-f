using System.Collections.ObjectModel;
using System.Windows.Input;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>One request line of the guest-request directory (Room | Category | Details | Status).</summary>
public sealed record RequestLineVm(int Id, string Room, string Category, string Details, string Status, string Response, DateTime CreatedAt);

/// <summary>One assigned room with its requests, expandable / collapsible.</summary>
public sealed class RequestRoomGroupVm : ObservableObject
{
    bool expanded = true;
    public required string RoomNumber { get; init; }
    public ObservableCollection<RequestLineVm> Rows { get; } = new();
    public int Count => Rows.Count;
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public ICommand ToggleCommand { get; }

    public RequestRoomGroupVm() => ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
}

/// <summary>The guest's Guest Request screen: every request they are allowed to see, grouped by assigned room number.
/// Guest, stay and room always come from the session via <see cref="RequestService"/>.</summary>
public sealed class GuestRequestDirectoryViewModel : PageViewModel
{
    readonly RequestService svc;

    public GuestRequestDirectoryViewModel(PageContext ctx)
        : base(ctx, "Guest Request", "Your requests grouped by room: item requests, cleaning and problem reports. Statuses come straight from the front desk and housekeeping.")
    {
        svc = new RequestService(ctx.Session);
        NewRequestCommand = new AsyncCommand(_ => NewRequestAsync(null), ShowError);
        CleaningCommand = new AsyncCommand(_ => NewRequestAsync(Category.Cleaning), ShowError);
        ItemsCommand = new AsyncCommand(_ => NewRequestAsync(Category.AdditionalItems), ShowError);
        ProblemCommand = new AsyncCommand(_ => NewRequestAsync(Category.Maintenance), ShowError);
    }

    public ObservableCollection<RequestRoomGroupVm> Rooms { get; } = new();
    public bool IsEmpty => Rooms.Count == 0;
    public string EmptyText => "No requests yet. Use “New request” — or right-click — to ask for cleaning, request items or report a problem.";
    public ICommand NewRequestCommand { get; }
    public ICommand CleaningCommand { get; }
    public ICommand ItemsCommand { get; }
    public ICommand ProblemCommand { get; }

    protected override async Task LoadAsync()
    {
        var rows = await svc.MineAsync();
        Rooms.Clear();
        foreach (var g in rows.GroupBy(r => r.RoomNumber).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var group = new RequestRoomGroupVm { RoomNumber = g.Key };
            foreach (var r in g) group.Rows.Add(Map(r));
            Rooms.Add(group);
        }
        Raise(nameof(IsEmpty));
    }

    static RequestLineVm Map(RequestRow r) => new(r.Id, r.RoomNumber, CategoryLabel(r.Category), Details(r),
        Labels.Of(r.Status), string.IsNullOrWhiteSpace(r.Resolution) ? "" : r.Resolution, r.CreatedAt);

    internal static string CategoryLabel(string category) => category switch
    {
        Category.AdditionalItems => "Item Request",
        Category.Cleaning => "Cleaning Request",
        Category.Maintenance => "Problem Report",
        Category.GeneralAssistance => "Assistance",
        _ => Labels.Of(category)
    };

    static string Details(RequestRow r)
    {
        var d = r.Description ?? "";
        if (r.Category == Category.AdditionalItems && r.ItemsText.Length > 0)
            d = d.Length > 0 ? $"{r.ItemsText} — {d}" : r.ItemsText;
        if (r.ItemsText.Length > 0 && r.Category != Category.AdditionalItems && !d.Contains(r.ItemsText, StringComparison.Ordinal))
            d = $"{d} ({r.ItemsText})";
        return d;
    }

    /// <summary>Submits a request for the signed-in guest's active stay. <paramref name="preset"/> skips the
    /// category chooser (Cleaning / AdditionalItems / Maintenance); null shows all three request types.</summary>
    public async Task NewRequestAsync(string? preset)
    {
        var stay = await new StayService(Ctx.Session).MyActiveStayAsync();
        if (stay is null)
        {
            await Ctx.Dialogs.MessageAsync("No active stay",
                "You can send requests only while you are checked in to a room. You have no active stay right now: you are either not checked in yet or have already checked out.", true);
            return;
        }

        var category = preset;
        if (category is null)
        {
            var c = await Ctx.Dialogs.FormAsync("New request", $"For room {stay.RoomNumber}. What do you need?", new[]
            {
                FormField.Combo("cat", "Category", new[]
                {
                    new Choice(Category.Maintenance, "Report a problem"),
                    new Choice(Category.Cleaning, "Room cleaning"),
                    new Choice(Category.AdditionalItems, "Additional items"),
                    new Choice(Category.GeneralAssistance, "General assistance"),
                }, Category.Cleaning)
            }, "Next");
            if (c is null) return;
            category = c.Get<string>("cat");
        }

        var fields = new List<FormField>();
        var items = new List<RequestableItemRow>();
        if (category == Category.Maintenance) fields.Add(FormField.Combo("issue", "What kind of problem?", IssueTypes.All.Select(t => new Choice(t, t)), "Other"));
        if (category == Category.AdditionalItems)
        {
            items = await svc.ItemsAsync(true);
            fields.AddRange(items.Select(i => FormField.Int("i" + i.Id, i.Name + (i.Rule.Length > 0 ? $"  ({i.Rule})" : ""), 0, false, 0, i.MaxQuantity)));
        }
        fields.Add(FormField.Multi("desc", category == Category.AdditionalItems ? "Anything else we should know? (optional)" : "Describe what you need",
            required: category != Category.AdditionalItems));
        fields.Add(FormField.Combo("pri", "How urgent is it?", SharedPages.Strs(Priorities.All), "Normal"));

        var title = category switch { Category.Maintenance => "Report a problem", Category.Cleaning => "Request cleaning", Category.AdditionalItems => "Request items", _ => "Ask for assistance" };
        var f = await Ctx.Dialogs.FormAsync(title, $"Room {stay.RoomNumber}. The request is linked to your stay automatically.", fields, "Send request");
        if (f is null) return;
        var lines = items.Where(i => f.Get<int>("i" + i.Id) > 0).Select(i => new RequestLine(i.Id, f.Get<int>("i" + i.Id))).ToList();
        var id = await svc.CreateAsync(category!, f.Str("desc"), f.Get<string>("pri")!, f.Get<string>("issue"), lines);
        StatusMessage = $"Request #{id} sent. We will keep you updated here.";
        await ReloadAsync();
        await Ctx.RefreshBadges();
    }
}
