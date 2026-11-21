// mtl.h: Metal backend of the D3D9 shim (METAL-PLAN.md, review console d13). d3d9.c keeps the vertex pipeline and the
// batch ring; with GW_RENDERER=metal each queued job is encoded into Metal instead of the CPU band rasteriser.
// Surfaces stay in guest memory for the guest; their GPU copies are MTLTextures held in Obj->mt.
#pragma once
#include <stdint.h>

typedef struct { float pos[4]; float c[4]; float sp[4]; float t[4][2]; float fog; float pad[3]; } MtlV;   // pos = x, y, z, 1/w (attributes pre-multiplied by 1/w, as SV)

// Per-draw uniforms (layout shared with the MSL source in mtl.m: plain 32-bit fields only).
typedef struct {
  uint32_t st[8][12];   // per stage: 0 cop 1 c1 2 c2 3 aop 4 a1 5 a2 6 tci 7 c0 8 a0 9 result 10 hastex 11 -
  uint32_t nst, tfactor, specen, fogon, fogtab, fogw, fogcol, atest, afunc, aref, pad0, pad1;
  float fogstart, fogend, fogdens, pad2;
  float rtw, rth, pad3, pad4;
} MtlUni;

typedef struct {   // fixed-function state the pipeline / depth / sampler objects are keyed on
  uint32_t blend, sbf, dbf, bop, sepa, sba, dba, wmask, bfactor;
  uint32_t zen, zfunc, zwrite;
  uint32_t samp[8];  // per stage: bit 0 linear, bits 4-7 address u, bits 8-11 address v (1 wrap 2 mirror 3 clamp)
} MtlFixed;

int mtl_init(void);                                         // 1 = Metal device ready
void *mtl_texture(void *old, int w, int h, int depth);     // (re)create a w x h BGRA8 (or depth) texture; returns retained handle
void mtl_release(void *tex);
void mtl_upload(void *tex, const uint32_t *argb, int w, int h);   // ordered in the command stream
void mtl_clear(void *rt, void *ds, int x0, int y0, int x1, int y1, int fl, uint32_t col, float z);
void mtl_draw(void *rt, void *ds, const MtlUni *u, const MtlFixed *f, void *const tex[8], int x0, int y0, int x1, int y1, const MtlV *v, int nv);
void mtl_present(void *bb, int w, int h, uint32_t *front, void (*done)(void));   // async: copies the frame to front, then calls done
void mtl_finish(void);                                      // commit and wait for all queued GPU work
void mtl_readback(void *tex, uint8_t *dst, int pitch, int w, int h);   // after mtl_finish
void mtl_pool(void (*fn)(void *), void *arg);            // run fn inside an autorelease pool (the emulator thread has none)
