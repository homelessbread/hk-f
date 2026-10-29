using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;

static class Audit
{
    public static Task LogAsync(Db db, Session? s, string action, string entity, int? id, string details) =>
        db.ExecAsync("INSERT INTO AuditLogs (UserId, Action, EntityType, EntityId, Details, CreatedAt) VALUES (@u, @a, @e, @i, @d, UTC_TIMESTAMP())",
            new { u = s?.UserId, a = action, e = entity, i = id, d = details.Length > 480 ? details[..480] : details });
}

static class Notify
{
    public static Task UserAsync(Db db, int userId, string title, string message, string? entity = null, int? entityId = null) =>
        db.ExecAsync("INSERT INTO Notifications (UserId, Title, Message, EntityType, EntityId, CreatedAt) VALUES (@u, @t, @m, @e, @i, UTC_TIMESTAMP())",
            new { u = userId, t = title, m = message, e = entity, i = entityId });

    /// <summary>Fan out one notification per active user in the role, so each person has their own read state.</summary>
    public static Task RoleAsync(Db db, string role, string title, string message, string? entity = null, int? entityId = null) =>
        db.ExecAsync(
            "INSERT INTO Notifications (UserId, Title, Message, EntityType, EntityId, CreatedAt) " +
            "SELECT u.Id, @t, @m, @e, @i, UTC_TIMESTAMP() FROM Users u JOIN Roles r ON r.Id = u.RoleId WHERE r.Name = @role AND u.IsActive = 1",
            new { role, t = title, m = message, e = entity, i = entityId });

    public static Task GuestAsync(Db db, int guestId, string title, string message, string? entity = null, int? entityId = null) =>
        db.ExecAsync(
            "INSERT INTO Notifications (UserId, Title, Message, EntityType, EntityId, CreatedAt) " +
            "SELECT UserId, @t, @m, @e, @i, UTC_TIMESTAMP() FROM Guests WHERE Id = @g",
            new { g = guestId, t = title, m = message, e = entity, i = entityId });
}

public sealed record NotificationRow(int Id, string Title, string Message, string? EntityType, int? EntityId, bool IsRead, DateTime CreatedAt);

public sealed class NotificationService(Session s)
{
    public async Task<List<NotificationRow>> ListAsync(int limit = 50)
    {
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<NotificationRow>(
            "SELECT Id, Title, Message, EntityType, EntityId, IsRead, CreatedAt FROM Notifications WHERE UserId=@u ORDER BY Id DESC LIMIT " + Math.Clamp(limit, 1, 200),
            new { u = s.UserId });
    }

    public async Task<int> UnreadCountAsync()
    {
        await using var db = await Db.OpenAsync();
        return await db.ScalarAsync<int>("SELECT COUNT(*) FROM Notifications WHERE UserId=@u AND IsRead=0", new { u = s.UserId });
    }

    public async Task MarkAllReadAsync()
    {
        await using var db = await Db.OpenAsync();
        await db.ExecAsync("UPDATE Notifications SET IsRead=1 WHERE UserId=@u AND IsRead=0", new { u = s.UserId });
    }

    public async Task MarkReadAsync(int id)
    {
        await using var db = await Db.OpenAsync();
        await db.ExecAsync("UPDATE Notifications SET IsRead=1 WHERE Id=@id AND UserId=@u", new { id, u = s.UserId });
    }
}

public sealed record AuditRow(int Id, DateTime CreatedAt, string User, string Action, string EntityType, int? EntityId, string Details);

public sealed class AuditService(Session s)
{
    public async Task<List<AuditRow>> ListAsync(string? search = null, int limit = 500)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<AuditRow>(
            "SELECT a.Id, a.CreatedAt, COALESCE(u.Username,'system') AS User, a.Action, a.EntityType, a.EntityId, a.Details " +
            "FROM AuditLogs a LEFT JOIN Users u ON u.Id=a.UserId " +
            "WHERE (@q IS NULL OR a.Action LIKE @q OR a.Details LIKE @q OR u.Username LIKE @q OR a.EntityType LIKE @q) ORDER BY a.Id DESC LIMIT " + Math.Clamp(limit, 1, 2000),
            new { q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }
}

public sealed record SettingRow(string SettingKey, string SettingValue, string Description);

public sealed class SettingsService(Session s)
{
    public static async Task<string> GetAsync(string key, string fallback)
    {
        await using var db = await Db.OpenAsync();
        return await db.ScalarAsync<string>("SELECT SettingValue FROM Settings WHERE SettingKey=@k", new { k = key }) ?? fallback;
    }

    public async Task<List<SettingRow>> ListAsync()
    {
        s.Require(Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<SettingRow>("SELECT SettingKey, SettingValue, Description FROM Settings ORDER BY SettingKey");
    }

    public async Task SetAsync(string key, string value)
    {
        s.Require(Roles.Admin);
        value = value.Trim();
        if (value.Length == 0) throw new DomainException("A value is required.");
        if (key is "CancellationHours" or "MaxStayNights" && (!int.TryParse(value, out var n) || n < 0))
            throw new DomainException("This setting must be a whole number, 0 or greater.");
        await using var db = await Db.BeginAsync();
        var old = await db.ScalarAsync<string>("SELECT SettingValue FROM Settings WHERE SettingKey=@k", new { k = key })
                  ?? throw new DomainException("Unknown setting.");
        await db.ExecAsync("UPDATE Settings SET SettingValue=@v WHERE SettingKey=@k", new { v = value, k = key });
        await Audit.LogAsync(db, s, "Setting changed", "Setting", null, $"{key}: '{old}' -> '{value}'");
        await db.CommitAsync();
    }
}
