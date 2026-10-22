using Lidget.Core.Models;

namespace Lidget.Core.Providers;

public interface IUsageProvider : IAsyncDisposable
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// Reads current usage. Implementations report failures through <see cref="ProviderSnapshot.Status"/>
    /// and throw only for cancellation.
    /// </summary>
    Task<ProviderSnapshot> RefreshAsync(CancellationToken ct);

    /// <summary>Raised for readings that arrive between refreshes, for example push notifications.</summary>
    event EventHandler<ProviderSnapshot>? SnapshotPushed;
}
