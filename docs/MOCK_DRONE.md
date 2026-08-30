# MockDrone design

Status: Milestone 0 design; implementation begins in Milestone 1.

## Purpose

`MockDroneAdapter` lets the Quest application, control state machine, safety
manager, mission engine, recording, and UI operate without DJI MSDK or powered
hardware. It implements the same generic capabilities as the Spark MVP and does
not imitate DJI classes.

## Boundaries

```text
UI / XRInput / MissionEngine
            |
      application services
            |
       IVehicle contracts
            |
      MockDroneAdapter
       /      |       \
 simulator  failures  placeholder video
```

The simulator is pure C# and independent of `MonoBehaviour`, frame rate, wall
clock, and Unity physics. A thin Unity host advances it from a deterministic clock
and publishes immutable state.

## Model

State includes:

- disconnected, connecting, connected, and connection-failed lifecycle;
- `DISCONNECTED`/preflight/ready-visible aircraft facts;
- battery percent and drain accumulator;
- home and current latitude/longitude;
- relative altitude and vertical velocity;
- heading/yaw rate;
- body-commanded horizontal velocity and north/east ground velocity;
- motors-on, flying, taking-off, landing, and RTH phases;
- camera recording/photo counter and gimbal pitch;
- link quality, GPS validity/satellites, and telemetry sequence;
- last accepted command sequence/time/authority epoch.

Initial kinematics are deliberately simple:

- fixed 20 Hz control integration and 10 Hz telemetry publication;
- first-order velocity/yaw response with conservative acceleration limits;
- heading normalized to `[0, 360)`;
- local north/east displacement converted to latitude/longitude with a documented
  small-distance approximation around the configured home;
- ground and altitude clamps prevent the simulated vehicle moving below ground;
- takeoff/landing/RTH are state machines, not instantaneous teleports;
- no collision or advanced aerodynamics claim.

The same normalized-command mapping and Safety Manager used by the real adapter
run before the mock. The mock does not hide invalid application behavior by adding
extra undocumented limits.

## Commands

The adapter initially supports:

- connect/disconnect;
- takeoff, land, and RTH state-machine requests;
- normalized flight command;
- camera record start/stop and photo;
- gimbal pitch;
- placeholder video start/stop.

Arm/prepare is represented by preflight readiness for the Spark MVP. There is no
motor-level command.

Every accepted command returns an asynchronous result after a configurable,
deterministic latency. Commands can fail with the same application error categories
as a real adapter: unavailable, invalid state, unsupported, timeout, connection,
rejected, or internal.

## Dead-man and timeout behavior

The mock records command expiry and stops applying movement when:

- the command timestamp is too old;
- its authority epoch is no longer current;
- no fresh command arrives within the configured 250 ms default;
- connection or control mode changes;
- manual dead-man authorization is absent.

On timeout it converges toward zero velocity/hover; it never reuses the prior
movement command. This behavior is observable through telemetry and an event so
the timeout tests verify the whole pipeline.

## Failure injection

Failures are deterministic and selected by configuration or test API:

- connect fails immediately;
- connect times out;
- disconnect at simulation time/command sequence;
- telemetry freezes while connection appears present;
- command acknowledgements time out;
- battery drains to low/critical thresholds;
- GPS becomes invalid or degrades;
- link quality degrades;
- camera/video becomes unavailable;
- RTH, takeoff, land, or mission command is rejected;
- placeholder video stalls.

Randomized scenarios use a recorded seed. A test failure must be reproducible from
seed, config, command/event log, and simulator version.

## Placeholder video

The mock video source generates a GPU-friendly Unity texture with:

- color bars or a simple synthetic horizon/grid;
- frame sequence and monotonic session time visibly encoded;
- current simulated mode/altitude/heading overlay;
- configurable frame rate, stall, and resolution change.

It does not allocate a new full-size texture or byte array per frame. The same FPV
presenter consumes mock and DJI texture-provider outputs.

## Battery and GPS defaults

Battery drain is time- and phase-based: minimal while connected/on ground, greater
with motors/flying, and configurable for accelerated tests. No battery value goes
below zero.

GPS defaults to valid at a Detroit-area example coordinate from checked-in config,
but tests may supply any non-sensitive coordinate. Home is captured at simulated
takeoff. Invalid GPS removes the coordinate/home-dependent validity flags rather
than publishing zero coordinates.

## Required tests

- deterministic connection success/failure/timeout;
- takeoff/landing/RTH state transitions without discontinuity;
- battery drain and threshold events;
- command expiry and no stale-command replay;
- normalized movement and heading wraparound;
- GPS displacement and invalid-GPS optional fields;
- manual takeover invalidates autopilot authority in one control tick;
- telemetry sequence/freshness and freeze injection;
- camera/gimbal capability and rejection behavior;
- video sequence/stall signaling;
- session log replay produces the same simulator state for a fixed version.

