// gwhost: runs the unmodified GodsWar Origin.exe (i386 PE) on arm64 macOS under unicorn,
// with host-side Win32/D3D9 shims. See ../POINTER-MODEL.md.
#pragma once
#include <unicorn/unicorn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <ctype.h>

typedef uint32_t u32; typedef uint16_t u16; typedef uint8_t u8; typedef int32_t i32;

extern u8 *M;                 // 4 GiB arena: guest address a = host M+a
extern uc_engine *uc;         // current thread's engine
extern FILE *LOG;             // run/gwhost.log
extern char g_client_dir[1024], g_run_dir[1024];
extern int g_trace;

static inline void *P(u32 a) { return M + a; }
static inline u32 r32(u32 a) { u32 v; memcpy(&v, M + a, 4); return v; }
static inline u16 r16(u32 a) { u16 v; memcpy(&v, M + a, 2); return v; }
static inline u8 r8(u32 a) { return M[a]; }
static inline void w32(u32 a, u32 v) { memcpy(M + a, &v, 4); }
static inline void w16(u32 a, u16 v) { memcpy(M + a, &v, 2); }
static inline void w8(u32 a, u8 v) { M[a] = v; }
static inline float rf(u32 a) { float v; memcpy(&v, M + a, 4); return v; }
static inline void wf(u32 a, float v) { memcpy(M + a, &v, 4); }

// ---- shim registry ----
typedef u32 (*shim_fn)(void);
void shim_register(const char *dll, const char *name, int nargs, shim_fn fn);
#define SHIM(dll, name, n) \
  static u32 sh_##dll##_##name(void); \
  __attribute__((constructor)) static void reg_##dll##_##name(void) { shim_register(#dll, #name, n, sh_##dll##_##name); } \
  static u32 sh_##dll##_##name(void)
#define SHIM_ORD(dll, ord, cname, n) \
  static u32 sh_##dll##_##cname(void); \
  __attribute__((constructor)) static void reg_##dll##_##cname(void) { shim_register(#dll, "#" #ord, n, sh_##dll##_##cname); shim_register(#dll, #cname, n, sh_##dll##_##cname); } \
  static u32 sh_##dll##_##cname(void)

extern u32 g_esp;             // guest esp at shim entry ([esp] = return address)
static inline u32 ARG(int i) { return r32(g_esp + 4 + 4 * i); }
extern int g_shim_jumped;     // set by a shim that redirected EIP itself
extern u32 g_ret_edx;         // optional edx for 64-bit returns
void shim_set_edx(u32 v);

u32 thunk_for(const char *dll, const char *name);   // 0 if no shim exists
u32 thunk_alloc_named(const char *dll, const char *name, int nargs, shim_fn fn);

// ---- guest calls (host -> guest) ----
u32 gcall(u32 fn, int n, ...);          // stdcall/cdecl both fine (esp is restored)
u32 gcall_this(u32 fn, u32 ecx, int n, ...);

// ---- memory ----
u32 heap_alloc(u32 size, int zero);
void heap_free(u32 p);
u32 heap_size(u32 p);
u32 heap_realloc(u32 p, u32 size, int zero);
u32 vm_alloc(u32 size, u32 align);       // page allocator (VirtualAlloc)
u32 gstrdup(const char *s);
u32 gwstrdup(const char *utf8);

// ---- strings ----
size_t gw_wcslen(u32 a);
char *g_w2u(u32 a);                 // guest UTF-16 -> malloc'd host UTF-8 (NULL if a==0)
char *g_a2u(u32 a);                 // guest ANSI(cp1252) -> host UTF-8 (strdup)
int u2w(const char *s, u32 dst, int dstchars);   // returns chars written incl NUL
int mb2wc(int cp, const u8 *src, int srclen, u16 *dst, int dstlen);
int wc2mb(int cp, const u16 *src, int srclen, u8 *dst, int dstlen);

// ---- modules ----
typedef struct Module { char name[64]; u32 base, size, entry; int guest; struct Module *next; } Module;
Module *mod_find(const char *name);
Module *mod_by_base(u32 base);
Module *pe_load(const char *name);           // load guest DLL by name (case-insensitive, from client dir)
u32 pe_export(Module *m, const char *name);  // name or "#ord"
const char *addr_sym(u32 a);                 // "module+off" for logs

// ---- handles ----
enum { H_FREE, H_FILE, H_FIND, H_EVENT, H_MUTEX, H_THREAD, H_SEM, H_HEAP, H_MAPPING, H_MISC, H_HWND, H_GDI };
typedef struct { int type; int fd; void *p; u32 v1, v2, v3; char *path; } Handle;
u32 h_new(int type);
Handle *h_get(u32 h);
void h_close(u32 h);

// ---- misc ----
void gw_fatal(const char *fmt, ...);
void gw_log(const char *fmt, ...);
void dump_regs(void);
void set_last_error(u32 e);
u32 now_ms(void);
u32 cpu_ms(void);
u32 wall_ms(void);             // real ms since start
uint64_t now_us(void);        // guest clock (virtual if GW_VCLOCK)
void clock_on_present(void);
void clock_sleep(u32 ms);              // process user+sys CPU ms
// GW_SPIKE: per-frame host counters, reset at each Present; logged for frames slower than the threshold (d3d9.c)
typedef struct { uint64_t decode_ns, read_ns, read_b, sleep_us, texbytes, open_ns; unsigned opens, decodes, texnew, reads; } SpikeCtr;
extern SpikeCtr g_sc; uint64_t mono_ns(void);
void spike_samples_log(void); void spike_shims_log(int show); void spike_samples_reset(void); extern int g_spike_ms;
char *map_path(const char *guestpath, int forwrite);    // malloc'd host path
u32 tls_teb(void);

// d3d9 entry for user32 (present target)
extern u32 g_hwnd_main;
void d3d_init_vtables(void);
u32 user_window_proc(u32 hwnd);

#define WAIT_OBJECT_0 0
#define WAIT_TIMEOUT 0x102
