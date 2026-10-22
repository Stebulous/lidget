namespace Lidget.Providers.Codex;

/// <summary>A line-based connection to a <c>codex app-server</c> process.</summary>
public interface ICodexTransport : IAsyncDisposable
{
    /// <summary>Raised for each line that the server writes, on a background thread.</summary>
    event Action<string>? LineReceived;

    /// <summary>Raised one time when the connection closes.</summary>
    event Action? Closed;

    Task SendLineAsync(string line, CancellationToken ct);
}
