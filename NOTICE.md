# NOTICE

MissionPlanner Avalonia is an independent, native cross-platform port of the user
interface of **ArduPilot Mission Planner**.

## Upstream credit

- Based on **Mission Planner** © Michael Oborne and the ArduPilot project.
  <https://github.com/ArduPilot/MissionPlanner>
- Mission Planner is licensed under the **GNU General Public License v3.0** (see `LICENSE`).
- This project links Mission Planner's library code (`ExtLibs/…`: Mavlink, Comms, Core,
  Utilities, ArduPilot, MissionPlanner.Drawing, …) **unmodified**, included as a git submodule
  pinned to upstream commit `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`.

## What this project changes

- The Windows-only WinForms UI is **not** used. A new UI is built with **Avalonia (.NET 10)** so the
  app runs natively on macOS (Apple Silicon), Linux and Windows.
- All flight/protocol/log/param/mission **logic is reused unchanged** from Mission Planner's libraries
  via project reference — only the presentation layer (Views + ViewModels) is new.
- See `docs/PORT_STATUS.md` for the page-by-page port state.

## BLE transport dependencies

- `Linux.Bluetooth` © 2024 Xeno Innovations, Inc. is used for the Linux BlueZ/D-Bus
  transport under Apache License 2.0; see `LICENSES/Apache-2.0.txt`.
- `Tmds.DBus` © Tom Deseyn and Alp Toker is used by that transport under the MIT
  License; see `LICENSES/MIT-Tmds.DBus.txt`.
- Unmodified `SimpleBLE` 0.7.3 provides the official Mission Planner-compatible Windows transport
  and the macOS CoreBluetooth transport under GPLv3; exact source, release assets and pinned hashes
  are recorded in `LICENSES/SimpleBLE-0.7.3-NOTICE.txt`.

## macOS joystick dependency

- `HIDSharp` © 2010–2025 James F. Bellinger is used for the macOS IOKit HID joystick
  transport under Apache License 2.0; see `LICENSES/Apache-2.0.txt`.

## License of this project

Because this work links Mission Planner's GPLv3 code, the combined work is a **derivative work and is
also licensed under GPLv3** (see `LICENSE`). You may copy, modify and redistribute it under those
terms, with source available and notices preserved.

## Not affiliated

This is an independent community port. It is **not** affiliated with, endorsed by, or supported by the
ArduPilot project or the original Mission Planner authors.
