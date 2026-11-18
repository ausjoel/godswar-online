// D3D9 vertex shader interpreter (vs_1_1 .. vs_2_0 arithmetic subset, a0 relative addressing).
// Enough for the game's GPU skinning shader gpu_vs_sm.code (tools/vsdis.py disassembles it); unknown
// opcodes are logged once and make the draw fall back to "skipped".
#include "gw.h"
#include <math.h>

typedef struct { u16 op; u8 ndst, nsrc; u32 dst; u32 dmask, dsat; u32 src[4]; u32 rel[4]; int hasrel[4]; } VIns;
typedef struct VSProg {
  int major; VIns *ins; int nins; int ok;
  int ndcl; struct { int usage, idx, reg; } dcl[16];
  int ndef; struct { int reg; float v[4]; } def[64];
  float c[256][4];   // constants for the current draw (vsc + defs)
} VSProg;

static int regtype(u32 t) { return ((t >> 28) & 7) | ((t >> 8) & 0x18); }
static int nsrc_of(int op) {
  switch (op) {
    case 1: case 6: case 7: case 14: case 15: case 16: case 19: case 35: case 36: case 46: case 34: case 78: case 79: case 37: return 1;
    case 2: case 3: case 5: case 8: case 9: case 10: case 11: case 12: case 13: case 17: case 20: case 21: case 22: case 23: case 24: case 32: case 33: return 2;
    case 4: case 18: return 3;
    default: return -1;
  }
}
void *vs_compile(u32 code, u32 nbytes) {
  const u32 *w = (const u32 *)P(code); u32 n = nbytes / 4;
  VSProg *p = calloc(1, sizeof *p); p->major = (w[0] >> 8) & 0xFF; p->ok = 1;
  p->ins = calloc(n, sizeof(VIns));
  for (u32 i = 1; i < n;) {
    u32 t = w[i], op = t & 0xFFFF;
    if (op == 0xFFFF) break;
    if (op == 0xFFFE) { i += 1 + (t >> 16); continue; }
    u32 len = p->major >= 2 ? (t >> 24) & 15 : 0;
    if (p->major < 2) {   // vs_1_x: no length field; derive from opcode
      if (op == 31) len = 2; else if (op == 81) len = 5; else { int ns = nsrc_of(op); len = ns < 0 ? 0 : 1 + ns; }
    }
    const u32 *a = &w[i + 1];
    if (op == 31) { if (p->ndcl < 16) { p->dcl[p->ndcl].usage = a[0] & 31; p->dcl[p->ndcl].idx = (a[0] >> 16) & 15; p->dcl[p->ndcl].reg = a[1] & 0x7FF; p->ndcl++; } }
    else if (op == 81) { if (p->ndef < 64) { p->def[p->ndef].reg = a[0] & 0x7FF; memcpy(p->def[p->ndef].v, &a[1], 16); p->ndef++; } }
    else if (op == 47 || op == 48 || op == 0) { }
    else {
      int ns = nsrc_of(op);
      if (ns < 0) { gw_log("VS: unsupported opcode %u", op); p->ok = 0; }
      else {
        VIns *I = &p->ins[p->nins++]; I->op = op; I->dst = a[0]; I->dmask = (a[0] >> 16) & 15; I->dsat = (a[0] >> 20) & 1; I->nsrc = ns;
        u32 k = 1;
        for (int s = 0; s < ns; s++) {
          I->src[s] = a[k]; I->hasrel[s] = 0;
          if (a[k] & (1u << 13)) { I->hasrel[s] = 1; if (p->major >= 2) { I->rel[s] = a[k + 1]; k++; } else I->rel[s] = 0xB0000000u | (0 << 16); }   // vs1: a0.x
          k++;
        }
      }
    }
    i += 1 + len;
  }
  gw_log("VS compiled: vs_%d_x, %d instructions, %d inputs, %d defs%s", p->major, p->nins, p->ndcl, p->ndef, p->ok ? "" : " (UNSUPPORTED ops)");
  return p;
}
int vs_ok(void *pp) { return pp && ((VSProg *)pp)->ok; }
int vs_input_reg(void *pp, int usage, int idx) { VSProg *p = pp; for (int i = 0; i < p->ndcl; i++) if (p->dcl[i].usage == usage && p->dcl[i].idx == idx) return p->dcl[i].reg; return -1; }
void vs_begin(void *pp, float (*vsc)[4]) { VSProg *p = pp; memcpy(p->c, vsc, sizeof p->c); for (int i = 0; i < p->ndef; i++) if (p->def[i].reg < 256) memcpy(p->c[p->def[i].reg], p->def[i].v, 16); }

typedef struct { float r[32][4], v[16][4], o[16][4], a[4]; } VRegs;   // o: 0 oPos 1 oFog 2 oPts, 4/5 oD0/oD1, 8.. oT0..7
static const float zero4[4];
static void fetch(VSProg *p, VRegs *R, const VIns *I, int s, float out[4]) {
  u32 t = I->src[s]; int ty = regtype(t), n = t & 0x7FF; const float *v = zero4;
  if (I->hasrel[s]) { u32 rt = I->rel[s]; int comp = (rt >> 16) & 3; n += (int)R->a[comp]; }
  switch (ty) { case 0: v = n < 32 ? R->r[n] : zero4; break; case 1: v = n < 16 ? R->v[n] : zero4; break;
    case 2: v = (n >= 0 && n < 256) ? p->c[n] : zero4; break; case 3: v = R->a; break; default: v = zero4; }
  u32 sw = (t >> 16) & 255;
  for (int i = 0; i < 4; i++) out[i] = v[(sw >> (2 * i)) & 3];
  u32 mod = (t >> 24) & 15;
  if (mod == 1) for (int i = 0; i < 4; i++) out[i] = -out[i];
  else if (mod == 11) for (int i = 0; i < 4; i++) out[i] = fabsf(out[i]);
  else if (mod == 12) for (int i = 0; i < 4; i++) out[i] = -fabsf(out[i]);
}
static float *dstp(VRegs *R, u32 t) {
  int ty = regtype(t), n = t & 0x7FF;
  switch (ty) { case 0: return R->r[n & 31]; case 3: return R->a; case 4: return R->o[n & 3]; case 5: return R->o[4 + (n & 1)]; case 6: return R->o[8 + (n & 7)]; default: return R->o[15]; }
}
static float dp(const float *a, const float *b, int n) { float s = 0; for (int i = 0; i < n; i++) s += a[i] * b[i]; return s; }
// in[16][4]: values of input registers v0..v15. out: pos[4], d0[4], d1[4], t[8][4]
float vs_fog;   // oFog.x of the last vs_run (emulator thread only)
void vs_run(void *pp, float in[16][4], float pos[4], float d0[4], float d1[4], float t[8][4]) {
  VSProg *p = pp; VRegs R; memset(R.r, 0, sizeof R.r); memcpy(R.v, in, sizeof R.v); memset(R.o, 0, sizeof R.o); memset(R.a, 0, sizeof R.a);
  for (int k = 0; k < 2; k++) R.o[4 + k][0] = R.o[4 + k][1] = R.o[4 + k][2] = R.o[4 + k][3] = k == 0 ? 1.f : 0.f;
  R.o[1][0] = 1.f;   // oFog default: no fog when the shader does not write it
  for (int i = 0; i < p->nins; i++) {
    const VIns *I = &p->ins[i]; float s0[4], s1[4], s2[4], res[4] = {0, 0, 0, 0};
    if (I->nsrc > 0) fetch(p, &R, I, 0, s0); if (I->nsrc > 1) fetch(p, &R, I, 1, s1); if (I->nsrc > 2) fetch(p, &R, I, 2, s2);
    switch (I->op) {
      case 1: case 46: memcpy(res, s0, 16); break;
      case 2: for (int k = 0; k < 4; k++) res[k] = s0[k] + s1[k]; break;
      case 3: for (int k = 0; k < 4; k++) res[k] = s0[k] - s1[k]; break;
      case 4: for (int k = 0; k < 4; k++) res[k] = s0[k] * s1[k] + s2[k]; break;
      case 5: for (int k = 0; k < 4; k++) res[k] = s0[k] * s1[k]; break;
      case 6: { float v = s0[3]; res[0] = res[1] = res[2] = res[3] = v == 0 ? 3.4e38f : 1.f / v; break; }
      case 7: { float v = fabsf(s0[3]); res[0] = res[1] = res[2] = res[3] = v == 0 ? 3.4e38f : 1.f / sqrtf(v); break; }
      case 8: res[0] = res[1] = res[2] = res[3] = dp(s0, s1, 3); break;
      case 9: res[0] = res[1] = res[2] = res[3] = dp(s0, s1, 4); break;
      case 10: for (int k = 0; k < 4; k++) res[k] = s0[k] < s1[k] ? s0[k] : s1[k]; break;
      case 11: for (int k = 0; k < 4; k++) res[k] = s0[k] > s1[k] ? s0[k] : s1[k]; break;
      case 12: for (int k = 0; k < 4; k++) res[k] = s0[k] < s1[k] ? 1.f : 0.f; break;
      case 13: for (int k = 0; k < 4; k++) res[k] = s0[k] >= s1[k] ? 1.f : 0.f; break;
      case 14: case 78: res[0] = res[1] = res[2] = res[3] = exp2f(s0[3]); break;
      case 15: case 79: { float v = fabsf(s0[3]); res[0] = res[1] = res[2] = res[3] = v == 0 ? -3.4e38f : log2f(v); break; }
      case 16: { float nl = s0[0], nh = s0[1], e = s0[3]; if (e < -127.9961f) e = -127.9961f; if (e > 127.9961f) e = 127.9961f;
        res[0] = 1; res[1] = nl > 0 ? nl : 0; res[2] = (nl > 0 && nh > 0) ? powf(nh, e) : 0; res[3] = 1; break; }
      case 17: res[0] = 1; res[1] = s0[1] * s1[1]; res[2] = s0[2]; res[3] = s1[3]; break;
      case 18: for (int k = 0; k < 4; k++) res[k] = s2[k] + s0[k] * (s1[k] - s2[k]); break;
      case 19: for (int k = 0; k < 4; k++) res[k] = s0[k] - floorf(s0[k]); break;
      case 20: case 21: case 22: case 23: case 24: {
        int rows = I->op == 20 ? 4 : I->op == 21 ? 3 : I->op == 22 ? 4 : I->op == 23 ? 3 : 2, cols = (I->op == 20 || I->op == 21) ? 4 : 3;
        VIns J = *I;
        for (int r = 0; r < rows; r++) { float m[4]; J.src[1] = I->src[1] + r; fetch(p, &R, &J, 1, m); res[r] = dp(s0, m, cols); }
        break; }
      case 32: { float v = powf(fabsf(s0[0]), s1[0]); res[0] = res[1] = res[2] = res[3] = v; break; }
      case 33: res[0] = s0[1] * s1[2] - s0[2] * s1[1]; res[1] = s0[2] * s1[0] - s0[0] * s1[2]; res[2] = s0[0] * s1[1] - s0[1] * s1[0]; break;
      case 34: for (int k = 0; k < 4; k++) res[k] = s0[k] > 0 ? 1.f : s0[k] < 0 ? -1.f : 0.f; break;
      case 35: for (int k = 0; k < 4; k++) res[k] = fabsf(s0[k]); break;
      case 36: { float l = sqrtf(dp(s0, s0, 3)); float il = l > 0 ? 1.f / l : 0; for (int k = 0; k < 4; k++) res[k] = s0[k] * il; break; }
      case 37: res[0] = cosf(s0[0]); res[1] = sinf(s0[0]); break;
    }
    float *d = dstp(&R, I->dst);
    if (I->op == 46 || (regtype(I->dst) == 3)) {   // mova / write to a0: round to nearest (vs_2_0), floor for vs_1_1 mov
      for (int k = 0; k < 4; k++) if (I->dmask >> k & 1) d[k] = p->major >= 2 ? floorf(res[k] + 0.5f) : floorf(res[k]);
      continue;
    }
    for (int k = 0; k < 4; k++) if (I->dmask >> k & 1) { float v = res[k]; if (I->dsat) v = v < 0 ? 0 : v > 1 ? 1 : v; d[k] = v; }
  }
  memcpy(pos, R.o[0], 16); memcpy(d0, R.o[4], 16); memcpy(d1, R.o[5], 16); vs_fog = R.o[1][0];
  for (int k = 0; k < 8; k++) memcpy(t[k], R.o[8 + k], 16);
}
