using System.Reflection;
using HassHwStats;
using HassHwStats.Core;
using HassHwStats.Mqtt;
using HassHwStats.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
bool dump = args.Contains("--dump") || args.Contains("--dump-all");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(a => !a.StartsWith("--dump")).ToArray(),
    ContentRootPath = AppContext.BaseDirectory, // services and scheduled tasks start in System32
});
builder.Configuration.AddEnvironmentVariables("HASSHWSTATS_");

var options = builder.Configuration.Get<AppOptions>() ?? new AppOptions();
if (args.Contains("--dump-all")) options.Hardware.Mode = "All";
var nodeName = string.IsNullOrWhiteSpace(options.NodeName) ? Environment.MachineName : options.NodeName.Trim();

builder.Services.AddWindowsService(o => o.ServiceName = "HassHwStats");
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "HassHwStats.log"), LogLevel.Information));

var pcDevice = new HaDevice("pc", nodeName, Environment.OSVersion.VersionString, null, version);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(sp =>
{
    var lf = sp.GetRequiredService<ILoggerFactory>();
    var list = new List<ISensorSource>();
    if (options.Hardware.Enabled) list.Add(new HardwareMonitorSource(options.Hardware, nodeName, lf.CreateLogger<HardwareMonitorSource>()));
    if (options.WireView.Enabled) list.Add(new WireViewSource(options.WireView, lf.CreateLogger<WireViewSource>()));
    if (options.Rtss.Enabled) list.Add(new RtssSource(pcDevice, lf.CreateLogger<RtssSource>()));
    return (IReadOnlyList<ISensorSource>)list;
});
builder.Services.AddSingleton(new HomeAssistantPayloads(options.Mqtt.DiscoveryPrefix, options.Mqtt.BaseTopic, nodeName, pcDevice, version));
builder.Services.AddSingleton(sp => new MqttPublisher(options.Mqtt, sp.GetRequiredService<HomeAssistantPayloads>(), sp.GetRequiredService<ILogger<MqttPublisher>>()));
builder.Services.AddHostedService<Worker>();

using var host = builder.Build();

if (dump)
{
    bool ownConsole = ConsoleHelper.Ensure();
    // Print what would be published, without touching MQTT. Handy for writing Include/Exclude rules.
    var log = host.Services.GetRequiredService<ILogger<Worker>>();
    var sources = Worker.StartSources(host.Services.GetRequiredService<IReadOnlyList<ISensorSource>>(), log);
    Worker.PollAll(sources, log); // first LHM read primes some sensors
    Thread.Sleep(1000);
    foreach (var g in Worker.PollAll(sources, log).GroupBy(v => v.Definition.Device.Name))
    {
        Console.WriteLine($"== {g.Key}");
        foreach (var v in g)
            Console.WriteLine($"  {v.Definition.Key,-48} {v.Definition.Name,-40} {v.Value,12} {v.Definition.Unit}");
    }
    foreach (var s in sources) s.Dispose();
    if (ownConsole)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
    return;
}

await host.RunAsync();
