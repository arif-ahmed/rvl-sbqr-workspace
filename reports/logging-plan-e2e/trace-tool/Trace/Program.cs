// Live investigation trace - exercises the running API + DB and prints
// exact commands + exact outputs as a developer/ops would see them.

using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

const string BaseUrl  = "http://127.0.0.1:5001";
const string ClientId = "000085-46a766da";
const string ClientSecret = "cBb9DsIfJEUT96ba9WbLm7jxAEGrwELYnv9AMwUDGII";

var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };

// Path constants used throughout - declared early so they are visible to the
// correlation_id scraper in step 3 (which needs to read the log immediately
// after the first /v1/qr/validate response).
string logPath = @"D:\Workspace\Sources\RVL\rvl-sbqr\rvl-sbqr-workspace\rvl-secure-bqr-manager\logs\api-stdout.log";
string connStr = "Host=localhost;Port=5432;Database=sbqr_app;Username=postgres;Password=postgres;Include Error Detail=true";

// FileShare.ReadWrite lets us tail the log while the dotnet API is still appending.
string[] ReadAllLinesShared(string p)
{
    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var sr = new StreamReader(fs);
    var list = new List<string>();
    string? line;
    while ((line = sr.ReadLine()) != null) list.Add(line);
    return list.ToArray();
}

Console.WriteLine("================================================================");
Console.WriteLine("STEP 1 - Get OAuth token");
Console.WriteLine("================================================================");
Console.WriteLine("$ curl -X POST " + BaseUrl + "/v1/oauth/token \\");
Console.WriteLine("       -d grant_type=client_credentials \\");
Console.WriteLine("       -d client_id=" + ClientId + " \\");
Console.WriteLine("       -d client_secret=<redacted> \\");
Console.WriteLine("       -d scope=qr:generate");

var tokenResp = await http.PostAsync("/v1/oauth/token",
    new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"]    = "client_credentials",
        ["client_id"]     = ClientId,
        ["client_secret"] = ClientSecret,
        ["scope"]         = "qr:generate",
    }));
Console.WriteLine("HTTP " + (int)tokenResp.StatusCode);
var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>();
var token = tokenJson.GetProperty("accessToken").GetString();
Console.WriteLine(JsonSerializer.Serialize(tokenJson, new JsonSerializerOptions { WriteIndented = true }));
http.DefaultRequestHeaders.Authorization =
    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

// Decode the JWT payload so we can show the tenant_id embedded in it.
string? jwtTenantId = null;
string? jwtSub      = null;
string? jwtScope    = null;
try
{
    var parts = token!.Split('.');
    var payload = parts[1];
    payload = payload.Replace('-', '+').Replace('_', '/');
    payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
    var bytes = Convert.FromBase64String(payload);
    var jwtJson = JsonDocument.Parse(bytes).RootElement;
    jwtTenantId = jwtJson.TryGetProperty("tenant_id", out var t) ? t.GetString() : null;
    jwtSub      = jwtJson.TryGetProperty("sub",        out var s) ? s.GetString() : null;
    jwtScope    = jwtJson.TryGetProperty("scope",      out var sc) ? sc.ToString() : null;
    Console.WriteLine($"  [JWT decoded] sub={jwtSub}  tenant_id={jwtTenantId}  scope={jwtScope}");
}
catch (Exception ex) { Console.WriteLine("  [JWT decode failed] " + ex.Message); }

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("STEP 2 - Generate a static QR (the one customer claims got CRC_MISMATCH)");
Console.WriteLine("================================================================");
var genReq = new
{
    recipientName = "Rahima Begum",
    recipientCity = "Dhaka",
    recipientPan  = "01933333333",
};
var genResp = await http.PostAsJsonAsync("/v1/qr/generate/static?Idempotency-Key=trace-idem-001", genReq);
Console.WriteLine("$ curl -X POST " + BaseUrl + "/v1/qr/generate/static \\");
Console.WriteLine("       -H 'Authorization: Bearer <token>' \\");
Console.WriteLine("       -H 'Idempotency-Key: trace-idem-001' \\");
Console.WriteLine("       -d '" + JsonSerializer.Serialize(genReq) + "'");
Console.WriteLine("HTTP " + (int)genResp.StatusCode);
var genJson = await genResp.Content.ReadFromJsonAsync<JsonElement>();
var qrPayload = genJson.GetProperty("qrPayload").GetString()!;
var payloadHash = genJson.GetProperty("payloadHash").GetString()!;
Console.WriteLine(JsonSerializer.Serialize(genJson, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(">>> payload_hash captured for tracing: " + payloadHash);

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("STEP 3 - Customer says it failed: tampered CRC (last 4 bytes zeroed)");
Console.WriteLine("================================================================");
var tampered = qrPayload[..^4] + "0000";
Console.WriteLine("Original CRC tail: " + qrPayload[^4..]);
Console.WriteLine("Tampered CRC tail: " + tampered[^4..]);
var v1Req = new
{
    qrPayload = tampered,
    requestId = "complaint-trace-req-001",
};
var v1ReqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/qr/validate")
{
    Content = JsonContent.Create(v1Req),
};
v1ReqMsg.Headers.Add("X-Correlation-Id", "investigation-trace-001");
var v1Resp = await http.SendAsync(v1ReqMsg);
Console.WriteLine();
Console.WriteLine("$ curl -X POST " + BaseUrl + "/v1/qr/validate \\");
Console.WriteLine("       -H 'Authorization: Bearer <token>' \\");
Console.WriteLine("       -H 'X-Correlation-Id: investigation-trace-001' \\");
Console.WriteLine("       -d '" + JsonSerializer.Serialize(v1Req) + "'");
Console.WriteLine("HTTP " + (int)v1Resp.StatusCode);
Console.WriteLine("Response headers:");
foreach (var h in v1Resp.Headers)
    Console.WriteLine("  " + h.Key + ": " + string.Join(", ", h.Value));
var v1Json = await v1Resp.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine("Response body:");
Console.WriteLine(JsonSerializer.Serialize(v1Json, new JsonSerializerOptions { WriteIndented = true }));
// We need the server-issued correlation_id (UUID, used in audit_logs.correlation_id).
// It's not in the response body/header here - we'll scrape it from the log file.
await Task.Delay(500);
string? corr1 = null;
foreach (var line in ReadAllLinesShared(logPath).Reverse().Take(50))
{
    using var d = JsonDocument.Parse(line);
    var s = d.RootElement.GetProperty("State");
    var cid = s.TryGetProperty("correlation_id", out var ci) ? ci.GetString() : null;
    var caller = s.TryGetProperty("caller_correlation_id", out var cc) ? cc.GetString() : null;
    var reqId = s.TryGetProperty("request_id", out var ri) ? ri.GetString() : null;
    if (caller == "investigation-trace-001" && reqId == "complaint-trace-req-001")
    {
        corr1 = cid;
        Console.WriteLine($"  [scrape] Found server correlation_id={corr1} (caller_correlation_id={caller}, request_id={reqId})");
        break;
    }
}
if (string.IsNullOrEmpty(corr1))
{
    corr1 = "investigation-trace-001";
    Console.WriteLine(">>> correlation_id (server returns X-Correlation-Id only when caller sets one; here we used our own)");
}
Console.WriteLine(">>> correlation_id (server-issued) for DB lookups: " + corr1);

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("STEP 4 - Replay: same request_id within the replay window");
Console.WriteLine("================================================================");
var v2Resp = await http.PostAsJsonAsync("/v1/qr/validate", v1Req);
Console.WriteLine("$ curl -X POST " + BaseUrl + "/v1/qr/validate \\");
Console.WriteLine("       -H 'Authorization: Bearer <token>' \\");
Console.WriteLine("       -d '" + JsonSerializer.Serialize(v1Req) + "'  # IDENTICAL to step 3");
Console.WriteLine("HTTP " + (int)v2Resp.StatusCode);
var v2Json = await v2Resp.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine(JsonSerializer.Serialize(v2Json, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("STEP 5 - Wait 2s, then run investigation queries");
Console.WriteLine("================================================================");
await Task.Delay(2000);

await using (var probeConn = new NpgsqlConnection(connStr))
{
    await probeConn.OpenAsync();
    Console.WriteLine("================================================================");
    Console.WriteLine("SCHEMA PROBE - discover audit_logs + tenants column shape");
    Console.WriteLine("================================================================");
    var schemaCmd = new NpgsqlCommand(
        @"SELECT table_name, column_name, data_type
          FROM information_schema.columns
          WHERE table_schema='public' AND table_name IN ('audit_logs','tenants','qr_validations')
          ORDER BY table_name, ordinal_position", probeConn);
    await using var sr = await schemaCmd.ExecuteReaderAsync();
    string cur = "";
    while (await sr.ReadAsync())
    {
        var t = sr.GetString(0); var colName = sr.GetString(1); var ty = sr.GetString(2);
        if (t != cur) { cur = t; Console.WriteLine($"\n  --- {cur} ---"); }
        Console.WriteLine($"    {colName,-30} {ty}");
    }
}

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("ANCHOR A - Find every log line for this complaint's correlation_id");
Console.WriteLine("================================================================");
Console.WriteLine("$ grep '" + corr1 + "' '" + logPath + "'");
var lines = ReadAllLinesShared(logPath).Where(l => l.Contains(corr1!)).ToArray();
foreach (var line in lines.Take(20))
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    var state = root.GetProperty("State");
    var msg = root.GetProperty("Message").GetString()!.Replace("\r", "").Replace("\n", " | ");
    var cat = root.GetProperty("Category").GetString();
    Console.WriteLine($"  [{cat}] {msg}");
    if (state.TryGetProperty("Duration", out var d))
        Console.WriteLine($"      duration={d}");
}
Console.WriteLine($"  >>> {lines.Length} total log lines for correlation_id={corr1}");

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("ANCHOR A2 - Find every log line that mentions this payload_hash");
Console.WriteLine("================================================================");
Console.WriteLine("$ grep '" + payloadHash + "' '" + logPath + "'");
var phLines = ReadAllLinesShared(logPath).Where(l => l.Contains(payloadHash)).ToArray();
foreach (var line in phLines.Take(10))
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    var state = root.GetProperty("State");
    var path = state.TryGetProperty("Path", out var p) ? p.GetString() : "?";
    var verdict = state.TryGetProperty("verdict", out var vc) ? vc.GetString() : "?";
    Console.WriteLine($"  [{path}] verdict={verdict}");
}
Console.WriteLine($"  >>> {phLines.Length} log lines for payload_hash={payloadHash}");

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("ANCHOR C - Walk the audit_logs table for this correlation_id");
Console.WriteLine("================================================================");
Console.WriteLine("$ psql 'Host=localhost ... dbname=sbqr_app' -c \"");
Console.WriteLine("    SELECT action, resource_id, occurred_at, metadata");
Console.WriteLine("    FROM audit_logs");
Console.WriteLine("    WHERE metadata->>'correlation_id' = '" + corr1 + "'");
Console.WriteLine("    ORDER BY occurred_at;\"");
await using var conn = new NpgsqlConnection(connStr);
await conn.OpenAsync();
// Discover audit_logs columns at runtime so the query works against the real schema.
var colCmd = new NpgsqlCommand(
    @"SELECT column_name FROM information_schema.columns
      WHERE table_schema='public' AND table_name='audit_logs'", conn);
var cols = new HashSet<string>();
await using (var cr = await colCmd.ExecuteReaderAsync())
    while (await cr.ReadAsync()) cols.Add(cr.GetString(0));

string Col(string c) => cols.Contains(c) ? c : "NULL";

// The schema has correlation_id as a UUID column on audit_logs (not in JSON metadata).
var hasCorrIdCol  = cols.Contains("correlation_id");
var hasEventType  = cols.Contains("event_type");
var tsCol         = cols.Contains("created_at") ? "created_at" :
                    cols.Contains("occurred_at") ? "occurred_at" : "NULL";
var hasMetadata   = cols.Contains("metadata");

string corrFilter;
NpgsqlParameter corrParam;
if (hasCorrIdCol)
{
    // correlation_id is uuid - cast the param.
    corrFilter = $"correlation_id = @c::uuid";
    corrParam  = new NpgsqlParameter("c", Guid.Parse(corr1!));
}
else
{
    corrFilter = $"metadata->>'correlation_id' = @c";
    corrParam  = new NpgsqlParameter("c", corr1!);
}

var auditCmd = new NpgsqlCommand(
    $"SELECT {Col("event_type")}, {Col("resource_type")}, {Col("resource_id")}, {tsCol}, {Col("metadata")} " +
    "FROM audit_logs " +
    $"WHERE {corrFilter} " +
    $"ORDER BY {tsCol} NULLS LAST", conn);
auditCmd.Parameters.Add(corrParam);
await using var rdr = await auditCmd.ExecuteReaderAsync();
int rowCount = 0;
while (await rdr.ReadAsync())
{
    rowCount++;
    var evType   = rdr.IsDBNull(0) ? "?" : rdr.GetString(0);
    var resType  = rdr.IsDBNull(1) ? "?" : rdr.GetString(1);
    var resource = rdr.IsDBNull(2) ? "(null)" : rdr.GetString(2);
    var ts       = rdr.IsDBNull(3) ? "?" : rdr.GetFieldValue<DateTime>(3).ToString("O");
    var meta     = rdr.IsDBNull(4) ? "(no metadata col)" : rdr.GetString(4);
    Console.WriteLine($"  [{ts}] event_type={evType}  resource_type={resType}  resource_id={resource[..Math.Min(16, resource.Length)]}...");
    Console.WriteLine($"      metadata={meta}");
}
Console.WriteLine($"  >>> {rowCount} audit row(s) for correlation_id={corr1}");
rdr.Close();

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("ANCHOR C2 - Resolve payload_hash -> audit row (regulator pivot)");
Console.WriteLine("================================================================");
Console.WriteLine("$ psql ... -c \"");
Console.WriteLine("    SELECT audit_log_id, event_type, resource_type, resource_id, metadata");
Console.WriteLine("    FROM audit_logs");
Console.WriteLine("    WHERE resource_id = '" + payloadHash + "';\"");
var c2 = new NpgsqlCommand(
    "SELECT audit_log_id, event_type, resource_type, resource_id, metadata " +
    "FROM audit_logs WHERE resource_id = @h", conn);
c2.Parameters.AddWithValue("h", payloadHash);
await using var r2 = await c2.ExecuteReaderAsync();
int rc2 = 0;
while (await r2.ReadAsync())
{
    rc2++;
    Console.WriteLine($"  audit_log_id={r2.GetGuid(0)}  event_type={r2.GetString(1)}  resource_type={r2.GetString(2)}");
    Console.WriteLine($"    resource_id={r2.GetString(3)[..Math.Min(16, r2.GetString(3).Length)]}...");
    Console.WriteLine($"    metadata={r2.GetString(4)}");
}
Console.WriteLine($"  >>> {rc2} audit row(s) for payload_hash={payloadHash}");
r2.Close();

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("ANCHOR C3 - Hash-chain integrity check for tenant 000085");
Console.WriteLine("================================================================");
var c3Cmd = new NpgsqlCommand(
    "SELECT column_name FROM information_schema.columns " +
    "WHERE table_schema='public' AND table_name='audit_logs'", conn);
var auditCols = new HashSet<string>();
await using (var r0 = await c3Cmd.ExecuteReaderAsync())
    while (await r0.ReadAsync()) auditCols.Add(r0.GetString(0));
bool hasEntry  = auditCols.Contains("entry_hash");
bool hasPrev   = auditCols.Contains("previous_hash");
string tsCol2  = auditCols.Contains("created_at") ? "created_at" :
                 auditCols.Contains("occurred_at") ? "occurred_at" : "created_at";

if (hasEntry && hasPrev)
{
    Console.WriteLine("$ psql ... -c \"");
    Console.WriteLine("    SELECT COUNT(*) FILTER (WHERE chain_ok) AS ok_rows,");
    Console.WriteLine("           COUNT(*) FILTER (WHERE NOT chain_ok) AS broken_rows");
    Console.WriteLine("    FROM (");
    Console.WriteLine("      SELECT lag(entry_hash) OVER (ORDER BY " + tsCol2 + ") = previous_hash AS chain_ok");
    Console.WriteLine("      FROM audit_logs WHERE tenant_id = (");
    Console.WriteLine("        SELECT tenant_id FROM tenants WHERE institution_code='000085'");
    Console.WriteLine("      )");
    Console.WriteLine("    ) s;\"");
    var c3 = new NpgsqlCommand($@"
SELECT
  COUNT(*) FILTER (WHERE chain_ok) AS ok_rows,
  COUNT(*) FILTER (WHERE NOT chain_ok) AS broken_rows
FROM (
  SELECT lag(entry_hash) OVER (ORDER BY {tsCol2}) = previous_hash AS chain_ok
  FROM audit_logs
  WHERE tenant_id = (
    SELECT tenant_id FROM tenants WHERE institution_code = '000085'
  )
) s;", conn);
    await using var r3 = await c3.ExecuteReaderAsync();
    await r3.ReadAsync();
    Console.WriteLine($"  ok_rows    = {r3.GetInt64(0)}");
    Console.WriteLine($"  broken_rows= {r3.GetInt64(1)}");
    r3.Close();
}
else
{
    Console.WriteLine($"  Skipped: audit_logs is missing entry_hash/previous_hash (has: {string.Join(",", auditCols)})");
}

Console.WriteLine();
Console.WriteLine("================================================================");
Console.WriteLine("ANCHOR E - Last 10 lines for any tenant_id (this window)");
Console.WriteLine("================================================================");
Console.WriteLine("$ jq -c 'select(.State.tenant_id != null)' '" + logPath + "' | tail -10");
var tenantLines = ReadAllLinesShared(logPath)
    .Where(l => l.Contains("\"tenant_id\""))
    .Select(l => JsonDocument.Parse(l))
    .ToList();
foreach (var doc in tenantLines.TakeLast(10))
{
    var s = doc.RootElement.GetProperty("State");
    var path = s.TryGetProperty("Path", out var p) ? p.GetString() : "-";
    var sc = s.TryGetProperty("StatusCode", out var st) ? st.GetInt32() : -1;
    var corr = s.TryGetProperty("correlation_id", out var c) ? c.GetString() : "-";
    var verdict = s.TryGetProperty("verdict", out var v) ? v.GetString() : "-";
    Console.WriteLine($"  {path,-40} status={sc} verdict={verdict,-20} corr={corr}");
}
Console.WriteLine($"  >>> {tenantLines.Count} log lines with tenant_id (whole run)");
