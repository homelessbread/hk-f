using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;

namespace HotelKeeper.Core.Data;

/// <summary>Creates connections. The WPF app supplies a MySqlConnector-based implementation.</summary>
public interface IConnectionFactory
{
    /// <summary>Returns a new, opened connection.</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>
/// Tiny parameterised-SQL helper: anonymous object properties become @parameters
/// (never string concatenation), and result rows map onto model properties by column name.
/// </summary>
public static class Db
{
    static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> PropertyCache = new();

    static DbCommand Build(DbConnection c, DbTransaction? tx, string sql, object? args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        if (args is not null)
        {
            foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = "@" + p.Name;
                var v = p.GetValue(args);
                prm.Value = v ?? DBNull.Value;
                cmd.Parameters.Add(prm);
            }
        }
        return cmd;
    }

    public static async Task<int> ExecAsync(this DbConnection c, string sql, object? args = null, DbTransaction? tx = null)
    {
        await using var cmd = Build(c, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Runs an INSERT and returns the generated auto-increment id.</summary>
    public static async Task<int> InsertAsync(this DbConnection c, string sql, object? args = null, DbTransaction? tx = null)
    {
        await c.ExecAsync(sql, args, tx);
        return await c.ScalarAsync<int>("SELECT LAST_INSERT_ID()", null, tx);
    }

    public static async Task<T> ScalarAsync<T>(this DbConnection c, string sql, object? args = null, DbTransaction? tx = null)
    {
        await using var cmd = Build(c, tx, sql, args);
        var v = await cmd.ExecuteScalarAsync();
        return v is null || v is DBNull ? default! : (T)Convert(v, typeof(T))!;
    }

    public static async Task<List<T>> QueryAsync<T>(this DbConnection c, string sql, object? args = null, DbTransaction? tx = null) where T : new()
    {
        await using var cmd = Build(c, tx, sql, args);
        await using var r = await cmd.ExecuteReaderAsync();
        var map = Properties(typeof(T));
        var columns = new PropertyInfo?[r.FieldCount];
        for (var i = 0; i < r.FieldCount; i++)
            columns[i] = map.TryGetValue(r.GetName(i), out var p) ? p : null;

        var list = new List<T>();
        while (await r.ReadAsync())
        {
            var item = new T();
            for (var i = 0; i < columns.Length; i++)
            {
                var prop = columns[i];
                if (prop is null || await r.IsDBNullAsync(i)) continue;
                prop.SetValue(item, Convert(r.GetValue(i), prop.PropertyType));
            }
            list.Add(item);
        }
        return list;
    }

    public static async Task<T?> FirstOrDefaultAsync<T>(this DbConnection c, string sql, object? args = null, DbTransaction? tx = null) where T : class, new()
        => (await c.QueryAsync<T>(sql, args, tx)).FirstOrDefault();

    static Dictionary<string, PropertyInfo> Properties(Type t) => PropertyCache.GetOrAdd(t, type =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase));

    static object? Convert(object value, Type target)
    {
        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(value)) return value;
        if (type == typeof(bool))
            return value is string s ? s is "1" or "True" or "true" : System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        if (type == typeof(DateTime))
            return value is string ds ? DateTime.Parse(ds, CultureInfo.InvariantCulture) : System.Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// MySQL error number of a driver exception, e.g. 1062 duplicate key, 1213 deadlock.
    /// Read by reflection so Core does not depend on a specific MySQL package.
    /// </summary>
    public static int ErrorNumber(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is not DbException) continue;
            var n = e.GetType().GetProperty("Number")?.GetValue(e);
            if (n is int i) return i;
        }
        return 0;
    }
}
