using System.Buffers.Binary;
using HassHwStats.Sources;
using Xunit;

namespace HassHwStats.Tests;

public class WireViewProtocolTests
{
    /// <summary>Same synthetic frame as the reference Rust implementation's test (wireview-xeneon-edge serial.rs).</summary>
    internal static byte[] SampleFrame()
    {
        var b = new byte[100];
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0), 355);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), 358);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), -1000); // absent
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), -55);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 3417);
        b[10] = 40;
        for (int n = 0; n < 6; n++)
        {
            int o = 12 + 12 * n;
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), 12040);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), (uint)(2000 + 100 * n));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), (uint)(24080 + 1204 * n));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(84), 162540);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(88), 13500);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(92), 12040);
        b[94] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(96), 0b010000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(98), 0b010010);
        return b;
    }

    [Fact]
    public void Decodes_a_frame()
    {
        var r = WireViewProtocol.ParseSensors(SampleFrame());
        Assert.NotNull(r);
        Assert.Equal(6, r.Pins.Count);
        Assert.Equal(12.04, r.Pins[0].Voltage, 3);
        Assert.Equal(2.0, r.Pins[0].Current, 3);
        Assert.Equal(24.08, r.Pins[0].Power, 3);
        Assert.Equal(2.5, r.Pins[5].Current, 3);
        Assert.Equal(13.5, r.TotalCurrent, 3);
        Assert.Equal(162.54, r.TotalPower, 2);
        Assert.Equal(12.04, r.AverageVoltage, 3);
        Assert.Equal(35.5, r.TempIn, 1);
        Assert.Equal(35.8, r.TempOut, 1);
        Assert.Null(r.TempExt1);
        Assert.Equal(-5.5, r.TempExt2!.Value, 1);
        Assert.Equal(3.417, r.Vdd, 3);
        Assert.Equal(40, r.FanDuty);
        Assert.Equal(450, r.CableWatts);
        Assert.Equal("Over-power", WireViewProtocol.DescribeFaults(r.ActiveFaults));
        Assert.Equal("Sensor over-temperature, Over-power", WireViewProtocol.DescribeFaults(r.LoggedFaults));
        Assert.Equal(2.5, r.MaxPinCurrent, 3);
        Assert.Equal(20.0, r.CurrentImbalancePercent, 1);
    }

    [Theory]
    [InlineData(10, 101)]
    [InlineData(11, 1)]
    [InlineData(95, 1)]
    [InlineData(94, 4)]
    public void Rejects_desynced_frames(int index, byte value)
    {
        var f = SampleFrame();
        f[index] = value;
        Assert.Null(WireViewProtocol.ParseSensors(f));
    }

    [Fact]
    public void Rejects_short_frames() => Assert.Null(WireViewProtocol.ParseSensors(new byte[99]));

    [Fact]
    public void Vendor_check()
    {
        Assert.True(WireViewProtocol.IsVendorOk([0xEF, 0x05, 0x03]));
        Assert.True(WireViewProtocol.IsVendorOk([0xEF, 0x06, 0x05])); // Noctua Edition
        Assert.False(WireViewProtocol.IsVendorOk([0xEF, 0x07, 0x03])); // WireView II: different frame
        Assert.Equal("WireView Pro II Noctua Edition", WireViewProtocol.ModelName(0x06));
    }

    [Fact]
    public void Parses_build_string()
    {
        var b = new byte[68];
        System.Text.Encoding.ASCII.GetBytes("v03 Feb 2026").CopyTo(b, 35);
        Assert.Equal("v03 Feb 2026", WireViewProtocol.ParseBuild(b));
    }
}
