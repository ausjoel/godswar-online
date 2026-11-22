// gwhost core: arena, CPU, thunks, guest calls, PE loader, heap, handles, main.
#define _XOPEN_SOURCE 700
#define _DARWIN_C_SOURCE 1
#include "gw.h"
#include <sys/mman.h>
#include <sys/stat.h>
#include <dirent.h>
#include <strings.h>
#include <time.h>
#include <unistd.h>
#include <iconv.h>
#include <signal.h>
#include <pthread.h>
#include <ucontext.h>
#include <pthread/qos.h>

u8 *M; uc_engine *uc; FILE *LOG; int g_trace;
char g_client_dir[1024], g_run_dir[1024];
u32 g_esp; int g_shim_jumped; u32 g_ret_edx; static int g_edx_set;
void shim_set_edx(u32 v) { g_ret_edx = v; g_edx_set = 1; }

#define ARENA_LO   0x00010000u
#define ARENA_HI   0xFE000000u
#define THUNK_BASE 0xFE000000u
#define THUNK_SIZE 0x00100000u
#define MAGIC_RET  (THUNK_BASE + THUNK_SIZE - 0x100)  // gcall return target (hlt)
#define MAGIC_EXIT (THUNK_BASE + THUNK_SIZE - 0x80)
#define GDT_ADDR   0x7FFF0000u
#define TEB_ADDR   0x7FFDE000u
#define PEB_ADDR   0x7FFDF000u
#define HEAP_LO    0x16000000u
#define HEAP_HI    0x40000000u
#define VM_LO      0x40000000u
#define VM_HI      0x70000000u
#define STACK_TOP  0x7F000000u

int g_log_buffered;
void gw_log(const char *fmt, ...) {
  va_list ap; va_start(ap, fmt); vfprintf(LOG, fmt, ap); va_end(ap); fputc('\n', LOG);
  if (!g_log_buffered) fflush(LOG);   // buffered from the first Present on; Present flushes once per frame
}
static u32 bt_eip;
void gw_fatal(const char *fmt, ...) {
  va_list ap; va_start(ap, fmt); fprintf(LOG, "FATAL: "); vfprintf(LOG, fmt, ap); va_end(ap); fputc('\n', LOG);
  dump_regs(); fflush(LOG);
  va_start(ap, fmt); fprintf(stderr, "FATAL: "); vfprintf(stderr, fmt, ap); va_end(ap); fputc('\n', stderr);
  exit(3);
}

// ---------------- shim registry / thunks ----------------
typedef struct { char dll[24]; char name[96]; int nargs; shim_fn fn; int seen; int co; unsigned long calls; int (*co_skip)(void); uint64_t fwall, fcpu; unsigned fcalls; } Shim;
static Shim **shims; static int nshims, capshims;
typedef struct { Shim *s; char dll[24]; char name[96]; } Thunk;
#define MAXTHUNK ((THUNK_SIZE - 0x1000) / 8)
static Thunk *thunks; static int nthunks;
static FILE *OBS;

static void upcase(char *d, const char *s, size_t n) {
  size_t i = 0; for (; s[i] && i < n - 1; i++) d[i] = (char)toupper((unsigned char)s[i]); d[i] = 0;
  char *dot = strrchr(d, '.'); if (dot && !strcmp(dot, ".DLL")) *dot = 0;
}
void shim_register(const char *dll, const char *name, int nargs, shim_fn fn) {
  if (nshims == capshims) { capshims = capshims ? capshims * 2 : 512; shims = realloc(shims, capshims * sizeof(Shim *)); }
  Shim *s = calloc(1, sizeof *s); shims[nshims++] = s;
  upcase(s->dll, dll, sizeof s->dll); snprintf(s->name, sizeof s->name, "%s", name); s->nargs = nargs; s->fn = fn;
  static const char *CO[] = {"CreateWindowExW", "CreateWindowExA", "DestroyWindow", "DispatchMessageW", "DispatchMessageA", "SendMessageW",
    "SendMessageA", "CallWindowProcW", "CallWindowProcA", "RaiseException", "RtlUnwind", "LoadLibraryA", "LoadLibraryW", "LoadLibraryExA",
    "LoadLibraryExW", "CreateThread", NULL};
  for (int i = 0; CO[i]; i++) if (!strcmp(name, CO[i])) s->co = 1;
}
// GW_SHIMSTATS=1: the Present stat line is followed by the most-called shims since the previous one
void shim_stats_dump(int frames) {
  static int on = -1; if (on < 0) on = getenv("GW_SHIMSTATS") != NULL; if (!on) return;
  char b[2048]; int o = 0; o += snprintf(b + o, sizeof b - o, "shim calls over %d frames:", frames);
  for (int k = 0; k < 14; k++) { Shim *best = NULL; for (int i = 0; i < nshims; i++) if (shims[i]->calls && (!best || shims[i]->calls > best->calls)) best = shims[i];
    if (!best) break; o += snprintf(b + o, sizeof b - o, " %s=%lu", best->name, best->calls); best->calls = 0; if (o > 1900) break; }
  for (int i = 0; i < nshims; i++) shims[i]->calls = 0;
  gw_log("%s", b);
}
// GW_SPIKE: per-frame wall and thread-CPU time of each plain shim (logged for slow frames)
static uint64_t thr_cpu_ns(void) { struct timespec t; clock_gettime(CLOCK_THREAD_CPUTIME_ID, &t); return (uint64_t)t.tv_sec * 1000000000ull + t.tv_nsec; }
static inline u32 shim_call(Shim *s) {
  static int on = -1; if (on < 0) on = getenv("GW_SPIKE_SHIMS") != NULL;
  if (!g_spike_ms || !on) return s->fn();
  uint64_t w = mono_ns(), c = thr_cpu_ns(); u32 r = s->fn(); s->fwall += mono_ns() - w; s->fcpu += thr_cpu_ns() - c; s->fcalls++; return r; }
void spike_shims_log(int show) {
  if (show) { char b[900]; int o = 0; uint64_t tw = 0, tc = 0; for (int i = 0; i < nshims; i++) { tw += shims[i]->fwall; tc += shims[i]->fcpu; }
    o += snprintf(b, sizeof b, "  spike shims total wall=%ums cpu=%ums; top:", (u32)(tw / 1000000), (u32)(tc / 1000000));
    for (int k = 0; k < 6; k++) { Shim *best = NULL; for (int i = 0; i < nshims; i++) if (shims[i]->fwall && (!best || shims[i]->fwall > best->fwall)) best = shims[i];
      if (!best) break; o += snprintf(b + o, sizeof b - o, " %s=%u/%ums(%u)", best->name, (u32)(best->fwall / 1000000), (u32)(best->fcpu / 1000000), best->fcalls); best->fwall = 0; if (o > 800) break; }
    gw_log("%s", b); }
  for (int i = 0; i < nshims; i++) { shims[i]->fwall = shims[i]->fcpu = 0; shims[i]->fcalls = 0; } }
// a coroutine shim whose co_skip() returns nonzero runs as a plain shim for that call (no guest callback needed)
void shim_set_co_skip(const char *name, int (*f)(void)) { for (int i = 0; i < nshims; i++) if (!strcmp(shims[i]->name, name)) shims[i]->co_skip = f; }
void shim_mark_co(const char *dll, const char *name) { (void)dll; for (int i = 0; i < nshims; i++) if (!strcmp(shims[i]->name, name)) shims[i]->co = 1; }
static Shim *shim_find(const char *dll, const char *name) {
  char D[24]; upcase(D, dll, sizeof D);
  for (int i = 0; i < nshims; i++) if (!strcmp(shims[i]->dll, D) && !strcmp(shims[i]->name, name)) return shims[i];
  return NULL;
}
// Thunks: simple shims (no guest callbacks, no EIP change) use `sysenter; ret 4*nargs`: unicorn calls the
// SYSENTER hook from inside the translated block, so the shim runs without leaving the CPU loop and the guest
// `ret n` pops the arguments itself (GW_SLOWTHUNK=1 disables this). Coroutine shims, unimplemented imports and
// shims that touch unicorn's code cache keep `int 0x2e; ret` (interrupt exit, full register control).
static int fast_thunks = -1;
static int shim_fast_ok(const Shim *s) {
  if (fast_thunks < 0) fast_thunks = getenv("GW_SLOWTHUNK") ? 0 : 1;
  if (!fast_thunks || !s || !s->fn || s->co) return 0;
  static const char *SLOW[] = {"FlushInstructionCache", "VirtualProtect", "ExitProcess", "ExitThread", "TerminateProcess",
    // thread.c: shims that may switch guest threads (they set g_shim_jumped)
    "Sleep", "SleepEx", "SwitchToThread", "WaitForSingleObject", "WaitForSingleObjectEx", "WaitForMultipleObjects",
    "WaitForMultipleObjectsEx", "EnterCriticalSection", "ThreadReturn", "PeekMessageW", "PeekMessageA", "GetMessageW", "GetMessageA", NULL};
  for (int i = 0; SLOW[i]; i++) if (!strcmp(s->name, SLOW[i])) return 0;
  return 1;
}
static u32 thunk_new(Shim *s, const char *dll, const char *name) {
  if (nthunks >= (int)MAXTHUNK) gw_fatal("out of thunks");
  int i = nthunks++; thunks[i].s = s; upcase(thunks[i].dll, dll, sizeof thunks[i].dll);
  snprintf(thunks[i].name, sizeof thunks[i].name, "%s", name);
  u32 a = THUNK_BASE + 8 * i;
  if (shim_fast_ok(s)) { u32 n = 4 * s->nargs; M[a] = 0x0F; M[a + 1] = 0x34; M[a + 2] = 0xC2; M[a + 3] = n & 255; M[a + 4] = n >> 8; }  // sysenter; ret n
  else { M[a] = 0xCD; M[a + 1] = 0x2E; M[a + 2] = 0xC3; }  // int 0x2e; ret
  return a;
}
u32 thunk_alloc_named(const char *dll, const char *name, int nargs, shim_fn fn) {
  Shim *s = shim_find(dll, name);
  if (!s) { shim_register(dll, name, nargs, fn); s = shims[nshims - 1]; }
  return thunk_new(s, dll, name);
}
u32 thunk_for(const char *dll, const char *name) {
  Shim *s = shim_find(dll, name); if (!s) return 0;
  return thunk_new(s, dll, name);
}
static u32 thunk_import(const char *dll, const char *name) {  // always succeeds; traps if unimplemented
  return thunk_new(shim_find(dll, name), dll, name);
}

// ---------------- registers / backtrace ----------------
static const int REGIDS[] = {UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDX, UC_X86_REG_ESI,
                             UC_X86_REG_EDI, UC_X86_REG_EBP, UC_X86_REG_ESP, UC_X86_REG_EIP, UC_X86_REG_EFLAGS};
static u32 rd(int r) { u32 v = 0; uc_reg_read(uc, r, &v); return v; }
static void wr(int r, u32 v) { uc_reg_write(uc, r, &v); }
void dump_regs(void) {
  if (!uc) return;
  u32 eip = rd(UC_X86_REG_EIP), esp = rd(UC_X86_REG_ESP), ebp = rd(UC_X86_REG_EBP);
  fprintf(LOG, "  eip=%08x(%s) eax=%08x ebx=%08x ecx=%08x edx=%08x esi=%08x edi=%08x ebp=%08x esp=%08x\n",
          eip, addr_sym(eip), rd(UC_X86_REG_EAX), rd(UC_X86_REG_EBX), rd(UC_X86_REG_ECX), rd(UC_X86_REG_EDX),
          rd(UC_X86_REG_ESI), rd(UC_X86_REG_EDI), ebp, esp);
  fprintf(LOG, "  stack:"); for (int i = 0; i < 12 && esp + 4 * i < ARENA_HI; i++) fprintf(LOG, " %08x", r32(esp + 4 * i));
  fprintf(LOG, "\n  ebp chain:");
  for (int i = 0; i < 24 && ebp > 0x10000 && ebp < STACK_TOP + 0x100000; i++) { fprintf(LOG, " %s", addr_sym(r32(ebp + 4))); ebp = r32(ebp); }
  fprintf(LOG, "\n"); fflush(LOG);
}

// ---------------- hooks ----------------
// Shims that call back into the guest run on host coroutines, so there is only ever ONE uc_emu_start
// level (unicorn's cpu jmp_env breaks under nesting). gcall() inside a coroutine points the CPU at the
// guest function with a per-coroutine trap address as return address, switches back to the hook, and the
// hook returns into the guest. When the guest returns to the trap, the coroutine is resumed.
#define NCO 64
#define CO_STACK (1u << 20)
#define CO_TRAP(k) (THUNK_BASE + THUNK_SIZE - 0x800 + 8 * (k))
typedef struct Co { ucontext_t ctx; u8 *stack; Shim *s; u32 esp, ret, gret; int done, used, jumped, waiting; u32 saved[10]; u32 edx; int edx_set; } Co;
static Co cos[NCO]; static ucontext_t main_ctx; static Co *co_cur;
static void finish_thunk(Shim *s, u32 esp, u32 ret, int jumped, int edx_set, u32 edx) {
  if (jumped) return;
  u32 ra = r32(esp);
  wr(UC_X86_REG_EAX, ret); if (edx_set) wr(UC_X86_REG_EDX, edx);
  wr(UC_X86_REG_ESP, esp + 4 + 4 * s->nargs); wr(UC_X86_REG_EIP, ra);
}
static void co_entry(void) {   // runs forever: one shim call per resume, so makecontext happens once per coroutine
  for (;;) {
    Co *c = co_cur; g_esp = c->esp; g_shim_jumped = 0; g_edx_set = 0;
    c->ret = c->s->fn();
    c->jumped = g_shim_jumped; c->edx_set = g_edx_set; c->edx = g_ret_edx; c->done = 1;
    swapcontext(&c->ctx, &main_ctx);
  }
}
static void co_resume(Co *c) {
  Co *prev = co_cur; co_cur = c;
  swapcontext(&main_ctx, &c->ctx);
  co_cur = prev;
  if (c->done) { c->used = 0; finish_thunk(c->s, c->esp, c->ret, c->jumped, c->edx_set, c->edx); }
}
static void on_thunk(void) {
  u32 eip = rd(UC_X86_REG_EIP) - 2, esp = rd(UC_X86_REG_ESP);
  if (eip >= CO_TRAP(0) && eip < CO_TRAP(NCO)) {           // guest returned from a coroutine's gcall
    Co *c = &cos[(eip - CO_TRAP(0)) / 8];
    if (!c->used || !c->waiting) gw_fatal("stray coroutine return %08x", eip);
    c->gret = rd(UC_X86_REG_EAX); c->waiting = 0;
    for (int i = 0; i < 10; i++) wr(REGIDS[i], c->saved[i]);
    co_resume(c); return;
  }
  u32 idx = (eip - THUNK_BASE) / 8;
  if (eip < THUNK_BASE || idx >= (u32)nthunks) gw_fatal("int 2e outside thunks at %08x", eip);
  Thunk *t = &thunks[idx]; Shim *s = t->s;
  if (!s || !s->fn) {
    gw_log("UNIMPLEMENTED %s!%s called from %s", t->dll, t->name, addr_sym(r32(esp)));
    fprintf(stderr, "UNIMPLEMENTED %s!%s called from %s\n", t->dll, t->name, addr_sym(r32(esp)));
    if (OBS) { fprintf(OBS, "%s\t%s\tUNIMPLEMENTED\n", t->dll, t->name); fflush(OBS); }
    dump_regs(); uc_emu_stop(uc); exit(4);
  }
  s->calls++;
  if (!s->seen) { s->seen = 1; if (OBS) { fprintf(OBS, "%s\t%s\n", s->dll, s->name); fflush(OBS); } }
  if (g_trace) gw_log("-> %s!%s(%08x,%08x,%08x,%08x) from %s", s->dll, s->name, r32(esp + 4), r32(esp + 8), r32(esp + 12), r32(esp + 16), addr_sym(r32(esp)));
  int skip = 0;
  if (s->co && s->co_skip) { u32 se = g_esp; g_esp = esp; skip = s->co_skip(); g_esp = se; }
  if (s->co && !skip) {
    Co *c = NULL; for (int i = 0; i < NCO; i++) if (!cos[i].used) { c = &cos[i]; break; }
    if (!c) gw_fatal("out of coroutines");
    if (!c->stack) {   // first use: build the context once; co_entry loops, a finished call leaves it parked
      c->stack = malloc(CO_STACK);
      getcontext(&c->ctx); c->ctx.uc_stack.ss_sp = c->stack; c->ctx.uc_stack.ss_size = CO_STACK; c->ctx.uc_link = NULL;
      makecontext(&c->ctx, co_entry, 0);
    }
    c->used = 1; c->done = 0; c->waiting = 0; c->s = s; c->esp = esp;
    co_resume(c); return;
  }
  u32 save_esp = g_esp; int save_j = g_shim_jumped;
  g_esp = esp; g_shim_jumped = 0; g_edx_set = 0;
  u32 ret = shim_call(s);
  finish_thunk(s, esp, ret, g_shim_jumped, g_edx_set, g_ret_edx);
  g_esp = save_esp; g_shim_jumped = save_j;
}
static void hook_sysenter(uc_engine *u, void *ud) {
  (void)u; (void)ud;
  u32 eip = rd(UC_X86_REG_EIP), esp = rd(UC_X86_REG_ESP), idx = (eip - THUNK_BASE) / 8;
  if (eip < THUNK_BASE || idx >= (u32)nthunks) gw_fatal("sysenter outside thunks at %08x", eip);
  Shim *s = thunks[idx].s; s->calls++;
  if (!s->seen) { s->seen = 1; if (OBS) { fprintf(OBS, "%s\t%s\n", s->dll, s->name); fflush(OBS); } }
  if (g_trace) gw_log("-> %s!%s(%08x,%08x,%08x,%08x) from %s", s->dll, s->name, r32(esp + 4), r32(esp + 8), r32(esp + 12), r32(esp + 16), addr_sym(r32(esp)));
  u32 save_esp = g_esp; int save_j = g_shim_jumped, save_e = g_edx_set;
  g_esp = esp; g_shim_jumped = 0; g_edx_set = 0;
  u32 ret = shim_call(s);
  if (g_shim_jumped) gw_fatal("fast-thunk shim %s changed EIP; add it to the SLOW list", s->name);
  wr(UC_X86_REG_EAX, ret); if (g_edx_set) wr(UC_X86_REG_EDX, g_ret_edx);
  g_esp = save_esp; g_shim_jumped = save_j; g_edx_set = save_e;
}
static void hook_intr(uc_engine *u, u32 intno, void *ud) {
  (void)u; (void)ud;
  if (intno == 0x2e) { on_thunk(); return; }
  if (intno == 3) { gw_log("int3 at %s (ignored)", addr_sym(rd(UC_X86_REG_EIP))); return; }
  gw_fatal("CPU exception/interrupt %u at %s", intno, addr_sym(rd(UC_X86_REG_EIP)));
}
static bool hook_badmem(uc_engine *u, uc_mem_type type, uint64_t addr, int size, int64_t value, void *ud) {
  (void)u; (void)ud;
  if (type == UC_MEM_FETCH_PROT && addr >= ARENA_LO && addr < ARENA_HI) {   // guest code outside a PE code section (ATL thunks, VirtualAlloc'd code)
    u32 pg = (u32)addr & ~0xfffu; gw_log("exec fault at %08x -> page made RWX", (u32)addr);
    uc_err pe = uc_mem_protect(u, pg, 0x2000, UC_PROT_ALL); if (pe == UC_ERR_OK) return true;
    gw_log("uc_mem_protect failed: %s", uc_strerror(pe));
  }
  gw_log("BAD MEMORY type=%d addr=%08llx size=%d value=%llx", type, (unsigned long long)addr, size, (long long)value);
  dump_regs();
  return false;
}
static bool hook_badins(uc_engine *u, void *ud) {
  (void)u; (void)ud; u32 eip = rd(UC_X86_REG_EIP);
  gw_log("INVALID INSN at %s: %02x %02x %02x %02x", addr_sym(eip), r8(eip), r8(eip + 1), r8(eip + 2), r8(eip + 3));
  dump_regs(); return false;
}

static int nan_hits;
static void hook_nanw(uc_engine *u, uc_mem_type type, uint64_t addr, int size, int64_t value, void *ud) {
  (void)u; (void)type; (void)ud;
  if (size == 4) { u32 v = (u32)value; if ((v & 0x7FFFFFFFu) == 0x7FC00000u && nan_hits < 6) { nan_hits++;
    gw_log("NaN/Inf store %08x to %08llx at %s", v, (unsigned long long)addr, addr_sym(rd(UC_X86_REG_EIP))); dump_regs(); } }
}
void watch_nan(u32 lo, u32 hi) { static int on; if (on && lo != 1) return; on = 1; if (lo == 1) { const char *e = getenv("GW_NANWATCH"); if (!e) return; lo = strtoul(e, NULL, 16); hi = lo + 0x1000; } uc_hook h; uc_hook_add(uc, &h, UC_HOOK_MEM_WRITE, hook_nanw, NULL, lo, hi); gw_log("watching NaN stores in %08x-%08x", lo, hi); }

// ---------------- guest calls ----------------
static int in_emu;
static u32 gcall_v(u32 fn, int has_this, u32 ecx, int n, va_list ap) {
  u32 args[16]; for (int i = 0; i < n; i++) args[i] = va_arg(ap, u32);
  Co *c = co_cur;
  u32 cur_esp = rd(UC_X86_REG_ESP);
  if (c) {
    for (int i = 0; i < 10; i++) c->saved[i] = rd(REGIDS[i]);
    u32 sp = ((c->esp < cur_esp ? c->esp : cur_esp) - 0x100) & ~15u;
    for (int i = n - 1; i >= 0; i--) { sp -= 4; w32(sp, args[i]); }
    sp -= 4; w32(sp, CO_TRAP(c - cos));
    wr(UC_X86_REG_ESP, sp); if (has_this) wr(UC_X86_REG_ECX, ecx); wr(UC_X86_REG_EIP, fn);
    u32 sg = g_esp; int sj = g_shim_jumped, se = g_edx_set; u32 sx = g_ret_edx;
    c->waiting = 1;
    swapcontext(&c->ctx, &main_ctx);            // run the guest; resumed when it returns to CO_TRAP
    g_esp = sg; g_shim_jumped = sj; g_edx_set = se; g_ret_edx = sx;
    return c->gret;
  }
  if (in_emu) gw_fatal("gcall(%s) from a non-coroutine shim; add it to the CO list", addr_sym(fn));
  u32 save[10]; for (int i = 0; i < 10; i++) save[i] = rd(REGIDS[i]);
  u32 sp = (cur_esp - 0x80) & ~15u;
  for (int i = n - 1; i >= 0; i--) { sp -= 4; w32(sp, args[i]); }
  sp -= 4; w32(sp, MAGIC_RET);
  wr(UC_X86_REG_ESP, sp); if (has_this) wr(UC_X86_REG_ECX, ecx);
  in_emu = 1; uc_err e = uc_emu_start(uc, fn, MAGIC_RET, 0, 0); in_emu = 0;
  if (e != UC_ERR_OK) gw_fatal("guest call %s failed: %s", addr_sym(fn), uc_strerror(e));
  u32 eip = rd(UC_X86_REG_EIP);
  if (eip != MAGIC_RET) gw_fatal("guest call %s stopped at %s", addr_sym(fn), addr_sym(eip));
  u32 r = rd(UC_X86_REG_EAX);
  for (int i = 0; i < 10; i++) wr(REGIDS[i], save[i]);
  return r;
}
u32 gcall(u32 fn, int n, ...) { va_list ap; va_start(ap, n); u32 r = gcall_v(fn, 0, 0, n, ap); va_end(ap); return r; }
u32 gcall_this(u32 fn, u32 ecx, int n, ...) { va_list ap; va_start(ap, n); u32 r = gcall_v(fn, 1, ecx, n, ap); va_end(ap); return r; }

// ---------------- heap (guest memory) ----------------
// Blocks: [u32 size][u32 magic] then payload (8-aligned). Size classes of 16 up to 64 KiB, larger by page.
#define HMAGIC 0x48ea9c01u
#define NCLS 4096
static u32 heap_brk = HEAP_LO, freelist[NCLS];
typedef struct Big { u32 p, size; struct Big *next; } Big;
static Big *bigfree;
static u32 heap_more(u32 total) {
  u32 p = heap_brk; heap_brk += total;
  if (heap_brk > HEAP_HI) gw_fatal("guest heap exhausted");
  return p;
}
u32 heap_alloc(u32 size, int zero) {
  if (size == 0) size = 1;
  u32 p;
  if (size <= 65536 - 16) {
    u32 c = (size + 15) / 16; u32 rs = c * 16;
    if (freelist[c]) { p = freelist[c]; freelist[c] = r32(p); }
    else p = heap_more(rs + 8) + 8;
    w32(p - 8, rs); w32(p - 4, HMAGIC);
    if (zero) memset(M + p, 0, rs);
    return p;
  }
  u32 rs = (size + 8 + 4095) & ~4095u;
  for (Big **b = &bigfree; *b; b = &(*b)->next) if ((*b)->size == rs) {
    Big *x = *b; *b = x->next; p = x->p; free(x); w32(p - 8, rs - 8); w32(p - 4, HMAGIC);
    if (zero) memset(M + p, 0, rs - 8); return p; }
  p = heap_more(rs) + 8; w32(p - 8, rs - 8); w32(p - 4, HMAGIC);
  return p;  // fresh pages are zero
}
u32 heap_size(u32 p) { if (!p || r32(p - 4) != HMAGIC) return 0; return r32(p - 8); }
void heap_free(u32 p) {
  if (!p) return;
  if (r32(p - 4) != HMAGIC) { gw_log("heap_free: bad block %08x", p); return; }
  u32 rs = r32(p - 8); w32(p - 4, 0);
  if (rs <= 65536 - 16) { u32 c = rs / 16; w32(p, freelist[c]); freelist[c] = p; }
  else { Big *b = malloc(sizeof *b); b->p = p; b->size = rs + 8; b->next = bigfree; bigfree = b; }
}
u32 heap_realloc(u32 p, u32 size, int zero) {
  if (!p) return heap_alloc(size, zero);
  u32 old = heap_size(p); if (size <= old && size + 64 > old / 2) return p;
  u32 n = heap_alloc(size, zero); memcpy(M + n, M + p, old < size ? old : size);
  if (zero && size > old) memset(M + n + old, 0, size - old);
  heap_free(p); return n;
}
static u32 vm_brk = VM_LO;
u32 vm_alloc(u32 size, u32 align) {
  if (!align) align = 0x10000;
  u32 p = (vm_brk + align - 1) & ~(align - 1); vm_brk = p + ((size + 0xfff) & ~0xfffu);
  if (vm_brk > VM_HI) gw_fatal("guest VM space exhausted");
  return p;
}
u32 gstrdup(const char *s) { size_t n = strlen(s) + 1; u32 p = heap_alloc(n, 0); memcpy(M + p, s, n); return p; }
u32 gwstrdup(const char *s) { size_t n = strlen(s) + 1; u32 p = heap_alloc(2 * n, 0); u2w(s, p, n); return p; }

// ---------------- strings ----------------
size_t gw_wcslen(u32 a) { size_t n = 0; while (r16(a + 2 * n)) n++; return n; }
static const char *cpname(int cp) {
  switch (cp) { case 0: case 1: case 3: case 1252: return "CP1252"; case 65001: return "UTF-8"; case 936: return "GBK";
    case 950: return "BIG5"; case 949: return "CP949"; case 932: return "SHIFT_JIS"; case 437: return "CP437"; default: return "CP1252"; }
}
int mb2wc(int cp, const u8 *src, int srclen, u16 *dst, int dstlen) {
  if (srclen < 0) srclen = strlen((const char *)src) + 1;
  iconv_t ic = iconv_open("UTF-16LE", cpname(cp));
  size_t outcap = (size_t)srclen * 4 + 4; char *out = malloc(outcap), *op = out; size_t ol = outcap;
  char *ip = (char *)src; size_t il = srclen;
  while (il > 0) {
    if (iconv(ic, &ip, &il, &op, &ol) == (size_t)-1) { if (il) { ip++; il--; *op++ = '?'; *op++ = 0; ol -= 2; } else break; }
  }
  iconv_close(ic);
  int n = (int)((op - out) / 2);
  if (dst && dstlen) { if (n > dstlen) n = 0; else memcpy(dst, out, n * 2); }
  free(out); return n;
}
int wc2mb(int cp, const u16 *src, int srclen, u8 *dst, int dstlen) {
  if (srclen < 0) { srclen = 0; while (src[srclen]) srclen++; srclen++; }
  iconv_t ic = iconv_open(cpname(cp), "UTF-16LE");
  size_t outcap = (size_t)srclen * 4 + 4; char *out = malloc(outcap), *op = out; size_t ol = outcap;
  char *ip = (char *)src; size_t il = (size_t)srclen * 2;
  while (il > 0) {
    if (iconv(ic, &ip, &il, &op, &ol) == (size_t)-1) { if (il >= 2) { ip += 2; il -= 2; *op++ = '?'; ol--; } else break; }
  }
  iconv_close(ic);
  int n = (int)(op - out);
  if (dst && dstlen) { if (n > dstlen) n = 0; else memcpy(dst, out, n); }
  free(out); return n;
}
char *g_w2u(u32 a) {
  if (!a) return NULL;
  int n = (int)gw_wcslen(a) + 1; u16 *tmp = malloc(n * 2); memcpy(tmp, M + a, n * 2);
  char *out = malloc(n * 4 + 1); int k = wc2mb(65001, tmp, n, (u8 *)out, n * 4 + 1); out[k > 0 ? k - 1 : 0] = 0; free(tmp); return out;
}
char *g_a2u(u32 a) {
  if (!a) return NULL;
  const char *s = (const char *)(M + a); int n = strlen(s) + 1;
  u16 *w = malloc(n * 2 + 2); int k = mb2wc(1252, (const u8 *)s, n, w, n);
  char *out = malloc(n * 4 + 1); int m = wc2mb(65001, w, k, (u8 *)out, n * 4 + 1); out[m > 0 ? m - 1 : 0] = 0; free(w); return out;
}
int u2w(const char *s, u32 dst, int dstchars) {
  int n = strlen(s) + 1; u16 *w = malloc(n * 2 + 2); int k = mb2wc(65001, (const u8 *)s, n, w, n);
  if (k > dstchars) { k = dstchars; if (k) w[k - 1] = 0; }
  memcpy(M + dst, w, k * 2); free(w); return k;
}

// ---------------- handles ----------------
#define MAXH 4096
static Handle htab[MAXH];
u32 h_new(int type) {
  for (int i = 1; i < MAXH; i++) if (htab[i].type == H_FREE) { memset(&htab[i], 0, sizeof htab[i]); htab[i].type = type; htab[i].fd = -1; return 0x1000 + 4 * i; }
  gw_fatal("out of handles");
  return 0;
}
Handle *h_get(u32 h) {
  if (h < 0x1000 || h >= 0x1000 + 4 * MAXH || (h & 3)) return NULL;
  Handle *x = &htab[(h - 0x1000) / 4]; return x->type == H_FREE ? NULL : x;
}
void h_close(u32 h) { Handle *x = h_get(h); if (!x) return; if (x->fd >= 0 && (x->type == H_FILE || x->type == H_MAPPING)) close(x->fd); free(x->path); x->type = H_FREE; }

// ---------------- paths ----------------
static int exists(const char *p) { struct stat st; return stat(p, &st) == 0; }
static void mkdirs_for(const char *path) {
  char tmp[2048]; snprintf(tmp, sizeof tmp, "%s", path);
  for (char *q = tmp + 1; *q; q++) if (*q == '/') { *q = 0; mkdir(tmp, 0755); *q = '/'; }
}
char *map_path(const char *gp, int forwrite) {
  char rel[2048]; size_t j = 0;
  const char *s = gp;
  if ((s[0] && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))) {   // C:\GodsWar\... -> relative
    s += 3; const char *k = strchr(s, '\\'); const char *k2 = strchr(s, '/');
    if (k2 && (!k || k2 < k)) k = k2;
    if (!strncasecmp(s, "GodsWar", 7) && k) s = k + 1;
  }
  for (; *s && j < sizeof rel - 1; s++) rel[j++] = (*s == '\\') ? '/' : *s;
  rel[j] = 0;
  char *r = rel; while (r[0] == '.' && r[1] == '/') r += 2; while (*r == '/') r++;
  // collapse "//" and "/./"
  char clean[2048]; size_t c = 0;
  for (size_t i = 0; r[i] && c < sizeof clean - 1; i++) {
    if (r[i] == '/' && c && clean[c - 1] == '/') continue;
    if (r[i] == '.' && r[i + 1] == '/' && (c == 0 || clean[c - 1] == '/')) { i++; continue; }
    clean[c++] = r[i];
  }
  clean[c] = 0;
  char *out = malloc(4200);
  snprintf(out, 4200, "%s/%s", g_run_dir, clean);
  if (forwrite) { mkdirs_for(out); return out; }
  if (exists(out)) return out;
  // GW_UI_OVERLAY (2026-11-21, console n01 "Compact layout"): a layout set such as Client/UIScale/1280x960 whose
  // files replace the client's same-named files for reading, so the client folder (and gwupdate's hashes) stay stock
  { static const char *ov = (const char *)-1; if (ov == (const char *)-1) ov = getenv("GW_UI_OVERLAY");
    if (ov && *ov) { snprintf(out, 4200, "%s/%s", ov, clean); if (exists(out)) return out; } }
  snprintf(out, 4200, "%s/%s", g_client_dir, clean);
  return out;
}

// ---------------- PE loader ----------------
static Module *mods;
static Module *init_order[32]; static int n_init;
Module *mod_find(const char *name) {
  const char *b = strrchr(name, '\\'); if (!b) b = strrchr(name, '/'); b = b ? b + 1 : name;
  for (Module *m = mods; m; m = m->next) {
    if (!strcasecmp(m->name, b)) return m;
    char t[80]; snprintf(t, sizeof t, "%s.dll", b); if (!strcasecmp(m->name, t)) return m;
  }
  return NULL;
}
Module *mod_by_base(u32 base) { for (Module *m = mods; m; m = m->next) if (m->base == base) return m; return NULL; }
const char *addr_sym(u32 a) {
  static char buf[8][96]; static int k; char *o = buf[k++ & 7];
  if (a >= THUNK_BASE && a < THUNK_BASE + THUNK_SIZE) {
    u32 i = (a - THUNK_BASE) / 8;
    if (a == MAGIC_RET) snprintf(o, 96, "<gcall-ret>"); else if (i < (u32)nthunks) snprintf(o, 96, "%s!%s", thunks[i].dll, thunks[i].name);
    else snprintf(o, 96, "thunk?%08x", a);
    return o;
  }
  for (Module *m = mods; m; m = m->next) if (m->guest && a >= m->base && a < m->base + m->size) {
    if (!strcasecmp(m->name, "Origin.exe")) snprintf(o, 96, "%08x", a); else snprintf(o, 96, "%s+%x", m->name, a - m->base);
    return o;
  }
  snprintf(o, 96, "%08x", a); return o;
}
static const char *GUEST_DLLS[] = {"Net.dll", "AudioSys.dll", "d3dx9_31.dll", "msvcr80.dll", "KMMonitor.dll", NULL};
static int is_guest_dll(const char *n, const char **canon) {
  if (!strcasecmp(n, "msvcrt.dll")) { *canon = "msvcr80.dll"; return 1; }
  for (int i = 0; GUEST_DLLS[i]; i++) if (!strcasecmp(n, GUEST_DLLS[i])) { *canon = GUEST_DLLS[i]; return 1; }
  return 0;
}
u32 pe_export(Module *m, const char *name) {
  u32 b = m->base, pe = b + r32(b + 0x3c), opt = pe + 24;
  u32 erva = r32(opt + 96), esz = r32(opt + 100); if (!erva) return 0;
  u32 e = b + erva, base = r32(e + 16), nf = r32(e + 20), nn = r32(e + 24);
  u32 af = b + r32(e + 28), an = b + r32(e + 32), ao = b + r32(e + 36);
  i32 idx = -1;
  if (name[0] == '#') idx = atoi(name + 1) - base;
  else for (u32 i = 0; i < nn; i++) if (!strcmp((char *)M + b + r32(an + 4 * i), name)) { idx = r16(ao + 2 * i); break; }
  if (idx < 0 || (u32)idx >= nf) return 0;
  u32 rva = r32(af + 4 * idx);
  if (rva >= erva && rva < erva + esz) {   // forwarder "DLL.func"
    char fw[128]; snprintf(fw, sizeof fw, "%s", (char *)M + b + rva); char *dot = strchr(fw, '.'); *dot = 0;
    char dll[80]; snprintf(dll, sizeof dll, "%s.dll", fw);
    const char *canon; if (is_guest_dll(dll, &canon)) { Module *t = pe_load(canon); return t ? pe_export(t, dot + 1) : 0; }
    return thunk_import(dll, dot + 1);
  }
  return b + rva;
}
static u32 next_dll_base = 0x05000000;
Module *pe_load(const char *name) {
  Module *m = mod_find(name); if (m) return m;
  char path[2048]; snprintf(path, sizeof path, "%s/%s", g_client_dir, name);
  FILE *f = fopen(path, "rb"); if (!f) { gw_log("pe_load: cannot open %s", path); return NULL; }
  fseek(f, 0, SEEK_END); long fl = ftell(f); fseek(f, 0, SEEK_SET);
  u8 *d = malloc(fl); fread(d, 1, fl, f); fclose(f);
  u32 pe; memcpy(&pe, d + 0x3c, 4);
  u16 nsec, osz; memcpy(&nsec, d + pe + 6, 2); memcpy(&osz, d + pe + 20, 2);
  u32 opt = pe + 24, pref, isz, hsz, ep; memcpy(&ep, d + opt + 16, 4); memcpy(&pref, d + opt + 28, 4); memcpy(&isz, d + opt + 56, 4); memcpy(&hsz, d + opt + 60, 4);
  u32 base = pref;
  for (Module *o = mods; o; o = o->next) if (!(base + isz <= o->base || base >= o->base + o->size)) { base = 0; break; }
  if (base == 0) { base = next_dll_base; next_dll_base = (next_dll_base + isz + 0xFFFFF) & ~0xFFFFFu; }
  m = calloc(1, sizeof *m); snprintf(m->name, sizeof m->name, "%s", name); m->base = base; m->size = isz; m->guest = 1;
  m->entry = ep ? base + ep : 0; m->next = mods; mods = m;
  memcpy(M + base, d, hsz);
  for (int i = 0; i < nsec; i++) {
    u32 o = opt + osz + 40 * i, vs, va, rs, ro; memcpy(&vs, d + o + 8, 4); memcpy(&va, d + o + 12, 4); memcpy(&rs, d + o + 16, 4); memcpy(&ro, d + o + 20, 4);
    u32 vsa = (vs + 0xfff) & ~0xfffu; u32 n = (!vs || rs < vsa) ? rs : vsa; if (ro + n > (u32)fl) n = fl - ro;  // like Windows: raw bytes up to the page end (patched data lives past VirtualSize)
    memcpy(M + base + va, d + ro, n);
  }
  free(d);
  gw_log("loaded %s at %08x (pref %08x) size %x", name, base, pref, isz);
  if (base != pref) {   // relocations
    u32 rr = r32(base + r32(base + 0x3c) + 24 + 96 + 5 * 8), rsz = r32(base + r32(base + 0x3c) + 24 + 96 + 5 * 8 + 4);
    i32 delta = (i32)(base - pref); u32 p = base + rr, end = p + rsz;
    if (!rr) gw_fatal("%s needs relocation but has none", name);
    while (p < end) {
      u32 page = r32(p), bs = r32(p + 4); if (!bs) break;
      for (u32 k = 8; k < bs; k += 2) { u16 e = r16(p + k); if ((e >> 12) == 3) { u32 a = base + page + (e & 0xfff); w32(a, r32(a) + delta); } }
      p += bs;
    }
    w32(base + r32(base + 0x3c) + 24 + 28, base);
  }
  // imports
  u32 ipe = base + r32(base + 0x3c) + 24, irva = r32(ipe + 96 + 8);
  for (u32 d0 = irva ? base + irva : 0; d0 && r32(d0 + 12); d0 += 20) {
    const char *dll = (const char *)M + base + r32(d0 + 12);
    u32 oft = r32(d0), ft = r32(d0 + 16), t = base + (oft ? oft : ft), iat = base + ft;
    const char *canon; Module *gm = NULL;
    if (is_guest_dll(dll, &canon)) { gm = pe_load(canon); if (!gm) gw_fatal("missing guest dll %s", canon); }
    for (; r32(t); t += 4, iat += 4) {
      u32 e = r32(t); char nm[128];
      if (e & 0x80000000u) snprintf(nm, sizeof nm, "#%u", e & 0xffff); else snprintf(nm, sizeof nm, "%s", (char *)M + base + e + 2);
      u32 a = gm ? pe_export(gm, nm) : thunk_import(dll, nm);
      if (!a) { gw_log("unresolved %s!%s (for %s) -> trap", dll, nm, name); a = thunk_import(dll, nm); }
      w32(iat, a);
    }
  }
  init_order[n_init++] = m;
  return m;
}

// ---------------- time ----------------
static struct timespec t0;
// GW_VCLOCK=<ms per Present>: deterministic virtual clock for A/B renders and frame-keyed scripts. Every time
// query advances it by 20 us (so polling loops progress) and each Present by the given ms.
static int vclock_ms = -1; static uint64_t vclock_us;
static int vclock_on(void) { if (vclock_ms < 0) vclock_ms = getenv("GW_VCLOCK") ? atoi(getenv("GW_VCLOCK")) : 0; return vclock_ms > 0; }
void clock_sleep(u32 ms) { if (vclock_on()) vclock_us += (uint64_t)ms * 1000; else if (ms) { u32 m = ms > 100 ? 100 : ms; g_sc.sleep_us += m * 1000; usleep(m * 1000); } }
SpikeCtr g_sc; int g_spike_ms;
void clock_on_present(void) { if (vclock_on()) vclock_us += (uint64_t)vclock_ms * 1000; }
uint64_t now_us(void) {
  if (vclock_on()) { vclock_us += 20; return vclock_us + 100000000ull; }
  struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)(t.tv_sec - t0.tv_sec) * 1000000 + (t.tv_nsec - t0.tv_nsec) / 1000 + 100000000ull; }
u32 now_ms(void) { return (u32)(now_us() / 1000); }
u32 wall_ms(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (u32)((t.tv_sec - t0.tv_sec) * 1000 + (t.tv_nsec - t0.tv_nsec) / 1000000); }

#include <sys/resource.h>
u32 cpu_ms(void) { struct rusage r; getrusage(RUSAGE_SELF, &r); return (u32)(r.ru_utime.tv_sec * 1000 + r.ru_utime.tv_usec / 1000 + r.ru_stime.tv_sec * 1000 + r.ru_stime.tv_usec / 1000); }
u32 tls_teb(void) { return TEB_ADDR; }
void set_last_error(u32 e) { w32(TEB_ADDR + 0x34, e); }

// ---------------- CPU setup ----------------
static void gdt_set(int i, u32 base, u32 limit, u8 access, u8 flags) {
  uint64_t dsc = (limit & 0xffff) | ((uint64_t)(base & 0xffffff) << 16) | ((uint64_t)access << 40) |
                 ((uint64_t)((limit >> 16) & 0xf) << 48) | ((uint64_t)flags << 52) | ((uint64_t)(base >> 24) << 56);
  memcpy(M + GDT_ADDR + 8 * i, &dsc, 8);
}
static void cpu_init(void) {
  uc_err e = uc_open(UC_ARCH_X86, UC_MODE_32, &uc); if (e) gw_fatal("uc_open %s", uc_strerror(e));
  if (getenv("GW_VTLB")) uc_ctl_tlb_mode(uc, UC_TLB_VIRTUAL);   // faster raw stores in smoke/bench.c, but 2x slower boot overall
  e = uc_mem_map_ptr(uc, ARENA_LO, ARENA_HI - ARENA_LO, UC_PROT_ALL, M + ARENA_LO); if (e) gw_fatal("map arena %s", uc_strerror(e));
  e = uc_mem_map_ptr(uc, THUNK_BASE, THUNK_SIZE, UC_PROT_ALL, M + THUNK_BASE); if (e) gw_fatal("map thunks %s", uc_strerror(e));
  gdt_set(1, 0, 0xfffff, 0x9b, 0xc); gdt_set(2, 0, 0xfffff, 0x93, 0xc); gdt_set(3, TEB_ADDR, 0xfff, 0x93, 0x4);
  uc_x86_mmr gdtr = {0, GDT_ADDR, 8 * 8 - 1, 0}; uc_reg_write(uc, UC_X86_REG_GDTR, &gdtr);
  int cs = 1 << 3, ds = 2 << 3, fs = 3 << 3;
  uc_reg_write(uc, UC_X86_REG_CS, &cs); uc_reg_write(uc, UC_X86_REG_SS, &ds); uc_reg_write(uc, UC_X86_REG_DS, &ds);
  uc_reg_write(uc, UC_X86_REG_ES, &ds); uc_reg_write(uc, UC_X86_REG_GS, &ds); uc_reg_write(uc, UC_X86_REG_FS, &fs);
  // TEB / PEB
  w32(TEB_ADDR + 0x00, 0xFFFFFFFF); w32(TEB_ADDR + 0x04, STACK_TOP); w32(TEB_ADDR + 0x08, STACK_TOP - 0x400000);
  w32(TEB_ADDR + 0x18, TEB_ADDR); w32(TEB_ADDR + 0x20, 0x1234); w32(TEB_ADDR + 0x24, 0x1238);
  w32(TEB_ADDR + 0x2C, TEB_ADDR + 0xE10); w32(TEB_ADDR + 0x30, PEB_ADDR);
  w32(PEB_ADDR + 0x08, 0x400000); w32(PEB_ADDR + 0x18, 0x00150000);
  w32(PEB_ADDR + 0xA4, 5); w32(PEB_ADDR + 0xA8, 1); w32(PEB_ADDR + 0xAC, 2600); w32(PEB_ADDR + 0xB0, 2);  // XP SP? os version
  wr(UC_X86_REG_ESP, STACK_TOP - 0x100); wr(UC_X86_REG_EBP, 0);
  { u32 cw = 0x27F; uc_reg_write(uc, UC_X86_REG_FPCW, &cw); u32 mx = 0x1F80; uc_reg_write(uc, UC_X86_REG_MXCSR, &mx);
    u32 v; uc_reg_read(uc, UC_X86_REG_FPCW, &v); gw_log("FPU control word %04x", v); }
  M[MAGIC_RET] = 0xF4; M[MAGIC_EXIT] = 0xF4;
  for (int k = 0; k < NCO; k++) { M[CO_TRAP(k)] = 0xCD; M[CO_TRAP(k) + 1] = 0x2E; }
  uc_hook h;
  uc_hook_add(uc, &h, UC_HOOK_INTR, hook_intr, NULL, 1, 0);
  uc_hook_add(uc, &h, UC_HOOK_INSN, hook_sysenter, NULL, 1, 0, UC_X86_INS_SYSENTER);
  uc_hook_add(uc, &h, UC_HOOK_MEM_INVALID, hook_badmem, NULL, 1, 0);
  uc_hook_add(uc, &h, UC_HOOK_INSN_INVALID, hook_badins, NULL, 1, 0);
}

extern void k32_init(const char *cmdline);
#include <execinfo.h>
static void on_sig(int s, siginfo_t *si, void *uctx) {
  (void)uctx; void *bt[32]; int n = backtrace(bt, 32);
  long off = (long)((u8 *)si->si_addr - M);
  fprintf(LOG, "host signal %d addr=%p (arena off %lx)\n", s, si->si_addr, off); fflush(LOG);
  backtrace_symbols_fd(bt, n, fileno(LOG)); dump_regs(); fflush(LOG); _exit(5); }

#ifdef GW_PATCHED_UC
extern int gw_smc_tracking, gw_memop_exit_check, gw_tb_exit_inline, gw_x87_fast;
#endif
static uc_err run_guest(u32 start); static int selftest_preempt(void);
void gw_flush_code(u32 a, u32 n) { if (n) uc_ctl_remove_cache(uc, a, (uint64_t)a + n); }
// macOS QoS: USER_INTERACTIVE makes the scheduler prefer performance cores for the emulator and raster threads.
// Measured 2026-11-02: the same world run took 34-41 ms CPU/frame on the emulator thread in "good" runs and
// 65-74 ms in runs whose profile shows the signature of an efficiency core (helper calls 2-3x slower).
// GW_QOS=0 keeps the default class (A/B).
void gw_set_qos(void) { const char *e = getenv("GW_QOS"); if (!e || atoi(e)) pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0); }
// raster workers one class lower: when performance cores are scarce the serial emulator thread gets them first
void gw_set_qos_workers(void) { const char *e = getenv("GW_QOS"); if (!e || atoi(e)) pthread_set_qos_class_self_np(getenv("GW_QOS_WI") && atoi(getenv("GW_QOS_WI")) ? QOS_CLASS_USER_INTERACTIVE : QOS_CLASS_USER_INITIATED, 0); }
int gw_main(int argc, char **argv) {
  gw_set_qos();
  const char *client = argc > 1 ? argv[1] : NULL, *run = argc > 2 ? argv[2] : "run";
  if (!client) { fprintf(stderr, "usage: gwhost <client-dir> [run-dir] [exe]\n"); return 2; }
  const char *exe = argc > 3 ? argv[3] : "Origin.exe";
#ifdef GW_PATCHED_UC
  if (getenv("GW_SMC")) gw_smc_tracking = 1;          // self-modifying-code detection (slow store path)
  if (getenv("GW_MEMEXIT")) gw_memop_exit_check = 1;  // stop right at a faulting load/store
  if (getenv("GW_TBHELPER")) gw_tb_exit_inline = 0;
  // x87 add/sub/mul/div and float/double loads/stores in host double when exact (53/24-bit precision, round to
  // nearest, normal operands; see fpu_helper.c). Default ON since 2026-11-02 (VCLOCK login frames bit-identical
  // to softfloat; world profile: x87 share of the emulator thread ~12 % -> ~9 %). GW_X87FAST=0 = exact softfloat.
  gw_x87_fast = getenv("GW_X87FAST") ? atoi(getenv("GW_X87FAST")) != 0 : 1;
#endif
  realpath(client, g_client_dir); mkdir(run, 0755); realpath(run, g_run_dir);
  g_trace = getenv("GW_TRACE") ? atoi(getenv("GW_TRACE")) : 0;
  char lp[2048]; snprintf(lp, sizeof lp, "%s/gwhost.log", g_run_dir);
  { char pp[2100]; snprintf(pp, sizeof pp, "%s/gwhost.prev.log", g_run_dir); rename(lp, pp); }   // keep the previous session's log
  LOG = fopen(lp, "w");
  snprintf(lp, sizeof lp, "%s/observed-imports.txt", g_run_dir); OBS = fopen(lp, "w");
  struct sigaction sa; memset(&sa, 0, sizeof sa); sa.sa_sigaction = on_sig; sa.sa_flags = SA_SIGINFO; sigaction(SIGSEGV, &sa, NULL); sigaction(SIGBUS, &sa, NULL);
  clock_gettime(CLOCK_MONOTONIC, &t0);
  M = mmap(NULL, 1ull << 32, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON | MAP_NORESERVE, -1, 0);
  if (M == MAP_FAILED) { perror("mmap"); return 1; }
  thunks = calloc(MAXTHUNK, sizeof(Thunk));
  cpu_init();
  char cmdline[512]; snprintf(cmdline, sizeof cmdline, "\"C:\\GodsWar\\%s\"", exe);
  k32_init(cmdline);
  d3d_init_vtables();
  if (getenv("GW_SELFTEST") && !strcmp(getenv("GW_SELFTEST"), "preempt")) return selftest_preempt();
  if (getenv("GW_SELFTEST") && !strcmp(getenv("GW_SELFTEST"), "prims")) { extern int d3d_selftest_prims(void); return d3d_selftest_prims(); }
  Module *em = pe_load(exe); if (!em) return 1;
  if (getenv("GW_SELFTEST") && !strcmp(getenv("GW_SELFTEST"), "cull")) { extern int selftest_cull(void); return selftest_cull(); }
  // DllMain for guest DLLs (dependencies were loaded first; mods list is reverse load order)
  Module **order = init_order; int no = n_init;
  for (int i = 0; i < no; i++) if (order[i] != em && order[i]->entry) {
    gw_log("DllMain %s", order[i]->name);
    u32 r = gcall(order[i]->entry, 3, order[i]->base, 1, 0);
    gw_log("DllMain %s -> %u", order[i]->name, r);
  }
  gw_log("entry %08x", em->entry);
  u32 sp = rd(UC_X86_REG_ESP) - 4; w32(sp, MAGIC_EXIT); wr(UC_X86_REG_ESP, sp);
  uc_err e = run_guest(em->entry);
  gw_log("emulation ended: %s at %s", uc_strerror(e), addr_sym(rd(UC_X86_REG_EIP)));
  dump_regs();
  return e == UC_ERR_OK ? 0 : 1;
}
// The top-level emulation loop. The preemption watchdog (thread.c) stops the engine when one guest thread ran
// a whole timeslice while others exist; we then switch threads (thread_preempt) and restart at the new EIP.
// A preempt stop while a coroutine shim waits for a guest callback only restarts the same thread.
extern volatile int g_preempt_req; extern int thread_preempt(void);
int co_any_waiting(void) { for (int i = 0; i < NCO; i++) if (cos[i].used) return 1; return 0; }
// ---- GW_PROF=<ms>: guest sampling profiler (2026-11-16) ----
// A host thread stops the engine every <ms> (same async stop as preemption) while the Present count is inside
// [GW_PROF_FROM, GW_PROF_TO) (defaults 0 / unlimited). Each stop records EIP ("self") and, for "inclusive", every
// dword in the top 4 KB of the guest stack that looks like a return address into the main exe (preceded by a CALL
// rel32 / CALL r/m32 encoding), counted once per sample. Written to <run>/prof.tsv at exit and every 500 samples:
// kind<TAB>addr<TAB>count. tools/prof_report.py maps addresses to Ghidra functions (decomp/out/index.tsv).
// Sampling restarts at the same EIP and thread, so emulation is unchanged except for the stop/start cost.
volatile int g_prof_req; static int prof_ms, prof_from, prof_to = 1 << 30; static u32 prof_lo, prof_hi;
static unsigned long prof_samples;
#define PROF_TAB 65536
typedef struct { u32 a; u32 n; } ProfEnt; static ProfEnt prof_self[PROF_TAB], prof_incl[PROF_TAB];
static void prof_add(ProfEnt *t, u32 a) { u32 h = (a * 2654435761u) & (PROF_TAB - 1);
  for (int i = 0; i < PROF_TAB; i++, h = (h + 1) & (PROF_TAB - 1)) { if (t[h].a == a) { t[h].n++; return; } if (!t[h].a) { t[h].a = a; t[h].n = 1; return; } } }
static void prof_dump(void) { char p[2048]; snprintf(p, sizeof p, "%s/prof.tsv", g_run_dir); FILE *f = fopen(p, "w"); if (!f) return;
  fprintf(f, "samples\t0\t%lu\n", prof_samples);
  for (int i = 0; i < PROF_TAB; i++) { if (prof_self[i].a) fprintf(f, "self\t%08x\t%u\n", prof_self[i].a, prof_self[i].n);
    if (prof_incl[i].a) fprintf(f, "incl\t%08x\t%u\n", prof_incl[i].a, prof_incl[i].n); }
  fclose(f); }
static int is_ret_addr(u32 v) { if (v < prof_lo + 6 || v >= prof_hi) return 0; const u8 *b = M + v;
  return b[-5] == 0xE8 || (b[-6] == 0xFF && (b[-5] & 0x38) == 0x10) || (b[-2] == 0xFF && (b[-1] & 0x38) == 0x10 && (b[-1] & 0xC0) == 0xC0)
      || (b[-3] == 0xFF && (b[-2] & 0x38) == 0x10) || (b[-7] == 0xFF && (b[-6] & 0x38) == 0x10); }
#define SPK_N 8192
static u32 spk_eip[SPK_N], spk_ra[SPK_N][3]; static int spk_n;   // this frame's samples (GW_SPIKE + GW_PROF)
void spike_samples_reset(void) { spk_n = 0; }
static void spk_top(const char *what, int col) {   // col -1 = eip, else return-address depth
  u32 a[16]; int n[16], k = 0;
  for (int i = 0; i < spk_n; i++) { u32 v = col < 0 ? spk_eip[i] : spk_ra[i][col]; if (!v) continue; int j; for (j = 0; j < k; j++) if (a[j] == v) { n[j]++; break; }
    if (j == k && k < 16) { a[k] = v; n[k++] = 1; } }
  char b[600]; int o = 0; for (int r = 0; r < 5 && k; r++) { int bi = 0; for (int j = 1; j < k; j++) if (n[j] > n[bi]) bi = j;
    o += snprintf(b + o, sizeof b - o, " %08x:%d", a[bi], n[bi]); n[bi] = -1; if (o > 500) break; }
  gw_log("  spike %s (%d samples):%s", what, spk_n, b); }
void spike_samples_log(void) { if (!spk_n) return; spk_top("eip", -1); spk_top("ret1", 0); spk_top("ret2", 1); spk_top("ret3", 2); }
static void prof_sample(u32 eip) {
  prof_samples++; prof_add(prof_self, eip);
  if (g_spike_ms && spk_n < SPK_N) { spk_eip[spk_n] = eip; spk_ra[spk_n][0] = spk_ra[spk_n][1] = spk_ra[spk_n][2] = 0; }
  u32 esp = rd(UC_X86_REG_ESP), seen[64]; int ns = 0;
  for (u32 a = esp; a < esp + 4096 && ns < 64; a += 4) { u32 v = r32(a); if (!is_ret_addr(v)) continue;
    int dup = 0; for (int i = 0; i < ns; i++) if (seen[i] == v) { dup = 1; break; } if (dup) continue; seen[ns++] = v; prof_add(prof_incl, v); }
  if (g_spike_ms && spk_n < SPK_N) { for (int i = 0; i < 3 && i < ns; i++) spk_ra[spk_n][i] = seen[i]; spk_n++; }
  if (prof_samples % 500 == 0) prof_dump(); }
extern int g_present_frames; extern void gw_uc_async_stop(uc_engine *uc);
static void *prof_thread(void *arg) { (void)arg;
  for (;;) { usleep((useconds_t)prof_ms * 1000); if (g_present_frames >= prof_from && g_present_frames < prof_to && in_emu) { g_prof_req = 1; gw_uc_async_stop(uc); } } return NULL; }
static void prof_start(void) { const char *e = getenv("GW_PROF"); if (!e || (prof_ms = atoi(e)) <= 0) return;
  if (getenv("GW_PROF_FROM")) prof_from = atoi(getenv("GW_PROF_FROM")); if (getenv("GW_PROF_TO")) prof_to = atoi(getenv("GW_PROF_TO"));
  Module *m = mod_by_base(0x400000); if (m) { prof_lo = m->base + 0x1000; prof_hi = m->base + m->size; }
  atexit(prof_dump); pthread_t th; pthread_create(&th, NULL, prof_thread, NULL); pthread_detach(th);
  gw_log("profiler: every %d ms, frames [%d,%d), exe %08x-%08x", prof_ms, prof_from, prof_to, prof_lo, prof_hi); }
// ---- GW_CALLCOUNT=<hexaddr>[,<hexaddr>...]: count calls to these guest functions by return address ----
// A UC_HOOK_CODE on each entry address reads [esp] (the return address) and counts (function, caller) pairs while
// the Present count is inside [GW_PROF_FROM, GW_PROF_TO). Written to <run>/callcount.tsv at exit and every 20000 calls.
#define CC_TAB 16384
typedef struct { u32 fn, ra, n; } CCEnt; static CCEnt cc_tab[CC_TAB]; static unsigned long cc_total;
static void cc_dump(void) { char p[2048]; snprintf(p, sizeof p, "%s/callcount.tsv", g_run_dir); FILE *f = fopen(p, "w"); if (!f) return;
  for (int i = 0; i < CC_TAB; i++) if (cc_tab[i].fn) fprintf(f, "%08x\t%08x\t%u\n", cc_tab[i].fn, cc_tab[i].ra, cc_tab[i].n); fclose(f); }
static void cc_hook(uc_engine *u, uint64_t addr, uint32_t size, void *ud) { (void)u; (void)size; (void)ud;
  if (g_present_frames < prof_from || g_present_frames >= prof_to) return;
  u32 fn = (u32)addr, ra = r32(rd(UC_X86_REG_ESP)); u32 sp0 = rd(UC_X86_REG_ESP);
  if (0) { float a1, a2; memcpy(&a1, M + sp0 + 4, 4); memcpy(&a2, M + sp0 + 8, 4);
    gw_log("callcount %08x from %08x ecx=%08x arg1=%08x (%g) arg2=%08x (%g)", fn, ra, rd(UC_X86_REG_ECX), r32(sp0 + 4), a1, r32(sp0 + 8), a2); }
  u32 h = ((fn ^ ra) * 2654435761u) & (CC_TAB - 1);
  for (int i = 0; i < CC_TAB; i++, h = (h + 1) & (CC_TAB - 1)) { if (cc_tab[h].fn == fn && cc_tab[h].ra == ra) { if (++cc_tab[h].n <= 3) { float a1, a2; memcpy(&a1, M + sp0 + 4, 4); memcpy(&a2, M + sp0 + 8, 4); gw_log("callcount %08x from %08x arg1=%g arg2=%g", fn, ra, a1, a2); } break; }
    if (!cc_tab[h].fn) { cc_tab[h].fn = fn; cc_tab[h].ra = ra; cc_tab[h].n = 1; break; } }
  if (++cc_total % 20000 == 0) cc_dump(); }
static void cc_start(void) { const char *e = getenv("GW_CALLCOUNT"); if (!e) return; char buf[512]; snprintf(buf, sizeof buf, "%s", e);
  if (getenv("GW_PROF_FROM")) prof_from = atoi(getenv("GW_PROF_FROM")); if (getenv("GW_PROF_TO")) prof_to = atoi(getenv("GW_PROF_TO"));
  for (char *t = strtok(buf, ","); t; t = strtok(NULL, ",")) { u32 a = (u32)strtoul(t, NULL, 16); uc_hook h;
    if (uc_hook_add(uc, &h, UC_HOOK_CODE, (void *)cc_hook, NULL, a, a) == UC_ERR_OK) gw_log("callcount hook %08x", a); else gw_log("callcount hook %08x FAILED", a); }
  atexit(cc_dump); }
// ---- GW_SKIPSCALE=1|2 (experiment, 2026-11-16, MAP-LOAD-RE.md): skip the whole-UI Scale(1,1) calls in lua_AddItem
// (call sites 0x6986be, 0x698757) and lua_AddText (0x698009, 0x6980c9) when both float args are 1.0.
// 1 = skip both calls of each pair; 2 = skip only the first (the "unscale" before the add). Callees are thiscall ret 8.
static int skipscale_mode; static unsigned long skipscale_n;
// Writing EIP from a code hook is not honoured by the patched unicorn (the first try popped the args but still made the
// call and crashed), so the experiment patches guest code instead: each 2-byte `call reg` becomes `pop reg; pop reg`,
// which drops the two float args exactly like the callee's `ret 8`; the register is dead after the call (caller-saved
// and overwritten next). Unconditional: only valid while the UI scale is 1.0 (true at 1024x768).
// ---- in-memory UI rescale fix, ON by default (GW_UIFIX=0 = off): the same bytes as tools/uiscale_skip_patch.py,
// written into guest memory after load when the four call sites hold the stock bytes (an exe already patched on disk
// is left as it is). Stubs skip lua_AddItem/AddText's whole-UI Scale walks only when both args are exactly 1.0;
// proof and timings in MAP-LOAD-RE.md (world entry in the emulator ~2x faster, no window field changes).
static void uifix_apply(void) {
  const char *e = getenv("GW_UIFIX"); if (e && !atoi(e)) return;
  static const u8 stubE[29] = {0xd9,0x5c,0x24,0x04, 0x81,0x7c,0x24,0x04,0x00,0x00,0x80,0x3f, 0x75,0x0d,
    0x81,0x7c,0x24,0x08,0x00,0x00,0x80,0x3f, 0x75,0x03, 0xc2,0x08,0x00, 0xff,0xe2};
  static const struct { u32 va; u8 reg; } sites[] = {{0x6986bb,0xd2},{0x698754,0xd0},{0x698006,0xd2},{0x6980c6,0xd0}};
  const u32 caveE = 0x91bf00, caveA = 0x91bf20;
  for (int i = 0; i < 4; i++) { const u8 *p = M + sites[i].va; if (!(p[0]==0xd9 && p[1]==0x1c && p[2]==0x24 && p[3]==0xff && p[4]==sites[i].reg)) { gw_log("uifix: site %08x not stock, left alone", sites[i].va); return; } }
  for (int i = 0; i < 64; i++) if (M[caveE + i]) { gw_log("uifix: cave not empty, left alone"); return; }
  memcpy(M + caveE, stubE, 29); memcpy(M + caveA, stubE, 29); M[caveA + 28] = 0xe0;
  for (int i = 0; i < 4; i++) { u32 va = sites[i].va, tgt = sites[i].reg == 0xd2 ? caveE : caveA; i32 rel = (i32)(tgt - (va + 5));
    M[va] = 0xe8; memcpy(M + va + 1, &rel, 4); }
  gw_flush_code(0x697f00, 0x900); gw_flush_code(caveE, 0x40); gw_log("uifix: UI rescale skip applied in memory (GW_UIFIX=0 to disable)"); }
// ---- GW_UI_OVERLAY's exe-bytes.txt ("<file offset> <stock hex> <new hex>" per line, the same file the Wine app's
// set_ui writes to disk): applied in guest memory, all or nothing (every site must hold its stock bytes first).
static void uiset_apply(void) {
  const char *ov = getenv("GW_UI_OVERLAY"); if (!ov || !*ov) return;
  char path[1200]; snprintf(path, sizeof path, "%s/exe-bytes.txt", ov); FILE *f = fopen(path, "r");
  if (!f) { gw_log("uiset: no %s", path); return; }
  u32 b = 0x400000, pe = b + r32(b + 0x3c); int ns = r16(pe + 6); u32 sec = pe + 24 + r16(pe + 20);
  struct { u32 va; u8 stock[32], neu[32]; int n; } site[256]; int nsite = 0; char line[256];
  while (fgets(line, sizeof line, f) && nsite < 256) {
    unsigned long off; char hs[80], hn[80]; if (sscanf(line, "%lu %79s %79s", &off, hs, hn) != 3) continue;
    int n = (int)strlen(hs) / 2; if (n > 32 || (int)strlen(hn) != 2 * n) continue;
    u32 va = 0; for (int i = 0; i < ns; i++) { u32 h = sec + 40 * i, rva = r32(h + 12), rsz = r32(h + 16), roff = r32(h + 20);
      if (off >= roff && off < roff + rsz) { va = b + rva + (u32)(off - roff); break; } }
    if (!va) { gw_log("uiset: offset %lu outside the sections, nothing applied", off); fclose(f); return; }
    site[nsite].va = va; site[nsite].n = n;
    for (int i = 0; i < n; i++) { unsigned x, y; sscanf(hs + 2 * i, "%2x", &x); sscanf(hn + 2 * i, "%2x", &y); site[nsite].stock[i] = x; site[nsite].neu[i] = y; }
    nsite++; }
  fclose(f);
  for (int i = 0; i < nsite; i++) if (memcmp(M + site[i].va, site[i].stock, site[i].n)) { gw_log("uiset: %08x not stock, layout left stock", site[i].va); return; }
  for (int i = 0; i < nsite; i++) { memcpy(M + site[i].va, site[i].neu, site[i].n); gw_flush_code(site[i].va & ~15u, 64); }
  gw_log("uiset: %d exe sites patched in memory for %s", nsite, ov);
}
// ---- in-memory SSE culling, ON by default (GW_CULLFIX=0 = off): 0x7760d0 -> patches/cull_sse.bin, exact (GW_SELFTEST=cull)
#include "cull_sse_bin.h"
static void cullfix_apply(void) {
  const char *e = getenv("GW_CULLFIX"); if (e && !atoi(e)) return;
  if (memcmp(M + 0x7760d0, cull_orig_head, 32)) { gw_log("cullfix: 0x7760d0 not the stock code, left alone"); return; }
  memcpy(M + 0x7760d0, cull_sse_bin, sizeof cull_sse_bin); memset(M + 0x7760d0 + sizeof cull_sse_bin, 0xcc, 0x3b0 - sizeof cull_sse_bin);
  gw_flush_code(0x7760d0, 0x3b0); gw_log("cullfix: SSE frustum test applied in memory (GW_CULLFIX=0 to disable)"); }
static void skipscale_start(void) { const char *e = getenv("GW_SKIPSCALE"); if (!e || !(skipscale_mode = atoi(e))) return;
  static const u32 sites[] = {0x6986be, 0x698009, 0x698757, 0x6980c9}; // first of each pair, then second
  for (int i = 0; i < (skipscale_mode == 2 ? 2 : 4); i++) { u8 *p = M + sites[i];
    if (p[0] != 0xff || (p[1] & 0xf8) != 0xd0) gw_fatal("skipscale: no call reg at %08x", sites[i]);
    u8 pop = 0x58 + (p[1] & 7); p[0] = pop; p[1] = pop; gw_flush_code(sites[i], 2); skipscale_n++; }
  gw_log("skipscale mode %d: %lu call sites patched", skipscale_mode, skipscale_n); }
unsigned long gw_skipscale_count(void) { return skipscale_n; }
// ---- GW_SCALEDIFF=1 (check, 2026-11-16): around each of those four Scale calls, snapshot every window reachable from
// the UI root ([0x1576154]; children = pointer vector +0x164..+0x168) - rect +0x98..+0xb4 and ratios +0x178/+0x17c -
// before the call and at its return address, and log how many windows changed and which. Answers: does the call change
// anything, and if so only the new item / the list's own subtree?
#define SD_MAX 32768
#define SD_F 92  /* dwords +0x60..+0x1d0 */
typedef struct { u32 w, par; u32 f[SD_F]; } SdEnt; static SdEnt *sd_before; static int sd_nb; static u32 sd_site; static unsigned long sd_calls, sd_changed_calls;
static int sd_walk(SdEnt *out) { int n = 0; u32 stack[4096]; int sp = 0; u32 root = r32(0x1576154); if (!root) return 0; root += 4; /* 0x6e79f0: the manager scales its IGUIWindow base at +4 */ stack[sp++] = root;
  while (sp && n < SD_MAX) { u32 w = stack[--sp]; SdEnt *e = &out[n++]; e->w = w; e->par = r32(w + 0x50);
    for (int i = 0; i < SD_F; i++) e->f[i] = r32(w + 0x60 + 4 * i);
    u32 b = r32(w + 0x164), en = r32(w + 0x168); if (en < b || en - b > 4 * 4096) continue;
    for (u32 c = b; c < en && sp < 4096; c += 4) { u32 ch = r32(c); if (ch > 0x10000 && ch < 0xfff00000u) stack[sp++] = ch; } }
  return n; }
static int sd_cmp(const void *a, const void *b) { u32 x = ((const SdEnt *)a)->w, y = ((const SdEnt *)b)->w; return x < y ? -1 : x > y; }
static void sd_before_hook(uc_engine *u, uint64_t addr, uint32_t size, void *ud) { (void)u; (void)size; (void)ud;
  if (g_present_frames < prof_from || g_present_frames >= prof_to) return;
  if (!sd_before) sd_before = malloc(sizeof(SdEnt) * SD_MAX); sd_nb = sd_walk(sd_before); qsort(sd_before, sd_nb, sizeof(SdEnt), sd_cmp); sd_site = (u32)addr; }
static void sd_after_hook(uc_engine *u, uint64_t addr, uint32_t size, void *ud) { (void)u; (void)size; (void)ud;
  if (!sd_site || sd_site + 2 != (u32)addr) return; static SdEnt *aft; if (!aft) aft = malloc(sizeof(SdEnt) * SD_MAX);
  int na = sd_walk(aft); int changed = 0, added = 0; u32 ex[4] = {0}; u32 exp[4] = {0}; int exf[4] = {0};
  for (int i = 0; i < na; i++) { SdEnt key = aft[i]; SdEnt *b = bsearch(&key, sd_before, sd_nb, sizeof(SdEnt), sd_cmp);
    if (!b) { added++; continue; } int df = 0; for (int k = 0; k < SD_F; k++) if (b->f[k] != aft[i].f[k]) { df = 0x60 + 4 * k; break; }
    if (df) { if (changed < 4) { ex[changed] = aft[i].w; exp[changed] = aft[i].par; exf[changed] = df; } changed++; } }
  sd_calls++; if (changed) sd_changed_calls++;
  if (changed || sd_calls <= 6) gw_log("scalediff site %08x: windows %d -> %d, changed %d, new %d; e.g. %08x(par %08x fields %03x) %08x(par %08x fields %03x) %08x(par %08x) %08x(par %08x)",
    sd_site, sd_nb, na, changed, added, ex[0], exp[0], exf[0], ex[1], exp[1], exf[1], ex[2], exp[2], ex[3], exp[3]);
  sd_site = 0; }
static void sd_report(void) { if (sd_calls) gw_log("scalediff total: %lu calls checked, %lu changed any window", sd_calls, sd_changed_calls); }
static void scalediff_start(void) { if (!getenv("GW_SCALEDIFF")) return;
  if (getenv("GW_PROF_FROM")) prof_from = atoi(getenv("GW_PROF_FROM")); if (getenv("GW_PROF_TO")) prof_to = atoi(getenv("GW_PROF_TO"));
  static const u32 sites[] = {0x6986be, 0x698757, 0x698009, 0x6980c9};
  for (int i = 0; i < 4; i++) { uc_hook h; uc_hook_add(uc, &h, UC_HOOK_CODE, (void *)sd_before_hook, NULL, sites[i], sites[i]);
    uc_hook_add(uc, &h, UC_HOOK_CODE, (void *)sd_after_hook, NULL, sites[i] + 2, sites[i] + 2); }
  atexit(sd_report); gw_log("scalediff on"); }
static uc_err run_guest(u32 start) {
  u32 eip = start; uc_err e; static int prof_init; if (!prof_init) { prof_init = 1; prof_start(); uifix_apply(); cullfix_apply(); uiset_apply(); cc_start(); skipscale_start(); scalediff_start(); }
  for (;;) {
    in_emu = 1; e = uc_emu_start(uc, eip, MAGIC_EXIT, 0, 0); in_emu = 0;
    eip = rd(UC_X86_REG_EIP);
    if (e != UC_ERR_OK || eip == MAGIC_EXIT || (!g_preempt_req && !g_prof_req)) break;
    if (g_prof_req) { g_prof_req = 0; prof_sample(eip); }
    if (!g_preempt_req) continue;
    g_preempt_req = 0;
    if (!co_any_waiting()) thread_preempt();
    eip = rd(UC_X86_REG_EIP);
  }
  return e;
}
// GW_SELFTEST=preempt: two hand-assembled guest threads that never call a blocking shim. The main thread
// creates thread B and spins until B sets a flag; B sets it and spins forever. Without preemption the main
// thread spins forever (B never runs); with it the main thread returns within a few timeslices.
static int selftest_preempt(void) {
  u32 code = vm_alloc(0x1000, 0x1000), flag = code + 0x800, startB = code + 0x100, ct = thunk_for("KERNEL32", "CreateThread");
  if (!ct) gw_fatal("selftest: no CreateThread thunk");
  u8 m[] = {0x6A,0, 0x6A,0, 0x6A,0, 0x68,0,0,0,0, 0x6A,0, 0x6A,0, 0xB8,0,0,0,0, 0xFF,0xD0,
            0x83,0x3D,0,0,0,0,0x00, 0x74,0xF7, 0xC3};
  memcpy(m + 7, &startB, 4); memcpy(m + 16, &ct, 4); memcpy(m + 24, &flag, 4);
  u8 b[] = {0xC7,0x05,0,0,0,0, 1,0,0,0, 0xEB,0xFE};
  memcpy(b + 2, &flag, 4);
  memcpy(M + code, m, sizeof m); memcpy(M + startB, b, sizeof b); w32(flag, 0);
  u32 sp = rd(UC_X86_REG_ESP) - 4; w32(sp, MAGIC_EXIT); wr(UC_X86_REG_ESP, sp);
  u32 t0 = wall_ms(); uc_err e = run_guest(code); u32 dt = wall_ms() - t0;
  int ok = e == UC_ERR_OK && rd(UC_X86_REG_EIP) == MAGIC_EXIT && r32(flag) == 1;
  gw_log("selftest preempt: %s (%s, flag=%u, %u ms)", ok ? "PASS" : "FAIL", uc_strerror(e), r32(flag), dt);
  fprintf(stderr, "selftest preempt: %s (flag=%u, %u ms)\n", ok ? "PASS" : "FAIL", r32(flag), dt);
  fflush(LOG); return ok ? 0 : 1;
}

#ifndef GW_APP
int main(int argc, char **argv) { return gw_main(argc, argv); }
#endif

// GW_SELFTEST=cull GW_CULLBIN=patches/cull_sse.bin (2026-11-16, WORLD-FRAME-RE.md): differential test of the SSE
// replacement for 0x7760d0 against the original x87 code (run with GW_X87FAST=0 for exact x87) on random and edge-case
// planes/boxes, with the x87 precision set to 24 bit (Direct3D under Windows/Wine) and 53 bit (emulator default).
static u32 st_rng = 12345; static u32 rnd(void) { st_rng ^= st_rng << 13; st_rng ^= st_rng >> 17; st_rng ^= st_rng << 5; return st_rng; }
static float cull_rf(float lo, float hi) { return lo + (hi - lo) * (rnd() & 0xffffff) / 16777216.0f; }
int selftest_cull(void) {
  const char *bp = getenv("GW_CULLBIN"); FILE *f = bp ? fopen(bp, "rb") : NULL; if (!f) { fprintf(stderr, "GW_CULLBIN?\n"); return 2; }
  u32 code = vm_alloc(0x1000, 0x1000), data = vm_alloc(0x1000, 0x1000); size_t n = fread(M + code, 1, 0x800, f); fclose(f);
  static const u8 setcw[] = {0x8b,0x44,0x24,0x04, 0x50, 0xd9,0x2c,0x24, 0x58, 0xc3}; memcpy(M + code + 0x800, setcw, sizeof setcw);
  w32(0xb3b0d8, r32(0xb3b0d8) | 0x1f);   // skip the original's static-init (atexit before CRT init)
  u32 P = data, N = data + 0x100; long bad = 0, total = 0, hist[3][3] = {{0}};
  for (int cw = 0; cw < 2; cw++) { gcall(code + 0x800, 1, cw ? 0x027f : 0x007f);
    for (int it = 0; it < 200000; it++) {
      float pl[24], nd[12]; int mode = it % 4;
      for (int i = 0; i < 6; i++) { float x = cull_rf(-1, 1), y = cull_rf(-1, 1), z = cull_rf(-1, 1);
        if (mode == 1 && (rnd() & 3) == 0) x = 0; if (mode == 1 && (rnd() & 7) == 0) y = -0.0f;
        pl[4*i] = x; pl[4*i+1] = y; pl[4*i+2] = z; pl[4*i+3] = cull_rf(-800, 800); }
      for (int i = 0; i < 3; i++) { float a = cull_rf(-500, 500), b = a + cull_rf(0, mode == 2 ? 0.01f : 200); nd[i] = a; nd[3+i] = b; }
      for (int i = 6; i < 9; i++) nd[i] = cull_rf(-50, 50); nd[9] = cull_rf(-1000, 1000); nd[10] = cull_rf(-1000, 1000); nd[11] = cull_rf(-1000, 1000);
      if (mode == 3) { int k = rnd() % 6; pl[4*k+3] = -(pl[4*k] * (nd[0] + nd[9]) + pl[4*k+1] * (nd[1] + nd[10]) + pl[4*k+2] * (nd[2] + nd[11])); } // plane through corner A
      memcpy(M + P, pl, sizeof pl); memcpy(M + N, nd, 9 * 4); memcpy(M + N + 0x24, nd + 9, 12);
      u32 a = gcall_this(0x7760d0, P, 1, N), b = gcall_this(code, P, 1, N); total++;
      if (a < 3 && b < 3) hist[a][b]++;
      if (a != b && bad++ < 10) gw_log("cull mismatch cw=%x it=%d orig=%u sse=%u", cw ? 0x27f : 0x7f, it, a, b); } }
  gw_log("selftest cull: %ld cases, %ld mismatches; orig/sse counts 0/0 %ld 1/1 %ld 2/2 %ld", total, bad, hist[0][0], hist[1][1], hist[2][2]);
  fprintf(stderr, "selftest cull: %ld cases, %ld mismatches (0:%ld 1:%ld 2:%ld)\n", total, bad, hist[0][0], hist[1][1], hist[2][2]); fflush(LOG); return bad ? 1 : 0; }
