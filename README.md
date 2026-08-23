# MissionPlanner Avalonia

[![CI](https://github.com/Rouniy/MissionPlanner-Avalonia/actions/workflows/ci.yml/badge.svg)](https://github.com/Rouniy/MissionPlanner-Avalonia/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Rouniy/MissionPlanner-Avalonia/actions/workflows/codeql.yml/badge.svg)](https://github.com/Rouniy/MissionPlanner-Avalonia/actions/workflows/codeql.yml)

A native, cross-platform (macOS / Linux / Windows) port of the **ArduPilot Mission Planner** UI,
built with **Avalonia (.NET 10)**. The original is Windows-only WinForms; this rebuilds the interface
while **reusing Mission Planner's flight / protocol / log / param / mission logic unchanged**.

> Independent community port — **not** affiliated with or endorsed by ArduPilot. Based on
> [Mission Planner](https://github.com/ArduPilot/MissionPlanner) (© Michael Oborne), GPLv3. See `NOTICE.md`.

## Project and upstream links

- Avalonia port: [releases](https://github.com/Rouniy/MissionPlanner-Avalonia/releases) and
  [issue tracker](https://github.com/Rouniy/MissionPlanner-Avalonia/issues).
- Official Mission Planner: [website and documentation](https://ardupilot.org/planner/),
  [support forum](https://discuss.ardupilot.org/c/ground-control-software/mission-planner),
  [source](https://github.com/ArduPilot/MissionPlanner) and
  [changelog](https://github.com/ArduPilot/MissionPlanner/blob/master/ChangeLog.txt).
- The official [Windows MSI](https://firmware.ardupilot.org/Tools/MissionPlanner/MissionPlanner-latest.msi)
  belongs to upstream Mission Planner; it is not an installer for this Avalonia port.

Please report Avalonia/Linux/macOS/Windows port problems in this repository. General ArduPilot or
official Mission Planner usage questions belong in the ArduPilot documentation and forum above.

The upstream source is pinned as the `external/MissionPlanner` submodule. The current integration
baseline is Mission Planner commit `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`.

The application version is copied automatically from that pinned MissionPlanner source and then
extended with the UTC build date and the port commit. For example, official MissionPlanner
`1.3.83` produces `1.3.83+20260821.8a07b1b`; a package made from uncommitted local changes adds
`.dirty`. The composite version appears in the window title and Help page and is shared by release
archives and update manifests. Filesystem/release names use the GitHub-safe equivalent
`1.3.83-20260821.8a07b1b`, and release tags add a leading `v`. Debian control metadata adds the
epoch `1:` plus a monotonically ordered port revision so
APT correctly upgrades installations that used the port's older `2026.8.0` CalVer.
Signed beta tags append `-beta` or `-beta.N`; they are published as GitHub prereleases and are
discovered by the enabled Beta Updates preference without replacing the stable GitHub Pages feed.

See [port status](docs/PORT_STATUS.md) for the Windows/macOS/Linux support matrix, upstream
synchronization details, and the explicit list of missing or intentionally disabled functionality.
The native [portable plugin host](docs/PLUGINS.md) documents the official-style plugin lifecycle,
installation paths, Avalonia extension API, binary compatibility for non-visual legacy plugins and
the remaining WinForms UI boundary.

## Platform targets

Release automation builds self-contained artifacts for Windows x64, macOS x64, macOS ARM64 and
Linux x64. Linux x64 is the platform verified locally in this synchronization; Windows and macOS
remain first-class targets and are built by the cross-platform CI/release workflows.

Set `RID` to the required runtime identifier: `win-x64`, `osx-x64`, `osx-arm64` or `linux-x64`.

```bash
RID=linux-x64
dotnet publish src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj \
  -c Release -r "$RID" --self-contained true -m:1 -p:DebugType=none \
  -o "out/$RID"
```

Both macOS artifacts bundle an architecture-matched VLC 3.0.23 runtime from the corresponding
official VideoLAN application image. The ARM64 artifact and its video pipeline run natively on
Apple Silicon; the x64 artifact remains available for Intel Macs and Rosetta 2. Exact source URLs,
hashes and licenses are recorded in `LICENSES/VLC-3.0.23-NOTICE.txt`. A non-macOS host needs `curl`,
`file` and `7z` when cross-publishing either macOS RID so the pinned official DMG can be verified and
extracted.

Joystick input uses the upstream DirectInput backend on Windows, a port-native joydev backend on
Linux and a native IOKit HID backend on macOS. All three feed the same mapping UI and target-safe
RC/manual-control sender; physical controller acceptance is tracked separately in the port-status
document.

Nordic-UART Bluetooth LE connections use managed BlueZ/D-Bus on Linux and the pinned SimpleBLE
0.7.3 native ABI on Windows and macOS. Windows keeps its upstream SimpleBLE DLLs; checksummed x64
and arm64 macOS dylibs are fetched from the official SimpleBLE release during publish and bundled
with the corresponding artifact. Discovery, connection and I/O are cancellable and bounded;
end-to-end traffic with representative BLE modems remains a native-platform acceptance item.

Setup > NV Modem is a native port of AgroSky GTU's `NV5Settings` for NV4/NV5 radio modems. It
discovers modems and performs parameter, key, RTSP and maintenance operations through the UDP/TCP/
UART MAVLink connections already open in Mission Planner; it never opens a second port. Parameter
values are session-only and cleared on refresh or device change, while the copied parameter
descriptions remain available in the tab. See [NV Modem](docs/NV_MODEM.md).

## Linux prerequisites

Ubuntu 24.04 / Linux Mint 22 can use the distribution SDK. `global.json` accepts the 10.0.100
feature band and rolls forward, so the distro-provided 10.0.111 SDK is sufficient; a manually
installed 10.0.301 SDK is not required.

```bash
sudo apt-get update
sudo apt-get install dotnet-sdk-10.0 libvlc5 vlc-plugin-base speech-dispatcher-espeak-ng bluez
# Optional official-style GDAL Custom local raster map provider:
sudo apt-get install gdal-bin
sudo usermod -aG dialout "$USER"
```

Log out and back in after adding `dialout`. `libvlc` is needed for video,
`speech-dispatcher-espeak-ng` for spoken warnings and BlueZ for Linux Nordic-UART Bluetooth LE
connections. A current system GDAL runtime enables the optional `GDAL Custom` map provider; the
managed GeoTIFF/DTED elevation path does not require it. Add `xvfb` for headless GUI smoke tests and
`dotnet-sdk-aot-10.0` only when experimenting with NativeAOT.

Video sources may be direct files or libVLC MRLs such as `rtsp://host/path`, `udp://@:5600`,
`rtp://@:5600` and `v4l2:///dev/video0`. The input dialog also accepts an RTP GStreamer pipeline
containing `udpsrc port=...`, `application/x-rtp` and H.264/H.265 depayloading; the port converts it
to a temporary SDP file for libVLC. A bare non-RTP GStreamer pipeline is not interchangeable with a
libVLC MRL and is rejected with an explanatory message.

## Build & run

Mission Planner's reusable libraries come in as one top-level git submodule. Its historical nested
Mono submodule is not needed by this build.

```bash
git clone https://github.com/Rouniy/MissionPlanner-Avalonia.git
cd MissionPlanner-Avalonia
git submodule update --init external/MissionPlanner
dotnet restore src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj -m:1
dotnet run --project src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj -m:1
```

`-m:1` avoids an intermittent MSBuild task-host failure seen in the large upstream project graph.

## Self-contained artifact details

For the `linux-x64` publish example above, launch `./out/linux-x64/MissionPlannerAvalonia`.

The launcher and native libraries are ELF files. The `.dll` files beside them are normal managed
.NET assemblies (portable ECMA-335 bytecode), not Windows native libraries. Windows-only native
`simpleble*.dll` and `libusb-1.0.dll` files inherited from upstream are explicitly removed from
Linux and macOS publish output, while `win-x64` retains the SimpleBLE runtime it needs.

NativeAOT can be produced experimentally with `-p:EnableNativeAot=true`, but it is not a supported
release mode on any target: upstream log4net and several reflection/serialization paths are not
NativeAOT-compatible. Use the self-contained CoreCLR artifacts for operational builds.

## Linux packages

One target publishes the application once and creates both a portable archive and a Debian package:

```bash
make linux-packages
# Individual formats:
make linux-tar
make linux-deb
```

Artifacts are written to `out/packages/`. The `.deb` is intended for Ubuntu 24.04 / Linux Mint 22
and compatible amd64 distributions. It is self-contained, so a .NET runtime is not required on the
target machine. APT installs the native GUI, ICU, OpenSSL and libVLC dependencies declared by the
package; `speech-dispatcher-espeak-ng` and `bluez` are recommended for spoken warnings and optional
BLE connections respectively, while `gdal-bin` is suggested for local GDAL raster maps.

```bash
sudo apt install ./out/packages/missionplanner-avalonia_*.deb
missionplanner-avalonia
```

The Debian package installs the application under `/usr/lib/missionplanner-avalonia`, adds a desktop
entry and exposes `/usr/bin/missionplanner-avalonia`. Package-managed installs do not overwrite
themselves with the in-app updater; update them through APT. The portable `tar.gz` keeps the signed
in-app updater and includes `install.sh` for a per-user desktop entry. Portable Linux, Windows and
macOS builds can opt into signed prereleases with Setup > Planner > Beta Updates or check the beta
channel directly from Help. Both stable and beta manifests use Ed25519 signatures; downloaded beta
bundles are additionally pinned by SHA-256 before extraction.

## Runtime files

The application does not write settings or downloaded data beside the executable. Paths follow the
native convention on every supported platform:

| Purpose | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Configuration | `$XDG_CONFIG_HOME/MissionPlannerAvalonia` (default `~/.config`) | `%APPDATA%\MissionPlannerAvalonia` | `~/Library/Application Support/MissionPlannerAvalonia` |
| Logs and user data | `$XDG_DATA_HOME/MissionPlannerAvalonia` (default `~/.local/share`) | `%LOCALAPPDATA%\MissionPlannerAvalonia` | `~/Library/Application Support/MissionPlannerAvalonia` |
| Download/cache data | `$XDG_CACHE_HOME/MissionPlannerAvalonia` (default `~/.cache`) | `%LOCALAPPDATA%\MissionPlannerAvalonia\cache` | `~/Library/Caches/MissionPlannerAvalonia` |
| Crash/state files | `$XDG_STATE_HOME/MissionPlannerAvalonia` (default `~/.local/state`) | `%LOCALAPPDATA%\MissionPlannerAvalonia\state` | `~/Library/Logs/MissionPlannerAvalonia` |

SRTM terrain, SITL binaries, parameter definitions, log metadata and updater downloads are caches.
Existing files from legacy `Mission Planner` folders are copied on first use without deleting the
old copy.

Portable plugin DLLs live under the user-data `plugins` directory and can be managed from
**Tools > Plugin Manager** (`Ctrl+P`). They run as trusted in-process code with full application
access; install only plugins you trust. See [portable plugins](docs/PLUGINS.md) for the API and
platform paths.

The official TerrainMakerPlugin is built into Flight Planner as **Make Terrain DAT…**. It creates
ArduPilot whole-degree DAT tiles from the visible map area using the configured local DEM/SRTM
sources, reports the exact estimated output size, supports cancellation and atomically replaces only
complete tiles.

## Network services and privacy

The port keeps the useful network integrations described by official Mission Planner, but only for
features that are present here. Analytics and the upstream Cloudflare geolocation request are not
implemented. The main runtime network destinations are:

| Service | Purpose | When it is contacted |
| --- | --- | --- |
| GitHub and the port's GitHub Pages feed | Signed stable/beta application updates | Portable builds check at startup; manual checks are in Help. Package-managed installs do not self-update. |
| `firmware.ardupilot.org`, ArduPilot GitHub/raw content | Vehicle firmware manifests and files, parameter/frame defaults and SITL assets | When a connected firmware version is checked or the operator starts the corresponding download/tool. |
| Google, Bing, OpenStreetMap, Esri or an operator-supplied WMS/WMTS server | Map imagery | Only for the selected online map provider; cached/local maps remain available offline. |
| Hong Kong CAD eSUA | Official Hong Kong no-fly polygons | Only when **Show NoFly** and the separate Hong Kong option are enabled; results use a 12-hour disk cache. |
| NOAA SWPC | K-index space-weather value | A bounded background refresh at application startup. |
| `adsb.lol` | Optional external ADS-B traffic | Only while the external ADS-B receiver is enabled. |
| ArduPilot documentation, forum, RFDesign or CubePilot | Help pages and optional vendor firmware | Only after an explicit user action, except the connected-firmware availability check noted above. |

Telemetry links configured by the operator (UDP, TCP, serial, Bluetooth, NTRIP, video and similar)
necessarily contact their configured device or server. Portable plugins are trusted in-process code
and may add their own network behavior.

## Offline use

Vehicle connections, cached maps, mission editing, parameter work against a connected vehicle, log
review and local terrain sources do not require Internet access. Data that can be prepared on one
machine and copied to the matching runtime directory includes:

| Data | Offline support |
| --- | --- |
| Map tiles | Use the map cache manager or copy the cache directory. WMS/WMTS and online basemaps need prior cached tiles. |
| Elevation | Cached SRTM, local WGS84/EGM96 GeoTIFF and DTED are supported; local sources take priority. |
| Parameter and log metadata | Bundled fallbacks are included; newer downloaded definitions remain in the cache. Parameter *values* are deliberately never restored from a previous device/session. |
| No-fly zones | Local KML/KMZ files under the user-data `NoFly` directory work offline; the last Hong Kong eSUA response can be used from cache. |
| Firmware and SITL | Previously downloaded cache content can be reused; discovering or downloading new versions requires a network connection. |

Unlike the official Windows MSI, this port does not install a serial-driver CA certificate. Linux
serial access uses the operating system's device permissions; Windows drivers remain an operating-
system/vendor responsibility.

## Development

```bash
dotnet format whitespace src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj --verify-no-changes --exclude external/
dotnet format style src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj --verify-no-changes --exclude external/
dotnet build tests/MissionPlannerAvalonia.Tests/MissionPlannerAvalonia.Tests.csproj \
  -t:Rebuild -c Release -m:1 --no-restore
dotnet test tests/MissionPlannerAvalonia.Tests/MissionPlannerAvalonia.Tests.csproj -c Release -m:1
```

The rebuild is expected to report zero warnings. The official MissionPlanner submodule remains
unmodified; its known legacy/generated-source diagnostics are isolated in `external/Directory.Build.props`,
while warnings in this repository's `src` and `tests` projects remain enabled.

## License

**GPLv3** (see `LICENSE`). This is a derivative work of Mission Planner and inherits its license.
