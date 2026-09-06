"""Mock data source - lets you build and debug hardware with MSFS closed.

Simulates an A320 brake system loosely enough to exercise every needle:
accumulator charged to ~3000 psi, parking brake cycling on and off, and a
slow bleed-down so you can watch the smoothing behave.
"""

import math
import time
from typing import Dict, Iterable

from .base import DataSource


class MockSource(DataSource):

    def __init__(self, cycle_seconds: float = 20.0):
        self._t0 = time.time()
        self._cycle = cycle_seconds
        self._watch = set()
        self._running = False

    def start(self):
        self._running = True

    @property
    def connected(self) -> bool:
        return self._running

    @property
    def aircraft(self):
        return "SimDeck Mock Bench"

    def set_watchlist(self, names: Iterable[str]):
        self._watch = set(names)

    def read(self) -> Dict[str, float]:
        t = time.time() - self._t0
        phase = (t % self._cycle) / self._cycle

        # accumulator: charged, with a small pump ripple
        accum = 2950.0 + 60.0 * math.sin(t * 0.8)

        # parking brake on for the first 40% of each cycle
        if phase < 0.40:
            target = 2700.0
        else:
            target = 0.0

        # crude first-order approach so the needles have something to chase
        ramp = min(1.0, (phase % 0.40) / 0.05)
        left = target * ramp + 40.0 * math.sin(t * 2.1)
        right = target * ramp + 40.0 * math.sin(t * 2.1 + 0.4)
        left = max(0.0, left)
        right = max(0.0, right)

        # keys here are the RAW names the Fenix profile maps to
        return {
            "MOCK_ACCUM_PSI": accum,
            "MOCK_BRAKE_L_PSI": left,
            "MOCK_BRAKE_R_PSI": right,
        }

    def write(self, name: str, value: float) -> None:
        print("[mock] write %s = %s" % (name, value))
