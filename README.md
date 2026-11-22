# HassHwStats

Publishes gaming-overlay-style PC stats to Home Assistant over MQTT, with auto-discovery, so every value shows up as an HA entity without any YAML.

| Source | What you get |
|---|---|
| **LibreHardwareMonitor** (built in) | CPU load, package temp, package power, core voltage, highest/average core clock. GPU core/memory/video-engine load, core/memory clocks, core/hot-spot/memory-junction temps, board power, fan RPM and %, VRAM used/free/total. RAM load and used/available. Optional: motherboard temps/fans, drive temps, network throughput. |
| **Thermal Grizzly WireView Pro II** or Noctua Edition (USB) | Total power, current and voltage on the 12V-2x6 cable; per-pin voltage/current/power (6 pins); connector input/output temps and the two external probes; fan duty; cable rating; highest pin current and current imbalance %; a Fault binary sensor plus active and logged fault names. |
| **RTSS** (RivaTuner Statistics Server, ships with MSI Afterburner) | Frame rate, frame time and the name of the game, when RTSS is running. |

In Home Assistant each piece of hardware is its own device (e.g. "Gaming-PC CPU", "Gaming-PC GPU", "WireView Pro II"), all linked to a "Gaming-PC" device. If the PC goes off, everything turns *unavailable* (MQTT last will).

## Requirements

- Windows 10/11 x64. The exe is self-contained: no .NET install needed.
- An MQTT broker that Home Assistant uses (for example the Mosquitto add-on) with the MQTT integration set up in HA.
- **PawnIO** driver for CPU temps and power: <https://pawnio.eu> (or `winget install namazso.PawnIO`). LibreHardwareMonitor uses it to read CPU registers. GPU stats work without it.
- **WireView Pro II:** connect its USB cable to a motherboard USB 2.0 header, and be on firmware v03 or later (flash it with Thermal Grizzly's WireView software once). The USB port can only be used by one program at a time, so **close the Thermal Grizzly WireView app** and **don't enable WireView in HWiNFO** while this runs. If something else holds the port, the log says so and the service keeps retrying.
- **FPS (optional):** MSI Afterburner / RTSS running.

## Install

1. Unzip somewhere, open `appsettings.json` and set at least `Mqtt.Host`, `Mqtt.Username` and `Mqtt.Password`. Optionally set `NodeName` (defaults to the computer name).
2. Right-click `install.ps1` > *Run with PowerShell* (or `powershell -ExecutionPolicy Bypass -File .\install.ps1`). It asks for admin, copies the app to `C:\Program Files\HassHwStats` and starts it.
3. In Home Assistant open **Settings > Devices & services > MQTT**. The new devices appear within a few seconds.

By default it installs a **scheduled task that starts at logon with admin rights**, hidden. That's the right choice for a gaming PC: it needs admin for the CPU sensors and has to run in your desktop session to see RTSS frame rates. If you'd rather have a real Windows service that runs before anyone logs on, use `install.ps1 -AsService`. Everything works that way except FPS.

After install, edit `C:\Program Files\HassHwStats\appsettings.json` and restart it (Task Scheduler > HassHwStats > End, then Run; or `Restart-Service HassHwStats`). Re-running `install.ps1` to upgrade keeps your settings.

To remove it: `uninstall.ps1` (add `-RemoveFiles` to delete the folder), then delete the devices in HA.

## Check what it sees

```powershell
& "C:\Program Files\HassHwStats\HassHwStats.exe" --dump      # what would be published
& "C:\Program Files\HassHwStats\HassHwStats.exe" --dump-all  # every sensor LibreHardwareMonitor finds
```

Both print the values and exit without touching MQTT. Stop the running copy first if you want the WireView to show up, because it holds the USB port. The log is `HassHwStats.log` next to the exe.

## Settings (`appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `NodeName` | computer name | Name of the PC in HA; also used in topics and entity IDs. |
| `IntervalMs` | `2000` | How often to read and publish. 1000 is fine too; HA records every change, so lower values grow the database faster. |
| `Mqtt.Host` / `Port` | `homeassistant.local` / `1883` | Broker address. |
| `Mqtt.Username` / `Password` | empty | Broker login. With the Mosquitto add-on, use an HA user (ideally a dedicated one). |
| `Mqtt.UseTls` / `AllowUntrustedCertificate` | `false` | Use TLS (usually port 8883); allow a self-signed broker certificate. |
| `Mqtt.DiscoveryPrefix` | `homeassistant` | Must match HA's MQTT discovery prefix. |
| `Mqtt.BaseTopic` | `hass-hw-stats` | Root of the state topics. |
| `Hardware.Cpu/Gpu/Memory/Motherboard/Storage/Network` | CPU, GPU, RAM on | Which hardware LibreHardwareMonitor scans. |
| `Hardware.Mode` | `Curated` | `Curated` = overlay-style set above. `All` = every sensor (hundreds). |
| `Hardware.PerCoreCpu` | `true` | Also publish load, clock and temperature for every CPU core (AMD: per-core power too). |
| `Hardware.Include` / `Exclude` | `[]` | Regexes on `Hardware name/SensorType/Sensor name`, e.g. `"Temperature/GPU Memory"` or `"/Clock/Core #1$"`. Exclude wins. See `--dump-all` for names. |
| `WireView.Enabled` | `true` | Read the WireView Pro II. |
| `WireView.Source` | `Auto` | `Auto` = USB when the port is free, otherwise HWiNFO's shared memory if HWiNFO is reading the WireView. `Usb` or `Hwinfo` to force one. |
| `WireView.Port` | empty | COM port; empty finds it by USB ID `0483:5740`. |
| `WireView.PublishPins` | `true` | Publish the 18 per-pin entities. |
| `Rtss.Enabled` | `true` | Read FPS from RTSS. |
| `WriteDashboard` | `true` | Write `dashboard.yaml` next to the exe with this PC's real entity IDs. |

Every setting can also come from an environment variable, e.g. `HASSHWSTATS_Mqtt__Password`.

## MQTT layout

- Availability: `hass-hw-stats/<node>/status` → `online` / `offline` (retained, last will).
- State: one JSON object per device every interval, e.g. `hass-hw-stats/<node>/wireview_<uid>/state` → `{"wireview_total_power":412.3,"wireview_pin1_current":8.12,...}`.
- Discovery: `homeassistant/sensor/<node>/<key>/config` (retained). Re-sent when HA publishes `online` on `homeassistant/status`.
- A value missing from a reading (unplugged WireView, sensor not present) makes just that entity *unavailable*.

## WireView not showing up?

The **WireView Pro II** device in HA has a **Status** entity that always says what's wrong, for example "no COM port with USB ID 0483:5740 (ports present: COM3, COM4)", "COM5 is in use by another program" or "COM5 opened but the WireView did not answer". The same text is in `HassHwStats.log`.

- *No COM port with that USB ID:* check Device Manager > Ports (COM & LPT). If the WireView is there under another name, put that port in `WireView.Port`. If it isn't there at all, check the internal USB cable.
- *Did not answer:* update the WireView to firmware v03+ with the Thermal Grizzly app, then close the app.
- *In use:* close the Thermal Grizzly app, or run HWiNFO 8.42+ with Shared Memory Support on and let `Auto` read it from there. (Free HWiNFO turns Shared Memory Support off after 12 hours.)

## Dashboard

Once it's running, HassHwStats writes **`dashboard.yaml`** next to the exe (`C:\Program Files\HassHwStats\dashboard.yaml`) using the entity IDs this PC actually publishes. It has three views: *Overview* (FPS, CPU/GPU/12V-2x6 gauges, GPU/CPU/RAM details), *CPU cores* (load, clock and temperature per core, plus history graphs) and *WireView* (status, faults, pin-current gauges, history).

To use it: HA **Settings > Dashboards > Add dashboard > New dashboard from scratch**, open it, pencil > three dots > **Raw configuration editor**, paste the file, **Save**. The file is rewritten when new sensors appear, so paste it again after adding hardware. `dashboard-example.yaml` in the zip is a sample from another PC.

Useful automation: notify when `binary_sensor.wireview_pro_ii_fault` turns on, or when *Highest pin current* stays above ~8.5 A or *Current imbalance* above 40% under load.

## Releases

GitHub Actions (`.github/workflows/build.yml`) runs the tests and builds the win-x64 zip on every push and pull request (download it from the run's artifacts). Pushing a tag like `v0.3.0` also creates a GitHub release with the zip attached:

```
git tag v0.3.0 && git push origin v0.3.0
```

## Build from source

Needs the .NET 10 SDK. Builds on Windows, Linux or macOS.

```
dotnet test tests/HassHwStats.Tests            # protocol + payload tests; MQTT test runs if a broker is on localhost:1883
dotnet publish src/HassHwStats -c Release -o publish/win-x64
```

## Credits

The WireView Pro II USB protocol (115200 8N1, command `0x04` returns a 100-byte sensor struct) comes from the community's notes at [Gustav0ar/wireview-pro-ii](https://github.com/Gustav0ar/wireview-pro-ii/blob/main/docs/protocol.md) and [jlobue10/wireview-xeneon-edge](https://github.com/jlobue10/wireview-xeneon-edge). It is unofficial; a future firmware could change it. CPU/GPU sensors use [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0); MQTT uses [MQTTnet](https://github.com/dotnet/MQTTnet) (MIT).
