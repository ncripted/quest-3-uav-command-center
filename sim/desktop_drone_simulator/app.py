from __future__ import annotations

import argparse
import math
import time
import tkinter as tk
from tkinter import ttk

from .model import DroneSimulatorModel
from .server import SimulatorServer


class SimulatorWindow:
    def __init__(self, root: tk.Tk, port: int) -> None:
        self.root = root
        self.model = DroneSimulatorModel()
        self.status_message = "Starting simulator"
        self.server = SimulatorServer(self.model, port, self._set_status)
        self.last_step = time.monotonic()
        self.trail: list[tuple[float, float]] = []

        root.title("Quest Command Center — Desktop Drone Simulator")
        root.geometry("1120x760")
        root.configure(bg="#07111b")
        root.protocol("WM_DELETE_WINDOW", self.close)

        style = ttk.Style()
        style.theme_use("clam")
        style.configure("QCC.TFrame", background="#07111b")
        style.configure("QCC.TLabel", background="#07111b", foreground="#d9f2ff", font=("Sans", 12))
        style.configure("Title.QCC.TLabel", font=("Sans", 18, "bold"), foreground="#8dffb2")

        container = ttk.Frame(root, style="QCC.TFrame", padding=14)
        container.pack(fill=tk.BOTH, expand=True)
        header = ttk.Label(container, text="QUEST COMMAND CENTER  |  SIMULATION ONLY", style="Title.QCC.TLabel")
        header.pack(anchor=tk.W)
        ttk.Label(
            container,
            text="No DJI SDK or aircraft output exists in this simulator path.",
            style="QCC.TLabel",
        ).pack(anchor=tk.W, pady=(2, 10))

        body = ttk.Frame(container, style="QCC.TFrame")
        body.pack(fill=tk.BOTH, expand=True)
        self.canvas = tk.Canvas(body, bg="#0a1824", highlightthickness=1, highlightbackground="#315169")
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        right = ttk.Frame(body, style="QCC.TFrame", padding=(16, 0, 0, 0))
        right.pack(side=tk.RIGHT, fill=tk.Y)
        self.telemetry_label = ttk.Label(right, style="QCC.TLabel", justify=tk.LEFT, width=36)
        self.telemetry_label.pack(anchor=tk.NW)
        ttk.Button(right, text="Reset simulated drone", command=self._reset).pack(fill=tk.X, pady=(18, 6))
        self.pause_button = ttk.Button(right, text="Pause simulation", command=self._toggle_pause)
        self.pause_button.pack(fill=tk.X)
        ttk.Label(
            right,
            text=(
                "Quest controls\n"
                "Right grip: authorize flight\n"
                "Left stick: altitude / yaw\n"
                "Right stick: forward / strafe\n"
                "B: brake / hover\n"
                "Left grip: manual takeover"
            ),
            style="QCC.TLabel",
            justify=tk.LEFT,
        ).pack(anchor=tk.W, pady=(24, 0))

        self.status_label = ttk.Label(container, style="QCC.TLabel")
        self.status_label.pack(anchor=tk.W, pady=(10, 0))
        self.server.start()
        self.root.after(16, self._tick)

    def _tick(self) -> None:
        now = time.monotonic()
        delta = min(0.1, now - self.last_step)
        self.last_step = now
        self.server.synchronized(lambda: self.model.step(delta, now))
        state = self.server.synchronized(lambda: self.model.state)
        self.trail.append((state.east_meters, state.north_meters))
        if len(self.trail) > 900:
            self.trail.pop(0)
        self._draw(state)
        self.root.after(16, self._tick)

    def _draw(self, state) -> None:
        width = max(1, self.canvas.winfo_width())
        height = max(1, self.canvas.winfo_height())
        center_x = width * 0.5
        center_y = height * 0.5
        pixels_per_meter = 90.0
        self.canvas.delete("all")

        for offset in range(-8, 9):
            x = center_x + offset * pixels_per_meter
            y = center_y + offset * pixels_per_meter
            self.canvas.create_line(x, 0, x, height, fill="#183247")
            self.canvas.create_line(0, y, width, y, fill="#183247")
        self.canvas.create_text(32, 24, text="N ↑", fill="#8dffb2", font=("Sans", 14, "bold"))

        if len(self.trail) > 1:
            points = []
            for east, north in self.trail:
                points.extend((center_x + east * pixels_per_meter, center_y - north * pixels_per_meter))
            self.canvas.create_line(*points, fill="#3e8da9", width=2)

        drone_x = center_x + state.east_meters * pixels_per_meter
        drone_y = center_y - state.north_meters * pixels_per_meter
        heading = math.radians(state.heading_degrees)
        shape = []
        for angle_offset, radius in ((0.0, 20.0), (2.5, 14.0), (-2.5, 14.0)):
            angle = heading + angle_offset
            shape.extend((drone_x + math.sin(angle) * radius, drone_y - math.cos(angle) * radius))
        color = "#8dffb2" if state.command_authorized else "#f7c75b"
        self.canvas.create_polygon(*shape, fill=color, outline="#ffffff", width=2)
        self.canvas.create_oval(drone_x - 5, drone_y - 5, drone_x + 5, drone_y + 5, fill="#ff5b5b")

        horizontal_speed = math.hypot(state.north_velocity_mps, state.east_velocity_mps)
        self.telemetry_label.configure(
            text=(
                f"LINK           {'CONNECTED' if state.connected else 'WAITING'}\n"
                f"CONTROL        {'AUTHORIZED' if state.command_authorized else 'NEUTRAL'}\n"
                f"INPUT          {'STALE / HOVER' if state.command_stale else 'FRESH'}\n"
                f"SEQUENCE       {state.last_sequence}\n\n"
                f"ALTITUDE       {state.altitude_meters:5.2f} m\n"
                f"HEADING        {state.heading_degrees:5.1f}°\n"
                f"HORIZONTAL     {horizontal_speed:5.2f} m/s\n"
                f"VERTICAL       {state.vertical_velocity_mps:5.2f} m/s\n"
                f"NORTH          {state.north_meters:5.2f} m\n"
                f"EAST           {state.east_meters:5.2f} m\n"
                f"BATTERY        {state.battery_percent:5.1f}%\n"
                f"TAKEOVERS      {state.takeover_count}"
            )
        )
        self.status_label.configure(text=self.status_message)

    def _set_status(self, message: str) -> None:
        self.status_message = message

    def _reset(self) -> None:
        self.server.synchronized(self.model.reset_airborne)
        self.trail.clear()

    def _toggle_pause(self) -> None:
        def toggle():
            self.model.paused = not self.model.paused
            return self.model.paused

        paused = self.server.synchronized(toggle)
        self.pause_button.configure(text="Resume simulation" if paused else "Pause simulation")

    def close(self) -> None:
        self.server.stop()
        self.root.destroy()


def main() -> None:
    parser = argparse.ArgumentParser(description="Quest controller desktop drone simulator")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    root = tk.Tk()
    SimulatorWindow(root, args.port)
    root.mainloop()


if __name__ == "__main__":
    main()
