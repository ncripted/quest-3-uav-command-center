# DJI Android bridge fallback

This process is not part of the default architecture and is not implemented in
Milestone 0. It is activated only if the direct-on-Quest compatibility gates fail.

The bridge hosts DJI MSDK V4 on a small supported Android device, forwards the
original video stream where practical, publishes normalized telemetry, and accepts
timestamped normalized commands. It must fail neutral when command heartbeats
expire. Mission execution and command arbitration remain on Quest.

See the fallback section of `docs/DJI_QUEST_COMPATIBILITY.md`.

