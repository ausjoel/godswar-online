#!/usr/bin/env python3
"""uiscale_skip_patch.py IN.exe OUT.exe : skip the two whole-UI Scale(1,1) walks that lua_AddItem (0x698590) and
lua_AddText (0x697ed0) do around every added list/text line, when the UI scale is exactly 1.0.
Background and proof: GodsWar-Native/exe-port/MAP-LOAD-RE.md (2026-11-16). In world entry these walks are ~2,200 x
~16,300 windows = 18 M x87-heavy calls; a snapshot of 92 fields of every window before/after all 2,194 calls showed
no change at all (scale 1.0), so skipping them is exact at scale 1.0. At any other scale the original call runs.

Each call site `fstp dword [esp] ; call edx|eax` (5 bytes) becomes `call stub` (5 bytes, same return address).
The stub stores the float like the original, and if both float args are 0x3f800000 returns with `ret 8` (exactly what
the thiscall callee's `ret 8` does to the stack); otherwise it tail-jumps to the original target register.
Stubs live in the zero bytes after .text's VirtualSize (0x91bedc); VirtualSize is raised to 0x51b000 (page end, below
.rdata at 0x91c000) so the bytes are part of the section by the header too. Idempotent; refuses unexpected bytes.
Test copies only until the admin approves shipping."""
import hashlib, struct, sys

CAVE = 0x91bf00
SITES = [  # (VA of `fstp dword [esp]`, register byte of the following `call reg`)
    (0x6986bb, 0xd2),  # lua_AddItem, before the add (1/sx, 1/sy)
    (0x698754, 0xd0),  # lua_AddItem, after the add  (sx, sy)
    (0x698006, 0xd2),  # lua_AddText, before
    (0x6980c6, 0xd0),  # lua_AddText, after
]


def stub(jmpreg):  # jmpreg: 0xe2 = jmp edx, 0xe0 = jmp eax
    return bytes.fromhex("d95c2404" "817c24040000803f" "750d" "817c24080000803f" "7503" "c20800") + bytes([0xff, jmpreg])


STUBS = {0xd2: (CAVE, stub(0xe2)), 0xd0: (CAVE + 0x20, stub(0xe0))}


def sections(d):
    e = struct.unpack_from("<I", d, 0x3C)[0]; opt = e + 24
    n = struct.unpack_from("<H", d, e + 6)[0]; s = opt + struct.unpack_from("<H", d, e + 20)[0]
    return [(s + 40 * i, d[s + 40 * i:s + 40 * i + 8].rstrip(b"\0")) + struct.unpack_from("<IIII", d, s + 40 * i + 8) for i in range(n)]


def va2off(d, va):
    for hdr, name, vsz, rva, rsz, roff in sections(d):
        if rva <= va - 0x400000 < rva + max(vsz, rsz):
            return va - 0x400000 - rva + roff
    raise ValueError(hex(va))


def main():
    src, dst = sys.argv[1], sys.argv[2]
    d = bytearray(open(src, "rb").read())
    print("in  md5", hashlib.md5(d).hexdigest())
    hdr, name, vsz, rva, rsz, roff = sections(d)[0]
    assert name == b".text" and rva == 0x1000 and rsz == 0x51b000 and vsz in (0x51aedc, 0x51b000), (name, hex(vsz), hex(rsz))
    for reg, (va, code) in STUBS.items():
        o = va2off(d, va); cur = bytes(d[o:o + len(code)])
        if cur == code: print("stub %x already present" % va); continue
        assert cur == bytes(len(code)), "cave %x not empty: %s" % (va, cur.hex())
        d[o:o + len(code)] = code; print("stub %x written (%d bytes)" % (va, len(code)))
    for va, reg in SITES:
        o = va2off(d, va); old = bytes.fromhex("d91c24ff") + bytes([reg])
        new = b"\xe8" + struct.pack("<i", STUBS[reg][0] - (va + 5))
        cur = bytes(d[o:o + 5])
        if cur == new: print("site %x already patched" % va); continue
        assert cur == old, "site %x: unexpected bytes %s" % (va, cur.hex())
        d[o:o + 5] = new; print("site %x -> call %x" % (va, STUBS[reg][0]))
    if vsz != 0x51b000:
        struct.pack_into("<I", d, hdr + 8, 0x51b000); print(".text VirtualSize 0x51aedc -> 0x51b000")
    open(dst, "wb").write(d)
    print("out md5", hashlib.md5(d).hexdigest())


if __name__ == "__main__":
    main()
