using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabletMonitor.Net;

/// <summary>Wire protocol shared with the Android app – see docs/PROTOCOL.md.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int TcpPort = 47800;
    public const int DiscoveryPort = 47801;
    public static readonly byte[] DiscoveryMagic = "TMD1"u8.ToArray();

    public const byte DiscoveryProbe = 1;
    public const byte DiscoveryInfo = 2;
    public const byte DiscoveryConnectRequest = 3;

    // PC → tablet
    public const byte Hello = 1;
    public const byte VideoConfig = 2;
    public const byte VideoFrame = 3;
    public const byte CursorShape = 4;
    public const byte CursorPos = 5;
    public const byte Ping = 6;
    public const byte Bye = 7;

    // tablet → PC
    public const byte Pointers = 10;
    public const byte RequestKeyFrame = 11;
    public const byte Pong = 12;
    public const byte ViewSize = 13;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Frame(byte type, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[5 + payload.Length];
        buffer[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), payload.Length);
        payload.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    public static byte[] JsonFrame<T>(byte type, T value) =>
        Frame(type, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public static byte[] DiscoveryPacket(byte type, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return [.. DiscoveryMagic, type, .. json];
    }
}

/// <summary>Information a tablet reports about itself.</summary>
public sealed record DeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Refresh { get; init; } = 60;
    public int Dpi { get; init; } = 160;
    public int Port { get; init; } = Protocol.TcpPort;
    public int Version { get; init; }
    public bool Busy { get; init; }
}

public enum PointerState : byte { Down = 0, Move = 1, Up = 2, Cancel = 3, Hover = 4, HoverExit = 5 }
public enum PointerTool : byte { Finger = 0, Stylus = 1, Eraser = 2 }

public readonly record struct PointerData(byte Id, PointerState State, PointerTool Tool, bool Barrel, float X, float Y, float Pressure);

public static class PointerParser
{
    public static PointerData[] Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return [];
        var count = payload[0];
        var result = new PointerData[count];
        var p = payload[1..];
        for (var i = 0; i < count && p.Length >= 16; i++, p = p[16..])
        {
            result[i] = new PointerData(
                p[0], (PointerState)p[1], (PointerTool)p[2], (p[3] & 1) != 0,
                BinaryPrimitives.ReadSingleBigEndian(p[4..]),
                BinaryPrimitives.ReadSingleBigEndian(p[8..]),
                BinaryPrimitives.ReadSingleBigEndian(p[12..]));
        }
        return result;
    }
}

internal static class Text
{
    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
