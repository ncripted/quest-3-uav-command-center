# Quest 3 / DJI MSDK V4 compatibility test plan

Status: Q0 build/install/cold-launch checks and Q1 first online registration
passed on 2026-08-26/27. Passthrough also runs concurrently with MSDK registration
at the observed 72 FPS headset rate. Q0 background/resume and Q1 cached offline
restart remain before those gates are formally complete; Q2 has not started.

## Safety boundary

Q0–Q5 and Q7 do not require aircraft movement. Q6 is split into a read-only
availability check and a later propellers-removed control-path test. Nothing in
this plan authorizes takeoff, motor start, or outdoor flight.

Before any powered-aircraft test, the operator owns aircraft placement, battery
condition, firmware readiness, propeller state, and physical access to power. The
test runner displays the exact operations and requires an explicit start action.

## Evidence bundle

Each run writes `artifacts/compatibility/<run-id>/` containing:

- Quest model, Horizon OS build, Android API, ABI, device feature list;
- APK hash, package name, version, build type, Unity version;
- resolved dependency tree and merged manifest;
- APK native-library list and compression/extraction flags;
- sanitized logcat, application JSONL events, and crash tombstone references;
- Wi-Fi topology, SSID redaction, route/interface facts, and reconnect timeline;
- telemetry/video performance metrics;
- test result and operator notes.

App Keys, Wi-Fi passwords, device identifiers, GPS coordinates, and DJI account
tokens are redacted. Raw flight data stays local.

## Q0 — build, install, launch

Prerequisites: activated Unity license, permanent package ID, build-time DJI key
in a local secret source, developer-mode Quest connected over ADB.

1. Build an ARM64-only development APK using Unity 2022.3.62f3.
2. Resolve exactly MSDK V4.18 plus the official provided artifact.
3. Inspect the merged manifest for permissions, application class, DJI metadata,
   USB feature requirements, multidex, and `extractNativeLibs`.
4. Inspect the APK for every required `arm64-v8a` DJI library and verify the
   no-strip rules.
5. Install and launch with no Spark network present.
6. Capture loader/Java/JNI errors for 60 seconds, background/foreground once, and
   restart once.

Pass: clean launch and lifecycle with no missing class/library, loader crash, ANR,
or repeated fatal SDK error. This gate does not require registration success if the
headset is intentionally offline.

Current evidence: the ARM64 Unity APK builds, resolves MSDK V4.18, and passes
manifest/native-library/package inspection. It uses package
`com.dronecommand.quest`; USB accessory/host features are non-required. It
installs and cold-launches without a loader/JNI crash. Meta passthrough is active
with the app process alive at the observed 72 FPS rate. A dedicated
background/foreground cycle remains before Q0 is formally complete.

## Q1 — registration

1. Put Quest on a normal Internet-connected network.
2. Clear app data so the test is a first registration.
3. Launch and call the verified DJI registration sequence.
4. Record the callback result and elapsed time.
5. Restart online, then disconnect Internet and restart using cached validation.

Pass: first registration and both restarts complete consistently. A package/App
Key mismatch, ungrantable permission, protected-loader error, or cache failure is
a direct-integration blocker until explained.

Quest runtime result: Horizon granted Fine Location but denied the legacy
`READ_PHONE_STATE` permission. After clearing app data and removing that permission
as a registration gate, MSDK V4.18 successfully completed first online
registration. A subsequent passthrough build also completed cached online
registration. The cached offline restart remains before Q1 is formally complete.

## Q2 — Spark discovery and reconnect

Physical setup: aircraft and optional remote controller powered according to DJI
instructions. This Q0–Q5 checklist predates the separate, confirmation-gated
Milestone 6 build; do not install that newer flight-capable APK for a passive
compatibility run.

Test separately:

- Quest joined directly to the Spark AP;
- Quest joined to the Spark remote-controller AP, if that controller is available.

For each topology, measure cold discovery, app restart, Wi-Fi reconnect,
background/foreground, and headset sleep/wake. Capture product model and component
connectivity without invoking camera movement or flight actions.

Pass target: 10 consecutive cold/reconnect cycles with correct Spark identity, no
process restart, no stuck stale-connected state, and an explicit disconnect event
within the configured timeout.

## Q3 — telemetry

Subscribe only to verified MSDK callbacks. Capture at least:

- flight-controller state and receive rate;
- battery percent/voltage/temperature when valid;
- location/altitude/attitude/velocity validity;
- motors/flying/flight-mode state;
- camera and gimbal state;
- uplink/downlink or Wi-Fi link state when supported;
- forward vision warnings when supported by the connected Spark/firmware.

Pass: 15 minutes of fresh telemetry, expected approximately 10 Hz flight state,
correct optional-value behavior, successful callback teardown/rebind, and no Unity
main-thread waiting. Do not require unsupported values.

## Q4 — FPV video

Current evidence (2026-08-27): steps 1 and 2 passed, and a diagnostic CPU-copy
Unity presentation passed. The first frame reached Unity and a 30-second sample
displayed 244 frames (8.11 Hz; 223 ms maximum gap). This does not complete Q4:
steps 3–5 and the acceptance measurements below remain required.

1. Confirm encoded primary-feed bytes and stream format.
2. Decode with `DJICodecManager` to an Android surface.
3. Move to `SurfaceTexture`/external OES texture and Unity presentation.
4. Run 15 minutes in Floating Monitor Mode while collecting decoder and XR metrics.
5. Interrupt/recover Wi-Fi and camera feed without restarting the Unity process.

Initial acceptance targets, to be refined with measurements:

- hardware codec selected;
- no per-frame full-resolution CPU copy in the accepted path;
- Unity render thread never waits on feed/decode callbacks;
- XR remains at or above the configured headset refresh rate with stable frame
  pacing; Meta's baseline requirement is at least 72 FPS;
- stale-frame state appears within 500 ms;
- feed reconnects cleanly and decoder resources do not grow per cycle;
- latency, drops, frame rate, resolution, CPU/GPU frame time, memory, and thermal
  state are recorded rather than inferred.

Reference: [Meta frame budgets and 72 FPS baseline](https://developers.meta.com/horizon/documentation/unreal/po-perf-opt-mobile/).

## Q5 — camera, recording, and gimbal boundary

Current evidence (2026-08-27): the generic command gate, exact MSDK V4.18
record/photo sequence, storage-state checks, Y tap/hold mapping, and result/error
presentation are implemented. Passive hardware startup confirmed Spark camera and
storage callbacks without issuing a command. The connected Spark currently has no
microSD card, so successful photo/recording tests remain blocked; the missing-card
rejection passed: Y input was received, the generic gate reported unavailable
storage, and no Android/DJI camera command was dispatched. Multiple rapid Y events
were observed during the first test, so the input layer now enforces a 1.5-second
cooldown in addition to the one-pending-command rule. The operator subsequently
confirmed guarded, pitch-only headset gimbal tracking works while the aircraft is
landed with motors off.

The default portion is state-only plus camera record/photo commands that cannot
move the aircraft. Confirm onboard recording can coexist with live feed and that
result callbacks/errors are surfaced.

Gimbal motion is a separate operator-confirmed subtest. Before it starts, display
the requested pitch range and note that the gimbal will physically move. Clamp to
queried capability limits and stop on disconnect.

Local command-center recording first proves timestamped encoded stream capture and
telemetry/events. Remux/replay validity is a later implementation gate.

## Q6 — Virtual Stick compatibility without flight

Phase A is read-only: inspect `isVirtualStickControlModeAvailable` and capability
state under the actual Spark firmware/link. Do not treat `false` on the ground as a
final conclusion until DJI's documented prerequisites are checked.

The Phase A implementation calls only the availability getter and
`getVirtualStickModeEnabled`; it displays the result in diagnostic status and
does not configure a mode or send control data.

Phase B is deferred until the operator approves a propellers-removed test. The app
will state that it will enable Virtual Stick, configure the selected velocity/yaw
modes, send newly generated neutral commands at 20 Hz, stop sending, verify timeout
handling, and disable Virtual Stick. Motors and takeoff are never requested.

The current aircraft cannot have its propellers removed, so Phase B requires an
equivalent restraint/exclusion-zone plan approved immediately before the test; it
must not be inferred from approval of Phase A.

Pass: explicit callbacks/results, no stale-command replay, neutral output on
dead-man release/timeout, and clean disable/disconnect. Actual directional movement
is Milestone 6 and is outside this compatibility spike.

## Q7 — endurance and failure recovery

Run telemetry, FPV, application recording, and structured logging for 60 minutes
without motors. Inject app-level command staleness and disconnect/reconnect the
network according to a scripted operator checklist.

Pass: no crash/ANR, bounded memory and queues, no sustained XR frame failure,
valid session files after forced interruption, explicit stale/disconnect state, and
repeatable recovery.

## Decision rule

- **Direct accepted:** all gates pass repeatably and Q4 meets XR performance.
- **Direct conditional:** a contained plugin/manifest issue has a documented,
  official-SDK-preserving fix and the affected gates pass afterward.
- **Bridge required:** persistent protected-loader/runtime incompatibility,
  unreliable discovery, unusable video-to-texture performance, or another issue
  that cannot be corrected without reverse-engineering DJI protocols.
