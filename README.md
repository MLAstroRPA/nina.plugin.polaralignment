# Changelog

## MLAstroRPA-BaseOn-16b785e branch is not merge into master yet.

### Versioning

This MLAstroRPA edition is a fork of the upstream Three Point Polar Alignment (TPPA) plugin, branched from commit `16b785e` of the TPPA `master` branch. At that commit, the upstream TPPA plugin had version number `2.2.5.0`.

- The plugin version follows the upstream TPPA scheme and stays at `2.2.5.0` (the version of the upstream commit this fork is based on).
- The fork identity is exposed through the plugin metadata: `Repository` and `ChangelogURL` point to the MLAstroRPA fork, and the plugin description states that this is a fork of TPPA (with a **Fork page** link).

### MLAstroRPA Alt-axis overshoot
- Added a master **"Enable overshoot"** option for the MLAstroRPA system, plus **"Run overshoot for moving Up"** / **"Run overshoot for moving Down"** checkboxes, each with its own arcminute field.
- When enabled for the on-screen correction direction, the Alt axis corrects the full 100% of the error and then moves a fixed overshoot amount past the target, configurable from 0–15 arcminutes (0 = correct the full error with no overshoot).
- The direction used for overshoot selection follows the direction reported on screen by the solve (`CurrentMountAxisAltitudeErrorDirection`), not the motor command direction (which may be flipped by the automated direction correction).

### Removed
- Removed the **"Log polling data"** checkbox and the serial data logging feature (`LoggingSerialPort` no longer logs TX/RX data lines; only connection lifecycle is logged).

### MLAstroRPA system integration
- Added `MLAstroRPA` as a new polar alignment system option alongside None / UPAS / OAPA in the plugin settings ComboBox.
- Added `UniversalPolarAlignmentMLAstroRPA` — a dedicated driver for the MLAstro Robotic Polar Alignment hardware communicating over serial (USB/WiFi bridge).
- Added `UniversalPolarAlignmentMLAstroRPAVM` — the ViewModel layer for the MLAstroRPA system, wired into the options panel with Connect / Disconnect controls and a live status text field.
- Added an `MLAstroRPA` GroupBox in the options panel, visible only when MLAstroRPA is the selected system.
- Added protocol documentation at `PolarAlignment/MLAstroRPA/protocol.md` describing the serial command set.

### MLAstroRPA driver details (`UniversalPolarAlignmentMLAstroRPA`)
- Performs a handshake on connect by sending `[MLAstroRPA-TC]` and verifying the device replies `ok` before accepting the port.
- Clears the serial input buffer after opening the port to avoid stale data.
- Translates TPPA arc-minute correction values into the device's DMS (degrees / minutes / seconds / direction) format before sending.
- Sends a structured align command (`AzED/AzEM/AzES/AzDi/AlED/AlEM/AlES/AlDi/AAll`) and waits for an `ok` acknowledgement.
- After sending the align command, starts an internal polling loop that queries `?` every 300 ms and parses the status response to detect `READY` or `ALIGN_COMPLETED`, then signals completion automatically.
- Supports `Abort` by sending `STOP:1` and immediately cancelling any in-progress alignment `TaskCompletionSource`.
- Overrides `MoveAbsolute` to calculate the delta from the current position and delegate to `MoveRelative`.
- Parses device telemetry with a dedicated regex matching the `<STATUS|Mpos:x,y|>` frame format.
- Overrides `UpdateStatus` to use `StatusQueryCommand` (`?`) and `ReadStatusResponse`.

### UniversalPolarAlignmentBase extensibility changes
- Made `MoveRelative`, `MoveAbsolute`, `UpdateStatus`, and `Abort` **virtual** so subclasses can override the full movement and status-polling behaviour.
- Added virtual hook `OnPortOpened(SerialPort)` called immediately after the port is opened, allowing subclasses to perform a protocol handshake before the first status query.
- Added virtual `IsStatusResponseValid(string)` so subclasses can define their own validation logic during port scanning.
- Added virtual `ReadStatusResponse(SerialPort)` with a configurable `StatusResponseLineCount` property.
- Added virtual `StatusQueryCommand` property (default `"?"`) so subclasses can change the polling command without reimplementing `UpdateStatus`.
- Extracted `TryApplyStatusLine(string)` as a protected helper that runs the regex, updates `Status` / `XPosition` / `YPosition` / `ZPosition`, and returns a bool — reusable by any override.
- Changed `Status`, `XLastDirection`, `YLastDirection`, `ZLastDirection`, `XPosition`, `YPosition`, `ZPosition`, and `semaphore` from `private` to `protected` so subclasses have direct access when needed.
- Added a no-op virtual `Abort(CancellationToken)` implementation to satisfy the `IPolarAlignmentSystem` interface; subclasses override it to send a hardware stop command.

### UniversalPolarAlignmentBaseVM extensibility changes
- Added virtual `TestConnectStatus` string property and `TestConnectCommand` relay command so subclasses (e.g. MLAstroRPA) can expose a lightweight connection test with a status message without reimplementing the full connect flow.
- Added `Abort` relay command in the base VM that calls `upa.Abort(token)`, wired to both the UI and the automated adjustment flow.
