using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;

public sealed record TaskRow(int Id, int RoomId, string RoomNumber, string RoomStatus, string TaskType, string Priority, string Status, int? AssignedToId,
    string AssignedTo, string Notes, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, int ChecklistDone, int ChecklistTotal, int OpenIssues)
{
    public string Progress => ChecklistTotal == 0 ? "—" : $"{ChecklistDone}/{ChecklistTotal}";
}

public sealed record ChecklistRow(int Id, string Label, bool IsDone);

public sealed class HousekeepingService(Session s)
{
    const string Select =
        "SELECT t.Id, t.RoomId, r.RoomNumber, r.Status AS RoomStatus, t.TaskType, t.Priority, t.Status, t.AssignedToId, COALESCE(u.FullName,'Unassigned') AS AssignedTo, t.Notes, " +
        "t.CreatedAt, t.StartedAt, t.CompletedAt, " +
        "(SELECT COUNT(*) FROM TaskChecklist c WHERE c.TaskId=t.Id AND c.IsDone=1) AS ChecklistDone, (SELECT COUNT(*) FROM TaskChecklist c WHERE c.TaskId=t.Id) AS ChecklistTotal, " +
        "(SELECT COUNT(*) FROM MaintenanceReports m WHERE m.RoomId=r.Id AND m.Status IN ('Open','Assigned','InProgress')) AS OpenIssues " +
        "FROM HousekeepingTasks t JOIN Rooms r ON r.Id=t.RoomId LEFT JOIN Users u ON u.Id=t.AssignedToId ";

    /// <summary>scope: open | done | all. Housekeepers only ever see their own tasks and the unassigned pool.</summary>
    public async Task<List<TaskRow>> ListAsync(string scope = "open", string? search = null)
    {
        s.Require(Roles.Housekeeper, Roles.Receptionist, Roles.Admin);
        var status = scope switch { "open" => "t.Status IN ('Pending','InProgress')", "done" => "t.Status IN ('Completed','Cancelled')", _ => "1=1" };
        var visible = s.Role == Roles.Housekeeper ? "AND (t.AssignedToId=@me OR (t.AssignedToId IS NULL AND t.Status='Pending'))" : "";
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<TaskRow>(
            Select + $"WHERE {status} {visible} AND (@q IS NULL OR r.RoomNumber LIKE @q) " +
            "ORDER BY FIELD(t.Priority,'Urgent','High','Normal','Low'), t.CreatedAt LIMIT 500",
            new { me = s.UserId, q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }

    /// <summary>Creates a housekeeping task (idempotent per room + type while one is still open) and copies the checklist template.</summary>
    internal static async Task<int> CreateTaskCoreAsync(Db db, Session by, int roomId, string type, string priority, string notes, int? requestId)
    {
        var existing = await db.ScalarAsync<int?>(
            "SELECT Id FROM HousekeepingTasks WHERE RoomId=@r AND TaskType=@t AND Status IN ('Pending','InProgress') AND (@req IS NULL OR RequestId=@req) LIMIT 1",
            new { r = roomId, t = type, req = requestId });
        if (existing is not null) return existing.Value;

        var id = await db.InsertAsync(
            "INSERT INTO HousekeepingTasks (RoomId, TaskType, Priority, Notes, RequestId, CreatedById, CreatedAt) VALUES (@r,@t,@p,@n,@req,@u,UTC_TIMESTAMP())",
            new { r = roomId, t = type, p = priority, n = notes, req = requestId, u = by.UserId });
        await db.ExecAsync("INSERT INTO TaskChecklist (TaskId, Label) SELECT @id, Label FROM ChecklistTemplate WHERE IsActive=1 ORDER BY SortOrder, Id", new { id });
        return id;
    }

    public async Task<int> CreateTaskAsync(int roomId, string priority, string notes)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var room = await db.FirstAsync<RoomBrief>("SELECT Id, RoomNumber, Status FROM Rooms WHERE Id=@id", new { id = roomId }) ?? throw new DomainException("Room not found.");
        if (room.Status == RoomStatus.OutOfService) throw new DomainException("This room is Out of Service.");
        var id = await CreateTaskCoreAsync(db, s, roomId, "Inspection", priority, notes.Trim(), null);
        await Notify.RoleAsync(db, Roles.Housekeeper, "New cleaning task", $"Room {room.RoomNumber}: {notes}", "Task", id);
        await Audit.LogAsync(db, s, "Task created", "Task", id, $"Room {room.RoomNumber}");
        await db.CommitAsync();
        return id;
    }

    sealed record RoomBrief(int Id, string RoomNumber, string Status);
    sealed record TaskLock(int Id, int RoomId, string Status, int? AssignedToId, int? RequestId, string TaskType);

    async Task<TaskLock> LockAsync(Db db, int taskId)
    {
        var t = await db.FirstAsync<TaskLock>("SELECT Id, RoomId, Status, AssignedToId, RequestId, TaskType FROM HousekeepingTasks WHERE Id=@id FOR UPDATE", new { id = taskId })
                ?? throw new DomainException("Task not found.");
        if (s.Role == Roles.Housekeeper && t.AssignedToId is not null && t.AssignedToId != s.UserId)
            throw new AuthorizationException("This task is assigned to another housekeeper.");
        return t;
    }

    public async Task AssignAsync(int taskId, int? userId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var t = await LockAsync(db, taskId);
        if (t.Status is not (WorkStatus.Pending or WorkStatus.InProgress)) throw new DomainException("Only open tasks can be assigned.");
        if (userId is not null &&
            await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=@id AND r.Name='Housekeeper' AND u.IsActive=1", new { id = userId }) == 0)
            throw new DomainException("Choose an active housekeeper.");
        await db.ExecAsync("UPDATE HousekeepingTasks SET AssignedToId=@u WHERE Id=@id", new { u = userId, id = taskId });
        if (userId is not null) await Notify.UserAsync(db, userId.Value, "Task assigned", "A cleaning task was assigned to you.", "Task", taskId);
        await Audit.LogAsync(db, s, "Task assigned", "Task", taskId, userId?.ToString() ?? "unassigned");
        await db.CommitAsync();
    }

    public async Task StartAsync(int taskId)
    {
        s.Require(Roles.Housekeeper, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var t = await LockAsync(db, taskId);
        if (t.Status != WorkStatus.Pending) throw new DomainException("Only a pending task can be started.");
        var room = await db.FirstAsync<RoomBrief>("SELECT Id, RoomNumber, Status FROM Rooms WHERE Id=@id FOR UPDATE", new { id = t.RoomId }) ?? throw new DomainException("Room not found.");
        if (room.Status == RoomStatus.OutOfService) throw new DomainException($"Room {room.RoomNumber} is Out of Service.");
        if (room.Status == RoomStatus.Cleaning) throw new DomainException($"Room {room.RoomNumber} is already being cleaned.");

        // Only a Dirty room moves to Cleaning. A service visit to an Occupied room leaves the room status alone:
        // the room's status and the task's status are separate pieces of information.
        if (room.Status == RoomStatus.Dirty) await RoomService.SetStatusAsync(db, t.RoomId, RoomStatus.Cleaning, $"Task #{taskId} started", s.UserId);
        await db.ExecAsync("UPDATE HousekeepingTasks SET Status='InProgress', StartedAt=UTC_TIMESTAMP(), AssignedToId=COALESCE(AssignedToId,@u) WHERE Id=@id", new { u = s.UserId, id = taskId });
        await Audit.LogAsync(db, s, "Task started", "Task", taskId, $"Room {room.RoomNumber}");
        await db.CommitAsync();
    }

    public async Task<List<ChecklistRow>> ChecklistAsync(int taskId)
    {
        s.Require(Roles.Housekeeper, Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<ChecklistRow>("SELECT Id, Label, IsDone FROM TaskChecklist WHERE TaskId=@t ORDER BY Id", new { t = taskId });
    }

    public async Task SetChecklistAsync(int taskId, IReadOnlyDictionary<int, bool> done)
    {
        s.Require(Roles.Housekeeper, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var t = await LockAsync(db, taskId);
        if (t.Status != WorkStatus.InProgress) throw new DomainException("Start the task before ticking the checklist.");
        foreach (var (id, value) in done)
            await db.ExecAsync("UPDATE TaskChecklist SET IsDone=@d, DoneAt=CASE WHEN @d=1 THEN UTC_TIMESTAMP() ELSE NULL END WHERE Id=@id AND TaskId=@t", new { d = value, id, t = taskId });
        await db.CommitAsync();
    }

    /// <summary>Finishes the task. The room becomes Available only if it was being cleaned AND nothing blocks it; otherwise it is held Out of Service.</summary>
    public async Task<string> CompleteAsync(int taskId, string? notes)
    {
        s.Require(Roles.Housekeeper, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var t = await LockAsync(db, taskId);
        if (t.Status != WorkStatus.InProgress) throw new DomainException("Only a task in progress can be completed.");
        var remaining = await db.ScalarAsync<int>("SELECT COUNT(*) FROM TaskChecklist WHERE TaskId=@t AND IsDone=0", new { t = taskId });
        if (remaining > 0) throw new DomainException($"{remaining} checklist item(s) are not done yet.");

        var room = await db.FirstAsync<RoomBrief>("SELECT Id, RoomNumber, Status FROM Rooms WHERE Id=@id FOR UPDATE", new { id = t.RoomId })!;
        string outcome;
        if (room!.Status == RoomStatus.Cleaning)
        {
            var blocking = await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND BlocksOccupancy=1 AND Status IN ('Open','Assigned','InProgress')", new { r = t.RoomId });
            if (blocking > 0)
            {
                await RoomService.SetStatusAsync(db, t.RoomId, RoomStatus.OutOfService, "Held after cleaning: unresolved maintenance issue", s.UserId);
                outcome = $"Cleaning complete. Room {room.RoomNumber} stays Out of Service because of an unresolved maintenance issue.";
                await Notify.RoleAsync(db, Roles.Receptionist, "Room on hold", $"Room {room.RoomNumber} was cleaned but has an unresolved maintenance issue.", "Room", t.RoomId);
            }
            else
            {
                await RoomService.SetStatusAsync(db, t.RoomId, RoomStatus.Available, $"Task #{taskId} completed", s.UserId);
                outcome = $"Room {room.RoomNumber} is now Available.";
                await Notify.RoleAsync(db, Roles.Receptionist, "Room ready", $"Room {room.RoomNumber} is clean and Available.", "Room", t.RoomId);
            }
        }
        else outcome = $"Task completed. Room {room.RoomNumber} status is unchanged ({Labels.Of(room.Status)}).";

        await db.ExecAsync("UPDATE HousekeepingTasks SET Status='Completed', CompletedAt=UTC_TIMESTAMP(), Notes=CASE WHEN @n IS NULL OR @n='' THEN Notes ELSE @n END WHERE Id=@id", new { n = notes?.Trim(), id = taskId });
        if (t.RequestId is not null) await RequestService.CompleteFromTaskAsync(db, s, t.RequestId.Value, "Completed by housekeeping");
        await Audit.LogAsync(db, s, "Task completed", "Task", taskId, outcome);
        await db.CommitAsync();
        return outcome;
    }

    public async Task CancelAsync(int taskId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var t = await LockAsync(db, taskId);
        if (t.Status is not (WorkStatus.Pending or WorkStatus.InProgress)) throw new DomainException("This task is already closed.");
        if (t.Status == WorkStatus.InProgress)
        {
            // Put a room that was being cleaned back to Dirty so it cannot be rented half-cleaned.
            var st = await db.ScalarAsync<string>("SELECT Status FROM Rooms WHERE Id=@r FOR UPDATE", new { r = t.RoomId });
            if (st == RoomStatus.Cleaning) await RoomService.SetStatusAsync(db, t.RoomId, RoomStatus.Dirty, $"Task #{taskId} cancelled", s.UserId);
        }
        await db.ExecAsync("UPDATE HousekeepingTasks SET Status='Cancelled', CompletedAt=UTC_TIMESTAMP() WHERE Id=@id", new { id = taskId });
        await Audit.LogAsync(db, s, "Task cancelled", "Task", taskId, "");
        await db.CommitAsync();
    }

    /// <summary>Report damage / missing items / plumbing etc. found in a room. Flagging it as blocking holds the room out of service.</summary>
    public async Task<int> ReportProblemAsync(int roomId, string issueType, string description, bool blocksOccupancy)
    {
        s.Require(Roles.Housekeeper, Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var id = await MaintenanceService.CreateCoreAsync(db, s, roomId, null, s.Role == Roles.Housekeeper ? "Housekeeping" : "Staff", issueType, description, blocksOccupancy);
        await db.CommitAsync();
        return id;
    }
}

public sealed record MaintenanceRow(int Id, int RoomId, string RoomNumber, string IssueType, string Description, string Source, string ReportedBy, bool BlocksOccupancy,
    string Status, int? AssignedToId, string AssignedTo, string Resolution, DateTime CreatedAt, DateTime? ResolvedAt, bool IsArchived);

public static class IssueTypes
{
    public static readonly string[] All =
        { "Air conditioning", "Lighting / electrical", "Furniture damaged", "Plumbing / leak", "Wi-Fi", "Equipment broken", "Missing items", "Safety hazard", "Other" };
}

public sealed class MaintenanceService(Session s)
{
    const string Select =
        "SELECT m.Id, m.RoomId, r.RoomNumber, m.IssueType, m.Description, m.Source, u.FullName AS ReportedBy, m.BlocksOccupancy, m.Status, m.AssignedToId, " +
        "COALESCE(a.FullName,'Unassigned') AS AssignedTo, COALESCE(m.Resolution,'') AS Resolution, m.CreatedAt, m.ResolvedAt, m.IsArchived " +
        "FROM MaintenanceReports m JOIN Rooms r ON r.Id=m.RoomId JOIN Users u ON u.Id=m.ReportedById LEFT JOIN Users a ON a.Id=m.AssignedToId ";

    internal static async Task<int> CreateCoreAsync(Db db, Session by, int roomId, int? requestId, string source, string issueType, string description, bool blocks)
    {
        description = description.Trim();
        if (description.Length < 3) throw new DomainException("Describe the problem.");
        var room = await db.FirstAsync<RoomBrief>("SELECT Id, RoomNumber, Status FROM Rooms WHERE Id=@id FOR UPDATE", new { id = roomId }) ?? throw new DomainException("Room not found.");
        var id = await db.InsertAsync(
            "INSERT INTO MaintenanceReports (RoomId, RequestId, ReportedById, Source, IssueType, Description, BlocksOccupancy, CreatedAt, UpdatedAt) VALUES (@r,@req,@u,@src,@t,@d,@b,UTC_TIMESTAMP(),UTC_TIMESTAMP())",
            new { r = roomId, req = requestId, u = by.UserId, src = source, t = issueType, d = description, b = blocks });

        // A blocking issue takes a vacant room out of service. An occupied room keeps its guest; the hold is applied when it is cleaned after check-out.
        if (blocks && room.Status is RoomStatus.Available or RoomStatus.Dirty or RoomStatus.Cleaning)
            await RoomService.SetStatusAsync(db, roomId, RoomStatus.OutOfService, $"{issueType}: {description}".Length > 200 ? $"{issueType}: {description}"[..200] : $"{issueType}: {description}", by.UserId);

        await Notify.RoleAsync(db, Roles.Receptionist, "Maintenance report", $"Room {room.RoomNumber}: {issueType}" + (blocks ? " (blocks occupancy)" : ""), "Report", id);
        if (blocks) await Notify.RoleAsync(db, Roles.Admin, "Room held for maintenance", $"Room {room.RoomNumber}: {issueType}", "Report", id);
        await Audit.LogAsync(db, by, "Maintenance report", "Report", id, $"Room {room.RoomNumber}: {issueType}, blocks={blocks}");
        return id;
    }

    sealed record RoomBrief(int Id, string RoomNumber, string Status);

    public async Task<List<MaintenanceRow>> ListAsync(bool unresolvedOnly = false, bool archived = false, string? search = null)
    {
        s.Require(Roles.Admin, Roles.Receptionist, Roles.Housekeeper);
        var own = s.Role == Roles.Housekeeper ? "AND (m.AssignedToId=@me OR m.ReportedById=@me)" : "";
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<MaintenanceRow>(
            Select + $"WHERE m.IsArchived=@arch AND (@un=0 OR m.Status IN ('Open','Assigned','InProgress')) {own} AND (@q IS NULL OR r.RoomNumber LIKE @q OR m.IssueType LIKE @q OR m.Description LIKE @q) ORDER BY m.Id DESC LIMIT 500",
            new { arch = archived ? 1 : 0, un = unresolvedOnly ? 1 : 0, me = s.UserId, q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }

    public async Task AssignAsync(int reportId, int userId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var m = await LockAsync(db, reportId);
        if (m.Status is not (ReportStatus.Open or ReportStatus.Assigned or ReportStatus.InProgress)) throw new DomainException("This report is already resolved.");
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=@id AND r.Name IN ('Housekeeper','Admin') AND u.IsActive=1", new { id = userId }) == 0)
            throw new DomainException("Choose an active staff member who can handle maintenance.");
        await db.ExecAsync("UPDATE MaintenanceReports SET AssignedToId=@u, Status=CASE WHEN Status='Open' THEN 'Assigned' ELSE Status END, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", new { u = userId, id = reportId });
        if (m.RequestId is not null) await RequestService.SyncFromReportAsync(db, s, m.RequestId.Value, ReqStatus.InProgress, null);
        await Notify.UserAsync(db, userId, "Maintenance task assigned", "A maintenance report was assigned to you.", "Report", reportId);
        await Audit.LogAsync(db, s, "Report assigned", "Report", reportId, userId.ToString());
        await db.CommitAsync();
    }

    public async Task SetStatusAsync(int reportId, string status, string? resolution)
    {
        s.Require(Roles.Receptionist, Roles.Admin, Roles.Housekeeper);
        await using var db = await Db.BeginAsync();
        var m = await LockAsync(db, reportId);
        var allowed = m.Status switch
        {
            ReportStatus.Open or ReportStatus.Assigned => new[] { ReportStatus.InProgress, ReportStatus.Resolved },
            ReportStatus.InProgress => new[] { ReportStatus.Resolved },
            ReportStatus.Resolved => new[] { ReportStatus.Closed },
            _ => Array.Empty<string>()
        };
        if (!allowed.Contains(status)) throw new DomainException($"A {Labels.Of(m.Status).ToLower()} report cannot move to {Labels.Of(status)}.");
        if (s.Role == Roles.Housekeeper && (m.AssignedToId != s.UserId || status == ReportStatus.Closed))
            throw new AuthorizationException("You can only update reports assigned to you, and only front desk can close them.");
        if (status == ReportStatus.Resolved && string.IsNullOrWhiteSpace(resolution)) throw new DomainException("Describe how the issue was resolved.");

        await db.ExecAsync(
            "UPDATE MaintenanceReports SET Status=@st, Resolution=COALESCE(@res, Resolution), UpdatedAt=UTC_TIMESTAMP(), " +
            "ResolvedAt=CASE WHEN @st='Resolved' THEN UTC_TIMESTAMP() ELSE ResolvedAt END, ClosedAt=CASE WHEN @st='Closed' THEN UTC_TIMESTAMP() ELSE ClosedAt END, " +
            "AssignedToId=CASE WHEN @st='InProgress' AND AssignedToId IS NULL THEN @me ELSE AssignedToId END WHERE Id=@id",
            new { st = status, res = string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim(), me = s.UserId, id = reportId });

        if (m.RequestId is not null)
        {
            if (status == ReportStatus.InProgress) await RequestService.SyncFromReportAsync(db, s, m.RequestId.Value, ReqStatus.InProgress, null);
            if (status == ReportStatus.Resolved) await RequestService.SyncFromReportAsync(db, s, m.RequestId.Value, ReqStatus.Fulfilled, resolution?.Trim());
        }
        if (status == ReportStatus.Resolved)
        {
            await Notify.RoleAsync(db, Roles.Receptionist, "Issue resolved", $"Report #{reportId} is resolved and waits for your confirmation.", "Report", reportId);
            var stillBlocked = await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND BlocksOccupancy=1 AND Status IN ('Open','Assigned','InProgress')", new { r = m.RoomId });
            var roomStatus = await db.ScalarAsync<string>("SELECT Status FROM Rooms WHERE Id=@r", new { r = m.RoomId });
            if (stillBlocked == 0 && roomStatus == RoomStatus.OutOfService)
                await Notify.RoleAsync(db, Roles.Admin, "Room ready for clearance", $"All blocking issues for the room are resolved. You can clear it for service.", "Room", m.RoomId);
        }
        await Audit.LogAsync(db, s, "Report " + status, "Report", reportId, resolution ?? "");
        await db.CommitAsync();
    }

    public async Task SetArchivedAsync(int reportId, bool archived)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        var m = await LockAsync(db, reportId);
        if (archived && m.Status != ReportStatus.Closed) throw new DomainException("Only closed reports can be archived.");
        await db.ExecAsync("UPDATE MaintenanceReports SET IsArchived=@a WHERE Id=@id", new { a = archived, id = reportId });
        await Audit.LogAsync(db, s, archived ? "Report archived" : "Report restored", "Report", reportId, "");
        await db.CommitAsync();
    }

    sealed record MLock(int Id, int RoomId, int? RequestId, string Status, int? AssignedToId);

    static async Task<MLock> LockAsync(Db db, int id) =>
        await db.FirstAsync<MLock>("SELECT Id, RoomId, RequestId, Status, AssignedToId FROM MaintenanceReports WHERE Id=@id FOR UPDATE", new { id })
        ?? throw new DomainException("Report not found.");
}
