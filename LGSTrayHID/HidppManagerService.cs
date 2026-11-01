using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;

namespace LGSTrayHID
{
    public class HidppManagerService : IHostedService
    {
        private readonly IDistributedPublisher<IPCMessageType, IPCMessage> _publisher;
        private readonly CancellationTokenSource _heartbeatCts = new();
        private Task? _heartbeatTask;
        private Task? _refreshTask;

        public HidppManagerService(IDistributedPublisher<IPCMessageType, IPCMessage> publisher)
        {
            _publisher = publisher;

            HidppManagerContext.Instance.HidppDeviceEvent += async (type, message) =>
            {
                if (message is InitMessage initMessage)
                {
                    LGSTrayPrimitives.DiagnosticLog.WriteLine($"Publishing INIT for {initMessage.deviceName} ({initMessage.deviceId})");
                }

                await _publisher.PublishAsync(type, message);
            };
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _refreshTask = LGSTrayPrimitives.IPC.BatteryRefreshPipe.ListenAsync(
                () => {
                    LGSTrayPrimitives.DiagnosticLog.WriteLine("Manual battery refresh requested");
                    HidppManagerContext.Instance.RequestBatteryUpdates();
                }, _heartbeatCts.Token);
            LGSTrayPrimitives.DiagnosticLog.WriteLine("Native HID manager service started");
            HidppManagerContext.Instance.Start(cancellationToken);
            _heartbeatTask = RunHeartbeatAsync(_heartbeatCts.Token);
            return Task.CompletedTask;

        }

        private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
        {
            LGSTrayPrimitives.DiagnosticLog.WriteLine(
                $"Native HID heartbeat started; interval={HeartbeatMessage.IntervalSeconds}s"
            );

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _publisher.PublishAsync(
                        IPCMessageType.HEARTBEAT,
                        new HeartbeatMessage(
                            string.Empty,
                            DateTimeOffset.UtcNow,
                            Environment.ProcessId,
                            HidppManagerContext.Instance.DeviceCandidateDetected
                        ),
                        cancellationToken
                    );
                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatMessage.IntervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LGSTrayPrimitives.DiagnosticLog.WriteException("Native HID heartbeat publish failed", ex);
                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatMessage.IntervalSeconds), cancellationToken);
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _heartbeatCts.Cancel();
            if (_refreshTask != null)
            {
                try { await _refreshTask.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
            }
            if (_heartbeatTask != null)
            {
                try
                {
                    await _heartbeatTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) { }
            }
        }
    }
}
