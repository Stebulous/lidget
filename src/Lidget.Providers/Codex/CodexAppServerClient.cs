using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Providers.Codex;

public sealed class CodexRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// JSON-RPC client for <c>codex app-server</c>. Messages are one JSON object on each line, without
/// a <c>jsonrpc</c> field (T0.3 evidence in docs/plans/lidget-mvp.md).
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly ICodexTransport _transport;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private int _closed;

    public CodexAppServerClient(ICodexTransport transport, ILogger? logger = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger.Instance;
        _transport.LineReceived += OnLine;
        _transport.Closed += OnClosed;
    }

    /// <summary>Raised for each server notification, with its method and its <c>params</c>.</summary>
    public event Action<string, JsonElement>? NotificationReceived;

    public event Action? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    public async Task InitializeAsync(string clientVersion, CancellationToken ct)
    {
        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "lidget", ["title"] = "Lidget", ["version"] = clientVersion },
        }, ct).ConfigureAwait(false);
        await SendAsync(new JsonObject { ["method"] = "initialized" }, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        if (IsClosed)
        {
            throw new IOException("codex app-server is not running.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await SendAsync(message, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendAsync(JsonObject message, CancellationToken ct) => _transport.SendLineAsync(message.ToJsonString(), ct);

    private void OnLine(string line)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            _logger.LogDebug("Ignored a codex app-server line that is not JSON");
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasId = message.TryGetProperty("id", out var idElement);
        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;

        if (hasId && !hasMethod)
        {
            HandleResponse(idElement, message);
        }
        else if (hasId && hasMethod)
        {
            // Lidget starts no threads, so it does not expect server requests such as approvals.
            _ = SendAsync(new JsonObject
            {
                ["id"] = JsonNode.Parse(idElement.GetRawText()),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Lidget does not support this request." },
            }, CancellationToken.None);
        }
        else if (hasMethod)
        {
            var parameters = message.TryGetProperty("params", out var p) ? p : default;
            NotificationReceived?.Invoke(methodElement.GetString()!, parameters);
        }
    }

    private void HandleResponse(JsonElement idElement, JsonElement message)
    {
        if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var completion))
        {
            return;
        }

        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
            var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Unknown error";
            completion.TrySetException(new CodexRpcException(code, text));
        }
        else
        {
            completion.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
        }
    }

    private void OnClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new IOException("codex app-server stopped."));
        }

        Closed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _transport.LineReceived -= OnLine;
        await _transport.DisposeAsync().ConfigureAwait(false);
        OnClosed();
    }
}
