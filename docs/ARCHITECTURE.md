# Architecture

Status: Milestone 6 software-only control foundation with read-only DJI hardware
integration, 2026-08-27.

## Design goals

Quest Command Center is a local-first command station with a capability-driven
vehicle model. The first adapter supports one DJI Spark, but the application core
must not assume that every vehicle flies, has GPS, has a camera, supports RTH, or
can execute onboard missions.

The architecture optimizes for four properties:

1. one authoritative source of flight commands;
2. deterministic fail-neutral behavior;
3. DJI types confined to an Android/Spark boundary;
4. identical application behavior with a local MSDK backend or remote bridge.

## Runtime topology

```text
Quest UI / spatial presentation
          |
XR Input -> normalized intent
          |
ControlModeStateMachine ---- MissionEngine / Autopilot
          |                         |
          +----> CommandArbiter <---+
                      |
                 SafetyManager
                      |
             FlightControllerService
                      |
                DjiSparkAdapter
                      |
               IDjiSparkBackend
                /            \
     LocalMsdkBackend      RemoteBridgeBackend
             |                    |
     Android MSDK plugin      local protocol
             |                    |
        DJI MSDK V4       Android bridge + MSDK V4
              \                  /
                    DJI Spark
```

Only `CommandArbiter` selects authority. Manual and autonomous producers never
write to the adapter independently.

The current Milestone 6 slice implements this boundary as pure `VehicleCore`
classes: `ControlModeStateMachine`, `FlightCommandArbiter`, timestamped
`FlightCommandEnvelope`, and `FlightSafetyManager`. They are intentionally not
wired to the Spark adapter yet, so normal builds and tests cannot emit Virtual
Stick commands.

For desktop controller testing, `DesktopSimulatorRelay` consumes the generic Quest
intent source, submits it through those core classes, and serializes only the
safety decision to localhost TCP port 8765. `adb reverse` carries that socket over
USB to the standalone Unity desktop simulator. The relay references `VehicleCore`
but not the DJI adapter. `QuestCommandCenter.DesktopSimulator` owns the local TCP
server, deterministic kinematic model, procedural 3D presentation, and telemetry
HUD without referencing the Spark adapter. It returns a 10 Hz heartbeat/telemetry
message used by the Quest-side freshness gate and independently expires received
movement after 250 ms. The Python implementation in `sim/` remains a minimal
protocol-compatible fallback.

## Unity modules

The planned root is `Assets/QuestCommandCenter` with one runtime assembly per
meaningful dependency boundary:

| Module | Responsibility | May depend on |
| --- | --- | --- |
| `VehicleCore` | vehicle identity, capabilities, commands, status, manager | shared domain only |
| `Telemetry` | normalized snapshots, freshness, history | `VehicleCore` |
| `Safety` | limits, validation, timeouts, recovery decisions | `VehicleCore`, `Telemetry` |
| `MissionEngine` | versioned mission model and execution lifecycle | `VehicleCore`, `Telemetry` |
| `Autopilot` | heading/altitude/timed-movement controllers | `VehicleCore`, `Telemetry` |
| `XRInput` | Touch mapping to normalized operator intent | Unity Input System/OpenXR, `VehicleCore` |
| `Video` | video-source contracts, frame/stream statistics, presentation | `VehicleCore` |
| `Recording` | synchronized session capture | `Video`, `Telemetry`, `Logging` |
| `Logging` | structured timestamped events and error records | shared domain only |
| `Persistence` | config and mission storage | mission/domain contracts |
| `Adapters/DjiSpark` | Spark behavior and backend selection | domain contracts, Android boundary |
| `UI` | render application state and emit intents | application-facing read models only |

Tests reference runtime assemblies; runtime assemblies never reference test or UI
assemblies. `UI` receives immutable read models and never sees a DJI object.

The current Floating Monitor FPV HUD consumes `IStatusSource`,
`IVehicleTelemetrySource`, `IVideoTextureSource`, and `ICameraControl`. It contains
no adapter reference. A dedicated 16:9 viewport reserves the center for video;
persistent telemetry, camera controls, flight actions, alerts, and primary status
live in opaque regions outside it. Only the targeting reticle is persistently
drawn over video. Preflight confirmation may temporarily cover the viewport while
real-flight control is locked, then disappears after the gate is satisfied. The
layout therefore renders the same way for the local Spark adapter, future remote
bridge, or MockDrone. With no vehicle it enters an explicit offline UI mode and
flight actions remain visible but locked.

## Vehicle contracts

The initial contracts should stay small and asynchronous. Names below are design
names, not DJI SDK method names.

```csharp
public interface IVehicle
{
    VehicleId Id { get; }
    VehicleCapabilities Capabilities { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    VehicleStatus Status { get; }
    event Action<TelemetrySnapshot> TelemetryUpdated;
}

public interface IAerialVehicle
{
    Task RequestTakeoffAsync(CommandAuthorization authorization,
        CancellationToken cancellationToken);
    Task RequestLandAsync(CommandAuthorization authorization,
        CancellationToken cancellationToken);
    Task RequestReturnHomeAsync(CommandAuthorization authorization,
        CancellationToken cancellationToken);
    ValueTask SetFlightCommandAsync(NormalizedFlightCommand command,
        CancellationToken cancellationToken);
}

public interface IVideoSource
{
    Task StartVideoAsync(CancellationToken cancellationToken);
    Task StopVideoAsync(CancellationToken cancellationToken);
    VideoStreamState State { get; }
}

public interface ICameraControl
{
    Task StartRecordingAsync(CancellationToken cancellationToken);
    Task StopRecordingAsync(CancellationToken cancellationToken);
    Task TakePhotoAsync(CancellationToken cancellationToken);
    Task SetGimbalPitchAsync(float degrees, CancellationToken cancellationToken);
}
```

An implementation may expose interfaces only when the corresponding capability is
available. A capability record includes both support and current availability so a
camera that exists but is temporarily disconnected is not reported as absent.

The current camera slice implements `ICameraControl` without exposing DJI types.
`CameraCommandSession` permits one pending operation, rejects photo/start-record
when storage is missing or unhealthy, allows stop-record as a recovery operation,
and ignores stale completion IDs. Quest Y tap requests one photo; holding Y for
one second toggles onboard recording. The adapter maps those intents to the exact
MSDK V4 camera sequence and publishes pending/success/failure state to the UI.
The input layer also imposes a 1.5-second cooldown so duplicate XR-device events
cannot generate repeated photo intents. This path cannot move the gimbal, motors,
or aircraft.

## Normalized commands and authority

`NormalizedFlightCommand` contains roll, pitch, yaw, and vertical values in
`[-1, +1]`, plus a sequence number, monotonic creation timestamp, source, and
authorization state. It contains no DJI units.

The command path is fixed:

```text
Touch controller
  -> XRInput mapper
  -> normalized command producer
  -> CommandArbiter
  -> SafetyManager
  -> FlightControllerService
  -> DjiSparkAdapter
  -> backend
```

The Spark backend maps normalized values to explicitly selected DJI Virtual Stick
control modes and conservative physical limits. The mapping is unit tested and
logged at configuration time. DJI's documentation requires Virtual Stick commands
to be sent at 5–25 Hz; the initial service target is 20 Hz. A missed input deadline
generates a fresh neutral command rather than repeating the last movement command.

Authority priority is:

```text
FAILSAFE recovery > manual takeover > authorized manual > autopilot > neutral
```

`FAILSAFE` does not mean “always RTH.” The recovery action is profile- and
state-dependent. Indoors, blind RTH can be less safe than hover or controlled land.

## Control-state machine

Required states are `DISCONNECTED`, `PREFLIGHT`, `READY`, `MANUAL`, `ASSISTED`,
`AUTOPILOT`, and `FAILSAFE`. Every transition has a reason, monotonic timestamp,
previous state, next state, and correlation ID.

Representative transitions:

- `DISCONNECTED -> PREFLIGHT`: backend connected and initial telemetry received.
- `PREFLIGHT -> READY`: required checks and operator acknowledgements pass.
- `READY -> MANUAL|ASSISTED`: dead-man authorization is active.
- `READY|MANUAL|ASSISTED -> AUTOPILOT`: mission is validated, confirmed, and owns
  authority.
- `AUTOPILOT -> MANUAL`: takeover input; mission pauses by default and autonomous
  output is neutralized before authority changes.
- `* -> FAILSAFE`: a configured critical condition occurs.
- `* -> DISCONNECTED`: product/backend connection is lost.

Transitions that can move the aircraft require a deliberate confirmation token;
UI button presses cannot call vehicle operations directly.

## Telemetry model

Telemetry is an immutable snapshot with:

- vehicle ID, session time, source time when available, receive time, and sequence;
- a connection/quality envelope;
- optional component groups for flight, battery, GPS, home, camera, gimbal, link,
  and obstacle data;
- validity and freshness for values used by safety or control.

Optional groups carry explicit availability/validity flags; placeholder numeric
values must never be consumed when their group or value is invalid. Coordinate
frames and units are part of field definitions. The initial Spark slice translates
flight and battery state into a DJI-independent `VehicleTelemetry` snapshot and
limits Unity publication to 5 Hz. Spark normalization begins from verified MSDK V4
callbacks such as `FlightController.setStateCallback`,
`Battery.setStateCallback`, `Camera.setSystemStateCallback`, and
`Gimbal.setStateCallback`; support is still checked at runtime.

## Video architecture

Acquisition and presentation are separate:

```text
IVideoSource -> encoded stream/decode backend -> texture provider -> FPV presenter
```

The direct backend receives encoded video through DJI `VideoFeeder`. The preferred
Quest path is hardware decode to an Android `SurfaceTexture`/external GPU texture,
updated on the render thread through a native rendering plugin. The first spike
uses OpenGL ES 3 because external OES textures have a clearer Unity interop path;
Vulkan is evaluated after the path works. A CPU YUV callback is permitted only as
a diagnostic fallback, never as the target FPV architecture.

The current verified diagnostic implementation keeps acquisition and presentation
separate through `IVideoTextureSource`. On Android it feeds DJI's primary encoded
stream to `DJICodecManager`, converts the callback's planar 1024x768 YUV420 frame
to a center-cropped 640x360 RGBA preview at a target 10 Hz, retains only the newest
completed frame, and lets Unity poll/upload it without waiting on decode. The
Floating Monitor presenter consumes only the generic texture-source contract. A
30-second stationary sample displayed 244 frames (8.11 Hz; 223 ms maximum gap).
This is evidence that direct FPV is viable, not the production performance design:
CPU conversion, JNI frame copying, and the Unity upload must be replaced or
measured against the external-OES/native-render path before Q4 acceptance.

The remote backend forwards the encoded stream and decodes on Quest. Presentation
starts with Floating Monitor Mode and depends only on a texture provider. The
current Quest shell renders that monitor over Meta OpenXR color passthrough so the
operator can retain awareness of the physical desk and computer; passthrough is a
presentation concern and does not change vehicle, video-source, or safety APIs.

## Threading

- Unity main thread: input sampling, state publication, scene/UI mutation.
- control scheduler: fixed-rate arbitration, safety, and command dispatch.
- Android callbacks: copy or translate the minimum immutable data, then enqueue.
- video/decode thread: encoded input and MediaCodec/native texture work.
- recording writer: bounded queues and asynchronous file writes.
- mission/autopilot worker: deterministic tick driven by monotonic time.

Queues are bounded. Overflow produces metrics and an event; telemetry may coalesce
to the newest snapshot, but safety events and state transitions are never silently
dropped. Unity rendering never waits for network, decode, or file I/O.

## Time and recording

A flight session captures one monotonic epoch at session creation plus a wall-clock
timestamp for human correlation. All internal ordering and timeouts use monotonic
nanoseconds/ticks; wall time is metadata only.

```text
FlightSession/<session-id>/
  video/
  telemetry.jsonl
  events.jsonl
  mission.json
  metadata.json
```

Every JSONL record includes `schemaVersion`, `sessionTimeNs`, `sequence`, and
`vehicleId`. Versioned envelopes allow a later replay engine to read historical
sessions without rewriting them.

## Multi-vehicle preparation

`VehicleManager` owns a collection keyed by `VehicleId`, plus an explicitly
selected operator-focus vehicle. Services take a vehicle ID and do not use a
global Spark singleton. Milestone 1 creates only one MockDrone; simultaneous
vehicle scheduling and UGV behavior are not implemented.

## Error handling

An operational error becomes all three of:

1. a structured log event with component, code, cause, and correlation ID;
2. application state with severity and recoverability;
3. an operator alert when action is required.

Missing optional telemetry is a capability/availability condition, not an
exception. Exceptions crossing JNI or worker boundaries are caught at the boundary
and converted into the same error model.
