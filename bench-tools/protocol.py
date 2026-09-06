"""SimDeck wire protocol.

Two planes, both UDP:

  Control plane  - JSON, low rate, module <-> hub
  Data plane     - packed binary, high rate, hub -> module

Keep this file in sync with firmware/SimDeckClient/SimDeckClient.h
"""

import struct

PROTO_VERSION = 1

CTRL_PORT = 27500     # hub listens here for HELLO / PING / EVENT
MODULE_PORT = 27501   # modules listen here for WELCOME and data frames

DATA_MAGIC = 0x5A

# flags byte in the data frame header
FLAG_SIM_OK = 0x01        # data source is alive
FLAG_PROFILE_OK = 0x02    # a matching aircraft profile is loaded

# magic, version, seq, count, flags, reserved(2)
#
# Eight bytes, not six. The reserved pair 4-byte aligns the float payload and
# makes the header length explicit rather than something each implementation
# derives from a format string. The firmware previously assumed 7 while this
# packed 6, so every frame was misread on real hardware.
_HEADER = struct.Struct("<BBHBBH")
HEADER_LEN = _HEADER.size  # 8

MAX_SLOTS = 64


def encode_frame(seq: int, flags: int, values) -> bytes:
    """Pack a data frame. values is an ordered list of floats (slot order).

    A value the hub could not resolve is sent as NaN; firmware treats NaN
    as 'no data' and holds the last good reading.
    """
    n = len(values)
    if n > MAX_SLOTS:
        raise ValueError("too many slots: %d" % n)
    head = _HEADER.pack(DATA_MAGIC, PROTO_VERSION, seq & 0xFFFF, n, flags, 0)
    return head + struct.pack("<%df" % n, *values)


def decode_frame(buf: bytes):
    """Returns (seq, flags, [floats]) or None if the packet is not ours."""
    if len(buf) < HEADER_LEN:
        return None
    magic, ver, seq, count, flags, _res = _HEADER.unpack_from(buf, 0)
    if magic != DATA_MAGIC or ver != PROTO_VERSION:
        return None
    if len(buf) < HEADER_LEN + 4 * count:
        return None
    vals = struct.unpack_from("<%df" % count, buf, HEADER_LEN)
    return seq, flags, list(vals)
