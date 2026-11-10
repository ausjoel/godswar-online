"""GodsWar Net.dll stream cipher (TEncryptClient / TEncryptServer from
Common/Include/sNet/Encryptor.h): two 256-byte tables from quadratic
recurrences, XORed byte-wise with running positions that persist across
messages on one socket. Client template parameters (Net.map of the 2.51
client and cNet.cpp of the 1.0 server): 0x20,0xFD,0x07,0x1F / 0x7A,0xCF,0xE5,0x3F.
"""


def table(a, b, c, first):
    t = []
    code = first
    for _ in range(256):
        t.append(code)
        code = (a * code * code + b * code + c) % 256
    assert code == first, "table does not cycle"
    return bytes(t)


CLIENT = ((0x20, 0xFD, 0x07, 0x1F), (0x7A, 0xCF, 0xE5, 0x3F))


class Stream:
    """one direction of one socket: XOR with t1[p1] ^ t2[p2], p1,p2 += 1"""

    def __init__(self, params=CLIENT):
        (a1, b1, c1, f1), (a2, b2, c2, f2) = params
        self.t1 = table(a1, b1, c1, f1)
        self.t2 = table(a2, b2, c2, f2)
        self.p = 0

    def xor(self, data):
        out = bytearray(data)
        for i in range(len(out)):
            out[i] ^= self.t1[self.p % 256] ^ self.t2[self.p % 256]
            self.p += 1
        return bytes(out)

    def encrypt(self, data):
        return self.xor(data)

    def decrypt(self, data):
        return self.xor(data)


if __name__ == "__main__":
    import sys
    s = Stream()
    print("keystream[0:16] =", s.t1[:16].hex(), "^", s.t2[:16].hex())
    data = bytes.fromhex(sys.argv[1]) if len(sys.argv) > 1 else b""
    if data:
        d = s.decrypt(data)
        for i in range(0, len(d), 16):
            print("%04x  %s  %s" % (i, d[i:i+16].hex(" "), "".join(chr(c) if 32 <= c < 127 else "." for c in d[i:i+16])))
