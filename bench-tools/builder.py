"""Optional: compile firmware from the hub.

Wraps arduino-cli if it is on PATH. Not required - you can build in the
Arduino IDE and drop the .bin into firmware_bin/ via the web UI instead.
"""

import glob
import json
import os
import shutil
import subprocess
import tempfile

DEFAULT_FQBN = "esp32:esp32:esp32s3"


def available() -> bool:
    return shutil.which("arduino-cli") is not None


def version() -> str:
    if not available():
        return ""
    try:
        out = subprocess.run(["arduino-cli", "version"],
                             capture_output=True, text=True, timeout=15)
        return out.stdout.strip()
    except Exception:
        return ""


def compile_sketch(sketch_dir: str, fqbn: str = DEFAULT_FQBN,
                   libraries_dir: str = "") -> dict:
    """Returns {'ok': bool, 'bin': path, 'log': str}."""
    if not available():
        return {"ok": False, "bin": "",
                "log": "arduino-cli not found on PATH. Install it, or build "
                       "in the Arduino IDE and upload the .bin instead."}
    if not os.path.isdir(sketch_dir):
        return {"ok": False, "bin": "", "log": "no such sketch: " + sketch_dir}

    outdir = tempfile.mkdtemp(prefix="simdeck_build_")
    cmd = ["arduino-cli", "compile", "--fqbn", fqbn,
           "--output-dir", outdir, sketch_dir]
    if libraries_dir:
        cmd += ["--libraries", libraries_dir]

    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    except subprocess.TimeoutExpired:
        return {"ok": False, "bin": "", "log": "build timed out after 15 min"}

    log = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return {"ok": False, "bin": "", "log": log}

    # the application image, not the bootloader or partition table
    cands = [p for p in glob.glob(os.path.join(outdir, "*.bin"))
             if not any(x in os.path.basename(p).lower()
                        for x in ("bootloader", "partitions", "boot_app"))]
    if not cands:
        return {"ok": False, "bin": "", "log": log + "\nno application .bin produced"}

    cands.sort(key=os.path.getsize, reverse=True)
    return {"ok": True, "bin": cands[0], "log": log}
