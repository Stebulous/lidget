using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Providers.Codex;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

public class CodexProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<FakeCodexTransport> _transports = [];
    private string? _executable = @"C:\fake\codex.cmd";
    private Func<FakeCodexTransport> _transportFactory = FakeCodexTransport.FromTranscript;

    private CodexProvider CreateProvider() => new(() => _executable, _ =>
    {
        var transport = _transportFactory();
        _transports.Add(transport);
        return transport;
    }, _time);

    [Fact]
    public async Task MapsRecordedTranscript()
    {
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Plus", snapshot.PlanName);
        Assert.Equal(2, snapshot.Limits.Count);
        var session = snapshot.Limits[0];
        Assert.Equal(CodexRateLimitMapper.SessionId, session.Id);
        Assert.Equal(LimitKind.Session, session.Kind);
        Assert.Equal(86, session.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792663869), session.ResetsAt);
        Assert.Equal("app-server", session.Source);
        Assert.Equal(LimitKind.Weekly, snapshot.Limits[1].Kind);
        Assert.Equal(47, snapshot.Limits[1].UsedPercent);
    }

    [Fact]
    public async Task HandshakeComesBeforeRequests()
    {
        await using var provider = CreateProvider();

        await provider.RefreshAsync(CancellationToken.None);

        var methods = _transports[0].Sent.Select(m => m["method"]!.GetValue<string>()).ToArray();
        Assert.Equal(["initialize", "initialized", "account/read", "account/rateLimits/read"], methods);
        Assert.Equal("lidget", _transports[0].Sent[0]["params"]!["clientInfo"]!["name"]!.GetValue<string>());
        Assert.Null(_transports[0].Sent[1]["id"]);
    }

    [Fact]
    public async Task ReusesRunningServer()
    {
        await using var provider = CreateProvider();

        await provider.RefreshAsync(CancellationToken.None);
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Single(_transports);
    }

    [Fact]
    public async Task SparseUpdateIsMergedAndPushed()
    {
        await using var provider = CreateProvider();
        var pushed = new List<ProviderSnapshot>();
        provider.SnapshotPushed += (_, s) => pushed.Add(s);
        await provider.RefreshAsync(CancellationToken.None);

        _transports[0].Notify("account/rateLimits/updated", """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":91,"windowDurationMins":300,"resetsAt":1792663869},"secondary":null,"planType":null}}
            """);

        var snapshot = Assert.Single(pushed);
        Assert.Equal(91, snapshot.Limits[0].UsedPercent);
        Assert.Equal(47, snapshot.Limits[1].UsedPercent);
        Assert.Equal("Plus", snapshot.PlanName);
    }

    [Fact]
    public async Task UnknownNotificationsAreIgnored()
    {
        await using var provider = CreateProvider();
        var pushed = 0;
        provider.SnapshotPushed += (_, _) => pushed++;
        await provider.RefreshAsync(CancellationToken.None);

        _transports[0].Notify("thread/started", """{"something":1}""");

        Assert.Equal(0, pushed);
    }

    [Fact]
    public async Task ServerRequestGetsMethodNotFoundReply()
    {
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        _transports[0].Receive("""{"id":"srv-1","method":"item/commandExecution/requestApproval","params":{}}""");

        var reply = _transports[0].Sent[^1];
        Assert.Equal("srv-1", reply["id"]!.GetValue<string>());
        Assert.Equal(-32601, reply["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MissingCliIsNotInstalled()
    {
        _executable = null;
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Empty(_transports);
    }

    [Fact]
    public async Task MissingAccountNeedsLogin()
    {
        _transportFactory = () => new FakeCodexTransport((method, _) => method switch
        {
            "initialize" => JsonNode.Parse("""{"userAgent":"x"}"""),
            "account/read" => JsonNode.Parse("""{"account":null,"requiresOpenaiAuth":true}"""),
            _ => throw new InvalidOperationException(method),
        });
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NeedsLogin, snapshot.Status);
        Assert.Contains("codex login", snapshot.StatusMessage);
    }

    [Fact]
    public async Task RpcErrorIsError()
    {
        _transportFactory = () => new FakeCodexTransport((method, _) => method switch
        {
            "account/rateLimits/read" => throw new CodexRpcException(-32000, "backend unavailable"),
            _ => FakeCodexTransport.TranscriptResult(method),
        });
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Contains("backend unavailable", snapshot.StatusMessage);
    }

    [Fact]
    public async Task RestartsWithBackoffAfterServerStops()
    {
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        _transports[0].Close();
        var stopped = await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Error, stopped.Status);
        Assert.Single(_transports);

        _time.Advance(CodexProvider.RestartBackoff[0]);
        var restarted = await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, restarted.Status);
        Assert.Equal(2, _transports.Count);
        Assert.True(_transports[0].Disposed);
    }

    [Fact]
    public async Task RepeatedStartFailuresIncreaseBackoff()
    {
        _transportFactory = () => throw new IOException("cannot start");
        await using var provider = CreateProvider();

        await provider.RefreshAsync(CancellationToken.None);
        _time.Advance(CodexProvider.RestartBackoff[0]);
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(CodexProvider.RestartBackoff[1] - TimeSpan.FromSeconds(1));
        var waiting = await provider.RefreshAsync(CancellationToken.None);
        Assert.Contains("restarts it", waiting.StatusMessage);
    }

    [Fact]
    public void MapsUnknownWindowDurationAsOther()
    {
        using var document = JsonDocument.Parse("""{"primary":{"usedPercent":5,"windowDurationMins":1440}}""");

        var limit = Assert.Single(CodexRateLimitMapper.Map(document.RootElement, Now));

        Assert.Equal(LimitKind.Other, limit.Kind);
        Assert.Equal("1-day window", limit.Label);
        Assert.Equal(TimeSpan.FromDays(1), limit.Duration);
    }

    [Theory]
    [InlineData("plus", 300.0, 10080.0)]
    [InlineData("free", null, 10080.0)]
    public void MissingWindowDurationFallsBackByPosition(string plan, double? primaryMinutes, double secondaryMinutes)
    {
        using var document = JsonDocument.Parse($$$"""{"planType":"{{{plan}}}","primary":{"usedPercent":5},"secondary":{"usedPercent":9}}""");

        var limits = CodexRateLimitMapper.Map(document.RootElement, Now);

        Assert.Equal(2, limits.Count);
        Assert.Equal(primaryMinutes, limits[0].Duration?.TotalMinutes);
        Assert.Equal(secondaryMinutes, limits[1].Duration?.TotalMinutes);
    }

    [Fact]
    public void LocatorFindsCmdShim()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("codex.cmd"), "@echo off");

        Assert.Equal(dir.File("codex.cmd"), CodexLocator.Find($"C:\\does-not-exist{Path.PathSeparator}{dir.Path}"));
        Assert.Null(CodexLocator.Find(@"C:\does-not-exist"));
    }

    [Fact]
    public void LocatorPrefersNativeBinaryOfNpmInstall()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("codex.cmd"), "@echo off");
        var binary = dir.File(@"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, string.Empty);

        Assert.Equal(binary, CodexLocator.FindNpmBinary(dir.Path, System.Runtime.InteropServices.Architecture.X64));
        Assert.Null(CodexLocator.FindNpmBinary(dir.Path, System.Runtime.InteropServices.Architecture.Arm64));
    }

    private sealed class FakeCodexTransport(Func<string, JsonElement, JsonNode?> respond) : ICodexTransport
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
}
