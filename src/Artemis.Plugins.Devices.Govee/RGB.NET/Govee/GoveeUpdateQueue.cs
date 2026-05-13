using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.Devices.Govee.RGB.NET;
using Artemis.Plugins.Devices.Govee.RGB.NET.Protocols;
using RGB.NET.Core;
using Serilog;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Artemis.Plugins.Devices.Govee.RGB.NET.Govee;

/// <summary>
/// Handles sending color updates to a BLE light device.
/// </summary>
public class GoveeUpdateQueue : UpdateQueue
{
    private readonly ulong _bluetoothAddress;
    private readonly ILightProtocol _protocol;
    private readonly GoveeRgbDeviceProvider _deviceProvider;
    private readonly ILogger _logger;

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeCharacteristic;
    private Guid? _selectedServiceUuid;
    private Guid? _selectedCharacteristicUuid;
    private bool _isConnected;

    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _isReconnecting;
    private int _reconnectAttempt;
    private bool _hasLoggedAccessDenied;

    private bool _lastSentPowerOff;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;

    public GoveeUpdateQueue(
        IDeviceUpdateTrigger updateTrigger,
        ulong bluetoothAddress,
        ILightProtocol protocol,
        GoveeRgbDeviceProvider deviceProvider,
        ILogger logger)
        : base(updateTrigger)
    {
        _bluetoothAddress = bluetoothAddress;
        _protocol = protocol;
        _deviceProvider = deviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Starts the connection/reconnection process.
    /// </summary>
    public void Connect()
    {
        _reconnectAttempt = 0;
        _hasLoggedAccessDenied = false;

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();

        if (_isReconnecting)
            return;

        _isReconnecting = true;
        _reconnectTask = RunReconnectLoopAsync(_reconnectCts.Token, immediateFirstAttempt: true);
    }

    /// <summary>
    /// Disconnects from the device and stops background workers.
    /// </summary>
    public void Disconnect()
    {
        _isConnected = false;
        _isReconnecting = false;
        _reconnectAttempt = 0;
        _lastSentPowerOff = false;

        StopKeepAliveLoop();

        _writeCharacteristic = null;
        _selectedServiceUuid = null;
        _selectedCharacteristicUuid = null;
        DetachDeviceEvents();
        _device?.Dispose();
        _device = null;

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        _reconnectTask = null;
    }

    /// <inheritdoc />
    protected override bool Update(ReadOnlySpan<(object key, Color color)> dataSet)
    {
        if (!_isConnected || _writeCharacteristic == null)
        {
            if (!_isReconnecting)
                TriggerReconnect();
            return false;
        }

        try
        {
            Color color = dataSet[0].color;
            byte r = (byte)Math.Clamp((int)Math.Round(color.R * 255.0), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round(color.G * 255.0), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round(color.B * 255.0), 0, 255);

            if (r == 0 && g == 0 && b == 0)
            {
                if (!_lastSentPowerOff)
                {
                    byte[] powerOffPacket = _protocol.BuildPowerPacket(false);
                    _ = _writeCharacteristic.WriteValueAsync(powerOffPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                    _lastSentPowerOff = true;
                }
                return true;
            }

            byte brightness = Math.Max(r, Math.Max(g, b));
            byte normalizedR = (byte)Math.Clamp((r * 255) / brightness, 0, 255);
            byte normalizedG = (byte)Math.Clamp((g * 255) / brightness, 0, 255);
            byte normalizedB = (byte)Math.Clamp((b * 255) / brightness, 0, 255);

            if (_lastSentPowerOff)
            {
                byte[] powerOnPacket = _protocol.BuildPowerPacket(true);
                _ = _writeCharacteristic.WriteValueAsync(powerOnPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                _lastSentPowerOff = false;
            }

            if (_protocol.UsesDedicatedBrightness)
            {
                byte[]? brightnessPacket = _protocol.BuildBrightnessPacket(brightness);
                if (brightnessPacket != null)
                    _ = _writeCharacteristic.WriteValueAsync(brightnessPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            }

            byte colorR = _protocol.UsesDedicatedBrightness ? normalizedR : r;
            byte colorG = _protocol.UsesDedicatedBrightness ? normalizedG : g;
            byte colorB = _protocol.UsesDedicatedBrightness ? normalizedB : b;
            byte[] colorPacket = _protocol.BuildColorPacket(colorR, colorG, colorB);
            _ = _writeCharacteristic.WriteValueAsync(colorPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[{Address}] Write failed, triggering reconnect", $"{_bluetoothAddress:X12}");
            _isConnected = false;
            _deviceProvider.Throw(ex);
            TriggerReconnect();
            return false;
        }
    }

    private void TriggerReconnect()
    {
        if (_isReconnecting)
            return;

        StopKeepAliveLoop();

        if (_reconnectCts == null || _reconnectCts.IsCancellationRequested)
            _reconnectCts = new CancellationTokenSource();

        _isReconnecting = true;
        _reconnectTask = RunReconnectLoopAsync(_reconnectCts.Token, immediateFirstAttempt: false);
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken, bool immediateFirstAttempt)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isConnected)
            {
                _reconnectAttempt++;
                int delayMs = GetReconnectDelayMs(_reconnectAttempt, immediateFirstAttempt);
                immediateFirstAttempt = false;

                _logger.Information("[{Address}] Scheduling reconnection attempt #{Attempt}", $"{_bluetoothAddress:X12}", _reconnectAttempt);
                if (delayMs > 0)
                {
                    _logger.Debug("[{Address}] Waiting {DelayMs}ms before reconnection attempt #{Attempt}", $"{_bluetoothAddress:X12}", delayMs, _reconnectAttempt);
                    await Task.Delay(delayMs, cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested)
                    return;

                _logger.Information("[{Address}] Attempting reconnection #{Attempt}", $"{_bluetoothAddress:X12}", _reconnectAttempt);
                bool connected = await TryConnectOnceAsync(cancellationToken);
                if (connected)
                {
                    _reconnectAttempt = 0;
                    _hasLoggedAccessDenied = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[{Address}] Reconnection attempt cancelled", $"{_bluetoothAddress:X12}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Address}] Error during reconnection attempt #{Attempt}", $"{_bluetoothAddress:X12}", _reconnectAttempt);
            _deviceProvider.Throw(ex);
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    private async Task<bool> TryConnectOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _isConnected = false;
            _writeCharacteristic = null;
            _selectedServiceUuid = null;
            _selectedCharacteristicUuid = null;
            DetachDeviceEvents();
            _device?.Dispose();
            _device = null;

            _logger.Debug("[{Address}] Connecting...", $"{_bluetoothAddress:X12}");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress);
            if (_device == null)
            {
                _logger.Warning("[{Address}] BluetoothLEDevice.FromBluetoothAddressAsync returned null", $"{_bluetoothAddress:X12}");
                return false;
            }

            AttachDeviceEvents(_device);

            _logger.Debug("[{Address}] Requesting all GATT services...", $"{_bluetoothAddress:X12}");
            GattDeviceServicesResult servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            _logger.Debug("[{Address}] Services result: {Status}, count: {Count}", $"{_bluetoothAddress:X12}", servicesResult.Status, servicesResult.Services.Count);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                return false;

            GattDeviceService? service = null;
            foreach (GattDeviceService s in servicesResult.Services)
            {
                _logger.Debug("[{Address}] Found service: {Uuid}", $"{_bluetoothAddress:X12}", s.Uuid);
                if (_protocol.MatchesServiceUuid(s.Uuid))
                {
                    service = s;
                    _selectedServiceUuid = s.Uuid;
                    break;
                }
            }

            if (service == null)
            {
                _logger.Warning("[{Address}] No matching service found for protocol {Protocol}", $"{_bluetoothAddress:X12}", _protocol.Name);
                return false;
            }

            GattCharacteristicsResult charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            _logger.Debug("[{Address}] Characteristics result: {Status}, count: {Count}", $"{_bluetoothAddress:X12}", charsResult.Status, charsResult.Characteristics.Count);
            if (charsResult.Status == GattCommunicationStatus.AccessDenied && !_hasLoggedAccessDenied)
            {
                _hasLoggedAccessDenied = true;
                _logger.Warning("[{Address}] GATT access denied. Pair the device in Windows Bluetooth settings and ensure Artemis has Bluetooth permission.", $"{_bluetoothAddress:X12}");
            }

            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                return false;

            GattCharacteristic? fallbackCharacteristic = null;
            foreach (GattCharacteristic c in charsResult.Characteristics)
            {
                _logger.Debug("[{Address}] Found characteristic: {Uuid}", $"{_bluetoothAddress:X12}", c.Uuid);
                if (_protocol.MatchesCharacteristicUuid(c.Uuid))
                {
                    fallbackCharacteristic ??= c;

                    bool canWriteNoResponse = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
                    bool canWrite = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
                    if (canWriteNoResponse || canWrite)
                    {
                        _writeCharacteristic = c;
                        _selectedCharacteristicUuid = c.Uuid;
                        break;
                    }
                }
            }

            if (_writeCharacteristic == null && fallbackCharacteristic != null)
            {
                _writeCharacteristic = fallbackCharacteristic;
                _selectedCharacteristicUuid = fallbackCharacteristic.Uuid;
            }

            if (_writeCharacteristic == null)
            {
                _logger.Warning("[{Address}] No matching characteristic found for protocol {Protocol}", $"{_bluetoothAddress:X12}", _protocol.Name);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
                return false;

            _isConnected = true;
            byte[] powerOnPacket = _protocol.BuildPowerPacket(true);
            _ = _writeCharacteristic.WriteValueAsync(powerOnPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            _lastSentPowerOff = false;
            StartKeepAliveLoop();
            _logger.Information("[{Address}] Connected and ready ({Protocol}) using service {ServiceUuid} and characteristic {CharacteristicUuid}",
                $"{_bluetoothAddress:X12}", _protocol.Name, _selectedServiceUuid, _selectedCharacteristicUuid);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Address}] Connect failed", $"{_bluetoothAddress:X12}");
            _deviceProvider.Throw(ex);
            _isConnected = false;
            return false;
        }
    }

    private void StartKeepAliveLoop()
    {
        StopKeepAliveLoop();

        if (_protocol.KeepAliveInterval == null || _protocol.BuildKeepAlivePacket() == null)
            return;

        _keepAliveCts = new CancellationTokenSource();
        _keepAliveTask = KeepAliveLoopAsync(_keepAliveCts.Token);
    }

    private void StopKeepAliveLoop()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
        _keepAliveTask = null;
    }

    private void AttachDeviceEvents(BluetoothLEDevice device)
    {
        device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
    }

    private void DetachDeviceEvents()
    {
        if (_device != null)
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            return;

        _logger.Warning("[{Address}] Bluetooth connection status changed to {Status}, triggering reconnect", $"{_bluetoothAddress:X12}", sender.ConnectionStatus);
        _isConnected = false;
        StopKeepAliveLoop();
        TriggerReconnect();
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_protocol.KeepAliveInterval!.Value, cancellationToken);

                if (!_isConnected || _writeCharacteristic == null)
                    return;

                byte[]? keepAlivePacket = _protocol.BuildKeepAlivePacket();
                if (keepAlivePacket == null)
                    return;

                GattCommunicationStatus status = await _writeCharacteristic.WriteValueAsync(keepAlivePacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                if (status != GattCommunicationStatus.Success)
                {
                    _logger.Warning("[{Address}] Keepalive write failed with status {Status}, triggering reconnect", $"{_bluetoothAddress:X12}", status);
                    _isConnected = false;
                    TriggerReconnect();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disconnecting or reconnecting.
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[{Address}] Keepalive loop failed, triggering reconnect", $"{_bluetoothAddress:X12}");
            _isConnected = false;
            TriggerReconnect();
        }
    }

    private static int GetReconnectDelayMs(int attempt, bool immediateFirstAttempt)
    {
        if (immediateFirstAttempt && attempt == 1)
            return 0;

        int backoffIndex = Math.Min(attempt - 1, 5);
        return Math.Min(1000 * (int)Math.Pow(2, backoffIndex), 30000);
    }
}
