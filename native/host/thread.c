// Cooperative guest threads on the single emulator engine.
//
// Every guest thread is a saved unicorn CPU context (uc_context) plus its own 4 KiB TEB image and stack.
// The TEB lives at one fixed guest address (fs base); switching threads swaps the page contents, so
// fs:[0x18], TLS slots, the SEH chain head and GetLastError are per thread without touching the GDT.
// Only one guest thread runs at a time, so guest `lock` instructions, the guest heap and every shim stay
// single-threaded. A switch happens only inside a blocking or yielding shim (Wait*, Sleep, contended
// EnterCriticalSection, PeekMessage/GetMessage, ExitThread, thread return): the shim saves the caller's
// context as if it had returned (EIP = return address, ESP popped, EAX = result filled in when the wait
// is satisfied), restores the next runnable thread's context and returns with g_shim_jumped set.
// These shims must use the interrupt thunk path (core.c SLOW list), never the sysenter fast path.
// When every thread is blocked, the host sleeps until the earliest timeout (or advances GW_VCLOCK).
// An infinite wait that nothing can satisfy wakes the main thread with "signalled" and a WARN (the old
// single-threaded behaviour), so a missing signal cannot hang the client.
#include "gw.h"
#include <unistd.h>

enum { T_RUN, T_READY, T_BLOCKED, T_SUSP, T_DONE };
typedef struct GThread {
  u32 h, tid, start, param, exitcode, stack; int state;
  uc_context *ctx; u8 teb[0x1000];
  u32 wh[64]; int nw, waitall; u32 cs;     // waited handles / contended critical section
  uint64_t deadline;                        // now_us() deadline, 0 = infinite
  int nargs_pending; uint64_t run_ns;
} GThread;
#define MAXT 64
static GThread *thr[MAXT]; static int nthr, cur;
static u32 next_tid = 0x1300;
int g_threads_active;   // >0 once a guest thread was created

#define TEB_SZ 0x1000
static GThread *main_thread(void) {
  if (!nthr) { GThread *t = calloc(1, sizeof *t); t->tid = r32(tls_teb() + 0x24); t->state = T_RUN; uc_context_alloc(uc, &t->ctx); thr[nthr++] = t; cur = 0; }
  return thr[0];
}
GThread *cur_thread(void) { return main_thread() ? thr[cur] : NULL; }

// ---- object state ----
static int signaled(Handle *h, GThread *t) {
  switch (h->type) {
    case H_EVENT: return h->v2 != 0;
    case H_MUTEX: return h->v2 == 0 || h->v1 == t->tid;
    case H_SEM: return h->v2 > 0;
    case H_THREAD: { GThread *x = h->p; return !x || x->state == T_DONE; }
    default: return 1;
  }
}
static void consume(Handle *h, GThread *t) {
  switch (h->type) {
    case H_EVENT: if (!h->v1) h->v2 = 0; break;
    case H_MUTEX: h->v1 = t->tid; h->v2++; break;
    case H_SEM: h->v2--; break;
  }
}
static int cs_free(u32 c, GThread *t) { u32 o = r32(c + 12); return o == 0 || o == t->tid; }
static void cs_take(u32 c, GThread *t) { w32(c + 8, r32(c + 8) + 1); w32(c + 12, t->tid); }
// result of t's wait if it can complete now (consuming the objects), else -1
static i32 try_wait(GThread *t) {
  if (t->cs) { if (!cs_free(t->cs, t)) return -1; cs_take(t->cs, t); t->cs = 0; return 0; }
  if (t->waitall) {
    for (int i = 0; i < t->nw; i++) { Handle *h = h_get(t->wh[i]); if (h && !signaled(h, t)) return -1; }
    for (int i = 0; i < t->nw; i++) { Handle *h = h_get(t->wh[i]); if (h) consume(h, t); }
    return 0;
  }
  for (int i = 0; i < t->nw; i++) { Handle *h = h_get(t->wh[i]); if (!h || signaled(h, t)) { if (h) consume(h, t); return i; } }
  return -1;
}
static void set_result(GThread *t, u32 v) { uc_context_reg_write(t->ctx, UC_X86_REG_EAX, &v); }
// wake every blocked thread whose wait is now satisfied or timed out
extern void ds_pump(void);
static void poke(void) {
  static int in; if (!in) { in = 1; ds_pump(); in = 0; }   // DirectSound notification events (dsound.c)
  uint64_t now = now_us();
  for (int i = 0; i < nthr; i++) { GThread *t = thr[i]; if (t->state != T_BLOCKED) continue;
    i32 r = try_wait(t);
    if (r >= 0) { set_result(t, (u32)r); t->state = T_READY; }
    else if (t->deadline && now >= t->deadline) { set_result(t, t->nw || t->cs ? WAIT_TIMEOUT : 0); t->state = T_READY; t->cs = 0; } }
}

// ---- context switch ----
static void teb_out(GThread *t) { memcpy(t->teb, P(tls_teb()), TEB_SZ); }
static void teb_in(GThread *t) { memcpy(P(tls_teb()), t->teb, TEB_SZ); }
// save the running thread as if the current shim returned (stdcall, nargs) with EAX = ret
static void save_returning(GThread *t, int nargs, u32 ret) {
  uc_context_save(uc, t->ctx);
  u32 ra = r32(g_esp), esp = g_esp + 4 + 4 * nargs;
  uc_context_reg_write(t->ctx, UC_X86_REG_EIP, &ra); uc_context_reg_write(t->ctx, UC_X86_REG_ESP, &esp); set_result(t, ret);
  teb_out(t);
}
#include <time.h>
static uint64_t mono(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return (uint64_t)ts.tv_sec * 1000000000ull + ts.tv_nsec; }
static uint64_t since;   // when the running thread was resumed
static volatile unsigned g_switch_seq;   // bumped at every switch; the preemption watchdog watches it
static void resume(int k) {
  g_switch_seq++;
  uint64_t now = mono(); if (since && cur < nthr) thr[cur]->run_ns += now - since; since = now;
  GThread *t = thr[k]; cur = k; t->state = T_RUN; teb_in(t);
  uc_context_restore(uc, t->ctx);
  u32 eip; uc_context_reg_read(t->ctx, UC_X86_REG_EIP, &eip); uc_reg_write(uc, UC_X86_REG_EIP, &eip);   // restart translation at the new EIP
  g_shim_jumped = 1;
}
// pick the next thread to run after the current one gave up the CPU; sleeps while all are blocked
static void schedule(void) {
  for (int spins = 0;; spins++) {
    poke();
    for (int d = 1; d <= nthr; d++) { int k = (cur + d) % nthr; if (thr[k]->state == T_READY) { resume(k); return; } }
    // nobody runnable: sleep until the earliest deadline
    uint64_t best = 0; int bk = -1;
    for (int i = 0; i < nthr; i++) if (thr[i]->state == T_BLOCKED && thr[i]->deadline && (bk < 0 || thr[i]->deadline < best)) { best = thr[i]->deadline; bk = i; }
    if (bk < 0) {   // deadlock: every thread waits forever on something nobody can signal
      GThread *m = thr[0];
      gw_log("WARN: all guest threads blocked without timeout; waking the main thread with WAIT_OBJECT_0");
      if (m->state == T_BLOCKED) { if (m->cs) { cs_take(m->cs, m); m->cs = 0; } set_result(m, 0); m->state = T_READY; continue; }
      gw_fatal("scheduler: no runnable thread");
    }
    uint64_t now = now_us(); if (best > now) clock_sleep((u32)((best - now + 999) / 1000));
  }
}
// block the current thread (wait spec already filled in) and switch; the shim returns g_shim_jumped
static void block_current(int nargs, uint64_t deadline) {
  GThread *t = thr[cur]; t->deadline = deadline; t->state = T_BLOCKED;
  save_returning(t, nargs, WAIT_TIMEOUT);
  schedule();
}
// let other READY threads run; the current thread continues later with EAX = ret
static int yield_current(int nargs, u32 ret) {
  if (nthr <= 1) return 0;
  poke();
  int any = 0; for (int i = 0; i < nthr; i++) if (i != cur && thr[i]->state == T_READY) any = 1;
  if (!any) return 0;
  GThread *t = thr[cur]; t->state = T_READY; save_returning(t, nargs, ret); schedule(); return 1;
}
int thread_yield_from_shim(int nargs, u32 ret) { main_thread(); return yield_current(nargs, ret); }

// ---- preemption ----
// A guest thread that spins (polls a flag, busy-waits) without calling a blocking shim would keep every other
// thread from running. A host watchdog thread (started with the first CreateThread) looks every GW_PREEMPT ms
// (default 10; 0 = off): if no switch happened during the whole slice, it asks the engine to stop
// (uc_emu_stop is thread-safe; the stop lands at the next translation-block boundary) and core.c's run loop
// calls thread_preempt(), which round-robins to the next READY thread, or simply continues when none is.
volatile int g_preempt_req;
static unsigned long n_preempt, n_preempt_noop;
static int preempt_ms = -1;
#include <pthread.h>
extern void gw_uc_async_stop(uc_engine *uc);   // patched unicorn (third_party/unicorn-2.1.4/uc.c)
static void *watchdog(void *arg) {
  (void)arg; unsigned last = g_switch_seq;
  for (;;) {
    usleep((useconds_t)preempt_ms * 1000);
    unsigned now = g_switch_seq;
    // gw_uc_async_stop, not uc_emu_stop: the latter touches unicorn's per-thread JIT bookkeeping and is not
    // safe from this thread. Re-issued while pending, because a stop landing between two uc_emu_start calls is lost.
    if (now == last && nthr > 1) { g_preempt_req = 1; gw_uc_async_stop(uc); }
    last = now;
  }
  return NULL;
}
static void watchdog_start(void) {
  if (preempt_ms < 0) { const char *e = getenv("GW_PREEMPT"); preempt_ms = e ? atoi(e) : 10; }
  static int started; if (started || preempt_ms <= 0) return; started = 1;
  pthread_t th; pthread_create(&th, NULL, watchdog, NULL); pthread_detach(th);
  gw_log("preemption watchdog: %d ms timeslice", preempt_ms);
}
int thread_preempt(void) {   // engine stopped between translation blocks; returns 1 if another thread now runs
  if (nthr <= 1) return 0;
  poke();
  int any = 0; for (int i = 0; i < nthr; i++) if (i != cur && thr[i]->state == T_READY) any = 1;
  if (!any) { n_preempt_noop++; g_switch_seq++; return 0; }
  GThread *t = thr[cur]; uc_context_save(uc, t->ctx); teb_out(t); t->state = T_READY;
  n_preempt++; schedule(); g_shim_jumped = 0; return 1;
}

// ---- waits ----
static uint64_t deadline_for(u32 ms) { return ms == 0xFFFFFFFF ? 0 : now_us() + (uint64_t)ms * 1000 + 1; }
static u32 wait_common(int nargs, const u32 *hs, int n, int all, u32 ms) {
  GThread *t = cur_thread(); t->nw = n; t->waitall = all; t->cs = 0; for (int i = 0; i < n; i++) t->wh[i] = hs[i];
  i32 r = try_wait(t); if (r >= 0) return (u32)r;
  if (ms == 0) return WAIT_TIMEOUT;
  if (nthr <= 1) {   // single thread: nobody else can signal; keep the old behaviour
    if (ms != 0xFFFFFFFF) { clock_sleep(ms > 50 ? 50 : ms); return WAIT_TIMEOUT; }
    gw_log("WARN: infinite wait on unsignalled object(s) %x.. with one guest thread -> returning signalled", hs[0]); return WAIT_OBJECT_0;
  }
  block_current(nargs, deadline_for(ms)); return 0;
}
SHIM(KERNEL32, WaitForSingleObject, 2) { u32 h = ARG(0); return wait_common(2, &h, 1, 0, ARG(1)); }
SHIM(KERNEL32, WaitForSingleObjectEx, 3) { u32 h = ARG(0); return wait_common(3, &h, 1, 0, ARG(1)); }
SHIM(KERNEL32, WaitForMultipleObjects, 4) { u32 n = ARG(0) > 64 ? 64 : ARG(0), hs[64]; for (u32 i = 0; i < n; i++) hs[i] = r32(ARG(1) + 4 * i); return wait_common(4, hs, n, ARG(2), ARG(3)); }
SHIM(KERNEL32, WaitForMultipleObjectsEx, 5) { u32 n = ARG(0) > 64 ? 64 : ARG(0), hs[64]; for (u32 i = 0; i < n; i++) hs[i] = r32(ARG(1) + 4 * i); return wait_common(5, hs, n, ARG(2), ARG(3)); }
static u32 do_sleep(int nargs, u32 ms) {
  main_thread();
  if (nthr <= 1) { clock_sleep(ms); return 0; }
  if (ms == 0) { yield_current(nargs, 0); return 0; }
  GThread *t = thr[cur]; t->nw = 0; t->cs = 0; block_current(nargs, deadline_for(ms)); return 0;
}
SHIM(KERNEL32, Sleep, 1) { return do_sleep(1, ARG(0)); }
SHIM(KERNEL32, SleepEx, 2) { return do_sleep(2, ARG(0)); }
SHIM(KERNEL32, SwitchToThread, 0) { main_thread(); return yield_current(0, 1) ? 0 : 0; }

// ---- events / mutexes / semaphores ----
static u32 mk_event(u32 manual, u32 state) { u32 h = h_new(H_EVENT); Handle *x = h_get(h); x->v1 = manual; x->v2 = state; return h; }
SHIM(KERNEL32, CreateEventA, 4) { return mk_event(ARG(1), ARG(2)); }
SHIM(KERNEL32, CreateEventW, 4) { return mk_event(ARG(1), ARG(2)); }
SHIM(KERNEL32, SetEvent, 1) { Handle *h = h_get(ARG(0)); if (h) { h->v2 = 1; if (nthr > 1) poke(); } return 1; }
void thread_set_event(u32 hh) { Handle *h = h_get(hh); if (h && h->type == H_EVENT) { h->v2 = 1; if (nthr > 1) poke(); } }
SHIM(KERNEL32, ResetEvent, 1) { Handle *h = h_get(ARG(0)); if (h) h->v2 = 0; return 1; }
SHIM(KERNEL32, PulseEvent, 1) { Handle *h = h_get(ARG(0)); if (h) { h->v2 = 1; if (nthr > 1) poke(); h->v2 = 0; } return 1; }
static u32 mk_mutex(u32 owned) { main_thread(); u32 h = h_new(H_MUTEX); Handle *x = h_get(h); if (owned) { x->v1 = thr[cur]->tid; x->v2 = 1; } return h; }
SHIM(KERNEL32, CreateMutexA, 3) { return mk_mutex(ARG(1)); }
SHIM(KERNEL32, CreateMutexW, 3) { return mk_mutex(ARG(1)); }
SHIM(KERNEL32, ReleaseMutex, 1) { Handle *h = h_get(ARG(0)); if (!h || h->type != H_MUTEX || !h->v2) return 0; if (--h->v2 == 0) { h->v1 = 0; if (nthr > 1) poke(); } return 1; }
SHIM(KERNEL32, CreateSemaphoreA, 4) { u32 h = h_new(H_SEM); Handle *x = h_get(h); x->v2 = ARG(1); x->v3 = ARG(2); return h; }
SHIM(KERNEL32, CreateSemaphoreW, 4) { u32 h = h_new(H_SEM); Handle *x = h_get(h); x->v2 = ARG(1); x->v3 = ARG(2); return h; }
SHIM(KERNEL32, ReleaseSemaphore, 3) { Handle *h = h_get(ARG(0)); if (!h) return 0; if (ARG(2)) w32(ARG(2), h->v2); h->v2 += ARG(1); if (nthr > 1) poke(); return 1; }

// ---- critical sections (guest CRITICAL_SECTION: +8 RecursionCount, +12 OwningThread) ----
SHIM(KERNEL32, InitializeCriticalSection, 1) { u32 c = ARG(0); memset(P(c), 0, 24); w32(c + 4, 0xFFFFFFFF); return 0; }
SHIM(KERNEL32, InitializeCriticalSectionAndSpinCount, 2) { u32 c = ARG(0); memset(P(c), 0, 24); w32(c + 4, 0xFFFFFFFF); return 1; }
SHIM(KERNEL32, DeleteCriticalSection, 1) { return 0; }
SHIM(KERNEL32, EnterCriticalSection, 1) {
  u32 c = ARG(0); GThread *t = cur_thread();
  if (cs_free(c, t)) { cs_take(c, t); return 0; }
  if (nthr <= 1) { gw_log("WARN: EnterCriticalSection %08x owned by %x with one guest thread; taking it", c, r32(c + 12)); w32(c + 8, 0); cs_take(c, t); return 0; }
  t->nw = 0; t->cs = c; block_current(1, 0); return 0;
}
SHIM(KERNEL32, TryEnterCriticalSection, 1) { u32 c = ARG(0); GThread *t = cur_thread(); if (!cs_free(c, t)) return 0; cs_take(c, t); return 1; }
SHIM(KERNEL32, LeaveCriticalSection, 1) {
  u32 c = ARG(0); u32 n = r32(c + 8); if (n) n--; w32(c + 8, n);
  if (!n) { w32(c + 12, 0); if (nthr > 1) poke(); }
  return 0;
}

// ---- threads ----
static u32 ret_trap;
static void thread_finish(GThread *t, u32 code) {
  t->exitcode = code; t->state = T_DONE;
  gw_log("thread %x (start %s) exited with %u", t->tid, addr_sym(t->start), code);
  poke(); schedule();
}
static u32 sh_thread_return(void) { u32 eax; uc_reg_read(uc, UC_X86_REG_EAX, &eax); thread_finish(thr[cur], eax); return 0; }
SHIM(KERNEL32, CreateThread, 6) {
  main_thread();
  if (nthr >= MAXT) { gw_log("CreateThread: too many threads"); return 0; }
  if (!ret_trap) ret_trap = thunk_alloc_named("GWHOST", "ThreadReturn", 0, sh_thread_return);
  GThread *t = calloc(1, sizeof *t); t->tid = next_tid; next_tid += 4; t->start = ARG(2); t->param = ARG(3);
  u32 ss = ARG(1) ? ARG(1) : 0x100000; if (ss < 0x40000) ss = 0x40000; ss = (ss + 0xFFFF) & ~0xFFFFu;
  t->stack = vm_alloc(ss, 0x10000); u32 top = t->stack + ss - 16;
  w32(top - 4, t->param); w32(top - 8, ret_trap); u32 esp = top - 8, zero = 0, efl = 0x202;
  uc_context_alloc(uc, &t->ctx); uc_context_save(uc, t->ctx);   // segment/FPU/SSE state copied from the creator
  static const int R[] = {UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDX, UC_X86_REG_ESI, UC_X86_REG_EDI, UC_X86_REG_EBP};
  for (int i = 0; i < 7; i++) uc_context_reg_write(t->ctx, R[i], &zero);
  uc_context_reg_write(t->ctx, UC_X86_REG_EIP, &t->start); uc_context_reg_write(t->ctx, UC_X86_REG_ESP, &esp); uc_context_reg_write(t->ctx, UC_X86_REG_EFLAGS, &efl);
  memcpy(t->teb, P(tls_teb()), TEB_SZ);
  u32 tb = tls_teb();
#define TW(off, v) do { u32 _v = (v); memcpy(t->teb + (off), &_v, 4); } while (0)
  TW(0x00, 0xFFFFFFFF); TW(0x04, t->stack + ss); TW(0x08, t->stack); TW(0x18, tb); TW(0x24, t->tid); TW(0x34, 0);
  memset(t->teb + 0xE10, 0, 0x100);   // TLS slots start empty
  t->h = h_new(H_THREAD); h_get(t->h)->p = t;
  t->state = (ARG(4) & 4) ? T_SUSP : T_READY;
  thr[nthr++] = t; g_threads_active = 1; watchdog_start();
  if (ARG(5)) w32(ARG(5), t->tid);
  gw_log("CreateThread(start=%s param=%08x flags=%x stack=%x) -> handle %x tid %x", addr_sym(t->start), t->param, ARG(4), ss, t->h, t->tid);
  return t->h;
}
static GThread *thr_of(u32 h) { if (h == 0xFFFFFFFE) return cur_thread(); Handle *x = h_get(h); return x && x->type == H_THREAD ? x->p : NULL; }
SHIM(KERNEL32, ResumeThread, 1) { GThread *t = thr_of(ARG(0)); if (t && t->state == T_SUSP) { t->state = T_READY; return 1; } return 0; }
SHIM(KERNEL32, SuspendThread, 1) { GThread *t = thr_of(ARG(0)); if (t && t->state == T_READY) { t->state = T_SUSP; return 0; } return 0; }
SHIM(KERNEL32, ExitThread, 1) {
  main_thread(); if (cur == 0) { gw_log("ExitThread on the main thread -> exit"); fflush(LOG); exit(ARG(0)); }
  thread_finish(thr[cur], ARG(0)); return 0;
}
SHIM(KERNEL32, TerminateThread, 2) { GThread *t = thr_of(ARG(0)); if (!t || t == cur_thread()) return 0; t->state = T_DONE; t->exitcode = ARG(1); gw_log("TerminateThread %x", t->tid); poke(); return 1; }
SHIM(KERNEL32, GetExitCodeThread, 2) { GThread *t = thr_of(ARG(0)); if (ARG(1)) w32(ARG(1), t && t->state == T_DONE ? t->exitcode : 0x103); return 1; }
SHIM(KERNEL32, SetThreadPriority, 2) { return 1; }
SHIM(KERNEL32, GetThreadPriority, 1) { return 0; }
// stat line helper: wall time each guest thread ran since the previous call
void thread_stats(void) {
  if (nthr <= 1) return; char b[512]; int o = 0; uint64_t now = mono(); if (since) { thr[cur]->run_ns += now - since; since = now; }
  for (int i = 0; i < nthr && o < 400; i++) { o += snprintf(b + o, sizeof b - o, " %x:%s=%ums", thr[i]->tid, thr[i]->state == T_DONE ? "done" : "", (u32)(thr[i]->run_ns / 1000000)); thr[i]->run_ns = 0; }
  gw_log("guest threads (run time since last stat):%s; preemptions so far %lu (+%lu with nothing else ready)", b, n_preempt, n_preempt_noop);
}
