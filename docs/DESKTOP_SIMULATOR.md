# Unity desktop drone simulator

The standalone Unity simulator is the primary safe test target for Milestone 6.
It accepts the same normalized Quest Touch commands intended for the Spark, but
its runtime assembly has no DJI adapter or MSDK dependency.

## Included features

- procedural Spark-style aircraft with animated propellers and attitude plus a
  visible gimbal/camera and navigation lights;
- buildings, roads, trees, home pad, development geofence, and flight trail;
- chase, FPV, top-down, and orbit camera views;
- link, authority, freshness, position, altitude, velocity, heading, battery,
  takeover, and geofence telemetry;
- development-limited kinematic movement;
- simulated takeoff, landing, RTH, brake/hover, manual takeover, stale input, and
  disconnect neutralization.

The model is deterministic and safety-oriented. It is not an aerodynamic model
and should not be used to predict real Spark handling.

## Build

```bash
/home/nate/Unity/Hub/Editor/2022.3.62f3/Editor/Unity \
  -batchmode -nographics -quit \
  -projectPath /home/nate/Desktop/UV/src/QuestCommandCenter \
  -executeMethod QuestCommandCenter.Editor.DesktopSimulatorSetup.ConfigureAndBuild \
  -logFile /tmp/qcc-unity-3d-simulator-build.log
```

The output is `Builds/DesktopSimulator/QuestCommandCenterSimulator.x86_64` plus
its adjacent data directory. The verified release directory is about 70 MiB. The
launcher SHA-256 is
`0bfe51325720c950b4ad75ce7c3b65595fd7e939908520ec33600919919327a8`.

## Run with Quest controllers

Keep Spark and its remote controller powered off. Start the simulator first:

```bash
./Builds/DesktopSimulator/QuestCommandCenterSimulator.x86_64 \
  -screen-width 1600 -screen-height 900 -screen-fullscreen 0
```

Then create the USB tunnel and launch the already-installed Quest relay app:

```bash
adb reverse tcp:8765 tcp:8765
adb shell am start -W -n \
  com.dronecommand.quest/com.unity3d.player.UnityPlayerActivity
```

The simulator now starts grounded. Quest controller actions are:

- while grounded, hold both sticks down and inward for one second: simulated takeoff;
- while flying, hold A for one second: simulated landing;
- while flying normally, hold X for one second: simulated RTH;
- an ordinary B press remains brake/hover.

These discrete actions use simulator telemetry and a dedicated queued TCP message;
they are not routed to the DJI adapter.

Unity Editor shutdown may restart ADB, so establish `adb reverse` after building.

Quest controls retain the authoritative Mode 2 mapping. Hold Right Grip to
authorize movement; B brakes; releasing Right Grip neutralizes. The headset may
remain on the desk after Quest's controller-required system prompt is cleared.
Both relay endpoints explicitly detect half-closed TCP sockets and reconnect; they
do not rely on the potentially stale `TcpClient.Connected` property.

Desktop keys:

- `1` chase, `2` FPV, `3` top-down, `4` orbit, `C` cycle view;
- `T` simulated takeoff, `L` simulated land, `H` simulated RTH;
- `R` reset airborne at home, `Esc` quit.
