// DirectSound 8 + WINMM mmio on the host, for AudioSys.dll (which keeps its own Vorbis decoder and threads).
//
// Sound buffers keep their PCM in guest memory (Lock returns pointers into it). A software mixer converts
// every playing secondary buffer (u8/s16, mono/stereo, any rate; volume in 1/100 dB, pan) into 44.1 kHz
// stereo s16. Output sinks, chosen by GW_AUDIO:
//   (unset)        no output; the mixer still runs on the guest clock so play cursors and
//                  notification events behave (the default for tests: nothing is played)
//   wav:PATH       the mix is written to PATH (headless tests: listen to it afterwards)
//   coreaudio      the default output device through an AudioUnit (the Mac app sets this)
// Notification events (IDirectSoundNotify) are set on the emulator thread in ds_pump(), which runs at the
// guest's yield points (PeekMessage, waits, Sleep), because the handle table is not thread-safe.
#define Handle MacOS_Handle   // MacTypes.h also defines Handle
#include <AudioToolbox/AudioToolbox.h>
#undef Handle
#include "gw.h"
#include <math.h>
#include <pthread.h>
#include <fcntl.h>
#include <unistd.h>

#define DS_OK 0
#define DSERR_INVALIDPARAM 0x80070057u
#define E_NOINTERFACE 0x80004002u
#define OUT_RATE 44100

typedef struct { u32 off, ev; } Notify;
typedef struct DSB {
  u32 g, gnotify, g3d; int ref; int primary;
  u32 mem; u32 size; int ch, bits, rate, align; u32 caps;
  struct DSB *src;                   // duplicate: shares mem/size/format of src
  int playing, looping; double pos;  // position in frames
  i32 vol, pan; u32 freq;
  Notify nt[64]; int nnt; u8 fired[64];
  struct DSB *next;
} DSB;
static DSB *bufs; static pthread_mutex_t dmu = PTHREAD_MUTEX_INITIALIZER;
static u32 vt_ds, vt_dsb, vt_dsn, vt_ds3d, vt_dsl; static u32 g_ds, g_listener;
static int sink = -1; static FILE *wavf; static long wav_frames; static uint64_t mixed_us;

static DSB *B(u32 g) { DSB *b = (DSB *)(uintptr_t)(((uint64_t)r32(g + 8) << 32) | r32(g + 4)); return b; }
static u32 obj_new(u32 vt, void *host) { u32 g = heap_alloc(16, 1); w32(g, vt); uint64_t p = (uintptr_t)host; w32(g + 4, (u32)p); w32(g + 8, (u32)(p >> 32)); return g; }
static u32 mkvt(const char *iface, const char **names, const int *nargs, shim_fn *fns, int n) {
  u32 vt = heap_alloc(4 * n, 1);
  for (int i = 0; i < n; i++) { char nm[96]; snprintf(nm, sizeof nm, "%s::%s", iface, names[i]); w32(vt + 4 * i, thunk_alloc_named("DSOUND", nm, nargs[i], fns[i])); }
  return vt;
}
static float db_gain(i32 v) { if (v <= -10000) return 0; return powf(10.f, v / 2000.f); }

// ---- mixer (called with dmu held) ----
static void notify_cross(DSB *b, u32 from, u32 to, int wrapped) {
  for (int i = 0; i < b->nnt; i++) { u32 o = b->nt[i].off; if (o == 0xFFFFFFFF) continue;
    if (!wrapped ? (o >= from && o < to) : (o >= from || o < to)) b->fired[i] = 1; }
}
static void mix(short *out, int frames) {
  static float *acc; static int cap; if (frames > cap) { cap = frames; acc = realloc(acc, sizeof(float) * 2 * cap); }
  memset(acc, 0, sizeof(float) * 2 * frames);
  for (DSB *b = bufs; b; b = b->next) {
    if (b->primary || !b->playing) continue;
    DSB *d = b->src ? b->src : b; if (!d->mem || !d->align) continue;
    u32 nfr = d->size / d->align; if (!nfr) continue;
    double step = (double)(b->freq ? b->freq : (u32)d->rate) / OUT_RATE; float g = db_gain(b->vol);
    float gl = g * (b->pan > 0 ? db_gain(-b->pan) : 1), gr = g * (b->pan < 0 ? db_gain(b->pan) : 1);
    const u8 *m = P(d->mem); u32 from = (u32)b->pos * d->align;
    int wrapped = 0;
    for (int i = 0; i < frames; i++) {
      u32 f = (u32)b->pos; if (f >= nfr) { if (b->looping) { b->pos -= nfr; f = (u32)b->pos; wrapped = 1; } else { b->playing = 0; break; } }
      const u8 *p = m + (size_t)f * d->align; float L, R;
      if (d->bits == 8) { L = (p[0] - 128) / 128.f; R = d->ch > 1 ? (p[1] - 128) / 128.f : L; }
      else { int16_t a, c; memcpy(&a, p, 2); L = a / 32768.f; if (d->ch > 1) { memcpy(&c, p + 2, 2); R = c / 32768.f; } else R = L; }
      acc[2 * i] += L * gl; acc[2 * i + 1] += R * gr; b->pos += step;
    }
    if (b->playing && (u32)b->pos >= nfr && !b->looping) b->playing = 0;
    u32 to = (u32)(b->playing ? b->pos : nfr) * d->align; if (to > d->size) to = d->size;
    notify_cross(b, from, to, wrapped);
    if (!b->playing) for (int i = 0; i < b->nnt; i++) if (b->nt[i].off == 0xFFFFFFFF) b->fired[i] = 1;
  }
  for (int i = 0; i < 2 * frames; i++) { float v = acc[i]; if (v > 1) v = 1; if (v < -1) v = -1; out[i] = (short)(v * 32767); }
  // meter (2026-11-17): once a second log how many buffers play (looping = music/ambience, one-shot = SFX) and the RMS
  { static double sq; static long n; static int maxp, maxl, maxo; static uint64_t t0; int p = 0, l = 0;
    for (DSB *b = bufs; b; b = b->next) if (!b->primary && b->playing) { p++; if (b->looping) l++; }
    if (p > maxp) maxp = p; if (l > maxl) maxl = l; if (p - l > maxo) maxo = p - l;
    for (int i = 0; i < 2 * frames; i++) sq += (double)acc[i] * acc[i]; n += 2 * frames;
    uint64_t t = now_us(); if (!t0) t0 = t;
    if (t - t0 >= 1000000) { gw_log("audio meter: playing<=%d looping<=%d oneshot<=%d rms=%.4f", maxp, maxl, maxo, n ? sqrt(sq / n) : 0.0); sq = 0; n = 0; maxp = maxl = maxo = 0; t0 = t; } }
}

// ---- sinks ----
static AudioComponentInstance au;
static OSStatus au_render(void *ref, AudioUnitRenderActionFlags *fl, const AudioTimeStamp *ts, UInt32 bus, UInt32 n, AudioBufferList *io) {
  (void)ref; (void)fl; (void)ts; (void)bus;
  pthread_mutex_lock(&dmu); mix((short *)io->mBuffers[0].mData, (int)n); pthread_mutex_unlock(&dmu); return noErr;
}
static void wav_header(FILE *f, long frames) {
  u32 data = (u32)frames * 4; u8 h[44]; memcpy(h, "RIFF", 4); u32 v = 36 + data; memcpy(h + 4, &v, 4); memcpy(h + 8, "WAVEfmt ", 8);
  v = 16; memcpy(h + 16, &v, 4); u16 s = 1; memcpy(h + 20, &s, 2); s = 2; memcpy(h + 22, &s, 2); v = OUT_RATE; memcpy(h + 24, &v, 4);
  v = OUT_RATE * 4; memcpy(h + 28, &v, 4); s = 4; memcpy(h + 32, &s, 2); s = 16; memcpy(h + 34, &s, 2); memcpy(h + 36, "data", 4); memcpy(h + 40, &data, 4);
  fseek(f, 0, SEEK_SET); fwrite(h, 1, 44, f); fseek(f, 0, SEEK_END);
}
static void sink_init(void) {
  if (sink >= 0) return; sink = 0; const char *e = getenv("GW_AUDIO"); if (!e) e = "";
  if (!strncmp(e, "wav:", 4)) { wavf = fopen(e + 4, "wb"); if (wavf) { wav_header(wavf, 0); sink = 1; gw_log("audio: mixing to %s", e + 4); } }
  else if (!strcmp(e, "coreaudio")) {
    AudioComponentDescription d = {kAudioUnitType_Output, kAudioUnitSubType_DefaultOutput, kAudioUnitManufacturer_Apple, 0, 0};
    AudioComponent c = AudioComponentFindNext(NULL, &d);
    if (c && AudioComponentInstanceNew(c, &au) == noErr) {
      AudioStreamBasicDescription f = {OUT_RATE, kAudioFormatLinearPCM, kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked, 4, 1, 4, 2, 16, 0};
      AURenderCallbackStruct cb = {au_render, NULL};
      AudioUnitSetProperty(au, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, &f, sizeof f);
      AudioUnitSetProperty(au, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0, &cb, sizeof cb);
      if (AudioUnitInitialize(au) == noErr && AudioOutputUnitStart(au) == noErr) { sink = 2; gw_log("audio: CoreAudio default output"); }
    }
    if (sink != 2) gw_log("audio: CoreAudio output failed; running silent");
  }
  if (!sink) gw_log("audio: no output (GW_AUDIO unset); mixer runs on the guest clock");
  mixed_us = now_us();
}
// emulator thread: advance the mixer on the guest clock (no device) and deliver notification events
extern void ds_pump(void);
void ds_pump(void) {
  if (!bufs) return;
  static short tmp[2 * 4096];
  pthread_mutex_lock(&dmu);
  if (sink != 2) {
    uint64_t now = now_us(); long due = (long)((now - mixed_us) * OUT_RATE / 1000000);
    if (due > OUT_RATE) due = OUT_RATE;   // never more than one second at once
    while (due > 0) { int n = due > 4096 ? 4096 : (int)due; mix(tmp, n); if (wavf) { fwrite(tmp, 4, n, wavf); wav_frames += n; } due -= n; mixed_us += (uint64_t)n * 1000000 / OUT_RATE; }
    if (now - mixed_us > 1000000) mixed_us = now;
  }
  u32 evs[256]; int ne = 0;
  for (DSB *b = bufs; b; b = b->next) for (int i = 0; i < b->nnt; i++) if (b->fired[i]) { b->fired[i] = 0; if (ne < 256) evs[ne++] = b->nt[i].ev; }
  pthread_mutex_unlock(&dmu);
  if (wavf && (wav_frames & 0xFFFF) < 4096) { wav_header(wavf, wav_frames); fflush(wavf); }
  extern void thread_set_event(u32 h); for (int i = 0; i < ne; i++) thread_set_event(evs[i]);
}

// ---- IDirectSound8 ----
static u32 DS_QI(void) { w32(ARG(2), ARG(0)); return 0; }
static u32 DS_AddRef(void) { return 2; }
static u32 DS_Release(void) { return 1; }
static u32 DSB_new(u32 desc) {
  u32 flags = r32(desc + 4), bytes = r32(desc + 8), fmt = r32(desc + 16);
  DSB *b = calloc(1, sizeof *b); b->ref = 1; b->caps = flags; b->primary = (flags & 1) != 0;
  if (fmt) { b->ch = r16(fmt + 2); b->rate = r32(fmt + 4); b->align = r16(fmt + 12); b->bits = r16(fmt + 14); }
  else { b->ch = 2; b->rate = OUT_RATE; b->bits = 16; b->align = 4; }
  if (!b->primary) { b->size = bytes; b->mem = heap_alloc(bytes ? bytes : 4, 1); }
  b->g = obj_new(vt_dsb, b);
  pthread_mutex_lock(&dmu); b->next = bufs; bufs = b; pthread_mutex_unlock(&dmu);
  gw_log("dsound: CreateSoundBuffer flags=%x bytes=%u %dch %dHz %dbit -> %08x", flags, bytes, b->ch, b->rate, b->bits, b->g);
  return b->g;
}
static u32 DS_CreateSoundBuffer(void) { sink_init(); w32(ARG(2), DSB_new(ARG(1))); return 0; }
static u32 DS_GetCaps(void) { u32 p = ARG(1); u32 sz = r32(p); memset(P(p + 4), 0, sz > 4 ? sz - 4 : 0); w32(p + 4, 0xF5F); w32(p + 8, 100); w32(p + 12, 200000); return 0; }
static u32 DS_Duplicate(void) {
  DSB *s = B(ARG(1)); DSB *b = calloc(1, sizeof *b); *b = *s; b->src = s->src ? s->src : s; b->playing = 0; b->pos = 0; b->nnt = 0; b->ref = 1;
  b->g = obj_new(vt_dsb, b); pthread_mutex_lock(&dmu); b->next = bufs; bufs = b; pthread_mutex_unlock(&dmu); w32(ARG(2), b->g); return 0;
}
static u32 DS_Ok(void) { return 0; }
static u32 DS_GetSpeaker(void) { w32(ARG(1), 4); return 0; }   // DSSPEAKER_STEREO

// ---- IDirectSoundBuffer8 ----
static u32 DSB_QI(void) {
  DSB *b = B(ARG(0)); u32 iid = r32(ARG(1));
  if (iid == 0xB0210783u) { if (!b->gnotify) b->gnotify = obj_new(vt_dsn, b); w32(ARG(2), b->gnotify); return 0; }   // IID_IDirectSoundNotify
  if (iid == 0x279AFA86u) { if (!b->g3d) b->g3d = obj_new(vt_ds3d, b); w32(ARG(2), b->g3d); return 0; }            // IID_IDirectSound3DBuffer
  if (iid == 0x279AFA84u) { if (!g_listener) g_listener = obj_new(vt_dsl, b); w32(ARG(2), g_listener); return 0; }  // IID_IDirectSound3DListener
  w32(ARG(2), ARG(0)); return 0;   // IDirectSoundBuffer / IDirectSoundBuffer8: same object
}
static u32 DSB_AddRef(void) { return ++B(ARG(0))->ref; }
static u32 DSB_Release(void) { DSB *b = B(ARG(0)); if (b->ref > 0) b->ref--; if (!b->ref) { pthread_mutex_lock(&dmu); b->playing = 0; b->nnt = 0; pthread_mutex_unlock(&dmu); } return b->ref; }
static u32 DSB_GetCaps(void) { DSB *b = B(ARG(0)); u32 p = ARG(1); w32(p + 4, b->caps); w32(p + 8, b->size); w32(p + 12, 0); w32(p + 16, 0); return 0; }
static u32 DSB_GetCurrentPosition(void) {
  DSB *b = B(ARG(0)); DSB *d = b->src ? b->src : b; ds_pump();
  pthread_mutex_lock(&dmu); u32 play = d->align ? ((u32)b->pos * d->align) % (d->size ? d->size : 1) : 0;
  u32 wr = d->size ? (play + (u32)(d->rate * d->align / 66) / d->align * d->align) % d->size : 0; pthread_mutex_unlock(&dmu);
  if (ARG(1)) w32(ARG(1), play); if (ARG(2)) w32(ARG(2), wr); return 0;
}
static u32 DSB_GetFormat(void) { DSB *b = B(ARG(0)); u32 p = ARG(1); if (p) { w16(p, 1); w16(p + 2, b->ch); w32(p + 4, b->rate); w32(p + 8, b->rate * b->align); w16(p + 12, b->align); w16(p + 14, b->bits); if (ARG(2) >= 18) w16(p + 16, 0); } if (ARG(3)) w32(ARG(3), 18); return 0; }
static u32 DSB_GetVolume(void) { w32(ARG(1), B(ARG(0))->vol); return 0; }
static u32 DSB_GetPan(void) { w32(ARG(1), B(ARG(0))->pan); return 0; }
static u32 DSB_GetFrequency(void) { DSB *b = B(ARG(0)); w32(ARG(1), b->freq ? b->freq : (u32)b->rate); return 0; }
static u32 DSB_GetStatus(void) { DSB *b = B(ARG(0)); ds_pump(); w32(ARG(1), b->playing ? (1 | (b->looping ? 4 : 0)) : 0); return 0; }
static u32 DSB_Lock(void) {
  DSB *b = B(ARG(0)); DSB *d = b->src ? b->src : b; u32 off = ARG(1), n = ARG(2), fl = ARG(7);
  if (!d->size) { w32(ARG(3), 0); w32(ARG(4), 0); if (ARG(5)) w32(ARG(5), 0); if (ARG(6)) w32(ARG(6), 0); return DSERR_INVALIDPARAM; }
  if (fl & 1) { u32 pc; pthread_mutex_lock(&dmu); pc = ((u32)b->pos * d->align) % d->size; pthread_mutex_unlock(&dmu); off = pc; }   // DSBLOCK_FROMWRITECURSOR (approx.)
  if (fl & 2) n = d->size;                                                                                                       // DSBLOCK_ENTIREBUFFER
  off %= d->size; if (n > d->size) n = d->size;
  u32 n1 = off + n <= d->size ? n : d->size - off;
  w32(ARG(3), d->mem + off); w32(ARG(4), n1);
  if (ARG(5)) w32(ARG(5), n1 < n ? d->mem : 0); if (ARG(6)) w32(ARG(6), n - n1);
  return 0;
}
static u32 DSB_Play(void) { DSB *b = B(ARG(0)); sink_init(); pthread_mutex_lock(&dmu); b->playing = 1; b->looping = ARG(3) & 1; pthread_mutex_unlock(&dmu); return 0; }
static u32 DSB_SetCurrentPosition(void) { DSB *b = B(ARG(0)); DSB *d = b->src ? b->src : b; pthread_mutex_lock(&dmu); b->pos = d->align ? (double)(ARG(1) / d->align) : 0; pthread_mutex_unlock(&dmu); return 0; }
static u32 DSB_SetFormat(void) { DSB *b = B(ARG(0)); u32 f = ARG(1); if (f) { b->ch = r16(f + 2); b->rate = r32(f + 4); b->align = r16(f + 12); b->bits = r16(f + 14); } return 0; }
static u32 DSB_SetVolume(void) { B(ARG(0))->vol = (i32)ARG(1); return 0; }
static u32 DSB_SetPan(void) { B(ARG(0))->pan = (i32)ARG(1); return 0; }
static u32 DSB_SetFrequency(void) { B(ARG(0))->freq = ARG(1); return 0; }
static u32 DSB_Stop(void) { DSB *b = B(ARG(0)); pthread_mutex_lock(&dmu); if (b->playing) { b->playing = 0; for (int i = 0; i < b->nnt; i++) if (b->nt[i].off == 0xFFFFFFFF) b->fired[i] = 1; } pthread_mutex_unlock(&dmu); return 0; }
static u32 DSB_Unlock(void) { return 0; }
static u32 DSB_SetFX(void) { return 0x80004001u; }
// ---- IDirectSoundNotify ----
static u32 DSN_SetPositions(void) {
  DSB *b = B(ARG(0)); u32 n = ARG(1), p = ARG(2); pthread_mutex_lock(&dmu); b->nnt = 0;
  for (u32 i = 0; i < n && i < 64; i++) { b->nt[i].off = r32(p + 8 * i); b->nt[i].ev = r32(p + 8 * i + 4); b->fired[i] = 0; b->nnt++; }
  pthread_mutex_unlock(&dmu); gw_log("dsound: SetNotificationPositions %u on %08x", n, b->g); return 0;
}

SHIM(DSOUND, DirectSoundCreate8, 3) {
  if (!vt_ds) {
    static const char *n1[] = {"QueryInterface", "AddRef", "Release", "CreateSoundBuffer", "GetCaps", "DuplicateSoundBuffer", "SetCooperativeLevel", "Compact", "GetSpeakerConfig", "SetSpeakerConfig", "Initialize", "VerifyCertification"};
    static const int a1[] = {3, 1, 1, 4, 2, 3, 3, 1, 2, 2, 2, 2};
    static shim_fn f1[] = {DS_QI, DS_AddRef, DS_Release, DS_CreateSoundBuffer, DS_GetCaps, DS_Duplicate, DS_Ok, DS_Ok, DS_GetSpeaker, DS_Ok, DS_Ok, DS_Ok};
    vt_ds = mkvt("IDirectSound8", n1, a1, f1, 12);
    static const char *n2[] = {"QueryInterface", "AddRef", "Release", "GetCaps", "GetCurrentPosition", "GetFormat", "GetVolume", "GetPan", "GetFrequency", "GetStatus", "Initialize", "Lock", "Play", "SetCurrentPosition", "SetFormat", "SetVolume", "SetPan", "SetFrequency", "Stop", "Unlock", "Restore", "SetFX", "AcquireResources", "GetObjectInPath"};
    static const int a2[] = {3, 1, 1, 2, 3, 4, 2, 2, 2, 2, 3, 8, 4, 2, 2, 2, 2, 2, 1, 5, 1, 4, 4, 5};
    static shim_fn f2[] = {DSB_QI, DSB_AddRef, DSB_Release, DSB_GetCaps, DSB_GetCurrentPosition, DSB_GetFormat, DSB_GetVolume, DSB_GetPan, DSB_GetFrequency, DSB_GetStatus, DS_Ok, DSB_Lock, DSB_Play, DSB_SetCurrentPosition, DSB_SetFormat, DSB_SetVolume, DSB_SetPan, DSB_SetFrequency, DSB_Stop, DSB_Unlock, DS_Ok, DSB_SetFX, DSB_SetFX, DSB_SetFX};
    vt_dsb = mkvt("IDirectSoundBuffer8", n2, a2, f2, 24);
    static const char *n3[] = {"QueryInterface", "AddRef", "Release", "SetNotificationPositions"};
    static const int a3[] = {3, 1, 1, 3}; static shim_fn f3[] = {DSB_QI, DSB_AddRef, DSB_Release, DSN_SetPositions};
    vt_dsn = mkvt("IDirectSoundNotify", n3, a3, f3, 4);
    // 3D buffer / listener: accepted and ignored (positional audio is mixed as plain stereo)
    static const char *n4[] = {"QueryInterface", "AddRef", "Release", "GetAllParameters", "GetConeAngles", "GetConeOrientation", "GetConeOutsideVolume", "GetMaxDistance", "GetMinDistance", "GetMode", "GetPosition", "GetVelocity", "SetAllParameters", "SetConeAngles", "SetConeOrientation", "SetConeOutsideVolume", "SetMaxDistance", "SetMinDistance", "SetMode", "SetPosition", "SetVelocity"};
    static const int a4[] = {3, 1, 1, 2, 3, 2, 2, 2, 2, 2, 2, 2, 3, 4, 5, 3, 3, 3, 3, 5, 5};
    static shim_fn f4[21]; f4[0] = DSB_QI; f4[1] = DSB_AddRef; f4[2] = DSB_Release; for (int i = 3; i < 21; i++) f4[i] = DS_Ok;
    vt_ds3d = mkvt("IDirectSound3DBuffer", n4, a4, f4, 21);
    static const char *n5[] = {"QueryInterface", "AddRef", "Release", "GetAllParameters", "GetDistanceFactor", "GetDopplerFactor", "GetOrientation", "GetPosition", "GetRolloffFactor", "GetVelocity", "SetAllParameters", "SetDistanceFactor", "SetDopplerFactor", "SetOrientation", "SetPosition", "SetRolloffFactor", "SetVelocity", "CommitDeferredSettings"};
    static const int a5[] = {3, 1, 1, 2, 2, 2, 3, 2, 2, 2, 3, 3, 3, 8, 5, 3, 5, 1};
    static shim_fn f5[18]; f5[0] = DSB_QI; f5[1] = DSB_AddRef; f5[2] = DSB_Release; for (int i = 3; i < 18; i++) f5[i] = DS_Ok;
    vt_dsl = mkvt("IDirectSound3DListener", n5, a5, f5, 18);
  }
  if (!g_ds) g_ds = obj_new(vt_ds, NULL);
  gw_log("DirectSoundCreate8 -> %08x", g_ds);
  w32(ARG(1), g_ds); return 0;
}
SHIM_ORD(DSOUND, 11, DirectSoundCreate8_ord, 3) { return sh_DSOUND_DirectSoundCreate8(); }

// ---------- WINMM mmio (RIFF/WAVE reading as CWaveFile does it) ----------
typedef struct { int fd; u32 pos, buf, bufoff, buflen; } MMF;
#define MMBUF 8192
static MMF *mmf_of(u32 h) { Handle *x = h_get(h); return x && x->type == H_FILE && x->v3 == 7 ? x->p : NULL; }
static void mm_fill(MMF *m, u32 at) { m->bufoff = at; ssize_t n = pread(m->fd, P(m->buf), MMBUF, at); m->buflen = n > 0 ? (u32)n : 0; }
static void mm_info(MMF *m, u32 info) {
  w32(info + 20, MMBUF); w32(info + 24, m->buf); w32(info + 28, m->buf + (m->pos - m->bufoff)); w32(info + 32, m->buf + m->buflen); w32(info + 36, m->buf + m->buflen);
  w32(info + 40, m->bufoff); w32(info + 44, m->bufoff + m->buflen);
}
SHIM(WINMM, mmioOpenA, 3) {
  char *path = map_path((char *)P(ARG(0)), 0); int fd = open(path, O_RDONLY);
  if (fd < 0) { static int nf; if (nf++ < 5) gw_log("mmioOpenA(%s) failed%s", (char *)P(ARG(0)), nf == 5 ? " (further failures not logged; the game probes numbered files)" : ""); free(path); if (ARG(1)) w16(ARG(1) + 12, 257); return 0; }
  free(path); u32 h = h_new(H_FILE); Handle *x = h_get(h); x->v3 = 7; MMF *m = calloc(1, sizeof *m); m->fd = fd; m->buf = heap_alloc(MMBUF, 1); x->p = m;
  return h;
}
SHIM(WINMM, mmioClose, 2) { MMF *m = mmf_of(ARG(0)); if (m) { close(m->fd); heap_free(m->buf); free(m); h_get(ARG(0))->p = NULL; h_get(ARG(0))->type = H_FREE; } return 0; }
SHIM(WINMM, mmioRead, 3) { MMF *m = mmf_of(ARG(0)); if (!m) return (u32)-1; ssize_t n = pread(m->fd, P(ARG(1)), ARG(2), m->pos); if (n < 0) return (u32)-1; m->pos += n; return (u32)n; }
SHIM(WINMM, mmioSeek, 3) { MMF *m = mmf_of(ARG(0)); if (!m) return (u32)-1; i32 off = ARG(1); u32 o = ARG(2);
  if (o == 0) m->pos = off; else if (o == 1) m->pos += off; else { off_t e = lseek(m->fd, 0, SEEK_END); m->pos = (u32)e + off; } return m->pos; }
static int rd4(MMF *m, u32 at, u32 *v) { return pread(m->fd, v, 4, at) == 4; }
SHIM(WINMM, mmioDescend, 4) {
  MMF *m = mmf_of(ARG(0)); u32 ck = ARG(1), fl = ARG(3); if (!m) return 256;
  u32 want = r32(ck), wtype = r32(ck + 8);
  for (;;) {
    u32 id, sz, type = 0; if (!rd4(m, m->pos, &id) || !rd4(m, m->pos + 4, &sz)) return 265;   // MMIOERR_CHUNKNOTFOUND
    int riff = id == 0x46464952u || id == 0x5453494Cu;   // 'RIFF' / 'LIST'
    if (riff) rd4(m, m->pos + 8, &type);
    int match = !(fl & 0x70) || ((fl & 0x10) && id == want) || ((fl & 0x20) && id == 0x46464952u && type == wtype) || ((fl & 0x40) && id == 0x5453494Cu && type == wtype);
    if (match) { w32(ck, id); w32(ck + 4, sz); w32(ck + 8, type); w32(ck + 12, m->pos + 8); w32(ck + 16, 0); m->pos += riff ? 12 : 8; return 0; }
    m->pos += 8 + sz + (sz & 1);
  }
}
SHIM(WINMM, mmioAscend, 3) { MMF *m = mmf_of(ARG(0)); if (!m) return 256; u32 ck = ARG(1); u32 end = r32(ck + 12) + r32(ck + 4); m->pos = end + (end & 1); return 0; }
SHIM(WINMM, mmioGetInfo, 3) { MMF *m = mmf_of(ARG(0)); if (!m) return 256; mm_fill(m, m->pos); mm_info(m, ARG(1)); return 0; }
SHIM(WINMM, mmioSetInfo, 3) { MMF *m = mmf_of(ARG(0)); if (!m) return 256; m->pos = m->bufoff + (r32(ARG(1) + 28) - m->buf); return 0; }
SHIM(WINMM, mmioAdvance, 3) { MMF *m = mmf_of(ARG(0)); if (!m) return 256; u32 info = ARG(1); m->pos = m->bufoff + (r32(info + 28) - m->buf); mm_fill(m, m->pos); mm_info(m, info); return 0; }
SHIM(WINMM, mmioCreateChunk, 3) { return 256; }
SHIM(WINMM, mmioWrite, 3) { return (u32)-1; }
