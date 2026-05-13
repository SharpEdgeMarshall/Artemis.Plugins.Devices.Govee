using RGB.NET.Core;
using Artemis.Plugins.Devices.Govee.RGB.NET.Protocols;

namespace Artemis.Plugins.Devices.Govee.RGB.NET.Govee;

public class GoveeDeviceInfo : IRGBDeviceInfo
{
    public GoveeDeviceInfo(ulong bluetoothAddress, string deviceName, ILightProtocol protocol)
    {
        BluetoothAddress = $"{bluetoothAddress:X12}";
        DeviceName = deviceName;
        Manufacturer = protocol.Manufacturer;
        Model = deviceName;
        Protocol = protocol.Name;
        DeviceType = RGBDeviceType.LedStripe;
    }

    public string BluetoothAddress { get; }

    public RGBDeviceType DeviceType { get; }
    public string DeviceName { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public string Protocol { get; }
    public object? LayoutMetadata { get; set; }
}
