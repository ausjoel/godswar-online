// KERNEL32 shims.
#include "gw.h"
#include <sys/stat.h>
#include <sys/time.h>
#include <fcntl.h>
#include <unistd.h>
#include <dirent.h>
#include <fnmatch.h>
#include <strings.h>
#include <time.h>
#include <wctype.h>

#define INVALID_HANDLE 0xFFFFFFFFu
static u32 cmdA, cmdW, envA, envW, h_stdin, h_stdout, h_stderr;
static u32 unhandled_filter;

static void reg_co_skip(void);
void k32_init(const char *cmdline) {
  reg_co_skip();
  cmdA = gstrdup(cmdline); cmdW = gwstrdup(cmdline);
  static const char env[] = "PATH=C:\\WINDOWS\\system32;C:\\WINDOWS\0SystemRoot=C:\\WINDOWS\0\0";
  envA = heap_alloc(sizeof env, 0); memcpy(M + envA, env, sizeof env);
  envW = heap_alloc(2 * sizeof env, 0); for (size_t i = 0; i < sizeof env; i++) w16(envW + 2 * i, (u8)env[i]);
  h_stdin = h_new(H_FILE); h_stdout = h_new(H_FILE); h_stderr = h_new(H_FILE);
  h_get(h_stdin)->v3 = 2; h_get(h_stdout)->v3 = 2; h_get(h_stderr)->v3 = 2;   // v3=2: console
}

// ---------- error/thread/process ----------
SHIM(KERNEL32, GetLastError, 0) { return r32(tls_teb() + 0x34); }
SHIM(KERNEL32, SetLastError, 1) { set_last_error(ARG(0)); return 0; }
SHIM(KERNEL32, GetCurrentThreadId, 0) { return r32(tls_teb() + 0x24); }
SHIM(KERNEL32, GetCurrentProcessId, 0) { return 0x1234; }
SHIM(KERNEL32, GetCurrentThread, 0) { return 0xFFFFFFFE; }
SHIM(KERNEL32, GetCurrentProcess, 0) { return 0xFFFFFFFF; }
SHIM(KERNEL32, IsDebuggerPresent, 0) { return 0; }
SHIM(KERNEL32, IsProcessorFeaturePresent, 1) { u32 f = ARG(0); return f == 3 || f == 6 || f == 10; }  // MMX, SSE, SSE2 (NOT 7 = 3DNow!, which d3dx would then use)
SHIM(KERNEL32, DebugBreak, 0) { gw_log("DebugBreak"); return 0; }
SHIM(KERNEL32, OutputDebugStringA, 1) { gw_log("ODS: %s", (char *)P(ARG(0))); return 0; }
SHIM(KERNEL32, OutputDebugStringW, 1) { char *s = g_w2u(ARG(0)); gw_log("ODS: %s", s); free(s); return 0; }
SHIM(KERNEL32, ExitProcess, 1) { gw_log("ExitProcess(%u)", ARG(0)); fflush(LOG); exit((int)ARG(0)); }
SHIM(KERNEL32, TerminateProcess, 2) { gw_log("TerminateProcess(%x,%u)", ARG(0), ARG(1)); fflush(LOG); exit((int)ARG(1)); }
SHIM(KERNEL32, FatalAppExitA, 2) { gw_log("FatalAppExitA: %s", (char *)P(ARG(1))); exit(6); }
SHIM(KERNEL32, SetUnhandledExceptionFilter, 1) { u32 o = unhandled_filter; unhandled_filter = ARG(0); return o; }
SHIM(KERNEL32, UnhandledExceptionFilter, 1) { gw_log("UnhandledExceptionFilter called"); dump_regs(); return 1; }
SHIM(KERNEL32, SetErrorMode, 1) { return 0; }
SHIM(KERNEL32, Beep, 2) { return 1; }
SHIM(KERNEL32, GetVersionExA, 1) {
  u32 p = ARG(0), sz = r32(p); memset(P(p + 4), 0, sz - 4);
  w32(p + 4, 5); w32(p + 8, 1); w32(p + 12, 2600); w32(p + 16, 2); strcpy((char *)P(p + 20), "Service Pack 3");
  if (sz >= 156) { w16(p + 148, 3); w16(p + 150, 0); w16(p + 152, 0x100); w8(p + 154, 1); }
  return 1;
}
SHIM(KERNEL32, GetVersionExW, 1) {
  u32 p = ARG(0), sz = r32(p); memset(P(p + 4), 0, sz - 4);
  w32(p + 4, 5); w32(p + 8, 1); w32(p + 12, 2600); w32(p + 16, 2); u2w("Service Pack 3", p + 20, 128);
  if (sz >= 284) { w16(p + 276, 3); w16(p + 280, 0x100); w8(p + 282, 1); }
  return 1;
}
SHIM(KERNEL32, GetVersion, 0) { return 0x0A280105; }
SHIM(KERNEL32, GetStartupInfoA, 1) { u32 p = ARG(0); memset(P(p), 0, 68); w32(p, 68); return 0; }
SHIM(KERNEL32, GetStartupInfoW, 1) { u32 p = ARG(0); memset(P(p), 0, 68); w32(p, 68); return 0; }
SHIM(KERNEL32, GetCommandLineA, 0) { return cmdA; }
SHIM(KERNEL32, GetCommandLineW, 0) { return cmdW; }
SHIM(KERNEL32, GetEnvironmentStrings, 0) { return envA; }
SHIM(KERNEL32, GetEnvironmentStringsA, 0) { return envA; }
SHIM(KERNEL32, GetEnvironmentStringsW, 0) { return envW; }
SHIM(KERNEL32, FreeEnvironmentStringsA, 1) { return 1; }
SHIM(KERNEL32, FreeEnvironmentStringsW, 1) { return 1; }
SHIM(KERNEL32, GetEnvironmentVariableA, 3) { set_last_error(203); return 0; }
SHIM(KERNEL32, GetEnvironmentVariableW, 3) { set_last_error(203); return 0; }
SHIM(KERNEL32, SetEnvironmentVariableA, 2) { return 1; }
SHIM(KERNEL32, SetEnvironmentVariableW, 2) { return 1; }
SHIM(KERNEL32, ExpandEnvironmentStringsA, 3) { u32 s = ARG(0), d = ARG(1), n = ARG(2); u32 l = strlen((char *)P(s)) + 1; if (d && n >= l) memcpy(P(d), P(s), l); return l; }
SHIM(KERNEL32, SetHandleCount, 1) { return ARG(0); }
SHIM(KERNEL32, GetStdHandle, 1) { i32 n = (i32)ARG(0); return n == -10 ? h_stdin : n == -11 ? h_stdout : n == -12 ? h_stderr : INVALID_HANDLE; }
SHIM(KERNEL32, SetStdHandle, 2) { return 1; }
SHIM(KERNEL32, GetFileType, 1) { Handle *h = h_get(ARG(0)); if (!h) return 0; return h->v3 == 2 ? 2 : 1; }
SHIM(KERNEL32, GetConsoleCP, 0) { return 437; }
SHIM(KERNEL32, GetConsoleOutputCP, 0) { return 437; }
SHIM(KERNEL32, GetConsoleMode, 2) { return 0; }
SHIM(KERNEL32, SetConsoleCtrlHandler, 2) { return 1; }

// ---------- TLS ----------
static u8 tls_used[64];
SHIM(KERNEL32, TlsAlloc, 0) { for (int i = 0; i < 64; i++) if (!tls_used[i]) { tls_used[i] = 1; w32(tls_teb() + 0xE10 + 4 * i, 0); return i; } return 0xFFFFFFFF; }
SHIM(KERNEL32, TlsFree, 1) { if (ARG(0) < 64) tls_used[ARG(0)] = 0; return 1; }
SHIM(KERNEL32, TlsGetValue, 1) { set_last_error(0); return ARG(0) < 64 ? r32(tls_teb() + 0xE10 + 4 * ARG(0)) : 0; }
SHIM(KERNEL32, TlsSetValue, 2) { if (ARG(0) >= 64) return 0; w32(tls_teb() + 0xE10 + 4 * ARG(0), ARG(1)); return 1; }

// ---------- sync: events, mutexes, critical sections, waits and threads live in thread.c ----------
SHIM(KERNEL32, InterlockedIncrement, 1) { u32 p = ARG(0), v = r32(p) + 1; w32(p, v); return v; }
SHIM(KERNEL32, InterlockedDecrement, 1) { u32 p = ARG(0), v = r32(p) - 1; w32(p, v); return v; }
SHIM(KERNEL32, InterlockedExchange, 2) { u32 p = ARG(0), o = r32(p); w32(p, ARG(1)); return o; }
SHIM(KERNEL32, InterlockedExchangeAdd, 2) { u32 p = ARG(0), o = r32(p); w32(p, o + ARG(1)); return o; }
SHIM(KERNEL32, InterlockedCompareExchange, 3) { u32 p = ARG(0), o = r32(p); if (o == ARG(2)) w32(p, ARG(1)); return o; }
SHIM(KERNEL32, DuplicateHandle, 7) { if (ARG(3)) w32(ARG(3), ARG(1)); return 1; }
SHIM(KERNEL32, CloseHandle, 1) { h_close(ARG(0)); return 1; }
SHIM(KERNEL32, DisableThreadLibraryCalls, 1) { return 1; }
extern void gw_flush_code(u32 a, u32 n);
SHIM(KERNEL32, FlushInstructionCache, 3) { gw_flush_code(ARG(1), ARG(2)); return 1; }

// ---------- memory ----------
#define PROCESS_HEAP 0x00150000u
SHIM(KERNEL32, GetProcessHeap, 0) { return PROCESS_HEAP; }
SHIM(KERNEL32, HeapCreate, 3) { static u32 n = 0x00160000; n += 0x10000; return n; }
SHIM(KERNEL32, HeapDestroy, 1) { return 1; }
SHIM(KERNEL32, HeapAlloc, 3) { return heap_alloc(ARG(2), ARG(1) & 8); }
SHIM(KERNEL32, HeapFree, 3) { heap_free(ARG(2)); return 1; }
SHIM(KERNEL32, HeapReAlloc, 4) {
  u32 p = ARG(2), sz = ARG(3), fl = ARG(1);
  if ((fl & 0x10) && sz > heap_size(p)) return 0;
  return heap_realloc(p, sz, fl & 8);
}
SHIM(KERNEL32, HeapSize, 3) { u32 p = ARG(2); return p ? heap_size(p) : 0xFFFFFFFF; }
SHIM(KERNEL32, HeapValidate, 3) { return 1; }
SHIM(KERNEL32, HeapCompact, 2) { return 0x100000; }
SHIM(KERNEL32, HeapWalk, 2) { set_last_error(259); return 0; }
SHIM(KERNEL32, HeapSetInformation, 4) { return 1; }
SHIM(KERNEL32, GlobalAlloc, 2) { return heap_alloc(ARG(1), ARG(0) & 0x40); }
SHIM(KERNEL32, GlobalFree, 1) { heap_free(ARG(0)); return 0; }
SHIM(KERNEL32, GlobalLock, 1) { return ARG(0); }
SHIM(KERNEL32, GlobalUnlock, 1) { return 1; }
SHIM(KERNEL32, GlobalSize, 1) { return heap_size(ARG(0)); }
SHIM(KERNEL32, LocalAlloc, 2) { return heap_alloc(ARG(1), ARG(0) & 0x40); }
SHIM(KERNEL32, LocalFree, 1) { heap_free(ARG(0)); return 0; }
SHIM(KERNEL32, GlobalMemoryStatus, 1) {
  u32 p = ARG(0); w32(p, 32); w32(p + 4, 30); w32(p + 8, 0x7FFF0000); w32(p + 12, 0x60000000); w32(p + 16, 0x7FFF0000);
  w32(p + 20, 0x60000000); w32(p + 24, 0x7FFE0000); w32(p + 28, 0x60000000); return 0;
}
typedef struct VReg { u32 base, size; struct VReg *next; } VReg;
static VReg *vregs;
SHIM(KERNEL32, VirtualAlloc, 4) {
  u32 a = ARG(0), sz = ARG(1);
  if (a) { for (VReg *r = vregs; r; r = r->next) if (a >= r->base && a < r->base + r->size) return a & ~0xfffu;
           gw_log("VirtualAlloc at fixed %08x size %x (granted)", a, sz); return a & ~0xfffu; }
  u32 p = vm_alloc(sz, 0x10000); VReg *r = malloc(sizeof *r); r->base = p; r->size = (sz + 0xfff) & ~0xfffu; r->next = vregs; vregs = r;
  return p;
}
SHIM(KERNEL32, VirtualFree, 3) { return 1; }
SHIM(KERNEL32, VirtualProtect, 4) { if (ARG(2) & 0xF0) gw_flush_code(ARG(0), ARG(1)); if (ARG(3)) w32(ARG(3), 4); return 1; }
SHIM(KERNEL32, VirtualQuery, 3) {
  u32 a = ARG(0), p = ARG(1); Module *mm = NULL;
  for (u32 b = a & ~0xffffu; b >= 0x10000 && b + 0x40000000 > a; b -= 0x10000) { mm = mod_by_base(b); if (mm && a < b + mm->size) break; mm = NULL; if (b < 0x10000 + 0x10000) break; }
  u32 ab = mm ? mm->base : a & ~0xfffu;
  for (VReg *r = vregs; r && !mm; r = r->next) if (a >= r->base && a < r->base + r->size) ab = r->base;
  w32(p, a & ~0xfffu); w32(p + 4, ab); w32(p + 8, 4); w32(p + 12, 0x1000); w32(p + 16, 0x1000); w32(p + 20, mm ? 0x20 : 4);
  w32(p + 24, mm ? 0x1000000 : 0x20000);
  return 28;
}
SHIM(KERNEL32, GetSystemInfo, 1) {
  u32 p = ARG(0); memset(P(p), 0, 36); w32(p + 4, 0x1000); w32(p + 8, 0x10000); w32(p + 12, 0x7FFEFFFF);
  w32(p + 16, 1); w32(p + 20, 1); w32(p + 24, 586); w32(p + 28, 0x10000); w16(p + 32, 6); w16(p + 34, 0x0f01); return 0;
}
SHIM(KERNEL32, IsBadReadPtr, 2) { return ARG(0) < 0x10000; }
SHIM(KERNEL32, IsBadWritePtr, 2) { return ARG(0) < 0x10000; }
SHIM(KERNEL32, IsBadCodePtr, 1) { return ARG(0) < 0x10000; }
SHIM(KERNEL32, IsBadStringPtrW, 2) { return ARG(0) < 0x10000; }
SHIM(KERNEL32, MulDiv, 3) { if (!ARG(2)) return 0xFFFFFFFF; int64_t r = ((int64_t)(i32)ARG(0) * (i32)ARG(1)); int64_t d = (i32)ARG(2); r = (r + (r >= 0 ? d / 2 : -d / 2)) / d; return (u32)(i32)r; }

// ---------- modules ----------
static const char *HOSTDLLS[] = {"KERNEL32", "USER32", "GDI32", "ADVAPI32", "OLEAUT32", "OLE32", "SHELL32", "SHLWAPI", "WINMM", "WS2_32",
  "NETAPI32", "DBGHELP", "DSOUND", "D3D9", "NTDLL", "FB", "MSCOREE", "PSAPI", "VERSION", "IMM32", "COMCTL32", "WSOCK32", "IPHLPAPI", NULL};
static int hostdll_index(const char *name) {
  const char *b = strrchr(name, '\\'); b = b ? b + 1 : name; char t[64]; size_t i;
  for (i = 0; b[i] && i < 63 && b[i] != '.'; i++) t[i] = toupper((unsigned char)b[i]); t[i] = 0;
  for (int k = 0; HOSTDLLS[k]; k++) if (!strcmp(t, HOSTDLLS[k])) return k;
  return -1;
}
#define PSEUDO_BASE 0x7C000000u
static u32 load_lib(const char *name) {
  Module *m = mod_find(name); if (m) return m->base;
  int k = hostdll_index(name); if (k >= 0) return PSEUDO_BASE + k * 0x10000;
  const char *b = strrchr(name, '\\'); b = b ? b + 1 : name;
  char nm[128]; snprintf(nm, sizeof nm, "%s", b); if (!strchr(nm, '.')) strcat(nm, ".dll");
  char *hp = map_path(nm, 0); FILE *f = fopen(hp, "rb"); free(hp);
  if (!f) { gw_log("LoadLibrary(%s): not found", name); set_last_error(126); return 0; }
  fclose(f);
  m = pe_load(nm); if (!m) { set_last_error(126); return 0; }
  if (m->entry) { gw_log("DllMain %s (LoadLibrary)", m->name); gcall(m->entry, 3, m->base, 1, 0); }
  return m->base;
}
// already-loaded modules are logged once and need no coroutine (no DllMain to run): the client calls
// LoadLibraryA("d3d9.dll") + GetProcAddress about 32 times per frame in the world
static int loaded_already(const char *name) { return name && (mod_find(name) || hostdll_index(name) >= 0); }
static int log_once(const char *kind, const char *name) {   // 1 the first time this (kind, name) pair is seen
  static char seen[256][80]; static int n; char k[80]; snprintf(k, sizeof k, "%s|%s", kind, name);
  for (int i = 0; i < n; i++) if (!strcmp(seen[i], k)) return 0;
  if (n < 256) snprintf(seen[n++], sizeof seen[0], "%s", k); return 1;
}
static u32 load_lib_logged(const char *kind, const char *name) { if (log_once(kind, name)) gw_log("%s(%s)", kind, name); return load_lib(name); }
SHIM(KERNEL32, LoadLibraryA, 1) { return load_lib_logged("LoadLibraryA", (char *)P(ARG(0))); }
SHIM(KERNEL32, LoadLibraryExA, 3) { return load_lib_logged("LoadLibraryExA", (char *)P(ARG(0))); }
SHIM(KERNEL32, LoadLibraryW, 1) { char *s = g_w2u(ARG(0)); u32 r = load_lib_logged("LoadLibraryW", s); free(s); return r; }
SHIM(KERNEL32, LoadLibraryExW, 3) { char *s = g_w2u(ARG(0)); u32 r = load_lib_logged("LoadLibraryExW", s); free(s); return r; }
static int co_skip_a(void) { return ARG(0) && loaded_already((char *)P(ARG(0))); }
static int co_skip_w(void) { char *s = g_w2u(ARG(0)); int r = loaded_already(s); free(s); return r; }
static void reg_co_skip(void) {   // called from k32_init (after every SHIM constructor ran)
  extern void shim_set_co_skip(const char *name, int (*f)(void));
  shim_set_co_skip("LoadLibraryA", co_skip_a); shim_set_co_skip("LoadLibraryExA", co_skip_a);
  shim_set_co_skip("LoadLibraryW", co_skip_w); shim_set_co_skip("LoadLibraryExW", co_skip_w);
}
SHIM(KERNEL32, FreeLibrary, 1) { return 1; }
static u32 get_module(const char *name) {
  if (!name) return 0x400000;
  Module *m = mod_find(name); if (m) return m->base;
  int k = hostdll_index(name); if (k >= 0) return PSEUDO_BASE + k * 0x10000;
  set_last_error(126); return 0;
}
SHIM(KERNEL32, GetModuleHandleA, 1) { return get_module(ARG(0) ? (char *)P(ARG(0)) : NULL); }
SHIM(KERNEL32, GetModuleHandleW, 1) { char *s = g_w2u(ARG(0)); u32 r = get_module(s); free(s); return r; }
SHIM(KERNEL32, GetProcAddress, 2) {
  u32 h = ARG(0), n = ARG(1); char nm[128];
  if (n < 0x10000) snprintf(nm, sizeof nm, "#%u", n); else snprintf(nm, sizeof nm, "%s", (char *)P(n));
  u32 r = 0;
  if (h >= PSEUDO_BASE && h < PSEUDO_BASE + 0x400000) { const char *dll = HOSTDLLS[(h - PSEUDO_BASE) / 0x10000]; r = thunk_for(dll, nm);
    if (log_once(dll, nm)) gw_log("GetProcAddress(%s, %s) -> %s", dll, nm, r ? "thunk" : "NULL"); }
  else { Module *m = mod_by_base(h ? h : 0x400000); if (m) r = pe_export(m, nm); if (log_once(m ? m->name : "?", nm)) gw_log("GetProcAddress(%s, %s) -> %08x", m ? m->name : "?", nm, r); }
  if (!r) set_last_error(127);
  return r;
}
static int modfilename(u32 h, char *out, size_t n) {
  Module *m = mod_by_base(h ? h : 0x400000);
  if (h >= PSEUDO_BASE && h < PSEUDO_BASE + 0x400000) return snprintf(out, n, "C:\\WINDOWS\\system32\\%s.dll", HOSTDLLS[(h - PSEUDO_BASE) / 0x10000]);
  return snprintf(out, n, "C:\\GodsWar\\%s", m ? m->name : "Origin.exe");
}
SHIM(KERNEL32, GetModuleFileNameA, 3) { char b[300]; int l = modfilename(ARG(0), b, sizeof b); u32 n = ARG(2); if ((u32)l >= n) l = n - 1; memcpy(P(ARG(1)), b, l); w8(ARG(1) + l, 0); return l; }
SHIM(KERNEL32, GetModuleFileNameW, 3) { char b[300]; modfilename(ARG(0), b, sizeof b); return u2w(b, ARG(1), ARG(2)) - 1; }
SHIM(KERNEL32, GetSystemDirectoryW, 2) { return u2w("C:\\WINDOWS\\system32", ARG(0), ARG(1)) - 1; }
SHIM(KERNEL32, GetSystemDirectoryA, 2) { strcpy((char *)P(ARG(0)), "C:\\WINDOWS\\system32"); return 19; }
SHIM(KERNEL32, GetWindowsDirectoryA, 2) { strcpy((char *)P(ARG(0)), "C:\\WINDOWS"); return 10; }

// ---------- resources ----------
static u32 rsrc_root(u32 base) { u32 pe = base + r32(base + 0x3c); u32 rva = r32(pe + 24 + 96 + 2 * 8); return rva ? base + rva : 0; }
static int res_name_eq(u32 root, u32 ent, u32 id, const char *sname) {
  u32 nm = r32(ent);
  if (nm & 0x80000000u) { if (!sname) return 0; u32 s = root + (nm & 0x7fffffff); u16 l = r16(s); if (strlen(sname) != l) return 0;
    for (u16 i = 0; i < l; i++) if (toupper(r16(s + 2 + 2 * i)) != toupper((unsigned char)sname[i])) return 0; return 1; }
  return !sname && nm == id;
}
static u32 res_dir_find(u32 root, u32 dir, u32 id, const char *sname) {   // returns entry's offset value (or 0)
  u16 nn = r16(dir + 12), ni = r16(dir + 14);
  for (u32 i = 0; i < (u32)(nn + ni); i++) { u32 e = dir + 16 + 8 * i; if (res_name_eq(root, e, id, sname)) return r32(e + 4); }
  return 0;
}
static u32 res_find(u32 base, u32 type, const char *tname, u32 name, const char *nname) {  // -> data entry address
  u32 root = rsrc_root(base); if (!root) return 0;
  u32 o = res_dir_find(root, root, type, tname); if (!(o & 0x80000000u)) return 0;
  u32 d2 = root + (o & 0x7fffffff); o = res_dir_find(root, d2, name, nname); if (!(o & 0x80000000u)) return 0;
  u32 d3 = root + (o & 0x7fffffff); u16 n = r16(d3 + 12) + r16(d3 + 14); if (!n) return 0;
  u32 le = r32(d3 + 16 + 4); if (le & 0x80000000u) return 0;
  return root + le;   // IMAGE_RESOURCE_DATA_ENTRY
}
u32 res_lookup(u32 base, u32 type, u32 name, u32 *size) {
  u32 de = res_find(base ? base : 0x400000, type, NULL, name, NULL); if (!de) return 0;
  if (size) *size = r32(de + 4);
  return (base ? base : 0x400000) + r32(de);
}
static char *resname(u32 v, int wide) { if (v < 0x10000) return NULL; return wide ? g_w2u(v) : strdup((char *)P(v)); }
static u32 find_res(u32 mod, u32 name, u32 type, int wide) {
  char *n = resname(name, wide), *t = resname(type, wide);
  if (n && n[0] == '#') { name = atoi(n + 1); free(n); n = NULL; }
  if (t && t[0] == '#') { type = atoi(t + 1); free(t); t = NULL; }
  u32 r = res_find(mod ? mod : 0x400000, type, t, name, n); free(n); free(t);
  if (!r) set_last_error(1813);
  return r;
}
SHIM(KERNEL32, FindResourceA, 3) { return find_res(ARG(0), ARG(1), ARG(2), 0); }
SHIM(KERNEL32, FindResourceW, 3) { return find_res(ARG(0), ARG(1), ARG(2), 1); }
SHIM(KERNEL32, FindResourceExW, 4) { return find_res(ARG(0), ARG(2), ARG(1), 1); }
SHIM(KERNEL32, LoadResource, 2) { u32 de = ARG(1); return de ? (ARG(0) ? ARG(0) : 0x400000) + r32(de) : 0; }
SHIM(KERNEL32, LockResource, 1) { return ARG(0); }
SHIM(KERNEL32, FreeResource, 1) { return 0; }
SHIM(KERNEL32, SizeofResource, 2) { return ARG(1) ? r32(ARG(1) + 4) : 0; }

// ---------- time ----------
static uint64_t filetime_now(void) { struct timespec t; clock_gettime(CLOCK_REALTIME, &t); return (uint64_t)t.tv_sec * 10000000ull + t.tv_nsec / 100 + 116444736000000000ull; }
SHIM(KERNEL32, GetTickCount, 0) { return now_ms(); }
SHIM(KERNEL32, QueryPerformanceFrequency, 1) { w32(ARG(0), 1000000); w32(ARG(0) + 4, 0); return 1; }
SHIM(KERNEL32, QueryPerformanceCounter, 1) { uint64_t v = now_us(); w32(ARG(0), (u32)v); w32(ARG(0) + 4, (u32)(v >> 32)); return 1; }
SHIM(KERNEL32, GetSystemTimeAsFileTime, 1) { uint64_t v = filetime_now(); w32(ARG(0), (u32)v); w32(ARG(0) + 4, (u32)(v >> 32)); return 0; }
static void put_systime(u32 p, struct tm *tm, int ms) {
  w16(p, tm->tm_year + 1900); w16(p + 2, tm->tm_mon + 1); w16(p + 4, tm->tm_wday); w16(p + 6, tm->tm_mday);
  w16(p + 8, tm->tm_hour); w16(p + 10, tm->tm_min); w16(p + 12, tm->tm_sec); w16(p + 14, ms);
}
SHIM(KERNEL32, GetLocalTime, 1) { struct timeval tv; gettimeofday(&tv, NULL); struct tm tm; localtime_r(&tv.tv_sec, &tm); put_systime(ARG(0), &tm, tv.tv_usec / 1000); return 0; }
SHIM(KERNEL32, GetSystemTime, 1) { struct timeval tv; gettimeofday(&tv, NULL); struct tm tm; gmtime_r(&tv.tv_sec, &tm); put_systime(ARG(0), &tm, tv.tv_usec / 1000); return 0; }
SHIM(KERNEL32, SetLocalTime, 1) { return 0; }
SHIM(KERNEL32, FileTimeToSystemTime, 2) {
  uint64_t v = r32(ARG(0)) | ((uint64_t)r32(ARG(0) + 4) << 32); time_t s = (time_t)((v - 116444736000000000ull) / 10000000ull);
  struct tm tm; gmtime_r(&s, &tm); put_systime(ARG(1), &tm, (int)((v / 10000) % 1000)); return 1;
}
SHIM(KERNEL32, FileTimeToLocalFileTime, 2) {
  uint64_t v = r32(ARG(0)) | ((uint64_t)r32(ARG(0) + 4) << 32); time_t now = time(NULL); struct tm tm; localtime_r(&now, &tm);
  v += (int64_t)tm.tm_gmtoff * 10000000ll; w32(ARG(1), (u32)v); w32(ARG(1) + 4, (u32)(v >> 32)); return 1;
}
SHIM(KERNEL32, LocalFileTimeToFileTime, 2) {
  uint64_t v = r32(ARG(0)) | ((uint64_t)r32(ARG(0) + 4) << 32); time_t now = time(NULL); struct tm tm; localtime_r(&now, &tm);
  v -= (int64_t)tm.tm_gmtoff * 10000000ll; w32(ARG(1), (u32)v); w32(ARG(1) + 4, (u32)(v >> 32)); return 1;
}
SHIM(KERNEL32, SystemTimeToFileTime, 2) {
  u32 p = ARG(0); struct tm tm = {0}; tm.tm_year = r16(p) - 1900; tm.tm_mon = r16(p + 2) - 1; tm.tm_mday = r16(p + 6);
  tm.tm_hour = r16(p + 8); tm.tm_min = r16(p + 10); tm.tm_sec = r16(p + 12);
  uint64_t v = (uint64_t)timegm(&tm) * 10000000ull + 116444736000000000ull + r16(p + 14) * 10000ull;
  w32(ARG(1), (u32)v); w32(ARG(1) + 4, (u32)(v >> 32)); return 1;
}
SHIM(KERNEL32, GetTimeZoneInformation, 1) { memset(P(ARG(0)), 0, 172); time_t now = time(NULL); struct tm tm; localtime_r(&now, &tm); w32(ARG(0), (u32)(-(i32)(tm.tm_gmtoff / 60))); return 0; }

// ---------- locale / code pages ----------
SHIM(KERNEL32, GetACP, 0) { return 1252; }
SHIM(KERNEL32, GetOEMCP, 0) { return 437; }
SHIM(KERNEL32, IsValidCodePage, 1) { u32 c = ARG(0); return c == 1252 || c == 437 || c == 65001 || c == 936 || c == 950 || c == 949 || c == 932; }
SHIM(KERNEL32, GetCPInfo, 2) {
  u32 cp = ARG(0), p = ARG(1); memset(P(p), 0, 20);
  int dbcs = cp == 936 || cp == 950 || cp == 949 || cp == 932;
  w32(p, cp == 65001 ? 4 : dbcs ? 2 : 1); w8(p + 4, '?');
  if (dbcs) { w8(p + 6, 0x81); w8(p + 7, 0xFE); }
  return 1;
}
SHIM(KERNEL32, IsDBCSLeadByte, 1) { return 0; }
SHIM(KERNEL32, IsDBCSLeadByteEx, 2) { u32 cp = ARG(0), b = ARG(1) & 0xff; return (cp == 936 || cp == 950 || cp == 949) && b >= 0x81 && b <= 0xFE; }
SHIM(KERNEL32, GetUserDefaultLCID, 0) { return 0x409; }
SHIM(KERNEL32, GetThreadLocale, 0) { return 0x409; }
SHIM(KERNEL32, GetSystemDefaultLCID, 0) { return 0x409; }
SHIM(KERNEL32, GetUserDefaultLangID, 0) { return 0x409; }
SHIM(KERNEL32, GetSystemDefaultLangID, 0) { return 0x409; }
SHIM(KERNEL32, IsValidLocale, 2) { return 1; }
SHIM(KERNEL32, EnumSystemLocalesA, 2) { return 1; }
static const char *locinfo(u32 t) {
  switch (t & 0xffff) {
    case 0x1: return "0409"; case 0x2: case 0x1001: return "English"; case 0x3: return "ENU"; case 0x7: return "ENU";
    case 0x5: case 0x6: return "United States"; case 0xB: return "437"; case 0x1004: return "1252"; case 0x1009: return "1";
    case 0xE: return "."; case 0xF: return ","; case 0x10: return "3;0"; case 0x14: return "$"; case 0x15: return ".";
    case 0x16: return ","; case 0x17: return "3;0"; case 0x18: return "2"; case 0x19: return "2"; case 0x1A: return "2";
    case 0x1B: return "1"; case 0x1C: return "0"; case 0x1D: return "/"; case 0x1E: return ":"; case 0x1F: return "M/d/yyyy";
    case 0x20: return "dddd, MMMM dd, yyyy"; case 0x1003: return "h:mm:ss tt"; case 0x28: return "AM"; case 0x29: return "PM";
    case 0x59: return "en"; case 0x5A: return "US"; case 0x50: return "1"; case 0x51: return "0";
    case 0x2A: return "Monday"; case 0x2B: return "Tuesday"; case 0x2C: return "Wednesday"; case 0x2D: return "Thursday"; case 0x2E: return "Friday"; case 0x2F: return "Saturday"; case 0x30: return "Sunday";
    case 0x31: return "Mon"; case 0x32: return "Tue"; case 0x33: return "Wed"; case 0x34: return "Thu"; case 0x35: return "Fri"; case 0x36: return "Sat"; case 0x37: return "Sun";
    case 0x38: return "January"; case 0x39: return "February"; case 0x3A: return "March"; case 0x3B: return "April"; case 0x3C: return "May"; case 0x3D: return "June";
    case 0x3E: return "July"; case 0x3F: return "August"; case 0x40: return "September"; case 0x41: return "October"; case 0x42: return "November"; case 0x43: return "December";
    case 0x44: return "Jan"; case 0x45: return "Feb"; case 0x46: return "Mar"; case 0x47: return "Apr"; case 0x48: return "May"; case 0x49: return "Jun";
    case 0x4A: return "Jul"; case 0x4B: return "Aug"; case 0x4C: return "Sep"; case 0x4D: return "Oct"; case 0x4E: return "Nov"; case 0x4F: return "Dec";
    case 0x100C: return "0"; case 0x1014: return "0"; case 0x1012: return "1"; default: return "";
  }
}
SHIM(KERNEL32, GetLocaleInfoA, 4) { const char *s = locinfo(ARG(1)); u32 l = strlen(s) + 1; if (!ARG(3)) return l; if (ARG(3) < l) { set_last_error(122); return 0; } memcpy(P(ARG(2)), s, l); return l; }
SHIM(KERNEL32, GetLocaleInfoW, 4) { const char *s = locinfo(ARG(1)); u32 l = strlen(s) + 1; if (!ARG(3)) return l; if (ARG(3) < l) { set_last_error(122); return 0; } for (u32 i = 0; i < l; i++) w16(ARG(2) + 2 * i, (u8)s[i]); return l; }
SHIM(KERNEL32, MultiByteToWideChar, 6) {
  u32 cp = ARG(0), src = ARG(2), dst = ARG(4); i32 sl = (i32)ARG(3), dl = (i32)ARG(5);
  if (sl < 0) sl = strlen((char *)P(src)) + 1;
  u16 *tmp = malloc((sl + 1) * 2 + 8); int n = mb2wc(cp, P(src), sl, tmp, sl + 1);
  if (dl == 0) { free(tmp); return n; }
  if (n > dl) { free(tmp); set_last_error(122); return 0; }
  memcpy(P(dst), tmp, n * 2); free(tmp); return n;
}
SHIM(KERNEL32, WideCharToMultiByte, 8) {
  u32 cp = ARG(0), src = ARG(2), dst = ARG(4); i32 sl = (i32)ARG(3), dl = (i32)ARG(5);
  if (sl < 0) sl = gw_wcslen(src) + 1;
  u16 *in = malloc(sl * 2 + 2); memcpy(in, P(src), sl * 2);
  u8 *tmp = malloc(sl * 4 + 8); int n = wc2mb(cp, in, sl, tmp, sl * 4 + 8); free(in);
  if (ARG(7)) w32(ARG(7), 0);
  if (dl == 0) { free(tmp); return n; }
  if (n > dl) { free(tmp); set_last_error(122); return 0; }
  memcpy(P(dst), tmp, n); free(tmp); return n;
}
static u16 ctype1(u32 c) {
  u16 f = 0;
  if (c < 128) { if (isupper(c)) f |= 1; if (islower(c)) f |= 2; if (isdigit(c)) f |= 4; if (isspace(c)) f |= 8; if (ispunct(c)) f |= 0x10;
    if (iscntrl(c)) f |= 0x20; if (c == ' ' || c == '\t') f |= 0x40; if (isxdigit(c)) f |= 0x80; if (isalpha(c)) f |= 0x100; return f; }
  if (iswupper(c)) f |= 1 | 0x100; if (iswlower(c)) f |= 2 | 0x100; if (iswalpha(c)) f |= 0x100; if (iswspace(c)) f |= 8; if (iswpunct(c)) f |= 0x10;
  if (c >= 0x80 && c < 0xA0) f |= 0x20; if (c == 0xA0) f |= 0x48;
  return f;
}
SHIM(KERNEL32, GetStringTypeW, 4) {
  u32 t = ARG(0), s = ARG(1), o = ARG(3); i32 n = (i32)ARG(2); if (n < 0) n = gw_wcslen(s) + 1;
  for (i32 i = 0; i < n; i++) w16(o + 2 * i, t == 1 ? ctype1(r16(s + 2 * i)) : 0); return 1;
}
SHIM(KERNEL32, GetStringTypeA, 5) {
  u32 t = ARG(1), s = ARG(2), o = ARG(4); i32 n = (i32)ARG(3); if (n < 0) n = strlen((char *)P(s)) + 1;
  for (i32 i = 0; i < n; i++) { u16 w; mb2wc(1252, P(s + i), 1, &w, 1); w16(o + 2 * i, t == 1 ? ctype1(w) : 0); } return 1;
}
SHIM(KERNEL32, GetStringTypeExA, 5) {
  u32 t = ARG(1), s = ARG(2), o = ARG(4); i32 n = (i32)ARG(3); if (n < 0) n = strlen((char *)P(s)) + 1;
  for (i32 i = 0; i < n; i++) { u16 w; mb2wc(1252, P(s + i), 1, &w, 1); w16(o + 2 * i, t == 1 ? ctype1(w) : 0); } return 1;
}
SHIM(KERNEL32, LCMapStringW, 6) {
  u32 fl = ARG(1), s = ARG(2), d = ARG(4); i32 n = (i32)ARG(3), dl = (i32)ARG(5); if (n < 0) n = gw_wcslen(s) + 1;
  if (fl & 0x400) { /* LCMAP_SORTKEY: bytes */ if (!dl) return n * 2 + 1; for (i32 i = 0; i < n && 2 * i + 1 < dl; i++) { u16 c = towupper(r16(s + 2 * i)); w8(d + 2 * i, c >> 8); w8(d + 2 * i + 1, c); } return n * 2 + 1; }
  if (!dl) return n; if (dl < n) { set_last_error(122); return 0; }
  for (i32 i = 0; i < n; i++) { u16 c = r16(s + 2 * i); if (fl & 0x100) c = towlower(c); if (fl & 0x200) c = towupper(c); w16(d + 2 * i, c); }
  return n;
}
SHIM(KERNEL32, LCMapStringA, 6) {
  u32 fl = ARG(1), s = ARG(2), d = ARG(4); i32 n = (i32)ARG(3), dl = (i32)ARG(5); if (n < 0) n = strlen((char *)P(s)) + 1;
  if (!dl) return n; if (dl < n) { set_last_error(122); return 0; }
  for (i32 i = 0; i < n; i++) { u8 c = r8(s + i); if (fl & 0x100) c = tolower(c); if (fl & 0x200) c = toupper(c); w8(d + i, c); }
  return n;
}
static int cmp_w(u32 a, i32 na, u32 b, i32 nb, int ci) {
  if (na < 0) na = gw_wcslen(a); if (nb < 0) nb = gw_wcslen(b);
  for (i32 i = 0; i < na && i < nb; i++) { u32 x = r16(a + 2 * i), y = r16(b + 2 * i); if (ci) { x = towlower(x); y = towlower(y); } if (x != y) return x < y ? 1 : 3; }
  return na == nb ? 2 : na < nb ? 1 : 3;
}
SHIM(KERNEL32, CompareStringW, 6) { return cmp_w(ARG(2), ARG(3), ARG(4), ARG(5), ARG(1) & 1); }
SHIM(KERNEL32, CompareStringA, 6) {
  u32 a = ARG(2), b = ARG(4); i32 na = ARG(3), nb = ARG(5); if (na < 0) na = strlen((char *)P(a)); if (nb < 0) nb = strlen((char *)P(b));
  for (i32 i = 0; i < na && i < nb; i++) { int x = r8(a + i), y = r8(b + i); if (ARG(1) & 1) { x = tolower(x); y = tolower(y); } if (x != y) return x < y ? 1 : 3; }
  return na == nb ? 2 : na < nb ? 1 : 3;
}
SHIM(KERNEL32, lstrlenA, 1) { return ARG(0) ? strlen((char *)P(ARG(0))) : 0; }
SHIM(KERNEL32, lstrlenW, 1) { return ARG(0) ? gw_wcslen(ARG(0)) : 0; }
SHIM(KERNEL32, lstrcmpW, 2) { return (u32)(cmp_w(ARG(0), -1, ARG(1), -1, 0) - 2); }
SHIM(KERNEL32, lstrcmpiW, 2) { return (u32)(cmp_w(ARG(0), -1, ARG(1), -1, 1) - 2); }
SHIM(KERNEL32, lstrcmpiA, 2) { int r = strcasecmp((char *)P(ARG(0)), (char *)P(ARG(1))); return (u32)(r < 0 ? -1 : r > 0); }
SHIM(KERNEL32, lstrcmpA, 2) { int r = strcmp((char *)P(ARG(0)), (char *)P(ARG(1))); return (u32)(r < 0 ? -1 : r > 0); }
SHIM(KERNEL32, lstrcpyA, 2) { strcpy((char *)P(ARG(0)), (char *)P(ARG(1))); return ARG(0); }
SHIM(KERNEL32, lstrcpyW, 2) { u32 n = gw_wcslen(ARG(1)) + 1; memmove(P(ARG(0)), P(ARG(1)), 2 * n); return ARG(0); }
SHIM(KERNEL32, lstrcpynW, 3) { u32 d = ARG(0), s = ARG(1), n = ARG(2); if (!n) return d; u32 i = 0; for (; i + 1 < n && r16(s + 2 * i); i++) w16(d + 2 * i, r16(s + 2 * i)); w16(d + 2 * i, 0); return d; }
SHIM(KERNEL32, lstrcatA, 2) { strcat((char *)P(ARG(0)), (char *)P(ARG(1))); return ARG(0); }
SHIM(KERNEL32, GetDateFormatA, 6) { const char *s = "1/1/2013"; if (!ARG(5)) return 9; strcpy((char *)P(ARG(4)), s); return 9; }
SHIM(KERNEL32, GetTimeFormatA, 6) { const char *s = "12:00:00 AM"; if (!ARG(5)) return 12; strcpy((char *)P(ARG(4)), s); return 12; }
SHIM(KERNEL32, FormatMessageA, 7) { if (ARG(4)) { if (ARG(0) & 0x100) { u32 p = gstrdup("error\r\n"); w32(ARG(4), p); } else strcpy((char *)P(ARG(4)), "error\r\n"); } return 7; }

// ---------- files ----------
static u32 open_file_(const char *gp, u32 access, u32 disp);
static u32 open_file(const char *gp, u32 access, u32 disp) { uint64_t t = mono_ns(); u32 r = open_file_(gp, access, disp); g_sc.opens++; g_sc.open_ns += mono_ns() - t; return r; }
static u32 open_file_(const char *gp, u32 access, u32 disp) {
  int wr = (access & 0x40000000u) || disp == 1 || disp == 2 || disp == 5;
  char *hp = map_path(gp, 0); struct stat st; int ex = stat(hp, &st) == 0;
  if (ex && S_ISDIR(st.st_mode)) { u32 h = h_new(H_FILE); h_get(h)->path = hp; h_get(h)->v3 = 3; return h; }
  if (wr) {
    char *wp = map_path(gp, 1);
    if (strcmp(wp, hp) && ex && (disp == 3 || disp == 4)) {   // copy-on-write into run dir
      FILE *a = fopen(hp, "rb"), *b = fopen(wp, "wb"); char buf[65536]; size_t k;
      if (a && b) while ((k = fread(buf, 1, sizeof buf, a)) > 0) fwrite(buf, 1, k, b);
      if (a) fclose(a); if (b) fclose(b);
    }
    free(hp); hp = wp; ex = stat(hp, &st) == 0;
  }
  int fl = wr ? ((access & 0x80000000u) ? O_RDWR : O_WRONLY) : O_RDONLY;
  switch (disp) {
    case 1: if (ex) { set_last_error(80); free(hp); return INVALID_HANDLE; } fl |= O_CREAT; break;
    case 2: fl |= O_CREAT | O_TRUNC; break;
    case 3: if (!ex) { set_last_error(2); free(hp); return INVALID_HANDLE; } break;
    case 4: fl |= O_CREAT; break;
    case 5: if (!ex) { set_last_error(2); free(hp); return INVALID_HANDLE; } fl |= O_TRUNC; break;
  }
  if (!wr) fl = O_RDONLY;
  int fd = open(hp, fl, 0644);
  if (fd < 0) { set_last_error(ex ? 5 : 2); if (g_trace) gw_log("open fail %s", hp); free(hp); return INVALID_HANDLE; }
  u32 h = h_new(H_FILE); Handle *x = h_get(h); x->fd = fd; x->path = hp;
  set_last_error((disp == 2 || disp == 4) && ex ? 183 : 0);
  if (g_trace || getenv("GW_FILES")) gw_log("open %s %s -> %x", wr ? "W" : "R", gp, h);
  return h;
}
SHIM(KERNEL32, CreateFileA, 7) { return open_file((char *)P(ARG(0)), ARG(1), ARG(4)); }
SHIM(KERNEL32, CreateFileW, 7) { char *s = g_w2u(ARG(0)); u32 r = open_file(s, ARG(1), ARG(4)); free(s); return r; }
SHIM(KERNEL32, ReadFile, 5) {
  Handle *h = h_get(ARG(0)); if (!h || h->fd < 0) { if (ARG(3)) w32(ARG(3), 0); set_last_error(6); return 0; }
  uint64_t t = mono_ns(); ssize_t n = read(h->fd, P(ARG(1)), ARG(2)); if (n < 0) n = 0; g_sc.read_ns += mono_ns() - t; g_sc.read_b += n; g_sc.reads++; if (ARG(3)) w32(ARG(3), (u32)n); return 1;
}
SHIM(KERNEL32, WriteFile, 5) {
  Handle *h = h_get(ARG(0)); u32 n = ARG(2);
  if (!h) { set_last_error(6); return 0; }
  if (h->fd < 0) { fprintf(LOG, "[guest-out] %.*s\n", (int)n, (char *)P(ARG(1))); fflush(LOG); }
  else write(h->fd, P(ARG(1)), n);
  if (ARG(3)) w32(ARG(3), n); return 1;
}
SHIM(KERNEL32, WriteConsoleA, 5) { fprintf(LOG, "[console] %.*s\n", (int)ARG(2), (char *)P(ARG(1))); if (ARG(3)) w32(ARG(3), ARG(2)); return 1; }
SHIM(KERNEL32, WriteConsoleW, 5) { if (ARG(3)) w32(ARG(3), ARG(2)); return 1; }
SHIM(KERNEL32, SetFilePointer, 4) {
  Handle *h = h_get(ARG(0)); if (!h || h->fd < 0) { set_last_error(6); return 0xFFFFFFFF; }
  int64_t off = (i32)ARG(1); if (ARG(2)) off = ((int64_t)(i32)r32(ARG(2)) << 32) | ARG(1);
  off_t r = lseek(h->fd, off, ARG(3) == 0 ? SEEK_SET : ARG(3) == 1 ? SEEK_CUR : SEEK_END);
  if (r < 0) { set_last_error(131); return 0xFFFFFFFF; }
  if (ARG(2)) w32(ARG(2), (u32)((uint64_t)r >> 32)); set_last_error(0); return (u32)r;
}
SHIM(KERNEL32, GetFileSize, 2) {
  Handle *h = h_get(ARG(0)); struct stat st; if (!h || h->fd < 0 || fstat(h->fd, &st)) { set_last_error(6); return 0xFFFFFFFF; }
  if (ARG(1)) w32(ARG(1), (u32)((uint64_t)st.st_size >> 32)); return (u32)st.st_size;
}
SHIM(KERNEL32, FlushFileBuffers, 1) { return 1; }
SHIM(KERNEL32, SetEndOfFile, 1) { Handle *h = h_get(ARG(0)); if (h && h->fd >= 0) ftruncate(h->fd, lseek(h->fd, 0, SEEK_CUR)); return 1; }
SHIM(KERNEL32, SetFileTime, 4) { return 1; }
SHIM(KERNEL32, LockFile, 5) { return 1; }
SHIM(KERNEL32, UnlockFile, 5) { return 1; }
static void put_ft(u32 p, time_t t) { uint64_t v = (uint64_t)t * 10000000ull + 116444736000000000ull; w32(p, (u32)v); w32(p + 4, (u32)(v >> 32)); }
SHIM(KERNEL32, GetFileInformationByHandle, 2) {
  Handle *h = h_get(ARG(0)); struct stat st; u32 p = ARG(1); if (!h || h->fd < 0 || fstat(h->fd, &st)) return 0;
  memset(P(p), 0, 52); w32(p, S_ISDIR(st.st_mode) ? 0x10 : 0x80); put_ft(p + 4, st.st_mtime); put_ft(p + 12, st.st_mtime); put_ft(p + 20, st.st_mtime);
  w32(p + 28, 0x1234); w32(p + 32, (u32)((uint64_t)st.st_size >> 32)); w32(p + 36, (u32)st.st_size); w32(p + 40, 1); w32(p + 48, (u32)st.st_ino); return 1;
}
static u32 file_attrs(const char *gp) {
  char *hp = map_path(gp, 0); struct stat st; int r = stat(hp, &st); free(hp);
  if (r) { set_last_error(2); return 0xFFFFFFFF; }
  return S_ISDIR(st.st_mode) ? 0x10 : 0x80;
}
SHIM(KERNEL32, GetFileAttributesA, 1) { return file_attrs((char *)P(ARG(0))); }
SHIM(KERNEL32, GetFileAttributesW, 1) { char *s = g_w2u(ARG(0)); u32 r = file_attrs(s); free(s); return r; }
SHIM(KERNEL32, SetFileAttributesA, 2) { return 1; }
SHIM(KERNEL32, SetFileAttributesW, 2) { return 1; }
static u32 mkdir_g(const char *gp) { char *hp = map_path(gp, 1); int r = mkdir(hp, 0755); free(hp); if (r) { set_last_error(183); return 0; } return 1; }
SHIM(KERNEL32, CreateDirectoryA, 2) { return mkdir_g((char *)P(ARG(0))); }
SHIM(KERNEL32, CreateDirectoryW, 2) { char *s = g_w2u(ARG(0)); u32 r = mkdir_g(s); free(s); return r; }
SHIM(KERNEL32, RemoveDirectoryA, 1) { return 1; }
SHIM(KERNEL32, RemoveDirectoryW, 1) { return 1; }
static u32 del_g(const char *gp) { char *hp = map_path(gp, 1); int r = unlink(hp); free(hp); if (r) { set_last_error(2); return 0; } return 1; }
SHIM(KERNEL32, DeleteFileA, 1) { gw_log("DeleteFileA(%s)", (char *)P(ARG(0))); return del_g((char *)P(ARG(0))); }
SHIM(KERNEL32, DeleteFileW, 1) { char *s = g_w2u(ARG(0)); gw_log("DeleteFileW(%s)", s); u32 r = del_g(s); free(s); return r; }
SHIM(KERNEL32, CopyFileA, 3) { gw_log("CopyFileA(%s -> %s) ignored", (char *)P(ARG(0)), (char *)P(ARG(1))); set_last_error(5); return 0; }
SHIM(KERNEL32, MoveFileA, 2) { gw_log("MoveFileA ignored"); set_last_error(5); return 0; }
SHIM(KERNEL32, MoveFileW, 2) { gw_log("MoveFileW ignored"); set_last_error(5); return 0; }
SHIM(KERNEL32, GetCurrentDirectoryA, 2) { const char *s = "C:\\GodsWar"; if (ARG(0) <= 10) return 11; strcpy((char *)P(ARG(1)), s); return 10; }
SHIM(KERNEL32, GetCurrentDirectoryW, 2) { if (ARG(0) <= 10) return 11; u2w("C:\\GodsWar", ARG(1), ARG(0)); return 10; }
SHIM(KERNEL32, SetCurrentDirectoryA, 1) { gw_log("SetCurrentDirectoryA(%s)", (char *)P(ARG(0))); return 1; }
SHIM(KERNEL32, SetCurrentDirectoryW, 1) { return 1; }
static int fullpath(const char *in, char *out, size_t n) {
  if (in[0] && in[1] == ':') return snprintf(out, n, "%s", in);
  const char *s = in; while (s[0] == '.' && (s[1] == '\\' || s[1] == '/')) s += 2;
  int l = snprintf(out, n, "C:\\GodsWar\\%s", s); for (char *q = out; *q; q++) if (*q == '/') *q = '\\'; return l;
}
SHIM(KERNEL32, GetFullPathNameA, 4) { char b[1024]; int l = fullpath((char *)P(ARG(0)), b, sizeof b); if ((u32)l >= ARG(1)) return l + 1; strcpy((char *)P(ARG(2)), b);
  if (ARG(3)) { char *bs = strrchr((char *)P(ARG(2)), '\\'); w32(ARG(3), bs ? ARG(2) + (u32)(bs + 1 - (char *)P(ARG(2))) : 0); } return l; }
SHIM(KERNEL32, GetFullPathNameW, 4) { char *s = g_w2u(ARG(0)); char b[1024]; int l = fullpath(s, b, sizeof b); free(s); if ((u32)l >= ARG(1)) return l + 1; u2w(b, ARG(2), ARG(1)); if (ARG(3)) w32(ARG(3), 0); return l; }
SHIM(KERNEL32, GetLongPathNameW, 3) { u32 n = gw_wcslen(ARG(0)); if (n >= ARG(2)) return n + 1; memmove(P(ARG(1)), P(ARG(0)), 2 * n + 2); return n; }
SHIM(KERNEL32, GetDriveTypeA, 1) { return 3; }
SHIM(KERNEL32, GetDriveTypeW, 1) { return 3; }
SHIM(KERNEL32, GetLogicalDrives, 0) { return 4; }
SHIM(KERNEL32, GetDiskFreeSpaceA, 5) { if (ARG(1)) w32(ARG(1), 8); if (ARG(2)) w32(ARG(2), 512); if (ARG(3)) w32(ARG(3), 0x1000000); if (ARG(4)) w32(ARG(4), 0x2000000); return 1; }
SHIM(KERNEL32, GetVolumeInformationA, 8) {
  if (ARG(1)) w8(ARG(1), 0); if (ARG(3)) w32(ARG(3), 0x1A2B3C4D); if (ARG(4)) w32(ARG(4), 255); if (ARG(5)) w32(ARG(5), 0x3e700ff);
  if (ARG(6) && ARG(7) > 4) strcpy((char *)P(ARG(6)), "NTFS"); return 1;
}
SHIM(KERNEL32, GetTempPathA, 2) { strcpy((char *)P(ARG(1)), "C:\\GodsWar\\Temp\\"); return 16; }
SHIM(KERNEL32, GetTempFileNameA, 4) { static int k; sprintf((char *)P(ARG(3)), "C:\\GodsWar\\Temp\\gw%04x.tmp", ++k); return k; }
SHIM(KERNEL32, GetTempFileNameW, 4) { static int k; char b[64]; sprintf(b, "C:\\GodsWar\\Temp\\gw%04x.tmp", ++k); u2w(b, ARG(3), 260); return k; }
// FindFirstFile
typedef struct { char dir[2048]; char pat[256]; DIR *d; int dots; } FindState;
static int find_next(FindState *fs, u32 out, int wide) {
  for (;;) {
    const char *nm; struct dirent *de = NULL;
    if (fs->dots < 2) { nm = fs->dots == 0 ? "." : ".."; fs->dots++; }
    else { de = readdir(fs->d); if (!de) return 0; nm = de->d_name; if (!strcmp(nm, ".") || !strcmp(nm, "..")) continue; }
    if (fnmatch(fs->pat, nm, FNM_CASEFOLD) && strcmp(fs->pat, "*.*")) continue;
    char full[4096]; snprintf(full, sizeof full, "%s/%s", fs->dir, nm); struct stat st; stat(full, &st);
    memset(P(out), 0, wide ? 592 : 320);
    w32(out, S_ISDIR(st.st_mode) ? 0x10 : 0x80); put_ft(out + 4, st.st_mtime); put_ft(out + 12, st.st_mtime); put_ft(out + 20, st.st_mtime);
    w32(out + 28, 0); w32(out + 32, S_ISDIR(st.st_mode) ? 0 : (u32)st.st_size);
    if (wide) u2w(nm, out + 44, 260); else snprintf((char *)P(out + 44), 260, "%s", nm);
    return 1;
  }
}
static u32 find_first(const char *gp, u32 out, int wide) {
  char g[2048]; snprintf(g, sizeof g, "%s", gp); for (char *q = g; *q; q++) if (*q == '\\') *q = '/';
  char *sl = strrchr(g, '/'); const char *pat = sl ? sl + 1 : g; char dirg[2048];
  if (sl) { *sl = 0; snprintf(dirg, sizeof dirg, "%s", g); } else strcpy(dirg, ".");
  char *hd = map_path(dirg[0] ? dirg : ".", 0);
  FindState *fs = calloc(1, sizeof *fs); snprintf(fs->dir, sizeof fs->dir, "%s", hd); snprintf(fs->pat, sizeof fs->pat, "%s", pat); free(hd);
  fs->dots = (!strcmp(pat, "*") || !strcmp(pat, "*.*")) ? 0 : 2;
  fs->d = opendir(fs->dir);
  if (!fs->d) { free(fs); set_last_error(3); if (getenv("GW_FILES")) gw_log("FindFirst %s -> none", gp); return INVALID_HANDLE; }
  if (!find_next(fs, out, wide)) { closedir(fs->d); free(fs); set_last_error(2); return INVALID_HANDLE; }
  u32 h = h_new(H_FIND); h_get(h)->p = fs; if (getenv("GW_FILES")) gw_log("FindFirst %s -> %x", gp, h); return h;
}
SHIM(KERNEL32, FindFirstFileA, 2) { return find_first((char *)P(ARG(0)), ARG(1), 0); }
SHIM(KERNEL32, FindFirstFileW, 2) { char *s = g_w2u(ARG(0)); u32 r = find_first(s, ARG(1), 1); free(s); return r; }
SHIM(KERNEL32, FindNextFileA, 2) { Handle *h = h_get(ARG(0)); if (!h || !h->p) return 0; if (!find_next(h->p, ARG(1), 0)) { set_last_error(18); return 0; } return 1; }
SHIM(KERNEL32, FindNextFileW, 2) { Handle *h = h_get(ARG(0)); if (!h || !h->p) return 0; if (!find_next(h->p, ARG(1), 1)) { set_last_error(18); return 0; } return 1; }
SHIM(KERNEL32, FindClose, 1) { Handle *h = h_get(ARG(0)); if (h && h->p) { FindState *fs = h->p; closedir(fs->d); free(fs); h->p = NULL; } h_close(ARG(0)); return 1; }
static u32 mk_mapping(u32 hf, u32 szlo) {
  Handle *f = h_get(hf); if (!f || f->fd < 0) { gw_log("CreateFileMapping on non-file %x", hf); set_last_error(6); return 0; }
  struct stat st; fstat(f->fd, &st); u32 h = h_new(H_MAPPING); Handle *m = h_get(h); m->fd = dup(f->fd); m->v1 = szlo ? szlo : (u32)st.st_size;
  if (getenv("GW_FILES")) gw_log("CreateFileMapping %s size %u", f->path, m->v1);
  return h;
}
SHIM(KERNEL32, CreateFileMappingA, 6) { return mk_mapping(ARG(0), ARG(4)); }
SHIM(KERNEL32, CreateFileMappingW, 6) { return mk_mapping(ARG(0), ARG(4)); }
SHIM(KERNEL32, MapViewOfFile, 5) {
  Handle *m = h_get(ARG(0)); if (!m || m->type != H_MAPPING) { set_last_error(6); return 0; }
  u32 off = ARG(3), n = ARG(4) ? ARG(4) : m->v1 - off;
  u32 p = vm_alloc(n + 1, 0x10000); pread(m->fd, P(p), n, off); return p;
}


SHIM(KERNEL32, UnmapViewOfFile, 1) { return 1; }
SHIM(KERNEL32, CreateProcessA, 10) { gw_log("CreateProcessA(%s, %s) refused", ARG(0) ? (char *)P(ARG(0)) : "", ARG(1) ? (char *)P(ARG(1)) : ""); set_last_error(2); return 0; }
SHIM(KERNEL32, CreateProcessW, 10) { gw_log("CreateProcessW refused"); set_last_error(2); return 0; }
SHIM(KERNEL32, GetExitCodeProcess, 2) { if (ARG(1)) w32(ARG(1), 0); return 1; }
SHIM(KERNEL32, OpenProcess, 3) { set_last_error(5); return 0; }
SHIM(KERNEL32, CreateToolhelp32Snapshot, 2) { return INVALID_HANDLE; }
SHIM(KERNEL32, Process32FirstW, 2) { return 0; }
SHIM(KERNEL32, Process32NextW, 2) { return 0; }
SHIM(KERNEL32, CreatePipe, 4) { return 0; }
SHIM(KERNEL32, PeekNamedPipe, 6) { return 0; }

// ---------- profile (ini) ----------
static int ini_get(const char *file, const char *sec, const char *key, char *out, size_t n) {
  char path[1100]; if (!strchr(file, '\\') && !strchr(file, '/')) snprintf(path, sizeof path, "C:\\WINDOWS\\%s", file); else snprintf(path, sizeof path, "%s", file);
  char *hp = map_path(path, 0); FILE *f = fopen(hp, "rb"); free(hp); if (!f) return 0;
  char line[2048]; int in = 0, found = 0;
  while (fgets(line, sizeof line, f)) {
    char *s = line; while (*s == ' ' || *s == '\t') s++;
    char *e = s + strlen(s); while (e > s && (e[-1] == '\n' || e[-1] == '\r' || e[-1] == ' ')) *--e = 0;
    if (*s == '[') { char *c = strchr(s, ']'); if (c) *c = 0; in = !strcasecmp(s + 1, sec); continue; }
    if (!in) continue; char *eq = strchr(s, '='); if (!eq) continue; *eq = 0; char *k = s; char *ke = eq; while (ke > k && ke[-1] == ' ') *--ke = 0;
    if (!strcasecmp(k, key)) { char *v = eq + 1; while (*v == ' ') v++; snprintf(out, n, "%s", v); found = 1; break; }
  }
  fclose(f); return found;
}
SHIM(KERNEL32, GetPrivateProfileStringA, 6) {
  char v[2048]; const char *def = ARG(2) ? (char *)P(ARG(2)) : "";
  if (!ARG(0) || !ARG(1)) { if (ARG(4)) w8(ARG(3), 0); return 0; }
  if (!ini_get((char *)P(ARG(5)), (char *)P(ARG(0)), (char *)P(ARG(1)), v, sizeof v)) snprintf(v, sizeof v, "%s", def);
  u32 n = ARG(4); if (!n) return 0; snprintf((char *)P(ARG(3)), n, "%s", v); return strlen((char *)P(ARG(3)));
}
SHIM(KERNEL32, GetPrivateProfileStringW, 6) {
  char v[2048]; char *sec = g_w2u(ARG(0)), *key = g_w2u(ARG(1)), *def = g_w2u(ARG(2)), *file = g_w2u(ARG(5));
  if (!sec || !key || !ini_get(file, sec, key, v, sizeof v)) snprintf(v, sizeof v, "%s", def ? def : "");
  free(sec); free(key); free(def); free(file); if (!ARG(4)) return 0; return u2w(v, ARG(3), ARG(4)) - 1;
}
SHIM(KERNEL32, GetPrivateProfileIntA, 4) { char v[256]; if (!ini_get((char *)P(ARG(3)), (char *)P(ARG(0)), (char *)P(ARG(1)), v, sizeof v)) return ARG(2); return (u32)atoi(v); }
SHIM(KERNEL32, GetPrivateProfileSectionA, 4) { if (ARG(2) >= 2) { w8(ARG(1), 0); w8(ARG(1) + 1, 0); } return 0; }
SHIM(KERNEL32, WritePrivateProfileStringA, 4) { return 1; }

// ---------- SEH ----------
// EXCEPTION_RECORD and CONTEXT are built on the guest stack; handlers are called through gcall.
#define CTX_SIZE 0x2CC
static const int CTXREG[][2] = {{0x9C, UC_X86_REG_EDI}, {0xA0, UC_X86_REG_ESI}, {0xA4, UC_X86_REG_EBX}, {0xA8, UC_X86_REG_EDX}, {0xAC, UC_X86_REG_ECX},
  {0xB0, UC_X86_REG_EAX}, {0xB4, UC_X86_REG_EBP}, {0xB8, UC_X86_REG_EIP}, {0xC0, UC_X86_REG_EFLAGS}, {0xC4, UC_X86_REG_ESP}};
SHIM(KERNEL32, RaiseException, 4) {
  u32 code = ARG(0), flags = ARG(1), nargs = ARG(2), args = ARG(3), ra = r32(g_esp);
  u32 esp = g_esp - 0x400; u32 rec = esp - 0x50; u32 ctx = rec - CTX_SIZE - 0x10;
  memset(P(rec), 0, 0x50); w32(rec, code); w32(rec + 4, flags & 1); w32(rec + 12, ra); if (nargs > 15) nargs = 15; w32(rec + 16, nargs);
  for (u32 i = 0; i < nargs; i++) w32(rec + 20 + 4 * i, r32(args + 4 * i));
  memset(P(ctx), 0, CTX_SIZE); w32(ctx, 0x1003f);
  for (int i = 0; i < 10; i++) { u32 v; uc_reg_read(uc, CTXREG[i][1], &v); w32(ctx + CTXREG[i][0], v); }
  w32(ctx + 0xB8, ra); w32(ctx + 0xC4, g_esp + 4 + 16);
  gw_log("RaiseException code=%08x from %s", code, addr_sym(ra));
  u32 frame = r32(tls_teb());
  while (frame != 0xFFFFFFFF && frame) {
    u32 handler = r32(frame + 4);
    u32 disp = esp - 0x10;
    u32 r = gcall(handler, 4, rec, frame, ctx, disp);
    if (r == 0) {   // ExceptionContinueExecution
      for (int i = 0; i < 10; i++) { u32 v = r32(ctx + CTXREG[i][0]); uc_reg_write(uc, CTXREG[i][1], &v); }
      g_shim_jumped = 1; return 0;
    }
    frame = r32(frame);
  }
  gw_log("unhandled exception %08x from %s; filter=%08x", code, addr_sym(ra), unhandled_filter);
  gw_fatal("unhandled guest exception %08x", code);
  return 0;
}
SHIM(KERNEL32, RtlUnwind, 4) {
  u32 target = ARG(0), rec = ARG(2);
  u32 myrec = 0;
  if (!rec) { myrec = g_esp - 0x400; memset(P(myrec), 0, 0x50); w32(myrec, 0xC0000027); rec = myrec; }
  w32(rec + 4, r32(rec + 4) | 2);   // EXCEPTION_UNWINDING
  if (!target) w32(rec + 4, r32(rec + 4) | 4);
  u32 frame = r32(tls_teb());
  while (frame != 0xFFFFFFFF && frame && frame != target) {
    u32 handler = r32(frame + 4); u32 ctx = g_esp - 0x800 - CTX_SIZE;
    memset(P(ctx), 0, CTX_SIZE);
    gcall(handler, 4, rec, frame, ctx, g_esp - 0x900);
    frame = r32(frame); w32(tls_teb(), frame);
  }
  w32(tls_teb(), frame);
  return 0;
}
SHIM(NTDLL, RtlUnwind, 4) { return sh_KERNEL32_RtlUnwind(); }
SHIM(KERNEL32, EncodePointer, 1) { return ARG(0); }
SHIM(KERNEL32, DecodePointer, 1) { return ARG(0); }
SHIM(USER32, GetLastActivePopup, 1) { return ARG(0); }
