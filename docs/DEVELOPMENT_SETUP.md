# Development setup

Audit date: 2026-08-26  
Host: Linux x86_64, Ubuntu 22.04 family

## Installed and verified

| Tool | Status |
| --- | --- |
| Unity Hub | 3.16.4 at `/opt/unityhub/unityhub` |
| Unity Editor | 2022.3.62f3 at `/home/nate/Unity/Hub/Editor/2022.3.62f3/Editor/Unity` |
| Android Build Support | installed with Unity, including SDK/NDK/OpenJDK |
| Unity Android SDK | platforms 34, 35, 36; build-tools 34.0.0 |
| Unity platform-tools/ADB | 32.0.0 |
| Unity Android NDK | 23.1.7779620 (r23b) |
| Unity embedded OpenJDK | 11.0.14.1 |
| Unity embedded Gradle libraries | 7.5.1 |
| System Java/Javac | OpenJDK 11.0.31 |
| Git | 2.34.1 |
| CMake | installed at `/usr/bin/cmake` |

This matches Unity 2022.3's documented Android development baseline of Android
SDK, NDK r23b, and OpenJDK 11. Reference: [Unity 2022.3 system requirements](https://docs.unity3d.com/2022.3/Documentation/Manual/system-requirements.html).

Use Unity's embedded SDK/JDK/NDK and ADB for reproducibility; do not use the system
ADB 28.0.2 for this project.

## Present but not ready

- Unity Personal is activated. Headless project creation, package resolution,
  script compilation, edit-mode tests, and an Android IL2CPP build all completed
  successfully on 2026-08-25.
- The Quest is physically visible to ADB. The earlier shell APK launched on the
  headset; after the latest ADB daemon restart the headset is awaiting a new USB
  debugging authorization before the DJI diagnostic APK can be installed.
- The workspace contains an empty read-only `.git` directory but no usable Git
  repository metadata. Files can be created, but Git status/history/commits are not
  available in this environment.
- Unrelated existing Quest projects on the machine reference Meta XR/OpenXR
  packages, but this project will not copy their configuration or assume their
  caches are complete.

## Not found

- Android Studio
- standalone `sdkmanager`, `gradle`, or `avdmanager` on `PATH`
- Meta Quest Developer Hub command-line utility
- `dotnet`
- standalone `ninja`/`clang` on `PATH` (the Android NDK carries its own toolchain)

None is an immediate Milestone 0 blocker. Android Studio and Meta Quest Developer
Hub are optional conveniences. A checked-in Gradle wrapper can build the Android
library plugin with the installed JDK/SDK/NDK. Unity's ADB is sufficient for
sideloading and logs.

## Required before on-device and DJI compatibility testing

1. Enable Developer Mode for the Quest 3, connect it over USB, accept the headset
   debugging prompt, and verify it with Unity's bundled ADB.
2. Use the permanent package name `com.dronecommand.quest` (completed).
3. Keep the matching DJI MSDK App Key in ignored `.env.local` (completed).
4. Allow build-time Internet access to resolve Unity packages and DJI/Maven
   dependencies. Core flight remains offline after DJI's first app registration.

Do not commit or log the App Key. Store it in ignored `.env.local` or the
`DJI_MSDK_APP_KEY` environment variable; the build processor injects it into the
generated manifest without printing it.

## Pinned Unity package baseline

Use Unity's OpenXR route rather than the older Oculus-provider-only route:

- XR Plug-in Management 4.5.4;
- Unity OpenXR 1.14.3;
- Unity OpenXR: Meta 1.0.4;
- Input System 1.14.0;
- XR Interaction Toolkit 2.5.4;
- Unity UI 1.0.0;
- Test Framework 1.1.33.

These versions resolved against Unity 2022.3.62f3 and produced the development
APK. Unity's Quest workflow recommends OpenXR and supports Quest 3. Reference:
[Unity Meta Quest workflow](https://docs.unity3d.com/Manual/xr-meta-quest-develop.html).

The Floating Monitor MVP does not require passthrough, hand tracking, maps, voice,
AI, cloud services, or the full Meta XR All-in-One SDK. Add a package only when a
tested feature requires it.

## DJI diagnostic build dependencies

The Android plugin/final Unity Gradle build needs:

```gradle
implementation("com.dji:dji-sdk:4.18")
compileOnly("com.dji:dji-sdk-provided:4.18")
implementation("androidx.appcompat:appcompat:1.0.0")
implementation("androidx.constraintlayout:constraintlayout:1.1.3")
implementation("androidx.lifecycle:lifecycle-process:2.3.1")
```

It also needs the official sample's multidex, manifest, Java compatibility,
packaging/no-strip, and R8/ProGuard behavior adapted to Unity's generated Gradle
modules. Do not paste a legacy tutorial's old SDK/Gradle versions into Unity; use
the current V4.18 sample as the reference.

## Project creation and verification

Create the actual Unity project through the installed editor so Unity owns its
metadata rather than hand-writing it:

```bash
/home/nate/Unity/Hub/Editor/2022.3.62f3/Editor/Unity \
  -batchmode -quit \
  -createProject /home/nate/Desktop/UV/src/QuestCommandCenter \
  -logFile -
```

The project has now been created. Reproduce its editor-generated configuration,
tests, and build from the repository root with:

```bash
UNITY=/home/nate/Unity/Hub/Editor/2022.3.62f3/Editor/Unity

"$UNITY" -batchmode -nographics -quit \
  -projectPath /home/nate/Desktop/UV/src/QuestCommandCenter \
  -executeMethod QuestCommandCenter.Editor.ProjectSetup.ConfigureProject \
  -logFile /tmp/questcc-configure.log

"$UNITY" -batchmode -nographics \
  -projectPath /home/nate/Desktop/UV/src/QuestCommandCenter \
  -runTests -testPlatform EditMode \
  -testResults /tmp/questcc-editmode-results.xml \
  -logFile /tmp/questcc-editmode.log

"$UNITY" -batchmode -nographics -quit \
  -projectPath /home/nate/Desktop/UV/src/QuestCommandCenter \
  -executeMethod QuestCommandCenter.Editor.ProjectSetup.ConfigureAndBuild \
  -logFile /tmp/questcc-android-build.log
```

The setup method intentionally regenerates `QuestShell.unity`; do not use it after
turning that generated scene into a hand-authored production scene without first
changing the setup workflow.

The verified APK is `Builds/QuestCommandCenter-dev.apk`. It uses application ID
`com.dronecommand.quest`, version `0.1.0`, ARM64 IL2CPP, OpenGL ES 3, minimum API
29, and target API 36. The 2026-08-27 DJI FPV-HUD, passthrough, and USB desktop
simulator-relay build is about 94 MiB and has SHA-256
`6fddcfd0ad5a184fb76e3a6d652421e4e39a1b57ac40eb61bb686c7ecd82640f`.

### Desktop controller simulator

Keep Spark and its remote controller powered off. Start the PC window from the
repository root, establish the USB tunnel, and launch the already-installed Quest
app:

```bash
./Builds/DesktopSimulator/QuestCommandCenterSimulator.x86_64 \
  -screen-width 1600 -screen-height 900 -screen-fullscreen 0
adb reverse tcp:8765 tcp:8765
adb shell am start -W -n \
  com.dronecommand.quest/com.unity3d.player.UnityPlayerActivity
```

The Quest can remain on the desk after its controller-required launch prompt is
dismissed. Touch thumbsticks, grips, and B are relayed; headset-driven gimbal and
spatial UI tests still require wearing the headset.
See [DESKTOP_SIMULATOR.md](DESKTOP_SIMULATOR.md) for build instructions, views,
simulated actions, safety behavior, and the Python fallback.

The passthrough shell uses the installed Meta OpenXR route: AR Foundation 5.2.0,
Meta AR Session, Meta AR Camera (Passthrough), `ARSession`/`ARInputManager`, an
enabled `ARCameraManager`, and a main-camera clear color with zero alpha. The
virtual command panel remains opaque.

### Known build warnings

- Unity OpenXR emits optional migration notices for newer pose and thumbstick
  control types. They do not fail this shell build and will be handled when the
  controller action asset is added.
- `libUnityOpenXR.so` from OpenXR 1.14.3 is reported as not 16 KB aligned. The APK
  still builds, but this must be re-evaluated before relying on Android 15+ device
  compatibility or store distribution. Do not suppress the warning.

## Device verification commands

```bash
ADB=/home/nate/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb
"$ADB" devices -l
"$ADB" shell getprop ro.build.version.release
"$ADB" shell getprop ro.build.version.sdk
"$ADB" shell getprop ro.product.cpu.abilist
"$ADB" install -r /home/nate/Desktop/UV/Builds/QuestCommandCenter-dev.apk
"$ADB" shell am start -W -n \
  com.dronecommand.quest/com.unity3d.player.UnityPlayerActivity
```

The property commands are read-only. Do not install or launch a flight-capable APK
until its exact behavior has been reviewed.

### No-motor hardware shadow test

The launch-only hardware shadow mode uses live Spark telemetry and Quest Touch
input but contains no DJI flight-command dispatch path. It is enabled only by the
explicit launch extra below; an ordinary app launch remains unchanged.

```bash
"$ADB" shell am force-stop com.dronecommand.quest
"$ADB" shell am start -W \
  -a android.intent.action.MAIN \
  -c android.intent.category.LAUNCHER \
  -n com.dronecommand.quest/com.unity3d.player.UnityPlayerActivity \
  --ez com.dronecommand.quest.DJI_HARDWARE_SHADOW_TEST true
```

Expected logs begin with `DJI SHADOW TEST ACTIVE`. Control-mode, safety-decision,
and dry-run action lines all end in `SDK_DISPATCH=BLOCKED`. The shadow takeoff
gesture changes only the synthetic test phase so airborne controls and the landing
gesture can be exercised; it does not enable Virtual Stick or call a DJI flight
action.

The stationary 2026-08-29 hardware-shadow run passed controller mapping, manual
takeover, safety clamping, dead-man neutralization, B brake/hover, synthetic
takeoff, and synthetic landing against live Spark telemetry. RTH was safely
rejected without a DJI-confirmed home point. The Spark remained grounded with
motors off throughout the test.

### Integrated full-app flight controls

The standard `QuestCommandCenter-dev.apk` contains both simulator and guarded
real-Spark control; there is no separate field-test package. A live Spark switches
Touch input from the simulator to real telemetry but does not unlock commands. The
headset preflight requires all displayed checks, then centered sticks plus both
grips and A held for three seconds, followed by release. Only then may the app
request Virtual Stick enable.

Controller mapping after unlock:

- left stick Y: vertical; left stick X: yaw;
- right stick Y: forward/back; right stick X: strafe;
- right grip: manual-flight dead-man; B: immediate neutral brake/hover;
- left grip: manual takeover;
- both sticks down/in for one second: confirmed takeoff;
- X held for one second: confirmed RTH;
- A held for one second while flying: confirmed landing.

Do not complete the real-flight unlock or test motors indoors. The computer is not
needed at the field: the APK, DJI SDK, preflight UI, and local session recorder all
run on Quest. Session data is written under the app's persistent-data directory as
`FlightSession/<session-id>/telemetry.jsonl`, `events.jsonl`, `metadata.json`,
`mission.json`, and `video/`.

## Normal-operation dependency target

- Direct accepted: Quest 3 + Spark (and optional Spark remote controller), no PC,
  phone, bridge, or cloud.
- Direct rejected: Quest 3 + small Android DJI bridge + Spark, entirely local.
- A PC remains a development tool only.
