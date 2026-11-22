using System.Buffers.Binary;

namespace HassHwStats.Sources;

/// <summary>
/// Thermal Grizzly WireView Pro II USB serial protocol (USB CDC/ACM 0483:5740, 115200 8N1).
/// Layout per the community protocol notes at https://github.com/Gustav0ar/wireview-pro-ii (docs/protocol.md).
/// Commands are single raw bytes; the device answers with a fixed-size, little-endian, pack-4 struct.
/// </summary>
public static class WireViewProtocol
{
    public const ushort UsbVid = 0x0483;
    public const ushort UsbPid = 0x5740;
    public const int BaudRate = 115200;

    public const string Greeting = "Thermal Grizzly WireView Pro II";

    public static readonly byte[] CmdVendor = [0x01];       // -> 3 bytes: EF 05 <fw>
    public static readonly byte[] CmdUid = [0x02];          // -> 12 bytes
    public static readonly byte[] CmdSensors = [0x04];      // -> 100 bytes
    public static readonly byte[] CmdBuild = [0x0D];        // -> 68 bytes
    public static readonly byte[] CmdScreenResume = [0x0C, 0xF1];

    public const int VendorSize = 3;
    public const int UidSize = 12;
    public const int SensorSize = 100;
    public const int BuildSize = 68;

    private const short ExtTempAbsent = -1000; // -100.0 °C

    public static readonly int[] CableWatts = [600, 450, 300, 150];

    public static readonly string[] FaultNames =
    [
        "Chip over-temperature",
        "Sensor over-temperature",
        "Over-current",
        "Per-wire over-current",
        "Over-power",
        "Current imbalance",
    ];

    public const byte VendorThermalGrizzly = 0xEF;
    public const byte ProductPro2 = 0x05;
    public const byte ProductPro2Noctua = 0x06; // same protocol, frame and config

    public static bool IsVendorOk(ReadOnlySpan<byte> vendor) =>
        vendor.Length == VendorSize && vendor[0] == VendorThermalGrizzly && vendor[1] is ProductPro2 or ProductPro2Noctua;

    public static string ModelName(byte productId) =>
        productId == ProductPro2Noctua ? "WireView Pro II Noctua Edition" : "WireView Pro II";

    /// <summary>The build-information string from a <see cref="CmdBuild"/> reply.</summary>
    public static string ParseBuild(ReadOnlySpan<byte> b)
    {
        if (b.Length < BuildSize) return "";
        var s = b.Slice(35, 32);
        int nul = s.IndexOf((byte)0);
        if (nul >= 0) s = s[..nul];
        return System.Text.Encoding.Latin1.GetString(s);
    }

    /// <summary>Decode a 100-byte sensor frame. Returns null if the frame is the wrong size or fails the plausibility checks (desync).</summary>
    public static WireViewReading? ParseSensors(ReadOnlySpan<byte> b)
    {
        if (b.Length != SensorSize) return null;
        // There is no framing or checksum; reject obviously misaligned data like the reference implementations.
        if (b[10] > 100 || b[11] != 0 || b[95] != 0) return null;
        if (b[94] >= CableWatts.Length) return null;

        static double? ExtTemp(short raw) => raw == ExtTempAbsent ? null : raw / 10.0;

        var pins = new WireViewPin[6];
        for (int i = 0; i < 6; i++)
        {
            int o = 12 + 12 * i;
            double v = BinaryPrimitives.ReadInt16LittleEndian(b[o..]) / 1000.0;
            double a = BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 4)..]) / 1000.0;
            pins[i] = new WireViewPin(i + 1, v, a, v * a);
        }

        double totalCurrent = pins.Sum(p => p.Current);
        double totalPower = pins.Sum(p => p.Power);

        return new WireViewReading(
            TempIn: BinaryPrimitives.ReadInt16LittleEndian(b) / 10.0,
            TempOut: BinaryPrimitives.ReadInt16LittleEndian(b[2..]) / 10.0,
            TempExt1: ExtTemp(BinaryPrimitives.ReadInt16LittleEndian(b[4..])),
            TempExt2: ExtTemp(BinaryPrimitives.ReadInt16LittleEndian(b[6..])),
            Vdd: BinaryPrimitives.ReadUInt16LittleEndian(b[8..]) / 1000.0,
            FanDuty: b[10],
            Pins: pins,
            TotalCurrent: totalCurrent,
            TotalPower: totalPower,
            AverageVoltage: BinaryPrimitives.ReadUInt16LittleEndian(b[92..]) / 1000.0,
            CableWatts: CableWatts[b[94]],
            ActiveFaults: BinaryPrimitives.ReadUInt16LittleEndian(b[96..]),
            LoggedFaults: BinaryPrimitives.ReadUInt16LittleEndian(b[98..]));
    }

    public static string DescribeFaults(ushort mask)
    {
        if (mask == 0) return "None";
        var names = new List<string>();
        for (int bit = 0; bit < 16; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            names.Add(bit < FaultNames.Length ? FaultNames[bit] : $"Unknown fault {bit}");
        }
        return string.Join(", ", names);
    }
}

public sealed record WireViewPin(int Number, double Voltage, double Current, double Power);

public sealed record WireViewReading(
    double TempIn,
    double TempOut,
    double? TempExt1,
    double? TempExt2,
    double Vdd,
    int FanDuty,
    IReadOnlyList<WireViewPin> Pins,
    double TotalCurrent,
    double TotalPower,
    double AverageVoltage,
    int CableWatts,
    ushort ActiveFaults,
    ushort LoggedFaults)
{
    public double MaxPinCurrent => Pins.Max(p => p.Current);
    public double MinPinCurrent => Pins.Min(p => p.Current);

    /// <summary>(max - min) / max pin current, in percent. Same idea as the device's imbalance alarm.</summary>
    public double CurrentImbalancePercent => MaxPinCurrent <= 0.05 ? 0 : (MaxPinCurrent - MinPinCurrent) / MaxPinCurrent * 100.0;
}
