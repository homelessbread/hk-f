using System.Reflection;

namespace HotelKeeper.Data;

/// <summary>
/// First-run setup: creates the database and tables from the embedded schema.sql (idempotent) and, only when the
/// database has no roles yet, inserts the starting data (roles, room types, rooms, checklist, requestable items,
/// settings and three staff accounts that must change their password at first sign-in).
/// </summary>
public static class DatabaseSetup
{
    public static readonly (string Username, string Password, string Role)[] InitialAccounts =
    {
        ("admin", "Admin12345", "Admin"),
        ("reception", "Reception123", "Receptionist"),
        ("housekeeper", "Housekeeper123", "Housekeeper"),
    };

    public static async Task EnsureAsync()
    {
        var (serverOnly, dbName) = SplitConnection(AppConfig.ConnectionString);

        // 1. Schema (CREATE DATABASE IF NOT EXISTS + CREATE TABLE IF NOT EXISTS).
        var script = ReadSchema();
        if (!string.IsNullOrEmpty(dbName))
            script = script.Replace("hotelkeeper", dbName, StringComparison.OrdinalIgnoreCase);
        await using (var db = await Db.OpenAsync(serverOnly))
            foreach (var stmt in Statements(script))
                await db.ExecAsync(stmt);

        // 2. Additive migrations for databases created by an older version of the schema (MySQL has no ADD COLUMN IF NOT EXISTS).
        await using (var m = await Db.BeginAsync())
        {
            if (await m.ScalarAsync<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='Stays' AND COLUMN_NAME='ExpectedCheckOutAt'") == 0)
                await m.ExecAsync("ALTER TABLE Stays ADD COLUMN ExpectedCheckOutAt DATETIME NULL AFTER ExpectedCheckOutDate");
            await m.CommitAsync();
        }

        // 3. Starting data, once.
        await using var d = await Db.BeginAsync();
        if (await d.ScalarAsync<long>("SELECT COUNT(*) FROM Roles") > 0) return;

        foreach (var r in new[] { "Admin", "Receptionist", "Housekeeper", "Guest" })
            await d.ExecAsync("INSERT INTO Roles (Name) VALUES (@n)", new { n = r });

        foreach (var (user, pass, role) in InitialAccounts)
            await d.ExecAsync(
                "INSERT INTO Users (Username, FullName, PasswordHash, RoleId, MustChangePassword) " +
                "SELECT @u, @f, @h, Id, 1 FROM Roles WHERE Name=@r",
                new { u = user, f = char.ToUpper(user[0]) + user[1..], h = Passwords.Hash(pass), r = role });

        var types = new[]
        {
            ("Standard", "Queen bed, work desk, city view.", 2500m, 2),
            ("Deluxe", "King bed, sitting area, larger bathroom.", 3800m, 3),
            ("Family Suite", "Two bedrooms, kitchenette, living room.", 6200m, 5),
        };
        foreach (var (n, desc, rate, cap) in types)
            await d.ExecAsync("INSERT INTO RoomTypes (Name, Description, BaseRate, Capacity) VALUES (@n,@d,@r,@c)", new { n, d = desc, r = rate, c = cap });

        for (var floor = 1; floor <= 3; floor++)
            for (var i = 1; i <= 4; i++)
            {
                var typeName = floor == 3 ? "Family Suite" : i <= 2 ? "Standard" : "Deluxe";
                await d.ExecAsync(
                    "INSERT INTO Rooms (RoomNumber, Building, `Floor`, RoomTypeId, Capacity, Rate, Amenities, Description) " +
                    "SELECT @num, 'Main', @fl, Id, Capacity, BaseRate, 'Air conditioning, Wi-Fi, TV, Private bathroom', Description FROM RoomTypes WHERE Name=@t",
                    new { num = $"{floor}0{i}", fl = floor, t = typeName });
            }

        foreach (var (name, cat, max, rule) in new[]
        {
            ("Towels", "Linen", 4, ""), ("Pillows", "Linen", 4, ""), ("Blankets", "Linen", 2, ""),
            ("Tissues", "Supplies", 3, ""), ("Toiletries set", "Supplies", 2, ""), ("Drinking water", "Supplies", 6, "Bottled, delivered cold"),
        })
            await d.ExecAsync("INSERT INTO RequestableItems (Name, ItemCategory, MaxQuantity, Rule) VALUES (@name,@cat,@max,@rule)", new { name, cat, max, rule });

        var order = 1;
        foreach (var label in new[] { "Change bed linens", "Clean bathroom", "Replace towels", "Replenish toiletries and supplies", "Vacuum / mop floors", "Check minibar and amenities" })
            await d.ExecAsync("INSERT INTO ChecklistTemplate (Label, SortOrder) VALUES (@l,@o)", new { l = label, o = order++ });

        foreach (var (k, v, desc) in new[]
        {
            ("HotelName", "Hotel Keeper", "Name shown in the application"),
            ("CancellationHours", "24", "Guests may cancel free of charge until this many hours before check-in"),
            ("CheckoutHour", "11", "Standard check-out hour; late check-outs extend the expected check-out time"),
        })
            await d.ExecAsync("INSERT INTO Settings (SettingKey, SettingValue, Description) VALUES (@k,@v,@desc)", new { k, v, desc });

        await d.CommitAsync();

        var envSeed = Environment.GetEnvironmentVariable("HOTELKEEPER_SEED_DEV");
        if (string.Equals(envSeed, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(envSeed, "true", StringComparison.OrdinalIgnoreCase))
        {
            await DevelopmentSampleData.SeedAsync();
        }
    }

    static string ReadSchema()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema.sql")
            ?? throw new InvalidOperationException("schema.sql is not embedded in the build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static IEnumerable<string> Statements(string script)
    {
        var lines = script.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        foreach (var part in string.Join('\n', lines).Split(';'))
            if (part.Trim().Length > 0) yield return part.Trim();
    }

    /// <summary>Returns the connection string without its Database part, and the database name that was in it.</summary>
    static (string serverOnly, string db) SplitConnection(string cs)
    {
        var keep = new List<string>();
        var db = "";
        foreach (var pair in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = kv[0].Trim();
            if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                db = kv.Length > 1 ? kv[1].Trim() : "";
            else keep.Add(pair);
        }
        return (string.Join(';', keep), db);
    }
}
