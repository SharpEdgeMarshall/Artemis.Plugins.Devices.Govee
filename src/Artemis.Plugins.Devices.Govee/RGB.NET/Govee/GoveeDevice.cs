using RGB.NET.Core;

namespace Artemis.Plugins.Devices.Govee.RGB.NET.Govee;

/// <summary>
/// Represents a single Govee RGB light as an RGB.NET device.
/// </summary>
public class GoveeDevice : AbstractRGBDevice<GoveeDeviceInfo>
{
    public GoveeDevice(GoveeDeviceInfo deviceInfo, GoveeUpdateQueue updateQueue)
        : base(deviceInfo, updateQueue)
    {
        InitializeLayout();
    }

    private void InitializeLayout()
    {
        // Single LED representing the entire light
        Led? led = AddLed(LedId.Custom1, new Point(0, 0), new Size(50));
        if (led != null)
            led.Shape = Shape.Rectangle;
    }
}
