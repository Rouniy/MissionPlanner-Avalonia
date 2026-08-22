# Cross-platform port status

The project targets Windows, macOS and Linux. The current synchronization was locally verified on
Linux Mint 22.3 (Ubuntu 24.04 base), x86-64, X11 on 2026-08-22. Self-contained Windows x64 and
macOS x64 outputs were also cross-published and inspected on Linux. Windows and macOS remain
first-class release targets and still require runtime acceptance on their native runners.

## Platform matrix

| Target | Packaging | Current verification |
| --- | --- | --- |
| Windows x64 (`win-x64`) | Self-contained folder, PE apphost; bundled libVLC runtime | Cross-publish passed and PE32+ executable inspected; native Windows execution pending |
| macOS x64 (`osx-x64`) | Self-contained `.app`, Mach-O/dylibs; bundled libVLC; CI signing/notarization when credentials are configured | Cross-publish passed; native macOS execution pending. Runs on Apple Silicon through Rosetta 2 |
| Linux x64 (`linux-x64`) | Self-contained ELF/CoreCLR `tar.gz` and FHS-compliant amd64 `.deb` with native dependencies | Current source: Release build and 706 tests verified; the direct-SLCAN/DroneCAN-session/MicroDrone/device-operations/default-settings/camera-overlay/SHP/DXF/GeoPackage/KML-GroundOverlay/GeoTIFF/DTED/airport-alpha/Rally/docking/SSH/SFTP/LogIndex/MagFit/Heli/connection-safety/multi-link `.deb` is rebuilt and verified after each functional commit; the portable tarball predates the latest rounds |

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
  Upstream-equivalent Ctrl+F/Ctrl+G/Ctrl+L/Ctrl+W
  shortcuts are restored, with Ctrl+I added for direct MAVLink Inspector access.
- The hidden upstream `temp.cs` developer form is replaced by an explicit Avalonia Developer Tools
  page. It ports MAVLink packet and hardware-ID decoders, APJ embedded-defaults editing, DataFlash
  splitting, DashWare CSV, raw GPS-correction extraction, log organization, arbitrary MAVFTP file
  download (including `@SYS/threads.txt`), parameter-recovery restore, QNH, forced recovery
  calibration flags, reboot/DFU/bootloader actions and remote DataFlash logging. Vehicle-changing
  actions require a live link, a disarmed vehicle and explicit confirmation where destructive.
- The hidden upstream `DevopsUI` is a native MAVLink Device Operations window reachable from Tools,
  Developer Tools and Ctrl+J. It supports the official SPI/I2C register reads and ICM20948
  write/read developer test, decodes all upstream DEVICE_OP status values and validates the fixed
  MAVLink field sizes. Operations are bound to the exact active modem/system/component session: a
  target change immediately disables the window, discards an in-flight result and requires an
  explicit rebind, including after a rapid switch away and back. The write test is additionally
  blocked while armed and repeats its target/disarm validation after the explicit hazard warning.
- Mission Command List is a native editor for upstream-compatible `PlannerExtraCommand` and
  `PlannerExtraCommandIDs` settings. Custom numeric MAV_CMD values and P1-P7 labels are immediately
  reflected in the Flight Planner command picker and column headers.
- Cursor-on-Target 2.0 output emits every MAVLink system with invariant XML timestamps/coordinates
  over TAK multicast, UDP client/host, TCP client/host or a serial port. UID prefix, callsign,
  event type and update interval are configurable in the native window. Its per-system identity grid
  restores upstream UID, TAKV, contact callsign/endpoint and VMF fields, reads/writes the official
  `CoTUID` settings format and preserves rows for offline systems.
- Warning engine startup, cross-platform speech adapter and warning editor. The advisory
  geomagnetic K-index is fetched from NOAA once per UTC day, cached and exposed through the shared
  `CurrentState` warning surface without delaying application startup.
- Theme selection includes a native custom-palette editor with validated, persisted Avalonia
  colours; the edited palette is applied immediately.
- FFT log analysis window.
- DataFlash spectrogram for ACC/GYR sensors 1–5 with X/Y/Z plots.
- Proximity radar for `DISTANCE_SENSOR` / `OBSTACLE_DISTANCE` telemetry.
- Parameter metadata regeneration from current ArduPilot definitions.
- Log organization for `.tlog`, `.rlog`, `.bin` and `.log`.
- The upstream Log Index is a native, single-instance Avalonia window reachable from Log Browser
  and Developer Tools. It recursively indexes DataFlash and timestamped telemetry logs, displays
  date/frame/sysid/duration/size/Home/time-in-air/distance/CAM metadata, aggregates multi-selection,
  opens a row in Log Browser and creates exact `<source>.jpg` route thumbnails from cache-only map
  tiles. Scans and initial DataFlash reads are cancellable. Permanent deletion is reject-by-default,
  revalidates root/source/paired-rlog identity and never uses the upstream ambiguous same-stem glob.
- Offline magnetometer calibration ports the official MagFit workflow for `.tlog`, `.bin` and `.log`
  files. It separates up to three compasses, preserves the upstream raw/offset convention and
  throttle/bucket filtering, and offers cancellable sphere or full ellipsoid ALGLIB fitting with
  sample count, eight-octant coverage, old/new offsets, DIA/ODI and RMS review. Analysis never needs
  a vehicle connection; applying values is a separate reject-by-default action that prevalidates the
  complete parameter set and requires a connected, disarmed vehicle.
- Tlog conversion now includes streamed CSV and human-readable packet exports, extraction of the
  latest parameter values, and reconstruction of complete QGC WPL mission snapshots in addition to
  KML, GPX and Matlab output.
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
  startup without reopening the endpoint prompt. An interactive network connection now uses the
  single combined Avalonia address/port dialog and suppresses the transport's second upstream prompt.
- Serial connections restore the exact upstream baud-rate set plus arbitrary custom rates, remember
  rates per physical port and expose the active MAVLink system/component selector. AUTO scan now has
  visible progress and cancellation and records the actual detected port/rate instead of persisting
  the synthetic `AUTO` endpoint.
- The official Connection List workflow is restored for `tcp://host:port`, `udp://host:port`,
  `udpcl://host:port` and `serial:port:baud` files. Independent MAVLink interfaces are opened with
  bounded parallelism, per-line telemetry logs, reader/heartbeat/stream-request lifecycles and
  prompt cancellation when a modem does not answer. The top selector combines systems/components
  from every live line and shows their endpoint; choosing one atomically redirects flight data,
  planner, parameter pages, joystick and traffic uplink. Flight Data keeps the active aircraft red
  and renders the other connected aircraft as grey heading-aware markers. Closing or losing the
  active line falls back to another live line without blocking on the old transport.
- Parameter lists are deliberately session-only: neither the port nor the compiled upstream
  `MAVState` writes a reusable vehicle-parameter cache. Disconnecting, beginning a new connection or
  selecting another MAVLink system or modem clears the applicable values/types/count immediately
  and keeps configuration fields behind the loading overlay until a complete live list arrives. Post-connect
  reads are globally serialized and latest-wins cancellable, so a silent UDP modem cannot hold the
  next selected device behind an unbounded retry. Connected configuration view models are recreated
  on target/list changes, preventing controls from retaining another device's values. After a live
  connection, the port refreshes version-specific parameter metadata and independently checks the
  official ArduPilot manifest for a newer stable vehicle firmware without blocking the link UI.
- Survey Grid now generates upstream-style mission commands: optional takeoff, speed, heading hold,
  spline starts, waypoint delay, RTL/land, distance/digicam/repeat-servo/set-servo camera control and
  per-strip trigger start/stop. It also ports the official complete-strip split-flight output with
  `DO_JUMP` selectors and speed restore, exact `GridData` `.grid` persistence, merged built-in/user
  camera XML profiles, sample-JPEG dimensions/focal length and SRTM ground-elevation statistics.
  Its native Mapsui preview mirrors the current Mission Planner controls: draggable red boundary,
  yellow route segments with distance hover, shared numbered waypoint/photo markers, terrain-aware
  camera footprints, the 1–8+ overlap palette and legend, point-start vertex selection and the
  optional distance optimiser. Boundary edits are returned to Flight Planner when the grid is
  accepted.
- The integrated DroneCAN parameter page now has search, favourites, modified-only filtering and
  `.param` import/export; failed writes remain visibly dirty instead of being accepted locally.
  Its MAVLink-CAN session, forwarding packets and every node parameter/firmware operation are bound
  to the exact modem, system ID and component ID captured at Connect. Changing the active modem,
  vehicle or selected DroneCAN node cancels or invalidates outstanding work; node/parameter/log UI
  is cleared immediately and late callbacks cannot repopulate it, including switch-away-and-back
  races. SLCAN initialization runs off the UI thread, and Disconnect remains available while an
  unresponsive operation is winding down. The official direct SLCAN workflow is also ported: a
  native serial-port/host-baud selector opens Lawicel-compatible adapters without a MAVLink link,
  persists stable removable-device paths, performs the upstream C/S/N/V/O/F initialization and
  exposes the same optional `C` command on exit. The host handle is always released even when the
  adapter is deliberately left in SLCAN mode. A selected port cannot be opened while any primary or
  Connection List MAVLink link still owns the same device, including Linux symlink aliases.
  `Prepare autopilot SLCAN` reproduces the official CAN1 parameter sequence
  (`CAN_SLCAN_CPORT`, `CAN_SLCAN_TIMOUT`, `CAN_P1_DRIVER`, optional `CAN_SLCAN_SERNUM=0`) with a
  disarmed check, explicit warning and exact modem/system/component revision binding. Direct-adapter
  node sessions remain independent when the operator switches an unrelated MAVLink modem.
- Full Parameter List includes the upstream-compatible, explicitly confirmed reset-to-default and
  reboot flow. Setup now also exposes the official Mission Planner `DefaultSettings` workflow as a
  dedicated page: it recursively catalogs `.param` profiles below ArduPilot `Tools/Frame_params`,
  caches the list for the session and supports an explicit refresh. Catalog/download requests are
  cancellable and paths are constrained to that upstream tree. Local files and downloaded profiles
  are compared with the live parameter set, with the upstream runtime and barometer-calibration
  exclusions, then selectively staged and reviewed before any write reaches the vehicle. A profile
  comparison is bound to the exact modem/system/component selection revision and is discarded if
  the user switches away while the download or comparison dialog is open.
- Traditional Heli setup now includes the official live visual feedback as native Avalonia controls:
  the four-point Stabilize collective curve, 101-point Acro expo curve, live mapped collective
  cursor, collective/rudder inputs with manual-mode range capture and the three swash-servo position
  needles. Its 100-ms timer follows page activation/deactivation instead of remaining alive after
  navigation, and the writable parameter/manual-servo workflow remains intact.
- ADS-B aircraft received as MAVLink `ADSB_VEHICLE` telemetry or from an external receiver are
  retained with source-aware expiry and rendered as labelled, heading-aware traffic markers.
  External input supports SBS-1, AVR and Beast TCP streams, local dump1090/readsb fallbacks and an
  adsb.lol-compatible HTTP endpoint; the official server/port settings and source-precedence rules
  are preserved. The nearest external targets can be uplinked to the vehicle at the upstream 1 Hz,
  ten-aircraft/10 km limits. MAVLink `OA_DB` obstacles are rendered with their radius, while AIS
  vessels are deduplicated by MMSI, aged independently and shown with distinct vessel markers.
  Explicit collision severity survives subsequent position packets, and unchanged traffic no
  longer rebuilds the layer at every 300 ms map tick.
- `CAMERA_FEEDBACK` telemetry is rendered as numbered photo markers, flags captures below
  `CAM_MIN_INTERVAL` and draws the projected footprints of the latest four images using the
  upstream terrain-aware camera geometry.
- The shared MAVLink runtime now owns every normal and built-in-SITL connection: it continuously
  reads packets, sends the optional GCS heartbeat, requests the configured stream rates, polls
  missing parameters, detects a disarmed silent/dead link and releases joystick control safely.
  An armed radio link is kept open through a telemetry fade so it can recover, as in upstream. It also
  restores GCS sysid, serial reset/ESP settings, refreshes Home on an arm transition and closes
  telemetry logs on an unexpected link loss.
- Vehicle terrain serving was re-audited against the pinned source: each opened `MAVLinkInterface`
  instantiates the official `TerrainFollow`, receives `TERRAIN_REQUEST` through the shared reader and
  sends 4×4 `TERRAIN_DATA` grids from the cross-platform SRTM cache. This inherited workflow was
  already active and is no longer incorrectly listed as absent.
- Setup > Advanced > Elevation Sources restores the official local DEM workflow and `GDALImageDir`
  setting without requiring a native GDAL installation. It recursively indexes GeoTIFF and
  DTED0/1/2 files in the background at startup, reports per-file coverage/errors, supports progress
  cancellation and preserves the official GeoTIFF -> DTED -> downloaded-SRTM altitude priority.
  Changing an already active DEM directory is staged for restart so stale and new terrain indexes
  cannot be mixed invisibly in one session.
- `Load Waypoints on connect` now opens the planner and reads the mission, while `Params Background
  Load` performs a cancellable live parameter-protocol read without holding the connect UI open.
- Flight Planner can read and write mission, fence and rally storage through ArduPilot's MAVFTP
  `@MISSION/*.dat` files, with an automatic fallback to the standard MAVLink mission protocol when
  the firmware or transport does not support MAVFTP mission storage.
- The official six-action Rally Points map submenu is restored: set at the clicked coordinate,
  download, upload, clear, save `.ral` and load `.ral`. Adding or clearing points preserves the
  separate active-mission store and participates in Undo. An online clear writes an empty Rally
  mission before changing the local list; a transport failure retains local data and directs the
  operator to download again to verify potentially partial vehicle state.
- Flight Planner's `Switch Docking` map action reproduces both upstream arrangements: action tools
  on the right with waypoints below the map, or action tools below with waypoints to the right.
  Both splitters remain usable, the bottom toolbar scrolls horizontally, and the exact upstream
  `FP_docking=Right|Bottom` setting restores the layout on the next view instance.
- Planner/Flight Data runtime settings that previously only wrote a preference now take effect:
  heading-up map rotation, track length, distance-to-home visibility, HUD overlay visibility and
  reduced refresh rate on slow machines. Colour-type WarningEngine rules now highlight their
  matching QuickView cell with readable foreground text.
- Map tiles support upstream-style server-only, server-and-persistent-cache and strictly offline
  cache-only modes. Flight Data and Flight Planner share the cross-platform disk cache and apply a
  mode change immediately without mixing providers that happen to use the same z/x/y coordinates.
- RF Propagation restores the upstream Ctrl+W settings and all three operational overlays on both
  Flight Data and Flight Planner maps: SRTM elevation/terrain shading, the `SightGen`-equivalent
  360-degree terrain-intercept contour, and red/orange Home/vehicle battery-distance rings. It
  preserves the official setting keys, rainbow palette and missing/ocean transparency, while
  making the published 0.5-degree azimuth option functional and bounding the upstream zero-degree
  convergence loop. Cancellable generation guards prevent stale Mapsui results from replacing a
  newer viewport; missing DEM sectors are reported instead of being drawn as verified RF coverage.
- Airport overlays reuse the pinned Mission Planner `airports.csv`, `Airports.ReadOurairports`
  filters and 100-km proximity cache. Flight Data honours the upstream-default-on `showairports`
  setting while Flight Planner always shows airports. The Mapsui layer preserves the official
  9-km/5,559-m Australia translucent-red disks without outlines, zoom > 3 gate, read-only behaviour
  and 50-pixel hover names. Its default opaque-white `VectorStyle` is disabled so the exact upstream
  red alpha of 25/255 composites directly over map tiles instead of producing solid pink disks. A
  renderer-level regression verifies the lower layer remains visible through a disk on a contrasting
  background; database loading is shared and asynchronous.
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
- The Terminal page now carries all three current workflows: MAVLink NSH over `SERIAL_CONTROL`,
  raw active-link mode for experts, and the official companion-computer SSH path. SSH uses a
  cancellable `xterm` 80×24 shell with interactive text/paste, Ctrl/arrow/Home/End/F-key sequences,
  cursor/screen editing and alternate-screen handling for tools such as `nano`. A selectable native
  renderer preserves standard/bright/256/true-colour SGR foreground/background colours, bold,
  italic, underline, inverse video, erase/move attributes and the remote cursor. Unlike upstream's
  implicit trust, first contact pins the server's SHA-256 host-key fingerprint and a changed key is
  rejected behind a non-default MITM warning. Host, port and username may be remembered; the
  password is never persisted or logged.
- ArduPilot onboard Lua REPL over `MAV_CMD_SCRIPTING` and MAVFTP, separate from the local script console.
- Runtime MAVLink message interval control (`SET_MESSAGE_INTERVAL`) from Flight Data.
- Configurable/persistent QuickView cell count and columns through the upstream-style context menu,
  without permanent layout controls consuming flight-data space. The pre-flight page loads the
  upstream `checklistDefault.xml` schema, evaluates chained CurrentState/parameter conditions,
  retains manual checks and provides a native colour/condition editor with persistent overrides.
- Mission file compatibility for QGC WPL, Mission Planner `.mission`, QGC `.plan`, `.poly`, legacy
  `.fen`/`.ral`, and load-and-append. QGC Plan round-trips mission, polygon/circle fence, the optional
  breach-return point and rally data, with strict coordinate/frame validation and duplicate-safe append.
- Flight Planner imports KML/KMZ LineStrings as missions and Point placemarks as persistent POIs;
  map overlays also accept KMZ and keep upstream-style separate LineString, outer Polygon and
  MultiGeometry routes, point labels, StyleMap line colour/alpha and integer width. Local KML and
  in-memory KMZ `GroundOverlay` images support rotated `LatLonBox` and `gx:LatLonQuad`, bounded
  affine raster warping and path-traversal rejection. As upstream does, a successful KML/KMZ
  overlay import offers to copy its routes/polygons to Flight Data while leaving point labels and
  rasters planner-only. Planner settings, default altitude frame, absolute-altitude write
  confirmation, display-unit conversions and last Flight Data viewport are restored. Portable map
  tools now include place search, arbitrary heading, UTM entry, polygon offset and Tracker Home.
- Flight Planner now ports the active managed SHP/DXF workflows from upstream: point shapefiles load
  as missions with `ELEVATION`/`alt`/geometry-Z precedence and numeric `wp` ordering; shapefiles can
  also replace the drawn polygon or render mixed point/line/polygon overlays. ESRI `.prj` reprojection,
  `.cpg` encodings and case-insensitive Linux sidecars are supported. DXF Line, Polyline, LwPolyline
  and MLine entities render with their source colours in longitude/latitude or a signed UTM zone.
- GeoPackage feature overlays are imported through a managed, cross-platform reader rather than the
  optional native OGR runtime. Standard GeoPackage geometry headers, points, lines, polygons,
  collections, quoted table names and declared coordinate-system reprojection are supported. As in
  official Mission Planner, imported vector geometry appears in both Flight Planner and Flight Data.
- Flight Planner and Flight Data use the same persisted map provider and update together. Google,
  OpenStreetMap, Esri and Bing have distinct tile sources; the Bing selector no longer silently serves
  Esri imagery.
- Moving Base is available from Advanced Tools with serial, TCP client/host and UDP client/host NMEA
  input, persisted rate/settings, optional rally-point updates, raw logging and a live map marker.
- Developer Tools now ports the official `SerialOutputMD` workflow as MicroDrone Downlink. It emits
  the same `#1` and `#4`-`#9` record families at 10 Hz, with the official baud choices, decimal
  additive/XOR checksum and legacy ECEF conversion. Numeric formatting is culture-independent and
  GPS time uses UTC. Output is bound to the exact active modem, system ID and component ID; changing
  any of them closes the serial output and requires an explicit restart instead of silently sending
  another vehicle's telemetry. Serial errors and window shutdown also cancel and close the sender.
- Planner map tools can prefetch either the visible area or a buffered waypoint path across a selected
  zoom range into the shared offline cache. Auto-WP Text uses the cross-platform vector path backend
  and selected distance/altitude units instead of requiring the Windows-only stick font/GDI path.
- Flight Planner now keeps a bounded undo history across Mission/Fence/Rally edits, home changes,
  map operations, generated patterns and direct grid edits. Ctrl+Z, file open/save and normal/fast
  mission-transfer shortcuts mirror upstream, while the existing KML overlay loader is reachable
  from the map context menu.
- Flight Data restores upstream Ctrl+1…Ctrl+0 action-tab selection and tlog playback/speed keyboard
  controls; the log context action is labelled for its actual convert/extract feature set.
- Flight Data's instrumentation and map columns now have a draggable main splitter. Its clamped
  position is persisted as the upstream-compatible `FlightSplitter` setting, allowing either side
  to be enlarged without collapsing the other into an unusable width.
- Flight Data now renders the loaded mission and current waypoint, Home, inclusion/exclusion fence
  polygons and circles, rally points, Guided target, POIs, camera feedback, the live terrain-projected
  gimbal target and a live mission-distance progress strip. Its context menu restores POI
  add/delete/clear, coordinate-based POI creation, Point Camera Coords and the upstream opt-in photo
  overlap-count layer with its exact 1–8 palette, 0.0001-degree lattice and fixed legend.
- The main menu restores upstream auto-hide with a persistent top-edge hover target. Flight Data HUD
  choices now survive restart (icons, Russian layout, ground palette, battery cell count, swap,
  individual indicators and custom fields/prefixes), and the map menu can import/export both legacy
  three-column and altitude-preserving four-column POI files and switch directly to Flight Planner.
- The complete 19-item upstream flight-action selector and Simple Actions tab are present. Command
  implementations match upstream for calibration, safety, engine, scripting, high-latency, ADS-B
  IDENT and system time; flight termination and SD-card formatting add explicit destructive-action
  warnings. Ground-station action output and connection status/progress are now visible, and
  rejected commands are no longer reported as successfully sent.
- Flight Data home and EKF-origin actions use terrain-backed AMSL altitudes with the upstream
  ocean/terrain safety policy. Legacy mount control uses upstream centidegree conversion, preserves
  the final slider position under throttling and resets into MAVLink targeting mode.
- Mount configuration supports both legacy `MNT_*` and current `MNT1_*` parameter schemas, including
  their different angle units, and refreshes from a thread-safe parameter snapshot after loading.
  Stabilization and camera-shutter controls are shown only when the vehicle exposes them.
- Aux Function is a port of the seven upstream `DO_AUX_FUNCTION` presets with Low/Middle/High
  switch levels. It no longer misinterprets that tab as an editor for `RC7_OPTION`…`RC13_OPTION`.
- Fence inclusion/exclusion polygons and circles can be created from the Avalonia planner.
- libVLC startup now resolves versioned Linux `libvlc.so.5`, reports live playback errors, retains
  media for its full native lifetime, and accepts direct MRLs plus common RTP/GStreamer input.
  Announced MAVLink camera streams can be selected and remembered, while the payload page exposes
  MAVLink photo, recording and zoom commands and a video snapshot action.
- Speech event announcements are functional: mode and waypoint changes speak through the upstream
  `CurrentState` hooks, and a cross-platform announcer covers arm/disarm, battery, custom, low
  altitude and low speed alerts with upstream-compatible templates and thresholds.
- Audio Vario is available from Planner Settings and consumes the same `climbrate` telemetry and
  tone/cadence formulas as upstream. A cancellable single-loop controller replaces the unsafe
  `async void` lifecycle, while an in-memory PCM/WAV backend uses the packaged libVLC audio output
  on Linux, macOS and Windows without `Console.Beep`; shutdown and device-failure state are bounded
  and reflected in the UI.
- Basic, Advanced and Custom DisplayView profiles now use the upstream JSON/XML contract, including
  migration from legacy `advancedview` and early Avalonia enum-caption settings. Profile changes are
  applied live to the main Simulation/Help navigation, Setup/Config backstage pages, Flight Data
  tabs and editing locks, and Flight Planner fence/rally/menu controls. User-customized hidden tabs
  remain a separate preference, so a profile switch cannot accidentally hide a tab permanently.
- DataFlash Log Browse now evaluates upstream graph expressions instead of relying on
  `DataTable.Compute`: indexed fields, mixed-message chronological state, `wrap_360`, `degrees`,
  `atan2`, `lowpass`, `delta` and the upstream DFLogScript vector helpers are supported. Every
  alternative in `mavgraphs.xml` is parsed and the best compatible one is selected; MODE overlays
  preserve decoded names and use the upstream firmware-specific numeric-mode resolver as fallback.
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
  EXIF-time/GPS-offset matching modes are available, including automatic offset estimation,
  configurable shutter lag, AMSL/GPS altitude selection and an explicit metre-based base-altitude
  adjustment; TRIG deliberately requires equal photo/event counts so a missing capture cannot
  silently shift every subsequent coordinate. The portable static `location.kml` report replaces
  the upstream loopback network link, which depended on the intentionally removed embedded server.

Existing port functionality includes serial/TCP/UDP/UDP-client/WebSocket connections, flight data,
mission planning, parameter pages, firmware/log tools, simulation launcher, NMEA/mirroring tools,
maps/KML, DroneCAN over MAVLink or direct SLCAN, joystick mapping where a backend exists, and libVLC video. These
code paths compile; hardware-specific paths still need native-platform acceptance testing.

## Linux verification details

- Distribution SDK: `/usr/bin/dotnet` 10.0.111.
- `global.json`: 10.0.100 with `latestFeature`, so the distribution SDK is accepted.
- Release build: succeeds with `-m:1`.
- Automated tests: 706 passed, 0 failed.
- Clean self-contained `linux-x64` publish: 173 MB including the pinned airport database.
- Headless Xvfb startup: reaches the normal application event loop.
- The `.deb` target was rebuilt from the current 706-test source on 2026-08-22. Package metadata,
  launcher, desktop entry, icon, man page, native dependencies and required checklist/parameter/log
  resources were verified; all 396 packaged-file checksums match after extraction, including the
  byte-for-byte pinned 8,443,722-byte `airports.csv`.
- `lintian --fail-on error,warning` passes without diagnostics. The extracted x86-64 ELF apphost
  reaches the normal event loop under Xvfb and has no unresolved direct library dependencies.
  The bundled optional .NET LTTng trace provider targets the older `liblttng-ust.so.0` ABI; it is
  not loaded during normal startup and EventPipe diagnostics are unaffected. Linux filtering
  removed the three known Windows-native SimpleBLE/libusb binaries.
- The smoke routes downloaded parameter/log/SRTM data and application settings to isolated XDG
  roots; the extracted application tree remains byte-for-byte unchanged.
- System runtime integrations installed: libVLC, speech-dispatcher and serial `dialout` membership.

The most recent Debian artifact is
`out/packages/missionplanner-avalonia_1.3.83-20260822.cbdaff8_amd64.deb`
(53,882,700 bytes; SHA-256
`70fde657d337e8eae14bb15fb7e9da1aff0c36b5c75826ecdec500ba99386dd8`), built from the current
706-test source including direct serial SLCAN, target-safe official DroneCAN parameter/firmware,
MicroDrone, DEVICE_OP
and ArduPilot Default Settings workflows,
camera feedback/overlap/gimbal overlays, managed SHP/DXF/GeoPackage
planner import, local GeoTIFF/DTED elevation sources,
styled KML/KMZ vector/GroundOverlay layers, Flight Data overlay copying, corrected translucent-red
airport disks, Rally Points actions, switchable Planner docking, the interactive verified-host-key
SSH terminal, secure SFTP DataFlash download/delete workflow, the recursive flight Log Index with
map thumbnails, offline sphere/ellipsoid MagFit, live Traditional Heli visualization, the movable
Flight Data splitter, session-only/latest-wins vehicle parameter loading, single-prompt network
connections, independent multi-link Connection List support and composite upstream/date/commit
versioning.
Its APT version is
`1:1.3.83+20260822.r162.cbdaff8`; epoch 1 preserves upgrade ordering from the old CalVer
packages and `r162` orders same-day builds before comparing hashes. The existing
`out/packages/MissionPlannerAvalonia-2026.8.0-linux-x64.tar.gz` predates the latest source changes.
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
| Full Mission Planner plugin loader | All | Discovery, lifecycle and WinForms plugin hosting are absent. Keep the new portable action/HUD hooks and add a cross-platform plugin host separately. |
| Optional native GDAL/OGR map drivers | All | GeoPackage feature layers, SHP and DXF are available through managed cross-platform readers. The generic native OGR/GDAL driver path for additional formats remains absent. |
| MAVLink Camera Protocol v2 remaining UI | All | Announced `VIDEO_STREAM_INFORMATION` streams can be selected and remembered, and photo, recording and zoom commands are wired to detected camera components. The legacy mount camera-target map overlay is ported; the upstream combined gimbal/video overlay recorder remains absent. |
| Swarm / formation flight | All | The upstream swarm controllers and UI are absent. The control logic is portable, but needs a new multi-vehicle foundation and Avalonia safety UI. |
| Grid v2 / SimpleGrid variants | All | The alternative upstream/plugin grid workflows remain absent. |
| DroneCAN multicast CAN1/CAN2 | All | The current official Mission Planner exposes pydronecan-compatible `239.65.82.0/1:57732` transports with network-interface selection. MAVLink-CAN1/CAN2 and direct serial SLCAN are ported; multicast remains to be translated and tested. |
| Antenna tracker interfaces other than Maestro | All, hardware-specific | Configuration rejects other drivers; port/test each driver independently. |
| Signed beta application updates | All | Stable signed updates work. The Beta Updates control is disabled until this project publishes and signs a separate beta manifest/channel. |
| Joystick input on macOS | macOS | Upstream only supplies DirectInput and Linux joydev backends; a GameController/HID backend is required. |
| Native macOS arm64 release with video | macOS Apple Silicon | The Avalonia apphost cross-publishes as arm64, but the official `VideoLAN.LibVLC.Mac` 3.1.3.1 package contains an x86-64-only dylib. The operational release stays `osx-x64`/Rosetta until an arm64 libVLC runtime is built and packaged. |
| BLE transport | Linux/macOS; Windows unverified | Upstream supplies Windows SimpleBLE binaries. Linux needs a `.so`; macOS needs a dylib/framework integration. Windows path remains packaged but needs hardware testing. |
| NativeAOT runtime | All | Linux links to a 66 MB ELF but fails in log4net `Assembly.GetCallingAssembly()`; MAVLink/XML/fastJSON also require dynamic code. Experimental only. |
| HUD frame recording | All | "Record Video Stream" records the libVLC stream and the upstream 4:3/16:9 HUD aspect toggle is ported. The separate upstream HUD-to-AVI frame-capture path remains absent. The current upstream `dropOutToolStripMenuItem_Click` handler is empty and is not counted as a functional gap. |
| Flight Data map extras | All | Mission/Home/current-WP, fence, rally, Guided target, POIs, camera feedback with latest footprints and opt-in overlap count, live terrain-projected gimbal target, ADS-B/AIS/OA_DB traffic, airports, RF propagation/elevation/distance overlays and mission-distance progress are ported. The current upstream `ProximityControl` launch in `FlightData` is commented out; the port already provides a live Proximity radar tab, so it is not counted as a missing map workflow. |
| Log tooling extras | All | Interactive DataFlash graphing, upstream expressions/preset alternatives/MODE overlays, log message/parameter inspection, MAVLink Inspector "Graph It", the recursive LogIndex with cache-only map thumbnails, offline three-compass sphere/ellipsoid MagFit and the upstream-named SCP workflow (actually SFTP over SSH) are ported. The SFTP page lists/downloads selected or all BIN logs, creates text LOG/KML outputs, applies GPS-time names and safely deletes selected/all remote logs with host-key pinning; passwords are never persisted and an inherited plaintext `LogDownloadscppath` is erased during migration. OSD video rendering from tlog remains absent. |
| Remaining developer utilities | All | The safe portable subset of `temp.cs` is now a native Developer Tools page. Translation/resource editor and the OpenGL 3D terrain view still need dedicated Avalonia implementations. Device Operations, Vehicle Default Settings, MicroDrone serial downlink, local GeoTIFF/DTED configuration and PX4Flow live image assembly are already port-native. |

## Intentionally disabled or replaced

| Area | Decision |
| --- | --- |
| Embedded Mission Planner HTTP/KML/MJPEG server | Not compiled on any target. Do not restore the older server implementation; any replacement needs the current authentication and anti-DoS model. GeoRef emits a portable static `location.kml` instead of the old loopback network link. |
| TFR download/overlay | Current Mission Planner disabled its Jepptech-backed background download on 2020-09-30 when the service ended (`b26095f9a`) and later removed the parser/Flight Data handler. The port retains the compatible `showtfr` preference but does not invent a replacement feed and incorrectly present it as upstream parity. |
| Support Proxy | Not ported on any target until authentication, explicit consent and networking are designed and reviewed. |
| Original WinForms/Windows driver-install UI | Not part of the Avalonia UI. Use native OS driver handling and add board-specific cross-platform DFU implementations where required. |
| Legacy CLI firmware/log paths and AC3.3-era terminal flows | Obsolete for supported firmware and intentionally not exposed. |
| DroneCAN file browser | The official control is unreachable from the current Mission Planner UI and remains half-stubbed. Node parameters and firmware upload are ported; a general browser will be reconsidered when upstream exposes a complete operational workflow. |
| X-Plane/FlightGear legacy HIL and Ateryx-specific pages | Superseded by SITL and not restored without a current user and maintenance case. |
| IronPython script host | The portable local console uses MoonSharp Lua. Old Python scripts would require a large legacy runtime and are not enabled by default. |
| Historical third-party service plugins | AltitudeAngel, DigitalSky, AirMarket and similar integrations require current API, authentication and privacy review before any port. |
| DirectShow device enumeration | Replaced by libVLC video input so the feature can share one API across Windows, macOS and Linux. |
| GDI+ HUD renderer switch | Not applicable to Avalonia: the port uses the cross-platform Skia renderer on every target, so the inherited GDI+ option is shown disabled. |
| Usage analytics | This port has no analytics sender; the Config page states that analytics are disabled rather than presenting a preference with no runtime consumer. |
| Windows SimpleBLE/libusb DLLs in Linux packages | Explicitly filtered only for Linux; shipping PE native libraries on Linux would not provide BLE/USB support. |
| NativeAOT as release format | Disabled by default on every target. Supported releases are self-contained CoreCLR builds. |
| Developer crash/flight commands | The upstream developer form's autopilot lockup and automatic arm/takeoff shortcuts remain intentionally absent. The normal Flight Data action selector's upstream flight-termination command is ported, but only behind an explicit irreversible-action warning. |

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
The SSH port uses SSH.NET 2026.0.0 and BouncyCastle.Cryptography 2.7.0 instead of upstream's
vulnerable SSH.NET 2020.0.2 dependency. A current `dotnet list package --vulnerable
--include-transitive` audit reports no vulnerable package in the Avalonia application graph.
