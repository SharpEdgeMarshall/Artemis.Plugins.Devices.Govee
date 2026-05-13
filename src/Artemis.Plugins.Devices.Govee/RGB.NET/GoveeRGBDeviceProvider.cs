using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.Devices.Govee.RGB.NET.Govee;
using Artemis.Plugins.Devices.Govee.RGB.NET.Protocols;
using RGB.NET.Core;
using Serilog;
using Windows.Devices.Bluetooth.Advertisement;

namespace Artemis.Plugins.Devices.Govee.RGB.NET;

/// <summary>
/// RGB.NET device provider implementation for Govee lights via Bluetooth.
/// </summary>
public class GoveeRgbDeviceProvider : AbstractRGBDeviceProvider
{
    private static readonly ILightProtocol[] SupportedProtocols =
    {
        GoveeLightProtocol.Instance,
        HappyLightingProtocol.Instance
    };

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
        _logger.Information("Scanning for supported BLE lights...");

        IDeviceUpdateTrigger updateTrigger = GetUpdateTrigger();
        Dictionary<ulong, DiscoveredDevice> discovered;

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            discovered = ScanForDevices(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error scanning for BLE devices");
            yield break;
        }

        _logger.Information("Found {Count} supported device(s)", discovered.Count);

        foreach ((ulong address, DiscoveredDevice discoveredDevice) in discovered)
        {
            GoveeDeviceInfo deviceInfo = new(address, discoveredDevice.Name, discoveredDevice.Protocol);
            GoveeUpdateQueue updateQueue = new(updateTrigger, address, discoveredDevice.Protocol, this, _logger);
            _updateQueues.Add(updateQueue);
            updateQueue.Connect();
            _logger.Information("Added {Protocol} device: {DeviceName} ({Address})", discoveredDevice.Protocol.Name, discoveredDevice.Name, deviceInfo.BluetoothAddress);
            yield return new GoveeDevice(deviceInfo, updateQueue);
        }
    }

    /// <summary>
    /// Scans for BLE advertisements synchronously using the native WinRT watcher,
    /// which is properly stoppable via cancellation.
    /// </summary>
    private Dictionary<ulong, DiscoveredDevice> ScanForDevices(CancellationToken cancellationToken)
    {
        var discovered = new Dictionary<ulong, DiscoveredDevice>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName;
            ILightProtocol? protocol = TryGetProtocol(name, args.Advertisement.ServiceUuids);
            if (protocol == null)
                return;

            string deviceName = string.IsNullOrWhiteSpace(name)
                ? $"{protocol.Name}_{args.BluetoothAddress:X12}"
                : name;

            discovered[args.BluetoothAddress] = new DiscoveredDevice(deviceName, protocol);
        };

        watcher.Start();
        cancellationToken.WaitHandle.WaitOne();
        watcher.Stop();

        return discovered;
    }

    private static ILightProtocol? TryGetProtocol(string? name, IList<Guid> serviceUuids)
    {
        for (int i = 0; i < serviceUuids.Count; i++)
        {
            Guid uuid = serviceUuids[i];
            for (int p = 0; p < SupportedProtocols.Length; p++)
            {
                ILightProtocol protocol = SupportedProtocols[p];
                if (protocol.MatchesServiceUuid(uuid))
                    return protocol;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
            return TryGetProtocolByName(name);

        return null;
    }

    private static ILightProtocol? TryGetProtocolByName(string name)
    {
        if (name.StartsWith("ihoment_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Govee_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GVH", StringComparison.OrdinalIgnoreCase))
            return GoveeLightProtocol.Instance;

        if (name.StartsWith("ELK-BLEDOM", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LEDBLE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Triones", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("HappyLighting", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("HTM", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Dream~", StringComparison.OrdinalIgnoreCase))
            return HappyLightingProtocol.Instance;

        return null;
    }

    private sealed record DiscoveredDevice(string Name, ILightProtocol Protocol);

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
