"""Firmware repository and OTA push logic.

Modules report their type and firmware version in the HELLO. The hub looks
that type up in the manifest and, if a newer build is on file, hands the
module a URL to pull it from. The hub never pushes bytes - the module does
the fetch, so a half-finished transfer is the module's problem to retry and
never leaves the hub blocked.
"""

import hashlib
import json
import os
import re
import time
from typing import Dict, Optional

MANIFEST_NAME = "manifest.json"

# per-module OTA states
IDLE = "idle"
OFFERED = "offered"
RUNNING = "running"
DONE = "done"
FAILED = "failed"

OFFER_TIMEOUT = 180.0     # give up waiting for a module to finish
REOFFER_DELAY = 30.0      # do not spam a module that just failed


def parse_version(v: str):
    """'1.4.2' -> (1, 4, 2). Unparseable pieces sort as 0."""
    if not v:
        return (0, 0, 0)
    parts = re.findall(r"\d+", str(v))[:3]
    while len(parts) < 3:
        parts.append("0")
    return tuple(int(p) for p in parts)


def newer(candidate: str, current: str) -> bool:
    return parse_version(candidate) > parse_version(current)


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


class FirmwareRepo:
    """The manifest is the source of truth; it is re-read on every access so
    you can drop in a new build without restarting the hub."""

    def __init__(self, directory: str):
        self.dir = directory
        os.makedirs(self.dir, exist_ok=True)
        self.path = os.path.join(self.dir, MANIFEST_NAME)
        if not os.path.exists(self.path):
            self._write({})

    def _write(self, blob: dict):
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(blob, fh, indent=2)
        os.replace(tmp, self.path)

    def all(self) -> dict:
        try:
            with open(self.path, encoding="utf-8") as fh:
                return json.load(fh)
        except Exception:
            return {}

    def get(self, module_type: str) -> Optional[dict]:
        entry = self.all().get(module_type)
        if not entry:
            return None
        if not os.path.exists(os.path.join(self.dir, entry.get("file", ""))):
            return None
        return entry

    def publish(self, module_type: str, binary_path: str, version: str,
                board: str = "", notes: str = "") -> dict:
        """Copy a built .bin into the repo and record it."""
        fname = "%s_%s.bin" % (module_type, version)
        dest = os.path.join(self.dir, fname)
        with open(binary_path, "rb") as src, open(dest, "wb") as dst:
            dst.write(src.read())
        entry = {
            "version": version,
            "file": fname,
            "sha256": sha256_file(dest),
            "size": os.path.getsize(dest),
            "board": board,
            "notes": notes,
            "published": time.strftime("%Y-%m-%d %H:%M:%S"),
        }
        blob = self.all()
        blob[module_type] = entry
        self._write(blob)
        return entry


class OtaTracker:
    """Per-module OTA bookkeeping, kept out of the Module class so a module
    dropping off the network does not lose its update history."""

    def __init__(self):
        self._state: Dict[str, dict] = {}

    def get(self, mid: str) -> dict:
        return self._state.setdefault(
            mid, {"state": IDLE, "pct": 0, "version": None,
                  "message": "", "changed": time.time()})

    def set(self, mid: str, state: str, **kw):
        rec = self.get(mid)
        rec.update(kw)
        rec["state"] = state
        rec["changed"] = time.time()

    def should_offer(self, mid: str) -> bool:
        rec = self.get(mid)
        age = time.time() - rec["changed"]
        if rec["state"] in (OFFERED, RUNNING):
            if age > OFFER_TIMEOUT:
                self.set(mid, FAILED, message="timed out")
                return False
            return False           # already in flight
        if rec["state"] == FAILED and age < REOFFER_DELAY:
            return False
        return True

    def snapshot(self) -> dict:
        return {k: dict(v) for k, v in self._state.items()}
