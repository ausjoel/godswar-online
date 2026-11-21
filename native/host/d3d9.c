// D3D9 on the host: COM objects live in guest memory (vtables of thunks), state + a fixed-function
#include <sys/resource.h>
// software rasterizer live here. Present() writes PNGs (offscreen; no window).
#include "gw.h"
#include <math.h>
#include <sys/stat.h>
#include <time.h>

int png_write_argb(const char *path, const u32 *src, int w, int h, int pitch, int force_opaque);
#include "mtl.h"
static int g_metal;   // 1 = GW_RENDERER=metal: jobs are encoded for the GPU (host/mtl.m) instead of the band rasteriser
extern void *vs_compile(u32 code, u32 nbytes); extern int vs_ok(void *p); extern int vs_input_reg(void *p, int usage, int idx);
extern void vs_begin(void *p, float (*vsc)[4]); extern void vs_run(void *p, float in[16][4], float pos[4], float d0[4], float d1[4], float t[8][4]);


#define D3D_OK 0
#define D3DERR_NOTAVAILABLE 0x8876086Au
#define D3DERR_INVALIDCALL 0x8876086Cu
#define D3DERR_NOTFOUND 0x88760866u
#define E_NOINTERFACE 0x80004002u

enum { K_D3D = 1, K_DEV, K_TEX, K_CUBE, K_VOLTEX, K_SURF, K_VB, K_IB, K_SB, K_VDECL, K_VS, K_PS, K_QUERY, K_SWAP };
enum { F_R8G8B8 = 20, F_A8R8G8B8 = 21, F_X8R8G8B8 = 22, F_R5G6B5 = 23, F_X1R5G5B5 = 24, F_A1R5G5B5 = 25, F_A4R4G4B4 = 26,
       F_A8 = 28, F_X4R4G4B4 = 30, F_A8B8G8R8 = 32, F_X8B8G8R8 = 33, F_L8 = 50, F_A8L8 = 51, F_D16 = 80, F_D24S8 = 75, F_D24X8 = 77, F_D32 = 71,
       F_INDEX16 = 101, F_INDEX32 = 102 };
#define FOURCC(a, b, c, d) ((u32)(a) | ((u32)(b) << 8) | ((u32)(c) << 16) | ((u32)(d) << 24))
#define F_DXT1 FOURCC('D', 'X', 'T', '1')
#define F_DXT2 FOURCC('D', 'X', 'T', '2')
#define F_DXT3 FOURCC('D', 'X', 'T', '3')
#define F_DXT4 FOURCC('D', 'X', 'T', '4')
#define F_DXT5 FOURCC('D', 'X', 'T', '5')

typedef struct { float m[16]; } Mat;
struct Obj;
typedef struct State {
  u32 rs[256]; u32 tss[8][33]; u32 ss[16][14]; struct Obj *tex[16];
  Mat xf[512]; u32 vp[6]; u8 mat[68]; u8 light[16][104]; u32 lighton[16];
  u32 fvf; struct Obj *vdecl, *vs, *ps; float vsc[256][4]; float psc[32][4];
  struct Obj *vb[4]; u32 vboff[4], vbstride[4]; struct Obj *ib; u32 scissor[4];
} State;
typedef struct Mask {
  u8 rs[256]; u8 tss[8][33]; u8 ss[16][14]; u8 tex[16]; u8 xf[512]; u8 vp, mat, light[16], lighton[16], fvf, vdecl, vs, ps, vsc[256], psc[32], vb[4], ib, scissor;
} Mask;

typedef struct Obj {
  int kind; u32 g; int ref; u32 usage, pool, fmt;
  // surfaces
  int w, h, pitch, size; u32 mem; struct Obj *container; int level, face; u32 *cache; int dirty; float *z; unsigned long rser, wser;  // raster-queue serials of the last queued read / write
  void *mt; int mcpu, mgpu;   // Metal (GW_RENDERER=metal): GPU texture; CPU wrote guest memory since the last upload; GPU wrote since the last read-back
  // textures
  int levels; struct Obj *lv[16 * 6];
  // buffers
  u32 bsize;
  // state block
  State *st; Mask *mask;
  // vdecl / shaders
  u8 decl[64 * 8]; int ndecl; u32 code; void *vsp;
  // device
  struct Obj *rt, *ds, *bb; State cur; State *recst; Mask *recmask; int recording; u32 d3d; u32 pp[14]; int frames;
} Obj;

static void rq_sync(void); static void rq_init(void); static void obj_sync(struct Obj *s);
static Obj **objs; static int nobjs, capobjs;
static u32 vt_d3d, vt_dev, vt_tex, vt_cube, vt_surf, vt_vb, vt_ib, vt_sb, vt_vdecl, vt_vs, vt_ps, vt_query, vt_swap, vt_voltex;
static Obj *new_obj(int kind, u32 vt) {
  Obj *o = calloc(1, sizeof *o); o->kind = kind; o->ref = 1;
  if (nobjs == capobjs) { capobjs = capobjs ? capobjs * 2 : 1024; objs = realloc(objs, capobjs * sizeof(Obj *)); }
  objs[nobjs] = o; o->g = heap_alloc(16, 1); w32(o->g, vt); w32(o->g + 4, nobjs); w32(o->g + 8, 0x4433aa01); nobjs++;
  return o;
}
static Obj *O(u32 g) {
  if (!g || r32(g + 8) != 0x4433aa01) gw_fatal("d3d: bad object %08x", g);
  return objs[r32(g + 4)];
}
static Obj *ON(u32 g) { return g ? O(g) : NULL; }
#define THIS O(ARG(0))
static Obj *g_dev;
static long g_draws, g_tris, g_pix;

// ---------- formats ----------
static int fmt_bpp(u32 f) {
  switch (f) { case F_A8R8G8B8: case F_X8R8G8B8: case F_A8B8G8R8: case F_X8B8G8R8: case F_D24S8: case F_D24X8: case F_D32: case F_INDEX32: return 32;
    case F_R8G8B8: return 24; case F_R5G6B5: case F_X1R5G5B5: case F_A1R5G5B5: case F_A4R4G4B4: case F_X4R4G4B4: case F_A8L8: case F_D16: case F_INDEX16: return 16;
    case F_A8: case F_L8: return 8; default: return 0; }
}
static int is_dxt(u32 f) { return f == F_DXT1 || f == F_DXT2 || f == F_DXT3 || f == F_DXT4 || f == F_DXT5; }
static int fmt_ok(u32 f) { return fmt_bpp(f) || is_dxt(f); }
static void surf_layout(Obj *s) {
  if (is_dxt(s->fmt)) { int bw = (s->w + 3) / 4, bh = (s->h + 3) / 4, bs = s->fmt == F_DXT1 ? 8 : 16; s->pitch = bw * bs; s->size = s->pitch * bh; }
  else { int bpp = fmt_bpp(s->fmt); if (!bpp) bpp = 32; s->pitch = ((s->w * bpp / 8) + 3) & ~3; s->size = s->pitch * s->h; }
}
static Obj *new_surface(int w, int h, u32 fmt, Obj *container, int level) {
  Obj *s = new_obj(K_SURF, vt_surf); s->w = w; s->h = h; s->fmt = fmt; s->container = container; s->level = level; surf_layout(s);
  s->mem = s->size > 4000 ? vm_alloc(s->size, 0x1000) : heap_alloc(s->size, 1); s->dirty = 1;
  return s;
}
static u32 c565(u16 p) { u32 r = (p >> 11) & 31, g = (p >> 5) & 63, b = p & 31; return 0xFF000000u | ((r << 3 | r >> 2) << 16) | ((g << 2 | g >> 4) << 8) | (b << 3 | b >> 2); }
static void dxt_color_block(const u8 *b, u32 out[16], int dxt1) {
  u16 c0 = b[0] | b[1] << 8, c1 = b[2] | b[3] << 8; u32 c[4]; c[0] = c565(c0); c[1] = c565(c1);
  u32 r0 = (c[0] >> 16) & 255, g0 = (c[0] >> 8) & 255, b0 = c[0] & 255, r1 = (c[1] >> 16) & 255, g1 = (c[1] >> 8) & 255, b1 = c[1] & 255;
  if (c0 > c1 || !dxt1) {
    c[2] = 0xFF000000u | ((2 * r0 + r1) / 3) << 16 | ((2 * g0 + g1) / 3) << 8 | ((2 * b0 + b1) / 3);
    c[3] = 0xFF000000u | ((r0 + 2 * r1) / 3) << 16 | ((g0 + 2 * g1) / 3) << 8 | ((b0 + 2 * b1) / 3);
  } else { c[2] = 0xFF000000u | ((r0 + r1) / 2) << 16 | ((g0 + g1) / 2) << 8 | ((b0 + b1) / 2); c[3] = 0; }
  u32 bits = b[4] | b[5] << 8 | b[6] << 16 | (u32)b[7] << 24;
  for (int i = 0; i < 16; i++) out[i] = c[(bits >> (2 * i)) & 3];
}
static u32 *surf_argb_(Obj *s);
static u32 *surf_argb(Obj *s) { unsigned d0 = g_sc.decodes; uint64_t t = mono_ns(); u32 *r = surf_argb_(s); if (g_sc.decodes != d0) g_sc.decode_ns += mono_ns() - t; return r; }
static u32 *surf_argb_(Obj *s) {   // decoded ARGB view of a surface
  if (s->fmt == F_A8R8G8B8 && s->pitch == s->w * 4) return (u32 *)P(s->mem);
  if (!s->cache) { s->cache = malloc((size_t)s->w * s->h * 4); s->dirty = 1; }
  if (!s->dirty) return s->cache;
  s->dirty = 0; u8 *m = P(s->mem); u32 *o = s->cache; int w = s->w, h = s->h;
  g_sc.decodes++;
  if (is_dxt(s->fmt)) {
    int bw = (w + 3) / 4, bh = (h + 3) / 4, bs = s->fmt == F_DXT1 ? 8 : 16;
    for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++) {
      const u8 *b = m + (size_t)by * s->pitch + (size_t)bx * bs; u32 px[16];
      if (s->fmt == F_DXT1) dxt_color_block(b, px, 1);
      else {
        dxt_color_block(b + 8, px, 0);
        if (s->fmt == F_DXT2 || s->fmt == F_DXT3) { for (int i = 0; i < 16; i++) { u32 a = (b[i / 2] >> (4 * (i & 1))) & 15; px[i] = (px[i] & 0xFFFFFF) | (a * 17) << 24; } }
        else { u32 a0 = b[0], a1 = b[1], a[8]; a[0] = a0; a[1] = a1;
          if (a0 > a1) for (int i = 1; i < 7; i++) a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
          else { for (int i = 1; i < 5; i++) a[i + 1] = ((5 - i) * a0 + i * a1) / 5; a[6] = 0; a[7] = 255; }
          uint64_t bits = 0; for (int i = 0; i < 6; i++) bits |= (uint64_t)b[2 + i] << (8 * i);
          for (int i = 0; i < 16; i++) px[i] = (px[i] & 0xFFFFFF) | a[(bits >> (3 * i)) & 7] << 24; }
      }
      for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) { int X = bx * 4 + x, Y = by * 4 + y; if (X < w && Y < h) o[(size_t)Y * w + X] = px[y * 4 + x]; }
    }
    return o;
  }
  for (int y = 0; y < h; y++) {
    u8 *r = m + (size_t)y * s->pitch; u32 *d = o + (size_t)y * w;
    for (int x = 0; x < w; x++) {
      u32 c;
      switch (s->fmt) {
        case F_A8R8G8B8: c = ((u32 *)r)[x]; break;
        case F_X8R8G8B8: c = ((u32 *)r)[x] | 0xFF000000u; break;
        case F_A8B8G8R8: { u32 v = ((u32 *)r)[x]; c = (v & 0xFF00FF00u) | (v & 0xFF) << 16 | (v >> 16 & 0xFF); break; }
        case F_X8B8G8R8: { u32 v = ((u32 *)r)[x]; c = 0xFF000000u | (v & 0xFF00) | (v & 0xFF) << 16 | (v >> 16 & 0xFF); break; }
        case F_R8G8B8: c = 0xFF000000u | r[3 * x + 2] << 16 | r[3 * x + 1] << 8 | r[3 * x]; break;
        case F_R5G6B5: c = c565(((u16 *)r)[x]); break;
        case F_X1R5G5B5: case F_A1R5G5B5: { u16 p = ((u16 *)r)[x]; u32 R = (p >> 10) & 31, G = (p >> 5) & 31, B = p & 31;
          c = ((s->fmt == F_X1R5G5B5 || (p & 0x8000)) ? 0xFF000000u : 0) | (R << 3 | R >> 2) << 16 | (G << 3 | G >> 2) << 8 | (B << 3 | B >> 2); break; }
        case F_A4R4G4B4: case F_X4R4G4B4: { u16 p = ((u16 *)r)[x]; c = ((s->fmt == F_X4R4G4B4 ? 15 : p >> 12) * 17u) << 24 | ((p >> 8) & 15) * 17u << 16 | ((p >> 4) & 15) * 17u << 8 | (p & 15) * 17u; break; }
        case F_A8: c = (u32)r[x] << 24; break;
        case F_L8: c = 0xFF000000u | r[x] * 0x010101u; break;
        case F_A8L8: c = (u32)r[2 * x + 1] << 24 | r[2 * x] * 0x010101u; break;
        default: c = 0xFFFF00FFu;
      }
      d[x] = c;
    }
  }
  return o;
}

// ---------- vtable helpers ----------
typedef struct { const char *name; int nargs; shim_fn fn; } VDef;
static u32 make_vtbl(const char *iface, const VDef *v, int n) {
  u32 vt = heap_alloc(4 * n, 1);
  for (int i = 0; i < n; i++) { char nm[96]; snprintf(nm, sizeof nm, "%s::%s", iface, v[i].name); w32(vt + 4 * i, thunk_alloc_named("D3D9", nm, v[i].nargs, v[i].fn)); }
  return vt;
}
static u32 Unk_QI(void) { Obj *o = THIS; o->ref++; w32(ARG(2), ARG(0)); return 0; }
static u32 Unk_AddRef(void) { return ++THIS->ref; }
static u32 Unk_Release(void) { Obj *o = THIS; if (o->ref > 0) o->ref--; return o->ref; }
static u32 Res_GetDevice(void) { w32(ARG(1), g_dev->g); g_dev->ref++; return 0; }
static u32 Res_SetPrivateData(void) { return 0; }
static u32 Res_GetPrivateData(void) { return D3DERR_NOTFOUND; }
static u32 Res_FreePrivateData(void) { return 0; }
static u32 Res_SetPriority(void) { return 0; }
static u32 Res_GetPriority(void) { return 0; }
static u32 Res_PreLoad(void) { return 0; }
static u32 Res_GetType(void) { switch (THIS->kind) { case K_SURF: return 1; case K_TEX: return 3; case K_VOLTEX: return 4; case K_CUBE: return 5; case K_VB: return 6; case K_IB: return 7; } return 0; }
#define UNK {"QueryInterface", 3, Unk_QI}, {"AddRef", 1, Unk_AddRef}, {"Release", 1, Unk_Release}
#define RES UNK, {"GetDevice", 2, Res_GetDevice}, {"SetPrivateData", 5, Res_SetPrivateData}, {"GetPrivateData", 4, Res_GetPrivateData}, \
  {"FreePrivateData", 2, Res_FreePrivateData}, {"SetPriority", 2, Res_SetPriority}, {"GetPriority", 1, Res_GetPriority}, {"PreLoad", 1, Res_PreLoad}, {"GetType", 1, Res_GetType}

// ---------- IDirect3D9 ----------
static u32 D3D_GetAdapterCount(void) { return 1; }
static u32 D3D_GetAdapterIdentifier(void) {
  u32 p = ARG(3); memset(P(p), 0, 1100); strcpy((char *)P(p), "gwhost.dll"); strcpy((char *)P(p + 512), "GodsWar native software renderer");
  strcpy((char *)P(p + 1024), "\\\\.\\DISPLAY1"); w32(p + 1056, 0x00010000); w32(p + 1060, 0x0006000E); w32(p + 1064, 0x10DE); w32(p + 1068, 0x0402); w32(p + 1096, 1); return 0;
}
static u32 D3D_GetAdapterModeCount(void) { return ARG(2) == F_X8R8G8B8 ? 1 : 0; }
static u32 D3D_EnumAdapterModes(void) { u32 p = ARG(4); w32(p, 1024); w32(p + 4, 768); w32(p + 8, 60); w32(p + 12, F_X8R8G8B8); return 0; }
static u32 D3D_GetAdapterDisplayMode(void) { u32 p = ARG(2); w32(p, 1920); w32(p + 4, 1080); w32(p + 8, 60); w32(p + 12, F_X8R8G8B8); return 0; }
static u32 D3D_CheckDeviceType(void) { return 0; }
static u32 D3D_CheckDeviceFormat(void) { u32 usage = ARG(4), rtype = ARG(5), f = ARG(6);
  if (usage & 2) return (f == F_D16 || f == F_D24S8 || f == F_D24X8 || f == F_D32) ? 0 : D3DERR_NOTAVAILABLE;
  if (usage & 1) return (f == F_A8R8G8B8 || f == F_X8R8G8B8) ? 0 : D3DERR_NOTAVAILABLE;
  (void)rtype; return fmt_ok(f) && f != F_D16 && f != F_D24S8 && f != F_D24X8 ? 0 : D3DERR_NOTAVAILABLE; }
static u32 D3D_CheckDeviceMultiSampleType(void) { return ARG(5) == 0 ? 0 : D3DERR_NOTAVAILABLE; }
static u32 D3D_CheckDepthStencilMatch(void) { return 0; }
static u32 D3D_CheckDeviceFormatConversion(void) { return 0; }
static void fill_caps(u32 p) {
  memset(P(p), 0, 304);
  w32(p + 0x00, 1); w32(p + 0x08, 0x20000); w32(p + 0x0C, 0x20000000 | 0x40000000); w32(p + 0x10, 0x20 | 0x80);
  w32(p + 0x14, 0x8000000F); w32(p + 0x18, 0); w32(p + 0x1C, 0x1BBEF0); w32(p + 0x20, 0x2FCF2); w32(p + 0x24, 0x7319191);
  w32(p + 0x28, 0xFF); w32(p + 0x2C, 0x3FFF); w32(p + 0x30, 0x3FFF); w32(p + 0x34, 0xFF); w32(p + 0x38, 0x84208);
  w32(p + 0x3C, 0x1EC45); w32(p + 0x40, 0x3030700); w32(p + 0x44, 0x3030300); w32(p + 0x48, 0x3030300); w32(p + 0x4C, 0x3F); w32(p + 0x50, 0x3F);
  w32(p + 0x54, 0x1F); w32(p + 0x58, 4096); w32(p + 0x5C, 4096); w32(p + 0x60, 512); w32(p + 0x64, 8192); w32(p + 0x68, 4096); w32(p + 0x6C, 16);
  wf(p + 0x70, 1e10f); wf(p + 0x74, -1e8f); wf(p + 0x78, -1e8f); wf(p + 0x7C, 1e8f); wf(p + 0x80, 1e8f);
  w32(p + 0x88, 0x1FF); w32(p + 0x8C, 0x180008); w32(p + 0x90, 0x3FEFFFF); w32(p + 0x94, 8); w32(p + 0x98, 8); w32(p + 0x9C, 0x13B);
  w32(p + 0xA0, 8); w32(p + 0xA4, 6); w32(p + 0xA8, 4); w32(p + 0xAC, 0); wf(p + 0xB0, 64.0f); w32(p + 0xB4, 0xFFFFF); w32(p + 0xB8, 0xFFFFFF);
  w32(p + 0xBC, 16); w32(p + 0xC0, 255); w32(p + 0xC4, 0xFFFE0200); w32(p + 0xC8, 256); w32(p + 0xCC, 0xFFFF0200); wf(p + 0xD0, 65504.0f);
  w32(p + 0xD4, 0x51); w32(p + 0xE8, 1); w32(p + 0xEC, 0x30F); w32(p + 0xF0, 1); w32(p + 0xF4, 0x3000300);
  w32(p + 0xF8, 1); w32(p + 0xFC, 24); w32(p + 0x100, 1); w32(p + 0x104, 4);
  w32(p + 0x108, 0x1F); w32(p + 0x10C, 24); w32(p + 0x110, 32); w32(p + 0x114, 4); w32(p + 0x118, 512);
  w32(p + 0x120, 0xFFFF); w32(p + 0x124, 0xFFFF);
}
static u32 D3D_GetDeviceCaps(void) { fill_caps(ARG(3)); return 0; }
static u32 D3D_GetAdapterMonitor(void) { return 0x0A000001; }
static void dev_defaults(Obj *d);
static u32 D3D_CreateDevice(void) {
  u32 pp = ARG(5); Obj *d = new_obj(K_DEV, vt_dev); g_dev = d; d->d3d = ARG(0);
  for (int i = 0; i < 14; i++) d->pp[i] = r32(pp + 4 * i);
  int w = d->pp[0] ? d->pp[0] : 1024, h = d->pp[1] ? d->pp[1] : 768;
  d->pp[0] = w; d->pp[1] = h; w32(pp, w); w32(pp + 4, h);
  if (!d->pp[2]) { d->pp[2] = F_X8R8G8B8; w32(pp + 8, F_X8R8G8B8); }
  gw_log("CreateDevice %dx%d fmt=%u windowed=%u autodepth=%u depthfmt=%u flags=%x behaviour=%x", w, h, d->pp[2], d->pp[8], d->pp[9], d->pp[10], d->pp[11], ARG(4));
  d->bb = new_surface(w, h, F_A8R8G8B8, NULL, 0); d->bb->usage = 1; d->rt = d->bb;
  if (d->pp[9]) { d->ds = new_surface(w, h, F_D24S8, NULL, 0); d->ds->z = malloc((size_t)w * h * 4); for (int i = 0; i < w * h; i++) d->ds->z[i] = 1.0f; }
  dev_defaults(d);
  w32(ARG(6), d->g); return 0;
}

// ---------- device state ----------
static void mat_ident(Mat *m) { memset(m, 0, sizeof *m); m->m[0] = m->m[5] = m->m[10] = m->m[15] = 1; }
static void dev_defaults(Obj *d) {
  State *s = &d->cur; memset(s, 0, sizeof *s);
  u32 *rs = s->rs;
  rs[7] = d->ds ? 1 : 0; rs[8] = 3; rs[9] = 2; rs[14] = 1; rs[19] = 2; rs[20] = 1; rs[22] = 3; rs[23] = 4; rs[25] = 8; rs[137] = 1;
  rs[141] = 1; rs[145] = 1; rs[146] = 2; rs[147] = 0; rs[148] = 0; rs[168] = 0xF; rs[171] = 1; rs[60] = 0xFFFFFFFF; rs[52] = 0; rs[53] = 0xFFFFFFFF;
  rs[58] = 0xFFFFFFFF; rs[59] = 0xFFFFFFFF; rs[34] = 0; rs[154] = 0x3F800000; rs[207] = 2; rs[208] = 1; rs[209] = 1; rs[193] = 0xFFFFFFFF;  // BLENDFACTOR default
  for (int i = 0; i < 8; i++) {
    u32 *t = s->tss[i]; t[1] = i ? 1 : 4; t[2] = 2; t[3] = 1; t[4] = i ? 1 : 2; t[5] = 2; t[6] = 1; t[11] = i; t[26] = 1; t[27] = 1; t[28] = 1;
  }
  for (int i = 0; i < 16; i++) { u32 *x = s->ss[i]; x[1] = x[2] = x[3] = 1; x[5] = x[6] = 1; x[7] = 0; x[10] = 1; }
  for (int i = 0; i < 512; i++) mat_ident(&s->xf[i]);
  s->vp[0] = 0; s->vp[1] = 0; s->vp[2] = d->pp[0]; s->vp[3] = d->pp[1]; float z0 = 0, z1 = 1; memcpy(&s->vp[4], &z0, 4); memcpy(&s->vp[5], &z1, 4);
  s->scissor[2] = d->pp[0]; s->scissor[3] = d->pp[1];
}
// state-setting helpers honouring state-block recording
#define DEV_STATE(d) ((d)->recording ? (d)->recst : &(d)->cur)
#define MARK(d, field) do { if ((d)->recording) (d)->recmask->field = 1; } while (0)

static u32 Dev_TestCooperativeLevel(void) { return 0; }
static u32 Dev_GetAvailableTextureMem(void) { return 512u << 20; }
static u32 Dev_EvictManagedResources(void) { return 0; }
static u32 Dev_GetDirect3D(void) { w32(ARG(1), THIS->d3d); return 0; }
static u32 Dev_GetDeviceCaps(void) { fill_caps(ARG(1)); return 0; }
static u32 Dev_GetDisplayMode(void) { u32 p = ARG(2); w32(p, 1920); w32(p + 4, 1080); w32(p + 8, 60); w32(p + 12, F_X8R8G8B8); return 0; }
static u32 Dev_GetCreationParameters(void) { u32 p = ARG(1); w32(p, 0); w32(p + 4, 1); w32(p + 8, g_hwnd_main); w32(p + 12, 0x40); return 0; }
static u32 Dev_SetCursorProperties(void) { return 0; }
static u32 Dev_SetCursorPosition(void) { return 0; }
static u32 Dev_ShowCursor(void) { return 0; }
static u32 Dev_GetNumberOfSwapChains(void) { return 1; }
static u32 Dev_Reset(void) { rq_sync(); gw_log("Device::Reset"); Obj *d = THIS; dev_defaults(d); return 0; }
static u32 Dev_GetBackBuffer(void) { Obj *d = THIS; d->bb->ref++; w32(ARG(4), d->bb->g); return 0; }
static u32 Dev_GetRasterStatus(void) { w32(ARG(2), 0); w32(ARG(2) + 4, 0); return 0; }
static u32 Dev_SetDialogBoxMode(void) { return 0; }
static u32 Dev_SetGammaRamp(void) { return 0; }
static u32 Dev_GetGammaRamp(void) { u32 p = ARG(2); for (int c = 0; c < 3; c++) for (int i = 0; i < 256; i++) w16(p + c * 512 + 2 * i, i * 257); return 0; }
static Obj *new_texture(int kind, int w, int h, int levels, u32 usage, u32 fmt, u32 pool) {
  Obj *t = new_obj(kind, kind == K_CUBE ? vt_cube : vt_tex); t->w = w; t->h = h; t->usage = usage; t->fmt = fmt; t->pool = pool;
  if (!levels) { levels = 1; int x = w, y = h; while (x > 1 || y > 1) { x = x > 1 ? x / 2 : 1; y = y > 1 ? y / 2 : 1; levels++; } }
  if (usage & 0x400) levels = 1;   // AUTOGENMIPMAP: expose one level
  t->levels = levels; int faces = kind == K_CUBE ? 6 : 1; g_sc.texnew++; g_sc.texbytes += (uint64_t)w * h * 4;
  for (int f = 0; f < faces; f++) { int x = w, y = h; for (int l = 0; l < levels; l++) {
    Obj *s = new_surface(x, y, fmt, t, l); s->face = f; s->usage = usage; t->lv[f * 16 + l] = s; x = x > 1 ? x / 2 : 1; y = y > 1 ? y / 2 : 1; } }
  return t;
}
static u32 Dev_CreateTexture(void) {
  u32 w = ARG(1), h = ARG(2), lv = ARG(3), usage = ARG(4), fmt = ARG(5), pool = ARG(6);
  if (!fmt_ok(fmt) || !w || !h) { gw_log("CreateTexture %ux%u fmt %u unsupported", w, h, fmt); return D3DERR_INVALIDCALL; }
  Obj *t = new_texture(K_TEX, w, h, lv, usage, fmt, pool); w32(ARG(7), t->g); return 0;
}
static u32 Dev_CreateCubeTexture(void) {
  u32 e = ARG(1), lv = ARG(2), usage = ARG(3), fmt = ARG(4), pool = ARG(5);
  if (!fmt_ok(fmt)) return D3DERR_INVALIDCALL;
  Obj *t = new_texture(K_CUBE, e, e, lv, usage, fmt, pool); w32(ARG(6), t->g); return 0;
}
static u32 Dev_CreateVolumeTexture(void) { gw_log("CreateVolumeTexture unsupported"); return D3DERR_NOTAVAILABLE; }
static u32 Dev_CreateVertexBuffer(void) {
  u32 len = ARG(1); Obj *b = new_obj(K_VB, vt_vb); b->bsize = len; b->usage = ARG(2); b->fmt = ARG(3); b->pool = ARG(4);
  b->mem = len > 4000 ? vm_alloc(len, 0x1000) : heap_alloc(len, 1); w32(ARG(5), b->g); return 0;
}
static u32 Dev_CreateIndexBuffer(void) {
  u32 len = ARG(1); Obj *b = new_obj(K_IB, vt_ib); b->bsize = len; b->usage = ARG(2); b->fmt = ARG(3); b->pool = ARG(4);
  b->mem = len > 4000 ? vm_alloc(len, 0x1000) : heap_alloc(len, 1); w32(ARG(5), b->g); return 0;
}
static u32 Dev_CreateRenderTarget(void) { Obj *s = new_surface(ARG(1), ARG(2), F_A8R8G8B8, NULL, 0); s->usage = 1; w32(ARG(7), s->g); return 0; }
static u32 Dev_CreateDepthStencilSurface(void) {
  Obj *s = new_surface(ARG(1), ARG(2), ARG(3), NULL, 0); s->usage = 2; s->z = malloc((size_t)s->w * s->h * 4);
  for (int i = 0; i < s->w * s->h; i++) s->z[i] = 1.0f; w32(ARG(7), s->g); return 0;
}
static void copy_rect(Obj *src, int sx, int sy, Obj *dst, int dx, int dy, int w, int h) {
  if (src->fmt == dst->fmt && !is_dxt(src->fmt)) {
    int bpp = fmt_bpp(src->fmt) / 8;
    for (int y = 0; y < h; y++) { if (sy + y >= src->h || dy + y >= dst->h) break;
      int n = w; if (sx + n > src->w) n = src->w - sx; if (dx + n > dst->w) n = dst->w - dx; if (n <= 0) break;
      memcpy(P(dst->mem + (dy + y) * dst->pitch + dx * bpp), P(src->mem + (sy + y) * src->pitch + sx * bpp), n * bpp); }
    dst->dirty = 1; dst->mcpu = 1; return;
  }
  if (is_dxt(src->fmt) && src->fmt == dst->fmt && !sx && !sy && !dx && !dy) { memcpy(P(dst->mem), P(src->mem), src->size < dst->size ? src->size : dst->size); dst->dirty = 1; dst->mcpu = 1; return; }
  if (dst->fmt == F_A8R8G8B8 || dst->fmt == F_X8R8G8B8) {
    u32 *s = surf_argb(src);
    for (int y = 0; y < h && sy + y < src->h && dy + y < dst->h; y++) for (int x = 0; x < w && sx + x < src->w && dx + x < dst->w; x++)
      w32(dst->mem + (dy + y) * dst->pitch + 4 * (dx + x), s[(sy + y) * src->w + sx + x]);
    dst->dirty = 1; dst->mcpu = 1; return;
  }
  gw_log("copy_rect: unsupported %u -> %u", src->fmt, dst->fmt);
}
static u32 Dev_UpdateSurface(void) { rq_sync();
  Obj *s = O(ARG(1)), *d = O(ARG(3)); u32 r = ARG(2), pt = ARG(4);
  int sx = 0, sy = 0, w = s->w, h = s->h; if (r) { sx = r32(r); sy = r32(r + 4); w = r32(r + 8) - sx; h = r32(r + 12) - sy; }
  int dx = pt ? (int)r32(pt) : 0, dy = pt ? (int)r32(pt + 4) : 0; copy_rect(s, sx, sy, d, dx, dy, w, h); return 0;
}
static u32 Dev_UpdateTexture(void) { rq_sync();
  Obj *s = O(ARG(1)), *d = O(ARG(2)); int faces = s->kind == K_CUBE ? 6 : 1;
  for (int f = 0; f < faces; f++) for (int l = 0; l < s->levels && l < d->levels; l++) copy_rect(s->lv[f * 16 + l], 0, 0, d->lv[f * 16 + l], 0, 0, s->lv[f * 16 + l]->w, s->lv[f * 16 + l]->h);
  return 0;
}
static u32 Dev_GetRenderTargetData(void) { rq_sync(); Obj *s = O(ARG(1)), *d = O(ARG(2)); copy_rect(s, 0, 0, d, 0, 0, s->w, s->h); return 0; }
static u32 Dev_GetFrontBufferData(void) { rq_sync(); Obj *d = THIS, *s = O(ARG(2)); copy_rect(d->bb, 0, 0, s, 0, 0, s->w, s->h); return 0; }
static u32 Dev_StretchRect(void) { rq_sync();
  Obj *s = O(ARG(1)), *d = O(ARG(3)); u32 sr = ARG(2), dr = ARG(4);
  int sx0 = 0, sy0 = 0, sx1 = s->w, sy1 = s->h, dx0 = 0, dy0 = 0, dx1 = d->w, dy1 = d->h;
  if (sr) { sx0 = r32(sr); sy0 = r32(sr + 4); sx1 = r32(sr + 8); sy1 = r32(sr + 12); }
  if (dr) { dx0 = r32(dr); dy0 = r32(dr + 4); dx1 = r32(dr + 8); dy1 = r32(dr + 12); }
  u32 *src = surf_argb(s);
  for (int y = dy0; y < dy1 && y < d->h; y++) for (int x = dx0; x < dx1 && x < d->w; x++) {
    int u = sx0 + (x - dx0) * (sx1 - sx0) / (dx1 - dx0), v = sy0 + (y - dy0) * (sy1 - sy0) / (dy1 - dy0);
    if (u >= 0 && v >= 0 && u < s->w && v < s->h) w32(d->mem + y * d->pitch + 4 * x, src[v * s->w + u]); }
  d->dirty = 1; d->mcpu = 1; return 0;
}
static u32 Dev_ColorFill(void) { rq_sync();
  Obj *s = O(ARG(1)); u32 r = ARG(2), c = ARG(3); int x0 = 0, y0 = 0, x1 = s->w, y1 = s->h;
  if (r) { x0 = r32(r); y0 = r32(r + 4); x1 = r32(r + 8); y1 = r32(r + 12); }
  for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) w32(s->mem + y * s->pitch + 4 * x, c); s->dirty = 1; s->mcpu = 1; return 0;
}
static u32 Dev_CreateOffscreenPlainSurface(void) { Obj *s = new_surface(ARG(1), ARG(2), ARG(3), NULL, 0); w32(ARG(6), s->g); return 0; }
static u32 Dev_SetRenderTarget(void) { Obj *d = THIS; if (ARG(1) == 0) { d->rt = ARG(2) ? O(ARG(2)) : d->bb; State *s = &d->cur; s->vp[0] = s->vp[1] = 0; s->vp[2] = d->rt->w; s->vp[3] = d->rt->h; } return 0; }
static u32 Dev_GetRenderTarget(void) { Obj *d = THIS; if (ARG(1)) { w32(ARG(2), 0); return D3DERR_NOTFOUND; } d->rt->ref++; w32(ARG(2), d->rt->g); return 0; }
static u32 Dev_SetDepthStencilSurface(void) { THIS->ds = ON(ARG(1)); return 0; }
static u32 Dev_GetDepthStencilSurface(void) { Obj *d = THIS; if (!d->ds) { w32(ARG(1), 0); return D3DERR_NOTFOUND; } d->ds->ref++; w32(ARG(1), d->ds->g); return 0; }
static u32 Dev_BeginScene(void) { return 0; }
static u32 Dev_EndScene(void) { return 0; }
static u32 Dev_SetTransform(void) { Obj *d = THIS; u32 t = ARG(1); if (t >= 512) return D3DERR_INVALIDCALL; memcpy(&DEV_STATE(d)->xf[t], P(ARG(2)), 64); MARK(d, xf[t]); return 0; }
static u32 Dev_GetTransform(void) { u32 t = ARG(1); if (t >= 512) return D3DERR_INVALIDCALL; memcpy(P(ARG(2)), &THIS->cur.xf[t], 64); return 0; }
static void matmul(const Mat *a, const Mat *b, Mat *o) { Mat r; for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { float s = 0; for (int k = 0; k < 4; k++) s += a->m[i * 4 + k] * b->m[k * 4 + j]; r.m[i * 4 + j] = s; } *o = r; }
static u32 Dev_MultiplyTransform(void) { Obj *d = THIS; u32 t = ARG(1); Mat m; memcpy(&m, P(ARG(2)), 64); matmul(&m, &d->cur.xf[t], &d->cur.xf[t]); return 0; }
static u32 Dev_SetViewport(void) { Obj *d = THIS; memcpy(DEV_STATE(d)->vp, P(ARG(1)), 24); MARK(d, vp); return 0; }
static u32 Dev_GetViewport(void) { memcpy(P(ARG(1)), THIS->cur.vp, 24); return 0; }
static u32 Dev_SetMaterial(void) { Obj *d = THIS; memcpy(DEV_STATE(d)->mat, P(ARG(1)), 68); MARK(d, mat); return 0; }
static u32 Dev_GetMaterial(void) { memcpy(P(ARG(1)), THIS->cur.mat, 68); return 0; }
static u32 Dev_SetLight(void) { Obj *d = THIS; u32 i = ARG(1); if (i >= 16) return 0; memcpy(DEV_STATE(d)->light[i], P(ARG(2)), 104); MARK(d, light[i]); return 0; }
static u32 Dev_GetLight(void) { u32 i = ARG(1); if (i >= 16) return D3DERR_INVALIDCALL; memcpy(P(ARG(2)), THIS->cur.light[i], 104); return 0; }
static u32 Dev_LightEnable(void) { Obj *d = THIS; u32 i = ARG(1); if (i >= 16) return 0; DEV_STATE(d)->lighton[i] = ARG(2); MARK(d, lighton[i]); return 0; }
static u32 Dev_GetLightEnable(void) { u32 i = ARG(1); w32(ARG(2), i < 16 ? THIS->cur.lighton[i] : 0); return 0; }
static u32 Dev_SetClipPlane(void) { return 0; }
static u32 Dev_SetRenderState(void) { Obj *d = THIS; u32 i = ARG(1); if (i < 256) { DEV_STATE(d)->rs[i] = ARG(2); MARK(d, rs[i]); } return 0; }
static u32 Dev_GetRenderState(void) { u32 i = ARG(1); w32(ARG(2), i < 256 ? THIS->cur.rs[i] : 0); return 0; }
static u32 Dev_SetTexture(void) { Obj *d = THIS; u32 i = ARG(1); if (i >= 257) i -= 241; if (i < 16) { DEV_STATE(d)->tex[i] = ON(ARG(2)); MARK(d, tex[i]); } return 0; }
static u32 Dev_GetTexture(void) { u32 i = ARG(1); Obj *t = i < 16 ? THIS->cur.tex[i] : NULL; if (t) t->ref++; w32(ARG(2), t ? t->g : 0); return 0; }
static u32 Dev_SetTextureStageState(void) { Obj *d = THIS; u32 s = ARG(1), t = ARG(2); if (s < 8 && t < 33) { DEV_STATE(d)->tss[s][t] = ARG(3); MARK(d, tss[s][t]); } return 0; }
static u32 Dev_GetTextureStageState(void) { u32 s = ARG(1), t = ARG(2); w32(ARG(3), s < 8 && t < 33 ? THIS->cur.tss[s][t] : 0); return 0; }
static u32 Dev_SetSamplerState(void) { Obj *d = THIS; u32 s = ARG(1), t = ARG(2); if (s >= 257) s -= 241; if (s < 16 && t < 14) { DEV_STATE(d)->ss[s][t] = ARG(3); MARK(d, ss[s][t]); } return 0; }
static u32 Dev_GetSamplerState(void) { u32 s = ARG(1), t = ARG(2); if (s >= 257) s -= 241; w32(ARG(3), s < 16 && t < 14 ? THIS->cur.ss[s][t] : 0); return 0; }
static u32 Dev_ValidateDevice(void) { w32(ARG(1), 1); return 0; }
static u32 Dev_SetScissorRect(void) { Obj *d = THIS; memcpy(DEV_STATE(d)->scissor, P(ARG(1)), 16); MARK(d, scissor); return 0; }
static u32 Dev_GetScissorRect(void) { memcpy(P(ARG(1)), THIS->cur.scissor, 16); return 0; }
static u32 Dev_SetSoftwareVertexProcessing(void) { return 0; }
static u32 Dev_GetSoftwareVertexProcessing(void) { return 0; }
static u32 Dev_SetNPatchMode(void) { return 0; }
static u32 Dev_CreateVertexDeclaration(void) {
  u32 p = ARG(1); Obj *v = new_obj(K_VDECL, vt_vdecl); int n = 0;
  while (n < 63 && r16(p + 8 * n) != 0xFF) n++;
  memcpy(v->decl, P(p), 8 * (n + 1)); v->ndecl = n; w32(ARG(2), v->g); return 0;
}
static u32 Dev_SetVertexDeclaration(void) { Obj *d = THIS; State *s = DEV_STATE(d); s->vdecl = ON(ARG(1)); MARK(d, vdecl); if (s->vdecl) { s->fvf = 0; MARK(d, fvf); } return 0; }
static u32 Dev_GetVertexDeclaration(void) { Obj *v = THIS->cur.vdecl; if (v) v->ref++; w32(ARG(1), v ? v->g : 0); return 0; }
static u32 Dev_SetFVF(void) { Obj *d = THIS; State *s = DEV_STATE(d); s->fvf = ARG(1); MARK(d, fvf); if (ARG(1)) { s->vdecl = NULL; MARK(d, vdecl); } return 0; }
static u32 Dev_GetFVF(void) { w32(ARG(1), THIS->cur.fvf); return 0; }
static u32 shader_len(u32 p) { u32 n = 1; for (;;) { u32 t = r32(p + 4 * n); if (t == 0x0000FFFF) return 4 * (n + 1); n++; if (n > 100000) return 4 * n; } }
static u32 Dev_CreateVertexShader(void) { Obj *v = new_obj(K_VS, vt_vs); u32 n = shader_len(ARG(1)); v->code = heap_alloc(n, 0); memcpy(P(v->code), P(ARG(1)), n); v->bsize = n; v->vsp = vs_compile(v->code, n); w32(ARG(2), v->g); return 0; }
static u32 Dev_SetVertexShader(void) { Obj *d = THIS; DEV_STATE(d)->vs = ON(ARG(1)); MARK(d, vs); return 0; }
static u32 Dev_GetVertexShader(void) { Obj *v = THIS->cur.vs; if (v) v->ref++; w32(ARG(1), v ? v->g : 0); return 0; }
static u32 Dev_SetVertexShaderConstantF(void) { Obj *d = THIS; u32 r = ARG(1), n = ARG(3); for (u32 i = 0; i < n && r + i < 256; i++) { memcpy(DEV_STATE(d)->vsc[r + i], P(ARG(2) + 16 * i), 16); MARK(d, vsc[r + i]); } return 0; }
static u32 Dev_GetVertexShaderConstantF(void) { u32 r = ARG(1), n = ARG(3); for (u32 i = 0; i < n && r + i < 256; i++) memcpy(P(ARG(2) + 16 * i), THIS->cur.vsc[r + i], 16); return 0; }
static u32 Dev_SetConstIB(void) { return 0; }
static u32 Dev_SetStreamSource(void) { Obj *d = THIS; u32 i = ARG(1); if (i < 4) { State *s = DEV_STATE(d); s->vb[i] = ON(ARG(2)); s->vboff[i] = ARG(3); s->vbstride[i] = ARG(4); MARK(d, vb[i]); } return 0; }
static u32 Dev_GetStreamSource(void) { u32 i = ARG(1); State *s = &THIS->cur; Obj *b = i < 4 ? s->vb[i] : NULL; if (b) b->ref++; w32(ARG(2), b ? b->g : 0); w32(ARG(3), i < 4 ? s->vboff[i] : 0); w32(ARG(4), i < 4 ? s->vbstride[i] : 0); return 0; }
static u32 Dev_SetStreamSourceFreq(void) { return 0; }
static u32 Dev_SetIndices(void) { Obj *d = THIS; DEV_STATE(d)->ib = ON(ARG(1)); MARK(d, ib); return 0; }
static u32 Dev_GetIndices(void) { Obj *b = THIS->cur.ib; if (b) b->ref++; w32(ARG(1), b ? b->g : 0); return 0; }
static u32 Dev_CreatePixelShader(void) { Obj *v = new_obj(K_PS, vt_ps); u32 n = shader_len(ARG(1)); v->code = heap_alloc(n, 0); memcpy(P(v->code), P(ARG(1)), n); v->bsize = n; w32(ARG(2), v->g); return 0; }
static u32 Dev_SetPixelShader(void) { Obj *d = THIS; DEV_STATE(d)->ps = ON(ARG(1)); MARK(d, ps); return 0; }
static u32 Dev_GetPixelShader(void) { Obj *v = THIS->cur.ps; if (v) v->ref++; w32(ARG(1), v ? v->g : 0); return 0; }
static u32 Dev_SetPixelShaderConstantF(void) { Obj *d = THIS; u32 r = ARG(1), n = ARG(3); for (u32 i = 0; i < n && r + i < 32; i++) { memcpy(DEV_STATE(d)->psc[r + i], P(ARG(2) + 16 * i), 16); MARK(d, psc[r + i]); } return 0; }
static u32 Dev_CreateQuery(void) { if (!ARG(2)) return 0; Obj *q = new_obj(K_QUERY, vt_query); q->fmt = ARG(1); w32(ARG(2), q->g); return 0; }

// state blocks
static void sb_apply(State *dst, const State *src, const Mask *m) {
#define CP(f) if (m->f) dst->f = src->f
  for (int i = 0; i < 256; i++) CP(rs[i]);
  for (int s = 0; s < 8; s++) for (int t = 0; t < 33; t++) CP(tss[s][t]);
  for (int s = 0; s < 16; s++) for (int t = 0; t < 14; t++) CP(ss[s][t]);
  for (int i = 0; i < 16; i++) { CP(tex[i]); CP(lighton[i]); if (m->light[i]) memcpy(dst->light[i], src->light[i], 104); }
  for (int i = 0; i < 512; i++) CP(xf[i]);
  if (m->vp) memcpy(dst->vp, src->vp, 24); if (m->mat) memcpy(dst->mat, src->mat, 68); if (m->scissor) memcpy(dst->scissor, src->scissor, 16);
  CP(fvf); CP(vdecl); CP(vs); CP(ps); CP(ib);
  for (int i = 0; i < 256; i++) if (m->vsc[i]) memcpy(dst->vsc[i], src->vsc[i], 16);
  for (int i = 0; i < 32; i++) if (m->psc[i]) memcpy(dst->psc[i], src->psc[i], 16);
  for (int i = 0; i < 4; i++) if (m->vb[i]) { dst->vb[i] = src->vb[i]; dst->vboff[i] = src->vboff[i]; dst->vbstride[i] = src->vbstride[i]; }
#undef CP
}
static u32 Dev_CreateStateBlock(void) {
  Obj *d = THIS; Obj *b = new_obj(K_SB, vt_sb); b->st = malloc(sizeof(State)); b->mask = malloc(sizeof(Mask));
  memset(b->mask, 1, sizeof(Mask)); *b->st = d->cur; w32(ARG(2), b->g); return 0;
}
static u32 Dev_BeginStateBlock(void) {
  Obj *d = THIS; d->recording = 1; d->recst = malloc(sizeof(State)); d->recmask = calloc(1, sizeof(Mask)); *d->recst = d->cur; return 0;
}
static u32 Dev_EndStateBlock(void) {
  Obj *d = THIS; Obj *b = new_obj(K_SB, vt_sb); b->st = d->recst; b->mask = d->recmask; d->recording = 0; d->recst = NULL; d->recmask = NULL;
  w32(ARG(1), b->g); return 0;
}
static u32 SB_Capture(void) { Obj *b = THIS; State tmp = *b->st; sb_apply(&tmp, &g_dev->cur, b->mask); *b->st = tmp; return 0; }
static u32 SB_Apply(void) { Obj *b = THIS; sb_apply(&g_dev->cur, b->st, b->mask); return 0; }

// ---------- vertex pipeline ----------
typedef struct { float x, y, z, w; float c[4], sp[4]; float t[8][4]; float fog; } Vtx;   // fog: vertex fog factor (1 = none)
enum { U_POS = 0, U_BW = 1, U_BI = 2, U_NRM = 3, U_PSIZE = 4, U_TEX = 5, U_POST = 9, U_COL = 10 };
typedef struct { int stream, off, type, usage, idx; } El;
static int build_layout(State *s, El *el, int *rhw) {
  int n = 0; *rhw = 0;
  if (s->vdecl) {
    for (int i = 0; i < s->vdecl->ndecl; i++) { u8 *e = s->vdecl->decl + 8 * i; el[n].stream = e[0] | e[1] << 8; el[n].off = e[2] | e[3] << 8;
      el[n].type = e[4]; el[n].usage = e[6]; el[n].idx = e[7]; if (el[n].usage == U_POST) *rhw = 1; n++; }
    return n;
  }
  u32 f = s->fvf; int off = 0; u32 pos = f & 0x400E;
  if (pos == 4) { el[n++] = (El){0, off, 3, U_POST, 0}; off += 16; *rhw = 1; }
  else if (pos) { el[n++] = (El){0, off, 2, U_POS, 0}; off += 12; int nb = pos == 2 ? 0 : (pos - 4) / 2; off += 4 * nb; }
  if (f & 0x10) { el[n++] = (El){0, off, 2, U_NRM, 0}; off += 12; }
  if (f & 0x20) off += 4;
  if (f & 0x40) { el[n++] = (El){0, off, 4, U_COL, 0}; off += 4; }
  if (f & 0x80) { el[n++] = (El){0, off, 4, U_COL, 1}; off += 4; }
  int nt = (f >> 8) & 15;
  for (int i = 0; i < nt; i++) { int code = (f >> (16 + 2 * i)) & 3; int cnt = code == 0 ? 2 : code == 1 ? 3 : code == 2 ? 4 : 1;
    el[n++] = (El){0, off, cnt - 1, U_TEX, i}; off += 4 * cnt; }
  return n;
}
static void read_el(const u8 *p, int type, float o[4]) {
  o[0] = o[1] = o[2] = 0; o[3] = 1;
  switch (type) {
    case 0: case 1: case 2: case 3: for (int i = 0; i <= type; i++) memcpy(&o[i], p + 4 * i, 4); break;
    case 4: { u32 c; memcpy(&c, p, 4); o[0] = ((c >> 16) & 255) / 255.f; o[1] = ((c >> 8) & 255) / 255.f; o[2] = (c & 255) / 255.f; o[3] = (c >> 24) / 255.f; break; }
    case 5: for (int i = 0; i < 4; i++) o[i] = p[i]; break;
    case 6: case 7: for (int i = 0; i < (type == 6 ? 2 : 4); i++) { int16_t v; memcpy(&v, p + 2 * i, 2); o[i] = v; } break;
    case 8: for (int i = 0; i < 4; i++) o[i] = p[i] / 255.f; break;
    case 9: case 10: for (int i = 0; i < (type == 9 ? 2 : 4); i++) { int16_t v; memcpy(&v, p + 2 * i, 2); o[i] = v / 32767.f; } break;
    default: break;
  }
}
static void xform(const float v[4], const Mat *m, float o[4]) { for (int j = 0; j < 4; j++) o[j] = v[0] * m->m[j] + v[1] * m->m[4 + j] + v[2] * m->m[8 + j] + v[3] * m->m[12 + j]; }
static void light_vertex(State *s, const float pos[4], const float nrm[3], int has_col0, const float col0[4], const float col1[4], float out[4]) {
  float matd[4], mata[4], mate[4]; memcpy(matd, s->mat, 16); memcpy(mata, s->mat + 16, 16); memcpy(mate, s->mat + 48, 16);
  int cv = s->rs[141];
  const float *D = (cv && s->rs[145] == 1 && has_col0) ? col0 : (cv && s->rs[145] == 2) ? col1 : matd;
  const float *A = (cv && s->rs[147] == 1 && has_col0) ? col0 : (cv && s->rs[147] == 2) ? col1 : mata;
  const float *E = (cv && s->rs[148] == 1 && has_col0) ? col0 : (cv && s->rs[148] == 2) ? col1 : mate;
  u32 ga = s->rs[139]; float gam[3] = {((ga >> 16) & 255) / 255.f, ((ga >> 8) & 255) / 255.f, (ga & 255) / 255.f};
  float wp[4], wn[4] = {0, 0, 0, 0}, n4[4] = {nrm[0], nrm[1], nrm[2], 0};
  xform(pos, &s->xf[256], wp); xform(n4, &s->xf[256], wn);
  float nl = sqrtf(wn[0] * wn[0] + wn[1] * wn[1] + wn[2] * wn[2]); if (nl > 0) for (int i = 0; i < 3; i++) wn[i] /= nl;
  float r[3]; for (int i = 0; i < 3; i++) r[i] = E[i] + A[i] * gam[i];
  for (int L = 0; L < 8; L++) if (s->lighton[L]) {
    const u8 *l = s->light[L]; u32 type; memcpy(&type, l, 4); float ld[4], la[4], lp[3], dir[3]; memcpy(ld, l + 4, 16); memcpy(la, l + 36, 16); memcpy(lp, l + 52, 12); memcpy(dir, l + 64, 12);
    float Ldir[3], att = 1;
    if (type == 3) { for (int i = 0; i < 3; i++) Ldir[i] = -dir[i]; }
    else { float d2 = 0; for (int i = 0; i < 3; i++) { Ldir[i] = lp[i] - wp[i]; d2 += Ldir[i] * Ldir[i]; } float d = sqrtf(d2); if (d > 0) for (int i = 0; i < 3; i++) Ldir[i] /= d;
      float a0, a1, a2, range; memcpy(&range, l + 76, 4); memcpy(&a0, l + 84, 4); memcpy(&a1, l + 88, 4); memcpy(&a2, l + 92, 4);
      if (range > 0 && d > range) continue; float den = a0 + a1 * d + a2 * d2; att = den > 0 ? 1 / den : 1; }
    float ll = sqrtf(Ldir[0] * Ldir[0] + Ldir[1] * Ldir[1] + Ldir[2] * Ldir[2]); if (ll > 0) for (int i = 0; i < 3; i++) Ldir[i] /= ll;
    float nd = wn[0] * Ldir[0] + wn[1] * Ldir[1] + wn[2] * Ldir[2]; if (nd < 0) nd = 0;
    for (int i = 0; i < 3; i++) r[i] += att * (A[i] * la[i] + D[i] * ld[i] * nd);
  }
  for (int i = 0; i < 3; i++) out[i] = r[i] < 0 ? 0 : r[i] > 1 ? 1 : r[i];
  out[3] = D[3] < 0 ? 0 : D[3] > 1 ? 1 : D[3];
}
// D3D9 fog factor for distance d (1 = no fog, 0 = full fog colour). mode: 1 EXP, 2 EXP2, 3 LINEAR.
static float fog_factor(const u32 *rs, u32 mode, float d) {
  float st, en, de; memcpy(&st, &rs[36], 4); memcpy(&en, &rs[37], 4); memcpy(&de, &rs[38], 4); float f;
  if (mode == 3) f = en != st ? (en - d) / (en - st) : 1.f;
  else if (mode == 1) f = expf(-de * d);
  else if (mode == 2) { float t = de * d; f = expf(-t * t); }
  else f = 1.f;
  return f < 0 ? 0 : f > 1 ? 1 : f;
}
extern float vs_fog;
static int warned_vs;
static int fetch_vertices(State *s, int base, int first, int count, const u8 *up, int upstride, Vtx *out) {
  El el[64]; int rhw; int n = build_layout(s, el, &rhw);
  if (s->vs) {   // programmable vertex stage: interpret the shader bytecode (host/vs.c)
    void *vp = s->vs->vsp; int ereg[64]; for (int k = 0; k < n; k++) ereg[k] = vs_input_reg(vp, el[k].usage, el[k].idx);
    vs_begin(vp, s->vsc);
    for (int i = 0; i < count; i++) {
      Vtx *v = &out[i]; float in[16][4]; for (int r = 0; r < 16; r++) { in[r][0] = in[r][1] = in[r][2] = 0; in[r][3] = 1; }
      for (int k = 0; k < n; k++) {
        if (ereg[k] < 0 || ereg[k] >= 16) continue;
        const u8 *p;
        if (up) p = up + (size_t)(first + i) * upstride + el[k].off;
        else { int st = el[k].stream; Obj *vb = s->vb[st]; if (!vb) continue; u32 off = s->vboff[st] + (u32)(base + first + i) * s->vbstride[st] + el[k].off;
          if (off + 16 > vb->bsize + 16) continue; p = P(vb->mem + off); }
        read_el(p, el[k].type, in[ereg[k]]);
      }
      float pos[4]; vs_run(vp, in, pos, v->c, v->sp, v->t);
      v->x = pos[0]; v->y = pos[1]; v->z = pos[2]; v->w = pos[3]; v->fog = vs_fog < 0 ? 0 : vs_fog > 1 ? 1 : vs_fog;
      for (int k = 0; k < 4; k++) { v->c[k] = v->c[k] < 0 ? 0 : v->c[k] > 1 ? 1 : v->c[k]; v->sp[k] = v->sp[k] < 0 ? 0 : v->sp[k] > 1 ? 1 : v->sp[k]; }
    }
    return 0;
  }
  Mat wv, wvp; matmul(&s->xf[256], &s->xf[2], &wv); matmul(&wv, &s->xf[3], &wvp);
  int lighting = s->rs[137] && !rhw;
  for (int i = 0; i < count; i++) {
    Vtx *v = &out[i]; memset(v, 0, sizeof *v); float pos[4] = {0, 0, 0, 1}, nrm[3] = {0, 0, 0}, c0[4] = {1, 1, 1, 1}, c1[4] = {0, 0, 0, 0}; int hc0 = 0;
    for (int t = 0; t < 8; t++) v->t[t][3] = 1;
    for (int k = 0; k < n; k++) {
      const u8 *p;
      if (up) p = up + (size_t)(first + i) * upstride + el[k].off;
      else { int st = el[k].stream; Obj *vb = s->vb[st]; if (!vb) continue; u32 off = s->vboff[st] + (u32)(base + first + i) * s->vbstride[st] + el[k].off;
        if (off + 16 > vb->bsize + 16) continue; p = P(vb->mem + off); }
      float f[4]; read_el(p, el[k].type, f);
      switch (el[k].usage) {
        case U_POS: if (el[k].idx == 0) { pos[0] = f[0]; pos[1] = f[1]; pos[2] = f[2]; } break;
        case U_POST: memcpy(pos, f, 16); break;
        case U_NRM: memcpy(nrm, f, 12); break;
        case U_COL: if (el[k].idx == 0) { memcpy(c0, f, 16); hc0 = 1; } else memcpy(c1, f, 16); break;
        case U_TEX: if (el[k].idx < 8) memcpy(v->t[el[k].idx], f, 16); break;
      }
    }
    if (rhw) { v->x = pos[0]; v->y = pos[1]; v->z = pos[2]; v->w = pos[3] != 0 ? 1.0f / pos[3] : 1.0f; }
    else { float o[4]; xform(pos, &wvp, o); v->x = o[0]; v->y = o[1]; v->z = o[2]; v->w = o[3]; }
    if (lighting) light_vertex(s, pos, nrm, hc0, c0, c1, v->c); else memcpy(v->c, c0, 16);
    memcpy(v->sp, c1, 16);
    // vertex fog (table fog is per pixel in the rasteriser): computed in eye space from FOGVERTEXMODE, else the
    // specular alpha carries the fog factor (pre-transformed vertices, or both fog modes NONE)
    v->fog = 1.f;
    if (s->rs[28] && !s->rs[35]) {
      if (!rhw && s->rs[140]) { float e[4]; xform(pos, &wv, e); float d = s->rs[48] ? sqrtf(e[0] * e[0] + e[1] * e[1] + e[2] * e[2]) : fabsf(e[2]); v->fog = fog_factor(s->rs, s->rs[140], d); }
      else v->fog = c1[3] < 0 ? 0 : c1[3] > 1 ? 1 : c1[3];
    }
  }
  return rhw;
}

// ---------- rasterizer ----------
typedef struct { float x, y, z, iw; float c[4], sp[4]; float t[8][4]; float fog; } SV;  // screen vertex; attributes pre-multiplied by iw
// compact render state snapshot read by the rasteriser (per queued job; the worker's current job via the TLS RS)
typedef struct RState { u32 rs[256]; u32 tss[8][33]; u32 ss[8][14]; struct Obj *tex[8]; u32 vp[6]; int fogw; } RState;   // fogw: table fog uses w
static __thread RState *RS; static __thread Obj *RT, *DS;
static inline float clampf(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
static void tex_fetch(Obj *t, int stage, float u, float v, float o[4]) {
  if (!t) { o[0] = o[1] = o[2] = o[3] = 1; return; }
  Obj *s = t->kind == K_SURF ? t : t->lv[0]; if (!s) { o[0] = o[1] = o[2] = o[3] = 1; return; }
  u32 *px = surf_argb(s); int W = s->w, H = s->h; u32 *ss = RS->ss[stage];
  int lin = ss[5] >= 2 || ss[6] >= 2;
  float fu = u * W, fv = v * H;
  if (lin) { fu -= 0.5f; fv -= 0.5f; }
  int x0 = (int)floorf(fu), y0 = (int)floorf(fv); float ax = fu - x0, ay = fv - y0;
#define ADDR(c, n, mode) ((mode) == 3 ? ((c) < 0 ? 0 : (c) >= (n) ? (n) - 1 : (c)) : (mode) == 2 ? ((((c) % (2 * (n))) + 2 * (n)) % (2 * (n)) >= (n) ? 2 * (n) - 1 - ((((c) % (2 * (n))) + 2 * (n)) % (2 * (n))) : ((((c) % (2 * (n))) + 2 * (n)) % (2 * (n)))) : ((((c) % (n)) + (n)) % (n)))
  if (!lin) { int x = ADDR(x0, W, ss[1]), y = ADDR(y0, H, ss[2]); u32 c = px[y * W + x];
    o[0] = ((c >> 16) & 255) / 255.f; o[1] = ((c >> 8) & 255) / 255.f; o[2] = (c & 255) / 255.f; o[3] = (c >> 24) / 255.f; return; }
  int xa = ADDR(x0, W, ss[1]), xb = ADDR(x0 + 1, W, ss[1]), ya = ADDR(y0, H, ss[2]), yb = ADDR(y0 + 1, H, ss[2]);
  u32 c00 = px[ya * W + xa], c10 = px[ya * W + xb], c01 = px[yb * W + xa], c11 = px[yb * W + xb];
  for (int k = 0; k < 4; k++) { int sh = k == 3 ? 24 : 16 - 8 * k;
    float a = ((c00 >> sh) & 255) * (1 - ax) + ((c10 >> sh) & 255) * ax, b = ((c01 >> sh) & 255) * (1 - ax) + ((c11 >> sh) & 255) * ax;
    o[k] = (a * (1 - ay) + b * ay) / 255.f; }
}
static void arg_val(u32 a, const float *diff, const float *spec, const float *cur, const float *tex, const float *tf, const float *temp, float o[4]) {
  const float *src;
  switch (a & 0xF) { case 0: src = diff; break; case 1: src = cur; break; case 2: src = tex; break; case 3: src = tf; break; case 4: src = spec; break; case 5: src = temp; break; default: src = cur; }
  for (int i = 0; i < 4; i++) o[i] = src[i];
  if (a & 0x20) o[0] = o[1] = o[2] = o[3];
  if (a & 0x10) for (int i = 0; i < 4; i++) o[i] = 1 - o[i];
}
static float op1(u32 op, float a1, float a2, float a0, const float *diff, const float *tex, const float *tf, const float *cur) {
  switch (op) {
    case 2: return a1; case 3: return a2; case 4: return a1 * a2; case 5: return clampf(a1 * a2 * 2); case 6: return clampf(a1 * a2 * 4);
    case 7: return clampf(a1 + a2); case 8: return clampf(a1 + a2 - 0.5f); case 9: return clampf((a1 + a2 - 0.5f) * 2); case 10: return clampf(a1 - a2);
    case 11: return clampf(a1 + a2 - a1 * a2); case 12: return a1 * diff[3] + a2 * (1 - diff[3]); case 13: return a1 * tex[3] + a2 * (1 - tex[3]);
    case 14: return a1 * tf[3] + a2 * (1 - tf[3]); case 15: return clampf(a1 + a2 * (1 - tex[3])); case 16: return a1 * cur[3] + a2 * (1 - cur[3]);
    case 25: return clampf(a0 + a1 * a2); case 26: return a0 * a1 + (1 - a0) * a2;
    default: return a1 * a2;
  }
}
static int cmpf(u32 fn, float a, float b) { switch (fn) { case 1: return 0; case 2: return a < b; case 3: return a == b; case 4: return a <= b; case 5: return a > b; case 6: return a != b; case 7: return a >= b; default: return 1; } }
static void blendf(u32 f, const float *s, const float *d, float o[4]) {
  switch (f) { case 1: o[0] = o[1] = o[2] = o[3] = 0; break; case 2: o[0] = o[1] = o[2] = o[3] = 1; break;
    case 3: for (int i = 0; i < 4; i++) o[i] = s[i]; break; case 4: for (int i = 0; i < 4; i++) o[i] = 1 - s[i]; break;
    case 5: o[0] = o[1] = o[2] = o[3] = s[3]; break; case 6: o[0] = o[1] = o[2] = o[3] = 1 - s[3]; break;
    case 7: o[0] = o[1] = o[2] = o[3] = d[3]; break; case 8: o[0] = o[1] = o[2] = o[3] = 1 - d[3]; break;
    case 9: for (int i = 0; i < 4; i++) o[i] = d[i]; break; case 10: for (int i = 0; i < 4; i++) o[i] = 1 - d[i]; break;
    case 11: { float m = s[3] < 1 - d[3] ? s[3] : 1 - d[3]; o[0] = o[1] = o[2] = m; o[3] = 1; break; }
    case 12: o[0] = o[1] = o[2] = o[3] = s[3]; break;           // BOTHSRCALPHA (source side)
    case 13: o[0] = o[1] = o[2] = o[3] = 1 - s[3]; break;
    case 14: case 15: { u32 bf = RS->rs[193]; float F[4] = {((bf >> 16) & 255) / 255.f, ((bf >> 8) & 255) / 255.f, (bf & 255) / 255.f, (bf >> 24) / 255.f};
      for (int i = 0; i < 4; i++) o[i] = f == 14 ? F[i] : 1 - F[i]; break; }   // BLENDFACTOR / INVBLENDFACTOR (D3DRS_BLENDFACTOR = 193)
    default: o[0] = o[1] = o[2] = o[3] = 1; }
}
static int shade_pixel(int x, int y, float z, const float *diff, const float *spec, float tc[8][4], float fogf) {
  RState *s = RS;
  u32 tfc = s->rs[60]; float tf[4] = {((tfc >> 16) & 255) / 255.f, ((tfc >> 8) & 255) / 255.f, (tfc & 255) / 255.f, (tfc >> 24) / 255.f};
  float cur[4] = {diff[0], diff[1], diff[2], diff[3]}, temp[4] = {0, 0, 0, 0};
  for (int st = 0; st < 8; st++) {
    u32 *t = s->tss[st]; if (t[1] == 1) break;
    float tex[4] = {1, 1, 1, 1}; int ci = t[11] & 7;
    if (s->tex[st]) tex_fetch(s->tex[st], st, tc[ci][0], tc[ci][1], tex);
    float a1[4], a2[4], a0[4], b1[4], b2[4], b0[4], res[4];
    arg_val(t[2], diff, spec, cur, tex, tf, temp, a1); arg_val(t[3], diff, spec, cur, tex, tf, temp, a2); arg_val(t[26], diff, spec, cur, tex, tf, temp, a0);
    for (int i = 0; i < 3; i++) res[i] = op1(t[1], a1[i], a2[i], a0[i], diff, tex, tf, cur);
    if (t[1] == 24) { float d = 4 * ((a1[0] - .5f) * (a2[0] - .5f) + (a1[1] - .5f) * (a2[1] - .5f) + (a1[2] - .5f) * (a2[2] - .5f)); res[0] = res[1] = res[2] = clampf(d); }
    u32 aop = t[4] == 1 ? 2 : t[4];
    if (t[4] == 1) { res[3] = cur[3]; }
    else { arg_val(t[5], diff, spec, cur, tex, tf, temp, b1); arg_val(t[6], diff, spec, cur, tex, tf, temp, b2); arg_val(t[27], diff, spec, cur, tex, tf, temp, b0);
      res[3] = op1(aop, b1[3], b2[3], b0[3], diff, tex, tf, cur); }
    if (t[28] == 5) memcpy(temp, res, 16); else memcpy(cur, res, 16);
  }
  if (s->rs[29] && spec) for (int i = 0; i < 3; i++) cur[i] = clampf(cur[i] + spec[i]);
  if (fogf < 1.f) { u32 fc = s->rs[34]; float fcol[3] = {((fc >> 16) & 255) / 255.f, ((fc >> 8) & 255) / 255.f, (fc & 255) / 255.f};
    for (int i = 0; i < 3; i++) cur[i] = fogf * cur[i] + (1.f - fogf) * fcol[i]; }
  if (s->rs[15]) { float ref = (s->rs[24] & 255) / 255.f; if (!cmpf(s->rs[25], (float)(int)(cur[3] * 255 + .5f) / 255.f, ref)) return 0; }
  if (DS && DS->z && s->rs[7]) { float *zp = &DS->z[y * DS->w + x]; if (!cmpf(s->rs[23], z, *zp)) return 0; if (s->rs[14]) *zp = z; }
  u32 *dp = (u32 *)P(RT->mem + y * RT->pitch + 4 * x); u32 dc = *dp;
  float out[4];
  if (s->rs[27]) {
    float d[4] = {((dc >> 16) & 255) / 255.f, ((dc >> 8) & 255) / 255.f, (dc & 255) / 255.f, (dc >> 24) / 255.f}, sf[4], df[4];
    blendf(s->rs[19], cur, d, sf); blendf(s->rs[20], cur, d, df);
    u32 bop = s->rs[171];
    for (int i = 0; i < 4; i++) { float a = cur[i] * sf[i], b = d[i] * df[i];
      out[i] = bop == 2 ? a - b : bop == 3 ? b - a : bop == 4 ? (cur[i] < d[i] ? cur[i] : d[i]) : bop == 5 ? (cur[i] > d[i] ? cur[i] : d[i]) : a + b; out[i] = clampf(out[i]); }
    if (s->rs[206]) { float sa[4], da[4]; blendf(s->rs[207], cur, d, sa); blendf(s->rs[208], cur, d, da); out[3] = clampf(cur[3] * sa[3] + d[3] * da[3]); }
  } else memcpy(out, cur, 16);
  u32 wm = s->rs[168]; u32 nc = dc;
  u32 R = (u32)(out[0] * 255 + .5f), G = (u32)(out[1] * 255 + .5f), B = (u32)(out[2] * 255 + .5f), A = (u32)(out[3] * 255 + .5f);
  if (wm & 1) nc = (nc & ~0x00FF0000u) | R << 16; if (wm & 2) nc = (nc & ~0x0000FF00u) | G << 8; if (wm & 4) nc = (nc & ~0xFFu) | B; if (wm & 8) nc = (nc & 0x00FFFFFFu) | A << 24;
  *dp = nc; return 1;
}
static __thread int clip_x0, clip_y0, clip_x1, clip_y1;
// ---- per-draw pipeline: integer fast path ----
// Up to 4 fixed-function stages: ops SELECTARG1/2, MODULATE(2X/4X), ADD, ADDSIGNED(2X), SUBTRACT, ADDSMOOTH,
// BLEND{DIFFUSE,TEXTURE,FACTOR,CURRENT}ALPHA; args DIFFUSE/CURRENT/TEXTURE/TFACTOR with COMPLEMENT/ALPHAREPLICATE;
// any texcoord index; wrap/mirror/clamp; point/bilinear; alpha test; z test/write; every blend factor 1..15
// with ADD/SUBTRACT/REVSUBTRACT/MIN/MAX. Specular add, TEMP results, separate alpha blend, bump/dot3 ops,
// texcoord generation and non-ARGB render targets stay on the float interpreter (shade_pixel).
// GW_SLOWRAST=1 forces the float path everywhere.
typedef struct { const u32 *px; int w, h, lin; u8 mu, mv; } Samp;      // address mode 1 wrap, 2 mirror, 3 clamp
typedef struct { u8 cop, aop, c1, c2, a1, a2, tci, hastex, same; Samp sm; } FStage;
typedef struct {
  int fast, nst; FStage st[4]; u32 tf;
  int atest; u32 afunc, aref;
  int ztest, zwrite; u32 zfunc; float *zbuf; int zw;
  int blend; u32 sbf, dbf, bop, bfac, wmask;
  u8 *rt; int rtpitch;
  int ntc, usespec, nuv; u8 uvslot[8];
} Pipe;
static __thread Pipe PP;
static int g_slowrast = -1;
static int fop_ok(u32 op) { return (op >= 2 && op <= 14) || op == 16; }
static int farg_ok(u32 a) { return (a & ~0x3Fu) == 0 && (a & 0xF) <= 3; }
static int samp_setup(Samp *sm, Obj *to, const u32 *ss) {
  Obj *l = to->kind == K_SURF ? to : to->lv[0]; if (!l) return 0;
  u32 au = ss[1], av = ss[2]; if (au < 1 || au > 3 || av < 1 || av > 3) return 0;
  sm->px = surf_argb(l); sm->w = l->w; sm->h = l->h; sm->lin = ss[5] >= 2 || ss[6] >= 2; sm->mu = au; sm->mv = av; return 1;
}
static void pipe_setup(void) {
  RState *s = RS; Pipe *p = &PP; memset(p, 0, sizeof *p);
  if (g_slowrast < 0) g_slowrast = getenv("GW_SLOWRAST") ? 1 : 0;
  p->rt = P(RT->mem); p->rtpitch = RT->pitch;
  int nt = 0; for (int st = 0; st < 8; st++) { if (s->tss[st][1] == 1) break; nt = st + 1; }
  p->ntc = 1; for (int st = 0; st < nt; st++) { int ci = (s->tss[st][11] & 7) + 1; if (ci > p->ntc) p->ntc = ci; }
  p->usespec = s->rs[29];
  if (!g_metal) for (int st = 0; st < nt; st++) if (s->tex[st]) { Obj *t = s->tex[st]; Obj *l = t->kind == K_SURF ? t : t->lv[0]; if (l) surf_argb(l); }  // decode once per draw, not per texel
  if (g_slowrast || nt > 4 || s->rs[29] || s->rs[28] || (RT->fmt != F_A8R8G8B8 && RT->fmt != F_X8R8G8B8)) return;   // fog: float path
  p->nst = nt; memset(p->uvslot, 0xFF, sizeof p->uvslot);
  for (int st = 0; st < nt; st++) {
    const u32 *t = s->tss[st]; FStage *f = &p->st[st];
    if (t[28] == 5 || (t[11] & ~7u)) return;               // TEMP result, texcoord generation
    u32 cop = t[1], aop = t[4];
    if (!fop_ok(cop) || !(aop == 1 || fop_ok(aop))) return;
    if ((cop != 3 && !farg_ok(t[2])) || (cop != 2 && !farg_ok(t[3]))) return;
    if (aop != 1 && ((aop != 3 && !farg_ok(t[5])) || (aop != 2 && !farg_ok(t[6])))) return;
    f->cop = cop; f->aop = aop; f->c1 = t[2]; f->c2 = t[3]; f->a1 = t[5]; f->a2 = t[6]; f->tci = t[11] & 7;
    f->same = cop == aop && t[2] == t[5] && t[3] == t[6];
    if (s->tex[st]) { if (!samp_setup(&f->sm, s->tex[st], s->ss[st])) return; f->hastex = 1;
      if (p->uvslot[f->tci] == 0xFF) p->uvslot[f->tci] = p->nuv++; }
  }
  p->tf = s->rs[60];
  p->atest = s->rs[15] != 0; p->afunc = s->rs[25]; p->aref = s->rs[24] & 255;
  p->ztest = DS && DS->z && s->rs[7]; p->zwrite = p->ztest && s->rs[14]; p->zfunc = s->rs[23]; p->zbuf = p->ztest ? DS->z : NULL; p->zw = DS ? DS->w : 0;
  if (s->rs[27]) {
    if (s->rs[206]) return;
    p->sbf = s->rs[19]; p->dbf = s->rs[20]; p->bop = s->rs[171] ? s->rs[171] : 1; p->bfac = s->rs[193];
    if (p->sbf < 1 || p->sbf > 15 || p->dbf < 1 || p->dbf > 15 || p->bop > 5) return;
    p->blend = 1;
  }
  p->wmask = s->rs[168] & 15;
  p->fast = 1;
}
static inline int icmp(u32 fn, u32 a, u32 b) { switch (fn) { case 1: return 0; case 2: return a < b; case 3: return a == b; case 4: return a <= b; case 5: return a > b; case 6: return a != b; case 7: return a >= b; default: return 1; } }
static inline u32 lerp32(u32 a, u32 b, u32 f) {   // per-channel a + (b-a)*f/256, f in 0..256
  u32 rb = (((a & 0xFF00FFu) * (256 - f) + (b & 0xFF00FFu) * f) >> 8) & 0xFF00FFu;
  u32 ag = (((a >> 8) & 0xFF00FFu) * (256 - f) + ((b >> 8) & 0xFF00FFu) * f) & 0xFF00FF00u;
  return rb | ag;
}
static inline int addr_wrap(int c, int n, int mode) {
  if (mode == 1) { if ((n & (n - 1)) == 0) return c & (n - 1); c %= n; return c < 0 ? c + n : c; }
  if (mode == 3) return c < 0 ? 0 : c >= n ? n - 1 : c;
  int m = c % (2 * n); if (m < 0) m += 2 * n; return m >= n ? 2 * n - 1 - m : m;
}
static inline __attribute__((always_inline)) u32 samp(const Samp *s, float u, float v) {
  const u32 *px = s->px; int W = s->w, H = s->h;
  if (!s->lin) {
    float fu = u * W, fv = v * H; if (!(fu > -1e8f && fu < 1e8f)) fu = 0; if (!(fv > -1e8f && fv < 1e8f)) fv = 0;
    int x = addr_wrap((int)floorf(fu), W, s->mu), y = addr_wrap((int)floorf(fv), H, s->mv);
    return px[y * W + x];
  }
  float fu = u * W - 0.5f, fv = v * H - 0.5f;
  if (!(fu > -1e8f && fu < 1e8f)) fu = 0; if (!(fv > -1e8f && fv < 1e8f)) fv = 0;
  int iu = (int)floorf(fu * 256.f), iv = (int)floorf(fv * 256.f);
  int x0 = iu >> 8, y0 = iv >> 8; u32 ax = iu & 255, ay = iv & 255;
  int x1 = addr_wrap(x0 + 1, W, s->mu), y1 = addr_wrap(y0 + 1, H, s->mv); x0 = addr_wrap(x0, W, s->mu); y0 = addr_wrap(y0, H, s->mv);
  u32 a = lerp32(px[y0 * W + x0], px[y0 * W + x1], ax), b = lerp32(px[y1 * W + x0], px[y1 * W + x1], ax);
  return lerp32(a, b, ay);
}
static inline u32 mul8(u32 a, u32 b) { u32 t = a * b + 128; return (t + (t >> 8)) >> 8; }   // a*b/255 rounded
static inline u32 c8(float f) { return f <= 0 ? 0 : f >= 1 ? 255 : (u32)(f * 255.f + .5f); }
// per-pixel combiner on 4 x u16 lanes (B, G, R, A = byte order of an A8R8G8B8 u32), NEON via clang ext vectors
typedef uint16_t v4h __attribute__((ext_vector_type(4)));
typedef uint8_t v4b __attribute__((ext_vector_type(4)));
typedef float v4f __attribute__((ext_vector_type(4)));
#define AI static inline __attribute__((always_inline))
#define V255 ((v4h)255)
AI v4h U(u32 x) { v4b b; memcpy(&b, &x, 4); return __builtin_convertvector(b, v4h); }
AI u32 PK(v4h v) { v4b b = __builtin_convertvector(v, v4b); u32 x; memcpy(&x, &b, 4); return x; }
AI v4h vmul(v4h a, v4h b) { v4h t = a * b + 128; return (t + (t >> 8)) >> 8; }
AI v4h vadds(v4h a, v4h b) { return __builtin_elementwise_min(a + b, V255); }
AI v4h vsubs(v4h a, v4h b) { return __builtin_elementwise_max(a, b) - b; }
AI v4h vaddsg(v4h a, v4h b, int x2) { v4h t = __builtin_elementwise_max(a + b, (v4h)128) - 128; if (x2) t = t * 2; return __builtin_elementwise_min(t, V255); }
AI v4h vlerpa(v4h x, v4h y, uint16_t a) { return __builtin_elementwise_min(vmul(x, (v4h)a) + vmul(y, (v4h)(uint16_t)(255 - a)), V255); }   // x*a + y*(1-a)
AI v4h vfarg(u32 a, v4h D, v4h C, v4h T, v4h F) {
  v4h v = (a & 7) == 0 ? D : (a & 7) == 1 ? C : (a & 7) == 2 ? T : F;
  if (a & 0x20) v = v.wwww; if (a & 0x10) v = V255 - v; return v;
}
AI v4h vfop(u32 op, v4h x, v4h y, v4h D, v4h C, v4h T, v4h F) {
  switch (op) {
    case 2: return x; case 3: return y; case 4: return vmul(x, y);
    case 5: { v4h m = vmul(x, y); return vadds(m, m); }
    case 6: { v4h m = vmul(x, y); m = vadds(m, m); return vadds(m, m); }
    case 7: return vadds(x, y); case 8: return vaddsg(x, y, 0); case 9: return vaddsg(x, y, 1); case 10: return vsubs(x, y);
    case 11: return vadds(x, vmul(y, V255 - x));
    case 12: return vlerpa(x, y, D.w); case 13: return vlerpa(x, y, T.w); case 14: return vlerpa(x, y, F.w); case 16: return vlerpa(x, y, C.w);
    default: return vmul(x, y);
  }
}
AI v4h vbfac(u32 f, v4h S, v4h Dd, v4h BF) {
  switch (f) {
    case 1: return (v4h)0; case 2: return V255; case 3: return S; case 4: return V255 - S; case 5: case 12: return S.wwww; case 6: case 13: return V255 - S.wwww;
    case 7: return Dd.wwww; case 8: return V255 - Dd.wwww; case 9: return Dd; case 10: return V255 - Dd;
    case 11: { uint16_t ida = 255 - Dd.w, m = S.w < ida ? S.w : ida; v4h r = (v4h)m; r.w = 255; return r; }
    case 14: return BF; default: return V255 - BF;
  }
}
AI int vcmpf(u32 fn, float a, float b) { switch (fn) { case 1: return 0; case 2: return a < b; case 3: return a == b; case 4: return a <= b; case 5: return a > b; case 6: return a != b; case 7: return a >= b; default: return 1; } }
// screen triangles, rasterised in horizontal 16-row bands by a worker pool
typedef struct { SV v[3]; } Tri;
#define BAND_SHIFT 4
static long raster_tri(const SV *a, const SV *b, const SV *c, int nband, int band) {
  float area = (b->x - a->x) * (c->y - a->y) - (c->x - a->x) * (b->y - a->y);
  if (area == 0 || !(area == area)) return 0;
  u32 cull = RS->rs[22];
  if (cull == 3 && area < 0) return 0;
  if (cull == 2 && area > 0) return 0;
  if (area < 0) { const SV *t = b; b = c; c = t; area = -area; }
  float minx = fminf(a->x, fminf(b->x, c->x)), maxx = fmaxf(a->x, fmaxf(b->x, c->x)), miny = fminf(a->y, fminf(b->y, c->y)), maxy = fmaxf(a->y, fmaxf(b->y, c->y));
  if (!(minx > -1e7f && maxx < 1e7f && miny > -1e7f && maxy < 1e7f)) return 0;
  int x0 = (int)ceilf(minx), x1 = (int)floorf(maxx), y0 = (int)ceilf(miny), y1 = (int)floorf(maxy);
  if (x0 < clip_x0) x0 = clip_x0; if (y0 < clip_y0) y0 = clip_y0; if (x1 > clip_x1 - 1) x1 = clip_x1 - 1; if (y1 > clip_y1 - 1) y1 = clip_y1 - 1;
  if (x0 > x1 || y0 > y1) return 0;
  if (nband > 1) {   // skip the setup when none of this band's rows are inside [y0, y1]
    int first = y0 >> BAND_SHIFT; int k = (band - first % nband + nband) % nband; int row = (first + k) << BAND_SHIFT;
    if (row < y0) row = y0; if (row > y1) return 0;
  }
  const SV *V[3] = {a, b, c};
  float ex[3], ey[3], ec[3]; int tl[3];
  for (int i = 0; i < 3; i++) { const SV *p = V[(i + 1) % 3], *q = V[(i + 2) % 3];
    ex[i] = -(q->y - p->y); ey[i] = (q->x - p->x); ec[i] = -(ex[i] * p->x + ey[i] * p->y);
    tl[i] = (q->y == p->y && q->x < p->x) || (q->y > p->y); }
  const Pipe *pp = &PP; int ntc = pp->ntc, usespec = pp->usespec;
  float inv = 1.0f / area; long n = 0;
  // fast path: attribute planes q(x,y) = qx*x + qy*y + q0 (attributes are pre-multiplied by 1/w), stepped per pixel
  enum { Q_IW, Q_Z, Q_B, Q_G, Q_R, Q_A, Q_UV, NQMAX = Q_UV + 16 };
  float qx[NQMAX], qy[NQMAX], q0[NQMAX]; int NQ = Q_UV + 2 * pp->nuv;
  if (pp->fast) {
    float va[NQMAX] = {a->iw, a->z, a->c[2], a->c[1], a->c[0], a->c[3]}, vb[NQMAX] = {b->iw, b->z, b->c[2], b->c[1], b->c[0], b->c[3]}, vc[NQMAX] = {c->iw, c->z, c->c[2], c->c[1], c->c[0], c->c[3]};
    for (int t = 0; t < 8; t++) if (pp->uvslot[t] != 0xFF) { int k = Q_UV + 2 * pp->uvslot[t];
      va[k] = a->t[t][0]; va[k + 1] = a->t[t][1]; vb[k] = b->t[t][0]; vb[k + 1] = b->t[t][1]; vc[k] = c->t[t][0]; vc[k + 1] = c->t[t][1]; }
    for (int k = 0; k < NQ; k++) { qx[k] = (ex[0] * va[k] + ex[1] * vb[k] + ex[2] * vc[k]) * inv; qy[k] = (ey[0] * va[k] + ey[1] * vb[k] + ey[2] * vc[k]) * inv;
      q0[k] = (ec[0] * va[k] + ec[1] * vb[k] + ec[2] * vc[k]) * inv; }
  }
  const v4h F = U(pp->tf), BF = U(pp->bfac); const int nst = pp->nst;
  // fog (float path only; pipe_setup keeps fogged draws off the fast path). Table fog uses eye depth (w) when
  // the projection is perspective ("W-friendly"), else device z, as D3D9 does.
  const int fogon = RS->rs[28] != 0; const u32 fogtab = RS->rs[35]; const int fogw = RS->fogw;
  for (int y = y0; y <= y1; y++) {
    if (nband > 1 && ((y >> BAND_SHIFT) % nband) != band) { y |= (1 << BAND_SHIFT) - 1; continue; }
    // span from the three edge inequalities (then exact per-pixel test at the span ends)
    float xl = (float)x0, xr = (float)x1;
    for (int i = 0; i < 3; i++) { float r = ey[i] * y + ec[i];
      if (ex[i] > 0) { float xm = -r / ex[i]; if (xm > xl) xl = xm; }
      else if (ex[i] < 0) { float xm = -r / ex[i]; if (xm < xr) xr = xm; }
      else if (r < 0) { xl = 1; xr = 0; } }
    int sx0 = (int)floorf(xl) - 1, sx1 = (int)ceilf(xr) + 1;
    if (sx0 < x0) sx0 = x0; if (sx1 > x1) sx1 = x1;
    if (pp->fast) {
      while (sx0 <= sx1) { int ok = 1; for (int i = 0; i < 3; i++) { float w = ex[i] * sx0 + ey[i] * y + ec[i]; if (w < 0 || (w == 0 && !tl[i])) { ok = 0; break; } } if (ok) break; sx0++; }
      while (sx1 >= sx0) { int ok = 1; for (int i = 0; i < 3; i++) { float w = ex[i] * sx1 + ey[i] * y + ec[i]; if (w < 0 || (w == 0 && !tl[i])) { ok = 0; break; } } if (ok) break; sx1--; }
      if (sx0 > sx1) continue;
      float q[NQMAX]; for (int k = 0; k < NQ; k++) q[k] = qx[k] * sx0 + qy[k] * y + q0[k];
      float *zrow = pp->ztest ? &pp->zbuf[y * pp->zw] : NULL; u32 *drow = (u32 *)(pp->rt + y * pp->rtpitch);
      for (int x = sx0; x <= sx1; x++, ({ for (int k = 0; k < NQ; k++) q[k] += qx[k]; })) {
        float z = q[Q_Z];
        if (zrow && !vcmpf(pp->zfunc, z, zrow[x])) continue;
        float pw = q[Q_IW] != 0 ? 1.0f / q[Q_IW] : 1.0f;
        v4f df = (v4f){q[Q_B], q[Q_G], q[Q_R], q[Q_A]} * pw;
        df = __builtin_elementwise_min(__builtin_elementwise_max(df, (v4f)0.f), (v4f)1.f) * 255.f + 0.5f;
        v4h D = __builtin_convertvector(df, v4h), C = D;
        for (int k = 0; k < nst; k++) {
          const FStage *f = &pp->st[k]; v4h T = V255;
          if (f->hastex) { int sl = Q_UV + 2 * pp->uvslot[f->tci]; T = U(samp(&f->sm, q[sl] * pw, q[sl + 1] * pw)); }
          v4h rgb = vfop(f->cop, vfarg(f->c1, D, C, T, F), vfarg(f->c2, D, C, T, F), D, C, T, F);
          if (f->aop == 1) rgb.w = C.w;
          else if (!f->same) rgb.w = vfop(f->aop, vfarg(f->a1, D, C, T, F), vfarg(f->a2, D, C, T, F), D, C, T, F).w;
          C = rgb;
        }
        if (pp->atest && !icmp(pp->afunc, C.w, pp->aref)) continue;
        if (zrow && pp->zwrite) zrow[x] = z;
        u32 *dp = &drow[x]; u32 dc = *dp, out;
        if (pp->blend) {
          v4h Dd = U(dc), S;
          if (pp->bop >= 4) S = pp->bop == 5 ? __builtin_elementwise_max(C, Dd) : __builtin_elementwise_min(C, Dd);
          else { v4h sa = vmul(C, vbfac(pp->sbf, C, Dd, BF)), da = vmul(Dd, vbfac(pp->dbf, C, Dd, BF));
            S = pp->bop == 2 ? vsubs(sa, da) : pp->bop == 3 ? vsubs(da, sa) : vadds(sa, da); }
          out = PK(S);
        } else out = PK(C);
        u32 wm = pp->wmask;
        *dp = wm == 15 ? out : ((wm & 1 ? out : dc) & 0x00FF0000u) | ((wm & 2 ? out : dc) & 0x0000FF00u) | ((wm & 4 ? out : dc) & 0xFFu) | ((wm & 8 ? out : dc) & 0xFF000000u);
        n++;
      }
      continue;
    }
    for (int x = sx0; x <= sx1; x++) {
      float w[3]; int in = 1;
      for (int i = 0; i < 3; i++) { w[i] = ex[i] * x + ey[i] * y + ec[i]; if (w[i] < 0 || (w[i] == 0 && !tl[i])) { in = 0; break; } }
      if (!in) continue;
      float l0 = w[0] * inv, l1 = w[1] * inv, l2 = w[2] * inv;
      float iw = l0 * a->iw + l1 * b->iw + l2 * c->iw; float pw = iw != 0 ? 1.0f / iw : 1.0f;
      float z = l0 * a->z + l1 * b->z + l2 * c->z;
      float diff[4], spec[4], tc[8][4];
      for (int k = 0; k < 4; k++) diff[k] = clampf((l0 * a->c[k] + l1 * b->c[k] + l2 * c->c[k]) * pw);
      if (usespec) for (int k = 0; k < 4; k++) spec[k] = clampf((l0 * a->sp[k] + l1 * b->sp[k] + l2 * c->sp[k]) * pw);
      for (int t = 0; t < ntc; t++) for (int k = 0; k < 2; k++) tc[t][k] = (l0 * a->t[t][k] + l1 * b->t[t][k] + l2 * c->t[t][k]) * pw;
      float fogf = 1.f;
      if (fogon) { if (fogtab) fogf = fog_factor(RS->rs, fogtab, fogw ? pw : z); else { fogf = (l0 * a->fog + l1 * b->fog + l2 * c->fog) * pw; fogf = fogf < 0 ? 0 : fogf > 1 ? 1 : fogf; } }
      n += shade_pixel(x, y, z, diff, usespec ? spec : NULL, tc, fogf);
    }
  }
  return n;
}
// ---- deferred raster queue ----
// draw_common fetches/transforms vertices on the emulator thread and appends a Job (render state snapshot +
// screen triangles) to the batch being built. Full batches enter a ring of RQ_N batches. GW_RTHREADS worker
// threads (default 6) each own every nth 16-row band and walk the ring in order at their own pace, so the
// emulator only waits when the ring is full (bursts of draws are absorbed; Present does not wait).
// Anything the guest CPU can observe (Lock of a surface a queued job reads or writes, copies, ColorFill, PNG
// shots) first drains the ring (rq_sync). A draw that samples a surface written by a batch that some band may
// not have finished (submitting the building batch first if it is the writer) goes into a barrier batch: no
// band starts a barrier batch before every band has finished all earlier batches, so cross-band texture reads
// (render-to-texture, e.g. the 1280x800 scene composited onto the back buffer) see finished rows.
// GW_RTHREADS=1: no workers; batches are rasterised on the emulator thread (synchronous).
typedef struct Job { int kind; RState st; Pipe pp; Obj *rt, *ds; int cx0, cy0, cx1, cy1; int t0, nt; u32 fl, col; float z; } Job;
typedef struct Batch { Job *jobs; int nj, cj; Tri *tris; int ntri, ctri; double area; int barrier; } Batch;
#define RQ_MAX 128
static int RQ_N = 16;   // ring size, GW_RQN (2..128)
static Batch bq[RQ_MAX];
static unsigned long rq_sub;                 // batches submitted; the batch being built is bq[rq_sub % RQ_N]
static unsigned long ser_built = 1, ser_done = 0;   // serial (1-based index) of the building batch / batches all bands finished
static int next_barrier;
#define bbuild ((int)(rq_sub % RQ_N))
#include <pthread.h>
static int nrth = -1; static pthread_t rth[32]; static pthread_mutex_t rmu = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t rcv_go = PTHREAD_COND_INITIALIZER, rcv_done = PTHREAD_COND_INITIALIZER;
static unsigned long wdone[32]; static long rpix[32];
static uint64_t rcpu_ns[32];   // per band thread CPU spent rasterising (load-independent cost metric)
static uint64_t g_rq_wait_ns;  // emulator thread time spent waiting for the workers
static uint64_t tcpu_ns(void) { struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID, &t); return (uint64_t)t.tv_sec * 1000000000ull + t.tv_nsec; }
uint64_t mono_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000000ull + t.tv_nsec; }
void (*gw_front_ready)(void);
// ---- Metal backend (GW_RENDERER=metal, METAL-PLAN.md) ----
static Obj **mgpu_list; static int mgpu_n, mgpu_cap;
static void mgpu_mark(Obj *s) { if (s->mgpu) return; s->mgpu = 1; if (mgpu_n == mgpu_cap) { mgpu_cap = mgpu_cap ? 2 * mgpu_cap : 16; mgpu_list = realloc(mgpu_list, sizeof(Obj *) * mgpu_cap); } mgpu_list[mgpu_n++] = s; }
static void *gpu_surface(Obj *s) {   // GPU copy of a colour surface, uploaded when the guest CPU wrote it
  if (!s) return NULL;
  if (!s->mt || s->mcpu) { s->mt = mtl_texture(s->mt, s->w, s->h, 0);
    if (!s->mgpu) { if (s->fmt == F_X8R8G8B8 && s->pitch == s->w * 4) s->dirty = 1; mtl_upload(s->mt, surf_argb(s), s->w, s->h); }
    s->mcpu = 0; }
  return s->mt;
}
static void *gpu_depth(Obj *s) { if (!s || !s->z) return NULL; if (!s->mt) s->mt = mtl_texture(NULL, s->w, s->h, 1); return s->mt; }
static void mtl_front_done(void) { extern volatile unsigned g_front_seq; __atomic_add_fetch(&g_front_seq, 1, __ATOMIC_RELEASE); if (gw_front_ready) gw_front_ready(); }
static void mtl_sync_all(void) {   // the guest CPU is about to look at surfaces: finish the GPU and copy what it wrote back
  mtl_pool((void (*)(void *))mtl_finish, NULL);
  for (int i = 0; i < mgpu_n; i++) { Obj *s = mgpu_list[i]; s->mgpu = 0;
    if ((s->fmt == F_A8R8G8B8 || s->fmt == F_X8R8G8B8) && s->mt) { mtl_readback(s->mt, P(s->mem), s->pitch, s->w, s->h); s->dirty = 1; }
    else { static int warned; if (!warned++) gw_log("metal: read-back of format %u not supported", s->fmt); } }
  mgpu_n = 0;
}
extern u32 *g_front;
static MtlV *mvb; static int mvcap;
static void mtl_run_batch_(Batch *b);
static void mtl_run_batch(Batch *b) { mtl_pool((void (*)(void *))mtl_run_batch_, b); }
static void mtl_run_batch_(Batch *b) {
  for (int j = 0; j < b->nj; j++) {
    Job *jb = &b->jobs[j]; RState *s = &jb->st;
    if (jb->kind == 2) { void *bb = gpu_surface(jb->rt); mtl_present(bb, jb->rt->w, jb->rt->h, g_front, mtl_front_done); continue; }
    if (jb->kind == 1) { void *rt = gpu_surface(jb->rt); if (!rt) { static int w; if (!w++) gw_log("metal: depth-only clear without a target skipped"); continue; }
      mtl_clear(rt, gpu_depth(jb->ds), jb->cx0, jb->cy0, jb->cx1, jb->cy1, jb->fl, jb->col, jb->z); if (jb->fl & 1) mgpu_mark(jb->rt); continue; }
    if (!jb->nt) continue;
    MtlUni u; memset(&u, 0, sizeof u); MtlFixed f; memset(&f, 0, sizeof f);
    int nt = 0; for (int st = 0; st < 8; st++) { if (s->tss[st][1] == 1) break; nt = st + 1; }
    if (nt > 4) { static int w; if (!w++) gw_log("metal: draw with %d texture stages, only 4 used", nt); nt = 4; }
    void *tex[8] = {0};
    for (int st = 0; st < nt; st++) { const u32 *t = s->tss[st]; u32 *o = u.st[st];
      o[0] = t[1]; o[1] = t[2]; o[2] = t[3]; o[3] = t[4]; o[4] = t[5]; o[5] = t[6]; o[6] = t[11] & 7; o[7] = t[26]; o[8] = t[27]; o[9] = t[28];
      Obj *to = s->tex[st]; Obj *l = to ? (to->kind == K_SURF ? to : to->lv[0]) : NULL;
      if (l) { tex[st] = gpu_surface(l); o[10] = 1; }
      const u32 *ss = s->ss[st]; f.samp[st] = (ss[5] >= 2 || ss[6] >= 2 ? 1 : 0) | (ss[1] & 3) << 4 | (ss[2] & 3) << 8; }
    u.nst = nt; u.tfactor = s->rs[60]; u.specen = s->rs[29]; u.fogon = s->rs[28]; u.fogtab = s->rs[35]; u.fogw = s->fogw; u.fogcol = s->rs[34];
    u.atest = s->rs[15]; u.afunc = s->rs[25]; u.aref = s->rs[24]; memcpy(&u.fogstart, &s->rs[36], 4); memcpy(&u.fogend, &s->rs[37], 4); memcpy(&u.fogdens, &s->rs[38], 4);
    u.rtw = (float)jb->rt->w; u.rth = (float)jb->rt->h;
    f.blend = s->rs[27] != 0; f.sbf = s->rs[19]; f.dbf = s->rs[20]; f.bop = s->rs[171] ? s->rs[171] : 1; f.sepa = s->rs[206] != 0; f.sba = s->rs[207]; f.dba = s->rs[208];
    f.wmask = s->rs[168] & 15; f.bfactor = s->rs[193]; f.zen = jb->ds && jb->ds->z && s->rs[7]; f.zfunc = s->rs[23]; f.zwrite = s->rs[14];
    if (mvcap < jb->nt * 3) { mvcap = jb->nt * 3 + 1024; mvb = realloc(mvb, sizeof(MtlV) * mvcap); }
    int nv = 0; u32 cull = s->rs[22];
    for (int i = jb->t0; i < jb->t0 + jb->nt; i++) { const SV *a = &b->tris[i].v[0], *bv = &b->tris[i].v[1], *c = &b->tris[i].v[2];
      float area = (bv->x - a->x) * (c->y - a->y) - (c->x - a->x) * (bv->y - a->y);
      if (area == 0 || !(area == area) || (cull == 3 && area < 0) || (cull == 2 && area > 0)) continue;
      const SV *V3[3] = {a, bv, c};
      for (int k = 0; k < 3; k++) { const SV *p = V3[k]; MtlV *o = &mvb[nv++];
        o->pos[0] = p->x; o->pos[1] = p->y; o->pos[2] = p->z; o->pos[3] = p->iw; memcpy(o->c, p->c, 16); memcpy(o->sp, p->sp, 16);
        for (int t = 0; t < 4; t++) { o->t[t][0] = p->t[t][0]; o->t[t][1] = p->t[t][1]; } o->fog = p->fog; } }
    mtl_draw(gpu_surface(jb->rt), gpu_depth(jb->ds), &u, &f, tex, jb->cx0, jb->cy0, jb->cx1, jb->cy1, mvb, nv);
    mgpu_mark(jb->rt);
  }
}
static void run_batch(Batch *b, int band, int nb) {
  uint64_t t0 = tcpu_ns(); long n = 0;
  for (int j = 0; j < b->nj; j++) {
    Job *jb = &b->jobs[j]; RS = &jb->st; RT = jb->rt; DS = jb->ds; PP = jb->pp;
    clip_x0 = jb->cx0; clip_y0 = jb->cy0; clip_x1 = jb->cx1; clip_y1 = jb->cy1;
    if (jb->kind == 2) {   // present: copy this band's rows of the back buffer to the front buffer
      extern u32 *g_front; extern volatile unsigned g_front_seq;
      for (int y = 0; y < RT->h; y++) { if (nb > 1 && ((y >> BAND_SHIFT) % nb) != band) { y |= (1 << BAND_SHIFT) - 1; continue; }
        memcpy(g_front + (size_t)y * RT->w, P(RT->mem + y * RT->pitch), (size_t)RT->w * 4); }
      // 2026-11-18: the frame is complete only when every band has copied its rows; the last band bumps the
      // sequence and wakes the window (gw_front_ready, set by the app shell) so it shows the frame at once
      if (__atomic_add_fetch(&jb->col, 1, __ATOMIC_ACQ_REL) == (u32)nb) { __atomic_add_fetch(&g_front_seq, 1, __ATOMIC_RELEASE); if (gw_front_ready) gw_front_ready(); }
      continue; }
    if (jb->kind == 0) { for (int i = jb->t0; i < jb->t0 + jb->nt; i++) n += raster_tri(&b->tris[i].v[0], &b->tris[i].v[1], &b->tris[i].v[2], nb, band); continue; }
    for (int y = clip_y0; y < clip_y1; y++) {   // clear job
      if (nb > 1 && ((y >> BAND_SHIFT) % nb) != band) { y |= (1 << BAND_SHIFT) - 1; continue; }
      if ((jb->fl & 1) && RT && y < RT->h) { u32 *r = (u32 *)P(RT->mem + y * RT->pitch); for (int x = clip_x0; x < clip_x1 && x < RT->w; x++) r[x] = jb->col; }
      if ((jb->fl & 2) && DS && DS->z && y < DS->h) { float *r = &DS->z[y * DS->w]; for (int x = clip_x0; x < clip_x1 && x < DS->w; x++) r[x] = jb->z; }
    }
  }
  __atomic_add_fetch(&rpix[band], n, __ATOMIC_RELAXED); rcpu_ns[band] += tcpu_ns() - t0;
}
static unsigned long wmin(void) { unsigned long m = wdone[0]; for (int i = 1; i < nrth; i++) if (wdone[i] < m) m = wdone[i]; return m; }
static void *rworker(void *arg) {
  int band = (int)(intptr_t)arg; extern void gw_set_qos_workers(void); gw_set_qos_workers();
  for (;;) {
    pthread_mutex_lock(&rmu);
    while (wdone[band] >= rq_sub) pthread_cond_wait(&rcv_go, &rmu);
    unsigned long i = wdone[band]; Batch *b = &bq[i % RQ_N];
    while (b->barrier && wmin() < i) pthread_cond_wait(&rcv_go, &rmu);
    pthread_mutex_unlock(&rmu);
    run_batch(b, band, nrth);
    pthread_mutex_lock(&rmu); wdone[band] = i + 1; pthread_cond_broadcast(&rcv_done); pthread_cond_broadcast(&rcv_go); pthread_mutex_unlock(&rmu);
  }
  return NULL;
}
static void rq_init(void) {
  if (nrth >= 0) return;
  nrth = getenv("GW_RTHREADS") ? atoi(getenv("GW_RTHREADS")) : 6; if (nrth < 1) nrth = 1; if (nrth > 32) nrth = 32;
  { const char *r = getenv("GW_RENDERER"); if (r && !strcmp(r, "metal")) { if (mtl_init()) { g_metal = 1; nrth = 1; } else gw_log("metal unavailable: CPU rasteriser"); } }
  if (getenv("GW_RQN")) { RQ_N = atoi(getenv("GW_RQN")); if (RQ_N < 2) RQ_N = 2; if (RQ_N > RQ_MAX) RQ_N = RQ_MAX; }
  if (nrth > 1) for (int i = 0; i < nrth; i++) pthread_create(&rth[i], NULL, rworker, (void *)(intptr_t)i);
  gw_log("rasteriser: %d worker thread(s)%s", nrth > 1 ? nrth : 0, nrth > 1 ? ", deferred ring queue" : " (synchronous on the emulator thread)");
}
static void batch_reset(Batch *b) { b->nj = 0; b->ntri = 0; b->area = 0; b->barrier = 0; }
static void rq_collect(void) {   // called with rmu held
  unsigned long m = wmin(); ser_done = m;
  long t = 0; for (int i = 0; i < 32; i++) t += __atomic_load_n(&rpix[i], __ATOMIC_RELAXED); g_pix = t;   // per-band counters only grow; exact after rq_sync
}
static void rq_wait_upto(unsigned long need) {   // wait until every band finished the first `need` batches
  if (nrth <= 1) return;
  uint64_t t0 = mono_ns();
  pthread_mutex_lock(&rmu); while (wmin() < need) pthread_cond_wait(&rcv_done, &rmu); rq_collect(); pthread_mutex_unlock(&rmu);
  g_rq_wait_ns += mono_ns() - t0;
}
static void rq_submit(void) {
  rq_init(); Batch *b = &bq[bbuild]; if (!b->nj) return;
  if (next_barrier) { b->barrier = 1; next_barrier = 0; }
  if (nrth <= 1) { if (g_metal) mtl_run_batch(b); else run_batch(b, 0, 1); batch_reset(b); rq_sub++; ser_built = rq_sub + 1; ser_done = rq_sub; g_pix = rpix[0]; return; }
  pthread_mutex_lock(&rmu); rq_sub++; ser_built = rq_sub + 1; pthread_cond_broadcast(&rcv_go); pthread_mutex_unlock(&rmu);
  // the next building slot must be free: all bands done with the batch that used it RQ_N submissions ago
  if (rq_sub >= RQ_N) rq_wait_upto(rq_sub - RQ_N + 1);
  batch_reset(&bq[bbuild]);
}
static void rq_wait(void) { rq_wait_upto(rq_sub); }
static void rq_sync(void) { rq_submit(); rq_wait(); if (g_metal) mtl_sync_all(); }
// write-after-read across bands: a surface that a possibly unfinished batch samples as a texture must not be
// overwritten by a later batch before every band is done reading it (e.g. the next frame's scene render into the
// render target that the previous frame's composite is still reading in another band)
static void rq_order_write(Obj *s) { if (s && s->rser > ser_done) { if (s->rser == ser_built) rq_submit(); next_barrier = 1; } }
static void obj_sync(Obj *s) { if (s && ((s->rser > ser_done || s->wser > ser_done) || (g_metal && s->mgpu))) rq_sync(); }   // before the guest CPU touches s
static Job *job_new(int kind) {
  Batch *b = &bq[bbuild];
  if (b->nj == b->cj) { b->cj = b->cj ? b->cj * 2 : 256; b->jobs = realloc(b->jobs, sizeof(Job) * b->cj); }
  Job *j = &b->jobs[b->nj++]; j->kind = kind; j->t0 = b->ntri; j->nt = 0; return j;
}
static void tri_push(const SV *a, const SV *b, const SV *c) {
  Batch *B = &bq[bbuild];
  if (B->ntri == B->ctri) { B->ctri = B->ctri ? B->ctri * 2 : 4096; B->tris = realloc(B->tris, sizeof(Tri) * B->ctri); }
  Tri *t = &B->tris[B->ntri++]; t->v[0] = *a; t->v[1] = *b; t->v[2] = *c;
  float ar = fabsf((b->x - a->x) * (c->y - a->y) - (c->x - a->x) * (b->y - a->y)) * 0.5f; B->area += ar < 2e6f ? ar : 2e6f;
}
static void rq_maybe_submit(void) { Batch *b = &bq[bbuild]; if (b->area > 250000 || b->nj >= 96 || b->ntri >= 20000) rq_submit(); }
static u32 Dev_Clear(void) {   // queued as a raster job (rows split over the bands like draws)
  Obj *d = THIS; u32 n = ARG(1), rects = ARG(2), fl = ARG(3), col = ARG(4); float z = rf(g_esp + 24);
  State *s = &d->cur; int vx0 = s->vp[0], vy0 = s->vp[1], vx1 = s->vp[0] + s->vp[2], vy1 = s->vp[1] + s->vp[3];
  if (!(fl & 1) && !((fl & 2) && d->ds && d->ds->z)) return 0;
  rq_init(); if (fl & 1) rq_order_write(d->rt);
  for (u32 k = 0; k < (n ? n : 1); k++) {
    int x0 = vx0, y0 = vy0, x1 = vx1, y1 = vy1;
    if (n && rects) { u32 r = rects + 16 * k; x0 = (i32)r32(r) > x0 ? (i32)r32(r) : x0; y0 = (i32)r32(r + 4) > y0 ? (i32)r32(r + 4) : y0;
      x1 = (i32)r32(r + 8) < x1 ? (i32)r32(r + 8) : x1; y1 = (i32)r32(r + 12) < y1 ? (i32)r32(r + 12) : y1; }
    if (s->rs[174]) { int sx0 = s->scissor[0], sy0 = s->scissor[1], sx1 = s->scissor[2], sy1 = s->scissor[3];
      if (x0 < sx0) x0 = sx0; if (y0 < sy0) y0 = sy0; if (x1 > sx1) x1 = sx1; if (y1 > sy1) y1 = sy1; }
    if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0; if (x0 >= x1 || y0 >= y1) continue;
    Job *j = job_new(1); j->rt = d->rt;   // colour is gated on fl & 1 (Metal needs the target for depth-only clears too) j->ds = (fl & 2) ? d->ds : NULL; j->fl = fl; j->col = col; j->z = z;
    j->cx0 = x0; j->cy0 = y0; j->cx1 = x1; j->cy1 = y1; RS = &j->st; j->st.rs[22] = 1;
    if (fl & 1) { d->rt->wser = ser_built; d->rt->dirty = 1; } if ((fl & 2) && d->ds) d->ds->wser = ser_built;
  }
  return 0;
}
static void to_screen(const Vtx *v, int rhw, SV *o) {
  RState *s = RS; float vx = (float)s->vp[0], vy = (float)s->vp[1], vw = (float)s->vp[2], vh = (float)s->vp[3], mnz, mxz; memcpy(&mnz, &s->vp[4], 4); memcpy(&mxz, &s->vp[5], 4);
  float iw;
  if (rhw) { o->x = v->x; o->y = v->y; o->z = v->z; iw = v->w; }
  else { iw = 1.0f / v->w; o->x = vx + (v->x * iw + 1) * vw * 0.5f; o->y = vy + (1 - v->y * iw) * vh * 0.5f; o->z = mnz + v->z * iw * (mxz - mnz); }
  o->iw = iw;
  for (int k = 0; k < 4; k++) { o->c[k] = v->c[k] * iw; o->sp[k] = v->sp[k] * iw; }
  int nt = PP.ntc > 8 ? 8 : PP.ntc;   // only the texcoord sets this draw's stages read (pipe_setup ran first)
  for (int t = 0; t < nt; t++) for (int k = 0; k < 4; k++) o->t[t][k] = v->t[t][k] * iw;
  o->fog = v->fog * iw;
}
static void lerp_vtx(const Vtx *a, const Vtx *b, float t, Vtx *o) {
  const float *pa = (const float *)a, *pb = (const float *)b; float *po = (float *)o;
  for (size_t i = 0; i < sizeof(Vtx) / 4; i++) po[i] = pa[i] + (pb[i] - pa[i]) * t;
}
static void draw_tri(const Vtx *a, const Vtx *b, const Vtx *c, int rhw) {
  if (rhw) { SV A, B, C; to_screen(a, 1, &A); to_screen(b, 1, &B); to_screen(c, 1, &C); tri_push(&A, &B, &C); return; }
  // clip against near plane z >= 0 and w > eps
  Vtx in[8], out[8]; int n = 3; in[0] = *a; in[1] = *b; in[2] = *c;
  for (int plane = 0; plane < 2; plane++) {
    int m = 0;
    for (int i = 0; i < n; i++) {
      const Vtx *p = &in[i], *q = &in[(i + 1) % n];
      float dp = plane == 0 ? p->z : p->w - 1e-5f, dq = plane == 0 ? q->z : q->w - 1e-5f;
      if (dp >= 0) out[m++] = *p;
      if ((dp >= 0) != (dq >= 0)) { float t = dp / (dp - dq); lerp_vtx(p, q, t, &out[m++]); }
    }
    n = m; memcpy(in, out, sizeof(Vtx) * n); if (n < 3) return;
  }
  SV sv[8]; for (int i = 0; i < n; i++) to_screen(&in[i], 0, &sv[i]);
  for (int i = 1; i + 1 < n; i++) tri_push(&sv[0], &sv[i], &sv[i + 1]);
}
// Points and lines become screen-space quads (two triangles, wound so the current cull mode keeps them).
// Lines: 1 pixel wide. Points: D3DRS_POINTSIZE (154) pixels square, clamped to POINTSIZE_MIN/MAX (155/166);
// with POINTSPRITEENABLE (156) texture coordinates run 0..1 across the sprite. Not clipped against the near
// plane: a point/line with a vertex behind the eye (w <= 0) is dropped.
static void push_quad(SV q[4]) {   // q: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right
  u32 cull = RS->rs[22]; const SV *a = &q[0], *b = &q[1], *c = &q[2];
  float area = (b->x - a->x) * (c->y - a->y) - (c->x - a->x) * (b->y - a->y);
  int flip = (cull == 3 && area < 0) || (cull == 2 && area > 0);
  if (flip) { tri_push(&q[0], &q[2], &q[1]); tri_push(&q[1], &q[2], &q[3]); } else { tri_push(&q[0], &q[1], &q[2]); tri_push(&q[1], &q[3], &q[2]); }
}
static void draw_point(const Vtx *v, int rhw, State *s) {
  if (!rhw && v->w <= 1e-5f) return;
  SV c; to_screen(v, rhw, &c); float sz, mn, mx; memcpy(&sz, &s->rs[154], 4); memcpy(&mn, &s->rs[155], 4); memcpy(&mx, &s->rs[166], 4);
  if (!(sz > 0)) sz = 1; if (mn > 0 && sz < mn) sz = mn; if (mx > 0 && sz > mx) sz = mx; if (sz > 256) sz = 256;
  float h = sz * 0.5f; SV q[4];
  for (int k = 0; k < 4; k++) { q[k] = c; q[k].x = c.x + ((k & 1) ? h : -h); q[k].y = c.y + ((k & 2) ? h : -h);
    if (s->rs[156]) { q[k].t[0][0] = ((k & 1) ? 1.f : 0.f) * c.iw; q[k].t[0][1] = ((k & 2) ? 1.f : 0.f) * c.iw; } }
  push_quad(q);
}
static void draw_line(const Vtx *a, const Vtx *b, int rhw) {
  if (!rhw && (a->w <= 1e-5f || b->w <= 1e-5f)) return;
  SV A, B; to_screen(a, rhw, &A); to_screen(b, rhw, &B);
  float dx = B.x - A.x, dy = B.y - A.y, l = sqrtf(dx * dx + dy * dy); if (!(l > 0)) return;
  float nx = -dy / l * 0.5f, ny = dx / l * 0.5f; SV q[4] = {A, B, A, B};
  q[0].x += nx; q[0].y += ny; q[1].x += nx; q[1].y += ny; q[2].x -= nx; q[2].y -= ny; q[3].x -= nx; q[3].y -= ny;
  push_quad(q);
}
static Vtx *vbuf; static int vcap;
static int g_drawlog = -1;
// census since the last stat line (GW_STAT_EVERY): fogged draws by mode, point and line draws
static long cn_draws, cn_fog, cn_fog_table, cn_fog_vertex, cn_fog_vs, cn_points, cn_lines;
static void census_log(void) {
  if (cn_fog || cn_points || cn_lines)
    gw_log("census: draws %ld, fog %ld (table %ld, vertex %ld, shader/specular %ld), point draws %ld, line draws %ld",
      cn_draws, cn_fog, cn_fog_table, cn_fog_vertex, cn_fog_vs, cn_points, cn_lines);
  cn_draws = cn_fog = cn_fog_table = cn_fog_vertex = cn_fog_vs = cn_points = cn_lines = 0;
}
static int draw_common(State *s, u32 type, u32 pc, int base, int minv, int nv, const u8 *idx, int idx32, const u8 *up, int upstride) {
  if (s->vs && !vs_ok(s->vs->vsp)) { if (!warned_vs++) gw_log("WARN: draw with an unsupported vertex shader skipped"); return 0; }
  if (nv <= 0) return 0;
  cn_draws++; if (type == 1) cn_points++; else if (type == 2 || type == 3) cn_lines++;
  if (s->rs[28]) { static int nlog; float fs, fe, fd; memcpy(&fs, &s->rs[36], 4); memcpy(&fe, &s->rs[37], 4); memcpy(&fd, &s->rs[38], 4);
    if (nlog < 12) { nlog++; gw_log("fog draw: table=%u vertex=%u range=%u color=%08x start=%g end=%g density=%g vs=%d rhw-fvf=%d type=%u proj34=%g",
      s->rs[35], s->rs[140], s->rs[48], s->rs[34], fs, fe, fd, s->vs != NULL, (s->fvf & 0x400E) == 4, type, s->xf[3].m[11]); }
    cn_fog++; if (s->rs[35]) cn_fog_table++; else if (s->rs[140]) cn_fog_vertex++; else cn_fog_vs++; }
  if (nv > vcap) { vcap = nv * 2; vbuf = realloc(vbuf, sizeof(Vtx) * vcap); }
  rq_init();
  if (g_drawlog < 0) g_drawlog = getenv("GW_DRAWLOG") ? atoi(getenv("GW_DRAWLOG")) : 0;
  int dl = g_drawlog && g_dev->frames + 1 == g_drawlog;
  if (dl) rq_sync();   // per-draw pixel counts in the draw log
  Obj *rt = g_dev->rt, *ds = g_dev->ds;
  rq_order_write(rt);
  for (int st = 0; st < 8; st++) { Obj *t = s->tex[st]; Obj *l = t ? (t->kind == K_SURF ? t : t->lv[0]) : NULL; if (l && l->wser > ser_done) { if (l->wser == ser_built) rq_submit(); next_barrier = 1; break; } }   // written by a batch some band may not have finished
  Job *jb = job_new(0); RState *rsn = &jb->st;
  memcpy(rsn->rs, s->rs, sizeof rsn->rs); memcpy(rsn->tss, s->tss, sizeof rsn->tss); memcpy(rsn->ss, s->ss, sizeof rsn->ss);
  memcpy(rsn->tex, s->tex, sizeof rsn->tex); memcpy(rsn->vp, s->vp, sizeof rsn->vp);
  rsn->fogw = s->vs || s->xf[3].m[11] != 0.f;   // perspective projection (_34 != 0): table fog on eye depth
  RS = rsn; RT = rt; DS = ds;
  int rhw = fetch_vertices(s, base, minv, nv, up, upstride, vbuf);
  clip_x0 = s->vp[0]; clip_y0 = s->vp[1]; clip_x1 = s->vp[0] + s->vp[2]; clip_y1 = s->vp[1] + s->vp[3];
  if (clip_x1 > RT->w) clip_x1 = RT->w; if (clip_y1 > RT->h) clip_y1 = RT->h;
  if (s->rs[174]) { if ((int)s->scissor[0] > clip_x0) clip_x0 = s->scissor[0]; if ((int)s->scissor[1] > clip_y0) clip_y0 = s->scissor[1];
    if ((int)s->scissor[2] < clip_x1) clip_x1 = s->scissor[2]; if ((int)s->scissor[3] < clip_y1) clip_y1 = s->scissor[3]; }
  jb->cx0 = clip_x0; jb->cy0 = clip_y0; jb->cx1 = clip_x1; jb->cy1 = clip_y1; jb->rt = rt; jb->ds = ds;
#define IDX(k) ((idx ? (idx32 ? ((const u32 *)idx)[k] : ((const u16 *)idx)[k]) : (u32)(minv + (k))) - (u32)minv)
  g_draws++;
  long pix0 = g_pix; pipe_setup(); jb->pp = PP;
  for (u32 p = 0; p < pc; p++) {
    u32 i0, i1, i2;
    if (type == 4) { i0 = IDX(3 * p); i1 = IDX(3 * p + 1); i2 = IDX(3 * p + 2); }
    else if (type == 5) { i0 = IDX(p); i1 = IDX(p + 1); i2 = IDX(p + 2); if (p & 1) { u32 t = i1; i1 = i2; i2 = t; } }
    else if (type == 6) { i0 = IDX(0); i1 = IDX(p + 1); i2 = IDX(p + 2); }
    else if (type == 1) { u32 i = IDX(p); if (i < (u32)nv) draw_point(&vbuf[i], rhw, s); continue; }
    else if (type == 2 || type == 3) { u32 a = IDX(type == 2 ? 2 * p : p), b = IDX(type == 2 ? 2 * p + 1 : p + 1); if (a < (u32)nv && b < (u32)nv) draw_line(&vbuf[a], &vbuf[b], rhw); continue; }
    else continue;
    if (i0 >= (u32)nv || i1 >= (u32)nv || i2 >= (u32)nv) continue;
    draw_tri(&vbuf[i0], &vbuf[i1], &vbuf[i2], rhw); g_tris++;
  }
  Batch *bb = &bq[bbuild]; jb->nt = bb->ntri - jb->t0;
  if (!jb->nt) bb->nj--;
  else {
    for (int st = 0; st < 8; st++) { Obj *t = s->tex[st]; Obj *l = t ? (t->kind == K_SURF ? t : t->lv[0]) : NULL; if (l) l->rser = ser_built; }
    rt->wser = ser_built; if (ds && jb->pp.zwrite) ds->wser = ser_built;
  }
  if (dl) rq_sync(); else rq_maybe_submit();
  RT->dirty = 1;
  if (dl) {
    { float bx0 = 1e9, by0 = 1e9, bx1 = -1e9, by1 = -1e9; for (int k = 0; k < nv; k++) { SV a; if (!rhw && vbuf[k].w <= 0) continue; to_screen(&vbuf[k], rhw, &a);
        if (a.x < bx0) bx0 = a.x; if (a.y < by0) by0 = a.y; if (a.x > bx1) bx1 = a.x; if (a.y > by1) by1 = a.y; }
      gw_log("draw bbox %.0f,%.0f-%.0f,%.0f rt=%s ss0=%u/%u/%u tss0=%u,%u,%u/%u,%u,%u tci=%x tss1=%u,%u,%u/%u,%u,%u blend=%u/%u op=%u tf=%08x", bx0, by0, bx1, by1, RT == g_dev->bb ? "bb" : "off",
        s->ss[0][1], s->ss[0][5], s->ss[0][6], s->tss[0][1], s->tss[0][2], s->tss[0][3], s->tss[0][4], s->tss[0][5], s->tss[0][6], s->tss[0][11],
        s->tss[1][1], s->tss[1][2], s->tss[1][3], s->tss[1][4], s->tss[1][5], s->tss[1][6], s->rs[19], s->rs[20], s->rs[171], s->rs[60]); }
    Obj *t0 = s->tex[0]; Obj *l0 = t0 ? (t0->kind == K_SURF ? t0 : t0->lv[0]) : NULL;
    gw_log("draw type=%u pc=%u nv=%d fvf=%x decl=%d rhw=%d up=%d tex0=%dx%d fmt=%x ps=%d vs=%d | v0=(%.1f,%.1f,%.3f,%.3f) c=(%.2f,%.2f,%.2f,%.2f) t=(%.3f,%.3f) | light=%u ab=%u %u/%u at=%u ref=%u z=%u zw=%u cull=%u op0=%u/%u/%u aop0=%u op1=%u tex1=%d fast=%d -> %ld px",
      type, pc, nv, s->fvf, s->vdecl ? s->vdecl->ndecl : -1, rhw, up != NULL, l0 ? l0->w : 0, l0 ? l0->h : 0, l0 ? l0->fmt : 0, s->ps != NULL, s->vs != NULL,
      vbuf[0].x, vbuf[0].y, vbuf[0].z, vbuf[0].w, vbuf[0].c[0], vbuf[0].c[1], vbuf[0].c[2], vbuf[0].c[3], vbuf[0].t[0][0], vbuf[0].t[0][1],
      s->rs[137], s->rs[27], s->rs[19], s->rs[20], s->rs[15], s->rs[24], s->rs[7], s->rs[14], s->rs[22], s->tss[0][1], s->tss[0][2], s->tss[0][3], s->tss[0][4], s->tss[1][1], s->tex[1] != NULL, PP.fast, g_pix - pix0);
    
    if (isnan(vbuf[0].x)) { Obj *vb = s->vb[0]; gw_log("   NaN vertex: vb=%p off=%u stride=%u base=%d minv=%d", (void*)vb, s->vboff[0], s->vbstride[0], base, minv);
      if (vb) { float *f = (float *)P(vb->mem + s->vboff[0] + (base + minv) * s->vbstride[0]); gw_log("   raw %f %f %f %f %f %f %f", f[0], f[1], f[2], f[3], f[4], f[5], f[6]); }
      for (int k = 0; k < s->vdecl->ndecl; k++) { u8 *e = s->vdecl->decl + 8 * k; gw_log("   decl[%d] stream=%d off=%d type=%d usage=%d idx=%d", k, e[0], e[2], e[4], e[6], e[7]); }
      int ids[3] = {256, 2, 3}; for (int k = 0; k < 3; k++) { float *m = s->xf[ids[k]].m; gw_log("   xf%d: %g %g %g %g / %g %g %g %g / %g %g %g %g / %g %g %g %g", ids[k], m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]); } }
    static int dvs; if (s->vs && l0 && dvs < 4) { char pth[1300]; snprintf(pth, sizeof pth, "%s/shots/tex_vs%d.png", g_run_dir, dvs++); png_write_argb(pth, surf_argb(l0), l0->w, l0->h, l0->w, 0);
      Obj *ps0 = s->tex[1]; (void)ps0; gw_log("   VS draw: c192=%g c202=%g tss0 %u %u %u | %u %u %u", s->vsc[192][0], s->vsc[202][0], s->tss[0][1], s->tss[0][2], s->tss[0][3], s->tss[0][4], s->tss[0][5], s->tss[0][6]);
      for (int k = 0; k < 3 && k < nv; k++) gw_log("   vsout v%d pos=%.3f %.3f %.3f %.3f c=%.2f %.2f %.2f %.2f t=%.3f %.3f", k, vbuf[k].x, vbuf[k].y, vbuf[k].z, vbuf[k].w, vbuf[k].c[0], vbuf[k].c[1], vbuf[k].c[2], vbuf[k].c[3], vbuf[k].t[0][0], vbuf[k].t[0][1]); }
    static int dumped; if (dumped < 3 && l0) { char pth[1300]; snprintf(pth, sizeof pth, "%s/shots/tex_draw%d.png", g_run_dir, dumped);
      png_write_argb(pth, surf_argb(l0), l0->w, l0->h, l0->w, 0); snprintf(pth, sizeof pth, "%s/shots/rt_after_draw%d.png", g_run_dir, dumped);
      png_write_argb(pth, (u32 *)P(RT->mem), RT->w, RT->h, RT->pitch / 4, 1); gw_log("   RT=%s bb=%s mem=%08x", RT == g_dev->bb ? "backbuffer" : "other", "", RT->mem); dumped++; }
    if (vbuf && nv > 2 && !rhw) { SV a; to_screen(&vbuf[0], 0, &a); gw_log("   screen v0 = %.1f,%.1f z=%.3f vp=%d,%d %dx%d", a.x, a.y, a.z, s->vp[0], s->vp[1], s->vp[2], s->vp[3]); }
  }
  return 0;
}
static u32 prim_verts(u32 type, u32 pc) { switch (type) { case 1: return pc; case 2: return 2 * pc; case 3: return pc + 1; case 4: return 3 * pc; case 5: case 6: return pc + 2; } return 0; }
static u32 Dev_DrawPrimitive(void) { State *s = &THIS->cur; u32 type = ARG(1), start = ARG(2), pc = ARG(3); return draw_common(s, type, pc, 0, start, prim_verts(type, pc), NULL, 0, NULL, 0); }
static u32 Dev_DrawIndexedPrimitive(void) {
  State *s = &THIS->cur; u32 type = ARG(1); i32 base = ARG(2); u32 minv = ARG(3), nv = ARG(4), si = ARG(5), pc = ARG(6);
  if (!s->ib) return D3DERR_INVALIDCALL; int i32f = s->ib->fmt == F_INDEX32;
  return draw_common(s, type, pc, base, minv, nv, P(s->ib->mem + si * (i32f ? 4 : 2)), i32f, NULL, 0);
}
static u32 Dev_DrawPrimitiveUP(void) { State *s = &THIS->cur; u32 type = ARG(1), pc = ARG(2), data = ARG(3), stride = ARG(4); return draw_common(s, type, pc, 0, 0, prim_verts(type, pc), NULL, 0, P(data), stride); }
static u32 Dev_DrawIndexedPrimitiveUP(void) {
  State *s = &THIS->cur; u32 type = ARG(1), minv = ARG(2), nv = ARG(3), pc = ARG(4), ix = ARG(5), fmt = ARG(6), vd = ARG(7), stride = ARG(8);
  return draw_common(s, type, pc, 0, minv, nv, P(ix), fmt == F_INDEX32, P(vd), stride);
}
int g_present_frames; static char shot_req[128];
void d3d_request_shot(const char *name) { snprintf(shot_req, sizeof shot_req, "%s", name); }
// front buffer: the last presented frame, copied band by band by the raster workers (Present job), read by the
// window shell (host/mac.m) without stopping the emulator. g_front_seq counts completed presents.
u32 *g_front; int g_front_w, g_front_h; volatile unsigned g_front_seq;
static u32 Dev_Present(void) {
  Obj *d = THIS; d->frames++; g_present_frames = d->frames; clock_on_present();
  { static int init; static uint64_t lt, lc, lw; static unsigned hist[7], hn; static const unsigned edge[6] = {34, 50, 80, 150, 300, 1000};
    if (!init) { init = 1; const char *e = getenv("GW_SPIKE"); g_spike_ms = e ? atoi(e) : 0; lt = mono_ns(); lc = tcpu_ns(); lw = g_rq_wait_ns; }
    if (g_spike_ms) { uint64_t t = mono_ns(), c = tcpu_ns(); u32 ms = (u32)((t - lt) / 1000000);
      int b = 0; while (b < 6 && ms >= edge[b]) b++; hist[b]++; hn++;
      if (ms >= (u32)g_spike_ms && d->frames > 2) {
        gw_log("spike frame #%d %ums: emu_cpu=%ums raster_wait=%ums sleep=%ums decode=%ums(%u) tex_new=%u(%luKB) read=%ums/%u/%luKB open=%ums(%u)",
          d->frames, ms, (u32)((c - lc) / 1000000), (u32)((g_rq_wait_ns - lw) / 1000000), (u32)(g_sc.sleep_us / 1000), (u32)(g_sc.decode_ns / 1000000), g_sc.decodes,
          g_sc.texnew, (unsigned long)(g_sc.texbytes >> 10), (u32)(g_sc.read_ns / 1000000), g_sc.reads, (unsigned long)(g_sc.read_b >> 10), (u32)(g_sc.open_ns / 1000000), g_sc.opens);
        { struct rusage ru; getrusage(RUSAGE_SELF, &ru); static long liv, lv; gw_log("  spike csw involuntary=%ld voluntary=%ld (process, since last spike)", ru.ru_nivcsw - liv, ru.ru_nvcsw - lv); liv = ru.ru_nivcsw; lv = ru.ru_nvcsw; }
        spike_samples_log(); spike_shims_log(1); } else spike_shims_log(0);
      if (hn >= 500) { gw_log("frametime last %u frames: <34ms %u, 34-50 %u, 50-80 %u, 80-150 %u, 150-300 %u, 300-1000 %u, >=1000 %u", hn, hist[0], hist[1], hist[2], hist[3], hist[4], hist[5], hist[6]);
        memset(hist, 0, sizeof hist); hn = 0; }
      lt = mono_ns(); lc = tcpu_ns(); lw = g_rq_wait_ns; memset(&g_sc, 0, sizeof g_sc); spike_samples_reset(); } }
  if (d->frames == 1) { extern void watch_nan(u32 lo, u32 hi); watch_nan(1, 0); }
  static int every = -1, maxf = -1, stat = 25; static char dir[1200];
  if (every < 0) { every = getenv("GW_SHOT_EVERY") ? atoi(getenv("GW_SHOT_EVERY")) : 25; maxf = getenv("GW_MAX_FRAMES") ? atoi(getenv("GW_MAX_FRAMES")) : 0;
    stat = getenv("GW_STAT_EVERY") ? atoi(getenv("GW_STAT_EVERY")) : (every ? every : 25);
    snprintf(dir, sizeof dir, "%s/shots", g_run_dir); mkdir(dir, 0755); }
  Obj *bb = d->bb;
  if (getenv("GW_FRONTRACE") && d->frames % 40 == 0 && g_front) { rq_sync(); char p[1300]; snprintf(p, sizeof p, "%s/front-race-%d.png", dir, d->frames); png_write_argb(p, g_front, g_front_w, g_front_h, g_front_w, 1);
    snprintf(p, sizeof p, "%s/bb-race-%d.png", dir, d->frames); png_write_argb(p, (u32 *)P(bb->mem), bb->w, bb->h, bb->pitch / 4, 1); }
  if (!g_front || g_front_w != bb->w || g_front_h != bb->h) { rq_sync(); free(g_front); g_front = calloc((size_t)bb->w * bb->h, 4); g_front_w = bb->w; g_front_h = bb->h; }
  rq_init(); Job *j = job_new(2); j->col = 0; j->rt = bb; j->ds = NULL; j->cx0 = 0; j->cy0 = 0; j->cx1 = bb->w; j->cy1 = bb->h; j->fl = 0; RS = &j->st; j->st.rs[22] = 1;
  int png = d->frames <= 3 || (every && d->frames % every == 0) || (maxf && d->frames >= maxf) || shot_req[0];
  if (png) rq_sync(); else rq_submit();   // no wait: the guest starts the next frame while the workers finish this one
  if (png && (d->frames <= 3 || (every && d->frames % every == 0) || (maxf && d->frames >= maxf))) {
    char p[1300]; snprintf(p, sizeof p, "%s/frame%05d.png", dir, d->frames);
    png_write_argb(p, (u32 *)P(bb->mem), bb->w, bb->h, bb->pitch / 4, 1);
    char l[1300]; snprintf(l, sizeof l, "%s/latest.png", dir); png_write_argb(l, (u32 *)P(bb->mem), bb->w, bb->h, bb->pitch / 4, 1);
  }
  if (d->frames <= 3 || (stat && d->frames % stat == 0) || (maxf && d->frames >= maxf)) {
    uint64_t rc = 0; for (int i = 0; i < 32; i++) rc += rcpu_ns[i];
    gw_log("Present #%d (draws so far %ld, tris %ld, pix %ld, t=%ums cpu=%ums main=%ums raster=%ums wait=%ums)", d->frames, g_draws, g_tris, g_pix, wall_ms(), cpu_ms(),
           (u32)(tcpu_ns() / 1000000), (u32)(rc / 1000000), (u32)(g_rq_wait_ns / 1000000));
    if (d->frames == 3 || d->frames % 100 == 0) { u32 cw = 0; uc_reg_read(uc, UC_X86_REG_FPCW, &cw); gw_log("guest x87 control word %04x", cw); }
    extern void thread_stats(void); thread_stats(); census_log();
    extern void shim_stats_dump(int frames); static int lastf; shim_stats_dump(d->frames - lastf); lastf = d->frames;
  }
  if (shot_req[0]) { char p[1400]; snprintf(p, sizeof p, "%s/%s.png", dir, shot_req); png_write_argb(p, (u32 *)P(bb->mem), bb->w, bb->h, bb->pitch / 4, 1);
    if (getenv("GW_FRONTSHOT")) { snprintf(p, sizeof p, "%s/%s-front.png", dir, shot_req); png_write_argb(p, g_front, g_front_w, g_front_h, g_front_w, 1); }
    gw_log("shot %s at frame %d (t=%ums)", p, d->frames, wall_ms()); shot_req[0] = 0; }
  if (maxf && d->frames >= maxf) { gw_log("GW_MAX_FRAMES reached; exiting"); fflush(LOG); exit(0); }
  { extern int g_log_buffered; g_log_buffered = 1; fflush(LOG); }   // the log is block-buffered between Presents
  return 0;
}
// ---------- texture / surface / buffer methods ----------
static u32 Tex_SetLOD(void) { return 0; }
static u32 Tex_GetLOD(void) { return 0; }
static u32 Tex_GetLevelCount(void) { return THIS->levels; }
static u32 Tex_SetAutoGenFilterType(void) { return 0; }
static u32 Tex_GetAutoGenFilterType(void) { return 2; }
static u32 Tex_GenerateMipSubLevels(void) { return 0; }
static void surf_desc(Obj *s, u32 p) { w32(p, s->fmt); w32(p + 4, 1); w32(p + 8, s->usage); w32(p + 12, s->container ? s->container->pool : 0); w32(p + 16, 0); w32(p + 20, 0); w32(p + 24, s->w); w32(p + 28, s->h); }
static u32 Tex_GetLevelDesc(void) { Obj *t = THIS; u32 l = ARG(1); if (l >= (u32)t->levels) return D3DERR_INVALIDCALL; surf_desc(t->lv[l], ARG(2)); return 0; }
static u32 Tex_GetSurfaceLevel(void) { Obj *t = THIS; u32 l = ARG(1); if (l >= (u32)t->levels) return D3DERR_INVALIDCALL; t->lv[l]->ref++; w32(ARG(2), t->lv[l]->g); return 0; }
static u32 lock_surface(Obj *s, u32 lr, u32 rect) {
  obj_sync(s); if (s->container && s->container->lv[0]) obj_sync(s->container->lv[0]);
  u32 off = 0;
  if (rect) { int x = r32(rect), y = r32(rect + 4); if (is_dxt(s->fmt)) off = (y / 4) * s->pitch + (x / 4) * (s->fmt == F_DXT1 ? 8 : 16); else off = y * s->pitch + x * (fmt_bpp(s->fmt) / 8); }
  w32(lr, s->pitch); w32(lr + 4, s->mem + off); s->dirty = 1; s->mcpu = 1; return 0;
}
static u32 Tex_LockRect(void) { Obj *t = THIS; u32 l = ARG(1); if (l >= (u32)t->levels) return D3DERR_INVALIDCALL; return lock_surface(t->lv[l], ARG(2), ARG(3)); }
static u32 Tex_UnlockRect(void) { Obj *t = THIS; u32 l = ARG(1); if (l < (u32)t->levels) t->lv[l]->dirty = 1; return 0; }
static u32 Tex_AddDirtyRect(void) { return 0; }
static u32 Cube_GetLevelDesc(void) { Obj *t = THIS; surf_desc(t->lv[ARG(1)], ARG(2)); return 0; }
static u32 Cube_GetCubeMapSurface(void) { Obj *t = THIS; Obj *s = t->lv[ARG(1) * 16 + ARG(2)]; s->ref++; w32(ARG(3), s->g); return 0; }
static u32 Cube_LockRect(void) { Obj *t = THIS; return lock_surface(t->lv[ARG(1) * 16 + ARG(2)], ARG(3), ARG(4)); }
static u32 Cube_UnlockRect(void) { Obj *t = THIS; t->lv[ARG(1) * 16 + ARG(2)]->dirty = 1; return 0; }
static u32 Cube_AddDirtyRect(void) { return 0; }
static u32 Surf_GetContainer(void) { Obj *s = THIS; u32 c = s->container ? s->container->g : g_dev->g; w32(ARG(2), c); return s->container ? 0 : 0; }
static u32 Surf_GetDesc(void) { surf_desc(THIS, ARG(1)); return 0; }
static u32 Surf_LockRect(void) { return lock_surface(THIS, ARG(1), ARG(2)); }
static u32 Surf_UnlockRect(void) { THIS->dirty = 1; return 0; }
static u32 Surf_GetDC(void) { gw_log("Surface::GetDC unsupported"); return D3DERR_INVALIDCALL; }
static u32 Surf_ReleaseDC(void) { return 0; }
static u32 Buf_Lock(void) { Obj *b = THIS; w32(ARG(3), b->mem + ARG(1)); return 0; }
static u32 Buf_Unlock(void) { return 0; }
static u32 Buf_GetDesc(void) { Obj *b = THIS; u32 p = ARG(1); w32(p, b->kind == K_IB ? b->fmt : 100); w32(p + 4, b->kind == K_IB ? 7 : 6); w32(p + 8, b->usage); w32(p + 12, b->pool); w32(p + 16, b->bsize); if (b->kind == K_VB) w32(p + 20, b->fmt); return 0; }
static u32 Decl_GetDeclaration(void) { Obj *v = THIS; w32(ARG(2), v->ndecl + 1); if (ARG(1)) memcpy(P(ARG(1)), v->decl, 8 * (v->ndecl + 1)); return 0; }
static u32 Shader_GetFunction(void) { Obj *v = THIS; w32(ARG(2), v->bsize); if (ARG(1)) memcpy(P(ARG(1)), P(v->code), v->bsize); return 0; }
static u32 Query_GetType(void) { return THIS->fmt; }
static u32 Query_GetDataSize(void) { return 4; }
static u32 Query_Issue(void) { return 0; }
static u32 Query_GetData(void) { if (ARG(1) && ARG(2) >= 4) w32(ARG(1), 1); return 0; }

// ---------- vtables ----------
#define N(a) (int)(sizeof(a) / sizeof(a[0]))
static u32 SB_GetDevice(void) { return Res_GetDevice(); }
void d3d_init_vtables(void) {
  static const VDef d3d[] = {UNK, {"RegisterSoftwareDevice", 2, NULL}, {"GetAdapterCount", 1, D3D_GetAdapterCount}, {"GetAdapterIdentifier", 4, D3D_GetAdapterIdentifier},
    {"GetAdapterModeCount", 3, D3D_GetAdapterModeCount}, {"EnumAdapterModes", 5, D3D_EnumAdapterModes}, {"GetAdapterDisplayMode", 3, D3D_GetAdapterDisplayMode},
    {"CheckDeviceType", 6, D3D_CheckDeviceType}, {"CheckDeviceFormat", 7, D3D_CheckDeviceFormat}, {"CheckDeviceMultiSampleType", 7, D3D_CheckDeviceMultiSampleType},
    {"CheckDepthStencilMatch", 6, D3D_CheckDepthStencilMatch}, {"CheckDeviceFormatConversion", 5, D3D_CheckDeviceFormatConversion}, {"GetDeviceCaps", 4, D3D_GetDeviceCaps},
    {"GetAdapterMonitor", 2, D3D_GetAdapterMonitor}, {"CreateDevice", 7, D3D_CreateDevice}};
  vt_d3d = make_vtbl("IDirect3D9", d3d, N(d3d));
  static const VDef dev[] = {UNK, {"TestCooperativeLevel", 1, Dev_TestCooperativeLevel}, {"GetAvailableTextureMem", 1, Dev_GetAvailableTextureMem},
    {"EvictManagedResources", 1, Dev_EvictManagedResources}, {"GetDirect3D", 2, Dev_GetDirect3D}, {"GetDeviceCaps", 2, Dev_GetDeviceCaps}, {"GetDisplayMode", 3, Dev_GetDisplayMode},
    {"GetCreationParameters", 2, Dev_GetCreationParameters}, {"SetCursorProperties", 4, Dev_SetCursorProperties}, {"SetCursorPosition", 4, Dev_SetCursorPosition},
    {"ShowCursor", 2, Dev_ShowCursor}, {"CreateAdditionalSwapChain", 3, NULL}, {"GetSwapChain", 3, NULL}, {"GetNumberOfSwapChains", 1, Dev_GetNumberOfSwapChains},
    {"Reset", 2, Dev_Reset}, {"Present", 5, Dev_Present}, {"GetBackBuffer", 5, Dev_GetBackBuffer}, {"GetRasterStatus", 3, Dev_GetRasterStatus},
    {"SetDialogBoxMode", 2, Dev_SetDialogBoxMode}, {"SetGammaRamp", 4, Dev_SetGammaRamp}, {"GetGammaRamp", 3, Dev_GetGammaRamp}, {"CreateTexture", 9, Dev_CreateTexture},
    {"CreateVolumeTexture", 10, Dev_CreateVolumeTexture}, {"CreateCubeTexture", 8, Dev_CreateCubeTexture}, {"CreateVertexBuffer", 7, Dev_CreateVertexBuffer},
    {"CreateIndexBuffer", 7, Dev_CreateIndexBuffer}, {"CreateRenderTarget", 9, Dev_CreateRenderTarget}, {"CreateDepthStencilSurface", 9, Dev_CreateDepthStencilSurface},
    {"UpdateSurface", 5, Dev_UpdateSurface}, {"UpdateTexture", 3, Dev_UpdateTexture}, {"GetRenderTargetData", 3, Dev_GetRenderTargetData},
    {"GetFrontBufferData", 3, Dev_GetFrontBufferData}, {"StretchRect", 6, Dev_StretchRect}, {"ColorFill", 4, Dev_ColorFill},
    {"CreateOffscreenPlainSurface", 7, Dev_CreateOffscreenPlainSurface}, {"SetRenderTarget", 3, Dev_SetRenderTarget}, {"GetRenderTarget", 3, Dev_GetRenderTarget},
    {"SetDepthStencilSurface", 2, Dev_SetDepthStencilSurface}, {"GetDepthStencilSurface", 2, Dev_GetDepthStencilSurface}, {"BeginScene", 1, Dev_BeginScene},
    {"EndScene", 1, Dev_EndScene}, {"Clear", 7, Dev_Clear}, {"SetTransform", 3, Dev_SetTransform}, {"GetTransform", 3, Dev_GetTransform},
    {"MultiplyTransform", 3, Dev_MultiplyTransform}, {"SetViewport", 2, Dev_SetViewport}, {"GetViewport", 2, Dev_GetViewport}, {"SetMaterial", 2, Dev_SetMaterial},
    {"GetMaterial", 2, Dev_GetMaterial}, {"SetLight", 3, Dev_SetLight}, {"GetLight", 3, Dev_GetLight}, {"LightEnable", 3, Dev_LightEnable},
    {"GetLightEnable", 3, Dev_GetLightEnable}, {"SetClipPlane", 3, Dev_SetClipPlane}, {"GetClipPlane", 3, NULL}, {"SetRenderState", 3, Dev_SetRenderState},
    {"GetRenderState", 3, Dev_GetRenderState}, {"CreateStateBlock", 3, Dev_CreateStateBlock}, {"BeginStateBlock", 1, Dev_BeginStateBlock},
    {"EndStateBlock", 2, Dev_EndStateBlock}, {"SetClipStatus", 2, NULL}, {"GetClipStatus", 2, NULL}, {"GetTexture", 3, Dev_GetTexture}, {"SetTexture", 3, Dev_SetTexture},
    {"GetTextureStageState", 4, Dev_GetTextureStageState}, {"SetTextureStageState", 4, Dev_SetTextureStageState}, {"GetSamplerState", 4, Dev_GetSamplerState},
    {"SetSamplerState", 4, Dev_SetSamplerState}, {"ValidateDevice", 2, Dev_ValidateDevice}, {"SetPaletteEntries", 3, NULL}, {"GetPaletteEntries", 3, NULL},
    {"SetCurrentTexturePalette", 2, NULL}, {"GetCurrentTexturePalette", 2, NULL}, {"SetScissorRect", 2, Dev_SetScissorRect}, {"GetScissorRect", 2, Dev_GetScissorRect},
    {"SetSoftwareVertexProcessing", 2, Dev_SetSoftwareVertexProcessing}, {"GetSoftwareVertexProcessing", 1, Dev_GetSoftwareVertexProcessing},
    {"SetNPatchMode", 2, Dev_SetNPatchMode}, {"GetNPatchMode", 1, NULL}, {"DrawPrimitive", 4, Dev_DrawPrimitive}, {"DrawIndexedPrimitive", 7, Dev_DrawIndexedPrimitive},
    {"DrawPrimitiveUP", 5, Dev_DrawPrimitiveUP}, {"DrawIndexedPrimitiveUP", 9, Dev_DrawIndexedPrimitiveUP}, {"ProcessVertices", 7, NULL},
    {"CreateVertexDeclaration", 3, Dev_CreateVertexDeclaration}, {"SetVertexDeclaration", 2, Dev_SetVertexDeclaration}, {"GetVertexDeclaration", 2, Dev_GetVertexDeclaration},
    {"SetFVF", 2, Dev_SetFVF}, {"GetFVF", 2, Dev_GetFVF}, {"CreateVertexShader", 3, Dev_CreateVertexShader}, {"SetVertexShader", 2, Dev_SetVertexShader},
    {"GetVertexShader", 2, Dev_GetVertexShader}, {"SetVertexShaderConstantF", 4, Dev_SetVertexShaderConstantF}, {"GetVertexShaderConstantF", 4, Dev_GetVertexShaderConstantF},
    {"SetVertexShaderConstantI", 4, Dev_SetConstIB}, {"GetVertexShaderConstantI", 4, NULL}, {"SetVertexShaderConstantB", 4, Dev_SetConstIB}, {"GetVertexShaderConstantB", 4, NULL},
    {"SetStreamSource", 5, Dev_SetStreamSource}, {"GetStreamSource", 5, Dev_GetStreamSource}, {"SetStreamSourceFreq", 3, Dev_SetStreamSourceFreq}, {"GetStreamSourceFreq", 3, NULL},
    {"SetIndices", 2, Dev_SetIndices}, {"GetIndices", 2, Dev_GetIndices}, {"CreatePixelShader", 3, Dev_CreatePixelShader}, {"SetPixelShader", 2, Dev_SetPixelShader},
    {"GetPixelShader", 2, Dev_GetPixelShader}, {"SetPixelShaderConstantF", 4, Dev_SetPixelShaderConstantF}, {"GetPixelShaderConstantF", 4, NULL},
    {"SetPixelShaderConstantI", 4, Dev_SetConstIB}, {"GetPixelShaderConstantI", 4, NULL}, {"SetPixelShaderConstantB", 4, Dev_SetConstIB}, {"GetPixelShaderConstantB", 4, NULL},
    {"DrawRectPatch", 4, NULL}, {"DrawTriPatch", 4, NULL}, {"DeletePatch", 2, NULL}, {"CreateQuery", 3, Dev_CreateQuery}};
  if (N(dev) != 119) gw_fatal("device vtable has %d entries", N(dev));
  vt_dev = make_vtbl("IDirect3DDevice9", dev, N(dev));
  static const VDef tex[] = {RES, {"SetLOD", 2, Tex_SetLOD}, {"GetLOD", 1, Tex_GetLOD}, {"GetLevelCount", 1, Tex_GetLevelCount}, {"SetAutoGenFilterType", 2, Tex_SetAutoGenFilterType},
    {"GetAutoGenFilterType", 1, Tex_GetAutoGenFilterType}, {"GenerateMipSubLevels", 1, Tex_GenerateMipSubLevels}, {"GetLevelDesc", 3, Tex_GetLevelDesc},
    {"GetSurfaceLevel", 3, Tex_GetSurfaceLevel}, {"LockRect", 5, Tex_LockRect}, {"UnlockRect", 2, Tex_UnlockRect}, {"AddDirtyRect", 2, Tex_AddDirtyRect}};
  vt_tex = make_vtbl("IDirect3DTexture9", tex, N(tex));
  static const VDef cube[] = {RES, {"SetLOD", 2, Tex_SetLOD}, {"GetLOD", 1, Tex_GetLOD}, {"GetLevelCount", 1, Tex_GetLevelCount}, {"SetAutoGenFilterType", 2, Tex_SetAutoGenFilterType},
    {"GetAutoGenFilterType", 1, Tex_GetAutoGenFilterType}, {"GenerateMipSubLevels", 1, Tex_GenerateMipSubLevels}, {"GetLevelDesc", 3, Cube_GetLevelDesc},
    {"GetCubeMapSurface", 4, Cube_GetCubeMapSurface}, {"LockRect", 6, Cube_LockRect}, {"UnlockRect", 3, Cube_UnlockRect}, {"AddDirtyRect", 3, Cube_AddDirtyRect}};
  vt_cube = make_vtbl("IDirect3DCubeTexture9", cube, N(cube));
  static const VDef surf[] = {RES, {"GetContainer", 3, Surf_GetContainer}, {"GetDesc", 2, Surf_GetDesc}, {"LockRect", 4, Surf_LockRect}, {"UnlockRect", 1, Surf_UnlockRect},
    {"GetDC", 2, Surf_GetDC}, {"ReleaseDC", 2, Surf_ReleaseDC}};
  vt_surf = make_vtbl("IDirect3DSurface9", surf, N(surf));
  static const VDef vb[] = {RES, {"Lock", 5, Buf_Lock}, {"Unlock", 1, Buf_Unlock}, {"GetDesc", 2, Buf_GetDesc}};
  vt_vb = make_vtbl("IDirect3DVertexBuffer9", vb, N(vb));
  vt_ib = make_vtbl("IDirect3DIndexBuffer9", vb, N(vb));
  static const VDef sb[] = {UNK, {"GetDevice", 2, SB_GetDevice}, {"Capture", 1, SB_Capture}, {"Apply", 1, SB_Apply}};
  vt_sb = make_vtbl("IDirect3DStateBlock9", sb, N(sb));
  static const VDef vd[] = {UNK, {"GetDevice", 2, SB_GetDevice}, {"GetDeclaration", 3, Decl_GetDeclaration}};
  vt_vdecl = make_vtbl("IDirect3DVertexDeclaration9", vd, N(vd));
  static const VDef sh[] = {UNK, {"GetDevice", 2, SB_GetDevice}, {"GetFunction", 3, Shader_GetFunction}};
  vt_vs = make_vtbl("IDirect3DVertexShader9", sh, N(sh)); vt_ps = make_vtbl("IDirect3DPixelShader9", sh, N(sh));
  static const VDef q[] = {UNK, {"GetDevice", 2, SB_GetDevice}, {"GetType", 1, Query_GetType}, {"GetDataSize", 1, Query_GetDataSize}, {"Issue", 2, Query_Issue}, {"GetData", 4, Query_GetData}};
  vt_query = make_vtbl("IDirect3DQuery9", q, N(q));
}
SHIM(D3D9, Direct3DCreate9, 1) { gw_log("Direct3DCreate9(%u)", ARG(0)); Obj *o = new_obj(K_D3D, vt_d3d); return o->g; }

// ---------- per-frame host hook (input injection / limits) ----------
void host_frame_hook(void) {}

// GW_SELFTEST=prims: rasterises a point (POINTSIZE 4), a line and a table-fogged triangle into a 64x64 target
// through draw_common (pre-transformed XYZRHW|DIFFUSE vertices) and checks pixels. Writes shots/selftest-prims.png.
int d3d_selftest_prims(void) {
  Obj *d = new_obj(K_DEV, vt_dev); g_dev = d; d->pp[0] = 64; d->pp[1] = 64;
  d->bb = new_surface(64, 64, F_A8R8G8B8, NULL, 0); d->rt = d->bb; dev_defaults(d);
  State *s = &d->cur; s->fvf = 0x004 | 0x040; s->rs[137] = 0; s->rs[22] = 1;   // XYZRHW | DIFFUSE, no culling
  memset(P(d->bb->mem), 0, (size_t)d->bb->size);
  typedef struct { float x, y, z, rhw; u32 c; } V;
  float four = 4.f; memcpy(&s->rs[154], &four, 4);
  V pt[1] = {{10, 10, 0.5f, 1, 0xFFFF0000}};
  draw_common(s, 1, 1, 0, 0, 1, NULL, 0, (const u8 *)pt, sizeof(V));
  V ln[2] = {{20, 5, 0.5f, 1, 0xFF00FF00}, {60, 5, 0.5f, 1, 0xFF00FF00}};
  draw_common(s, 2, 1, 0, 0, 2, NULL, 0, (const u8 *)ln, sizeof(V));
  s->rs[28] = 1; s->rs[35] = 3; s->rs[34] = 0x000000FF; float f0 = 0, f1 = 1; memcpy(&s->rs[36], &f0, 4); memcpy(&s->rs[37], &f1, 4);
  V tri[3] = {{0, 20, 0.5f, 1, 0xFFFFFFFF}, {64, 20, 0.5f, 1, 0xFFFFFFFF}, {0, 64, 0.5f, 1, 0xFFFFFFFF}};
  draw_common(s, 4, 1, 0, 0, 3, NULL, 0, (const u8 *)tri, sizeof(V));
  s->rs[28] = 0;
  rq_sync();
  const u32 *px = (const u32 *)P(d->bb->mem); int W = 64;
  u32 p_pt = px[10 * W + 10] & 0xFFFFFF, p_out = px[2 * W + 2] & 0xFFFFFF, p_ln = px[5 * W + 40] & 0xFFFFFF, p_lo = px[8 * W + 40] & 0xFFFFFF, p_fog = px[30 * W + 10] & 0xFFFFFF;
  int r = (p_fog >> 16) & 255, g = (p_fog >> 8) & 255, b = p_fog & 255;
  int ok_pt = p_pt == 0xFF0000 && p_out == 0, ok_ln = p_ln == 0x00FF00 && p_lo == 0, ok_fog = r >= 125 && r <= 130 && g >= 125 && g <= 130 && b == 255;
  char p[1200]; snprintf(p, sizeof p, "%s/shots", g_run_dir); mkdir(p, 0755); snprintf(p, sizeof p, "%s/shots/selftest-prims.png", g_run_dir);
  png_write_argb(p, (u32 *)px, 64, 64, 64, 1);
  gw_log("selftest prims: point %s (%06x, outside %06x), line %s (%06x, off-line %06x), table fog %s (%06x, want about 8080ff = half white, half blue)",
    ok_pt ? "PASS" : "FAIL", p_pt, p_out, ok_ln ? "PASS" : "FAIL", p_ln, p_lo, ok_fog ? "PASS" : "FAIL", p_fog);
  fprintf(stderr, "selftest prims: point %s, line %s, fog %s\n", ok_pt ? "PASS" : "FAIL", ok_ln ? "PASS" : "FAIL", ok_fog ? "PASS" : "FAIL");
  return ok_pt && ok_ln && ok_fog ? 0 : 1;
}
