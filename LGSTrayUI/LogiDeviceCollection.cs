using LGSTrayCore;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows;

namespace LGSTrayUI
{
    public class LogiDeviceCollection : ILogiDeviceCollection
    {
        private readonly UserSettingsWrapper _userSettings;
        private readonly LogiDeviceViewModelFactory _logiDeviceViewModelFactory;
        private readonly ISubscriber<IPCMessage> _subscriber;
        private DateTimeOffset _lastNativeHeartbeat = DateTimeOffset.MinValue;
        private int _nativeBackendProcessId;

        public ObservableCollection<LogiDeviceViewModel> Devices { get; } = [];
        public IEnumerable<LogiDevice> GetDevices() => Devices;

        public LogiDeviceCollection(
            UserSettingsWrapper userSettings,
            LogiDeviceViewModelFactory logiDeviceViewModelFactory,
            ISubscriber<IPCMessage> subscriber
        )
        {
            _userSettings = userSettings;
            _logiDeviceViewModelFactory = logiDeviceViewModelFactory;
            _subscriber = subscriber;

            _subscriber.Subscribe(x =>
            {
                if (x is InitMessage initMessage)
                {
                    OnInitMessage(initMessage);
                }
                else if (x is UpdateMessage updateMessage)
                {
                    OnUpdateMessage(updateMessage);
                }
                else if (x is HeartbeatMessage heartbeatMessage)
                {
                    OnHeartbeatMessage(heartbeatMessage);
                }
                else if (x is RemoveMessage removeMessage)
                {
                    OnRemoveMessage(removeMessage);
                }
            });

            LoadPreviouslySelectedDevices();
        }

        private void LoadPreviouslySelectedDevices()
        {
            foreach (var deviceId in _userSettings.SelectedDevices)
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    continue;
                }

                Devices.Add(
                    _logiDeviceViewModelFactory.CreateViewModel((x) =>
                    {
                        x.DeviceId = deviceId!;
                        x.DeviceName = "Not Initialised";
                        x.IsChecked = true;
                    })
                );
            }
        }

        public bool TryGetDevice(string deviceId, [NotNullWhen(true)] out LogiDevice? device)
        {
            device = Devices.SingleOrDefault(x => x.DeviceId == deviceId);

            return device != null;
        }

        public void OnInitMessage(InitMessage initMessage)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var dev = Devices.SingleOrDefault(x => x.DeviceId == initMessage.deviceId);
                if (dev != null)
                {
                    dev.UpdateState(initMessage);
                    RecordCurrentHeartbeat(dev);
                    return;
                }

                dev = _logiDeviceViewModelFactory.CreateViewModel((x) => x.UpdateState(initMessage));
                RecordCurrentHeartbeat(dev);
                Devices.Add(dev);
            });
        }

        public void OnUpdateMessage(UpdateMessage updateMessage)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var device = Devices.FirstOrDefault(dev => dev.DeviceId == updateMessage.deviceId);
                if (device == null) { return; }

                device.UpdateState(updateMessage);
            });
        }

        public void OnHeartbeatMessage(HeartbeatMessage heartbeatMessage)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var backendRestarted = _nativeBackendProcessId != 0 &&
                    _nativeBackendProcessId != heartbeatMessage.processId;
                _nativeBackendProcessId = heartbeatMessage.processId;
                _lastNativeHeartbeat = heartbeatMessage.sentAt;

                foreach (var device in Devices.Where(x =>
                    x.Source == DeviceSource.Native || x.Source == DeviceSource.Unknown))
                {
                    if (backendRestarted)
                    {
                        device.MarkDisconnected(DeviceDisconnectReason.BackendRestart);
                    }
                    device.RecordBackendHeartbeat(heartbeatMessage);
                }
            });
        }

        public void OnRemoveMessage(RemoveMessage removeMessage)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var device = Devices.FirstOrDefault(x => x.DeviceId == removeMessage.deviceId);
                device?.MarkDisconnected(removeMessage.reason);
            });
        }

        private void RecordCurrentHeartbeat(LogiDeviceViewModel device)
        {
            if (device.Source != DeviceSource.Native || _lastNativeHeartbeat == DateTimeOffset.MinValue)
            {
                return;
            }

            device.RecordBackendHeartbeat(new HeartbeatMessage(string.Empty, _lastNativeHeartbeat, _nativeBackendProcessId));
        }
    }
}
