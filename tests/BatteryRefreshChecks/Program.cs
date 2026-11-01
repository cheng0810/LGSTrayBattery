using System.Reflection;
using System.Xml.Linq;
using LGSTrayHID;
using LGSTrayCore;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayPrimitives.IPC;

if (args.Contains("--request-refresh"))
{
    await BatteryRefreshPipe.RequestAsync();
    await Task.Delay(1000);
    Console.WriteLine("Refresh request sent to the running application.");
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// Exercise the receive path without opening hardware. Only the selected battery
// feature's unsolicited events may wake the poller; query replies must not.
var receiver = new HidppDevices();
var device = new HidppDevice(receiver, 1);
const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
typeof(HidppDevice).GetField("_batteryFeatureIndex", fields)!.SetValue(device, 6);
var collection = (Dictionary<ushort, HidppDevice>)typeof(HidppDevices)
    .GetField("_deviceCollection", fields)!.GetValue(receiver)!;
collection.Add(1, device);
var pending = (SemaphoreSlim)typeof(HidppDevice).GetField("_refreshRequested", fields)!.GetValue(device)!;
var receive = typeof(HidppDevices).GetMethod("ProcessMessgage", fields)!;
async Task Feed(byte[] packet) => await (Task)receive.Invoke(receiver, new object[] { packet })!;
await Feed(new byte[] { 0x11, 1 });
await Feed(new byte[] { 0x11, 1, 6, 0x1A, 0, 0, 0 });
await Feed(new byte[] { 0x11, 1, 7, 0, 0, 0, 0 });
Check(pending.CurrentCount == 0, "Replies and unrelated events must not refresh");
await Feed(new byte[] { 0x11, 1, 6, 0, 0, 0, 0 });
Check(pending.CurrentCount == 1, "Battery event must wake polling");
Parallel.For(0, 100, _ => device.RequestBatteryRefresh());
Check(pending.CurrentCount == 1, "Repeated refreshes must coalesce");
await pending.WaitAsync();
device.MarkRemoved();
device.RequestBatteryRefresh();
Check(pending.CurrentCount == 0, "Removed device must ignore refreshes");
GC.SuppressFinalize(receiver); // No native handles were opened.

var model = new LogiDevice {
    Source = DeviceSource.Native, IsConnected = true,
    BackendLastHeartbeat = DateTimeOffset.UtcNow,
    LastUpdate = DateTimeOffset.Now.AddSeconds(-60)
};
bool Online() => bool.Parse(XDocument.Parse(model.GetXmlData()).Root!.Element("online")!.Value);
Check(Online(), "Recent data should be online");
model.LastUpdate = DateTimeOffset.Now.AddSeconds(-121);
Check(!Online(), "Data older than 120 seconds must be stale");
model.LastUpdate = DateTimeOffset.Now;
model.BackendLastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-46);
Check(!Online(), "Fresh data must not conceal a dead backend");
Console.WriteLine("Battery notification, refresh coalescing and freshness checks passed.");
