using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Providers.Codex;

/// <summary>
/// Reads Codex limits from a long-running <c>codex app-server</c> process. Codex owns its login and
/// token refresh: Lidget never reads or writes <c>~/.codex/auth.json</c>.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    public static readonly IReadOnlyList<TimeSpan> RestartBackoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly Func<string?> _locate;
    private readonly Func<string, ICodexTransport> _startTransport;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _rateLimitsLock = new();
    private CodexAppServerClient? _client;
    private JsonObject? _lastRateLimits;
    private int _failures;
    private DateTimeOffset? _nextStartAllowed;

    public CodexProvider(Func<string?> locate, Func<string, ICodexTransport> startTransport, TimeProvider time, ILogger<CodexProvider>? logger = null)
    {
        _locate = locate;
        _startTransport = startTransport;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public static CodexProvider CreateDefault(TimeProvider time, ILoggerFactory loggerFactory) =>
        new(
            () => CodexLocator.Find(),
            exe => ProcessCodexTransport.Start(exe, loggerFactory.CreateLogger<ProcessCodexTransport>()),
            time,
            loggerFactory.CreateLogger<CodexProvider>());

    public string Id => ProviderIds.Codex;

    public string DisplayName => "Codex";

    public event EventHandler<ProviderSnapshot>? SnapshotPushed;

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (client, failure) = await EnsureClientAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                return failure!;
            }

            try
            {
                var account = await client.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, ct).ConfigureAwait(false);
                if (LoginProblem(account) is { } loginProblem)
                {
                    return loginProblem;
                }

                var result = await client.RequestAsync("account/rateLimits/read", new JsonObject { ["excludeResetCreditDetails"] = true }, ct).ConfigureAwait(false);
                if (CodexRateLimitMapper.MainSnapshot(result) is not { } rateLimits)
                {
                    _logger.LogWarning("codex app-server returned rate limits in an unknown format");
                    return Failed(ProviderStatus.Error, "Codex returned rate limits that Lidget cannot read.");
                }

                var snapshot = JsonNode.Parse(rateLimits.GetRawText())!.AsObject();
                lock (_rateLimitsLock)
                {
                    _lastRateLimits = snapshot;
                }

                _failures = 0;
                return BuildSnapshot(snapshot);
            }
            catch (CodexRpcException ex)
            {
                _logger.LogWarning("codex app-server error {Code}: {Message}", ex.Code, Truncate(ex.Message));
                return Failed(ProviderStatus.Error, $"Codex reported an error: {Truncate(ex.Message)}");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                _logger.LogWarning("codex app-server request failed: {Error}", ex.Message);
                await StopClientAsync(countAsFailure: true).ConfigureAwait(false);
                return Failed(ProviderStatus.Error, "The Codex app-server stopped responding. Lidget will restart it.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProviderSnapshot? LoginProblem(JsonElement account)
    {
        if (account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("account", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return Failed(ProviderStatus.NeedsLogin, "Run `codex login` to show Codex limits.");
        }

        var type = details.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return type is "chatgpt" or null
            ? null
            : Failed(ProviderStatus.NeedsLogin, "Codex uses an API key. Run `codex login` with a ChatGPT account to show plan limits.");
    }

    private async Task<(CodexAppServerClient? Client, ProviderSnapshot? Failure)> EnsureClientAsync(CancellationToken ct)
    {
        if (_client is { IsClosed: false } running)
        {
            return (running, null);
        }

        if (_client is not null)
        {
            await StopClientAsync(countAsFailure: true).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        if (_nextStartAllowed is { } next && now < next)
        {
            return (null, Failed(ProviderStatus.Error, $"The Codex app-server stopped. Lidget restarts it at {next.ToLocalTime():t}."));
        }

        var executable = _locate();
        if (executable is null)
        {
            return (null, Failed(ProviderStatus.NotInstalled, "Install the Codex CLI to show Codex limits."));
        }

        CodexAppServerClient? client = null;
        try
        {
            client = new CodexAppServerClient(_startTransport(executable), _logger);
            client.NotificationReceived += OnNotification;
            var version = typeof(CodexProvider).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            await client.InitializeAsync(version, ct).ConfigureAwait(false);
            _client = client;
            _logger.LogInformation("Started codex app-server from {Executable}", executable);
            return (client, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Could not start codex app-server: {Error}", ex.Message);
            if (client is not null)
            {
                client.NotificationReceived -= OnNotification;
                await client.DisposeAsync().ConfigureAwait(false);
            }

            RegisterFailure();
            return (null, Failed(ProviderStatus.Error, "Could not start the Codex app-server. Lidget will retry."));
        }
    }

    private async Task StopClientAsync(bool countAsFailure)
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.NotificationReceived -= OnNotification;
            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (countAsFailure)
        {
            RegisterFailure();
        }
    }

    private void RegisterFailure()
    {
        _failures++;
        var delay = RestartBackoff[Math.Min(_failures, RestartBackoff.Count) - 1];
        _nextStartAllowed = _time.GetUtcNow() + delay;
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method != "account/rateLimits/updated" || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("rateLimits", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // A model limit must not replace the plan allowance.
        if (!CodexRateLimitMapper.IsMainSnapshot(update))
        {
            return;
        }

        JsonObject merged;
        lock (_rateLimitsLock)
        {
            merged = _lastRateLimits is null
                ? JsonNode.Parse(update.GetRawText())!.AsObject()
                : CodexRateLimitMapper.Merge(_lastRateLimits, update);
            _lastRateLimits = merged;
        }

        var snapshot = BuildSnapshot(merged);
        if (snapshot.Limits.Count > 0)
        {
            SnapshotPushed?.Invoke(this, snapshot);
        }
    }

    private ProviderSnapshot BuildSnapshot(JsonObject rateLimits)
    {
        using var document = JsonDocument.Parse(rateLimits.ToJsonString());
        var now = _time.GetUtcNow();
        var limits = CodexRateLimitMapper.Map(document.RootElement, now);
        if (limits.Count == 0)
        {
            return Failed(ProviderStatus.Error, "Codex returned no usage limits.");
        }

        return new ProviderSnapshot(Id, ProviderStatus.Ok, limits, null, now)
        {
            PlanName = CodexRateLimitMapper.FormatPlan(document.RootElement),
        };
    }

    private ProviderSnapshot Failed(ProviderStatus status, string message) =>
        ProviderSnapshot.Failed(Id, status, message, _time.GetUtcNow());

    private static string Truncate(string text) => text.Length > 200 ? text[..200] + "…" : text;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopClientAsync(countAsFailure: false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
