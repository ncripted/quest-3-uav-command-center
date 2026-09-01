# Safety model

Status: Milestone 0 baseline. These values and behaviors require simulator,
bench, and controlled-field validation before flight.

## Core invariants

1. Exactly one arbiter owns flight-command authority.
2. Every movement command has a source, sequence, creation time, expiry, and
   authority epoch.
3. Expired controller, mission, bridge, or telemetry data is rejected.
4. Releasing manual dead-man authorization produces a new neutral command.
5. The last movement command is never blindly repeated after freshness expires.
6. Manual takeover neutralizes autonomous output before authority transfers.
7. Takeoff, land, RTH, and emergency behavior require deliberate authorization.
8. A normal automated build/test cannot start motors or request aircraft movement.
9. DJI's flight controller remains responsible for low-level stabilization; this
   software never attempts motor-level control.
10. Every transition, limit, rejection, and recovery action is logged.

## Indoor Development Profile

Initial defaults are intentionally restrictive:

| Setting | Default |
| --- | ---: |
| normalized horizontal command | 0.35 |
| normalized vertical command | 0.25 |
| normalized yaw command | 0.30 |
| horizontal velocity limit | 0.5 m/s |
| vertical velocity limit | 0.3 m/s |
| yaw-rate limit | 20 deg/s |
| relative altitude limit | 2.0 m |
| radius from valid home | 10 m |
| controller input stale | 150 ms |
| command output expiry | 250 ms |
| telemetry warning | 500 ms |
| telemetry failsafe | 1000 ms |
| low battery warning | 30% |
| critical battery threshold | 20% |

RTH and GPS autopilot are disabled by default indoors. Invalid GPS never silently
becomes `(0, 0)` and cannot satisfy a navigation completion condition. Software
limits supplement rather than replace DJI firmware limits.

Battery thresholds are initial product settings, not universal safe values. The
configured critical action depends on whether the aircraft is airborne, altitude,
GPS/home validity, environment profile, link state, and DJI-reported battery/RTH
state. The UI must tell the operator which recovery action is configured.

The integrated Milestone 6 implementation enforces the normalized command,
altitude, controller-freshness, telemetry-freshness, flight-state, connection,
battery, control-mode, and authority-epoch gates before adapter dispatch. The
Spark adapter converts only the approved normalized output to body-relative
velocity and angular-velocity units. Radius/obstacle constraints remain follow-up
work; initial real-flight unlock therefore additionally requires valid GPS and a
DJI-confirmed home point.

## Desktop simulation boundary

The development controller relay connects only to `127.0.0.1:8765`, normally
forwarded to the PC with `adb reverse`. It has no DJI adapter or MSDK reference.
Quest-side authorization, arbitration, telemetry freshness, altitude, and Indoor
Development limits run before serialization. The Unity desktop model validates
every axis, ignores old sequences, neutralizes unauthorized/brake input, and stops
stale movement after 250 ms. Its kinematics are for application testing and are
not represented as aerodynamically accurate. Spark and its remote controller
should remain powered off during desktop controller tests so existing
camera/gimbal features are isolated.

## Command shaping

Safety processing is ordered and observable:

1. validate numeric range and finite values;
2. validate authority and state;
3. validate command/input age;
4. apply dead zone and deliberate dead-man gate;
5. apply slew/jerk-limited smoothing;
6. apply horizontal, vertical, and yaw limits;
7. apply altitude/radius/obstacle directional constraints when valid;
8. convert to adapter-specific physical units;
9. timestamp, dispatch, and record the applied command.

Raw and applied commands may both be logged, but only the applied command reaches
the adapter. A rejected command includes a reason code.

## Manual controls

Default Mode 2 intent:

| Quest control | Intent |
| --- | --- |
| left stick Y | vertical velocity |
| left stick X | yaw rate |
| right stick Y | forward/backward |
| right stick X | left/right |
| Y | take photo |
| both sticks down/inward while simulator is grounded | hold 1 s for simulated takeoff |
| X hold while simulator is airborne | hold 1 s for simulated RTH |
| left trigger | camera modifier / pitch-only head-gimbal mode |
| left grip | immediate manual takeover; output remains neutral |
| A tap | select/confirm; never a casual direct takeoff |
| A hold while simulator is flying | hold 1 s for simulated landing |
| B press | immediate brake-to-hover intent; never motor stop |
| right trigger | spatial UI selection |
| right grip hold | manual-flight dead-man authorization |

Controller loss, focus loss, application pause, stale sampling, or right-grip release
revokes manual authorization. The command scheduler emits neutral while connected
and safe to do so, then follows the configured link/state recovery behavior.

## Manual takeover transaction

Takeover is atomic from the application's perspective:

1. increment the authority epoch so queued autonomous commands become invalid;
2. pause the mission by default (cancel if configured);
3. write a fresh neutral autonomous command;
4. transfer the arbiter to manual input;
5. publish `MANUAL` application state and high-priority UI feedback;
6. log mission step, old/new authority, reason, and latency.

The target from grip activation to authority epoch change is one control tick
(50 ms at 20 Hz) or faster. Adapter/backend commands from an older authority epoch
are discarded.

## Failsafe inputs

The Safety Manager evaluates:

- command and controller freshness;
- telemetry freshness and validity;
- backend/product/component connection;
- control-state consistency and Virtual Stick availability;
- horizontal/vertical/yaw limits;
- relative altitude and GPS radius when valid;
- battery and DJI-reported RTH/landing states;
- GPS quality for actions that require position;
- Spark forward vision warnings only in the sensed direction;
- recording/logging health as an alert, not normally a flight-control failure.

Spark obstacle data is an aid, not complete collision avoidance. Forward warnings
may reduce or reject positive forward commands. They do not imply rear, side, or
overhead perception, and cannot authorize autonomous motion into an unobserved
direction.

## Recovery policy

Recovery is a decision table, not one global action:

- stale manual input: neutral/hover intent and revoke manual authorization;
- stale command producer: neutral and remove that producer from arbitration;
- telemetry warning: alert and limit new autonomous actions;
- telemetry timeout: enter `FAILSAFE`; select a preconfigured recovery using last
  validated flight state and DJI's still-current onboard behavior;
- connection loss: stop generating normal commands and surface DJI-configured
  link-loss behavior; never pretend the command station can still control it;
- critical battery: reject new mission steps and run the explicit profile action;
- invalid GPS: block GPS-dependent steps/RTH assumptions; do not fabricate home;
- obstacle warning: constrain only motion toward the supported sensing direction.

Any fallback Android bridge independently neutralizes Virtual Stick output when
Quest heartbeats or command expiry fail. It does not run the mission.

The direct Quest MSDK V4.18 bridge implements the same independent boundary: a
20 Hz native scheduler, 150 ms movement-command
expiry, neutral substitution, and mode disable after 500 ms without a fresh
heartbeat. It also disables on stale flight telemetry, DJI failsafe, component
disconnect/rebind, or send failure. The backend is connected to Touch input only
through the integrated preflight, authority arbiter, Safety Manager, and normalized
adapter interface. Normal launches begin with a read-only Virtual Stick status
getter and keep real control locked. A successful getter marks the API ready; it
does not mark command sending available. DJI documents command availability as
false until the mode has been enabled, so the enable callback and post-enable
availability must both succeed before `READY`. Unlock requires ten visible preflight checks,
centered sticks, a three-second both-grips-plus-A hold, and release before Virtual
Stick enable is requested. The separately approved one-shot grounded diagnostic
additionally requires fresh flight telemetry, failsafe clear, motors off, and
`isFlying=false`; its 2026-08-28 hardware run enabled the mode with neutral-only
output and verified the automatic timeout disable.

A failed mode-enable callback does not authorize control and does not erase an
earlier successful read-only API probe. The aircraft remains locked, the exact
DJI error is displayed and logged, and retry requires the operator to repeat the
full deliberate unlock chord. The native stale-command timer begins only after a
successful enable callback, preventing SDK callback latency from consuming the
500 ms heartbeat window before Unity can acknowledge control.

The hardware shadow mode is a separate, launch-only dry-run path. It reads live
Spark telemetry and Quest controller input, then exercises the generic command
arbiter, Indoor Development Safety Profile, stale-input handling, action
validation, and synthetic shadow flight phase. It has no DJI command-adapter
reference and logs every output with `SDK_DISPATCH=BLOCKED`. Virtual Stick,
takeoff, landing, RTH, and movement SDK calls remain unavailable in this mode.
The synthetic phase exists only to validate takeoff, airborne movement, RTH, and
landing controller gestures while the physical aircraft remains grounded. A real
motors-on or flying telemetry state can never be projected as grounded.

The 2026-08-29 stationary Spark hardware-shadow run passed the complete manual
control path without dispatching a flight command. Live product/telemetry binding,
manual takeover, all four stick axes, Indoor Profile clamping, dead-man
neutralization, brake/hover, synthetic takeoff, and synthetic landing were
observed. RTH was correctly rejected because DJI had not confirmed a home point.
Every sampled live state reported motors off and `isFlying=false`, and every
shadow command reported `SDK_DISPATCH=BLOCKED`.

## Deliberate dangerous actions

Takeoff, land, RTH, and emergency/stop behavior use a two-stage authorization:

1. request creates a short-lived pending action showing prerequisites, effect, and
   abort path;
2. a distinct confirmation input within the window issues an authorization token.

Confirmation is invalidated by state, connection, focus, telemetry, or authority
change. Emergency behavior is named for its actual configured effect; “Stop” must
not ambiguously mean motor stop in flight.

The current high-level Spark gates are intentionally conservative:

- request and confirmation require connected flight telemetry no older than
  500 ms and DJI failsafe clear;
- takeoff requires landed/motors-off and reported battery at least 30%;
- landing requires DJI-reported flying state;
- RTH requires DJI-reported flying state and `isHomeLocationSet()`;
- Java repeats the checks immediately before `startTakeoff`, `startLanding`, or
  `startGoHome`;
- a successful SDK callback means DJI accepted the request, not that the physical
  maneuver completed; the operator must monitor live telemetry and the aircraft.

## Hardware-test policy

Hardware tests are never discovered by the normal Unity test runner. Each has a
manifest containing `requiresPoweredAircraft`, `requiresPropellersRemoved`,
`canMoveGimbal`, `canStartMotors`, `canMoveAircraft`, operator checklist, and abort
instructions.

Before a test with any possible physical effect, the application must stop and
state exactly what it will do. Flight tests begin only in Milestone 6 after
connection, telemetry, FPV, camera, recording, timeout, and MockDrone behavior are
stable.

Onboard camera commands are serialized separately from future flight authority.
Photo and start-record requests require a connected camera and healthy inserted
storage; stop-record remains available as a recovery action if storage later
reports an error. A missing card, active command, invalid recording state, link
loss, or stale callback produces a visible rejection/failure and a log entry.
Camera input also has a 1.5-second cooldown to suppress duplicate XR events.
Camera controls never imply gimbal or aircraft movement.

The guarded gimbal backend is isolated from future flight authority. It accepts
only absolute pitch targets from −20° through 0°, explicitly leaves roll and yaw
unchanged, and requests a 0.35-second move. Both managed and Android layers require
connected gimbal state, confirmed landed/motors-off flight state, no calibration
or overload, telemetry no older than 500 ms, and no pending command.
Left-trigger-modified headset tracking applies
smoothing, a 2° target deadband, and a four-command-per-second ceiling. Releasing
the trigger or pausing the application holds the last bounded absolute pitch.
