"""Test-only helper: executes SQL for the C# DbShim over a real MySQL/MariaDB server (one process per connection)."""
import sys, json, re, datetime, decimal
import pymysql

host, port, user, password, db = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4], sys.argv[5]
conn = pymysql.connect(host=host, port=port, user=user, password=password, database=db or None, autocommit=True, charset="utf8mb4")

def enc(v):
    if isinstance(v, decimal.Decimal): return {"d": str(v)}
    if isinstance(v, datetime.datetime): return {"t": v.strftime("%Y-%m-%d %H:%M:%S")}
    if isinstance(v, datetime.date): return {"t": v.strftime("%Y-%m-%d 00:00:00")}
    if isinstance(v, (bytes, bytearray)): return v.decode("utf8", "replace")
    return v

def dec(v):
    if isinstance(v, dict):
        if "d" in v: return decimal.Decimal(v["d"])
        if "t" in v: return datetime.datetime.strptime(v["t"], "%Y-%m-%d %H:%M:%S")
    return v

for line in sys.stdin:
    req = json.loads(line)
    try:
        op = req["op"]
        if op == "begin": conn.begin(); out = {"ok": True, "cols": [], "rows": [], "affected": 0}
        elif op == "commit": conn.commit(); out = {"ok": True, "cols": [], "rows": [], "affected": 0}
        elif op == "rollback": conn.rollback(); out = {"ok": True, "cols": [], "rows": [], "affected": 0}
        else:
            params = {k: dec(v) for k, v in (req.get("params") or {}).items()}
            sql = req["sql"].replace("%", "%%")
            sql = re.sub(r"@(\w+)", lambda m: "%(" + m.group(1) + ")s" if m.group(1) in params else m.group(0), sql)
            with conn.cursor() as cur:
                cur.execute(sql, params or None)
                cols = [d[0] for d in cur.description] if cur.description else []
                rows = [[enc(x) for x in r] for r in cur.fetchall()] if cur.description else []
                out = {"ok": True, "cols": cols, "rows": rows, "affected": cur.rowcount}
    except Exception as e:
        out = {"ok": False, "err": str(e), "code": e.args[0] if e.args and isinstance(e.args[0], int) else 0}
    sys.stdout.write(json.dumps(out) + "\n"); sys.stdout.flush()
