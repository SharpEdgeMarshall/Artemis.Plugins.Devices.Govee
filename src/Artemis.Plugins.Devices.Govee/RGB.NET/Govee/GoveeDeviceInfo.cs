using RGB.NET.Core;

namespace Artemis.Plugins.Devices.Govee.RGB.NET.Govee;

public class GoveeDeviceInfo : IRGBDeviceInfo
{
    public GoveeDeviceInfo(ulong bluetoothAddress, string deviceName)
    {
        BluetoothAddress = $"{bluetoothAddress:X12}";
        DeviceName = deviceName;
        Manufacturer = "Govee";
        Model = deviceName;
        DeviceType = RGBDeviceType.LedStripe;
    }

    public string BluetoothAddress { get; }

    public RGBDeviceType DeviceType { get; }
    public string DeviceName { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public object? LayoutMetadata { get; set; }
}
