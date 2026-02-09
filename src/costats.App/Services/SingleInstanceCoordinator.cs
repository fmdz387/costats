using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Serilog;

namespace costats.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;

    public SingleInstanceCoordinator(string appId)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        PipeName = $"{appId}.pipe.{sid}";
        var mutexName = $"Global\\{appId}.mutex.{sid}";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public string PipeName { get; }

    public Task StartListenerAsync(Func<ActivationMessage, Task> onActivation, CancellationToken cancellationToken)
    {
        if (!IsPrimary)
        {
            return Task.CompletedTask;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        _listenerTask = Task.Run(async () =>
        {
            try
            {
                while (!linkedCts.IsCancellationRequested)
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    try
                    {
                        await server.WaitForConnectionAsync(linkedCts.Token).ConfigureAwait(false);
                        using var reader = new StreamReader(server);
                        var line = await reader.ReadLineAsync().WaitAsync(linkedCts.Token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(line) &&
                            Enum.TryParse(line, ignoreCase: true, out ActivationMessage message))
                        {
                            await onActivation(message).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Named pipe listener error");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Named pipe listener crashed");
                throw;
            }
        }, linkedCts.Token);

        return Task.CompletedTask;
    }

    public static async Task SignalPrimaryAsync(string pipeName, ActivationMessage message, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(message.ToString()).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown failures.
        }

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore release failures.
            }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
