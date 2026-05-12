using System;
using System.Runtime.InteropServices.WindowsRuntime;
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

    private readonly ulong _bluetoothAddress;
    private readonly GoveeRgbDeviceProvider _deviceProvider;
    private readonly ILogger _logger;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeCharacteristic;
    private bool _isConnected;

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
    public async void Connect()
    {
        try
        {
            _logger.Debug("[{Address}] Connecting...", $"{_bluetoothAddress:X12}");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress);
            if (_device == null)
            {
                _logger.Warning("[{Address}] BluetoothLEDevice.FromBluetoothAddressAsync returned null", $"{_bluetoothAddress:X12}");
                return;
            }

            _logger.Debug("[{Address}] Requesting all GATT services...", $"{_bluetoothAddress:X12}");
            GattDeviceServicesResult servicesResult =
                await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            _logger.Debug("[{Address}] Services result: {Status}, count: {Count}", $"{_bluetoothAddress:X12}", servicesResult.Status, servicesResult.Services.Count);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                return;

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
                return;
            }
            GattCharacteristicsResult charsResult =
                await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            _logger.Debug("[{Address}] Characteristics result: {Status}, count: {Count}", $"{_bluetoothAddress:X12}", charsResult.Status, charsResult.Characteristics.Count);
            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                return;

            _writeCharacteristic = null;
            foreach (GattCharacteristic c in charsResult.Characteristics)
            {
                _logger.Debug("[{Address}] Found characteristic: {Uuid}", $"{_bluetoothAddress:X12}", c.Uuid);
                if (c.Uuid == CharacteristicUuid)
                    _writeCharacteristic = c;
            }

            if (_writeCharacteristic == null)
            {
                _logger.Warning("[{Address}] Target characteristic {Uuid} not found", $"{_bluetoothAddress:X12}", CharacteristicUuid);
                return;
            }
            _isConnected = true;
            _logger.Information("[{Address}] Connected and ready", $"{_bluetoothAddress:X12}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Address}] Connect failed", $"{_bluetoothAddress:X12}");
            _deviceProvider.Throw(ex);
            _isConnected = false;
        }
    }

    /// <summary>
    /// Disconnects from the Govee device.
    /// </summary>
    public void Disconnect()
    {
        _isConnected = false;
        _writeCharacteristic = null;
        _device?.Dispose();
        _device = null;
    }

    /// <inheritdoc />
    protected override bool Update(ReadOnlySpan<(object key, Color color)> dataSet)
    {
        if (!_isConnected || _writeCharacteristic == null)
            return false;

        try
        {
            Color color = dataSet[0].color;
            byte[] packet = BuildColorPacket(
                (byte)(color.R * 255),
                (byte)(color.G * 255),
                (byte)(color.B * 255));

            _ = _writeCharacteristic.WriteValueAsync(packet.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            return true;
        }
        catch (Exception ex)
        {
            _deviceProvider.Throw(ex);
            return false;
        }
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
    /// Brightness range: 0x00 (off) to 0xFE (100%).
    /// </summary>
    public static byte[] BuildBrightnessPacket(byte brightness)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33;
        packet[1] = 0x04; // Brightness command
        packet[2] = brightness;
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
