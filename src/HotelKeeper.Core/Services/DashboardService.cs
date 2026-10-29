using System.Globalization;
using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;
public sealed class DashboardService(Session s)
{
    sealed record Pair(string Name, decimal V);
    sealed record Line(string A, string B, string C);

    static string D(DateTime d) => d.ToString("d", CultureInfo.CurrentCulture);

    public async Task<DashboardData> AdminAsync()
    {
        s.Require(Roles.Admin);
        await using var db = await Db.OpenAsync();
        var rooms = await db.QueryAsync<Pair>("SELECT Status AS Name, COUNT(*) AS V FROM Rooms WHERE IsArchived=0 GROUP BY Status");
        int N(string st) => (int)(rooms.FirstOrDefault(x => x.Name == st)?.V ?? 0);
        var total = rooms.Sum(x => (int)x.V);
        var today = DateTime.Today;
        var guests = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Stays WHERE CheckOutAt IS NULL");
        var pending = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Requests WHERE Status IN ('Pending','Acknowledged','InProgress') AND IsArchived=0");
        var reports = await db.ScalarAsync<int>("SELECT COUNT(*) FROM MaintenanceReports WHERE Status IN ('Open','Assigned','InProgress') AND IsArchived=0");
        var activeStays = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Stays WHERE CheckOutAt IS NULL");
        var departuresToday = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Stays WHERE ExpectedCheckOutDate=@t AND CheckOutAt IS NULL", new { t = today });

        var cards = new List<StatCard>
        {
            new("Total Rooms", total.ToString(), "", "bed", "rooms"),
            new("Available", N(RoomStatus.Available).ToString(), "ready to rent", "check", "rooms"),
            new("Occupied", N(RoomStatus.Occupied).ToString(), "", "key", "rooms"),
            new("Dirty", N(RoomStatus.Dirty).ToString(), "waiting for cleaning", "broom", "housekeeping"),
            new("Cleaning", N(RoomStatus.Cleaning).ToString(), "in progress", "broom", "housekeeping"),
            new("Out of Service", N(RoomStatus.OutOfService).ToString(), "", "wrench", "rooms"),
            new("Active Guests", guests.ToString(), "in house", "users", "guests"),
            new("Active Stays", activeStays.ToString(), "current stays", "history", "stays"),
            new("Departures Today", departuresToday.ToString(), "due today", "logout", "stays"),
            new("Pending Requests", pending.ToString(), "open guest requests", "inbox", "requests"),
            new("Unresolved Reports", reports.ToString(), "maintenance", "alert", "maintenance")
        };

        var inHouse = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',rm.RoomNumber,'  ',g.FullName) AS A, CONCAT('until ',DATE_FORMAT(st.ExpectedCheckOutDate,'%Y-%m-%d')) AS B, t.Name AS C " +
            "FROM Stays st JOIN Guests g ON g.Id=st.GuestId JOIN Rooms rm ON rm.Id=st.RoomId JOIN RoomTypes t ON t.Id=rm.RoomTypeId WHERE st.CheckOutAt IS NULL ORDER BY rm.RoomNumber LIMIT 8");
        var activity = await db.QueryAsync<Line>(
            "SELECT a.Action AS A, CONCAT(COALESCE(u.Username,'system'),'  ·  ',a.Details) AS B, '' AS C FROM AuditLogs a LEFT JOIN Users u ON u.Id=a.UserId ORDER BY a.Id DESC LIMIT 7");
        var reqs = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',r.RoomNumber,'  ·  ',q.Category) AS A, LEFT(q.Description,70) AS B, q.Status AS C FROM Requests q JOIN Rooms r ON r.Id=q.RoomId WHERE q.Status IN ('Pending','Acknowledged','InProgress') AND q.IsArchived=0 ORDER BY q.Id DESC LIMIT 6");
        var maint = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',rm.RoomNumber,'  ·  ',m.IssueType) AS A, LEFT(m.Description,70) AS B, m.Status AS C FROM MaintenanceReports m JOIN Rooms rm ON rm.Id=m.RoomId WHERE m.Status IN ('Open','Assigned','InProgress') AND m.IsArchived=0 ORDER BY m.Id DESC LIMIT 6");

        var panels = new List<Panel>
        {
            new("Current occupancy", "Rooms with a guest right now", inHouse.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No guests in house.", "stays"),
            new("Room status overview", "All active rooms", RoomStatus.All.Select(st => new PanelRow(Labels.Of(st), $"{N(st)} of {total} rooms", st)).ToList(), "No rooms found.", "rooms"),
            new("Recent guest requests", "Open requests needing attention", reqs.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No open requests.", "requests"),
            new("Open maintenance reports", "Issues requiring maintenance", maint.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No active reports.", "maintenance"),
            new("Recent system activity", "From the audit log", activity.Select(x => new PanelRow(x.A, x.B)).ToList(), "No activity yet.", "audit")
        };
        return new DashboardData(cards, panels);
    }

    public async Task<DashboardData> ReceptionistAsync()
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        var today = DateTime.Today;
        var counts = await db.QueryAsync<Pair>("SELECT Status AS Name, COUNT(*) AS V FROM Rooms WHERE IsArchived=0 GROUP BY Status");
        int N(string st) => (int)(counts.FirstOrDefault(x => x.Name == st)?.V ?? 0);
        var departures = await db.QueryAsync<Line>(
            "SELECT g.FullName AS A, CONCAT('Room ',rm.RoomNumber) AS B, 'Due out' AS C FROM Stays st JOIN Guests g ON g.Id=st.GuestId JOIN Rooms rm ON rm.Id=st.RoomId " +
            "WHERE st.ExpectedCheckOutDate<=@t AND st.CheckOutAt IS NULL ORDER BY g.FullName LIMIT 8", new { t = today });
        var activeStays = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Stays WHERE CheckOutAt IS NULL");
        var avail = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',r.RoomNumber) AS A, CONCAT(t.Name,'  ·  sleeps ',r.Capacity) AS B, r.Status AS C FROM Rooms r JOIN RoomTypes t ON t.Id=r.RoomTypeId WHERE r.IsArchived=0 AND r.Status='Available' ORDER BY r.RoomNumber LIMIT 8");
        var hk = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',r.RoomNumber) AS A, COALESCE((SELECT CONCAT(t.Status,' · ',COALESCE(u.FullName,'unassigned')) FROM HousekeepingTasks t LEFT JOIN Users u ON u.Id=t.AssignedToId WHERE t.RoomId=r.Id AND t.Status IN ('Pending','InProgress') ORDER BY t.Id DESC LIMIT 1),'no task') AS B, r.Status AS C " +
            "FROM Rooms r WHERE r.IsArchived=0 AND r.Status IN ('Dirty','Cleaning') ORDER BY r.RoomNumber LIMIT 8");
        var reqs = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',r.RoomNumber,'  ·  ',q.Category) AS A, LEFT(q.Description,70) AS B, q.Status AS C FROM Requests q JOIN Rooms r ON r.Id=q.RoomId WHERE q.Status IN ('Pending','Acknowledged','InProgress') AND q.IsArchived=0 ORDER BY q.Id DESC LIMIT 6");

        var cards = new List<StatCard>
        {
            new("Available Rooms", N(RoomStatus.Available).ToString(), "ready for guests", "check", "rooms"),
            new("Occupied Rooms", N(RoomStatus.Occupied).ToString(), "", "key", "rooms"),
            new("Active Stays", activeStays.ToString(), "current guests", "history", "stays"),
            new("Departures Due", departures.Count.ToString(), "due today", "logout", "stays"),
            new("Open Requests", reqs.Count.ToString(), "from guests", "inbox", "requests"),
            new("Rooms Requiring Cleaning", (N(RoomStatus.Dirty) + N(RoomStatus.Cleaning)).ToString(), "dirty or in cleaning", "broom", "housekeeping")
        };
        var panels = new List<Panel>
        {
            new("Today's departures", "Stays expecting checkout", departures.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No departures due.", "stays"),
            new("Guest requests", "Open requests from guests", reqs.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No open requests.", "requests"),
            new("Rooms currently available", "Ready for walk-ins", avail.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No rooms are available.", "rooms"),
            new("Housekeeping attention", "Dirty or being cleaned", hk.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "All rooms are clean.", "housekeeping")
        };
        return new DashboardData(cards, panels);
    }

    public async Task<DashboardData> HousekeeperAsync()
    {
        s.Require(Roles.Housekeeper, Roles.Admin);
        await using var db = await Db.OpenAsync();
        var mine = await db.ScalarAsync<int>("SELECT COUNT(*) FROM HousekeepingTasks WHERE AssignedToId=@u AND Status IN ('Pending','InProgress')", new { u = s.UserId });
        var inProg = await db.ScalarAsync<int>("SELECT COUNT(*) FROM HousekeepingTasks WHERE AssignedToId=@u AND Status='InProgress'", new { u = s.UserId });
        var pool = await db.ScalarAsync<int>("SELECT COUNT(*) FROM HousekeepingTasks WHERE AssignedToId IS NULL AND Status='Pending'");
        var dirty = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Rooms WHERE Status='Dirty' AND IsArchived=0");
        var items = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Requests WHERE Category IN ('Cleaning','AdditionalItems') AND Status IN ('Pending','Acknowledged','InProgress') AND (AssignedToId=@u OR AssignedToId IS NULL) AND IsArchived=0", new { u = s.UserId });
        var doneToday = await db.ScalarAsync<int>("SELECT COUNT(*) FROM HousekeepingTasks WHERE AssignedToId=@u AND Status='Completed' AND CompletedAt >= @d", new { u = s.UserId, d = DateTime.Today.ToUniversalTime() });
        var tasks = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',r.RoomNumber,'  ·  ',t.TaskType) AS A, CONCAT(t.Priority,' priority  ·  ',COALESCE(u.FullName,'unassigned')) AS B, t.Status AS C FROM HousekeepingTasks t JOIN Rooms r ON r.Id=t.RoomId LEFT JOIN Users u ON u.Id=t.AssignedToId " +
            "WHERE t.Status IN ('Pending','InProgress') AND (t.AssignedToId=@u OR t.AssignedToId IS NULL) ORDER BY FIELD(t.Priority,'Urgent','High','Normal','Low'), t.CreatedAt LIMIT 8", new { u = s.UserId });
        var reqs = await db.QueryAsync<Line>(
            "SELECT CONCAT('Room ',r.RoomNumber,'  ·  ',q.Category) AS A, COALESCE((SELECT GROUP_CONCAT(CONCAT(ri.Quantity,' x ',ri.ItemName) SEPARATOR ', ') FROM RequestItems ri WHERE ri.RequestId=q.Id), LEFT(q.Description,60)) AS B, q.Status AS C " +
            "FROM Requests q JOIN Rooms r ON r.Id=q.RoomId WHERE q.Category IN ('Cleaning','AdditionalItems') AND q.Status IN ('Pending','Acknowledged','InProgress') AND (q.AssignedToId=@u OR q.AssignedToId IS NULL) AND q.IsArchived=0 ORDER BY q.Id DESC LIMIT 8", new { u = s.UserId });
        var cards = new List<StatCard>
        {
            new("My Open Tasks", mine.ToString(), "assigned to me", "clipboard", "tasks"),
            new("In Progress", inProg.ToString(), "", "broom", "tasks"),
            new("Unassigned Pool", pool.ToString(), "anyone can pick up", "inbox", "tasks"),
            new("Dirty Rooms", dirty.ToString(), "hotel-wide", "bed", "rooms"),
            new("Open Requests", items.ToString(), "items & cleaning", "package", "requests"),
            new("Completed Today", doneToday.ToString(), "", "check", "tasks")
        };
        var panels = new List<Panel>
        {
            new("Tasks to do", "Highest priority first", tasks.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No open cleaning tasks.", "tasks"),
            new("Guest requests", "Items and cleaning", reqs.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No open requests.", "requests")
        };
        return new DashboardData(cards, panels);
    }

    public async Task<DashboardData> GuestAsync()
    {
        var gid = s.RequireGuestId();
        await using var db = await Db.OpenAsync();
        var stay = await new StayService(s).MyActiveStayAsync();
        var reqs = await db.QueryAsync<Line>(
            "SELECT CONCAT('#',q.Id,'  ',q.Category) AS A, LEFT(q.Description,70) AS B, q.Status AS C FROM Requests q WHERE q.GuestId=@g ORDER BY q.Id DESC LIMIT 5", new { g = gid });

        string status, hint;
        if (stay is not null) { status = "Checked in"; hint = $"Room {stay.RoomNumber}"; }
        else { status = "No active stay"; hint = "Check in with the front desk"; }

        var cards = new List<StatCard>
        {
            new("Stay Status", status, hint, "key", "room-info"),
            new("Check-in", stay is not null ? D(stay.CheckInAt) : "—", "", "login", "room-info"),
            new("Expected Check-out", stay is not null ? D(stay.ExpectedCheckOutDate) : "—", "", "logout", "room-info"),
            new("Assigned Room", stay?.RoomNumber ?? "—", stay is null ? "assigned at check-in" : $"{stay.RoomType} · floor {stay.Floor}", "bed", "room-info")
        };
        var panels = new List<Panel>
        {
            new("My requests", "Messages to the front desk & services", reqs.Select(x => new PanelRow(x.A, x.B, x.C)).ToList(), "No requests yet.", "requests")
        };
        return new DashboardData(cards, panels);
    }

    public async Task<DashboardData> AnalyticsAsync()
    {
        s.Require(Roles.Admin);
        await using var db = await Db.OpenAsync();
        var total = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Rooms WHERE IsArchived=0");
        var occ = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Rooms WHERE IsArchived=0 AND Status='Occupied'");
        var avgNights = await db.ScalarAsync<decimal>("SELECT COALESCE(AVG(DATEDIFF(CheckOutDate, CheckInDate)),0) FROM Reservations WHERE Status IN ('CheckedIn','CheckedOut')");
        var res30 = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Reservations WHERE CreatedAt >= @d", new { d = DateTime.Today.AddDays(-30).ToUniversalTime() });
        var cancelled = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Reservations WHERE Status='Cancelled'");
        var allRes = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Reservations");
        var cleanMin = await db.ScalarAsync<decimal>("SELECT COALESCE(AVG(TIMESTAMPDIFF(MINUTE, StartedAt, CompletedAt)),0) FROM HousekeepingTasks WHERE Status='Completed' AND StartedAt IS NOT NULL");

        var byStatus = await db.QueryAsync<Pair>("SELECT Status AS Name, COUNT(*) AS V FROM Reservations GROUP BY Status ORDER BY V DESC");
        var byType = await db.QueryAsync<Pair>(
            "SELECT t.Name AS Name, COUNT(r.Id) AS V FROM RoomTypes t LEFT JOIN Rooms rm ON rm.RoomTypeId=t.Id LEFT JOIN Reservations r ON r.RoomId=rm.Id AND r.Status<>'Cancelled' GROUP BY t.Name ORDER BY V DESC");
        var reqByCat = await db.QueryAsync<Line>(
            "SELECT Category AS A, CONCAT(COUNT(*),' total  ·  ',SUM(Status IN ('Pending','Acknowledged','InProgress')),' open') AS B, '' AS C FROM Requests GROUP BY Category ORDER BY Category");
        var issues = await db.QueryAsync<Pair>("SELECT IssueType AS Name, COUNT(*) AS V FROM MaintenanceReports GROUP BY IssueType ORDER BY V DESC LIMIT 6");

        var pct = total == 0 ? 0 : occ * 100m / total;
        var cards = new List<StatCard>
        {
            new("Occupancy Rate", pct.ToString("0.#", CultureInfo.CurrentCulture) + "%", $"{occ} of {total} rooms", "chart"),
            new("Average Stay", avgNights.ToString("0.0", CultureInfo.CurrentCulture), "nights", "calendar"),
            new("Bookings (30 days)", res30.ToString(), "", "calendar"),
            new("Cancellation Rate", (allRes == 0 ? 0 : cancelled * 100m / allRes).ToString("0.#", CultureInfo.CurrentCulture) + "%", "", "x"),
            new("Avg Cleaning Time", cleanMin.ToString("0", CultureInfo.CurrentCulture), "minutes per task", "broom")
        };
        var panels = new List<Panel>
        {
            new("Reservations by status", "All time", byStatus.Select(x => new PanelRow(Labels.Of(x.Name), ((int)x.V).ToString(), x.Name)).ToList(), "No reservations yet."),
            new("Bookings by room type", "Excluding cancelled", byType.Select(x => new PanelRow(x.Name, ((int)x.V).ToString())).ToList(), "No room types."),
            new("Requests by category", "Guest requests", reqByCat.Select(x => new PanelRow(Labels.Of(x.A), x.B)).ToList(), "No requests yet."),
            new("Most common issues", "Maintenance reports", issues.Select(x => new PanelRow(x.Name, ((int)x.V).ToString())).ToList(), "No reports yet.")
        };
        return new DashboardData(cards, panels);
    }
}
