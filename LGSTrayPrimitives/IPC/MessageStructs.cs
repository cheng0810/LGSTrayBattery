using MessagePack;

namespace LGSTrayPrimitives.MessageStructs;

public enum IPCMessageType : byte
{
    HEARTBEAT = 0,
    INIT,
    UPDATE,
    REMOVE
}

public enum DeviceSource : byte
{
    Unknown = 0,
    Native,
    GHub
}

public enum DeviceDisconnectReason : byte
{
    Unknown = 0,
    Removed,
    BackendRestart
}

public enum IPCMessageRequestType : byte
{
    BATTERY_UPDATE_REQUEST = 0
}

[Union(0, typeof(InitMessage))]
[Union(1, typeof(UpdateMessage))]
[Union(2, typeof(HeartbeatMessage))]
[Union(3, typeof(RemoveMessage))]
public abstract class IPCMessage(string deviceId)
{
    [Key(0)]
    public string deviceId = deviceId;
}

[MessagePackObject]
public class InitMessage(
    string deviceId,
    string deviceName,
    bool hasBattery,
    DeviceType deviceType,
    DeviceSource source = DeviceSource.Unknown
) : IPCMessage(deviceId)
{
    [Key(1)]
    public string deviceName = deviceName;

    [Key(2)]
    public bool hasBattery = hasBattery;

    [Key(3)]
    public DeviceType deviceType = deviceType;

    [Key(4)]
    public DeviceSource source = source;
}

[MessagePackObject]
public class UpdateMessage(
    string deviceId,
    double batteryPercentage,
    PowerSupplyStatus powerSupplyStatus,
    int batteryMVolt,
    DateTimeOffset updateTime,
    double mileage = -1
) : IPCMessage(deviceId)
{
    [Key(1)]
    public double batteryPercentage = batteryPercentage;

    [Key(2)]
    public PowerSupplyStatus powerSupplyStatus = powerSupplyStatus;

    [Key(3)]
    public int batteryMVolt = batteryMVolt;

    [Key(4)]
    public DateTimeOffset updateTime = updateTime;

    [Key(5)]
    public double Mileage = mileage;
}

[MessagePackObject]
public class HeartbeatMessage(string deviceId, DateTimeOffset sentAt, int processId) : IPCMessage(deviceId)
{
    public const int IntervalSeconds = 15;
    public const int StaleAfterSeconds = IntervalSeconds * 3;

    [Key(1)]
    public DateTimeOffset sentAt = sentAt;

    [Key(2)]
    public int processId = processId;
}

[MessagePackObject]
public class RemoveMessage(string deviceId, DeviceDisconnectReason reason) : IPCMessage(deviceId)
{
    [Key(1)]
    public DeviceDisconnectReason reason = reason;
}

[MessagePackObject]
public class BatteryUpdateRequestMessage()
{
    [Key(0)]
    public int id;
}
