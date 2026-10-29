using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MySqlConnector;

namespace HotelKeeper.Data;

/// <summary>Business-rule violation. The message is safe to show to the user.</summary>
public class DomainException(string message) : Exception(message);

/// <summary>The signed-in user is not allowed to perform the operation.</summary>
public class AuthorizationException(string message) : DomainException(message);

public static class AppConfig
{
    public static string ConnectionString { get; set; } = "";

    /// <summary>
    /// Order of precedence: HOTELKEEPER_CONNECTION environment variable, appsettings.local.json (git-ignored),
    /// appsettings.json (checked in, no secrets).
    /// </summary>
    public static void Load(string baseDirectory)
    {
        var env = Environment.GetEnvironmentVariable("HOTELKEEPER_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) { ConnectionString = env; return; }

        foreach (var name in new[] { "appsettings.local.json", "appsettings.json" })
        {
            var path = Path.Combine(baseDirectory, name);
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("ConnectionString", out var cs) && !string.IsNullOrWhiteSpace(cs.GetString()))
            {
                ConnectionString = cs.GetString()!;
                return;
            }
        }
        throw new InvalidOperationException("No database connection string found. Create appsettings.local.json (see README).");
    }
}

/// <summary>A connection (optionally inside a transaction) with small parameterised-query helpers.</summary>
public sealed class Db : IAsyncDisposable
{
    readonly DbConnection conn;
    DbTransaction? tx;

    Db(DbConnection c) => conn = c;

    public static async Task<Db> OpenAsync(string? connectionString = null)
    {
        var c = new MySqlConnection(connectionString ?? AppConfig.ConnectionString);
        await c.OpenAsync();
        return new Db(c);
    }

    /// <summary>Opens a connection and starts a transaction. Disposing without Commit rolls back.</summary>
    public static async Task<Db> BeginAsync()
    {
        var db = await OpenAsync();
        db.tx = await db.conn.BeginTransactionAsync();
        return db;
    }

    public async Task CommitAsync()
    {
        if (tx is null) return;
        await tx.CommitAsync();
        await tx.DisposeAsync();
        tx = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (tx is not null)
        {
            try { await tx.RollbackAsync(); } catch { /* connection may already be gone */ }
            await tx.DisposeAsync();
        }
        await conn.DisposeAsync();
    }

    DbCommand Cmd(string sql, object? args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (args is not null)
        {
            foreach (var p in args.GetType().GetProperties())
            {
                var par = cmd.CreateParameter();
                par.ParameterName = "@" + p.Name;
                var v = p.GetValue(args);
                if (v is DateTime dt && p.Name.EndsWith("At", StringComparison.Ordinal)) v = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
                par.Value = v ?? DBNull.Value;
                cmd.Parameters.Add(par);
            }
        }
        return cmd;
    }

    public async Task<int> ExecAsync(string sql, object? args = null)
    {
        await using var cmd = Cmd(sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Runs an INSERT and returns the generated key.</summary>
    public async Task<int> InsertAsync(string sql, object? args = null)
    {
        await ExecAsync(sql, args);
        return await ScalarAsync<int>("SELECT LAST_INSERT_ID()");
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var cmd = Cmd(sql, args);
        var v = await cmd.ExecuteScalarAsync();
        return (T?)Mapper.Convert(v, typeof(T), "");
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? args = null)
    {
        await using var cmd = Cmd(sql, args);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        var map = Mapper.For(typeof(T));
        string[]? cols = null;
        while (await r.ReadAsync())
        {
            cols ??= Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
            var values = new object?[r.FieldCount];
            r.GetValues(values!);
            list.Add((T)map(cols, values)!);
        }
        return list;
    }

    public async Task<T?> FirstAsync<T>(string sql, object? args = null)
    {
        var rows = await QueryAsync<T>(sql, args);
        return rows.Count == 0 ? default : rows[0];
    }
}

static class Mapper
{
    static readonly Dictionary<Type, Func<string[], object?[], object?>> cache = new();

    public static Func<string[], object?[], object?> For(Type t)
    {
        lock (cache)
        {
            if (cache.TryGetValue(t, out var f)) return f;
            f = Build(t);
            cache[t] = f;
            return f;
        }
    }

    static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(object);
    }

    static Func<string[], object?[], object?> Build(Type t)
    {
        if (IsScalar(t)) return (cols, vals) => Convert(vals[0], t, cols[0]);

        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var ps = ctor.GetParameters();
        return (cols, vals) =>
        {
            var args = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var idx = Array.FindIndex(cols, c => string.Equals(c, ps[i].Name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) throw new InvalidOperationException($"Query for {t.Name} is missing column '{ps[i].Name}'.");
                args[i] = Convert(vals[idx], ps[i].ParameterType, cols[idx]);
            }
            return ctor.Invoke(args);
        };
    }

    public static object? Convert(object? v, Type target, string column)
    {
        var u = Nullable.GetUnderlyingType(target) ?? target;
        if (v is null or DBNull) return target.IsValueType && Nullable.GetUnderlyingType(target) is null ? Activator.CreateInstance(target) : null;
        if (u == typeof(object)) return v;
        if (u == typeof(bool)) return System.Convert.ToBoolean(v, CultureInfo.InvariantCulture);
        if (u == typeof(DateTime))
        {
            var d = v is DateTime dt ? dt : System.Convert.ToDateTime(v, CultureInfo.InvariantCulture);
            // Timestamps (…At) are stored as UTC; DATE columns (…Date) are plain calendar dates.
            return column.EndsWith("At", StringComparison.Ordinal) ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime() : d;
        }
        if (u == typeof(string)) return System.Convert.ToString(v, CultureInfo.InvariantCulture);
        return System.Convert.ChangeType(v, u, CultureInfo.InvariantCulture);
    }
}
