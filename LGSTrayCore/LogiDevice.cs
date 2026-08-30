using CommunityToolkit.Mvvm.ComponentModel;
using LGSTrayPrimitives;

using System.Xml.Linq;
using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayCore
{
    public partial class LogiDevice : ObservableObject
    {
        public const string NOT_FOUND = "NOT FOUND";

        [ObservableProperty]
        private DeviceType _deviceType;

        [ObservableProperty]
        private string _deviceId = NOT_FOUND;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTipString))]
        private string _deviceName = NOT_FOUND;

        [ObservableProperty]
        private bool _hasBattery = true;

        [ObservableProperty]
        private DeviceSource _source = DeviceSource.Unknown;

        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private DateTimeOffset _backendLastHeartbeat = DateTimeOffset.MinValue;

        [ObservableProperty]
        private int _backendProcessId;

        [ObservableProperty]
        private DeviceDisconnectReason _disconnectReason = DeviceDisconnectReason.Unknown;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTipString))]
        private double _batteryPercentage = -1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTipString))]
        private double _batteryVoltage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTipString))]
        private double _batteryMileage;


        [ObservableProperty]
        private PowerSupplyStatus _powerSupplyStatus;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTipString))]
        private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;

        public string ToolTipString
        {
            get
            {
#if DEBUG
                return $"{DeviceName}, {BatteryPercentage:f2}% - {LastUpdate}";
#else
                return $"{DeviceName}, {BatteryPercentage:f2}%";
#endif
            }
        }

        public Func<Task>? UpdateBatteryFunc;
        public async Task UpdateBatteryAsync()
        {
            if (UpdateBatteryFunc != null)
            {
                await UpdateBatteryFunc.Invoke();
            }
        }

        partial void OnLastUpdateChanged(DateTimeOffset value)
        {
            Console.WriteLine(ToolTipString);
        }

        public string GetXmlData(int staleAfterSeconds = 1200)
        {
            var hasUpdate = LastUpdate != DateTimeOffset.MinValue;
            var dataAgeSeconds = hasUpdate
                ? Math.Max(0, (long)(DateTimeOffset.Now - LastUpdate).TotalSeconds)
                : -1;
            var backendOnline = Source switch
            {
                DeviceSource.GHub => true,
                DeviceSource.Native => BackendLastHeartbeat != DateTimeOffset.MinValue &&
                    (DateTimeOffset.UtcNow - BackendLastHeartbeat).TotalSeconds <= HeartbeatMessage.StaleAfterSeconds,
                _ => false
            };
            var deviceOnline = IsConnected && hasUpdate && dataAgeSeconds <= staleAfterSeconds;
            var online = backendOnline && deviceOnline;

            var document = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("xml",
                    new XElement("device_id", DeviceId),
                    new XElement("device_name", DeviceName),
                    new XElement("device_type", DeviceType),
                    new XElement("battery_percent", $"{BatteryPercentage:f2}"),
                    new XElement("battery_voltage", $"{BatteryVoltage:f2}"),
                    new XElement("mileage", $"{BatteryMileage:f2}"),
                    new XElement("charging", (PowerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING).ToString()),
                    new XElement("last_update", LastUpdate),
                    new XElement("online", online.ToString()),
                    new XElement("data_age_seconds", dataAgeSeconds),
                    new XElement("backend_online", backendOnline.ToString()),
                    new XElement("device_online", deviceOnline.ToString()),
                    new XElement("disconnect_reason", DisconnectReason.ToString()),
                    new XElement("backend_pid", BackendProcessId)
                )
            );

            return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
        }
    }
}
