#!/usr/bin/env python3
"""cull_sse_patch.py IN.exe OUT.exe : replace GodsWar.exe 0x7760d0 (AABB vs frustum test, x87 /Od code, the top
in-world hotspot: 19 % in exe-port, 26 % of the frame under Wine per PERF.md) with patches/cull_sse.bin (SSE, same
results: GW_SELFTEST=cull, 400,000 cases at x87 precision 24 and 53 bit, 0 mismatches). The rest of the old function,
up to 0x776480, becomes int3. Asserts the original bytes (sha256 of the 0x3b0-byte function); idempotent. Test copies
only until the admin approves shipping. Details: WORLD-FRAME-RE.md."""
import hashlib, os, struct, sys
VA, END = 0x7760d0, 0x776480
HERE = os.path.dirname(os.path.abspath(__file__))
def off(d, va):
    e = struct.unpack_from("<I", d, 0x3C)[0]; opt = e + 24; s = opt + struct.unpack_from("<H", d, e + 20)[0]
    vsz, rva, rsz, ro = struct.unpack_from("<IIII", d, s + 8)
    assert rva <= va - 0x400000 < rva + rsz; return va - 0x400000 - rva + ro
src, dst = sys.argv[1], sys.argv[2]
d = bytearray(open(src, "rb").read()); o = off(d, VA); n = END - VA
new = open(os.path.join(HERE, "..", "patches", "cull_sse.bin"), "rb").read(); assert len(new) <= n
new = new + b"\xcc" * (n - len(new)); cur = bytes(d[o:o + n])
if cur == new: print("0x7760d0 already patched")
else:
    h = hashlib.sha256(cur).hexdigest()
    assert h == "dcee6cbcc7947b7bff84b9adb7dc90f05779b3fc9e413c26f15cc32fdf76d5cc", "0x7760d0: unexpected bytes (sha256 %s)" % h
    d[o:o + n] = new; print("0x7760d0 replaced (%d bytes of SSE code, rest int3)" % len(new.rstrip(bytes([0xcc]))))
open(dst, "wb").write(d); print("out md5", hashlib.md5(d).hexdigest())
