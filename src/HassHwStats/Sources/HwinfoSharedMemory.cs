using System.Buffers.Binary;
using System.Text;

namespace HassHwStats.Sources;

/// <summary>
/// Parser for HWiNFO's sensor shared memory (Global\HWiNFO_SENS_SM2, "Shared Memory Support" in HWiNFO settings).
/// Layout is pack(1), little endian. Every offset and count is checked against the snapshot before use.
/// </summary>
public static class HwinfoSharedMemory
{
    public const string MappingName = @"Global\HWiNFO_SENS_SM2";
    public const int HeaderSize = 44;
    private const int SensorMinSize = 8 + 128 + 128;
    private const int ReadingMinSize = 284 + 4 * 8;
    private const int MaxRecordSize = 64 * 1024;
    private const int MaxSensors = 4096;
    private const int MaxReadings = 65536;

    public enum ReadingType : uint { None = 0, Temperature = 1, Voltage = 2, Fan = 3, Current = 4, Power = 5, Clock = 6, Usage = 7, Other = 8 }

    public sealed record Sensor(uint Id, uint Instance, string Name, string UserName);
    public sealed record Reading(ReadingType Type, int SensorIndex, uint Id, string Label, string UserLabel, string Unit, double Value);
    public sealed record Snapshot(long PollTime, IReadOnlyList<Sensor> Sensors, IReadOnlyList<Reading> Readings);

    /// <summary>Header geometry: where the arrays are and how long the whole block is.</summary>
    public static (int SensorOffset, int SensorSize, int SensorCount, int ReadingOffset, int ReadingSize, int ReadingCount)? Geometry(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..4].SequenceEqual("HWiS"u8)) return null;
        var f = header.Slice(20, 24);
        uint sOff = BinaryPrimitives.ReadUInt32LittleEndian(f), sSz = BinaryPrimitives.ReadUInt32LittleEndian(f[4..]),
             sN = BinaryPrimitives.ReadUInt32LittleEndian(f[8..]), rOff = BinaryPrimitives.ReadUInt32LittleEndian(f[12..]),
             rSz = BinaryPrimitives.ReadUInt32LittleEndian(f[16..]), rN = BinaryPrimitives.ReadUInt32LittleEndian(f[20..]);
        if (sSz is < SensorMinSize or > MaxRecordSize || rSz is < ReadingMinSize or > MaxRecordSize) return null;
        if (sN > MaxSensors || rN > MaxReadings) return null;
        if (sOff > int.MaxValue || rOff > int.MaxValue) return null;
        return ((int)sOff, (int)sSz, (int)sN, (int)rOff, (int)rSz, (int)rN);
    }

    /// <summary>Total bytes needed to hold the header and both arrays.</summary>
    public static long RequiredLength(ReadOnlySpan<byte> header)
    {
        var g = Geometry(header);
        if (g is null) return -1;
        var (so, ss, sn, ro, rs, rn) = g.Value;
        return Math.Max(HeaderSize, Math.Max((long)so + (long)ss * sn, (long)ro + (long)rs * rn));
    }

    public static Snapshot? Parse(ReadOnlySpan<byte> block)
    {
        var g = Geometry(block);
        if (g is null) return null;
        var (so, ss, sn, ro, rs, rn) = g.Value;
        if ((long)so + (long)ss * sn > block.Length || (long)ro + (long)rs * rn > block.Length) return null;

        long pollTime = BinaryPrimitives.ReadInt64LittleEndian(block[12..]);
        var sensors = new List<Sensor>(sn);
        for (int i = 0; i < sn; i++)
        {
            var b = block.Slice(so + i * ss, ss);
            sensors.Add(new Sensor(
                BinaryPrimitives.ReadUInt32LittleEndian(b),
                BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
                CString(b.Slice(8, 128)),
                CString(b.Slice(136, 128))));
        }
        var readings = new List<Reading>(rn);
        for (int i = 0; i < rn; i++)
        {
            var b = block.Slice(ro + i * rs, rs);
            readings.Add(new Reading(
                (ReadingType)BinaryPrimitives.ReadUInt32LittleEndian(b),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
                CString(b.Slice(12, 128)),
                CString(b.Slice(140, 128)),
                CString(b.Slice(268, 16)),
                BinaryPrimitives.ReadDoubleLittleEndian(b[284..])));
        }
        return new Snapshot(pollTime, sensors, readings);
    }

    private static string CString(ReadOnlySpan<byte> s)
    {
        int nul = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? s : s[..nul]);
    }
}
