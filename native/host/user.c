// USER32 / GDI32 / ADVAPI32 / OLE / misc shims. No real windows: a window is a host record.
#include "gw.h"
#include <strings.h>
#include <unistd.h>

u32 g_hwnd_main;
extern u32 res_lookup(u32 base, u32 type, u32 name, u32 *size);

// ---------- printf-style formatter over guest varargs (wsprintf) ----------
static int gfmt(int wide, u32 fmt, u32 argp, char *out, size_t cap) {
  size_t o = 0; u32 i = 0;
#define FCH(k) (wide ? r16(fmt + 2 * (k)) : r8(fmt + (k)))
#define PUT(c) do { if (o + 1 < cap) out[o++] = (char)(c); } while (0)
  for (;;) {
    u32 c = FCH(i++); if (!c) break;
    if (c != '%') { if (c < 0x80) PUT(c); else { char tmp[8]; u16 w = c; int n = wc2mb(65001, &w, 1, (u8 *)tmp, 8); for (int k = 0; k < n; k++) PUT(tmp[k]); } continue; }
    char spec[32]; int sl = 0; spec[sl++] = '%';
    for (;;) { c = FCH(i); if (c && strchr("-+ #0", (int)c)) { spec[sl++] = c; i++; } else break; }
    while ((c = FCH(i)) >= '0' && c <= '9') { spec[sl++] = c; i++; }
    if (FCH(i) == '.') { spec[sl++] = '.'; i++; while ((c = FCH(i)) >= '0' && c <= '9') { spec[sl++] = c; i++; } }
    int lng = 0, hsh = 0; c = FCH(i);
    if (c == 'l') { lng = 1; i++; } else if (c == 'h') { hsh = 1; i++; } else if (c == 'I' && FCH(i + 1) == '6' && FCH(i + 2) == '4') { lng = 2; i += 3; }
    c = FCH(i++); char buf[512];
    if (c == '%') { PUT('%'); continue; }
    if (c == 'd' || c == 'i' || c == 'u' || c == 'x' || c == 'X' || c == 'o') {
      if (lng == 2) { uint64_t v = r32(argp) | ((uint64_t)r32(argp + 4) << 32); argp += 8; spec[sl++] = 'l'; spec[sl++] = 'l'; spec[sl++] = c; spec[sl] = 0; snprintf(buf, sizeof buf, spec, v); }
      else { u32 v = r32(argp); argp += 4; spec[sl++] = c; spec[sl] = 0; if (c == 'd' || c == 'i') snprintf(buf, sizeof buf, spec, (i32)v); else snprintf(buf, sizeof buf, spec, v); }
    } else if (c == 'c' || c == 'C') { u32 v = r32(argp); argp += 4; buf[0] = (char)v; buf[1] = 0; }
    else if (c == 's' || c == 'S') {
      u32 p = r32(argp); argp += 4; int ws = wide ? (c == 's' && !hsh) : (c == 'S' || lng);
      char *s = !p ? strdup("(null)") : ws ? g_w2u(p) : g_a2u(p); spec[sl++] = 's'; spec[sl] = 0; snprintf(buf, sizeof buf, spec, s); free(s);
    } else if (c == 'p') { snprintf(buf, sizeof buf, "%08X", r32(argp)); argp += 4; }
    else if (c == 'f' || c == 'g' || c == 'e') { double d; memcpy(&d, P(argp), 8); argp += 8; spec[sl++] = c; spec[sl] = 0; snprintf(buf, sizeof buf, spec, d); }
    else { buf[0] = 0; }
    for (char *q = buf; *q; q++) PUT(*q);
  }
  out[o] = 0; return (int)o;
}
SHIM(USER32, wsprintfW, 0) { char b[4096]; gfmt(1, ARG(1), g_esp + 12, b, sizeof b); return u2w(b, ARG(0), 1024) - 1; }
SHIM(USER32, wsprintfA, 0) { char b[4096]; gfmt(0, ARG(1), g_esp + 12, b, sizeof b); strcpy((char *)P(ARG(0)), b); return strlen(b); }

// ---------- windows ----------
typedef struct { u32 atom; char name[64]; u32 wndproc, style; } WClass;
static WClass classes[64]; static int nclasses;
typedef struct { u32 hwnd, wndproc, style, exstyle, parent, userdata, id; i32 x, y, w, h; char title[128]; int visible; u32 longs[8]; } Wnd;
static Wnd wins[64]; static int nwins;
static Wnd *wnd(u32 h) { for (int i = 0; i < nwins; i++) if (wins[i].hwnd == h) return &wins[i]; return NULL; }
u32 user_window_proc(u32 h) { Wnd *w = wnd(h); return w ? w->wndproc : 0; }
static u32 reg_class(const char *name, u32 proc, u32 style) {
  WClass *c = &classes[nclasses++]; snprintf(c->name, sizeof c->name, "%s", name); c->wndproc = proc; c->style = style; c->atom = 0xC000 + nclasses;
  gw_log("RegisterClass %s proc=%s", name, addr_sym(proc)); return c->atom;
}
static WClass *find_class(u32 namep, int wide) {
  if (namep < 0x10000) { for (int i = 0; i < nclasses; i++) if (classes[i].atom == namep) return &classes[i]; return NULL; }
  char *n = wide ? g_w2u(namep) : strdup((char *)P(namep));
  for (int i = 0; i < nclasses; i++) if (!strcasecmp(classes[i].name, n)) { free(n); return &classes[i]; }
  free(n); return NULL;
}
SHIM(USER32, RegisterClassExW, 1) { u32 p = ARG(0); char *n = r32(p + 40) >= 0x10000 ? g_w2u(r32(p + 40)) : strdup("?"); u32 a = reg_class(n, r32(p + 8), r32(p + 4)); free(n); return a; }
SHIM(USER32, RegisterClassA, 1) { u32 p = ARG(0); u32 a = reg_class(r32(p + 36) >= 0x10000 ? (char *)P(r32(p + 36)) : "?", r32(p + 4), r32(p)); return a; }
SHIM(USER32, RegisterClassW, 1) { u32 p = ARG(0); char *n = g_w2u(r32(p + 36)); u32 a = reg_class(n ? n : "?", r32(p + 4), r32(p)); free(n); return a; }
SHIM(USER32, UnregisterClassA, 2) { return 1; }
SHIM(USER32, GetClassInfoExW, 3) { WClass *c = find_class(ARG(1), 1); if (!c) return 0; u32 p = ARG(2); memset(P(p + 4), 0, 44); w32(p + 4, c->style); w32(p + 8, c->wndproc); return c->atom; }
static u32 next_hwnd = 0x00010010;
#define WM_CREATE 1
#define WM_NCCREATE 0x81
static u32 create_window(u32 ex, u32 cls, u32 title, u32 style, i32 x, i32 y, i32 w, i32 h, u32 parent, u32 menu, u32 inst, u32 param, int wide) {
  WClass *c = find_class(cls, wide);
  char *cn = cls >= 0x10000 ? (wide ? g_w2u(cls) : strdup((char *)P(cls))) : strdup("#atom");
  if (!c) { gw_log("CreateWindow: unknown class %s", cn); free(cn); return 0; }
  Wnd *W = &wins[nwins++]; memset(W, 0, sizeof *W); W->hwnd = next_hwnd; next_hwnd += 0x10;
  W->wndproc = c->wndproc; W->style = style; W->exstyle = ex; W->parent = parent; W->x = x; W->y = y; W->w = w; W->h = h; W->id = menu;
  char *t = title ? (wide ? g_w2u(title) : strdup((char *)P(title))) : strdup(""); snprintf(W->title, sizeof W->title, "%s", t);
  gw_log("CreateWindow class=%s title=%s style=%08x %dx%d at %d,%d -> %x", cn, t, style, w, h, x, y, W->hwnd); free(t); free(cn);
  if (!parent && !g_hwnd_main) g_hwnd_main = W->hwnd;
  u32 cs = heap_alloc(48, 1);
  w32(cs, param); w32(cs + 4, inst); w32(cs + 8, menu); w32(cs + 12, parent); w32(cs + 16, h); w32(cs + 20, w); w32(cs + 24, y); w32(cs + 28, x);
  w32(cs + 32, style); w32(cs + 36, title); w32(cs + 40, cls); w32(cs + 44, ex);
  u32 hw = W->hwnd, proc = W->wndproc;
  if (!gcall(proc, 4, hw, WM_NCCREATE, 0, cs)) { gw_log("WM_NCCREATE refused"); return 0; }
  if ((i32)gcall(proc, 4, hw, WM_CREATE, 0, cs) == -1) { gw_log("WM_CREATE refused"); return 0; }
  heap_free(cs);
  return hw;
}
SHIM(USER32, CreateWindowExW, 12) { return create_window(ARG(0), ARG(1), ARG(2), ARG(3), ARG(4), ARG(5), ARG(6), ARG(7), ARG(8), ARG(9), ARG(10), ARG(11), 1); }
SHIM(USER32, CreateWindowExA, 12) { return create_window(ARG(0), ARG(1), ARG(2), ARG(3), ARG(4), ARG(5), ARG(6), ARG(7), ARG(8), ARG(9), ARG(10), ARG(11), 0); }
SHIM(USER32, DestroyWindow, 1) { gw_log("DestroyWindow %x", ARG(0)); Wnd *w = wnd(ARG(0)); if (w) { u32 p = w->wndproc; gcall(p, 4, ARG(0), 2, 0, 0); } return 1; }
SHIM(USER32, ShowWindow, 2) { Wnd *w = wnd(ARG(0)); if (w) w->visible = ARG(1) != 0; return 1; }
SHIM(USER32, UpdateWindow, 1) { return 1; }
SHIM(USER32, IsWindow, 1) { return wnd(ARG(0)) != NULL; }
SHIM(USER32, IsChild, 2) { return 0; }
SHIM(USER32, GetParent, 1) { Wnd *w = wnd(ARG(0)); return w ? w->parent : 0; }
SHIM(USER32, GetWindow, 2) { return 0; }
SHIM(USER32, GetDesktopWindow, 0) { return 0x00010000; }
SHIM(USER32, GetFocus, 0) { return g_hwnd_main; }
SHIM(USER32, SetFocus, 1) { return g_hwnd_main; }
SHIM(USER32, GetActiveWindow, 0) { return g_hwnd_main; }
SHIM(USER32, GetForegroundWindow, 0) { return g_hwnd_main; }
SHIM(USER32, SetForegroundWindow, 1) { return 1; }
SHIM(USER32, SetCapture, 1) { return 0; }
SHIM(USER32, ReleaseCapture, 0) { return 1; }
SHIM(USER32, GetMenu, 1) { return 0; }
SHIM(USER32, GetDlgItem, 2) { return 0; }
SHIM(USER32, EndDialog, 2) { return 1; }
SHIM(USER32, SetWindowTextW, 2) { Wnd *w = wnd(ARG(0)); char *s = g_w2u(ARG(1)); gw_log("SetWindowTextW %s", s); if (w && s) snprintf(w->title, sizeof w->title, "%s", s); free(s); return 1; }
SHIM(USER32, GetWindowTextW, 3) { Wnd *w = wnd(ARG(0)); if (!w || !ARG(2)) return 0; return u2w(w->title, ARG(1), ARG(2)) - 1; }
SHIM(USER32, GetWindowTextLengthW, 1) { Wnd *w = wnd(ARG(0)); return w ? strlen(w->title) : 0; }
SHIM(USER32, GetClassNameW, 3) { return u2w("OLYMPLUS", ARG(1), ARG(2)) - 1; }
static u32 getlong(u32 h, i32 idx) {
  Wnd *w = wnd(h); if (!w) return 0;
  switch (idx) { case -4: return w->wndproc; case -16: return w->style; case -20: return w->exstyle; case -21: return w->userdata; case -12: return w->id; case -6: return 0x400000; case -8: return w->parent; }
  if (idx >= 0 && idx < 32) return w->longs[idx / 4];
  return 0;
}
static u32 setlong(u32 h, i32 idx, u32 v) {
  Wnd *w = wnd(h); if (!w) return 0; u32 o = getlong(h, idx);
  switch (idx) { case -4: w->wndproc = v; break; case -16: w->style = v; break; case -20: w->exstyle = v; break; case -21: w->userdata = v; break; case -12: w->id = v; break;
    default: if (idx >= 0 && idx < 32) w->longs[idx / 4] = v; }
  return o;
}
SHIM(USER32, GetWindowLongW, 2) { return getlong(ARG(0), ARG(1)); }
SHIM(USER32, GetWindowLongA, 2) { return getlong(ARG(0), ARG(1)); }
SHIM(USER32, SetWindowLongW, 3) { return setlong(ARG(0), ARG(1), ARG(2)); }
SHIM(USER32, SetWindowLongA, 3) { return setlong(ARG(0), ARG(1), ARG(2)); }
static void put_rect(u32 p, i32 l, i32 t, i32 r, i32 b) { w32(p, l); w32(p + 4, t); w32(p + 8, r); w32(p + 12, b); }
SHIM(USER32, GetClientRect, 2) { Wnd *w = wnd(ARG(0)); if (!w) { put_rect(ARG(1), 0, 0, 1920, 1080); return 1; } put_rect(ARG(1), 0, 0, w->w, w->h); return 1; }
SHIM(USER32, GetWindowRect, 2) { Wnd *w = wnd(ARG(0)); if (!w) { put_rect(ARG(1), 0, 0, 1920, 1080); return 1; } put_rect(ARG(1), w->x, w->y, w->x + w->w, w->y + w->h); return 1; }
SHIM(USER32, AdjustWindowRectEx, 4) { return 1; }   // no borders: client == window
SHIM(USER32, SetWindowPos, 7) {
  Wnd *w = wnd(ARG(0)); u32 fl = ARG(6);
  if (w) { if (!(fl & 2)) { w->x = ARG(2); w->y = ARG(3); } if (!(fl & 1)) { w->w = ARG(4); w->h = ARG(5); } }
  gw_log("SetWindowPos %x -> %d,%d %dx%d flags %x", ARG(0), w ? w->x : 0, w ? w->y : 0, w ? w->w : 0, w ? w->h : 0, fl); return 1;
}
SHIM(USER32, MoveWindow, 6) { Wnd *w = wnd(ARG(0)); if (w) { w->x = ARG(1); w->y = ARG(2); w->w = ARG(3); w->h = ARG(4); } return 1; }
SHIM(USER32, ClientToScreen, 2) { Wnd *w = wnd(ARG(0)); if (w) { w32(ARG(1), r32(ARG(1)) + w->x); w32(ARG(1) + 4, r32(ARG(1) + 4) + w->y); } return 1; }
SHIM(USER32, ScreenToClient, 2) { Wnd *w = wnd(ARG(0)); if (w) { w32(ARG(1), r32(ARG(1)) - w->x); w32(ARG(1) + 4, r32(ARG(1) + 4) - w->y); } return 1; }
i32 g_mouse_x = 512, g_mouse_y = 384; static u8 keystate[256];
static u8 vk_char[256];   // character last injected for a virtual key (exact case / symbols), used by ToAscii
SHIM(USER32, GetCursorPos, 1) { Wnd *w = wnd(g_hwnd_main); w32(ARG(0), g_mouse_x + (w ? w->x : 0)); w32(ARG(0) + 4, g_mouse_y + (w ? w->y : 0)); return 1; }
SHIM(USER32, SetCursorPos, 2) { return 1; }
SHIM(USER32, GetSystemMetrics, 1) {
  switch (ARG(0)) { case 0: return 1920; case 1: return 1080; case 16: return 1920; case 17: return 1050; case 4: return 22; case 5: case 6: return 1; case 7: case 8: return 3;
    case 32: case 33: return 4; case 11: case 12: return 32; case 13: case 14: return 32; case 78: return 1920; case 79: return 1080; case 80: return 1; default: return 0; }
}
SHIM(USER32, SystemParametersInfoW, 4) { if (ARG(0) == 0x30 && ARG(2)) put_rect(ARG(2), 0, 0, 1920, 1050); return 1; }
SHIM(USER32, GetSysColor, 1) { return 0xC0C0C0; }
SHIM(USER32, InvalidateRect, 3) { return 1; }
SHIM(USER32, InvalidateRgn, 3) { return 1; }
SHIM(USER32, RedrawWindow, 4) { return 1; }
SHIM(USER32, BeginPaint, 2) { memset(P(ARG(1)), 0, 64); w32(ARG(1), 0x0D000001); return 0x0D000001; }
SHIM(USER32, EndPaint, 2) { return 1; }
SHIM(USER32, FillRect, 3) { return 1; }
SHIM(USER32, DrawTextW, 5) { return 16; }
SHIM(USER32, GetDC, 1) { return 0x0D000001; }
SHIM(USER32, ReleaseDC, 2) { return 1; }
SHIM(USER32, SetRect, 5) { put_rect(ARG(0), ARG(1), ARG(2), ARG(3), ARG(4)); return 1; }
SHIM(USER32, MessageBoxA, 4) { gw_log("MessageBoxA: [%s] %s", ARG(2) ? (char *)P(ARG(2)) : "", ARG(1) ? (char *)P(ARG(1)) : ""); return 1; }
SHIM(USER32, MessageBoxW, 4) { char *t = g_w2u(ARG(1)), *c = g_w2u(ARG(2)); gw_log("MessageBoxW: [%s] %s", c ? c : "", t ? t : ""); free(t); free(c); return 1; }
SHIM(USER32, LoadStringW, 4) {
  u32 inst = ARG(0), id = ARG(1), buf = ARG(2); i32 n = (i32)ARG(3);
  u32 sz, blk = res_lookup(inst, 6, id / 16 + 1, &sz); if (!blk) { if (n > 0) w16(buf, 0); return 0; }
  u32 p = blk; for (u32 i = 0; i < (id & 15); i++) p += 2 + 2 * r16(p);
  u32 len = r16(p);
  if (n == 0) { w32(buf, p + 2); return len; }
  if ((i32)len >= n) len = n - 1; memcpy(P(buf), P(p + 2), 2 * len); w16(buf + 2 * len, 0);
  char *s = g_w2u(buf); gw_log("LoadStringW(%u) = %s", id, s); free(s); return len;
}
SHIM(USER32, LoadCursorFromFileA, 1) { return 0x0C000001; }
SHIM(USER32, LoadCursorW, 2) { return 0x0C000002; }
SHIM(USER32, LoadIconW, 2) { return 0x0C000003; }
SHIM(USER32, SetCursor, 1) { return 0x0C000002; }
SHIM(USER32, ShowCursor, 1) { return ARG(0) ? 1 : 0; }
SHIM(USER32, LoadAcceleratorsW, 2) { return 0x0C000004; }
SHIM(USER32, CreateAcceleratorTableW, 2) { return 0x0C000005; }
SHIM(USER32, DestroyAcceleratorTable, 1) { return 1; }
SHIM(USER32, RegisterWindowMessageW, 1) { static u32 n = 0xC100; return n++; }
SHIM(USER32, GetKeyboardState, 1) { memcpy(P(ARG(0)), keystate, 256); return 1; }
SHIM(USER32, GetKeyState, 1) { return (keystate[ARG(0) & 255] & 0x80) ? 0xFFFF8000u : 0; }
SHIM(USER32, GetAsyncKeyState, 1) { return (keystate[ARG(0) & 255] & 0x80) ? 0xFFFF8000u : 0; }
SHIM(USER32, ToAscii, 5) {
  u32 vk = ARG(0) & 255, up = ARG(1) & 0x8000; if (up) return 0;
  int ch = vk_char[vk];
  if (!ch) { if (vk >= 'A' && vk <= 'Z') ch = (keystate[0x10] & 0x80) ? (int)vk : (int)vk + 32; else if (vk >= '0' && vk <= '9') ch = vk; else if (vk == 0x20 || vk == 8 || vk == 9 || vk == 13 || vk == 27) ch = vk; }
  if (!ch) return 0; w16(ARG(3), (u16)ch); return 1;
}
SHIM(USER32, CharNextW, 1) { u32 p = ARG(0); return r16(p) ? p + 2 : p; }
SHIM(USER32, CharNextExA, 3) { u32 p = ARG(1); return r8(p) ? p + 1 : p; }
SHIM(USER32, SetWindowsHookExW, 4) { gw_log("SetWindowsHookExW type %d (ignored)", ARG(0)); return 0x0E000001; }
SHIM(USER32, UnhookWindowsHookEx, 1) { return 1; }
SHIM(USER32, CallNextHookEx, 4) { return 0; }
SHIM(USER32, OpenClipboard, 1) { return 0; }
SHIM(USER32, CloseClipboard, 0) { return 1; }
SHIM(USER32, EmptyClipboard, 0) { return 1; }
SHIM(USER32, GetClipboardData, 1) { return 0; }
SHIM(USER32, SetClipboardData, 2) { return 0; }

// ---------- message queue ----------
typedef struct { u32 hwnd, msg, wp, lp; } QMsg;
static QMsg q[256]; static int qh, qt;
void post_msg(u32 hwnd, u32 msg, u32 wp, u32 lp) { q[qt] = (QMsg){hwnd, msg, wp, lp}; qt = (qt + 1) & 255; }
typedef struct { u32 hwnd, id, ms, proc, next; int used; } Timer;
static Timer timers[32];
SHIM(USER32, SetTimer, 4) { for (int i = 0; i < 32; i++) if (!timers[i].used || (timers[i].hwnd == ARG(0) && timers[i].id == ARG(1))) {
  timers[i] = (Timer){ARG(0), ARG(1), ARG(2), ARG(3), now_ms() + ARG(2), 1}; gw_log("SetTimer id=%u %ums proc=%08x", ARG(1), ARG(2), ARG(3)); return ARG(1) ? ARG(1) : 1; } return 0; }
SHIM(USER32, KillTimer, 2) { for (int i = 0; i < 32; i++) if (timers[i].used && timers[i].hwnd == ARG(0) && timers[i].id == ARG(1)) timers[i].used = 0; return 1; }
int g_quit; u32 g_frames_peek;
// ---------- scripted input (GW_INPUT=<file>) ----------
// One event per line, keyed by Present count (frame): "<frame> <cmd> [args]". Commands:
//   move X Y | down X Y | up X Y (left button, client coords 1024x768) | rdown/rup X Y (right button)
//   type TEXT (rest of line; KEYDOWN/CHAR/KEYUP per character) | key VK (decimal; 13 Enter, 9 Tab, 8 Back)
//   shot NAME (save the next presented frame as shots/NAME.png) | log TEXT | quit
// A line "<frame> typefile PATH" types the first line of PATH (keeps passwords out of scripts and logs).
typedef struct { int frame; char cmd[16]; int x, y; char text[160]; } InEv;
static InEv evs[512]; static int nev = -1, evi;
extern int g_present_frames; extern void d3d_request_shot(const char *name);
static void input_load(void) {
  nev = 0; const char *fn = getenv("GW_INPUT"); if (!fn) return;
  FILE *f = fopen(fn, "r"); if (!f) { gw_log("GW_INPUT: cannot open %s", fn); return; }
  char line[512];
  while (fgets(line, sizeof line, f) && nev < 512) {
    char *nl = strchr(line, '\n'); if (nl) *nl = 0; if (!line[0] || line[0] == '#') continue;
    InEv *e = &evs[nev]; memset(e, 0, sizeof *e); int off = 0;
    if (sscanf(line, "%d %15s %n", &e->frame, e->cmd, &off) < 2) continue;
    const char *rest = line + off;
    if (!strcmp(e->cmd, "typefile")) { FILE *g = fopen(rest, "r"); if (!g) { gw_log("GW_INPUT: cannot open %s", rest); continue; }
      if (!fgets(e->text, sizeof e->text, g)) e->text[0] = 0; fclose(g); char *q = strpbrk(e->text, "\r\n"); if (q) *q = 0; strcpy(e->cmd, "type"); e->x = 1; }  // x=1: secret, not logged
    else if (!strcmp(e->cmd, "type") || !strcmp(e->cmd, "shot") || !strcmp(e->cmd, "log")) snprintf(e->text, sizeof e->text, "%s", rest);
    else sscanf(rest, "%d %d", &e->x, &e->y);
    nev++;
  }
  fclose(f); gw_log("GW_INPUT: %d events from %s", nev, fn);
  const char *ul = getenv("GW_INPUT_UNLINK");   // gwplay auto-login: private temp dir with the typed files, removed once read
  if (ul && *ul) { char c[1200]; snprintf(c, sizeof c, "%s/p", ul); unlink(c); snprintf(c, sizeof c, "%s/a", ul); unlink(c); snprintf(c, sizeof c, "%s/login.in", ul); unlink(c); rmdir(ul); }
}
static u32 vk_of(int ch) { if (ch >= 'a' && ch <= 'z') return ch - 32; if (ch >= 'A' && ch <= 'Z') return ch; if (ch >= '0' && ch <= '9') return ch;
  switch (ch) { case ' ': return 0x20; case '.': return 0xBE; case '-': return 0xBD; case '_': return 0xBD; case '@': return 0x32; default: return 0; } }
static void key_event(u32 vk, int ch) {
  if (ch) vk_char[vk & 255] = (u8)ch;
  // lParam carries repeat count 1 and a scan code like real Windows: the game's WndProc drops WM_CHAR with lParam == 1
  u32 sc = ((vk & 0xFF) | 0x10) << 16;
  keystate[vk & 255] = 0x80; post_msg(g_hwnd_main, 0x100, vk, 1 | sc);
  if (ch) post_msg(g_hwnd_main, 0x102, ch, 1 | sc);
  keystate[vk & 255] = 0; post_msg(g_hwnd_main, 0x101, vk, 0xC0000001u | sc);
}
static void mouse_msg(u32 msg, int x, int y) {
  g_mouse_x = x; g_mouse_y = y;
  u32 mk = (keystate[1] & 0x80 ? 1 : 0) | (keystate[2] & 0x80 ? 2 : 0);
  post_msg(g_hwnd_main, msg, mk, ((u32)(y & 0xFFFF) << 16) | (u32)(x & 0xFFFF));
}
// GW_CTL=<file> (2026-11-17): live control. Whenever <file> exists it is read, deleted, and its lines
// "<delay-frames> <cmd> [args]" (same commands as GW_INPUT, frames counted from now) are appended to the queue.
// Lets a shell drive a running gwhost interactively: write the file, wait, look at the shot, write the next one.
static void ctl_poll(void) {
  static const char *fn; static int init; if (!init) { init = 1; fn = getenv("GW_CTL"); } if (!fn) return;
  FILE *f = fopen(fn, "r"); if (!f) return; char tmp[1200]; snprintf(tmp, sizeof tmp, "%s.taken", fn);
  fclose(f); if (rename(fn, tmp)) return; f = fopen(tmp, "r"); if (!f) return;
  if (evi == nev) evi = nev = 0;   // queue drained: reuse it
  char line[512];
  while (fgets(line, sizeof line, f) && nev < 512) {
    char *nl = strchr(line, '\n'); if (nl) *nl = 0; if (!line[0] || line[0] == '#') continue;
    InEv *e = &evs[nev]; memset(e, 0, sizeof *e); int off = 0, d = 0;
    if (sscanf(line, "%d %15s %n", &d, e->cmd, &off) < 2) continue;
    e->frame = g_present_frames + d; const char *rest = line + off;
    if (!strcmp(e->cmd, "typefile")) { FILE *g = fopen(rest, "r"); if (!g) continue;
      if (!fgets(e->text, sizeof e->text, g)) e->text[0] = 0; fclose(g); char *q = strpbrk(e->text, "\r\n"); if (q) *q = 0; strcpy(e->cmd, "type"); e->x = 1; }
    else if (!strcmp(e->cmd, "type") || !strcmp(e->cmd, "shot") || !strcmp(e->cmd, "log")) snprintf(e->text, sizeof e->text, "%s", rest);
    else sscanf(rest, "%d %d", &e->x, &e->y);
    nev++;
  }
  fclose(f); unlink(tmp); gw_log("GW_CTL: queue now %d events (frame %d)", nev - evi, g_present_frames);
}
static void inject_due(void) {
  if (nev < 0) input_load();
  ctl_poll();
  while (evi < nev && evs[evi].frame <= g_present_frames) {
    InEv *e = &evs[evi++]; const char *c = e->cmd;
    gw_log("INPUT frame %d: %s %s", g_present_frames, c, e->x == 1 && !strcmp(c, "type") ? "<secret>" : e->text[0] ? e->text : "");
    if (!strcmp(c, "move")) mouse_msg(0x200, e->x, e->y);
    else if (!strcmp(c, "down")) { mouse_msg(0x200, e->x, e->y); keystate[1] = 0x80; mouse_msg(0x201, e->x, e->y); }
    else if (!strcmp(c, "up")) { keystate[1] = 0; mouse_msg(0x202, e->x, e->y); }
    else if (!strcmp(c, "rdown")) { mouse_msg(0x200, e->x, e->y); keystate[2] = 0x80; mouse_msg(0x204, e->x, e->y); }
    else if (!strcmp(c, "rup")) { keystate[2] = 0; mouse_msg(0x205, e->x, e->y); }
    else if (!strcmp(c, "type")) { for (const char *p = e->text; *p; p++) key_event(vk_of((u8)*p), (u8)*p); }
    else if (!strcmp(c, "key")) { int ch = (e->x == 8 || e->x == 9 || e->x == 13 || e->x == 27) ? e->x : 0; key_event(e->x, ch); }
    else if (!strcmp(c, "shot")) d3d_request_shot(e->text);
    else if (!strcmp(c, "peek")) { u32 p = r32(0x1575eac);  // main player's rank state (rank-display test)
      if (p) gw_log("PEEK main=%08x wr=%u ar=%u weff=%u aeff=%u wscore=%d ascore=%d caps a=%u w=%u magic=%02x%02x",
        p, r32(p + 0x974), r32(p + 0x978), r32(p + 0x96c), r32(p + 0x970), (int)r32(p + 0x97c), (int)r32(p + 0x980),
        M[p + 0x9a1], M[p + 0x9a2], M[p + 0x9a3], M[p + 0x9a4]); if (p) gw_log("PEEK ids c=%u 248=%u state=%u", r32(p + 0xc), r32(p + 0x248), r32(0x1575f4c));
      const char *sv = getenv("GW_PEEK_FIND"); if (p && sv) { u32 v = (u32)atoi(sv);
        for (u32 o = 0; o < 0x23a68; o += 4) if (r32(p + o) == v) gw_log("PEEK find %u at +0x%x", v, o); } }
    else if (!strcmp(c, "log")) ;
    else if (!strcmp(c, "quit")) { gw_log("GW_INPUT quit"); fflush(LOG); exit(0); }
  }
}
// ---------- live input (the Mac app shell, host/mac.m) ----------
// Called from the UI thread; drained on the emulator thread in peek(), where key/mouse state is updated
// as each event becomes a window message (so GetKeyState matches the message being processed).
#include <pthread.h>
typedef struct { int kind; i32 a, b; } LiveEv;   // kind: 0 move 1 ldown 2 lup 3 rdown 4 rup 5 wheel(a=x,b=y; delta in g_wheel) 6 keydown 7 keyup 8 char
static LiveEv live[1024]; static int lh, lt; static pthread_mutex_t lmu = PTHREAD_MUTEX_INITIALIZER; static i32 wheel_q[1024];
static void live_push(int kind, i32 a, i32 b, i32 w) { pthread_mutex_lock(&lmu); int n = (lt + 1) & 1023; if (n != lh) { live[lt] = (LiveEv){kind, a, b}; wheel_q[lt] = w; lt = n; } pthread_mutex_unlock(&lmu); }
void gw_in_mouse(int kind, int x, int y) { live_push(kind, x, y, 0); }
void gw_in_wheel(int x, int y, int delta) { live_push(5, x, y, delta); }
void gw_in_key(u32 vk, int down) { live_push(down ? 6 : 7, (i32)vk, 0, 0); }
void gw_in_char(u32 ch) { live_push(8, (i32)ch, 0, 0); }
static void live_drain(void) {
  for (;;) {
    pthread_mutex_lock(&lmu); if (lh == lt) { pthread_mutex_unlock(&lmu); break; } LiveEv e = live[lh]; i32 w = wheel_q[lh]; lh = (lh + 1) & 1023; pthread_mutex_unlock(&lmu);
    switch (e.kind) {
      case 0: mouse_msg(0x200, e.a, e.b); break;
      case 1: keystate[1] = 0x80; mouse_msg(0x201, e.a, e.b); break;
      case 2: keystate[1] = 0; mouse_msg(0x202, e.a, e.b); break;
      case 3: keystate[2] = 0x80; mouse_msg(0x204, e.a, e.b); break;
      case 4: keystate[2] = 0; mouse_msg(0x205, e.a, e.b); break;
      case 5: { g_mouse_x = e.a; g_mouse_y = e.b; u32 mk = (keystate[1] & 0x80 ? 1 : 0) | (keystate[2] & 0x80 ? 2 : 0);
        post_msg(g_hwnd_main, 0x20A, ((u32)(w & 0xFFFF) << 16) | mk, ((u32)(e.b & 0xFFFF) << 16) | (u32)(e.a & 0xFFFF)); break; }
      case 6: case 7: { u32 vk = e.a & 255, sc = ((vk | 0x10) & 0xFF) << 16; int down = e.kind == 6;
        int was = keystate[vk] & 0x80; keystate[vk] = down ? 0x80 : 0;
        post_msg(g_hwnd_main, down ? 0x100 : 0x101, vk, down ? (1 | sc | (was ? 0x40000000u : 0)) : (0xC0000001u | sc));
        if (down && (vk == 8 || vk == 9 || vk == 13 || vk == 27)) post_msg(g_hwnd_main, 0x102, vk, 1 | sc);   // TranslateMessage's control characters
        break; }
      case 8: post_msg(g_hwnd_main, 0x102, (u32)e.a, 1 | (0x1E << 16)); break;
    }
  }
}
extern void host_frame_hook(void);
static int peek(u32 msgp, u32 remove) {
  g_frames_peek++;
  host_frame_hook(); inject_due(); live_drain();
  if (qh != qt) {
    QMsg m = q[qh]; if (remove & 1) qh = (qh + 1) & 255;
    w32(msgp, m.hwnd); w32(msgp + 4, m.msg); w32(msgp + 8, m.wp); w32(msgp + 12, m.lp); w32(msgp + 16, now_ms()); w32(msgp + 20, g_mouse_x); w32(msgp + 24, g_mouse_y);
    return 1;
  }
  u32 t = now_ms();
  for (int i = 0; i < 32; i++) if (timers[i].used && (i32)(t - timers[i].next) >= 0) {
    timers[i].next = t + timers[i].ms;
    w32(msgp, timers[i].hwnd); w32(msgp + 4, 0x113); w32(msgp + 8, timers[i].id); w32(msgp + 12, timers[i].proc); w32(msgp + 16, t);
    return 1;
  }
  return 0;
}
// the message pump is the main thread's yield point: other runnable guest threads (thread.c) run here
extern int thread_yield_from_shim(int nargs, u32 ret); extern int g_threads_active;
SHIM(USER32, PeekMessageW, 5) { extern void ds_pump(void); ds_pump(); u32 r = peek(ARG(0), ARG(4)); if (g_threads_active) thread_yield_from_shim(5, r); return r; }
SHIM(USER32, PeekMessageA, 5) { extern void ds_pump(void); ds_pump(); u32 r = peek(ARG(0), ARG(4)); if (g_threads_active) thread_yield_from_shim(5, r); return r; }
SHIM(USER32, TranslateMessage, 1) { return 0; }
static u32 dispatch(u32 m) {
  u32 h = r32(m), msg = r32(m + 4), wp = r32(m + 8), lp = r32(m + 12);
  if (msg == 0x113 && lp) return gcall(lp, 4, h, msg, wp, now_ms());
  Wnd *w = wnd(h); if (!w) w = wnd(g_hwnd_main); if (!w) return 0;
  return gcall(w->wndproc, 4, w->hwnd, msg, wp, lp);
}
SHIM(USER32, DispatchMessageW, 1) { return dispatch(ARG(0)); }
SHIM(USER32, DispatchMessageA, 1) { return dispatch(ARG(0)); }
SHIM(USER32, PostMessageA, 4) { post_msg(ARG(0), ARG(1), ARG(2), ARG(3)); return 1; }
SHIM(USER32, PostMessageW, 4) { post_msg(ARG(0), ARG(1), ARG(2), ARG(3)); return 1; }
SHIM(USER32, PostQuitMessage, 1) { gw_log("PostQuitMessage(%d)", ARG(0)); post_msg(0, 0x12, ARG(0), 0); g_quit = 1; return 0; }
SHIM(USER32, SendMessageW, 4) { Wnd *w = wnd(ARG(0)); if (!w) return 0; return gcall(w->wndproc, 4, ARG(0), ARG(1), ARG(2), ARG(3)); }
SHIM(USER32, CallWindowProcW, 5) { return gcall(ARG(0), 4, ARG(1), ARG(2), ARG(3), ARG(4)); }
SHIM(USER32, DefWindowProcW, 4) { u32 m = ARG(1); if (m == WM_NCCREATE) return 1; if (m == 0x10) { gw_log("WM_CLOSE -> DestroyWindow"); } return 0; }
SHIM(USER32, DefWindowProcA, 4) { u32 m = ARG(1); if (m == WM_NCCREATE) return 1; return 0; }
SHIM(USER32, MsgWaitForMultipleObjectsEx, 5) { return 0x102; }
SHIM(USER32, WaitMessage, 0) { usleep(1000); return 1; }

// ---------- GDI32 (minimal) ----------
SHIM(GDI32, GetDeviceCaps, 2) { switch (ARG(1)) { case 12: return 32; case 14: return 1; case 88: case 90: return 96; case 8: return 1920; case 10: return 1080; case 116: return 60; default: return 0; } }
SHIM(GDI32, GetStockObject, 1) { return 0x0B000000 + ARG(0); }
SHIM(GDI32, CreateSolidBrush, 1) { return 0x0B100001; }
SHIM(GDI32, DeleteObject, 1) { return 1; }
SHIM(GDI32, SelectObject, 2) { return 0x0B000001; }
SHIM(GDI32, CreateCompatibleDC, 1) { return 0x0D000002; }
SHIM(GDI32, DeleteDC, 1) { return 1; }
SHIM(GDI32, SetBkMode, 2) { return 1; }
SHIM(GDI32, SetBkColor, 2) { return 0; }
SHIM(GDI32, SetTextColor, 2) { return 0; }
SHIM(GDI32, SetTextAlign, 2) { return 0; }
SHIM(GDI32, SetMapMode, 2) { return 1; }

// ---------- ADVAPI32 (empty registry) ----------
SHIM(ADVAPI32, RegOpenKeyExA, 5) { return 2; }
SHIM(ADVAPI32, RegOpenKeyExW, 5) { return 2; }
SHIM(ADVAPI32, RegOpenKeyA, 3) { return 2; }
SHIM(ADVAPI32, RegOpenKeyW, 3) { return 2; }
SHIM(ADVAPI32, RegCreateKeyExA, 9) { w32(ARG(7), 0x0F000001); if (ARG(8)) w32(ARG(8), 1); return 0; }
SHIM(ADVAPI32, RegCreateKeyExW, 9) { w32(ARG(7), 0x0F000001); if (ARG(8)) w32(ARG(8), 1); return 0; }
SHIM(ADVAPI32, RegQueryValueExA, 6) { return 2; }
SHIM(ADVAPI32, RegQueryValueExW, 6) { return 2; }
SHIM(ADVAPI32, RegSetValueExA, 6) { return 0; }
SHIM(ADVAPI32, RegSetValueExW, 6) { return 0; }
SHIM(ADVAPI32, RegCloseKey, 1) { return 0; }
SHIM(ADVAPI32, RegFlushKey, 1) { return 0; }
SHIM(ADVAPI32, RegDeleteValueA, 2) { return 2; }
SHIM(ADVAPI32, RegDeleteValueW, 2) { return 2; }
SHIM(ADVAPI32, RegDeleteKeyA, 2) { return 2; }
SHIM(ADVAPI32, RegDeleteKeyW, 2) { return 2; }
SHIM(ADVAPI32, RegEnumKeyExA, 8) { return 259; }
SHIM(ADVAPI32, RegEnumKeyExW, 8) { return 259; }
SHIM(ADVAPI32, RegEnumValueA, 8) { return 259; }
SHIM(ADVAPI32, RegEnumValueW, 8) { return 259; }
SHIM(ADVAPI32, RegQueryInfoKeyA, 12) { return 2; }
SHIM(ADVAPI32, RegQueryInfoKeyW, 12) { return 2; }

// ---------- OLE ----------
SHIM(OLE32, CoInitialize, 1) { return 0; }
SHIM(OLE32, CoInitializeEx, 2) { return 0; }
SHIM(OLE32, CoUninitialize, 0) { return 0; }
SHIM(OLE32, OleInitialize, 1) { return 0; }
SHIM(OLE32, OleUninitialize, 0) { return 0; }
SHIM(OLE32, CoCreateInstance, 5) { gw_log("CoCreateInstance refused"); if (ARG(4)) w32(ARG(4), 0); return 0x80040154; }
SHIM(OLE32, CoGetClassObject, 5) { if (ARG(4)) w32(ARG(4), 0); return 0x80040154; }
SHIM(OLE32, CoTaskMemAlloc, 1) { return heap_alloc(ARG(0), 0); }
SHIM(OLE32, CoTaskMemFree, 1) { heap_free(ARG(0)); return 0; }
SHIM(OLE32, CoTaskMemRealloc, 2) { return heap_realloc(ARG(0), ARG(1), 0); }
SHIM_ORD(OLEAUT32, 2, SysAllocString, 1) { if (!ARG(0)) return 0; u32 n = gw_wcslen(ARG(0)); u32 p = heap_alloc(4 + 2 * n + 2, 0); w32(p, 2 * n); memcpy(P(p + 4), P(ARG(0)), 2 * n + 2); return p + 4; }
SHIM_ORD(OLEAUT32, 4, SysAllocStringLen, 2) { u32 n = ARG(1); u32 p = heap_alloc(4 + 2 * n + 2, 1); w32(p, 2 * n); if (ARG(0)) memcpy(P(p + 4), P(ARG(0)), 2 * n); w16(p + 4 + 2 * n, 0); return p + 4; }
SHIM_ORD(OLEAUT32, 6, SysFreeString, 1) { if (ARG(0)) heap_free(ARG(0) - 4); return 0; }
SHIM_ORD(OLEAUT32, 7, SysStringLen, 1) { return ARG(0) ? r32(ARG(0) - 4) / 2 : 0; }
SHIM_ORD(OLEAUT32, 149, SysStringByteLen, 1) { return ARG(0) ? r32(ARG(0) - 4) : 0; }
SHIM_ORD(OLEAUT32, 8, VariantInit, 1) { memset(P(ARG(0)), 0, 16); return 0; }
SHIM_ORD(OLEAUT32, 9, VariantClear, 1) { memset(P(ARG(0)), 0, 16); return 0; }

// ---------- misc ----------
SHIM(WINMM, timeGetTime, 0) { return now_ms(); }
SHIM(WINMM, timeBeginPeriod, 1) { return 0; }
SHIM(WINMM, timeEndPeriod, 1) { return 0; }
SHIM(SHLWAPI, StrCmpW, 2) { u32 a = ARG(0), b = ARG(1); for (u32 i = 0;; i++) { u16 x = r16(a + 2 * i), y = r16(b + 2 * i); if (x != y) return x < y ? (u32)-1 : 1; if (!x) return 0; } }
SHIM(SHELL32, ShellExecuteA, 6) { gw_log("ShellExecuteA(%s) refused", ARG(2) ? (char *)P(ARG(2)) : ""); return 2; }
SHIM(SHELL32, ShellExecuteExW, 1) { gw_log("ShellExecuteExW refused"); return 0; }
SHIM(NETAPI32, Netbios, 1) {
  u32 ncb = ARG(0); u8 cmd = r8(ncb) & 0x7f; u32 buf = r32(ncb + 4); u16 len = r16(ncb + 8);
  if (cmd == 0x37 && len >= 2) { w8(buf, 1); w8(buf + 1, 0); }
  else if (cmd == 0x33 && len >= 6) { memset(P(buf), 0, len); const u8 mac[6] = {0x00, 0x1C, 0x42, 0x47, 0x57, 0x01}; memcpy(P(buf), mac, 6); }
  w8(ncb + 1, 0); return 0;
}
SHIM(DBGHELP, SymSetOptions, 1) { return 0; }
SHIM(DBGHELP, SymGetOptions, 0) { return 0; }
SHIM(DBGHELP, SymInitialize, 3) { return 0; }
SHIM(DBGHELP, SymCleanup, 1) { return 1; }
SHIM(DBGHELP, MiniDumpWriteDump, 7) { gw_log("MiniDumpWriteDump (ignored)"); return 0; }
SHIM(OLE32, CLSIDFromProgID, 2) { char *s = g_w2u(ARG(0)); gw_log("CLSIDFromProgID(%s) -> fail", s); free(s); return 0x800401F3; }
SHIM(OLE32, CLSIDFromString, 2) { return 0x800401F3; }
SHIM(OLE32, StringFromGUID2, 3) { return u2w("{00000000-0000-0000-0000-000000000000}", ARG(1), ARG(2)); }
SHIM(OLE32, OleLockRunning, 3) { return 0; }
SHIM(OLE32, CreateStreamOnHGlobal, 3) { if (ARG(2)) w32(ARG(2), 0); return 0x8007000E; }
SHIM(FB, FacebookClientCreate, 0) { gw_log("FacebookClientCreate -> NULL (Facebook login not supported)"); return 0; }
