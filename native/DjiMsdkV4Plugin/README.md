# DJI MSDK V4 Unity plugin

Android bridge used by the direct-on-Quest compatibility spike. It owns the DJI
MSDK V4 protected startup, registration, product discovery callbacks, native
library packaging, and later video surfaces. No DJI class appears in the C# API.

The installed adapter covers SDK startup,
registration, discovery, normalized read-only telemetry, camera/gimbal state, and
a diagnostic live-preview path. The preview feeds the primary encoded stream to
`DJICodecManager`, consumes its planar YUV callback, and exposes only the newest
640x360 RGBA frame to Unity. Its only command operations are guarded calls to
`startRecordVideo`, `stopRecordVideo`, and single-photo capture. These
commands require ready storage, serialize one pending request, and return every
asynchronous DJI result to Unity. The only gimbal operation is guarded headset
pitch tracking using official absolute-angle rotation: it is limited to −20°
through 0°, takes 0.35 seconds, leaves roll/yaw untouched, requires landed and
motors-off state, and serializes one request. Confirmation-gated takeoff,
landing, and RTH wrappers are connected only through the integrated preflight,
command arbiter, Safety Manager, and generic normalized control interface.

The Milestone 6 bridge contains a guarded Virtual Stick backend compiled
against the exact MSDK V4.18 API. Its automatic operation is limited to a
read-only status getter. DJI availability is expected to be false before mode
enable; the getter establishes API reachability, while enable success and
post-enable availability establish command readiness. After the main app's explicit preflight, its entry points configure body-relative
roll/pitch velocity, vertical velocity, and yaw angular velocity, then schedule
DJI commands at 20 Hz. Enable first establishes DJI's required aircraft-heading
orientation. A DJI common-timeout result is reconciled against authoritative
mode state and permits at most one delayed, state-safe retry for that same
grounded request. A seven-second native deadline force-disables the mode if any
orientation, enable, query, retry, or disable callback stalls. Explicit disable
can preempt a pending enable, and lifecycle changes invalidate every late
callback while issuing an idempotent best-effort disable. The native layer independently replaces input older than
150 ms with neutral and disables Virtual Stick after 500 ms without a fresh
heartbeat, on stale flight telemetry, DJI failsafe, disconnect, or send failure.
The native freshness and failsafe checks remain independent of Unity state.

Build the small Java bridge JAR with:

```bash
./native/DjiMsdkV4Plugin/build-bridge.sh
```

The script downloads the public V4.18 compile artifacts into ignored `.deps/`,
compiles against Unity's Android classes, and writes `DjiMsdkBridge.jar` into the
Unity Android plug-in directory. The final Unity Gradle build resolves the
official `com.dji:dji-sdk:4.18` dependency separately.
