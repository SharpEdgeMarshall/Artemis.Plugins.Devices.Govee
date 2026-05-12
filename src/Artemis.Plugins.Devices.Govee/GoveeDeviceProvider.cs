using Artemis.Core.DeviceProviders;
using Artemis.Core.Services;
using RGB.NET.Core;
using Serilog;
using Artemis.Plugins.Devices.Govee.RGB.NET;

namespace Artemis.Plugins.Devices.Govee;

/// <summary>
/// Artemis device provider for Govee RGB lights via Bluetooth.
/// </summary>
public class GoveeDeviceProvider : DeviceProvider
{
    private readonly ILogger _logger;
    private readonly IDeviceService _deviceService;
    private readonly GoveeRgbDeviceProvider _rgbDeviceProvider;

    public GoveeDeviceProvider(ILogger logger, IDeviceService deviceService)
    {
        _logger = logger;
        _deviceService = deviceService;
        _rgbDeviceProvider = new GoveeRgbDeviceProvider(logger);
    }

    public override GoveeRgbDeviceProvider RgbDeviceProvider => _rgbDeviceProvider;

    public override void Enable()
    {
        RgbDeviceProvider.Exception += Provider_OnException;
        _deviceService.AddDeviceProvider(this);
    }

    public override void Disable()
    {
        _deviceService.RemoveDeviceProvider(this);
        RgbDeviceProvider.Exception -= Provider_OnException;
        RgbDeviceProvider.Dispose();
    }

    private void Provider_OnException(object? sender, ExceptionEventArgs args)
    {
        _logger.Debug(args.Exception, "Govee Exception: {message}", args.Exception.Message);
    }
}