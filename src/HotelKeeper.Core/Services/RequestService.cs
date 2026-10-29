using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;

public sealed record RequestRow(int Id, int GuestId, string GuestName, string RoomNumber, string Category, string Description, string ItemsText, string Priority,
    string Status, int? AssignedToId, string AssignedTo, string Resolution, DateTime CreatedAt, DateTime UpdatedAt, bool IsArchived);

public sealed record RequestableItemRow(int Id, string Name, string ItemCategory, int MaxQuantity, string Rule, bool IsActive);
public sealed record RequestLine(int ItemId, int Quantity);

public sealed class RequestService(Session s)
{
    const string Select =
        "SELECT q.Id, q.GuestId, g.FullName AS GuestName, r.RoomNumber, q.Category, q.Description, " +
        "COALESCE((SELECT GROUP_CONCAT(CONCAT(ri.Quantity,' x ',ri.ItemName) SEPARATOR ', ') FROM RequestItems ri WHERE ri.RequestId=q.Id),'') AS ItemsText, " +
        "q.Priority, q.Status, q.AssignedToId, COALESCE(u.FullName,'Unassigned') AS AssignedTo, COALESCE(q.Resolution,'') AS Resolution, q.CreatedAt, q.UpdatedAt, q.IsArchived " +
        "FROM Requests q JOIN Guests g ON g.Id=q.GuestId JOIN Rooms r ON r.Id=q.RoomId LEFT JOIN Users u ON u.Id=q.AssignedToId ";

    // ---- Guest ----

    /// <summary>Creates a request for the signed-in guest's own active stay. Guest, stay and room are never taken from the caller.</summary>
    public async Task<int> CreateAsync(string category, string description, string priority, string? issueType = null, IReadOnlyList<RequestLine>? items = null)
    {
        var gid = s.RequireGuestId();
        if (!Category.All.Contains(category)) throw new DomainException("Choose a request category.");
        if (!Priorities.All.Contains(priority)) priority = "Normal";
        description = description.Trim();
        if (description.Length < 3 && category != Category.AdditionalItems) throw new DomainException("Please describe what you need.");

        await using var db = await Db.BeginAsync();
        var stay = await db.FirstAsync<StayBrief>(
            "SELECT st.Id, st.RoomId, rm.RoomNumber, g.FullName AS GuestName FROM Stays st JOIN Rooms rm ON rm.Id=st.RoomId JOIN Guests g ON g.Id=st.GuestId WHERE st.GuestId=@g AND st.CheckOutAt IS NULL",
            new { g = gid });
        if (stay is null)
            throw new DomainException("You can send requests only while you are checked in to a room. You have no active stay right now (not yet checked in, or already checked out).");

        var lines = (items ?? Array.Empty<RequestLine>()).Where(l => l.Quantity > 0).ToList();
        var itemNames = new List<string>();
        if (category == Category.AdditionalItems)
        {
            if (lines.Count == 0) throw new DomainException("Choose at least one item.");
            if (lines.Select(l => l.ItemId).Distinct().Count() != lines.Count) throw new DomainException("Each item can be listed once.");
            foreach (var l in lines)
            {
                var item = await db.FirstAsync<RequestableItemRow>("SELECT Id, Name, ItemCategory, MaxQuantity, Rule, IsActive FROM RequestableItems WHERE Id=@id", new { id = l.ItemId });
                if (item is null || !item.IsActive) throw new DomainException("One of the items is no longer available.");
                if (l.Quantity > item.MaxQuantity) throw new DomainException($"You can request at most {item.MaxQuantity} × {item.Name} at a time.");
                itemNames.Add(item.Name);
            }
            if (description.Length == 0) description = "Item request: " + string.Join(", ", itemNames);
        }
        else if (lines.Count > 0) throw new DomainException("Items can only be added to an Additional Items request.");

        if (category == Category.Maintenance && string.IsNullOrWhiteSpace(issueType)) issueType = "Other";
        var fullDescription = category == Category.Maintenance ? $"{issueType}: {description}" : description;

        var id = await db.InsertAsync(
            "INSERT INTO Requests (GuestId, StayId, RoomId, Category, Description, Priority, CreatedAt, UpdatedAt) VALUES (@g,@st,@r,@c,@d,@p,UTC_TIMESTAMP(),UTC_TIMESTAMP())",
            new { g = gid, st = stay.Id, r = stay.RoomId, c = category, d = fullDescription, p = priority });
        foreach (var l in lines)
            await db.ExecAsync("INSERT INTO RequestItems (RequestId, ItemId, ItemName, Quantity) SELECT @r, Id, Name, @q FROM RequestableItems WHERE Id=@i", new { r = id, q = l.Quantity, i = l.ItemId });

        if (category == Category.Maintenance)
            await MaintenanceService.CreateCoreAsync(db, s, stay.RoomId, id, "Guest", issueType!, description, blocks: false);

        await Notify.RoleAsync(db, Roles.Receptionist, "New guest request", $"Room {stay.RoomNumber}: {Labels.Of(category)} – {Short(fullDescription)}", "Request", id);
        if (category == Category.AdditionalItems)
            await Notify.RoleAsync(db, Roles.Housekeeper, "Item request", $"Room {stay.RoomNumber}: {string.Join(", ", lines.Select((l, i) => $"{l.Quantity} × {itemNames[i]}"))}", "Request", id);
        await Audit.LogAsync(db, s, "Request created", "Request", id, $"{category}, room {stay.RoomNumber}");
        await db.CommitAsync();
        return id;
    }

    sealed record StayBrief(int Id, int RoomId, string RoomNumber, string GuestName);
    static string Short(string t) => t.Length <= 80 ? t : t[..77] + "...";

    public async Task<List<RequestRow>> MineAsync()
    {
        var gid = s.RequireGuestId();
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RequestRow>(Select + "WHERE q.GuestId=@g ORDER BY q.Id DESC LIMIT 300", new { g = gid });
    }

    // ---- Staff ----

    /// <summary>Housekeepers see only cleaning/item requests that are unassigned or assigned to them.</summary>
    public async Task<List<RequestRow>> ListAsync(string? status = null, string? category = null, string? search = null, bool archived = false, bool openOnly = false)
    {
        s.Require(Roles.Admin, Roles.Receptionist, Roles.Housekeeper);
        var hk = s.Role == Roles.Housekeeper ? "AND q.Category IN ('Cleaning','AdditionalItems') AND (q.AssignedToId=@me OR q.AssignedToId IS NULL)" : "";
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RequestRow>(
            Select + $"WHERE q.IsArchived=@arch AND (@st IS NULL OR q.Status=@st) AND (@cat IS NULL OR q.Category=@cat) AND (@op=0 OR q.Status IN ('Pending','Acknowledged','InProgress')) {hk} " +
            "AND (@q IS NULL OR g.FullName LIKE @q OR r.RoomNumber LIKE @q OR q.Description LIKE @q) ORDER BY FIELD(q.Priority,'Urgent','High','Normal','Low'), q.Id DESC LIMIT 500",
            new { arch = archived ? 1 : 0, st = status, cat = category, op = openOnly ? 1 : 0, me = s.UserId, q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }

    sealed record ReqLock(int Id, int GuestId, int RoomId, string Category, string Status, int? AssignedToId, string Priority, string RoomNumber);

    static async Task<ReqLock> LockAsync(Db db, int id) =>
        await db.FirstAsync<ReqLock>("SELECT q.Id, q.GuestId, q.RoomId, q.Category, q.Status, q.AssignedToId, q.Priority, r.RoomNumber FROM Requests q JOIN Rooms r ON r.Id=q.RoomId WHERE q.Id=@id FOR UPDATE", new { id })
        ?? throw new DomainException("Request not found.");

    public static readonly Dictionary<string, string[]> Flow = new()
    {
        [ReqStatus.Pending] = new[] { ReqStatus.Acknowledged, ReqStatus.InProgress, ReqStatus.Rejected, ReqStatus.UnableToFulfill },
        [ReqStatus.Acknowledged] = new[] { ReqStatus.InProgress, ReqStatus.Fulfilled, ReqStatus.Rejected, ReqStatus.UnableToFulfill },
        [ReqStatus.InProgress] = new[] { ReqStatus.Fulfilled, ReqStatus.UnableToFulfill, ReqStatus.Rejected }
    };

    public async Task UpdateStatusAsync(int id, string newStatus, string? notes)
    {
        s.Require(Roles.Admin, Roles.Receptionist, Roles.Housekeeper);
        notes = notes?.Trim();
        await using var db = await Db.BeginAsync();
        var q = await LockAsync(db, id);

        if (s.Role == Roles.Housekeeper)
        {
            if (q.Category is not (Category.Cleaning or Category.AdditionalItems)) throw new AuthorizationException("Housekeeping handles only cleaning and item requests.");
            if (q.AssignedToId is not null && q.AssignedToId != s.UserId) throw new AuthorizationException("This request is assigned to someone else.");
            if (newStatus is not (ReqStatus.InProgress or ReqStatus.Fulfilled or ReqStatus.UnableToFulfill)) throw new AuthorizationException("You can mark a request In Progress, Fulfilled or Unable to Fulfill.");
        }
        if (!Flow.TryGetValue(q.Status, out var next) || !next.Contains(newStatus))
            throw new DomainException($"A request that is {Labels.Of(q.Status)} cannot move to {Labels.Of(newStatus)}.");
        if (q.Category == Category.Maintenance && newStatus is ReqStatus.InProgress or ReqStatus.Fulfilled or ReqStatus.UnableToFulfill)
            throw new DomainException("Maintenance requests progress through their maintenance report (assign it, then resolve it).");
        if (newStatus is ReqStatus.Rejected or ReqStatus.UnableToFulfill && string.IsNullOrEmpty(notes)) throw new DomainException("Please give a reason.");

        var terminal = newStatus is ReqStatus.Fulfilled or ReqStatus.Rejected or ReqStatus.UnableToFulfill;
        await db.ExecAsync(
            "UPDATE Requests SET Status=@st, Resolution=COALESCE(@n,Resolution), UpdatedAt=UTC_TIMESTAMP(), ResolvedAt=CASE WHEN @t=1 THEN UTC_TIMESTAMP() ELSE ResolvedAt END, " +
            "AssignedToId=CASE WHEN @hk=1 AND AssignedToId IS NULL THEN @me ELSE AssignedToId END WHERE Id=@id",
            new { st = newStatus, n = string.IsNullOrEmpty(notes) ? null : notes, t = terminal, hk = s.Role == Roles.Housekeeper, me = s.UserId, id });
        if (q.Category == Category.Maintenance && newStatus == ReqStatus.Rejected)
            await db.ExecAsync("UPDATE MaintenanceReports SET Status='Closed', ClosedAt=UTC_TIMESTAMP(), Resolution=@n, UpdatedAt=UTC_TIMESTAMP() WHERE RequestId=@id AND Status IN ('Open','Assigned','InProgress')", new { n = "Rejected: " + notes, id });

        await Notify.GuestAsync(db, q.GuestId, $"Request #{id} {Labels.Of(newStatus).ToLower()}", string.IsNullOrEmpty(notes) ? $"Your request is now {Labels.Of(newStatus)}." : $"Your request is now {Labels.Of(newStatus)}: {notes}", "Request", id);
        await Audit.LogAsync(db, s, "Request " + newStatus, "Request", id, notes ?? "");
        await db.CommitAsync();
    }

    /// <summary>Front desk routes a request to a housekeeper (or back to the housekeeping pool). A cleaning request also gets a housekeeping task.</summary>
    public async Task AssignAsync(int id, int? userId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var q = await LockAsync(db, id);
        if (!ReqStatus.IsOpen(q.Status)) throw new DomainException("This request is already closed.");
        if (userId is not null &&
            await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=@id AND r.Name='Housekeeper' AND u.IsActive=1", new { id = userId }) == 0)
            throw new DomainException("Choose an active housekeeper.");
        if (q.Category == Category.Maintenance) throw new DomainException("Assign maintenance requests from the Maintenance Reports page.");

        await db.ExecAsync("UPDATE Requests SET AssignedToId=@u, Status=CASE WHEN Status='Pending' THEN 'Acknowledged' ELSE Status END, UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", new { u = userId, id });
        if (q.Category == Category.Cleaning)
        {
            var taskId = await HousekeepingService.CreateTaskCoreAsync(db, s, q.RoomId, "Requested", q.Priority, $"Guest cleaning request #{id}", id);
            await db.ExecAsync("UPDATE HousekeepingTasks SET AssignedToId=@u WHERE Id=@t", new { u = userId, t = taskId });
        }
        if (userId is not null) await Notify.UserAsync(db, userId.Value, "Request assigned", $"Room {q.RoomNumber}: request #{id} was assigned to you.", "Request", id);
        else await Notify.RoleAsync(db, Roles.Housekeeper, "Request for housekeeping", $"Room {q.RoomNumber}: request #{id}.", "Request", id);
        await Notify.GuestAsync(db, q.GuestId, $"Request #{id} acknowledged", "Our team has received your request.", "Request", id);
        await Audit.LogAsync(db, s, "Request assigned", "Request", id, userId?.ToString() ?? "housekeeping pool");
        await db.CommitAsync();
    }

    public async Task SetArchivedAsync(int id, bool archived)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        var q = await LockAsync(db, id);
        if (archived && ReqStatus.IsOpen(q.Status)) throw new DomainException("Only completed or rejected requests can be archived.");
        await db.ExecAsync("UPDATE Requests SET IsArchived=@a WHERE Id=@id", new { a = archived, id });
        await Audit.LogAsync(db, s, archived ? "Request archived" : "Request restored", "Request", id, "");
        await db.CommitAsync();
    }

    // ---- hooks used by housekeeping / maintenance workflows (inside their transactions) ----

    internal static async Task CompleteFromTaskAsync(Db db, Session by, int requestId, string note)
    {
        var q = await LockAsync(db, requestId);
        if (!ReqStatus.IsOpen(q.Status)) return;
        await db.ExecAsync("UPDATE Requests SET Status='Fulfilled', Resolution=@n, ResolvedAt=UTC_TIMESTAMP(), UpdatedAt=UTC_TIMESTAMP() WHERE Id=@id", new { n = note, id = requestId });
        await Notify.GuestAsync(db, q.GuestId, $"Request #{requestId} fulfilled", "Your room has been serviced.", "Request", requestId);
    }

    internal static async Task SyncFromReportAsync(Db db, Session by, int requestId, string status, string? resolution)
    {
        var q = await LockAsync(db, requestId);
        if (!ReqStatus.IsOpen(q.Status) || q.Status == status) return;
        await db.ExecAsync(
            "UPDATE Requests SET Status=@st, Resolution=COALESCE(@n,Resolution), UpdatedAt=UTC_TIMESTAMP(), ResolvedAt=CASE WHEN @st='Fulfilled' THEN UTC_TIMESTAMP() ELSE ResolvedAt END WHERE Id=@id",
            new { st = status, n = resolution, id = requestId });
        await Notify.GuestAsync(db, q.GuestId, $"Request #{requestId} {Labels.Of(status).ToLower()}", status == ReqStatus.Fulfilled ? "The issue in your room has been resolved." : "A technician is working on your report.", "Request", requestId);
    }

    // ---- Requestable items catalog (admin) ----

    public async Task<List<RequestableItemRow>> ItemsAsync(bool activeOnly = true)
    {
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<RequestableItemRow>(
            "SELECT Id, Name, ItemCategory, MaxQuantity, Rule, IsActive FROM RequestableItems WHERE (@a=0 OR IsActive=1) ORDER BY ItemCategory, Name", new { a = activeOnly ? 1 : 0 });
    }

    public async Task SaveItemAsync(int? id, string name, string category, int maxQuantity, string rule, bool active)
    {
        s.Require(Roles.Admin);
        name = name.Trim();
        if (name.Length == 0) throw new DomainException("A name is required.");
        if (maxQuantity < 1) throw new DomainException("The maximum quantity must be at least 1.");
        await using var db = await Db.BeginAsync();
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM RequestableItems WHERE Name=@n AND (@id IS NULL OR Id<>@id)", new { n = name, id }) > 0)
            throw new DomainException("An item with this name already exists.");
        if (id is null)
            id = await db.InsertAsync("INSERT INTO RequestableItems (Name, ItemCategory, MaxQuantity, Rule, IsActive) VALUES (@n,@c,@m,@r,@a)",
                new { n = name, c = category.Trim(), m = maxQuantity, r = rule.Trim(), a = active });
        else
            await db.ExecAsync("UPDATE RequestableItems SET Name=@n, ItemCategory=@c, MaxQuantity=@m, Rule=@r, IsActive=@a WHERE Id=@id",
                new { n = name, c = category.Trim(), m = maxQuantity, r = rule.Trim(), a = active, id });
        await Audit.LogAsync(db, s, "Request item saved", "RequestItem", id, $"{name}, max {maxQuantity}, active={active}");
        await db.CommitAsync();
    }

    // ---- Housekeeping checklist template (admin) ----

    public async Task<List<ChecklistTemplateRow>> ChecklistTemplateAsync()
    {
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<ChecklistTemplateRow>("SELECT Id, Label, SortOrder, IsActive FROM ChecklistTemplate ORDER BY SortOrder, Id");
    }

    public async Task SaveChecklistItemAsync(int? id, string label, int sortOrder, bool active)
    {
        s.Require(Roles.Admin);
        label = label.Trim();
        if (label.Length == 0) throw new DomainException("A label is required.");
        await using var db = await Db.BeginAsync();
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM ChecklistTemplate WHERE Label=@l AND (@id IS NULL OR Id<>@id)", new { l = label, id }) > 0)
            throw new DomainException("This checklist step already exists.");
        if (id is null) id = await db.InsertAsync("INSERT INTO ChecklistTemplate (Label, SortOrder, IsActive) VALUES (@l,@o,@a)", new { l = label, o = sortOrder, a = active });
        else await db.ExecAsync("UPDATE ChecklistTemplate SET Label=@l, SortOrder=@o, IsActive=@a WHERE Id=@id", new { l = label, o = sortOrder, a = active, id });
        await Audit.LogAsync(db, s, "Checklist step saved", "Checklist", id, label);
        await db.CommitAsync();
    }
}

public sealed record ChecklistTemplateRow(int Id, string Label, int SortOrder, bool IsActive);
