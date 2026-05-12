using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.Devices.Govee.RGB.NET;
using RGB.NET.Core;
using Serilog;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Artemis.Plugins.Devices.Govee.RGB.NET.Govee;

/// <summary>
/// Handles sending color updates to a Govee device over BLE.
/// </summary>
public class GoveeUpdateQueue : UpdateQueue
{
    private static readonly Guid ServiceUuid = new("00010203-0405-0607-0809-0a0b0c0d1910");
    private static readonly Guid CharacteristicUuid = new("00010203-0405-0607-0809-0a0b0c0d2b11");
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    private readonly ulong _bluetoothAddress;
    private readonly GoveeRgbDeviceProvider _deviceProvider;
    private readonly ILogger _logger;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeCharacteristic;
    private bool _isConnected;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _isReconnecting;
    private int _reconnectAttempt;
    private bool _hasLoggedAccessDenied;
    private bool _lastSentPowerOff;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;

    public GoveeUpdateQueue(IDeviceUpdateTrigger updateTrigger, ulong bluetoothAddress, GoveeRgbDeviceProvider deviceProvider, ILogger logger)
        : base(updateTrigger)
    {
        _bluetoothAddress = bluetoothAddress;
        _deviceProvider = deviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Connects to the Govee device and resolves the GATT write characteristic.
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
    /// Disconnects from the Govee device.
    /// </summary>
    public void Disconnect()
    {
        _isConnected = false;
        _isReconnecting = false;
        _reconnectAttempt = 0;
        _lastSentPowerOff = false;
        StopKeepAliveLoop();
        _writeCharacteristic = null;
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
                    byte[] powerOffPacket = BuildPowerPacket(false);
                    _ = _writeCharacteristic.WriteValueAsync(powerOffPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                    _lastSentPowerOff = true;
                }
                return true;
            }

            byte brightness = Math.Max(r, Math.Max(g, b));

            // Split intensity and color so the dedicated brightness packet controls dimming.
            byte normalizedR = (byte)Math.Clamp((r * 255) / brightness, 0, 255);
            byte normalizedG = (byte)Math.Clamp((g * 255) / brightness, 0, 255);
            byte normalizedB = (byte)Math.Clamp((b * 255) / brightness, 0, 255);

            if (_lastSentPowerOff)
            {
                byte[] powerOnPacket = BuildPowerPacket(true);
                _ = _writeCharacteristic.WriteValueAsync(powerOnPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                _lastSentPowerOff = false;
            }

            byte[] brightnessPacket = BuildBrightnessPacket(brightness);
            _ = _writeCharacteristic.WriteValueAsync(brightnessPacket.AsBuffer(), GattWriteOption.WriteWithoutResponse);

            byte[] colorPacket = BuildColorPacket(normalizedR, normalizedG, normalizedB);
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

    /// <summary>
    /// Triggers a background reconnection attempt with exponential backoff.
    /// </summary>
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

    /// <summary>
    /// Attempts to reconnect with exponential backoff (1s, 2s, 4s, 8s, 16s, then 30s).
    /// </summary>
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
            _device?.Dispose();
            _device = null;

            _logger.Debug("[{Address}] Connecting...", $"{_bluetoothAddress:X12}");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress);
            if (_device == null)
            {
                _logger.Warning("[{Address}] BluetoothLEDevice.FromBluetoothAddressAsync returned null", $"{_bluetoothAddress:X12}");
                return false;
            }

            _logger.Debug("[{Address}] Requesting all GATT services...", $"{_bluetoothAddress:X12}");
            GattDeviceServicesResult servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            _logger.Debug("[{Address}] Services result: {Status}, count: {Count}", $"{_bluetoothAddress:X12}", servicesResult.Status, servicesResult.Services.Count);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                return false;

            GattDeviceService? service = null;
            foreach (GattDeviceService s in servicesResult.Services)
            {
                _logger.Debug("[{Address}] Found service: {Uuid}", $"{_bluetoothAddress:X12}", s.Uuid);
                if (s.Uuid == ServiceUuid)
                    service = s;
            }

            if (service == null)
            {
                _logger.Warning("[{Address}] Target service {Uuid} not found", $"{_bluetoothAddress:X12}", ServiceUuid);
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

            foreach (GattCharacteristic c in charsResult.Characteristics)
            {
                _logger.Debug("[{Address}] Found characteristic: {Uuid}", $"{_bluetoothAddress:X12}", c.Uuid);
                if (c.Uuid == CharacteristicUuid)
                    _writeCharacteristic = c;
            }

            if (_writeCharacteristic == null)
            {
                _logger.Warning("[{Address}] Target characteristic {Uuid} not found", $"{_bluetoothAddress:X12}", CharacteristicUuid);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
                return false;

            _isConnected = true;
            StartKeepAliveLoop();
            _logger.Information("[{Address}] Connected and ready", $"{_bluetoothAddress:X12}");
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

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, cancellationToken);

                if (!_isConnected || _writeCharacteristic == null)
                    return;

                byte[] keepAlivePacket = BuildKeepAlivePacket();
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

    /// <summary>
    /// Builds a Govee BLE color command packet.
    /// Packet format: 0x33 0x05 0x02 R G B [padding] XOR
    /// </summary>
    public static byte[] BuildColorPacket(byte r, byte g, byte b)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33; // Command indicator
        packet[1] = 0x05; // Color command
        packet[2] = 0x02; // Manual color mode
        packet[3] = r;
        packet[4] = g;
        packet[5] = b;
        // bytes 6-18 are zero-padded
        packet[19] = CalculateXor(packet);
        return packet;
    }

    /// <summary>
    /// Builds a Govee BLE power command packet.
    /// </summary>
    public static byte[] BuildPowerPacket(bool on)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33;
        packet[1] = 0x01; // Power command
        packet[2] = (byte)(on ? 0x01 : 0x00);
        packet[19] = CalculateXor(packet);
        return packet;
    }

    /// <summary>
    /// Builds a Govee BLE brightness command packet.
    /// Brightness range: 0x01 (minimum) to 0xFE (maximum).
    /// </summary>
    public static byte[] BuildBrightnessPacket(byte brightness)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33;
        packet[1] = 0x04; // Brightness command
        packet[2] = Math.Clamp(brightness, (byte)0x01, (byte)0xFE);
        packet[19] = CalculateXor(packet);
        return packet;
    }

    /// <summary>
    /// Builds a keep-alive packet (sent every ~2 seconds by the app).
    /// </summary>
    public static byte[] BuildKeepAlivePacket()
    {
        byte[] packet = new byte[20];
        packet[0] = 0xAA;
        packet[1] = 0x01;
        packet[19] = CalculateXor(packet);
        return packet;
    }

    /// <summary>
    /// Calculates the XOR checksum for a Govee command packet.
    /// </summary>
    private static byte CalculateXor(byte[] packet)
    {
        byte xor = 0;
        for (int i = 0; i < packet.Length - 1; i++)
            xor ^= packet[i];
        return xor;
    }
}
