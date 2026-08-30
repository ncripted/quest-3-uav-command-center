# Mission format

Status: Milestone 0 design. Runtime classes and JSON Schema are created with the
Milestone 7 implementation so the schema is generated/tested against real
serialization rather than drifting from it.

## Principles

- UTF-8 JSON, human-readable, versioned, and deterministic to serialize.
- Missions execute on the command station; they are not stored on the Spark.
- Every step has an ID, type, parameters, timeout, completion condition, and
  failure behavior.
- Units and coordinate frames are explicit.
- Validation is capability- and safety-profile-aware.
- Unknown schema versions or step types are rejected, not guessed.

## Envelope

```json
{
  "schemaVersion": 1,
  "missionId": "indoor-hover-demo",
  "name": "Indoor hover demo",
  "createdUtc": "2026-08-25T13:00:00Z",
  "modifiedUtc": "2026-08-25T13:00:00Z",
  "vehicleRequirements": {
    "vehicleClass": "aerial",
    "capabilities": ["takeoff", "land", "flightCommand"]
  },
  "steps": [
    {
      "id": "takeoff-1",
      "type": "Takeoff",
      "parameters": {},
      "timeoutMs": 15000,
      "completion": {
        "type": "airborneAndMinimumAltitude",
        "minimumRelativeAltitudeMeters": 0.8,
        "stableForMs": 1000
      },
      "onFailure": {
        "action": "cancelMission",
        "recovery": "configuredFailsafe"
      }
    },
    {
      "id": "hover-1",
      "type": "Hover",
      "parameters": {
        "durationMs": 3000
      },
      "timeoutMs": 5000,
      "completion": {
        "type": "durationElapsed"
      },
      "onFailure": {
        "action": "pauseMission",
        "recovery": "neutral"
      }
    },
    {
      "id": "land-1",
      "type": "Land",
      "parameters": {},
      "timeoutMs": 30000,
      "completion": {
        "type": "notFlyingAndMotorsOff",
        "stableForMs": 1000
      },
      "onFailure": {
        "action": "enterFailsafe",
        "recovery": "configuredFailsafe"
      }
    }
  ]
}
```

This example describes format only. It is not an executable hardware test and
does not authorize takeoff.

## Initial step types

| Type | Initial parameters | Typical completion evidence |
| --- | --- | --- |
| `Takeoff` | none | flying plus minimum relative altitude |
| `Wait` | `durationMs` | monotonic duration elapsed |
| `Hover` | `durationMs` | duration while command remains neutral |
| `Ascend` | `deltaMeters`, optional `maxSpeedMps` | altitude band held for stability time |
| `Descend` | `deltaMeters`, optional `maxSpeedMps` | altitude band held for stability time |
| `RotateToHeading` | `headingDegrees`, `toleranceDegrees` | heading tolerance held |
| `MoveForward` | `durationMs`, `speedMps` | duration elapsed |
| `MoveBackward` | `durationMs`, `speedMps` | duration elapsed |
| `MoveLeft` | `durationMs`, `speedMps` | duration elapsed |
| `MoveRight` | `durationMs`, `speedMps` | duration elapsed |
| `CameraStartRecording` | none | camera reports recording |
| `CameraStopRecording` | none | camera reports not recording |
| `TakePhoto` | none | command success/camera event |
| `ReturnHome` | none | DJI RTH/flight state reaches configured condition |
| `Land` | none | not flying and motors off |

Timed movement is the initial relative-motion mechanism. It is not treated as
precise navigation. A later GPS move-to step requires a separate schema addition
after telemetry accuracy and controller behavior are measured.

## Validation

Validation runs before save and again before execution. It checks:

- schema version, unique mission/step IDs, known types, and required fields;
- finite values, positive timeouts, valid headings, and unit ranges;
- vehicle capabilities and current component availability;
- safety-profile speed/altitude/radius constraints;
- state prerequisites and GPS/home validity for GPS-dependent actions;
- at most one terminal recovery action per failure behavior;
- camera/gimbal actions only when those capabilities exist;
- a mission cannot imply onboard waypoint storage for Spark.

Validation produces structured errors with JSON paths and actionable messages. A
warning never silently upgrades to permission to execute.

## Execution state

Mission lifecycle is `Created`, `Validated`, `Running`, `Paused`, `Cancelling`,
`Cancelled`, `Completed`, or `Failed`. Step lifecycle is `Pending`, `Starting`,
`Running`, `Paused`, `Succeeded`, `Failed`, `TimedOut`, or `Skipped`.

Pause generates neutral autopilot output and preserves explicitly serializable
step progress. Resume revalidates connection, telemetry freshness, capabilities,
safety profile, and completion inputs. Cancel invalidates the mission authority
epoch immediately, then performs configured recovery.

Manual takeover pauses by default. It invalidates any queued mission command even
if the mission worker has not yet processed the pause event.

## Failure behavior

Initial `action` values:

- `pauseMission`
- `cancelMission`
- `enterFailsafe`

Initial `recovery` values:

- `neutral`
- `configuredFailsafe`
- `landIfSafe`
- `returnHomeIfValid`

Recovery names are resolved by Safety Manager using current state. A mission file
cannot force an action that the active safety profile or vehicle capability rejects.

## Persistence and replay preparation

The original validated mission is copied into each `FlightSession`. Runtime events
reference `missionId`, `stepId`, execution attempt, authority epoch, and monotonic
session time. Mission files do not contain credentials, device IDs, or unredacted
session telemetry.

