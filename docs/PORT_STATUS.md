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
| Linux x64 (`linux-x64`) | Self-contained ELF/CoreCLR `tar.gz` and FHS-compliant amd64 `.deb` with native dependencies | Current source: Release build and 148 tests verified; Linux `.deb` is rebuilt at the end of each release pass, while the existing `tar.gz` predates the latest feature rounds |

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
- A visible cross-platform Tools menu exposes MAVLink Inspector, MAVLink Mirror, NMEA output,
  Cursor-on-Target/TAK output, spectrogram, link/connection diagnostics and log tools.
  Upstream-equivalent Ctrl+F/Ctrl+G/Ctrl+L
  shortcuts are restored, with Ctrl+I added for direct MAVLink Inspector access.
- The hidden upstream `temp.cs` developer form is replaced by an explicit Avalonia Developer Tools
  page. It ports MAVLink packet and hardware-ID decoders, APJ embedded-defaults editing, DataFlash
  splitting, DashWare CSV, raw GPS-correction extraction, log organization, arbitrary MAVFTP file
  download (including `@SYS/threads.txt`), parameter-recovery restore, QNH, forced recovery
  calibration flags, reboot/DFU/bootloader actions and remote DataFlash logging. Vehicle-changing
  actions require a live link, a disarmed vehicle and explicit confirmation where destructive.
- Mission Command List is a native editor for upstream-compatible `PlannerExtraCommand` and
  `PlannerExtraCommandIDs` settings. Custom numeric MAV_CMD values and P1-P7 labels are immediately
  reflected in the Flight Planner command picker and column headers.
- Cursor-on-Target 2.0 output emits every MAVLink system with invariant XML timestamps/coordinates
  over TAK multicast, UDP client/host, TCP client/host or a serial port. UID prefix, callsign,
  event type and update interval are configurable in the native window.
- Warning engine startup, cross-platform speech adapter and warning editor.
- FFT log analysis window.
- DataFlash spectrogram for ACC/GYR sensors 1–5 with X/Y/Z plots.
- Proximity radar for `DISTANCE_SENSOR` / `OBSTACLE_DISTANCE` telemetry.
- Parameter metadata regeneration from current ArduPilot definitions.
- Log organization for `.tlog`, `.rlog`, `.bin` and `.log`.
- Upstream-compatible custom flight actions and HUD drawing extension points.
- Linux joystick input uses a port-native joydev reader with deterministic lifecycle, raw input
  preview and Avalonia-safe axis/button detection; Windows continues to use upstream DirectInput.
  An application-level 20 Hz sender now mirrors upstream RC override/manual-control behaviour,
  including the native UDP RC path for the built-in SITL and safe release on disable/link loss. It
  remains active after leaving Setup and can be released immediately from Flight Data. Opt-in,
  per-device Linux range calibration expands controllers whose real HID endpoints do not cover
  their advertised range; uncalibrated devices keep the original bit-for-bit mapping. Calibration
  uses a detached read-only joydev session and releases active control first, so endpoint movements
  cannot reach SITL or a real vehicle.
- Persisted serial, TCP, UDP client/listener and WebSocket endpoints can auto-connect at normal app
  startup without reopening the endpoint prompt.
- Survey Grid now generates upstream-style mission commands: optional takeoff, speed, heading hold,
  spline starts, waypoint delay, RTL/land, distance/digicam/repeat-servo/set-servo camera control and
  per-strip trigger start/stop.
- The integrated DroneCAN parameter page now has search, favourites, modified-only filtering and
  `.param` import/export; failed writes remain visibly dirty instead of being accepted locally.
- Full Parameter List includes the upstream-compatible, explicitly confirmed reset-to-default and
  reboot flow.
- ADS-B aircraft received as MAVLink `ADSB_VEHICLE` telemetry are retained with expiry and rendered
  as labelled, heading-aware traffic markers on Avalonia flight maps. Explicit collision severity
  survives subsequent position packets, and unchanged traffic no longer rebuilds the layer at every
  300 ms map tick.
- `CAMERA_FEEDBACK` telemetry is rendered as numbered photo markers, flags captures below
  `CAM_MIN_INTERVAL` and draws the projected footprints of the latest four images using the
  upstream terrain-aware camera geometry.
- The shared MAVLink runtime now owns every normal and built-in-SITL connection: it continuously
  reads packets, sends the optional GCS heartbeat, requests the configured stream rates, polls
  missing parameters, detects a disarmed silent/dead link and releases joystick control safely.
  An armed radio link is kept open through a telemetry fade so it can recover, as in upstream. It also
  restores GCS sysid, serial reset/ESP settings, refreshes Home on an arm transition and closes
  telemetry logs on an unexpected link loss.
- `Load Waypoints on connect` now opens the planner and reads the mission, while `Params Background
  Load` performs upstream MAVFTP-with-fallback loading without holding the connect UI open.
- Planner/Flight Data runtime settings that previously only wrote a preference now take effect:
  heading-up map rotation, track length, distance-to-home visibility, HUD overlay visibility and
  reduced refresh rate on slow machines. Colour-type WarningEngine rules now highlight their
  matching QuickView cell with readable foreground text.
- SITL and normal/auto connection attempts share one serialization gate; delayed auto-scan cannot
  overwrite a concurrently started simulator link. Joystick shutdown also waits for an in-flight
  sender asynchronously, so releasing overrides cannot freeze the Avalonia UI.
- Autopilot and DroneCAN parameter favourites use separate persisted namespaces, with a
  non-destructive migration from the early shared-key build.
- The uAvionix transponder page now displays live maintenance/GPS/TX/airborne fault flags and the
  upstream NIC/NACp accuracy labels decoded by `CurrentState` instead of fixed disabled placeholders.
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
  `GeoRefImageBase`/ExifLibrary path, in addition to location.txt and location.kml. CAM, TRIG and
  EXIF-time/GPS-offset matching modes are available; TRIG deliberately requires equal photo/event
  counts so a missing capture cannot silently shift every subsequent coordinate.

Existing port functionality includes serial/TCP/UDP/UDP-client/WebSocket connections, flight data,
mission planning, parameter pages, firmware/log tools, simulation launcher, NMEA/mirroring tools,
maps/KML, DroneCAN over MAVLink, joystick mapping where a backend exists, and libVLC video. These
code paths compile; hardware-specific paths still need native-platform acceptance testing.

## Linux verification details

- Distribution SDK: `/usr/bin/dotnet` 10.0.111.
- `global.json`: 10.0.100 with `latestFeature`, so the distribution SDK is accepted.
- Release build: succeeds with `-m:1`.
- Automated tests: 148 passed, 0 failed.
- Clean self-contained `linux-x64` publish: 156 MB.
- Headless Xvfb startup: reaches the normal application event loop.
- The `.deb` target was rebuilt from the current 148-test source on 2026-08-21; package structure,
  dependencies and an extracted-package Xvfb event-loop smoke were verified. The existing
  `tar.gz` is older and must still be rebuilt before release.
- Debian install: registered as `missionplanner-avalonia 2026.8.0`; launcher, desktop entry, icon,
  man page and dependency metadata verified.
- Installed-package smoke test: `/usr/lib/missionplanner-avalonia` remains byte-for-byte unchanged;
  runtime files are routed to isolated XDG config/data/cache/state roots.
- System runtime integrations installed: libVLC, speech-dispatcher and serial `dialout` membership.

The current Debian artifact is `out/packages/missionplanner-avalonia_2026.8.0_amd64.deb`; any
existing `out/packages/MissionPlannerAvalonia-2026.8.0-linux-x64.tar.gz` predates the latest source.
The apphost is an x86-64 ELF PIE, native libraries are ELF `.so` files and the `.dll` files are
managed assemblies.

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
| External traffic, airport and TFR overlays | All | MAVLink ADS-B traffic is rendered. External ADS-B/AIS feeds, airports and TFR data still need portable data services and Mapsui layers. |
| SHP/DXF and optional GDAL map import | All | KML is supported. The upstream managed SHP/DXF readers and an optional native GDAL adapter still need Mapsui layers and an Avalonia import flow. |
| Vehicle terrain service and extra elevation sources | All | Local SRTM profiles work; serving `TERRAIN_DATA` to a vehicle and DTED/GeoTIFF sources are absent. |
| MAVLink Camera Protocol v2 integration | All | Reusable upstream protocol code exists, but stream discovery/selection and the gimbal/video overlay recorder are not wired into the Avalonia video UI. |
| Moving Base tool | All | Advanced-page window is not ported. Follow Me is available but is not equivalent. |
| Swarm / formation flight | All | The upstream swarm controllers and UI are absent. The control logic is portable, but needs a new multi-vehicle foundation and Avalonia safety UI. |
| Survey grid advanced helpers | All | Core mission-command generation is ported. Upstream split-mission output, sample-photo preview, terrain-following adjustment and the camera-profile editor remain absent. |
| Grid v2 / SimpleGrid variants | All | The alternative upstream/plugin grid workflows remain absent. |
| DroneCAN file browser | All, hardware-specific | Node parameters and firmware upload work; general node file browsing is not exposed. |
| Secondary log/interchange tools | All | Tlog conversion, DataFlash split/DashWare and GPS-correction extraction are present; tlog parameter/waypoint extraction, ULog UI and offline MagFit remain to be ported. |
| Direct DroneCAN SLCAN adapter mode | All, hardware-specific | UI reports unsupported; MAVLink-CAN1/CAN2 works. Direct serial lifecycle needs porting and native testing. |
| Antenna tracker interfaces other than Maestro | All, hardware-specific | Configuration rejects other drivers; port/test each driver independently. |
| Traditional-heli live curve/servo visualization | All | Writable configuration is present; remaining ZedGraph visuals need Avalonia replacements. |
| Persistent map tile cache | All | The Mapsui layer does not connect a persistent tile cache; the nonfunctional selector is disabled and maps currently need network access. |
| Vehicle firmware/update metadata after connect | All | Connection/runtime parameter handling is ported, but the upstream online check for a newer ArduPilot vehicle firmware and version-specific parameter metadata download is not yet wired. |
| Cached parameter reuse on connect | All | The port writes a cross-platform offline parameter snapshot and supports foreground/background vehicle loads, but does not yet skip a live download using upstream's one-hour `ParamCachePath` policy. |
| Signed beta application updates | All | Stable signed updates work. The Beta Updates control is disabled until this project publishes and signs a separate beta manifest/channel. |
| QGC Plan fence return point | All | QGC Plan has polygons/circles but no Mission Planner fence-return field. `.plan` export warns when it omits one; use legacy `.fen` when that point must round-trip. |
| Custom theme editor and audio vario | All | Theme selection works, but the editor is absent. Upstream vario uses unsupported `Console.Beep`; it needs a native audio abstraction. |
| Joystick input on macOS | macOS | Upstream only supplies DirectInput and Linux joydev backends; a GameController/HID backend is required. |
| Native macOS arm64 release with video | macOS Apple Silicon | The Avalonia apphost cross-publishes as arm64, but the official `VideoLAN.LibVLC.Mac` 3.1.3.1 package contains an x86-64-only dylib. The operational release stays `osx-x64`/Rosetta until an arm64 libVLC runtime is built and packaged. |
| BLE transport | Linux/macOS; Windows unverified | Upstream supplies Windows SimpleBLE binaries. Linux needs a `.so`; macOS needs a dylib/framework integration. Windows path remains packaged but needs hardware testing. |
| NativeAOT runtime | All | Linux links to a 66 MB ELF but fails in log4net `Assembly.GetCallingAssembly()`; MAVLink/XML/fastJSON also require dynamic code. Experimental only. |
| Mission transfer over MAVFTP | All | Write Fast now uses the upstream-style pipelined `MISSION_ITEM_INT` upload; mission transfer over MAVFTP (`chk_usemavftp`) remains absent. |
| HUD recording and pop-out UI | All | "Record Video Stream" records the libVLC stream; upstream HUD-to-AVI frame capture, fixed aspect override, and undock/pop-out of HUD/map/tab panels are absent. |
| Flight Data map extras | All | Camera feedback markers/footprints are ported. POI actions on the Flight Data map, Point Camera Coords, and the mission `DistanceBar` strip remain absent (POI is available in the planner). |
| Pre-flight checklist engine | All | Only the manual checklist and six fixed automatic checks exist; the upstream configurable rule engine (`checklistDefault.xml`, condition types, colours, editor) is absent. |
| DisplayView profiles | All | `DisplayViewExtensions.custompath` is set, but no screen consumes `DisplayConfiguration` to show/hide individual widgets (tab-level Customize is ported). |
| Planner map tools | All | Tile prefetch (area and along-WP-path), offset polygon, Tracker Home from map, rally set/get/save/clear vehicle actions, and a general KML/KMZ/DXF overlay (beyond NoFly) are not ported. |
| Full Parameter List extras | All | Reset-to-default is ported. The ArduPilot GitHub parameter-file browser/comparison remains absent. |
| ADS-B connection settings | All | The toggle controls MAVLink traffic rendering. The upstream external ADS-B server/port connection and configuration prompt are not implemented. |
| SSH terminal | All | The upstream companion-computer SSH terminal (`Renci.SshNet`) is not ported; the terminal is MAVLink NSH only. |
| RF propagation overlay | All | The terrain line-of-sight/RF coverage overlay (Ctrl+W upstream) is absent. |
| Log tooling extras | All | OSD video rendering from tlog, LogIndex with map thumbnails, log download over SCP, tlog→CSV/human-readable text/graph in the tlog converter, and MAVLink Inspector "Graph It" are not ported. |
| Geo-reference gaps | All | GPS EXIF and TRIG matching are ported. Shutter lag, Estimate Offset, AMSL base altitude and KML network-link export remain absent. |
| Geomagnetic K-index | All | The upstream K-index fetch and warning is not ported. |
| Remaining developer utilities | All | The safe portable subset of `temp.cs` is now a native Developer Tools page. Translation/resource editor, OpenGL 3D terrain view, MicroDrones serial downlink, vehicle default-settings loader, DevopsUI, custom GDAL/DEM browser and optical-flow live calibration image still need dedicated Avalonia implementations. |
| CoT advanced identity fields | All | CoT 2.0 transport, type, UID and callsign are ported. Upstream's per-system TAKV/contact endpoint/VMF identity grid is not yet exposed. |

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
| GDI+ HUD renderer switch | Not applicable to Avalonia: the port uses the cross-platform Skia renderer on every target, so the inherited GDI+ option is shown disabled. |
| Usage analytics | This port has no analytics sender; the Config page states that analytics are disabled rather than presenting a preference with no runtime consumer. |
| Windows SimpleBLE/libusb DLLs in Linux packages | Explicitly filtered only for Linux; shipping PE native libraries on Linux would not provide BLE/USB support. |
| NativeAOT as release format | Disabled by default on every target. Supported releases are self-contained CoreCLR builds. |
| Developer crash/flight commands | The upstream developer form's autopilot lockup, automatic arm/takeoff and flight-termination actions are intentionally not exposed. They can crash or command a live aircraft and are not required for normal diagnostics. |

## Native-platform acceptance still required

A Flysky FS-i6XCN was used for a Linux joydev smoke: the port-native reader opened it through
`/dev/input/by-id`, reported 16 buttons, returned live raw axis/button state and shut down cleanly.
Its advertised `-127..127` HID axes only reached about `-90..90` physically, producing roughly
`7431..58105` instead of the full unsigned range; the new per-device range calibration specifically
covers this mismatch and its endpoint/clamping/monotonicity behaviour is unit-tested.
The 20 Hz MAVLink and built-in-SITL packet paths are unit-tested; hands-on auto-detect/mapping and
RC output to a live vehicle or SITL still need acceptance. Camera-footprint projection is
unit-tested, but a live `CAMERA_FEEDBACK` source is still required for acceptance. No USB flight
controller, CAN adapter, camera or live vehicle was attached during this verification. Each release
target still needs acceptance tests with its native serial/USB permissions, video/audio stack and
representative hardware. The port-native PX4Flow frame path is unit-tested but still needs a live
sensor acceptance run. ArduPilot SITL end-to-end mission testing is also pending; macOS SITL
currently has no prebuilt launcher binary in this port.

## Security dependency overrides

The root build overrides vulnerable versions inherited from the upstream project without modifying
the submodule: log4net 3.3.2, SharpCompress 0.48.0 and SkiaSharp/SkiaSharp native assets 2.88.6.
Restore/build no longer reports the corresponding NuGet vulnerability warnings.
