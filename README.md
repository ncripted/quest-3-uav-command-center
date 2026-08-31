# Quest Command Center

Quest Command Center is a local-first Meta Quest 3 command station for robotic
vehicles. The first vehicle is the DJI Spark. The product boundary is the command
center; Spark-specific SDK types and lifecycle code remain inside an adapter.

## Current status

Milestone 0 research and the first hardware compatibility slices are complete.
Unity Personal is active, the Unity 2022.3 project is generated, its Quest/OpenXR
dependencies are pinned, all 72 edit-mode tests pass, and an ARM64 Android
development APK containing DJI MSDK V4.18 builds successfully.
The initial scene contains an XR-tracked camera, Quest 3 color passthrough, and a
DJI-GO-inspired world-space FPV HUD. Live video occupies a dedicated unobstructed
16:9 viewport, with readable telemetry and flight/camera controls in fixed regions
around it. Only the targeting reticle remains over the live image. The operator
can see their physical desk/computer around the command surface. It has an explicit
offline UI mode. The Touch controllers produce generic Mode 2 intent, while the
MockDrone runtime is not implemented yet.

Primary altitude, horizontal/vertical speed, heading, and control state are shown
as large labeled values. A separate detailed card reports connection, flight mode,
failsafe, GPS validity and position, attitude, directional velocity, battery,
camera/storage, gimbal state, and DJI home-point validity. Missing or unsupported data is labeled
`Unavailable` rather than rendered as a plausible zero.

Direct Quest execution has loaded and registered MSDK, discovered the Spark through
the Spark remote controller, and reported AirLink component changes. The current
DJI bridge subscribes to read-only flight-controller, battery, camera, and gimbal
state callbacks and translates them to a DJI-independent `VehicleTelemetry` model.
It also receives the Spark's encoded primary video feed, decodes it with DJI's
hardware-decoder path, and presents a diagnostic 640x360 live texture in the
Floating Monitor. Its only command APIs are a narrowly guarded camera slice that
can request one onboard photo or start/stop onboard Spark recording; it
requires camera/storage health, permits only one pending command, and reports
every completion error. A guarded gimbal backend is limited to absolute pitch from
−20° to 0°, requires landed/motors-off and healthy gimbal telemetry, and serializes
one request. Its temporary button trigger was removed to avoid conflicting with
the authoritative controller map. Pitch-only headset tracking is active only while
Left Trigger is held and is smoothed, deadbanded, and rate-limited.

Takeoff, landing, Return Home, brake/hover, takeover, and continuous Mode 2 manual
control are integrated into the standard Quest application. The same controller
map drives either the desktop simulator or, after a live Spark is selected and the
on-headset preflight is completed, the real adapter. Takeoff uses both sticks held
down and inward; X hold requests RTH, A hold requests landing, B is brake/hover,
right grip is the movement dead-man, and left grip requests takeover. Real control
cannot unlock until fresh telemetry, battery, GPS/home, failsafe, grounded-state,
Virtual Stick, and centered-stick checks pass, followed by a deliberate
three-second both-grips-plus-A confirmation and control release.

The first integrated Milestone 6 slice is implemented. An explicit control-mode
state machine, single-authority command arbiter, timestamped command envelopes,
manual-takeover authority epochs, fail-neutral command expiry, and conservative
development safety limits sit in the only path to DJI Virtual Stick. The native
adapter independently neutralizes stale/unauthorized commands and disables its
control mode on heartbeat, telemetry, failsafe, disconnect, or send failure.
Flight sessions are stored locally as synchronized telemetry and event JSONL.

A simulation-only Unity desktop application is also available for Milestone 6
controller testing. The Quest app sends safety-approved normalized commands
through a localhost TCP connection carried over USB/ADB to a detailed 3D simulator
window on the PC. It includes a procedural Spark-style aircraft, animated rotors
and attitude, environment, chase/FPV/top/orbit cameras, telemetry HUD, flight trail,
geofence, and simulated takeoff/land/RTH. The relay and simulator assemblies have
no DJI adapter reference. Both sides independently neutralize stale control. The
earlier Python 2D simulator remains as a lightweight fallback.

The current compatibility decision is **promising but not production-proven**:
registration, discovery, telemetry, encoded video receipt, hardware decode, and a
Unity preview work directly on Quest. A zero-copy render path, endurance,
reconnection, and later control gates remain. See
[docs/DJI_QUEST_COMPATIBILITY.md](docs/DJI_QUEST_COMPATIBILITY.md).

## Repository layout

```text
config/                     checked-in, non-secret configuration examples
docs/                       architecture, safety, compatibility, and formats
sim/                        localhost Python desktop drone simulator
src/QuestCommandCenter/     Unity 2022.3 Quest application
native/DjiMsdkV4Plugin/     minimal Android library wrapping DJI MSDK V4
bridge/DjiAndroidBridge/    fallback Android bridge, created only if needed
tests/                      test-suite ownership and hardware-test boundary
```

The intended Unity runtime module layout is documented in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). The current source slice establishes
the `VehicleCore`, `UI`, editor setup, scene, and test assemblies; other modules
will be added only as their first tested behavior is implemented.

## Safety boundary

- No build or automated test may start motors, take off, or move a real aircraft.
- Hardware tests are separate, opt-in, and operator supervised.
- Releasing manual authorization or receiving stale commands results in neutral
  command output; the previous command is never replayed.
- One command arbiter owns flight authority. Manual takeover outranks autopilot.

See [docs/SAFETY.md](docs/SAFETY.md) before adding any real-aircraft code.

## First development gates

1. Run the desktop simulator with Spark and its remote controller powered off.
2. Verify controller-confirmed simulated takeoff, landing, and RTH.
3. Complete the MockDrone and command-timeout acceptance gates.
4. Review the integrated field checklist and use the installed app only at a clear
   outdoor test site.
5. Begin with low-altitude hover, brake/dead-man checks, landing, then bounded axes.

No cloud service, PC, phone, or bridge is part of the intended direct runtime.
If direct MSDK execution fails, only the Spark backend changes; the Unity
application, mission engine, safety manager, and UI remain unchanged.

## Clone and local secrets

The repository contains source, tests, Unity project settings, documentation,
and the small compiled Unity/DJI bridge. It intentionally excludes Unity caches,
APK/desktop builds, downloaded DJI compile dependencies, flight-session data,
editor settings, signing files, and local agent metadata.

After cloning:

1. Copy `.env.example` to `.env.local`.
2. Put the package-bound DJI MSDK V4 key in `.env.local`; never commit that file.
3. Run `./native/DjiMsdkV4Plugin/build-bridge.sh` when the Java bridge changes.
4. Open `src/QuestCommandCenter` with Unity 2022.3.62f3.

The Android build processor injects the key only into generated, ignored Gradle
output. Core flight, telemetry, video, missions, and recording remain local-first.
