using System.Text;
using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;

public sealed record RoomRow(int Id, string RoomNumber, string Building, int Floor, int RoomTypeId, string RoomType, int Capacity, decimal Rate,
    string Amenities, string Description, string Status, string StatusReason, bool IsArchived, int Version, int OpenIssues, string CurrentGuest)
{
    public string Display => $"Room {RoomNumber} · {RoomType} · floor {Floor}";
}

public sealed record RoomOffer(int Id, string RoomNumber, string Building, int Floor, string RoomType, string Description, string Amenities,
    int Capacity, decimal Rate, string Status)
{
    public string Display => $"Room {RoomNumber} · {RoomType} · floor {Floor} · sleeps {Capacity} · {Rate:N2}/night";
}

public sealed record RoomTypeRow(int Id, string Name, string Description, decimal BaseRate, int Capacity, bool IsActive, int RoomCount);
public sealed record RoomItemRow(int Id, int RoomId, string ItemName, int Quantity, string ConditionStatus, bool NeedsReplenishment, DateTime UpdatedAt);
public sealed record StatusHistoryRow(DateTime ChangedAt, string PreviousStatus, string NewStatus, string ChangedBy, string Reason);

/// <summary>Which manual (user-initiated) room status changes are valid. System workflows (check-in/out, cleaning) change status through their own code paths.</summary>
public static class RoomRules
{
    public static string? CheckManual(string role, string from, string to)
    {
        if (from == to) return $"Room is already {Labels.Of(to)}.";
        if (from == RoomStatus.Occupied) return "An occupied room changes status only through check-out.";

        return (from, to) switch
        {
            (RoomStatus.Available, RoomStatus.Dirty) when role is Roles.Receptionist or Roles.Admin => null,
            (RoomStatus.Available or RoomStatus.Dirty or RoomStatus.Cleaning, RoomStatus.OutOfService) when role is Roles.Receptionist or Roles.Admin => null,
            (RoomStatus.OutOfService, RoomStatus.Dirty) when role == Roles.Admin => null,
            (RoomStatus.OutOfService, _) => "Only an admin can clear a room that is Out of Service, and it returns as Dirty so it is cleaned first.",
            (_, RoomStatus.Occupied) => "A room becomes Occupied only through check-in.",
            (_, RoomStatus.Cleaning) => "A room moves to Cleaning when a housekeeper starts its cleaning task.",
            (_, RoomStatus.Available) => "A room becomes Available only when housekeeping completes its cleaning task.",
            _ => "That status change is not allowed."
        };
    }
}

public sealed class RoomService(Session s)
{
    const string RoomSelect =
        "SELECT r.Id, r.RoomNumber, r.Building, r.`Floor` AS Floor, r.RoomTypeId, t.Name AS RoomType, r.Capacity, r.Rate, r.Amenities, r.Description, r.Status, " +
        "COALESCE(r.StatusReason,'') AS StatusReason, r.IsArchived, r.Version, " +
        "(SELECT COUNT(*) FROM MaintenanceReports m WHERE m.RoomId=r.Id AND m.Status IN ('Open','Assigned','InProgress')) AS OpenIssues, " +
        "COALESCE((SELECT g.FullName FROM Stays st JOIN Guests g ON g.Id=st.GuestId WHERE st.RoomId=r.Id AND st.CheckOutAt IS NULL LIMIT 1),'') AS CurrentGuest " +
        "FROM Rooms r JOIN RoomTypes t ON t.Id=r.RoomTypeId ";

    public async Task<List<RoomRow>> ListAsync(string? status = null, string? search = null, bool archived = false)
    {
        s.Require(Roles.Admin, Roles.Receptionist, Roles.Housekeeper);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RoomRow>(
            RoomSelect + "WHERE r.IsArchived=@arch AND (@st IS NULL OR r.Status=@st) AND (@q IS NULL OR r.RoomNumber LIKE @q OR t.Name LIKE @q OR r.Building LIKE @q) " +
            "ORDER BY r.Building, r.`Floor`, r.RoomNumber",
            new { arch = archived ? 1 : 0, st = status, q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }

    public async Task<Dictionary<string, int>> CountsAsync()
    {
        s.Require(Roles.Admin, Roles.Receptionist, Roles.Housekeeper);
        await using var db = await Db.OpenAsync();
        var counts = RoomStatus.All.ToDictionary(x => x, _ => 0);
        foreach (var (st, n) in (await db.QueryAsync<StatusCount>("SELECT Status AS Name, COUNT(*) AS N FROM Rooms WHERE IsArchived=0 GROUP BY Status")).Select(x => (x.Name, x.N)))
            counts[st] = n;
        return counts;
    }

    sealed record StatusCount(string Name, int N);

    /// <summary>
    /// Rooms that can be reserved for the dates: not archived, not Out of Service, big enough and free of overlapping reservations.
    /// A same-day arrival additionally needs the room to be Available right now (Dirty/Cleaning rooms are not ready).
    /// Guests may call this; it exposes no guest data.
    /// </summary>
    public async Task<List<RoomOffer>> AvailableAsync(DateTime checkIn, DateTime checkOut, int guests, int? roomTypeId = null, int? ignoreReservationId = null)
    {
        checkIn = checkIn.Date; checkOut = checkOut.Date;
        ReservationRules.ValidateDates(checkIn, checkOut, guests);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RoomOffer>(
            "SELECT r.Id, r.RoomNumber, r.Building, r.`Floor` AS Floor, t.Name AS RoomType, r.Description, r.Amenities, r.Capacity, r.Rate, r.Status " +
            "FROM Rooms r JOIN RoomTypes t ON t.Id=r.RoomTypeId " +
            "WHERE r.IsArchived=0 AND t.IsActive=1 AND r.Status<>'OutOfService' AND r.Capacity>=@g AND (@t IS NULL OR r.RoomTypeId=@t) " +
            "AND (@in > @today OR r.Status='Available') " +
            "AND NOT EXISTS (SELECT 1 FROM Reservations x WHERE x.RoomId=r.Id AND x.Status IN ('Pending','Confirmed','CheckedIn') " +
            "AND x.CheckInDate < @out AND x.CheckOutDate > @in AND (@ign IS NULL OR x.Id<>@ign)) " +
            "AND NOT EXISTS (SELECT 1 FROM MaintenanceReports m WHERE m.RoomId=r.Id AND m.BlocksOccupancy=1 AND m.Status IN ('Open','Assigned','InProgress')) " +
            "ORDER BY r.Rate, r.RoomNumber",
            new { g = guests, t = roomTypeId, @in = checkIn, @out = checkOut, today = DateTime.Today, ign = ignoreReservationId });
    }

    public async Task<List<StatusHistoryRow>> HistoryAsync(int roomId)
    {
        s.Require(Roles.Admin, Roles.Receptionist);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<StatusHistoryRow>(
            "SELECT h.ChangedAt, h.PreviousStatus, h.NewStatus, u.Username AS ChangedBy, COALESCE(h.Reason,'') AS Reason " +
            "FROM RoomStatusHistory h JOIN Users u ON u.Id=h.ChangedById WHERE h.RoomId=@r ORDER BY h.Id DESC LIMIT 200", new { r = roomId });
    }

    /// <summary>Changes a room's status inside the caller's transaction and records history. The room row is locked first.</summary>
    internal static async Task<string> SetStatusAsync(Db db, int roomId, string to, string? reason, int userId)
    {
        var from = await db.ScalarAsync<string>("SELECT Status FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        await db.ExecAsync("UPDATE Rooms SET Status=@s, StatusReason=@why, Version=Version+1, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id",
            new { s = to, why = to == RoomStatus.OutOfService ? reason : null, id = roomId });
        await db.ExecAsync(
            "INSERT INTO RoomStatusHistory (RoomId, PreviousStatus, NewStatus, ChangedById, Reason, ChangedAt) VALUES (@r,@p,@n,@u,@why,UTC_TIMESTAMP())",
            new { r = roomId, p = from, n = to, u = userId, why = reason });
        return from;
    }

    public async Task ChangeStatusAsync(int roomId, string target, string? reason)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        reason = reason?.Trim();
        await using var db = await Db.BeginAsync();
        var room = await db.FirstAsync<RoomMini>("SELECT Id, RoomNumber, Status, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        if (room.IsArchived) throw new DomainException("This room is archived.");
        var err = RoomRules.CheckManual(s.Role, room.Status, target);
        if (err != null) throw new DomainException(err);
        var activeStays = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Stays WHERE RoomId=@r AND CheckOutAt IS NULL", new { r = roomId });
        if (activeStays > 0 && target != RoomStatus.Occupied)
            throw new DomainException($"Room {room.RoomNumber} still has a checked-in guest. A room with an active stay changes status only through check-out.");
        if (target == RoomStatus.Available && activeStays > 0)
            throw new DomainException($"Room {room.RoomNumber} cannot be marked Available while a guest is still checked in.");
        if (target == RoomStatus.OutOfService && string.IsNullOrEmpty(reason)) throw new DomainException("A reason is required to take a room out of service.");

        if (target == RoomStatus.OutOfService)
        {
            var upcoming = await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Reservations WHERE RoomId=@r AND Status IN ('Pending','Confirmed') AND CheckOutDate > @today", new { r = roomId, today = DateTime.Today });
            // Allowed, but tell the user: affected reservations must be re-assigned by front desk.
            if (upcoming > 0) reason += $" ({upcoming} upcoming reservation(s) need a new room)";
            // An in-flight cleaning task is abandoned; housekeeping will create a new one after clearance.
            await db.ExecAsync("UPDATE HousekeepingTasks SET Status='Cancelled' WHERE RoomId=@r AND Status IN ('Pending','InProgress') AND TaskType<>'Requested'", new { r = roomId });
        }

        await SetStatusAsync(db, roomId, target, reason, s.UserId);
        if (target == RoomStatus.Dirty)
        {
            await HousekeepingService.CreateTaskCoreAsync(db, s, roomId, "Inspection", "Normal", "Marked dirty by front desk", null);
            await Notify.RoleAsync(db, Roles.Housekeeper, "Room needs cleaning", $"Room {room.RoomNumber} is Dirty.", "Room", roomId);
        }
        await Audit.LogAsync(db, s, "Room status changed", "Room", roomId, $"{room.RoomNumber}: {room.Status} -> {target}" + (reason is null ? "" : $" ({reason})"));
        await db.CommitAsync();
    }

    sealed record RoomMini(int Id, string RoomNumber, string Status, bool IsArchived);

    /// <summary>Clearance: an admin returns an Out of Service room to circulation. It comes back Dirty and only if no blocking issue is still open.</summary>
    public async Task RestoreToServiceAsync(int roomId)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        var room = await db.FirstAsync<RoomMini>("SELECT Id, RoomNumber, Status, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        if (room.Status != RoomStatus.OutOfService) throw new DomainException("This room is not Out of Service.");
        var blocking = await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND BlocksOccupancy=1 AND Status IN ('Open','Assigned','InProgress')", new { r = roomId });
        if (blocking > 0) throw new DomainException($"{blocking} maintenance/safety issue(s) must be resolved before this room can be cleared.");
        await SetStatusAsync(db, roomId, RoomStatus.Dirty, "Cleared for service", s.UserId);
        await HousekeepingService.CreateTaskCoreAsync(db, s, roomId, "Inspection", "High", "Room cleared after being out of service", null);
        await Notify.RoleAsync(db, Roles.Housekeeper, "Room needs cleaning", $"Room {room.RoomNumber} was cleared for service and is Dirty.", "Room", roomId);
        await Audit.LogAsync(db, s, "Room cleared", "Room", roomId, room.RoomNumber);
        await db.CommitAsync();
    }

    // ---- Admin: room records ----

    public async Task<int> SaveRoomAsync(int? id, string number, string building, int floor, int roomTypeId, int capacity, decimal rate, string amenities, string description)
    {
        s.Require(Roles.Admin);
        number = number.Trim();
        if (number.Length == 0) throw new DomainException("Room number is required.");
        if (capacity < 1) throw new DomainException("Capacity must be at least 1.");
        if (rate <= 0) throw new DomainException("Rate must be greater than zero.");
        await using var db = await Db.BeginAsync();
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Rooms WHERE RoomNumber=@n AND (@id IS NULL OR Id<>@id)", new { n = number, id }) > 0)
            throw new DomainException("A room with this number already exists.");
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM RoomTypes WHERE Id=@t", new { t = roomTypeId }) == 0) throw new DomainException("Choose a room type.");
        var p = new { n = number, b = string.IsNullOrWhiteSpace(building) ? "Main" : building.Trim(), f = floor, t = roomTypeId, c = capacity, r = rate, a = amenities.Trim(), d = description.Trim(), id };
        if (id is null)
        {
            id = await db.InsertAsync(
                "INSERT INTO Rooms (RoomNumber, Building, `Floor`, RoomTypeId, Capacity, Rate, Amenities, Description, Status, UpdatedAt) VALUES (@n,@b,@f,@t,@c,@r,@a,@d,'Dirty',UTC_TIMESTAMP())", p);
            await db.ExecAsync("INSERT INTO RoomStatusHistory (RoomId, PreviousStatus, NewStatus, ChangedById, Reason, ChangedAt) VALUES (@r,'Dirty','Dirty',@u,'Room created',UTC_TIMESTAMP())", new { r = id, u = s.UserId });
            await HousekeepingService.CreateTaskCoreAsync(db, s, id.Value, "Inspection", "Normal", "New room: prepare for first guest", null);
        }
        else
        {
            var activeRes = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Reservations WHERE RoomId=@id AND Status IN ('Confirmed','Pending','CheckedIn')", new { id });
            var oldCap = await db.ScalarAsync<int>("SELECT Capacity FROM Rooms WHERE Id=@id", new { id });
            if (activeRes > 0 && capacity < oldCap) throw new DomainException("Capacity cannot be reduced while the room has active or upcoming reservations.");
            await db.ExecAsync(
                "UPDATE Rooms SET RoomNumber=@n, Building=@b, `Floor`=@f, RoomTypeId=@t, Capacity=@c, Rate=@r, Amenities=@a, Description=@d, Version=Version+1, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", p);
        }
        await Audit.LogAsync(db, s, "Room saved", "Room", id, $"{number}, floor {floor}, rate {rate:0.00}");
        await db.CommitAsync();
        return id!.Value;
    }

    public async Task SetArchivedAsync(int roomId, bool archived)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        var room = await db.FirstAsync<RoomMini>("SELECT Id, RoomNumber, Status, IsArchived FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId })
                   ?? throw new DomainException("Room not found.");
        if (archived)
        {
            if (room.Status == RoomStatus.Occupied) throw new DomainException("A room with an active guest cannot be archived.");
            if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Reservations WHERE RoomId=@r AND Status IN ('Pending','Confirmed','CheckedIn')", new { r = roomId }) > 0)
                throw new DomainException("This room has active or upcoming reservations. Move or cancel them first.");
            if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM HousekeepingTasks WHERE RoomId=@r AND Status IN ('Pending','InProgress')", new { r = roomId }) > 0)
                throw new DomainException("This room has open housekeeping tasks.");
        }
        await db.ExecAsync("UPDATE Rooms SET IsArchived=@a, Version=Version+1, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", new { a = archived, id = roomId });
        await Audit.LogAsync(db, s, archived ? "Room archived" : "Room restored from archive", "Room", roomId, room.RoomNumber);
        await db.CommitAsync();
    }

    // ---- Room types ----

    public async Task<List<RoomTypeRow>> TypesAsync(bool activeOnly = false)
    {
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RoomTypeRow>(
            "SELECT t.Id, t.Name, t.Description, t.BaseRate, t.Capacity, t.IsActive, (SELECT COUNT(*) FROM Rooms r WHERE r.RoomTypeId=t.Id AND r.IsArchived=0) AS RoomCount " +
            "FROM RoomTypes t WHERE (@a=0 OR t.IsActive=1) ORDER BY t.BaseRate", new { a = activeOnly ? 1 : 0 });
    }

    public async Task SaveTypeAsync(int? id, string name, string description, decimal baseRate, int capacity, bool active)
    {
        s.Require(Roles.Admin);
        name = name.Trim();
        if (name.Length == 0) throw new DomainException("A name is required.");
        if (baseRate <= 0 || capacity < 1) throw new DomainException("Rate must be above zero and capacity at least 1.");
        await using var db = await Db.BeginAsync();
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM RoomTypes WHERE Name=@n AND (@id IS NULL OR Id<>@id)", new { n = name, id }) > 0)
            throw new DomainException("A room type with this name already exists.");
        if (id is null)
            id = await db.InsertAsync("INSERT INTO RoomTypes (Name, Description, BaseRate, Capacity, IsActive) VALUES (@n,@d,@r,@c,@a)",
                new { n = name, d = description.Trim(), r = baseRate, c = capacity, a = active });
        else
            await db.ExecAsync("UPDATE RoomTypes SET Name=@n, Description=@d, BaseRate=@r, Capacity=@c, IsActive=@a WHERE Id=@id",
                new { n = name, d = description.Trim(), r = baseRate, c = capacity, a = active, id });
        await Audit.LogAsync(db, s, "Room type saved", "RoomType", id, $"{name} @ {baseRate:0.00}");
        await db.CommitAsync();
    }

    // ---- Room items ----

    public async Task<List<RoomItemRow>> ItemsAsync(int roomId)
    {
        s.Require(Roles.Admin, Roles.Receptionist, Roles.Housekeeper);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RoomItemRow>(
            "SELECT Id, RoomId, ItemName, Quantity, ConditionStatus, NeedsReplenishment, UpdatedAt FROM RoomItems WHERE RoomId=@r ORDER BY ItemName", new { r = roomId });
    }

    /// <summary>The items in the guest's own room (guest id and room come from the session / active stay, not from the caller).</summary>
    public async Task<List<RoomItemRow>> MyRoomItemsAsync()
    {
        var gid = s.RequireGuestId();
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RoomItemRow>(
            "SELECT i.Id, i.RoomId, i.ItemName, i.Quantity, i.ConditionStatus, i.NeedsReplenishment, i.UpdatedAt FROM RoomItems i " +
            "JOIN Stays st ON st.RoomId=i.RoomId WHERE st.GuestId=@g AND st.CheckOutAt IS NULL ORDER BY i.ItemName", new { g = gid });
    }

    public async Task SaveItemAsync(int? id, int roomId, string name, int quantity, string condition, bool needsReplenishment)
    {
        s.Require(Roles.Admin);
        name = name.Trim();
        if (name.Length == 0) throw new DomainException("Item name is required.");
        if (quantity < 0) throw new DomainException("Quantity cannot be negative.");
        await using var db = await Db.BeginAsync();
        if (id is null)
            id = await db.InsertAsync("INSERT INTO RoomItems (RoomId, ItemName, Quantity, ConditionStatus, NeedsReplenishment, UpdatedAt) VALUES (@r,@n,@q,@c,@x,UTC_TIMESTAMP())",
                new { r = roomId, n = name, q = quantity, c = condition, x = needsReplenishment });
        else
            await db.ExecAsync("UPDATE RoomItems SET ItemName=@n, Quantity=@q, ConditionStatus=@c, NeedsReplenishment=@x, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id AND RoomId=@r",
                new { n = name, q = quantity, c = condition, x = needsReplenishment, id, r = roomId });
        await Audit.LogAsync(db, s, "Room item saved", "Room", roomId, $"{name} x{quantity} ({condition})");
        await db.CommitAsync();
    }

    public async Task DeleteItemAsync(int itemId)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        var info = await db.FirstAsync<ItemInfo>("SELECT RoomId, ItemName FROM RoomItems WHERE Id=@id", new { id = itemId }) ?? throw new DomainException("Item not found.");
        await db.ExecAsync("DELETE FROM RoomItems WHERE Id=@id", new { id = itemId });
        await Audit.LogAsync(db, s, "Room item removed", "Room", info.RoomId, info.ItemName);
        await db.CommitAsync();
    }

    sealed record ItemInfo(int RoomId, string ItemName);
}
