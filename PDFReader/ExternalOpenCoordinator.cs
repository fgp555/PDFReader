using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace PDFReader;

internal sealed class ExternalOpenCoordinator : IDisposable
{
    private const string AcceptedResponse = "Accepted";
    private const string BusyResponse = "Busy";
    private readonly MainWindow _window;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenerTask;

    public ExternalOpenCoordinator(MainWindow window) => _window = window;

    public void Start() => _listenerTask ??= ListenAsync(_shutdown.Token);

    public static async Task<bool> TryRedirectToEmptyInstanceAsync(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            return false;

        var fullPath = Path.GetFullPath(path);
        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName(current.ProcessName)
            .Where(process => process.Id != Environment.ProcessId)
            .OrderBy(process => process.StartTime)
            .ToArray();

        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                    await using var pipe = new NamedPipeClientStream(".", PipeName(process.Id),
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(timeout.Token);
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(fullPath);
                    var response = await reader.ReadLineAsync(timeout.Token);
                    if (string.Equals(response, AcceptedResponse, StringComparison.Ordinal)) return true;
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException)
                {
                    // The process may be closing, starting, or from a different build.
                }
            }
        }
        finally
        {
            foreach (var process in candidates) process.Dispose();
        }

        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName(Environment.ProcessId),
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var path = await reader.ReadLineAsync(cancellationToken);
                var accepted = false;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        accepted = _window.Dispatcher.Invoke(() => _window.TryOpenExternalPdf(path));
                    }
                    catch (TaskCanceledException)
                    {
                        accepted = false;
                    }
                }

                await writer.WriteLineAsync(accepted ? AcceptedResponse : BusyResponse);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // Recreate the server after a client disconnects unexpectedly.
            }
        }
    }

    private static string PipeName(int processId) => $"PDFReader.ExternalOpen.{processId}";

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
