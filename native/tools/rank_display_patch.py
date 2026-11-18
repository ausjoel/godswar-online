#!/usr/bin/env python3
"""RANK-DISPLAY client patch: lets the server's /rank command cap the armour / weapon rank effect a player shows.
Usage: rank_display_patch.py IN.exe OUT.exe   (idempotent; asserts the original bytes; IN may already carry uifix)
Stubs: patches/rank_display.bin (assembled from rank_display.s) at VA 0x9C3500 (.rdata slack, section is executable;
the GWX1 decoder stub sits at 0x9C3270, other upstream stubs at 0x9C3300-0x9C341f and 0x9C3c00+)."""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
STUBS = open(os.path.join(HERE, "..", "patches", "rank_display.bin"), "rb").read()
CAVE = 0x9C3500
OFF = {"wpn": 0x00, "arm": 0x60, "ttl": 0xC0, "miss": 0xE0}
HOOKS = [  # (va, original bytes, stub)
    (0x4A7168, bytes.fromhex("8be833ff8d642400"), "wpn"),          # mov ebp,eax; xor edi,edi; lea esp,[esp]
    (0x4A7615, bytes.fromhex("33ffeb078da42400000000"), "arm"),  # xor edi,edi; jmp +7; lea esp,[esp] (7)
    (0x4ECCD9, bytes.fromhex("8b475089869c090000"), "ttl"),  # mov eax,[edi+0x50]; mov [esi+0x99c],eax
]

def va2off(va):
    return va - 0x400000  # .text and .rdata have file offset == RVA in this exe

def main(src, dst):
    d = bytearray(open(src, "rb").read())
    co = va2off(CAVE)
    if d[co:co + len(STUBS)] == STUBS:
        print("already patched"); open(dst, "wb").write(d); return
    assert d[co:co + 0x200] == bytes(0x200), "cave not empty"
    d[co:co + len(STUBS)] = STUBS
    for va, orig, stub in HOOKS:
        o = va2off(va)
        assert d[o:o + len(orig)] == orig, f"unexpected bytes at {va:#x}: {d[o:o+len(orig)].hex()}"
        rel = (CAVE + OFF[stub]) - (va + 5)
        d[o:o + len(orig)] = b"\xe9" + rel.to_bytes(4, "little", signed=True) + b"\x90" * (len(orig) - 5)
    # 0x4eccc0 `je 0x4ec995` (player not found) -> `je miss`: the own avatar is not in the other-players list
    o = va2off(0x4ECCC0); orig = bytes.fromhex("0f84cffcffff")
    assert d[o:o + 6] == orig, f"unexpected bytes at 0x4eccc0: {d[o:o+6].hex()}"
    d[o:o + 6] = b"\x0f\x84" + ((CAVE + OFF["miss"]) - (0x4ECCC0 + 6)).to_bytes(4, "little", signed=True)
    open(dst, "wb").write(d)
    print("patched", dst)

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
