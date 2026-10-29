using HotelKeeper.Data;
using HotelKeeper.Models;

namespace HotelKeeper.Services;

public static class AuthService
{
    public static async Task<Session> LoginAsync(string username, string password)
    {
        username = username.Trim().ToUpperInvariant();
        if (username.Length == 0 || password.Length == 0) throw new DomainException("Enter your username and password.");

        await using var db = await Db.OpenAsync();
        var row = await db.FirstAsync<LoginRow>(
            "SELECT u.Id, u.Username, u.FullName, u.Email, u.PasswordHash, r.Name AS Role, u.IsActive, u.MustChangePassword, g.Id AS GuestId, COALESCE(g.IsArchived,0) AS GuestArchived " +
            "FROM Users u JOIN Roles r ON r.Id=u.RoleId LEFT JOIN Guests g ON g.UserId=u.Id WHERE u.Username=@u", new { u = username });

        // Same message for unknown user / wrong password / disabled account: do not reveal which accounts exist.
        const string bad = "Incorrect username or password, or the account is disabled.";
        if (row is null || !row.IsActive || row.GuestArchived || !Passwords.Verify(password, row.PasswordHash))
        {
            await Audit.LogAsync(db, null, "Login failed", "User", row?.Id, $"Username '{username}'");
            throw new DomainException(bad);
        }

        await db.ExecAsync("UPDATE Users SET LastLoginAt=UTC_TIMESTAMP() WHERE Id=@id", new { id = row.Id });
        await Audit.LogAsync(db, null, "Login", "User", row.Id, row.Username);
        return new Session
        {
            UserId = row.Id, Username = row.Username, FullName = row.FullName, Email = row.Email, Role = row.Role,
            GuestId = row.Role == Roles.Guest ? row.GuestId : null, MustChangePassword = row.MustChangePassword
        };
    }

    sealed record LoginRow(int Id, string Username, string FullName, string Email, string PasswordHash, string Role, bool IsActive,
        bool MustChangePassword, int? GuestId, bool GuestArchived);

    public static async Task ChangePasswordAsync(Session s, string current, string next)
    {
        var err = Passwords.Validate(next);
        if (err != null) throw new DomainException(err);
        if (current == next) throw new DomainException("Choose a password different from the current one.");
        await using var db = await Db.BeginAsync();
        var hash = await db.ScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id = s.UserId });
        if (hash is null || !Passwords.Verify(current, hash)) throw new DomainException("Your current password is incorrect.");
        await db.ExecAsync("UPDATE Users SET PasswordHash=@h, MustChangePassword=0 WHERE Id=@id", new { h = Passwords.Hash(next), id = s.UserId });
        await Audit.LogAsync(db, s, "Password changed", "User", s.UserId, s.Username);
        await db.CommitAsync();
        s.MustChangePassword = false;
    }

    /// <summary>Public self-registration: always creates a Guest account, never a staff account.</summary>
    public static async Task RegisterGuestAsync(string fullName, string email, string phone, string username, string password)
    {
        var err = Passwords.Validate(password);
        if (err != null) throw new DomainException(err);
        await using var db = await Db.BeginAsync();
        await GuestService.CreateGuestCoreAsync(db, null, fullName, email, phone, "", "", "", username, password, mustChange: false);
        await db.CommitAsync();
    }
}

public sealed record GuestRow(int Id, int UserId, string FullName, string Email, string Phone, string Address, string Nationality,
    string IdDocument, string Username, bool IsActive, bool IsArchived, string CurrentRoom, int StayCount, DateTime CreatedAt);

public sealed record GuestCreated(int GuestId, string Username, string TemporaryPassword);

public sealed class GuestService(Session s)
{
    const string Select =
        "SELECT g.Id, g.UserId, g.FullName, g.Email, g.Phone, g.Address, g.Nationality, g.IdDocument, u.Username, u.IsActive, g.IsArchived, " +
        "COALESCE((SELECT rm.RoomNumber FROM Stays st JOIN Rooms rm ON rm.Id=st.RoomId WHERE st.GuestId=g.Id AND st.CheckOutAt IS NULL LIMIT 1),'') AS CurrentRoom, " +
        "(SELECT COUNT(*) FROM Stays st WHERE st.GuestId=g.Id) AS StayCount, g.CreatedAt " +
        "FROM Guests g JOIN Users u ON u.Id=g.UserId ";

    internal static async Task<GuestCreated> CreateGuestCoreAsync(Db db, Session? by, string fullName, string email, string phone,
        string address, string nationality, string idDocument, string username, string? password, bool mustChange)
    {
        fullName = fullName.Trim(); email = email.Trim(); phone = phone.Trim(); username = username.Trim().ToUpperInvariant();
        if (fullName.Length < 2) throw new DomainException("Enter the guest's full name.");
        if (phone.Length == 0 && email.Length == 0) throw new DomainException("Enter a phone number or an email address.");
        if (email.Length > 0 && !email.Contains('@')) throw new DomainException("The email address is not valid.");
        if (username.Length < 3 || username.Any(char.IsWhiteSpace)) throw new DomainException("Username must be at least 3 characters with no spaces.");
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@u", new { u = username }) > 0)
            throw new DomainException("That username is already taken.");

        password ??= Passwords.Generate();
        var roleId = await db.ScalarAsync<int>("SELECT Id FROM Roles WHERE Name=@n", new { n = Roles.Guest });
        var userId = await db.InsertAsync(
            "INSERT INTO Users (Username, FullName, Email, Phone, PasswordHash, RoleId, MustChangePassword, CreatedAt) VALUES (@u,@f,@e,@p,@h,@r,@m,UTC_TIMESTAMP())",
            new { u = username, f = fullName, e = email, p = phone, h = Passwords.Hash(password), r = roleId, m = mustChange });
        var guestId = await db.InsertAsync(
            "INSERT INTO Guests (UserId, FullName, Email, Phone, Address, Nationality, IdDocument, CreatedById, CreatedAt) VALUES (@u,@f,@e,@p,@a,@n,@i,@c,UTC_TIMESTAMP())",
            new { u = userId, f = fullName, e = email, p = phone, a = address.Trim(), n = nationality.Trim(), i = idDocument.Trim(), c = by?.UserId });
        await Audit.LogAsync(db, by, "Guest registered", "Guest", guestId, $"{fullName} ({username})");
        return new GuestCreated(guestId, username, password);
    }

    public async Task<GuestCreated> RegisterAsync(string fullName, string email, string phone, string address, string nationality, string idDocument, string username)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var r = await CreateGuestCoreAsync(db, s, fullName, email, phone, address, nationality, idDocument, username, null, mustChange: true);
        await db.CommitAsync();
        return r;
    }

    public async Task<List<GuestRow>> SearchAsync(string? search = null, bool archived = false, bool inHouseOnly = false)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<GuestRow>(
            Select + "WHERE g.IsArchived=@arch AND (@q IS NULL OR g.FullName LIKE @q OR g.Email LIKE @q OR g.Phone LIKE @q OR u.Username LIKE @q) " +
            "AND (@inh=0 OR EXISTS (SELECT 1 FROM Stays st WHERE st.GuestId=g.Id AND st.CheckOutAt IS NULL)) ORDER BY g.FullName LIMIT 500",
            new { arch = archived ? 1 : 0, inh = inHouseOnly ? 1 : 0, q = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" });
    }

    public async Task<GuestRow?> GetAsync(int guestId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.FirstAsync<GuestRow>(Select + "WHERE g.Id=@id", new { id = guestId });
    }

    public async Task<GuestRow> GetMineAsync()
    {
        var id = s.RequireGuestId();
        await using var db = await Db.OpenAsync();
        return await db.FirstAsync<GuestRow>(Select + "WHERE g.Id=@id", new { id }) ?? throw new DomainException("Guest record not found.");
    }

    public async Task UpdateAsync(int guestId, string fullName, string email, string phone, string address, string nationality, string idDocument)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        fullName = fullName.Trim();
        if (fullName.Length < 2) throw new DomainException("Enter the guest's full name.");

        if (phone.Trim().Length == 0 && email.Trim().Length == 0) throw new DomainException("Enter a phone number or an email address.");
        await using var db = await Db.BeginAsync();
        var uid = await db.ScalarAsync<int?>("SELECT UserId FROM Guests WHERE Id=@id", new { id = guestId }) ?? throw new DomainException("Guest not found.");
        await db.ExecAsync("UPDATE Guests SET FullName=@f, Email=@e, Phone=@p, Address=@a, Nationality=@n, IdDocument=@i WHERE Id=@id",
            new { f = fullName, e = email.Trim(), p = phone.Trim(), a = address.Trim(), n = nationality.Trim(), i = idDocument.Trim(), id = guestId });
        await db.ExecAsync("UPDATE Users SET FullName=@f, Email=@e, Phone=@p WHERE Id=@u", new { f = fullName, e = email.Trim(), p = phone.Trim(), u = uid });
        await Audit.LogAsync(db, s, "Guest updated", "Guest", guestId, fullName);
        await db.CommitAsync();
    }

    /// <summary>A guest may change only their own contact details; the id comes from the session, never from the caller.</summary>
    public async Task UpdateMyProfileAsync(string email, string phone, string address)
    {
        var id = s.RequireGuestId();
        if (phone.Trim().Length == 0 && email.Trim().Length == 0) throw new DomainException("Keep a phone number or an email address.");
        if (email.Trim().Length > 0 && !email.Contains('@')) throw new DomainException("The email address is not valid.");
        await using var db = await Db.BeginAsync();
        await db.ExecAsync("UPDATE Guests SET Email=@e, Phone=@p, Address=@a WHERE Id=@id", new { e = email.Trim(), p = phone.Trim(), a = address.Trim(), id });
        await db.ExecAsync("UPDATE Users SET Email=@e, Phone=@p WHERE Id=@u", new { e = email.Trim(), p = phone.Trim(), u = s.UserId });
        await Audit.LogAsync(db, s, "Profile updated", "Guest", id, "Contact details");
        await db.CommitAsync();
    }

    public async Task<string> ResetPasswordAsync(int guestId)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.BeginAsync();
        var uid = await db.ScalarAsync<int?>("SELECT UserId FROM Guests WHERE Id=@id", new { id = guestId }) ?? throw new DomainException("Guest not found.");
        var temp = Passwords.Generate();
        await db.ExecAsync("UPDATE Users SET PasswordHash=@h, MustChangePassword=1 WHERE Id=@id", new { h = Passwords.Hash(temp), id = uid });
        await Audit.LogAsync(db, s, "Guest password reset", "Guest", guestId, "");
        await db.CommitAsync();
        return temp;
    }

    /// <summary>Archive (never delete) a guest: login is blocked but every stay, request and payment stays on record.</summary>
    public async Task SetArchivedAsync(int guestId, bool archived)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        if (archived)
        {
            var open = await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Reservations WHERE GuestId=@id AND Status IN ('Pending','Confirmed','CheckedIn')", new { id = guestId });
            if (open > 0) throw new DomainException("This guest has an active stay or upcoming reservation. Cancel or complete it before archiving.");
        }
        await db.ExecAsync("UPDATE Guests SET IsArchived=@a WHERE Id=@id", new { a = archived, id = guestId });
        await Audit.LogAsync(db, s, archived ? "Guest archived" : "Guest restored", "Guest", guestId, "");
        await db.CommitAsync();
    }
}

public sealed record StaffRow(int Id, string Username, string FullName, string Email, string Phone, string Role, bool IsActive, DateTime? LastLoginAt);

public sealed record StaffOption(int Id, string FullName, string Role);

public sealed class StaffService(Session s)
{
    public async Task<List<StaffRow>> ListAsync()
    {
        s.Require(Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<StaffRow>(
            "SELECT u.Id, u.Username, u.FullName, u.Email, u.Phone, r.Name AS Role, u.IsActive, u.LastLoginAt FROM Users u JOIN Roles r ON r.Id=u.RoleId " +
            "WHERE r.Name<>'Guest' ORDER BY r.Name, u.FullName");
    }

    /// <summary>Staff who can be assigned work (receptionists use this to pick a housekeeper).</summary>
    public async Task<List<StaffOption>> AssigneesAsync(string role = Roles.Housekeeper)
    {
        s.Require(Roles.Receptionist, Roles.Admin);
        await using var db = await Db.OpenAsync();
        return await db.QueryAsync<StaffOption>(
            "SELECT u.Id, u.FullName, r.Name AS Role FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE r.Name=@r AND u.IsActive=1 ORDER BY u.FullName", new { r = role });
    }

    public async Task<string> CreateAsync(string username, string fullName, string email, string phone, string role)
    {
        s.Require(Roles.Admin);
        username = username.Trim().ToUpperInvariant(); fullName = fullName.Trim();
        if (!Roles.Staff.Contains(role)) throw new DomainException("Choose Admin, Receptionist or Housekeeper.");
        if (username.Length < 3 || username.Any(char.IsWhiteSpace)) throw new DomainException("Username must be at least 3 characters with no spaces.");
        if (fullName.Length < 2) throw new DomainException("Enter the staff member's full name.");
        await using var db = await Db.BeginAsync();
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@u", new { u = username }) > 0)
            throw new DomainException("That username is already taken.");
        var temp = Passwords.Generate();
        var roleId = await db.ScalarAsync<int>("SELECT Id FROM Roles WHERE Name=@n", new { n = role });
        var id = await db.InsertAsync(
            "INSERT INTO Users (Username, FullName, Email, Phone, PasswordHash, RoleId, MustChangePassword, CreatedAt) VALUES (@u,@f,@e,@p,@h,@r,1,UTC_TIMESTAMP())",
            new { u = username, f = fullName, e = email.Trim(), p = phone.Trim(), h = Passwords.Hash(temp), r = roleId });
        await Audit.LogAsync(db, s, "Staff created", "User", id, $"{username} as {role}");
        await db.CommitAsync();
        return temp;
    }

    public async Task UpdateAsync(int userId, string fullName, string email, string phone, string role)
    {
        s.Require(Roles.Admin);
        if (!Roles.Staff.Contains(role)) throw new DomainException("Choose Admin, Receptionist or Housekeeper.");
        if (fullName.Trim().Length < 2) throw new DomainException("Enter the staff member's full name.");
        await using var db = await Db.BeginAsync();
        var cur = await db.FirstAsync<StaffRow>(
            "SELECT u.Id, u.Username, u.FullName, u.Email, u.Phone, r.Name AS Role, u.IsActive, u.LastLoginAt FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=@id AND r.Name<>'Guest'",
            new { id = userId }) ?? throw new DomainException("Staff member not found.");
        if (cur.Role == Roles.Admin && role != Roles.Admin) await EnsureAnotherAdminAsync(db, userId);
        var roleId = await db.ScalarAsync<int>("SELECT Id FROM Roles WHERE Name=@n", new { n = role });
        await db.ExecAsync("UPDATE Users SET FullName=@f, Email=@e, Phone=@p, RoleId=@r WHERE Id=@id",
            new { f = fullName.Trim(), e = email.Trim(), p = phone.Trim(), r = roleId, id = userId });
        await Audit.LogAsync(db, s, "Staff updated", "User", userId, $"{cur.Username}: role {cur.Role} -> {role}");
        await db.CommitAsync();
    }

    public async Task SetActiveAsync(int userId, bool active)
    {
        s.Require(Roles.Admin);
        if (!active && userId == s.UserId) throw new DomainException("You cannot disable your own account.");
        await using var db = await Db.BeginAsync();
        var role = await db.ScalarAsync<string>("SELECT r.Name FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=@id AND r.Name<>'Guest'", new { id = userId })
                   ?? throw new DomainException("Staff member not found.");
        if (!active && role == Roles.Admin) await EnsureAnotherAdminAsync(db, userId);
        await db.ExecAsync("UPDATE Users SET IsActive=@a WHERE Id=@id", new { a = active, id = userId });
        await Audit.LogAsync(db, s, active ? "Staff enabled" : "Staff disabled", "User", userId, "");
        await db.CommitAsync();
    }

    public async Task<string> ResetPasswordAsync(int userId)
    {
        s.Require(Roles.Admin);
        await using var db = await Db.BeginAsync();
        if (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=@id AND r.Name<>'Guest'", new { id = userId }) == 0)
            throw new DomainException("Staff member not found.");
        var temp = Passwords.Generate();

        await db.ExecAsync("UPDATE Users SET PasswordHash=@h, MustChangePassword=1 WHERE Id=@id", new { h = Passwords.Hash(temp), id = userId });

        await Audit.LogAsync(db, s, "Staff password reset", "User", userId, "");
        await db.CommitAsync();
        return temp;
    }

    static async Task EnsureAnotherAdminAsync(Db db, int exceptUserId)
    {
        var others = await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE r.Name='Admin' AND u.IsActive=1 AND u.Id<>@id", new { id = exceptUserId });
        if (others == 0) throw new DomainException("There must be at least one active admin.");
    }
}
