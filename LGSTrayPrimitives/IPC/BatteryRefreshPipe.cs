using System.IO.Pipes;

namespace LGSTrayPrimitives.IPC;

public static class BatteryRefreshPipe
{
    private const string PipeName = "LGSTrayBattery.Refresh";

    public static async Task RequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
    }

    public static async Task ListenAsync(Action refresh, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await pipe.ReadAsync(command, timeout.Token) == 1 && command[0] == 1) { refresh(); }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException ex) { DiagnosticLog.WriteException("Battery refresh pipe", ex); }
        }
    }
}
