using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Lidget.Providers.Codex;

/// <summary>Runs <c>codex app-server</c> with redirected stdio.</summary>
public sealed class ProcessCodexTransport : ICodexTransport
{
    private readonly Process _process;
    private readonly IDisposable? _job;
    private readonly ILogger _logger;
    private int _closed;

    private ProcessCodexTransport(Process process, IDisposable? job, ILogger logger)
    {
        _process = process;
        _job = job;
        _logger = logger;
    }

    public event Action<string>? LineReceived;

    public event Action? Closed;

    public static ProcessCodexTransport Start(string executable, ILogger logger)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo(executable)
        {
            ArgumentList = { "app-server" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // The npm shim starts Node.js, which starts codex.exe. The job stops the whole tree with Lidget.
        var job = OperatingSystem.IsWindows() ? KillOnCloseJob.TryAssign(process) : null;

        var transport = new ProcessCodexTransport(process, job, logger);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                transport.OnClosed();
            }
            else if (e.Data.Length > 0)
            {
                transport.LineReceived?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                logger.LogDebug("codex app-server stderr: {Line}", e.Data.Length > 300 ? e.Data[..300] : e.Data);
            }
        };
        process.Exited += (_, _) => transport.OnClosed();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return transport;
    }

    public async Task SendLineAsync(string line, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private void OnClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _logger.LogInformation("codex app-server stopped");
            Closed?.Invoke();
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _job?.Dispose();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
