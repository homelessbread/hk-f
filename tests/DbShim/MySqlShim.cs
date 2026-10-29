// TEST-ONLY stand-in for the MySqlConnector package so the logic can run in environments without NuGet access.
// Compiled only when building with -p:DbShim=true. It speaks to a real MySQL/MariaDB server through bridge.py.
#nullable disable
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MySqlConnector;

public class MySqlException : DbException { public MySqlException(string m, int number) : base(m) { Number = number; } public int Number { get; } }

public sealed class MySqlConnection : DbConnection
{
    Process proc;
    ConnectionState state = ConnectionState.Closed;
    string cs;
    public MySqlConnection(string connectionString) { cs = connectionString; }
    public override string ConnectionString { get => cs; set => cs = value; }
    public override string Database { get { var b = new DbConnectionStringBuilder { ConnectionString = cs }; return b.TryGetValue("Database", out var d) ? d.ToString() : ""; } }
    public override string DataSource => "shim";
    public override string ServerVersion => "shim";
    public override ConnectionState State => state;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Open()
    {
        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        string Get(string k, string d) => b.TryGetValue(k, out var v) ? v.ToString() : d;
        var script = Path.Combine(AppContext.BaseDirectory, "bridge.py");
        var psi = new ProcessStartInfo("python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(Get("Server", "127.0.0.1")); psi.ArgumentList.Add(Get("Port", "3306"));
        psi.ArgumentList.Add(Get("User", "root")); psi.ArgumentList.Add(Get("Password", "")); psi.ArgumentList.Add(Get("Database", ""));
        proc = Process.Start(psi)!;
        state = ConnectionState.Open;
        try { Send(new { op = "ping_dummy_never" }, ignoreError: true); } catch { }
    }

    internal JsonElement Send(object req, bool ignoreError = false)
    {
        lock (this)
        {
            proc.StandardInput.WriteLine(JsonSerializer.Serialize(req));
            proc.StandardInput.Flush();
            var line = proc.StandardOutput.ReadLine();
            if (line is null) throw new MySqlException("bridge died: " + proc.StandardError.ReadToEnd(), 0);
            var doc = JsonDocument.Parse(line).RootElement.Clone();
            if (!doc.GetProperty("ok").GetBoolean() && !ignoreError)
                throw new MySqlException(doc.GetProperty("err").GetString(), doc.GetProperty("code").GetInt32());
            return doc;
        }
    }

    public override void Close()
    {
        if (proc is null) return;
        try { proc.StandardInput.Close(); proc.WaitForExit(2000); if (!proc.HasExited) proc.Kill(); } catch { }
        proc = null; state = ConnectionState.Closed;
    }
    protected override void Dispose(bool disposing) { Close(); base.Dispose(disposing); }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) { Send(new { op = "begin" }); return new ShimTransaction(this); }
    protected override DbCommand CreateDbCommand() => new ShimCommand { Connection = this };
}

sealed class ShimTransaction(MySqlConnection c) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.RepeatableRead;
    protected override DbConnection DbConnection => c;
    public override void Commit() => c.Send(new { op = "commit" });
    public override void Rollback() => c.Send(new { op = "rollback" });
}

sealed class ShimParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; }
    public override int Size { get; set; }
    public override string SourceColumn { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override object Value { get; set; }
    public override void ResetDbType() { }
}

sealed class ShimParams : DbParameterCollection
{
    readonly List<ShimParameter> list = new();
    public override int Count => list.Count;
    public override object SyncRoot => list;
    public override int Add(object value) { list.Add((ShimParameter)value); return list.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v); }
    public override void Clear() => list.Clear();
    public override bool Contains(object value) => list.Contains((ShimParameter)value);
    public override bool Contains(string value) => list.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => list.GetEnumerator();
    public override int IndexOf(object value) => list.IndexOf((ShimParameter)value);
    public override int IndexOf(string parameterName) => list.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => list.Insert(index, (ShimParameter)value);
    public override void Remove(object value) => list.Remove((ShimParameter)value);
    public override void RemoveAt(int index) => list.RemoveAt(index);
    public override void RemoveAt(string parameterName) => list.RemoveAll(p => p.ParameterName == parameterName);
    protected override DbParameter GetParameter(int index) => list[index];
    protected override DbParameter GetParameter(string parameterName) => list.First(p => p.ParameterName == parameterName);
    protected override void SetParameter(int index, DbParameter value) => list[index] = (ShimParameter)value;
    protected override void SetParameter(string parameterName, DbParameter value) => list[IndexOf(parameterName)] = (ShimParameter)value;
    internal IEnumerable<ShimParameter> All => list;
}

sealed class ShimCommand : DbCommand
{
    readonly ShimParams ps = new();
    public override string CommandText { get; set; }
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => ps;
    protected override DbTransaction DbTransaction { get; set; }
    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new ShimParameter();

    JsonElement Run()
    {
        var dict = new Dictionary<string, object>();
        foreach (var p in ps.All)
        {
            var v = p.Value;
            object o = v switch
            {
                null or DBNull => null,
                DateTime d => new { t = d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) },
                decimal m => new { d = m.ToString(CultureInfo.InvariantCulture) },
                _ => v
            };
            dict[p.ParameterName.TrimStart('@')] = o;
        }
        return ((MySqlConnection)DbConnection).Send(new { op = "exec", sql = CommandText, @params = dict });
    }

    public override int ExecuteNonQuery() => Run().GetProperty("affected").GetInt32();
    public override object ExecuteScalar()
    {
        var r = Run();
        var rows = r.GetProperty("rows");
        if (rows.GetArrayLength() == 0) return null;
        return ShimReader.Val(rows[0][0]);
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new ShimReader(Run());
}

sealed class ShimReader : DbDataReader
{
    readonly string[] cols; readonly List<object[]> rows = new(); int pos = -1;
    public ShimReader(JsonElement r)
    {
        cols = r.GetProperty("cols").EnumerateArray().Select(x => x.GetString()).ToArray();
        foreach (var row in r.GetProperty("rows").EnumerateArray()) rows.Add(row.EnumerateArray().Select(Val).ToArray());
    }
    internal static object Val(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.Object => e.TryGetProperty("d", out var d) ? decimal.Parse(d.GetString(), CultureInfo.InvariantCulture)
                              : DateTime.ParseExact(e.GetProperty("t").GetString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => e.ToString()
    };
    public override int Depth => 0;
    public override int FieldCount => cols.Length;
    public override bool HasRows => rows.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override bool Read() => ++pos < rows.Count;
    public override bool NextResult() => false;
    public override string GetName(int ordinal) => cols[ordinal];
    public override int GetOrdinal(string name) => Array.FindIndex(cols, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    public override object GetValue(int ordinal) => rows[pos][ordinal];
    public override int GetValues(object[] values) { var n = Math.Min(values.Length, cols.Length); for (var i = 0; i < n; i++) values[i] = rows[pos][i]; return n; }
    public override bool IsDBNull(int ordinal) => rows[pos][ordinal] is DBNull;
    public override string GetString(int o) => Convert.ToString(GetValue(o), CultureInfo.InvariantCulture);
    public override int GetInt32(int o) => Convert.ToInt32(GetValue(o));
    public override long GetInt64(int o) => Convert.ToInt64(GetValue(o));
    public override short GetInt16(int o) => Convert.ToInt16(GetValue(o));
    public override byte GetByte(int o) => Convert.ToByte(GetValue(o));
    public override bool GetBoolean(int o) => Convert.ToBoolean(GetValue(o));
    public override decimal GetDecimal(int o) => Convert.ToDecimal(GetValue(o));
    public override double GetDouble(int o) => Convert.ToDouble(GetValue(o));
    public override float GetFloat(int o) => Convert.ToSingle(GetValue(o));
    public override DateTime GetDateTime(int o) => Convert.ToDateTime(GetValue(o));
    public override char GetChar(int o) => throw new NotSupportedException();
    public override Guid GetGuid(int o) => throw new NotSupportedException();
    public override long GetBytes(int o, long a, byte[] b, int c, int d) => throw new NotSupportedException();
    public override long GetChars(int o, long a, char[] b, int c, int d) => throw new NotSupportedException();
    public override string GetDataTypeName(int o) => GetFieldType(o).Name;
    public override Type GetFieldType(int o) => rows.Count > 0 ? rows[0][o].GetType() : typeof(object);
    public override IEnumerator GetEnumerator() => rows.GetEnumerator();
}
