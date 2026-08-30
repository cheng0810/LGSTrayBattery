using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayHID.Features;
using System.Text;

using static LGSTrayHID.HidppDevices;

using Log = LGSTrayPrimitives.DiagnosticLog;

namespace LGSTrayHID
{
    public class HidppDevice
    {
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        private Func<HidppDevice, Task<BatteryUpdateReturn?>>? _getBatteryAsync;
        private int _removed;

        public string DeviceName { get; private set; } = string.Empty;
        public int DeviceType { get; private set; } = 3;
        public string Identifier { get; private set; } = string.Empty;

        private BatteryUpdateReturn lastBatteryReturn;
        private DateTimeOffset lastUpdate = DateTimeOffset.MinValue;

        private readonly HidppDevices _parent;
        public HidppDevices Parent => _parent;

        private readonly byte _deviceIdx;
        public byte DeviceIdx => _deviceIdx;
        public bool Removed => Volatile.Read(ref _removed) != 0;

        private readonly Dictionary<ushort, byte> _featureMap = [];
        public Dictionary<ushort, byte> FeatureMap => _featureMap;

        public HidppDevice(HidppDevices parent, byte deviceIdx)
        {
            _parent = parent;
            _deviceIdx = deviceIdx;
        }

        public async Task InitAsync()
        {
            if (Removed) { return; }
            await _initSemaphore.WaitAsync();
            try
            {
                Hidpp20 ret;

                // Sync Ping
                int successCount = 0;
                int successThresh = 3;
                for (int i = 0; i < 10; i++)
                {
                    var ping = await _parent.Ping20(_deviceIdx, 100);
                    if (ping)
                    {
                        successCount++;
                    }
                    else
                    {
                        successCount = 0;
                    }

                    if (successCount >= successThresh) { break; }
                }

                if (successCount < successThresh)
                {
                    Log.WriteLine($"Device index {_deviceIdx} failed HID++ ping sync");
                    return;
                }

                // Find 0x0001 IFeatureSet
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, 0x00, 0x00 | SW_ID, 0x00, 0x01, 0x00 });
                if (ret.Length == 0)
                {
                    Log.WriteLine($"Device index {_deviceIdx} did not return IFeatureSet index");
                    return;
                }
                _featureMap[0x0001] = ret.GetParam(0);

                // Get Feature Count
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, _featureMap[0x0001], 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length == 0)
                {
                    Log.WriteLine($"Device index {_deviceIdx} did not return feature count");
                    return;
                }
                int featureCount = ret.GetParam(0);
                Log.WriteLine($"Device index {_deviceIdx} reports {featureCount} feature(s)");

                // Enumerate Features
                for (byte i = 0; i <= featureCount; i++)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, _featureMap[0x0001], 0x10 | SW_ID, i, 0x00, 0x00 });
                    if (ret.Length == 0)
                    {
                        Log.WriteLine($"Device index {_deviceIdx} feature slot {i} returned no response");
                        continue;
                    }
                    ushort featureId = (ushort)((ret.GetParam(0) << 8) + ret.GetParam(1));

                    _featureMap[featureId] = i;
                }

                await InitPopulateAsync();
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0018:Inline variable declaration")]
        private async Task InitPopulateAsync()
        {
            Hidpp20 ret;
            byte featureId;

            // Device name
            if (_featureMap.TryGetValue(0x0005, out featureId))
            {
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length == 0)
                {
                    Log.WriteLine($"Device index {_deviceIdx} did not return a device-name length");
                    return;
                }
                int nameLength = ret.GetParam(0);

                string name = "";

                while (name.Length < nameLength)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x10 | SW_ID, (byte)name.Length, 0x00, 0x00 });
                    if (ret.Length == 0)
                    {
                        Log.WriteLine($"Device index {_deviceIdx} did not return device-name bytes at offset {name.Length}");
                        return;
                    }
                    name += Encoding.UTF8.GetString(ret.GetParams());
                }

                DeviceName = name.TrimEnd('\0');

                foreach (var tag in GlobalSettings.settings.DisabledDevices)
                {
                    if (DeviceName.Contains(tag))
                    {
                        Log.WriteLine($"{DeviceName} is marked as disabled");
                        return;
                    }
                };

                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x20 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length == 0)
                {
                    Log.WriteLine($"{DeviceName} did not return a device type");
                    return;
                }
                DeviceType = ret.GetParam(0);
            }
            else
            {
                // Device does not have a name/Hidpp error ignore it
                Log.WriteLine($"Device index {_deviceIdx} does not expose feature 0x0005 device name");
                return;
            }

            if (_featureMap.TryGetValue(0x0003, out featureId))
            {
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length < 19)
                {
                    Log.WriteLine($"{DeviceName} did not return enough device-info bytes");
                    return;
                }

                string unitId = BitConverter.ToString(ret.GetParams().ToArray(), 1, 4).Replace("-", string.Empty);
                string modelId = BitConverter.ToString(ret.GetParams().ToArray(), 7, 5).Replace("-", string.Empty);

                bool serialNumberSupported = (ret.GetParam(14) & 0x1) == 0x1;
                string? serialNumber = null;
                if (serialNumberSupported)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x20 | SW_ID, 0x00, 0x00, 0x00 });
                    if (ret.Length < 15)
                    {
                        Log.WriteLine($"{DeviceName} did not return a serial number");
                        return;
                    }
                    serialNumber = BitConverter.ToString(ret.GetParams().ToArray(), 0, 11).Replace("-", string.Empty);
                }

                Identifier = serialNumber ?? $"{unitId}-{modelId}";
            }
            else
            {
                // Device does not have a serial identifier the device name as a hash identifier
                Identifier = $"{DeviceName.GetHashCode():X04}";
            }

#if DEBUG
            Log.WriteLine("---");
            Log.WriteLine(DeviceName + " Ready");
            Log.WriteLine(Identifier);
            foreach ((ushort featureIdItr, string featureDesc) in new (ushort, string)[]
            {
                (0x1000, "Battery Unified Level"),
                (0x1001, "Battery Voltage"),
                (0x1004, "Unified Battery"),
            })
            {
                if (_featureMap.ContainsKey(featureIdItr))
                {
                    Log.WriteLine($"0x{featureIdItr:X} - {featureDesc} Found");
                }
            }
            Log.WriteLine("---");
#endif

            _getBatteryAsync = FeatureMap switch
            {
                { } when FeatureMap.ContainsKey(0x1000) => Battery1000.GetBatteryAsync,
                { } when FeatureMap.ContainsKey(0x1001) => Battery1001.GetBatteryAsync,
                { } when FeatureMap.ContainsKey(0x1004) => Battery1004.GetBatteryAsync,
                _ => null
            };
            Log.WriteLine($"{DeviceName} battery feature: {(_getBatteryAsync == null ? "none" : "found")}");

            SignalInit();

            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(1000);
                    if (Parent.Disposed) { return; }

                    SignalInit();
                }
            });

            await Task.Delay(1000);

            _ = Task.Run(async () =>
            {
                if (_getBatteryAsync == null) { return; }

                while (!Removed && !Parent.Disposed)
                {
                    var now = DateTimeOffset.Now;
#if DEBUG
                    var expectedUpdateTime = lastUpdate.AddSeconds(1);
#else
                    var expectedUpdateTime = lastUpdate.AddSeconds(GlobalSettings.settings.PollPeriod);
#endif
                    if (now < expectedUpdateTime)
                    {
                        await Task.Delay((int)(expectedUpdateTime - now).TotalMilliseconds);
                    }

                    await UpdateBattery();
                    await Task.Delay(GlobalSettings.settings.RetryTime * 1000);
                }
            });
        }

        public async Task UpdateBattery(bool forceIpcUpdate = false)
        {
            if (Removed) { return; }
            if (Parent.Disposed) { return; }
            if (_getBatteryAsync == null) { return; }

            var ret = await _getBatteryAsync.Invoke(this);

            if (ret == null)
            {
                Log.WriteLine($"{DeviceName} battery poll returned no data");
                return;
            }

            var batStatus = ret.Value;
            lastUpdate = DateTimeOffset.Now;

            // INIT is idempotent. Re-announcing here lets the UI recover if the
            // named-pipe connection was not ready during initial discovery.
            SignalInit();

            var batteryChanged = batStatus != lastBatteryReturn;
            lastBatteryReturn = batStatus;
            var updateKind = batteryChanged || forceIpcUpdate ? "update" : "refresh";
            Log.WriteLine($"{DeviceName} battery {updateKind}: {batStatus.batteryPercentage:f0}% status={batStatus.status}");

            // A successful poll is also the device liveness signal. Publish it
            // even when the battery value is unchanged so API freshness does
            // not expire while the device remains connected.
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(Identifier, batStatus.batteryPercentage, batStatus.status, batStatus.batteryMVolt, lastUpdate)
            );
        }

        private void SignalInit()
        {
            if (Removed) { return; }
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.INIT,
                new InitMessage(
                    Identifier,
                    DeviceName,
                    _getBatteryAsync != null,
                    (DeviceType)DeviceType,
                    DeviceSource.Native
                )
            );
        }

        public void MarkRemoved()
        {
            Interlocked.Exchange(ref _removed, 1);
        }
    }
}
