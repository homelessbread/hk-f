using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>One row of the reservation board: a room plus the reservation or stay that owns it right now.</summary>
public sealed record ReservationRoomRow(int Id, string RoomNumber, string Building, int Floor, string RoomType, int Capacity, decimal Rate,
    string Status, string BoardState, string Guest, string Detail, int OpenIssues, int? ReservationId, string ReservationStatus, int? StayId);

/// <summary>The receptionist's single Reservation menu: the room status board. Right-clicking a room offers only the
/// actions that are valid for its current state (Available / Reserved / Occupied).</summary>
public static class ReceptionistPages
{
    public const string Reserved = "Reserved";

    public static ListPageViewModel Reservation(PageContext ctx)
    {
        var s = ctx.Session;
        var rooms = new RoomService(s);
        var res = new ReservationService(s);
        var stays = new StayService(s);

        var p = new ListPageViewModel(ctx, "Reservation",
            "Room status board. Right-click a room for the actions its status allows: reserve or check in (Available), view / confirm / check in (Reserved), view stay, check out early or extend (Occupied).",
            new[]
            {
                new ColumnDef("Room", nameof(ReservationRoomRow.RoomNumber), Width: 80),
                new ColumnDef("Building", nameof(ReservationRoomRow.Building), Width: 90),
                new ColumnDef("Floor", nameof(ReservationRoomRow.Floor), ColumnKind.Number, 60),
                new ColumnDef("Type", nameof(ReservationRoomRow.RoomType), Width: 120),
                new ColumnDef("Sleeps", nameof(ReservationRoomRow.Capacity), ColumnKind.Number, 65),
                new ColumnDef("Rate / night", nameof(ReservationRoomRow.Rate), ColumnKind.Money, 110),
                new ColumnDef("Status", nameof(ReservationRoomRow.BoardState), ColumnKind.Badge, 120),
                new ColumnDef("Guest", nameof(ReservationRoomRow.Guest)),
                new ColumnDef("Details", nameof(ReservationRoomRow.Detail)),
                new ColumnDef("Open issues", nameof(ReservationRoomRow.OpenIssues), ColumnKind.Number, 95),
            },
            async q => await LoadAsync(q),
            "No rooms match.", searchHint: "Search room number or type…");
        p.AddCombo("status", "Status",
            SharedPages.Strs(new[] { RoomStatus.Available, Reserved, RoomStatus.Occupied, RoomStatus.Dirty, RoomStatus.Cleaning, RoomStatus.OutOfService }),
            allLabel: "All statuses");
        p.SummaryCards = rows =>
        {
            var r = rows.Cast<ReservationRoomRow>().ToList();
            return new[] { RoomStatus.Available, Reserved, RoomStatus.Occupied, RoomStatus.Dirty, RoomStatus.Cleaning, RoomStatus.OutOfService }
                .Select(st => new StatCard(st == Reserved ? Reserved : Labels.Of(st), r.Count(x => x.BoardState == st).ToString())).ToList();
        };

        // global: create a reservation for any available room (category filter + room picker)
        p.Act("New reservation", "calendar", () => SharedPages.NewReservation(ctx, res, p), primary: true);

        // Available: create a reservation for this room, or check a walk-in straight in
        p.Act<ReservationRoomRow>("Reserve this room", "calendar-check", r => ReserveRoom(ctx, p, r),
            r => r.BoardState == RoomStatus.Available);
        p.Act<ReservationRoomRow>("Check in guest", "log-in", r => WalkIn(ctx, p, r),
            r => r.BoardState == RoomStatus.Available);

        // Reserved: view the reservation and run the valid check-in actions
        p.Act<ReservationRoomRow>("View reservation", "calendar", r => ViewReservation(ctx, res, r),
            r => r.BoardState == Reserved && r.ReservationId is not null);
        p.Act<ReservationRoomRow>("Confirm reservation", "check",
            async r => { await res.ConfirmAsync(r.ReservationId!.Value); p.StatusMessage = $"Reservation #{r.ReservationId} confirmed."; await p.ReloadAsync(); },
            r => r.BoardState == Reserved && r.ReservationStatus == ResStatus.Pending);
        p.Act<ReservationRoomRow>("Check in reservation", "log-in", r => CheckInReservation(ctx, p, stays, r),
            r => r.BoardState == Reserved && r.ReservationStatus == ResStatus.Confirmed);

        // Occupied: stay details, early check-out, extension
        p.Act<ReservationRoomRow>("View stay details", "users", r => ViewStay(ctx, stays, r),
            r => r.StayId is not null);
        p.Act<ReservationRoomRow>("Check out early", "log-out", r => CheckOutEarly(ctx, p, stays, r),
            r => r.StayId is not null);
        p.Act<ReservationRoomRow>("Extend stay", "history", r => Extend(ctx, p, stays, r),
            r => r.StayId is not null);
        return p;

        async Task<List<object>> LoadAsync(ListQuery q)
        {
            var board = await rooms.ListAsync(null, q.SearchOrNull, false);
            var arrivals = await res.ListAsync("arrivals", null, null);
            var active = await stays.ListAsync(true);
            var byRes = arrivals.GroupBy(x => x.RoomId).ToDictionary(g => g.Key, g => g.First());
            var byStay = active.GroupBy(x => x.RoomId).ToDictionary(g => g.Key, g => g.First());

            var rows = new List<ReservationRoomRow>();
            foreach (var rm in board)
            {
                byStay.TryGetValue(rm.Id, out var st);
                ReservationRow? rr = null;
                if (st is null && byRes.TryGetValue(rm.Id, out var x)) rr = x;

                string state, guest = rm.CurrentGuest, detail = "";
                int? resId = null, stayId = null;
                var resStatus = "";
                if (st is not null)
                {
                    state = RoomStatus.Occupied; guest = st.GuestName; stayId = st.StayId; resId = st.ReservationId;
                    detail = $"Checked in {st.CheckInAt.ToLocalTime():d} · out {st.ExpectedCheckOutDate:d}" + (st.ExpectedCheckOutAt is { } t ? $" {t:HH:mm}" : "");
                }
                else if (rr is not null)
                {
                    state = Reserved; guest = rr.GuestName; resId = rr.Id; resStatus = rr.Status;
                    detail = $"{Labels.Of(rr.Status)} · {rr.CheckInDate:d} – {rr.CheckOutDate:d} · {rr.GuestCount} guest(s)";
                }
                else if (rm.Status == RoomStatus.Occupied) { state = RoomStatus.Occupied; detail = "Occupied"; }
                else { state = rm.Status; detail = rm.Status == RoomStatus.Available ? "Ready to sell" : Labels.Of(rm.Status); }

                rows.Add(new ReservationRoomRow(rm.Id, rm.RoomNumber, rm.Building, rm.Floor, rm.RoomType, rm.Capacity, rm.Rate,
                    rm.Status, state, guest, detail, rm.OpenIssues, resId, resStatus, stayId));
            }
            var filter = q.Str("status");
            if (filter is not null) rows = rows.Where(r => r.BoardState == filter).ToList();
            return rows.Cast<object>().ToList();
        }
    }

    static async Task ReserveRoom(PageContext ctx, ListPageViewModel p, ReservationRoomRow room)
    {
        var s = ctx.Session;
        var guests = (await new GuestService(s).SearchAsync())
            .Select(g => new Choice(g.Id, $"{g.FullName}  ({(g.Phone.Length > 0 ? g.Phone : g.Email)})")).ToList();
        if (guests.Count == 0) { await ctx.Dialogs.MessageAsync("No guests yet", "Register the guest first in the Guest directory.", true); return; }

        var f = await ctx.Dialogs.FormAsync($"Reserve room {room.RoomNumber}",
            $"{room.RoomType} · sleeps {room.Capacity} · {room.Rate:N2}/night. Availability is rechecked before the booking is saved.",
            new[]
            {
                FormField.Combo("guest", "Guest", guests),
                FormField.Day("in", "Check-in", DateTime.Today),
                FormField.Day("out", "Check-out", DateTime.Today.AddDays(1)),
                FormField.Int("n", "Number of guests", 2, true, 1, room.Capacity),
                FormField.Multi("notes", "Notes"),
            }, "Check availability");
        if (f is null) return;
        ReservationRules.ValidateDates(f.Day("in"), f.Day("out"), f.Int("n"));

        var offers = await new RoomService(s).AvailableAsync(f.Day("in"), f.Day("out"), f.Int("n"));
        if (offers.All(o => o.Id != room.Id))
        {
            await ctx.Dialogs.MessageAsync("Room not available",
                $"Room {room.RoomNumber} is not free for {f.Day("in"):d} – {f.Day("out"):d} (already reserved, not ready, or blocked). Use “New reservation” to pick another room.", true);
            return;
        }
        var id = await new ReservationService(s).CreateAsync(f.Get<int>("guest"), room.Id, f.Day("in"), f.Day("out"), f.Int("n"), f.Str("notes"), confirm: true);
        p.StatusMessage = $"Reservation #{id} created for room {room.RoomNumber}.";
        await p.ReloadAsync();
    }

    static async Task WalkIn(PageContext ctx, ListPageViewModel p, ReservationRoomRow room)
    {
        var s = ctx.Session;
        var guests = (await new GuestService(s).SearchAsync())
            .Select(g => new Choice(g.Id, $"{g.FullName}  ({(g.Phone.Length > 0 ? g.Phone : g.Email)})")).ToList();
        if (guests.Count == 0) { await ctx.Dialogs.MessageAsync("No guests yet", "Register the guest first in the Guest directory.", true); return; }

        var f = await ctx.Dialogs.FormAsync($"Check in to Room {room.RoomNumber}",
            $"{room.RoomType} · sleeps {room.Capacity} · {room.Rate:N2}/night. The room is rechecked and reserved in one step.",
            new[]
            {
                FormField.Combo("guest", "Guest", guests),
                FormField.Int("nights", "Nights", 1, true, 1, 365),
                FormField.Int("n", "Number of guests", 1, true, 1, room.Capacity),
                FormField.Multi("notes", "Notes"),
            }, "Check in");
        if (f is null) return;
        await new StayService(s).WalkInAsync(f.Get<int>("guest"), room.Id, f.Int("nights"), f.Int("n"), f.Str("notes"));
        p.StatusMessage = $"Guest checked in to room {room.RoomNumber}.";
        await p.ReloadAsync();
    }

    static async Task ViewReservation(PageContext ctx, ReservationService res, ReservationRoomRow room)
    {
        var r = await res.GetAsync(room.ReservationId!.Value);
        if (r is null) { await ctx.Dialogs.MessageAsync("Reservation not found", "This reservation is no longer available.", true); return; }
        await ctx.Dialogs.ShowLinesAsync($"Reservation #{r.Id}", $"{Labels.Of(r.Status)} · {r.GuestName}", new[]
        {
            $"Room:  {r.RoomNumber}  ({r.RoomType})",
            $"Dates:  {r.CheckInDate:d} – {r.CheckOutDate:d}  ({r.Nights} night(s))",
            $"Guests:  {r.GuestCount}",
            $"Total:  {r.TotalAmount:N2}   Paid:  {r.Paid:N2}   Balance:  {r.Balance:N2}",
            $"Created:  {r.CreatedAt.ToLocalTime():g}",
            string.IsNullOrWhiteSpace(r.Notes) ? "Notes:  —" : $"Notes:  {r.Notes}",
        });
    }

    static async Task CheckInReservation(PageContext ctx, ListPageViewModel p, StayService stays, ReservationRoomRow room)
    {
        var rooms = await stays.CandidateRoomsAsync(room.ReservationId!.Value);
        if (rooms.Count == 0)
        {
            await ctx.Dialogs.MessageAsync("No suitable room available",
                "No clean room that fits this guest is ready right now. Check Housekeeping or the board — check-in cannot be completed.", true);
            return;
        }
        var f = await ctx.Dialogs.FormAsync($"Check in to Room {room.RoomNumber}",
            $"Reservation #{room.ReservationId}: the reserved room is suggested first; change it if needed.",
            new[] { FormField.Combo("room", "Room", rooms.Select(o => new Choice(o.Id, o.Display + (o.Id == room.Id ? "   ← reserved" : ""))), rooms[0].Id) },
            "Confirm check-in");
        if (f is null) return;
        await stays.CheckInAsync(room.ReservationId!.Value, f.Get<int>("room"));
        p.StatusMessage = $"Guest checked in to room {room.RoomNumber}.";
        await p.ReloadAsync();
    }

    static async Task ViewStay(PageContext ctx, StayService stays, ReservationRoomRow room)
    {
        var st = (await stays.ListAsync(true)).FirstOrDefault(x => x.StayId == room.StayId);
        if (st is null) { await ctx.Dialogs.MessageAsync("Stay not found", "This stay has already ended.", true); return; }
        await ctx.Dialogs.ShowLinesAsync($"Stay #{st.StayId} · room {st.RoomNumber}", st.GuestName, new[]
        {
            $"Room type:  {st.RoomType}",
            $"Checked in:  {st.CheckInAt.ToLocalTime():f}",
            $"Expected out:  {st.ExpectedCheckOutDate:d}" + (st.ExpectedCheckOutAt is { } t ? $"  {t:HH:mm} (late check-out)" : ""),
            $"Total:  {st.TotalAmount:N2}   Balance:  {st.Balance:N2}",
        });
    }

    static async Task CheckOutEarly(PageContext ctx, ListPageViewModel p, StayService stays, ReservationRoomRow room)
    {
        var st = (await stays.ListAsync(true)).FirstOrDefault(x => x.StayId == room.StayId);
        if (st is null) { await ctx.Dialogs.MessageAsync("No active stay", $"Room {room.RoomNumber} has no active stay to check out.", true); return; }
        var early = st.ExpectedCheckOutDate > DateTime.Today ? $" Expected check-out is {st.ExpectedCheckOutDate:d}." : "";
        var msg = st.Balance > 0
            ? $"{st.GuestName} still owes {st.Balance:N2}.{early} Check out now?"
            : $"Check {st.GuestName} out of room {st.RoomNumber} now?{early}";
        if (!await ctx.Dialogs.ConfirmAsync("Check out early", msg, "Check out")) return;
        var result = await stays.CheckOutAsync(st.StayId);
        p.StatusMessage = result.Balance > 0
            ? $"{st.GuestName} checked out. Outstanding balance: {result.Balance:N2}."
            : $"{st.GuestName} checked out.";
        await p.ReloadAsync();
    }

    static async Task Extend(PageContext ctx, ListPageViewModel p, StayService stays, ReservationRoomRow room)
    {
        var st = (await stays.ListAsync(true)).FirstOrDefault(x => x.StayId == room.StayId);
        if (st is null) { await ctx.Dialogs.MessageAsync("No active stay", "This stay has already ended.", true); return; }
        var f = await ctx.Dialogs.FormAsync($"Extend stay: {st.GuestName}",
            $"Room {st.RoomNumber}. Currently expected out on {st.ExpectedCheckOutDate:d}"
            + (st.ExpectedCheckOutAt is { } t ? $" {t:HH:mm}" : "") + ".",
            new[]
            {
                FormField.Combo("unit", "Unit", new[] { new Choice("Days", "Days"), new Choice("Hours", "Hours") }, "Days"),
                FormField.Int("amount", "Amount", 1, true, 1, 30),
            }, "Extend stay");
        if (f is null) return;
        var byHours = f.Get<string>("unit") == "Hours";
        await stays.ExtendStayAsync(st.StayId, f.Int("amount"), byHours);
        p.StatusMessage = byHours
            ? $"Stay extended by {f.Int("amount")} hour(s)."
            : $"Stay extended by {f.Int("amount")} day(s).";
        await p.ReloadAsync();
    }
}
