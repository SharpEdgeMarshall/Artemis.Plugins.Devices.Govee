using System;

namespace Artemis.Plugins.Devices.Govee.RGB.NET.Protocols;

public interface ILightProtocol
{
    string Name { get; }
    string Manufacturer { get; }
    TimeSpan? KeepAliveInterval { get; }
    bool UsesDedicatedBrightness { get; }

    byte[] BuildColorPacket(byte r, byte g, byte b);
    byte[] BuildPowerPacket(bool on);
    byte[]? BuildBrightnessPacket(byte brightness);
    byte[]? BuildKeepAlivePacket();

    bool MatchesServiceUuid(Guid uuid);
    bool MatchesCharacteristicUuid(Guid uuid);
}

public sealed class GoveeLightProtocol : ILightProtocol
{
    public static readonly GoveeLightProtocol Instance = new();
    private static readonly Guid GoveeServiceUuid = new("00010203-0405-0607-0809-0a0b0c0d1910");
    private static readonly Guid GoveeCharacteristicUuid = new("00010203-0405-0607-0809-0a0b0c0d2b11");

    private GoveeLightProtocol() { }

    public string Name => "Govee";
    public string Manufacturer => "Govee";
    public TimeSpan? KeepAliveInterval => TimeSpan.FromSeconds(2);
    public bool UsesDedicatedBrightness => true;

    public byte[] BuildColorPacket(byte r, byte g, byte b)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33;
        packet[1] = 0x05;
        packet[2] = 0x02;
        packet[3] = r;
        packet[4] = g;
        packet[5] = b;
        packet[19] = CalculateXor(packet);
        return packet;
    }

    public byte[] BuildPowerPacket(bool on)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33;
        packet[1] = 0x01;
        packet[2] = (byte)(on ? 0x01 : 0x00);
        packet[19] = CalculateXor(packet);
        return packet;
    }

    public byte[]? BuildBrightnessPacket(byte brightness)
    {
        byte[] packet = new byte[20];
        packet[0] = 0x33;
        packet[1] = 0x04;
        packet[2] = Math.Clamp(brightness, (byte)0x01, (byte)0xFE);
        packet[19] = CalculateXor(packet);
        return packet;
    }

    public byte[]? BuildKeepAlivePacket()
    {
        byte[] packet = new byte[20];
        packet[0] = 0xAA;
        packet[1] = 0x01;
        packet[19] = CalculateXor(packet);
        return packet;
    }

    public bool MatchesServiceUuid(Guid uuid) => uuid == GoveeServiceUuid;

    public bool MatchesCharacteristicUuid(Guid uuid) => uuid == GoveeCharacteristicUuid;

    private static byte CalculateXor(byte[] packet)
    {
        byte xor = 0;
        for (int i = 0; i < packet.Length - 1; i++)
            xor ^= packet[i];
        return xor;
    }
}

public sealed class HappyLightingProtocol : ILightProtocol
{
    public static readonly HappyLightingProtocol Instance = new();
    private static readonly Guid HappyLightingServiceFfe0 = new("0000ffe0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HappyLightingServiceFfd0 = new("0000ffd0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HappyLightingServiceFfd5 = new("0000ffd5-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HappyLightingCharacteristicFfe1 = new("0000ffe1-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HappyLightingCharacteristicFfd9 = new("0000ffd9-0000-1000-8000-00805f9b34fb");

    private HappyLightingProtocol() { }

    public string Name => "HappyLighting";
    public string Manufacturer => "HappyLighting";

    public TimeSpan? KeepAliveInterval => null;
    public bool UsesDedicatedBrightness => false;

    public byte[] BuildColorPacket(byte r, byte g, byte b)
    {
        return new byte[] { 0x56, r, g, b, 0x25, 0xF0, 0xAA };
    }

    public byte[] BuildPowerPacket(bool on)
    {
        return on
            ? new byte[] { 0xCC, 0x23, 0x33 }
            : new byte[] { 0xCC, 0x24, 0x33 };
    }

    public byte[]? BuildBrightnessPacket(byte brightness)
    {
        return null;
    }

    public byte[]? BuildKeepAlivePacket()
    {
        return null;
    }

    public bool MatchesServiceUuid(Guid uuid) =>
        uuid == HappyLightingServiceFfe0 ||
        uuid == HappyLightingServiceFfd0 ||
        uuid == HappyLightingServiceFfd5;

    public bool MatchesCharacteristicUuid(Guid uuid) =>
        uuid == HappyLightingCharacteristicFfe1 || uuid == HappyLightingCharacteristicFfd9;
}
