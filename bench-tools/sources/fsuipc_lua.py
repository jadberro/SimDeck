"""FSUIPC7 source.

Python talking to SimConnect for LVARs is painful. Instead we let FSUIPC7's
own Lua engine do the LVAR reads - it is built for exactly this - and have it
spit values at us over loopback UDP.

Data flow:

    MSFS <- FSUIPC7 <- lua/simdeck.lua --udp 27510--> this class --> hub
                              ^                                       |
                              +---------- udp 27511 (writes) ---------+

The hub writes the current watchlist to a text file that the Lua script
re-reads periodically, so adding a module never means editing Lua.
"""

import os
import socket
import threading
import time
from typing import Dict, Iterable, Optional

from .base import DataSource

RX_PORT = 27510   # lua -> python
TX_PORT = 27511   # python -> lua
STALE_AFTER = 2.0


class FsuipcLuaSource(DataSource):

    def __init__(self, watchlist_path: str, rx_port: int = RX_PORT,
                 tx_port: int = TX_PORT):
        self._path = watchlist_path
        self._rx_port = rx_port
        self._tx_port = tx_port
        self._sock: Optional[socket.socket] = None
        self._values: Dict[str, float] = {}
        self._aircraft: Optional[str] = None
        self._last_rx = 0.0
        self._lock = threading.Lock()
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()

    def start(self):
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", self._rx_port))
        self._sock.settimeout(0.5)
        self._thread = threading.Thread(target=self._rx_loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        if self._sock:
            self._sock.close()

    def _rx_loop(self):
        while not self._stop.is_set():
            try:
                data, _ = self._sock.recvfrom(4096)
            except socket.timeout:
                continue
            except OSError:
                break
            self._ingest(data.decode("utf-8", "replace"))

    def _ingest(self, text: str):
        """Payload is 'NAME=VALUE;NAME=VALUE;...'.

        The special key __AC carries the aircraft title.
        """
        updates = {}
        aircraft = None
        for pair in text.split(";"):
            pair = pair.strip()
            if not pair or "=" not in pair:
                continue
            key, _, val = pair.partition("=")
            key = key.strip()
            if key == "__AC":
                aircraft = val.strip()
                continue
            try:
                updates[key] = float(val)
            except ValueError:
                continue
        with self._lock:
            self._values.update(updates)
            if aircraft is not None:
                self._aircraft = aircraft
            self._last_rx = time.time()

    @property
    def connected(self) -> bool:
        with self._lock:
            return (time.time() - self._last_rx) < STALE_AFTER

    @property
    def aircraft(self):
        with self._lock:
            return self._aircraft

    def set_watchlist(self, names: Iterable[str]):
        names = sorted(set(n for n in names if n))
        tmp = self._path + ".tmp"
        os.makedirs(os.path.dirname(self._path) or ".", exist_ok=True)
        with open(tmp, "w", encoding="utf-8") as fh:
            fh.write("\n".join(names))
            fh.write("\n")
        os.replace(tmp, self._path)   # atomic, so Lua never reads a half file

    def read(self) -> Dict[str, float]:
        with self._lock:
            if (time.time() - self._last_rx) >= STALE_AFTER:
                return {}
            return dict(self._values)

    def write(self, name: str, value: float) -> None:
        if not self._sock:
            return
        payload = ("%s=%.6f" % (name, value)).encode("utf-8")
        try:
            self._sock.sendto(payload, ("127.0.0.1", self._tx_port))
        except OSError:
            pass
