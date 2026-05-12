using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.Devices.Govee.RGB.NET.Govee;
using RGB.NET.Core;
using Serilog;
using Windows.Devices.Bluetooth.Advertisement;

namespace Artemis.Plugins.Devices.Govee.RGB.NET;

/// <summary>
/// RGB.NET device provider implementation for Govee lights via Bluetooth.
/// </summary>
public class GoveeRgbDeviceProvider : AbstractRGBDeviceProvider
{
    private readonly ILogger _logger;
    private readonly List<GoveeUpdateQueue> _updateQueues = new();

    public GoveeRgbDeviceProvider(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    protected override void InitializeSDK() { }

    /// <summary>
    /// Loads Govee devices discovered via Bluetooth LE advertisement scanning.
    /// </summary>
    protected override IEnumerable<IRGBDevice> LoadDevices()
    {
        _logger.Information("Scanning for Govee BLE devices...");

        IDeviceUpdateTrigger updateTrigger = GetUpdateTrigger();
        Dictionary<ulong, string> discovered;

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            discovered = ScanForDevices(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error scanning for Govee BLE devices");
            yield break;
        }

        _logger.Information("Found {Count} Govee device(s)", discovered.Count);

        foreach ((ulong address, string name) in discovered)
        {
            GoveeDeviceInfo deviceInfo = new(address, name);
            GoveeUpdateQueue updateQueue = new(updateTrigger, address, this, _logger);
            _updateQueues.Add(updateQueue);
            updateQueue.Connect();
            _logger.Information("Added Govee device: {DeviceName} ({Address})", name, deviceInfo.BluetoothAddress);
            yield return new GoveeDevice(deviceInfo, updateQueue);
        }
    }

    /// <summary>
    /// Scans for BLE advertisements synchronously using the native WinRT watcher,
    /// which is properly stoppable via cancellation.
    /// </summary>
    private static Dictionary<ulong, string> ScanForDevices(CancellationToken cancellationToken)
    {
        var discovered = new Dictionary<ulong, string>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(name) && IsGoveeDevice(name))
                discovered[args.BluetoothAddress] = name;
        };

        watcher.Start();
        cancellationToken.WaitHandle.WaitOne();
        watcher.Stop();

        return discovered;
    }

    private static bool IsGoveeDevice(string name) =>
        name.StartsWith("ihoment_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Govee_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("GVH", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the update trigger for this device provider.
    /// </summary>
    protected override IDeviceUpdateTrigger CreateUpdateTrigger(int id, double updateRateHardLimit)
        => new DeviceUpdateTrigger(updateRateHardLimit);

    /// <summary>
    /// Disconnects all Govee devices when the provider is disposed.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            foreach (GoveeUpdateQueue queue in _updateQueues)
                queue.Disconnect();
            _updateQueues.Clear();
        }
    }
}
