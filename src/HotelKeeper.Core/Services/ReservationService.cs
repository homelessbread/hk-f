using System.Data.Common;
using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;

public sealed record ReservationRow(int Id, int GuestId, string GuestName, int RoomId, string RoomNumber, string RoomType, DateTime CheckInDate,
    DateTime CheckOutDate, int Nights, int GuestCount, string Status, decimal NightlyRate, decimal TotalAmount, decimal Paid, decimal Balance,
    DateTime CreatedAt, string Notes);

public sealed record PaymentRow(int Id, int ReservationId, string GuestName, string RoomNumber, decimal Amount, string Method, string Status,
    string Reference, string RecordedBy, DateTime CreatedAt);

public static class ReservationRules
{
    public const int MaxNights = 60;

    public static void ValidateDates(DateTime checkIn, DateTime checkOut, int guests)
    {
        if (checkIn.Date < DateTime.Today) throw new DomainException("The check-in date cannot be in the past.");
        if (checkOut.Date <= checkIn.Date) throw new DomainException("The check-out date must be after the check-in date.");
        if ((checkOut.Date - checkIn.Date).Days > MaxNights) throw new DomainException($"A stay cannot be longer than {MaxNights} nights.");
        if (guests < 1) throw new DomainException("At least one guest is required.");
    }
}

public sealed class ReservationService(Session s)
{
    internal const string Select =
        "SELECT r.Id, r.GuestId, g.FullName AS GuestName, r.RoomId, rm.RoomNumber, t.Name AS RoomType, r.CheckInDate, r.CheckOutDate, " +
        "DATEDIFF(r.CheckOutDate, r.CheckInDate) AS Nights, r.GuestCount, r.Status, r.NightlyRate, r.TotalAmount, " +
        "COALESCE((SELECT SUM(CASE WHEN p.Status='Paid' THEN p.Amount WHEN p.Status='Refunded' THEN -p.Amount ELSE 0 END) FROM Payments p WHERE p.ReservationId=r.Id),0) AS Paid, " +
        "r.TotalAmount - COALESCE((SELECT SUM(CASE WHEN p.Status='Paid' THEN p.Amount WHEN p.Status='Refunded' THEN -p.Amount ELSE 0 END) FROM Payments p WHERE p.ReservationId=r.Id),0) AS Balance, " +
        "r.CreatedAt, r.Notes " +
        "FROM Reservations r JOIN Guests g ON g.Id=r.GuestId JOIN Rooms rm ON rm.Id=r.RoomId JOIN RoomTypes t ON t.Id=rm.RoomTypeId ";

    /// <summary>Staff view. scope: all | arrivals | departures | upcoming | history.</summary>
    public async Task<List<ReservationRow>> ListAsync(string scope = "all", string? status = null, string? search = null)
    {
        s.Require(Roles.Admin, Roles.Receptionist);
        var where = scope switch
        {
            "arrivals" => "r.CheckInDate <= @today AND r.CheckOutDate > @today AND r.Status IN ('Pending','Confirmed')",
            "departures" => "r.CheckOutDate = @today AND r.Status IN ('CheckedIn','CheckedOut')",
            "upcoming" => "r.Status IN ('Pending','Confirmed') AND r.CheckInDate >= @today",
            "history" => "r.Status IN ('CheckedOut','Cancelled')",
            _ => "1=1"
        };
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<ReservationRow>(
            Select + $"WHERE {where} AND (@st IS NULL OR r.Status=@st) AND (@q IS NULL OR g.FullName LIKE @q OR rm.RoomNumber LIKE @q OR CAST(r.Id AS CHAR) = @id) " +
            "ORDER BY r.CheckInDate DESC, r.Id DESC LIMIT 500",
            new { today = DateTime.Today, st = status, q = Like(search), id = search?.Trim() });
    }

    /// <summary>A guest's own reservations. The guest id comes from the session.</summary>
    public async Task<List<ReservationRow>> MineAsync()
    {
        var gid = s.RequireGuestId();
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<ReservationRow>(Select + "WHERE r.GuestId=@g ORDER BY r.CheckInDate DESC, r.Id DESC", new { g = gid });
    }

    public async Task<ReservationRow?> GetAsync(int id)
    {
        await using var db = await Db.OpenAsync();
        var r = await db.FirstAsync<ReservationRow>(Select + "WHERE r.Id=@id", new { id });
        if (r is not null && s.Role == Roles.Guest && r.GuestId != s.GuestId) return null;   // never reveal other guests' bookings
        if (s.Role == Roles.Housekeeper) throw new AuthorizationException("You do not have permission to do this.");
        return r;
    }

    static string? Like(string? q) => string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%";

    internal static async Task<int> CreateCoreAsync(Db db, Session by, int guestId, int roomId, DateTime checkIn, DateTime checkOut, int guests, string notes, string status)
    {
        checkIn = checkIn.Date; checkOut = checkOut.Date;
        ReservationRules.ValidateDates(checkIn, checkOut, guests);

        var g = await db.FirstAsync<GuestMini>("SELECT Id, FullName, IsArchived FROM Guests WHERE Id=@id", new { id = guestId }) ?? throw new DomainException("Guest not found.");
        if (g.IsArchived) throw new DomainException("This guest account is archived.");

        // Lock the room row: two people reserving the same room serialise here, so the overlap check below cannot be raced.
        var room = await db.FirstAsync<RoomMini>("SELECT Id, RoomNumber, Status, Capacity, Rate, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        if (room.IsArchived) throw new DomainException($"Room {room.RoomNumber} is not available.");
        if (room.Status == RoomStatus.OutOfService) throw new DomainException($"Room {room.RoomNumber} is Out of Service and cannot be reserved.");
        if (room.Capacity < guests) throw new DomainException($"Room {room.RoomNumber} sleeps {room.Capacity}; you have {guests} guests.");
        if (checkIn == DateTime.Today && room.Status != RoomStatus.Available)
            throw new DomainException($"Room {room.RoomNumber} is {Labels.Of(room.Status)} and not ready for a same-day arrival.");
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND BlocksOccupancy=1 AND Status IN ('Open','Assigned','InProgress')", new { r = roomId }) > 0)
            throw new DomainException($"Room {room.RoomNumber} has an unresolved maintenance or safety issue.");

        var clash = await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Reservations WHERE RoomId=@r AND Status IN ('Pending','Confirmed','CheckedIn') AND CheckInDate < @out AND CheckOutDate > @in",
            new { r = roomId, @in = checkIn, @out = checkOut });
        if (clash > 0) throw new DomainException($"Room {room.RoomNumber} is already reserved for part of {checkIn:d} – {checkOut:d}.");

        var own = await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Reservations WHERE GuestId=@g AND Status IN ('Pending','Confirmed','CheckedIn') AND CheckInDate < @out AND CheckOutDate > @in",
            new { g = guestId, @in = checkIn, @out = checkOut });
        if (own > 0) throw new DomainException("This guest already has a reservation that overlaps these dates.");

        var nights = (checkOut - checkIn).Days;
        var id = await db.InsertAsync(
            "INSERT INTO Reservations (GuestId, RoomId, CheckInDate, CheckOutDate, GuestCount, Status, NightlyRate, TotalAmount, Notes, CreatedById, CreatedAt, UpdatedAt) " +
            "VALUES (@g,@r,@in,@out,@n,@st,@rate,@total,@notes,@u,UTC_TIMESTAMP(),UTC_TIMESTAMP())",
            new { g = guestId, r = roomId, @in = checkIn, @out = checkOut, n = guests, st = status, rate = room.Rate, total = room.Rate * nights, notes = notes.Trim(), u = by.UserId });
        await Audit.LogAsync(db, by, "Reservation created", "Reservation", id, $"{g.FullName}, room {room.RoomNumber}, {checkIn:d}–{checkOut:d}, {status}");
        return id;
    }

    sealed record GuestMini(int Id, string FullName, bool IsArchived);
    sealed record RoomMini(int Id, string RoomNumber, string Status, int Capacity, decimal Rate, bool IsArchived);

    /// <summary>Guests book for themselves (Pending until front desk confirms); staff book for any guest and may confirm immediately.</summary>
    public async Task<int> CreateAsync(int? guestId, int roomId, DateTime checkIn, DateTime checkOut, int guests, string notes, bool confirm = false)
    {
        s.Require(Roles.Guest, Roles.Receptionist, Roles.Admin);
        var gid = s.Role == Roles.Guest ? s.RequireGuestId() : guestId ?? throw new DomainException("Choose a guest.");
        var status = s.Role == Roles.Guest || !confirm ? ResStatus.Pending : ResStatus.Confirmed;

        await using var db = await Db.BeginAsync();
        var id = await CreateCoreAsync(db, s, gid, roomId, checkIn, checkOut, guests, notes, status);
        if (s.Role == Roles.Guest)
            await Notify.RoleAsync(db, Roles.Receptionist, "New reservation", $"Reservation #{id} is waiting for confirmation.", "Reservation", id);
        else
            await Notify.GuestAsync(db, gid, "Reservation " + status.ToLower(), $"Reservation #{id} is {status.ToLower()}.", "Reservation", id);
        await db.CommitAsync();
        return id;
    }

    public async Task ConfirmAsync(int id)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var r = await LockAsync(db, id);
        if (r.Status != ResStatus.Pending) throw new DomainException($"Only a Pending reservation can be confirmed (this one is {Labels.Of(r.Status)}).");
        await db.ExecAsync("UPDATE Reservations SET Status='Confirmed', UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", new { id });
        await Audit.LogAsync(db, s, "Reservation confirmed", "Reservation", id, "");
        await Notify.GuestAsync(db, r.GuestId, "Reservation confirmed", $"Reservation #{id} is confirmed. We look forward to welcoming you.", "Reservation", id);
        await db.CommitAsync();
    }

    public async Task CancelAsync(int id, string reason)
    {
        s.Require(Roles.Guest, Roles.Receptionist, Roles.Admin);
        reason = reason.Trim();
        await using var db = await Db.BeginAsync();
        var r = await LockAsync(db, id);
        if (s.Role == Roles.Guest && r.GuestId != s.GuestId) throw new AuthorizationException("You can only manage your own reservations.");
        if (r.Status is not (ResStatus.Pending or ResStatus.Confirmed))
            throw new DomainException($"A {Labels.Of(r.Status).ToLower()} reservation cannot be cancelled.");

        if (s.Role == Roles.Guest && r.Status == ResStatus.Confirmed)
        {
            var hours = int.TryParse(await SettingsService.GetAsync("CancellationHours", "24"), out var h) ? h : 24;
            var deadline = r.CheckInDate.AddHours(-hours);
            if (DateTime.Now > deadline)
                throw new DomainException($"Free cancellation ends {hours} hours before check-in ({deadline:g}). Please contact the front desk.");
        }
        await db.ExecAsync("UPDATE Reservations SET Status='Cancelled', CancelledAt=UTC_TIMESTAMP(), CancelReason=@why, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id",
            new { why = reason.Length == 0 ? null : reason, id });
        await Audit.LogAsync(db, s, "Reservation cancelled", "Reservation", id, reason);
        if (s.Role == Roles.Guest)
            await Notify.RoleAsync(db, Roles.Receptionist, "Reservation cancelled", $"Guest cancelled reservation #{id}.", "Reservation", id);
        else
            await Notify.GuestAsync(db, r.GuestId, "Reservation cancelled", $"Reservation #{id} was cancelled by the hotel.", "Reservation", id);
        await db.CommitAsync();
    }

    /// <summary>Staff edit of dates, guest count or room for a reservation that has not started. Re-runs every availability check.</summary>
    public async Task ModifyAsync(int id, int roomId, DateTime checkIn, DateTime checkOut, int guests, string notes)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        checkIn = checkIn.Date; checkOut = checkOut.Date;
        ReservationRules.ValidateDates(checkIn, checkOut, guests);
        await using var db = await Db.BeginAsync();
        var r = await LockAsync(db, id);
        if (r.Status is not (ResStatus.Pending or ResStatus.Confirmed)) throw new DomainException("Only Pending or Confirmed reservations can be edited.");

        var room = await db.FirstAsync<RoomMini>("SELECT Id, RoomNumber, Status, Capacity, Rate, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        if (room.IsArchived || room.Status == RoomStatus.OutOfService) throw new DomainException($"Room {room.RoomNumber} is not available.");
        if (room.Capacity < guests) throw new DomainException($"Room {room.RoomNumber} sleeps {room.Capacity}.");
        if (checkIn == DateTime.Today && room.Status != RoomStatus.Available) throw new DomainException($"Room {room.RoomNumber} is not ready for a same-day arrival.");
        if (await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Reservations WHERE RoomId=@r AND Id<>@id AND Status IN ('Pending','Confirmed','CheckedIn') AND CheckInDate < @out AND CheckOutDate > @in",
                new { r = roomId, id, @in = checkIn, @out = checkOut }) > 0)
            throw new DomainException($"Room {room.RoomNumber} is already reserved for part of these dates.");
        if (await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Reservations WHERE GuestId=@g AND Id<>@id AND Status IN ('Pending','Confirmed','CheckedIn') AND CheckInDate < @out AND CheckOutDate > @in",
                new { g = r.GuestId, id, @in = checkIn, @out = checkOut }) > 0)
            throw new DomainException("This guest already has another reservation overlapping these dates.");

        var nights = (checkOut - checkIn).Days;
        await db.ExecAsync(
            "UPDATE Reservations SET RoomId=@r, CheckInDate=@in, CheckOutDate=@out, GuestCount=@n, NightlyRate=@rate, TotalAmount=@total, Notes=@notes, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id",
            new { r = roomId, @in = checkIn, @out = checkOut, n = guests, rate = room.Rate, total = room.Rate * nights, notes = notes.Trim(), id });
        await Audit.LogAsync(db, s, "Reservation edited", "Reservation", id, $"room {room.RoomNumber}, {checkIn:d}–{checkOut:d}, {guests} guests");
        await Notify.GuestAsync(db, r.GuestId, "Reservation updated", $"Reservation #{id} was updated: room {room.RoomNumber}, {checkIn:d} – {checkOut:d}.", "Reservation", id);
        await db.CommitAsync();
    }

    internal sealed record ResLock(int Id, int GuestId, int RoomId, string Status, DateTime CheckInDate, DateTime CheckOutDate);

    internal static async Task<ResLock> LockAsync(Db db, int id) =>
        await db.FirstAsync<ResLock>("SELECT Id, GuestId, RoomId, Status, CheckInDate, CheckOutDate FROM Reservations WHERE Id=@id FOR UPDATE", new { id })
        ?? throw new DomainException("Reservation not found.");

    // ---- Payments ----

    public async Task AddPaymentAsync(int reservationId, decimal amount, string method, string status, string reference)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        if (amount <= 0) throw new DomainException("The amount must be greater than zero.");
        if (!new[] { "Cash", "Card", "Bank Transfer", "Online" }.Contains(method)) throw new DomainException("Choose a payment method.");
        if (!new[] { "Paid", "Pending", "Refunded" }.Contains(status)) throw new DomainException("Choose a payment status.");
        await using var db = await Db.BeginAsync();
        var r = await ReservationService.LockAsync(db, reservationId);
        if (r.Status == ResStatus.Cancelled && status != "Refunded") throw new DomainException("A cancelled reservation can only receive refunds.");
        if (status == "Refunded")
        {
            var paid = await db.ScalarAsync<decimal>(
                "SELECT COALESCE(SUM(CASE WHEN Status='Paid' THEN Amount WHEN Status='Refunded' THEN -Amount ELSE 0 END),0) FROM Payments WHERE ReservationId=@r", new { r = reservationId });
            if (amount > paid) throw new DomainException($"Cannot refund more than has been paid ({paid:0.00}).");
        }
        var id = await db.InsertAsync(
            "INSERT INTO Payments (ReservationId, Amount, Method, Status, Reference, RecordedById, CreatedAt) VALUES (@r,@a,@m,@s,@ref,@u,UTC_TIMESTAMP())",
            new { r = reservationId, a = amount, m = method, s = status, @ref = reference.Trim(), u = s.UserId });
        await Audit.LogAsync(db, s, "Payment recorded", "Payment", id, $"Reservation #{reservationId}: {amount:0.00} {method} {status}");
        await db.CommitAsync();
    }

    public async Task<List<PaymentRow>> PaymentsAsync(int? reservationId = null, int limit = 300)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<PaymentRow>(
            "SELECT p.Id, p.ReservationId, g.FullName AS GuestName, rm.RoomNumber, p.Amount, p.Method, p.Status, p.Reference, u.Username AS RecordedBy, p.CreatedAt " +
            "FROM Payments p JOIN Reservations r ON r.Id=p.ReservationId JOIN Guests g ON g.Id=r.GuestId JOIN Rooms rm ON rm.Id=r.RoomId JOIN Users u ON u.Id=p.RecordedById " +
            "WHERE (@r IS NULL OR p.ReservationId=@r) ORDER BY p.Id DESC LIMIT " + Math.Clamp(limit, 1, 1000), new { r = reservationId });
    }
}

public sealed record StayRow(int StayId, int ReservationId, int GuestId, string GuestName, int RoomId, string RoomNumber, string RoomType,
    DateTime CheckInAt, DateTime ExpectedCheckOutDate, DateTime? ExpectedCheckOutAt, DateTime? CheckOutAt, string StayStatus, decimal TotalAmount, decimal Balance);

public sealed record MyStayInfo(int StayId, int ReservationId, string GuestName, string RoomNumber, string RoomType, string Building, int Floor,
    DateTime CheckInAt, DateTime ExpectedCheckOutDate, string StayStatus);

public sealed record CheckOutResult(decimal Balance, string RoomNumber);

public sealed class StayService(Session s)
{
    const string StaySelect =
        "SELECT st.Id AS StayId, st.ReservationId, st.GuestId, g.FullName AS GuestName, st.RoomId, rm.RoomNumber, t.Name AS RoomType, st.CheckInAt, " +
        "st.ExpectedCheckOutDate, st.ExpectedCheckOutAt, st.CheckOutAt, CASE WHEN st.CheckOutAt IS NULL THEN 'Active' ELSE 'Ended' END AS StayStatus, r.TotalAmount, " +
        "r.TotalAmount - COALESCE((SELECT SUM(CASE WHEN p.Status='Paid' THEN p.Amount WHEN p.Status='Refunded' THEN -p.Amount ELSE 0 END) FROM Payments p WHERE p.ReservationId=r.Id),0) AS Balance " +
        "FROM Stays st JOIN Reservations r ON r.Id=st.ReservationId JOIN Guests g ON g.Id=st.GuestId JOIN Rooms rm ON rm.Id=st.RoomId JOIN RoomTypes t ON t.Id=rm.RoomTypeId ";

    public async Task<List<StayRow>> ListAsync(bool activeOnly, string? search = null)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<StayRow>(
            StaySelect + "WHERE (@a=0 OR st.CheckOutAt IS NULL) AND (@q IS NULL OR g.FullName LIKE @q OR rm.RoomNumber LIKE @q) ORDER BY st.Id DESC LIMIT 500",
            new { a = activeOnly ? 1 : 0, q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }

    public async Task<MyStayInfo?> MyActiveStayAsync()
    {
        var gid = s.RequireGuestId();
        await using var db = await Db.OpenAsync();
        return await db.FirstAsync<MyStayInfo>(
            "SELECT st.Id AS StayId, st.ReservationId, g.FullName AS GuestName, rm.RoomNumber, t.Name AS RoomType, rm.Building, rm.`Floor` AS Floor, st.CheckInAt, " +
            "st.ExpectedCheckOutDate, 'Active' AS StayStatus FROM Stays st JOIN Guests g ON g.Id=st.GuestId JOIN Rooms rm ON rm.Id=st.RoomId JOIN RoomTypes t ON t.Id=rm.RoomTypeId " +
            "WHERE st.GuestId=@g AND st.CheckOutAt IS NULL", new { g = gid });
    }

    /// <summary>Rooms ready now that fit this reservation: its own room first, then alternatives of any type that have enough capacity.</summary>
    public async Task<List<RoomOffer>> CandidateRoomsAsync(int reservationId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        var r = await db.FirstAsync<ResInfo>("SELECT Id, RoomId, GuestCount, CheckOutDate FROM Reservations WHERE Id=@id", new { id = reservationId })
                ?? throw new DomainException("Reservation not found.");
        var rooms = new RoomService(s);
        var offers = await rooms.AvailableAsync(DateTime.Today, r.CheckOutDate, r.GuestCount, null, reservationId);
        var own = offers.Where(o => o.Id == r.RoomId);
        var typeOf = await db.ScalarAsync<int>("SELECT RoomTypeId FROM Rooms WHERE Id=@r", new { r = r.RoomId });
        var sameType = (await rooms.AvailableAsync(DateTime.Today, r.CheckOutDate, r.GuestCount, typeOf, reservationId)).Where(o => o.Id != r.RoomId);
        var rest = offers.Where(o => o.Id != r.RoomId && sameType.All(x => x.Id != o.Id));
        return own.Concat(sameType).Concat(rest).Where(o => o.Status == RoomStatus.Available).ToList();
    }

    sealed record ResInfo(int Id, int RoomId, int GuestCount, DateTime CheckOutDate);

    /// <summary>Picks the best ready room for a guest count/type, or null if none is available. Used for automatic assignment.</summary>
    public async Task<RoomOffer?> AutoAssignAsync(DateTime checkOut, int guests, int? roomTypeId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        var offers = await new RoomService(s).AvailableAsync(DateTime.Today, checkOut, guests, roomTypeId);
        // Smallest suitable room first (least wasted capacity), then cheapest.
        return offers.Where(o => o.Status == RoomStatus.Available).OrderBy(o => o.Capacity).ThenBy(o => o.Rate).ThenBy(o => o.RoomNumber).FirstOrDefault();
    }

    public async Task<int> CheckInAsync(int reservationId, int roomId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var stayId = await CheckInCoreAsync(db, reservationId, roomId);
        await db.CommitAsync();
        return stayId;
    }

    /// <summary>Walk-in: create a confirmed reservation starting today and check the guest in, in one transaction.</summary>
    public async Task<int> WalkInAsync(int guestId, int roomId, int nights, int guests, string notes)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        if (nights < 1) throw new DomainException("Enter at least one night.");
        await using var db = await Db.BeginAsync();
        var resId = await ReservationService.CreateCoreAsync(db, s, guestId, roomId, DateTime.Today, DateTime.Today.AddDays(nights), guests, notes, ResStatus.Confirmed);
        var stayId = await CheckInCoreAsync(db, resId, roomId);
        await db.CommitAsync();
        return stayId;
    }

    async Task<int> CheckInCoreAsync(Db db, int reservationId, int roomId)
    {
        var r = await db.FirstAsync<ResFull>(
            "SELECT r.Id, r.GuestId, r.RoomId, r.Status, r.CheckInDate, r.CheckOutDate, r.GuestCount, g.FullName AS GuestName FROM Reservations r JOIN Guests g ON g.Id=r.GuestId WHERE r.Id=@id FOR UPDATE",
            new { id = reservationId }) ?? throw new DomainException("Reservation not found.");
        if (r.Status == ResStatus.Pending) throw new DomainException("Confirm the reservation before checking the guest in.");
        if (r.Status == ResStatus.CheckedIn) throw new DomainException("This guest is already checked in.");
        if (r.Status != ResStatus.Confirmed) throw new DomainException($"A {Labels.Of(r.Status).ToLower()} reservation cannot be checked in.");
        var today = DateTime.Today;
        if (r.CheckInDate.Date > today) throw new DomainException($"This reservation starts on {r.CheckInDate:d}. Check-in is only possible from the arrival date.");
        if (r.CheckOutDate.Date <= today) throw new DomainException("The reservation's departure date has passed.");

        var room = await db.FirstAsync<RoomChk>("SELECT Id, RoomNumber, Status, Capacity, Rate, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        if (room.IsArchived) throw new DomainException($"Room {room.RoomNumber} is archived.");
        if (room.Status != RoomStatus.Available)
            throw new DomainException($"Room {room.RoomNumber} is {Labels.Of(room.Status)} and cannot be assigned. Choose a room that is Available.");
        if (room.Capacity < r.GuestCount) throw new DomainException($"Room {room.RoomNumber} sleeps {room.Capacity}; the reservation has {r.GuestCount} guests.");
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND BlocksOccupancy=1 AND Status IN ('Open','Assigned','InProgress')", new { r = roomId }) > 0)
            throw new DomainException($"Room {room.RoomNumber} has an unresolved maintenance or safety issue.");
        if (roomId != r.RoomId &&
            await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Reservations WHERE RoomId=@r AND Id<>@id AND Status IN ('Pending','Confirmed','CheckedIn') AND CheckInDate < @out AND CheckOutDate > @in",
                new { r = roomId, id = reservationId, @in = today, @out = r.CheckOutDate }) > 0)
            throw new DomainException($"Room {room.RoomNumber} is reserved by another guest during this stay.");

        int stayId;
        try
        {
            stayId = await db.InsertAsync(
                "INSERT INTO Stays (ReservationId, GuestId, RoomId, CheckInAt, ExpectedCheckOutDate, CheckedInById) VALUES (@res,@g,@r,UTC_TIMESTAMP(),@out,@u)",
                new { res = reservationId, g = r.GuestId, r = roomId, @out = r.CheckOutDate, u = s.UserId });
        }
        catch (DbException ex) when (ex.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("That room or guest already has an active stay.");
        }

        var nights = (r.CheckOutDate.Date - r.CheckInDate.Date).Days;
        await db.ExecAsync(
            "UPDATE Reservations SET Status='CheckedIn', RoomId=@r, NightlyRate=@rate, TotalAmount=@total, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id",
            new { r = roomId, rate = room.Rate, total = room.Rate * nights, id = reservationId });
        await RoomService.SetStatusAsync(db, roomId, RoomStatus.Occupied, $"Check-in: {r.GuestName}", s.UserId);
        await Audit.LogAsync(db, s, "Check-in", "Stay", stayId, $"{r.GuestName} into room {room.RoomNumber}");
        await Notify.GuestAsync(db, r.GuestId, "Welcome", $"You are checked in to room {room.RoomNumber}. You can now send requests from the app.", "Stay", stayId);
        return stayId;
    }

    sealed record ResFull(int Id, int GuestId, int RoomId, string Status, DateTime CheckInDate, DateTime CheckOutDate, int GuestCount, string GuestName);
    sealed record RoomChk(int Id, string RoomNumber, string Status, int Capacity, decimal Rate, bool IsArchived);

    /// <summary>Extends an active stay. <paramref name="byHours"/> adds a late check-out on the current departure day
    /// (the exact time is stored in Stays.ExpectedCheckOutAt); days push the departure date itself forward.</summary>
    public async Task ExtendStayAsync(int stayId, int amount, bool byHours = false)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        if (amount <= 0) throw new DomainException(byHours ? "Enter at least 1 hour to extend." : "Enter at least 1 day to extend.");
        await using var db = await Db.BeginAsync();
        var st = await db.FirstAsync<StayLock>(
            "SELECT st.Id, st.ReservationId, st.GuestId, st.RoomId, st.CheckOutAt, rm.RoomNumber, g.FullName AS GuestName FROM Stays st " +
            "JOIN Rooms rm ON rm.Id=st.RoomId JOIN Guests g ON g.Id=st.GuestId WHERE st.Id=@id FOR UPDATE", new { id = stayId })
                 ?? throw new DomainException("Stay not found.");
        if (st.CheckOutAt is not null) throw new DomainException("Cannot extend a stay that has already ended.");

        var r = await db.FirstAsync<ResFull>(
            "SELECT r.Id, r.GuestId, r.RoomId, r.Status, r.CheckInDate, r.CheckOutDate, r.GuestCount, g.FullName AS GuestName FROM Reservations r JOIN Guests g ON g.Id=r.GuestId WHERE r.Id=@id FOR UPDATE",
            new { id = st.ReservationId }) ?? throw new DomainException("Reservation not found.");

        DateTime newCheckOut;
        DateTime? lateUntil = null;
        if (byHours)
        {
            var checkoutHour = int.TryParse(await SettingsService.GetAsync("CheckoutHour", "11"), out var h) ? Math.Clamp(h, 0, 23) : 11;
            var current = await db.FirstAsync<OutAt>("SELECT ExpectedCheckOutAt FROM Stays WHERE Id=@id", new { id = stayId });
            var until = (current?.ExpectedCheckOutAt ?? r.CheckOutDate.Date.AddHours(checkoutHour)).AddHours(amount);
            lateUntil = until;
            newCheckOut = until.Date;
        }
        else newCheckOut = r.CheckOutDate.AddDays(amount);

        if (newCheckOut.Date < r.CheckOutDate.Date)
            throw new DomainException("An extension cannot move the departure date earlier.");
        var nights = (newCheckOut.Date - r.CheckInDate.Date).Days;
        if (nights > ReservationRules.MaxNights) throw new DomainException($"A stay cannot be longer than {ReservationRules.MaxNights} nights.");

        if (newCheckOut.Date > r.CheckOutDate.Date)
        {
            // Recheck availability: the extended nights must not collide with another booking of this room.
            var clash = await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Reservations WHERE RoomId=@room AND Id<>@id AND Status IN ('Pending','Confirmed','CheckedIn') AND CheckInDate < @out AND CheckOutDate > @in",
                new { room = r.RoomId, id = st.ReservationId, @in = r.CheckOutDate, @out = newCheckOut });
            if (clash > 0)
                throw new DomainException($"Room {st.RoomNumber} is already reserved during those extra nights, so the stay cannot be extended that far.");
            if (await db.ScalarAsync<int>(
                    "SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND BlocksOccupancy=1 AND Status IN ('Open','Assigned','InProgress')", new { r = r.RoomId }) > 0)
                throw new DomainException("The room has an open maintenance issue, so the stay cannot be extended into new nights.");
        }

        var room = await db.FirstAsync<RoomChk>("SELECT Id, RoomNumber, Status, Capacity, Rate, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = st.RoomId })
                   ?? throw new DomainException("Room not found.");

        var newTotal = room.Rate * nights;

        await db.ExecAsync("UPDATE Stays SET ExpectedCheckOutDate=@out, ExpectedCheckOutAt=@at WHERE Id=@id", new { @out = newCheckOut, at = lateUntil, id = stayId });
        await db.ExecAsync("UPDATE Reservations SET CheckOutDate=@out, TotalAmount=@total, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id",
            new { @out = newCheckOut, total = newTotal, id = st.ReservationId });

        var how = byHours ? $"by {amount} hour(s) until {lateUntil:HH:mm} on {newCheckOut:d}" : $"by {amount} day(s) until {newCheckOut:d}";
        await Audit.LogAsync(db, s, "Stay extended", "Stay", stayId, $"{st.GuestName} room {st.RoomNumber} extended {how}");
        await Notify.GuestAsync(db, st.GuestId, "Stay extended", byHours
            ? $"Your check-out in room {st.RoomNumber} is now {lateUntil:HH:mm} on {newCheckOut:d}."
            : $"Your stay in room {st.RoomNumber} has been extended until {newCheckOut:d}.", "Stay", stayId);
        await db.CommitAsync();
    }

    sealed record OutAt(DateTime? ExpectedCheckOutAt);

    public async Task<CheckOutResult> CheckOutAsync(int stayId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var st = await db.FirstAsync<StayLock>(
            "SELECT st.Id, st.ReservationId, st.GuestId, st.RoomId, st.CheckOutAt, rm.RoomNumber, g.FullName AS GuestName FROM Stays st " +
            "JOIN Rooms rm ON rm.Id=st.RoomId JOIN Guests g ON g.Id=st.GuestId WHERE st.Id=@id FOR UPDATE", new { id = stayId })
                 ?? throw new DomainException("Stay not found.");
        if (st.CheckOutAt is not null) throw new DomainException("This guest has already checked out.");

        await db.ExecAsync("UPDATE Stays SET CheckOutAt=UTC_TIMESTAMP(), CheckedOutById=@u WHERE Id=@id", new { u = s.UserId, id = stayId });
        await db.ExecAsync("UPDATE Reservations SET Status='CheckedOut', UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", new { id = st.ReservationId });
        await RoomService.SetStatusAsync(db, st.RoomId, RoomStatus.Dirty, $"Check-out: {st.GuestName}", s.UserId);
        await HousekeepingService.CreateTaskCoreAsync(db, s, st.RoomId, "Checkout", "High", $"Check-out of {st.GuestName}", null);
        await Notify.RoleAsync(db, Roles.Housekeeper, "Room needs cleaning", $"Room {st.RoomNumber} was checked out and is Dirty.", "Room", st.RoomId);
        await Notify.GuestAsync(db, st.GuestId, "Thank you for staying", "You have been checked out. We hope to see you again.", "Stay", stayId);
        await Audit.LogAsync(db, s, "Check-out", "Stay", stayId, $"{st.GuestName} from room {st.RoomNumber}");
        var balance = await db.ScalarAsync<decimal>(
            "SELECT r.TotalAmount - COALESCE((SELECT SUM(CASE WHEN p.Status='Paid' THEN p.Amount WHEN p.Status='Refunded' THEN -p.Amount ELSE 0 END) FROM Payments p WHERE p.ReservationId=r.Id),0) FROM Reservations r WHERE r.Id=@id",
            new { id = st.ReservationId });
        await db.CommitAsync();
        return new CheckOutResult(balance, st.RoomNumber);
    }

    sealed record StayLock(int Id, int ReservationId, int GuestId, int RoomId, DateTime? CheckOutAt, string RoomNumber, string GuestName);
}
