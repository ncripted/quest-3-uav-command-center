from __future__ import annotations

import socket
import threading
import time
from typing import Callable

from .model import ControlCommand, DroneSimulatorModel


class SimulatorServer:
    """Single-client localhost TCP server used through `adb reverse`."""

    def __init__(
        self,
        model: DroneSimulatorModel,
        port: int = 8765,
        on_status: Callable[[str], None] | None = None,
    ) -> None:
        self.model = model
        self.port = port
        self.on_status = on_status or (lambda _message: None)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="qcc-simulator-server", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2.0)

    def synchronized(self, action):
        with self._lock:
            return action()

    def _run(self) -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(("127.0.0.1", self.port))
            listener.listen(1)
            listener.settimeout(0.25)
            self.on_status(f"Waiting for Quest on 127.0.0.1:{self.port}")
            while not self._stop.is_set():
                try:
                    connection, _address = listener.accept()
                except socket.timeout:
                    continue
                self._serve_connection(connection)

    def _serve_connection(self, connection: socket.socket) -> None:
        self.on_status("Quest controller relay connected")
        with self._lock:
            self.model.set_connected(True)
        connection.settimeout(0.02)
        receive_buffer = b""
        next_telemetry = 0.0
        try:
            while not self._stop.is_set():
                now = time.monotonic()
                try:
                    chunk = connection.recv(4096)
                    if not chunk:
                        break
                    receive_buffer += chunk
                except socket.timeout:
                    pass

                while b"\n" in receive_buffer:
                    raw_line, receive_buffer = receive_buffer.split(b"\n", 1)
                    if not raw_line:
                        continue
                    try:
                        command = ControlCommand.from_json_line(raw_line.decode("utf-8"))
                        with self._lock:
                            self.model.receive_command(command, now)
                    except (UnicodeDecodeError, ValueError, KeyError) as error:
                        self.on_status(f"Rejected control message: {error}")

                if now >= next_telemetry:
                    with self._lock:
                        telemetry = self.model.telemetry_json_line().encode("utf-8")
                    connection.sendall(telemetry)
                    next_telemetry = now + 0.1
        except (ConnectionError, OSError) as error:
            self.on_status(f"Quest connection ended: {error}")
        finally:
            connection.close()
            with self._lock:
                self.model.set_connected(False)
            self.on_status("Quest disconnected; simulator neutralized")
