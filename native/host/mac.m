// gwplay: the Mac app shell around gwhost. The emulator (gw_main in core.c) runs on its own thread; the
// main thread runs AppKit: one window whose view shows the front buffer (d3d9.c, updated by the raster
// workers at every Present) and forwards mouse, wheel, keys and typed text to the guest (user.c live
// input queue). Only view-local events are used: no event taps, no global monitors, so no permission prompts.
//
//   gwplay --settings FILE        normal app (play.command / the .app bundle)
//   GW_APP_TEST=SCRIPT gwplay --settings FILE
//                                 headless test: the window is created but never ordered front and the
//                                 app never activates; SCRIPT lines "<ms> <cmd> [args]" drive the view
//                                 (times are ABSOLUTE ms since start) with synthesised NSEvents (click X Y | rclick X Y | move X Y | type TEXT |
//                                 typefile PATH | key NAME | snap PATH | frame N (wait for Present N) | quit)
//
// Settings file (key=value; '#' comments): client, server (default YOUR_SERVER_IP = live), port (default 6999),
// sound (0/1), avatar (2 = new models, default; 1 = old), width, height. Rig test aid: account, password, autologin (0/1) are honoured only when server
// is a private address (10.x, 127.x, 192.168.x); the password is never logged; play.command keeps the file 600.
#import <Cocoa/Cocoa.h>
#import <QuartzCore/QuartzCore.h>
#include <pthread.h>
void gw_log(const char *fmt, ...);
#include <sys/stat.h>
#include <stdatomic.h>

extern int gw_main(int argc, char **argv);
extern unsigned int *g_front; extern int g_front_w, g_front_h; extern volatile unsigned g_front_seq; extern int g_present_frames;
extern void gw_in_mouse(int kind, int x, int y); extern void gw_in_wheel(int x, int y, int delta);
extern void (*gw_front_ready)(void);
extern void gw_in_key(unsigned vk, int down); extern void gw_in_char(unsigned ch);

static int CW = 1024, CH = 768;   // client (game) resolution

// ---- settings ----
static NSMutableDictionary *settings;
static void load_settings(NSString *path) {
  settings = [NSMutableDictionary dictionary];
  NSString *txt = [NSString stringWithContentsOfFile:path encoding:NSUTF8StringEncoding error:nil];
  for (NSString *line in [txt componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]) {
    NSString *l = [line stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
    if (!l.length || [l hasPrefix:@"#"]) continue;
    NSRange r = [l rangeOfString:@"="]; if (r.location == NSNotFound) continue;
    settings[[[l substringToIndex:r.location] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]]] =
      [[l substringFromIndex:r.location + 1] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
  }
}
static NSString *S(NSString *k, NSString *def) { NSString *v = settings[k]; return v.length ? v : def; }

// ---- macOS key code -> Windows virtual key ----
static unsigned vk_of_keycode(unsigned short kc) {
  static const unsigned char map[128] = {
    ['\x00'] = 'A', [0x0B] = 'B', [0x08] = 'C', [0x02] = 'D', [0x0E] = 'E', [0x03] = 'F', [0x05] = 'G', [0x04] = 'H', [0x22] = 'I', [0x26] = 'J',
    [0x28] = 'K', [0x25] = 'L', [0x2E] = 'M', [0x2D] = 'N', [0x1F] = 'O', [0x23] = 'P', [0x0C] = 'Q', [0x0F] = 'R', [0x01] = 'S', [0x11] = 'T',
    [0x20] = 'U', [0x09] = 'V', [0x0D] = 'W', [0x07] = 'X', [0x10] = 'Y', [0x06] = 'Z',
    [0x1D] = '0', [0x12] = '1', [0x13] = '2', [0x14] = '3', [0x15] = '4', [0x17] = '5', [0x16] = '6', [0x1A] = '7', [0x1C] = '8', [0x19] = '9',
    [0x24] = 0x0D, [0x4C] = 0x0D, [0x30] = 0x09, [0x31] = 0x20, [0x33] = 0x08, [0x35] = 0x1B, [0x75] = 0x2E,
    [0x7B] = 0x25, [0x7C] = 0x27, [0x7D] = 0x28, [0x7E] = 0x26, [0x73] = 0x24, [0x77] = 0x23, [0x74] = 0x21, [0x79] = 0x22,
    [0x7A] = 0x70, [0x78] = 0x71, [0x63] = 0x72, [0x76] = 0x73, [0x60] = 0x74, [0x61] = 0x75, [0x62] = 0x76, [0x64] = 0x77,
    [0x65] = 0x78, [0x6D] = 0x79, [0x67] = 0x7A, [0x6F] = 0x7B,
    [0x1B] = 0xBD, [0x18] = 0xBB, [0x21] = 0xDB, [0x1E] = 0xDD, [0x2A] = 0xDC, [0x29] = 0xBA, [0x27] = 0xDE, [0x2B] = 0xBC, [0x2F] = 0xBE,
    [0x2C] = 0xBF, [0x32] = 0xC0,
    [0x52] = 0x60, [0x53] = 0x61, [0x54] = 0x62, [0x55] = 0x63, [0x56] = 0x64, [0x57] = 0x65, [0x58] = 0x66, [0x59] = 0x67, [0x5B] = 0x68, [0x5C] = 0x69,
  };
  return kc < 128 ? map[kc] : 0;
}

// ---- the view ----
@interface GWView : NSView { CGImageRef img; unsigned seen; NSEventModifierFlags mods; NSTrackingArea *ta; }
- (void)tick;
- (CGImageRef)image;
@end
@implementation GWView
- (BOOL)acceptsFirstResponder { return YES; }
- (BOOL)isOpaque { return YES; }
- (BOOL)wantsUpdateLayer { return YES; }
- (void)updateLayer { self.layer.backgroundColor = CGColorGetConstantColor(kCGColorBlack); }
- (void)tick {
  unsigned s = g_front_seq; if (s == seen || !g_front) return; seen = s;
  { // 2026-11-18: interval between frames actually shown in the window (what the player sees), logged every 500
    static double last; static unsigned hist[6], n; static const double edge[5] = {0.034, 0.050, 0.080, 0.150, 0.300};
    double now = [NSDate timeIntervalSinceReferenceDate];
    if (last > 0) { double d = now - last; int b = 0; while (b < 5 && d >= edge[b]) b++; hist[b]++;
      if (++n >= 500) { gw_log("display intervals last %u frames: <34ms %u, 34-50 %u, 50-80 %u, 80-150 %u, 150-300 %u, >=300 %u", n, hist[0], hist[1], hist[2], hist[3], hist[4], hist[5]); memset(hist, 0, sizeof hist); n = 0; } }
    last = now; }
  int w = g_front_w, h = g_front_h; size_t n = (size_t)w * h * 4; void *copy = malloc(n); memcpy(copy, g_front, n);
  CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
  CGDataProviderRef dp = CGDataProviderCreateWithData(NULL, copy, n, (CGDataProviderReleaseDataCallback)(void (*)(void *, const void *, size_t))free);
  CGImageRef ni = CGImageCreate(w, h, 8, 32, w * 4, cs, kCGImageAlphaNoneSkipFirst | kCGBitmapByteOrder32Little, dp, NULL, false, kCGRenderingIntentDefault);
  CGDataProviderRelease(dp); CGColorSpaceRelease(cs);
  // 2026-11-18: hand the frame to Core Animation (GPU scales it, linear filter) instead of drawRect on the CPU
  [CATransaction begin]; [CATransaction setDisableActions:YES]; self.layer.contents = (__bridge id)ni; [CATransaction commit];
  if (img) CGImageRelease(img); img = ni;
}
- (void)drawRect:(NSRect)r {
  CGContextRef c = [[NSGraphicsContext currentContext] CGContext];
  CGContextSetRGBFillColor(c, 0, 0, 0, 1); CGContextFillRect(c, NSRectToCGRect(self.bounds));
  if (img) { CGContextSetInterpolationQuality(c, kCGInterpolationMedium); CGContextDrawImage(c, NSRectToCGRect(self.bounds), img); }
}
- (CGImageRef)image { return img; }
- (void)updateTrackingAreas {
  if (ta) [self removeTrackingArea:ta];
  ta = [[NSTrackingArea alloc] initWithRect:self.bounds options:NSTrackingMouseMoved | NSTrackingActiveInKeyWindow | NSTrackingInVisibleRect owner:self userInfo:nil];
  [self addTrackingArea:ta]; [super updateTrackingAreas];
}
- (void)client:(NSEvent *)e x:(int *)x y:(int *)y {
  NSPoint p = [self convertPoint:e.locationInWindow fromView:nil]; NSRect b = self.bounds;
  int cx = (int)(p.x * CW / b.size.width), cy = (int)((b.size.height - p.y) * CH / b.size.height);
  *x = cx < 0 ? 0 : cx >= CW ? CW - 1 : cx; *y = cy < 0 ? 0 : cy >= CH ? CH - 1 : cy;
}
#define MOUSE(sel, kind) - (void)sel:(NSEvent *)e { int x, y; [self client:e x:&x y:&y]; gw_in_mouse(kind, x, y); }
MOUSE(mouseMoved, 0) MOUSE(mouseDragged, 0) MOUSE(rightMouseDragged, 0) MOUSE(mouseDown, 1) MOUSE(mouseUp, 2) MOUSE(rightMouseDown, 3) MOUSE(rightMouseUp, 4)
- (void)scrollWheel:(NSEvent *)e { int x, y; [self client:e x:&x y:&y]; int d = (int)(e.scrollingDeltaY * (e.hasPreciseScrollingDeltas ? 4 : 40)); if (d) gw_in_wheel(x, y, d); }
- (void)keyDown:(NSEvent *)e {
  unsigned vk = vk_of_keycode(e.keyCode); if (vk) gw_in_key(vk, 1);
  if (!(e.modifierFlags & NSEventModifierFlagCommand)) [self interpretKeyEvents:@[e]];
}
- (void)keyUp:(NSEvent *)e { unsigned vk = vk_of_keycode(e.keyCode); if (vk) gw_in_key(vk, 0); }
- (void)insertText:(id)s {
  NSString *t = [s isKindOfClass:[NSAttributedString class]] ? [s string] : s;
  for (NSUInteger i = 0; i < t.length; i++) { unichar ch = [t characterAtIndex:i]; if (ch >= 0x20 && ch != 0x7F) gw_in_char(ch); }
}
- (void)doCommandBySelector:(SEL)sel { (void)sel; }   // Enter/Tab/Backspace/Escape already sent as WM_KEYDOWN + WM_CHAR
- (void)flagsChanged:(NSEvent *)e {
  NSEventModifierFlags f = e.modifierFlags, ch = f ^ mods; mods = f;
  if (ch & NSEventModifierFlagShift) gw_in_key(0x10, (f & NSEventModifierFlagShift) != 0);
  if (ch & NSEventModifierFlagControl) gw_in_key(0x11, (f & NSEventModifierFlagControl) != 0);
  if (ch & NSEventModifierFlagOption) gw_in_key(0x12, (f & NSEventModifierFlagOption) != 0);
}
@end

// called on a raster thread when a frame is complete: show it on the main thread now (one pending wake at a time)
static GWView *g_view; static atomic_int g_wake_pending;
static void front_ready(void) { if (atomic_exchange(&g_wake_pending, 1)) return;
  dispatch_async(dispatch_get_main_queue(), ^{ atomic_store(&g_wake_pending, 0); [g_view tick]; }); }
// ---- app ----
@interface GWApp : NSObject <NSApplicationDelegate, NSWindowDelegate> { @public NSWindow *win; GWView *view; NSArray *script; NSUInteger si; double t0; int waitframe; }
@end
static int test_mode;
@implementation GWApp
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)a { return YES; }
- (void)windowWillClose:(NSNotification *)n { exit(0); }
- (void)tick:(NSTimer *)t {
  [view tick]; if (test_mode) [self step];
  // the emulator block-buffers gwhost.log between Presents; flush about once a second from here too, so the
  // last lines survive a freeze followed by a force quit (stdio locks the stream, so this thread may call it)
  static double lastflush; double now = [NSDate timeIntervalSinceReferenceDate];
  extern FILE *LOG; if (now - lastflush > 1.0) { lastflush = now; if (LOG) fflush(LOG); }
}
- (NSEvent *)mouse:(NSEventType)type x:(int)x y:(int)y {
  NSRect b = view.bounds; NSPoint p = NSMakePoint((x + 0.5) * b.size.width / CW, b.size.height - (y + 0.5) * b.size.height / CH);
  p = [view convertPoint:p toView:nil];
  return [NSEvent mouseEventWithType:type location:p modifierFlags:0 timestamp:0 windowNumber:win.windowNumber context:nil eventNumber:0 clickCount:1 pressure:1];
}
- (void)typeChar:(unichar)c keyCode:(unsigned short)kc {
  NSString *s = [NSString stringWithCharacters:&c length:1];
  NSEvent *d = [NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:0 timestamp:0 windowNumber:win.windowNumber context:nil characters:s charactersIgnoringModifiers:s isARepeat:NO keyCode:kc];
  NSEvent *u = [NSEvent keyEventWithType:NSEventTypeKeyUp location:NSZeroPoint modifierFlags:0 timestamp:0 windowNumber:win.windowNumber context:nil characters:s charactersIgnoringModifiers:s isARepeat:NO keyCode:kc];
  [view keyDown:d]; [view keyUp:u];
}
static unsigned short kc_of_char(unichar c) {
  static const char *row = "asdfhgzxcv?bqweryt123465=97-80]ou[ip?lj'k;\\,/nm.";
  if (c >= 'A' && c <= 'Z') c += 32;
  for (int i = 0; row[i]; i++) if (row[i] == c && row[i] != '?') return (unsigned short)i;
  return c == ' ' ? 0x31 : 0x7F;
}
- (void)step {
  if (waitframe) { if (g_present_frames < waitframe) return; waitframe = 0; }
  double now = [NSDate timeIntervalSinceReferenceDate] - t0;
  while (si < script.count) {
    NSString *line = script[si]; NSArray *w = [line componentsSeparatedByString:@" "];
    if (w.count < 2 || [line hasPrefix:@"#"]) { si++; continue; }
    if ([w[0] doubleValue] / 1000.0 > now) return;
    si++; NSString *cmd = w[1]; int x = w.count > 2 ? [w[2] intValue] : 0, y = w.count > 3 ? [w[3] intValue] : 0;
    NSString *rest = w.count > 2 ? [[w subarrayWithRange:NSMakeRange(2, w.count - 2)] componentsJoinedByString:@" "] : @"";
    if ([cmd isEqual:@"click"]) { [view mouseMoved:[self mouse:NSEventTypeMouseMoved x:x y:y]]; [view mouseDown:[self mouse:NSEventTypeLeftMouseDown x:x y:y]]; [view mouseUp:[self mouse:NSEventTypeLeftMouseUp x:x y:y]]; }
    else if ([cmd isEqual:@"rclick"]) { [view rightMouseDown:[self mouse:NSEventTypeRightMouseDown x:x y:y]]; [view rightMouseUp:[self mouse:NSEventTypeRightMouseUp x:x y:y]]; }
    else if ([cmd isEqual:@"move"]) [view mouseMoved:[self mouse:NSEventTypeMouseMoved x:x y:y]];
    else if ([cmd isEqual:@"type"] || [cmd isEqual:@"typefile"]) {
      NSString *t = rest; BOOL secret = [cmd isEqual:@"typefile"];
      if (secret) t = [[[NSString stringWithContentsOfFile:rest encoding:NSUTF8StringEncoding error:nil] componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]] firstObject] ?: @"";
      for (NSUInteger i = 0; i < t.length; i++) { unichar c = [t characterAtIndex:i]; [self typeChar:c keyCode:kc_of_char(c)]; }
      NSLog(@"gwplay test: typed %@", secret ? @"<secret>" : t);
    }
    else if ([cmd isEqual:@"key"]) { unsigned short kc = [rest isEqual:@"enter"] ? 0x24 : [rest isEqual:@"tab"] ? 0x30 : [rest isEqual:@"escape"] ? 0x35 : [rest isEqual:@"backspace"] ? 0x33 : 0x31;
      unichar c = kc == 0x24 ? 13 : kc == 0x30 ? 9 : kc == 0x35 ? 27 : kc == 0x33 ? 8 : ' '; [self typeChar:c keyCode:kc]; }
    else if ([cmd isEqual:@"snap"]) {
      [view tick]; if (!view.image) continue;
      NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithCGImage:view.image];   // the frame as handed to the layer
      [[rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}] writeToFile:rest atomically:YES];
      NSLog(@"gwplay test: view snapshot %@ (frame %d)", rest, g_present_frames);
    }
    else if ([cmd isEqual:@"lsnap"]) {   // what the window's layer holds, scaled to the view (checks the Core Animation path)
      [view tick]; NSRect b = view.bounds; NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:(NSInteger)b.size.width pixelsHigh:(NSInteger)b.size.height
        bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
      NSGraphicsContext *gc = [NSGraphicsContext graphicsContextWithBitmapImageRep:rep]; [view.layer renderInContext:gc.CGContext];
      [[rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}] writeToFile:rest atomically:YES];
      NSLog(@"gwplay test: layer snapshot %@ (frame %d)", rest, g_present_frames);
    }
    else if ([cmd isEqual:@"frame"]) { waitframe = x; return; }
    else if ([cmd isEqual:@"quit"]) { NSLog(@"gwplay test: quit"); exit(0); }
  }
}
@end

static char *emu_argv[4];
static void *emu_thread(void *a) { (void)a; exit(gw_main(4, emu_argv)); return NULL; }

int main(int argc, char **argv) {
  @autoreleasepool {
    NSString *sp = nil;
    for (int i = 1; i + 1 < argc; i++) if (!strcmp(argv[i], "--settings")) sp = [NSString stringWithUTF8String:argv[i + 1]];
    if (!sp) sp = [[NSBundle mainBundle] pathForResource:@"settings" ofType:@"ini"];
    load_settings(sp ?: @"");
    NSString *base = sp ? [sp stringByDeletingLastPathComponent] : NSHomeDirectory();
    NSString *client = S(@"client", @"");
    if (client.length && ![client hasPrefix:@"/"]) client = [[base stringByAppendingPathComponent:client] stringByStandardizingPath];
    NSString *run = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/GodsWar Native/run"];
    if (settings[@"rundir"]) { run = settings[@"rundir"]; if (![run hasPrefix:@"/"]) run = [[base stringByAppendingPathComponent:run] stringByStandardizingPath]; }
    [[NSFileManager defaultManager] createDirectoryAtPath:run withIntermediateDirectories:YES attributes:nil error:nil];
    CW = [S(@"width", @"1024") intValue]; CH = [S(@"height", @"768") intValue];
    test_mode = getenv("GW_APP_TEST") != NULL;
    NSString *exe = S(@"exe", @"GodsWar.exe");   // 2026-11-16: Origin.exe was removed from client 1.5.0; GodsWar.exe is the same exe
    if (!client.length || ![[NSFileManager defaultManager] fileExistsAtPath:[client stringByAppendingPathComponent:exe]]) {
      fprintf(stderr, "gwplay: settings 'client' must point at a GodsWar client folder with %s (got '%s')\n", exe.UTF8String, client.UTF8String); return 2; }
    // config.ini for the guest (the run dir overrides the client's own)
    NSString *cfg = [NSString stringWithFormat:@"[SERVER]\r\nPORT=%@\r\nIP=%@\r\n\r\n[SYSTEM]\r\nLocals=en_us\r\nSCREENWIDTH=%d\r\nSCREENHEIGHT=%d\r\nISWINDOWS=0\r\nSOUND=%@\r\nFULL_WINDOWS=0\r\nRegion=11\r\nISFB=0\r\nAvatar=%@\r\n\r\n[DEBUG]\r\nMAP_TERRAIN=1\r\nMAP_WATER=1\r\nSKILL_DISPLAY=1\r\nAVATAR=1\r\n",
      S(@"port", @"6999"), S(@"server", @"YOUR_SERVER_IP"), CW, CH, S(@"sound", @"1"),
      S(@"avatar", @"2")];   // 2 = new character models (Characters_new, what the Wine app's players use), 1 = old
    [cfg writeToFile:[run stringByAppendingPathComponent:@"config.ini"] atomically:YES encoding:NSUTF8StringEncoding error:nil];
    setenv("GW_SHOT_EVERY", "0", 0); setenv("GW_STAT_EVERY", "250", 0);
    setenv("GW_SPIKE", "80", 0);   // 2026-11-18: slow-frame log + frame-time histogram in gwhost.log (cheap; no sampler, no shim timing)
    if (!test_mode && [S(@"sound", @"1") isEqual:@"1"]) setenv("GW_AUDIO", "coreaudio", 0);
    // optional auto-login: the account/password are typed into the game's own login form by a generated
    // input script; the password goes through a private temp file that is deleted once read
    // Rig test aid only: refused for any server outside private address ranges, so a live password is never
    // typed from a file (the example settings no longer carry account/password lines).
    NSString *srv = S(@"server", @"YOUR_SERVER_IP");
    BOOL rig = [srv hasPrefix:@"10."] || [srv hasPrefix:@"127."] || [srv hasPrefix:@"192.168."];
    if ([S(@"autologin", @"0") isEqual:@"1"] && !rig) fprintf(stderr, "gwplay: autologin ignored (only for the private rig)\n");
    if (rig && [S(@"autologin", @"0") isEqual:@"1"] && settings[@"account"] && settings[@"password"] && !getenv("GW_INPUT")) {
      NSString *dir = [NSTemporaryDirectory() stringByAppendingPathComponent:[NSString stringWithFormat:@"gwplay-%d", getpid()]];
      [[NSFileManager defaultManager] createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions: @0700} error:nil];
      NSString *pf = [dir stringByAppendingPathComponent:@"p"], *af = [dir stringByAppendingPathComponent:@"a"], *sf = [dir stringByAppendingPathComponent:@"login.in"];
      [settings[@"account"] writeToFile:af atomically:NO encoding:NSUTF8StringEncoding error:nil];
      [settings[@"password"] writeToFile:pf atomically:NO encoding:NSUTF8StringEncoding error:nil];
      chmod(pf.UTF8String, 0600); chmod(af.UTF8String, 0600);
      NSString *scr = [NSString stringWithFormat:@"10 down 513 333\n12 up 513 333\n14 typefile %@\n18 down 513 407\n20 up 513 407\n22 typefile %@\n28 down 510 492\n30 up 510 492\n", af, pf];
      [scr writeToFile:sf atomically:NO encoding:NSUTF8StringEncoding error:nil]; setenv("GW_INPUT", sf.UTF8String, 1); setenv("GW_INPUT_UNLINK", dir.UTF8String, 1);
    }
    emu_argv[0] = "gwplay"; emu_argv[1] = strdup(client.UTF8String); emu_argv[2] = strdup(run.UTF8String); emu_argv[3] = strdup(exe.UTF8String);

    [NSApplication sharedApplication];
    // 2026-11-17: keep macOS from throttling the game (App Nap, timer coalescing) when its window is behind others
    static id activity; activity = [[NSProcessInfo processInfo] beginActivityWithOptions:NSActivityUserInitiated | NSActivityLatencyCritical reason:@"GodsWar game loop"];
    GWApp *app = [GWApp new]; NSApp.delegate = app;
    // GW_APP_TEST_SHOW=1 (2026-11-17): scripted test with a real, visible window that never takes focus: accessory app
    // (no Dock icon, no menu bar), window ordered BEHIND the user's windows, app never activated.
    int test_show = test_mode && getenv("GW_APP_TEST_SHOW") && atoi(getenv("GW_APP_TEST_SHOW"));
    [NSApp setActivationPolicy:test_show ? NSApplicationActivationPolicyAccessory : test_mode ? NSApplicationActivationPolicyProhibited : NSApplicationActivationPolicyRegular];
    if (!test_mode) {
      NSMenu *bar = [NSMenu new]; NSMenuItem *it = [NSMenuItem new]; [bar addItem:it]; NSMenu *m = [NSMenu new];
      [m addItemWithTitle:@"Quit GodsWar" action:@selector(terminate:) keyEquivalent:@"q"]; it.submenu = m; NSApp.mainMenu = bar;
    }
    NSRect fr = NSMakeRect(0, 0, CW, CH);
    app->win = [[NSWindow alloc] initWithContentRect:fr styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
    app->win.title = @"GodsWar"; app->win.delegate = app; app->win.contentAspectRatio = NSMakeSize(CW, CH); app->win.acceptsMouseMovedEvents = YES;
    app->win.releasedWhenClosed = NO;
    app->view = [[GWView alloc] initWithFrame:fr]; app->view.wantsLayer = YES; app->view.layerContentsRedrawPolicy = NSViewLayerContentsRedrawNever;
    app->view.layer.contentsGravity = kCAGravityResize; app->view.layer.magnificationFilter = kCAFilterLinear; app->view.layer.minificationFilter = kCAFilterLinear; app->view.layer.opaque = YES; app->win.contentView = app->view; [app->win makeFirstResponder:app->view];
    if (test_mode) {
      NSString *txt = [NSString stringWithContentsOfFile:[NSString stringWithUTF8String:getenv("GW_APP_TEST")] encoding:NSUTF8StringEncoding error:nil];
      app->script = [txt componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]];
      if (test_show) { [app->win setFrameOrigin:NSMakePoint(40, 40)]; [app->win orderBack:nil]; NSLog(@"gwplay test: window %ld shown behind other windows", (long)app->win.windowNumber); }
    } else { [app->win center]; [app->win makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES]; }
    app->t0 = [NSDate timeIntervalSinceReferenceDate];
    [NSTimer scheduledTimerWithTimeInterval:1.0 / 60 target:app selector:@selector(tick:) userInfo:nil repeats:YES];
    g_view = app->view; gw_front_ready = front_ready;
    pthread_attr_t at; pthread_attr_init(&at); pthread_attr_setstacksize(&at, 256u << 20); pthread_t th; pthread_create(&th, &at, emu_thread, NULL);
    [NSApp run];
  }
  return 0;
}
