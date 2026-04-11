// Volt/Terminal/ConPty/PtySession.cs
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using static Volt.NativeMethods;

namespace Volt;

public sealed class PtySession : IDisposable
{
    public event Action<ReadOnlyMemory<byte>>? Output;
    public event Action<int>? Exited;

    private readonly PtyHandles _handles;
    private readonly Dispatcher _uiDispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;
    private FileStream _input;
    private FileStream _output;
    private bool _disposed;

    public PtySession(string shellExe, string? args, string cwd, short rows, short cols)
    {
        _handles = ConPtyHost.Create(shellExe, args, cwd, rows, cols);
        _input = new FileStream(_handles.Input, FileAccess.Write);
        _output = new FileStream(_handles.Output, FileAccess.Read);
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _readTask = Task.Run(ReadLoop);
        try
        {
            _handles.Process.EnableRaisingEvents = true;
            _handles.Process.Exited += OnProcessExited;
            // Check HasExited after subscribing to handle the race where the process
            // exits between EnableRaisingEvents and the event subscription.
            if (_handles.Process.HasExited)
                OnProcessExited(null, EventArgs.Empty);
        }
        catch (InvalidOperationException)
        {
            // Process already exited before EnableRaisingEvents could be set.
            OnProcessExited(null, EventArgs.Empty);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) return;
        try { _input.Write(data); _input.Flush(); }
        catch { /* broken pipe — treat as session end */ }
    }

    public void Resize(short rows, short cols)
    {
        if (_disposed) return;
        var sz = new COORD { X = cols, Y = rows };
        ResizePseudoConsole(_handles.PseudoConsole, sz);
    }

    private async Task ReadLoop()
    {
        var pool = ArrayPool<byte>.Shared;
        var buf = pool.Rent(4096);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = await _output.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }
                if (n <= 0) break;

                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
#pragma warning disable CS4014 // fire-and-forget dispatch is intentional
                _uiDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    try { Output?.Invoke(copy); }
#if DEBUG
                    catch { throw; }
#else
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Terminal] Output handler threw: {ex}");
                    }
#endif
                }));
#pragma warning restore CS4014
            }
        }
        finally
        {
            pool.Return(buf);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int code = 0;
        try { code = _handles.Process.ExitCode; } catch { }
        _uiDispatcher.BeginInvoke(new Action(() => Exited?.Invoke(code)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _output?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
        try { _readTask.Wait(500); } catch { }
        _handles.Dispose();
    }
}
