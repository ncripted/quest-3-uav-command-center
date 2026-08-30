# Unity project

The project lives at `src/QuestCommandCenter` and uses Unity 2022.3.62f3. Open it
through Unity Hub or directly with that editor version. Generated `Library`,
`Logs`, `UserSettings`, build output, IDE projects, APKs, and signing material are
excluded by the repository `.gitignore`.

## Current shell

`Assets/QuestCommandCenter/Scenes/QuestShell.unity` is the only build scene. It
contains:

- an `XROrigin` using floor tracking;
- a tracked XR camera driven by Input System head-pose actions;
- Meta OpenXR color passthrough behind the virtual UI;
- a wide 2400 by 1200 DJI-GO-inspired Floating Monitor command board;
- a dedicated, unobstructed 1620 by 911 (16:9) FPV viewport fed by the diagnostic Spark texture when available;
- compact top vehicle, battery, GPS, link, video, and recording status;
- a central green targeting reticle as the only persistent graphic over video;
- fixed, opaque side columns for detailed telemetry and flight/camera controls;
- full-width top and bottom status bars plus narrow alert/control-state bands outside the image;
- a labeled detailed telemetry card with explicit validity and unavailable states;
- an explicit offline UI mode that does not require a powered aircraft;
- DJI registration, discovery, telemetry, video, and guarded camera control;
- simulator telemetry context and confirmation UI for takeoff/land/RTH;
- Y tap for one onboard photo and Y hold for one second to toggle onboard recording;
- an Input System UI event module for later controller interaction.

DJI MSDK V4.18 is packaged for initialization, registration, passive product
discovery, read-only telemetry, camera/gimbal state, and live-feed decode. Onboard
photo and start/stop recording are operator-triggered and storage-gated. The only
gimbal operation is left-trigger-modified, pitch-only headset tracking between 0°
and −20°, with independent C#, Java, landed/motors-off, health, range, smoothing,
deadband, rate, and pending-command gates. Controller takeoff, land, RTH,
brake/hover, takeover, and manual Mode 2 input use one integrated control router.
With no Spark selected they target the simulator. With a live Spark they remain
locked until the on-headset preflight passes and then route through the command
arbiter, Safety Manager, normalized Spark adapter, and native timeout boundary.

## Initial assemblies

- `QuestCommandCenter.VehicleCore`: generic `ControlMode` and bounded
  `NormalizedFlightCommand` types with no DJI dependency.
- `QuestCommandCenter.Video`: generic texture-source contract with no DJI dependency.
- `QuestCommandCenter.UI`: FPV HUD, camera, and video presenters; they consume generic
  state and video contracts.
- `QuestCommandCenter.Adapters.DjiSpark`: Android-only diagnostic adapter exposed
  through generic status/telemetry/video contracts; DJI Java types do not cross
  into C#.
- `QuestCommandCenter.Editor`: deterministic Android/OpenXR and scene setup plus
  the development build entry point.
- `QuestCommandCenter.VehicleCore.Tests`: isolated edit-mode tests.

Future modules from `docs/ARCHITECTURE.md` will be added as vertical slices, not
as empty scaffolding.

## Android and XR configuration

- permanent DJI-registered package ID: `com.dronecommand.quest`;
- version: `0.1.0` / code 1;
- IL2CPP, ARM64 only;
- minimum Android API 29, automatic target (API 36 on the verified machine);
- OpenGL ES 3;
- OpenXR loader initialized at startup;
- Meta AR Session and AR Camera (Passthrough) OpenXR features enabled;
- Meta Quest Support, Meta Quest Touch Plus, and Oculus Touch interaction features
  enabled, with Quest 3 as the only current device target;
- new Input System enabled.

DJI binds its App Key to the exact Android package name. The App Key is read from
an ignored local secret at build time and inserted only into generated Android
output; it is not stored in checked-in project settings.

## Verification result

On 2026-08-26/27:

- Unity compiled the project without script errors;
- 51/51 edit-mode tests passed, including the software-only Milestone 6 control
  state, arbitration, takeover, expiry, and safety gates;
- the Android Gradle/IL2CPP pipeline completed successfully;
- `Builds/QuestCommandCenter-dev.apk` was produced (about 93 MiB compressed);
- AAPT verified package `com.dronecommand.quest`, API 29 minimum, API 36 target,
  OpenGL ES 3, and ARM64-only native code;
- APK inspection verified the DJI V4.18 native/JNI/video libraries, multidex,
  non-required USB features, protected-loader application class, and resolved
  App Key metadata;
- the APK installed and cold-launched on Quest 3 without a Java/native crash;
- Quest runtime logs confirmed passthrough active for `com.dronecommand.quest`,
  camera input active, and 72 FPS during the observed startup window;
- DJI MSDK V4.18 loaded and registered successfully while passthrough was active,
  discovered Spark over the controller Wi-Fi, and delivered read-only telemetry;
- DJI's primary encoded feed reached its hardware decoder, whose planar YUV420
  callback produced a 640x360 diagnostic Unity texture;
- a 30-second stationary sample displayed 244 preview frames (8.11 Hz) with a
  223 ms maximum observed display gap and no decoder/runtime error.

See `docs/DEVELOPMENT_SETUP.md` for repeatable commands and known build warnings.
