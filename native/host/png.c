// Minimal PNG writer (RGBA8) using zlib.
#include "gw.h"
#include <zlib.h>
static void be32(u8 *p, u32 v) { p[0] = v >> 24; p[1] = v >> 16; p[2] = v >> 8; p[3] = v; }
static void chunk(FILE *f, const char *t, const u8 *d, u32 n) {
  u8 h[8]; be32(h, n); memcpy(h + 4, t, 4); fwrite(h, 1, 8, f); if (n) fwrite(d, 1, n, f);
  uLong c = crc32(0, (const u8 *)t, 4); if (n) c = crc32(c, d, n); u8 cb[4]; be32(cb, (u32)c); fwrite(cb, 1, 4, f);
}
// src: 0xAARRGGBB pixels, pitch in pixels
int png_write_argb(const char *path, const u32 *src, int w, int h, int pitch, int force_opaque) {
  FILE *f = fopen(path, "wb"); if (!f) return -1;
  static const u8 sig[8] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10}; fwrite(sig, 1, 8, f);
  u8 ih[13]; be32(ih, w); be32(ih + 4, h); ih[8] = 8; ih[9] = 6; ih[10] = 0; ih[11] = 0; ih[12] = 0; chunk(f, "IHDR", ih, 13);
  size_t rl = (size_t)w * 4 + 1; u8 *raw = malloc(rl * h);
  for (int y = 0; y < h; y++) { u8 *r = raw + rl * y; r[0] = 0;
    for (int x = 0; x < w; x++) { u32 p = src[(size_t)y * pitch + x]; r[1 + 4 * x] = p >> 16; r[2 + 4 * x] = p >> 8; r[3 + 4 * x] = p; r[4 + 4 * x] = force_opaque ? 255 : p >> 24; } }
  uLongf cl = compressBound(rl * h); u8 *cz = malloc(cl); compress2(cz, &cl, raw, rl * h, 6);
  chunk(f, "IDAT", cz, cl); chunk(f, "IEND", NULL, 0); fclose(f); free(raw); free(cz); return 0;
}
