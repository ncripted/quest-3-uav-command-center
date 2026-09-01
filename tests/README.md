# Test boundaries

Planned suites:

- `EditMode`: pure C# domain, mission, safety, arbitration, serialization, and
  deterministic MockDrone tests.
- `PlayMode`: Unity input/UI/video-placeholder integration tests.
- `AndroidPlugin`: JVM/instrumentation tests for the MSDK wrapper without aircraft
  motion.
- `Hardware`: explicitly selected, operator-supervised tests. These are never part
  of a normal build or test command and never auto-initiate takeoff.

Hardware tests must declare prerequisites, physical effects, abort behavior, and
whether propellers must be removed before the operator starts the test.

Current automated coverage contains 73 `EditMode` tests. In addition to normalized
commands, telemetry, camera, gimbal, and input mapping, the suite now verifies
control-mode transitions, command expiry, authority epochs, immediate manual
takeover neutralization, dead-man rejection, telemetry/controller freshness, and
Indoor Development command/altitude limits. It also verifies that a failed DJI
Virtual Stick enable preserves API reachability and its exact error message. No
hardware suite exists and normal test execution cannot initiate aircraft behavior.

Seven of those Unity tests cover the standalone 3D simulator model: authorized
movement, stale/dead-man/brake neutralization, altitude/radius limits, takeoff,
landing, and return-home completion.

Six Python unit tests cover the desktop simulator protocol, range rejection,
authorized movement, dead-man/brake neutralization, stale-command expiry, and the
development altitude ceiling. Run them with:

```bash
python3 -m unittest sim.desktop_drone_simulator.test_model
```
