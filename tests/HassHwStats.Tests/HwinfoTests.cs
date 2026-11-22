using System.Buffers.Binary;
using System.Text;
using HassHwStats.Core;
using HassHwStats.Sources;
using Xunit;

namespace HassHwStats.Tests;

public class HwinfoTests
{
    private const int SensorSize = 264, ReadingSize = 316;

    private static byte[] Block(string[] sensors, (uint Type, int Sensor, string Label, string Unit, double Value)[] readings, long pollTime)
    {
        int sOff = 44, rOff = sOff + sensors.Length * SensorSize;
        var b = new byte[rOff + readings.Length * ReadingSize];
        "HWiS"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(12), pollTime);
        uint[] geo = [(uint)sOff, SensorSize, (uint)sensors.Length, (uint)rOff, ReadingSize, (uint)readings.Length];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20 + 4 * i), geo[i]);
        for (int i = 0; i < sensors.Length; i++) Encoding.ASCII.GetBytes(sensors[i]).CopyTo(b, sOff + i * SensorSize + 8);
        for (int i = 0; i < readings.Length; i++)
        {
            int o = rOff + i * ReadingSize;
            var r = readings[i];
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), r.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), (uint)r.Sensor);
            Encoding.ASCII.GetBytes(r.Label).CopyTo(b, o + 12);
            Encoding.ASCII.GetBytes(r.Unit).CopyTo(b, o + 268);
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o + 284), r.Value);
        }
        return b;
    }

    [Fact]
    public void Parses_and_maps_the_wireview_sensor()
    {
        var readings = new List<(uint, int, string, string, double)>
        {
            (1, 0, "CPU Package", "°C", 70),
            (4, 1, "Total Current", "A", 30.5),
            (5, 1, "Total Power", "W", 366.0),
            (2, 1, "Average Pin Voltage", "V", 12.0),
            (1, 1, "Temperature In", "°C", 35.5),
            (1, 1, "Temperature Out", "°C", 40.1),
            (1, 1, "External Temperature 1", "°C", 28.0),
            (7, 1, "Fan Duty", "%", 40),
        };
        for (int n = 1; n <= 6; n++)
        {
            readings.Add((2, 1, $"Pin {n} Voltage", "V", 12.0));
            readings.Add((4, 1, $"Pin {n} Current", "A", n == 1 ? 8.0 : 4.5));
            readings.Add((5, 1, $"Pin {n} Power", "W", 12.0 * (n == 1 ? 8.0 : 4.5)));
        }
        for (int f = 0; f < 12; f++) readings.Add((8, 1, $"Flag {f}", "Yes/No", f == 4 ? 1 : 0));

        var block = Block(["Intel CPU", "Thermal Grizzly WireView Pro II"], readings.ToArray(), 1_790_000_000);
        Assert.Equal(block.Length, HwinfoSharedMemory.RequiredLength(block.AsSpan(0, 44)));
        var snap = HwinfoSharedMemory.Parse(block);
        Assert.NotNull(snap);
        Assert.Equal(1_790_000_000, snap.PollTime);
        Assert.Equal("Thermal Grizzly WireView Pro II", snap.Sensors[1].Name);

        var e = new WireViewEntities(new HaDevice("wireview", "WireView Pro II"));
        var values = e.MapHwinfo(snap.Readings.Where(r => r.SensorIndex == 1).ToList(), pins: true, "Connected through HWiNFO")
            .ToDictionary(v => v.Definition.Key, v => v.Value);

        Assert.Equal("Connected through HWiNFO", values["wireview_status"]);
        Assert.Equal(366.0, values["wireview_total_power"]);
        Assert.Equal(40.1, values["wireview_temp_out"]);
        Assert.Equal(28.0, values["wireview_temp_ext1"]);
        Assert.Equal(40.0, values["wireview_fan_duty"]);
        Assert.Equal(8.0, values["wireview_pin1_current"]);
        Assert.Equal(8.0, values["wireview_max_pin_current"]);
        Assert.Equal(43.75, (double)values["wireview_current_imbalance"]!, 2);
        Assert.Equal(true, values["wireview_fault"]);
        Assert.Equal("Over-power", values["wireview_active_faults"]);
        Assert.Equal("None", values["wireview_logged_faults"]);
        Assert.False(values.ContainsKey("wireview_cpu_package"));
    }

    [Fact]
    public void Rejects_bad_blocks()
    {
        var good = Block(["x"], [], 0);
        var bad = (byte[])good.Clone();
        bad[0] = (byte)'X';
        Assert.Null(HwinfoSharedMemory.Parse(bad));
        var tooMany = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooMany.AsSpan(28), 100_000_000);
        Assert.Null(HwinfoSharedMemory.Parse(tooMany));
        Assert.Null(HwinfoSharedMemory.Parse(good.AsSpan(0, 50)));
    }

    [Fact]
    public void Status_is_truncated_to_ha_limit()
    {
        var e = new WireViewEntities(new HaDevice("wireview", "WireView Pro II"));
        Assert.Equal(255, ((string)e.StatusValue(new string('x', 400)).Value!).Length);
    }
}
