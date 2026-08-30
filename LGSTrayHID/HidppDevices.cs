using LGSTrayHID.HidApi;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID
{
    public class HidppDevices : IDisposable
    {
        public const byte SW_ID = 0x0A;
        private byte PING_PAYLOAD = 0x55;

        private bool _isReading = true;
        private const int READ_TIMEOUT = 100;

        private readonly Dictionary<ushort, HidppDevice> _deviceCollection = [];
        private readonly object _deviceCollectionLock = new();
        public IReadOnlyDictionary<ushort, HidppDevice> DeviceCollection => _deviceCollection;

        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        private HidDevicePtr _devShort = IntPtr.Zero;
        public HidDevicePtr DevShort
        {
            get => _devShort;
        }

        private HidDevicePtr _devLong = IntPtr.Zero;
        public HidDevicePtr DevLong
        {
            get => _devLong;
        }

        private int _disposeCount = 0;
        public bool Disposed => _disposeCount > 0;

        public HidppDevices() { }

        private static void Log(string message)
        {
            LGSTrayPrimitives.DiagnosticLog.WriteLine(message);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                _isReading = false;

                _devShort = IntPtr.Zero;
                _devLong = IntPtr.Zero;
            }
        }

        ~HidppDevices()
        {
            Dispose(disposing: false);
        }

        public async Task SetDevShort(nint devShort)
        { 
            if (_devShort != IntPtr.Zero)
            {
                Log("Ignoring duplicate HID++ short endpoint");
                HidClose(devShort);
                return;
            }
            _devShort = devShort;
            await SetUp();
        }

        public async Task SetDevLong(nint devLong)
        {
            if (_devLong != IntPtr.Zero)
            {
                Log("Ignoring duplicate HID++ long endpoint");
                HidClose(devLong);
                return;
            }
            _devLong = devLong;
            await SetUp();
        }

        private async Task ReadThread(HidDevicePtr dev, int bufferSize, string endpointName)
        {
            byte[] buffer = new byte[bufferSize];
            while(_isReading)
            {
                var ret = dev.Read(buffer, bufferSize, READ_TIMEOUT);
                if (!_isReading) { break; }

                if (ret < 0)
                {
                    HidppManagerContext.Instance.RequestRestart($"HID++ {endpointName} endpoint read failed");
                    break;
                }
                else if (ret == 0)
                {
                    continue;
                }

                await ProcessMessgage(buffer);
            }

            HidClose(dev);
        }

        private async Task ProcessMessgage(byte[] buffer)
        {
            if ((buffer[2] == 0x41) && ((buffer[4] & 0x40) == 0))
            {
                byte deviceIdx = buffer[1];
                Log($"HID++ device arrival announce: index={deviceIdx}");
                HidppDevice? device = null;
                lock (_deviceCollectionLock)
                {
                    if (!_deviceCollection.ContainsKey(deviceIdx))
                    {
                        device = new(this, deviceIdx);
                        _deviceCollection[deviceIdx] = device;
                    }
                }

                if (device != null)
                {
                    new Thread(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000);
                            await device.InitAsync();
                        }
                        catch (Exception ex)
                        {
                            LGSTrayPrimitives.DiagnosticLog.WriteException($"Device index {deviceIdx} initialization failed", ex);
                        }
                    }).Start();
                }
            }
            else
            {
                await _channel.Writer.WriteAsync(buffer);
            }
        }

        public async Task<byte[]> WriteRead10(HidDevicePtr hidDevicePtr, byte[] buffer, int timeout = 100)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            bool locked = await _semaphore.WaitAsync(100);
            if (!locked)
            {
                return [];
            }

            try
            {
                var writeResult = await hidDevicePtr.WriteAsync((byte[])buffer);
                if (writeResult < 0)
                {
                    HidppManagerContext.Instance.RequestRestart("HID++ short endpoint write failed");
                    return [];
                }

                CancellationTokenSource cts = new();
                cts.CancelAfter(timeout);

                byte[] ret;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        ret = await _channel.Reader.ReadAsync(cts.Token);

                        if ((ret[2] == 0x8F) || (ret[2] == buffer[2]))
                        {
                            return ret;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }

                return [];
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<Hidpp20> WriteRead20(HidDevicePtr hidDevicePtr, Hidpp20 buffer, int timeout = 100, bool ignoreHID10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            bool locked = await _semaphore.WaitAsync(100);
            if (!locked)
            {
                return (Hidpp20)Array.Empty<byte>();
            }

            try
            {
                var writeResult = await hidDevicePtr.WriteAsync((byte[]) buffer);
                if (writeResult < 0)
                {
                    HidppManagerContext.Instance.RequestRestart("HID++ short endpoint write failed");
                    return (Hidpp20)Array.Empty<byte>();
                }

                CancellationTokenSource cts = new();
                cts.CancelAfter(timeout);

                Hidpp20 ret;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        ret = await _channel.Reader.ReadAsync(cts.Token);

                        if (!ignoreHID10 && (ret.GetFeatureIndex() == 0x8F))
                        {
                            // HID++ 1.0 response or timeout
                            break;
                        }

                        if ((ret.GetFeatureIndex() == buffer.GetFeatureIndex()) && (ret.GetSoftwareId() == SW_ID))
                        {
                            return ret;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }

                return (Hidpp20) Array.Empty<byte>();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> Ping20(byte deviceId, int timeout = 100, bool ignoreHIDPP10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            byte pingPayload = ++PING_PAYLOAD;
            Hidpp20 buffer = new byte[7] { 0x10, deviceId, 0x00, 0x10 | SW_ID, 0x00, 0x00, pingPayload };
            Hidpp20 ret = await WriteRead20(_devShort, buffer, timeout, ignoreHIDPP10);
            if (ret.Length == 0)
            {
                return false;
            }

            return (ret.GetFeatureIndex() == 0x00) && (ret.GetSoftwareId() == SW_ID) && (ret.GetParam(2) == pingPayload);

            //bool locked = await _semaphore.WaitAsync(100);
            //if (!locked)
            //{
            //    return false;
            //}

            //try
            //{
            //    byte pingPayload = ++PING_PAYLOAD;
            //    Hidpp20 buffer = new byte[7] { 0x10, deviceId, 0x00, 0x10 | SW_ID, 0x00, 0x00, pingPayload };
            //    await _devShort.WriteAsync((byte[])buffer);

            //    CancellationTokenSource cts = new();
            //    cts.CancelAfter(timeout);

            //    Hidpp20 ret;
            //    while (!cts.IsCancellationRequested)
            //    {
            //        try
            //        {
            //            ret = await _channel.Reader.ReadAsync(cts.Token);

            //            if (!ignoreHIDPP10 && (ret.GetFeatureIndex() == 0x8F))
            //            {
            //                // HID++ 1.0 response or timeout
            //                break;
            //            }

            //            if ((ret.GetFeatureIndex() == 0x00) && (ret.GetSoftwareId() == SW_ID) && (ret.GetParam(2) == pingPayload))
            //            {
            //                return true;
            //            }
            //        }
            //        catch (OperationCanceledException) { break; }
            //    }

            //    return false;
            //}
            //finally
            //{
            //    _semaphore.Release();
            //}
        }

        private async Task SetUp()
        {
            if ((_devShort == IntPtr.Zero) || (_devLong == IntPtr.Zero))
            {
                Log($"Waiting for HID++ endpoints: short={_devShort != IntPtr.Zero}, long={_devLong != IntPtr.Zero}");
                return;
            }
            
#if DEBUG
            Console.WriteLine("Device ready");
#endif
            Log("HID++ endpoints ready");

            Thread t1 = new(async () => { await ReadThread(_devShort, 7, "short"); })
            {
                Priority = ThreadPriority.BelowNormal
            };
            t1.Start();

            Thread t2 = new(async () => { await ReadThread(_devLong, 20, "long"); })
            {
                Priority = ThreadPriority.BelowNormal
            };
            t2.Start();

            byte[] ret;

            // Read number of devices on reciever
            ret = await WriteRead10(_devShort, [0x10, 0xFF, 0x81, 0x02, 0x00, 0x00, 0x00], 1000);
            byte numDeviceFound = 0;
            if ((ret.Length > 5) && (ret[2] == 0x81) && (ret[3] == 0x02))
            {
                numDeviceFound = ret[5];
            }
            else
            {
                Log("Receiver device-count query returned no usable response");
            }

            Log($"Receiver reports {numDeviceFound} device(s)");

            if (numDeviceFound > 0)
            {
                // Force arrival announce
                ret = await WriteRead10(_devShort, [0x10, 0xFF, 0x80, 0x02, 0x02, 0x00, 0x00], 1000);
            }

            await Task.Delay(500);

            if ((numDeviceFound > 0) || (_deviceCollection.Count == 0))
            {
                var fallbackDevices = new List<HidppDevice>();

                // Some receivers do not send arrival announces reliably; ping known slots.
                for (byte i = 1; i <= 6; i++)
                {
                    var ping = await Ping20(i, 100, false);
                    Log($"HID++ ping device index {i}: {ping}");
                    if (ping)
                    {
                        var deviceIdx = i;
                        lock (_deviceCollectionLock)
                        {
                            if (!_deviceCollection.ContainsKey(deviceIdx))
                            {
                                var device = new HidppDevice(this, deviceIdx);
                                _deviceCollection[deviceIdx] = device;
                                fallbackDevices.Add(device);
                            }
                        }
                    }
                }

                foreach (var device in fallbackDevices)
                {
                    await device.InitAsync();
                }
            }
        }
    }
}
