using static LGSTrayHID.HidApi.HidApi;
using static LGSTrayHID.HidApi.HidApiWinApi;
using static LGSTrayHID.HidApi.HidApiHotPlug;
using LGSTrayHID.HidApi;
using System.Collections.Concurrent;
using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayHID
{
    public sealed class HidppManagerContext
    {
        public static readonly HidppManagerContext _instance = new();
        public static HidppManagerContext Instance => _instance;

        private readonly Dictionary<string, Guid> _containerMap = [];
        private readonly Dictionary<Guid, HidppDevices> _deviceMap = [];
        private readonly BlockingCollection<HidDeviceInfo> _deviceQueue = [];
        private readonly object _deviceMapLock = new();
        private int _restartRequested;

        public delegate void HidppDeviceEventHandler(IPCMessageType messageType, IPCMessage message);

        public event HidppDeviceEventHandler? HidppDeviceEvent;

        private HidppManagerContext()
        {

        }

        private static void Log(string message)
        {
            LGSTrayPrimitives.DiagnosticLog.WriteLine(message);
        }

        static HidppManagerContext()
        {
            _ = HidInit();
        }

        public void SignalDeviceEvent(IPCMessageType messageType, IPCMessage message)
        {
            HidppDeviceEvent?.Invoke(messageType, message);
        }

        public void RequestRestart(string reason)
        {
            if (Interlocked.Exchange(ref _restartRequested, 1) != 0)
            {
                return;
            }

            Log($"Restart requested: {reason}");
            Environment.Exit(2);
        }

        private unsafe int EnqueueDevice(HidHotPlugCallbackHandle _, HidDeviceInfo* device, HidApiHotPlugEvent hidApiHotPlugEvent, nint __)
        {
            if (hidApiHotPlugEvent == HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED)
            {
                Log($"HID hotplug arrival: VID_{device->VendorId:X04}&PID_{device->ProductId:X04} interface={device->InterfaceNumber}");
                _deviceQueue.Add(*device);
            }

            return 0;
        }

        private async Task<int> InitDevice(HidDeviceInfo deviceInfo)
        {
            var messageType = (deviceInfo).GetHidppMessageType();
            switch (messageType)
            {
                case HidppMessageType.NONE:
                case HidppMessageType.VERY_LONG:
                    Log($"Skipping HID device: VID_{deviceInfo.VendorId:X04}&PID_{deviceInfo.ProductId:X04} usagePage=0x{deviceInfo.UsagePage:X04} usage=0x{deviceInfo.Usage:X04}");
                    return 0;
            }

            string devPath = (deviceInfo).GetPath();

            HidDevicePtr dev = HidOpenPath(ref deviceInfo);
            _ = HidWinApiGetContainerId(dev, out Guid containerId);

            Log($"Initializing HID++ {messageType}: VID_{deviceInfo.VendorId:X04}&PID_{deviceInfo.ProductId:X04} interface={deviceInfo.InterfaceNumber} usagePage=0x{deviceInfo.UsagePage:X04} usage=0x{deviceInfo.Usage:X04} container={containerId}");

#if DEBUG
            Console.WriteLine(devPath);
            Console.WriteLine(containerId.ToString());
            Console.WriteLine("x{0:X04}", (deviceInfo).Usage);
            Console.WriteLine("x{0:X04}", (deviceInfo).UsagePage);
            Console.WriteLine();
#endif

            HidppDevices value;
            lock (_deviceMapLock)
            {
                if (!_deviceMap.TryGetValue(containerId, out value!))
                {
                    value = new();
                    _deviceMap[containerId] = value;
                }

                _containerMap[devPath] = containerId;
            }

            switch (messageType)
            {
                case HidppMessageType.SHORT:
                    await value.SetDevShort(dev);
                    break;
                case HidppMessageType.LONG:
                    await value.SetDevLong(dev);
                    break;
            }

            return 0;
        }

        private unsafe int DeviceLeft(HidHotPlugCallbackHandle callbackHandle, HidDeviceInfo* deviceInfo, HidApiHotPlugEvent hidApiHotPlugEvent, nint userData)
        {
            string devPath = (*deviceInfo).GetPath();
            Log($"HID hotplug removal: VID_{deviceInfo->VendorId:X04}&PID_{deviceInfo->ProductId:X04} interface={deviceInfo->InterfaceNumber}");

            lock (_deviceMapLock)
            {
                if (_containerMap.TryGetValue(devPath, out var containerId))
                {
                    if (_deviceMap.Remove(containerId, out var devices))
                    {
                        devices.Dispose();
                    }

                    foreach (var path in _containerMap
                        .Where(x => x.Value == containerId)
                        .Select(x => x.Key)
                        .ToArray())
                    {
                        _containerMap.Remove(path);
                    }
                }
            }

            return 0;
        }

        public void Start(CancellationToken cancellationToken)
        {
            Log("Registering Logitech HID hotplug callbacks");
            new Thread(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var dev = _deviceQueue.Take();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await InitDevice(dev);
                }
            }).Start();

            unsafe
            {
                var arrivalResult = HidHotplugRegisterCallback(0x046D, 0x00, HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED, HidApiHotPlugFlag.HID_API_HOTPLUG_ENUMERATE, EnqueueDevice, IntPtr.Zero, (int*)IntPtr.Zero);
                var removalResult = HidHotplugRegisterCallback(0x046D, 0x00, HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_LEFT, HidApiHotPlugFlag.NONE, DeviceLeft, IntPtr.Zero, (int*)IntPtr.Zero);
                Log($"HID hotplug callbacks registered: arrival={arrivalResult}, removal={removalResult}");
            }
        }
    
        public async Task ForceBatteryUpdates()
        {
            foreach (var (_, hidppDevice) in _deviceMap)
            {
                var tasks = hidppDevice.DeviceCollection
                    .Select(x => x.Value)
                    .Select(x => x.UpdateBattery(true));

                await Task.WhenAll(tasks);
            }
        }
    }
}
