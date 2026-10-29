using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>Pages of the guest window. Every service call takes the guest from the session, so a guest can only ever reach their own data.</summary>
public static class GuestPages
{
    public static ListPageViewModel FindRoom(PageContext ctx)
    {
        var rooms = new RoomService(ctx.Session); var res = new ReservationService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Find a room", "Choose your dates and number of guests to see the rooms you can book.",
            new[] { new ColumnDef("Room type", nameof(RoomOffer.RoomType), Width: 130), new ColumnDef("Room", nameof(RoomOffer.RoomNumber), Width: 70), new ColumnDef("Floor", nameof(RoomOffer.Floor), ColumnKind.Number, 60),
                    new ColumnDef("Sleeps", nameof(RoomOffer.Capacity), ColumnKind.Number, 70), new ColumnDef("Per night", nameof(RoomOffer.Rate), ColumnKind.Money, 105), new ColumnDef("Amenities", nameof(RoomOffer.Amenities)), new ColumnDef("About", nameof(RoomOffer.Description)) },
            async q =>
            {
                var from = q.Day("in") ?? DateTime.Today; var to = q.Day("out") ?? from.AddDays(1);
                ReservationRules.ValidateDates(from, to, q.Int("n") ?? 1);
                return (await rooms.AvailableAsync(from, to, q.Int("n") ?? 1, q.Int("type"))).Cast<object>().ToList();
            }, "No rooms are available for these dates. Try different dates or fewer guests.", searchable: false);
        p.AddDate("in", "Check-in", DateTime.Today); p.AddDate("out", "Check-out", DateTime.Today.AddDays(1)); p.AddInteger("n", "Guests", 2);
        p.AddCombo("type", "Room type", loader: async () => (await rooms.TypesAsync(true)).Select(t => new Choice(t.Id, t.Name)).ToList(), allLabel: "Any type");
        p.Act<RoomOffer>("Review & book", "calendar-check", async o =>
        {
            DateTime from = (p.Filters.First(f => f.Key == "in").Value as DateTime?) ?? DateTime.Today, to = (p.Filters.First(f => f.Key == "out").Value as DateTime?) ?? from.AddDays(1);
            var n = p.Filters.First(f => f.Key == "n").Value as int? ?? 1;
            var nights = (to - from).Days;
            var f2 = await ctx.Dialogs.FormAsync("Review your booking", "Your booking is sent to the front desk for confirmation.", new[]
            {
                FormField.InfoLine("Room", o.Display), FormField.InfoLine("Dates", $"{from:ddd d MMM yyyy} → {to:ddd d MMM yyyy}  ({nights} night{(nights == 1 ? "" : "s")})"),
                FormField.InfoLine("Guests", n.ToString()), FormField.InfoLine("Total", $"{o.Rate * nights:N2}  ({o.Rate:N2} × {nights})"), FormField.Multi("notes", "Special requests (optional)"),
            }, "Confirm booking");
            if (f2 is null) return;
            var id = await res.CreateAsync(null, o.Id, from, to, n, f2.Str("notes"));
            await ctx.Dialogs.MessageAsync("Booking received", $"Reservation #{id} is waiting for confirmation by the front desk. You can follow it in My reservations.");
            await ctx.Navigate("reservations");
        }, primary: true);
        return p;
    }

    public static ListPageViewModel MyReservations(PageContext ctx)
    {
        var svc = new ReservationService(ctx.Session);
        var p = new ListPageViewModel(ctx, "My reservations", "Your upcoming and past bookings.",
            new[] { new ColumnDef("#", nameof(ReservationRow.Id), ColumnKind.Number, 55), new ColumnDef("Room", nameof(ReservationRow.RoomNumber), Width: 70), new ColumnDef("Type", nameof(ReservationRow.RoomType), Width: 130),
                    new ColumnDef("Check-in", nameof(ReservationRow.CheckInDate), ColumnKind.Date, 110), new ColumnDef("Check-out", nameof(ReservationRow.CheckOutDate), ColumnKind.Date, 110), new ColumnDef("Nights", nameof(ReservationRow.Nights), ColumnKind.Number, 70),
                    new ColumnDef("Guests", nameof(ReservationRow.GuestCount), ColumnKind.Number, 70), new ColumnDef("Status", nameof(ReservationRow.Status), ColumnKind.Badge, 120), new ColumnDef("Total", nameof(ReservationRow.TotalAmount), ColumnKind.Money, 100),
                    new ColumnDef("Balance", nameof(ReservationRow.Balance), ColumnKind.Money, 100) },
            async _ => (await svc.MineAsync()).Cast<object>().ToList(), "You have no reservations yet. Use “Find a room” to make one.", searchable: false);
        p.Act("Find a room", "search", () => ctx.Navigate("find"), primary: true);
        p.Act<ReservationRow>("Cancel reservation", "x", async r =>
        {
            var hours = await SettingsService.GetAsync("CancellationHours", "24");
            var f = await ctx.Dialogs.FormAsync($"Cancel reservation #{r.Id}", $"{r.CheckInDate:d} – {r.CheckOutDate:d}. Confirmed bookings can be cancelled until {hours} hours before check-in.", new[] { FormField.Multi("why", "Reason (optional)") }, "Cancel reservation", danger: true);
            if (f is null) return;
            await svc.CancelAsync(r.Id, f.Str("why")); p.StatusMessage = $"Reservation #{r.Id} was cancelled."; await p.ReloadAsync();
        }, r => r.Status is ResStatus.Pending or ResStatus.Confirmed, danger: true);
        return p;
    }

    public static ListPageViewModel MyRequests(PageContext ctx)
    {
        var svc = new RequestService(ctx.Session);
        var p = new ListPageViewModel(ctx, "My requests", "Report a problem, ask for cleaning or extra items, and follow what happens next.",
            new[] { new ColumnDef("#", nameof(RequestRow.Id), ColumnKind.Number, 55), new ColumnDef("Submitted", nameof(RequestRow.CreatedAt), ColumnKind.DateTime, 140), new ColumnDef("Category", nameof(RequestRow.Category), ColumnKind.Badge, 140),
                    new ColumnDef("Details", nameof(RequestRow.Description)), new ColumnDef("Items", nameof(RequestRow.ItemsText), Width: 160), new ColumnDef("Status", nameof(RequestRow.Status), ColumnKind.Badge, 125), new ColumnDef("Response", nameof(RequestRow.Resolution), Width: 220) },
            async _ => (await svc.MineAsync()).Cast<object>().ToList(), "No requests yet.", searchable: false);
        p.Act("New request", "plus", () => NewRequest(ctx, svc, p, null), primary: true);
        p.Act("Report a problem", "alert", () => NewRequest(ctx, svc, p, Category.Maintenance));
        p.Act("Request cleaning", "sparkles", () => NewRequest(ctx, svc, p, Category.Cleaning));
        p.Act("Request items", "package", () => NewRequest(ctx, svc, p, Category.AdditionalItems));
        return p;
    }

    static async Task NewRequest(PageContext ctx, RequestService svc, ListPageViewModel p, string? preset)
    {
        var stay = await new StayService(ctx.Session).MyActiveStayAsync();
        if (stay is null) { await ctx.Dialogs.MessageAsync("No active stay", "You can send requests only while you are checked in to a room. You have no active stay right now: you are either not checked in yet or have already checked out.", true); return; }

        var category = preset;
        if (category is null)
        {
            var c = await ctx.Dialogs.FormAsync("New request", $"For room {stay.RoomNumber}. What do you need?", new[]
            { FormField.Combo("cat", "Category", new[] { new Choice(Category.Maintenance, "Report a problem"), new Choice(Category.Cleaning, "Room cleaning"), new Choice(Category.AdditionalItems, "Additional items"), new Choice(Category.GeneralAssistance, "General assistance") }, Category.Cleaning) }, "Next");
            if (c is null) return; category = c.Get<string>("cat");
        }

        var fields = new List<FormField>();
        List<RequestableItemRow> items = new();
        if (category == Category.Maintenance) fields.Add(FormField.Combo("issue", "What kind of problem?", IssueTypes.All.Select(t => new Choice(t, t)), "Other"));
        if (category == Category.AdditionalItems)
        {
            items = await svc.ItemsAsync(true);
            fields.AddRange(items.Select(i => FormField.Int("i" + i.Id, i.Name + (i.Rule.Length > 0 ? $"  ({i.Rule})" : ""), 0, false, 0, i.MaxQuantity)));
        }
        fields.Add(FormField.Multi("desc", category == Category.AdditionalItems ? "Anything else we should know? (optional)" : "Describe what you need", required: category != Category.AdditionalItems));
        fields.Add(FormField.Combo("pri", "How urgent is it?", SharedPages.Strs(Priorities.All), "Normal"));

        var title = category switch { Category.Maintenance => "Report a problem", Category.Cleaning => "Request cleaning", Category.AdditionalItems => "Request items", _ => "Ask for assistance" };
        var f = await ctx.Dialogs.FormAsync(title, $"Room {stay.RoomNumber}. The request is linked to your stay automatically.", fields, "Send request");
        if (f is null) return;
        var lines = items.Where(i => f.Get<int>("i" + i.Id) > 0).Select(i => new RequestLine(i.Id, f.Get<int>("i" + i.Id))).ToList();
        var id = await svc.CreateAsync(category!, f.Str("desc"), f.Get<string>("pri")!, f.Get<string>("issue"), lines);
        p.StatusMessage = $"Request #{id} sent. We will keep you updated here."; await p.ReloadAsync(); await ctx.RefreshBadges();
    }

    public static ListPageViewModel RoomInformation(PageContext ctx)
    {
        var rooms = new RoomService(ctx.Session);
        var stays = new StayService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Room information", "What is in your room.",
            new[] { new ColumnDef("Item", nameof(RoomItemRow.ItemName), Width: 240), new ColumnDef("Quantity", nameof(RoomItemRow.Quantity), ColumnKind.Number, 100), new ColumnDef("Condition", nameof(RoomItemRow.ConditionStatus), ColumnKind.Badge, 130) },
            async _ => (await rooms.MyRoomItemsAsync()).Cast<object>().ToList(), "Room information appears here while you are checked in.", searchable: false);
        p.Act("Stay details", "bed", async () =>
        {
            var st = await stays.MyActiveStayAsync();
            if (st is null) { await ctx.Dialogs.MessageAsync("No active stay", "You are not checked in right now.", false); return; }
            await ctx.Dialogs.ShowLinesAsync("Your stay", null, new[] { $"Room:  {st.RoomNumber}  ({st.RoomType})", $"Location:  {st.Building}, floor {st.Floor}", $"Checked in:  {st.CheckInAt.ToLocalTime():f}", $"Expected check-out:  {st.ExpectedCheckOutDate:D}" });
        }, primary: true);
        return p;
    }

    public static FormPageViewModel Profile(PageContext ctx)
    {
        var svc = new GuestService(ctx.Session);
        return new FormPageViewModel(ctx, "My profile", "Keep your contact details up to date. Name and ID can be changed by the front desk.",
            async () =>
            {
                var g = await svc.GetMineAsync();
                return new[]
                {
                    FormField.InfoLine("Name", g.FullName), FormField.InfoLine("Username", g.Username), FormField.Txt("email", "Email", g.Email), FormField.Txt("phone", "Phone", g.Phone, hint: "Phone or email is required."), FormField.Txt("addr", "Address", g.Address),
                };
            },
            async f => { await svc.UpdateMyProfileAsync(f.Str("email"), f.Str("phone"), f.Str("addr")); return "Your profile was saved."; });
    }
}
