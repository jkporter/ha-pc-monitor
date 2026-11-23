using HassHwStats.Core;
using HassHwStats.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HassHwStats.Tests;

public class RtssSourceTests
{
    [Fact]
    public void Reports_rtss_missing_and_makes_fps_entities_unavailable()
    {
        if (OperatingSystem.IsWindows()) return; // on Windows the result depends on whether RTSS happens to run
        var source = new RtssSource(new HaDevice("pc", "Test Rig"), NullLogger<RtssSource>.Instance);
        source.Start();
        var v = source.Poll().ToDictionary(x => x.Definition.Key, x => x.Value);
        Assert.StartsWith("RTSS not running", (string)v["rtss_status"]!);
        Assert.Null(v["fps"]);
        Assert.Null(v["frame_time"]);
        Assert.Null(v["game"]);
    }
}
