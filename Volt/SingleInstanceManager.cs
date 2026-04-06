using System.IO;
using System.IO.Pipes;

namespace Volt;

/// <summary>
/// Ensures only one instance of Volt runs at a time.
/// Second instances forward their command-line file path to the first via a named pipe.
/// </summary>
internal sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "Volt-SingleInstance-E7F3A2B1";
    private const string PipeName = "Volt-IPC-E7F3A2B1";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public event Action<string>? FileRequested;

    /// <summary>
    /// Tries to become the first instance.
    /// Returns true if this is the first instance (caller should continue startup).
    /// Returns false if another instance is already running (file path was forwarded; caller should exit).
    /// </summary>
    public bool TryStart(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (createdNew)
        {
            StartPipeServer();
            return true;
        }

        // Another instance owns the mutex — forward args and exit
        var filePath = args.Length > 0 ? args[0] : "";
        SendToFirstInstance(filePath);
        return false;
    }

    private void StartPipeServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using (server)
                    using (var reader = new StreamReader(server))
                    {
                        var path = await reader.ReadLineAsync(token);
                        if (!string.IsNullOrWhiteSpace(path))
                            FileRequested?.Invoke(path);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Pipe error — keep listening
                }
            }
        }, token);
    }

    private static void SendToFirstInstance(string filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(filePath);
        }
        catch
        {
            // First instance may have just closed — nothing we can do
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
