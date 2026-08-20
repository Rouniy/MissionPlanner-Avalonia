# Cross-platform port status

The project targets Windows, macOS and Linux. The current synchronization was locally verified on
Linux Mint 22.3 (Ubuntu 24.04 base), x86-64, X11 on 2026-08-21. Self-contained Windows x64 and
macOS x64 outputs were also cross-published and inspected on Linux. Windows and macOS remain
first-class release targets and still require runtime acceptance on their native runners.

## Platform matrix

| Target | Packaging | Current verification |
| --- | --- | --- |
| Windows x64 (`win-x64`) | Self-contained folder, PE apphost; bundled libVLC runtime | Cross-publish passed and PE32+ executable inspected; native Windows execution pending |
| macOS x64 (`osx-x64`) | Self-contained `.app`, Mach-O/dylibs; bundled libVLC; CI signing/notarization when credentials are configured | Cross-publish passed; native macOS execution pending. Runs on Apple Silicon through Rosetta 2 |
| Linux x64 (`linux-x64`) | Self-contained ELF/CoreCLR `tar.gz` and FHS-compliant amd64 `.deb` with native dependencies | Current source: Release build and 104 tests verified; final package verification is recorded below |

Speech is implemented per platform: Windows uses `System.Speech` through PowerShell, macOS uses
`say`, and Linux uses `speech-dispatcher` (`spd-say`, with a Festival fallback).

## Upstream baseline

This port is concretely based on a Mission Planner git commit, not on an approximate source copy.
The `external/MissionPlanner` gitlink was originally pinned to:

- `14840eb0cd56b6ad824e05475383484d3213678f` (2026-06-19)

The integration branch now pins:

- `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`

That advances the reusable upstream source by ten commits. All changes in referenced ArduPilot,
MAVLink, DroneCAN, DFLog, LogSort and location libraries are compiled directly from the updated
submodule. UI-only changes were translated to Avalonia where applicable:

| Upstream change | Cross-platform port result |
| --- | --- |
| DroneCAN parameter enumeration fix | Compiled directly from upstream |
| MAVLink null handling, CurrentState messages, Locationwp fixes | Compiled directly from upstream |
| DFLog bad/unnamed cache and large `timems` fixes | Compiled directly from upstream |
| Log sorting: `.tlog`, `.rlog`, `.bin`, `.log`, HIL flag | Upstream sorter plus a new Avalonia action in Flight Data |
| Plugin custom flight action hook | Avalonia `RegisterCustomAction` / `UnregisterCustomAction` API added |
| Plugin custom HUD drawing hook | Avalonia HUD `CustomPaint` event added |
| Embedded HTTP server hardening | Server is absent from the Avalonia port; no older vulnerable server was reintroduced |

## Features restored during this synchronization

- MAVLink signing key management: add/use/delete/disable.
- Warning engine startup, cross-platform speech adapter and warning editor.
- FFT log analysis window.
- DataFlash spectrogram for ACC/GYR sensors 1–5 with X/Y/Z plots.
- Proximity radar for `DISTANCE_SENSOR` / `OBSTACLE_DISTANCE` telemetry.
- Parameter metadata regeneration from current ArduPilot definitions.
- Log organization for `.tlog`, `.rlog`, `.bin` and `.log`.
- Upstream-compatible custom flight actions and HUD drawing extension points.
- Linux joystick input uses a port-native joydev reader with deterministic lifecycle, raw input
  preview and Avalonia-safe axis/button detection; Windows continues to use upstream DirectInput.
- Upstream library message/input callbacks are bridged to Avalonia, so recoverable joystick,
  mission-file, GStreamer and transport errors cannot fall through to `ShowEvent Not Set`.
- PX4Flow image assembly is port-native and writes grayscale MAVLink frames directly to an Avalonia
  bitmap; the Windows-only upstream `System.Drawing.Bitmap` path is no longer used by the page.
- Cross-platform telemetry log directory picker.
- Four palette resources use statically typed Avalonia resources.
- MAVLink NSH shell over `SERIAL_CONTROL`, with raw-link mode kept as an explicit expert option.
- ArduPilot onboard Lua REPL over `MAV_CMD_SCRIPTING` and MAVFTP, separate from the local script console.
- Runtime MAVLink message interval control (`SET_MESSAGE_INTERVAL`) from Flight Data.
- Configurable/persistent QuickView cell count and columns, plus persistent manual preflight checks.
- Mission file compatibility for QGC WPL, Mission Planner `.mission`, QGC `.plan`, `.poly`, legacy
  `.fen`/`.ral`, and load-and-append. QGC Plan round-trips mission, polygon/circle fence, and rally data.
- Fence inclusion/exclusion polygons and circles can be created from the Avalonia planner.
- libVLC startup now resolves versioned Linux `libvlc.so.5`, reports live playback errors, retains
  media for its full native lifetime, and accepts direct MRLs plus common RTP/GStreamer input.
- Speech event announcements are functional: mode and waypoint changes speak through the upstream
  `CurrentState` hooks, and a cross-platform announcer covers arm/disarm, battery, custom, low
  altitude and low speed alerts with upstream-compatible templates and thresholds.
- Password protection of the Setup and Config screens is enforced: enabling the option prompts for
  a password (stored as the upstream salted hash) and both screens require it once per session.
- The `ShowNoFly` planner option now auto-loads every KML/KMZ from the `NoFly/` folder in the
  user data directory as a map overlay when the planner opens.
- The serial link and telemetry logs are closed on application exit, so the tlog tail is no longer
  lost when the window is closed while connected.
- Write Fast performs the upstream pipelined `MISSION_ITEM_INT` upload with
  MISSION_REQUEST/MISSION_ACK resynchronization instead of duplicating the normal write.
- Geo-referencing writes GPS EXIF into geotagged image copies through the upstream
  `GeoRefImageBase`/ExifLibrary path, in addition to location.txt and location.kml.

Existing port functionality includes serial/TCP/UDP/UDP-client/WebSocket connections, flight data,
mission planning, parameter pages, firmware/log tools, simulation launcher, NMEA/mirroring tools,
maps/KML, DroneCAN over MAVLink, joystick mapping where a backend exists, and libVLC video. These
code paths compile; hardware-specific paths still need native-platform acceptance testing.

## Linux verification details

- Distribution SDK: `/usr/bin/dotnet` 10.0.111.
- `global.json`: 10.0.100 with `latestFeature`, so the distribution SDK is accepted.
- Release build: succeeds with `-m:1`.
- Automated tests: 104 passed, 0 failed.
- Clean self-contained `linux-x64` publish: 156 MB.
- Headless Xvfb startup: reaches the normal application event loop.
- `tar.gz` and `.deb` targets: rebuilt from the current 104-test source on 2026-08-21 with the
  distribution SDK; `lintian`, dependency simulation and extracted-package Xvfb smokes pass.
- Debian install: registered as `missionplanner-avalonia 2026.8.0`; launcher, desktop entry, icon,
  man page and dependency metadata verified.
- Installed-package smoke test: `/usr/lib/missionplanner-avalonia` remains byte-for-byte unchanged;
  runtime files are routed to isolated XDG config/data/cache/state roots.
- System runtime integrations installed: libVLC, speech-dispatcher and serial `dialout` membership.

The current handoff artifacts are
`out/packages/MissionPlannerAvalonia-2026.8.0-linux-x64.tar.gz` and
`out/packages/missionplanner-avalonia_2026.8.0_amd64.deb`. The apphost is an x86-64 ELF PIE, native
libraries are ELF `.so` files and the `.dll` files are managed assemblies.

## Runtime path behavior

The port now separates configuration, persistent user data, downloaded caches and state files using
XDG on Linux, AppData on Windows and Library folders on macOS. SRTM, SITL, parameter definitions,
log metadata and update downloads never belong in the installation directory. Legacy files are
copied to the appropriate new location without deleting the source. A build-only compatibility
override fixes upstream `Settings.GetDataDirectory()`, which otherwise resolves to unwritable
`/usr/share/Mission Planner` under modern CoreCLR Linux.

Package-managed Linux installs contain a marker and launch-time environment flag. The in-app updater
is disabled for those installs and directs the user to APT; portable Linux, Windows and macOS builds
retain their existing signed update flow.

## Managed DLL versus platform-native libraries

Managed .NET assemblies normally use the `.dll` suffix on Windows, macOS and Linux. They contain
portable ECMA-335 IL and are not evidence of an unfinished platform port. Native files remain
platform-specific: PE DLLs on Windows, dylibs/Mach-O on macOS, and ELF `.so` files on Linux.

The executable project, resolved NuGet graph and Linux runtime output were also audited for
WinForms: they contain no `System.Windows.Forms.dll`, `MissionPlanner.Controls.dll`,
`UseWindowsForms` setting or direct `System.Windows.Forms` source reference. Portable upstream
libraries retain a UI callback contract; it is now handled by Avalonia rather than by the original
WinForms host.

An earlier Linux publish also contained three genuinely Windows-native PE DLLs copied by upstream:
`simpleble-c.dll`, `simpleble.dll`, and `libusb-1.0.dll`. They were packaging pollution, not managed
assemblies. The Linux publish target now filters all three. File-type inspection confirms that every
remaining Linux `.dll` is managed and every `.so` is ELF. The filter is Linux-conditional and does
not remove required Windows-native files from `win-x64` builds.

## Not implemented or currently non-working

| Area | Affected targets | Current state and direction |
| --- | --- | --- |
| Multiple simultaneous vehicle links / Connection List | All | Menu remains disabled; the port has one global primary link. Requires multi-link AppState/UI architecture. |
| Full Mission Planner plugin loader | All | Discovery, lifecycle and WinForms plugin hosting are absent. Keep the new portable action/HUD hooks and add a cross-platform plugin host separately. |
| Traffic, airport and TFR map overlays | All | Planner settings are persisted, but Mapsui does not yet render upstream ADS-B/AIS traffic, airports or TFR overlays. |
| SHP/DXF and optional GDAL map import | All | KML is supported. The upstream managed SHP/DXF readers and an optional native GDAL adapter still need Mapsui layers and an Avalonia import flow. |
| Vehicle terrain service and extra elevation sources | All | Local SRTM profiles work; serving `TERRAIN_DATA` to a vehicle and DTED/GeoTIFF sources are absent. |
| MAVLink Camera Protocol v2 integration | All | Reusable upstream protocol code exists, but stream discovery/selection and the gimbal/video overlay recorder are not wired into the Avalonia video UI. |
| Moving Base tool | All | Advanced-page window is not ported. Follow Me is available but is not equivalent. |
| Swarm / formation flight | All | The upstream swarm controllers and UI are absent. The control logic is portable, but needs a new multi-vehicle foundation and Avalonia safety UI. |
| General startup auto-connect | All | SITL can auto-connect after launch; persisted serial/network auto-connect and upstream discovery pipelines are not wired into normal application startup. |
| Survey grid mission commands | All | The survey grid generates plain waypoints only. Upstream camera triggers (`DO_SET_CAM_TRIGG_DIST`, `DO_DIGICAM_CONTROL`, servo modes), takeoff/RTL/land wrapping, speed, spline, heading hold and the sample-photo/camera-profile helpers are not generated. |
| Grid v2 / SimpleGrid variants | All | The alternative upstream/plugin grid workflows remain absent. |
| DroneCAN file browser | All, hardware-specific | Node parameters and firmware upload work; general node file browsing is not exposed. |
| DroneCAN parameter UI conveniences | All | The integrated DroneCAN page lacks parameter search, favourites, a modified-only filter and `.param` import/export. A removed unreferenced prototype with these features remains available in git history (`DroneCANParamsViewModel`). |
| Secondary log/interchange tools | All | Tlog conversion is present; tlog parameter/waypoint extraction, ULog UI, offline MagFit and CoT output remain to be ported. |
| Direct DroneCAN SLCAN adapter mode | All, hardware-specific | UI reports unsupported; MAVLink-CAN1/CAN2 works. Direct serial lifecycle needs porting and native testing. |
| Antenna tracker interfaces other than Maestro | All, hardware-specific | Configuration rejects other drivers; port/test each driver independently. |
| Traditional-heli live curve/servo visualization | All | Writable configuration is present; remaining ZedGraph visuals need Avalonia replacements. |
| Persistent map tile cache | All | The Mapsui layer does not connect a persistent tile cache; the nonfunctional selector is disabled and maps currently need network access. |
| QGC Plan fence return point | All | QGC Plan has polygons/circles but no Mission Planner fence-return field. `.plan` export warns when it omits one; use legacy `.fen` when that point must round-trip. |
| Custom theme editor and audio vario | All | Theme selection works, but the editor is absent. Upstream vario uses unsupported `Console.Beep`; it needs a native audio abstraction. |
| Joystick input on macOS | macOS | Upstream only supplies DirectInput and Linux joydev backends; a GameController/HID backend is required. |
| Native macOS arm64 release with video | macOS Apple Silicon | The Avalonia apphost cross-publishes as arm64, but the official `VideoLAN.LibVLC.Mac` 3.1.3.1 package contains an x86-64-only dylib. The operational release stays `osx-x64`/Rosetta until an arm64 libVLC runtime is built and packaged. |
| BLE transport | Linux/macOS; Windows unverified | Upstream supplies Windows SimpleBLE binaries. Linux needs a `.so`; macOS needs a dylib/framework integration. Windows path remains packaged but needs hardware testing. |
| NativeAOT runtime | All | Linux links to a 66 MB ELF but fails in log4net `Assembly.GetCallingAssembly()`; MAVLink/XML/fastJSON also require dynamic code. Experimental only. |
| Mission transfer over MAVFTP | All | Write Fast now uses the upstream-style pipelined `MISSION_ITEM_INT` upload; mission transfer over MAVFTP (`chk_usemavftp`) remains absent. |
| HUD recording and pop-out UI | All | "Record Video Stream" records the libVLC stream; upstream HUD-to-AVI frame capture, fixed aspect override, and undock/pop-out of HUD/map/tab panels are absent. |
| Flight Data map extras | All | Camera footprint overlay (`CAMERA_FEEDBACK`), POI actions on the Flight Data map, Point Camera Coords, and the mission `DistanceBar` strip are not ported (POI is available in the planner). |
| Pre-flight checklist engine | All | Only the manual checklist and six fixed automatic checks exist; the upstream configurable rule engine (`checklistDefault.xml`, condition types, colours, editor) is absent. |
| DisplayView profiles | All | `DisplayViewExtensions.custompath` is set, but no screen consumes `DisplayConfiguration` to show/hide individual widgets (tab-level Customize is ported). |
| Planner map tools | All | Tile prefetch (area and along-WP-path), offset polygon, Tracker Home from map, rally set/get/save/clear vehicle actions, and a general KML/KMZ/DXF overlay (beyond NoFly) are not ported. |
| Full Parameter List extras | All | "Reset to Default" (`FORMAT_VERSION` wipe) and the ArduPilot GitHub parameter-file browser/comparison are absent. |
| ADS-B connection settings | All | The ADS-B toggle is persisted, but the upstream server/port configuration prompt is not implemented. |
| SSH terminal | All | The upstream companion-computer SSH terminal (`Renci.SshNet`) is not ported; the terminal is MAVLink NSH only. |
| RF propagation overlay | All | The terrain line-of-sight/RF coverage overlay (Ctrl+W upstream) is absent. |
| Log tooling extras | All | OSD video rendering from tlog, LogIndex with map thumbnails, log download over SCP, tlog→CSV/human-readable text/graph in the tlog converter, and MAVLink Inspector "Graph It" are not ported. |
| Geo-reference gaps | All | GPS EXIF is now written into geotagged copies; trigger-message mode, shutter lag, Estimate Offset, AMSL base altitude and KML network-link export are still absent. |
| Geomagnetic K-index | All | The upstream K-index fetch and warning is not ported. |
| Misc upstream tools | All | The hidden developer tool window (`temp.cs`), translation/resource editor, OpenGL 3D terrain view, MicroDrones serial downlink, vehicle default-settings loader, DevopsUI, menu auto-hide and menu icon sets are not ported. |

## Intentionally disabled or replaced

| Area | Decision |
| --- | --- |
| Embedded Mission Planner HTTP/KML/MJPEG server | Not compiled on any target. Do not restore the older server implementation; any replacement needs the current authentication and anti-DoS model. |
| Support Proxy | Not ported on any target until authentication, explicit consent and networking are designed and reviewed. |
| Original WinForms/Windows driver-install UI | Not part of the Avalonia UI. Use native OS driver handling and add board-specific cross-platform DFU implementations where required. |
| Legacy CLI firmware/log paths and AC3.3-era terminal flows | Obsolete for supported firmware and intentionally not exposed. |
| X-Plane/FlightGear legacy HIL and Ateryx-specific pages | Superseded by SITL and not restored without a current user and maintenance case. |
| IronPython script host | The portable local console uses MoonSharp Lua. Old Python scripts would require a large legacy runtime and are not enabled by default. |
| Historical third-party service plugins | AltitudeAngel, DigitalSky, AirMarket and similar integrations require current API, authentication and privacy review before any port. |
| DirectShow device enumeration | Replaced by libVLC video input so the feature can share one API across Windows, macOS and Linux. |
| Windows SimpleBLE/libusb DLLs in Linux packages | Explicitly filtered only for Linux; shipping PE native libraries on Linux would not provide BLE/USB support. |
| NativeAOT as release format | Disabled by default on every target. Supported releases are self-contained CoreCLR builds. |

## Native-platform acceptance still required

A Flysky FS-i6XCN was used for a Linux joydev smoke: the port-native reader opened it through
`/dev/input/by-id`, reported 16 buttons, returned live raw axis/button state and shut down cleanly.
Hands-on auto-detect/mapping and RC output to a live vehicle still need acceptance. No USB flight
controller, CAN adapter, camera or live vehicle was attached during this verification. Each release
target still needs acceptance tests with its native serial/USB permissions, video/audio stack and
representative hardware. The port-native PX4Flow frame path is unit-tested but still needs a live
sensor acceptance run. ArduPilot SITL end-to-end mission testing is also pending; macOS SITL
currently has no prebuilt launcher binary in this port.

## Security dependency overrides

The root build overrides vulnerable versions inherited from the upstream project without modifying
the submodule: log4net 3.3.2, SharpCompress 0.48.0 and SkiaSharp/SkiaSharp native assets 2.88.6.
Restore/build no longer reports the corresponding NuGet vulnerability warnings.
