# DJI MSDK V4 on Meta Quest 3

Status: **direct execution, telemetry, diagnostic FPV, and grounded Virtual Stick lifecycle proven; endurance in progress**  
Assessment date: 2026-08-28  
Target: DJI MSDK V4.18, DJI Spark, Meta Quest 3, Unity 2022.3 LTS

## Decision

There is no confirmed build-time ABI mismatch: DJI's published V4.18 AAR contains
`arm64-v8a`, and DJI's current V4 sample is an Android application with a minimum
SDK of 23 and a target SDK of 34. Unity 2022.3 can package Android AAR/native
plugins and this machine has the matching Android toolchain.

DJI does not list Meta Quest as a tested mobile device, so individual runtime gates
still require hardware evidence. On-device testing has now proven protected SDK
initialization, registration, Spark discovery through the Spark remote controller,
read-only telemetry, encoded primary-video receipt, DJI hardware decode, and a
live Unity texture on Quest 3. A stationary motors-off test has also proven that
Virtual Stick can be enabled, reports available, accepts neutral output, and can
be disabled by the native command-timeout boundary. Sustained telemetry/video
endurance, a production zero-copy video path, reconnection behavior, and airborne
manual-control behavior remain unproven.

No flight-capable code should be merged on the strength of this document alone.

## Evidence summary

### 1. Android requirements

DJI's official V4 repository identifies 4.18 as the latest V4 Android release and
declares the `com.dji:dji-sdk:4.18` and `com.dji:dji-sdk-provided:4.18`
dependencies. The current official sample uses `compileSdkVersion 33`,
`minSdkVersion 23`, `targetSdkVersion 34`, multidex, Java 8 bytecode, and
`org.apache.http.legacy`. It also sets `android:extractNativeLibs="true"` and lists
the MSDK permissions in its manifest.

Sources: [DJI V4 Android repository](https://github.com/dji-sdk/Mobile-SDK-Android),
[official sample build.gradle](https://github.com/dji-sdk/Mobile-SDK-Android/blob/master/Sample%20Code/app/build.gradle),
[official sample AndroidManifest.xml](https://github.com/dji-sdk/Mobile-SDK-Android/blob/master/Sample%20Code/app/src/main/AndroidManifest.xml).

The AAR's embedded manifest itself declares minimum API 21 and target API 33. We
will follow the complete sample application baseline (minimum API 23) rather than
the looser library manifest.

### 2. Native libraries and ABIs

The V4.18 AAR downloaded from Maven Central was inspected directly:

```text
artifact: com.dji:dji-sdk:4.18
SHA-256:  6ec0b866057aba2b874bcbfabe7f3c991d00081b6093de88b9317412cd881897
ABIs:     arm64-v8a, armeabi-v7a, x86
```

The ARM64 payload includes DJI JNI, video, FFmpeg, fly-safe, logging, upgrade, and
waypoint-related native libraries. The official sample filters final builds to
`armeabi-v7a` and `arm64-v8a` and preserves a documented list of DJI `.so` files
from symbol stripping. Quest will use ARM64 only.

Artifact reference: [Maven Central `com.dji:dji-sdk`](https://central.sonatype.com/artifact/com.dji/dji-sdk/4.18).

The ABI match removes one obvious blocker but does not prove that every library can
load against Horizon OS's Android runtime or security policy.

### 3. DJI application registration and App Key

Every app needs a DJI App Key. The Android package name registered in DJI's
Developer Center must exactly match the final Android application ID. DJI's
`DJISDKManager.registerApp` must succeed before product use. On first initialization
after installation it contacts a DJI server to validate the App Key; later starts
can use cached validation while it remains valid.

Sources: [DJI sample-app setup](https://developer.dji.com/mobile-sdk/documentation/quick-start/index.html),
[`DJISDKManager.registerApp` reference](https://developer.dji.com/api-reference/android-api/Components/SDKManager/DJISDKManager.html?i=0&search=onproduct).

Consequences:

- the permanent package name must be chosen before requesting the key;
- the key must be injected at build time and kept out of Git;
- first registration must occur while Quest has Internet, before joining the
  Spark/remote-controller Wi-Fi network that may have no Internet;
- uninstall/clear-data tests must repeat first-registration validation.

### 4. Package and application startup integration

The current official sample installs DJI's protected loader from an Android
`Application.attachBaseContext`, installs multidex, and only then uses SDK classes.
In the current source the call is
`com.cySdkyc.clx.Helper.install(this)`. This exact class is present in the V4.18
AAR inspected above.

Source: [official `DJISampleApplication`](https://github.com/dji-sdk/Mobile-SDK-Android/blob/master/Sample%20Code/app/src/main/java/com/dji/sdk/sample/internal/controller/DJISampleApplication.java).

Unity therefore needs a controlled Android `Application` integration, not just C#
calls into arbitrary DJI classes. The plugin spike must verify manifest merging,
startup order, multidex, `extractNativeLibs`, `org.apache.http.legacy`, ProGuard/R8
rules, and JNI library preservation in the final APK.

The AAR also declares `android.hardware.usb.accessory` as required even though the
Spark path is Wi-Fi. That manifest feature must be inspected in the merged Quest
APK and tested. If the requirement filters installation or is false for Quest, a
manifest override to non-required may be appropriate for the Wi-Fi-only build, but
it will not be changed until the merge result and device feature list are captured.

### 5. Spark network connection

DJI documents two supported Spark paths:

- mobile device joins the Wi-Fi network created by the Spark; or
- mobile device joins the Wi-Fi network created by the optional Spark remote
  controller, which is linked to the aircraft.

Sources: [DJI sample Wi-Fi connection procedure](https://developer.dji.com/mobile-sdk/documentation/quick-start/index.html),
[DJI hardware/remote-controller comparison](https://developer.dji.com/mobile-sdk/documentation/introduction/product_introduction.html),
[DJI AirLink overview](https://developer.dji.com/mobile-sdk/documentation/introduction/component-guide-airlink.html).

Quest can act as the Android mobile device in topology, but the following are
hardware-test questions: whether Horizon OS retains a no-Internet Wi-Fi network,
whether MSDK binds discovery traffic to it, whether location/Wi-Fi permissions are
grantable in-headset, and whether the link remains stable while XR rendering and
video decode are active.

### 6. SDK initialization

**Result: passed on Quest 3.** The verified sequence for the spike is:

1. protected loader during `Application.attachBaseContext`;
2. runtime permissions;
3. `DJISDKManager.getInstance().registerApp(...)`;
4. on registration success,
   `DJISDKManager.getInstance().startConnectionToProduct()`.

The diagnostic screen must record SDK version, every registration result, callback
thread, elapsed time, merged-manifest facts, and native-load errors. Initialization
is a hard gate; a crash, hang, or repeatable registration failure rejects direct
execution.

### 7. Product discovery

**Result: passed for the remote-controller Wi-Fi topology.** Quest joined the
Spark remote controller's Wi-Fi network while ADB remained connected over USB.
MSDK identified the product as `Spark` and reported an `AIR_LINK` component
change. Reconnect, headset sleep/wake, and direct-to-aircraft Wi-Fi endurance
remain to be tested. The official sample observes
`DJISDKManager.SDKManagerCallback` product connection/disconnection callbacks after
starting product connection. The spike passes only if Spark is identified
consistently after cold start, headset sleep/wake, Wi-Fi reconnect, and app restart.

Source: [official connection sample](https://github.com/dji-sdk/Mobile-SDK-Android/blob/master/Sample%20Code/app/src/main/java/com/dji/sdk/sample/internal/view/MainContent.java).

### 8. Telemetry

**Initial Quest runtime pass; endurance validation remains.** MSDK V4 provides
approximately 10 Hz flight state
through `FlightController.setStateCallback`. Verified normalized inputs include
aircraft location/altitude, attitude, NED velocities, motor state, and flying state.
Battery, camera, gimbal, and link state have their own callbacks. Unsupported or
invalid values must stay optional.

Sources: [`FlightController` reference](https://developer.dji.com/api-reference/android-api/Components/FlightController/DJIFlightController.html),
[`FlightControllerState` reference](https://developer.dji.com/api-reference/android-api/Components/FlightController/DJIFlightController_DJIFlightControllerCurrentState.html),
[`BatteryState` reference](https://developer.dji.com/api-reference/android-api/Components/Battery/DJIBattery_BatteryState.html),
[`AirLink` reference](https://developer.dji.com/api-reference/android-api/Components/AirLink/DJIAirLink.html).

The current plugin subscribes only to `FlightControllerState` and `BatteryState`,
sanitizes non-finite values, preserves position validity, timestamps with Android
monotonic time, and coalesces publication to Unity at no more than 5 Hz. On
2026-08-27, the flight-controller, battery, camera-system, and gimbal-state
callbacks all delivered state immediately after the Spark connected through the
remote controller. Camera state includes mode, recording/photo status, overheat,
and error flags. Gimbal state includes attitude, mode, calibration, stop, and motor
overload flags. The telemetry gate still requires sustained values, reconnect
behavior, and no material XR frame-time regression. No coordinates or telemetry
payload values are written to diagnostic logs.

### 9. Live video

**Diagnostic Quest-to-Unity path passed on 2026-08-27; production path remains.**
MSDK V4 exposes the primary
encoded feed through `VideoFeeder`/`VideoDataListener`. DJI's sample feeds those
bytes to `DJICodecManager`, whose documented constructors accept an Android
`SurfaceHolder` or `SurfaceTexture` and use Android media codecs.

Sources: [`VideoFeeder` reference](https://developer.dji.com/api-reference/android-api/BaseClasses/DJIVideoFeeder.html),
[`DJICodecManager` reference](https://developer.dji.com/api-reference/android-api/Components/CodecManager/DJICodecManager.html),
[official `VideoFeedView`](https://github.com/dji-sdk/Mobile-SDK-Android/blob/master/Sample%20Code/app/src/main/java/com/dji/sdk/sample/internal/utils/VideoFeedView.java).

The most likely production path is:

```text
VideoDataListener -> DJICodecManager/MediaCodec -> SurfaceTexture
  -> external OES texture -> Unity native render event -> FPV material
```

Start the spike with OpenGL ES 3. CPU YUV output may prove stream/decode access but
is not acceptance for production because it adds copies and Quest CPU load. The
gate records first-frame time, decoder name, resolution, frame rate, dropped/stale
frames, glass-to-glass latency, Unity main-thread time, and headset thermals.

The stationary hardware spike confirmed this sequence on Quest 3 through the
Spark remote-controller Wi-Fi:

```text
Spark primary encoded feed (1024x768 at 30 FPS)
  -> VideoDataListener
  -> DJICodecManager hardware decoder
  -> planar YUV420 callback (color format 19)
  -> bounded center-crop/downscale and RGBA conversion (640x360)
  -> newest-frame JNI polling
  -> Unity Texture2D
  -> Floating Monitor RawImage
```

DJI's documented `getRgbaData` helper failed on Quest with
`GLYUVSurface: getRgbaData: ColorDisplayView failed`. Calling `getYuvData` from
inside the decoder callback also stalled that callback. Using the planar YUV
buffer supplied directly to `YuvDataCallback` avoided both device-specific
failures. The first Unity frame was logged about 0.7 seconds after the first
encoded packet. A 30-second sample displayed 244 frames (8.11 Hz) with a 223 ms
maximum observed display gap and no decoder/runtime error.

This proves direct video accessibility and Unity presentation, but it is not Q4
production acceptance: the diagnostic implementation performs CPU color
conversion, a Java-to-C# copy, and a Unity texture upload at a target of 10 Hz.
The production task remains the external-OES/native-render path above, followed
by latency, XR frame-time, dropped-frame, and thermal measurements.

### 10. Virtual Stick and control

Spark cannot execute the normal onboard Waypoint Mission: DJI explicitly lists it
as unsupported. Command-station missions must therefore supervise real-time
control rather than upload a waypoint mission.

Source: [DJI `WaypointMission` reference](https://developer.dji.com/api-reference/android-api/Components/Missions/DJIWaypointMission.html?i=0&search=waypointmission).

MSDK V4 exposes `isVirtualStickControlModeAvailable`,
`setVirtualStickModeEnabled`, and `sendVirtualStickFlightControlData`. DJI requires
commands at 5–25 Hz. Crucially, DJI documents that availability is false until
Virtual Stick mode itself is enabled. Therefore a pre-enable status getter proves
API reachability, while command readiness requires a successful enable callback
and a true post-enable availability value. Spark is not listed among the method's
exclusions, but runtime behavior on the actual firmware/link must still be proven.

Source: [DJI `FlightController` Virtual Stick reference](https://developer.dji.com/api-reference/android-api/Components/FlightController/DJIFlightController.html).

The 2026-08-28 software slice compiles a locked native backend using body-relative
roll/pitch velocity, vertical velocity, and yaw angular velocity. It schedules at
20 Hz, accepts only increasing command sequences, substitutes neutral after
150 ms of input staleness, and requests mode disable after 500 ms or on stale
telemetry, DJI failsafe, disconnect, or send failure. Development conversion
limits are 1.0 m/s horizontal, 0.8 m/s vertical, and 30 degrees/s yaw before the
stricter normalized Safety Manager limits are applied. No controller or UI path
can bypass the integrated preflight and command arbiter. A one-shot ADB launch
diagnostic remains available only for separately approved grounded verification.

The stationary, motors-off Spark test passed on 2026-08-28:

1. the read-only probe returned `available=false`, `enabled=false`, and success;
2. fresh flight telemetry confirmed failsafe clear, motors off, and not flying;
3. MSDK accepted the configured modes and reported `available=true` and
   `enabled=true`;
4. no authorized movement command was submitted; the native scheduler emitted
   only zero-valued neutral data;
5. after 500 ms without a fresh command heartbeat, the native boundary requested
   safety disable; and
6. the final read-only probe returned `available=false`, `enabled=false`, and
   success.

This proves the grounded Virtual Stick lifecycle on the Quest/Spark firmware and
link combination. It does not prove airborne response, direction/sign mapping,
manual takeover, or safe handling of real controller input.

The 2026-08-29 outdoor field session exposed and safely contained a circular
preflight gate: the application required DJI's availability value before making
the enable request, although DJI specifies that value becomes true only after
enable. The aircraft never received software flight commands; it remained usable
from the physical DJI controller and FPV remained live. The gate now requires a
successful status getter before confirmation, then independently requires the
enable callback and post-enable availability before entering `READY`.

The installed MSDK 4.18 artifacts were locally inspected with `javap` and confirm
that `FlightController` exposes `startTakeoff`, `startLanding`, and `startGoHome`.
The current development build wraps those three high-level calls behind a
two-second hold, a separate A confirmation, fresh telemetry, per-action state
checks, a five-second confirmation expiry, and duplicate native gates. It also
uses `FlightControllerState.isHomeLocationSet()` as a mandatory RTH prerequisite.
The bridge and APK compile successfully, but this flight-capable build has not yet
been installed or exercised on the real aircraft. SDK acceptance and physical
maneuver completion are deliberately represented as different facts.

### 11. Unity Android native plugin

**Most likely integration: an Android library boundary plus a small native render
plugin.** Unity 2022.3 supports AAR plugins and C#↔Java calls through its JNI APIs.
The wrapper should expose asynchronous, plain-data operations and callbacks; it
must never surface a DJI object to C#.

Sources: [Unity AAR/JAR plugin manual](https://docs.unity3d.com/2022.3/Documentation/Manual/AndroidJARPlugins.html),
[Unity C# to Java/Kotlin manual](https://docs.unity3d.com/2022.3/Documentation/Manual/android-plugins-java-code-from-c-sharp.html),
[Unity Android native plugin manual](https://docs.unity3d.com/2022.3/Documentation/Manual/AndroidNativePlugins.html).

Proposed split:

- Java/Kotlin AAR: DJI lifecycle, permission checks, connection, telemetry
  normalization, camera/gimbal commands, encoded video listener, and errors;
- C/C++ render plugin: render-thread-safe external texture/surface interop;
- C# `LocalMsdkBackend`: marshals neutral DTOs/events and implements the same
  internal backend contract as the remote bridge.

## Direct compatibility test gates

These gates are ordered to avoid propeller motion. Full procedures and evidence
artifacts are in [COMPATIBILITY_TEST_PLAN.md](COMPATIBILITY_TEST_PLAN.md).

Build and runtime evidence collected on 2026-08-26/27 confirms that Unity 2022.3
can package MSDK V4.18 and its ARM64 native libraries into the Quest APK, install
it, and cold-launch it on Quest 3. Meta OpenXR passthrough and MSDK registration
also run concurrently at the observed 72 FPS rate. Spark discovery through the
remote controller has now identified the aircraft and its AirLink. Q0
background/resume, Q1 cached offline restart, reconnect endurance, Q3 telemetry
endurance, and the production half of Q4 remain.

The first on-device launch exposed one missing dependency from DJI's official
sample, `androidx.lifecycle:lifecycle-process:2.3.1`; adding that pinned artifact
resolved the startup crash. With Fine Location granted and `READ_PHONE_STATE`
granted temporarily through ADB, MSDK V4.18 loaded and returned registration
success. A later clean run with app data cleared and `READ_PHONE_STATE` ungranted
also registered successfully, proving that Horizon's denial of that legacy
permission is not a blocker. Product connection was subsequently started on the
controller Wi-Fi topology and successfully discovered the Spark.

| Gate | Pass condition | Aircraft motion |
| --- | --- | --- |
| Q0 Build | ARM64 APK resolves V4.18, installs, launches, and has expected merged manifest/native libs | none |
| Q1 Register | first-run online registration and cached offline restart succeed | none |
| Q2 Discover | Spark discovery/reconnect is stable over both intended Wi-Fi topologies | none |
| Q3 Telemetry | flight, battery, camera, gimbal, and link callbacks remain fresh | none |
| Q4 Video | hardware-decoded FPV reaches a Unity texture without main-thread stalls | none |
| Q5 Camera | state and non-motion camera operations work; gimbal test is separately confirmed | no aircraft motion |
| Q6 Control API | grounded lifecycle passed: configured modes, enabled/available, neutral output, automatic timeout disable, final disabled status | no flight; airborne behavior remains an explicit later test |
| Q7 Endurance | telemetry/video/recording soak meets latency, frame, thermal, and reconnect criteria | none |

Direct execution is accepted only after Q0–Q7 are repeatable. A failure that can be
fixed without bypassing DJI security/protocols may be investigated. Persistent
native-loader, discovery, video-texture, or performance failure activates the
bridge design.

## Fallback bridge architecture

```text
Quest application
  DjiSparkAdapter -> RemoteBridgeBackend
    reliable control/state channel + time sync + RTP/H.264 video
                         |
                 Android DJI bridge
    fail-neutral watchdog + MSDK V4 + encoded video forwarder
                         |
                     Spark Wi-Fi
```

The Quest remains the command computer: mission execution, autopilot, safety
policy, command arbitration, UI, session coordination, and operator input stay on
Quest. The bridge is a low-level MSDK transport adapter with its own mandatory
watchdog; it neutralizes command output when timestamped heartbeats expire.

MVP protocol design:

- versioned Protocol Buffers or equivalently versioned binary DTOs;
- mutually authenticated local reliable channel for commands, results, telemetry,
  capabilities, events, and heartbeats;
- commands include sequence, session time, expiry, authority epoch, and vehicle ID;
- encoded H.264 is packetized over RTP/UDP and decoded on Quest;
- an NTP-like monotonic time exchange estimates bridge offset/uncertainty;
- manual IP pairing first; mDNS discovery is optional;
- no Internet or cloud dependency after DJI's required app registration.

Network topology must also be spiked. Preferred first test puts Quest and bridge on
the Spark remote-controller AP and checks client-to-client reachability. If the AP
isolates clients, use a bridge device with two simultaneous interfaces (Spark
Wi-Fi plus Ethernet/travel-router LAN). Concurrent Wi-Fi hotspot behavior is too
device-specific to be the baseline.

The C# application selects `LocalMsdkBackend` or `RemoteBridgeBackend` from config.
Both publish identical normalized DTOs, errors, capabilities, and video-source
state. No UI, safety, mission, or autopilot code changes between them.

## Open questions requiring hardware

1. Exact Quest 3 Horizon OS version/API/feature list and permission behavior.
2. Protected MSDK native-loader success under Unity's Application lifecycle.
3. Merged USB-feature impact on Quest installation.
4. Spark discovery on a no-Internet Wi-Fi network and after headset sleep.
5. Exact codec identity selected by `DJICodecManager` and its sustained thermal cost.
6. External OES texture correctness and latency in Unity XR (diagnostic YUV-to-texture is proven).
7. Airborne Virtual Stick response, direction/sign validation, and takeover behavior under conservative limits.
8. Peer reachability on the Spark/remote-controller AP for the fallback topology.
