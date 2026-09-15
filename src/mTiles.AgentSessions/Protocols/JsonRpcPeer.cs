using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace mTiles.AgentSessions.Protocols;

/// <summary>
/// JSON-RPC over newline-delimited JSON, in both directions: requests we send, requests the agent sends
/// us, and notifications either way.
/// </summary>
/// <remarks>
/// <para><b>One class for two dialects.</b> ACP is JSON-RPC 2.0 and carries <c>"jsonrpc":"2.0"</c>;
/// codex's app server leaves the field out altogether (measured in t3code's generated client, and in
/// codex 0.153.2's own schema). Everything else — ids, <c>result</c>, <c>error</c> — is the same, so the
/// difference is a flag rather than a second implementation.</para>
/// <para>A request from the agent is answered with whatever its handler returns, or with the error its
/// handler throws as a <see cref="JsonRpcException"/>; a method nobody handles is answered
/// <c>-32601</c> rather than left hanging, because an agent that waits for an answer that never comes is
/// a turn that never ends.</para>
/// </remarks>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;

    private readonly AgentProcess _process;
    private readonly bool _writesVersion;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;

    private JsonRpcPeer(AgentProcess process, bool writesVersion)
    {
        _process = process;
        _writesVersion = writesVersion;
        _ = process.Exited.ContinueWith(_ => FailPending("The agent's process ended."), TaskScheduler.Default);
    }

    /// <summary>A notification arrived: the method and its params (undefined when there were none).</summary>
    /// <remarks>Synchronous, and called on the reading thread in the order the lines arrived: a stream of
    /// deltas handled by concurrently running tasks would be text assembled out of order.</remarks>
    public Action<string, JsonElement>? OnNotification { get; set; }

    /// <summary>A request arrived: answer with the result object, or throw <see cref="JsonRpcException"/>.
    /// </summary>
    public Func<string, JsonElement, Task<object?>>? OnRequest { get; set; }

    /// <summary>A line that was not JSON-RPC at all — some agents print banners on stdout.</summary>
    public Action<string>? OnStrayLine { get; set; }

    public AgentProcess Process => _process;

    /// <summary>Starts the process and speaks JSON-RPC to it.</summary>
    /// <param name="writesVersion">Whether to put <c>"jsonrpc":"2.0"</c> on what we send.</param>
    public static JsonRpcPeer Start(ProcessStartInfo psi, bool writesVersion)
    {
        JsonRpcPeer? peer = null;
        var pendingLines = new List<string>();
        var process = AgentProcess.Start(psi, line =>
        {
            // The pump can deliver a line before the constructor below has returned.
            if (peer is null)
            {
                lock (pendingLines)
                {
                    if (peer is null)
                    {
                        pendingLines.Add(line);
                        return;
                    }
                }
            }

            peer.HandleLine(line);
        });

        lock (pendingLines)
        {
            peer = new JsonRpcPeer(process, writesVersion);
            foreach (var line in pendingLines) peer.HandleLine(line);
        }

        return peer;
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        // Checked after registering, so an exit either finds this request in the table or is seen here:
        // the process's writes return silently once it has gone, and nothing else would answer it.
        if (_process.Exited.IsCompleted) FailPending("The agent's process ended.");

        var message = Envelope();
        message["id"] = id;
        message["method"] = method;
        if (parameters is not null) message["params"] = JsonSerializer.SerializeToNode(parameters, AgentSessionJson.Options);

        await _process.WriteLineAsync(message.ToJsonString(), ct);
        try
        {
            var wait = completion.Task.WaitAsync(ct);
            return timeout is { } limit ? await wait.WaitAsync(limit, ct) : await wait;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken ct)
    {
        var message = Envelope();
        message["method"] = method;
        if (parameters is not null) message["params"] = JsonSerializer.SerializeToNode(parameters, AgentSessionJson.Options);
        return _process.WriteLineAsync(message.ToJsonString(), ct);
    }

    public ValueTask DisposeAsync()
    {
        FailPending("The session was closed.");
        return _process.DisposeAsync();
    }

    private JsonObject Envelope() => _writesVersion ? new JsonObject { ["jsonrpc"] = "2.0" } : new JsonObject();

    private void HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            OnStrayLine?.Invoke(line);
            return;
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
            {
                OnStrayLine?.Invoke(line);
                return;
            }

            var hasMethod = root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            var parameters = root.TryGetProperty("params", out var p) ? p : default;

            if (hasMethod && hasId)
                _ = AnswerAsync(id.Clone(), method.GetString()!, parameters);
            else if (hasMethod)
                NotifyHandler(method.GetString()!, parameters);
            else if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number)
                     && _pending.TryGetValue(number, out var completion))
                Complete(completion, root);
        }
    }

    private static void Complete(TaskCompletionSource<JsonElement> completion, JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : InternalError;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            completion.TrySetException(new JsonRpcException(code, message,
                error.TryGetProperty("data", out var data) ? data : null));
            return;
        }

        completion.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
    }

    private void NotifyHandler(string method, JsonElement parameters)
    {
        if (OnNotification is not { } handler) return;
        try
        {
            handler(method, parameters);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentSessions] Handling notification {method} failed: {ex}");
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        var response = Envelope();
        response["id"] = JsonNode.Parse(id.GetRawText());
        try
        {
            if (OnRequest is not { } handler) throw new JsonRpcException(MethodNotFound, $"Method not found: {method}");
            var result = await handler(method, parameters);
            response["result"] = result is null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(result, AgentSessionJson.Options);
        }
        catch (JsonRpcException ex)
        {
            response["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message };
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentSessions] Answering {method} failed: {ex}");
            response["error"] = new JsonObject { ["code"] = InternalError, ["message"] = ex.Message };
        }

        await _process.WriteLineAsync(response.ToJsonString(), CancellationToken.None);
    }

    private void FailPending(string reason)
    {
        foreach (var (id, completion) in _pending)
        {
            completion.TrySetException(new JsonRpcException(InternalError, reason));
            _pending.TryRemove(id, out _);
        }
    }
}

/// <summary>An error answer, in either direction.</summary>
public sealed class JsonRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;
}
