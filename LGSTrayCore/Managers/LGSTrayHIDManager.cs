using LGSTrayPrimitives.MessageStructs;
using LGSTrayPrimitives;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace LGSTrayCore.Managers
{
    public class LGSTrayHIDManager : IDeviceManager, IHostedService, IDisposable
    {
        #region IDisposable
        private Func<Task>? _diposeSubs;
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _ = _diposeSubs?.Invoke();
                    _diposeSubs = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~LGSTrayHIDDaemon()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion

        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenSource? _daemonCts;
        private int _deviceMessageCount;
        private long _lastDeviceMessageUnixMilliseconds;
        private readonly TimeSpan _messageStaleTimeout;

        private readonly IDistributedSubscriber<IPCMessageType, IPCMessage> _subscriber;
        private readonly IPublisher<IPCMessage> _deviceEventBus;

        public LGSTrayHIDManager(
            IDistributedSubscriber<IPCMessageType, IPCMessage> subscriber,
            IPublisher<IPCMessage> deviceEventBus,
            IOptions<AppSettings> appSettings
        )
        {
            _subscriber = subscriber;
            _deviceEventBus = deviceEventBus;
            var nativeSettings = appSettings.Value.Native;
            _messageStaleTimeout = TimeSpan.FromSeconds(
                nativeSettings.PollPeriod + Math.Max(90, nativeSettings.RetryTime * 3)
            );
        }

        private async Task MonitorDaemonHealth(Process proc, int messageCountAtStart, CancellationToken cancellationToken)
        {
            var startedAt = DateTimeOffset.UtcNow;

            while (!cancellationToken.IsCancellationRequested && !proc.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

                var messageCount = Volatile.Read(ref _deviceMessageCount);
                var now = DateTimeOffset.UtcNow;
                if (messageCount == messageCountAtStart)
                {
                    if (now - startedAt >= TimeSpan.FromSeconds(30))
                    {
                        DiagnosticLog.WriteLine($"HID watchdog restarting pid={proc.Id}: no device IPC received within 30 seconds");
                        proc.Kill();
                        return;
                    }

                    continue;
                }

                var lastMessageAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    Interlocked.Read(ref _lastDeviceMessageUnixMilliseconds)
                );
                if (now - lastMessageAt >= _messageStaleTimeout)
                {
                    DiagnosticLog.WriteLine(
                        $"HID watchdog restarting pid={proc.Id}: no device IPC for {(now - lastMessageAt).TotalSeconds:f0} seconds " +
                        $"(limit={_messageStaleTimeout.TotalSeconds:f0})"
                    );
                    proc.Kill();
                    return;
                }
            }
        }

        private void RecordDeviceMessage(string description)
        {
            Interlocked.Increment(ref _deviceMessageCount);
            Interlocked.Exchange(
                ref _lastDeviceMessageUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );
            DiagnosticLog.WriteLine($"Received native HID {description}");
        }

        private async Task<int> DaemonLoop()
        {
            _daemonCts = new();

            using Process proc = new();
            proc.StartInfo = new()
            {
                RedirectStandardError = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                FileName = Path.Combine(AppContext.BaseDirectory, "LGSTrayHID.exe"),
                Arguments = Environment.ProcessId.ToString(),
                UseShellExecute = true,
                CreateNoWindow = true
            };
            proc.Start();
            DiagnosticLog.WriteLine($"Started native HID service pid={proc.Id}");

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _daemonCts.Token);
                var messageCountAtStart = Volatile.Read(ref _deviceMessageCount);
                var processExitTask = proc.WaitForExitAsync(cts.Token);
                var watchdogTask = MonitorDaemonHealth(proc, messageCountAtStart, cts.Token);
                var completedTask = await Task.WhenAny(processExitTask, watchdogTask);

                if (completedTask == watchdogTask)
                {
                    await watchdogTask;
                    await processExitTask;
                }
                else
                {
                    await processExitTask;
                    await cts.CancelAsync();
                    try
                    {
                        await watchdogTask;
                    }
                    catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException)
            {
                if (!proc.HasExited)
                {
                    DiagnosticLog.WriteLine($"Stopping native HID service pid={proc.Id} after cancellation");
                    proc.Kill();
                    await proc.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException($"Native HID service pid={proc.Id} monitor failed", ex);
                if (!proc.HasExited)
                {
                    proc.Kill();
                    await proc.WaitForExitAsync(CancellationToken.None);
                }
            }
            finally
            {
                _daemonCts.Dispose();
                _daemonCts = null;
            }

            DiagnosticLog.WriteLine($"Native HID service pid={proc.Id} exited with code {proc.ExitCode}");
            await Task.Delay(1000);
            return proc.ExitCode;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var sub1 = await _subscriber.SubscribeAsync(
                IPCMessageType.INIT,
                x =>
                {
                    var initMessage = (InitMessage)x;
                    RecordDeviceMessage($"INIT: {initMessage.deviceName} ({initMessage.deviceId})");
                    //_logiDeviceCollection.OnInitMessage(initMessage);
                    _deviceEventBus.Publish(initMessage);
                },
                cancellationToken
            );

            var sub2 = await _subscriber.SubscribeAsync(
                IPCMessageType.UPDATE,
                x =>
                {
                    var updateMessage = (UpdateMessage)x;
                    RecordDeviceMessage($"UPDATE: {updateMessage.deviceId} battery={updateMessage.batteryPercentage:f0}%");
                    //_logiDeviceCollection.OnUpdateMessage(updateMessage);
                    _deviceEventBus.Publish(updateMessage);
                },
                cancellationToken
            );

            _diposeSubs = async () =>
            {
                await sub1.DisposeAsync();
                await sub2.DisposeAsync();
            };

            _ = Task.Run(async () =>
            {
                int fastFailCount = 0;
                DiagnosticLog.WriteLine($"Native HID watchdog started; stale limit={_messageStaleTimeout.TotalSeconds:f0}s");

                while (!_cts.Token.IsCancellationRequested)
                {
                    DateTime then = DateTime.Now;
                    int ret = await DaemonLoop();

                    // Ignore user-requested rediscovery. Stop only if the daemon
                    // repeatedly fails before it has remained healthy for 20 seconds.
                    if ((ret != -1) && (DateTime.Now - then).TotalSeconds < 20)
                    {
                        fastFailCount++;
                    }
                    else
                    {
                        fastFailCount = 0;
                    }

                    if (fastFailCount > 3)
                    {
                        DiagnosticLog.WriteLine("Native HID service stopped after four consecutive fast failures");
                        break;
                    }
                }
            }, CancellationToken.None);

            return;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            DiagnosticLog.WriteLine("Native HID manager stopping");
            _cts.Cancel();

            return Task.CompletedTask;
        }

        public void RediscoverDevices()
        {
            DiagnosticLog.WriteLine("Manual native HID rediscovery requested");
            _daemonCts?.Cancel();
        }
    }
}
