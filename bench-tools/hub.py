"""SimDeck - one PC-side service for every cockpit module.

Modules announce themselves and say what they want in logical terms
("brake.accum_psi"). An aircraft profile maps logical names onto whatever
the current add-on actually exposes. The hub streams values back at each
module's requested rate, forwards module inputs into the sim, and keeps
module firmware up to date.

    python hub.py --source mock
    python hub.py --source fsuipc

Then open http://localhost:27502
"""

import argparse
import json
import os
import socket
import time
from typing import Dict, List, Optional

import builder
import ota
import protocol as proto
import webui
from sources import FsuipcLuaSource, MockSource

HERE = os.path.dirname(os.path.abspath(__file__))
PROFILE_DIR = os.path.join(HERE, "profiles")
FIRMWARE_DIR = os.path.join(HERE, "firmware_bin")
SKETCH_ROOT = os.path.abspath(os.path.join(HERE, "..", "firmware"))

MODULE_TIMEOUT = 8.0
SOURCE_POLL_HZ = 60.0


# --------------------------------------------------------------------------
# profiles
# --------------------------------------------------------------------------

class Profile:
    def __init__(self, blob: dict, path: str):
        self.path = path
        self.name = blob.get("name", os.path.basename(path))
        self.match = [m.lower() for m in blob.get("match", [])]
        self.priority = int(blob.get("priority", 0))
        self.vars = blob.get("vars", {})
        self.inputs = blob.get("inputs", {})

    def score(self, aircraft: Optional[str]) -> int:
        """How well this profile fits. 0 = no match, higher = more specific.

        Longest matching token wins, so a profile matching "fenix a320" beats
        one matching just "a320". Without this, a generic profile loaded
        earlier silently steals aircraft from a specific one.
        """
        if not aircraft:
            return 0
        low = aircraft.lower()
        best = 0
        for m in self.match:
            if m in low and len(m) > best:
                best = len(m)
        return best + self.priority if best else 0

    def raw_names(self, logical_names) -> List[str]:
        out = []
        for ln in logical_names:
            spec = self.vars.get(ln)
            if spec:
                out.append(spec["name"])
        return out

    def resolve(self, logical: str, snapshot: Dict[str, float]) -> float:
        spec = self.vars.get(logical)
        if not spec:
            return float("nan")
        raw = snapshot.get(spec["name"])
        if raw is None:
            return float("nan")
        val = raw * float(spec.get("scale", 1.0)) + float(spec.get("offset", 0.0))
        lo, hi = spec.get("clamp", [None, None])
        if lo is not None:
            val = max(lo, val)
        if hi is not None:
            val = min(hi, val)
        return val


def load_profiles() -> List[Profile]:
    profiles = []
    if not os.path.isdir(PROFILE_DIR):
        return profiles
    for fn in sorted(os.listdir(PROFILE_DIR)):
        if not fn.endswith(".json"):
            continue
        path = os.path.join(PROFILE_DIR, fn)
        try:
            with open(path, encoding="utf-8") as fh:
                profiles.append(Profile(json.load(fh), path))
        except Exception as exc:
            print("[deck] bad profile %s: %s" % (fn, exc))
    return profiles


# --------------------------------------------------------------------------
# modules
# --------------------------------------------------------------------------

class Module:
    def __init__(self, mid, name, mtype, fw, addr, subs, rate, inputs):
        self.id = mid
        self.name = name
        self.type = mtype or mid.split("-")[0]
        self.fw = fw or "0.0.0"
        self.ip = addr[0]
        self.subs = subs
        self.rate = max(1.0, min(60.0, rate))
        self.inputs = {i.get("id"): i for i in inputs if isinstance(i, dict)}
        self.last_seen = time.time()
        self.seq = 0
        self._next_tx = 0.0

    @property
    def period(self) -> float:
        return 1.0 / self.rate

    def due(self, now: float) -> bool:
        return now >= self._next_tx

    def mark_sent(self, now: float):
        self._next_tx = now + self.period    # schedule from now, no drift


# --------------------------------------------------------------------------
# hub
# --------------------------------------------------------------------------

class Hub:
    def __init__(self, source, source_name: str, auto_update: bool = True):
        self.source = source
        self.source_name = source_name
        self.auto_update = auto_update
        self.profiles = load_profiles()
        self.profile: Optional[Profile] = None
        self.modules: Dict[str, Module] = {}
        self.snapshot: Dict[str, float] = {}
        self.repo = ota.FirmwareRepo(FIRMWARE_DIR)
        self.ota = ota.OtaTracker()
        self._watch_key = None
        self._last_poll = 0.0

        self.ctrl = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.ctrl.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.ctrl.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        self.ctrl.bind(("0.0.0.0", proto.CTRL_PORT))
        self.ctrl.setblocking(False)

        print("[deck] control udp/%d, %d profile(s)"
              % (proto.CTRL_PORT, len(self.profiles)))

    # -- local address ------------------------------------------------------

    def local_ip_for(self, peer: str) -> str:
        """Which of our addresses can that module actually reach us on?

        Matters on a PC with a VPN or several NICs, where the first address
        the OS reports is often not the one on the cockpit LAN. Getting this
        wrong means the module downloads firmware from an address that does
        not route, and OTA fails for no visible reason.
        """
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.connect((peer, 9))
            return s.getsockname()[0]
        except OSError:
            return "127.0.0.1"
        finally:
            s.close()

    # -- control plane ------------------------------------------------------

    def _drain_control(self):
        while True:
            try:
                data, addr = self.ctrl.recvfrom(4096)
            except (BlockingIOError, OSError):
                return
            try:
                msg = json.loads(data.decode("utf-8"))
            except Exception:
                continue
            self._handle(msg, addr)

    def _handle(self, msg: dict, addr):
        kind = msg.get("t")
        mid = msg.get("id")
        if not mid:
            return

        if kind == "hello":
            self._on_hello(msg, mid, addr)
        elif kind == "ping":
            mod = self.modules.get(mid)
            if mod:
                mod.last_seen = time.time()
                self._send_json(mod.ip, {"t": "pong"})
        elif kind == "ev":
            self._on_event(mid, msg)
        elif kind == "ota_status":
            self._on_ota_status(mid, msg)

    def _on_hello(self, msg, mid, addr):
        subs = list(msg.get("sub", []))[:proto.MAX_SLOTS]
        mod = Module(mid, msg.get("name", mid), msg.get("type"),
                     msg.get("fw"), addr, subs,
                     float(msg.get("rate", 25)), msg.get("in", []))
        known = self.modules.get(mid)
        fresh = (known is None or known.subs != subs or known.ip != mod.ip
                 or known.fw != mod.fw)
        self.modules[mid] = mod
        if fresh:
            print("[deck] + %s (%s) @ %s  fw %s  %d value(s) @ %.0fHz"
                  % (mod.name, mid, mod.ip, mod.fw, len(subs), mod.rate))
            self._refresh_watchlist()

        self._send_json(mod.ip, {
            "t": "welcome",
            "hub": 1,
            "slots": {n: i for i, n in enumerate(mod.subs)},
            "rate": mod.rate,
            "unknown": [n for n in mod.subs
                        if self.profile and n not in self.profile.vars],
        })

        if self.auto_update:
            self._maybe_offer_ota(mod)

    def _on_event(self, mid, msg):
        mod = self.modules.get(mid)
        if not mod:
            return
        mod.last_seen = time.time()
        if not self.profile:
            return
        key = msg.get("in")
        val = float(msg.get("v", 0))
        spec = self.profile.inputs.get(key)
        if not spec:
            print("[deck] %s: no mapping for input '%s'" % (mid, key))
            return
        try:
            self.source.write(spec["name"], val * float(spec.get("scale", 1.0)))
        except NotImplementedError:
            print("[deck] source cannot write (%s)" % key)

    def _on_ota_status(self, mid, msg):
        state = msg.get("state", "")
        pct = int(msg.get("pct", 0))
        err = msg.get("err", "")
        if state == "start":
            self.ota.set(mid, ota.RUNNING, pct=0, message="downloading")
        elif state == "progress":
            self.ota.set(mid, ota.RUNNING, pct=pct, message="downloading")
        elif state == "ok":
            self.ota.set(mid, ota.DONE, pct=100, message="rebooting")
            print("[deck] %s: firmware updated" % mid)
        elif state == "fail":
            self.ota.set(mid, ota.FAILED, message=err or "failed")
            print("[deck] %s: OTA failed: %s" % (mid, err))

    def _send_json(self, ip: str, obj: dict):
        try:
            self.ctrl.sendto(json.dumps(obj).encode("utf-8"),
                             (ip, proto.MODULE_PORT))
        except OSError:
            pass

    # -- OTA ----------------------------------------------------------------

    def _offer(self, mod: Module, entry: dict) -> None:
        host = self.local_ip_for(mod.ip)
        url = "http://%s:%d/fw/%s" % (host, webui.PORT, entry["file"])
        self._send_json(mod.ip, {
            "t": "ota",
            "url": url,
            "ver": entry["version"],
            "sha256": entry["sha256"],
            "size": entry.get("size", 0),
        })
        self.ota.set(mod.id, ota.OFFERED, pct=0,
                     version=entry["version"], message="offered")
        print("[deck] %s: offering fw %s (%s)"
              % (mod.id, entry["version"], entry["file"]))

    def _maybe_offer_ota(self, mod: Module) -> None:
        entry = self.repo.get(mod.type)
        if not entry:
            return
        if not ota.newer(entry["version"], mod.fw):
            return
        if not self.ota.should_offer(mod.id):
            return
        self._offer(mod, entry)

    def force_ota(self, mid: str):
        """Push regardless of version - the web UI button."""
        mod = self.modules.get(mid)
        if not mod:
            return False, "module not connected"
        entry = self.repo.get(mod.type)
        if not entry:
            return False, "no firmware published for type '%s'" % mod.type
        self.ota.set(mid, ota.IDLE)      # clear any backoff
        self._offer(mod, entry)
        return True, ""

    def identify(self, mid: str):
        mod = self.modules.get(mid)
        if mod:
            self._send_json(mod.ip, {"t": "identify"})

    def build_and_publish(self, module_type: str) -> dict:
        sketch = os.path.join(SKETCH_ROOT, module_type)
        res = builder.compile_sketch(sketch)
        if not res["ok"]:
            return {"ok": False, "log": res["log"][-4000:]}
        ver = time.strftime("%Y.%m.%d")
        entry = self.repo.publish(module_type, res["bin"], ver,
                                  board=builder.DEFAULT_FQBN,
                                  notes="built by hub")
        return {"ok": True, "entry": entry, "log": res["log"][-2000:]}

    # -- profile / watchlist ------------------------------------------------

    def _pick_profile(self):
        ac = self.source.aircraft
        scored = [(p.score(ac), p) for p in self.profiles]
        scored = [x for x in scored if x[0] > 0]
        chosen = max(scored, key=lambda x: x[0])[1] if scored else None
        if chosen is not self.profile:
            self.profile = chosen
            print("[deck] aircraft=%r -> profile=%s"
                  % (ac, chosen.name if chosen else "NONE"))
            self._refresh_watchlist(force=True)

    def _refresh_watchlist(self, force: bool = False):
        logical = set()
        for m in self.modules.values():
            logical.update(m.subs)
        raw = sorted(self.profile.raw_names(logical)) if self.profile else []
        key = tuple(raw)
        if force or key != self._watch_key:
            self._watch_key = key
            self.source.set_watchlist(raw)
            print("[deck] watching %d raw name(s)" % len(raw))

    def _reap(self, now: float):
        dead = [k for k, m in self.modules.items()
                if now - m.last_seen > MODULE_TIMEOUT]
        for k in dead:
            # A module mid-OTA goes quiet while it flashes and reboots.
            # Dropping it from the live list is right; wiping its OTA
            # record is not, so the tracker is deliberately left alone.
            print("[deck] - %s (timeout)" % self.modules[k].name)
            del self.modules[k]
        if dead:
            self._refresh_watchlist()

    # -- data plane ---------------------------------------------------------

    def _flags(self) -> int:
        f = 0
        if self.source.connected:
            f |= proto.FLAG_SIM_OK
        if self.profile:
            f |= proto.FLAG_PROFILE_OK
        return f

    def _stream(self, now: float):
        flags = self._flags()
        nan = float("nan")
        for mod in self.modules.values():
            if not mod.due(now):
                continue
            if self.profile:
                vals = [self.profile.resolve(n, self.snapshot) for n in mod.subs]
            else:
                vals = [nan] * len(mod.subs)
            try:
                self.ctrl.sendto(proto.encode_frame(mod.seq, flags, vals),
                                 (mod.ip, proto.MODULE_PORT))
            except OSError:
                pass
            mod.seq += 1
            mod.mark_sent(now)

    # -- status for the web UI ----------------------------------------------

    def status(self) -> dict:
        manifest = self.repo.all()
        mods = []
        for m in sorted(self.modules.values(), key=lambda x: x.id):
            vals = []
            for n in m.subs:
                v = self.profile.resolve(n, self.snapshot) if self.profile else None
                if v is not None and v != v:      # NaN is not JSON
                    v = None
                vals.append({"name": n, "value": v})
            entry = manifest.get(m.type)
            upd = (entry["version"] if entry
                   and ota.newer(entry["version"], m.fw) else None)
            mods.append({
                "id": m.id, "name": m.name, "type": m.type, "ip": m.ip,
                "fw": m.fw, "rate": int(m.rate), "values": vals,
                "update_available": upd,
                "ota": self.ota.get(m.id),
            })
        return {
            "source": self.source_name,
            "connected": self.source.connected,
            "aircraft": self.source.aircraft,
            "profile": self.profile.name if self.profile else None,
            "modules": mods,
        }

    # -- main ---------------------------------------------------------------

    def run(self):
        self.source.start()
        if webui.start(self):
            print("[deck] web ui http://localhost:%d" % webui.PORT)
        poll_period = 1.0 / SOURCE_POLL_HZ
        last_profile_check = 0.0
        try:
            while True:
                now = time.time()
                self._drain_control()

                if now - last_profile_check > 1.0:
                    self._pick_profile()
                    last_profile_check = now

                if now - self._last_poll >= poll_period:
                    self.snapshot = self.source.read()
                    self._last_poll = now

                self._stream(now)
                self._reap(now)
                time.sleep(0.002)
        except KeyboardInterrupt:
            print("\n[deck] stopping")
        finally:
            self.source.stop()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", choices=["mock", "fsuipc"], default="mock")
    ap.add_argument("--no-auto-update", action="store_true")
    ap.add_argument("--watchlist",
                    default=os.path.join(HERE, "lua", "simdeck_lvars.txt"))
    args = ap.parse_args()

    src = MockSource() if args.source == "mock" else FsuipcLuaSource(args.watchlist)
    Hub(src, args.source, auto_update=not args.no_auto_update).run()


if __name__ == "__main__":
    main()
