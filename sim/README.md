# Lightweight Python desktop simulator

This fallback simulator renders a small kinematic drone on the PC and accepts normalized
Quest Touch input over a localhost-only TCP connection. The connection is carried
over USB with `adb reverse`; it does not use DJI MSDK and cannot command an
aircraft.

The primary visual simulator is now the standalone Unity 3D build documented in
`docs/DESKTOP_SIMULATOR.md`. This Python version remains useful for protocol tests
and machines where the Unity player cannot run.

Run tests from the repository root:

```bash
python3 -m unittest sim.desktop_drone_simulator.test_model
```

Start the desktop window:

```bash
python3 -m sim.desktop_drone_simulator.app
```

Create the USB tunnel before launching the Quest app:

```bash
adb reverse tcp:8765 tcp:8765
```

The protocol is newline-delimited JSON, schema version 1. Quest sends normalized
`manual_control` messages at 20 Hz; the simulator returns `simulator_telemetry` at
10 Hz. Both ends neutralize control when messages become stale.
