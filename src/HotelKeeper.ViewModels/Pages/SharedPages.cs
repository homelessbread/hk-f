using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>Pages used by more than one role. Each builder decides which buttons to show from the signed-in role, and the services enforce the same rules again.</summary>
public static class SharedPages
{
    internal static readonly string[] PaymentMethods = { "Cash", "Card", "Bank Transfer", "Online" };
    internal static readonly string[] PaymentStatuses = { "Paid", "Pending", "Refunded" };

    internal static IEnumerable<Choice> Strs(IEnumerable<string> v, Func<string, string>? label = null) => v.Select(x => new Choice(x, label?.Invoke(x) ?? Labels.Of(x)));

    // ================================================================================== notifications & account
    public static ListPageViewModel Notifications(PageContext ctx)
    {
        var svc = new NotificationService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Notifications", "Updates about your work, requests and reservations.",
            new[] { new ColumnDef("When", nameof(NotificationRow.CreatedAt), ColumnKind.DateTime, 150), new ColumnDef("Title", nameof(NotificationRow.Title), Width: 200),
                    new ColumnDef("Message", nameof(NotificationRow.Message)), new ColumnDef("Read", nameof(NotificationRow.IsRead), ColumnKind.Bool, 70) },
            async _ => (await svc.ListAsync(200)).Cast<object>().ToList(), "You are all caught up.", searchable: false);
        p.Act<NotificationRow>("Mark as read", "check", async n => { await svc.MarkReadAsync(n.Id); await p.ReloadAsync(); await ctx.RefreshBadges(); }, n => !n.IsRead);
        p.Act("Mark all as read", "checks", async () => { await svc.MarkAllReadAsync(); await p.ReloadAsync(); await ctx.RefreshBadges(); });
        return p;
    }

    public static FormPageViewModel Account(PageContext ctx) => new(ctx, "My account", "Your sign-in details.",
        () => Task.FromResult<IReadOnlyList<FormField>>(new[]
        {
            FormField.InfoLine("Name", ctx.Session.FullName), FormField.InfoLine("Username", ctx.Session.Username),
            FormField.InfoLine("Role", ctx.Session.Role), FormField.InfoLine("Email", string.IsNullOrWhiteSpace(ctx.Session.Email) ? "—" : ctx.Session.Email),
        }),
        async _ => { await ctx.ChangePassword(); return null; }, "Change password…");

    // ================================================================================== rooms
    public enum RoomMode { Manage, Status, View }

    public static ListPageViewModel Rooms(PageContext ctx, RoomMode mode, bool archivedOnly = false, string? title = null, string? description = null)
    {
        var s = ctx.Session; var svc = new RoomService(s); var admin = s.Role == Roles.Admin;
        title ??= mode == RoomMode.Status ? "Room status" : "Rooms";
        description ??= mode == RoomMode.Status ? "Live status of every room. Statuses change only through valid hotel activity." : "Room records, rates and current status.";
        var p = new ListPageViewModel(ctx, title, description,
            new[]
            {
                new ColumnDef("Room", nameof(RoomRow.RoomNumber), Width: 80), new ColumnDef("Building", nameof(RoomRow.Building), Width: 90), new ColumnDef("Floor", nameof(RoomRow.Floor), ColumnKind.Number, 60),
                new ColumnDef("Type", nameof(RoomRow.RoomType), Width: 120), new ColumnDef("Sleeps", nameof(RoomRow.Capacity), ColumnKind.Number, 65), new ColumnDef("Rate / night", nameof(RoomRow.Rate), ColumnKind.Money, 110),
                new ColumnDef("Status", nameof(RoomRow.Status), ColumnKind.Badge, 130), new ColumnDef("Guest", nameof(RoomRow.CurrentGuest)), new ColumnDef("Open issues", nameof(RoomRow.OpenIssues), ColumnKind.Number, 95),
            },
            async q => (await svc.ListAsync(q.Str("status"), q.SearchOrNull, archivedOnly || q.Flag("archived"))).Cast<object>().ToList(),
            "No rooms match.", searchHint: "Search room number or type…");
        if (admin && !archivedOnly && mode == RoomMode.Manage) p.AddTabs("archived", ("Active", false), ("Archived", true));
        p.AddCombo("status", "Status", Strs(RoomStatus.All), allLabel: "All statuses");
        p.SummaryCards = rows =>
        {
            var r = rows.Cast<RoomRow>().ToList();
            return RoomStatus.All.Select(st => new StatCard(Labels.Of(st), r.Count(x => x.Status == st).ToString())).ToList();
        };

        if (mode == RoomMode.Manage && admin && !archivedOnly)
        {
            p.Act("Add room", "plus", async () =>
            {
                var f = await RoomForm(null); if (f is null) return;
                var id = await svc.SaveRoomAsync(null, f.Str("number"), f.Str("building"), f.Int("floor"), f.Get<int>("type"), f.Int("cap"), f.Dec("rate"), f.Str("amen"), f.Str("desc"));
                p.StatusMessage = $"Room {f.Str("number")} was added. It starts as Dirty and a housekeeping task was created."; await p.ReloadAsync();
            }, primary: true);
            p.Act<RoomRow>("Edit", "pencil", async r =>
            {
                var f = await RoomForm(r); if (f is null) return;
                await svc.SaveRoomAsync(r.Id, f.Str("number"), f.Str("building"), f.Int("floor"), f.Get<int>("type"), f.Int("cap"), f.Dec("rate"), f.Str("amen"), f.Str("desc"));
                p.StatusMessage = $"Room {f.Str("number")} was updated."; await p.ReloadAsync();
            });
        }
        if (mode != RoomMode.View && !archivedOnly)
        {
            if (s.Role is Roles.Receptionist or Roles.Admin)
            {
                p.Act<RoomRow>("Check in guest", "log-in", async r =>
                {
                    var guests = (await new GuestService(s).SearchAsync()).Select(g => new Choice(g.Id, $"{g.FullName} ({(g.Phone.Length > 0 ? g.Phone : g.Email)})")).ToList();
                    if (guests.Count == 0) { await ctx.Dialogs.MessageAsync("No guests yet", "Register the guest first in the Guest directory.", true); return; }
                    var f = await ctx.Dialogs.FormAsync($"Check in to Room {r.RoomNumber}", $"{r.RoomType} (Capacity: {r.Capacity}, Rate: {r.Rate:N2})", new[]
                    {
                        FormField.Combo("guest", "Guest", guests),
                        FormField.Int("nights", "Nights", 1, true, 1, 365),
                        FormField.Int("n", "Number of guests", 1, true, 1, r.Capacity),
                        FormField.Multi("notes", "Notes")
                    }, "Check in");
                    if (f is null) return;
                    await new StayService(s).WalkInAsync(f.Get<int>("guest"), r.Id, f.Int("nights"), f.Int("n"), f.Str("notes"));
                    p.StatusMessage = $"Guest checked in to Room {r.RoomNumber}.";
                    await p.ReloadAsync();
                }, r => r.Status == RoomStatus.Available);

                p.Act<RoomRow>("Check out guest", "log-out", async r =>
                {
                    var active = (await new StayService(s).ListAsync(true)).FirstOrDefault(st => st.RoomId == r.Id);
                    if (active is null) { await ctx.Dialogs.MessageAsync("No active stay", $"Room {r.RoomNumber} has no active stay to check out.", true); return; }
                    var msg = active.Balance > 0 ? $"{active.GuestName} still owes {active.Balance:N2}. Check out now?" : $"Check {active.GuestName} out of room {r.RoomNumber}?";
                    if (!await ctx.Dialogs.ConfirmAsync("Check out", msg, "Check out")) return;
                    var result = await new StayService(s).CheckOutAsync(active.StayId);
                    p.StatusMessage = $"{active.GuestName} checked out of Room {result.RoomNumber}." + (result.Balance > 0 ? $" Outstanding balance: {result.Balance:N2}." : "");
                    await p.ReloadAsync();
                }, r => r.Status == RoomStatus.Occupied);
            }

            p.Act<RoomRow>("Change status", "refresh", async r =>
            {
                var targets = RoomStatus.All.Where(t => r.Status != RoomStatus.OutOfService && RoomRules.CheckManual(s.Role, r.Status, t) == null).ToList();
                if (targets.Count == 0)
                {
                    await ctx.Dialogs.MessageAsync("No manual change available", r.Status == RoomStatus.Occupied ? "An occupied room changes status only through check-out."
                        : r.Status == RoomStatus.OutOfService ? "Use “Return to service” to clear a room that is Out of Service." : "This room's status moves forward through housekeeping and front-desk work.", false);
                    return;
                }
                var f = await ctx.Dialogs.FormAsync($"Room {r.RoomNumber}: change status", $"Now {Labels.Of(r.Status)}. Only valid changes are offered.", new[]
                {
                    FormField.Combo("to", "New status", Strs(targets), targets[0]),
                    FormField.Txt("why", "Reason", hint: "Required for Out of Service."),
                }, "Change status");
                if (f is null) return;
                await svc.ChangeStatusAsync(r.Id, f.Get<string>("to")!, f.Str("why"));
                p.StatusMessage = $"Room {r.RoomNumber} is now {Labels.Of(f.Get<string>("to"))}."; await p.ReloadAsync();
            }, can: r => r.Status is not RoomStatus.Occupied);
        }
        if (admin && mode != RoomMode.View && !archivedOnly)
            p.Act<RoomRow>("Return to service", "shield-check", async r =>
            {
                if (!await ctx.Dialogs.ConfirmAsync("Return room to service", $"Clear room {r.RoomNumber}? It becomes Dirty so it is cleaned before the next guest. This is refused while blocking maintenance issues are open.", "Return to service")) return;
                await svc.RestoreToServiceAsync(r.Id);
                p.StatusMessage = $"Room {r.RoomNumber} was returned to service as Dirty."; await p.ReloadAsync();
            }, can: r => r.Status == RoomStatus.OutOfService);
        if (s.Role is Roles.Admin or Roles.Receptionist)
            p.Act<RoomRow>("History", "history", async r =>
            {
                var h = await svc.HistoryAsync(r.Id);
                await ctx.Dialogs.ShowLinesAsync($"Room {r.RoomNumber}: status history", h.Count == 0 ? "No status changes recorded." : null,
                    h.Select(x => $"{x.ChangedAt.ToLocalTime():g}   {Labels.Of(x.PreviousStatus)} → {Labels.Of(x.NewStatus)}   by {x.ChangedBy}" + (string.IsNullOrWhiteSpace(x.Reason) ? "" : $"   ({x.Reason})")));
            });
        if (admin && mode == RoomMode.Manage)
            p.Act<RoomRow>(archivedOnly ? "Restore" : "Archive", "archive", async r =>
            {
                var restoring = r.IsArchived;
                if (!await ctx.Dialogs.ConfirmAsync(restoring ? "Restore room" : "Archive room",
                    restoring ? $"Bring room {r.RoomNumber} back into use?" : $"Archive room {r.RoomNumber}? It disappears from booking but every record about it is kept.", restoring ? "Restore" : "Archive", danger: !restoring)) return;
                await svc.SetArchivedAsync(r.Id, !restoring);
                p.StatusMessage = restoring ? $"Room {r.RoomNumber} was restored." : $"Room {r.RoomNumber} was archived."; await p.ReloadAsync();
            }, danger: !archivedOnly);
        return p;

        async Task<FormResult?> RoomForm(RoomRow? r)
        {
            var types = (await svc.TypesAsync(activeOnly: true)).Select(t => new Choice(t.Id, $"{t.Name} (sleeps {t.Capacity}, {t.BaseRate:N2})")).ToList();
            return await ctx.Dialogs.FormAsync(r is null ? "Add room" : $"Edit room {r.RoomNumber}", null, new[]
            {
                FormField.Txt("number", "Room number", r?.RoomNumber, true), FormField.Txt("building", "Building", r?.Building ?? "Main"),
                FormField.Int("floor", "Floor", r?.Floor ?? 1, true, 0, 100), FormField.Combo("type", "Room type", types, r?.RoomTypeId),
                FormField.Int("cap", "Capacity (guests)", r?.Capacity ?? 2, true, 1, 20), FormField.Dec("rate", "Rate per night", r?.Rate, true, 0.01m),
                FormField.Txt("amen", "Amenities", r?.Amenities, hint: "Comma separated."), FormField.Multi("desc", "Description", r?.Description),
            }, r is null ? "Add room" : "Save changes");
        }
    }

    public static ListPageViewModel RoomTypes(PageContext ctx)
    {
        var svc = new RoomService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Room types", "Categories of rooms and their default rate and capacity.",
            new[] { new ColumnDef("Name", nameof(RoomTypeRow.Name), Width: 160), new ColumnDef("Description", nameof(RoomTypeRow.Description)), new ColumnDef("Sleeps", nameof(RoomTypeRow.Capacity), ColumnKind.Number, 70),
                    new ColumnDef("Base rate", nameof(RoomTypeRow.BaseRate), ColumnKind.Money, 110), new ColumnDef("Rooms", nameof(RoomTypeRow.RoomCount), ColumnKind.Number, 70), new ColumnDef("Active", nameof(RoomTypeRow.IsActive), ColumnKind.Bool, 70) },
            async _ => (await svc.TypesAsync()).Cast<object>().ToList(), "No room types yet.", searchable: false);
        p.Act("Add room type", "plus", () => Edit(null), primary: true);
        p.Act<RoomTypeRow>("Edit", "pencil", t => Edit(t));
        return p;

        async Task Edit(RoomTypeRow? t)
        {
            var f = await ctx.Dialogs.FormAsync(t is null ? "Add room type" : $"Edit {t.Name}", null, new[]
            {
                FormField.Txt("name", "Name", t?.Name, true), FormField.Multi("desc", "Description", t?.Description),
                FormField.Dec("rate", "Base rate per night", t?.BaseRate, true, 0.01m), FormField.Int("cap", "Capacity", t?.Capacity ?? 2, true, 1, 20), FormField.Check("active", "Active (can be booked)", t?.IsActive ?? true),
            }, "Save");
            if (f is null) return;
            await svc.SaveTypeAsync(t?.Id, f.Str("name"), f.Str("desc"), f.Dec("rate"), f.Int("cap"), f.Bool("active"));
            p.StatusMessage = "Room type saved."; await p.ReloadAsync();
        }
    }

    public static ListPageViewModel RoomItems(PageContext ctx)
    {
        var svc = new RoomService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Room items", "Items kept in each room: quantities, condition and what needs replacing.",
            new[] { new ColumnDef("Item", nameof(RoomItemRow.ItemName), Width: 200), new ColumnDef("Quantity", nameof(RoomItemRow.Quantity), ColumnKind.Number, 90), new ColumnDef("Condition", nameof(RoomItemRow.ConditionStatus), ColumnKind.Badge, 120),
                    new ColumnDef("Needs replenishment", nameof(RoomItemRow.NeedsReplenishment), ColumnKind.Bool, 150), new ColumnDef("Updated", nameof(RoomItemRow.UpdatedAt), ColumnKind.DateTime, 150) },
            async q => q.Int("room") is int id ? (await svc.ItemsAsync(id)).Cast<object>().ToList() : new List<object>(),
            "Choose a room to see its items.", searchable: false);
        p.AddCombo("room", "Room", loader: async () => (await svc.ListAsync()).Select(r => new Choice(r.Id, r.Display)).ToList(), includeAll: false);
        string[] conditions = { "Good", "Worn", "Damaged", "Missing" };
        p.Act("Add item", "plus", () => Edit(null), primary: true);
        p.Act<RoomItemRow>("Edit", "pencil", i => Edit(i));
        p.Act<RoomItemRow>("Remove", "trash", async i =>
        {
            if (!await ctx.Dialogs.ConfirmAsync("Remove item", $"Remove “{i.ItemName}” from this room?", "Remove", danger: true)) return;
            await svc.DeleteItemAsync(i.Id); p.StatusMessage = "Item removed."; await p.ReloadAsync();
        }, danger: true);
        return p;

        async Task Edit(RoomItemRow? i)
        {
            var roomId = (p.Filters.First(f => f.Key == "room").Value as int?) ?? 0;
            if (roomId == 0) { await ctx.Dialogs.MessageAsync("Choose a room", "Pick a room first.", true); return; }
            var f = await ctx.Dialogs.FormAsync(i is null ? "Add room item" : "Edit room item", null, new[]
            {
                FormField.Txt("name", "Item", i?.ItemName, true), FormField.Int("qty", "Quantity", i?.Quantity ?? 1, true, 0, 999),
                FormField.Combo("cond", "Condition", conditions.Select(c => new Choice(c, c)), i?.ConditionStatus ?? "Good"), FormField.Check("repl", "Needs replacement / replenishment", i?.NeedsReplenishment ?? false),
            }, "Save");
            if (f is null) return;
            await svc.SaveItemAsync(i?.Id, roomId, f.Str("name"), f.Int("qty"), f.Get<string>("cond")!, f.Bool("repl"));
            p.StatusMessage = "Room item saved."; await p.ReloadAsync();
        }
    }

    // ================================================================================== guests
    public static ListPageViewModel Guests(PageContext ctx, string? title = null, string? description = null)
    {
        var s = ctx.Session; var svc = new GuestService(s); var admin = s.Role == Roles.Admin; var rec = s.Role is Roles.Receptionist or Roles.Admin;
        var p = new ListPageViewModel(ctx, title ?? "Guest", description ?? "Register guests and keep their contact details up to date.",
            new[] { new ColumnDef("Name", nameof(GuestRow.FullName), Width: 190), new ColumnDef("Username", nameof(GuestRow.Username), Width: 130), new ColumnDef("Email", nameof(GuestRow.Email), Width: 200), new ColumnDef("Phone", nameof(GuestRow.Phone), Width: 130),
                    new ColumnDef("Current room", nameof(GuestRow.CurrentRoom), Width: 110), new ColumnDef("Stays", nameof(GuestRow.StayCount), ColumnKind.Number, 70), new ColumnDef("Registered", nameof(GuestRow.CreatedAt), ColumnKind.Date, 110) },
            async q => (await svc.SearchAsync(q.SearchOrNull, q.Str("scope") == "archived", q.Str("scope") == "inhouse")).Cast<object>().ToList(),
            "No guests found.", searchHint: "Search name, email or phone…");
        p.AddTabs("scope", admin ? new (string, object?)[] { ("All guests", "all"), ("In-house", "inhouse"), ("Archived", "archived") } : new (string, object?)[] { ("All guests", "all"), ("In-house", "inhouse") });

        p.Act("Register guest", "user-plus", async () =>
        {
            var f = await ctx.Dialogs.FormAsync("Register guest", "A sign-in account is created for the guest with a temporary password.", GuestFields(null), "Register");
            if (f is null) return;
            var c = await svc.RegisterAsync(f.Str("name"), f.Str("email"), f.Str("phone"), f.Str("addr"), f.Str("nat"), f.Str("id"), f.Str("user"));
            await ctx.Dialogs.ShowLinesAsync("Guest registered", "Give these sign-in details to the guest. The password is shown only now.",
                new[] { $"Username:  {c.Username}", $"Temporary password:  {c.TemporaryPassword}" });
            p.StatusMessage = $"{f.Str("name")} was registered."; await p.ReloadAsync();
        });

        if (rec)
        {
            p.Act<GuestRow>("Walk-in check-in", "log-in", async g =>
            {
                var types = (await new RoomService(s).TypesAsync(true)).Select(t => new Choice(t.Id, t.Name)).ToList();
                var f = await ctx.Dialogs.FormAsync($"Walk-in check-in: {g.FullName}", "Step 1 of 2: choose duration and guest count.", new[]
                {
                    FormField.Int("nights", "Nights", 1, true, 1, 365),
                    FormField.Int("n", "Number of guests", 1, true, 1, 20),
                    FormField.Combo("type", "Room type", types, null, required: false, hint: "Leave empty for any type."),
                }, "Find a room");
                if (f is null) return;
                var checkOut = DateTime.Today.AddDays(f.Int("nights"));
                var offers = (await new RoomService(s).AvailableAsync(DateTime.Today, checkOut, f.Int("n"), f.Get<int?>("type"))).Where(o => o.Status == RoomStatus.Available).ToList();
                var auto = await new StayService(s).AutoAssignAsync(checkOut, f.Int("n"), f.Get<int?>("type"));
                if (offers.Count == 0 || auto is null) { await ctx.Dialogs.MessageAsync("No suitable room available", "No clean room fits this guest right now.", true); return; }
                var f2 = await ctx.Dialogs.FormAsync($"Walk-in check-in: {g.FullName}", "Step 2 of 2: review room and confirm.", new[]
                {
                    FormField.Combo("room", "Room", offers.Select(o => new Choice(o.Id, o.Display)), auto.Id),
                    FormField.Multi("notes", "Notes"),
                }, "Confirm check-in");
                if (f2 is null) return;
                await new StayService(s).WalkInAsync(g.Id, f2.Get<int>("room"), f.Int("nights"), f.Int("n"), f2.Str("notes"));
                p.StatusMessage = $"{g.FullName} checked in."; await p.ReloadAsync();
            }, g => string.IsNullOrEmpty(g.CurrentRoom));

            p.Act<GuestRow>("Check out", "log-out", async g =>
            {
                var active = (await new StayService(s).ListAsync(true)).FirstOrDefault(st => st.GuestId == g.Id);
                if (active is null) { await ctx.Dialogs.MessageAsync("No active stay", "Guest has no active stay to check out.", true); return; }
                var msg = active.Balance > 0 ? $"{g.FullName} still owes {active.Balance:N2}. Check out now?" : $"Check {g.FullName} out of room {active.RoomNumber}?";
                if (!await ctx.Dialogs.ConfirmAsync("Check out", msg, "Check out")) return;
                var result = await new StayService(s).CheckOutAsync(active.StayId);
                p.StatusMessage = $"{g.FullName} checked out. Room {result.RoomNumber} is Dirty." + (result.Balance > 0 ? $" Outstanding balance: {result.Balance:N2}." : "");
                await p.ReloadAsync();
            }, g => !string.IsNullOrEmpty(g.CurrentRoom));
        }

        p.Act<GuestRow>("Edit", "pencil", async g =>
        {
            var f = await ctx.Dialogs.FormAsync($"Edit {g.FullName}", null, GuestFields(g), "Save changes");
            if (f is null) return;
            await svc.UpdateAsync(g.Id, f.Str("name"), f.Str("email"), f.Str("phone"), f.Str("addr"), f.Str("nat"), f.Str("id"));
            p.StatusMessage = "Guest details saved."; await p.ReloadAsync();
        });
        p.Act<GuestRow>("Reset password", "key", async g =>
        {
            if (!await ctx.Dialogs.ConfirmAsync("Reset password", $"Create a new temporary password for {g.FullName}? Their old password stops working.", "Reset")) return;
            var pw = await svc.ResetPasswordAsync(g.Id);
            await ctx.Dialogs.ShowLinesAsync("Password reset", "Give this to the guest. It is shown only now.", new[] { $"Username:  {g.Username}", $"Temporary password:  {pw}" });
        });
        if (admin)
            p.Act<GuestRow>("Archive / restore", "archive", async g =>
            {
                var restoring = g.IsArchived;
                if (!await ctx.Dialogs.ConfirmAsync(restoring ? "Restore guest" : "Archive guest", restoring ? $"Allow {g.FullName} to sign in again?" : $"Archive {g.FullName}? They can no longer sign in, but all stays, requests and payments are kept.", restoring ? "Restore" : "Archive", danger: !restoring)) return;
                await svc.SetArchivedAsync(g.Id, !restoring);
                p.StatusMessage = restoring ? "Guest restored." : "Guest archived."; await p.ReloadAsync();
            }, danger: true);
        return p;

        static IReadOnlyList<FormField> GuestFields(GuestRow? g) => new[]
        {
            FormField.Txt("name", "Full name", g?.FullName, true), FormField.Txt("email", "Email", g?.Email), FormField.Txt("phone", "Phone", g?.Phone, hint: "Phone or email is required."),
            FormField.Txt("addr", "Address", g?.Address), FormField.Txt("nat", "Nationality", g?.Nationality), FormField.Txt("id", "ID document", g?.IdDocument),
            g is null ? FormField.Txt("user", "Username", hint: "Optional. Generated from the name if left empty.") : FormField.InfoLine("Username", g.Username),
        };
    }

    // ================================================================================== reservations & front desk
    static readonly ColumnDef[] ReservationColumns =
    {
        new("#", nameof(ReservationRow.Id), ColumnKind.Number, 55), new("Guest", nameof(ReservationRow.GuestName), Width: 170), new("Room", nameof(ReservationRow.RoomNumber), Width: 65), new("Type", nameof(ReservationRow.RoomType), Width: 110),
        new("Check-in", nameof(ReservationRow.CheckInDate), ColumnKind.Date, 100), new("Check-out", nameof(ReservationRow.CheckOutDate), ColumnKind.Date, 100), new("Nights", nameof(ReservationRow.Nights), ColumnKind.Number, 65),
        new("Status", nameof(ReservationRow.Status), ColumnKind.Badge, 115), new("Total", nameof(ReservationRow.TotalAmount), ColumnKind.Money, 100), new("Paid", nameof(ReservationRow.Paid), ColumnKind.Money, 100), new("Balance", nameof(ReservationRow.Balance), ColumnKind.Money, 100),
    };

    public static ListPageViewModel Reservations(PageContext ctx, string scope = "all", string? title = null, string? description = null, bool history = false)
    {
        var s = ctx.Session; var svc = new ReservationService(s);
        var p = new ListPageViewModel(ctx, title ?? "Reservations", description ?? "Create, confirm, change and cancel reservations.", ReservationColumns,
            async q => (await svc.ListAsync(history ? "history" : q.Str("scope") ?? scope, q.Str("status"), q.SearchOrNull)).Cast<object>().ToList(),
            "No reservations match.", searchHint: "Search guest, room or reservation #…");
        if (!history)
        {
            p.AddTabs("scope", ("All", "all"), ("Arrivals today", "arrivals"), ("Departures today", "departures"), ("Upcoming", "upcoming"), ("History", "history"));
            p.AddCombo("status", "Status", Strs(ResStatus.All), allLabel: "Any status");
        }
        else p.AddCombo("status", "Status", Strs(new[] { ResStatus.CheckedOut, ResStatus.Cancelled }), allLabel: "Any status");

        if (!history)
        {
            p.Act("New reservation", "plus", () => NewReservation(ctx, svc, p), primary: true);
            p.Act<ReservationRow>("Confirm", "check", async r => { await svc.ConfirmAsync(r.Id); p.StatusMessage = $"Reservation #{r.Id} confirmed."; await p.ReloadAsync(); }, r => r.Status == ResStatus.Pending);
            p.Act<ReservationRow>("Change", "pencil", r => ModifyReservation(ctx, svc, p, r), r => r.Status is ResStatus.Pending or ResStatus.Confirmed);
            p.Act<ReservationRow>("Cancel", "x", async r =>
            {
                var f = await ctx.Dialogs.FormAsync($"Cancel reservation #{r.Id}", $"{r.GuestName}, room {r.RoomNumber}, {r.CheckInDate:d} – {r.CheckOutDate:d}", new[] { FormField.Multi("why", "Reason (optional)") }, "Cancel reservation", danger: true);
                if (f is null) return;
                await svc.CancelAsync(r.Id, f.Str("why")); p.StatusMessage = $"Reservation #{r.Id} was cancelled."; await p.ReloadAsync();
            }, r => r.Status is ResStatus.Pending or ResStatus.Confirmed, danger: true);
            p.Act<ReservationRow>("Record payment", "banknote", r => AddPayment(ctx, svc, p, r.Id, r.Balance), r => r.Status != ResStatus.Cancelled || r.Paid > 0);
        }
        return p;
    }

    internal static async Task NewReservation(PageContext ctx, ReservationService svc, ListPageViewModel p)
    {
        var guests = (await new GuestService(ctx.Session).SearchAsync()).Select(g => new Choice(g.Id, $"{g.FullName}  ({(g.Phone.Length > 0 ? g.Phone : g.Email)})")).ToList();
        var types = (await new RoomService(ctx.Session).TypesAsync(true)).Select(t => new Choice(t.Id, t.Name)).ToList();
        if (guests.Count == 0) { await ctx.Dialogs.MessageAsync("No guests yet", "Register the guest first in the Guest directory.", true); return; }
        var f = await ctx.Dialogs.FormAsync("New reservation", "Step 1 of 2: who, when and how many. Available rooms are shown next.", new[]
        {
            FormField.Combo("guest", "Guest", guests), FormField.Day("in", "Check-in", DateTime.Today), FormField.Day("out", "Check-out", DateTime.Today.AddDays(1)),
            FormField.Int("n", "Number of guests", 2, true, 1, 20), FormField.Combo("type", "Room type", types, null, required: false, hint: "Leave empty for any type."),
        }, "Find rooms");
        if (f is null) return;
        ReservationRules.ValidateDates(f.Day("in"), f.Day("out"), f.Int("n"));
        var offers = await new RoomService(ctx.Session).AvailableAsync(f.Day("in"), f.Day("out"), f.Int("n"), f.Get<int?>("type"));
        offers = offers.ToList();
        if (offers.Count == 0) { await ctx.Dialogs.MessageAsync("No rooms available", "No room fits these dates and guest count. Try other dates or another room type.", true); return; }
        var nights = (f.Day("out") - f.Day("in")).Days;
        var f2 = await ctx.Dialogs.FormAsync("New reservation", $"Step 2 of 2: {nights} night(s). Choose a room.", new[]
        {
            FormField.Combo("room", "Room", offers.Select(o => new Choice(o.Id, o.Display + (o.Status == RoomStatus.Available ? "" : $"  [{Labels.Of(o.Status)} now]"))), offers[0].Id),
            FormField.Multi("notes", "Notes"), FormField.Check("confirm", "Confirm the reservation now", true),
        }, "Create reservation");
        if (f2 is null) return;
        var id = await svc.CreateAsync(f.Get<int>("guest"), f2.Get<int>("room"), f.Day("in"), f.Day("out"), f.Int("n"), f2.Str("notes"), f2.Bool("confirm"));
        p.StatusMessage = $"Reservation #{id} was created."; await p.ReloadAsync();
    }

    static async Task ModifyReservation(PageContext ctx, ReservationService svc, ListPageViewModel p, ReservationRow r)
    {
        var f = await ctx.Dialogs.FormAsync($"Change reservation #{r.Id}", "Step 1 of 2: dates and guests.", new[]
        {
            FormField.Day("in", "Check-in", r.CheckInDate), FormField.Day("out", "Check-out", r.CheckOutDate), FormField.Int("n", "Number of guests", r.GuestCount, true, 1, 20),
        }, "Find rooms");
        if (f is null) return;
        ReservationRules.ValidateDates(f.Day("in"), f.Day("out"), f.Int("n"));
        var offers = (await new RoomService(ctx.Session).AvailableAsync(f.Day("in"), f.Day("out"), f.Int("n"), null, r.Id)).ToList();
        if (offers.Count == 0) { await ctx.Dialogs.MessageAsync("No rooms available", "No room fits these dates and guest count.", true); return; }
        var f2 = await ctx.Dialogs.FormAsync($"Change reservation #{r.Id}", "Step 2 of 2: room and notes.", new[]
        {
            FormField.Combo("room", "Room", offers.Select(o => new Choice(o.Id, o.Display)), offers.Any(o => o.Id == r.RoomId) ? r.RoomId : offers[0].Id), FormField.Multi("notes", "Notes", r.Notes),
        }, "Save changes");
        if (f2 is null) return;
        await svc.ModifyAsync(r.Id, f2.Get<int>("room"), f.Day("in"), f.Day("out"), f.Int("n"), f2.Str("notes"));
        p.StatusMessage = $"Reservation #{r.Id} was updated."; await p.ReloadAsync();
    }

    static async Task AddPayment(PageContext ctx, ReservationService svc, ListPageViewModel p, int reservationId, decimal balance)
    {
        var f = await ctx.Dialogs.FormAsync($"Record payment: reservation #{reservationId}", balance > 0 ? $"Outstanding balance: {balance:N2}" : null, new[]
        {
            FormField.Dec("amount", "Amount", balance > 0 ? balance : null, true, 0.01m), FormField.Combo("method", "Method", Strs(PaymentMethods, x => x), "Cash"),
            FormField.Combo("status", "Status", Strs(PaymentStatuses, x => x), "Paid"), FormField.Txt("ref", "Reference", hint: "Receipt or transaction number."),
        }, "Record payment");
        if (f is null) return;
        await svc.AddPaymentAsync(reservationId, f.Dec("amount"), f.Get<string>("method")!, f.Get<string>("status")!, f.Str("ref"));
        p.StatusMessage = "Payment recorded."; await p.ReloadAsync();
    }

    public static ListPageViewModel CheckIn(PageContext ctx)
    {
        var s = ctx.Session; var res = new ReservationService(s); var stays = new StayService(s);
        var p = new ListPageViewModel(ctx, "Check-in", "Guests due to arrive. Pick the reservation, review the suggested room and confirm.",
            ReservationColumns.Where(c => c.Path is not (nameof(ReservationRow.Paid) or nameof(ReservationRow.Balance) or nameof(ReservationRow.Nights))).ToArray(),
            async q => (await res.ListAsync("arrivals", null, q.SearchOrNull)).Cast<object>().ToList(), "No arrivals waiting.", searchHint: "Search guest or room…");
        p.Act<ReservationRow>("Check in", "log-in", async r =>
        {
            if (r.Status == ResStatus.Pending) { await ctx.Dialogs.MessageAsync("Confirm first", "Confirm this reservation (from Reservations) before checking the guest in.", true); return; }
            var rooms = await stays.CandidateRoomsAsync(r.Id);
            if (rooms.Count == 0) { await ctx.Dialogs.MessageAsync("No suitable room available", "No clean room that fits this guest is ready right now. Check Housekeeping or Room status. Check-in cannot be completed.", true); return; }
            var f = await ctx.Dialogs.FormAsync($"Check in {r.GuestName}", $"Reservation #{r.Id}: {r.GuestCount} guest(s), until {r.CheckOutDate:d}. The reserved room is suggested first; change it if needed.", new[]
            {
                FormField.Combo("room", "Room", rooms.Select(o => new Choice(o.Id, o.Display + (o.Id == r.RoomId ? "   ← reserved" : ""))), rooms[0].Id),
            }, "Confirm check-in");
            if (f is null) return;
            await stays.CheckInAsync(r.Id, f.Get<int>("room"));
            p.StatusMessage = $"{r.GuestName} is checked in. The room is now Occupied and linked to their account."; await p.ReloadAsync();
        }, r => r.Status is ResStatus.Confirmed or ResStatus.Pending, primary: true);
        p.Act<ReservationRow>("Confirm reservation", "check", async r => { await res.ConfirmAsync(r.Id); p.StatusMessage = $"Reservation #{r.Id} confirmed."; await p.ReloadAsync(); }, r => r.Status == ResStatus.Pending);
        p.Act("Walk-in", "user-plus", async () =>
        {
            var guests = (await new GuestService(s).SearchAsync()).Select(g => new Choice(g.Id, $"{g.FullName}  ({(g.Phone.Length > 0 ? g.Phone : g.Email)})")).ToList();
            if (guests.Count == 0) { await ctx.Dialogs.MessageAsync("No guests yet", "Register the guest first in the Guest directory.", true); return; }
            var types = (await new RoomService(s).TypesAsync(true)).Select(t => new Choice(t.Id, t.Name)).ToList();
            var f = await ctx.Dialogs.FormAsync("Walk-in check-in", "Step 1 of 2: the guest and what they need.", new[]
            {
                FormField.Combo("guest", "Guest", guests), FormField.Int("nights", "Nights", 1, true, 1, 365), FormField.Int("n", "Number of guests", 1, true, 1, 20),
                FormField.Combo("type", "Room type", types, null, required: false, hint: "Leave empty for any type."),
            }, "Find a room");
            if (f is null) return;
            var checkOut = DateTime.Today.AddDays(f.Int("nights"));
            var offers = (await new RoomService(s).AvailableAsync(DateTime.Today, checkOut, f.Int("n"), f.Get<int?>("type"))).Where(o => o.Status == RoomStatus.Available).ToList();
            var auto = await stays.AutoAssignAsync(checkOut, f.Int("n"), f.Get<int?>("type"));
            if (offers.Count == 0 || auto is null) { await ctx.Dialogs.MessageAsync("No suitable room available", "No clean room fits this guest right now, so the walk-in cannot be checked in.", true); return; }
            var f2 = await ctx.Dialogs.FormAsync("Walk-in check-in", "Step 2 of 2: the best-fitting room is pre-selected. Review and confirm.", new[]
            {
                FormField.Combo("room", "Room", offers.Select(o => new Choice(o.Id, o.Display)), auto.Id), FormField.Multi("notes", "Notes"),
            }, "Confirm check-in");
            if (f2 is null) return;
            await stays.WalkInAsync(f.Get<int>("guest"), f2.Get<int>("room"), f.Int("nights"), f.Int("n"), f2.Str("notes"));
            p.StatusMessage = "Walk-in guest checked in."; await p.ReloadAsync();
        });
        return p;
    }

    public static ListPageViewModel CheckOut(PageContext ctx)
    {
        var s = ctx.Session; var stays = new StayService(s); var res = new ReservationService(s);
        var p = new ListPageViewModel(ctx, "Check-out", "Guests currently in the hotel. Checking out ends the stay and sends the room to housekeeping.",
            new[] { new ColumnDef("Guest", nameof(StayRow.GuestName), Width: 190), new ColumnDef("Room", nameof(StayRow.RoomNumber), Width: 70), new ColumnDef("Type", nameof(StayRow.RoomType), Width: 120),
                    new ColumnDef("Checked in", nameof(StayRow.CheckInAt), ColumnKind.DateTime, 150), new ColumnDef("Expected out", nameof(StayRow.ExpectedCheckOutDate), ColumnKind.Date, 110),
                    new ColumnDef("Total", nameof(StayRow.TotalAmount), ColumnKind.Money, 100), new ColumnDef("Balance", nameof(StayRow.Balance), ColumnKind.Money, 100) },
            async q => (await stays.ListAsync(true, q.SearchOrNull)).Cast<object>().ToList(), "Nobody is checked in.", searchHint: "Search guest or room…");
        p.Act<StayRow>("Check out", "log-out", async r =>
        {
            var msg = r.Balance > 0 ? $"{r.GuestName} still owes {r.Balance:N2}. You can check them out and record payment afterwards, or cancel and record it first." : $"Check {r.GuestName} out of room {r.RoomNumber}?";
            if (!await ctx.Dialogs.ConfirmAsync("Check out", msg, "Check out")) return;
            var result = await stays.CheckOutAsync(r.StayId);
            p.StatusMessage = $"{r.GuestName} checked out. Room {result.RoomNumber} is Dirty and a cleaning task was created." + (result.Balance > 0 ? $" Outstanding balance: {result.Balance:N2}." : "");
            await p.ReloadAsync();
        }, primary: true);
        p.Act<StayRow>("Record payment", "banknote", r => AddPayment(ctx, res, p, r.ReservationId, r.Balance));
        return p;
    }

    public static ListPageViewModel StayHistory(PageContext ctx, string? title = null, string? description = null)
    {
        var stays = new StayService(ctx.Session);
        var res = new ReservationService(ctx.Session);
        var p = new ListPageViewModel(ctx, title ?? "Stays", description ?? "Every check-in and check-out.",
            new[] { new ColumnDef("Guest", nameof(StayRow.GuestName), Width: 190), new ColumnDef("Room", nameof(StayRow.RoomNumber), Width: 70), new ColumnDef("Type", nameof(StayRow.RoomType), Width: 120),
                    new ColumnDef("Checked in", nameof(StayRow.CheckInAt), ColumnKind.DateTime, 150), new ColumnDef("Expected out", nameof(StayRow.ExpectedCheckOutDate), ColumnKind.Date, 110),
                    new ColumnDef("Checked out", nameof(StayRow.CheckOutAt), ColumnKind.DateTime, 150),
                    new ColumnDef("Status", nameof(StayRow.StayStatus), ColumnKind.Badge, 100), new ColumnDef("Total", nameof(StayRow.TotalAmount), ColumnKind.Money, 100), new ColumnDef("Balance", nameof(StayRow.Balance), ColumnKind.Money, 100) },
            async q => (await stays.ListAsync(q.Str("scope") == "active", q.SearchOrNull)).Cast<object>().ToList(), "No stays recorded yet.", searchHint: "Search guest or room…");
        p.AddTabs("scope", ("All stays", "all"), ("Active only", "active"));

        p.Act<StayRow>("Check out", "log-out", async r =>
        {
            var msg = r.Balance > 0 ? $"{r.GuestName} still owes {r.Balance:N2}. Check out now?" : $"Check {r.GuestName} out of room {r.RoomNumber}?";
            if (!await ctx.Dialogs.ConfirmAsync("Check out", msg, "Check out")) return;
            var result = await stays.CheckOutAsync(r.StayId);
            p.StatusMessage = $"{r.GuestName} checked out. Room {result.RoomNumber} is Dirty and a cleaning task was created." + (result.Balance > 0 ? $" Outstanding balance: {result.Balance:N2}." : "");
            await p.ReloadAsync();
        }, r => r.StayStatus == "Active");

        p.Act<StayRow>("Extend stay", "history", async r =>
        {
            var f = await ctx.Dialogs.FormAsync($"Extend stay: {r.GuestName}", $"Room {r.RoomNumber}. Currently expected out on {r.ExpectedCheckOutDate:d}.", new[]
            {
                FormField.Int("days", "Additional days", 1, true, 1, 30)
            }, "Extend stay");
            if (f is null) return;
            await stays.ExtendStayAsync(r.StayId, f.Int("days"));
            p.StatusMessage = $"Stay extended by {f.Int("days")} day(s).";
            await p.ReloadAsync();
        }, r => r.StayStatus == "Active");

        p.Act<StayRow>("Record payment", "banknote", r => AddPayment(ctx, res, p, r.ReservationId, r.Balance));
        return p;
    }

    public static ListPageViewModel Payments(PageContext ctx)
    {
        var res = new ReservationService(ctx.Session);
        return new ListPageViewModel(ctx, "Payments", "Recent payments, refunds and pending amounts.",
            new[] { new ColumnDef("When", nameof(PaymentRow.CreatedAt), ColumnKind.DateTime, 150), new ColumnDef("Reservation", nameof(PaymentRow.ReservationId), ColumnKind.Number, 95), new ColumnDef("Guest", nameof(PaymentRow.GuestName), Width: 180),
                    new ColumnDef("Room", nameof(PaymentRow.RoomNumber), Width: 65), new ColumnDef("Amount", nameof(PaymentRow.Amount), ColumnKind.Money, 100), new ColumnDef("Method", nameof(PaymentRow.Method), Width: 110),
                    new ColumnDef("Status", nameof(PaymentRow.Status), ColumnKind.Badge, 100), new ColumnDef("Reference", nameof(PaymentRow.Reference)), new ColumnDef("Recorded by", nameof(PaymentRow.RecordedBy), Width: 130) },
            async q => (await res.PaymentsAsync()).Where(x => q.SearchOrNull is not { } t || x.GuestName.Contains(t, StringComparison.OrdinalIgnoreCase) || x.RoomNumber.Contains(t, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList(),
            "No payments recorded yet.", searchHint: "Search guest or room…");
    }

    // ================================================================================== requests & maintenance
    public static ListPageViewModel Requests(PageContext ctx, bool archiveView = false, string? title = null, string? description = null, bool housekeepingOnly = false)
    {
        var s = ctx.Session; var svc = new RequestService(s); var admin = s.Role == Roles.Admin; var hk = s.Role == Roles.Housekeeper;
        var p = new ListPageViewModel(ctx, title ?? "Guest requests", description ?? "Cleaning, item, maintenance and assistance requests from guests.",
            new[] { new ColumnDef("#", nameof(RequestRow.Id), ColumnKind.Number, 55), new ColumnDef("Room", nameof(RequestRow.RoomNumber), Width: 65), new ColumnDef("Guest", nameof(RequestRow.GuestName), Width: 150),
                    new ColumnDef("Category", nameof(RequestRow.Category), ColumnKind.Badge, 140), new ColumnDef("Details", nameof(RequestRow.Description)), new ColumnDef("Items", nameof(RequestRow.ItemsText), Width: 150),
                    new ColumnDef("Priority", nameof(RequestRow.Priority), ColumnKind.Badge, 90), new ColumnDef("Status", nameof(RequestRow.Status), ColumnKind.Badge, 120), new ColumnDef("Assigned to", nameof(RequestRow.AssignedTo), Width: 130),
                    new ColumnDef("Submitted", nameof(RequestRow.CreatedAt), ColumnKind.DateTime, 140) },
            async q => (await svc.ListAsync(null, q.Str("category"), q.SearchOrNull, archiveView || q.Str("scope") == "archived", !archiveView && (q.Str("scope") ?? "open") == "open")).Cast<object>().ToList(),
            "No requests here.", searchHint: "Search room, guest or text…");
        if (!archiveView)
            p.AddTabs("scope", admin ? new (string, object?)[] { ("Open", "open"), ("All", "all"), ("Archived", "archived") } : new (string, object?)[] { ("Open", "open"), ("All", "all") });
        p.AddCombo("category", "Category", Strs(housekeepingOnly || hk ? new[] { Category.Cleaning, Category.AdditionalItems } : Category.All), allLabel: "All categories");

        if (!archiveView)
        {
            p.Act<RequestRow>("Update status", "refresh", async r =>
            {
                var next = RequestService.Flow.TryGetValue(r.Status, out var n) ? n : Array.Empty<string>();
                if (hk) next = next.Where(x => x is ReqStatus.InProgress or ReqStatus.Fulfilled or ReqStatus.UnableToFulfill).ToArray();
                if (r.Category == Category.Maintenance) next = Array.Empty<string>();
                if (next.Length == 0) { await ctx.Dialogs.MessageAsync("Nothing to update", r.Category == Category.Maintenance ? "Maintenance requests progress through their maintenance report. Open Maintenance reports." : $"A {Labels.Of(r.Status).ToLower()} request cannot change status.", false); return; }
                var f = await ctx.Dialogs.FormAsync($"Request #{r.Id}: update status", $"Now {Labels.Of(r.Status)}.", new[]
                {
                    FormField.Combo("to", "New status", Strs(next), next[0]), FormField.Multi("note", "Note to the guest", hint: "Required when rejecting or unable to fulfil."),
                }, "Update");
                if (f is null) return;
                await svc.UpdateStatusAsync(r.Id, f.Get<string>("to")!, f.Str("note"));
                p.StatusMessage = $"Request #{r.Id} is now {Labels.Of(f.Get<string>("to"))}. The guest was notified."; await p.ReloadAsync();
            }, r => ReqStatus.IsOpen(r.Status), primary: true);
            if (!hk)
                p.Act<RequestRow>("Assign", "user", async r =>
                {
                    var staff = await new StaffService(s).AssigneesAsync(Roles.Housekeeper);
                    var f = await ctx.Dialogs.FormAsync($"Request #{r.Id}: assign", "Cleaning and item requests go to a housekeeper.", new[]
                    { FormField.Combo("who", "Assign to", staff.Select(x => new Choice(x.Id, x.FullName)), r.AssignedToId, required: false, hint: "Leave empty to unassign.") }, "Assign");
                    if (f is null) return;
                    await svc.AssignAsync(r.Id, f.Get<int?>("who"));
                    p.StatusMessage = "Assignment saved."; await p.ReloadAsync();
                }, r => ReqStatus.IsOpen(r.Status) && r.Category != Category.Maintenance);
        }
        if (admin)
            p.Act<RequestRow>(archiveView || false ? "Restore" : "Archive / restore", "archive", async r =>
            {
                if (ReqStatus.IsOpen(r.Status)) { await ctx.Dialogs.MessageAsync("Request still open", "Only completed or rejected requests can be archived.", true); return; }
                await svc.SetArchivedAsync(r.Id, !r.IsArchived);
                p.StatusMessage = r.IsArchived ? "Request restored." : "Request archived. It stays on record."; await p.ReloadAsync();
            });
        return p;
    }

    public static ListPageViewModel Maintenance(PageContext ctx, bool archiveView = false, string? title = null, string? description = null)
    {
        var s = ctx.Session; var svc = new MaintenanceService(s); var admin = s.Role == Roles.Admin; var hk = s.Role == Roles.Housekeeper;
        var p = new ListPageViewModel(ctx, title ?? "Maintenance reports", description ?? "Room problems reported by guests and staff, from report to resolution.",
            new[] { new ColumnDef("#", nameof(MaintenanceRow.Id), ColumnKind.Number, 55), new ColumnDef("Room", nameof(MaintenanceRow.RoomNumber), Width: 65), new ColumnDef("Issue", nameof(MaintenanceRow.IssueType), Width: 160),
                    new ColumnDef("Description", nameof(MaintenanceRow.Description)), new ColumnDef("From", nameof(MaintenanceRow.Source), Width: 80), new ColumnDef("Reported by", nameof(MaintenanceRow.ReportedBy), Width: 130),
                    new ColumnDef("Blocks room", nameof(MaintenanceRow.BlocksOccupancy), ColumnKind.Bool, 95), new ColumnDef("Status", nameof(MaintenanceRow.Status), ColumnKind.Badge, 110),
                    new ColumnDef("Assigned to", nameof(MaintenanceRow.AssignedTo), Width: 130), new ColumnDef("Reported", nameof(MaintenanceRow.CreatedAt), ColumnKind.DateTime, 140) },
            async q => (await svc.ListAsync(!archiveView && (q.Str("scope") ?? "open") == "open", archiveView || q.Str("scope") == "archived", q.SearchOrNull)).Cast<object>().ToList(),
            "No reports here.", searchHint: "Search room or issue…");
        if (!archiveView)
            p.AddTabs("scope", admin ? new (string, object?)[] { ("Unresolved", "open"), ("All", "all"), ("Archived", "archived") } : new (string, object?)[] { ("Unresolved", "open"), ("All", "all") });

        if (!archiveView)
        {
            p.Act("Report a problem", "alert", () => ReportProblem(ctx, p, null), primary: true);
            if (!hk)
                p.Act<MaintenanceRow>("Assign", "user", async m =>
                {
                    var staff = await new StaffService(s).AssigneesAsync(Roles.Housekeeper);
                    if (staff.Count == 0) { await ctx.Dialogs.MessageAsync("No staff available", "There are no active housekeeping staff to assign.", true); return; }
                    var f = await ctx.Dialogs.FormAsync($"Report #{m.Id}: assign", $"Room {m.RoomNumber}: {m.IssueType}", new[] { FormField.Combo("who", "Assign to", staff.Select(x => new Choice(x.Id, x.FullName)), m.AssignedToId) }, "Assign");
                    if (f is null) return;
                    await svc.AssignAsync(m.Id, f.Get<int>("who")); p.StatusMessage = "Report assigned."; await p.ReloadAsync();
                }, m => m.Status is ReportStatus.Open or ReportStatus.Assigned);
            p.Act<MaintenanceRow>("Start work", "play", async m => { await svc.SetStatusAsync(m.Id, ReportStatus.InProgress, null); p.StatusMessage = "Marked In Progress."; await p.ReloadAsync(); },
                m => m.Status is ReportStatus.Open or ReportStatus.Assigned && (!hk || m.AssignedToId == s.UserId));
            p.Act<MaintenanceRow>("Mark resolved", "check", async m =>
            {
                var f = await ctx.Dialogs.FormAsync($"Report #{m.Id}: resolved", $"Room {m.RoomNumber}: {m.IssueType}", new[] { FormField.Multi("res", "What was done?", required: true) }, "Mark resolved");
                if (f is null) return;
                await svc.SetStatusAsync(m.Id, ReportStatus.Resolved, f.Str("res")); p.StatusMessage = "Report resolved. Front desk will confirm and close it."; await p.ReloadAsync();
            }, m => m.Status is ReportStatus.Open or ReportStatus.Assigned or ReportStatus.InProgress && (!hk || m.AssignedToId == s.UserId));
            if (!hk)
                p.Act<MaintenanceRow>("Confirm & close", "shield-check", async m =>
                {
                    if (!await ctx.Dialogs.ConfirmAsync("Close report", $"Confirm the issue in room {m.RoomNumber} is fixed and close report #{m.Id}?", "Close report")) return;
                    await svc.SetStatusAsync(m.Id, ReportStatus.Closed, null); p.StatusMessage = "Report closed."; await p.ReloadAsync();
                }, m => m.Status == ReportStatus.Resolved);
        }
        if (admin)
            p.Act<MaintenanceRow>("Archive / restore", "archive", async m =>
            {
                if (m.Status != ReportStatus.Closed && !m.IsArchived) { await ctx.Dialogs.MessageAsync("Report not closed", "Only closed reports can be archived.", true); return; }
                await svc.SetArchivedAsync(m.Id, !m.IsArchived); p.StatusMessage = m.IsArchived ? "Report restored." : "Report archived. It stays on record."; await p.ReloadAsync();
            });
        return p;
    }

    internal static async Task ReportProblem(PageContext ctx, PageViewModel page, int? roomId)
    {
        var rooms = (await new RoomService(ctx.Session).ListAsync()).Select(r => new Choice(r.Id, r.Display)).ToList();
        var f = await ctx.Dialogs.FormAsync("Report a room problem", "Blocking issues put the room Out of Service until they are resolved.", new[]
        {
            FormField.Combo("room", "Room", rooms, roomId), FormField.Combo("type", "Issue", HousekeepingIssueTypes().Select(t => new Choice(t, t)), "Other"),
            FormField.Multi("desc", "What did you find?", required: true), FormField.Check("block", "Unsafe or unsuitable for guests: take the room out of service"),
        }, "Submit report");
        if (f is null) return;
        await new HousekeepingService(ctx.Session).ReportProblemAsync(f.Get<int>("room"), f.Get<string>("type")!, f.Str("desc"), f.Bool("block"));
        page.StatusMessage = "Problem reported. Front desk has been notified."; await page.ReloadAsync(); await ctx.RefreshBadges();
    }

    static IEnumerable<string> HousekeepingIssueTypes() => IssueTypes.All;
}
