using System.IO;
using System.IO.Pipes;

namespace PlanLite.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\PlanLite_SingleInstance_Mutex";
    private const string PipeName = "PlanLite_Activate_Pipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _isPrimary;

    public event Action? ActivateRequested;

    public bool TryAcquireOrNotifyExisting()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _isPrimary = true;
            StartPipeServer();
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        NotifyExistingInstance();
        return false;
    }

    private void StartPipeServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var msg = await reader.ReadLineAsync(token);
                    if (string.Equals(msg, "SHOW", StringComparison.Ordinal))
                    {
                        ActivateRequested?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                    {
                        await Task.Delay(200, token).ConfigureAwait(false);
                    }
                }
            }
        }, token);
    }

    private static void NotifyExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("SHOW");
        }
        catch
        {
            // Existing instance might be shutting down or busy; ignore cleanly.
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_isPrimary && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore if already abandoned.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
