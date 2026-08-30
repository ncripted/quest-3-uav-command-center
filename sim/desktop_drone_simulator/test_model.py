import json
import math
import unittest

from .model import ControlCommand, DroneSimulatorModel, MAX_ALTITUDE_METERS


class ControlCommandTests(unittest.TestCase):
    def test_parses_valid_message(self):
        command = ControlCommand.from_json_line(json.dumps({
            "schemaVersion": 1,
            "type": "manual_control",
            "sequence": 4,
            "roll": 0.2,
            "pitch": -0.3,
            "yaw": 0.4,
            "vertical": -0.5,
            "authorized": True,
            "brake": False,
            "takeover": True,
        }))
        self.assertEqual(4, command.sequence)
        self.assertTrue(command.authorized)
        self.assertTrue(command.takeover)

    def test_rejects_non_finite_or_out_of_range_axes(self):
        base = {
            "schemaVersion": 1,
            "type": "manual_control",
            "sequence": 1,
            "roll": 0.0,
            "pitch": 0.0,
            "yaw": 0.0,
            "vertical": 0.0,
        }
        for invalid in (1.01, -1.01, math.inf, math.nan):
            payload = dict(base, roll=invalid)
            with self.assertRaises(ValueError):
                ControlCommand.from_json_line(json.dumps(payload))


class DroneSimulatorModelTests(unittest.TestCase):
    def test_authorized_command_moves_simulated_drone(self):
        model = DroneSimulatorModel()
        model.set_connected(True)
        model.receive_command(ControlCommand(1, 0.0, 1.0, 0.0, 0.0, True, False, False), 10.0)
        model.step(1.0, 10.1)
        self.assertAlmostEqual(0.5, model.state.north_meters, places=4)
        self.assertTrue(model.state.command_authorized)

    def test_deadman_release_and_brake_neutralize(self):
        model = DroneSimulatorModel()
        model.set_connected(True)
        model.receive_command(ControlCommand(1, 0.0, 1.0, 0.0, 0.0, False, False, False), 10.0)
        model.step(0.1, 10.1)
        self.assertEqual(0.0, model.state.north_velocity_mps)

        model.receive_command(ControlCommand(2, 0.0, 1.0, 0.0, 0.0, True, True, False), 10.2)
        model.step(0.1, 10.21)
        self.assertEqual(0.0, model.state.north_velocity_mps)

    def test_stale_command_is_not_repeated(self):
        model = DroneSimulatorModel()
        model.set_connected(True)
        model.receive_command(ControlCommand(1, 0.0, 1.0, 0.0, 0.0, True, False, False), 10.0)
        model.step(0.1, 10.1)
        position_after_fresh_command = model.state.north_meters
        model.step(1.0, 10.4)
        self.assertAlmostEqual(position_after_fresh_command, model.state.north_meters)
        self.assertTrue(model.state.command_stale)

    def test_altitude_is_bounded(self):
        model = DroneSimulatorModel()
        model.set_connected(True)
        model.state.altitude_meters = MAX_ALTITUDE_METERS - 0.01
        model.receive_command(ControlCommand(1, 0.0, 0.0, 0.0, 1.0, True, False, False), 10.0)
        model.step(1.0, 10.1)
        self.assertEqual(MAX_ALTITUDE_METERS, model.state.altitude_meters)
        self.assertEqual(0.0, model.state.vertical_velocity_mps)


if __name__ == "__main__":
    unittest.main()
