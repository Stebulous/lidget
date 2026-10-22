using System.Net.Http;
using Lidget.Core;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Lidget.App.Services;

/// <summary>
/// Checks for updates at startup and every 24 hours, and downloads them in the background.
/// Velopack applies a downloaded update at the next start (plan task T4.1).
/// An app that Velopack did not install, such as a Debug build, does not check.
/// </summary>
public sealed class UpdateService(ILogger logger, TimeProvider time) : IAsyncDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public void Start()
    {
        var manager = CreateManager();
        if (!manager.IsInstalled)
        {
            logger.LogInformation("Update checks are off, because Velopack did not install this copy of Lidget");
            return;
        }

        _loop = Task.Run(() => RunAsync(manager, _stopping.Token));
    }

    private static UpdateManager CreateManager()
    {
        var feed = Environment.GetEnvironmentVariable(AppInfo.UpdateFeedVariable);
        return string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false))
            : new UpdateManager(feed);
    }

    private async Task RunAsync(UpdateManager manager, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(manager).ConfigureAwait(false);
                await Task.Delay(CheckInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task CheckAsync(UpdateManager manager)
    {
        try
        {
            if (manager.UpdatePendingRestart is { } pending)
            {
                logger.LogDebug("Update {Version} is ready for the next start", pending.Version);
                return;
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                logger.LogDebug("Lidget {Version} is up to date", manager.CurrentVersion);
                return;
            }

            logger.LogInformation("Downloading update {Version}", update.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            logger.LogInformation("Update {Version} is ready for the next start", update.TargetFullRelease.Version);
        }
        catch (HttpRequestException ex)
        {
            // No network, a rate limit, or no release yet. The next check tries again.
            logger.LogWarning("Could not check for updates: {Message}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not check for updates");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            // A check or a download cannot be cancelled. Do not let it delay the exit.
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        _stopping.Dispose();
    }
}
