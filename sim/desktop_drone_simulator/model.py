from __future__ import annotations

from dataclasses import asdict, dataclass
import json
import math
from typing import Any


COMMAND_TIMEOUT_SECONDS = 0.25
MAX_HORIZONTAL_METERS_PER_SECOND = 0.5
MAX_VERTICAL_METERS_PER_SECOND = 0.3
MAX_YAW_DEGREES_PER_SECOND = 20.0
MAX_ALTITUDE_METERS = 2.0


@dataclass(frozen=True)
class ControlCommand:
    sequence: int
    roll: float
    pitch: float
    yaw: float
    vertical: float
    authorized: bool
    brake: bool
    takeover: bool

    @staticmethod
    def from_json_line(line: str) -> "ControlCommand":
        payload = json.loads(line)
        if payload.get("schemaVersion") != 1 or payload.get("type") != "manual_control":
            raise ValueError("unsupported control message")

        axes = {}
        for name in ("roll", "pitch", "yaw", "vertical"):
            value = float(payload[name])
            if not math.isfinite(value) or value < -1.0 or value > 1.0:
                raise ValueError(f"{name} must be finite and within [-1, 1]")
            axes[name] = value

        sequence = int(payload["sequence"])
        if sequence <= 0:
            raise ValueError("sequence must be positive")

        return ControlCommand(
            sequence=sequence,
            roll=axes["roll"],
            pitch=axes["pitch"],
            yaw=axes["yaw"],
            vertical=axes["vertical"],
            authorized=bool(payload.get("authorized", False)),
            brake=bool(payload.get("brake", False)),
            takeover=bool(payload.get("takeover", False)),
        )

    @staticmethod
    def neutral(sequence: int = 1) -> "ControlCommand":
        return ControlCommand(sequence, 0.0, 0.0, 0.0, 0.0, False, False, False)


@dataclass
class DroneState:
    north_meters: float = 0.0
    east_meters: float = 0.0
    altitude_meters: float = 1.0
    heading_degrees: float = 0.0
    north_velocity_mps: float = 0.0
    east_velocity_mps: float = 0.0
    vertical_velocity_mps: float = 0.0
    yaw_rate_dps: float = 0.0
    battery_percent: float = 100.0
    connected: bool = False
    command_authorized: bool = False
    command_stale: bool = True
    last_sequence: int = 0
    takeover_count: int = 0


class DroneSimulatorModel:
    """Small deterministic kinematic model for controller and safety testing."""

    def __init__(self) -> None:
        self.state = DroneState()
        self._command = ControlCommand.neutral()
        self._last_command_received_seconds: float | None = None
        self.paused = False

    def set_connected(self, connected: bool) -> None:
        self.state.connected = connected
        if not connected:
            self._neutralize(stale=True)

    def receive_command(self, command: ControlCommand, received_at_seconds: float) -> None:
        if command.sequence <= self.state.last_sequence:
            return
        self.state.last_sequence = command.sequence
        self._last_command_received_seconds = received_at_seconds
        if command.takeover:
            self.state.takeover_count += 1

        if not command.authorized or command.brake:
            self._command = ControlCommand.neutral(command.sequence)
            self.state.command_authorized = False
        else:
            self._command = command
            self.state.command_authorized = True
        self.state.command_stale = False

    def step(self, delta_seconds: float, now_seconds: float) -> None:
        if delta_seconds < 0.0 or not math.isfinite(delta_seconds):
            raise ValueError("delta time must be finite and non-negative")

        stale = (
            self._last_command_received_seconds is None
            or now_seconds - self._last_command_received_seconds > COMMAND_TIMEOUT_SECONDS
        )
        if stale:
            self._neutralize(stale=True)

        command = self._command
        if self.paused or not self.state.connected:
            command = ControlCommand.neutral(max(1, self.state.last_sequence))

        self.state.yaw_rate_dps = command.yaw * MAX_YAW_DEGREES_PER_SECOND
        self.state.heading_degrees = (
            self.state.heading_degrees + self.state.yaw_rate_dps * delta_seconds
        ) % 360.0

        heading_radians = math.radians(self.state.heading_degrees)
        forward_mps = command.pitch * MAX_HORIZONTAL_METERS_PER_SECOND
        right_mps = command.roll * MAX_HORIZONTAL_METERS_PER_SECOND
        self.state.north_velocity_mps = (
            forward_mps * math.cos(heading_radians)
            - right_mps * math.sin(heading_radians)
        )
        self.state.east_velocity_mps = (
            forward_mps * math.sin(heading_radians)
            + right_mps * math.cos(heading_radians)
        )
        self.state.vertical_velocity_mps = command.vertical * MAX_VERTICAL_METERS_PER_SECOND

        self.state.north_meters += self.state.north_velocity_mps * delta_seconds
        self.state.east_meters += self.state.east_velocity_mps * delta_seconds
        next_altitude = self.state.altitude_meters + self.state.vertical_velocity_mps * delta_seconds
        self.state.altitude_meters = max(0.0, min(MAX_ALTITUDE_METERS, next_altitude))
        if (
            self.state.altitude_meters <= 0.0 and self.state.vertical_velocity_mps < 0.0
        ) or (
            self.state.altitude_meters >= MAX_ALTITUDE_METERS
            and self.state.vertical_velocity_mps > 0.0
        ):
            self.state.vertical_velocity_mps = 0.0

        if self.state.connected and not self.paused:
            self.state.battery_percent = max(
                0.0, self.state.battery_percent - (delta_seconds / 300.0)
            )

    def reset_airborne(self) -> None:
        connected = self.state.connected
        self.state = DroneState(connected=connected)
        self._command = ControlCommand.neutral()
        self._last_command_received_seconds = None

    def telemetry_json_line(self) -> str:
        payload: dict[str, Any] = {
            "schemaVersion": 1,
            "type": "simulator_telemetry",
            **asdict(self.state),
        }
        return json.dumps(payload, separators=(",", ":"), allow_nan=False) + "\n"

    def _neutralize(self, stale: bool) -> None:
        self._command = ControlCommand.neutral(max(1, self.state.last_sequence))
        self.state.command_authorized = False
        self.state.command_stale = stale
        self.state.north_velocity_mps = 0.0
        self.state.east_velocity_mps = 0.0
        self.state.vertical_velocity_mps = 0.0
        self.state.yaw_rate_dps = 0.0
