using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Providers.Codex;

namespace Lidget.Tests.Providers;

/// <summary>A <c>codex app-server</c> transport that answers each request with a function.</summary>
internal sealed class FakeCodexTransport(Func<string, JsonElement, JsonNode?> respond) : ICodexTransport
{
    private static readonly Lazy<JsonArray> Transcript = new(() =>
        JsonNode.Parse(Fixtures.Read("codex/app-server-transcript-0.163.0.json"))!.AsArray());

    public List<JsonNode> Sent { get; } = [];

    public bool Disposed { get; private set; }

    public event Action<string>? LineReceived;

    public event Action? Closed;

    public static FakeCodexTransport FromTranscript() => new((method, _) => TranscriptResult(method));

    /// <summary>The recorded server result for the client request with <paramref name="method"/>.</summary>
    public static JsonNode? TranscriptResult(string method)
    {
        var request = Transcript.Value.First(e => e!["dir"]!.GetValue<string>() == "client" && e["msg"]!["method"]?.GetValue<string>() == method)!;
        var id = request["msg"]!["id"]!.GetValue<int>();
        var response = Transcript.Value.First(e => e!["dir"]!.GetValue<string>() == "server" && e["msg"]!["id"]?.GetValue<int>() == id)!;
        return response["msg"]!["result"]!.DeepClone();
    }

    public Task SendLineAsync(string line, CancellationToken ct)
    {
        var message = JsonNode.Parse(line)!;
        Sent.Add(message);
        if (message["id"] is { } id && message["method"] is { } method)
        {
            JsonObject reply;
            try
            {
                using var parameters = JsonDocument.Parse(message["params"]?.ToJsonString() ?? "null");
                reply = new JsonObject { ["id"] = id.DeepClone(), ["result"] = respond(method.GetValue<string>(), parameters.RootElement.Clone()) };
            }
            catch (CodexRpcException ex)
            {
                reply = new JsonObject { ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message } };
            }

            Receive(reply.ToJsonString());
        }

        return Task.CompletedTask;
    }

    public void Receive(string line) => LineReceived?.Invoke(line);

    public void Notify(string method, string parameters) =>
        Receive(new JsonObject { ["method"] = method, ["params"] = JsonNode.Parse(parameters) }.ToJsonString());

    public void Close() => Closed?.Invoke();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
