using System.Diagnostics;
using HassHwStats.Sources;
using HassHwStats.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HassHwStats.Tests;

/// <summary>Reconnect behaviour against a simulated WireView whose COM port can vanish, stall or move.</summary>
public class WireViewReconnectTests
{
    /// <summary>A fake WireView Pro II Noctua Edition (vendor EF 06, firmware 5) behind a COM port.</summary>
    private sealed class FakeDevice
    {
        public string? Port = "COM5";   // null = unplugged
        public bool Stalled;            // port open but the device never answers
        public bool Wedged;             // after S3: port listed, but every open/read fails with ERROR_GEN_FAILURE
        public bool RestartFixes = true;
        public bool HandleOpen;         // a restart does nothing while a handle is open (seen on the real PC)
        public int Opens, Restarts, Cycles;

        public (bool, string) Cycle(string? port)
        {
            Cycles++;
            Wedged = false;
            return (true, "cycled");
        }

        public static IOException GenFailure() =>
            new("A device attached to the system is not functioning.") { HResult = unchecked((int)0x8007001F) };

        public (bool, string) Restart(string? port)
        {
            Restarts++;
            if (HandleOpen) return (true, "restarted (no effect: handle open)");
            if (RestartFixes) Wedged = false;
            return (true, "restarted");
        }

        public IReadOnlyList<string> PortNames() => Port is null ? [] : [Port];

        public ISerialLink Open(string name)
        {
            if (Port is null || name != Port) throw new IOException($"The port '{name}' does not exist.");
            if (Wedged) throw GenFailure();
            Opens++;
            HandleOpen = true;
            return new Link(this, name);
        }

        private sealed class Link(FakeDevice dev, string name) : ISerialLink
        {
            private readonly Queue<byte> _rx = new();
            private bool Alive => dev.Port == name;

            public void DiscardInBuffer() { if (!Alive) throw new IOException("device removed"); _rx.Clear(); }
            public bool RtsEnable { set { } }
            public void Close() => dev.HandleOpen = false;
            public void Dispose() => dev.HandleOpen = false;

            public void Write(byte[] buffer, int offset, int count)
            {
                if (!Alive) throw new IOException("device removed");
                if (dev.Stalled) return;
                byte[] reply = buffer[offset] switch
                {
                    0x01 => [0xEF, 0x06, 0x05],
                    0x02 => Enumerable.Range(1, 12).Select(i => (byte)i).ToArray(),
                    0x0D => new byte[68],
                    0x04 => WireViewProtocolTests.SampleFrame(),
                    _ => [],
                };
                foreach (var b in reply) _rx.Enqueue(b);
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                if (!Alive) throw new IOException("device removed");
                if (dev.Wedged) throw GenFailure();
                if (_rx.Count == 0) throw new TimeoutException();
                int n = 0;
                while (n < count && _rx.Count > 0) buffer[offset + n++] = _rx.Dequeue();
                return n;
            }
        }
    }

    private static (WireViewSource Source, FakeDevice Dev) Create()
    {
        var dev = new FakeDevice();
        var source = new WireViewSource(new WireViewOptions { Source = "Usb" }, NullLogger<WireViewSource>.Instance,
            dev.Open, dev.PortNames, ports => ports.FirstOrDefault(), dev.Restart, dev.Cycle);
        return (source, dev);
    }

    private static Dictionary<string, object?> Poll(WireViewSource s) => s.Poll().ToDictionary(v => v.Definition.Key, v => v.Value);

    private static Dictionary<string, object?> PollUntil(WireViewSource s, Func<Dictionary<string, object?>, bool> done, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var v = Poll(s);
            if (done(v)) return v;
            if (sw.Elapsed > timeout) Assert.Fail($"timed out; last status: {v["wireview_status"]}");
            Thread.Sleep(100);
        }
    }

    private static bool Connected(Dictionary<string, object?> v) => Equals(v["wireview_connected"], true);

    [Fact]
    public void Reconnects_when_the_port_vanishes_and_comes_back_under_a_new_number()
    {
        var (source, dev) = Create();
        source.Start();
        var v = Poll(source);
        Assert.True(Connected(v), $"status: {v["wireview_status"]}");
        Assert.Equal("Connected over USB (COM5)", v["wireview_status"]);
        Assert.Equal(162.54, (double)v["wireview_total_power"]!, 2);

        dev.Port = null; // sleep / USB reset: the COM port disappears
        v = Poll(source);
        Assert.False(Connected(v));
        Assert.StartsWith("Reconnecting.", (string)v["wireview_status"]!);
        Assert.Null(v["wireview_total_power"]);           // unavailable in HA...
        Assert.True(v.ContainsKey("wireview_pin6_current")); // ...but still listed, so HA keeps the entity

        dev.Port = "COM7"; // re-enumerated under another number
        v = PollUntil(source, Connected, TimeSpan.FromSeconds(10));
        Assert.Equal("Connected over USB (COM7)", v["wireview_status"]);
    }

    [Fact]
    public void Reconnects_when_the_device_stops_answering()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));

        dev.Stalled = true;
        Assert.True(Connected(Poll(source)));  // one slow reply is tolerated
        Assert.False(Connected(Poll(source))); // the second in a row drops the handle
        dev.Stalled = false;

        int opensBefore = dev.Opens;
        PollUntil(source, Connected, TimeSpan.FromSeconds(10));
        Assert.Equal(opensBefore + 1, dev.Opens);
    }

    [Fact]
    public void Reopens_the_port_after_wake_from_sleep()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));
        int opensBefore = dev.Opens;

        source.OnResume();
        var v = Poll(source);
        Assert.False(Connected(v));
        Assert.Contains("PC woke from sleep", (string)v["wireview_status"]!);

        PollUntil(source, Connected, TimeSpan.FromSeconds(10));
        Assert.Equal(opensBefore + 1, dev.Opens);
    }

    [Fact]
    public void Accepts_the_noctua_edition_vendor_reply()
    {
        var (source, _) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));
    }

    [Fact]
    public void Restarts_the_usb_device_when_it_is_wedged_after_wake()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));

        dev.Wedged = true; // S3 resume left usbser half-dead: COM15 listed, opens fail with error 31
        source.OnResume();
        var v = PollUntil(source, Connected, TimeSpan.FromSeconds(15));
        Assert.Equal(1, dev.Restarts);
        Assert.StartsWith("Connected over USB", (string)v["wireview_status"]!);
    }

    [Fact]
    public void Restarts_the_usb_device_on_gen_failure_without_a_wake_event()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));

        dev.Wedged = true; // reads start failing with ERROR_GEN_FAILURE
        Assert.False(Connected(Poll(source)));
        PollUntil(source, Connected, TimeSpan.FromSeconds(15));
        Assert.Equal(1, dev.Restarts);
    }

    [Fact]
    public void Restarts_at_most_once_a_minute()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));

        dev.RestartFixes = false;
        dev.Wedged = true;
        var sw = Stopwatch.StartNew();
        string? status = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            status = (string?)Poll(source)["wireview_status"];
            Thread.Sleep(100);
        }
        Assert.Equal(1, dev.Restarts);
        Assert.Equal(1, dev.Cycles); // escalated once, then rate-limited
    }

    [Fact]
    public void Power_cycles_the_hub_port_when_a_restart_does_not_help()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));

        dev.RestartFixes = false;
        dev.Wedged = true;
        source.OnResume();
        PollUntil(source, Connected, TimeSpan.FromSeconds(20));
        Assert.Equal(1, dev.Restarts);
        Assert.Equal(1, dev.Cycles);
    }

    [Fact]
    public void Closes_the_port_before_restarting_the_device()
    {
        var (source, dev) = Create();
        source.Start();
        Assert.True(Connected(Poll(source)));

        dev.Wedged = true;
        source.OnResume(); // handle is still open at this point; the restart only works once it is closed
        PollUntil(source, Connected, TimeSpan.FromSeconds(15));
        Assert.Equal(1, dev.Restarts);
        Assert.Equal(0, dev.Cycles);
    }
}
