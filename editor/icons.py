#!/usr/bin/env python3
"""icons.py - turn the 1.0 client's item icon atlas into static/icons.png.

GodsWar-1.0/Client/UI/Texture/Icon.gwo is NOT a DDS like the other .gwo
textures.  Measured on the archived file (4,194,348 bytes):

  * 65,562 bytes of header (44-byte head + a 65,518-byte block; the file
    is not documented anywhere, the layout below was found by measuring
    where the 36x36 tiles line up),
  * then a 1024 x 1008 image, 32 bpp, D3D A8R8G8B8 memory order (B,G,R,A),
    stored bottom-up (last row first),
  * 18 trailing bytes.

ItemBaseAttribute.xml's `Icon="x,y"` (catalog items.json `icon`) is the
top-left pixel of the item's 36x36 tile in that image (BtnTopRect 3,3,39,39
in the UI XML = a 36-px icon in a 42-px cell).  Verified: 36,144 = Short
Sword, 72,144 = Scimitar, 0,972 = Small Healing Potion, 252,972 = Small Mana
Potion, the equipment-slot silhouettes at 0,432 (head) etc.

    python3 icons.py [CLIENT_DIR]          # writes static/icons.png (needs Pillow)

The PNG is checked in so the VPS (no Pillow, no client) can serve it.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
HEADER = 65562
W, H = 1024, 1008


def build(client_dir, out):
    try:
        from PIL import Image
    except ImportError:
        print("Pillow is not installed (pip install Pillow); keeping the existing %s" % out)
        return 1
    src = os.path.join(client_dir, "UI", "Texture", "Icon.gwo")
    data = open(src, "rb").read()
    px = data[HEADER:HEADER + W * H * 4]
    if len(px) != W * H * 4:
        print("unexpected size of %s: %d bytes" % (src, len(data)))
        return 1
    im = Image.frombuffer("RGBA", (W, H), px, "raw", "BGRA", 0, 1).transpose(Image.FLIP_TOP_BOTTOM)
    im.save(out, optimize=True)
    print("wrote %s (%d x %d, %d bytes)" % (out, W, H, os.path.getsize(out)))
    return 0


if __name__ == "__main__":
    root = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # .../Godswar
    client = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "GodsWar-1.0", "Client")
    sys.exit(build(client, os.path.join(HERE, "static", "icons.png")))
