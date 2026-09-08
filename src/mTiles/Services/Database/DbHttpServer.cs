using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace mTiles.Services.Database;

public sealed class DbHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly DbRegistry _registry;
    private readonly DbLogger _logger;
    private readonly DatabaseServiceManager _manager;
    private volatile bool _running;
    private Task? _acceptLoop;
    private const long MaxBodySize = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOpts = new() { Converters = { new JsonStringEnumConverter() } };

    public DbHttpServer(int port, DbRegistry registry, DbLogger logger, DatabaseServiceManager manager)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _registry = registry;
        _logger = logger;
        _manager = manager;
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _logger.Write($"HTTP server listening on {string.Join(", ", _listener.Prefixes)}", "System");

        _acceptLoop = Task.Run(() =>
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    _ = Task.Run(() => HandleRequestAsync(context));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    public void Stop()
    {
        if (!_running && _acceptLoop is null) return;

        _running = false;
        try { _listener.Stop(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _listener.Close(); } catch { }
        _acceptLoop = null;

        // Said out loud, and that is the point of it: the registration is with http.sys, so the port
        // stays listening for as long as this process does — and when the next launch reports the port
        // taken, this line is the only way to tell a bridge that was never closed from one that was.
        try { _logger.Write("HTTP server stopped", "System"); } catch { }
    }

    public void Dispose() => Stop();

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var sw = Stopwatch.StartNew();
        string? sql = null;
        string clientIp = context.Request.RemoteEndPoint?.Address.ToString() ?? "?";

        try
        {
            var request = context.Request;
            var response = context.Response;

            var rawHost = request.Headers["Host"]?.Trim() ?? "";
            string host;
            if (rawHost.StartsWith('['))
            {
                // IPv6: "Host: [::1]:18090" — extract address without brackets
                var closeBracket = rawHost.IndexOf(']');
                if (closeBracket < 0)
                {
                    RespondError(response, sw, 400, "Invalid Host header", clientIp, "-", null);
                    return;
                }
                host = rawHost[1..closeBracket].ToLowerInvariant();
            }
            else
            {
                host = rawHost.Split(':')[0].ToLowerInvariant();
            }
            if (host != "localhost" && host != "127.0.0.1" && host != "::1")
            {
                RespondError(response, sw, 400, "Invalid Host header", clientIp, "-", null);
                return;
            }

            string path = request.Url!.AbsolutePath.TrimEnd('/');

            if (path.Equals("/databases", StringComparison.OrdinalIgnoreCase))
            {
                string json = HandleDatabases();
                Respond(response, sw, 200, json, clientIp, "-", null);
                return;
            }

            const string prefix = "/query/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length)
            {
                RespondError(response, sw, 404, "Not found. Use /query/{server}/{database}", clientIp, "-", null);
                return;
            }

            string instanceKey = path[prefix.Length..];
            string[] segments = instanceKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 1 || segments.Length > 3)
            {
                RespondError(response, sw, 400, "Use /query/{alias} or /query/{server}/{database}", clientIp, instanceKey, null);
                return;
            }

            string lookupKey = string.Join("/", segments).ToLowerInvariant();

            if (!_registry.TryGet(lookupKey, out var entry) || entry == null)
            {
                RespondError(response, sw, 404, $"Unknown instance '{instanceKey}'", clientIp, instanceKey, null);
                return;
            }

            var allowKey = entry.Info.Key;
            if (!_manager.IsDatabaseAllowed(allowKey))
            {
                RespondError(response, sw, 403, $"Database '{instanceKey}' is not enabled in any workspace", clientIp, instanceKey, null);
                return;
            }

            if (request.HttpMethod != "GET" && request.HttpMethod != "POST")
            {
                RespondError(response, sw, 405, "Method not allowed", clientIp, instanceKey, null);
                return;
            }

            if (request.HttpMethod == "GET")
            {
                sql = request.QueryString["sql"];
            }
            else
            {
                if (request.ContentLength64 > MaxBodySize)
                {
                    RespondError(response, sw, 413, $"Request body too large (max {MaxBodySize / 1024}KB)", clientIp, instanceKey, null);
                    return;
                }
                using var limitedStream = new LimitedStream(request.InputStream, MaxBodySize);
                using var reader = new StreamReader(limitedStream, request.ContentEncoding);
                sql = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                RespondError(response, sw, 400, "Missing SQL query", clientIp, instanceKey, null);
                return;
            }

            bool writeAllowed = _manager.IsDatabaseWriteAllowed(allowKey);

            try
            {
                if (entry.Provider != null)
                    SqlGuard.Validate(sql, writeAllowed, entry.Provider.GuardProfile);
            }
            catch (UnauthorizedAccessException) when (!writeAllowed)
            {
                var approved = await _manager.RequestWriteAccessAsync(allowKey, sql);
                if (!approved)
                {
                    RespondError(response, sw, 403, "Write access denied by user", clientIp, instanceKey, sql);
                    return;
                }
                try
                {
                    if (entry.Provider != null)
                        SqlGuard.Validate(sql, true, entry.Provider.GuardProfile);
                }
                catch (UnauthorizedAccessException ex2)
                {
                    RespondError(response, sw, 403, ex2.Message, clientIp, instanceKey, sql);
                    return;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                RespondError(response, sw, 403, ex.Message, clientIp, instanceKey, sql);
                return;
            }
            catch (ArgumentException ex)
            {
                RespondError(response, sw, 400, ex.Message, clientIp, instanceKey, sql);
                return;
            }

            if (entry.Handler == null)
            {
                RespondError(response, sw, 500, "No query handler for this instance", clientIp, instanceKey, sql);
                return;
            }

            try
            {
                string json = entry.Handler.Execute(sql, 120);
                entry.Info.LastUsed = DateTime.UtcNow;
                Respond(response, sw, 200, json, clientIp, instanceKey, sql);
            }
            catch (Exception ex)
            {
                RespondError(response, sw, 500, ex.Message, clientIp, instanceKey, sql);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.WriteQuery(clientIp, "-", sql, 500, sw.ElapsedMilliseconds, 0, "Unhandled: " + ex.Message);
            try { SendJson(context.Response, 500, JsonSerializer.Serialize(new { error = "Internal server error" })); } catch { }
        }
    }

    private void Respond(HttpListenerResponse response, Stopwatch sw, int statusCode, string json,
        string clientIp, string instance, string? sql)
    {
        SendJson(response, statusCode, json);
        sw.Stop();
        _logger.WriteQuery(clientIp, instance, sql, statusCode, sw.ElapsedMilliseconds, json.Length, null);
    }

    private void RespondError(HttpListenerResponse response, Stopwatch sw, int statusCode, string message,
        string clientIp, string instance, string? sql)
    {
        var json = JsonSerializer.Serialize(new { error = message });
        SendJson(response, statusCode, json);
        sw.Stop();
        _logger.WriteQuery(clientIp, instance, sql, statusCode, sw.ElapsedMilliseconds, 0, message);
    }

    private string HandleDatabases()
    {
        var list = new List<object>();
        foreach (var entry in _registry.Entries)
        {
            if (!_manager.IsDatabaseAllowed(entry.Info.Key)) continue;
            var item = new Dictionary<string, object?>
            {
                ["server"] = entry.Info.Server,
                ["instance"] = entry.Info.Instance,
                ["database"] = entry.Info.Database,
                ["provider"] = entry.Info.Provider,
                ["source"] = entry.Info.Source,
                ["allowModifications"] = _manager.IsDatabaseWriteAllowed(entry.Info.Key)
            };
            if (!string.IsNullOrWhiteSpace(entry.Info.Alias))
                item["alias"] = entry.Info.Alias;
            list.Add(item);
        }
        return JsonSerializer.Serialize(list, JsonOpts);
    }

    private static void SendJson(HttpListenerResponse response, int statusCode, string json)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    private sealed class LimitedStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = maxBytes - _read;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)remaining;
            int n = inner.Read(buffer, offset, count);
            _read += n;
            return n;
        }
    }
}
